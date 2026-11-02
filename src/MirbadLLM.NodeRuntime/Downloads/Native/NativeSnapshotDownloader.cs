using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>
/// One native snapshot download in progress: where its files go, its manifest, and the token, which lives here in memory
/// only and is dropped with <see cref="ForgetToken"/> when the download ends (design §6.4, design-p3p4 §A.8).
/// </summary>
public sealed class NativeDownloadJob
{
    /// <summary>Creates a job.</summary>
    /// <param name="folder">Folder name (messages and logs).</param>
    /// <param name="workDirectory">Where the files are written: <c>{folder}.partial</c>, or the folder itself for <c>existing: complete</c>.</param>
    /// <param name="finalDirectory">Renamed to when every file is verified; null = in place.</param>
    /// <param name="manifest">The manifest to work from (already written to <paramref name="workDirectory"/>).</param>
    /// <param name="token">Hugging Face token; null for a public repo.</param>
    public NativeDownloadJob(string folder, string workDirectory, string? finalDirectory, DownloadManifest manifest, string? token)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(workDirectory);
        ArgumentNullException.ThrowIfNull(manifest);
        Folder = folder;
        WorkDirectory = workDirectory;
        FinalDirectory = finalDirectory;
        Manifest = manifest;
        Token = string.IsNullOrEmpty(token) ? null : token;
    }

    /// <summary>Folder name.</summary>
    public string Folder { get; }

    /// <summary>Where the files are written.</summary>
    public string WorkDirectory { get; }

    /// <summary>Rename target, or null.</summary>
    public string? FinalDirectory { get; }

    /// <summary>The current manifest (replaced as files are verified).</summary>
    public DownloadManifest Manifest { get; internal set; }

    /// <summary>The token, until <see cref="ForgetToken"/>.</summary>
    public string? Token { get; private set; }

    /// <summary>Drops the token reference.</summary>
    public void ForgetToken() => Token = null;

    /// <summary>Never prints the token.</summary>
    public override string ToString() => $"NativeDownloadJob {{ Folder = {Folder}, Token = {(Token is null ? "null" : "***")} }}";
}

/// <summary>
/// The engine of the native Hugging Face downloader (design §6.4 "Native downloader"): resolves the immutable revision,
/// builds the manifest, then fetches each file at <c>resolve/{sha}/{file}</c> with <see cref="RangeDownloader"/> (range
/// resume, LFS SHA-256 or git blob SHA-1, one retry after a mismatch, a few resumed attempts after transient errors),
/// records each verified file in the manifest, and finalizes with one rename of <c>{folder}.partial</c> to
/// <c>{folder}</c>. Confinement: every name passes <see cref="RepoPathRules"/>, links under the folder are refused, and
/// on Windows each opened file is checked by handle.
/// </summary>
public sealed class NativeSnapshotDownloader
{
    private readonly NativeDownloadOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly RangeDownloader _range;

    /// <summary>Creates the engine.</summary>
    /// <param name="http">Client for the Hub and its CDN; its <see cref="HttpClient.BaseAddress"/> is the endpoint (default <c>https://huggingface.co/</c>).</param>
    /// <param name="options">Rules; defaults when null.</param>
    /// <param name="time">Clock; system when null.</param>
    /// <param name="logger">Logger; none when null.</param>
    /// <param name="openWrite">File opener (tests); <see cref="RangeDownloader.OpenFile"/> when null.</param>
    public NativeSnapshotDownloader(
        HttpClient http, NativeDownloadOptions? options = null, TimeProvider? time = null, ILogger? logger = null, Func<string, Stream>? openWrite = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _options = options ?? new NativeDownloadOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        var endpoint = http.BaseAddress ?? new Uri("https://huggingface.co/");
        Transport = new SecureDownloadTransport(http, endpoint, _options);
        Hub = new HuggingFaceHubClient(Transport, endpoint);
        _range = new RangeDownloader(Transport, _time, openWrite);
    }

    /// <summary>The Hub client.</summary>
    public HuggingFaceHubClient Hub { get; }

    /// <summary>The transport (HTTPS rule).</summary>
    public SecureDownloadTransport Transport { get; }

    /// <summary>
    /// The manifest for <paramref name="snapshot"/>'s files matching <paramref name="patterns"/>, carrying over the
    /// verified state of files from <paramref name="previous"/> whose size and digests are unchanged. Throws
    /// <see cref="DownloadPathException"/> when a selected name breaks the confinement rules.
    /// </summary>
    public static DownloadManifest BuildManifest(
        HubSnapshot snapshot, string? revision, IReadOnlyList<string> patterns, bool authenticated, DownloadManifest? previous, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(patterns);
        var selected = snapshot.Files.Where(f => HuggingFacePatterns.MatchesAny(f.Path, patterns)).ToList();
        if (RepoPathRules.CheckAll(selected.Select(f => f.Path)) is { } error)
        {
            throw new DownloadPathException($"Refused repo file {error}.");
        }

        var before = previous?.Files.ToDictionary(f => f.Path, StringComparer.Ordinal) ?? [];
        var files = selected.Select(f =>
        {
            var file = new DownloadManifestFile { Path = f.Path, Size = f.Size, LfsSha256 = f.LfsSha256, BlobSha1 = f.BlobSha1 };
            return before.TryGetValue(f.Path, out var old) && old.State == DownloadFileState.Verified &&
                old.Size == f.Size && old.LfsSha256 == f.LfsSha256 && old.BlobSha1 == f.BlobSha1
                ? file with { State = DownloadFileState.Verified }
                : file;
        }).ToList();

        return new DownloadManifest
        {
            RepoId = snapshot.RepoId,
            Revision = revision,
            CommitSha = snapshot.CommitSha,
            AllowPatterns = patterns.ToList(),
            Authenticated = authenticated,
            State = DownloadManifestState.Downloading,
            CreatedAt = previous?.CreatedAt ?? now,
            UpdatedAt = now,
            Files = files,
        };
    }

    /// <summary>
    /// Downloads every file of <paramref name="job"/> that is not verified yet, then finalizes. <paramref name="onProgress"/>
    /// receives the bytes present (all files) as they grow. Throws <see cref="NativeDownloadException"/> subtypes for the
    /// failures of §6.4 (state kept, resumable), <see cref="OperationCanceledException"/> when cancelled (state kept).
    /// </summary>
    public async Task RunAsync(NativeDownloadJob job, Action<long>? onProgress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (RepoPathRules.FindLink(job.WorkDirectory) is { } link)
        {
            throw new DownloadPathException($"'{RepoPathRules.Printable(link)}' under the download folder is a link; links are refused, never followed.");
        }

        var manifest = job.Manifest;
        long[] lengths = manifest.Files.Select(f => PresentLength(job.WorkDirectory, f)).ToArray();
        void Report() => onProgress?.Invoke(lengths.Sum());
        Report();

        for (int i = 0; i < manifest.Files.Count; i++)
        {
            var file = manifest.Files[i];
            string target = RepoPathRules.PrepareTarget(job.WorkDirectory, file.Path);
            if (file.State == DownloadFileState.Verified && new FileInfo(target) is { Exists: true } present && present.Length == file.Size)
            {
                continue;
            }

            int index = i;
            var request = new RangeDownloadRequest
            {
                Url = Hub.FileUrl(manifest.RepoId, manifest.CommitSha, file.Path),
                FilePath = target,
                DisplayName = file.Path,
                ExpectedLength = file.Size,
                Digest = file.Digest(),
                Credential = HuggingFaceHubClient.Credential(job.Token),
                ConfinementRoot = job.WorkDirectory,
            };

            var result = await FetchVerifiedAsync(job, request, len =>
            {
                lengths[index] = len;
                Report();
            }, ct).ConfigureAwait(false);

            lengths[index] = result.Length;
            Report();
            NativeDownloadLog.FileVerified(_logger, job.Folder, file.Path, result.Length, result.Response);
            var files = manifest.Files.ToList();
            files[i] = file with { State = DownloadFileState.Verified };
            manifest = manifest with { Files = files, UpdatedAt = _time.GetUtcNow() };
            manifest.Write(job.WorkDirectory);
            job.Manifest = manifest;
        }

        ct.ThrowIfCancellationRequested();
        string finalFolder = job.WorkDirectory;
        if (job.FinalDirectory is { } destination)
        {
            if (Directory.Exists(destination) || File.Exists(destination))
            {
                throw new DownloadDestinationExistsException(
                    $"'{job.Folder}' appeared while the download ran; the verified files stay in '{job.Folder}.partial'.");
            }

            await MoveAsync(job.WorkDirectory, destination, ct).ConfigureAwait(false);
            DirectorySync.TryFlushParent(destination);
            finalFolder = destination;
        }

        manifest = manifest with { State = DownloadManifestState.Completed, UpdatedAt = _time.GetUtcNow() };
        manifest.Write(finalFolder);
        job.Manifest = manifest;
        NativeDownloadLog.Finalized(_logger, job.Folder);
    }

    private async Task<RangeDownloadResult> FetchVerifiedAsync(NativeDownloadJob job, RangeDownloadRequest request, Action<long> onLength, CancellationToken ct)
    {
        int transientFailures = 0;
        int mismatches = 0;
        while (true)
        {
            RangeDownloadResult result;
            try
            {
                result = await _range.DownloadAsync(request, onLength, ct).ConfigureAwait(false);
            }
            catch (DownloadTransientException ex) when (++transientFailures < Math.Max(1, _options.MaxAttemptsPerFile))
            {
                NativeDownloadLog.FileTransientRetry(_logger, job.Folder, request.DisplayName, transientFailures, ex.Message);
                await Task.Delay(_options.RetryDelay, _time, ct).ConfigureAwait(false);
                continue;
            }

            if (result.Verified)
            {
                return result;
            }

            File.Delete(request.FilePath);
            onLength(0);
            if (++mismatches > 1)
            {
                throw new DownloadIntegrityException(
                    $"{request.DisplayName} failed its integrity check twice (expected {(request.Digest?.ToString() ?? "size " + request.ExpectedLength)}); the file was deleted.");
            }

            NativeDownloadLog.FileMismatchRetry(_logger, job.Folder, request.DisplayName);
        }
    }

    private async Task MoveAsync(string source, string destination, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (IOException) when (attempt < 5 && !Directory.Exists(destination))
            {
                // Windows: a scanner or indexer may hold a handle on a fresh file for a moment.
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), _time, ct).ConfigureAwait(false);
            }
        }
    }

    private static long PresentLength(string folder, DownloadManifestFile file)
    {
        var info = new FileInfo(Path.Combine(folder, Path.Combine(file.Path.Split('/'))));
        return info.Exists ? Math.Min(info.Length, file.Size) : 0;
    }
}
