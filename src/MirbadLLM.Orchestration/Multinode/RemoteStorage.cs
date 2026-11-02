using System.Text.RegularExpressions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Recovery;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// <see cref="IHuggingFaceDownloader"/> on one node through its agent (design §4.3 downloads, §6.4): start and cancel are
/// agent operations (<see cref="AgentOperationRunner"/>), list and get are reads. The folder name is derived on the master
/// exactly as today (repo name, characters outside <c>[A-Za-z0-9._-]</c> as dashes) because the agent requires one.
/// <para>The Hugging Face token passes through in memory only: it travels in the request body to the agent, is never
/// logged here, and the master's operation reference stores the digest computed without it (D38).</para>
/// <para>Errors keep today's exception types: invalid requests and <c>no_files_match</c> → <see cref="ArgumentException"/>;
/// <c>already_running</c>, <c>folder_exists</c>, <c>insufficient_disk</c> and an unreachable node →
/// <see cref="InvalidOperationException"/>.</para>
/// </summary>
public sealed partial class RemoteHuggingFaceDownloader : IHuggingFaceDownloader
{
    private readonly string _nodeId;
    private readonly IAgentClientFactory _clients;
    private readonly AgentOperationRunner _runner;
    private readonly DownloadExistingMode _existing;

    /// <param name="nodeId">The node that downloads (and later runs) the checkpoint.</param>
    /// <param name="clients">Agent clients.</param>
    /// <param name="runner">Operation lifecycle.</param>
    /// <param name="existing">
    /// What the agent does when the folder exists. Default <see cref="DownloadExistingMode.Complete"/>: today's helper
    /// downloads into an existing folder and fetches what is missing, and restore relies on that for partial folders.
    /// </param>
    public RemoteHuggingFaceDownloader(string nodeId, IAgentClientFactory clients, AgentOperationRunner runner, DownloadExistingMode existing = DownloadExistingMode.Complete)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(runner);
        _nodeId = nodeId;
        _clients = clients;
        _runner = runner;
        _existing = existing;
    }

    /// <summary>The node this downloader drives.</summary>
    public string NodeId => _nodeId;

    public async Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RepoId))
        {
            throw new ArgumentException("RepoId must not be empty.", nameof(request));
        }

        string folder = FolderFor(request);
        var envelope = _runner.CreateEnvelope();
        var body = new DownloadStartRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            RepoId = request.RepoId,
            Revision = request.Revision,
            Folder = folder,
            AllowPatterns = request.AllowPatterns is { Count: > 0 } patterns ? patterns : null,
            Token = string.IsNullOrEmpty(request.Token) ? null : request.Token,
            Existing = _existing,
        };

        try
        {
            var status = await _runner.SubmitAsync(
                _nodeId,
                AgentRoutes.DownloadStart,
                folder,
                body,
                (c, r, t) => c.StartDownloadAsync(r, t),
                s => ToOperationState(s.State),
                errorOf: null,
                owner: null,
                ct).ConfigureAwait(false);
            return status.ToDownloadStatus();
        }
        catch (Exception ex) when (RemoteAgentErrors.IsAgentFailure(ex))
        {
            throw RemoteAgentErrors.ToStorageException(ex, _nodeId);
        }
    }

    public async Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default)
    {
        var downloads = await ReadAsync(c => c.ListDownloadsAsync(ct)).ConfigureAwait(false);
        return [.. downloads.Select(d => d.ToDownloadStatus())];
    }

    public async Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var download = await ReadAsync(c => c.GetDownloadAsync(folder, ct)).ConfigureAwait(false);
        return download?.ToDownloadStatus();
    }

    /// <summary>
    /// Cancels the download into <paramref name="folder"/> (an agent operation). False when the node has no download for
    /// it; a download that already finished is not an error (D39): true, nothing changes.
    /// </summary>
    public async Task<bool> CancelAsync(string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var envelope = _runner.CreateEnvelope();
        var body = new DownloadCancelRequest { OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt };
        try
        {
            await _runner.SubmitAsync(_nodeId, AgentRoutes.DownloadCancel, folder, body, (c, r, t) => c.CancelDownloadAsync(folder, r, t), owner: null, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (AgentException ex) when (ex.Code == ErrorCodes.NotFound)
        {
            return false;
        }
        catch (Exception ex) when (RemoteAgentErrors.IsAgentFailure(ex))
        {
            throw RemoteAgentErrors.ToStorageException(ex, _nodeId);
        }
    }

    /// <summary>The destination folder of a request, as today's downloader names it.</summary>
    public static string FolderFor(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(request.Folder))
        {
            return InvalidFolderChars().Replace(request.Folder, "-");
        }

        int slash = request.RepoId.LastIndexOf('/');
        string name = slash >= 0 ? request.RepoId[(slash + 1)..] : request.RepoId;
        return InvalidFolderChars().Replace(name, "-");
    }

    private static OperationState? ToOperationState(DownloadState state) => state switch
    {
        DownloadState.Completed => OperationState.Succeeded,
        DownloadState.Failed => OperationState.Failed,
        DownloadState.Cancelled => OperationState.Cancelled,
        _ => null,
    };

    private async Task<T> ReadAsync<T>(Func<IAgentClient, Task<T>> read)
    {
        try
        {
            return await read(_clients.GetClient(_nodeId)).ConfigureAwait(false);
        }
        catch (Exception ex) when (RemoteAgentErrors.IsAgentFailure(ex))
        {
            throw RemoteAgentErrors.ToStorageException(ex, _nodeId);
        }
    }

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex InvalidFolderChars();
}

/// <summary>
/// Checkpoint folders on nodes through their agents (design §4.3 model folders): the "add model" list and folder
/// inspection (today's <see cref="ModelFolderInspector"/> shapes, for the editor, the tuning gates and tag suggestions),
/// and folder deletion as an agent operation. Deletion is refused on the master first when a model placed on that node
/// references the folder (MN-032 brief); the agent separately refuses folders an instance or a download uses (409
/// <c>in_use</c>).
/// </summary>
public sealed class RemoteModelFolders
{
    private readonly IAgentClientFactory _clients;
    private readonly AgentOperationRunner _runner;
    private readonly IModelRegistry _models;

    public RemoteModelFolders(IAgentClientFactory clients, AgentOperationRunner runner, IModelRegistry models)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(models);
        _clients = clients;
        _runner = runner;
        _models = models;
    }

    /// <summary>Every folder under the node's models root, with size, config and partial flags.</summary>
    public Task<IReadOnlyList<ModelFolderEntry>> ListEntriesAsync(string nodeId, CancellationToken ct = default) =>
        ReadAsync(nodeId, c => c.ListModelFoldersAsync(ct));

    /// <summary>Today's <see cref="ModelFolderInspector.ListFolders"/> for a node: folders with a readable <c>config.json</c>.</summary>
    public async Task<IReadOnlyList<ModelFolderSummary>> ListFoldersAsync(string nodeId, CancellationToken ct = default)
    {
        var entries = await ListEntriesAsync(nodeId, ct).ConfigureAwait(false);
        return
        [
            .. entries
                .Where(e => e.HasConfig && e.WeightsGiB is not null)
                .OrderBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
                .Select(e => new ModelFolderSummary(e.Folder, e.WeightsGiB!.Value, e.Architecture, e.DetectedFormat ?? "")),
        ];
    }

    /// <summary>Today's <see cref="ModelFolderInspector.Inspect"/> for a node; null when the folder or its <c>config.json</c> is missing.</summary>
    public Task<ModelFolderInspection?> InspectAsync(string nodeId, string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        return ReadAsync(nodeId, c => c.InspectModelFolderAsync(folder, ct));
    }

    /// <summary>Ids of the models placed on <paramref name="nodeId"/> that reference <paramref name="folder"/>.</summary>
    public async Task<IReadOnlyList<string>> FindReferencesAsync(string nodeId, string folder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var models = await _models.ListAsync(ct).ConfigureAwait(false);
        return [.. models.Where(m => References(m, nodeId, folder)).Select(m => m.Id).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Deletes a folder on a node (agent operation, typed confirmation <paramref name="confirm"/> = the folder name).
    /// </summary>
    /// <exception cref="InvalidOperationException">A model placed on the node references the folder (nothing is sent), or the agent refused (<c>in_use</c>, unreachable).</exception>
    /// <exception cref="ArgumentException">The agent refused the name or the confirmation.</exception>
    /// <exception cref="KeyNotFoundException">No such folder on the node.</exception>
    public async Task<OperationStatus> DeleteAsync(string nodeId, string folder, string confirm, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var users = await FindReferencesAsync(nodeId, folder, ct).ConfigureAwait(false);
        if (users.Count > 0)
        {
            throw new InvalidOperationException(
                $"Folder '{folder}' on node {nodeId} is used by {(users.Count == 1 ? "model" : "models")} {string.Join(", ", users)}; it was not deleted.");
        }

        var envelope = _runner.CreateEnvelope();
        var body = new FolderDeleteRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            Confirm = confirm ?? "",
        };
        try
        {
            return await _runner.SubmitAsync(nodeId, AgentRoutes.ModelFolderDelete, folder, body, (c, r, t) => c.DeleteModelFolderAsync(folder, r, t), owner: null, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (RemoteAgentErrors.IsAgentFailure(ex))
        {
            throw RemoteAgentErrors.ToStorageException(ex, nodeId);
        }
    }

    /// <summary>
    /// True when <paramref name="model"/> is placed on <paramref name="nodeId"/> and references the top-level folder
    /// <paramref name="folder"/>: its checkpoint, a source, the speculative draft model, a <c>/models/…</c> path in its
    /// extra arguments, or a llama.cpp model or projector file.
    /// </summary>
    public static bool References(ModelDefinition model, string nodeId, string folder)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!string.Equals(model.Placement?.NodeId, nodeId, StringComparison.Ordinal))
        {
            return false;
        }

        IEnumerable<string?> paths =
        [
            model.ModelPath,
            .. model.Sources.Select(s => s.Folder),
            model.Speculative?.DraftModel,
            model.LlamaCpp?.ModelFile,
            model.LlamaCpp?.ProjectorFile,
        ];
        return paths.Any(p => string.Equals(TopFolder(p), folder, StringComparison.Ordinal))
            || model.ExtraArgs.Any(a => MentionsModelsPath(a, folder));
    }

    /// <summary>The top-level folder of a path relative to the models root (<c>/models/</c> prefix allowed); null when none.</summary>
    public static string? TopFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string p = path.Trim().Replace('\\', '/');
        if (p.StartsWith(LogicalVolumes.ModelsTarget + "/", StringComparison.Ordinal))
        {
            p = p[(LogicalVolumes.ModelsTarget.Length + 1)..];
        }

        p = p.TrimStart('/');
        int slash = p.IndexOf('/', StringComparison.Ordinal);
        string top = slash >= 0 ? p[..slash] : p;
        return top.Length == 0 ? null : top;
    }

    private static bool MentionsModelsPath(string argument, string folder)
    {
        string needle = LogicalVolumes.ModelsTarget + "/" + folder;
        int at = argument.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            int end = at + needle.Length;
            if (end == argument.Length || argument[end] is '/' or '"' or '\'' or ',' or ' ' or '}' or ']')
            {
                return true;
            }

            at = argument.IndexOf(needle, end, StringComparison.Ordinal);
        }

        return false;
    }

    private async Task<T> ReadAsync<T>(string nodeId, Func<IAgentClient, Task<T>> read)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        try
        {
            return await read(_clients.GetClient(nodeId)).ConfigureAwait(false);
        }
        catch (Exception ex) when (RemoteAgentErrors.IsAgentFailure(ex))
        {
            throw RemoteAgentErrors.ToStorageException(ex, nodeId);
        }
    }
}

/// <summary>
/// <see cref="ICheckpointFolders"/> for one node through its agent (restore preview and restore jobs). The interface is
/// synchronous, so the folder list is cached for <see cref="MaxAge"/>; call <see cref="RefreshAsync"/> before a batch of
/// checks to avoid a blocking read. A folder counts as present when the node lists it with a non-zero size (today: at
/// least one file); a nested path is judged by its top-level folder. An unreachable node reads as "not present", as an
/// unreadable folder does today.
/// </summary>
public sealed class RemoteCheckpointFolders : ICheckpointFolders
{
    /// <summary>Default cache lifetime of the folder list.</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(5);

    private readonly string _nodeId;
    private readonly IAgentClientFactory _clients;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Snapshot? _snapshot;

    public RemoteCheckpointFolders(string nodeId, IAgentClientFactory clients, TimeProvider timeProvider, TimeSpan? maxAge = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _nodeId = nodeId;
        _clients = clients;
        _time = timeProvider;
        MaxAge = maxAge ?? DefaultMaxAge;
    }

    /// <summary>The node whose models root is checked.</summary>
    public string NodeId => _nodeId;

    /// <summary>How long a folder list is reused.</summary>
    public TimeSpan MaxAge { get; }

    /// <summary>Reads the node's folder list now.</summary>
    /// <exception cref="InvalidOperationException">The agent could not be asked.</exception>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ModelFolderEntry> entries;
        try
        {
            entries = await _clients.GetClient(_nodeId).ListModelFoldersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (RemoteAgentErrors.IsAgentFailure(ex))
        {
            throw RemoteAgentErrors.ToStorageException(ex, _nodeId);
        }

        var present = entries.Where(e => e.SizeBytes > 0).Select(e => e.Folder).ToHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            _snapshot = new Snapshot(present, _time.GetUtcNow());
        }
    }

    public bool IsPresent(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string? top = RemoteModelFolders.TopFolder(folder);
        if (top is null)
        {
            return false;
        }

        Snapshot? snapshot;
        lock (_gate)
        {
            snapshot = _snapshot;
        }

        if (snapshot is null || _time.GetUtcNow() - snapshot.At > MaxAge)
        {
            try
            {
                // Synchronous interface: read on the thread pool so no caller context is captured.
                Task.Run(() => RefreshAsync(CancellationToken.None)).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                return false;
            }

            lock (_gate)
            {
                snapshot = _snapshot;
            }
        }

        return snapshot is not null && snapshot.Present.Contains(top);
    }

    private sealed record Snapshot(HashSet<string> Present, DateTimeOffset At);
}
