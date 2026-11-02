using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Downloads.Fakes;

/// <summary>Hand-written fake <see cref="IHuggingFaceDownloader"/> for tests that only need the admin API's shape, not real downloads.</summary>
public sealed class FakeHuggingFaceDownloader : IHuggingFaceDownloader
{
    private readonly Dictionary<string, DownloadStatus> _downloads = new(StringComparer.Ordinal);

    public List<DownloadRequest> StartCalls { get; } = [];

    public List<string> CancelCalls { get; } = [];

    public Func<DownloadRequest, DownloadStatus>? OnStart { get; set; }

    /// <summary>Thrown from <see cref="StartAsync"/> when set, e.g. to simulate the zero-match pre-flight refusal.</summary>
    public Exception? ThrowOnStart { get; set; }

    /// <summary>Thrown from the read/cancel calls when set, e.g. a node that cannot be reached (MN-230).</summary>
    public Exception? ThrowOnRead { get; set; }

    public void Seed(DownloadStatus status) => _downloads[status.Folder] = status;

    public Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
    {
        StartCalls.Add(request);
        if (ThrowOnStart is { } exception)
        {
            throw exception;
        }

        string folder = string.IsNullOrWhiteSpace(request.Folder) ? request.RepoId.Split('/')[^1] : request.Folder;
        var status = OnStart?.Invoke(request) ?? new DownloadStatus(folder, request.RepoId, request.Revision, DownloadState.Queued, 0, 0, null, null);
        _downloads[folder] = status;
        return Task.FromResult(status);
    }

    public Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default)
    {
        ThrowIfReadFails();
        return Task.FromResult<IReadOnlyList<DownloadStatus>>(_downloads.Values.ToList());
    }

    public Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default)
    {
        ThrowIfReadFails();
        return Task.FromResult(_downloads.GetValueOrDefault(folder));
    }

    public Task<bool> CancelAsync(string folder, CancellationToken ct = default)
    {
        ThrowIfReadFails();
        CancelCalls.Add(folder);
        return Task.FromResult(_downloads.Remove(folder));
    }

    private void ThrowIfReadFails()
    {
        if (ThrowOnRead is { } exception)
        {
            throw exception;
        }
    }
}
