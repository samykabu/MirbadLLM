using System.Net;
using System.Security.Cryptography;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Contract;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// Design §13.6 "failed certificate rotation … both sides end on a working certificate; commit only after a fresh-connection
/// check; post-commit abort attempt → 409 already_committed; lost commit response → query/retry finishes promotion", driven
/// by the composed <see cref="CertificateRotationService"/> of the real master against the real agent (master crash after a
/// step was sent, then a master restart over the same data) and FakeAgent (lost answers).
/// </summary>
public sealed class RotationExitTests
{
    public static TheoryData<string> MasterCrashPoints => ["sent:cert", "sent:commit"];

    [Theory]
    [MemberData(nameof(MasterCrashPoints))]
    public async Task AMasterCrashAfterSendingAStepResumesAfterRestartCommitsAndRefusesAnAbortAfterCommit(string point)
    {
        await using var node = await RealNode.StartAsync(models: Model("qwen"));
        string initialPin = node.Node.AgentSpki;
        string rotationId;
        await using (var crashed = await node.Master().StartAsync())
        {
            var service = crashed.Get<CertificateRotationService>();
            service.FaultInjection = p => p == point ? throw new SimulatedMasterCrash(p) : Task.CompletedTask;
            await Assert.ThrowsAsync<SimulatedMasterCrash>(() => service.RotateAsync(NodeId, Ct));
            var open = (await crashed.Get<INodeRegistry>().GetAsync(NodeId, Ct))!.Rotation!;
            rotationId = open.RotationId;
            Assert.NotEqual(CertificateRotationStage.Committed, open.Stage);
            if (point == "sent:commit")
            {
                // The agent committed; the master never heard it. Aborting is refused from Committing on.
                Assert.Equal(CertificateRotationStage.Committing, open.Stage);
                Assert.Equal(PkiRotationStage.Committed, (await crashed.Client.GetRotationAsync(rotationId, Ct))?.Stage);
                await Assert.ThrowsAsync<InvalidOperationException>(() => service.AbortAsync(NodeId, Ct));
            }
        }

        // The restarted master's rotation pass (at boot) resumes the stored rotation from its stage and finishes the promotion.
        await using var master = await node.Master().StartAsync(state: null);
        var rotation = await UntilValueAsync(async () => (await master.Get<INodeRegistry>().GetAsync(NodeId, Ct))?.Rotation is
            { Stage: CertificateRotationStage.Committed } done && done.RotationId == rotationId ? done : null, "the stored rotation to commit");
        await master.WaitForNodeAsync(n => n.State == NodeState.Online, "dgx Online over the new certificate");
        var record = (await master.Get<INodeRegistry>().GetAsync(NodeId, Ct))!;
        Assert.Equal(rotation.NewSpkiSha256, record.CertSpkiSha256);
        Assert.NotEqual(initialPin, record.CertSpkiSha256);
        Assert.Null(record.PendingCertSpkiSha256);

        // Both sides are on the new certificate: the agent's key is the pinned one, a fresh connection pinned to it works and
        // one pinned to the old key is refused.
        using (var key = ECDsa.Create())
        {
            key.ImportFromPem(ReadShared(Path.Combine(node.Node.StateDirectory, MirbadLLM.Agent.Hosting.FileAgentCredentials.KeyFileName)));
            Assert.Equal(record.CertSpkiSha256, Spki.Sha256(key));
        }

        var clients = master.Get<MirbadLLM.Orchestration.Agents.AgentClientFactory>();
        await using (var fresh = await clients.CreateFreshClientAsync(NodeId, [record.CertSpkiSha256!], Ct))
        {
            Assert.Equal(NodeId, (await fresh.Client.GetInfoAsync(Ct)).NodeId);
        }

        await using (var old = await clients.CreateFreshClientAsync(NodeId, [initialPin], Ct))
        {
            await Assert.ThrowsAsync<AgentUnreachableException>(() => old.Client.GetInfoAsync(Ct));
        }

        // An abort after the commit: 409 already_committed from the agent; the master's abort is a no-op on a closed rotation.
        var refused = await Assert.ThrowsAsync<AgentException>(() => master.Client.AbortRotationAsync(Abort(rotationId), Ct));
        Assert.Equal(409, refused.StatusCode);
        Assert.Equal(ErrorCodes.AlreadyCommitted, refused.Code);
        Assert.Equal(CertificateRotationStage.Committed, (await master.Get<CertificateRotationService>().AbortAsync(NodeId, Ct))?.Stage);

        // And the node keeps working over the new certificate: a model starts through it.
        var (status, body) = await SendAsync(master.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        await master.WaitForModelAsync("qwen", ModelState.Ready);
    }

    [Fact]
    public async Task APendingKeyTheAgentCannotUseIsAbortedOnBothSidesAndTheNextRotationCommits()
    {
        // W3 review (MN-094, D49), real agent: the master crashed after issuing the certificate, and the agent's pending key
        // became unreadable meanwhile; the restarted master's pass aborts that rotation on both sides.
        await using var node = await RealNode.StartAsync();
        string initialPin = node.Node.AgentSpki;
        string first;
        await using (var crashed = await node.Master().StartAsync())
        {
            var service = crashed.Get<CertificateRotationService>();
            service.FaultInjection = p => p == "persisted:CertIssued" ? throw new SimulatedMasterCrash(p) : Task.CompletedTask;
            await Assert.ThrowsAsync<SimulatedMasterCrash>(() => service.RotateAsync(NodeId, Ct));
            first = (await crashed.Get<INodeRegistry>().GetAsync(NodeId, Ct))!.Rotation!.RotationId;
        }

        await File.WriteAllTextAsync(Path.Combine(node.Node.StateDirectory, MirbadLLM.Agent.Pki.AgentRotationStore.PendingKeyFileName), "not a key", Ct);
        await using var master = await node.Master().StartAsync(state: null);
        var aborted = await UntilValueAsync(async () => (await master.Get<INodeRegistry>().GetAsync(NodeId, Ct))?.Rotation is
            { Stage: CertificateRotationStage.Aborted } done && done.RotationId == first ? done : null, "the unusable rotation to be aborted");
        Assert.Equal(PkiRotationStage.Aborted, (await master.Client.GetRotationAsync(first, Ct))?.Stage);
        var record = (await master.Get<INodeRegistry>().GetAsync(NodeId, Ct))!;
        Assert.Equal(initialPin, record.CertSpkiSha256);
        Assert.Null(record.PendingCertSpkiSha256);
        await master.WaitForNodeAsync(n => n.State == NodeState.Online, "dgx Online on its old certificate");

        var next = await master.Get<CertificateRotationService>().RotateAsync(NodeId, Ct);
        Assert.NotEqual(aborted.RotationId, next.RotationId);
        Assert.Equal(CertificateRotationStage.Committed, next.Stage);
        Assert.Equal(next.NewSpkiSha256, (await master.Get<INodeRegistry>().GetAsync(NodeId, Ct))!.CertSpkiSha256);
        Assert.Equal(NodeId, (await master.Client.GetInfoAsync(Ct)).NodeId);
    }

    [Fact]
    public async Task LostCertAndCommitAnswersAreRetriedWithTheSameIdsAndTheRotationCommits()
    {
        await using var node = await FakeNode.StartAsync(models: Model("qwen"));
        await using var master = await node.Master().StartAsync();
        string initialPin = node.Agent.CurrentSpki;
        int lostCert = 0, lostCommit = 0;
        node.Agent.DropResponses(route =>
            (route == AgentRoutes.PkiRotationCert && Interlocked.Exchange(ref lostCert, 1) == 0) ||
            (route == AgentRoutes.PkiRotationCommit && Interlocked.Exchange(ref lostCommit, 1) == 0));

        var rotation = await master.Get<CertificateRotationService>().RotateAsync(NodeId, Ct);

        Assert.Equal(1, Volatile.Read(ref lostCert));
        Assert.Equal(1, Volatile.Read(ref lostCommit));
        Assert.Equal(CertificateRotationStage.Committed, rotation.Stage);
        var record = (await master.Get<INodeRegistry>().GetAsync(NodeId, Ct))!;
        Assert.Equal(rotation.NewSpkiSha256, record.CertSpkiSha256);
        Assert.Equal(node.Agent.CurrentSpki, record.CertSpkiSha256);
        Assert.NotEqual(initialPin, record.CertSpkiSha256);

        // Each step ran once on the agent: one operation per step kind for this rotation.
        var operations = await master.Client.ListOperationsAsync(new OperationsQuery(), Ct);
        Assert.Single(operations, o => o.Kind == OperationKinds.PkiRotationCert && o.Target == rotation.RotationId);
        Assert.Single(operations, o => o.Kind == OperationKinds.PkiRotationCommit && o.Target == rotation.RotationId);

        var refused = await Assert.ThrowsAsync<AgentException>(() => master.Client.AbortRotationAsync(Abort(rotation.RotationId), Ct));
        Assert.Equal(409, refused.StatusCode);
        Assert.Equal(ErrorCodes.AlreadyCommitted, refused.Code);
        await master.WaitForNodeAsync(n => n.State == NodeState.Online, "dgx Online over the new certificate");
        var (status, body) = await SendAsync(master.Http.GetAsync("/api/model-folders", Ct));
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
    }

    private static PkiRotationAbortRequest Abort(string rotationId) => new()
    {
        OperationId = AgentContractSuite.NewId(),
        IssuedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow + OperationRetention.EnvelopeLifetime,
        RotationId = rotationId,
    };

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The master process dying at a rotation hook point (nothing after it runs).</summary>
    private sealed class SimulatedMasterCrash(string point) : Exception("Simulated master crash at " + point);
}
