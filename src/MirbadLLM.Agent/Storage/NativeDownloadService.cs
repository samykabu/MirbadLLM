using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Downloads.Native;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// The native Hugging Face downloader (design §6.4 "Native downloader", design-p3p4 §A.2.1, §A.2.3, §A.8; feature
/// <c>downloads.hf-native</c>) behind Core's <see cref="IHuggingFaceDownloader"/>, the interface
/// <see cref="AgentDownloadService"/> consumes: on a node without Docker the composition (MN-232) registers this class
/// instead of the container helper, and the download routes, operations, events and recovery stay as they are.
/// <list type="bullet">
/// <item><b>Start.</b> Downloads into <c>{models}/{folder}.partial</c> (or, when <c>{folder}</c> exists, which
/// <see cref="AgentDownloadService"/> allows only for <c>existing: complete</c>, into the folder itself: every listed file
/// is verified and only what is missing or wrong is fetched). The revision is resolved to its commit SHA once; a
/// <c>.partial</c> with a manifest for the same repo resumes it (same revision and patterns: from the manifest, no Hub
/// call), a manifest for another repo or commit, a <c>.partial</c> without a manifest or a link anywhere under the folder
/// is refused (409 <c>folder_exists</c> / 400 <c>invalid_request</c>). Free space must cover the remaining bytes + the
/// configured headroom (507 <c>insufficient_disk</c>).</item>
/// <item><b>Run.</b> <see cref="NativeSnapshotDownloader"/> fetches the files (range resume, SHA-256 / blob SHA-1,
/// HTTPS only, confinement) and renames <c>{folder}.partial</c> to <c>{folder}</c>. A failure keeps the files and the
/// manifest (resumable by starting again); a cancel keeps them too.</item>
/// <item><b>Token.</b> In memory only, for the life of the run (<see cref="NativeDownloadJob.ForgetToken"/>); the
/// manifest records only that one was used. Error text is scrubbed with <see cref="StorageErrors.RedactSecrets"/>.</item>
/// <item><b>Agent restart.</b> The first call after a start scans the models root once: a download interrupted while it
/// used a token is <b>paused (needs token)</b> (reported as <see cref="DownloadState.Failed"/> with an error starting
/// with <see cref="NeedsTokenMessage"/>, and <see cref="NeedsToken"/> true); starting it again with the token resumes it
/// from its bytes on disk. An interrupted public download resumes by itself.</item>
/// </list>
/// Errors thrown by <see cref="StartAsync"/> are <see cref="AgentException"/>s with their storage code, or the exceptions
/// <see cref="AgentDownloadService"/> already maps (<see cref="ArgumentException"/> = no file matches,
/// <see cref="HuggingFaceApiException"/> = Hub refusal, <see cref="InvalidOperationException"/> = already running);
/// never an <see cref="IOException"/> or <see cref="HttpRequestException"/>, which it would read as a Docker failure.
/// </summary>
public sealed partial class NativeDownloadService : IHuggingFaceDownloader, IAsyncDisposable
{
    /// <summary>First words of the error of a download paused because the agent restarted without its token (§6.4).</summary>
    public const string NeedsTokenMessage = "Paused (needs token)";

    /// <summary>Suffix of the folder a download writes into before it is finalized.</summary>
    public const string PartialSuffix = ".partial";

    private readonly NativeSnapshotDownloader _engine;
    private readonly ModelRootPaths _paths;
    private readonly IDiskSpaceProbe _disk;
    private readonly AgentStorageOptions _storage;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    // Folder names compare as the file system does (MN-219): on Windows "Foo" and "foo" are one folder and one download.
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _recoveryGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _recovered;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="huggingFaceHttp">
    /// Client for the Hub; its base address is the endpoint (default <c>https://huggingface.co/</c>). Register it without
    /// automatic redirects, so each hop is checked by the HTTPS rule before it is requested.
    /// </param>
    /// <param name="paths">Models-root confinement.</param>
    /// <param name="disk">Free-space probe.</param>
    /// <param name="storage">Storage options (headroom).</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Downloader rules; production defaults when null.</param>
    public NativeDownloadService(
        HttpClient huggingFaceHttp,
        ModelRootPaths paths,
        IDiskSpaceProbe disk,
        IOptions<AgentStorageOptions> storage,
        TimeProvider time,
        ILogger<NativeDownloadService> logger,
        NativeDownloadOptions? options = null)
        : this(huggingFaceHttp, paths, disk, storage, time, logger, options, openWrite: null)
    {
    }

    /// <summary>Creates the service with a file opener (tests inject one that fails like a full disk).</summary>
    internal NativeDownloadService(
        HttpClient huggingFaceHttp,
        ModelRootPaths paths,
        IDiskSpaceProbe disk,
        IOptions<AgentStorageOptions> storage,
        TimeProvider time,
        ILogger<NativeDownloadService> logger,
        NativeDownloadOptions? options,
        Func<string, Stream>? openWrite)
    {
        ArgumentNullException.ThrowIfNull(huggingFaceHttp);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _disk = disk;
        _storage = storage.Value;
        _time = time;
        _logger = logger;
        _engine = new NativeSnapshotDownloader(huggingFaceHttp, options, time, logger, openWrite);
    }

    /// <inheritdoc />
    /// <exception cref="AgentException">Invalid request, folder exists, insufficient disk, Hub unreachable.</exception>
    public async Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RepoId))
        {
            throw new ArgumentException("RepoId must not be empty.", nameof(request));
        }

        if (request.Source != ModelSourceKind.HuggingFace)
        {
            throw StorageErrors.InvalidRequest("The native downloader fetches Hugging Face repos only.");
        }

        if (!HuggingFaceHubClient.IsValidRepoId(request.RepoId))
        {
            throw StorageErrors.InvalidRequest($"'{RepoPathRules.Printable(request.RepoId)}' is not a Hugging Face repo id.");
        }

        if (!HuggingFaceHubClient.IsValidRevision(request.Revision))
        {
            throw StorageErrors.InvalidRequest("The revision is not a valid branch, tag or commit.");
        }

        string folder = string.IsNullOrWhiteSpace(request.Folder) ? DeriveFolderName(request.RepoId) : request.Folder;
        if (ModelRootPaths.DownloadNameError(folder) is { } nameError)
        {
            throw StorageErrors.InvalidRequest(nameError);
        }

        if (folder.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase))
        {
            throw StorageErrors.InvalidRequest($"'{folder}' ends with '{PartialSuffix}', which is reserved for unfinished downloads.");
        }

        if (_engine.Transport.RefusalReason(_engine.Hub.Endpoint) is { } endpointError)
        {
            throw StorageErrors.Create(ErrorCodes.InternalError, "This node's Hugging Face endpoint is refused: " + endpointError);
        }

        if (RootError(_paths.Root) is { } rootError)
        {
            throw StorageErrors.Create(ErrorCodes.InternalError, rootError);
        }

        var patterns = request.AllowPatterns is { Count: > 0 } ? request.AllowPatterns : HuggingFacePatterns.DefaultAllowPatterns;
        string? token = string.IsNullOrEmpty(request.Token) ? null : request.Token;
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(folder, out var current) && current.IsRunning)
            {
                throw new InvalidOperationException($"A download for '{folder}' is already in progress.");
            }

            var job = await PlanAsync(folder, request, patterns, token, ct).ConfigureAwait(false);
            var entry = new Entry(folder, request.RepoId, request.Revision, job.Manifest.TotalBytes, job.Manifest.PresentBytes(job.WorkDirectory));
            NativeDownloadServiceLog.Started(_logger, folder, request.RepoId, job.Manifest.CommitSha, job.Manifest.Files.Count, token is not null);
            var accepted = entry.Status(_time); // Queued: taken before the run can move it on
            StartRun(entry, job);
            // Published only once its token source and its task exist, so no caller can see an entry whose IsRunning is
            // still false because StartRun has not run yet (MN-239, D129 (7d); CancelAsync takes the same gate).
            _entries[folder] = entry;
            return accepted;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default)
    {
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        return _entries.Values.Select(e => e.Status(_time)).ToList();
    }

    /// <inheritdoc />
    public async Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default)
    {
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        return folder is not null && _entries.TryGetValue(folder, out var entry) ? entry.Status(_time) : null;
    }

    /// <summary>
    /// Stops a running download (its files and manifest are kept, so starting it again resumes it) and returns true; for a
    /// finished or paused one, forgets it (a paused one is marked cancelled so a restart does not pause it again). False
    /// when this agent knows no download for <paramref name="folder"/>.
    /// <para><b>Decided under the start gate</b> (MN-239, D129 (7d)). <see cref="StartAsync"/> plans inside that gate —
    /// a Hub call, so it can take a while — and only then publishes the entry with its token source and its task.
    /// Without the gate here, a cancel that landed while a start was in flight read whatever entry <c>_entries</c>
    /// still held (a finished one from an earlier run, or a freshly published one whose <c>Run</c> was not installed
    /// yet), took the "forget it" branch and answered <b>true</b> — while the download it was asked to stop carried on
    /// invisibly and renamed its <c>.partial</c> folder into place. The wait for the run is outside the gate, so a slow
    /// cancellation never blocks another folder's start.</para>
    /// </summary>
    public async Task<bool> CancelAsync(string folder, CancellationToken ct = default)
    {
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        if (folder is null)
        {
            return false;
        }

        Entry? running;
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(folder, out var entry))
            {
                return false;
            }

            if (entry.IsRunning)
            {
                entry.CancelRequested = true;
                await entry.Cancellation.CancelAsync().ConfigureAwait(false);
                running = entry;
            }
            else
            {
                if (entry.NeedsToken && entry.WorkDirectory is { } paused)
                {
                    TryWriteState(paused, DownloadManifestState.Cancelled);
                }

                _entries.TryRemove(new KeyValuePair<string, Entry>(folder, entry));
                running = null;
            }
        }
        finally
        {
            _startGate.Release();
        }

        if (running is not null)
        {
            try
            {
                await running.Run.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The run observed the cancellation.
            }
        }

        return true;
    }

    /// <summary>
    /// True when the download into <paramref name="folder"/> is paused because the agent restarted during an authenticated
    /// download (design §6.4): the operator gives the token again by starting the download again.
    /// </summary>
    public bool NeedsToken(string folder) => folder is not null && _entries.TryGetValue(folder, out var entry) && entry.NeedsToken;

    /// <summary>
    /// Runs the start-up scan now (it otherwise runs on the first call): interrupted public downloads resume, interrupted
    /// authenticated ones become paused (needs token). Idempotent.
    /// </summary>
    public Task RecoverAsync(CancellationToken ct = default) => EnsureRecoveredAsync(ct);

    /// <summary>Waits until the run into <paramref name="folder"/> ends (tests).</summary>
    public Task WhenFinishedAsync(string folder) =>
        folder is not null && _entries.TryGetValue(folder, out var entry) ? entry.Run : Task.CompletedTask;

    /// <summary>Stops every run (an agent stop): manifests stay <c>downloading</c>, so the next start resumes or pauses them.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var entry in _entries.Values)
        {
            try
            {
                await entry.Run.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopping.
            }
        }

        _shutdown.Dispose();
        _startGate.Dispose();
        _recoveryGate.Dispose();
    }

    /// <summary>
    /// Why <paramref name="modelsRoot"/> cannot hold native downloads, or null (design-p3p4 §A.2.3: on Windows a local
    /// volume, never a UNC path or a network drive: the service account has no network credentials and mmap over SMB is
    /// slow). Always null on other OSes.
    /// </summary>
    internal static string? RootError(string modelsRoot, Func<string, DriveType>? driveType = null)
    {
        ArgumentNullException.ThrowIfNull(modelsRoot);
        if (!OperatingSystem.IsWindows() && driveType is null)
        {
            return null;
        }

        if (modelsRoot.StartsWith(@"\\", StringComparison.Ordinal) || modelsRoot.StartsWith("//", StringComparison.Ordinal))
        {
            return "The models root is a network (UNC) path; native downloads need a local NTFS volume.";
        }

        DriveType type;
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(modelsRoot)) ?? modelsRoot;
            type = (driveType ?? (r => new DriveInfo(r).DriveType))(root);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }

        return type is DriveType.Network or DriveType.CDRom or DriveType.NoRootDirectory
            ? $"The models root is on a {type} drive; native downloads need a local NTFS volume."
            : null;
    }

    private async Task<NativeDownloadJob> PlanAsync(string folder, DownloadRequest request, IReadOnlyList<string> patterns, string? token, CancellationToken ct)
    {
        var final = _paths.Locate(folder);
        if (final.IsLink)
        {
            throw StorageErrors.InvalidRequest($"'{folder}' is a symbolic link; downloads never write through links.");
        }

        bool complete = final.Exists;
        string work = final.Path;
        string? finalDirectory = null;
        string workName = folder;
        if (!complete)
        {
            workName = folder + PartialSuffix;
            var partial = _paths.Locate(workName);
            if (partial.IsLink)
            {
                throw StorageErrors.InvalidRequest($"'{workName}' is a symbolic link; downloads never write through links.");
            }

            work = partial.Path;
            finalDirectory = final.Path;
        }

        DownloadManifest? previous = null;
        if (Directory.Exists(work))
        {
            if (FindLinkSafe(work) is { } link)
            {
                throw StorageErrors.InvalidRequest($"'{workName}/{link}' is a link; downloads refuse links under the folder and never follow them.");
            }

            try
            {
                previous = DownloadManifest.TryRead(work);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                if (!complete)
                {
                    throw StorageErrors.Create(ErrorCodes.FolderExists,
                        $"'{workName}' has a download manifest this agent cannot read ({ex.Message}); delete '{workName}' to start over.");
                }
            }

            if (!complete && previous is null)
            {
                throw StorageErrors.Create(ErrorCodes.FolderExists,
                    $"'{workName}' exists but has no download manifest; delete it to start over.");
            }
        }

        if (previous is not null && (previous.Source != ModelSourceKind.HuggingFace || !string.Equals(previous.RepoId, request.RepoId, StringComparison.OrdinalIgnoreCase)))
        {
            throw StorageErrors.Create(ErrorCodes.FolderExists,
                $"'{workName}' holds a download of '{previous.RepoId}', not '{request.RepoId}'; delete it or choose another folder.");
        }

        var now = _time.GetUtcNow();
        DownloadManifest manifest;
        if (previous is not null && previous.Revision == request.Revision && SamePatterns(previous.AllowPatterns, patterns))
        {
            // Resume of the same request: the pinned commit and file list come from the manifest (no Hub call).
            manifest = previous with { Authenticated = token is not null, State = DownloadManifestState.Downloading, UpdatedAt = now };
        }
        else
        {
            var snapshot = await GetSnapshotAsync(request, token, ct).ConfigureAwait(false);
            if (previous is not null && !string.Equals(previous.CommitSha, snapshot.CommitSha, StringComparison.Ordinal))
            {
                throw StorageErrors.Create(ErrorCodes.FolderExists, string.Create(CultureInfo.InvariantCulture,
                    $"'{workName}' was downloaded at commit {previous.CommitSha[..12]}, but '{request.Revision ?? "the default branch"}' is now {snapshot.CommitSha[..12]}; delete '{workName}' to start over."));
            }

            try
            {
                manifest = NativeSnapshotDownloader.BuildManifest(snapshot, request.Revision, patterns, token is not null, previous, now);
            }
            catch (DownloadPathException ex)
            {
                throw StorageErrors.InvalidRequest(ex.Message);
            }

            if (manifest.Files.Count == 0)
            {
                throw new ArgumentException(
                    $"No files in '{request.RepoId}' match {string.Join(", ", patterns)} ({snapshot.Files.Count} files in the repo).", nameof(request));
            }
        }

        if (complete)
        {
            // existing: complete — verify every file against the listing, fetch only what is missing or wrong.
            manifest = manifest with { Files = manifest.Files.Select(f => f with { State = DownloadFileState.Pending }).ToList() };
        }

        CheckDisk(manifest, work);
        try
        {
            Directory.CreateDirectory(work);
            manifest.Write(work);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw StorageErrors.Create(ErrorCodes.InternalError, $"The download manifest of '{workName}' could not be written: {ex.Message}");
        }

        return new NativeDownloadJob(folder, work, finalDirectory, manifest, token);
    }

    private async Task<HubSnapshot> GetSnapshotAsync(DownloadRequest request, string? token, CancellationToken ct)
    {
        try
        {
            return await _engine.Hub.GetSnapshotAsync(request.RepoId, request.Revision, token, ct).ConfigureAwait(false);
        }
        catch (HuggingFaceApiException ex)
        {
            throw new HuggingFaceApiException(StorageErrors.RedactSecrets(ex.Message, token)!);
        }
        catch (DownloadRefusedException ex)
        {
            throw StorageErrors.HuggingFaceRefused(StorageErrors.RedactSecrets(ex.Message, token)!);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw StorageErrors.Create(ErrorCodes.InternalError,
                "Hugging Face could not be reached from this node: " + StorageErrors.RedactSecrets(ex.Message, token));
        }
    }

    private void CheckDisk(DownloadManifest manifest, string work)
    {
        long remaining = 0;
        foreach (var file in manifest.Files)
        {
            var info = new FileInfo(Path.Combine(work, Path.Combine(file.Path.Split('/'))));
            long present = info.Exists ? Math.Min(info.Length, file.Size) : 0;
            remaining += file.Size - present;
        }

        long needed = remaining + (long)Math.Ceiling(remaining * _storage.DiskHeadroomFraction);
        if (_disk.Probe(_paths.Root) is { } space && space.FreeBytes < needed)
        {
            throw StorageErrors.Create(ErrorCodes.InsufficientDisk, string.Create(CultureInfo.InvariantCulture,
                $"Not enough free space on this node's models volume: {Gib(needed)} GiB needed (remaining {Gib(remaining)} GiB + {_storage.DiskHeadroomFraction:P0}), {Gib(space.FreeBytes)} GiB free."));
        }
    }

    private void StartRun(Entry entry, NativeDownloadJob job)
    {
        entry.WorkDirectory = job.WorkDirectory;
        entry.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        entry.Run = Task.Run(() => RunAsync(entry, job), CancellationToken.None);
    }

    private async Task RunAsync(Entry entry, NativeDownloadJob job)
    {
        string? token = job.Token;
        try
        {
            entry.State = DownloadState.Downloading;
            await _engine.RunAsync(job, bytes => entry.Bytes = bytes, entry.Cancellation.Token).ConfigureAwait(false);
            entry.Finish(DownloadState.Completed, null, entry.Total);
            NativeDownloadServiceLog.Finished(_logger, entry.Folder, DownloadState.Completed);
        }
        catch (OperationCanceledException) when (entry.CancelRequested)
        {
            TryWriteState(job, DownloadManifestState.Cancelled);
            entry.Finish(DownloadState.Cancelled, null, entry.Bytes);
            NativeDownloadServiceLog.Finished(_logger, entry.Folder, DownloadState.Cancelled);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Agent stopping: the manifest stays "downloading"; the next start resumes it (or pauses it for its token).
            entry.Finish(DownloadState.Failed, "The agent stopped; the download continues when it starts again.", entry.Bytes);
        }
        catch (Exception ex)
        {
            TryWriteState(job, DownloadManifestState.Failed);
            string message = StorageErrors.RedactSecrets(Describe(ex), token)!;
            entry.Finish(DownloadState.Failed, message, entry.Bytes);
            NativeDownloadServiceLog.Failed(_logger, entry.Folder, ex.GetType().Name);
        }
        finally
        {
            job.ForgetToken();
            token = null;
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        DownloadDiskFullException disk =>
            disk.Message + " The files downloaded so far are kept: free space on the models volume and start the download again to resume.",
        DownloadRefusedException { IsAuthorization: true } refused =>
            refused.Message + " Start the download again with a token that has access; the files downloaded so far are kept.",
        DownloadDestinationExistsException exists => exists.Message,
        NativeDownloadException native => native.Message + " The files downloaded so far are kept; start the download again to resume.",
        IOException io => "Writing the download failed: " + io.Message,
        UnauthorizedAccessException denied => "The agent may not write the download folder: " + denied.Message,
        _ => "The download failed: " + ex.Message,
    };

    private async Task EnsureRecoveredAsync(CancellationToken ct)
    {
        if (_recovered)
        {
            return;
        }

        await _recoveryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_recovered)
            {
                Recover();
                _recovered = true;
            }
        }
        finally
        {
            _recoveryGate.Release();
        }
    }

    private void Recover()
    {
        string root;
        IEnumerable<string> directories;
        try
        {
            root = _paths.Root;
            directories = Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NativeDownloadServiceLog.RecoveryScanFailed(_logger, ex);
            return;
        }

        foreach (string directory in directories)
        {
            try
            {
                RecoverOne(root, directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                NativeDownloadServiceLog.RecoveryScanFailed(_logger, ex);
            }
        }
    }

    private void RecoverOne(string root, string directory)
    {
        var info = new DirectoryInfo(directory);
        if (RepoPathRules.IsLink(info))
        {
            return;
        }

        bool partial = info.Name.EndsWith(PartialSuffix, StringComparison.Ordinal);
        string folder = partial ? info.Name[..^PartialSuffix.Length] : info.Name;
        if (ModelRootPaths.DownloadNameError(folder) is not null || _entries.ContainsKey(folder))
        {
            return;
        }

        if (DownloadManifest.TryRead(directory) is not { Source: ModelSourceKind.HuggingFace } manifest ||
            manifest.State is not (DownloadManifestState.Downloading or DownloadManifestState.Paused))
        {
            return;
        }

        long present = manifest.PresentBytes(directory);
        if (manifest.Authenticated)
        {
            if (manifest.State != DownloadManifestState.Paused)
            {
                (manifest with { State = DownloadManifestState.Paused, UpdatedAt = _time.GetUtcNow() }).Write(directory);
            }

            var paused = new Entry(folder, manifest.RepoId, manifest.Revision, manifest.TotalBytes, present) { WorkDirectory = directory };
            paused.Finish(DownloadState.Failed, string.Create(CultureInfo.InvariantCulture,
                $"{NeedsTokenMessage}: the agent restarted during an authenticated download of '{manifest.RepoId}'. The token is kept in memory only, so start the download again with the token to resume ({present} of {manifest.TotalBytes} bytes kept)."),
                present);
            paused.NeedsToken = true;
            _entries[folder] = paused;
            NativeDownloadServiceLog.Paused(_logger, folder);
            return;
        }

        var job = new NativeDownloadJob(folder, directory, partial ? Path.Combine(root, folder) : null,
            manifest with { State = DownloadManifestState.Downloading }, token: null);
        var entry = new Entry(folder, manifest.RepoId, manifest.Revision, manifest.TotalBytes, present);
        _entries[folder] = entry;
        NativeDownloadServiceLog.Resumed(_logger, folder);
        StartRun(entry, job);
    }

    private void TryWriteState(NativeDownloadJob job, DownloadManifestState state)
    {
        string folder = Directory.Exists(job.WorkDirectory) ? job.WorkDirectory : job.FinalDirectory ?? job.WorkDirectory;
        try
        {
            (job.Manifest with { State = state, UpdatedAt = _time.GetUtcNow() }).Write(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NativeDownloadServiceLog.ManifestWriteFailed(_logger, job.Folder, ex);
        }
    }

    private void TryWriteState(string folder, DownloadManifestState state)
    {
        try
        {
            if (DownloadManifest.TryRead(folder) is { } manifest)
            {
                (manifest with { State = state, UpdatedAt = _time.GetUtcNow() }).Write(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            NativeDownloadServiceLog.ManifestWriteFailed(_logger, Path.GetFileName(folder), ex);
        }
    }

    private static string? FindLinkSafe(string folder)
    {
        try
        {
            return RepoPathRules.FindLink(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ".";
        }
    }

    private static bool SamePatterns(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.ToHashSet(StringComparer.Ordinal).SetEquals(b);

    private static string DeriveFolderName(string repoId)
    {
        string name = repoId[(repoId.LastIndexOf('/') + 1)..];
        return UnsafeFolderChars().Replace(name, "-");
    }

    private static string Gib(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("0.##", CultureInfo.InvariantCulture);

    [GeneratedRegex("[^A-Za-z0-9._-]", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeFolderChars();

    /// <summary>One download known to this agent process.</summary>
    private sealed class Entry(string folder, string repoId, string? revision, long total, long bytes)
    {
        private readonly Lock _sampleLock = new();
        private long _sampleBytes = -1;
        private DateTimeOffset _sampleAt;
        private double? _rate;

        public string Folder { get; } = folder;

        public string RepoId { get; } = repoId;

        public string? Revision { get; } = revision;

        public long Total { get; } = total;

        public long Bytes { get; set; } = bytes;

        public DownloadState State { get; set; } = DownloadState.Queued;

        public string? Error { get; private set; }

        public bool NeedsToken { get; set; }

        public bool CancelRequested { get; set; }

        public string? WorkDirectory { get; set; }

        public CancellationTokenSource Cancellation { get; set; } = new();

        public Task Run { get; set; } = Task.CompletedTask;

        public bool IsRunning => !Run.IsCompleted && State is DownloadState.Queued or DownloadState.Downloading;

        public void Finish(DownloadState state, string? error, long bytes)
        {
            Bytes = bytes;
            Error = error;
            State = state;
        }

        public DownloadStatus Status(TimeProvider time)
        {
            long bytes = Bytes;
            double? rate = null;
            if (State is DownloadState.Queued or DownloadState.Downloading)
            {
                var now = time.GetUtcNow();
                lock (_sampleLock)
                {
                    if (_sampleBytes >= 0 && bytes >= _sampleBytes && (now - _sampleAt).TotalSeconds > 0.5)
                    {
                        _rate = (bytes - _sampleBytes) / (now - _sampleAt).TotalSeconds;
                        _sampleBytes = bytes;
                        _sampleAt = now;
                    }
                    else if (_sampleBytes < 0 || bytes < _sampleBytes)
                    {
                        _sampleBytes = bytes;
                        _sampleAt = now;
                    }

                    rate = _rate;
                }
            }

            return new DownloadStatus(Folder, RepoId, Revision, State, Math.Min(bytes, Total), Total, rate, Error);
        }
    }
}

/// <summary>Log lines of <see cref="NativeDownloadService"/>; none takes the request, so no token can reach a log.</summary>
internal static partial class NativeDownloadServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Native download {Folder}: started {RepoId} at {CommitSha} ({FileCount} file(s), authenticated: {Authenticated}).")]
    public static partial void Started(ILogger logger, string folder, string repoId, string commitSha, int fileCount, bool authenticated);

    [LoggerMessage(Level = LogLevel.Information, Message = "Native download {Folder}: ended {State}.")]
    public static partial void Finished(ILogger logger, string folder, DownloadState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Native download {Folder}: failed ({Kind}); its files are kept for a resume.")]
    public static partial void Failed(ILogger logger, string folder, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "Native download {Folder}: interrupted by an agent restart, resuming.")]
    public static partial void Resumed(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Native download {Folder}: interrupted by an agent restart while it used a token; paused until the token is given again.")]
    public static partial void Paused(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Native downloads: scanning the models root for interrupted downloads failed.")]
    public static partial void RecoveryScanFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Native download {Folder}: writing its manifest state failed.")]
    public static partial void ManifestWriteFailed(ILogger logger, string folder, Exception exception);
}
