using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Auth;
using MirbadLLM.Web.Api;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// Agent bundle downloads from the master: <see cref="AgentBundleCatalog"/> (hash once, layout check), the anonymous
/// <c>GET /agent/bundles</c> routes on the real composition (outside <c>/api</c> auth and the <c>/v1</c> router; a clear 404
/// without bundles), the join and rekey lines <see cref="LinuxInstallerLine"/> builds with a download (hashes, URLs, no token
/// in the prompt form), and, where bash runs, the rendered command itself against stand-ins for curl, sudo and uname.
/// </summary>
public sealed partial class AgentBundleTests : IDisposable
{
    private const string CaHash = "sha256:q3vYc2ZpeHR1cmUtY2EtaGFzaA=";
    private const string Token = "abc_DEF-123xyz";
    private const string Enroll = "https://192.0.2.191:8443";

    private static readonly string[] BothRids = ["linux-x64", "linux-arm64"];

    private static readonly string[] AllRids = ["linux-x64", "linux-arm64", "win-x64"];

    private static readonly string[] X64Rids = ["linux-x64", "win-x64"];

    private static readonly string[] FileNames = ["linux-x64.tar.gz", "linux-arm64.tar.gz", "win-x64.zip", "osx-arm64.tar.gz"];

    private static readonly string[] GetOnly = ["GET"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-bundles-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string BundleDir => Path.Combine(_root, "bundles");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort (read-only files left by tar on Windows).
        }
    }

    // ---- catalog ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Catalog_HashesEachBundle_AndReadsItsVersionAndFolder()
    {
        string x64 = AgentBundleFixtures.Write(BundleDir, "linux-x64", version: "0123456789ab");
        string arm = AgentBundleFixtures.Write(BundleDir, "linux-arm64", version: "0123456789ab");

        var catalog = new AgentBundleCatalog(BundleDir, NullLogger<AgentBundleCatalog>.Instance);

        Assert.Equal(BothRids, catalog.Bundles.Select(b => b.Rid));
        AgentBundle a = catalog.Find("linux-x64")!;
        Assert.Equal(x64, a.Sha256);
        Assert.Equal(new FileInfo(Path.Combine(BundleDir, "linux-x64.tar.gz")).Length, a.Size);
        Assert.Equal("0123456789ab", a.Version);
        Assert.Equal("mirbadllm-agent-0123456789ab-linux-x64", a.Folder);
        Assert.Equal("2026-10-07T12:00:00Z", a.Built);
        Assert.Equal(arm, catalog.Find("linux-arm64")!.Sha256);
        Assert.Null(catalog.Find("linux-riscv64"));
    }

    [Fact]
    public void Catalog_HashesAtConstruction_NotOnEachRequest()
    {
        string before = AgentBundleFixtures.Write(BundleDir, "linux-x64");
        var catalog = new AgentBundleCatalog(BundleDir, NullLogger<AgentBundleCatalog>.Instance);

        // A file swapped after startup keeps the startup hash, so a join command built from it refuses the new file.
        string after = AgentBundleFixtures.Write(BundleDir, "linux-x64");

        Assert.NotEqual(before, after);
        Assert.Equal(before, catalog.Find("linux-x64")!.Sha256);
    }

    [Fact]
    public void Catalog_LeavesOut_BundlesWithoutTheLayout()
    {
        AgentBundleFixtures.Write(BundleDir, "linux-x64", withBinary: false);
        AgentBundleFixtures.Write(BundleDir, "linux-arm64", folder: "mirbadllm-agent-abc-linux-x64"); // wrong architecture
        File.WriteAllText(Path.Combine(BundleDir, "linux-riscv64.tar.gz"), "not served");

        var catalog = new AgentBundleCatalog(BundleDir, NullLogger<AgentBundleCatalog>.Instance);

        Assert.Empty(catalog.Bundles);
        Assert.Null(catalog.DownloadFrom(new Uri("http://m:8080/")));
    }

    [Fact]
    public void Catalog_NotGzip_IsLeftOut_AndTheVersionFallsBackToTheFolder()
    {
        Directory.CreateDirectory(BundleDir);
        File.WriteAllText(Path.Combine(BundleDir, "linux-arm64.tar.gz"), "plain text, not a tarball");
        AgentBundleFixtures.Write(BundleDir, "linux-x64", version: "v1.2", withBuildInfo: false);

        var catalog = new AgentBundleCatalog(BundleDir, NullLogger<AgentBundleCatalog>.Instance);

        Assert.Equal("linux-x64", Assert.Single(catalog.Bundles).Rid);
        Assert.Equal("v1.2", catalog.Bundles[0].Version);
    }

    [Fact]
    public void Catalog_MissingDirectory_HasNoBundles()
    {
        var catalog = new AgentBundleCatalog(Path.Combine(_root, "nowhere"), NullLogger<AgentBundleCatalog>.Instance);
        Assert.Empty(catalog.Bundles);
    }

    // ---- the Windows zip (MN-218, design-p3p4 §A.4.5) -------------------------------------------------------------------

    [Fact]
    public void Catalog_ReadsTheWindowsZip_AfterTheLinuxTarballs()
    {
        AgentBundleFixtures.Write(BundleDir, "linux-x64", version: "0123456789ab");
        AgentBundleFixtures.Write(BundleDir, "linux-arm64", version: "0123456789ab");
        string win = WindowsBundleFixtures.Write(BundleDir, version: "0123456789ab");

        var catalog = new AgentBundleCatalog(BundleDir, NullLogger<AgentBundleCatalog>.Instance);

        Assert.Equal(AllRids, catalog.Bundles.Select(b => b.Rid));
        AgentBundle bundle = catalog.Find("win-x64")!;
        Assert.Equal(win, bundle.Sha256);
        Assert.Equal(Path.Combine(BundleDir, "win-x64.zip"), bundle.FilePath);
        Assert.Equal(new FileInfo(bundle.FilePath).Length, bundle.Size);
        Assert.Equal("0123456789ab", bundle.Version);
        Assert.Equal("mirbadllm-agent-0123456789ab-win-x64", bundle.Folder);
        Assert.Equal("2026-10-08T12:00:00Z", bundle.Built);
        Assert.Same(bundle, catalog.FindFile("win-x64.zip"));
        Assert.Null(catalog.FindFile("win-x64.tar.gz"));
        Assert.Null(catalog.FindFile("linux-x64.zip"));

        AgentDownload download = catalog.DownloadFrom(new Uri("http://m:8080/"))!;
        Assert.Equal(AllRids, download.Bundles.Select(b => b.Rid));
        Assert.Equal(new AgentBundleLink("win-x64", "http://m:8080/agent/bundles/win-x64.zip", win, "0123456789ab"), download.Bundles[2]);
    }

    [Fact]
    public void Catalog_TheWindowsZipAlone_IsServed_AndALinuxTarballNamedWin_IsNot()
    {
        WindowsBundleFixtures.Write(BundleDir, version: "v1", backslashes: true); // an old Windows zip tool's '\' separators
        AgentBundleFixtures.Write(BundleDir, "win-x64"); // win-x64.tar.gz: not a name the catalog reads

        var catalog = new AgentBundleCatalog(BundleDir, NullLogger<AgentBundleCatalog>.Instance);

        AgentBundle bundle = Assert.Single(catalog.Bundles);
        Assert.Equal(("win-x64", "mirbadllm-agent-v1-win-x64"), (bundle.Rid, bundle.Folder));
    }

    [Theory]
    [InlineData("mirbadllm-agent.exe")]
    [InlineData("install-agent.ps1")]
    [InlineData("agent.windows.example.json")]
    [InlineData("llama-cpp.lock.json")]
    [InlineData("BUILD-INFO")]
    public void Catalog_LeavesOut_AWindowsZipWithoutARequiredFile(string missing)
    {
        WindowsBundleFixtures.Write(BundleDir, omit: [missing]);
        Assert.Contains(missing, AgentBundleCatalog.WindowsRequiredFiles);
        Assert.Empty(new AgentBundleCatalog(BundleDir, NullLogger<AgentBundleCatalog>.Instance).Bundles);
    }

    [Fact]
    public void Catalog_WithoutTheReadme_StillServesTheWindowsZip()
    {
        WindowsBundleFixtures.Write(BundleDir, omit: ["README.md"]);
        Assert.Single(new AgentBundleCatalog(BundleDir, NullLogger<AgentBundleCatalog>.Instance).Bundles);
    }

    [Theory]
    [InlineData(null, "../evil.ps1")]                                         // leaves the temp folder
    [InlineData(null, "mirbadllm-agent-abc123def456-win-x64/../../evil.ps1")]
    [InlineData(null, "/evil.ps1")]
    [InlineData(null, "C:/Windows/evil.ps1")]
    [InlineData(null, "other-folder/file.txt")]                              // two top-level folders
    [InlineData("mirbadllm-agent-abc-linux-x64", null)]                     // the wrong architecture
    [InlineData("mirbadllm-agent-win-x64", null)]                           // no version
    public void Catalog_LeavesOut_AWindowsZipWithTheWrongLayout(string? folder, string? extra)
    {
        WindowsBundleFixtures.Write(BundleDir, folder: folder, extraEntries: extra is null ? null : [extra]);
        Assert.Empty(new AgentBundleCatalog(BundleDir, NullLogger<AgentBundleCatalog>.Instance).Bundles);
    }

    [Fact]
    public void Catalog_NotAZip_IsLeftOut()
    {
        Directory.CreateDirectory(BundleDir);
        File.WriteAllText(Path.Combine(BundleDir, "win-x64.zip"), "plain text, not a zip");
        AgentBundleFixtures.Write(BundleDir, "linux-x64");

        Assert.Equal("linux-x64", Assert.Single(new AgentBundleCatalog(BundleDir, NullLogger<AgentBundleCatalog>.Instance).Bundles).Rid);
    }

    [Fact]
    public async Task TheWindowsZip_IsServedAnonymously_AsAZip_WithTheIndexHash()
    {
        AgentBundleFixtures.Write(BundleDir, "linux-x64");
        string win = WindowsBundleFixtures.Write(BundleDir);
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        factory.Settings["MirbadLLM:NodeApi:AgentBundleDirectory"] = BundleDir;
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage index = await client.GetAsync("/agent/bundles", Ct);
        using JsonDocument json = JsonDocument.Parse(await index.Content.ReadAsStringAsync(Ct));
        JsonElement[] entries = [.. json.RootElement.GetProperty("bundles").EnumerateArray()];
        Assert.Equal(X64Rids, entries.Select(e => e.GetProperty("rid").GetString()));
        Assert.Equal("http://localhost/agent/bundles/win-x64.zip", entries[1].GetProperty("url").GetString());
        Assert.Equal(win, entries[1].GetProperty("sha256").GetString());

        using HttpResponseMessage file = await client.GetAsync("/agent/bundles/win-x64.zip", Ct);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("application/zip", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal("mirbadllm-agent-win-x64.zip", file.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal(win, file.Headers.GetValues("X-Content-SHA256").Single());
        byte[] bytes = await file.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(win, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        // The Linux tarball keeps its media type; neither bundle answers under the other's extension.
        using HttpResponseMessage tarball = await client.GetAsync("/agent/bundles/linux-x64.tar.gz", Ct);
        Assert.Equal("application/gzip", tarball.Content.Headers.ContentType?.MediaType);
        foreach (string wrong in new[] { "/agent/bundles/win-x64.tar.gz", "/agent/bundles/linux-x64.zip", "/agent/bundles/win-x64" })
        {
            using HttpResponseMessage response = await client.GetAsync(wrong, Ct);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("linux-x64.tar.gz, win-x64.zip", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FileNames_AndUrls_PerArchitecture()
    {
        Assert.Equal(FileNames, AgentBundleCatalog.Rids.Select(AgentBundleCatalog.FileName));
        Assert.Equal(AgentBundleCatalog.LinuxRids, AgentBundleCatalog.Rids.Where(r => r.StartsWith("linux-", StringComparison.Ordinal)));
        Assert.Equal("https://spark.dev/ui/agent/bundles/win-x64.zip", AgentBundleCatalog.BundleUrl(new Uri("https://spark.dev/ui"), "win-x64").AbsoluteUri);
    }

    [Theory]
    [InlineData("http://192.0.2.191:8080", "http://192.0.2.191:8080/agent/bundles/linux-x64.tar.gz")]
    [InlineData("http://192.0.2.191:8080/", "http://192.0.2.191:8080/agent/bundles/linux-x64.tar.gz")]
    [InlineData("https://spark.dev/ui", "https://spark.dev/ui/agent/bundles/linux-x64.tar.gz")]
    public void BundleUrl_IsUnderTheMastersBase(string baseUrl, string expected) =>
        Assert.Equal(expected, AgentBundleCatalog.BundleUrl(new Uri(baseUrl), "linux-x64").AbsoluteUri);

    // ---- endpoints on the real composition ------------------------------------------------------------------------

    [Fact]
    public async Task Bundles_AreServedAnonymously_WithTheIndexHash_WhileApiStillNeedsTheLogin()
    {
        AgentBundleFixtures.Write(BundleDir, "linux-x64");
        AgentBundleFixtures.Write(BundleDir, "linux-arm64");
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        factory.Settings["MirbadLLM:NodeApi:AgentBundleDirectory"] = BundleDir;
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using HttpResponseMessage index = await client.GetAsync("/agent/bundles", Ct);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await index.Content.ReadAsStringAsync(Ct));
        JsonElement[] entries = [.. json.RootElement.GetProperty("bundles").EnumerateArray()];
        Assert.Equal(BothRids, entries.Select(e => e.GetProperty("rid").GetString()));

        foreach (JsonElement entry in entries)
        {
            string rid = entry.GetProperty("rid").GetString()!;
            Assert.Equal($"http://localhost/agent/bundles/{rid}.tar.gz", entry.GetProperty("url").GetString());
            using HttpResponseMessage file = await client.GetAsync($"/agent/bundles/{rid}.tar.gz", Ct);
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("application/gzip", file.Content.Headers.ContentType?.MediaType);
            byte[] bytes = await file.Content.ReadAsByteArrayAsync(Ct);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(BundleDir, rid + ".tar.gz"), Ct), bytes);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), entry.GetProperty("sha256").GetString());
            Assert.Equal(bytes.Length, entry.GetProperty("size").GetInt64());
        }

        // The same anonymous client is still turned away from /api and the pages.
        using HttpResponseMessage api = await client.GetAsync("/api/models", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        using HttpResponseMessage page = await client.GetAsync("/nodes", Ct);
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);

        using HttpResponseMessage unknown = await client.GetAsync("/agent/bundles/linux-riscv64.tar.gz", Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains(ErrorCodes.NotFound, await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoBundles_Is404WithAClearMessage_NotTheLoginPage()
    {
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        factory.Settings["MirbadLLM:NodeApi:AgentBundleDirectory"] = Path.Combine(_root, "none");
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (string path in new[] { "/agent/bundles", "/agent/bundles/linux-x64.tar.gz" })
        {
            using HttpResponseMessage response = await client.GetAsync(path, Ct);
            string body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{path}: {(int)response.StatusCode} {body}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("No agent bundles", body, StringComparison.Ordinal);
            Assert.Contains("install-agent.sh", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BundleRoutes_AreAnonymous_OnTheirOwnPrefix_AndNotInTheV1Router()
    {
        using var factory = new AuthWebFactory { AdminPassword = "correct-password" };
        _ = factory.Server;
        RouteEndpoint[] routes = [.. factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()];

        // The admin route /api/nodes/{id}/agent-update is not a bundle route (it needs the admin login).
        RouteEndpoint[] bundles = [.. routes.Where(r => r.RoutePattern.RawText?.Contains("agent", StringComparison.OrdinalIgnoreCase) == true
            && r.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) != true)];
        Assert.Equal(2, bundles.Length);
        Assert.All(bundles, r =>
        {
            Assert.StartsWith(AgentBundleCatalog.RoutePrefix, r.RoutePattern.RawText, StringComparison.Ordinal);
            Assert.NotNull(r.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Equal(GetOnly, r.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
        });
        Assert.DoesNotContain(routes, r => r.RoutePattern.RawText?.StartsWith("/v1", StringComparison.Ordinal) == true
            && r.RoutePattern.RawText.Contains("bundle", StringComparison.OrdinalIgnoreCase));
    }

    // ---- the join and rekey lines ---------------------------------------------------------------------------------

    [Fact]
    public void JoinWithDownload_EmbedsEachArchitecturesHashAndUrl_AndThePromptFormHasNoToken()
    {
        AgentDownload download = Download("http://192.0.2.191:8080");
        LinuxInstallerLine line = LinuxInstallerLine.Join("pc-1", Enroll, CaHash, download);

        string prompt = line.Prompt();
        string inline = line.WithToken(Token);

        Assert.True(line.DownloadsAgent);
        foreach (string text in new[] { prompt, inline })
        {
            Assert.Contains($"x86_64|amd64) u='http://192.0.2.191:8080/agent/bundles/linux-x64.tar.gz' h={download.Bundles[0].Sha256} ;;", text, StringComparison.Ordinal);
            Assert.Contains($"aarch64|arm64) u='http://192.0.2.191:8080/agent/bundles/linux-arm64.tar.gz' h={download.Bundles[1].Sha256} ;;", text, StringComparison.Ordinal);
            Assert.Contains("| sha256sum -c -;", text, StringComparison.Ordinal);
            // The check comes before the unpack, and the unpack before anything runs as root.
            Assert.True(text.IndexOf("sha256sum -c", StringComparison.Ordinal) < text.IndexOf("tar -xzf", StringComparison.Ordinal));
            Assert.True(text.IndexOf("tar -xzf", StringComparison.Ordinal) < text.IndexOf("sudo", StringComparison.Ordinal));
            Assert.EndsWith($"./mirbadllm-agent-*/install-agent.sh install --node-id pc-1 --enroll-url {Enroll} --ca-hash {CaHash})", text, StringComparison.Ordinal);
            Assert.StartsWith("(set -e; ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("--token", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(Token, prompt, StringComparison.Ordinal);
        Assert.Contains("sudo --preserve-env=MIRBADLLM_JOIN_TOKEN ./mirbadllm-agent-*/install-agent.sh install ", prompt, StringComparison.Ordinal);
        Assert.Contains($"sudo MIRBADLLM_JOIN_TOKEN='{Token}' ./mirbadllm-agent-*/install-agent.sh install ", inline, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(inline, Regex.Escape(Token)));
    }

    [Fact]
    public void RekeyWithDownload_RunsTheRekeyVerb_WithTheUid()
    {
        LinuxInstallerLine line = LinuxInstallerLine.Rekey("pc-1", "uid-42", Enroll, CaHash, Download("http://m:8080"));

        Assert.EndsWith($"./mirbadllm-agent-*/install-agent.sh rekey --node-id pc-1 --node-uid uid-42 --enroll-url {Enroll} --ca-hash {CaHash})", line.Prompt(), StringComparison.Ordinal);
        Assert.DoesNotContain("--rekey", line.WithToken(Token), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, line.Prompt(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutBundles_TheLinesAreThePreBundleOnes()
    {
        Assert.Equal($"sudo MIRBADLLM_JOIN_TOKEN='{Token}' ./install-agent.sh --node-id pc-1 --enroll-url {Enroll} --ca-hash {CaHash}",
            LinuxInstallerLine.Join("pc-1", Enroll, CaHash).WithToken(Token));
        Assert.Equal($"sudo ./install-agent.sh --node-id pc-1 --enroll-url {Enroll} --ca-hash {CaHash}", LinuxInstallerLine.Join("pc-1", Enroll, CaHash).Prompt());
        Assert.Equal($"sudo ./install-agent.sh --rekey --node-id pc-1 --node-uid u1 --enroll-url {Enroll} --ca-hash {CaHash}",
            LinuxInstallerLine.Rekey("pc-1", "u1", Enroll, CaHash, new AgentDownload { Bundles = [] }).Prompt());
    }

    [Theory]
    [InlineData("linux-x64", "NOT-A-HASH", "http://m:8080/agent/bundles/linux-x64.tar.gz")]
    [InlineData("linux-riscv64", null, "http://m:8080/agent/bundles/linux-riscv64.tar.gz")]
    [InlineData("linux-x64", null, "ftp://m/agent/bundles/linux-x64.tar.gz")]
    [InlineData("linux-x64", null, "agent/bundles/linux-x64.tar.gz")]
    public void AnUnusableDownload_GivesThePreBundleLine_NeverAGuessedHash(string rid, string? sha, string url)
    {
        var download = new AgentDownload { Bundles = [new AgentBundleLink(rid, url, sha ?? new string('a', 64), "v")] };
        LinuxInstallerLine line = LinuxInstallerLine.Join("pc-1", Enroll, CaHash, download);

        Assert.False(line.DownloadsAgent);
        Assert.StartsWith("sudo ./install-agent.sh ", line.Prompt(), StringComparison.Ordinal);
    }

    [Fact]
    public void AQuoteInTheUrl_StaysInsideItsSingleQuotes()
    {
        var download = new AgentDownload { Bundles = [new AgentBundleLink("linux-x64", "http://m:8080/it's/agent/bundles/linux-x64.tar.gz", new string('b', 64), "v")] };
        string prompt = LinuxInstallerLine.Join("pc-1", Enroll, CaHash, download).Prompt();
        Assert.Contains(@"u='http://m:8080/it'\''s/agent/bundles/linux-x64.tar.gz'", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Of_RebuildsTheMastersLine_WithItsDownload_AndRefusesAnotherOne()
    {
        AgentDownload download = Download("http://m:8080");
        var response = new JoinTokenCreateResponse
        {
            NodeId = "pc-1",
            Token = Token,
            CaHash = CaHash,
            EnrollUrl = Enroll,
            ExpiresAt = DateTimeOffset.UnixEpoch,
            Commands = new JoinCommands(LinuxInstallerLine.Join("pc-1", Enroll, CaHash, download).WithToken(Token)),
            AgentDownload = download,
        };

        LinuxInstallerLine? rebuilt = LinuxInstallerLine.Of(response, null);
        Assert.NotNull(rebuilt);
        Assert.True(rebuilt.DownloadsAgent);
        Assert.DoesNotContain(Token, rebuilt.Prompt(), StringComparison.Ordinal);

        // A download list that does not match the master's line (another hash): no prompt form is guessed.
        AgentDownload other = new() { Bundles = [download.Bundles[0] with { Sha256 = new string('c', 64) }, download.Bundles[1]] };
        Assert.Null(LinuxInstallerLine.Of(response with { AgentDownload = other }, null));
        Assert.Null(LinuxInstallerLine.Of(response with { AgentDownload = null }, null));
    }

    // ---- the rendered command, run ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("x86_64", false, "linux-x64")]
    [InlineData("aarch64", true, "linux-arm64")]
    public async Task RenderedCommand_DownloadsChecksUnpacksAndRunsTheBundlesInstaller(string machine, bool inline, string rid)
    {
        SkipUnlessBash();
        Sandbox box = CreateSandbox();
        AgentDownload download = SandboxDownload(box);
        LinuxInstallerLine line = LinuxInstallerLine.Join("pc-1", Enroll, CaHash, download);

        (int exit, string output, string error) = await RunCommandAsync(box, inline ? line.WithToken(Token) : line.Prompt(), machine);

        Assert.True(exit == 0, $"exit {exit}\n{output}\n{error}");
        Assert.Contains("agent.tar.gz: OK", output, StringComparison.Ordinal);
        string[] log = await File.ReadAllLinesAsync(box.Log, Ct);
        Assert.Equal($"mirbadllm-agent-v9-{rid}", log[0]);
        Assert.Equal(new[] { "install", "--node-id", "pc-1", "--enroll-url", Enroll, "--ca-hash", CaHash }, log[2..9]);
        Assert.Equal(inline ? $"token={Token}" : "token=unset", log[9]);
        Assert.Equal(inline ? "preserve=" : "preserve=MIRBADLLM_JOIN_TOKEN", log[10]);
        // The temp folder (the installer's working directory) is gone afterwards.
        Assert.Equal("exists=no", (await RunBashAsync(["-c", $"[ -e '{log[1]}' ] && echo exists=yes || echo exists=no"], box)).Output.Trim());
    }

    [Fact]
    public async Task RenderedCommand_RefusesATamperedBundle_BeforeRunningAnything()
    {
        SkipUnlessBash();
        Sandbox box = CreateSandbox();
        AgentDownload download = SandboxDownload(box);
        // The master's file changes after the command was built (or a proxy swaps it): the embedded hash no longer matches.
        AgentBundleFixtures.Write(box.Served, "linux-x64", version: "v9");

        (int exit, string output, string error) = await RunCommandAsync(box, LinuxInstallerLine.Join("pc-1", Enroll, CaHash, download).Prompt(), "x86_64");

        Assert.NotEqual(0, exit);
        Assert.Contains("FAILED", output + error, StringComparison.Ordinal);
        Assert.False(File.Exists(box.Log), "the installer ran despite the hash mismatch");
        Assert.False(File.Exists(box.SudoLog), "sudo ran despite the hash mismatch");
    }

    [Fact]
    public async Task RenderedCommand_OnAnArchitectureWithoutABundle_StopsBeforeTheDownload()
    {
        SkipUnlessBash();
        Sandbox box = CreateSandbox();
        LinuxInstallerLine line = LinuxInstallerLine.Join("pc-1", Enroll, CaHash, SandboxDownload(box));

        (int exit, _, string error) = await RunCommandAsync(box, line.Prompt(), "riscv64");

        Assert.NotEqual(0, exit);
        Assert.Contains("no mirbadllm agent for riscv64", error, StringComparison.Ordinal);
        Assert.False(File.Exists(box.CurlLog), "curl ran for an architecture without a bundle");
    }

    [Fact]
    public async Task RenderedCommand_PassesShellcheck()
    {
        string? shellcheck = FindOnPath(OperatingSystem.IsWindows() ? "shellcheck.exe" : "shellcheck");
        if (shellcheck is null)
        {
            Assert.Skip("shellcheck is not on PATH (docker run --rm -v <dir>:/mnt koalaman/shellcheck:stable /mnt/<file> checks it by hand).");
        }

        Directory.CreateDirectory(_root);
        AgentDownload download = Download("http://192.0.2.191:8080");
        foreach (LinuxInstallerLine line in new[] { LinuxInstallerLine.Join("pc-1", Enroll, CaHash, download), LinuxInstallerLine.Rekey("pc-1", "u1", Enroll, CaHash, download) })
        {
            foreach (string command in new[] { line.Prompt(), line.WithToken(Token) })
            {
                string file = Path.Combine(_root, "command.sh");
                await File.WriteAllTextAsync(file, "#!/usr/bin/env bash\n" + command + "\n", Ct);
                (int exit, string output, string error) = await RunAsync(shellcheck!, ["-s", "bash", file], null);
                Assert.True(exit == 0, $"{command}\n{output}\n{error}");
            }
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------------

    private static AgentDownload Download(string baseUrl) => new()
    {
        Bundles =
        [
            new AgentBundleLink("linux-x64", AgentBundleCatalog.BundleUrl(new Uri(baseUrl), "linux-x64").AbsoluteUri, Convert.ToHexStringLower(SHA256.HashData("x64"u8)), "v9"),
            new AgentBundleLink("linux-arm64", AgentBundleCatalog.BundleUrl(new Uri(baseUrl), "linux-arm64").AbsoluteUri, Convert.ToHexStringLower(SHA256.HashData("arm"u8)), "v9"),
        ],
    };

    /// <summary>The bundles in <see cref="Sandbox.Served"/>, through the real catalog, under a made-up master address.</summary>
    private static AgentDownload SandboxDownload(Sandbox box) =>
        new AgentBundleCatalog(box.Served, NullLogger<AgentBundleCatalog>.Instance).DownloadFrom(new Uri("http://master.test:8080/"))!;

    private sealed record Sandbox(string Root, string Bin, string Served, string Log, string SudoLog, string CurlLog);

    /// <summary>
    /// Stand-ins on PATH: <c>uname</c> (the machine under test), <c>curl</c> (copies the served file named by the URL),
    /// <c>sudo</c> (records <c>--preserve-env</c>, applies <c>VAR=value</c>, runs the rest as is). Each bundle's
    /// <c>install-agent.sh</c> records its folder, working directory, arguments and whether it saw the token.
    /// </summary>
    private Sandbox CreateSandbox()
    {
        string root = Path.Combine(_root, "sandbox");
        string bin = Path.Combine(root, "bin");
        string served = Path.Combine(root, "served");
        Directory.CreateDirectory(bin);
        var box = new Sandbox(root, bin, served, Path.Combine(root, "install.log"), Path.Combine(root, "sudo.log"), Path.Combine(root, "curl.log"));
        const string installer = """
            #!/usr/bin/env bash
            {
                basename "$(cd "$(dirname "$0")" && pwd)"
                pwd
                printf '%s\n' "$@"
                echo "token=${MIRBADLLM_JOIN_TOKEN-unset}"
                echo "preserve=${FAKE_PRESERVE-}"
            } >"$FAKE_INSTALL_LOG"
            """;
        foreach (string rid in AgentBundleCatalog.LinuxRids)
        {
            AgentBundleFixtures.Write(served, rid, version: "v9", installScript: installer.ReplaceLineEndings("\n") + "\n");
        }

        WriteScript(bin, "uname", """
            #!/usr/bin/env bash
            if [ "${1-}" = -m ]; then echo "$FAKE_MACHINE"; else exec /usr/bin/uname "$@"; fi
            """);
        WriteScript(bin, "curl", """
            #!/usr/bin/env bash
            out=""; url=""
            while [ $# -gt 0 ]; do case "$1" in -o) out="$2"; shift 2 ;; -*) shift ;; *) url="$1"; shift ;; esac; done
            echo "$url" >>"$FAKE_CURL_LOG"
            cp "$FAKE_SERVED/${url##*/}" "$out"
            """);
        WriteScript(bin, "sudo", """
            #!/usr/bin/env bash
            echo "$*" >>"$FAKE_SUDO_LOG"
            while [ $# -gt 0 ]; do
                case "$1" in
                    --preserve-env=*) export FAKE_PRESERVE="${1#--preserve-env=}"; shift ;;
                    [A-Z_]*=*) export "$1"; shift ;;
                    *) break ;;
                esac
            done
            exec "$@"
            """);
        return box;
    }

    private static void WriteScript(string dir, string name, string text)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, text.ReplaceLineEndings("\n") + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static Task<(int Exit, string Output, string Error)> RunCommandAsync(Sandbox box, string command, string machine)
    {
        // The stand-ins go first on PATH inside bash (Git for Windows' bash puts its own folders before an inherited PATH);
        // the command itself is the line exactly as the master renders it.
        string script = Path.Combine(box.Root, "command.sh");
        File.WriteAllText(script, "PATH=\"$FAKE_BIN:$PATH\"\n" + command + "\n");
        return RunBashAsync([script], box, machine);
    }

    private static Task<(int Exit, string Output, string Error)> RunBashAsync(string[] args, Sandbox box, string machine = "x86_64") =>
        RunAsync(Bash()!, args, start =>
        {
            start.Environment["FAKE_BIN"] = BashPath(box.Bin);
            start.Environment["FAKE_MACHINE"] = machine;
            start.Environment["FAKE_SERVED"] = BashPath(box.Served);
            start.Environment["FAKE_INSTALL_LOG"] = BashPath(box.Log);
            start.Environment["FAKE_SUDO_LOG"] = BashPath(box.SudoLog);
            start.Environment["FAKE_CURL_LOG"] = BashPath(box.CurlLog);
            start.Environment.Remove(LinuxInstallerLine.TokenVariable);
        });

    /// <summary>A path the scripts can use under Git for Windows' bash (/c/…); unchanged on Linux.</summary>
    private static string BashPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        string full = Path.GetFullPath(path).Replace('\\', '/');
        return $"/{char.ToLowerInvariant(full[0])}{full[2..]}";
    }

    private static async Task<(int Exit, string Output, string Error)> RunAsync(string file, string[] args, Action<ProcessStartInfo>? configure)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        configure?.Invoke(start);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    private static readonly string[] BashLocations = OperatingSystem.IsWindows()
        ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")]
        : ["/bin/bash", "/usr/bin/bash"];

    private static string? Bash() => BashLocations.FirstOrDefault(File.Exists);

    private static void SkipUnlessBash()
    {
        if (Bash() is null)
        {
            Assert.Skip("Runs the rendered command: needs bash (Linux, or Git for Windows on a Windows box).");
        }
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);
}
