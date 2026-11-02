using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Operations;
using MirbadLLM.Agent.Pki;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Agent;
using ManualClock = MirbadLLM.UnitTests.Pki.ManualTimeProvider;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-116: the CA rotation procedure (design §10.2 "CA rotation") end to end, with two real agents (their PKI routes over
/// mTLS, state on disk) and the real master pieces (<see cref="MasterTrustStore"/>, <see cref="AgentClientFactory"/> over
/// <c>pki/trust.json</c>, <see cref="MasterCertificates"/>, <see cref="CertificateRotationService"/>,
/// <see cref="CaRotationService"/>, <see cref="JsonlAuditLog"/>) rebuilt from <c>{DataPath}</c> on every simulated master
/// restart:
/// <list type="bullet">
/// <item>the full procedure: both roots during the rotation, new CA promoted on disk, master and agent certificates
/// re-issued under it, each proven on a fresh connection under the new root only, the old root removed everywhere;</item>
/// <item>a master crash at every stage and between every pair of writes (and inside the agent-certificate rotations it
/// drives), resumed from disk; an agent restart and a lost <c>PUT /pki/trust</c> answer in the middle;</item>
/// <item>an offline node: both roots stay on the master, it is reached with the previous client certificate when it
/// returns, then the rotation closes; or the operator closes early and the node is left for re-pairing;</item>
/// <item>audit lines for every transition, no key material in them or in the logs.</item>
/// </list>
/// </summary>
public sealed class CaRotationTests
{
    private const string NodeA = "node-a";
    private const string NodeB = "node-b";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARotationWithEveryNodeOnline_EndsWithOnlyTheNewCaOnBothSides()
    {
        await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
        string oldKey = env.ReadPki(CaStore.KeyFileName);

        CaRotationRecord result = await env.Service.StartAsync(AuditActors.Admin("a1b2c3d4e5f6"), Ct);

        AssertStage(CaRotationStage.Closed, result);
        Assert.Equal(env.OldCaSpki, result.OldCaSpkiSha256);
        Assert.NotEqual(env.OldCaSpki, result.NewCaSpkiSha256);
        await env.AssertClosedAsync(result.NewCaSpkiSha256, oldKey, [NodeA, NodeB]);

        // Audit: one line per transition, joined by the rotation id; operator start, system steps after.
        IReadOnlyList<AuditRecord> lines = await env.Audit.ReadAsync(new AuditQuery { Max = 100 }, Ct);
        List<AuditRecord> ca = [.. lines.Where(l => l.Target == result.RotationId).Reverse()];
        Assert.Equal("admin:a1b2c3d4e5f6", ca[0].Actor);
        Assert.Contains("started", ca[0].Detail, StringComparison.Ordinal);
        Assert.All(ca.Skip(1), l => Assert.Equal(AuditActors.SystemPki, l.Actor));
        foreach (string node in new[] { NodeA, NodeB })
        {
            Assert.Contains(ca, l => l.NodeId == node && l.Action == AuditActions.PkiCaRotation && l.Detail!.Contains("trusts the new CA", StringComparison.Ordinal));
            Assert.Contains(ca, l => l.NodeId == node && l.Action == AuditActions.NodeRotateCert && l.Result == AuditResults.Succeeded);
            Assert.Contains(ca, l => l.NodeId == node && l.Detail!.Contains("verified on a fresh connection", StringComparison.Ordinal));
            Assert.Contains(ca, l => l.NodeId == node && l.Detail!.Contains("only the new CA", StringComparison.Ordinal));
        }

        Assert.Contains(ca, l => l.Detail!.Contains("master certificates re-issued", StringComparison.Ordinal));
        Assert.Contains(ca, l => l.NodeId is null && l.Detail!.Contains("the master trusts only the new CA", StringComparison.Ordinal));
        Assert.Equal("stage Closed: closed: only the new CA is trusted", ca[^1].Detail);
        // Nothing secret anywhere: audit file, master and agent logs.
        env.AssertNoKeyMaterialLogged();
    }

    [Fact]
    public async Task AfterTheRotation_TheOldCaIsRefusedOnBothSidesOverRealTls()
    {
        await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
        CaRotationRecord result = await env.Service.StartAsync(AuditActors.Config, Ct);
        AssertStage(CaRotationStage.Closed, result);

        // Agent side: a master client certificate under the old CA is refused; the current one (new CA) is accepted.
        using IssuedCredential oldMasterClient = env.OldCa.IssueMasterClientCertificate();
        await using (var withOld = new AgentClientFactory(env.Registry, new TestMasterIdentity(), oldMasterClient, env.Trust, TimeProvider.System))
        {
            await withOld.RefreshAsync(Ct);
            await using IAgentClientLease lease = await withOld.CreateFreshClientAsync(NodeA, Ct);
            await Assert.ThrowsAsync<AgentUnreachableException>(() => lease.Client.GetInfoAsync(Ct));
        }

        await using (IAgentClientLease current = await env.Factory.CreateFreshClientAsync(NodeA, Ct))
        {
            Assert.Equal(NodeA, (await current.Client.GetInfoAsync(Ct)).NodeId);
        }

        // Master side: node-b's agent put back on its old certificate (old CA, old pin) is refused by the master, while
        // the agent itself would accept the master (its trust file holds the new CA, the master presents a new-CA client).
        await env.StopAgentAsync(NodeB);
        env.WriteAgentState(NodeB, FileAgentCredentials.CertificateFileName, env.InitialCertificatePem(NodeB));
        env.WriteAgentState(NodeB, FileAgentCredentials.KeyFileName, env.InitialKeyPem(NodeB));
        await env.StartAgentAsync(NodeB);
        await using IAgentClientLease stale = await env.Factory.CreateFreshClientAsync(NodeB, [env.InitialPin(NodeB)], Ct);
        await Assert.ThrowsAsync<AgentUnreachableException>(() => stale.Client.GetInfoAsync(Ct));

        // Control: the same handshake validated under the old root (the pre-rotation trust) succeeds, so the refusal above
        // is the master's trust alone (the agent accepted the new-CA client certificate).
        await using IAgentClientLease oldTrust = await env.Factory.CreateFreshClientAsync(NodeB, [env.InitialPin(NodeB)], [env.OldCa.CertificatePem], Ct);
        Assert.Equal(NodeB, (await oldTrust.Client.GetInfoAsync(Ct)).NodeId);
    }

    [Fact]
    public async Task AnAgentRestartAndALostTrustAnswerInTheMiddle_DoNotStopTheRotation()
    {
        await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
        string oldKey = env.ReadPki(CaStore.KeyFileName);
        env.Agent(NodeB).DropResponses("/pki/trust", 1);
        env.Service.FaultInjection = async point =>
        {
            if (point == "persisted:AgentsRotating")
            {
                await env.StopAgentAsync(NodeA);
                await env.StartAgentAsync(NodeA); // its trust (both roots) and certificate come back from disk
            }
        };

        CaRotationRecord result = await env.Service.StartAsync(AuditActors.Config, Ct);

        Assert.Equal(0, env.Agent(NodeB).PendingDrops);
        AssertStage(CaRotationStage.Closed, result);
        await env.AssertClosedAsync(result.NewCaSpkiSha256, oldKey, [NodeA, NodeB]);
    }

    [Fact]
    public async Task AnOfflineNode_KeepsBothRootsOnTheMaster_IsReachedWithThePreviousClientCertificate_AndClosesWhenBack()
    {
        await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
        string oldKey = env.ReadPki(CaStore.KeyFileName);
        await env.StopAgentAsync(NodeB);

        CaRotationRecord waiting = await env.Service.StartAsync(AuditActors.Config, Ct);

        // The rotation stays open: both roots on the master, node-a done, node-b recorded with its error.
        AssertStage(CaRotationStage.AgentsRotating, waiting);
        Assert.Equal([env.OldCaSpki, waiting.NewCaSpkiSha256], env.Trust.Document.Anchors.Select(a => a.SpkiSha256));
        Assert.Equal(2, env.Factory.TrustedCaSpkis.Count);
        CaRotationNodeProgress a = waiting.Nodes.Single(n => n.NodeId == NodeA);
        CaRotationNodeProgress b = waiting.Nodes.Single(n => n.NodeId == NodeB);
        Assert.True(a.TrustAdded && a.CertificateRotated && !a.OldTrustRemoved);
        Assert.False(b.TrustAdded || b.CertificateRotated);
        Assert.NotNull(b.Error);
        Assert.False(env.Trust.IsPending(NodeA));
        Assert.True(env.Trust.IsPending(NodeB));
        Assert.NotNull(env.Trust.PendingReason(NodeB));
        Assert.Equal([NodeB], env.Factory.PreviousClientNodeIds);
        Assert.True(File.Exists(Path.Combine(env.PkiDir, MasterCertificates.PreviousClientCertificateFileName)));
        Assert.Single(await env.Audit.ReadAsync(new AuditQuery { NodeId = NodeB }, Ct), l => l.Result == AuditResults.Failed);

        // Another pass and a master restart while it waits: still open, still both roots; no second failure line.
        AssertStage(CaRotationStage.AgentsRotating, (await env.Service.ResumeAsync(Ct))!);
        await env.RestartMasterAsync();
        Assert.Equal(2, env.Factory.TrustedCaSpkis.Count);
        Assert.Equal([NodeB], env.Factory.PreviousClientNodeIds);
        Assert.Single(await env.Audit.ReadAsync(new AuditQuery { NodeId = NodeB }, Ct), l => l.Result == AuditResults.Failed);

        // node-b returns trusting only the old CA: the previous client certificate reaches it, then it rotates.
        await env.StartAgentAsync(NodeB);
        CaRotationRecord closed = (await env.Service.ResumeAsync(Ct))!;

        AssertStage(CaRotationStage.Closed, closed);
        Assert.Empty(env.Factory.PreviousClientNodeIds);
        await env.AssertClosedAsync(closed.NewCaSpkiSha256, oldKey, [NodeA, NodeB]);
    }

    [Fact]
    public async Task ClosingEarlyWithAnOfflineNode_TrustsOnlyTheNewCa_AndLeavesTheNodeForRepairing()
    {
        await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
        await env.StopAgentAsync(NodeB);
        CaRotationRecord waiting = await env.Service.StartAsync(AuditActors.Config, Ct);
        AssertStage(CaRotationStage.AgentsRotating, waiting);

        CaRotationRecord closed = await env.Service.CloseEarlyAsync(AuditActors.Admin("0123456789ab"), Ct);

        AssertStage(CaRotationStage.ClosedEarly, closed);
        Assert.Equal([closed.NewCaSpkiSha256], env.Trust.Document.Anchors.Select(x => x.SpkiSha256));
        CaRotationNodeProgress a = closed.Nodes.Single(n => n.NodeId == NodeA);
        CaRotationNodeProgress b = closed.Nodes.Single(n => n.NodeId == NodeB);
        Assert.True(a.CertificateRotated && a.OldTrustRemoved);
        Assert.False(b.CertificateRotated);
        Assert.Equal(CaRotationService.EarlyCloseError, b.Error);
        Assert.False(File.Exists(Path.Combine(env.PkiDir, MasterTrustStore.NextCaKeyFileName)));
        Assert.False(File.Exists(Path.Combine(env.PkiDir, MasterCertificates.PreviousClientCertificateFileName)));
        Assert.Empty(env.Factory.PreviousClientNodeIds);
        Assert.False(env.Trust.IsPending(NodeB)); // closed: nothing pending any more, the node needs a rekey instead

        // node-b comes back on the old CA only: the master no longer accepts it (it must be re-paired).
        await env.StartAgentAsync(NodeB);
        await using IAgentClientLease lease = await env.Factory.CreateFreshClientAsync(NodeB, Ct);
        await Assert.ThrowsAsync<AgentUnreachableException>(() => lease.Client.GetInfoAsync(Ct));
        Assert.Contains(await env.Audit.ReadAsync(new AuditQuery { Max = 100 }, Ct), l => l.Actor == "admin:0123456789ab" && l.Detail!.Contains("close early", StringComparison.Ordinal));
        AssertStage(CaRotationStage.ClosedEarly, (await env.Service.ResumeAsync(Ct))!);
    }

    [Fact]
    public async Task AnOrdinaryRenewalLeftOpenUnderTheOldCa_IsRotatedAgainUnderTheNewCa()
    {
        await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
        string oldKey = env.ReadPki(CaStore.KeyFileName);
        env.Rotations.FaultInjection = CrashAt("persisted:CertInstalled", out _);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Rotations.RotateAsync(NodeA, Ct));
        env.Rotations.FaultInjection = null;
        string firstRotation = (await env.Registry.GetAsync(NodeA, Ct))!.Rotation!.RotationId;

        CaRotationRecord result = await env.Service.StartAsync(AuditActors.Config, Ct);

        AssertStage(CaRotationStage.Closed, result);
        CaRotationNodeProgress a = result.Nodes.Single(n => n.NodeId == NodeA);
        Assert.NotEqual(firstRotation, a.CertificateRotationId);
        IReadOnlyList<AuditRecord> rotations = [.. (await env.Audit.ReadAsync(new AuditQuery { NodeId = NodeA }, Ct)).Where(l => l.Action == AuditActions.NodeRotateCert)];
        Assert.Equal(2, rotations.Count); // the old-CA renewal finished, then a new-CA rotation
        await env.AssertClosedAsync(result.NewCaSpkiSha256, oldKey, [NodeA, NodeB]);
    }

    [Fact]
    public async Task AHalfLifeReissueDuringTheRotation_KeepsBothRootsAndSignsUnderTheNewCa()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var env = await CaEnv.StartAsync([NodeA, NodeB], clock);
        string oldKey = env.ReadPki(CaStore.KeyFileName);
        env.Service.FaultInjection = CrashAt("persisted:AgentsRotating", out _);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.StartAsync(AuditActors.Config, Ct));
        await env.RestartMasterAsync();
        string clientBefore = env.Master.ClientCredential.SpkiSha256;

        clock.Advance(TimeSpan.FromDays(16));
        await env.Rotations.RunDueAsync(Ct);

        Assert.NotEqual(clientBefore, env.Master.ClientCredential.SpkiSha256);
        Assert.True(env.Master.IsIssuedBy(env.Master.Authority));
        Assert.Equal(env.Trust.Document.Rotation!.NewCaSpkiSha256, env.Master.Authority.SpkiSha256);
        Assert.Equal(2, env.Factory.TrustedCaSpkis.Count); // the re-issue did not drop the old root (agents still on it)
        Assert.Equal(NodeA, (await env.Factory.GetClient(NodeA).GetInfoAsync(Ct)).NodeId);

        CaRotationRecord closed = (await env.Service.ResumeAsync(Ct))!;
        AssertStage(CaRotationStage.Closed, closed);
        await env.AssertClosedAsync(closed.NewCaSpkiSha256, oldKey, [NodeA, NodeB]);
    }

    [Fact]
    public async Task NodesEnrolledOrRevokedDuringTheRotation_JoinOrLeaveIt()
    {
        await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
        string oldKey = env.ReadPki(CaStore.KeyFileName);
        await env.StopAgentAsync(NodeB);
        CaRotationRecord waiting = await env.Service.StartAsync(AuditActors.Config, Ct);
        AssertStage(CaRotationStage.AgentsRotating, waiting);

        // node-b is revoked: it leaves the rotation, which then closes.
        await env.Registry.RevokeAsync(NodeB, Ct);
        await env.Factory.RefreshAsync(Ct);
        CaRotationRecord closed = (await env.Service.ResumeAsync(Ct))!;

        AssertStage(CaRotationStage.Closed, closed);
        Assert.Equal([NodeA], closed.Nodes.Select(n => n.NodeId));
        await env.AssertClosedAsync(closed.NewCaSpkiSha256, oldKey, [NodeA]);
    }

    [Fact]
    public async Task ANodeRepairedDuringTheRotation_StartsItsProgressOver()
    {
        // W7 review (MN-126): a rekey resets the agent's trust to the issuing CA only; a progress entry kept from before
        // the re-pair (same uid, older EnrolledAt) would leave it marked trust-added and unreachable.
        await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
        var enrolled = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        await env.Registry.UpdateAsync(NodeB, n => n with { EnrolledAt = enrolled }, Ct);
        await env.StopAgentAsync(NodeB);
        CaRotationRecord waiting = await env.Service.StartAsync(AuditActors.Config, Ct);
        AssertStage(CaRotationStage.AgentsRotating, waiting);
        CaRotationNodeProgress before = waiting.Nodes.Single(n => n.NodeId == NodeB);
        Assert.Equal(enrolled, before.EnrolledAt);

        DateTimeOffset repaired = enrolled.AddMinutes(5);
        await env.Registry.UpdateAsync(NodeB, n => n with { EnrolledAt = repaired }, Ct);
        await env.Factory.RefreshAsync(Ct);
        CaRotationRecord resumed = (await env.Service.ResumeAsync(Ct))!;

        CaRotationNodeProgress after = resumed.Nodes.Single(n => n.NodeId == NodeB);
        Assert.Equal(repaired, after.EnrolledAt);
        Assert.Equal(waiting.Nodes.Single(n => n.NodeId == NodeA), resumed.Nodes.Single(n => n.NodeId == NodeA));
    }

    [Fact]
    public async Task Refusals_AnOpenRotation_AnEarlyCloseBeforeTheReissue_AndAConfiguredCa()
    {
        await using var env = await CaEnv.StartAsync([NodeA]);
        Assert.Equal(ErrorCodes.InvalidState, (await Assert.ThrowsAsync<CaRotationRefusedException>(() => env.Service.CloseEarlyAsync(AuditActors.Config, Ct))).Code);

        env.Service.FaultInjection = CrashAt("persisted:AgentsTrusting", out _);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.StartAsync(AuditActors.Config, Ct));
        await env.RestartMasterAsync();
        Assert.Equal(ErrorCodes.InvalidState, (await Assert.ThrowsAsync<CaRotationRefusedException>(() => env.Service.StartAsync(AuditActors.Config, Ct))).Code);
        Assert.Equal(ErrorCodes.InvalidState, (await Assert.ThrowsAsync<CaRotationRefusedException>(() => env.Service.CloseEarlyAsync(AuditActors.Config, Ct))).Code);
        AssertStage(CaRotationStage.Closed, (await env.Service.ResumeAsync(Ct))!);

        // A master whose CA is not the one in the PKI directory (configured, topology b) does not rotate it.
        File.WriteAllText(Path.Combine(env.PkiDir, CaStore.CertificateFileName), env.OldCa.CertificatePem);
        CaRotationRefusedException configured = await Assert.ThrowsAsync<CaRotationRefusedException>(() => env.Service.StartAsync(AuditActors.Config, Ct));
        Assert.Equal(ErrorCodes.NotSupported, configured.Code);
        Assert.Contains("MirbadLLM:Pki:CaCertPem", configured.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<InvalidOperationException>(configured); // the flag-file pass logs it as a refusal
    }

    [Fact]
    public async Task AConfiguredCa_IsNotRotated_EvenWhenThePkiDirectoryHoldsTheSameCa()
    {
        // Topology b after the move from the DGX (D63): the PVC still holds the DGX's /data/pki and the secret carries the
        // same CA. Rotating here would promote a new CA to disk, and the next start would refuse the configured one.
        await using var env = await CaEnv.StartAsync([NodeA]);
        env.PkiOptions = new MirbadLLM.Orchestration.MasterPkiOptions { CaCertPem = env.OldCa.CertificatePem, CaKeyPem = env.OldCa.ExportPrivateKeyPem() };
        await env.RestartMasterAsync();
        string caFile = env.ReadPki(CaStore.CertificateFileName);
        string flag = Path.Combine(env.PkiDir, CaRotationOptions.StartFlagFileName);
        await File.WriteAllTextAsync(flag, "", Ct);

        CaRotationRefusedException refused = await Assert.ThrowsAsync<CaRotationRefusedException>(() => env.Service.StartAsync(AuditActors.Config, Ct));
        await env.Service.RunOnceAsync(Ct); // the flag file is refused too, and deleted

        Assert.Equal(ErrorCodes.NotSupported, refused.Code);
        Assert.False(File.Exists(flag));
        Assert.Null(env.Service.Rotation);
        Assert.False(File.Exists(Path.Combine(env.PkiDir, MasterTrustStore.NextCaKeyFileName)));
        Assert.False(File.Exists(Path.Combine(env.PkiDir, MasterTrustDocument.FileName)));
        Assert.Equal(caFile, env.ReadPki(CaStore.CertificateFileName));
        using CertificateAuthority reloaded = CaStore.LoadOrCreate(env.PkiDir, env.PkiOptions.CaCertPem, env.PkiOptions.CaKeyPem);
        Assert.Equal(env.OldCaSpki, reloaded.SpkiSha256); // the next start still accepts the configured CA
    }

    [Fact]
    public async Task ANodeEnrolledUnderTheNewCaDuringTheRotation_IsAdopted_NotCutOffByThePreviousClientCertificate()
    {
        const string NodeC = "node-c";
        await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
        string oldKey = env.ReadPki(CaStore.KeyFileName);
        await env.StopAgentAsync(NodeB);
        CaRotationRecord waiting = await env.Service.StartAsync(AuditActors.Config, Ct);
        AssertStage(CaRotationStage.AgentsRotating, waiting);

        // After step 3 enrollment issues under the new CA (MN-126): the node trusts the new CA only.
        await env.AddEnrolledNodeAsync(NodeC, env.Master.Authority);
        CaRotationRecord still = (await env.Service.ResumeAsync(Ct))!;

        AssertStage(CaRotationStage.AgentsRotating, still); // node-b is still offline
        CaRotationNodeProgress c = still.Nodes.Single(n => n.NodeId == NodeC);
        Assert.True(c.TrustAdded && c.CertificateRotated && c.OldTrustRemoved, $"trust={c.TrustAdded} rotated={c.CertificateRotated} narrowed={c.OldTrustRemoved} error={c.Error}");
        Assert.Null(c.Error);
        Assert.Equal([NodeB], env.Factory.PreviousClientNodeIds); // node-c is reached with the new client certificate
        Assert.Equal(NodeC, (await env.Factory.GetClient(NodeC).GetInfoAsync(Ct)).NodeId);
        NodeRecord record = (await env.Registry.GetAsync(NodeC, Ct))!;
        Assert.Equal(env.InitialPin(NodeC), record.CertSpkiSha256); // no second rotation
        Assert.Null(record.Rotation);
        Assert.Contains(await env.Audit.ReadAsync(new AuditQuery { NodeId = NodeC }, Ct), l => l.Detail!.Contains("already under the new CA", StringComparison.Ordinal));

        // node-b returns; the rotation closes with node-c untouched (never given the old root).
        await env.StartAgentAsync(NodeB);
        CaRotationRecord closed = (await env.Service.ResumeAsync(Ct))!;
        AssertStage(CaRotationStage.Closed, closed);
        await env.AssertClosedAsync(closed.NewCaSpkiSha256, oldKey, [NodeA, NodeB]);
        await using IAgentClientLease lease = await env.Factory.CreateFreshClientAsync(NodeC, null, [env.Master.Authority.CertificatePem], Ct);
        Assert.Equal([closed.NewCaSpkiSha256], (await lease.Client.GetTrustAsync(Ct)).Anchors.Select(a => a.SpkiSha256));
    }

    [Fact]
    public async Task ThePreviousClientCertificate_IsNotUsedFromClosingOn_AndNeverOutlivesTheRotation()
    {
        await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
        await env.StopAgentAsync(NodeB);
        CaRotationRecord waiting = await env.Service.StartAsync(AuditActors.Config, Ct);
        AssertStage(CaRotationStage.AgentsRotating, waiting);
        Assert.Equal([NodeB], env.Factory.PreviousClientNodeIds);

        // An early close that cannot finish (node-a is offline for its narrowing) stays at Closing; after a master
        // restart the node left behind is not reached with the previous client certificate again.
        await env.StopAgentAsync(NodeA);
        AssertStage(CaRotationStage.Closing, await env.Service.CloseEarlyAsync(AuditActors.Config, Ct));
        Assert.Empty(env.Factory.PreviousClientNodeIds);
        await env.RestartMasterAsync();
        Assert.Empty(env.Factory.PreviousClientNodeIds);

        await env.StartAgentAsync(NodeA);
        AssertStage(CaRotationStage.ClosedEarly, (await env.Service.ResumeAsync(Ct))!);
        Assert.Null(env.Master.PreviousClientCredential);

        // Previous client files left next to a closed rotation (restored by hand) are dropped at the next start.
        env.WriteMasterPki(MasterCertificates.PreviousClientKeyFileName, env.Master.ClientCredential.ExportPrivateKeyPem());
        env.WriteMasterPki(MasterCertificates.PreviousClientCertificateFileName, env.Master.ClientCredential.CertificatePem);
        await env.RestartMasterAsync();
        Assert.Null(env.Master.PreviousClientCredential);
        Assert.False(File.Exists(Path.Combine(env.PkiDir, MasterCertificates.PreviousClientKeyFileName)));
        Assert.False(File.Exists(Path.Combine(env.PkiDir, MasterCertificates.PreviousClientCertificateFileName)));
        Assert.Empty(env.Factory.PreviousClientNodeIds);
    }

    [Fact]
    public async Task WithRegisteredAnchors_UpdateMasterTlsRefusesAnotherTrust_AndUpdateMasterClientKeepsBothRoots()
    {
        await using var env = await CaEnv.StartAsync([NodeA]);
        env.Service.FaultInjection = CrashAt("persisted:AgentsRotating", out _);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.StartAsync(AuditActors.Config, Ct));
        await env.RestartMasterAsync();
        Assert.Equal(2, env.Factory.TrustedCaSpkis.Count);

        // A caller holding only the issuing CA (the half-life re-issue did this before MN-126) is refused, not ignored.
        using IssuedCredential reissued = env.Master.Authority.IssueMasterClientCertificate();
        using (TrustAnchorSet narrower = TrustAnchorSet.Create([env.Master.Authority.Certificate]))
        {
            Assert.Throws<ArgumentException>(() => env.Factory.UpdateMasterTls(reissued, narrower));
        }

        Assert.Equal(2, env.Factory.TrustedCaSpkis.Count);

        // The anchors themselves are accepted; the certificate alone keeps both roots.
        using (TrustAnchorSet anchors = env.Trust.CreateTrustSet())
        {
            env.Factory.UpdateMasterTls(env.Master.ClientCredential, anchors);
        }

        env.Factory.UpdateMasterClient(env.Master.ClientCredential);
        Assert.Equal(2, env.Factory.TrustedCaSpkis.Count);
        Assert.Equal(NodeA, (await env.Factory.GetClient(NodeA).GetInfoAsync(Ct)).NodeId);
    }

    [Fact]
    public async Task TheFlagFile_StartsTheRotationAtTheNextPass_AndIsDeleted()
    {
        await using var env = await CaEnv.StartAsync([NodeA]);
        string flag = Path.Combine(env.PkiDir, CaRotationOptions.StartFlagFileName);
        await File.WriteAllTextAsync(flag, "", Ct);

        await env.Service.RunOnceAsync(Ct);

        Assert.False(File.Exists(flag));
        AssertStage(CaRotationStage.Closed, env.Trust.Document.Rotation!);
        Assert.Equal(AuditActors.Config, env.Trust.Document.Rotation!.Actor);
        Assert.Contains(await env.Audit.ReadAsync(new AuditQuery(), Ct), l => l.Actor == AuditActors.Config && l.Detail!.Contains("started", StringComparison.Ordinal));
    }

    [Fact]
    public void TrustStore_LoadsWithoutAFile_RefusesATamperedAnchor_AndRepairsOnlyPastTheReissue()
    {
        string dir = NewTempDir();
        try
        {
            using var ca = CaStore.LoadOrCreate(dir, null, null);
            using (MasterTrustStore fresh = MasterTrustStore.Load(dir, ca.Certificate))
            {
                Assert.Equal([ca.SpkiSha256], fresh.Document.Anchors.Select(a => a.SpkiSha256));
                Assert.Null(fresh.Document.Rotation);
                Assert.False(File.Exists(Path.Combine(dir, MasterTrustDocument.FileName)));
                Assert.False(fresh.IsPending("any"));
            }

            using var other = CertificateAuthority.Create();
            string tampered = System.Text.Json.JsonSerializer.Serialize(new MasterTrustDocument
            {
                Anchors = [new MasterTrustAnchor(other.CertificatePem, ca.SpkiSha256, DateTimeOffset.UtcNow)],
            }, MasterStateJson.Options);
            File.WriteAllText(Path.Combine(dir, MasterTrustDocument.FileName), tampered);
            Assert.Throws<PkiException>(() => MasterTrustStore.Load(dir, ca.Certificate));

            // Before MasterReissued the CA files are never touched, even with the new CA's files present.
            WriteRotation(dir, ca, other, CaRotationStage.AgentsTrusting);
            PkiFiles.WritePublic(Path.Combine(dir, MasterTrustStore.NextCaCertificateFileName), other.CertificatePem);
            PkiFiles.WriteSecret(Path.Combine(dir, MasterTrustStore.NextCaKeyFileName), other.ExportPrivateKeyPem());
            Assert.False(MasterTrustStore.RecoverCaFiles(dir));
            using (var still = CaStore.LoadOrCreate(dir, null, null))
            {
                Assert.Equal(ca.SpkiSha256, still.SpkiSha256);
            }

            // At MasterReissued a split promotion (new key, old certificate) is completed.
            WriteRotation(dir, ca, other, CaRotationStage.MasterReissued);
            PkiFiles.WriteSecret(Path.Combine(dir, CaStore.KeyFileName), other.ExportPrivateKeyPem());
            Assert.Throws<PkiException>(() => CaStore.LoadOrCreate(dir, null, null).Dispose());
            Assert.True(MasterTrustStore.RecoverCaFiles(dir));
            using (var promoted = CaStore.LoadOrCreate(dir, null, null))
            {
                Assert.Equal(other.SpkiSha256, promoted.SpkiSha256);
            }

            Assert.False(MasterTrustStore.RecoverCaFiles(dir)); // idempotent
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    /// <summary>A master crash at every point of the procedure, resumed from disk (own class: runs in parallel with the others).</summary>
    public sealed class MasterCrash
    {
        public static TheoryData<string> MasterCrashPoints =>
        [
            "next:key-written", "persisted:Started", "persisted:AgentsTrusting", "sent:trust", "sent:trust#2", "previous-retained",
            "persisted:MasterReissued", "promote:key-written", "promoted", "reissued", "persisted:AgentsRotating", "rotating",
            "rotated", "rotated#2", "persisted:Closing", "master-narrowed", "sent:trust#4", "cleaned", "persisted:Closed",
        ];

        [Theory]
        [MemberData(nameof(MasterCrashPoints))]
        public async Task AMasterCrashAtAnyPoint_ResumesFromDiskAndCloses(string crashPoint)
        {
            await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
            string oldKey = env.ReadPki(CaStore.KeyFileName);
            env.Service.FaultInjection = CrashAt(crashPoint, out Func<bool> crashed);

            await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.StartAsync(AuditActors.Config, Ct));
            Assert.True(crashed());

            if (crashPoint == "promote:key-written")
            {
                // The crash split ca.key (new) from ca.crt (old): without the boot repair the master could not load its CA.
                Assert.Throws<PkiException>(() => CaStore.LoadOrCreate(env.PkiDir, null, null).Dispose());
            }

            await env.RestartMasterAsync();
            CaRotationRecord? resumed = await env.Service.ResumeAsync(Ct);
            if (crashPoint == "next:key-written")
            {
                // Nothing was recorded: no rotation, the old CA alone; starting again works over the stale new-CA file.
                Assert.Null(resumed);
                Assert.Single(env.Trust.Document.Anchors);
                Assert.Equal(env.OldCaSpki, env.Trust.Document.Anchors[0].SpkiSha256);
                resumed = await env.Service.StartAsync(AuditActors.Config, Ct);
            }

            Assert.NotNull(resumed);
            AssertStage(CaRotationStage.Closed, resumed);
            await env.AssertClosedAsync(resumed!.NewCaSpkiSha256, oldKey, [NodeA, NodeB]);
            env.AssertNoKeyMaterialLogged();
        }
    }

    /// <summary>A master crash inside the agent-certificate rotations the procedure drives, resumed under the new CA.</summary>
    public sealed class AgentCertificateRotationCrash
    {
        public static TheoryData<string> AgentCertificateRotationCrashPoints =>
        [
            "persisted:Requested", "sent:start", "persisted:CertIssued", "sent:cert", "persisted:CertInstalled", "persisted:Committing", "sent:commit",
        ];

        [Theory]
        [MemberData(nameof(AgentCertificateRotationCrashPoints))]
        public async Task AMasterCrashInsideAnAgentCertificateRotation_ResumesUnderTheNewCa(string crashPoint)
        {
            await using var env = await CaEnv.StartAsync([NodeA, NodeB]);
            string oldKey = env.ReadPki(CaStore.KeyFileName);
            env.Rotations.FaultInjection = CrashAt(crashPoint, out Func<bool> crashed);

            await Assert.ThrowsAsync<SimulatedCrashException>(() => env.Service.StartAsync(AuditActors.Config, Ct));
            Assert.True(crashed());
            AssertStage(CaRotationStage.AgentsRotating, env.Trust.Document.Rotation!);

            await env.RestartMasterAsync();
            CaRotationRecord? resumed = await env.Service.ResumeAsync(Ct);

            AssertStage(CaRotationStage.Closed, resumed!);
            await env.AssertClosedAsync(resumed!.NewCaSpkiSha256, oldKey, [NodeA, NodeB]);
        }
    }

    private static void AssertStage(CaRotationStage expected, CaRotationRecord? record)
    {
        Assert.NotNull(record);
        Assert.True(expected == record.Stage,
            $"Expected {expected}, was {record.Stage}; nodes: " + string.Join("; ", record.Nodes.Select(n => $"{n.NodeId} trust={n.TrustAdded} rotated={n.CertificateRotated} narrowed={n.OldTrustRemoved} error={n.Error}")));
    }

    private static void WriteRotation(string dir, CertificateAuthority oldCa, CertificateAuthority newCa, CaRotationStage stage)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string json = System.Text.Json.JsonSerializer.Serialize(new MasterTrustDocument
        {
            Anchors = [MasterTrustStore.AnchorOf(oldCa.Certificate, now), MasterTrustStore.AnchorOf(newCa.Certificate, now)],
            Rotation = new CaRotationRecord { RotationId = "carot-test", OldCaSpkiSha256 = oldCa.SpkiSha256, NewCaSpkiSha256 = newCa.SpkiSha256, Stage = stage },
        }, MasterStateJson.Options);
        File.WriteAllText(Path.Combine(dir, MasterTrustDocument.FileName), json);
    }

    /// <summary>A hook that throws <see cref="SimulatedCrashException"/> at the n-th hit of a point (<c>point</c> or <c>point#n</c>).</summary>
    private static Func<string, Task> CrashAt(string spec, out Func<bool> crashed)
    {
        string[] parts = spec.Split('#');
        string point = parts[0];
        int nth = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 1;
        int hits = 0;
        bool done = false;
        crashed = () => done;
        return p =>
        {
            if (!done && p == point && Interlocked.Increment(ref hits) == nth)
            {
                done = true;
                throw new SimulatedCrashException(spec);
            }

            return Task.CompletedTask;
        };
    }

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mirbadllm-carot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDirectory(string dir)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>Two (or more) real agents and the master pieces of a CA rotation, the master rebuilt from disk on restart.</summary>
    private sealed class CaEnv : IAsyncDisposable
    {
        private readonly Dictionary<string, AgentSlot> _agents = new(StringComparer.Ordinal);
        private readonly CapturedLogs _masterLogs = new();
        private ILoggerFactory? _loggerFactory;

        private CaEnv(TimeProvider time)
        {
            Time = time;
            DataDir = NewTempDir();
            OldCa = CaStore.LoadOrCreate(PkiDir, null, null, time); // the on-disk CA (topology a)
            OldCaSpki = OldCa.SpkiSha256;
        }

        public TimeProvider Time { get; }

        public string DataDir { get; }

        public string PkiDir => MirbadLLM.Orchestration.MasterPkiOptions.DirectoryOf(DataDir);

        /// <summary>The CA at the start (a test copy; the master loads its own from disk).</summary>
        public CertificateAuthority OldCa { get; }

        public string OldCaSpki { get; }

        public JsonNodeRegistry Registry { get; private set; } = null!;

        public CertificateAuthority BootCa { get; private set; } = null!;

        public MasterCertificates Master { get; private set; } = null!;

        public MasterTrustStore Trust { get; private set; } = null!;

        public AgentClientFactory Factory { get; private set; } = null!;

        public CertificateRotationService Rotations { get; private set; } = null!;

        public CaRotationService Service { get; private set; } = null!;

        public JsonlAuditLog Audit { get; private set; } = null!;

        /// <summary>The PKI options the next master boot passes to <see cref="CaRotationService"/> (a configured CA).</summary>
        public MirbadLLM.Orchestration.MasterPkiOptions? PkiOptions { get; set; }

        /// <summary>A node enrolled while the master runs: certificate and trust from <paramref name="issuer"/>, approved.</summary>
        public async Task AddEnrolledNodeAsync(string node, CertificateAuthority issuer)
        {
            CreateAgentState(node, issuer);
            await StartAgentAsync(node);
            AgentSlot slot = _agents[node];
            await Registry.UpsertAsync(new NodeRecord
            {
                Id = node,
                Uid = "uid-" + node,
                Address = $"https://127.0.0.1:{slot.Port}/",
                Approved = true,
                CertSpkiSha256 = slot.InitialPin,
                CertNotAfter = slot.InitialNotAfter,
                EnrolledAt = Time.GetUtcNow(),
            });
            await Factory.RefreshAsync(Ct);
        }

        public void WriteMasterPki(string file, string content)
        {
            string path = Path.Combine(PkiDir, file);
            if (file.EndsWith(".key", StringComparison.Ordinal))
            {
                PkiFiles.WriteSecret(path, content);
            }
            else
            {
                PkiFiles.WritePublic(path, content);
            }
        }

        public static async Task<CaEnv> StartAsync(string[] nodes, TimeProvider? time = null)
        {
            var env = new CaEnv(time ?? TimeProvider.System);
            env.Registry = env.NewRegistry();
            foreach (string node in nodes)
            {
                env.CreateAgentState(node);
                await env.StartAgentAsync(node);
                AgentSlot slot = env._agents[node];
                await env.Registry.UpsertAsync(new NodeRecord
                {
                    Id = node,
                    Uid = "uid-" + node,
                    Address = $"https://127.0.0.1:{slot.Port}/",
                    Approved = true,
                    CertSpkiSha256 = slot.InitialPin,
                    CertNotAfter = slot.InitialNotAfter,
                });
            }

            env.Registry.Dispose();
            await env.BootMasterAsync();
            return env;
        }

        public PkiAgentHost Agent(string node) => _agents[node].Host ?? throw new InvalidOperationException($"{node} is stopped.");

        public string InitialPin(string node) => _agents[node].InitialPin;

        public string InitialCertificatePem(string node) => _agents[node].InitialCertificatePem;

        public string InitialKeyPem(string node) => _agents[node].InitialKeyPem;

        public async Task StartAgentAsync(string node)
        {
            AgentSlot slot = _agents[node];
            slot.Host = await PkiAgentHost.StartAsync(slot.StateDir, Time, slot.Port);
        }

        public async Task StopAgentAsync(string node)
        {
            AgentSlot slot = _agents[node];
            if (slot.Host is { } host)
            {
                slot.Logs.AddRange(host.Logs.Messages);
                await host.DisposeAsync();
                slot.Host = null;
            }
        }

        public void WriteAgentState(string node, string file, string content)
        {
            string path = Path.Combine(_agents[node].StateDir, file);
            if (file.EndsWith(".key", StringComparison.Ordinal))
            {
                PkiFiles.WriteSecret(path, content);
            }
            else
            {
                PkiFiles.WritePublic(path, content);
            }
        }

        public string ReadPki(string file) => File.ReadAllText(Path.Combine(PkiDir, file), Encoding.ASCII);

        public async Task RestartMasterAsync()
        {
            DisposeMaster();
            await BootMasterAsync();
        }

        /// <summary>The state a closed rotation must leave on disk, on the master and on every listed agent (over real TLS).</summary>
        public async Task AssertClosedAsync(string newCaSpki, string oldCaKeyPem, string[] nodes)
        {
            Assert.NotEqual(OldCaSpki, newCaSpki);

            // Master trust: only the new CA, in the file and in the client factory.
            Assert.Equal([newCaSpki], Trust.Document.Anchors.Select(a => a.SpkiSha256));
            Assert.Equal([newCaSpki], Factory.TrustedCaSpkis);

            // CA files: the new CA is ca.crt/ca.key (the leaked key is gone), its rotation copies and the previous client removed.
            string caKey = ReadPki(CaStore.KeyFileName);
            Assert.NotEqual(oldCaKeyPem.Trim(), caKey.Trim());
            using (CertificateAuthority onDisk = CaStore.LoadOrCreate(PkiDir, null, null))
            {
                Assert.Equal(newCaSpki, onDisk.SpkiSha256);
                Assert.True(Master.IsIssuedBy(onDisk));
                using MasterTrustStore reloaded = MasterTrustStore.Load(PkiDir, onDisk.Certificate);
                Assert.Equal([newCaSpki], reloaded.Document.Anchors.Select(a => a.SpkiSha256));
            }

            foreach (string gone in new[]
                     {
                         MasterTrustStore.NextCaCertificateFileName, MasterTrustStore.NextCaKeyFileName,
                         MasterCertificates.PreviousClientCertificateFileName, MasterCertificates.PreviousClientKeyFileName,
                     })
            {
                Assert.False(File.Exists(Path.Combine(PkiDir, gone)), gone);
            }

            Assert.Null(Master.PreviousClientCredential);
            Assert.Equal(newCaSpki, Master.Authority.SpkiSha256);

            // A restarted master comes up on the new CA alone.
            await RestartMasterAsync();
            Assert.Equal(newCaSpki, BootCa.SpkiSha256);
            Assert.True(Master.IsIssuedBy(BootCa));
            Assert.Equal([newCaSpki], Factory.TrustedCaSpkis);

            MasterTrustAnchor anchor = Trust.Document.Anchors.Single();
            foreach (string node in nodes)
            {
                NodeRecord record = (await Registry.GetAsync(node, Ct))!;
                Assert.NotEqual(_agents[node].InitialPin, record.CertSpkiSha256);
                Assert.Null(record.PendingCertSpkiSha256);
                CaRotationNodeProgress progress = Trust.Document.Rotation!.Nodes.Single(n => n.NodeId == node);
                Assert.True(progress.TrustAdded && progress.CertificateRotated && progress.OldTrustRemoved, node);
                Assert.Null(progress.Error);

                // Fresh connection under the new root only: the agent's certificate chains to it and it lists only the new CA.
                await using (IAgentClientLease lease = await Factory.CreateFreshClientAsync(node, null, [anchor.CaCertPem], Ct))
                {
                    PkiTrustResponse trust = await lease.Client.GetTrustAsync(Ct);
                    Assert.Equal([newCaSpki], trust.Anchors.Select(a => a.SpkiSha256));
                }

                // Under the old root only, the agent's certificate is refused.
                await using (IAgentClientLease old = await Factory.CreateFreshClientAsync(node, null, [OldCa.CertificatePem], Ct))
                {
                    await Assert.ThrowsAsync<AgentUnreachableException>(() => old.Client.GetInfoAsync(Ct));
                }

                // The pooled client works too (handlers rebuilt on the new material).
                Assert.Equal(node, (await Factory.GetClient(node).GetInfoAsync(Ct)).NodeId);

                // On the agent's disk: its trust file holds only the new CA.
                string caFile = File.ReadAllText(Path.Combine(_agents[node].StateDir, FileAgentCredentials.CaFileName));
                using TrustAnchorSet agentTrust = TrustAnchorSet.FromPem([caFile]);
                Assert.Equal([newCaSpki], agentTrust.SpkiSha256s);
            }
        }

        public void AssertNoKeyMaterialLogged()
        {
            string audit = string.Join("\n", Directory.Exists(Audit.Directory)
                ? Directory.GetFiles(Audit.Directory).Select(File.ReadAllText)
                : []);
            Assert.False(string.IsNullOrEmpty(audit));
            Assert.DoesNotContain("PRIVATE KEY", audit, StringComparison.Ordinal);
            Assert.DoesNotContain("-----BEGIN", audit, StringComparison.Ordinal);
            IEnumerable<string> logs = _masterLogs.Messages.Concat(_agents.Values.SelectMany(a => a.Logs.Concat(a.Host?.Logs.Messages ?? [])));
            Assert.DoesNotContain(logs, m => m.Contains("PRIVATE KEY", StringComparison.Ordinal) || m.Contains("-----BEGIN", StringComparison.Ordinal));
        }

        public async ValueTask DisposeAsync()
        {
            DisposeMaster();
            foreach (string node in _agents.Keys.ToList())
            {
                await StopAgentAsync(node);
            }

            OldCa.Dispose();
            _loggerFactory?.Dispose();
            foreach (AgentSlot slot in _agents.Values)
            {
                DeleteDirectory(slot.StateDir);
            }

            DeleteDirectory(DataDir);
        }

        private JsonNodeRegistry NewRegistry() => new(Options.Create(new MirbadLLMOptions { DataPath = DataDir }), Time);

        private void CreateAgentState(string node, CertificateAuthority? issuer = null)
        {
            issuer ??= OldCa;
            string stateDir = NewTempDir();
            using ECDsa key = PkiDefaults.CreateKey();
            using IssuedCertificate certificate = issuer.IssueAgentCertificate(CertificateSigningRequests.CreatePem(key, node), node, ["127.0.0.1"]);
            string keyPem = key.ExportPkcs8PrivateKeyPem();
            PkiFiles.WritePublic(Path.Combine(stateDir, FileAgentCredentials.CertificateFileName), certificate.CertificatePem);
            PkiFiles.WriteSecret(Path.Combine(stateDir, FileAgentCredentials.KeyFileName), keyPem);
            PkiFiles.WritePublic(Path.Combine(stateDir, FileAgentCredentials.CaFileName), issuer.CertificatePem);
            File.WriteAllText(Path.Combine(stateDir, FileAgentCredentials.NodeUidFileName), "uid-" + node);
            _agents[node] = new AgentSlot(stateDir, TestPorts.Reserve(), certificate.SpkiSha256, certificate.NotAfter, certificate.CertificatePem, keyPem);
        }

        /// <summary>The master's boot order for the PKI (what MN-130 composes): repair, CA, master certificates, trust, clients, services.</summary>
        private async Task BootMasterAsync()
        {
            MasterTrustStore.RecoverCaFiles(PkiDir);
            BootCa = CaStore.LoadOrCreate(PkiDir, null, null, Time);
            Master = MasterCertificates.LoadOrIssue(BootCa, PkiDir, ["127.0.0.1"], Time);
            Trust = MasterTrustStore.Load(PkiDir, BootCa.Certificate, Time);
            Registry = NewRegistry();
            _loggerFactory ??= LoggerFactory.Create(b => b.AddProvider(_masterLogs).SetMinimumLevel(LogLevel.Debug));
            Factory = new AgentClientFactory(Registry, new TestMasterIdentity(), Master.ClientCredential, Trust, Time,
                _loggerFactory.CreateLogger<AgentClientFactory>(), new AgentClientFactoryOptions { DrainTimeout = TimeSpan.FromSeconds(2) });
            await Factory.RefreshAsync(Ct);
            Rotations = new CertificateRotationService(Registry, Factory, BootCa, Time, _loggerFactory.CreateLogger<CertificateRotationService>(), masterCertificates: Master);
            Audit = new JsonlAuditLog(Path.Combine(DataDir, AuditLogFiles.DirectoryName), Time);
            Service = new CaRotationService(Registry, Factory, Trust, Master, Rotations, Time, Audit, _loggerFactory.CreateLogger<CaRotationService>(), pki: PkiOptions);
            await Service.InitializeAsync(Ct);
        }

        private void DisposeMaster()
        {
            Service?.Dispose();
            Rotations?.Dispose();
            Factory?.Dispose();
            Trust?.Dispose();
            Master?.Dispose();
            BootCa?.Dispose();
            Registry?.Dispose();
            Audit?.Dispose();
        }

        private sealed class AgentSlot(string stateDir, int port, string initialPin, DateTimeOffset initialNotAfter, string initialCertificatePem, string initialKeyPem)
        {
            public string StateDir { get; } = stateDir;

            public int Port { get; } = port;

            public string InitialPin { get; } = initialPin;

            public DateTimeOffset InitialNotAfter { get; } = initialNotAfter;

            public string InitialCertificatePem { get; } = initialCertificatePem;

            public string InitialKeyPem { get; } = initialKeyPem;

            public PkiAgentHost? Host { get; set; }

            public List<string> Logs { get; } = [];
        }
    }

    /// <summary>The real agent's host pieces a CA rotation needs: mTLS listener, pipeline, operation log, /info and the PKI routes.</summary>
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

        public CapturedLogs Logs { get; }

        public int PendingDrops => Volatile.Read(ref _drops);

        public static async Task<PkiAgentHost> StartAsync(string stateDirectory, TimeProvider time, int port)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await StartOnceAsync(stateDirectory, time, port);
                }
                catch (Exception ex) when (attempt < 20 && TestPorts.IsAddressInUse(ex))
                {
                    // The previous host on this port is still releasing it.
                    await Task.Delay(100, Ct);
                }
            }
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

        private static async Task<PkiAgentHost> StartOnceAsync(string stateDirectory, TimeProvider time, int port)
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
            try
            {
                await app.StartAsync();
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }

            return host;
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

    private sealed class TestMasterIdentity : IMasterIdentity
    {
        private static readonly MasterTriple Triple = new("ca-rotation-master", 1, "ca-rotation-boot");

        public string InstanceId => Triple.InstanceId;

        public long Epoch => Triple.Epoch;

        public string BootId => Triple.BootId;

        public MasterTriple Current => Triple;

        public bool TakeoverRequested => false;

        public IReadOnlyList<TakeoverRecord> Takeovers => [];

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
