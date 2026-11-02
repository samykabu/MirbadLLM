using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Operations;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Downloads.Native;

#pragma warning disable CA1707, CA2000, xUnit1051 // underscore test names; the app owns its services; hand-written fakes ignore the test token.

namespace MirbadLLM.UnitTests.Downloads;

/// <summary>
/// The native Hugging Face downloader (MN-213; design §6.4, §13 item 6 "native download", design-p3p4 §A.2.1, §A.2.3,
/// §A.8) against a fake Hub on loopback: commit pinning, the manifest, 200/206/416 range handling, LFS SHA-256 and git
/// blob SHA-1 with one retry, the disk pre-check and a full disk mid-write, path-traversal names and links, atomic
/// finalize, existing-folder rules, cancel, HTTPS-only redirects and the credential origin, an agent restart (public:
/// resumes; authenticated: paused until the token is given again, through the real storage endpoints and the real
/// operation log), and the token never in logs, the operation log, the manifest or error text.
/// </summary>
public sealed partial class NativeDownloaderTests : IAsyncDisposable
{
    private const string Token = "hf_NativeSecretToken0123456789";
    private const string RepoId = "Qwen/Qwen3-Embedding-4B-GGUF";
    private const string Sha1 = "1111111111111111111111111111111111111111";
    private const string Sha2 = "2222222222222222222222222222222222222222";
    private const string Folder = "Qwen3-Embedding-4B";
    private static readonly string[] GgufPatterns = ["*.json", "*Q8_0.gguf"];
    private static readonly MasterTriple Master = new("master-1", 3, "boot-a");

    private readonly string _base;
    private readonly string _root;
    private readonly FakeDisk _disk = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly List<IAsyncDisposable> _disposables = [];
    private FakeHub _hub = null!;

    public NativeDownloaderTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "mirbadllm-native-dl-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "models");
        Directory.CreateDirectory(_root);
    }

    public async ValueTask DisposeAsync()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            await _disposables[i].DisposeAsync();
        }

        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }

        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    // ---------------------------------------------------------------- set-up helpers

    private async Task<FakeRepo> HubWithRepoAsync(string? requiredToken = null, int ggufSize = 300_000)
    {
        _hub = await FakeHub.StartAsync();
        return _hub.AddRepo(RepoId, requiredToken).Commit(Sha1,
            FakeFile.Small("config.json", """{"architectures":["Qwen3Model"]}"""),
            FakeFile.Large("Qwen3-Embedding-4B-Q8_0.gguf", ggufSize, seed: 7),
            FakeFile.Small("sub/tokenizer.json", """{"version":"1.0"}"""),
            FakeFile.Large("Qwen3-Embedding-4B-F16.gguf", 1000, seed: 8),
            FakeFile.Small("README.md", "# readme"));
    }

    private HttpClient HubClient(Uri? baseAddress = null) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = baseAddress ?? _hub.BaseUri };

    private static NativeDownloadOptions TestOptions(Action<NativeDownloadOptions>? configure = null)
    {
        var options = new NativeDownloadOptions
        {
            AllowLoopbackHttp = true,
            RetryDelay = TimeSpan.Zero,
            ReadIdleTimeout = TimeSpan.FromSeconds(20),
            BufferSize = 8 * 1024,
        };
        configure?.Invoke(options);
        return options;
    }

    private NativeDownloadService Service(
        NativeDownloadOptions? options = null, Func<string, Stream>? openWrite = null, HttpClient? http = null, bool track = true)
    {
        var loggers = LoggerFactory.Create(b => b.AddProvider(_logs).SetMinimumLevel(LogLevel.Trace));
        var service = new NativeDownloadService(
            http ?? HubClient(),
            new ModelRootPaths(_root),
            _disk,
            Options.Create(new AgentStorageOptions { ModelsRoot = _root }),
            TimeProvider.System,
            loggers.CreateLogger<NativeDownloadService>(),
            options ?? TestOptions(),
            openWrite);
        if (track)
        {
            _disposables.Add(service);
        }

        return service;
    }

    private static DownloadRequest Request(string? token = null, IReadOnlyList<string>? patterns = null, string? revision = null, string folder = Folder) =>
        new(RepoId, revision, token, folder, patterns ?? GgufPatterns);

    private static async Task<DownloadStatus> RunToEndAsync(NativeDownloadService service, DownloadRequest request)
    {
        await service.StartAsync(request);
        await service.WhenFinishedAsync(request.Folder!).WaitAsync(TimeSpan.FromSeconds(60));
        return (await service.GetAsync(request.Folder!))!;
    }

    private string FinalPath(params string[] parts) => Path.Combine([_root, Folder, .. parts]);

    private string PartialPath(params string[] parts) => Path.Combine([_root, Folder + ".partial", .. parts]);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for " + what);
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Leaves a half-downloaded <c>.partial</c> with exactly <paramref name="bytes"/> of the GGUF (real code: the Hub stalls
    /// after that many bytes, the download is cancelled, which keeps its files).
    /// </summary>
    private async Task<FakeFile> PartialDownloadAsync(FakeRepo repo, long bytes)
    {
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.StallAfter = bytes;
        var service = Service();
        await service.StartAsync(Request());
        await gguf.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForLengthAsync(PartialPath(gguf.Path), bytes);
        Assert.True(await service.CancelAsync(Folder));
        gguf.StallAfter = null;
        Assert.Equal(DownloadState.Cancelled, (await service.GetAsync(Folder))!.State);
        Assert.Equal(bytes, new FileInfo(PartialPath(gguf.Path)).Length);
        return gguf;
    }

    private static Task WaitForLengthAsync(string path, long length) =>
        WaitUntilAsync(() => Task.FromResult(new FileInfo(path) is { Exists: true } f && f.Length >= length), $"{Path.GetFileName(path)} to reach {length} bytes");

    // ---------------------------------------------------------------- the happy path

    [Fact]
    public async Task Download_PinsTheCommit_VerifiesEveryFile_AndRenamesThePartialFolderAtomically()
    {
        var repo = await HubWithRepoAsync();
        var moved = FakeFile.Large("Qwen3-Embedding-4B-Q8_0.gguf", 300_000, seed: 99);
        repo.Commits[Sha2] = [FakeFile.Small("config.json", "{}"), moved];

        // The branch moves to another commit right after the listing: every file must still come from the first one.
        repo.AfterListing = () => repo.Branches["main"] = Sha2;
        var service = Service();

        var started = await service.StartAsync(Request());
        Assert.Equal(DownloadState.Queued, started.State);
        Assert.Equal(300_000 + repo.File(Sha1, "config.json").Content.Length + repo.File(Sha1, "sub/tokenizer.json").Content.Length, started.TotalBytes);
        await service.WhenFinishedAsync(Folder);
        var done = (await service.GetAsync(Folder))!;

        Assert.Equal(DownloadState.Completed, done.State);
        Assert.Null(done.Error);
        Assert.Equal(done.TotalBytes, done.BytesDownloaded);
        Assert.False(Directory.Exists(PartialPath()));
        Assert.Equal(repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf").Content, await File.ReadAllBytesAsync(FinalPath("Qwen3-Embedding-4B-Q8_0.gguf")));
        Assert.Equal(repo.File(Sha1, "sub/tokenizer.json").Content, await File.ReadAllBytesAsync(FinalPath("sub", "tokenizer.json")));
        Assert.False(File.Exists(FinalPath("README.md")));
        Assert.False(File.Exists(FinalPath("Qwen3-Embedding-4B-F16.gguf")));

        var files = _hub.Requests.Where(r => r.Path.Contains("/resolve/", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, files.Count);
        Assert.All(files, r => Assert.Contains("/resolve/" + Sha1 + "/", r.Path, StringComparison.Ordinal));

        var manifest = DownloadManifest.TryRead(FinalPath())!;
        Assert.Equal(DownloadManifestState.Completed, manifest.State);
        Assert.Equal(Sha1, manifest.CommitSha);
        Assert.Equal(RepoId, manifest.RepoId);
        Assert.False(manifest.Authenticated);
        Assert.All(manifest.Files, f => Assert.Equal(DownloadFileState.Verified, f.State));
        Assert.Equal(repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf").Sha256, manifest.Files.Single(f => f.Path.EndsWith(".gguf", StringComparison.Ordinal)).LfsSha256);
        Assert.Equal(repo.File(Sha1, "config.json").BlobSha1, manifest.Files.Single(f => f.Path == "config.json").BlobSha1);
    }

    [Fact]
    public async Task Contributor_ReportsDownloadsHfNative_Once()
    {
        var contributor = new NativeDownloadContributor();
        var empty = new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "1.0.0",
            Os = new NodeOsInfo("windows", "Windows 11"),
            Arch = "amd64",
            Memory = new NodeMemoryInfo(MemoryModel.Discrete, 127),
            Features = [AgentFeatures.TelemetryV1],
        };

        var once = await contributor.ContributeAsync(empty);
        var twice = await contributor.ContributeAsync(once);

        Assert.Equal([AgentFeatures.TelemetryV1, AgentFeatures.DownloadsHfNative], once.Features);
        Assert.Equal(once.Features, twice.Features);
        Assert.True(AgentRoutes.DownloadStart.IsOfferedBy(once.Features));
    }

    // ---------------------------------------------------------------- range handling (200 / 206 / 416)

    [Fact]
    public async Task Resume_206_AppendsFromTheLocalLength()
    {
        var repo = await HubWithRepoAsync();
        var gguf = await PartialDownloadAsync(repo, 100_000);

        var done = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Completed, done.State);
        var last = _hub.FileRequests(gguf.Path)[^1];
        Assert.Equal("bytes=100000-", last.Range);
        Assert.Equal(206, last.Status);
        Assert.Equal(200_000, last.BodyBytes);
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(FinalPath(gguf.Path)));
    }

    [Fact]
    public async Task Resume_200_WhenTheServerIgnoresTheRange_RewritesTheWholeFile()
    {
        var repo = await HubWithRepoAsync();
        var gguf = await PartialDownloadAsync(repo, 100_000);
        gguf.IgnoreRange = true;

        var done = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Completed, done.State);
        var last = _hub.FileRequests(gguf.Path)[^1];
        Assert.Equal("bytes=100000-", last.Range);
        Assert.Equal(200, last.Status);
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(FinalPath(gguf.Path)));
    }

    [Fact]
    public async Task Resume_416_WithTheWholeFilePresent_OnlyVerifiesIt()
    {
        var repo = await HubWithRepoAsync();
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");

        // The file is complete on disk but its manifest entry is still pending (a crash after the last byte): the resume
        // asks for bytes={size}-, gets 416 and verifies the SHA-256 without fetching anything.
        var tokenizer = repo.File(Sha1, "sub/tokenizer.json");
        tokenizer.StatusOverride = 404;
        var failed = await RunToEndAsync(Service(), Request());
        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(PartialPath(gguf.Path)));
        MarkPending(PartialPath(), gguf.Path);
        tokenizer.StatusOverride = null;
        int before = _hub.FileRequests(gguf.Path).Count;

        var done = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Completed, done.State);
        var resumed = _hub.FileRequests(gguf.Path).Skip(before).ToList();
        var only = Assert.Single(resumed);
        Assert.Equal($"bytes={gguf.Content.Length}-", only.Range);
        Assert.Equal(416, only.Status);
        Assert.Equal(0, only.BodyBytes);
    }

    [Fact]
    public async Task Resume_416_WithAShorterFile_DeletesItAndStartsOver()
    {
        var repo = await HubWithRepoAsync();
        var gguf = await PartialDownloadAsync(repo, 100_000);
        gguf.Refuse416Remaining = 1;

        var done = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Completed, done.State);
        var tail = _hub.FileRequests(gguf.Path).TakeLast(2).ToList();
        Assert.Equal(("bytes=100000-", 416), (tail[0].Range, tail[0].Status));
        Assert.Equal((null, 200), (tail[1].Range, tail[1].Status));
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(FinalPath(gguf.Path)));
    }

    [Fact]
    public async Task AConnectionDroppedMidFile_IsResumedByRangeInTheSameRun()
    {
        var repo = await HubWithRepoAsync();
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.DropAfter = 100_000;
        gguf.DropRemaining = 1;

        var done = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Completed, done.State);
        var requests = _hub.FileRequests(gguf.Path);
        Assert.Equal(2, requests.Count);
        Assert.Null(requests[0].Range);
        Assert.StartsWith("bytes=", requests[1].Range, StringComparison.Ordinal);
        Assert.Equal(206, requests[1].Status);
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(FinalPath(gguf.Path)));
        Assert.Contains(_logs.Lines, l => l.Contains("attempt 1 failed", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- integrity

    [Fact]
    public async Task LfsSha256Mismatch_DeletesTheFileAndRetriesOnce()
    {
        var repo = await HubWithRepoAsync();
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.CorruptRemaining = 1;

        var done = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Completed, done.State);
        var requests = _hub.FileRequests(gguf.Path);
        Assert.Equal(2, requests.Count);
        Assert.Null(requests[1].Range); // deleted, then fetched whole
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(FinalPath(gguf.Path)));
    }

    [Fact]
    public async Task LfsSha256MismatchTwice_FailsTheDownload_AndLeavesNoBadFile()
    {
        var repo = await HubWithRepoAsync();
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.CorruptRemaining = 2;

        var failed = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("integrity check twice", failed.Error, StringComparison.Ordinal);
        Assert.Contains("sha256:", failed.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(PartialPath(gguf.Path)));
        Assert.False(Directory.Exists(FinalPath()));
        Assert.Equal(DownloadManifestState.Failed, DownloadManifest.TryRead(PartialPath())!.State);
        Assert.Equal(2, _hub.FileRequests(gguf.Path).Count);
    }

    [Fact]
    public async Task GitBlobSha1Mismatch_OfASmallFile_IsCaughtToo()
    {
        var repo = await HubWithRepoAsync();
        repo.File(Sha1, "config.json").CorruptRemaining = 2;

        var failed = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("config.json", failed.Error, StringComparison.Ordinal);
        Assert.Contains("sha1:", failed.Error, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- disk

    [Fact]
    public async Task DiskPreCheck_Refuses507_AndCountsTheBytesAlreadyPresent()
    {
        var repo = await HubWithRepoAsync();
        long total = repo.Commits[Sha1].Where(f => f.Path != "README.md" && !f.Path.Contains("F16", StringComparison.Ordinal)).Sum(f => (long)f.Content.Length);
        _disk.Space = new DiskSpace(FreeBytes: (long)Math.Ceiling(total * 1.05) - 1, TotalBytes: 1L << 40);

        var refused = await Assert.ThrowsAsync<AgentException>(() => Service().StartAsync(Request()));
        Assert.Equal(ErrorCodes.InsufficientDisk, refused.Code);
        Assert.Equal(507, refused.StatusCode);
        Assert.False(Directory.Exists(PartialPath()));

        // With 200 000 bytes of the GGUF already on disk the same free space is enough: only the remaining bytes count.
        _disk.Space = new DiskSpace(FreeBytes: 1L << 40, TotalBytes: 1L << 41);
        await PartialDownloadAsync(repo, 200_000);
        long present = Directory.EnumerateFiles(PartialPath(), "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith(".mirbadllm", StringComparison.Ordinal)).Sum(f => new FileInfo(f).Length);
        _disk.Space = new DiskSpace(FreeBytes: (long)Math.Ceiling((total - present) * 1.05), TotalBytes: 1L << 40);

        var done = await RunToEndAsync(Service(), Request());
        Assert.Equal(DownloadState.Completed, done.State);
    }

    [Fact]
    public async Task DiskFullMidWrite_FailsResumable_ThenTheNextStartResumesByRange()
    {
        var repo = await HubWithRepoAsync();
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        Stream FullAt120K(string path) =>
            path.EndsWith(".gguf", StringComparison.Ordinal) ? new FullDiskStream(RangeDownloader.OpenFile(path), 120_000) : RangeDownloader.OpenFile(path);

        var failed = await RunToEndAsync(Service(openWrite: FullAt120K), Request());

        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("volume is full", failed.Error, StringComparison.Ordinal);
        Assert.Contains("start the download again to resume", failed.Error, StringComparison.Ordinal);
        Assert.Equal(120_000, new FileInfo(PartialPath(gguf.Path)).Length);
        Assert.Equal(DownloadManifestState.Failed, DownloadManifest.TryRead(PartialPath())!.State);
        Assert.Single(_hub.FileRequests(gguf.Path)); // a full disk is not retried

        var done = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Completed, done.State);
        Assert.Equal("bytes=120000-", _hub.FileRequests(gguf.Path)[^1].Range);
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(FinalPath(gguf.Path)));
    }

    // ---------------------------------------------------------------- path confinement

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("sub/../../evil.json")]
    [InlineData("/etc/evil.json")]
    [InlineData("C:/Windows/evil.json")]
    [InlineData("C:evil.json")]
    [InlineData("sub\\evil.json")]
    [InlineData("\\\\server\\share\\evil.json")]
    [InlineData("//server/share/evil.json")]
    [InlineData("config.json:hidden")]
    [InlineData("CON.json")]
    [InlineData("sub/nul")]
    [InlineData("COM1")]
    [InlineData("lpt9.txt")]
    [InlineData("trailing.json.")]
    [InlineData("trailing.json ")]
    [InlineData("sub./x.json")]
    [InlineData("a//b.json")]
    [InlineData("./a.json")]
    [InlineData("bad\u0001.json")]
    [InlineData("star*.json")]
    [InlineData(".mirbadllm-download.json")]
    public async Task PathTraversalAndWindowsReservedNames_RefuseTheWholeDownload(string name)
    {
        var repo = await HubWithRepoAsync();
        repo.Commits[Sha1].Add(new FakeFile(name, [1, 2, 3], lfs: false));

        var refused = await Assert.ThrowsAsync<AgentException>(() => Service().StartAsync(Request(patterns: ["*"])));

        Assert.Equal(ErrorCodes.InvalidRequest, refused.Code);
        Assert.Contains("Refused repo file", refused.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(PartialPath()));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_base, "evil*", SearchOption.AllDirectories));
        Assert.DoesNotContain(_hub.Requests, r => r.Path.Contains("/resolve/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData(".gitattributes")]
    [InlineData("sub/dir/tokenizer.json")]
    [InlineData("Q4_K_M/model-00001-of-00002.gguf")]
    [InlineData("mmproj-F16.gguf")]
    [InlineData("console.json")]
    [InlineData("com10.json")]
    public void RepoPathRules_AcceptPlainNames(string name) => Assert.Null(RepoPathRules.Check(name));

    [Theory]
    [InlineData("../x.json")]
    [InlineData("a/./x.json")]
    [InlineData("a//x.json")]
    public void RepoPathRules_NameTheDotAndEmptySegmentRule(string name) =>
        Assert.Contains("empty, '.' or '..' segment", RepoPathRules.Check(name), StringComparison.Ordinal);

    [Fact]
    public void RepoPathRules_RefuseCaseInsensitiveDuplicates_AndFileDirectoryClashes()
    {
        Assert.Contains("twice", RepoPathRules.CheckAll(["Config.json", "config.JSON"]), StringComparison.Ordinal);
        Assert.Contains("both a file and a directory", RepoPathRules.CheckAll(["a", "A/b.json"]), StringComparison.Ordinal);
        Assert.Null(RepoPathRules.CheckAll(["a/b.json", "a/c.json", "d.json"]));
    }

    [Fact]
    public async Task ALinkUnderThePartialFolder_IsRefused_NeverFollowed()
    {
        var repo = await HubWithRepoAsync();
        await PartialDownloadAsync(repo, 50_000);
        string outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);
        if (!TryCreateDirectoryLink(PartialPath("sub"), outside))
        {
            Assert.Skip("This account cannot create directory symbolic links here.");
        }

        var refused = await Assert.ThrowsAsync<AgentException>(() => Service().StartAsync(Request()));

        Assert.Equal(ErrorCodes.InvalidRequest, refused.Code);
        Assert.Contains("is a link", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public async Task ALinkPlantedAfterTheStartChecks_IsCaughtBeforeAnyByteGoesThroughIt()
    {
        var repo = await HubWithRepoAsync();
        string outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);

        // config.json is fetched first; while it is served, sub/ is replaced by a link to a folder outside the models root.
        var config = repo.File(Sha1, "config.json");
        config.StallAfter = 1;
        var service = Service();
        await service.StartAsync(Request());
        await config.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (!TryCreateDirectoryLink(PartialPath("sub"), outside))
        {
            config.StallRelease.TrySetResult();
            Assert.Skip("This account cannot create directory symbolic links here.");
        }

        config.StallAfter = null;
        config.StallRelease.TrySetResult();
        await service.WhenFinishedAsync(Folder).WaitAsync(TimeSpan.FromSeconds(60));
        var failed = (await service.GetAsync(Folder))!;

        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("link", failed.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    [Trait("Os", "Windows")]
    public async Task OnWindows_AFileReachedThroughALink_IsRefusedByItsHandle_AndNothingLandsOutside()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only: confinement by GetFinalPathNameByHandle (design-p3p4 A.2.3).");
        }

        var repo = await HubWithRepoAsync();
        var config = repo.File(Sha1, "config.json");
        string work = Path.Combine(_base, "work");
        string outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(outside);
        if (!TryCreateDirectoryLink(Path.Combine(work, "sub"), outside))
        {
            Assert.Skip("This account cannot create directory symbolic links here.");
        }

        var downloader = new RangeDownloader(new SecureDownloadTransport(HubClient(), _hub.BaseUri, TestOptions()));
        RangeDownloadRequest RequestFor(string path) => new()
        {
            Url = new Uri(_hub.BaseUri, $"{RepoId}/resolve/{Sha1}/config.json"),
            FilePath = path,
            DisplayName = "config.json",
            ExpectedLength = config.Content.Length,
            Digest = ContentDigest.GitBlobSha1(config.BlobSha1, config.Content.Length),
            ConfinementRoot = work,
        };

        var refused = await Assert.ThrowsAsync<DownloadPathException>(() => downloader.DownloadAsync(RequestFor(Path.Combine(work, "sub", "config.json")), null, CancellationToken.None));
        Assert.Contains("resolves outside the download folder", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));

        var plain = await downloader.DownloadAsync(RequestFor(Path.Combine(work, "config.json")), null, CancellationToken.None);
        Assert.True(plain.Verified);
        Assert.Equal(config.Content, await File.ReadAllBytesAsync(Path.Combine(work, "config.json")));
    }

    // ---------------------------------------------------------------- existing folders and manifests

    [Fact]
    public async Task APartialFolderOfAnotherRepo_OrAnotherCommit_OrWithoutManifest_IsRefusedWithFolderExists()
    {
        var repo = await HubWithRepoAsync();
        await PartialDownloadAsync(repo, 50_000);

        // Another revision that resolves to another commit.
        repo.Commits[Sha2] = repo.Commits[Sha1];
        repo.Branches["v2"] = Sha2;
        var otherCommit = await Assert.ThrowsAsync<AgentException>(() => Service().StartAsync(Request(revision: "v2")));
        Assert.Equal(ErrorCodes.FolderExists, otherCommit.Code);
        Assert.Contains(Sha2[..12], otherCommit.Message, StringComparison.Ordinal);

        // Another repo into the same folder.
        _hub.AddRepo("other/repo").Commit(Sha2, FakeFile.Small("config.json", "{}"));
        var otherRepo = await Assert.ThrowsAsync<AgentException>(() => Service().StartAsync(new DownloadRequest("other/repo", null, null, Folder, GgufPatterns)));
        Assert.Equal(ErrorCodes.FolderExists, otherRepo.Code);
        Assert.Contains(RepoId, otherRepo.Message, StringComparison.Ordinal);

        // A .partial nobody's manifest describes.
        File.Delete(PartialPath(DownloadManifest.FileName));
        var noManifest = await Assert.ThrowsAsync<AgentException>(() => Service().StartAsync(Request()));
        Assert.Equal(ErrorCodes.FolderExists, noManifest.Code);
    }

    [Fact]
    public async Task ExistingComplete_VerifiesWhatIsThere_AndFetchesOnlyWhatIsMissing_InPlace()
    {
        var repo = await HubWithRepoAsync();
        var config = repo.File(Sha1, "config.json");
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        Directory.CreateDirectory(FinalPath());
        await File.WriteAllBytesAsync(FinalPath("config.json"), config.Content);
        await File.WriteAllBytesAsync(FinalPath(gguf.Path), gguf.Content.AsSpan(0, 1000).ToArray());

        var done = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Completed, done.State);
        Assert.False(Directory.Exists(PartialPath()));
        Assert.Equal(416, Assert.Single(_hub.FileRequests("config.json")).Status);
        Assert.Equal(("bytes=1000-", 206), (Assert.Single(_hub.FileRequests(gguf.Path)).Range, _hub.FileRequests(gguf.Path)[0].Status));
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(FinalPath(gguf.Path)));
        Assert.Equal(DownloadManifestState.Completed, DownloadManifest.TryRead(FinalPath())!.State);
    }

    [Fact]
    public async Task Start_RefusesAReservedSuffix_AndASecondStartWhileRunning()
    {
        await HubWithRepoAsync();
        var service = Service();
        var suffix = await Assert.ThrowsAsync<AgentException>(() => service.StartAsync(Request(folder: "x.partial")));
        Assert.Equal(ErrorCodes.InvalidRequest, suffix.Code);

        var gguf = _hub.Repos[RepoId].File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.StallAfter = 10_000;
        await service.StartAsync(Request());
        await gguf.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(Request()));
        Assert.Contains("already in progress", again.Message, StringComparison.Ordinal);
        gguf.StallRelease.TrySetResult();
    }

    [Fact]
    public async Task OnWindows_TheSameFolderInAnotherCase_IsAlreadyInProgress()
    {
        // MN-219: folder keys were ordinal, so on Windows "Foo" and "foo" (one folder on disk) ran as two downloads into
        // the same .partial; the second one's failure overwrote the first one's manifest.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only: NTFS folder names are case-insensitive.");
        }

        var repo = await HubWithRepoAsync();
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.StallAfter = 10_000;
        var service = Service();
        await service.StartAsync(Request());
        await gguf.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(Request(folder: Folder.ToUpperInvariant())));
        Assert.Contains("already in progress", again.Message, StringComparison.Ordinal);
        gguf.StallRelease.TrySetResult();
    }

    [Fact]
    public async Task Cancel_StopsTheRun_KeepsItsFiles_AndStartingAgainResumes()
    {
        var repo = await HubWithRepoAsync();
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.StallAfter = 64 * 1024;
        var service = Service();
        await service.StartAsync(Request());
        await gguf.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitForLengthAsync(PartialPath(gguf.Path), 64 * 1024);

        Assert.True(await service.CancelAsync(Folder));

        var cancelled = (await service.GetAsync(Folder))!;
        Assert.Equal(DownloadState.Cancelled, cancelled.State);
        Assert.Equal(DownloadManifestState.Cancelled, DownloadManifest.TryRead(PartialPath())!.State);
        Assert.Equal(64 * 1024, new FileInfo(PartialPath(gguf.Path)).Length);

        gguf.StallAfter = null;
        var done = await RunToEndAsync(service, Request());
        Assert.Equal(DownloadState.Completed, done.State);
        Assert.Equal("bytes=65536-", _hub.FileRequests(gguf.Path)[^1].Range);
    }

    [Fact]
    public async Task Cancel_WhileAStartIsStillPlanning_StopsThatDownload_InsteadOfAnsweringAboutAnEntryItCannotSeeYet()
    {
        // MN-239, D129 (7d). StartAsync plans inside _startGate — the plan makes a Hub call, so it can take a while —
        // and only then publishes the entry with its token source and its task. CancelAsync did not take that gate, so
        // a cancel landing in the window read whatever _entries held at that instant and answered from it: here, no
        // entry at all, so "false" ("this agent knows no download") while the download it was asked to stop carried on
        // and renamed its .partial folder into place. The operator pressed cancel, was told there was nothing to
        // cancel, and got the download anyway.
        var repo = await HubWithRepoAsync();
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.StallAfter = 16 * 1024; // the run blocks, so the cancellation is observable rather than a race with the end
        var planning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repo.BeforeListing = () =>
        {
            planning.TrySetResult();
            return release.Task;
        };

        var service = Service();
        var start = service.StartAsync(Request());
        await planning.Task.WaitAsync(TimeSpan.FromSeconds(30)); // inside PlanAsync, holding the start gate

        var cancel = service.CancelAsync(Folder);
        release.SetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(30));
        gguf.StallAfter = null;

        Assert.True(await cancel.WaitAsync(TimeSpan.FromSeconds(60)), "the cancel answered about a download it had not stopped");
        Assert.Equal(DownloadState.Cancelled, (await service.GetAsync(Folder))!.State);
        Assert.False(Directory.Exists(FinalPath()), "the cancelled download was promoted into the models root anyway");
    }

    // ---------------------------------------------------------------- HTTPS only and the credential origin

    [Fact]
    public async Task APlainHttpEndpoint_IsRefusedBeforeAnyRequest()
    {
        await HubWithRepoAsync();
        var remote = await Assert.ThrowsAsync<AgentException>(() =>
            Service(http: HubClient(new Uri("http://hub.example.test/"))).StartAsync(Request()));
        Assert.Contains("HTTPS only", remote.Message, StringComparison.Ordinal);

        // Loopback HTTP only when the dev setup allows it (production default: never).
        var loopback = await Assert.ThrowsAsync<AgentException>(() => Service(new NativeDownloadOptions()).StartAsync(Request()));
        Assert.Contains("HTTPS only", loopback.Message, StringComparison.Ordinal);
        Assert.Empty(_hub.Requests);
    }

    [Fact]
    public async Task ARedirectToPlainHttp_IsRefused_AndTheFileFails()
    {
        var repo = await HubWithRepoAsync();
        repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf").Redirect = sha => new Uri($"http://cdn.example.test/{RepoId}/{sha}/x.gguf?X-Amz-Signature=abc");

        var failed = await RunToEndAsync(Service(), Request());

        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("http://cdn.example.test is not allowed: downloads use HTTPS only", failed.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Signature", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirectToTheCdn_IsFollowed_WithoutTheToken()
    {
        var repo = await HubWithRepoAsync(requiredToken: Token);
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.Redirect = sha => new Uri(_hub.LocalhostUri, $"cdn/{RepoId}/{sha}/{gguf.Path}");

        var done = await RunToEndAsync(Service(), Request(token: Token));

        Assert.Equal(DownloadState.Completed, done.State);
        var hops = _hub.FileRequests(gguf.Path);
        Assert.Equal(2, hops.Count);
        Assert.Equal((302, "Bearer " + Token), (hops[0].Status, hops[0].Authorization));
        Assert.Equal(("localhost", 200), (hops[1].Host, hops[1].Status));
        Assert.Null(hops[1].Authorization); // another origin never gets the credential
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(FinalPath(gguf.Path)));
    }

    // ---------------------------------------------------------------- the token

    [Fact]
    public async Task TheToken_NeverReachesErrorsLogsOrTheManifest_EvenWhenTheHubEchoesIt()
    {
        var repo = await HubWithRepoAsync(requiredToken: Token);
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.StatusOverride = 403;
        gguf.StatusBody = $"Access to this file is restricted for token {Token}";

        var failed = await RunToEndAsync(Service(), Request(token: Token));

        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("HTTP 403", failed.Error, StringComparison.Ordinal);
        Assert.Contains("token", failed.Error, StringComparison.Ordinal);
        AssertNoTokenAnywhere(failed.Error);
        Assert.True(DownloadManifest.TryRead(PartialPath())!.Authenticated);

        // A wrong token on the listing: Hub refusal, no token in the message.
        var wrong = await Assert.ThrowsAsync<HuggingFaceApiException>(() => Service().StartAsync(Request(token: "hf_WrongTokenWrongToken99", folder: "other")));
        Assert.DoesNotContain("hf_WrongToken", wrong.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, new DownloadRequest(RepoId, null, Token).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentRestart_DuringAPublicDownload_ResumesByItself()
    {
        var repo = await HubWithRepoAsync();
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.StallAfter = 100_000;
        var first = Service(track: false);
        await first.StartAsync(Request());
        await gguf.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(async () => (await first.GetAsync(Folder))!.BytesDownloaded >= 100_000, "the stalled bytes to land");

        await first.DisposeAsync(); // the agent stops mid-download
        Assert.Equal(DownloadManifestState.Downloading, DownloadManifest.TryRead(PartialPath())!.State);
        gguf.StallAfter = null;

        var second = Service();
        var resumed = await second.GetAsync(Folder);
        Assert.NotNull(resumed);
        Assert.NotEqual(DownloadState.Failed, resumed.State);
        Assert.False(second.NeedsToken(Folder));
        await second.WhenFinishedAsync(Folder).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(DownloadState.Completed, (await second.GetAsync(Folder))!.State);
        Assert.Equal("bytes=100000-", _hub.FileRequests(gguf.Path)[^1].Range);
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(FinalPath(gguf.Path)));
    }

    [Fact]
    public async Task AgentRestart_DuringAnAuthenticatedDownload_PausesForTheToken_ThenResumesWithIt()
    {
        var repo = await HubWithRepoAsync(requiredToken: Token);
        var gguf = repo.File(Sha1, "Qwen3-Embedding-4B-Q8_0.gguf");
        gguf.StallAfter = 100_000;
        string state = Path.Combine(_base, "state");
        string[] patterns = ["*.json", "*Q8_0.gguf"];
        object Body(string id, string? token) => new { operationId = id, issuedAt = DateTimeOffset.UtcNow, expiresAt = DateTimeOffset.UtcNow.AddHours(1), repoId = RepoId, folder = Folder, allowPatterns = patterns, token };

        // Agent boot 1: POST /downloads with the token through the real endpoints, operation log and AgentDownloadService.
        await using (var agent = await AgentHost.StartAsync(this, state))
        {
            var accepted = await agent.PostAsync("/downloads", Body("dl-1", Token));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            await gguf.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await WaitUntilAsync(async () => (await agent.Native.GetAsync(Folder))!.BytesDownloaded >= 100_000, "the stalled bytes to land");
        }

        gguf.StallAfter = null;
        Assert.Equal(DownloadManifestState.Downloading, DownloadManifest.TryRead(PartialPath())!.State);

        // Agent boot 2: reconciliation resumes the non-terminal operation; the token is gone, so the download pauses.
        await using (var agent = await AgentHost.StartAsync(this, state))
        {
            await agent.RecoverAsync();

            var paused = await agent.GetDownloadAsync();
            Assert.Equal(DownloadState.Failed, paused.State);
            Assert.StartsWith(NativeDownloadService.NeedsTokenMessage, paused.Error, StringComparison.Ordinal);
            Assert.True(agent.Native.NeedsToken(Folder));
            Assert.True(paused.NeedsToken); // MN-219: the agent's status says so too (AgentDownloadService mapped false)
            Assert.Equal(100_000 + repo.File(Sha1, "config.json").Content.Length, paused.BytesDownloaded);
            var manifest = DownloadManifest.TryRead(PartialPath())!;
            Assert.Equal((DownloadManifestState.Paused, true), (manifest.State, manifest.Authenticated));
            var operation = agent.Log.Find("dl-1")!;
            Assert.Equal(OperationState.Failed, operation.State);
            Assert.Contains(NativeDownloadService.NeedsTokenMessage, operation.Error!.Message, StringComparison.Ordinal);

            // MN-219: the disk pre-check counts the bytes already in {folder}.partial; a disk with room for the rest only
            // (plus the headroom) admits the resume (before, the whole repo was required again and it was refused).
            long remaining = paused.TotalBytes - paused.BytesDownloaded;
            _disk.Space = new DiskSpace(FreeBytes: (long)Math.Ceiling(remaining * 1.05) + 1, TotalBytes: 1L << 40);

            // The operator gives the token again: the download resumes from its bytes on disk.
            var again = await agent.PostAsync("/downloads", Body("dl-2", Token));
            Assert.True(again.StatusCode == HttpStatusCode.Accepted, await again.Content.ReadAsStringAsync());
            await WaitUntilAsync(() => Task.FromResult(agent.Log.Find("dl-2")?.State == OperationState.Succeeded), "the resumed download's operation");
            var completed = await agent.GetDownloadAsync();
            Assert.Equal((DownloadState.Completed, false), (completed.State, completed.NeedsToken));
            Assert.False(agent.Native.NeedsToken(Folder));
        }

        var resumedRequest = _hub.FileRequests(gguf.Path)[^1];
        Assert.Equal(("bytes=100000-", "Bearer " + Token), (resumedRequest.Range, resumedRequest.Authorization));
        Assert.Equal(gguf.Content, await File.ReadAllBytesAsync(FinalPath(gguf.Path)));

        // Never on disk (operation log, manifest, any file under the state or models roots) nor in any log line.
        foreach (string file in Directory.EnumerateFiles(_base, "*", SearchOption.AllDirectories))
        {
            if (!file.EndsWith(".gguf", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(Token, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
            }
        }

        Assert.Contains(_logs.Lines, l => l.Contains("paused until the token is given again", StringComparison.Ordinal));
        AssertNoTokenAnywhere();
    }

    [Fact]
    public void RootError_RefusesNetworkModelsRoots()
    {
        Assert.Contains("UNC", NativeDownloadService.RootError(@"\\nas\models", _ => DriveType.Fixed), StringComparison.Ordinal);
        Assert.Contains("Network", NativeDownloadService.RootError(@"Z:\models", _ => DriveType.Network), StringComparison.Ordinal);
        Assert.Null(NativeDownloadService.RootError(@"D:\mirbadllm\models", _ => DriveType.Fixed));
    }

    // ---------------------------------------------------------------- helpers

    private void AssertNoTokenAnywhere(params string?[] texts)
    {
        Assert.DoesNotContain(_logs.Lines, l => l.Contains(Token, StringComparison.Ordinal));
        foreach (string? text in texts)
        {
            Assert.DoesNotContain(Token, text ?? "", StringComparison.Ordinal);
        }

        foreach (string file in Directory.EnumerateFiles(_root, DownloadManifest.FileName, SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(Token, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private static void MarkPending(string folder, string path)
    {
        var manifest = DownloadManifest.TryRead(folder)!;
        (manifest with { Files = manifest.Files.Select(f => f.Path == path ? f with { State = DownloadFileState.Pending } : f).ToList() }).Write(folder);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: true);
            }

            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private sealed class FakeDisk : IDiskSpaceProbe
    {
        public DiskSpace Space { get; set; } = new(FreeBytes: 1L << 40, TotalBytes: 1L << 41);

        public DiskSpace? Probe(string path) => Space;
    }

    /// <summary>
    /// One agent boot with the real storage package (endpoints, <see cref="AgentDownloadService"/>, recovery handler), the
    /// real file-backed <see cref="OperationLog"/> and <see cref="EventBus"/>, and <see cref="NativeDownloadService"/> as the
    /// <see cref="IHuggingFaceDownloader"/>, as MN-232 composes it on a node without Docker.
    /// </summary>
    private sealed class AgentHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly OperationLog _log;
        private readonly HttpClient _client;

        private AgentHost(WebApplication app, OperationLog log)
        {
            _app = app;
            _log = log;
            _client = app.GetTestClient();
        }

        public OperationLog Log => _log;

        public NativeDownloadService Native => (NativeDownloadService)_app.Services.GetRequiredService<IHuggingFaceDownloader>();

        public static async Task<AgentHost> StartAsync(NativeDownloaderTests test, string stateDirectory)
        {
            var log = new OperationLog(new OperationLogOptions { StateDirectory = stateDirectory, BootId = Guid.NewGuid().ToString("N") }, TimeProvider.System);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(test._logs);
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            var hubHttp = test.HubClient();
            builder.Services.AddSingleton<IOperationLog>(log);
            builder.Services.AddSingleton<IFencingState>(new AlwaysCurrentFencing());
            builder.Services.AddSingleton<IEventBus>(new EventBus());
            builder.Services.AddSingleton<IRuntimeDriver>(new NoRuntime());
            builder.Services.AddSingleton<IDiskSpaceProbe>(test._disk);
            builder.Services.AddSingleton<IContainerImageEngine>(new NoImages());
            builder.Services.AddSingleton<IHuggingFaceRepoInspector>(new MirbadLLM.NodeRuntime.HuggingFaceRepoInspector(hubHttp));
            builder.Services.AddSingleton<IHuggingFaceDownloader>(sp => new NativeDownloadService(
                hubHttp, sp.GetRequiredService<ModelRootPaths>(), test._disk, sp.GetRequiredService<IOptions<AgentStorageOptions>>(),
                TimeProvider.System, sp.GetRequiredService<ILogger<NativeDownloadService>>(), TestOptions()));
            builder.Services.AddAgentStorage(options =>
            {
                options.ModelsRoot = test._root;
                options.DownloadPollInterval = TimeSpan.FromMilliseconds(10);
                options.ProgressRecordInterval = TimeSpan.Zero;
            });
            var app = builder.Build();
            app.MapGroup(Protocol.BasePath).MapStorageEndpoints();
            await app.StartAsync();
            return new AgentHost(app, log);
        }

        /// <summary>What <c>AgentReconciler</c> does for storage operations at start (design §4.4 item 5).</summary>
        public async Task RecoverAsync()
        {
            var handler = _app.Services.GetRequiredService<StorageRecoveryHandler>();
            foreach (var operation in _log.ListRecoverable().Where(o => handler.Kinds.Contains(o.Status.Kind)))
            {
                await handler.RecoverAsync(operation);
            }
        }

        public Task<HttpResponseMessage> PostAsync(string path, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, Protocol.BasePath + path) { Content = JsonContent.Create(body, options: AgentJson.Options) };
            request.Headers.Add(Protocol.MasterHeader, Master.ToHeaderValue());
            return _client.SendAsync(request);
        }

        public async Task<NodeDownloadStatus> GetDownloadAsync()
        {
            var response = await _client.GetAsync(new Uri(Protocol.BasePath + "/downloads/" + Folder, UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<NodeDownloadStatus>(AgentJson.Options))!;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _log.Dispose();
        }
    }

    private sealed class AlwaysCurrentFencing : IFencingState
    {
        public MasterTriple? Accepted => Master;

        public Task<FencingCheck> CheckAsync(MasterTriple presented, CancellationToken ct = default) =>
            Task.FromResult(new FencingCheck(FencingDecision.Accepted, Master));

        public bool IsCurrent(MasterTriple operationMaster) => true;
    }

    private sealed class NoImages : IContainerImageEngine
    {
        public Task<IReadOnlyList<ImageInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ImageInfo>>([]);

        public Task<ImageInfo?> InspectAsync(string reference, CancellationToken ct = default) => Task.FromResult<ImageInfo?>(null);

        public Task PullAsync(string repository, string tagOrDigest, Action<ImagePullProgress> progress, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoRuntime : IRuntimeDriver
    {
        public LaunchDelivery Delivery => LaunchDelivery.Process;

        public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RuntimeStatus>>([]);

        public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default) => Task.FromResult<RuntimeStatus?>(null);

        public Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
