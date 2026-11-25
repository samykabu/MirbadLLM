using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Downloads.Native;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using MirbadLLM.Web.HubMirror;
using static MirbadLLM.UnitTests.Downloads.NativeDownloaderTests;

#pragma warning disable CA1707, xUnit1051

namespace MirbadLLM.UnitTests.Downloads;

public sealed class HubMirrorTests : IDisposable
{
    private const string Repo = "owner/checkpoint";
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NewSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _root = Directory.CreateTempSubdirectory("mirbad-hub-mirror-").FullName;

    [Fact]
    public async Task Resolve_UsesCompletedManifestAndLegacySource_ServesRangesWithoutUpstreamBytes()
    {
        using var ca = CertificateAuthority.Create();
        using var leaf = ca.IssueEnrollServerCertificate(["127.0.0.1"]);
        using var certificate = leaf.CreateTlsCertificate();
        await using var official = await FakeHub.StartAsync(certificate);
        var shard = FakeFile.Large("weights.gguf", 223, 7);
        var config = FakeFile.Small("config.json", "abc123");
        official.AddRepo(Repo).Commit(Sha, shard, config);
        string legacy = Directory.CreateDirectory(Path.Combine(_root, "legacy-name")).FullName;
        string downloaded = Directory.CreateDirectory(Path.Combine(_root, "downloaded")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(legacy, config.Path), config.Content);
        await File.WriteAllBytesAsync(Path.Combine(downloaded, shard.Path), shard.Content);
        var manifest = NativeSnapshotDownloader.BuildManifest(
            new HubSnapshot(Repo, Sha, [new(shard.Path, shard.Content.Length, shard.Sha256, null)]),
            "main", ["*"], false, null, DateTimeOffset.UnixEpoch);
        (manifest with
        {
            State = DownloadManifestState.Completed,
            Files = [manifest.Files[0] with { State = DownloadFileState.Verified }],
        }).Write(downloaded);

        using var transport = OpenFake(official.BaseUri, ca);
        var clock = new ManualTimeProvider();
        using var endpoints = Mirror(transport, clock);
        string target = $"/{Repo}/resolve/{Sha}/{shard.Path}";
        var head = await SendAsync(endpoints, target, HttpMethods.Head);
        Assert.Equal(200, head.Response.StatusCode);
        Assert.Equal(shard.Content.Length, head.Response.ContentLength);
        Assert.Equal(Sha, head.Response.Headers["X-Repo-Commit"]);
        Assert.Empty(Body(head));

        foreach (var (start, end) in new[] { (0, 9), (10, 29) })
        {
            var range = await SendAsync(endpoints, target, range: $"bytes={start}-{end}");
            Assert.Equal(206, range.Response.StatusCode);
            Assert.Equal($"bytes {start}-{end}/{shard.Content.Length}", range.Response.Headers.ContentRange);
            Assert.Equal($"\"{shard.Sha256}\"", range.Response.Headers.ETag);
            Assert.Equal(shard.Content[start..(end + 1)], Body(range));
        }

        var invalid = await SendAsync(endpoints, target, range: "bytes=999-");
        Assert.Equal(416, invalid.Response.StatusCode);
        Assert.Equal($"bytes */{shard.Content.Length}", invalid.Response.Headers.ContentRange);
        var failedIfRange = await SendAsync(endpoints, target, range: "bytes=0-1", ifRange: "\"stale\"");
        Assert.Equal(200, failedIfRange.Response.StatusCode);
        Assert.Equal(shard.Content, Body(failedIfRange));

        var legacyConfig = await SendAsync(endpoints, $"/{Repo}/resolve/{Sha}/{config.Path}");
        Assert.Equal("local", legacyConfig.Response.Headers["MirbadLLM-Content-Source"]);
        Assert.Equal(config.Content, Body(legacyConfig));
        Assert.Equal(0, official.FileRequests(shard.Path).Sum(request => request.BodyBytes));
        Assert.Equal(0, official.FileRequests(config.Path).Sum(request => request.BodyBytes));
        Assert.Equal(HttpMethods.Head, Assert.Single(official.FileRequests(shard.Path)).Method);
        Assert.Single(official.Requests, request => request.Path.StartsWith("/api/models/", StringComparison.Ordinal));

        clock.Advance(TimeSpan.FromMinutes(5));
        shard.HeadStatus = 403;
        var revoked = await SendAsync(endpoints, target);
        Assert.Equal(403, revoked.Response.StatusCode);
        Assert.Empty(Body(revoked));
        Assert.Equal(2, official.FileRequests(shard.Path).Count);
    }

    [Fact]
    public async Task Resolve_ModifiedMissingAndDigestlessFilesUseOfficialBytesAtOldSha()
    {
        using var ca = CertificateAuthority.Create();
        using var leaf = ca.IssueEnrollServerCertificate(["127.0.0.1"]);
        using var certificate = leaf.CreateTlsCertificate();
        await using var official = await FakeHub.StartAsync(certificate);
        var config = FakeFile.Small("config.json", "trusted");
        var missing = FakeFile.Small("sub/missing.json", "missing");
        var unverified = FakeFile.Small("size-only.json", "official");
        unverified.MissingDigest = true;
        var repo = official.AddRepo(Repo).Commit(Sha, config, missing, unverified)
            .Commit(NewSha, FakeFile.Small("config.json", "new-ref"));
        repo.Branches["refs/branch"] = Sha;
        string legacy = Directory.CreateDirectory(Path.Combine(_root, "legacy-name")).FullName;
        string configPath = Path.Combine(legacy, config.Path);
        await File.WriteAllBytesAsync(configPath, config.Content);
        await File.WriteAllTextAsync(Path.Combine(legacy, unverified.Path), "spoofed!");

        using var officialHttp = OpenFake(official.BaseUri, ca);
        using var endpoints = Mirror(officialHttp, TimeProvider.System);
        var listing = await SendAsync(endpoints, $"/api/models/{Repo}/revision/refs%2Fbranch?blobs=true");
        using var json = JsonDocument.Parse(Body(listing));
        Assert.Equal(Repo, json.RootElement.GetProperty("id").GetString());
        Assert.Equal(Sha, json.RootElement.GetProperty("sha").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("siblings").GetArrayLength());
        repo.Branches["main"] = NewSha;

        string configUrl = $"/{Repo}/resolve/{Sha}/{config.Path}";
        Assert.Equal("local", (await SendAsync(endpoints, configUrl)).Response.Headers["MirbadLLM-Content-Source"]);
        await endpoints.PendingDigestChecks();
        var lastWrite = File.GetLastWriteTimeUtc(configPath);
        string replacement = Path.Combine(legacy, "replacement.json");
        await File.WriteAllTextAsync(replacement, "spoofed");
        File.SetLastWriteTimeUtc(replacement, lastWrite);
        File.Move(replacement, configPath, overwrite: true);

        await SendAsync(endpoints, configUrl, HttpMethods.Head);
        await endpoints.PendingDigestChecks();
        foreach (var file in new[] { config, missing, unverified })
        {
            var response = await SendAsync(endpoints, $"/{Repo}/resolve/{Sha}/{file.Path}", range: "bytes=1-3");
            Assert.Equal(206, response.Response.StatusCode);
            Assert.Equal("hub", response.Response.Headers["MirbadLLM-Content-Source"]);
            Assert.Equal(file.Content[1..4], Body(response));
            var upstream = Assert.Single(official.FileRequests(file.Path), request => request.Method == HttpMethods.Get);
            Assert.Contains($"/resolve/{Sha}/", upstream.Path, StringComparison.Ordinal);
            Assert.Equal(3, upstream.BodyBytes);
        }
    }

    [Fact]
    public async Task Resolve_ServesWhileHashIsBlocked_AndNegativeVerdictOnlyAffectsThatIdentity()
    {
        using var ca = CertificateAuthority.Create();
        using var leaf = ca.IssueEnrollServerCertificate(["127.0.0.1"]);
        using var certificate = leaf.CreateTlsCertificate();
        await using var official = await FakeHub.StartAsync(certificate);
        var shard = FakeFile.Large("weights.gguf", 8 * 1024 * 1024, 4);
        official.AddRepo(Repo).Commit(Sha, shard);
        string path = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "legacy-name")).FullName, shard.Path);
        byte[] corrupt = (byte[])shard.Content.Clone();
        corrupt[0] ^= 1;
        await File.WriteAllBytesAsync(path, corrupt);

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var officialHttp = OpenFake(official.BaseUri, ca);
        using var endpoints = Mirror(officialHttp, TimeProvider.System, async (digest, stream, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return await digest.MatchesAsync(stream, ct);
        });
        string target = $"/{Repo}/resolve/{Sha}/{shard.Path}";
        var first = SendAsync(endpoints, target, range: "bytes=0-63");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var hit = await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(206, hit.Response.StatusCode);
        Assert.Equal("local", hit.Response.Headers["MirbadLLM-Content-Source"]);
        Assert.Equal(corrupt[..64], Body(hit));
        var second = await SendAsync(endpoints, target, range: "bytes=0-63");
        Assert.Equal(corrupt[..64], Body(second));
        Assert.Equal(1, calls);
        Assert.Equal(HttpMethods.Head, Assert.Single(official.FileRequests(shard.Path)).Method);

        release.SetResult();
        await endpoints.PendingDigestChecks();
        var miss = await SendAsync(endpoints, target, range: "bytes=0-63");
        Assert.Equal("hub", miss.Response.Headers["MirbadLLM-Content-Source"]);
        Assert.Equal(shard.Content[..64], Body(miss));
        Assert.Equal(64, Assert.Single(official.FileRequests(shard.Path), request => request.Method == HttpMethods.Get).BodyBytes);

        var lastWrite = File.GetLastWriteTimeUtc(path);
        var replacement = path + ".new";
        await File.WriteAllBytesAsync(replacement, shard.Content);
        File.SetLastWriteTimeUtc(replacement, lastWrite);
        File.Move(replacement, path, overwrite: true);
        var changed = await SendAsync(endpoints, target, range: "bytes=0-63");
        Assert.Equal("local", changed.Response.Headers["MirbadLLM-Content-Source"]);
        Assert.Equal(shard.Content[..64], Body(changed));
        await endpoints.PendingDigestChecks();
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task NativeReceiver_DiscardsCorruptColdMirrorBytesAndRetriesThePinnedHub()
    {
        using var ca = CertificateAuthority.Create();
        using var leaf = ca.IssueEnrollServerCertificate(["127.0.0.1"]);
        using var certificate = leaf.CreateTlsCertificate();
        await using var official = await FakeHub.StartAsync(certificate);
        var shard = FakeFile.Large("weights.gguf", 32 * 1024, 9);
        official.AddRepo(Repo).Commit(Sha, shard);
        string models = Directory.CreateDirectory(Path.Combine(_root, "legacy-name")).FullName;
        byte[] corrupt = (byte[])shard.Content.Clone();
        corrupt[^1] ^= 1;
        await File.WriteAllBytesAsync(Path.Combine(models, shard.Path), corrupt);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var officialHttp = OpenFake(official.BaseUri, ca);
        using var endpoints = Mirror(officialHttp, TimeProvider.System, async (digest, stream, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return await digest.MatchesAsync(stream, ct);
        });

        int port = TestPorts.Reserve();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port, listener => listener.UseHttps(certificate)));
        await using var app = builder.Build();
        var served = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Run(async context =>
        {
            await endpoints.HandleAsync(context);
            if (context.Request.Path.Value!.Contains("/resolve/", StringComparison.Ordinal))
            {
                served.TrySetResult(context.Response.Headers["MirbadLLM-Content-Source"].ToString());
            }
        });
        await app.StartAsync();
        var clients = new HubHttpClients(officialHttp, () => [ca.Certificate.ExportCertificatePem()]);
        var origin = HubOrigin.Parse($"https://127.0.0.1:{port}/", null);
        var request = new DownloadRequest(Repo, "main") { Hub = origin };
        var acquisition = await HubAcquisition.PrepareAsync(clients, request, ["*.gguf"], null, CancellationToken.None);
        Assert.Equal(origin, acquisition.Mirror);
        var manifest = NativeSnapshotDownloader.BuildManifest(acquisition.Snapshot, "main", ["*.gguf"], false, null, DateTimeOffset.UtcNow)
            with { HubEndpoint = origin.Endpoint.AbsoluteUri, HubTrust = origin.Trust };
        string work = Directory.CreateDirectory(Path.Combine(_root, "test.partial")).FullName;
        string final = Path.Combine(_root, "test");
        var progress = new List<long>();
        var engine = new NativeSnapshotDownloader(officialHttp, hubClients: clients);

        await engine.RunAsync(new NativeDownloadJob("test", work, final, manifest, null) { Acquisition = acquisition },
            progress.Add, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("local", await served.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains(Enumerable.Range(1, progress.Count - 1), i => progress[i - 1] == shard.Content.Length && progress[i] == 0);
        Assert.Equal(shard.Content, await File.ReadAllBytesAsync(Path.Combine(final, shard.Path)));
        Assert.Equal(DownloadManifestState.Completed, DownloadManifest.TryRead(final)!.State);
        var fallback = Assert.Single(official.FileRequests(shard.Path), next => next.Method == HttpMethods.Get);
        Assert.Equal($"/{Repo}/resolve/{Sha}/{shard.Path}", fallback.Path);
        Assert.Equal(shard.Content.Length, fallback.BodyBytes);
        release.SetResult();
        await endpoints.PendingDigestChecks();
    }

    [Fact]
    public async Task Resolve_RejectsEncodedSeparatorsCredentialsAndGatedContent_AndWonTFollowALink()
    {
        using var ca = CertificateAuthority.Create();
        using var leaf = ca.IssueEnrollServerCertificate(["127.0.0.1"]);
        using var certificate = leaf.CreateTlsCertificate();
        await using var official = await FakeHub.StartAsync(certificate);
        var asset = FakeFile.Small("sub/asset.json", "public");
        var repo = official.AddRepo(Repo).Commit(Sha, asset);
        using var transport = OpenFake(official.BaseUri, ca);
        using var mirror = Mirror(transport, TimeProvider.System);
        foreach (string path in new[] { "sub%2Fasset.json", "sub%5Casset.json", "%2e%2e/asset.json", ".mirbadllm-download.json" })
        {
            Assert.Equal(404, (await SendAsync(mirror, $"/{Repo}/resolve/{Sha}/{path}")).Response.StatusCode);
        }

        string url = $"/{Repo}/resolve/{Sha}/{asset.Path}";
        Assert.Equal(403, (await SendAsync(mirror, url, authorization: "Bearer secret")).Response.StatusCode);
        Assert.Empty(official.Requests);

        repo.Gated = true;
        var gated = await SendAsync(mirror, url);
        Assert.Equal(403, gated.Response.StatusCode);
        Assert.Empty(Body(gated));
        Assert.Empty(official.FileRequests(asset.Path));
        Assert.All(official.Requests, request => Assert.Null(request.Authorization));

        repo.Gated = false;
        var legacy = Directory.CreateDirectory(Path.Combine(_root, "legacy-name"));
        string real = Directory.CreateDirectory(Path.Combine(_root, "real-directory")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(real, "asset.json"), asset.Content);
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "/c", "mklink", "/J", Path.Combine(legacy.FullName, "sub"), real })
            {
                start.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(Path.Combine(legacy.FullName, "sub"), real);
        }

        using var fresh = Mirror(transport, TimeProvider.System);
        var fallback = await SendAsync(fresh, url);
        Assert.Equal("hub", fallback.Response.Headers["MirbadLLM-Content-Source"]);
        Assert.Equal(asset.Content, Body(fallback));
        Directory.Delete(Path.Combine(legacy.FullName, "sub"), recursive: false);
    }

    [Fact]
    public async Task FleetClient_ValidatesNameChainValidityAndOverlap()
    {
        using var ca = CertificateAuthority.Create();
        using var next = CertificateAuthority.Create();
        using var firstLeaf = ca.IssueEnrollServerCertificate(["127.0.0.1"]);
        using var secondLeaf = next.IssueEnrollServerCertificate(["127.0.0.1"]);
        using var firstCertificate = firstLeaf.CreateTlsCertificate();
        using var secondCertificate = secondLeaf.CreateTlsCertificate();
        await using var first = await FakeHub.StartAsync(firstCertificate);
        await using var second = await FakeHub.StartAsync(secondCertificate);
        first.AddRepo(Repo).Commit(Sha, FakeFile.Small("a.json", "first"));
        second.AddRepo(Repo).Commit(Sha, FakeFile.Small("a.json", "second"));
        IReadOnlyList<string> roots = [ca.Certificate.ExportCertificatePem()];
        using var official = new HttpClient();
        var clients = new HubHttpClients(official, () => roots);

        using var known = clients.OpenMirror(HubOrigin.Parse(first.BaseUri.AbsoluteUri, null));
        Assert.Equal(200, (int)(await known.GetAsync($"api/models/{Repo}")).StatusCode);
        using var wrongCa = clients.OpenMirror(HubOrigin.Parse(second.BaseUri.AbsoluteUri, null));
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongCa.GetAsync($"api/models/{Repo}"));
        var wrongName = new UriBuilder(first.BaseUri) { Host = "localhost" };
        using var wrongHost = clients.OpenMirror(HubOrigin.Parse(wrongName.Uri.AbsoluteUri, null));
        await Assert.ThrowsAsync<HttpRequestException>(() => wrongHost.GetAsync($"api/models/{Repo}"));

        roots = [ca.Certificate.ExportCertificatePem(), next.Certificate.ExportCertificatePem()];
        using var overlap = clients.OpenMirror(HubOrigin.Parse(second.BaseUri.AbsoluteUri, null));
        Assert.Equal(200, (int)(await overlap.GetAsync($"api/models/{Repo}")).StatusCode);
        roots = [next.Certificate.ExportCertificatePem()];
        using var retired = clients.OpenMirror(HubOrigin.Parse(first.BaseUri.AbsoluteUri, null));
        await Assert.ThrowsAsync<HttpRequestException>(() => retired.GetAsync($"api/models/{Repo}"));

        var past = new ManualTimeProvider();
        past.Advance(TimeSpan.FromDays(-365 * 3));
        using var old = CertificateAuthority.Create(time: past);
        using var expired = old.IssueEnrollServerCertificate(["127.0.0.1"]);
        Assert.False(HubHttpClients.ValidateFleetLeaf(expired.PublicCertificate, "127.0.0.1",
            SslPolicyErrors.RemoteCertificateChainErrors, [old.Certificate.ExportCertificatePem()]));
    }

    private HubMirrorEndpoints Mirror(HttpClient official, TimeProvider time,
        Func<ContentDigest, Stream, CancellationToken, Task<bool>>? hash = null)
    {
        var registry = new FakeModelRegistry().Add(new ModelDefinition
        {
            Id = "legacy", ServedName = "legacy", ModelPath = "legacy-name/config.json",
            Sources = [new ModelSource(Repo, "legacy-name")],
        });
        var catalog = new HubMirrorCatalog(_root, registry, "dgx");
        var clients = new HubHttpClients(official, () => []);
        return hash is null ? new HubMirrorEndpoints(catalog, clients, time) : new HubMirrorEndpoints(catalog, clients, time, hash);
    }

    internal static HttpClient OpenFake(Uri origin, CertificateAuthority ca)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                certificate is not null && HubHttpClients.ValidateFleetLeaf(certificate, origin.IdnHost, errors,
                    [ca.Certificate.ExportCertificatePem()]),
        };
        return new HttpClient(handler) { BaseAddress = origin };
    }

    private static async Task<DefaultHttpContext> SendAsync(HubMirrorEndpoints endpoints, string target, string method = "GET",
        string? range = null, string? ifRange = null, string? authorization = null)
    {
        var context = new DefaultHttpContext();
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = target;
        context.Request.Method = method;
        if (range is not null)
        {
            context.Request.Headers.Range = range;
        }

        if (ifRange is not null)
        {
            context.Request.Headers.IfRange = ifRange;
        }

        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        context.Response.Body = new MemoryStream();
        await endpoints.HandleAsync(context);
        return context;
    }

    private static byte[] Body(HttpContext context) => ((MemoryStream)context.Response.Body).ToArray();

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
