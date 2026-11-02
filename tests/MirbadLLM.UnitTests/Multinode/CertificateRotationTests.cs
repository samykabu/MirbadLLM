using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Operations;
using MirbadLLM.Agent.Pki;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Contract;
using AgentReply = MirbadLLM.UnitTests.Multinode.Contract.AgentReply;
using ManualClock = MirbadLLM.UnitTests.Pki.ManualTimeProvider;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-036: two-phase agent-certificate rotation (design §10.2) end to end, with the real agent PKI routes hosted in
/// process over mTLS and the master's <see cref="CertificateRotationService"/> driving them through the real
/// <see cref="AgentClientFactory"/>:
/// <list type="bullet">
/// <item>the PKI cases of the shared contract suite, against the real agent (they ran only against FakeAgent before);</item>
/// <item>fault injection at every stage: a master crash after each persisted stage and after each agent call, an agent
/// crash between each of its writes (then a restart), and lost responses of every step; each ends with both sides on a
/// working certificate;</item>
/// <item>abort only before commit (409 <c>already_committed</c> after), lost commit resolved by query/retry, the old key
/// deleted only after commit, both handlers replaced at commit, renewal at 60 days and master re-issue at half life
/// driven by <see cref="TimeProvider"/>, the Degraded reason from 14 days before expiry.</item>
/// </list>
/// </summary>
public sealed class CertificateRotationTests
{
    private const string NodeId = "dgx";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> MasterCrashPoints =>
    [
        "persisted:Requested", "sent:start", "persisted:CsrReceived", "persisted:CertIssued", "sent:cert",
        "persisted:CertInstalled", "persisted:Committing", "sent:commit", "persisted:Committed",
    ];

    public static TheoryData<string> AgentCrashPoints =>
    [
        "start:key-written", "cert:written", "commit:persisted", "promote:key-written", "commit:promoted", "commit:pending-deleted",
    ];

    // =====================================================================================================
    // The PKI cases of the shared contract suite, against the real agent
    // =====================================================================================================

    [Fact]
    public async Task RealAgent_ARotationStartIsRepeatableAndReturnsTheSameCsr()
    {
        await using var env = await RotationEnv.StartAsync();
        using var agent = env.ContractClient();

        var first = await AcceptedAsync(agent, AgentRoutes.PkiRotationStart, Requests.Start("rot-1"));
        Assert.Equal(OperationKinds.PkiRotationStart, first.Kind);
        Assert.Equal("rot-1", first.Target);
        var csr = first.ReadResult<PkiRotationCsr>();
        Assert.NotNull(csr);
        Assert.Equal("rot-1", csr.RotationId);
        Assert.Contains("CERTIFICATE REQUEST", csr.CsrPem, StringComparison.Ordinal);

        var second = await AcceptedAsync(agent, AgentRoutes.PkiRotationStart, Requests.Start("rot-1"));
        Assert.Equal(csr.CsrPem, second.ReadResult<PkiRotationCsr>()?.CsrPem);

        var status = (await agent.SendAsync(AgentRoutes.PkiRotationGet, ["rot-1"], ct: Ct)).Read<PkiRotationStatus>();
        Assert.Equal(PkiRotationStage.Pending, status.Stage);
        Assert.Equal(env.InitialPin, status.CurrentSpkiSha256);
        Assert.False(string.IsNullOrEmpty(status.PendingSpkiSha256));

        var other = await agent.SendAsync(AgentRoutes.PkiRotationStart, [], Requests.Start("rot-2"), ct: Ct);
        var error = AssertError(other, 409, ErrorCodes.InvalidState);
        Assert.Equal("rot-1", error.Details?.GetProperty("activeRotationId").GetString());

        // A repeated operation id answers the stored status without running again.
        var start = Requests.Start("rot-1");
        var once = await AcceptedAsync(agent, AgentRoutes.PkiRotationStart, start);
        var replay = await AcceptedAsync(agent, AgentRoutes.PkiRotationStart, start);
        Assert.Equal(once.StartedAt, replay.StartedAt);
    }

    [Fact]
    public async Task RealAgent_ARotationPresentsTheNewCertificateOnNewConnectionsAndCommits_DeletingTheOldKeyOnlyAtCommit()
    {
        await using var env = await RotationEnv.StartAsync();
        using var agent = env.ContractClient();
        string oldKey = env.ReadStateFile(FileAgentCredentials.KeyFileName);

        var csr = (await AcceptedAsync(agent, AgentRoutes.PkiRotationStart, Requests.Start("rot-1"))).ReadResult<PkiRotationCsr>()!;
        using var issued = env.Ca.IssueAgentCertificate(csr.CsrPem, NodeId, ["127.0.0.1"]);

        var commitTooEarly = await agent.SendAsync(AgentRoutes.PkiRotationCommit, ["rot-1"], Requests.Commit("rot-1"), ct: Ct);
        AssertError(commitTooEarly, 409, ErrorCodes.InvalidState);

        var installed = await AcceptedAsync(agent, AgentRoutes.PkiRotationCert, Requests.Cert("rot-1", issued.CertificatePem), "rot-1");
        var installedStatus = installed.ReadResult<PkiRotationStatus>();
        Assert.Equal(PkiRotationStage.CertInstalled, installedStatus?.Stage);
        Assert.Equal(issued.SpkiSha256, installedStatus?.PendingSpkiSha256);
        Assert.Equal(oldKey, env.ReadStateFile(FileAgentCredentials.KeyFileName));
        Assert.True(env.StateFileExists(AgentRotationStore.PendingKeyFileName));

        // The pooled connection keeps the old certificate; a fresh one negotiates the new one.
        Assert.Equal(200, (await agent.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        using (var oldOnly = env.ContractClient(env.InitialPin))
        {
            await AssertRefusedAsync(oldOnly);
        }

        // The commit must come over a connection that negotiated the new certificate: the pooled one is refused.
        AssertError(await agent.SendAsync(AgentRoutes.PkiRotationCommit, ["rot-1"], Requests.Commit("rot-1"), ct: Ct), 409, ErrorCodes.InvalidState);
        Assert.Equal(oldKey, env.ReadStateFile(FileAgentCredentials.KeyFileName));

        using var fresh = env.ContractClient(issued.SpkiSha256);
        Assert.Equal(200, (await fresh.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        var commit = await fresh.SendAsync(AgentRoutes.PkiRotationCommit, ["rot-1"], Requests.Commit("rot-1"), ct: Ct);
        Assert.Equal(200, commit.Status);
        var committed = commit.Read<OperationStatus>().ReadResult<PkiRotationStatus>();
        Assert.Equal(PkiRotationStage.Committed, committed?.Stage);
        Assert.Equal(issued.SpkiSha256, committed?.CurrentSpkiSha256);
        Assert.Null(committed?.PendingSpkiSha256);

        // The old key is gone, replaced by the pending one; the pending files are removed.
        string newKey = env.ReadStateFile(FileAgentCredentials.KeyFileName);
        Assert.NotEqual(oldKey, newKey);
        using (ECDsa promoted = ECDsa.Create())
        {
            promoted.ImportFromPem(newKey);
            Assert.Equal(issued.SpkiSha256, Spki.Sha256(promoted));
        }

        Assert.False(env.StateFileExists(AgentRotationStore.PendingKeyFileName));
        Assert.False(env.StateFileExists(AgentRotationStore.PendingCertificateFileName));

        var again = await fresh.SendAsync(AgentRoutes.PkiRotationCommit, ["rot-1"], Requests.Commit("rot-1"), ct: Ct);
        Assert.Equal(200, again.Status);
        var abort = await fresh.SendAsync(AgentRoutes.PkiRotationAbort, ["rot-1"], Requests.Abort("rot-1"), ct: Ct);
        AssertError(abort, 409, ErrorCodes.AlreadyCommitted);
        Assert.Equal(PkiRotationStage.Committed, (await fresh.SendAsync(AgentRoutes.PkiRotationGet, ["rot-1"], ct: Ct)).Read<PkiRotationStatus>().Stage);

        // A restart keeps the committed identity.
        await env.RestartAgentAsync();
        using var afterRestart = env.ContractClient(issued.SpkiSha256);
        Assert.Equal(200, (await afterRestart.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        Assert.DoesNotContain(env.Agent.Logs.Messages, m => m.Contains("PRIVATE KEY", StringComparison.Ordinal));
        Assert.DoesNotContain("PRIVATE KEY", env.ReadStateFile(OperationRetention.LogRelativePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealAgent_AnAbortedRotationKeepsTheOldCertificate()
    {
        await using var env = await RotationEnv.StartAsync();
        using var agent = env.ContractClient();
        var csr = (await AcceptedAsync(agent, AgentRoutes.PkiRotationStart, Requests.Start("rot-1"))).ReadResult<PkiRotationCsr>()!;
        using var issued = env.Ca.IssueAgentCertificate(csr.CsrPem, NodeId, ["127.0.0.1"]);
        await AcceptedAsync(agent, AgentRoutes.PkiRotationCert, Requests.Cert("rot-1", issued.CertificatePem), "rot-1");

        var aborted = await AcceptedAsync(agent, AgentRoutes.PkiRotationAbort, Requests.Abort("rot-1"), "rot-1");

        Assert.Equal(PkiRotationStage.Aborted, aborted.ReadResult<PkiRotationStatus>()?.Stage);
        using (var oldOnly = env.ContractClient(env.InitialPin))
        {
            Assert.Equal(200, (await oldOnly.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        }

        using var newOnly = env.ContractClient(issued.SpkiSha256);
        await AssertRefusedAsync(newOnly);
        AssertError(await agent.SendAsync(AgentRoutes.PkiRotationCommit, ["rot-1"], Requests.Commit("rot-1"), ct: Ct), 409, ErrorCodes.InvalidState);
        Assert.False(env.StateFileExists(AgentRotationStore.PendingKeyFileName));
        Assert.Equal(200, (await agent.SendAsync(AgentRoutes.PkiRotationAbort, ["rot-1"], Requests.Abort("rot-1"), ct: Ct)).Status);

        // A new rotation can start after an abort.
        Assert.NotNull((await AcceptedAsync(agent, AgentRoutes.PkiRotationStart, Requests.Start("rot-2"))).ReadResult<PkiRotationCsr>());
    }

    [Fact]
    public async Task RealAgent_ACertificateForAnotherKeyOrRotationIsRefused()
    {
        await using var env = await RotationEnv.StartAsync();
        using var agent = env.ContractClient();
        await AcceptedAsync(agent, AgentRoutes.PkiRotationStart, Requests.Start("rot-1"));
        using var otherKey = PkiDefaults.CreateKey();
        using var foreign = env.Ca.IssueAgentCertificate(CertificateSigningRequests.CreatePem(otherKey, NodeId), NodeId, ["127.0.0.1"]);

        AssertError(await agent.SendAsync(AgentRoutes.PkiRotationCert, ["rot-1"], Requests.Cert("rot-1", foreign.CertificatePem), ct: Ct), 400, ErrorCodes.InvalidRequest);
        AssertError(await agent.SendAsync(AgentRoutes.PkiRotationCert, ["rot-1"], Requests.Cert("rot-other", foreign.CertificatePem), ct: Ct), 400, ErrorCodes.InvalidRequest);
        AssertError(await agent.SendAsync(AgentRoutes.PkiRotationCert, ["rot-9"], Requests.Cert("rot-9", foreign.CertificatePem), ct: Ct), 404, ErrorCodes.NotFound);
        AssertError(await agent.SendAsync(AgentRoutes.PkiRotationGet, ["rot-9"], ct: Ct), 404, ErrorCodes.NotFound);

        // The right key but another node's identity, or another CA, is refused too.
        var csr = (await AcceptedAsync(agent, AgentRoutes.PkiRotationStart, Requests.Start("rot-1"))).ReadResult<PkiRotationCsr>()!;
        using var otherNode = env.Ca.IssueAgentCertificate(csr.CsrPem, "other-node", ["127.0.0.1"]);
        AssertError(await agent.SendAsync(AgentRoutes.PkiRotationCert, ["rot-1"], Requests.Cert("rot-1", otherNode.CertificatePem), ct: Ct), 400, ErrorCodes.InvalidRequest);
        using var rogue = CertificateAuthority.Create();
        using var rogueIssued = rogue.IssueAgentCertificate(csr.CsrPem, NodeId, ["127.0.0.1"]);
        AssertError(await agent.SendAsync(AgentRoutes.PkiRotationCert, ["rot-1"], Requests.Cert("rot-1", rogueIssued.CertificatePem), ct: Ct), 400, ErrorCodes.InvalidRequest);
        AssertError(await agent.SendAsync(AgentRoutes.PkiRotationStart, [], Requests.Start("bad id!"), ct: Ct), 400, ErrorCodes.InvalidRequest);

        // Refusals record nothing (D41): the operation log holds only the two accepted starts.
        var log = env.Agent.Services.GetRequiredService<IOperationLog>();
        Assert.Empty(log.ListActive());
        Assert.Equal(2, log.ListRecent(100).Count);
    }

    [Fact]
    public async Task RealAgent_TrustAnchorsCanBeReplacedButMustKeepTheCaller()
    {
        await using var env = await RotationEnv.StartAsync();
        using var agent = env.ContractClient();
        var trust = (await agent.SendAsync(AgentRoutes.PkiTrustGet, [], ct: Ct)).Read<PkiTrustResponse>();
        Assert.Equal(Protocol.IssuedMasterSpiffeId, trust.MasterSan);
        Assert.Contains(trust.Anchors, a => a.SpkiSha256 == env.Ca.SpkiSha256);

        using var second = CertificateAuthority.Create();
        var both = await AcceptedAsync(agent, AgentRoutes.PkiTrustSet, Requests.Trust(env.Ca.CertificatePem, second.CertificatePem));
        Assert.Equal(OperationKinds.PkiTrustSet, both.Kind);
        Assert.Equal(2, both.ReadResult<PkiTrustResponse>()?.Anchors.Count);

        // A master of the second CA is accepted on the next handshake.
        using (var secondMaster = second.IssueMasterClientCertificate())
        using (var secondTls = secondMaster.CreateTlsCertificate())
        using (var viaSecond = new AgentContractClient(new AgentContractTarget(env.Agent.BaseUri, NodeId, env.Ca, secondTls, env.InitialPin)))
        {
            Assert.Equal(200, (await viaSecond.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        }

        AssertError(await agent.SendAsync(AgentRoutes.PkiTrustSet, [], Requests.Trust(second.CertificatePem), ct: Ct), 409, ErrorCodes.InvalidState);
        AssertError(await agent.SendAsync(AgentRoutes.PkiTrustSet, [], Requests.Trust("not a certificate"), ct: Ct), 400, ErrorCodes.InvalidRequest);

        var back = await AcceptedAsync(agent, AgentRoutes.PkiTrustSet, Requests.Trust(env.Ca.CertificatePem));
        Assert.Single(back.ReadResult<PkiTrustResponse>()!.Anchors);
        Assert.Equal(200, (await agent.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        Assert.Contains("BEGIN CERTIFICATE", env.ReadStateFile(FileAgentCredentials.CaFileName), StringComparison.Ordinal);

        // The trust survives a restart (ca.crt was replaced).
        await env.RestartAgentAsync();
        using var afterRestart = env.ContractClient();
        Assert.Single((await afterRestart.SendAsync(AgentRoutes.PkiTrustGet, [], ct: Ct)).Read<PkiTrustResponse>().Anchors);
    }

    // =====================================================================================================
    // Master-driven rotation
    // =====================================================================================================

    [Fact]
    public async Task TheMasterRotatesEndToEnd_PromotingThePinAndReplacingBothHandlers()
    {
        await using var env = await RotationEnv.StartAsync();
        long before = env.Factory.GetGeneration(NodeId).Id;
        long? atInstall = null;
        env.Service.FaultInjection = point =>
        {
            if (point == "persisted:CertInstalled")
            {
                atInstall = env.Factory.GetGeneration(NodeId).Id;
            }

            return Task.CompletedTask;
        };

        var rotation = await env.Service.RotateAsync(NodeId, Ct);

        Assert.Equal(CertificateRotationStage.Committed, rotation.Stage);
        Assert.Null(rotation.Error);
        NodeRecord node = (await env.Registry.GetAsync(NodeId, Ct))!;
        Assert.Equal(rotation.NewSpkiSha256, node.CertSpkiSha256);
        Assert.NotEqual(env.InitialPin, node.CertSpkiSha256);
        Assert.Null(node.PendingCertSpkiSha256);
        Assert.Equal(rotation.NewCertNotAfter, node.CertNotAfter);
        Assert.True(node.CertNotAfter > DateTimeOffset.UtcNow.AddDays(89));

        // Adding the pending pin kept the handlers; the commit replaced them.
        Assert.Equal(before, atInstall);
        Assert.NotEqual(before, env.Factory.GetGeneration(NodeId).Id);
        Assert.Equal(NodeId, (await env.Factory.GetClient(NodeId).GetInfoAsync(Ct)).NodeId);

        await env.AssertAgentIdentityAsync(node.CertSpkiSha256!);
        var agentView = await env.Factory.GetClient(NodeId).GetRotationAsync(rotation.RotationId, Ct);
        Assert.Equal(PkiRotationStage.Committed, agentView?.Stage);

        // A second rotation replaces the certificate again.
        var next = await env.Service.RotateAsync(NodeId, Ct);
        Assert.NotEqual(rotation.RotationId, next.RotationId);
        await env.AssertAgentIdentityAsync((await env.Registry.GetAsync(NodeId, Ct))!.CertSpkiSha256!);
    }

    [Theory]
    [MemberData(nameof(MasterCrashPoints))]
    public async Task AMasterCrashAtAnyStageResumesFromTheStoredStage(string point)
    {
        await using var env = await RotationEnv.StartAsync();
        env.Service.FaultInjection = p => p == point ? throw new SimulatedCrashException(p) : Task.CompletedTask;

        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.RotateAsync(NodeId, Ct));
        string stored = (await env.Registry.GetAsync(NodeId, Ct))!.Rotation!.Stage.ToString();
        if (point.StartsWith("persisted:", StringComparison.Ordinal))
        {
            Assert.Equal(point["persisted:".Length..], stored);
        }

        await env.RestartMasterAsync();
        var rotation = await env.Service.RotateAsync(NodeId, Ct);

        Assert.Equal(CertificateRotationStage.Committed, rotation.Stage);
        NodeRecord node = (await env.Registry.GetAsync(NodeId, Ct))!;
        Assert.Equal(rotation.NewSpkiSha256, node.CertSpkiSha256);
        Assert.Null(node.PendingCertSpkiSha256);
        await env.AssertAgentIdentityAsync(node.CertSpkiSha256!);
    }

    [Theory]
    [MemberData(nameof(AgentCrashPoints))]
    public async Task AnAgentCrashAtAnyStepRecoversAtRestartAndTheRotationFinishes(string point)
    {
        await using var env = await RotationEnv.StartAsync();
        env.Agent.Credentials.Store.FaultInjection = p =>
        {
            if (p == point)
            {
                throw new SimulatedCrashException(p);
            }
        };

        await Assert.ThrowsAsync<CertificateRotationException>(() => env.Service.RotateAsync(NodeId, Ct));
        await env.RestartAgentAsync();

        // The step that was cut off is reconciled into a terminal record at startup.
        var handler = env.Agent.Services.GetRequiredService<PkiRecoveryHandler>();
        foreach (var operation in env.Agent.Services.GetRequiredService<IOperationLog>().ListRecoverable())
        {
            await handler.RecoverAsync(operation, Ct);
        }

        Assert.Empty(env.Agent.Services.GetRequiredService<IOperationLog>().ListActive());

        var rotation = await env.Service.RotateAsync(NodeId, Ct);

        Assert.Equal(CertificateRotationStage.Committed, rotation.Stage);
        NodeRecord node = (await env.Registry.GetAsync(NodeId, Ct))!;
        await env.AssertAgentIdentityAsync(node.CertSpkiSha256!);
        Assert.False(env.StateFileExists(AgentRotationStore.PendingKeyFileName));
        using ECDsa key = ECDsa.Create();
        key.ImportFromPem(env.ReadStateFile(FileAgentCredentials.KeyFileName));
        Assert.Equal(node.CertSpkiSha256, Spki.Sha256(key));
    }

    [Theory]
    [InlineData("/pki/rotations", 1)]
    [InlineData("/pki/rotations", 3)]
    [InlineData("/cert", 1)]
    [InlineData("/cert", 3)]
    [InlineData("/commit", 1)]
    [InlineData("/commit", 3)]
    public async Task LostResponsesAreResolvedByRetryOrQuery(string pathSuffix, int lost)
    {
        await using var env = await RotationEnv.StartAsync();
        env.Agent.DropResponses(pathSuffix, lost);

        CertificateRotationRecord? rotation = null;
        for (int attempt = 0; attempt < 3 && rotation is null; attempt++)
        {
            try
            {
                rotation = await env.Service.RotateAsync(NodeId, Ct);
            }
            catch (CertificateRotationException)
            {
                // The step's answer was lost after the client's own retries: the next run resumes.
            }
        }

        Assert.Equal(0, env.Agent.PendingDrops);
        Assert.Equal(CertificateRotationStage.Committed, rotation?.Stage);
        await env.AssertAgentIdentityAsync((await env.Registry.GetAsync(NodeId, Ct))!.CertSpkiSha256!);
    }

    [Fact]
    public async Task ALostCommitAnswerIsResolvedInTheSameRunByReadingTheRotation()
    {
        await using var env = await RotationEnv.StartAsync();
        env.Agent.DropResponses("/commit", 3);

        var rotation = await env.Service.RotateAsync(NodeId, Ct);

        Assert.Equal(CertificateRotationStage.Committed, rotation.Stage);
        await env.AssertAgentIdentityAsync(rotation.NewSpkiSha256!);
    }

    [Fact]
    public async Task AbortBeforeCommitReturnsBothSidesToTheOldMaterial()
    {
        await using var env = await RotationEnv.StartAsync();
        env.Service.FaultInjection = p => p == "persisted:CertInstalled" ? throw new SimulatedCrashException(p) : Task.CompletedTask;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.RotateAsync(NodeId, Ct));
        env.Service.FaultInjection = null;
        Assert.NotNull((await env.Registry.GetAsync(NodeId, Ct))!.PendingCertSpkiSha256);

        var aborted = await env.Service.AbortAsync(NodeId, Ct);

        Assert.Equal(CertificateRotationStage.Aborted, aborted?.Stage);
        NodeRecord node = (await env.Registry.GetAsync(NodeId, Ct))!;
        Assert.Equal(env.InitialPin, node.CertSpkiSha256);
        Assert.Null(node.PendingCertSpkiSha256);
        await env.AssertAgentIdentityAsync(env.InitialPin);
        Assert.Equal(PkiRotationStage.Aborted, (await env.Factory.GetClient(NodeId).GetRotationAsync(aborted!.RotationId, Ct))?.Stage);
        Assert.False(env.StateFileExists(AgentRotationStore.PendingKeyFileName));

        // A later rotation starts afresh and commits.
        var next = await env.Service.RotateAsync(NodeId, Ct);
        Assert.Equal(CertificateRotationStage.Committed, next.Stage);
        Assert.NotEqual(aborted.RotationId, next.RotationId);
    }

    [Fact]
    public async Task AbortIsRefusedOnceTheMasterPersistedCommitting()
    {
        await using var env = await RotationEnv.StartAsync();
        env.Service.FaultInjection = p => p == "persisted:Committing" ? throw new SimulatedCrashException(p) : Task.CompletedTask;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.RotateAsync(NodeId, Ct));
        env.Service.FaultInjection = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Service.AbortAsync(NodeId, Ct));

        var rotation = await env.Service.RotateAsync(NodeId, Ct);
        Assert.Equal(CertificateRotationStage.Committed, rotation.Stage);
        await env.AssertAgentIdentityAsync(rotation.NewSpkiSha256!);
    }

    [Fact]
    public async Task AnAgentRestartWhileTheCertificateIsInstalledKeepsPresentingIt()
    {
        await using var env = await RotationEnv.StartAsync();
        env.Service.FaultInjection = p => p == "persisted:CertInstalled" ? throw new SimulatedCrashException(p) : Task.CompletedTask;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.RotateAsync(NodeId, Ct));
        string pending = (await env.Registry.GetAsync(NodeId, Ct))!.PendingCertSpkiSha256!;

        await env.RestartAgentAsync();

        using (var fresh = env.ContractClient(pending))
        {
            Assert.Equal(200, (await fresh.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        }

        await env.RestartMasterAsync();
        var rotation = await env.Service.RotateAsync(NodeId, Ct);
        Assert.Equal(CertificateRotationStage.Committed, rotation.Stage);
        await env.AssertAgentIdentityAsync(pending);
    }

    [Fact]
    public async Task AnInstalledCertificateTheAgentDroppedIsInstalledAgain()
    {
        await using var env = await RotationEnv.StartAsync();
        env.Service.FaultInjection = p => p == "persisted:CertInstalled" ? throw new SimulatedCrashException(p) : Task.CompletedTask;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.RotateAsync(NodeId, Ct));

        // The pending certificate file is lost: at restart the agent goes back to Pending (same key, same CSR).
        await env.Agent.DisposeAsync();
        File.Delete(Path.Combine(env.StateDir, AgentRotationStore.PendingCertificateFileName));
        await env.RestartAgentAsync(alreadyStopped: true);
        await env.RestartMasterAsync();

        var rotation = await env.Service.RotateAsync(NodeId, Ct);

        Assert.Equal(CertificateRotationStage.Committed, rotation.Stage);
        await env.AssertAgentIdentityAsync(rotation.NewSpkiSha256!);
    }

    [Fact]
    public async Task APendingKeyTheAgentCannotUseIsAbortedOnBothSides_AndTheNextRotationCommits()
    {
        await using var env = await RotationEnv.StartAsync();
        env.Service.FaultInjection = p => p == "persisted:CertIssued" ? throw new SimulatedCrashException(p) : Task.CompletedTask;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.RotateAsync(NodeId, Ct));
        env.Service.FaultInjection = null;
        string first = (await env.Registry.GetAsync(NodeId, Ct))!.Rotation!.RotationId;

        // The pending key became unreadable while the agent runs: it refuses the certificate (409 invalid_state) while
        // the rotation stays Pending. W3 review MN-094: the master aborts it instead of failing at every pass.
        File.WriteAllText(Path.Combine(env.StateDir, AgentRotationStore.PendingKeyFileName), "not a key");
        await env.RestartMasterAsync();

        var aborted = await env.Service.RotateAsync(NodeId, Ct);

        Assert.Equal(first, aborted.RotationId);
        Assert.Equal(CertificateRotationStage.Aborted, aborted.Stage);
        Assert.Equal(PkiRotationStage.Aborted, (await env.Factory.GetClient(NodeId).GetRotationAsync(first, Ct))?.Stage);
        Assert.Null((await env.Registry.GetAsync(NodeId, Ct))!.PendingCertSpkiSha256);
        await env.AssertAgentIdentityAsync(env.InitialPin);

        var next = await env.Service.RotateAsync(NodeId, Ct);

        Assert.NotEqual(first, next.RotationId);
        Assert.Equal(CertificateRotationStage.Committed, next.Stage);
        await env.AssertAgentIdentityAsync(next.NewSpkiSha256!);
    }

    [Fact]
    public async Task AnOrphanRotationOnTheAgentIsAbortedBeforeANewOneStarts()
    {
        await using var env = await RotationEnv.StartAsync();
        using (var agent = env.ContractClient())
        {
            await AcceptedAsync(agent, AgentRoutes.PkiRotationStart, Requests.Start("rot-orphan"));
        }

        var rotation = await env.Service.RotateAsync(NodeId, Ct);

        Assert.Equal(CertificateRotationStage.Committed, rotation.Stage);
        Assert.Equal(PkiRotationStage.Aborted, (await env.Factory.GetClient(NodeId).GetRotationAsync("rot-orphan", Ct))?.Stage);
    }

    [Fact]
    public async Task ARotationOfAReEnrolledNodeIsAbandoned()
    {
        await using var env = await RotationEnv.StartAsync();
        env.Service.FaultInjection = p => p == "persisted:CsrReceived" ? throw new SimulatedCrashException(p) : Task.CompletedTask;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.RotateAsync(NodeId, Ct));
        env.Service.FaultInjection = null;
        string old = (await env.Registry.GetAsync(NodeId, Ct))!.Rotation!.RotationId;
        await env.Registry.UpdateAsync(NodeId, n => n with { Rotation = n.Rotation! with { NodeUid = "uid-before-rekey" } }, Ct);

        var rotation = await env.Service.RotateAsync(NodeId, Ct);

        Assert.NotEqual(old, rotation.RotationId);
        Assert.Equal(CertificateRotationStage.Committed, rotation.Stage);
    }

    // =====================================================================================================
    // Time-driven renewal, master re-issue, Degraded
    // =====================================================================================================

    [Fact]
    public async Task ARenewalStartsAt60DaysOnTheTimeProvider_AfterTheMasterCertificateWasReissued()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var env = await RotationEnv.StartAsync(clock);
        DateTimeOffset enrolledUntil = (await env.Registry.GetAsync(NodeId, Ct))!.CertNotAfter!.Value;
        IssuedCredential firstMaster = env.MasterCertificates.ClientCredential;

        clock.Advance(TimeSpan.FromDays(59));
        await env.Service.RunDueAsync(Ct);

        NodeRecord early = (await env.Registry.GetAsync(NodeId, Ct))!;
        Assert.Null(early.Rotation);
        Assert.Equal(env.InitialPin, early.CertSpkiSha256);
        Assert.NotSame(firstMaster, env.MasterCertificates.ClientCredential);

        clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        await env.Service.RunDueAsync(Ct);

        NodeRecord renewed = (await env.Registry.GetAsync(NodeId, Ct))!;
        Assert.Equal(CertificateRotationStage.Committed, renewed.Rotation?.Stage);
        Assert.NotEqual(env.InitialPin, renewed.CertSpkiSha256);
        Assert.True(renewed.CertNotAfter > enrolledUntil.AddDays(59));
        Assert.Equal(NodeId, (await env.Factory.GetClient(NodeId).GetInfoAsync(Ct)).NodeId);

        // Nothing is due right after a renewal.
        await env.Service.RunDueAsync(Ct);
        Assert.Equal(renewed.Rotation!.RotationId, (await env.Registry.GetAsync(NodeId, Ct))!.Rotation!.RotationId);
    }

    [Fact]
    public async Task MasterCertificatesAreReissuedAtHalfLifeAndPersisted()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var ca = CertificateAuthority.Create(clock);
        string dir = NewTempDir();
        try
        {
            using (var certificates = MasterCertificates.LoadOrIssue(ca, dir, ["127.0.0.1", "mirbadllm"], clock))
            {
                string client = certificates.ClientCredential.SpkiSha256;
                string enroll = certificates.EnrollCredential.SpkiSha256;

                clock.Advance(TimeSpan.FromDays(14));
                Assert.Equal(MasterCertificateChange.None, certificates.ReissueIfDue());

                clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(10));
                Assert.Equal(MasterCertificateChange.Client | MasterCertificateChange.Enroll, certificates.ReissueIfDue());
                Assert.NotEqual(client, certificates.ClientCredential.SpkiSha256);
                Assert.NotEqual(enroll, certificates.EnrollCredential.SpkiSha256);
                Assert.Equal(MasterCertificateChange.None, certificates.ReissueIfDue());
                Assert.NotNull(certificates.EnrollCertificateContext);
                client = certificates.ClientCredential.SpkiSha256;

                // A restart loads the stored certificates instead of issuing new ones.
                using var reloaded = MasterCertificates.LoadOrIssue(ca, dir, ["127.0.0.1"], clock);
                Assert.Equal(client, reloaded.ClientCredential.SpkiSha256);
            }

            // Expired material on disk is replaced.
            clock.Advance(TimeSpan.FromDays(40));
            using var replaced = MasterCertificates.LoadOrIssue(ca, dir, null, clock);
            Assert.True(replaced.ClientCredential.NotAfter > clock.GetUtcNow().AddDays(29));
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public void RenewalProblemsAreADegradedReasonFrom14DaysBeforeExpiry()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        NodeRecord node = new() { Id = NodeId, Uid = "uid-1", Address = "https://127.0.0.1:9443", CertNotAfter = now.AddDays(15) };

        Assert.Null(CertificateRotationService.RenewalProblem(node, now));
        Assert.Equal(now.AddDays(-15), CertificateRotationService.RenewalDue(node));
        Assert.Contains("has not been renewed", CertificateRotationService.RenewalProblem(node, now.AddDays(1).AddMinutes(1)), StringComparison.Ordinal);
        string? failing = CertificateRotationService.RenewalProblem(
            node with { Rotation = new CertificateRotationRecord { RotationId = "r", NodeUid = "uid-1", Stage = CertificateRotationStage.Requested, Error = "node unreachable" } },
            now.AddDays(2));
        Assert.Contains("node unreachable", failing, StringComparison.Ordinal);
        Assert.Contains("expired", CertificateRotationService.RenewalProblem(node, now.AddDays(16)), StringComparison.Ordinal);
        Assert.Null(CertificateRotationService.RenewalProblem(node with { Revoked = true }, now.AddDays(16)));
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    private static async Task<OperationStatus> AcceptedAsync(AgentContractClient agent, AgentRoute route, AgentMutation request, params string[] path)
    {
        var reply = await agent.SendAsync(route, path, request, ct: Ct);
        Assert.True(reply.Status == route.SuccessStatus, $"{route.Name}: {reply.Status} {reply.Body}");
        var status = reply.Read<OperationStatus>();
        Assert.Equal(OperationState.Succeeded, status.State);
        return status;
    }

    private static AgentError AssertError(AgentReply reply, int status, string code)
    {
        Assert.True(reply.Status == status, $"Expected {status} {code}, got {reply.Status} {reply.Body}");
        var error = reply.Read<AgentErrorResponse>().Error;
        Assert.Equal(code, error.Code);
        return error;
    }

    private static async Task AssertRefusedAsync(AgentContractClient client) =>
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.SendAsync(AgentRoutes.Info, [], ct: Ct));

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mn036-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDirectory(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static class Requests
    {
        public static PkiRotationStartRequest Start(string rotationId) => Envelope(new PkiRotationStartRequest
        {
            OperationId = Id(), IssuedAt = default, ExpiresAt = default, RotationId = rotationId,
        });

        public static PkiRotationCertRequest Cert(string rotationId, string certPem) => Envelope(new PkiRotationCertRequest
        {
            OperationId = Id(), IssuedAt = default, ExpiresAt = default, RotationId = rotationId, CertPem = certPem,
        });

        public static PkiRotationCommitRequest Commit(string rotationId) => Envelope(new PkiRotationCommitRequest
        {
            OperationId = Id(), IssuedAt = default, ExpiresAt = default, RotationId = rotationId,
        });

        public static PkiRotationAbortRequest Abort(string rotationId) => Envelope(new PkiRotationAbortRequest
        {
            OperationId = Id(), IssuedAt = default, ExpiresAt = default, RotationId = rotationId,
        });

        public static PkiTrustSetRequest Trust(params string[] anchors) => Envelope(new PkiTrustSetRequest
        {
            OperationId = Id(), IssuedAt = default, ExpiresAt = default, Anchors = anchors,
        });

        private static T Envelope<T>(T request)
            where T : AgentMutation
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            return request with { IssuedAt = now, ExpiresAt = now + OperationRetention.EnvelopeLifetime };
        }

        private static string Id() => Guid.NewGuid().ToString("N");
    }

    /// <summary>A real agent (host, operation log, PKI routes) and a master (registry, client factory, rotation service).</summary>
    private sealed class RotationEnv : IAsyncDisposable
    {
        public static readonly MasterTriple MasterTriple = new("rotation-master", 1, "rotation-boot");

        private readonly TimeProvider _time;
        private X509Certificate2? _masterTls;

        private RotationEnv(TimeProvider time)
        {
            _time = time;
            Ca = CertificateAuthority.Create(time);
            StateDir = NewTempDir();
            DataDir = NewTempDir();
            using ECDsa key = PkiDefaults.CreateKey();
            using IssuedCertificate certificate = Ca.IssueAgentCertificate(CertificateSigningRequests.CreatePem(key, NodeId), NodeId, ["127.0.0.1"]);
            PkiFiles.WritePublic(Path.Combine(StateDir, FileAgentCredentials.CertificateFileName), certificate.CertificatePem);
            PkiFiles.WriteSecret(Path.Combine(StateDir, FileAgentCredentials.KeyFileName), key.ExportPkcs8PrivateKeyPem());
            PkiFiles.WritePublic(Path.Combine(StateDir, FileAgentCredentials.CaFileName), Ca.CertificatePem);
            File.WriteAllText(Path.Combine(StateDir, FileAgentCredentials.NodeUidFileName), "uid-rotation-node");
            InitialPin = certificate.SpkiSha256;
            InitialNotAfter = certificate.NotAfter;
            MasterCertificates = MasterCertificates.LoadOrIssue(Ca, Path.Combine(DataDir, "pki"), ["127.0.0.1"], time);
        }

        public CertificateAuthority Ca { get; }

        public string StateDir { get; }

        public string DataDir { get; }

        public string InitialPin { get; }

        public DateTimeOffset InitialNotAfter { get; }

        public MasterCertificates MasterCertificates { get; }

        public PkiAgentHost Agent { get; private set; } = null!;

        public JsonNodeRegistry Registry { get; private set; } = null!;

        public AgentClientFactory Factory { get; private set; } = null!;

        public CertificateRotationService Service { get; private set; } = null!;

        public static async Task<RotationEnv> StartAsync(TimeProvider? time = null)
        {
            var env = new RotationEnv(time ?? TimeProvider.System);
            env.Agent = await PkiAgentHost.StartAsync(env.StateDir, env._time, port: 0);
            env.Registry = env.NewRegistry();
            await env.Registry.UpsertAsync(new NodeRecord
            {
                Id = NodeId,
                Uid = "uid-rotation-node",
                Address = env.Agent.BaseUri.AbsoluteUri,
                Approved = true,
                CertSpkiSha256 = env.InitialPin,
                CertNotAfter = env.InitialNotAfter,
            });
            await env.StartMasterAsync();
            return env;
        }

        /// <summary>A contract-suite client (fresh handler) pinned to <paramref name="pin"/> (default: the current registry pin).</summary>
        public AgentContractClient ContractClient(string? pin = null)
        {
            _masterTls ??= MasterCertificates.ClientCredential.CreateTlsCertificate();
            string accepted = pin ?? Registry.GetAsync(NodeId).GetAwaiter().GetResult()!.CertSpkiSha256!;
            return new AgentContractClient(new AgentContractTarget(Agent.BaseUri, NodeId, Ca, _masterTls, accepted)) { Master = MasterTriple };
        }

        /// <summary>The agent answers on a fresh connection pinned to <paramref name="pin"/> only, and refuses one pinned to anything else.</summary>
        public async Task AssertAgentIdentityAsync(string pin)
        {
            await using (IAgentClientLease lease = await Factory.CreateFreshClientAsync(NodeId, [pin], Ct))
            {
                Assert.Equal(NodeId, (await lease.Client.GetInfoAsync(Ct)).NodeId);
            }

            string other = pin == InitialPin ? Spki.Sha256(RandomNumberGenerator.GetBytes(91)) : InitialPin;
            await using IAgentClientLease wrong = await Factory.CreateFreshClientAsync(NodeId, [other], Ct);
            await Assert.ThrowsAsync<AgentUnreachableException>(() => wrong.Client.GetInfoAsync(Ct));
        }

        public string ReadStateFile(string relative)
        {
            using var stream = new FileStream(Path.Combine(StateDir, relative), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public bool StateFileExists(string relative) => File.Exists(Path.Combine(StateDir, relative));

        /// <summary>Simulated agent crash + restart: the host goes away and a new one starts on the same port and state.</summary>
        public async Task RestartAgentAsync(bool alreadyStopped = false)
        {
            int port = Agent.BaseUri.Port;
            if (!alreadyStopped)
            {
                await Agent.DisposeAsync();
            }

            Agent = await PkiAgentHost.StartAsync(StateDir, _time, port);
        }

        /// <summary>Simulated master crash + restart: new registry instance, client factory and rotation service over the same data.</summary>
        public async Task RestartMasterAsync()
        {
            Service.Dispose();
            await Factory.DisposeAsync();
            Registry.Dispose();
            Registry = NewRegistry();
            await StartMasterAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Factory.DisposeAsync();
            await Agent.DisposeAsync();
            Registry.Dispose();
            _masterTls?.Dispose();
            MasterCertificates.Dispose();
            Ca.Dispose();
            DeleteDirectory(StateDir);
            DeleteDirectory(DataDir);
        }

        private JsonNodeRegistry NewRegistry() => new(Options.Create(new MirbadLLMOptions { DataPath = DataDir }), _time);

        private async Task StartMasterAsync()
        {
            using TrustAnchorSet trust = TrustAnchorSet.Create([Ca.Certificate]);
            Factory = new AgentClientFactory(Registry, new TestMasterIdentity(MasterTriple), MasterCertificates.ClientCredential, trust, _time,
                options: new AgentClientFactoryOptions { DrainTimeout = TimeSpan.FromSeconds(2) });
            await Factory.RefreshAsync(Ct);
            Service = new CertificateRotationService(Registry, Factory, Ca, _time, masterCertificates: MasterCertificates);
        }
    }

    /// <summary>The real agent's host pieces MN-036 needs: mTLS listener, pipeline, operation log, /info and the PKI routes.</summary>
    private sealed class PkiAgentHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private string? _dropSuffix;
        private int _drops;

        private PkiAgentHost(WebApplication app, CapturedLogs logs)
        {
            _app = app;
            Logs = logs;
        }

        public Uri BaseUri { get; private set; } = null!;

        public CapturedLogs Logs { get; }

        public IServiceProvider Services => _app.Services;

        public RotatingAgentCredentials Credentials => Services.GetRequiredService<RotatingAgentCredentials>();

        public int PendingDrops => Volatile.Read(ref _drops);

        public static async Task<PkiAgentHost> StartAsync(string stateDirectory, TimeProvider time, int port)
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            var logs = new CapturedLogs();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Logging.SetMinimumLevel(LogLevel.Information);
            var options = new AgentHostOptions { StateDirectory = stateDirectory, ListenAddress = "127.0.0.1", Port = port };
            builder.Services.AddSingleton(time);
            builder.Services.AddAgentOperationLog(_ => new OperationLogOptions { StateDirectory = stateDirectory, BootId = Guid.NewGuid().ToString("N") });
            builder.Services.AddAgentPki(options);
            AgentHostBuilder.Configure(builder, options);

            WebApplication app = builder.Build();
            var host = new PkiAgentHost(app, logs);

            // Lost acknowledgement: the step runs and is recorded, then the stream is reset instead of answered.
            app.Use(async (HttpContext context, RequestDelegate next) =>
            {
                if (host.TakeDrop(context.Request.Path))
                {
                    context.Response.OnStarting(() =>
                    {
                        context.Abort();
                        return Task.CompletedTask;
                    });
                }

                await next(context);
            });
            AgentHostBuilder.UsePipeline(app);
            app.MapInfoEndpoints();
            app.MapPkiEndpoints();

            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
            host.BaseUri = new Uri(new Uri(address).GetLeftPart(UriPartial.Authority) + "/");
            return host;
        }

        /// <summary>Drops the answers of the next <paramref name="count"/> requests whose path ends with <paramref name="pathSuffix"/>.</summary>
        public void DropResponses(string pathSuffix, int count)
        {
            _dropSuffix = pathSuffix;
            Volatile.Write(ref _drops, count);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private bool TakeDrop(PathString path)
        {
            if (_dropSuffix is not { } suffix || !path.HasValue || !path.Value!.EndsWith(suffix, StringComparison.Ordinal))
            {
                return false;
            }

            while (true)
            {
                int left = Volatile.Read(ref _drops);
                if (left <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _drops, left - 1, left) == left)
                {
                    return true;
                }
            }
        }
    }

    private sealed class TestMasterIdentity(MasterTriple triple) : IMasterIdentity
    {
        public string InstanceId => triple.InstanceId;

        public long Epoch => triple.Epoch;

        public string BootId => triple.BootId;

        public MasterTriple Current => triple;

        public bool TakeoverRequested => false;

        public IReadOnlyList<TakeoverRecord> Takeovers => [];

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

/// <summary>Thrown by a fault-injection hook to simulate a process crash at that point.</summary>
public sealed class SimulatedCrashException : Exception
{
    public SimulatedCrashException()
    {
    }

    public SimulatedCrashException(string message)
        : base("Simulated crash at " + message)
    {
    }

    public SimulatedCrashException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
