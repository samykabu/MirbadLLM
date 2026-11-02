using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Api;
using MirbadLLM.Web.Enrollment;

#pragma warning disable CA1707, CA2000, xUnit1051 // underscore test names; the app owns its services; hand-written fakes ignore the test token.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-102: the node admin API (<c>/api/nodes*</c>) over an in-process host with the real <see cref="JsonNodeRegistry"/>,
/// <see cref="JsonModelRegistry"/>, <see cref="AdminReauthenticator"/> and <see cref="EnrollmentService"/>; hand-written
/// fakes for the audit log, the connection refresher and the model runtime. Also the registry's new token methods
/// (expire unused, prune, rekey purpose rules) and the bootstrap bundle rewrite expiring the previous token.
/// </summary>
public sealed class NodeApiTests : IAsyncDisposable
{
    private const string Password = "correct horse battery staple";
    private const string NodeId = "pc-office";

    private static readonly JsonSerializerOptions Json = CreateJson();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-nodeapi-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();
    private readonly CertificateAuthority _ca = CertificateAuthority.Create();
    private readonly RecordingAuditLog _audit = new();
    private readonly RecordingRefresher _refresher = new();
    private readonly RecordingRuntime _runtime = new();
    private WebApplication? _app;
    private HttpClient? _client;

    private string DataDir => Path.Combine(_root, "data");

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _nodes?.Dispose();
        _models?.Dispose();
        _ca.Dispose();
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
    }

    // ---- registry: token methods ----------------------------------------------------------------------------------

    [Fact]
    public async Task ExpireUnusedJoinTokens_ExpiresOnlyThatNodesLiveTokens_AndCountsThem()
    {
        using JsonNodeRegistry registry = Registry();
        JoinTokenIssue a = await registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        JoinTokenIssue b = await registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        JoinTokenIssue used = await registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        JoinTokenIssue other = await registry.CreateJoinTokenAsync("pc-lab", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        Assert.Equal(JoinTokenConsumeOutcome.Consumed, (await registry.TryConsumeJoinTokenAsync(used.Token, NodeId)).Outcome);
        DateTimeOffset usedAt = (await registry.ListJoinTokensAsync()).Single(t => t.Sha256 == used.Record.Sha256).UsedAt!.Value;

        int expired = await registry.ExpireUnusedJoinTokensAsync(NodeId);

        Assert.Equal(2, expired);
        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await registry.TryConsumeJoinTokenAsync(a.Token, NodeId)).Outcome);
        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await registry.TryConsumeJoinTokenAsync(b.Token, NodeId)).Outcome);
        Assert.Equal(JoinTokenConsumeOutcome.Consumed, (await registry.TryConsumeJoinTokenAsync(other.Token, "pc-lab")).Outcome);
        JoinTokenRecord usedRecord = (await registry.ListJoinTokensAsync()).Single(t => t.Sha256 == used.Record.Sha256);
        Assert.Equal(usedAt, usedRecord.UsedAt);
        Assert.Equal(used.Record.ExpiresAt, usedRecord.ExpiresAt);
        Assert.Equal(0, await registry.ExpireUnusedJoinTokensAsync(NodeId));
    }

    [Fact]
    public async Task PruneJoinTokens_RemovesOldUsedAndExpiredRecords_KeepsRecentAndLiveOnes()
    {
        using JsonNodeRegistry registry = Registry();
        JoinTokenIssue oldUsed = await registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        await registry.TryConsumeJoinTokenAsync(oldUsed.Token, NodeId);
        JoinTokenIssue oldExpired = await registry.CreateJoinTokenAsync("pc-lab", TimeSpan.FromMinutes(10), JoinTokenPurpose.Join);
        _time.Advance(TimeSpan.FromDays(10));
        JoinTokenIssue recentUsed = await registry.CreateJoinTokenAsync("pc-b", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        await registry.TryConsumeJoinTokenAsync(recentUsed.Token, "pc-b");
        JoinTokenIssue live = await registry.CreateJoinTokenAsync("pc-c", TimeSpan.FromHours(24), JoinTokenPurpose.Join);

        int removed = await registry.PruneJoinTokensAsync(TimeSpan.FromDays(7));

        Assert.Equal(2, removed);
        string[] kept = [.. (await registry.ListJoinTokensAsync()).Select(t => t.Sha256)];
        Assert.Equal([recentUsed.Record.Sha256, live.Record.Sha256], kept);
        Assert.DoesNotContain(oldUsed.Record.Sha256, kept);
        Assert.DoesNotContain(oldExpired.Record.Sha256, kept);
        Assert.Equal(JoinTokenConsumeOutcome.Consumed, (await registry.TryConsumeJoinTokenAsync(live.Token, "pc-c")).Outcome);

        // A zero retention never removes a token that is still valid, only finished ones.
        JoinTokenIssue fresh = await registry.CreateJoinTokenAsync("pc-d", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, await registry.PruneJoinTokensAsync(TimeSpan.Zero));
        Assert.Equal(fresh.Record.Sha256, Assert.Single(await registry.ListJoinTokensAsync()).Sha256);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => registry.PruneJoinTokensAsync(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public async Task RekeyToken_IsOnlyIssuedForAnEnrolledNonRevokedNode_ApprovedOrStillPending()
    {
        // One rule (JsonNodeRegistry.RekeyRefusal), shared with the node API and /enroll/rekey (MN-114).
        using JsonNodeRegistry registry = Registry();
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Rekey));

        // A record without any pin never enrolled (e.g. the approved bootstrap record before the DGX joined): nothing to re-pair.
        await registry.UpsertAsync(Record(NodeId, approved: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Rekey));

        // Enrolled, still awaiting approval: the rekey keeps it pending (MN-114).
        await registry.UpsertAsync(Record("pc-pending", approved: false, pendingPin: Pin()));
        JoinTokenIssue pending = await registry.CreateJoinTokenAsync("pc-pending", TimeSpan.FromHours(1), JoinTokenPurpose.Rekey);
        Assert.Equal(JoinTokenPurpose.Rekey, pending.Record.Purpose);

        await registry.UpsertAsync(Record("pc-ok", approved: true, pin: Pin()));
        JoinTokenIssue rekey = await registry.CreateJoinTokenAsync("pc-ok", TimeSpan.FromHours(1), JoinTokenPurpose.Rekey);
        Assert.Equal(JoinTokenPurpose.Rekey, rekey.Record.Purpose);

        await registry.RevokeAsync("pc-ok");
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.CreateJoinTokenAsync("pc-ok", TimeSpan.FromHours(1), JoinTokenPurpose.Rekey));
        Assert.Equal([pending.Record.Sha256, rekey.Record.Sha256], (await registry.ListJoinTokensAsync()).Select(t => t.Sha256));

        Assert.Equal("unknown node", JsonNodeRegistry.RekeyRefusal(null));
        Assert.Equal("node never enrolled", JsonNodeRegistry.RekeyRefusal(await registry.GetAsync(NodeId)));
        Assert.Equal("node revoked", JsonNodeRegistry.RekeyRefusal(await registry.GetAsync("pc-ok")));
        Assert.Null(JsonNodeRegistry.RekeyRefusal(await registry.GetAsync("pc-pending")));
    }

    [Fact]
    public async Task BootstrapBundleRewrite_ExpiresThePreviousBundlesToken()
    {
        using JsonNodeRegistry registry = Registry();
        string bundleDir = Path.Combine(_root, "bootstrap");
        using var writer = new BootstrapBundleWriter(
            registry,
            _ca,
            Options.Create(new EnrollmentOptions { BootstrapDirectory = bundleDir }),
            Options.Create(new RegistryMigrationOptions()),
            _time,
            NullLogger<BootstrapBundleWriter>.Instance);

        Assert.Equal(BootstrapBundleState.Written, await writer.EnsureAsync());
        string first = ReadBundleToken(writer.BundlePath);
        File.Delete(writer.BundlePath); // the agent container lost the bundle: the next check writes a fresh one
        Assert.Equal(BootstrapBundleState.Written, await writer.EnsureAsync());
        string second = ReadBundleToken(writer.BundlePath);

        Assert.NotEqual(first, second);
        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await registry.TryConsumeJoinTokenAsync(first, "dgx")).Outcome);
        Assert.Equal(JoinTokenConsumeOutcome.Consumed, (await registry.TryConsumeJoinTokenAsync(second, "dgx")).Outcome);
    }

    // ---- join tokens over the API ---------------------------------------------------------------------------------

    [Fact]
    public async Task JoinToken_WithTheAdminPassword_ReturnsTheTokenOnce_StoresOnlyItsHash()
    {
        HttpClient client = await StartAsync();

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = NodeId, Password = Password, DisplayName = "Office PC" }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        JoinTokenCreateResponse body = (await response.Content.ReadFromJsonAsync<JoinTokenCreateResponse>(Json))!;
        Assert.Equal(NodeId, body.NodeId);
        Assert.Equal(Spki.ToJoinHash(_ca.SpkiSha256), body.CaHash);
        Assert.Equal("https://localhost:8443", body.EnrollUrl);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(60), body.ExpiresAt);
        Assert.Equal(JoinTokenPurpose.Join, body.Purpose);
        Assert.Contains($"MIRBADLLM_JOIN_TOKEN='{body.Token}'", body.Commands.Linux, StringComparison.Ordinal);
        Assert.Contains($"--node-id {NodeId} --enroll-url https://localhost:8443 --ca-hash {body.CaHash}", body.Commands.Linux, StringComparison.Ordinal);
        Assert.Null(body.Commands.Windows);

        JoinTokenRecord stored = Assert.Single(await Nodes.ListJoinTokensAsync());
        Assert.Equal(Sha256Url(body.Token), stored.Sha256);
        Assert.Equal(JoinTokenPurpose.Join, stored.Purpose);
        Assert.Null(await Nodes.GetAsync(NodeId)); // no placeholder record before enrollment
        Assert.DoesNotContain(body.Token, File.ReadAllText(Path.Combine(DataDir, NodeRegistryDocument.FileName)), StringComparison.Ordinal);

        AuditRecord line = Assert.Single(_audit.Records);
        Assert.Equal((AuditActions.NodeJoinToken, NodeId, AuditResults.Succeeded), (line.Action, line.NodeId, line.Result));
        Assert.StartsWith("admin:", line.Actor, StringComparison.Ordinal);
        Assert.DoesNotContain(body.Token, _audit.AllText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Password, _audit.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JoinToken_WithAgentBundles_CommandDownloadsFromTheRequestAddress_AndEmbedsEachHash()
    {
        string bundles = Path.Combine(_root, "bundles");
        string x64 = MirbadLLM.UnitTests.Web.AgentBundleFixtures.Write(bundles, "linux-x64", version: "0123456789ab");
        string arm = MirbadLLM.UnitTests.Web.AgentBundleFixtures.Write(bundles, "linux-arm64", version: "0123456789ab");
        HttpClient client = await StartAsync(o => o.AgentBundleDirectory = bundles);
        client.BaseAddress = new Uri("http://192.0.2.191:8080/");

        JoinTokenCreateResponse body = await CreateTokenAsync(client);

        Assert.NotNull(body.AgentDownload);
        Assert.Equal(
            new[]
            {
                new AgentBundleLink("linux-x64", "http://192.0.2.191:8080/agent/bundles/linux-x64.tar.gz", x64, "0123456789ab"),
                new AgentBundleLink("linux-arm64", "http://192.0.2.191:8080/agent/bundles/linux-arm64.tar.gz", arm, "0123456789ab"),
            },
            body.AgentDownload.Bundles);
        string line = body.Commands.Linux;
        Assert.Contains($"u='http://192.0.2.191:8080/agent/bundles/linux-x64.tar.gz' h={x64} ;;", line, StringComparison.Ordinal);
        Assert.Contains($"u='http://192.0.2.191:8080/agent/bundles/linux-arm64.tar.gz' h={arm} ;;", line, StringComparison.Ordinal);
        // The enroll URL keeps its own listener (8443, TLS); only the download uses the main port.
        Assert.EndsWith($"./mirbadllm-agent-*/install-agent.sh install --node-id {NodeId} --enroll-url https://192.0.2.191:8443 --ca-hash {body.CaHash})", line, StringComparison.Ordinal);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(line, System.Text.RegularExpressions.Regex.Escape(body.Token)));
        Assert.DoesNotContain(body.Token, LinuxInstallerLine.Of(body, null)!.Prompt(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JoinToken_WithAgentBundles_AndAConfiguredBundleUrl_UsesIt()
    {
        string bundles = Path.Combine(_root, "bundles");
        MirbadLLM.UnitTests.Web.AgentBundleFixtures.Write(bundles, "linux-x64");
        HttpClient client = await StartAsync(o =>
        {
            o.AgentBundleDirectory = bundles;
            o.AgentBundleUrl = new Uri("http://spark.lan:8080");
        });

        JoinTokenCreateResponse body = await CreateTokenAsync(client);

        AgentBundleLink link = Assert.Single(body.AgentDownload!.Bundles);
        Assert.Equal("http://spark.lan:8080/agent/bundles/linux-x64.tar.gz", link.Url);
        Assert.DoesNotContain("aarch64", body.Commands.Linux, StringComparison.Ordinal); // only the architectures served
    }

    [Fact]
    public async Task JoinToken_WithoutAgentBundles_KeepsThePreBundleLine()
    {
        HttpClient client = await StartAsync(o => o.AgentBundleDirectory = Path.Combine(_root, "no-bundles"));

        JoinTokenCreateResponse body = await CreateTokenAsync(client);

        Assert.Null(body.AgentDownload);
        Assert.Equal($"sudo MIRBADLLM_JOIN_TOKEN='{body.Token}' ./install-agent.sh --node-id {NodeId} --enroll-url https://localhost:8443 --ca-hash {body.CaHash}", body.Commands.Linux);
    }

    [Theory]
    [InlineData("ftp://m/")]
    [InlineData("http://m:8080/?x=1")]
    public void Options_RefuseABundleUrlThatIsNotPlainHttp(string url) =>
        Assert.Throws<OptionsValidationException>(() => NodeApiOptions.Validate(new NodeApiOptions { AgentBundleUrl = new Uri(url) }));

    [Fact]
    public async Task JoinToken_RequiresASession_EvenWithAPassword_AndNoPasswordIsNeededWhenSignedIn()
    {
        HttpClient client = await StartAsync();
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        for (int i = 0; i <= AdminPasswordGuard.PermitLimit; i++)
        {
            var (status, code, _) = await PostProblemAsync(client, "/api/nodes/join-tokens",
                new JoinTokenCreateRequest { NodeId = NodeId, Password = Password });
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal(ErrorCodes.ReauthenticationFailed, code);
        }
        Assert.Empty(await Nodes.ListJoinTokensAsync());
        Assert.All(_audit.Records, r => Assert.Equal(AuditResults.Refused, r.Result));
        client.DefaultRequestHeaders.Remove("X-Test-Anonymous");
        using var ok = await client.PostAsJsonAsync("/api/nodes/join-tokens", new { nodeId = NodeId });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Single(await Nodes.ListJoinTokensAsync());
    }

    [Theory]
    [InlineData("Not Valid", null, ErrorCodes.InvalidRequest)]
    [InlineData("join-tokens", null, ErrorCodes.InvalidRequest)]
    [InlineData(NodeId, 9, ErrorCodes.InvalidRequest)]
    [InlineData(NodeId, 1441, ErrorCodes.InvalidRequest)]
    public async Task JoinToken_InvalidRequest_Is400_BeforeThePasswordIsChecked(string nodeId, int? ttl, string code)
    {
        HttpClient client = await StartAsync();

        (HttpStatusCode status, string actual, _) = await PostProblemAsync(client, "/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = nodeId, TtlMinutes = ttl, Password = "not checked" });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(code, actual);
        Assert.Empty(await Nodes.ListJoinTokensAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JoinToken_Rekey_ForAnEnrolledNode_MintsARekeyToken_WithAJoinRekeyLineCarryingTheUid(bool approved)
    {
        HttpClient client = await StartAsync();
        NodeRecord node = approved ? Record(NodeId, approved: true, pin: Pin()) : Record(NodeId, approved: false, pendingPin: Pin());
        await Nodes.UpsertAsync(node);

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = NodeId, Password = Password, Rekey = true }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JoinTokenCreateResponse body = (await response.Content.ReadFromJsonAsync<JoinTokenCreateResponse>(Json))!;
        Assert.Equal(JoinTokenPurpose.Rekey, body.Purpose);
        JoinTokenRecord stored = Assert.Single(await Nodes.ListJoinTokensAsync());
        Assert.Equal((Sha256Url(body.Token), JoinTokenPurpose.Rekey, NodeId), (stored.Sha256, stored.Purpose, stored.NodeId));
        NodeRecord after = (await Nodes.GetAsync(NodeId))!;
        Assert.Equal((node.CertSpkiSha256, node.PendingCertSpkiSha256, node.Approved), (after.CertSpkiSha256, after.PendingCertSpkiSha256, after.Approved));

        // The Linux line is install-agent.sh --rekey (W7 review MN-126: the script stops the service, runs `join --rekey` as
        // the service user, LinuxAgentPackagingTests checks that call against the agent's parser, and restarts the service
        // on the old identity when it fails), uid included (the state may be lost), with the token in the environment only.
        string line = body.Commands.Linux;
        Assert.Contains($"MIRBADLLM_JOIN_TOKEN='{body.Token}'", line, StringComparison.Ordinal);
        Assert.DoesNotContain("--token", line, StringComparison.Ordinal);
        const string verb = "./install-agent.sh ";
        int start = line.IndexOf(verb, StringComparison.Ordinal);
        Assert.True(start >= 0, line);
        string[] args = line[(start + verb.Length)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("--rekey", args[0]);
        string Arg(string name) => args[Array.IndexOf(args, name) + 1];
        Assert.Equal((NodeId, node.Uid, body.CaHash), (Arg("--node-id"), Arg("--node-uid"), Arg("--ca-hash")));
        Assert.Equal(new Uri(body.EnrollUrl), new Uri(Arg("--enroll-url")));

        AuditRecord audit = Assert.Single(_audit.Records);
        Assert.Equal((AuditActions.NodeJoinToken, NodeId, AuditResults.Succeeded), (audit.Action, audit.NodeId, audit.Result));
        Assert.DoesNotContain(body.Token, _audit.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JoinToken_Rekey_ForAnUnknownNeverEnrolledOrRevokedNode_IsRefused_AndMintsNothing()
    {
        HttpClient client = await StartAsync();
        await Nodes.UpsertAsync(Record("pc-new", approved: false)); // a record without a pin: it never enrolled
        await Nodes.UpsertAsync(Record("pc-gone", approved: true, pin: Pin()));
        await Nodes.RevokeAsync("pc-gone");

        (HttpStatusCode Status, string Code, string Text) unknown = await PostProblemAsync(client, "/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = NodeId, Password = Password, Rekey = true });
        (HttpStatusCode Status, string Code, string Text) neverEnrolled = await PostProblemAsync(client, "/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = "pc-new", Password = Password, Rekey = true });
        (HttpStatusCode Status, string Code, string Text) revoked = await PostProblemAsync(client, "/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = "pc-gone", Password = Password, Rekey = true });

        Assert.Equal((HttpStatusCode.NotFound, ErrorCodes.NotFound), (unknown.Status, unknown.Code));
        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.InvalidState), (neverEnrolled.Status, neverEnrolled.Code));
        Assert.Contains("never enrolled", neverEnrolled.Text, StringComparison.Ordinal);
        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.NodeRevoked), (revoked.Status, revoked.Code));
        Assert.Empty(await Nodes.ListJoinTokensAsync());
        Assert.Equal(3, _audit.Records.Count(r => r.Action == AuditActions.NodeJoinToken && r.Result == AuditResults.Refused));
    }

    [Fact]
    public async Task JoinToken_ForAnApprovedOrRevokedNode_Is409()
    {
        HttpClient client = await StartAsync();
        await Nodes.UpsertAsync(Record(NodeId, approved: true, pin: Pin()));
        await Nodes.UpsertAsync(Record("pc-gone", approved: true, pin: Pin()));
        await Nodes.RevokeAsync("pc-gone");

        (HttpStatusCode approved, string approvedCode, _) = await PostProblemAsync(client, "/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = NodeId, Password = Password });
        (HttpStatusCode revoked, string revokedCode, _) = await PostProblemAsync(client, "/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = "pc-gone", Password = Password });

        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.InvalidState), (approved, approvedCode));
        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.NodeRevoked), (revoked, revokedCode));
        Assert.Empty(await Nodes.ListJoinTokensAsync());
    }

    [Fact]
    public async Task JoinToken_Creation_PrunesOldFinishedTokens()
    {
        HttpClient client = await StartAsync();
        JoinTokenIssue old = await Nodes.CreateJoinTokenAsync("pc-old", TimeSpan.FromMinutes(10), JoinTokenPurpose.Join);
        _time.Advance(TimeSpan.FromDays(31));

        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = NodeId, Password = Password, TtlMinutes = 10 }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JoinTokenRecord kept = Assert.Single(await Nodes.ListJoinTokensAsync());
        Assert.NotEqual(old.Record.Sha256, kept.Sha256);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(10), kept.ExpiresAt);
    }

    [Fact]
    public async Task ExpireJoinTokens_Endpoint_RevokesTheNodesUnusedTokens()
    {
        HttpClient client = await StartAsync();
        JoinTokenIssue issue = await Nodes.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        using HttpResponseMessage response = await client.DeleteAsync(new Uri($"/api/nodes/{NodeId}/join-tokens", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("expired").GetInt32());
        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await Nodes.TryConsumeJoinTokenAsync(issue.Token, NodeId)).Outcome);
    }

    // ---- enrollment, approval, reject -----------------------------------------------------------------------------

    [Fact]
    public async Task ApprovalFlow_TokenEnrollApprove_PromotesThePin_RefusesCommandsBefore_AndBurnsLeftoverTokens()
    {
        HttpClient client = await StartAsync();
        JoinTokenCreateResponse first = await CreateTokenAsync(client, displayName: "Office PC");
        JoinTokenCreateResponse spare = await CreateTokenAsync(client);
        string pin = await EnrollAsync(first.Token, addresses: ["192.0.2.50"]);

        NodeRecord pending = (await Nodes.GetAsync(NodeId))!;
        Assert.False(pending.Approved);
        Assert.Equal(pin, pending.PendingCertSpkiSha256);
        Assert.Null(pending.CertSpkiSha256);

        // Approval comes before any command: drain is refused while the node is pending.
        (HttpStatusCode drain, string drainCode, _) = await PostProblemAsync(client, $"/api/nodes/{NodeId}/drain", null);
        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.InvalidState), (drain, drainCode));
        Assert.False((await Nodes.GetAsync(NodeId))!.Draining);

        // The dialog showed another key than the one now pending: refused, nothing changes.
        (HttpStatusCode stale, string staleCode, _) = await PostProblemAsync(client, $"/api/nodes/{NodeId}/approve",
            new NodeApproveRequest { ExpectedSpkiSha256 = Pin() });
        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.InvalidState), (stale, staleCode));
        Assert.False((await Nodes.GetAsync(NodeId))!.Approved);
        Assert.Empty(_refresher.Calls);

        using HttpResponseMessage approve = await client.PostAsJsonAsync($"/api/nodes/{NodeId}/approve",
            new NodeApproveRequest { ExpectedSpkiSha256 = pin }, Json);

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        NodeView view = (await approve.Content.ReadFromJsonAsync<NodeView>(Json))!;
        Assert.True(view.Node.Approved);
        NodeRecord approved = (await Nodes.GetAsync(NodeId))!;
        Assert.True(approved.Approved);
        Assert.Equal(_time.GetUtcNow(), approved.ApprovedAt);
        Assert.Equal(pin, approved.CertSpkiSha256);
        Assert.Null(approved.PendingCertSpkiSha256);
        Assert.Equal("https://192.0.2.50:9443", approved.Address);
        Assert.Equal("Office PC", approved.DisplayName);
        Assert.Equal([NodeId], _refresher.Calls);

        // The spare token minted before approval can no longer re-enroll (and un-approve) the node.
        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await Nodes.TryConsumeJoinTokenAsync(spare.Token, NodeId)).Outcome);

        // Approving twice is a conflict, not a silent re-approval.
        (HttpStatusCode again, string againCode, _) = await PostProblemAsync(client, $"/api/nodes/{NodeId}/approve", null);
        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.InvalidState), (again, againCode));

        Assert.Contains(_audit.Records, r => r is { Action: AuditActions.NodeApprove, Result: AuditResults.Succeeded, NodeId: NodeId });
        Assert.Contains(_audit.Records, r => r is { Action: AuditActions.NodeDrain, Result: AuditResults.Refused, ErrorCode: ErrorCodes.InvalidState });
    }

    [Fact]
    public async Task Approve_WithAddresses_StoresTheConfirmedOnes_AndRefusesAnInvalidOne()
    {
        HttpClient client = await StartAsync();
        string pin = await EnrollAsync((await CreateTokenAsync(client)).Token, addresses: ["192.0.2.50"]);

        (HttpStatusCode bad, string badCode, _) = await PostProblemAsync(client, $"/api/nodes/{NodeId}/approve",
            new NodeApproveRequest { Address = "http://192.0.2.50:9443" });
        Assert.Equal((HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest), (bad, badCode));
        Assert.False((await Nodes.GetAsync(NodeId))!.Approved);

        using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/nodes/{NodeId}/approve", new NodeApproveRequest
        {
            ExpectedSpkiSha256 = pin,
            Address = "https://pc-office.lan:9443/",
            AltAddresses = ["https://192.0.2.50:9443", "https://pc-office.lan:9443"],
        }, Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        NodeRecord node = (await Nodes.GetAsync(NodeId))!;
        Assert.Equal("https://pc-office.lan:9443", node.Address);
        Assert.Equal(["https://192.0.2.50:9443"], node.AltAddresses);
    }

    [Fact]
    public async Task Reject_DeletesAPendingNodeAndItsUnusedTokens_RefusesAnApprovedOne()
    {
        HttpClient client = await StartAsync();
        await EnrollAsync((await CreateTokenAsync(client)).Token);
        JoinTokenCreateResponse spare = await CreateTokenAsync(client);
        await Nodes.UpsertAsync(Record("pc-lab", approved: true, pin: Pin()));

        using HttpResponseMessage reject = await client.PostAsync(new Uri($"/api/nodes/{NodeId}/reject", UriKind.Relative), null);
        (HttpStatusCode refused, string refusedCode, _) = await PostProblemAsync(client, "/api/nodes/pc-lab/reject", null);
        (HttpStatusCode missing, string missingCode, _) = await PostProblemAsync(client, "/api/nodes/pc-none/reject", null);

        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
        Assert.Null(await Nodes.GetAsync(NodeId));
        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await Nodes.TryConsumeJoinTokenAsync(spare.Token, NodeId)).Outcome);
        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.InvalidState), (refused, refusedCode));
        Assert.NotNull(await Nodes.GetAsync("pc-lab"));
        Assert.Equal((HttpStatusCode.NotFound, ErrorCodes.NotFound), (missing, missingCode));
        Assert.Contains(NodeId, _refresher.Calls);
        Assert.Contains(_audit.Records, r => r is { Action: AuditActions.NodeReject, Result: AuditResults.Succeeded, NodeId: NodeId });
    }

    // ---- drain, patch ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DrainAndUndrain_SetTheFlag_RefuseRevokedAndUnknownNodes()
    {
        HttpClient client = await StartAsync();
        await Nodes.UpsertAsync(Record(NodeId, approved: true, pin: Pin()));
        await Nodes.UpsertAsync(Record("pc-gone", approved: true, pin: Pin()));
        await Nodes.RevokeAsync("pc-gone");

        using HttpResponseMessage drain = await client.PostAsync(new Uri($"/api/nodes/{NodeId}/drain", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, drain.StatusCode);
        Assert.True((await drain.Content.ReadFromJsonAsync<NodeView>(Json))!.Node.Draining);
        Assert.True((await Nodes.GetAsync(NodeId))!.Draining);

        using HttpResponseMessage undrain = await client.PostAsync(new Uri($"/api/nodes/{NodeId}/undrain", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, undrain.StatusCode);
        Assert.False((await Nodes.GetAsync(NodeId))!.Draining);

        (HttpStatusCode revoked, string revokedCode, _) = await PostProblemAsync(client, "/api/nodes/pc-gone/drain", null);
        (HttpStatusCode unknown, string unknownCode, _) = await PostProblemAsync(client, "/api/nodes/pc-none/drain", null);
        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.NodeRevoked), (revoked, revokedCode));
        Assert.Equal((HttpStatusCode.NotFound, ErrorCodes.NotFound), (unknown, unknownCode));
        Assert.Equal([NodeId, NodeId], _refresher.Calls);
        Assert.Equal([AuditActions.NodeDrain, AuditActions.NodeUndrain], _audit.Records.Where(r => r.Result == AuditResults.Succeeded).Select(r => r.Action));
    }

    [Fact]
    public async Task Patch_AppliesPresentFields_KeepsAbsentOnes_AndClearsWithEmptyOrZero()
    {
        HttpClient client = await StartAsync();
        string pin = Pin();
        await Nodes.UpsertAsync(Record(NodeId, approved: true, pin: pin) with
        {
            DisplayName = "Old",
            DefaultVllmImage = "vllm/vllm-openai:v0.11",
            MaxConcurrentStarts = 2,
            Labels = ["x64"],
        });
        const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        using HttpResponseMessage response = await PatchAsync(client, $"/api/nodes/{NodeId}", new NodePatchRequest
        {
            DisplayName = "  Office PC  ",
            Address = "https://192.0.2.60:9443/",
            AltAddresses = ["https://pc.lan:9443", "https://pc.lan:9443", "https://192.0.2.60:9443"],
            MemoryReserveGiB = 6,
            GpuReserveGiB = new Dictionary<string, double> { ["GPU-aaaa"] = 1.5 },
            MaxConcurrentStarts = 0,
            DefaultVllmImage = "",
            DefaultLlamaCppImage = "ghcr.io/ggml-org/llama.cpp:server-cuda",
            DefaultLlamaCppImageDigest = Digest,
            Labels = ["x64", "rtx", "x64"],
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        NodeRecord node = (await Nodes.GetAsync(NodeId))!;
        Assert.Equal("Office PC", node.DisplayName);
        Assert.Equal("https://192.0.2.60:9443", node.Address);
        Assert.Equal(["https://pc.lan:9443"], node.AltAddresses);
        Assert.Equal(6, node.MemoryReserveGiB);
        Assert.Equal(1.5, node.GpuReserveGiB["GPU-aaaa"]);
        Assert.Null(node.MaxConcurrentStarts);
        Assert.Null(node.DefaultVllmImage);
        Assert.Equal("ghcr.io/ggml-org/llama.cpp:server-cuda", node.DefaultLlamaCppImage);
        Assert.Equal(Digest, node.DefaultLlamaCppImageDigest);
        Assert.Equal(["x64", "rtx"], node.Labels);
        Assert.Equal(pin, node.CertSpkiSha256); // pins and approval are not patchable
        Assert.True(node.Approved);

        // An empty patch keeps everything.
        using HttpResponseMessage empty = await PatchAsync(client, $"/api/nodes/{NodeId}", new NodePatchRequest());
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Equal(node with { }, (await Nodes.GetAsync(NodeId))! with { }, new NodeRecordComparer());
    }

    [Theory]
    [InlineData("""{"address":"http://192.0.2.60:9443"}""")]
    [InlineData("""{"address":"https://192.0.2.60:9443/agent"}""")]
    [InlineData("""{"altAddresses":["https://user@host:9443"]}""")]
    [InlineData("""{"memoryReserveGiB":-1}""")]
    [InlineData("""{"maxConcurrentStarts":-2}""")]
    [InlineData("""{"defaultVllmImageDigest":"sha256:XYZ"}""")]
    [InlineData("""{"defaultVllmImage":"bad image"}""")]
    [InlineData("""{"labels":["has space"]}""")]
    [InlineData("""{"gpuReserveGiB":{"GPU-1":-0.5}}""")]
    [InlineData("""{"memoryReserveGiB":"lots"}""")]
    public async Task Patch_InvalidValue_Is400_AndChangesNothing(string body)
    {
        HttpClient client = await StartAsync();
        NodeRecord before = Record(NodeId, approved: true, pin: Pin()) with { DisplayName = "Keep" };
        await Nodes.UpsertAsync(before);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PatchAsync(new Uri($"/api/nodes/{NodeId}", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidRequest, (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString());
        NodeRecord after = (await Nodes.GetAsync(NodeId))!;
        Assert.Equal(before.Address, after.Address);
        Assert.Equal(before.MemoryReserveGiB, after.MemoryReserveGiB);
        Assert.Equal(before.Labels, after.Labels);
        Assert.Null(after.DefaultVllmImageDigest);
        Assert.Empty(_refresher.Calls);
    }

    [Fact]
    public async Task Patch_RevokedNode_Is409()
    {
        HttpClient client = await StartAsync();
        await Nodes.UpsertAsync(Record(NodeId, approved: true, pin: Pin()));
        await Nodes.RevokeAsync(NodeId);

        using HttpResponseMessage response = await PatchAsync(client, $"/api/nodes/{NodeId}", new NodePatchRequest { DisplayName = "x" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.NodeRevoked, (await response.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString());
        Assert.Null((await Nodes.GetAsync(NodeId))!.DisplayName);
    }

    // ---- revoke ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Revoke_WithPlacedModels_Is409ModelsPlaced_UnlessUnplaceModels()
    {
        HttpClient client = await StartAsync();
        await Nodes.UpsertAsync(Record(NodeId, approved: true, pin: Pin()));
        await PlaceAsync("m-b", NodeId);
        await PlaceAsync("m-a", NodeId);
        await PlaceAsync("m-other", "dgx");
        JoinTokenIssue leftover = await Nodes.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        (HttpStatusCode placed, JsonElement problem) = await DeleteAsync(client, NodeId,
            new NodeRevokeRequest { Password = Password, Confirm = NodeId });

        Assert.Equal(HttpStatusCode.Conflict, placed);
        Assert.Equal(ErrorCodes.ModelsPlaced, problem.GetProperty("code").GetString());
        Assert.Equal(["m-a", "m-b"], problem.GetProperty("modelIds").EnumerateArray().Select(e => e.GetString()));
        Assert.False((await Nodes.GetAsync(NodeId))!.Revoked);
        Assert.Equal(NodeId, (await Models.GetAsync("m-a"))!.Placement?.NodeId);
        Assert.Empty(_runtime.Stopped);

        (HttpStatusCode ok, JsonElement body) = await DeleteAsync(client, NodeId,
            new NodeRevokeRequest { Password = Password, Confirm = NodeId, UnplaceModels = true });

        Assert.Equal(HttpStatusCode.OK, ok);
        NodeRevokeResult result = body.Deserialize<NodeRevokeResult>(Json)!;
        Assert.Equal(NodeId, result.NodeId);
        Assert.Equal(["m-a", "m-b"], result.UnplacedModelIds);
        Assert.Equal(1, result.ExpiredJoinTokens);
        Assert.Equal(["m-a", "m-b"], _runtime.Stopped);
        Assert.Null((await Models.GetAsync("m-a"))!.Placement);
        Assert.Null((await Models.GetAsync("m-b"))!.Placement);
        Assert.Equal("dgx", (await Models.GetAsync("m-other"))!.Placement?.NodeId);

        NodeRecord revoked = (await Nodes.GetAsync(NodeId))!;
        Assert.True(revoked.Revoked);
        Assert.Null(revoked.CertSpkiSha256);
        Assert.Null(revoked.PendingCertSpkiSha256);
        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await Nodes.TryConsumeJoinTokenAsync(leftover.Token, NodeId)).Outcome);
        Assert.Equal([NodeId], _refresher.Calls);

        Assert.Equal(2, _audit.Records.Count(r => r is { Action: AuditActions.ModelUnplace, Result: AuditResults.Succeeded, NodeId: NodeId }));
        Assert.Contains(_audit.Records, r => r is { Action: AuditActions.NodeRevoke, Result: AuditResults.Refused, ErrorCode: ErrorCodes.ModelsPlaced });
        Assert.Contains(_audit.Records, r => r is { Action: AuditActions.NodeRevoke, Result: AuditResults.Succeeded });

        // Revoked for good: no new token, no second revoke.
        (HttpStatusCode again, JsonElement againProblem) = await DeleteAsync(client, NodeId,
            new NodeRevokeRequest { Password = Password, Confirm = NodeId });
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Equal(ErrorCodes.NodeRevoked, againProblem.GetProperty("code").GetString());
        (HttpStatusCode token, string tokenCode, _) = await PostProblemAsync(client, "/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = NodeId, Password = Password });
        Assert.Equal((HttpStatusCode.Conflict, ErrorCodes.NodeRevoked), (token, tokenCode));
    }

    [Fact]
    public async Task Revoke_NeedsTheTypedIdAndTheSession()
    {
        HttpClient client = await StartAsync();
        await Nodes.UpsertAsync(Record(NodeId, approved: true, pin: Pin()));

        (HttpStatusCode confirm, JsonElement confirmProblem) = await DeleteAsync(client, NodeId,
            new NodeRevokeRequest { Password = Password, Confirm = "pc-offic" });
        client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        (HttpStatusCode password, JsonElement passwordProblem) = await DeleteAsync(client, NodeId,
            new NodeRevokeRequest { Password = "nope", Confirm = NodeId });

        Assert.Equal(HttpStatusCode.Conflict, confirm);
        Assert.Equal(ErrorCodes.ConfirmationRequired, confirmProblem.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, password);
        Assert.Equal(ErrorCodes.ReauthenticationFailed, passwordProblem.GetProperty("code").GetString());
        Assert.False((await Nodes.GetAsync(NodeId))!.Revoked);
        Assert.Empty(_refresher.Calls);
    }

    [Fact]
    public async Task Revoke_ModelThatDoesNotStop_IsUnplacedAfterTheStopTimeout()
    {
        HttpClient client = await StartAsync(o => o.RevokeStopTimeout = TimeSpan.FromMilliseconds(200));
        await Nodes.UpsertAsync(Record(NodeId, approved: true, pin: Pin()));
        await PlaceAsync("m-stuck", NodeId);
        _runtime.Hang = true;

        (HttpStatusCode status, JsonElement body) = await DeleteAsync(client, NodeId,
            new NodeRevokeRequest { Password = Password, Confirm = NodeId, UnplaceModels = true });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["m-stuck"], body.Deserialize<NodeRevokeResult>(Json)!.UnplacedModelIds);
        Assert.True(_runtime.StopWasCancelled);
        Assert.Null((await Models.GetAsync("m-stuck"))!.Placement);
        Assert.True((await Nodes.GetAsync(NodeId))!.Revoked);
    }

    [Fact]
    public async Task Revoke_MalformedBody_Is400_WithoutEchoingIt()
    {
        HttpClient client = await StartAsync();
        await Nodes.UpsertAsync(Record(NodeId, approved: true, pin: Pin()));

        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/nodes/{NodeId}")
        {
            Content = new StringContent("""{"password":"hunter2-secret","confirm":""", Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(ErrorCodes.InvalidRequest, text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2-secret", text, StringComparison.Ordinal);
        Assert.False((await Nodes.GetAsync(NodeId))!.Revoked);
    }

    // ---- reads ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_HidesRevokedNodesUnlessAsked_AndCountsPlacedModels()
    {
        HttpClient client = await StartAsync();
        await Nodes.UpsertAsync(Record(NodeId, approved: true, pin: Pin()));
        await Nodes.UpsertAsync(Record("pc-gone", approved: true, pin: Pin()));
        await Nodes.RevokeAsync("pc-gone");
        await PlaceAsync("m-a", NodeId);
        await PlaceAsync("m-b", NodeId);

        NodeView[] visible = (await client.GetFromJsonAsync<NodeView[]>("/api/nodes", Json))!;
        NodeView[] all = (await client.GetFromJsonAsync<NodeView[]>("/api/nodes?includeRevoked=true", Json))!;
        NodeView one = (await client.GetFromJsonAsync<NodeView>($"/api/nodes/{NodeId}", Json))!;
        using HttpResponseMessage missing = await client.GetAsync(new Uri("/api/nodes/pc-none", UriKind.Relative));

        Assert.Equal([NodeId], visible.Select(v => v.Node.Id));
        Assert.Equal(["pc-gone", NodeId], all.Select(v => v.Node.Id));
        Assert.Equal(2, one.PlacedModels);
        Assert.Equal(0, one.ReadyModels);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // ---- harness --------------------------------------------------------------------------------------------------

    private JsonNodeRegistry? _nodes;
    private JsonModelRegistry? _models;

    private JsonNodeRegistry Nodes => _nodes ??= Registry();

    private JsonModelRegistry Models => _models ??= new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = DataDir }));

    private JsonNodeRegistry Registry() => new(Options.Create(new MirbadLLMOptions { DataPath = DataDir }), _time);

    private async Task<HttpClient> StartAsync(Action<NodeApiOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.Configure<MirbadLLMOptions>(o => o.DataPath = DataDir);
        builder.Services.AddSingleton<INodeRegistry>(_ => Nodes);
        builder.Services.AddSingleton<IModelRegistry>(_ => Models);
        builder.Services.AddSingleton(_ca);
        builder.Services.AddSingleton<IAuditLog>(_audit);
        builder.Services.AddSingleton<INodeConnectionRefresher>(_refresher);
        builder.Services.AddSingleton<IModelRuntime>(_runtime);
        builder.Services.AddOptions<AuthOptions>().Configure(o => o.AdminPassword = Password);
        builder.Services.AddMirbadLLMAdminReauth();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddMirbadLLMNodeApi(builder.Configuration);
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        _app = builder.Build();
        _app.Use(async (http, next) => { if (!http.Request.Headers.ContainsKey("X-Test-Anonymous")) http.User = TestAdmin.User; await next(http); });
        _app.MapNodeApi();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        return _client;
    }

    private static async Task<JoinTokenCreateResponse> CreateTokenAsync(HttpClient client, string? displayName = null)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/api/nodes/join-tokens",
            new JoinTokenCreateRequest { NodeId = NodeId, Password = Password, DisplayName = displayName }, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JoinTokenCreateResponse>(Json))!;
    }

    /// <summary>Enrolls <see cref="NodeId"/> through the real <see cref="EnrollmentService"/>; returns the pending pin.</summary>
    private async Task<string> EnrollAsync(string token, IReadOnlyList<string>? addresses = null)
    {
        using var service = new EnrollmentService(
            Nodes,
            _ca,
            Options.Create(new EnrollmentOptions()),
            Options.Create(new RegistryMigrationOptions()),
            _time,
            NullLogger<EnrollmentService>.Instance);
        using ECDsa key = PkiDefaults.CreateKey();
        EnrollmentResult result = await service.EnrollAsync(new EnrollRequest
        {
            Token = token,
            NodeId = NodeId,
            CsrPem = CertificateSigningRequests.CreatePem(key, NodeId),
            Addresses = addresses ?? [],
        }, IPAddress.Parse("192.0.2.50"));
        Assert.Equal(EnrollmentStatus.Enrolled, result.Status);
        return Spki.Sha256(key);
    }

    private async Task PlaceAsync(string modelId, string nodeId) =>
        await Models.UpsertAsync(new ModelDefinition { Id = modelId, ServedName = modelId, ModelPath = modelId, Placement = new ModelPlacement(nodeId) });

    private static async Task<(HttpStatusCode Status, string Code, string Text)> PostProblemAsync(HttpClient client, string path, object? body)
    {
        using HttpResponseMessage response = body is null
            ? await client.PostAsync(new Uri(path, UriKind.Relative), null)
            : await client.PostAsync(new Uri(path, UriKind.Relative), JsonContent.Create(body, body.GetType(), options: Json));
        string text = await response.Content.ReadAsStringAsync();
        string code = text.Length > 0 && JsonDocument.Parse(text).RootElement.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "" : "";
        return (response.StatusCode, code, text);
    }

    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, string path, NodePatchRequest body) =>
        client.PatchAsJsonAsync(path, body, Json);

    private static async Task<(HttpStatusCode Status, JsonElement Body)> DeleteAsync(HttpClient client, string nodeId, NodeRevokeRequest body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/nodes/{nodeId}") { Content = JsonContent.Create(body, options: Json) };
        using HttpResponseMessage response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>(Json));
    }

    private static NodeRecord Record(string id, bool approved, string? pin = null, string? pendingPin = null) => new()
    {
        Id = id,
        Uid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
        Address = "https://192.0.2.50:9443",
        Approved = approved,
        CertSpkiSha256 = pin,
        PendingCertSpkiSha256 = pendingPin,
        MemoryReserveGiB = 4,
        Labels = ["x64"],
    };

    private static string Pin()
    {
        using ECDsa key = PkiDefaults.CreateKey();
        return Spki.Sha256(key);
    }

    private static string ReadBundleToken(string path) =>
        JsonSerializer.Deserialize<BootstrapBundle>(File.ReadAllText(path), AgentJson.Options)!.Token;

    private static string Sha256Url(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Compares records by their serialized form (collections compare by content).</summary>
    private sealed class NodeRecordComparer : IEqualityComparer<NodeRecord>
    {
        public bool Equals(NodeRecord? x, NodeRecord? y) =>
            JsonSerializer.Serialize(x, MasterStateJson.Options) == JsonSerializer.Serialize(y, MasterStateJson.Options);

        public int GetHashCode(NodeRecord obj) => 0;
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        private readonly ConcurrentQueue<AuditRecord> _records = new();

        public IReadOnlyList<AuditRecord> Records => [.. _records];

        public string AllText() => string.Join("\n", _records.Select(r => JsonSerializer.Serialize(r, MasterStateJson.Options)));

        public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            _records.Enqueue(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([.. _records.Reverse()]);
    }

    private sealed class RecordingRefresher : INodeConnectionRefresher
    {
        private readonly ConcurrentQueue<string> _calls = new();

        public IReadOnlyList<string> Calls => [.. _calls];

        public Task RefreshAsync(string nodeId, CancellationToken ct = default)
        {
            _calls.Enqueue(nodeId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntime : IModelRuntime
    {
        private readonly ConcurrentQueue<string> _stopped = new();

        public bool Hang { get; set; }

        public bool StopWasCancelled { get; private set; }

        public IReadOnlyList<string> Stopped => [.. _stopped];

        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelStatus>>([]);

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) => Task.FromResult(new ModelStatus(id, ModelState.Stopped));

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task StopAsync(string id, CancellationToken ct = default)
        {
            _stopped.Enqueue(id);
            if (Hang)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException)
                {
                    StopWasCancelled = true;
                    throw;
                }
            }
        }

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998
    }
}
