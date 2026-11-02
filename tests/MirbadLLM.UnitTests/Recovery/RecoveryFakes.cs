using System.Runtime.CompilerServices;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Recovery;

namespace MirbadLLM.UnitTests.Recovery;

/// <summary><see cref="ICheckpointFolders"/> over an in-memory set of folder names.</summary>
internal sealed class FakeCheckpointFolders : ICheckpointFolders
{
    private readonly HashSet<string> _present = new(StringComparer.Ordinal);

    public FakeCheckpointFolders(params string[] present)
    {
        foreach (string folder in present)
        {
            _present.Add(folder);
        }
    }

    public void Add(string folder)
    {
        lock (_present)
        {
            _present.Add(folder);
        }
    }

    public bool IsPresent(string folder)
    {
        lock (_present)
        {
            return _present.Contains(folder);
        }
    }
}

/// <summary>
/// A downloader that takes a few polls per download, records the order downloads started in and the most that
/// ever ran at once, and adds the folder to <see cref="FakeCheckpointFolders"/> when a download completes.
/// </summary>
internal sealed class SequencedDownloader(FakeCheckpointFolders folders) : IHuggingFaceDownloader
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DownloadRequest Request, int Polls)> _active = new(StringComparer.Ordinal);

    public List<string> Started { get; } = [];

    public List<string> Events { get; } = [];

    public int MaxConcurrent { get; private set; }

    /// <summary>Folders whose download fails instead of completing.</summary>
    public HashSet<string> FailFolders { get; } = new(StringComparer.Ordinal);

    public int PollsToComplete { get; set; } = 3;

    public Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
    {
        lock (_gate)
        {
            string folder = request.Folder!;
            _active[folder] = (request, 0);
            Started.Add(folder);
            Events.Add($"download:{folder}");
            MaxConcurrent = Math.Max(MaxConcurrent, _active.Count);
            return Task.FromResult(new DownloadStatus(folder, request.RepoId, request.Revision, DownloadState.Queued, 0, 1000, null, null));
        }
    }

    public Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DownloadStatus>>([]);

    public Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(folder, out var entry))
            {
                return Task.FromResult<DownloadStatus?>(null);
            }

            int polls = entry.Polls + 1;
            if (polls < PollsToComplete)
            {
                _active[folder] = (entry.Request, polls);
                return Task.FromResult<DownloadStatus?>(new DownloadStatus(folder, entry.Request.RepoId, null, DownloadState.Downloading, 400, 1000, null, null));
            }

            _active.Remove(folder);
            if (FailFolders.Contains(folder))
            {
                return Task.FromResult<DownloadStatus?>(new DownloadStatus(folder, entry.Request.RepoId, null, DownloadState.Failed, 0, 1000, null, "boom\nHTTP 404: repo not found"));
            }

            folders.Add(folder);
            Events.Add($"downloaded:{folder}");
            return Task.FromResult<DownloadStatus?>(new DownloadStatus(folder, entry.Request.RepoId, null, DownloadState.Completed, 1000, 1000, null, null));
        }
    }

    public Task<bool> CancelAsync(string folder, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Events.Add($"cancel:{folder}");
            return Task.FromResult(_active.Remove(folder));
        }
    }
}

/// <summary>A runtime whose starts can be refused: a start conflict the first N times, or memory admission for given models.</summary>
internal sealed class ScriptedRuntime(List<string> events) : IModelRuntime
{
    private readonly Dictionary<string, ModelStatus> _statuses = new(StringComparer.Ordinal);

    public int ConflictsBeforeStart { get; set; }

    public HashSet<string> RefuseMemory { get; } = new(StringComparer.Ordinal);

    public List<string> StartAttempts { get; } = [];

    public void SetStatus(ModelStatus status) => _statuses[status.Id] = status;

    public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ModelStatus>>(_statuses.Values.ToList());

    public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_statuses.GetValueOrDefault(id) ?? new ModelStatus(id, ModelState.Stopped));

    public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
    {
        lock (events)
        {
            StartAttempts.Add(id);
            if (ConflictsBeforeStart > 0)
            {
                ConflictsBeforeStart--;
                throw new ModelStartConflictException("still starting") { StartingModelId = "other" };
            }

            if (RefuseMemory.Contains(id))
            {
                throw new ModelMemoryAdmissionException($"Model '{id}' needs 90 GiB but only 40 GiB is free.");
            }

            events.Add($"start:{id}");
            var status = new ModelStatus(id, ModelState.Starting, Since: DateTimeOffset.UtcNow);
            _statuses[id] = status;
            return Task.FromResult(status);
        }
    }

    public Task StopAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
