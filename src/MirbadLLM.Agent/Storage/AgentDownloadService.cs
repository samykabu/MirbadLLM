using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// Hugging Face downloads on the node as operations (design §4.3 downloads, §6.4), over today's container helper
/// (<see cref="IHuggingFaceDownloader"/>, i.e. <c>HuggingFaceDownloader</c> reused as is).
/// <list type="bullet">
/// <item>Start: folder validated (plain name, helper character set, not a link), <c>already_running</c>,
/// <c>folder_exists</c> (unless <c>existing: complete</c>), a pre-flight of the repo listing for <c>no_files_match</c> and
/// free space (remaining bytes + 5 %, else 507 <c>insufficient_disk</c>), then the helper starts and the operation is
/// <c>downloading</c>; a monitor polls the helper and finishes the operation with the final <see cref="NodeDownloadStatus"/>.</item>
/// <item>The HF token lives only in memory and in the helper container's environment for the helper's life (design §6.4,
/// §10.4): the payload handed to the operation log is redacted, nothing logs it, and error text from the helper is
/// scrubbed. Finished helpers are removed by the downloader as soon as their outcome is read, and
/// <see cref="RemoveFinishedHelpersAsync"/> removes any left over at agent start.</item>
/// <item>Recovery after an agent restart (<see cref="ResumeAsync"/>): a helper still running is monitored again (it still
/// has its token, so no "needs token" pause on the container path), a finished one completes the operation, a missing
/// one ends it Cancelled (a cancel of that folder was recorded) or Failed.</item>
/// </list>
/// </summary>
public sealed class AgentDownloadService(
    IHuggingFaceDownloader downloader,
    IHuggingFaceRepoInspector repoInspector,
    ModelRootPaths paths,
    IDiskSpaceProbe disk,
    StorageOperations operations,
    IOptions<AgentStorageOptions> options,
    TimeProvider time,
    ILogger<AgentDownloadService> logger) : IAgentDownloads, IAsyncDisposable
{
    private readonly AgentStorageOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, TrackedDownload> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _folderLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<NodeDownloadStatus> _recent = new();

    // Every monitor still running, independent of _active: a monitor that finishes removes its download from _active
    // before it writes the terminal record (under the event lock), so disposal must not use _active to find what to wait
    // for (W8 review MN-128: the log was disposed under a monitor's CompleteAsync).
    private readonly ConcurrentDictionary<TrackedDownload, Task> _monitors = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    /// <summary>
    /// Executes an accepted download operation. Refusals throw <see cref="AgentException"/> before the first stage (the
    /// caller fails the operation with that error); on success the helper runs and the 202 status is returned.
    /// </summary>
    public async Task<NodeDownloadStatus> StartAsync(DownloadStartRequest request, OperationStatus accepted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(accepted);
        if (string.IsNullOrWhiteSpace(request.RepoId))
        {
            throw StorageErrors.InvalidRequest("repoId is required.");
        }

        if (ModelRootPaths.DownloadNameError(request.Folder) is { } nameError)
        {
            throw StorageErrors.InvalidRequest(nameError);
        }

        string folder = request.Folder;
        var gate = _folderLocks.GetOrAdd(folder, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var location = paths.Locate(folder);
            if (location.IsLink)
            {
                throw StorageErrors.InvalidRequest($"'{folder}' is a symbolic link; downloads never write through links.");
            }

            if (_active.ContainsKey(folder) || await IsHelperRunningAsync(folder, ct).ConfigureAwait(false))
            {
                throw StorageErrors.Create(ErrorCodes.AlreadyRunning, $"A download into '{folder}' is already running.");
            }

            if (location.Exists && request.Existing == DownloadExistingMode.Refuse)
            {
                throw StorageErrors.Create(ErrorCodes.FolderExists,
                    $"'{folder}' already exists on this node. Use existing: complete to fetch only what is missing, or delete the folder first.");
            }

            var patterns = request.AllowPatterns is { Count: > 0 } ? request.AllowPatterns : HuggingFacePatterns.DefaultAllowPatterns;
            long totalBytes = await PreflightAsync(request, patterns, location, ct).ConfigureAwait(false);

            DownloadStatus started;
            try
            {
                started = await downloader.StartAsync(
                    new DownloadRequest(request.RepoId, request.Revision, request.Token, folder, patterns), ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                throw StorageErrors.Create(ErrorCodes.NoFilesMatch, StorageErrors.RedactSecrets(ex.Message, request.Token)!);
            }
            catch (HuggingFaceApiException ex)
            {
                throw StorageErrors.HuggingFaceRefused(StorageErrors.RedactSecrets(ex.Message, request.Token)!);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already in progress", StringComparison.Ordinal))
            {
                throw StorageErrors.Create(ErrorCodes.AlreadyRunning, $"A download into '{folder}' is already running.");
            }
            catch (Exception ex) when (AgentImageService.IsDockerFailure(ex))
            {
                throw StorageErrors.DockerUnavailable(null);
            }

            var now = time.GetUtcNow();
            var tracked = new TrackedDownload(accepted.Id, folder, request.RepoId, request.Revision, now, request.Token);
            var status = Describe(tracked, started with { TotalBytes = started.TotalBytes > 0 ? started.TotalBytes : totalBytes });
            Track(tracked, status);

            await operations.StageAsync(accepted.Id, new OperationStageUpdate
            {
                Stage = OperationStages.Downloading,
                State = OperationState.Running,
                Progress = ProgressOf(status),
            }, ct).ConfigureAwait(false);
            StorageLog.DownloadStarted(logger, accepted.Id, request.RepoId, folder);
            StartMonitor(tracked);
            return status;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// <c>POST /downloads/{folder}/cancel</c> for an accepted cancel operation: stops and removes the helper, ends the
    /// download operation Cancelled and returns the final status (the cancel's result). A download that already finished
    /// returns its final state; no download at all is 404 <c>not_found</c>.
    /// </summary>
    public async Task<NodeDownloadStatus> CancelFolderAsync(string folder, CancellationToken ct = default)
    {
        if (ModelRootPaths.NameError(folder) is { } nameError)
        {
            throw StorageErrors.InvalidRequest(nameError);
        }

        if (_active.TryGetValue(folder, out var tracked))
        {
            return await CancelTrackedAsync(tracked, ct).ConfigureAwait(false);
        }

        DownloadStatus? current;
        try
        {
            current = await downloader.GetAsync(folder, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentImageService.IsDockerFailure(ex))
        {
            throw StorageErrors.DockerUnavailable(ex);
        }

        if (current is null)
        {
            return _recent.LastOrDefault(r => r.Folder == folder)
                ?? throw StorageErrors.NotFound($"No download for '{folder}' on this node.");
        }

        if (current.State is DownloadState.Queued or DownloadState.Downloading)
        {
            // A helper this agent did not start (e.g. from before the agent took over): stop it all the same.
            await downloader.CancelAsync(folder, ct).ConfigureAwait(false);
            var cancelled = ToNode(current with { State = DownloadState.Cancelled, BytesPerSecond = null }, null, null, time.GetUtcNow());
            Remember(cancelled);
            operations.Events.Publish(new DownloadEvent { Download = cancelled });
            return cancelled;
        }

        return _recent.LastOrDefault(r => r.Folder == folder) ?? ToNode(current, null, null, null);
    }

    /// <summary>
    /// Cancels the download started by <paramref name="operationId"/> (<c>/operations/{id}/cancel</c>). Null when that
    /// operation is not a download running in this process.
    /// </summary>
    public async Task<NodeDownloadStatus?> CancelOperationAsync(string operationId, CancellationToken ct = default)
    {
        var tracked = _active.Values.FirstOrDefault(t => t.OperationId == operationId);
        return tracked is null ? null : await CancelTrackedAsync(tracked, ct).ConfigureAwait(false);
    }

    /// <summary>Running downloads plus the recently finished ones (newest last), each with its operation id when known.</summary>
    public async Task<IReadOnlyList<NodeDownloadStatus>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<DownloadStatus> helpers;
        try
        {
            helpers = await downloader.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentImageService.IsDockerFailure(ex))
        {
            helpers = [];
        }

        var result = new Dictionary<string, NodeDownloadStatus>(StringComparer.Ordinal);
        foreach (var recent in _recent)
        {
            result[recent.Folder] = recent;
        }

        foreach (var helper in helpers)
        {
            if (_active.TryGetValue(helper.Folder, out var tracked))
            {
                result[helper.Folder] = Describe(tracked, helper);
            }
            else if (!result.ContainsKey(helper.Folder) || helper.State is DownloadState.Queued or DownloadState.Downloading)
            {
                result[helper.Folder] = ToNode(helper, null, null, null);
            }
        }

        foreach (var tracked in _active.Values)
        {
            if (!result.ContainsKey(tracked.Folder) && tracked.Last is { } last)
            {
                result[tracked.Folder] = last;
            }
        }

        return result.Values.ToList();
    }

    /// <summary>One download by folder; null when the node knows none.</summary>
    public async Task<NodeDownloadStatus?> GetAsync(string folder, CancellationToken ct = default)
    {
        if (ModelRootPaths.NameError(folder) is { } nameError)
        {
            throw StorageErrors.InvalidRequest(nameError);
        }

        DownloadStatus? helper;
        try
        {
            helper = await downloader.GetAsync(folder, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentImageService.IsDockerFailure(ex))
        {
            throw StorageErrors.DockerUnavailable(ex);
        }

        if (_active.TryGetValue(folder, out var tracked))
        {
            return helper is null ? tracked.Last : Describe(tracked, helper);
        }

        var recent = _recent.LastOrDefault(r => r.Folder == folder);
        return recent ?? (helper is null ? null : ToNode(helper, null, null, null));
    }

    /// <summary>True when a running download (tracked or a helper container still at work) writes into <paramref name="folder"/>.</summary>
    public async Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default)
    {
        string bare = folder.EndsWith(".partial", StringComparison.Ordinal) ? folder[..^".partial".Length] : folder;
        if (_active.ContainsKey(folder) || _active.ContainsKey(bare))
        {
            return true;
        }

        try
        {
            return await IsHelperRunningAsync(folder, ct).ConfigureAwait(false) ||
                (bare != folder && await IsHelperRunningAsync(bare, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (AgentImageService.IsDockerFailure(ex))
        {
            // Docker unreachable: we cannot prove the folder is free, so treat it as used.
            return true;
        }
    }

    /// <summary>
    /// Agent start (design §6.4): reads every download helper once, which removes each finished one (the downloader
    /// removes a helper as soon as it sees its outcome), so no exited helper keeps an <c>HF_TOKEN</c> in Docker's state.
    /// Returns how many helpers were finished.
    /// </summary>
    public async Task<int> RemoveFinishedHelpersAsync(CancellationToken ct = default)
    {
        var helpers = await downloader.ListAsync(ct).ConfigureAwait(false);
        int finished = helpers.Count(h => h.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled);
        if (finished > 0)
        {
            StorageLog.FinishedHelpersRemoved(logger, finished);
        }

        return finished;
    }

    /// <summary>
    /// Recovers a non-terminal download operation at agent start (design §4.4 item 5, §6.4): monitor a running helper,
    /// complete a finished one, or end a missing one Cancelled (a cancel for the folder was recorded after it started) or
    /// Failed.
    /// </summary>
    public async Task ResumeAsync(RecoverableOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        string folder = operation.Status.Target;
        DownloadStartRequest? request = null;
        try
        {
            request = operation.Payload?.Deserialize<DownloadStartRequest>(AgentJson.Options);
        }
        catch (JsonException)
        {
            // Fall back to the target only.
        }

        var tracked = new TrackedDownload(operation.Status.Id, folder, request?.RepoId ?? "", request?.Revision,
            operation.Status.StartedAt, token: null);
        var helper = await downloader.GetAsync(folder, ct).ConfigureAwait(false);
        if (helper is null)
        {
            bool cancelled = operations.Log.ListActive().Concat(operations.Log.ListRecent(OperationRetention.MinRetainedRecords))
                .Any(o => o.Kind == OperationKinds.DownloadCancel && o.Target == folder && o.StartedAt >= operation.Status.StartedAt);
            var final = Describe(tracked, new DownloadStatus(folder, tracked.RepoId, tracked.Revision,
                cancelled ? DownloadState.Cancelled : DownloadState.Failed, ModelRootPaths.SizeOf(Path.Combine(paths.Root, folder)), 0, null,
                cancelled ? null : "The download helper was gone when the agent restarted; start the download again (existing: complete)."));
            await FinishAsync(tracked, final, ct).ConfigureAwait(false);
            return;
        }

        var status = Describe(tracked, helper);
        Track(tracked, status);
        if (helper.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)
        {
            await FinishAsync(tracked, status, ct).ConfigureAwait(false);
            return;
        }

        StorageLog.DownloadResumed(logger, operation.Status.Id, folder);
        StartMonitor(tracked);
    }

    /// <summary>Waits until the monitor of <paramref name="folder"/>'s download ends (tests).</summary>
    public Task WhenFinishedAsync(string folder) =>
        _active.TryGetValue(folder, out var tracked) ? tracked.Monitor : Task.CompletedTask;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // DI may dispose this singleton twice (it is also registered as IAgentDownloads).
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        // Wait for every running monitor, not only those still listed in _active: a monitor that saw its helper finish has
        // already left _active and is writing the terminal record; returning before that write lets the container
        // dispose the operation log under it (ObjectDisposedException) and loses the record, so the next start would
        // resume an operation that had ended. A monitor that is only polling is cancelled first.
        foreach (var (tracked, monitor) in _monitors)
        {
            if (tracked.Final is null)
            {
                await tracked.Cancellation.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                await monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down: the operation stays non-terminal and is resumed at the next start.
            }
        }

        _shutdown.Dispose();
    }

    private async Task<long> PreflightAsync(DownloadStartRequest request, IReadOnlyList<string> patterns, FolderLocation location, CancellationToken ct)
    {
        RepoInspection inspection;
        try
        {
            inspection = await repoInspector.InspectAsync(request.RepoId, request.Revision, request.Token, ct).ConfigureAwait(false);
        }
        catch (HuggingFaceApiException ex)
        {
            throw StorageErrors.HuggingFaceRefused(StorageErrors.RedactSecrets(ex.Message, request.Token)!);
        }
        catch (HttpRequestException ex)
        {
            throw StorageErrors.Create(ErrorCodes.InternalError,
                "Hugging Face could not be reached from this node: " + StorageErrors.RedactSecrets(ex.Message, request.Token));
        }

        var matched = inspection.AllFiles.Where(f => HuggingFacePatterns.MatchesAny(f.Name, patterns)).ToList();
        if (matched.Count == 0)
        {
            throw StorageErrors.Create(ErrorCodes.NoFilesMatch,
                $"No files in '{request.RepoId}' match {string.Join(", ", patterns)} ({inspection.AllFiles.Count} files in the repo, format {inspection.Format}).");
        }

        long total = matched.Sum(f => f.Bytes);
        long present = location.Exists && location.ResolvedPath is { } existing ? ModelRootPaths.SizeOf(existing) : 0;
        if (!location.Exists && downloader is NativeDownloadService)
        {
            // The native downloader resumes into {folder}.partial (MN-213): its bytes are already on the disk (MN-219).
            // Only on that path (the container helper writes the folder itself), and never through a link.
            var partial = new DirectoryInfo(System.IO.Path.Combine(paths.Root, request.Folder + NativeDownloadService.PartialSuffix));
            if (partial.Exists && !ModelRootPaths.IsLink(partial))
            {
                present = ModelRootPaths.SizeOf(partial.FullName);
            }
        }

        long remaining = Math.Max(0, total - present);
        long needed = remaining + (long)Math.Ceiling(remaining * _options.DiskHeadroomFraction);
        if (disk.Probe(paths.Root) is { } space && space.FreeBytes < needed)
        {
            throw StorageErrors.Create(ErrorCodes.InsufficientDisk, string.Create(CultureInfo.InvariantCulture,
                $"Not enough free space on this node's models volume: {Gib(needed)} GiB needed (remaining {Gib(remaining)} GiB + {_options.DiskHeadroomFraction:P0}), {Gib(space.FreeBytes)} GiB free."));
        }

        return total;
    }

    private async Task<bool> IsHelperRunningAsync(string folder, CancellationToken ct)
    {
        var helper = await downloader.GetAsync(folder, ct).ConfigureAwait(false);
        return helper is { State: DownloadState.Queued or DownloadState.Downloading };
    }

    private void StartMonitor(TrackedDownload tracked)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return; // stopping: the operation stays non-terminal and is resumed at the next start
        }

        var monitor = Task.Run(() => MonitorAsync(tracked, tracked.Cancellation.Token), CancellationToken.None);
        tracked.Monitor = monitor;
        _monitors[tracked] = monitor;
        _ = monitor.ContinueWith(_ => _monitors.TryRemove(new KeyValuePair<TrackedDownload, Task>(tracked, monitor)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task MonitorAsync(TrackedDownload tracked, CancellationToken ct)
    {
        DateTimeOffset lastRecorded = time.GetUtcNow();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.DownloadPollInterval, time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            DownloadStatus? helper;
            try
            {
                helper = await downloader.GetAsync(tracked.Folder, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (AgentImageService.IsDockerFailure(ex))
            {
                StorageLog.DownloadPollFailed(logger, ex, tracked.Folder);
                continue;
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (helper is null)
            {
                var gone = Describe(tracked, new DownloadStatus(tracked.Folder, tracked.RepoId, tracked.Revision,
                    tracked.CancelRequested ? DownloadState.Cancelled : DownloadState.Failed, tracked.Last?.BytesDownloaded ?? 0,
                    tracked.Last?.TotalBytes ?? 0, null,
                    tracked.CancelRequested ? null : "The download helper container disappeared before it finished."));
                await FinishAsync(tracked, gone, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var status = Describe(tracked, helper);
            if (helper.State is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled)
            {
                await FinishAsync(tracked, status, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            bool changed = tracked.Last is not { } previous ||
                previous.BytesDownloaded != status.BytesDownloaded || previous.State != status.State;
            if (changed)
            {
                Track(tracked, status);
                var progress = ProgressOf(status);
                var now = time.GetUtcNow();
                if (now - lastRecorded >= _options.ProgressRecordInterval)
                {
                    lastRecorded = now;
                    await operations.StageAsync(tracked.OperationId, new OperationStageUpdate { Progress = progress }, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                else if (operations.Log.Find(tracked.OperationId) is { } current)
                {
                    operations.Publish(current with { Progress = progress });
                }
            }
        }
    }

    private async Task<NodeDownloadStatus> CancelTrackedAsync(TrackedDownload tracked, CancellationToken ct)
    {
        tracked.CancelRequested = true;
        await tracked.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await tracked.Monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The monitor observed the cancellation.
        }

        if (tracked.Final is { } alreadyFinal)
        {
            return alreadyFinal;
        }

        try
        {
            await downloader.CancelAsync(tracked.Folder, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentImageService.IsDockerFailure(ex))
        {
            throw StorageErrors.DockerUnavailable(ex);
        }

        var cancelled = Describe(tracked, new DownloadStatus(tracked.Folder, tracked.RepoId, tracked.Revision, DownloadState.Cancelled,
            tracked.Last?.BytesDownloaded ?? 0, tracked.Last?.TotalBytes ?? 0, null, null));
        StorageLog.DownloadCancelled(logger, tracked.OperationId, tracked.Folder);
        return await FinishAsync(tracked, cancelled, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<NodeDownloadStatus> FinishAsync(TrackedDownload tracked, NodeDownloadStatus final, CancellationToken ct)
    {
        await tracked.FinishGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (tracked.Final is { } done)
            {
                return done;
            }

            final = final with { EndedAt = final.EndedAt ?? time.GetUtcNow(), BytesPerSecond = null };
            tracked.Final = final;
            tracked.ForgetToken();
            operations.Events.Publish(() =>
            {
                _active.TryRemove(new KeyValuePair<string, TrackedDownload>(tracked.Folder, tracked));
                Remember(final);
            }, new DownloadEvent { Download = final });

            var (state, error) = final.State switch
            {
                DownloadState.Completed => (OperationState.Succeeded, (AgentError?)null),
                DownloadState.Cancelled => (OperationState.Cancelled, null),
                _ => (OperationState.Failed, StorageErrors.Error(ErrorCodes.InternalError, final.Error ?? "The download failed.")),
            };
            await operations.CompleteAsync(tracked.OperationId, state, error, AgentJson.ToElement(final), ct).ConfigureAwait(false);
            StorageLog.DownloadFinished(logger, tracked.OperationId, tracked.Folder, final.State);
            return final;
        }
        finally
        {
            tracked.FinishGate.Release();
        }
    }

    private void Track(TrackedDownload tracked, NodeDownloadStatus status)
    {
        operations.Events.Publish(() =>
        {
            tracked.Last = status;
            _active[tracked.Folder] = tracked;
        }, new DownloadEvent { Download = status });
    }

    private void Remember(NodeDownloadStatus final)
    {
        _recent.Enqueue(final);
        while (_recent.Count > Math.Max(1, _options.RecentDownloadsKept) && _recent.TryDequeue(out _))
        {
        }
    }

    private NodeDownloadStatus Describe(TrackedDownload tracked, DownloadStatus helper) =>
        ToNode(helper with
        {
            RepoId = helper.RepoId.Length > 0 ? helper.RepoId : tracked.RepoId,
            Revision = helper.Revision ?? tracked.Revision,
            Error = StorageErrors.RedactSecrets(helper.Error, tracked.Token),
        }, tracked.OperationId, tracked.StartedAt, null);

    private NodeDownloadStatus ToNode(DownloadStatus status, string? operationId, DateTimeOffset? startedAt, DateTimeOffset? endedAt) => new()
    {
        Folder = status.Folder,
        RepoId = status.RepoId,
        Revision = status.Revision,
        State = status.State,
        BytesDownloaded = status.BytesDownloaded,
        TotalBytes = status.TotalBytes,
        BytesPerSecond = status.BytesPerSecond,
        Error = StorageErrors.RedactSecrets(status.Error),
        OperationId = operationId,
        // A paused authenticated native download (design §6.4, MN-213): Failed with "Paused (needs token)" until it is
        // started again with the token (MN-219, D125). The container helper never pauses, so the DGX reports false.
        NeedsToken = status.State == DownloadState.Failed && downloader is NativeDownloadService native && native.NeedsToken(status.Folder),
        StartedAt = startedAt,
        EndedAt = endedAt,
    };

    private static OperationProgress ProgressOf(NodeDownloadStatus status) => new()
    {
        BytesDone = status.BytesDownloaded,
        BytesTotal = status.TotalBytes,
        Percent = status.TotalBytes > 0 ? Math.Round(100.0 * status.BytesDownloaded / status.TotalBytes, 1) : null,
        Message = status.State.ToString(),
    };

    private static string Gib(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture);

    private sealed class TrackedDownload(string operationId, string folder, string repoId, string? revision, DateTimeOffset startedAt, string? token)
    {
        public string OperationId { get; } = operationId;

        public string Folder { get; } = folder;

        public string RepoId { get; } = repoId;

        public string? Revision { get; } = revision;

        public DateTimeOffset StartedAt { get; } = startedAt;

        /// <summary>Kept in memory only, to scrub it from helper error text; dropped when the download ends.</summary>
        public string? Token { get; private set; } = token;

        public CancellationTokenSource Cancellation { get; } = new();

        public SemaphoreSlim FinishGate { get; } = new(1, 1);

        public Task Monitor { get; set; } = Task.CompletedTask;

        public NodeDownloadStatus? Last { get; set; }

        public NodeDownloadStatus? Final { get; set; }

        public bool CancelRequested { get; set; }

        public void ForgetToken() => Token = null;
    }
}
