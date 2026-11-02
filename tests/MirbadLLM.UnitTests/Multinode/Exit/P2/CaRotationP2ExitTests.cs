using System.Net;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, CA rotation (design §10.2; MN-116, MN-130; D80, D86) on the real master with its own CA in <c>{DataPath}/pki</c>
/// and real agents. The product entry point is the flag file <c>{DataPath}/pki/rotate-ca</c>, read by the hosted
/// <see cref="CaRotationService"/> at boot and every <c>CheckInterval</c> (5 min); the tests run that pass
/// (<see cref="CaRotationService.RunOnceAsync"/>) instead of waiting for the interval. An offline PC keeps the rotation
/// open (both roots on the master, across a restart) and reads Degraded <c>ca_rotation_pending</c> when it returns until the
/// next pass rotates it; the rotation then closes with only the new CA everywhere. A rekey during the open rotation (D86)
/// re-pairs the PC under the new CA and the rotation closes too.
/// </summary>
public sealed class CaRotationP2ExitTests
{
    private const string PendingText = "A CA rotation is open";

    [Fact]
    public async Task AnOfflinePcKeepsBothRootsUntilItReturnsReadsCaRotationPendingAndThenTheRotationClosesOnTheNewCa()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx")] });
        await fleet.JoinAsync("pc", PcGpus);
        await fleet.Master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[0]]));
        string oldCaPem = await File.ReadAllTextAsync(PkiFile(fleet, CaStore.CertificateFileName), Ct);
        string oldSpki = Assert.Single(fleet.Master.Get<MasterTrustStore>().Document.Anchors).SpkiSha256;

        await fleet["pc"].StopAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Offline, "pc Offline");

        // The operator's entry point: the flag file, read by the next pass.
        string flag = PkiFile(fleet, CaRotationOptions.StartFlagFileName);
        await File.WriteAllTextAsync(flag, "", Ct);
        await fleet.Master.Get<CaRotationService>().RunOnceAsync(Ct);
        Assert.False(File.Exists(flag));
        CaRotationRecord open = Rotation(fleet)!;
        Assert.Equal(CaRotationStage.AgentsRotating, open.Stage);
        Assert.Equal(AuditActors.Config, open.Actor);
        string newSpki = open.NewCaSpkiSha256;
        Assert.NotEqual(oldSpki, newSpki);
        Assert.Equal([oldSpki, newSpki], Anchors(fleet));
        Assert.True(open.Nodes.Single(n => n.NodeId == "dgx").CertificateRotated);
        Assert.False(open.Nodes.Single(n => n.NodeId == "pc").CertificateRotated);
        Assert.NotNull(open.Nodes.Single(n => n.NodeId == "pc").Error);
        Assert.Equal(NodeState.Offline, fleet.Master.Cache.GetNode("pc")!.State);
        await fleet.Master.WaitForNodeAsync("dgx", n => n.State == NodeState.Online && !n.Reasons.Any(r => r.Contains(PendingText, StringComparison.Ordinal)),
            "dgx Online without ca_rotation_pending");

        // A master restart while it waits keeps both roots; its boot pass still waits for pc.
        await fleet.RestartMasterAsync();
        await ExitEnv.UntilAsync(() => fleet.Master.Log.Records.Any(r => r.Message.Contains($"waits for node(s) pc", StringComparison.Ordinal)),
            "the restarted master's first CA-rotation pass");
        Assert.Equal(CaRotationStage.AgentsRotating, Rotation(fleet)!.Stage);
        Assert.Equal([oldSpki, newSpki], Anchors(fleet));
        Assert.Equal(2, fleet.Master.Get<AgentClientFactory>().TrustedCaSpkis.Count);

        // pc returns (still trusting the old CA only): reached, but Degraded ca_rotation_pending until a pass rotates it.
        await fleet["pc"].StartAsync();
        var degraded = await fleet.Master.WaitForNodeAsync("pc",
            n => n.State == NodeState.Degraded && n.Reasons.Any(r => r.Contains(PendingText, StringComparison.Ordinal)), "pc Degraded (ca_rotation_pending)");
        Assert.Contains(degraded.Reasons, r => r.Contains(open.RotationId, StringComparison.Ordinal));

        await fleet.Master.Get<CaRotationService>().RunOnceAsync(Ct);
        CaRotationRecord closed = Rotation(fleet)!;
        Assert.Equal(CaRotationStage.Closed, closed.Stage);
        Assert.All(closed.Nodes, n => Assert.True(n.TrustAdded && n.CertificateRotated && n.OldTrustRemoved && n.Error is null, n.NodeId));
        await AssertOnlyTheNewCaAsync(fleet, newSpki, oldCaPem, ["dgx", "pc"]);

        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online after the rotation", TimeSpan.FromSeconds(90));
        await fleet.Master.StartModelAsync("pcm");

        // Audited: started by the flag (config), pc's failure while offline, the close.
        var lines = (await fleet.AuditAsync()).Where(l => l.Action == AuditActions.PkiCaRotation).ToList();
        Assert.Contains(lines, l => l.Actor == AuditActors.Config && l.Detail!.Contains("started", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.NodeId == "pc" && l.Result == AuditResults.Failed);
        Assert.Contains(lines, l => l.Target == open.RotationId && l.Detail!.Contains("Closed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ARekeyDuringAnOpenRotationRePairsThePcUnderTheNewCaAndTheRotationCloses()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx")] });
        var pc = await fleet.JoinAsync("pc", PcGpus);
        await fleet.Master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[0]]));
        string oldCaPem = await File.ReadAllTextAsync(PkiFile(fleet, CaStore.CertificateFileName), Ct);
        NodeRecord before = (await fleet.Master.Nodes.GetAsync("pc", Ct))!;

        await pc.StopAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Offline, "pc Offline");
        await File.WriteAllTextAsync(PkiFile(fleet, CaRotationOptions.StartFlagFileName), "", Ct);
        await fleet.Master.Get<CaRotationService>().RunOnceAsync(Ct);
        CaRotationRecord open = Rotation(fleet)!;
        Assert.Equal(CaRotationStage.AgentsRotating, open.Stage);
        string newSpki = open.NewCaSpkiSha256;

        // The rekey token and the join command name the current (new) CA (D86); the PC re-pairs keeping its uid.
        JoinTokenCreateResponse token = await fleet.CreateJoinTokenAsync("pc", rekey: true);
        Assert.Equal(Spki.ToJoinHash(newSpki), token.CaHash);
        Assert.NotEqual(Spki.ToJoinHash(open.OldCaSpkiSha256), token.CaHash);

        await fleet.RunJoinAsync(pc, token, rekey: true, nodeUid: before.Uid);
        NodeRecord repaired = (await fleet.Master.Nodes.GetAsync("pc", Ct))!;
        Assert.Equal(before.Uid, repaired.Uid);
        Assert.True(repaired.Approved);
        Assert.NotEqual(before.CertSpkiSha256, repaired.CertSpkiSha256);
        using (var agentTrust = TrustAnchorSet.FromPem([await File.ReadAllTextAsync(Path.Combine(pc.Node.StateDirectory, FileAgentCredentials.CaFileName), Ct)]))
        {
            Assert.Equal([newSpki], agentTrust.SpkiSha256s);
        }

        // The next pass adopts the re-paired node; the rekey alone already took it out of the previous-client set (D105).
        Assert.DoesNotContain("pc", fleet.Master.Get<AgentClientFactory>().PreviousClientNodeIds);
        // The registry sync's refresh (every 5 s) is run now so the factory holds the new pin before the pass.
        await pc.StartAsync();
        await fleet.Master.Get<AgentClientFactory>().RefreshAsync(Ct);
        await fleet.Master.Get<CaRotationService>().RunOnceAsync(Ct);
        Assert.DoesNotContain("pc", fleet.Master.Get<AgentClientFactory>().PreviousClientNodeIds);

        CaRotationRecord closed = Rotation(fleet)!;
        Assert.Equal(CaRotationStage.Closed, closed.Stage);
        CaRotationNodeProgress progress = closed.Nodes.Single(n => n.NodeId == "pc");
        Assert.True(progress.CertificateRotated && progress.Error is null, $"pc: rotated={progress.CertificateRotated} error={progress.Error}");
        Assert.Equal(repaired.EnrolledAt, progress.EnrolledAt); // the rekey restarted its entry
        await AssertOnlyTheNewCaAsync(fleet, newSpki, oldCaPem, ["dgx", "pc"]);

        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online after the rotation");
        await fleet.Master.StartModelAsync("pcm");

        // The rekey is audited (token and enrollment) without the token itself.
        var audit = await fleet.AuditAsync();
        Assert.Contains(audit, l => l.Action == AuditActions.NodeJoinToken && l.NodeId == "pc" && l.Result == AuditResults.Succeeded &&
                                    l.Detail!.Contains("rekey", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit, l => l.Action == AuditActions.NodeEnroll && l.NodeId == "pc" && l.Result == AuditResults.Succeeded &&
                                    l.Time > before.EnrolledAt);
        Assert.DoesNotContain(token.Token, fleet.AuditText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// MN-123 found, MN-124 fixed (D105): after a rekey during an open CA rotation (D86) the master kept reaching the re-paired
    /// node with the previous client certificate (old CA), which it no longer trusts, until the next rotation pass (up to
    /// <c>CheckInterval</c>, 5 min). The enroll listener now tells <c>CaRotationService.NodeEnrolled</c>, which drops the node
    /// from the previous-client set at once when its certificate came from the new CA.
    /// </summary>
    [Fact]
    public async Task ARePairedNodeIsReachedAtOnceWithoutWaitingForTheNextRotationPass()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        NodeRecord before = (await fleet.Master.Nodes.GetAsync("pc", Ct))!;
        await pc.StopAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Offline, "pc Offline");
        await File.WriteAllTextAsync(PkiFile(fleet, CaRotationOptions.StartFlagFileName), "", Ct);
        await fleet.Master.Get<CaRotationService>().RunOnceAsync(Ct);
        Assert.Equal(CaRotationStage.AgentsRotating, Rotation(fleet)!.Stage);

        await fleet.RunJoinAsync(pc, await fleet.CreateJoinTokenAsync("pc", rekey: true), rekey: true, nodeUid: before.Uid);
        await pc.StartAsync();

        // No rotation pass here (the next one is CheckInterval away): the node monitor alone must reach it.
        await fleet.Master.WaitForNodeAsync("pc", n => n.State is NodeState.Online or NodeState.Degraded, "pc reachable after the rekey",
            TimeSpan.FromSeconds(30));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static string PkiFile(P2Fleet fleet, string name) => Path.Combine(fleet.DataDir, MasterPkiOptions.DirectoryName, name);

    private static CaRotationRecord? Rotation(P2Fleet fleet) => fleet.Master.Get<MasterTrustStore>().Document.Rotation;

    private static string[] Anchors(P2Fleet fleet) => [.. fleet.Master.Get<MasterTrustStore>().Document.Anchors.Select(a => a.SpkiSha256)];

    /// <summary>Closed on the new CA: the master trusts and issues under it alone, every agent trusts it alone, the old root is refused.</summary>
    private static async Task AssertOnlyTheNewCaAsync(P2Fleet fleet, string newSpki, string oldCaPem, string[] nodes)
    {
        Assert.Equal([newSpki], Anchors(fleet));
        var factory = fleet.Master.Get<AgentClientFactory>();
        Assert.Equal([newSpki], factory.TrustedCaSpkis);
        Assert.Equal(newSpki, fleet.Master.Get<MasterCertificates>().Authority.SpkiSha256);
        using (CertificateAuthority onDisk = CaStore.LoadOrCreate(Path.Combine(fleet.DataDir, MasterPkiOptions.DirectoryName), null, null))
        {
            Assert.Equal(newSpki, onDisk.SpkiSha256);
        }

        string newCaPem = fleet.Master.Get<MasterTrustStore>().Document.Anchors.Single().CaCertPem;
        foreach (string node in nodes)
        {
            await using (IAgentClientLease lease = await factory.CreateFreshClientAsync(node, null, [newCaPem], Ct))
            {
                Assert.Equal([newSpki], (await lease.Client.GetTrustAsync(Ct)).Anchors.Select(a => a.SpkiSha256));
            }

            await using (IAgentClientLease old = await factory.CreateFreshClientAsync(node, null, [oldCaPem], Ct))
            {
                await Assert.ThrowsAsync<AgentUnreachableException>(() => old.Client.GetInfoAsync(Ct));
            }

            using var agentTrust = TrustAnchorSet.FromPem([await File.ReadAllTextAsync(Path.Combine(fleet[node].Node.StateDirectory, FileAgentCredentials.CaFileName), Ct)]);
            Assert.Equal([newSpki], agentTrust.SpkiSha256s);
        }
    }
}
