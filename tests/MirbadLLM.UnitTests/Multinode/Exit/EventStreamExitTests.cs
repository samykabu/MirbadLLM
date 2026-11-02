using System.Net;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Multinode.Fakes;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// Design §13.6 "event stream gap and overflow → snapshot + resume without lost or doubled state" (§4.7), driven through
/// the master composition's node monitor and status cache against a FakeAgent whose ring or subscriber queue is tiny: the
/// master's view of the node converges to the agent's own snapshot (same instances, states and seq), and commands keep
/// working.
/// </summary>
public sealed class EventStreamExitTests
{
    [Fact]
    public async Task WhenEveryEventOverflowsTheMasterResnapshotsAndConverges()
    {
        // A subscriber queue of 0: every state event overflows the stream, so the master learns state only from snapshots.
        await using var node = await FakeNode.StartAsync(new FakeAgentOptions { SubscriberQueueCapacity = 0 }, Model("qwen"), Model("other"));
        await using var master = await node.Master().StartAsync();

        // The agent really overflows a subscriber on its first state event.
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var info = await master.Client.GetInfoAsync(Ct);
        var firstEvent = FirstStateEventAsync(master, new EventsQuery { BootId = info.BootId, AfterSeq = (await master.Client.GetStatusAsync(Ct)).Seq }, probe.Token);

        await StartAsync(master, "qwen");
        Assert.IsType<OverflowEvent>(await firstEvent.WaitAsync(Patience, Ct));
        await master.WaitForModelAsync("qwen", ModelState.Ready);

        await StartAsync(master, "other");
        await master.WaitForModelAsync("other", ModelState.Ready);
        var (stop, stopBody) = await SendAsync(master.Http.PostAsync("/api/models/qwen/stop", content: null, Ct));
        Assert.True(stop == HttpStatusCode.NoContent, $"{(int)stop} {stopBody}");
        await UntilAsync(() => master.Cache.GetRuntime("qwen") is null, "qwen to leave the master's cache");

        await AssertConvergedAsync(master);
        Assert.Equal(NodeState.Online, master.Cache.GetNode(NodeId)?.State);
        await probe.CancelAsync();
    }

    [Fact]
    public async Task AGapLongerThanTheReplayRingIsResetAndTheMasterResnapshots()
    {
        // A ring of 2 events: a cursor that falls behind it cannot be replayed (§4.7: reset, then snapshot).
        await using var node = await FakeNode.StartAsync(new FakeAgentOptions { EventRingCapacity = 2 }, Model("qwen"), Model("other"));
        await using var master = await node.Master().StartAsync();
        await node.Agent.SetStartsHeldAsync(true);
        var (status, body) = await SendAsync(master.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        await master.WaitForModelAsync("qwen", ModelState.Starting);
        string launchId = Assert.Single(await master.Client.ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }, Ct)).Id;
        long cursor = master.Cache.GetNode(NodeId)!.Seq ?? 0;
        string boot = (await master.Client.GetInfoAsync(Ct)).BootId;

        // The master loses the stream while the launch finishes (ready stage, reservation released, runtime Ready,
        // operation succeeded: more events than the ring holds); the agent's durable log shows when it is done.
        node.Agent.SetPartitioned(true);
        await node.Agent.SetStartsHeldAsync(false);
        await UntilAsync(() => LaunchSucceededInLog(node.Agent, launchId), "the launch to finish while partitioned");
        Assert.Equal(ModelState.Starting, master.Cache.GetRuntime("qwen")?.State);
        node.Agent.SetPartitioned(false);
        await UntilAsync(async () =>
        {
            try
            {
                return (await master.Client.GetStatusAsync(Ct)).Seq > cursor + 2;
            }
            catch (AgentUnreachableException)
            {
                return false;
            }
        }, "the agent to answer again");

        // The cursor the master held is gone from the ring: a subscription from it is reset.
        using (var probe = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var replay = await FirstStateEventAsync(master, new EventsQuery { BootId = boot, AfterSeq = cursor }, probe.Token).WaitAsync(Patience, Ct);
            Assert.IsType<ResetEvent>(replay);
            await probe.CancelAsync();
        }

        // The master converges anyway (snapshot after the gap) and keeps commanding the node.
        await master.WaitForModelAsync("qwen", ModelState.Ready);
        await StartAsync(master, "other");
        await master.WaitForModelAsync("other", ModelState.Ready);
        await AssertConvergedAsync(master);
    }

    /// <summary>The master's cache equals the agent's own snapshot: same instances and states, seq caught up, nothing doubled.</summary>
    private static async Task AssertConvergedAsync(ExitMaster master)
    {
        await UntilAsync(async () =>
        {
            var snapshot = await master.Client.GetStatusAsync(Ct);
            var cached = master.Cache.ListRuntimes(NodeId);
            return snapshot.Operations.All(o => o.State.IsTerminal()) &&
                cached.Count == snapshot.Runtimes.Count &&
                snapshot.Runtimes.All(r => cached.Any(c => c.ModelId == r.ModelId && c.State == r.State && c.InstanceId == r.InstanceId)) &&
                master.Cache.GetNode(NodeId)!.Seq >= snapshot.Seq;
        }, "the master's cache to equal the agent's snapshot");
        var runtimes = master.Cache.ListRuntimes(NodeId);
        Assert.Equal(runtimes.Count, runtimes.Select(r => r.ModelId).Distinct().Count());
        Assert.Empty(master.Cache.GetNode(NodeId)!.OpenOperations);
    }

    /// <summary>The FakeAgent's operation log (<c>operations/log.jsonl</c>) holds the launch's terminal Succeeded entry.</summary>
    private static bool LaunchSucceededInLog(FakeAgent agent, string operationId)
    {
        string path = Path.Combine(agent.StateDirectory, OperationRetention.LogRelativePath);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Contains(operationId, StringComparison.Ordinal) && line.Contains("\"completed\"", StringComparison.Ordinal) &&
                line.Contains("succeeded", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task StartAsync(ExitMaster master, string modelId)
    {
        var (status, body) = await SendAsync(master.Http.PostAsync($"/api/models/{modelId}/start", content: null, Ct));
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
    }

    /// <summary>The first event other than a heartbeat on a subscription of the master's own client.</summary>
    private static async Task<AgentEvent> FirstStateEventAsync(ExitMaster master, EventsQuery query, CancellationToken ct)
    {
        await foreach (var evt in master.Client.SubscribeEventsAsync(query, ct))
        {
            if (evt is not HeartbeatEvent)
            {
                return evt;
            }
        }

        throw new InvalidOperationException("The event stream ended without a state event.");
    }
}
