using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime;

/// <summary>Hugging Face returned 401/403 (gated/private repo) or 404 (unknown repo/revision) for a metadata lookup.</summary>
public sealed class HuggingFaceApiException(string message) : Exception(message);

/// <summary>
/// <see cref="IHuggingFaceDownloader"/> implemented as a Docker helper container per download: the configured
/// vLLM image already ships <c>huggingface_hub</c>, so the container just runs
/// <c>python3 -c "...snapshot_download..."</c> with the models root bound read-write at <c>/models</c>.
/// State is always derived from Docker (containers labelled <see cref="LabelDownload"/>), the same way
/// the container runtime driver treats model containers as truth, so a controller restart re-adopts any
/// download in flight. The expected total byte count is stashed on the container as a label at creation time
/// so it survives that restart without holding process-local state.
/// </summary>
/// <param name="helperUser">
/// Docker <c>User</c> of the helper container (MN-126, <see cref="DownloadHelperUser.Resolve"/>): the agent's own
/// uid:gid so the folders it writes stay deletable by the agent; null = the image default (root). A non-root helper also
/// gets <c>HOME</c>/<c>HF_HOME</c> under <see cref="DownloadHelperUser.HelperHome"/>, since such a uid has no passwd entry
/// in the image and Docker would leave <c>HOME=/</c>, which it cannot write.
/// </param>
public sealed partial class HuggingFaceDownloader(
    DockerEngineClient docker,
    HttpClient huggingFaceHttp,
    IOptions<MirbadLLMOptions> options,
    IOptions<ModelStorageOptions> storageOptions,
    ILogger<HuggingFaceDownloader> logger,
    string? helperUser = null) : IHuggingFaceDownloader
{
    private const string LabelDownload = "mirbadllm.download";
    private const string LabelFolder = "mirbadllm.download.folder";
    private const string LabelRepo = "mirbadllm.download.repo";
    private const string LabelRevision = "mirbadllm.download.revision";
    private const string LabelTotalBytes = "mirbadllm.download.total-bytes";

    /// <summary>Default files fetched when a request does not specify its own allow-patterns.</summary>
    public static readonly IReadOnlyList<string> DefaultAllowPatterns = HuggingFacePatterns.DefaultAllowPatterns;

    // python3 -c script; argv: repo_id, revision ("" = default branch), local_dir, comma-separated allow-patterns.
    // HF_TOKEN (and HF_HUB_ENABLE_HF_TRANSFER) come from the environment, never from argv or logs.
    private const string DownloadScript = """
        import sys
        from huggingface_hub import snapshot_download

        repo_id, revision, local_dir, patterns_csv = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
        patterns = patterns_csv.split(",") if patterns_csv else None
        snapshot_download(
            repo_id=repo_id,
            revision=revision or None,
            local_dir=local_dir,
            allow_patterns=patterns,
        )
        print("mirbadllm: download complete", flush=True)
        """;

    private readonly MirbadLLMOptions _options = options.Value;
    private readonly ModelStorageOptions _storage = storageOptions.Value;
    private readonly HuggingFaceRepoInspector _repoInspector = new(huggingFaceHttp);
    private readonly ConcurrentDictionary<string, (long Bytes, DateTimeOffset At)> _lastSample = new(StringComparer.Ordinal);

    // Final state of downloads whose helper container has already been removed. A finished container is
    // removed as soon as its outcome is known (so ss-dl-* containers don't pile up), and this map keeps
    // reporting that outcome to pollers afterwards. Lost on a controller restart, which only means a
    // download that finished before the restart is no longer listed.
    private readonly ConcurrentDictionary<string, DownloadStatus> _finished = new(StringComparer.Ordinal);

    public async Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.RepoId))
        {
            throw new ArgumentException("RepoId must not be empty.", nameof(request));
        }

        string folder = string.IsNullOrWhiteSpace(request.Folder) ? DeriveFolderName(request.RepoId) : SanitizeFolder(request.Folder);
        var patterns = request.AllowPatterns is { Count: > 0 } ? request.AllowPatterns : DefaultAllowPatterns;

        var existing = await FindContainerAsync(folder, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.State is "running" or "created")
            {
                throw new InvalidOperationException($"A download for '{folder}' is already in progress.");
            }

            await docker.RemoveContainerAsync(existing.Id, ct).ConfigureAwait(false);
        }

        _finished.TryRemove(folder, out _);

        var files = await _repoInspector.ListFilesAsync(request.RepoId, request.Revision, request.Token, ct).ConfigureAwait(false);
        var matched = files.Where(f => MatchesAny(f.Name, patterns)).ToList();
        if (matched.Count == 0)
        {
            throw new ArgumentException(BuildNoMatchMessage(request.RepoId, request.Revision, patterns, files), nameof(request));
        }

        long totalBytes = matched.Sum(f => f.Bytes);

        await docker.EnsureNetworkAsync(_options.DockerNetwork, ct).ConfigureAwait(false);

        var env = new List<string> { "HF_HUB_ENABLE_HF_TRANSFER=0" };
        if (!string.IsNullOrEmpty(request.Token))
        {
            env.Add($"HF_TOKEN={request.Token}");
        }

        if (helperUser is not null)
        {
            env.Add($"HOME={DownloadHelperUser.HelperHome}");
            env.Add($"HF_HOME={DownloadHelperUser.HelperHome}/huggingface");
        }

        var labels = new Dictionary<string, string>
        {
            [LabelDownload] = "true",
            [LabelFolder] = folder,
            [LabelRepo] = request.RepoId,
            [LabelRevision] = request.Revision ?? "",
            [LabelTotalBytes] = totalBytes.ToString(CultureInfo.InvariantCulture),
        };

        var jobRequest = new CreateJobContainerRequest(
            Name: $"ss-dl-{folder}",
            Image: _options.VllmImage,
            Entrypoint: ["python3"],
            Cmd: ["-c", DownloadScript, request.RepoId, request.Revision ?? "", $"/models/{folder}", string.Join(",", patterns)],
            Labels: labels,
            Binds: [$"{_options.ModelsHostPath}:/models"],
            Env: env,
            Network: _options.DockerNetwork,
            NetworkAlias: $"ss-dl-{folder}",
            User: helperUser);

        string containerId = await docker.CreateJobContainerAsync(jobRequest, ct).ConfigureAwait(false);
        await docker.StartContainerAsync(containerId, ct).ConfigureAwait(false);
        DownloadLog.DownloadStarted(logger, folder, request.RepoId);

        return new DownloadStatus(folder, request.RepoId, request.Revision, DownloadState.Queued, 0, totalBytes, null, null);
    }

    public async Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default)
    {
        var containers = await docker.ListContainersAsync([LabelDownload], all: true, ct).ConfigureAwait(false);
        var statuses = new List<DownloadStatus>(containers.Count + _finished.Count);
        foreach (var container in containers)
        {
            statuses.Add(await MapAsync(container, ct).ConfigureAwait(false));
        }

        foreach (var finished in _finished.Values)
        {
            if (!statuses.Any(s => string.Equals(s.Folder, finished.Folder, StringComparison.Ordinal)))
            {
                statuses.Add(finished);
            }
        }

        return statuses;
    }

    public async Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default)
    {
        var container = await FindContainerAsync(folder, ct).ConfigureAwait(false);
        if (container is null)
        {
            return _finished.TryGetValue(folder, out var finished) ? finished : null;
        }

        return await MapAsync(container, ct).ConfigureAwait(false);
    }

    public async Task<bool> CancelAsync(string folder, CancellationToken ct = default)
    {
        var container = await FindContainerAsync(folder, ct).ConfigureAwait(false);
        if (container is null)
        {
            // Nothing running any more; forgetting a remembered final state still counts as "removed".
            return _finished.TryRemove(folder, out _);
        }

        await docker.StopContainerAsync(container.Id, timeoutSeconds: 5, ct).ConfigureAwait(false);
        await docker.RemoveContainerAsync(container.Id, ct).ConfigureAwait(false);
        _lastSample.TryRemove(folder, out _);
        DownloadLog.DownloadCancelled(logger, folder);
        return true;
    }

    private async Task<ContainerSummary?> FindContainerAsync(string folder, CancellationToken ct)
    {
        var containers = await docker.ListContainersAsync([$"{LabelFolder}={folder}"], all: true, ct).ConfigureAwait(false);
        return containers.Count > 0 ? containers[0] : null;
    }

    private async Task<DownloadStatus> MapAsync(ContainerSummary container, CancellationToken ct)
    {
        string folder = container.Labels.GetValueOrDefault(LabelFolder, "");
        string repoId = container.Labels.GetValueOrDefault(LabelRepo, "");
        string? revision = container.Labels.TryGetValue(LabelRevision, out string? r) && r.Length > 0 ? r : null;
        long totalBytes = container.Labels.TryGetValue(LabelTotalBytes, out string? t) &&
            long.TryParse(t, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;

        long bytesDownloaded = ComputeBytesOnDisk(folder);
        if (totalBytes > 0)
        {
            // The on-disk size is a proxy; never report more than the expected total.
            bytesDownloaded = Math.Min(bytesDownloaded, totalBytes);
        }

        double? rate = ComputeRate(folder, bytesDownloaded);

        DownloadState state;
        string? error = null;
        switch (container.State)
        {
            case "created":
                state = DownloadState.Queued;
                break;
            case "running":
                state = DownloadState.Downloading;
                break;
            case "exited":
                int exitCode = ParseExitCode(container.Status);
                if (exitCode != 0)
                {
                    state = DownloadState.Failed;
                    error = await ReadFailureLogAsync(container.Id, ct).ConfigureAwait(false);
                }
                else if (totalBytes <= 0 || bytesDownloaded <= 0)
                {
                    // The download process exited cleanly but wrote nothing: either the expected total was
                    // never known (a controller restart lost it) or nothing landed on disk. Either way this
                    // is not a success — never report Completed for a folder with 0 bytes in it.
                    state = DownloadState.Failed;
                    error = totalBytes <= 0
                        ? "The download finished, but no files in the repo matched the allow-patterns (expected total was 0 bytes), so nothing was fetched."
                        : "The download finished, but 0 bytes were written to disk.";
                }
                else
                {
                    state = DownloadState.Completed;
                }

                break;
            case "dead":
                // Docker could neither run nor remove it: it never finishes on its own, and it keeps HF_TOKEN in its
                // configuration until removed (W2 review MN-092).
                state = DownloadState.Failed;
                error = "The download helper container is dead (Docker could not run or remove it).";
                break;
            default: // restarting, paused, removing
                state = DownloadState.Downloading;
                break;
        }

        var status = new DownloadStatus(folder, repoId, revision, state, bytesDownloaded, totalBytes, rate, error);
        if (state is DownloadState.Completed or DownloadState.Failed)
        {
            await RememberAndRemoveAsync(container.Id, status with { BytesPerSecond = null }, ct).ConfigureAwait(false);
        }

        return status;
    }

    /// <summary>Records a finished download's outcome, then removes its helper container (best effort).</summary>
    private async Task RememberAndRemoveAsync(string containerId, DownloadStatus final, CancellationToken ct)
    {
        _finished[final.Folder] = final;
        _lastSample.TryRemove(final.Folder, out _);
        try
        {
            await docker.RemoveContainerAsync(containerId, ct).ConfigureAwait(false);
            DownloadLog.DownloadContainerRemoved(logger, final.Folder, final.State);
        }
        catch (Exception ex) when (ex is DockerApiException or HttpRequestException or InvalidOperationException or IOException)
        {
            // Left for the next poll (or StartAsync) to remove; the remembered state is already recorded.
            DownloadLog.RemoveDownloadContainerFailed(logger, ex, containerId);
        }
    }

    private long ComputeBytesOnDisk(string folder)
    {
        string dir = Path.Combine(_storage.ModelsLocalPath, folder);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(dir))
        {
            return 0;
        }

        // huggingface_hub keeps its own bookkeeping (and partial ".incomplete" files) under {folder}/.cache;
        // counting it double-counts bytes and pushed progress past 100%.
        string cacheDir = Path.Combine(dir, ".cache") + Path.DirectorySeparatorChar;
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(cacheDir, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                total += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // File is mid-write or was just removed by the download process; skip this sample.
            }
        }

        return total;
    }

    private double? ComputeRate(string folder, long bytesNow)
    {
        var now = DateTimeOffset.UtcNow;
        double? rate = null;
        if (_lastSample.TryGetValue(folder, out var previous))
        {
            double elapsedSeconds = (now - previous.At).TotalSeconds;
            if (elapsedSeconds > 0.5 && bytesNow >= previous.Bytes)
            {
                rate = (bytesNow - previous.Bytes) / elapsedSeconds;
            }
        }

        _lastSample[folder] = (bytesNow, now);
        return rate;
    }

    private async Task<string?> ReadFailureLogAsync(string containerId, CancellationToken ct)
    {
        try
        {
            var lines = new List<string>();
            await foreach (var line in docker.GetLogsAsync(containerId, tail: 20, follow: false, ct).ConfigureAwait(false))
            {
                lines.Add(line);
            }

            return lines.Count > 0 ? string.Join('\n', lines) : null;
        }
        catch (Exception ex)
        {
            DownloadLog.ReadDownloadLogsFailed(logger, ex, containerId);
            return null;
        }
    }

    private static bool MatchesAny(string fileName, IReadOnlyList<string> patterns) => HuggingFacePatterns.MatchesAny(fileName, patterns);

    /// <summary>
    /// Message for the zero-match pre-flight refusal: names the patterns that matched nothing and, using the
    /// same grouping <see cref="HuggingFaceRepoInspector"/> exposes to the editor, what the repo actually has
    /// (e.g. a GGUF-only repo when the caller asked for the safetensors defaults).
    /// </summary>
    private static string BuildNoMatchMessage(string repoId, string? revision, IReadOnlyList<string> patterns, IReadOnlyList<RepoFile> files)
    {
        string patternList = string.Join(", ", patterns);
        if (files.Count == 0)
        {
            return $"No files match {patternList}; '{repoId}' has no files in its repo listing.";
        }

        var inspection = HuggingFaceRepoInspector.BuildInspection(repoId, revision, files);
        return $"No files match {patternList}; {DescribeContents(inspection)}.";
    }

    private static string DescribeContents(RepoInspection inspection)
    {
        int ggufCount = inspection.Variants.Sum(v => v.FileCount) + inspection.AuxiliaryFiles.Count;
        if (ggufCount > 0)
        {
            return $"this repo contains {ggufCount} .gguf file{(ggufCount == 1 ? "" : "s")} (GGUF format)";
        }

        var topExtensions = inspection.AllFiles
            .GroupBy(f => ExtensionLabel(f.Name))
            .OrderByDescending(g => g.Count())
            .Take(3)
            .Select(g => $"{g.Count()} {g.Key} file{(g.Count() == 1 ? "" : "s")}");

        return $"this repo contains {string.Join(", ", topExtensions)}";
    }

    private static string ExtensionLabel(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return ext.Length > 0 ? ext : "extensionless";
    }

    private static string DeriveFolderName(string repoId)
    {
        int slash = repoId.LastIndexOf('/');
        string name = slash >= 0 ? repoId[(slash + 1)..] : repoId;
        return SanitizeFolder(name);
    }

    private static string SanitizeFolder(string folder) => InvalidFolderChars().Replace(folder, "-");

    private static int ParseExitCode(string status)
    {
        var match = ExitCodePattern().Match(status);
        return match.Success && int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out int code) ? code : -1;
    }

    [GeneratedRegex(@"Exited \((-?\d+)\)")]
    private static partial Regex ExitCodePattern();

    [GeneratedRegex(@"[^A-Za-z0-9._-]")]
    private static partial Regex InvalidFolderChars();
}

internal static partial class DownloadLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Started Hugging Face download of {RepoId} into {Folder}.")]
    public static partial void DownloadStarted(ILogger logger, string folder, string repoId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Cancelled download for {Folder}.")]
    public static partial void DownloadCancelled(ILogger logger, string folder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed finished download container for {Folder} ({State}).")]
    public static partial void DownloadContainerRemoved(ILogger logger, string folder, DownloadState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to remove finished download container {ContainerId}; will retry on the next poll.")]
    public static partial void RemoveDownloadContainerFailed(ILogger logger, Exception exception, string containerId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to read logs for download container {ContainerId}.")]
    public static partial void ReadDownloadLogsFailed(ILogger logger, Exception exception, string containerId);
}
