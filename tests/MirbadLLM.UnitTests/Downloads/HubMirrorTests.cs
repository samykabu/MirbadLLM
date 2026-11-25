using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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
        var lastWrite = File.GetLastWriteTimeUtc(configPath);
        string replacement = Path.Combine(legacy, "replacement.json");
        await File.WriteAllTextAsync(replacement, "spoofed");
        File.SetLastWriteTimeUtc(replacement, lastWrite);
        File.Move(replacement, configPath, overwrite: true);

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

    private HubMirrorEndpoints Mirror(HttpClient official, TimeProvider time)
    {
        var registry = new FakeModelRegistry().Add(new ModelDefinition
        {
            Id = "legacy", ServedName = "legacy", ModelPath = "legacy-name/config.json",
            Sources = [new ModelSource(Repo, "legacy-name")],
        });
        return new HubMirrorEndpoints(new HubMirrorCatalog(_root, registry, "dgx"), new HubHttpClients(official, () => []), time);
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
