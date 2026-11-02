using System.Net;
using System.Net.Http.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using Microsoft.Extensions.Options;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Multinode.Contract;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// Design §13.6 fencing items with two (or three) real master compositions on their own data directories driving one
/// FakeAgent: equal epochs → <c>competing_master</c>; a restored older <c>/data</c> → <c>stale_master</c> everywhere until a
/// takeover, which then succeeds; a queued launch of a superseded master stops at its next destructive stage; an agent that
/// holds a higher epoch when it returns stays Pending takeover until the operator continues.
/// <para>The operator's §4.2 step 3 runs through MN-115's <see cref="TakeoverService"/> over the composed identity, node
/// status cache, registry and agent clients (the P2 composition, MN-130, registers it; <c>TakeoverTests</c> drives it
/// through <c>/api/fencing</c>), then the tests observe the composed node monitor, admin API and router.</para>
/// </summary>
public sealed class FencingExitTests
{
    [Fact]
    public async Task ASecondMasterWithTheSameEpochIsACompetingMasterAndTheFirstKeepsTheNode()
    {
        await using var node = await FakeNode.StartAsync(models: [Model("qwen"), Model("other")]);
        await using var first = await node.Master().StartAsync();
        await StartAsync(first, "qwen");
        Assert.Equal(first.Identity.Current, (await first.Client.GetInfoAsync(Ct)).AcceptedMaster);

        string secondDir = await node.NewDataDirAsync(Model("qwen"), Model("other"));
        await using var second = await node.Master(secondDir).StartAsync(state: null);
        Assert.Equal(first.Identity.Epoch, second.Identity.Epoch);
        Assert.NotEqual(first.Identity.InstanceId, second.Identity.InstanceId);

        // The second master sees the node held by another master at its own epoch: Pending takeover, not serving.
        var blocked = await second.WaitForNodeAsync(n => n.State == NodeState.PendingTakeover, "Pending takeover on the second master");
        Assert.False(blocked.IsServing);

        // Its mutations are refused with competing_master and run nothing.
        var refusal = await Assert.ThrowsAsync<AgentException>(() => StopThroughRunnerAsync(second, "qwen"));
        Assert.Equal(409, refusal.StatusCode);
        Assert.Equal(ErrorCodes.CompetingMaster, refusal.Code);
        var (startStatus, startBody) = await SendAsync(second.Http.PostAsync("/api/models/other/start", content: null, Ct));
        Assert.True(startStatus == HttpStatusCode.ServiceUnavailable, $"{(int)startStatus} {startBody}");
        Assert.Null(await first.Client.GetRuntimeAsync("other", Ct));
        Assert.Equal(ModelState.Ready, (await first.Client.GetRuntimeAsync("qwen", Ct))?.State);

        // The first master still drives the node.
        Assert.Equal(first.Identity.Current, (await first.Client.GetInfoAsync(Ct)).AcceptedMaster);
        var (stopStatus, stopBody) = await SendAsync(first.Http.PostAsync("/api/models/qwen/stop", content: null, Ct));
        Assert.True(stopStatus == HttpStatusCode.NoContent, $"{(int)stopStatus} {stopBody}");
        Assert.Null(await first.Client.GetRuntimeAsync("qwen", Ct));
        Assert.Equal(NodeState.Online, first.Cache.GetNode(NodeId)?.State);
    }

    [Fact]
    public async Task ARestoredOlderDataPathIsStaleMasterEverywhereUntilATakeoverThenTheTakeoverSucceeds()
    {
        await using var node = await FakeNode.StartAsync(models: [Model("qwen"), Model("other")]);

        // A backup of /data taken while the master ran at epoch 1 …
        await (await node.Master().StartAsync()).DisposeAsync();
        string restored = node.Track(NewDirectory("restored"));
        CopyDataDirectory(node.DataDir, restored);

        // … while the live master went on (several boots later, epoch 6) and drives the node; then its host is lost.
        await RaiseEpochAsync(node.DataDir, 5);
        MasterTriple live;
        await using (var liveMaster = await node.Master().StartAsync())
        {
            await StartAsync(liveMaster, "qwen");
            live = liveMaster.Identity.Current;
            Assert.Equal(6, live.Epoch);
            Assert.Equal(live, (await liveMaster.Client.GetInfoAsync(Ct)).AcceptedMaster);
        }

        // The restored /data boots at epoch 2, below the epoch the agent accepted.
        await using var master = await node.Master(restored).StartAsync(state: null);
        Assert.Equal(2, master.Identity.Epoch);
        var blocked = await master.WaitForNodeAsync(n => n.State == NodeState.PendingTakeover, "Pending takeover");
        Assert.False(blocked.IsServing);
        Assert.Equal(6, blocked.AcceptedMaster?.Epoch);

        // stale_master everywhere: a mutation through the runner, the admin API (503, nothing sent) and /v1 (503 node_offline).
        var stale = await Assert.ThrowsAsync<AgentException>(() => StopThroughRunnerAsync(master, "qwen"));
        Assert.Equal(409, stale.StatusCode);
        Assert.Equal(ErrorCodes.StaleMaster, stale.Code);
        var (stopStatus, stopBody) = await SendAsync(master.Http.PostAsync("/api/models/qwen/stop", content: null, Ct));
        Assert.True(stopStatus == HttpStatusCode.ServiceUnavailable, $"{(int)stopStatus} {stopBody}");
        var (chatStatus, chatBody) = await SendAsync(master.Http.PostAsJsonAsync("/v1/chat/completions",
            new { model = "qwen", messages = new[] { new { role = "user", content = "hi" } } }, Ct));
        Assert.True(chatStatus == HttpStatusCode.ServiceUnavailable, $"{(int)chatStatus} {chatBody}");
        Assert.Contains(ErrorCodes.NodeOffline, chatBody, StringComparison.Ordinal);
        Assert.Equal(ModelState.Ready, (await master.Client.GetRuntimeAsync("qwen", Ct))?.State); // nothing was stopped
        Assert.Equal(live, (await master.Client.GetInfoAsync(Ct)).AcceptedMaster);

        // The operator's takeover (§4.2 step 3): epoch = max(restored, every answering agent) + 1 = 7.
        var record = await Takeover(master).TakeOverAsync("exit-test", Ct);
        Assert.Equal(7, record.Epoch);
        Assert.Equal(6, Assert.Single(record.Answered).AcceptedEpoch);
        Assert.Equal(7, master.Identity.Epoch);
        await master.WaitForNodeAsync(n => n.State == NodeState.Online, "Online after the takeover");

        // The node now accepts the restored master: its commands run and the agent holds its triple.
        var (afterStatus, afterBody) = await SendAsync(master.Http.PostAsync("/api/models/qwen/stop", content: null, Ct));
        Assert.True(afterStatus == HttpStatusCode.NoContent, $"{(int)afterStatus} {afterBody}");
        Assert.Null(await master.Client.GetRuntimeAsync("qwen", Ct));
        Assert.Equal(master.Identity.Current, (await master.Client.GetInfoAsync(Ct)).AcceptedMaster);
        await StartAsync(master, "other");
    }

    [Fact]
    public async Task AQueuedLaunchOfASupersededMasterStopsAtItsNextDestructiveStageAndCreatesNothing()
    {
        await using var node = await FakeNode.StartAsync(models: [Model("qwen", image: AgentContractSeed.PullImage), Model("other")]);
        await using var old = await node.Master().StartAsync();
        await node.Agent.SetPullsHeldAsync(true);

        // The old master's launch is accepted and waits in its pull.
        var oldStart = SendAsync(old.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        var queued = await UntilValueAsync(async () => (await old.Client.ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }, Ct))
            .FirstOrDefault(o => o.Kind == OperationKinds.Launch && o.Target == "qwen" && o.Stage == OperationStages.Pulling), "the launch to pull");

        // A newer master (epoch 5) drives the node once.
        string newerDir = await node.NewDataDirAsync(Model("qwen", image: AgentContractSeed.PullImage), Model("other"));
        await RaiseEpochAsync(newerDir, 4);
        await using var newer = await node.Master(newerDir).StartAsync();
        await StopThroughRunnerAsync(newer, "other");
        Assert.Equal(newer.Identity.Current, (await newer.Client.GetInfoAsync(Ct)).AcceptedMaster);

        // The pull finishes; the next destructive stage (create) re-checks fencing and stops the stale launch.
        await node.Agent.SetPullsHeldAsync(false);
        var ended = await UntilValueAsync(async () => await newer.Client.GetOperationAsync(queued.Id, Ct) is { } o && o.State.IsTerminal() ? o : null, "the stale launch to end");
        Assert.Equal(OperationState.Failed, ended.State);
        Assert.Equal(ErrorCodes.Superseded, ended.Error?.Code);
        Assert.Null(await newer.Client.GetRuntimeAsync("qwen", Ct));
        Assert.Empty(await newer.Client.ListRuntimesAsync(new RuntimesQuery(), Ct));
        await UntilAsync(async () => (await newer.Client.GetStatusAsync(Ct)).Reservations.Count == 0, "the stale reservation to be released");

        // The old master's start did not succeed (its 409 is the skipped test below) and created nothing.
        var (status, body) = await oldStart;
        Assert.False(status == HttpStatusCode.OK, $"{(int)status} {body}");
        Assert.Contains(ErrorCodes.Superseded, (await old.Get<IAgentOperationReferenceStore>().GetAsync(queued.Id, Ct))?.Error ?? "", StringComparison.Ordinal);

        // Its next command, sent through its runner, is refused stale_master, which turns its node Pending takeover (D52).
        Assert.Equal(NodeState.Online, old.Cache.GetNode(NodeId)?.State);
        var stale = await Assert.ThrowsAsync<AgentException>(() => StopThroughRunnerAsync(old, "other"));
        Assert.Equal(ErrorCodes.StaleMaster, stale.Code);
        var blocked = await old.WaitForNodeAsync(n => n.State == NodeState.PendingTakeover, "Pending takeover on the superseded master");
        Assert.False(blocked.IsServing);
        Assert.Empty(await newer.Client.ListRuntimesAsync(new RuntimesQuery(), Ct));
    }

    [Fact]
    public async Task AnAdminCommandRefusedAsStaleMasterAnswers503()
    {
        await using var node = await FakeNode.StartAsync(models: [Model("qwen"), Model("other")]);
        await using var old = await node.Master().StartAsync();
        string newerDir = await node.NewDataDirAsync(Model("qwen"), Model("other"));
        await RaiseEpochAsync(newerDir, 4);
        await using var newer = await node.Master(newerDir).StartAsync();
        await StopThroughRunnerAsync(newer, "other");
        Assert.Equal(NodeState.Online, old.Cache.GetNode(NodeId)?.State);

        var (status, body) = await SendAsync(old.Http.PostAsync("/api/models/other/stop", content: null, Ct));

        Assert.True(status == HttpStatusCode.ServiceUnavailable, $"{(int)status} {body}");
    }

    [Fact]
    public async Task AStartSupersededBeforeItsContainerExistedAnswers409AtTheAdminApi()
    {
        await using var node = await FakeNode.StartAsync(models: [Model("qwen", image: AgentContractSeed.PullImage), Model("other")]);
        await using var old = await node.Master().StartAsync();
        await node.Agent.SetPullsHeldAsync(true);
        var oldStart = SendAsync(old.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        await UntilAsync(async () => (await old.Client.ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }, Ct))
            .Any(o => o.Kind == OperationKinds.Launch && o.Stage == OperationStages.Pulling), "the launch to pull");
        string newerDir = await node.NewDataDirAsync(Model("other"));
        await RaiseEpochAsync(newerDir, 4);
        await using var newer = await node.Master(newerDir).StartAsync();
        await StopThroughRunnerAsync(newer, "other");
        await node.Agent.SetPullsHeldAsync(false);

        var (status, body) = await oldStart;

        Assert.True(status == HttpStatusCode.Conflict, $"{(int)status} {body}");
        Assert.Contains("on node dgx", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgentReturningWithAHigherEpochStaysPendingTakeoverUntilTheOperatorContinues()
    {
        await using var node = await FakeNode.StartAsync(models: [Model("qwen"), Model("other")]);

        // Another master (epoch 20) drove the node last, then went away.
        string otherDir = await node.NewDataDirAsync(Model("qwen"), Model("other"));
        await RaiseEpochAsync(otherDir, 19);
        await using (var other = await node.Master(otherDir).StartAsync())
        {
            await StopThroughRunnerAsync(other, "qwen");
            Assert.Equal(20, (await other.Client.GetInfoAsync(Ct)).AcceptedMaster?.Epoch);
        }

        // The node is offline while this master takes over: it is recorded unreachable (Pending takeover), epoch 2.
        node.Agent.SetPartitioned(true);
        await using var master = await node.Master().StartAsync(state: null);
        Assert.Equal(1, master.Identity.Epoch);
        var takeover = Takeover(master);
        var record = await takeover.TakeOverAsync("exit-test", Ct);
        Assert.Equal(2, record.Epoch);
        Assert.Equal([NodeId], record.Unreachable);
        await master.WaitForNodeAsync(n => n.State == NodeState.PendingTakeover, "Pending takeover while unreachable");
        Assert.Equal(NodeFencingState.PendingTakeover, Assert.Single(takeover.GetStatus().Nodes).State);

        // It returns holding epoch 20: Pending takeover, and it stays so — no automatic continuation.
        node.Agent.SetPartitioned(false);
        await master.Cache.RefreshNodeAsync(NodeId, Ct);
        var blocked = await master.WaitForNodeAsync(n => n.State == NodeState.PendingTakeover && n.AcceptedMaster?.Epoch == 20, "Pending takeover on return");
        var assessment = TakeoverRules.AssessAgent(master.Identity.Current, blocked.AcceptedMaster);
        Assert.Equal(ReturningAgentStatus.NeedsContinuation, assessment.Status);
        Assert.Equal(new NodeFencingStatus(NodeId, NodeFencingState.NeedsContinuation, 20), Assert.Single(takeover.GetStatus().Nodes));
        Assert.True(takeover.GetStatus().ActionNeeded);
        for (int i = 0; i < 3; i++)
        {
            await master.Cache.RefreshNodeAsync(NodeId, Ct);
            Assert.Equal(NodeState.PendingTakeover, master.Cache.GetNode(NodeId)?.State);
        }

        Assert.Equal(2, master.Identity.Epoch);
        var stale = await Assert.ThrowsAsync<AgentException>(() => StopThroughRunnerAsync(master, "qwen"));
        Assert.Equal(ErrorCodes.StaleMaster, stale.Code);
        var (refusedStatus, refusedBody) = await SendAsync(master.Http.PostAsync("/api/models/other/start", content: null, Ct));
        Assert.True(refusedStatus == HttpStatusCode.ServiceUnavailable, $"{(int)refusedStatus} {refusedBody}");

        // The operator continues the takeover for this node: epoch 21, the node accepts it on the next command.
        var continued = await takeover.ContinueAsync(NodeId, record.TakeoverId, 20, "exit-test", Ct);
        Assert.Equal(21, Assert.Single(continued.Continuations).NewEpoch);
        Assert.Equal(21, master.Identity.Epoch);
        await master.WaitForNodeAsync(n => n.State == NodeState.Online, "Online after the continuation");
        await StartAsync(master, "other");
        Assert.Equal(master.Identity.Current, (await master.Client.GetInfoAsync(Ct)).AcceptedMaster);
    }

    /// <summary>MN-115's takeover service over the master's composed identity, cache, registry and agent clients.</summary>
    private static TakeoverService Takeover(ExitMaster master) => new(
        master.Identity,
        master.Get<NodeStatusCache>(),
        master.Get<INodeRegistry>(),
        master.Get<IAgentClientFactory>(),
        master.Get<TimeProvider>(),
        audit: null,
        master.Get<IOptions<FencingOptions>>().Value);

    /// <summary>A start through the admin API that must succeed, then waits for Ready on the agent.</summary>
    private static async Task StartAsync(ExitMaster master, string modelId)
    {
        var (status, body) = await SendAsync(master.Http.PostAsync($"/api/models/{modelId}/start", content: null, Ct));
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        await UntilAsync(async () => (await master.Client.GetRuntimeAsync(modelId, Ct))?.State == ModelState.Ready, $"{modelId} Ready");
    }

    /// <summary>A stop mutation sent the way every master caller sends one (MN-028 …): through the composed runner.</summary>
    private static Task<OperationStatus> StopThroughRunnerAsync(ExitMaster master, string modelId) =>
        master.Get<AgentOperationRunner>().SubmitAsync(NodeId, AgentRoutes.RuntimeStop, modelId, AgentContractSuite.Stop(),
            (client, request, ct) => client.StopAsync(modelId, request, ct), owner: null, Ct);
}
