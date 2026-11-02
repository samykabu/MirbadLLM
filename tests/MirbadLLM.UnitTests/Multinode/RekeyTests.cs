using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Enrollment;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Pki;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Pki;
using MirbadLLM.Web.Enrollment;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required here.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-114: <c>join --rekey</c> and <c>POST /enroll/rekey</c> (design §9.4 step 5, §10.2 item 6, D78) over real TLS on
/// loopback (SChannel on Windows, OpenSSL on Linux): the agent's CA-hash check, the master's token purpose split between
/// <c>/enroll</c> and <c>/enroll/rekey</c>, the uid and pin handling, the rotation clean-up on both sides, one generic
/// refusal, the shared rate limit and the audit lines.
/// </summary>
public sealed class RekeyTests : IDisposable
{
    private const string NodeId = "pc-office";
    private static readonly string[] AgentEkus = [PkiDefaults.ServerAuthOid, PkiDefaults.ClientAuthOid];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-rekey-" + Guid.NewGuid().ToString("N"));
    private readonly CertificateAuthority _ca = CertificateAuthority.Create();

    public void Dispose()
    {
        _ca.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string DataDir => Path.Combine(_root, "data");

    private string StateDir(string name = "agent") => Path.Combine(_root, "state-" + name);

    private string CaHash => Spki.ToJoinHash(_ca.SpkiSha256);

    private static AgentEnrollment Agent() => new(TimeProvider.System, NullLogger<AgentEnrollment>.Instance);

    private JoinParameters Join(Master master, string token, string nodeId = NodeId, string? state = null, bool force = false) => new()
    {
        EnrollUrl = master.Https,
        Token = token,
        CaHash = CaHash,
        NodeId = nodeId,
        StateDirectory = state ?? StateDir(),
        Addresses = ["192.0.2.50"],
        Force = force,
    };

    private RekeyParameters Rekey(Master master, string token, string? uid = null, string nodeId = NodeId, string? state = null, string? caHash = null) => new()
    {
        EnrollUrl = master.Https,
        Token = token,
        CaHash = caHash ?? CaHash,
        NodeId = nodeId,
        NodeUid = uid,
        StateDirectory = state ?? StateDir(),
        Addresses = ["192.0.2.50"],
    };

    /// <summary>Enrolls <see cref="NodeId"/> with a join token and approves it, as the operator would.</summary>
    private async Task<EnrollmentOutcome> EnrollApprovedAsync(Master master, string? state = null)
    {
        JoinTokenIssue join = await master.Registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        EnrollmentOutcome outcome = await Agent().JoinAsync(Join(master, join.Token, state: state));
        await master.Registry.UpdateAsync(NodeId, n => n with
        {
            Approved = true,
            ApprovedAt = DateTimeOffset.UtcNow,
            CertSpkiSha256 = n.PendingCertSpkiSha256,
            PendingCertSpkiSha256 = null,
        });
        return outcome;
    }

    private static Task<JoinTokenIssue> RekeyTokenAsync(Master master, string nodeId = NodeId, TimeSpan? lifetime = null) =>
        master.Registry.CreateJoinTokenAsync(nodeId, lifetime ?? TimeSpan.FromHours(1), JoinTokenPurpose.Rekey);

    /// <summary>
    /// A rekey token whose node no longer qualifies when the token is redeemed. The registry only mints one for an enrolled,
    /// non-revoked node (<see cref="JsonNodeRegistry.RekeyRefusal"/>), so the node enrolls first; then its record is deleted
    /// (<paramref name="keep"/> false: an unknown node; the delete also expires the token) or replaced by a record of the
    /// same id that never enrolled (<paramref name="keep"/> true; the token stays live). The routes must still refuse it.
    /// </summary>
    private static async Task<JoinTokenIssue> StrandedRekeyTokenAsync(Master master, string nodeId, bool keep = false)
    {
        const string uid = "00112233445566778899aabbccddeeff";
        string pin;
        using (ECDsa key = PkiDefaults.CreateKey())
        {
            pin = Spki.Sha256(key);
        }

        var enrolled = new NodeRecord { Id = nodeId, Uid = uid, Address = "https://10.0.0.8:9443", PendingCertSpkiSha256 = pin, EnrolledAt = DateTimeOffset.UtcNow };
        await master.Registry.UpsertAsync(enrolled);
        JoinTokenIssue token = await RekeyTokenAsync(master, nodeId);
        if (keep)
        {
            await master.Registry.UpsertAsync(enrolled with { PendingCertSpkiSha256 = null, EnrolledAt = null });
        }
        else
        {
            Assert.True(await master.Registry.DeleteAsync(nodeId));
        }

        return token;
    }

    private FileAgentCredentials Credentials(string? state = null) =>
        new(new AgentHostOptions { StateDirectory = state ?? StateDir() }, NullLogger<FileAgentCredentials>.Instance);

    /// <summary>What the master's agent client would accept for the node now (current + pending pin, design §4.2).</summary>
    private async Task<PeerValidationResult> MasterAcceptsAsync(Master master, X509Certificate2 certificate)
    {
        NodeRecord node = (await master.Registry.GetAsync(NodeId))!;
        string[] pins = [.. new[] { node.CertSpkiSha256, node.PendingCertSpkiSha256 }.Where(p => p is not null).Select(p => p!)];
        using TrustAnchorSet trust = PkiTestSupport.Trust(_ca);
        return PeerValidator.ForAgent(NodeId, trust, pins).Validate(certificate);
    }

    // ---- the re-pair itself --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rekey_AfterLostState_KeepsIdUidApprovalAndSettings_AndReplacesThePins()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(master);
        await master.Registry.UpdateAsync(NodeId, n => n with { DisplayName = "Office PC", Address = "https://office.lan:9443", Draining = true, MemoryReserveGiB = 6 });
        NodeRecord before = (await master.Registry.GetAsync(NodeId))!;
        using X509Certificate2 oldCertificate = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(StateDir(), FileAgentCredentials.CertificateFileName)));
        Assert.True((await MasterAcceptsAsync(master, oldCertificate)).IsValid);

        // The state volume is lost: nothing of the old identity is left on the node.
        Directory.Delete(StateDir(), recursive: true);
        JoinTokenIssue rekey = await RekeyTokenAsync(master);
        using StringWriter output = new();
        using StringWriter error = new();
        int exit = await JoinCommand.RunAsync(
            ["--rekey", "--enroll-url", master.Https.ToString(), "--token", rekey.Token, "--ca-hash", CaHash,
             "--node-id", NodeId, "--node-uid", first.NodeUid, "--state-dir", StateDir(), "--address", "192.0.2.50"],
            output, error);

        Assert.True(exit == JoinCommand.ExitOk, error.ToString());
        Assert.DoesNotContain(rekey.Token, output.ToString() + error.ToString(), StringComparison.Ordinal);
        Assert.Contains(first.NodeUid, output.ToString(), StringComparison.Ordinal);

        NodeRecord after = (await master.Registry.GetAsync(NodeId))!;
        FileAgentCredentials credentials = Credentials();
        Assert.True(credentials.IsEnrolled);
        Assert.Equal(NodeId, credentials.NodeId);
        Assert.Equal(first.NodeUid, credentials.NodeUid);
        string newPin = Spki.Sha256(credentials.ServerCertificate);

        Assert.Equal(before.Uid, after.Uid);
        Assert.True(after.Approved);
        Assert.Equal(before.ApprovedAt, after.ApprovedAt);
        Assert.Equal(newPin, after.CertSpkiSha256);
        Assert.NotEqual(first.CertificateSpkiSha256, newPin);
        Assert.Null(after.PendingCertSpkiSha256);
        Assert.Null(after.Rotation);
        Assert.Equal(("Office PC", "https://office.lan:9443", true, 6d), (after.DisplayName, after.Address, after.Draining, after.MemoryReserveGiB));
        Assert.NotNull((await master.Registry.ListJoinTokensAsync()).Single(t => t.Purpose == JoinTokenPurpose.Rekey).UsedAt);

        // The master now accepts exactly the new key: the old certificate is refused on pin.
        Assert.True((await MasterAcceptsAsync(master, credentials.ServerCertificate)).IsValid);
        Assert.Equal(PeerValidationFailure.PinMismatch, (await MasterAcceptsAsync(master, oldCertificate)).Failure);
    }

    [Fact]
    public async Task Rekey_ExpiredCertificate_TakesTheUidFromTheState_AndKeepsTheRestOfTheState()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(master);

        // The node was offline for > 90 days: its certificate (same key, same pin) expired.
        string keyPath = Path.Combine(StateDir(), FileAgentCredentials.KeyFileName);
        using (ECDsa oldKey = ECDsa.Create())
        {
            oldKey.ImportFromPem(File.ReadAllText(keyPath));
            using X509Certificate2 expired = PkiTestSupport.SignCustom(_ca.Certificate, _ca.ExportPrivateKeyPem(),
                DateTimeOffset.UtcNow.AddDays(-200), DateTimeOffset.UtcNow.AddDays(-110), [Protocol.NodeSpiffeId(NodeId)], AgentEkus, subjectKey: oldKey);
            File.WriteAllText(Path.Combine(StateDir(), FileAgentCredentials.CertificateFileName), expired.ExportCertificatePem() + "\n");
            Assert.Equal(PeerValidationFailure.NotTimeValid, (await MasterAcceptsAsync(master, expired)).Failure);
        }

        string operations = Path.Combine(StateDir(), "operations.jsonl");
        File.WriteAllText(operations, "{\"operationId\":\"op-1\"}\n");

        EnrollmentOutcome outcome = await Agent().RekeyAsync(Rekey(master, (await RekeyTokenAsync(master)).Token));

        Assert.Equal(first.NodeUid, outcome.NodeUid);
        Assert.False(outcome.PendingApproval);
        Assert.True(outcome.NotAfter > DateTimeOffset.UtcNow.AddDays(80));
        FileAgentCredentials credentials = Credentials();
        Assert.True(credentials.IsEnrolled);
        Assert.True((await MasterAcceptsAsync(master, credentials.ServerCertificate)).IsValid);
        Assert.Equal("{\"operationId\":\"op-1\"}\n", File.ReadAllText(operations));
        Assert.Equal(outcome.CertificateSpkiSha256, (await master.Registry.GetAsync(NodeId))!.CertSpkiSha256);
    }

    [Fact]
    public async Task Rekey_DropsTheOldIdentitysRotation_OnTheAgentAndOnTheMaster()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(master);
        const string staleRotationPin = "c3RhbGUtcGVuZGluZy1waW4tb2YtdGhlLW9sZC1rZXk=";
        await master.Registry.UpdateAsync(NodeId, n => n with
        {
            PendingCertSpkiSha256 = staleRotationPin,
            Rotation = new CertificateRotationRecord
            {
                RotationId = "rot-0011223344556677",
                NodeUid = n.Uid,
                Stage = CertificateRotationStage.CertInstalled,
                NewSpkiSha256 = staleRotationPin,
                StartedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            },
        });
        string[] rotationFiles = [AgentRotationStore.DocumentFileName, AgentRotationStore.PendingKeyFileName, AgentRotationStore.PendingCertificateFileName];
        foreach (string file in rotationFiles)
        {
            File.WriteAllText(Path.Combine(StateDir(), file), "stale");
        }

        EnrollmentOutcome outcome = await Agent().RekeyAsync(Rekey(master, (await RekeyTokenAsync(master)).Token));

        Assert.All(rotationFiles, f => Assert.False(File.Exists(Path.Combine(StateDir(), f)), f));
        NodeRecord node = (await master.Registry.GetAsync(NodeId))!;
        Assert.Null(node.Rotation);
        Assert.Null(node.PendingCertSpkiSha256);
        Assert.Equal(outcome.CertificateSpkiSha256, node.CertSpkiSha256);
        Assert.Equal(first.NodeUid, node.Uid);
        AuditRecord line = Assert.Single(master.Audit.Lines, l => l.Result == AuditResults.Succeeded && l.Detail!.StartsWith("rekey", StringComparison.Ordinal));
        Assert.Equal("rot-0011223344556677", line.Target);
    }

    [Fact]
    public async Task Rekey_OfANodeStillPendingApproval_KeepsItPending_WithTheNewPendingPin()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue join = await master.Registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        EnrollmentOutcome first = await Agent().JoinAsync(Join(master, join.Token));

        EnrollmentOutcome outcome = await Agent().RekeyAsync(Rekey(master, (await RekeyTokenAsync(master)).Token));

        Assert.True(outcome.PendingApproval);
        NodeRecord node = (await master.Registry.GetAsync(NodeId))!;
        Assert.False(node.Approved);
        Assert.Null(node.CertSpkiSha256);
        Assert.Equal(outcome.CertificateSpkiSha256, node.PendingCertSpkiSha256);
        Assert.NotEqual(first.CertificateSpkiSha256, node.PendingCertSpkiSha256);
        Assert.Equal(first.NodeUid, node.Uid);
    }

    [Fact]
    public async Task BootstrapNodeThatLostItsStateVolume_CannotReEnrollWithABootstrapToken_ButRePairsWithRekey()
    {
        // The P1 readiness gap (plan: "an agent that loses its state volume cannot re-pair in P1", D48).
        const string dgx = "dgx";
        await using Master master = await Master.StartAsync(DataDir, _ca);
        JoinTokenIssue bootstrap = await master.Registry.CreateJoinTokenAsync(dgx, TimeSpan.FromHours(1), JoinTokenPurpose.Bootstrap);
        EnrollmentOutcome first = await Agent().JoinAsync(Join(master, bootstrap.Token, nodeId: dgx));
        Assert.False(first.PendingApproval);
        Directory.Delete(StateDir(), recursive: true);

        JoinTokenIssue olderBundle = await master.Registry.CreateJoinTokenAsync(dgx, TimeSpan.FromHours(1), JoinTokenPurpose.Bootstrap);
        AgentEnrollmentException refused = await Assert.ThrowsAsync<AgentEnrollmentException>(() => Agent().JoinAsync(Join(master, olderBundle.Token, nodeId: dgx)));
        Assert.Equal(EnrollmentFailure.Refused, refused.Failure);

        EnrollmentOutcome repaired = await Agent().RekeyAsync(Rekey(master, (await RekeyTokenAsync(master, dgx)).Token, uid: first.NodeUid, nodeId: dgx));

        Assert.Equal(first.NodeUid, repaired.NodeUid);
        Assert.False(repaired.PendingApproval);
        NodeRecord node = (await master.Registry.GetAsync(dgx))!;
        Assert.True(node.Approved);
        Assert.Equal(repaired.CertificateSpkiSha256, node.CertSpkiSha256);
        Assert.Equal(first.NodeUid, node.Uid);
    }

    // ---- token purposes: rekey only on /enroll/rekey, the others never there ---------------------------------------------

    [Fact]
    public async Task RekeyToken_OnEnroll_IsRefusedLikeAnUnknownToken_NotConsumed_AndStillRePairs()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(master);
        NodeRecord before = (await master.Registry.GetAsync(NodeId))!;
        JoinTokenIssue rekey = await RekeyTokenAsync(master);
        JoinTokenIssue rekeyForNewNode = await StrandedRekeyTokenAsync(master, "pc-new");
        using HttpClient client = master.CreateClient();
        int consumed = master.Registry.ConsumeCalls;

        (HttpStatusCode Status, string Body) onEnroll = await PostEnrollAsync(client, master, rekey.Token, NodeId);
        (HttpStatusCode Status, string Body) newNode = await PostEnrollAsync(client, master, rekeyForNewNode.Token, "pc-new");
        (HttpStatusCode Status, string Body) unknown = await PostEnrollAsync(client, master, "unknown-token", NodeId);

        Assert.Equal(HttpStatusCode.Forbidden, onEnroll.Status);
        Assert.Equal(unknown, onEnroll);
        Assert.Equal(unknown, newNode);
        Assert.Equal(ErrorCodes.EnrollmentRefused, JsonSerializer.Deserialize<AgentErrorResponse>(onEnroll.Body, AgentJson.Options)!.Error.Code);
        Assert.Equal(consumed + 1, master.Registry.ConsumeCalls); // only the unknown token reached consumption
        Assert.All((await master.Registry.ListJoinTokensAsync()).Where(t => t.Purpose == JoinTokenPurpose.Rekey), t => Assert.Null(t.UsedAt));
        Assert.Null(await master.Registry.GetAsync("pc-new"));
        NodeRecord unchanged = (await master.Registry.GetAsync(NodeId))!;
        Assert.Equal((before.Uid, before.CertSpkiSha256, before.PendingCertSpkiSha256, before.Approved), (unchanged.Uid, unchanged.CertSpkiSha256, unchanged.PendingCertSpkiSha256, unchanged.Approved));

        EnrollmentOutcome repaired = await Agent().RekeyAsync(Rekey(master, rekey.Token));
        Assert.Equal(first.NodeUid, repaired.NodeUid);
    }

    [Fact]
    public async Task JoinAndBootstrapTokens_OnRekey_AreRefused_AndNotConsumed()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(master);
        NodeRecord before = (await master.Registry.GetAsync(NodeId))!;
        JoinTokenIssue join = await master.Registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        JoinTokenIssue bootstrap = await master.Registry.CreateJoinTokenAsync(NodeId, TimeSpan.FromHours(1), JoinTokenPurpose.Bootstrap);
        using HttpClient client = master.CreateClient();
        int consumed = master.Registry.ConsumeCalls;

        (HttpStatusCode Status, string Body) withJoin = await PostRekeyAsync(client, master, join.Token, NodeId, first.NodeUid);
        (HttpStatusCode Status, string Body) withBootstrap = await PostRekeyAsync(client, master, bootstrap.Token, NodeId, first.NodeUid);
        AgentEnrollmentException agentSide = await Assert.ThrowsAsync<AgentEnrollmentException>(() => Agent().RekeyAsync(Rekey(master, join.Token)));

        Assert.Equal(HttpStatusCode.Forbidden, withJoin.Status);
        Assert.Equal(withJoin, withBootstrap);
        Assert.Equal(EnrollmentFailure.Refused, agentSide.Failure);
        Assert.Equal(consumed, master.Registry.ConsumeCalls);
        Assert.Null((await master.Registry.ListJoinTokensAsync()).Single(t => t.Purpose == JoinTokenPurpose.Bootstrap).UsedAt);
        Assert.Equal(2, (await master.Registry.ListJoinTokensAsync()).Count(t => t.UsedAt is null));
        NodeRecord unchanged = (await master.Registry.GetAsync(NodeId))!;
        Assert.Equal(before.CertSpkiSha256, unchanged.CertSpkiSha256);

        // The state directory still holds the first identity (nothing was stored on the refusal).
        Assert.Equal(first.CertificateSpkiSha256, Spki.Sha256(Credentials().ServerCertificate));
    }

    // ---- one generic refusal ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EveryRekeyRefusal_IsTheOneGenericAnswer_OfEnroll_AndChangesNothing()
    {
        ManualTimeProvider registryTime = new(DateTimeOffset.UtcNow);
        await using Master master = await Master.StartAsync(DataDir, _ca, registryTime: registryTime);
        EnrollmentOutcome first = await EnrollApprovedAsync(master);
        using HttpClient client = master.CreateClient();

        JoinTokenIssue used = await RekeyTokenAsync(master);
        Assert.Equal(HttpStatusCode.OK, (await PostRekeyAsync(client, master, used.Token, NodeId, first.NodeUid)).Status);
        NodeRecord before = (await master.Registry.GetAsync(NodeId))!;

        JoinTokenIssue expired = await RekeyTokenAsync(master, lifetime: TimeSpan.FromMinutes(10));
        registryTime.Advance(TimeSpan.FromMinutes(11));
        JoinTokenIssue wrongUid = await RekeyTokenAsync(master);
        JoinTokenIssue badCsr = await RekeyTokenAsync(master);
        JoinTokenIssue otherNode = await StrandedRekeyTokenAsync(master, "pc-lab", keep: true);
        JoinTokenIssue unknownNode = await StrandedRekeyTokenAsync(master, "pc-nowhere");
        await master.Registry.UpsertAsync(new NodeRecord
        {
            Id = "pc-gone", Uid = "ffeeddccbbaa99887766554433221100", Address = "https://10.0.0.9:9443",
            Approved = true, CertSpkiSha256 = before.CertSpkiSha256, EnrolledAt = DateTimeOffset.UtcNow,
        });
        JoinTokenIssue revoked = await RekeyTokenAsync(master, "pc-gone");
        await master.Registry.RevokeAsync("pc-gone");

        (HttpStatusCode Status, string Body)[] answers =
        [
            await PostRekeyAsync(client, master, used.Token, NodeId, first.NodeUid),
            await PostRekeyAsync(client, master, expired.Token, NodeId, first.NodeUid),
            await PostRekeyAsync(client, master, wrongUid.Token, NodeId, "0123456789abcdef0123456789abcdef"),
            await PostRekeyAsync(client, master, badCsr.Token, NodeId, first.NodeUid, TamperedCsr(NodeId)),
            await PostRekeyAsync(client, master, otherNode.Token, NodeId, first.NodeUid),          // token bound to pc-lab
            await PostRekeyAsync(client, master, otherNode.Token, "pc-lab", "00112233445566778899aabbccddeeff"), // pc-lab never enrolled
            await PostRekeyAsync(client, master, unknownNode.Token, "pc-nowhere", first.NodeUid),
            await PostRekeyAsync(client, master, revoked.Token, "pc-gone", "ffeeddccbbaa99887766554433221100"),
            await PostRekeyAsync(client, master, "unknown-token", NodeId, first.NodeUid),
        ];
        (HttpStatusCode Status, string Body) enrollRefusal = await PostEnrollAsync(client, master, "unknown-token", NodeId);

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Forbidden, a.Status));
        Assert.Single(answers.Select(a => a.Body).Append(enrollRefusal.Body).Distinct());
        Assert.Equal(ErrorCodes.EnrollmentRefused, JsonSerializer.Deserialize<AgentErrorResponse>(answers[0].Body, AgentJson.Options)!.Error.Code);
        Assert.All(answers, a => Assert.DoesNotContain("pc-lab", a.Body, StringComparison.Ordinal));

        // The route's own node check refused the stranded tokens before consuming them (the reason is in the audit only).
        string[] details = [.. master.Audit.Lines.Select(l => l.Detail ?? "")];
        Assert.Contains("rekey: node never enrolled (not consumed)", details);
        Assert.Contains("rekey: unknown node (not consumed)", details);
        Assert.Contains("rekey: node revoked (not consumed)", details);

        NodeRecord after = (await master.Registry.GetAsync(NodeId))!;
        Assert.Equal((before.Uid, before.CertSpkiSha256, before.PendingCertSpkiSha256), (after.Uid, after.CertSpkiSha256, after.PendingCertSpkiSha256));
        Assert.True((await master.Registry.GetAsync("pc-gone"))!.Revoked);
        Assert.Null(await master.Registry.GetAsync("pc-nowhere"));

        // Refusals decided before consumption burn nothing: the wrong-uid and broken-CSR tokens still re-pair.
        Assert.Equal(HttpStatusCode.OK, (await PostRekeyAsync(client, master, wrongUid.Token, NodeId, first.NodeUid)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostRekeyAsync(client, master, badCsr.Token, NodeId, first.NodeUid.ToUpperInvariant())).Status);
        Assert.Equal(first.NodeUid, (await master.Registry.GetAsync(NodeId))!.Uid);
    }

    [Fact]
    public async Task ConcurrentRekeysWithOneToken_ReplaceThePinsExactlyOnce()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(master);
        JoinTokenIssue rekey = await RekeyTokenAsync(master);
        RekeyService service = master.App.Services.GetRequiredService<RekeyService>();
        List<ECDsa> keys = [.. Enumerable.Range(0, 16).Select(_ => PkiDefaults.CreateKey())];
        try
        {
            EnrollmentResult[] results = await Task.WhenAll(keys.Select(key => Task.Run(() => service.RekeyAsync(new RekeyRequest
            {
                Token = rekey.Token,
                NodeId = NodeId,
                NodeUid = first.NodeUid,
                CsrPem = CertificateSigningRequests.CreatePem(key, NodeId),
            }, IPAddress.Loopback))));

            EnrollmentResult winner = Assert.Single(results, r => r.Status == EnrollmentStatus.Enrolled);
            Assert.All(results.Where(r => r != winner), r => Assert.Equal(EnrollmentStatus.Refused, r.Status));
            using X509Certificate2 issued = X509Certificate2.CreateFromPem(winner.Response!.AgentCertPem);
            NodeRecord node = (await master.Registry.GetAsync(NodeId))!;
            Assert.Equal(Spki.Sha256(issued), node.CertSpkiSha256);
            Assert.Null(node.PendingCertSpkiSha256);
            Assert.Equal(first.NodeUid, winner.Response.NodeUid);
        }
        finally
        {
            keys.ForEach(k => k.Dispose());
        }
    }

    [Fact]
    public async Task ANodeRevokedAfterTheTokenWasConsumed_IsRefused_AndStaysRevokedWithoutPins()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(master);
        JoinTokenIssue rekey = await RekeyTokenAsync(master);
        master.Registry.AfterConsume = () => master.Registry.RevokeAsync(NodeId);
        using HttpClient client = master.CreateClient();

        (HttpStatusCode status, string body) = await PostRekeyAsync(client, master, rekey.Token, NodeId, first.NodeUid);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains(ErrorCodes.EnrollmentRefused, body, StringComparison.Ordinal);
        NodeRecord node = (await master.Registry.GetAsync(NodeId))!;
        Assert.True(node.Revoked);
        Assert.Null(node.CertSpkiSha256);
        Assert.Null(node.PendingCertSpkiSha256);
        Assert.NotNull((await master.Registry.ListJoinTokensAsync()).Single(t => t.Purpose == JoinTokenPurpose.Rekey).UsedAt);
    }

    // ---- listener hygiene ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RateLimit_IsOneBudgetForEnrollAndRekey_AndA429TouchesNoToken()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca, configure: o => o.RateLimitPermits = MirbadLLM.Web.AdminPasswordGuard.PermitLimit);
        using HttpClient client = master.CreateClient();

        List<HttpStatusCode> answers = [];
        for (int i = 0; i < 3; i++)
        {
            answers.Add((await PostEnrollAsync(client, master, "guess-" + i, NodeId)).Status);
        }

        for (int i = 3; i < 5; i++)
        {
            answers.Add((await PostRekeyAsync(client, master, "guess-" + i, NodeId, "00112233445566778899aabbccddeeff")).Status);
        }

        int lookups = master.Registry.TokenListCalls;
        (HttpStatusCode Status, string Body) limitedRekey = await PostRekeyAsync(client, master, "guess-5", NodeId, "00112233445566778899aabbccddeeff");
        (HttpStatusCode Status, string Body) limitedEnroll = await PostEnrollAsync(client, master, "guess-6", NodeId);

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Forbidden, a));
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedRekey.Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, limitedEnroll.Status);
        Assert.Equal(ErrorCodes.RateLimited, JsonSerializer.Deserialize<AgentErrorResponse>(limitedRekey.Body, AgentJson.Options)!.Error.Code);
        Assert.Equal(lookups, master.Registry.TokenListCalls);
    }

    [Fact]
    public async Task Rekey_OnlyOnTheTlsListener_AndOnlyAsPost()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        using HttpClient client = master.CreateClient();
        using HttpClient plain = new();
        using ECDsa key = PkiDefaults.CreateKey();

        using HttpResponseMessage overHttp = await plain.PostAsync(new Uri(master.Http, AgentRoutes.Rekey.Path), JsonContent.Create(new RekeyRequest
        {
            Token = "t", NodeId = NodeId, NodeUid = "00112233445566778899aabbccddeeff", CsrPem = CertificateSigningRequests.CreatePem(key, NodeId),
        }, options: AgentJson.Options));
        using HttpResponseMessage get = await client.GetAsync(new Uri(master.Https, AgentRoutes.Rekey.Path));
        using HttpResponseMessage other = await client.PostAsync(new Uri(master.Https, "/enroll/rekey/x"), new StringContent("{}"));

        Assert.Equal(HttpStatusCode.NotFound, overHttp.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(0, master.Registry.TokenListCalls);
        Assert.Equal(0, master.Registry.ConsumeCalls);
    }

    [Fact]
    public async Task MalformedRekeyBodies_AreInvalidRequest_AndTouchNoToken()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        using HttpClient client = master.CreateClient();
        using ECDsa key = PkiDefaults.CreateKey();
        string csr = CertificateSigningRequests.CreatePem(key, NodeId);

        string[] bodies =
        [
            "not json",
            "{\"token\":\"t\",\"nodeId\":\"Bad Id\",\"nodeUid\":\"00112233445566778899aabbccddeeff\",\"csrPem\":\"x\"}",
            JsonSerializer.Serialize(new RekeyRequest { Token = "t", NodeId = NodeId, NodeUid = "uid with spaces", CsrPem = csr }, AgentJson.Options),
            JsonSerializer.Serialize(new RekeyRequest { Token = "t", NodeId = NodeId, NodeUid = new string('a', 65), CsrPem = csr }, AgentJson.Options),
            JsonSerializer.Serialize(new RekeyRequest { Token = "t", NodeId = NodeId, NodeUid = "00112233445566778899aabbccddeeff", CsrPem = csr, Addresses = ["not an address!"] }, AgentJson.Options),
        ];

        foreach (string body in bodies)
        {
            using HttpResponseMessage response = await client.PostAsync(new Uri(master.Https, AgentRoutes.Rekey.Path), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(ErrorCodes.InvalidRequest, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Assert.Equal(0, master.Registry.TokenListCalls);
        Assert.Equal(0, master.Registry.ConsumeCalls);
    }

    // ---- the agent's own checks ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rekey_ToAListenerOfAnotherCa_NeverSendsTheToken()
    {
        using CertificateAuthority other = CertificateAuthority.Create();
        await using Master master = await Master.StartAsync(DataDir, other);
        await using Master real = await Master.StartAsync(Path.Combine(_root, "real"), _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(real);

        AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            Agent().RekeyAsync(Rekey(master, "rekey-token-value", uid: first.NodeUid)));

        Assert.Equal(EnrollmentFailure.ServerNotTrusted, ex.Failure);
        Assert.DoesNotContain("rekey-token-value", ex.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, master.Requests);
        Assert.Equal(first.CertificateSpkiSha256, Spki.Sha256(Credentials().ServerCertificate));
    }

    [Fact]
    public async Task Rekey_RefusesAnAnswerForAnotherUid_AndStoresNothing()
    {
        await using Master real = await Master.StartAsync(DataDir, _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(real);
        Dictionary<string, byte[]> stateBefore = Directory.EnumerateFiles(StateDir()).ToDictionary(f => f, File.ReadAllBytes);

        // A listener with the right CA that answers with a valid certificate for the new key but another uid.
        await using Master lying = await Master.StartAsync(Path.Combine(_root, "lying"), _ca, fakeRekey: async http =>
        {
            RekeyRequest request = (await http.Request.ReadFromJsonAsync<RekeyRequest>(AgentJson.Options))!;
            using IssuedCertificate issued = _ca.IssueAgentCertificate(request.CsrPem, request.NodeId, addresses: null);
            await http.Response.WriteAsJsonAsync(new EnrollResponse
            {
                CaCertPem = _ca.CertificatePem,
                AgentCertPem = issued.CertificatePem,
                NodeUid = "ffffffffffffffffffffffffffffffff",
            }, AgentJson.Options);
        });

        AgentEnrollmentException ex = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            Agent().RekeyAsync(Rekey(lying, "t-0123456789")));

        Assert.Equal(EnrollmentFailure.InvalidResponse, ex.Failure);
        Assert.Equal(1, lying.Requests);
        Assert.Equal(stateBefore.Keys.Order(), Directory.EnumerateFiles(StateDir()).Order());
        Assert.All(stateBefore, kv => Assert.Equal(kv.Value, File.ReadAllBytes(kv.Key)));
    }

    [Fact]
    public async Task Rekey_LocalChecks_RefuseBeforeAnythingIsSent()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        await using Master real = await Master.StartAsync(Path.Combine(_root, "real"), _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(real);

        // (1) Lost state and no --node-uid.
        AgentEnrollmentException noUid = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            Agent().RekeyAsync(Rekey(master, "t-0123456789", state: StateDir("empty"))));
        // (2) --node-uid disagrees with the state directory's uid.
        AgentEnrollmentException mismatch = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            Agent().RekeyAsync(Rekey(master, "t-0123456789", uid: "0123456789abcdef0123456789abcdef")));
        // (3) The state directory holds another node's identity.
        AgentEnrollmentException otherNode = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            Agent().RekeyAsync(Rekey(master, "t-0123456789", uid: first.NodeUid, nodeId: "pc-lab")));
        // (4) A malformed --node-uid.
        AgentEnrollmentException malformed = await Assert.ThrowsAsync<AgentEnrollmentException>(() =>
            Agent().RekeyAsync(Rekey(master, "t-0123456789", uid: "../../etc", state: StateDir("empty"))));

        Assert.All([noUid, mismatch, otherNode, malformed], e => Assert.Equal(EnrollmentFailure.InvalidArguments, e.Failure));
        Assert.Contains("--node-uid", noUid.Message, StringComparison.Ordinal);
        Assert.Equal(0, master.Requests);
        Assert.Equal(first.CertificateSpkiSha256, Spki.Sha256(Credentials().ServerCertificate));
    }

    [Fact]
    public void JoinCommand_ParsesRekey_AndRejectsMixedForms()
    {
        string[] common = ["--enroll-url", "https://m:8443", "--ca-hash", CaHash, "--node-id", "n1"];

        Assert.True(JoinCommand.IsRekey([.. common, "--rekey"]));
        Assert.False(JoinCommand.IsRekey(common));
        Assert.True(JoinCommand.TryParseRekey([.. common, "--rekey", "--node-uid", "abc-123"], "env-rekey-token", out RekeyParameters? parsed, out _));
        Assert.Equal("env-rekey-token", parsed!.Token);
        Assert.Equal("abc-123", parsed.NodeUid);
        Assert.Equal(JoinCommand.DefaultStateDirectory, parsed.StateDirectory);
        Assert.DoesNotContain("env-rekey-token", parsed.ToString(), StringComparison.Ordinal);
        Assert.True(JoinCommand.TryParseRekey([.. common, "--rekey", "--token", "t"], null, out RekeyParameters? fromState, out _));
        Assert.Null(fromState!.NodeUid);

        Assert.False(JoinCommand.TryParseRekey([.. common, "--rekey", "--force", "--token", "t"], null, out _, out string? force));
        Assert.Contains("--force", force, StringComparison.Ordinal);
        Assert.False(JoinCommand.TryParseRekey([.. common, "--token", "t"], null, out _, out _));
        Assert.False(JoinCommand.TryParse([.. common, "--token", "t", "--node-uid", "abc"], null, out _, out string? uidWithoutRekey));
        Assert.Contains("--rekey", uidWithoutRekey, StringComparison.Ordinal);
        Assert.False(JoinCommand.TryParse([.. common, "--token", "t", "--rekey"], null, out _, out _));
    }

    [Fact]
    public async Task JoinCommand_RekeyUsageErrors_AreExitUsage_AndNeverPrintTheToken()
    {
        using StringWriter output = new();
        using StringWriter error = new();

        int exit = await JoinCommand.RunAsync(
            ["--rekey", "--force", "--enroll-url", "https://127.0.0.1:1", "--token", "secret-rekey-value", "--ca-hash", CaHash, "--node-id", NodeId],
            output, error);

        Assert.Equal(JoinCommand.ExitUsage, exit);
        Assert.DoesNotContain("secret-rekey-value", output.ToString() + error.ToString(), StringComparison.Ordinal);
    }

    // ---- audit -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AuditLines_RecordRekeysAndRefusals_WithoutSecrets()
    {
        await using Master master = await Master.StartAsync(DataDir, _ca);
        EnrollmentOutcome first = await EnrollApprovedAsync(master);
        JoinTokenIssue wrongUid = await RekeyTokenAsync(master);
        JoinTokenIssue good = await RekeyTokenAsync(master);
        using HttpClient client = master.CreateClient();
        master.Audit.Clear();

        await PostRekeyAsync(client, master, wrongUid.Token, NodeId, "0123456789abcdef0123456789abcdef");
        await PostEnrollAsync(client, master, good.Token, NodeId); // a rekey token on /enroll
        await PostRekeyAsync(client, master, "x", "Not A Node Id", first.NodeUid);
        EnrollmentOutcome repaired = await Agent().RekeyAsync(Rekey(master, good.Token));

        AuditRecord[] lines = [.. master.Audit.Lines];
        Assert.Equal(4, lines.Length);
        Assert.All(lines, l =>
        {
            Assert.Equal(AuditActions.NodeEnroll, l.Action);
            Assert.Equal(AuditActors.ApiCaller("127.0.0.1"), l.Actor);
        });
        Assert.Equal(
            [
                (AuditResults.Refused, ErrorCodes.EnrollmentRefused, (string?)NodeId),
                (AuditResults.Refused, ErrorCodes.EnrollmentRefused, NodeId),
                (AuditResults.Refused, ErrorCodes.InvalidRequest, null),
                (AuditResults.Succeeded, null, NodeId),
            ],
            lines.Select(l => (l.Result, l.ErrorCode, l.NodeId)));
        Assert.StartsWith("rekey: node uid mismatch", lines[0].Detail, StringComparison.Ordinal);
        Assert.StartsWith("enroll: rekey token", lines[1].Detail, StringComparison.Ordinal);
        Assert.StartsWith("rekey: ", lines[3].Detail, StringComparison.Ordinal);

        string all = JsonSerializer.Serialize(lines, MasterStateJson.Options);
        Assert.DoesNotContain(good.Token, all, StringComparison.Ordinal);
        Assert.DoesNotContain(wrongUid.Token, all, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", all, StringComparison.Ordinal);
        Assert.NotEqual(first.CertificateSpkiSha256, repaired.CertificateSpkiSha256);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------------

    private static string TamperedCsr(string nodeId)
    {
        using ECDsa key = PkiDefaults.CreateKey();
        string pem = CertificateSigningRequests.CreatePem(key, nodeId);
        byte[] der = Convert.FromBase64String(pem[PemEncoding.Find(pem).Base64Data]);
        der[^1] ^= 0x01; // the last byte of the ECDSA signature's s value
        return PemEncoding.WriteString("CERTIFICATE REQUEST", der);
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostEnrollAsync(HttpClient client, Master master, string token, string nodeId)
    {
        using ECDsa key = PkiDefaults.CreateKey();
        using HttpResponseMessage response = await client.PostAsync(new Uri(master.Https, AgentRoutes.Enroll.Path), JsonContent.Create(new EnrollRequest
        {
            Token = token,
            NodeId = nodeId,
            CsrPem = CertificateSigningRequests.CreatePem(key, nodeId),
        }, options: AgentJson.Options));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostRekeyAsync(HttpClient client, Master master, string token, string nodeId, string uid, string? csrPem = null)
    {
        using ECDsa key = PkiDefaults.CreateKey();
        using HttpResponseMessage response = await client.PostAsync(new Uri(master.Https, AgentRoutes.Rekey.Path), JsonContent.Create(new RekeyRequest
        {
            Token = token,
            NodeId = nodeId,
            NodeUid = uid,
            CsrPem = csrPem ?? CertificateSigningRequests.CreatePem(key, Protocol.IsValidNodeId(nodeId) ? nodeId : "x"),
        }, options: AgentJson.Options));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Records audit lines in memory (MN-116's store is composed by MN-130).</summary>
    private sealed class RecordingAudit : IAuditLog
    {
        private readonly ConcurrentQueue<AuditRecord> _lines = new();

        public IReadOnlyList<AuditRecord> Lines => [.. _lines];

        public void Clear() => _lines.Clear();

        public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            _lines.Enqueue(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([.. _lines.Reverse()]);
    }

    /// <summary>Counts token look-ups and consumptions, and can act right after a consumption (race injection).</summary>
    private sealed class RecordingRegistry(JsonNodeRegistry inner) : INodeRegistry
    {
        private int _consumeCalls;
        private int _tokenListCalls;

        public int ConsumeCalls => Volatile.Read(ref _consumeCalls);

        public int TokenListCalls => Volatile.Read(ref _tokenListCalls);

        public Func<Task>? AfterConsume { get; set; }

        public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);

        public Task UpsertAsync(NodeRecord node, CancellationToken ct = default) => inner.UpsertAsync(node, ct);

        public Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default) => inner.UpdateAsync(id, update, ct);

        public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => inner.RevokeAsync(id, ct);

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);

        public Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default) =>
            inner.CreateJoinTokenAsync(nodeId, lifetime, purpose, ct);

        public async Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _consumeCalls);
            JoinTokenConsumption result = await inner.TryConsumeJoinTokenAsync(token, nodeId, ct);
            if (result.Outcome == JoinTokenConsumeOutcome.Consumed && AfterConsume is { } after)
            {
                await after();
            }

            return result;
        }

        public Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _tokenListCalls);
            return inner.ListJoinTokensAsync(ct);
        }
    }

    /// <summary>The master's enroll listener on loopback (TestPorts): real TLS endpoint + a plain-HTTP endpoint.</summary>
    private sealed class Master : IAsyncDisposable
    {
        private readonly JsonNodeRegistry _inner;
        private int _requests;

        private Master(JsonNodeRegistry inner, WebApplication app)
        {
            _inner = inner;
            Registry = new RecordingRegistry(inner);
            App = app;
        }

        public RecordingRegistry Registry { get; }

        public RecordingAudit Audit { get; } = new();

        public WebApplication App { get; private set; }

        public Uri Https { get; private set; } = null!;

        public Uri Http { get; private set; } = null!;

        /// <summary>HTTP requests that reached the application (i.e. after a completed TLS handshake).</summary>
        public int Requests => Volatile.Read(ref _requests);

        private string CaPin { get; set; } = "";

        public static async Task<Master> StartAsync(
            string dataDir,
            CertificateAuthority ca,
            Action<EnrollmentOptions>? configure = null,
            RequestDelegate? fakeRekey = null,
            TimeProvider? registryTime = null)
        {
            JsonNodeRegistry inner = new(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), registryTime ?? TimeProvider.System);
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls();
            Master master = new(inner, null!) { CaPin = ca.SpkiSha256 };
            builder.Services.AddSingleton(ca);
            builder.Services.AddSingleton<INodeRegistry>(master.Registry);
            builder.Services.AddSingleton<IAuditLog>(master.Audit);
            builder.Services.AddMirbadLLMEnrollment(builder.Configuration);
            builder.Services.PostConfigure<EnrollmentOptions>(o =>
            {
                o.BootstrapEnabled = false;
                o.RateLimitPermits = 100;
                configure?.Invoke(o);
            });
            int tlsPort = TestPorts.Reserve();
            int httpPort = TestPorts.Reserve();
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.Listen(IPAddress.Loopback, tlsPort, listen => kestrel.ApplicationServices.GetRequiredService<EnrollListenerTls>().ConfigureEndpoint(listen));
                kestrel.Listen(IPAddress.Loopback, httpPort);
            });

            WebApplication app = builder.Build();
            master.App = app;
            app.Use(async (context, next) =>
            {
                Interlocked.Increment(ref master._requests);
                await next(context);
            });
            app.UseEnrollListenerGate();
            if (fakeRekey is null)
            {
                app.MapEnrollEndpoint();
            }
            else
            {
                app.MapPost(AgentRoutes.Rekey.Path, fakeRekey);
            }

            await app.StartAsync();
            master.Https = new Uri($"https://127.0.0.1:{tlsPort}/");
            master.Http = new Uri($"http://127.0.0.1:{httpPort}/");
            return master;
        }

        /// <summary>A client that trusts the listener the way a joining agent does (CA hash).</summary>
        public HttpClient CreateClient() => new(new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = PkiTls.CreateClientOptions(null, new PeerValidatorSource(PeerValidator.ForEnrollListener(CaPin)), "localhost"),
        });

        public async ValueTask DisposeAsync()
        {
            await App.StopAsync();
            await App.DisposeAsync();
            _inner.Dispose();
        }
    }
}
