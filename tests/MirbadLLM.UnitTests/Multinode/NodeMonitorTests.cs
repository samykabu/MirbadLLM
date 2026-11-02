using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;

#pragma warning disable xUnit1051 // helpers pass the test token where it matters.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-027: <see cref="NodeMonitor"/> and <see cref="NodeStatusCache"/> against <see cref="FakeAgent"/> over real mTLS:
/// negotiation, Incompatible, Offline after 15 s of silence, re-snapshot on gap/overflow/new boot, stale-master takeover
/// state, operation references resumed after a snapshot, served-name resolution. The monitor's clock is manual (its
/// backoffs, polls and the liveness loop only move when a test advances it); the agent and the transport use real time.
/// </summary>
public sealed class NodeMonitorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Negotiation and node states ----

    [Fact]
    public async Task ANodeIsOnlineAfterNegotiationWithItsSnapshotApplied()
    {
        await using var env = await MonitorEnv.StartAsync();

        var node = await env.WaitForAsync(n => n.State == NodeState.Online, "Online");

        Assert.Equal(Protocol.CurrentVersion, node.SelectedProtocol);
        Assert.Equal(env.Agent.BootId, node.BootId);
        Assert.Equal(env.Agent.NodeUid, node.NodeUid);
        Assert.Equal("0.0.0-fake", node.AgentVersion);
        Assert.NotNull(node.Memory);
        Assert.NotNull(node.Capabilities);
        Assert.Empty(node.Reasons);
        Assert.True(node.IsServing);
        Assert.Equal(Protocol.CurrentVersion, env.Factory.GetNegotiatedProtocol(MonitorEnv.NodeId));
        // The stream resumes from the snapshot watermark (design §4.7).
        await EventuallyAsync(() => Task.FromResult(env.Taps.Subscriptions.Count > 0), "the subscription");
        var snapshot = Assert.Single(env.Taps.Snapshots);
        var subscription = Assert.Single(env.Taps.Subscriptions);
        Assert.Equal(snapshot.BootId, subscription.BootId);
        Assert.Equal(snapshot.Seq, subscription.AfterSeq);
        // Last contact and capabilities are written back to the registry for an Offline node's page.
        await EventuallyAsync(async () => (await env.Registry.GetAsync(MonitorEnv.NodeId, Ct))?.LastCapabilities is not null, "the registry write");
        Assert.NotNull((await env.Registry.GetAsync(MonitorEnv.NodeId, Ct))?.LastContact);
    }

    [Fact]
    public async Task ANodeWithoutACommonProtocolVersionIsIncompatibleAndOnlyInfoIsCalled()
    {
        var future = new ProtocolRange(Protocol.CurrentVersion + 1, Protocol.CurrentVersion + 1);
        await using var env = await MonitorEnv.StartAsync(new FakeAgentOptions { Protocol = future });
        env.Cache.SetModels([MonitorEnv.Model("model-a")]);

        var node = await env.WaitForAsync(n => n.State == NodeState.Incompatible, "Incompatible");

        Assert.Null(node.SelectedProtocol);
        Assert.Equal(future, node.AgentProtocol);
        Assert.Contains(node.Reasons, r => r.Contains("No common protocol version", StringComparison.Ordinal));
        Assert.False(node.IsServing);
        Assert.Equal(0, env.Taps.StatusCalls);
        Assert.Empty(env.Taps.Subscriptions);
        // Blocked for routing (design §3.5): the router sees the node state.
        var route = env.Cache.ResolveServedName("model-a");
        Assert.Equal(NodeState.Incompatible, route?.NodeState);
    }

    [Fact]
    public async Task AnAgentReportingAnotherUidIsBlocked()
    {
        await using var env = await MonitorEnv.StartAsync(recordUid: "0123456789abcdef0123456789abcdef");

        var node = await env.WaitForAsync(n => n.State == NodeState.Incompatible, "identity mismatch");

        Assert.Contains(node.Reasons, r => r.Contains("Identity mismatch", StringComparison.Ordinal));
        Assert.Equal(0, env.Taps.StatusCalls);
    }

    [Fact]
    public async Task ANodeWaitingForApprovalGetsOnlyInfo()
    {
        await using var env = await MonitorEnv.StartAsync(approved: false);

        await env.WaitForAsync(_ => env.Taps.InfoCalls > 0, "the first /info");
        var node = env.Cache.GetNode(MonitorEnv.NodeId)!;

        Assert.Equal(NodeState.PendingApproval, node.State);
        Assert.Equal(0, env.Taps.StatusCalls);
        Assert.Empty(env.Taps.Subscriptions);
    }

    [Fact]
    public async Task AgentHealthReasonsMakeTheNodeDegraded()
    {
        await using var env = await MonitorEnv.StartAsync(start: false);
        env.Agent.SetDockerAvailable(false);
        await env.StartMonitorAsync();

        var node = await env.WaitForAsync(n => n.State == NodeState.Degraded, "Degraded");

        Assert.Contains("Docker does not answer.", node.Reasons);
        Assert.True(node.IsServing);
    }

    [Fact]
    public async Task ClockSkewAndAnExpiringCertificateAreMasterComputedDegradedReasons()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(1);
        await using var env = await MonitorEnv.StartAsync(clockStart: start, certNotAfter: start + TimeSpan.FromDays(3));

        var node = await env.WaitForAsync(n => n.State == NodeState.Degraded, "Degraded");

        Assert.Contains(node.Reasons, r => r.StartsWith("Clock skew of", StringComparison.Ordinal));
        Assert.Contains(node.Reasons, r => r.StartsWith("The agent certificate expires", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FifteenSecondsOfSilenceMakeTheNodeOfflineAndContactRestoresIt()
    {
        await using var env = await MonitorEnv.StartAsync();
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");

        env.Agent.SetPartitioned(true);
        await env.Clock.WaitForOneShotAsync(Ct); // the dropped stream waits for its reconnect backoff
        Task tick = env.Clock.WaitForPeriodicTickAsync(Ct);
        env.Clock.Advance(TimeSpan.FromSeconds(14));
        await tick;
        Assert.NotEqual(NodeState.Offline, env.Cache.GetNode(MonitorEnv.NodeId)!.State); // 14 s: still within the window
        env.Clock.Advance(TimeSpan.FromSeconds(2));

        var offline = await env.WaitForAsync(n => n.State == NodeState.Offline, "Offline");
        Assert.Contains(offline.Reasons, r => r.StartsWith("No contact for more than 15 s", StringComparison.Ordinal));
        Assert.False(offline.IsServing);

        env.Agent.SetPartitioned(false);
        var online = await env.PumpUntilAsync(n => n.State == NodeState.Online, "Online again");
        Assert.Empty(online.Reasons);
    }

    [Fact]
    public async Task TheOfflineRuleIsExactlyFifteenSeconds()
    {
        await using var env = await MonitorEnv.StartAsync(start: false);
        env.Cache.SetNodes(await env.Registry.ListAsync(Ct));
        Assert.Equal(NodeState.Offline, env.Cache.GetNode(MonitorEnv.NodeId)!.State); // nothing synchronized yet

        await env.Cache.SynchronizeAsync(MonitorEnv.NodeId, Ct);
        Assert.Equal(NodeState.Online, env.Cache.GetNode(MonitorEnv.NodeId)!.State);

        env.Clock.Advance(Protocol.OfflineAfter);
        env.Cache.EvaluateLiveness();
        Assert.Equal(NodeState.Online, env.Cache.GetNode(MonitorEnv.NodeId)!.State);

        env.Clock.Advance(TimeSpan.FromMilliseconds(1));
        env.Cache.EvaluateLiveness();
        Assert.Equal(NodeState.Offline, env.Cache.GetNode(MonitorEnv.NodeId)!.State);
    }

    // ---- Events: order, gaps, overflow, new boot ----

    [Fact]
    public async Task EventsFollowTheSeqAndBootRules()
    {
        await using var env = await MonitorEnv.StartAsync(start: false);
        env.Cache.SetNodes(await env.Registry.ListAsync(Ct));
        var sync = await env.Cache.SynchronizeAsync(MonitorEnv.NodeId, Ct);
        string boot = sync.BootId!;
        long seq = sync.Seq;
        DateTimeOffset now = env.Clock.GetUtcNow();
        OperationEvent Op(long s, OperationState state) => new()
        {
            BootId = boot,
            Seq = s,
            At = now,
            Operation = new OperationStatus { Id = "op-x", Kind = OperationKinds.Stop, Target = "model-x", PayloadDigest = "d", State = state, StartedAt = now },
        };

        Assert.Equal(NodeEventResult.Ignored, env.Cache.ApplyEvent(MonitorEnv.NodeId, new HeartbeatEvent { BootId = boot, Seq = seq }));
        Assert.Equal(NodeEventResult.Applied, env.Cache.ApplyEvent(MonitorEnv.NodeId, Op(seq + 1, OperationState.Running)));
        Assert.Contains(env.Cache.GetNode(MonitorEnv.NodeId)!.OpenOperations, o => o.OperationId == "op-x");
        Assert.Equal(NodeEventResult.Ignored, env.Cache.ApplyEvent(MonitorEnv.NodeId, Op(seq + 1, OperationState.Running))); // replayed twice
        Assert.Equal(NodeEventResult.Applied, env.Cache.ApplyEvent(MonitorEnv.NodeId, Op(seq + 2, OperationState.Succeeded)));
        Assert.Empty(env.Cache.GetNode(MonitorEnv.NodeId)!.OpenOperations);
        // An ended operation is never re-opened by a later event (W2 review MN-092).
        Assert.Equal(NodeEventResult.Applied, env.Cache.ApplyEvent(MonitorEnv.NodeId, Op(seq + 3, OperationState.Running)));
        Assert.Empty(env.Cache.GetNode(MonitorEnv.NodeId)!.OpenOperations);

        // Gap, heartbeat above the cursor, another boot, reset and overflow: re-snapshot, cursor unchanged.
        Assert.Equal(NodeEventResult.Resync, env.Cache.ApplyEvent(MonitorEnv.NodeId, Op(seq + 5, OperationState.Running)));
        Assert.Equal(NodeEventResult.Resync, env.Cache.ApplyEvent(MonitorEnv.NodeId, new HeartbeatEvent { BootId = boot, Seq = seq + 4 }));
        Assert.Equal(NodeEventResult.Resync, env.Cache.ApplyEvent(MonitorEnv.NodeId, new HeartbeatEvent { BootId = "other-boot", Seq = 1 }));
        Assert.Equal(NodeEventResult.Resync, env.Cache.ApplyEvent(MonitorEnv.NodeId, new ResetEvent { BootId = boot, Seq = seq + 9, Reason = ResetEvent.Evicted }));
        Assert.Equal(NodeEventResult.Resync, env.Cache.ApplyEvent(MonitorEnv.NodeId, new OverflowEvent { BootId = boot, Seq = seq + 9 }));
        Assert.Equal(seq + 3, env.Cache.GetNode(MonitorEnv.NodeId)!.Seq);
    }

    [Fact]
    public async Task AGapInTheStreamTriggersANewSnapshot()
    {
        await using var env = await MonitorEnv.StartAsync();
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");
        int dropped = 0;
        env.Taps.Drop = e => e is OperationEvent && Interlocked.Exchange(ref dropped, 1) == 0; // lose the first state event

        await env.StopAsync("model-a");
        await env.StopAsync("model-b");

        // The tap counts a snapshot when /status answers, before the cache applies it; the cache already looks settled
        // then (the gap left no open operation), so wait until the cached cursor reached the second snapshot (MN-047).
        var node = await env.PumpUntilAsync(
            n => env.Taps.Snapshots is { Count: >= 2 } snapshots && n.Seq >= snapshots[1].Seq && n.OpenOperations.Count == 0 && n.State == NodeState.Online,
            "a re-snapshot");
        Assert.Equal(1, Volatile.Read(ref dropped));
        await EventuallyAsync(() => Task.FromResult(env.Taps.Subscriptions.Count >= 2), "the second subscription");
        Assert.Equal(env.Taps.Snapshots[1].Seq, env.Taps.Subscriptions[1].AfterSeq);
        Assert.True(node.Seq >= env.Taps.Snapshots[1].Seq);
    }

    [Fact]
    public async Task AnOverflowTriggersANewSnapshotAndNeverMovesTheCursor()
    {
        await using var env = await MonitorEnv.StartAsync(new FakeAgentOptions { SubscriberQueueCapacity = 0 });
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");
        // Live events, not a replay: the agent must have registered the subscriber (the tap records the query before the
        // request reaches it), else the stop's events are replayed from the ring and nothing overflows.
        await EventuallyAsync(() => Task.FromResult(env.Agent.SubscriberCount > 0), "the agent-side subscription");

        await env.StopAsync("model-a");

        await env.PumpUntilAsync(n => env.Taps.StatusCalls >= 2 && n.OpenOperations.Count == 0, "a re-snapshot after overflow");
        Assert.Contains(env.Taps.Seen, e => e is OverflowEvent);
        // Every subscription resumed from a snapshot's seq, never from the overflow's.
        for (int i = 0; i < env.Taps.Subscriptions.Count; i++)
        {
            Assert.Equal(env.Taps.Snapshots[i].Seq, env.Taps.Subscriptions[i].AfterSeq);
        }
    }

    [Fact]
    public async Task AHealthEventTurnsTheNodeDegradedAndBackWithoutANewSnapshot()
    {
        // MN-047 / D68: health changes arrive as events while the stream stays up.
        await using var env = await MonitorEnv.StartAsync();
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");
        await EventuallyAsync(() => Task.FromResult(env.Taps.Subscriptions.Count > 0), "the subscription");
        int snapshots = env.Taps.StatusCalls;

        env.Agent.SetDockerAvailable(false);
        var degraded = await env.PumpUntilAsync(n => n.State == NodeState.Degraded, "Degraded");
        Assert.Contains("Docker does not answer.", degraded.Reasons);

        env.Agent.SetDockerAvailable(true);
        var online = await env.PumpUntilAsync(n => n.State == NodeState.Online, "Online again");
        Assert.Empty(online.Reasons);
        Assert.Equal(snapshots, env.Taps.StatusCalls);
    }

    [Fact]
    public async Task ARestartedAgentIsANewBootAndIsSnapshottedAgain()
    {
        await using var env = await MonitorEnv.StartAsync();
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");
        await env.LaunchAsync("model-a");
        await env.WaitForAsync(_ => env.Cache.GetRuntime("model-a")?.State == ModelState.Ready, "model-a Ready");
        string oldBoot = env.Agent.BootId;
        int snapshots = env.Taps.StatusCalls;

        await env.Agent.RestartAsync();

        var node = await env.PumpUntilAsync(n => n.BootId == env.Agent.BootId && n.State == NodeState.Online, "the new boot");
        Assert.NotEqual(oldBoot, node.BootId);
        Assert.True(env.Taps.StatusCalls > snapshots);
        await EventuallyAsync(() => Task.FromResult(env.Taps.Subscriptions[^1].BootId == node.BootId), "a subscription to the new boot");
        // Instances keep running across an agent restart and are in the new snapshot.
        Assert.Equal(ModelState.Ready, env.Cache.GetRuntime("model-a")?.State);
    }

    [Fact]
    public async Task AHostRebootRestoresTheObservedRunningModelButNotOneExplicitlyStopped()
    {
        var models = new InMemoryModels([MonitorEnv.Model("model-a"), MonitorEnv.Model("model-b")]);
        await using var env = await MonitorEnv.StartAsync(models: models);
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");
        await env.LaunchAsync("model-a");
        await env.WaitForAsync(_ => env.Cache.GetRuntime("model-a")?.State == ModelState.Ready, "model-a Ready");
        await env.LaunchAsync("model-b");
        await env.WaitForAsync(_ => env.Cache.GetRuntime("model-b")?.State == ModelState.Ready, "model-b Ready");
        await env.StopAsync("model-b");
        await env.WaitForAsync(_ => env.Cache.GetRuntime("model-b") is null, "model-b stopped");

        var options = Options.Create(new MirbadLLMOptions { HealthPollInterval = TimeSpan.FromMilliseconds(10), StartupTimeout = TimeSpan.FromSeconds(5) });
        var runtime = new RemoteModelRuntime(models, env.Registry, env.Cache, env.Taps, env.Runner, options, recovery: env.Recovery);
        using var health = new ModelHealthMonitor(models, runtime, options, NullLogger<ModelHealthMonitor>.Instance,
            recovery: env.Recovery, nodes: env.Cache);
        await health.StartAsync(Ct);
        try
        {
            string boot = env.Agent.BootId;
            await env.Agent.RestartAsync(loseRuntimes: true);
            await env.PumpUntilAsync(n => n.BootId != boot && n.State == NodeState.Online, "the rebooted host");
            await env.WaitForAsync(_ => env.Cache.GetRuntime("model-a")?.State == ModelState.Ready, "model-a automatically restored");
            Assert.Null(env.Cache.GetRuntime("model-b"));
            Assert.Equal(3, env.Agent.LaunchRequests.Count); // Two original starts and one recovery, without AutoStart flags.

            await env.Agent.RestartAsync(); // Agent service upgrades adopt the live instance instead of starting another.
            await env.PumpUntilAsync(n => n.BootId == env.Agent.BootId && n.State == NodeState.Online, "the adopted instance");
            Assert.Equal(3, env.Agent.LaunchRequests.Count);
        }
        finally
        {
            await health.StopAsync(CancellationToken.None);
        }
    }

    // ---- Fencing ----

    [Fact]
    public async Task AnAgentThatAcceptedAHigherEpochBlocksTheNodeUntilTheMasterTakesOver()
    {
        await using var env = await MonitorEnv.StartAsync(start: false);
        await env.StopAsAnotherMasterAsync(epoch: 10);
        await env.StartMonitorAsync();

        var node = await env.WaitForAsync(n => n.State == NodeState.PendingTakeover, "PendingTakeover");

        Assert.Contains(node.Reasons, r => r.StartsWith("Stale master", StringComparison.Ordinal));
        Assert.Equal(10, node.AcceptedMaster?.Epoch);
        Assert.False(node.IsServing);
        Assert.Equal(0, env.Taps.StatusCalls);

        env.Identity.Epoch = 11; // an operator-confirmed takeover raised the epoch (design §4.2)
        await env.Cache.RefreshNodeAsync(MonitorEnv.NodeId, Ct);

        Assert.Equal(NodeState.Online, env.Cache.GetNode(MonitorEnv.NodeId)!.State);
        await EventuallyAsync(() => Task.FromResult(env.Taps.Subscriptions.Count > 0), "the event stream after the takeover");
    }

    [Fact]
    public async Task AStaleMasterAnswerToAMutationSentThroughTheRunnerReachesTheCacheWithoutTheCaller()
    {
        // W3 review MN-094: callers (MN-028, MN-032) submit through AgentOperationRunner and never call ReportAgentError;
        // the monitor wires the runner's observer to the cache.
        await using var env = await MonitorEnv.StartAsync();
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");
        await env.StopAsAnotherMasterAsync(epoch: 10);

        var refusal = await Assert.ThrowsAsync<AgentException>(() => env.Runner.SubmitAsync(
            MonitorEnv.NodeId, AgentRoutes.RuntimeStop, "model-a", AgentContractSuite.Stop(),
            (client, request, ct) => client.StopAsync("model-a", request, ct), owner: null, TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.StaleMaster, refusal.Code);
        var node = await env.WaitForAsync(n => n.State == NodeState.PendingTakeover, "PendingTakeover");
        Assert.False(node.IsServing);
    }

    [Fact]
    public async Task AStaleMasterAnswerToAMutationSurfacesATakeoverNeededState()
    {
        await using var env = await MonitorEnv.StartAsync();
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");
        await env.StopAsAnotherMasterAsync(epoch: 10);

        var refusal = await Assert.ThrowsAsync<AgentException>(() => env.StopAsync("model-a"));
        Assert.Equal(ErrorCodes.StaleMaster, refusal.Code);
        Assert.True(env.Cache.ReportAgentError(MonitorEnv.NodeId, refusal));

        var node = await env.WaitForAsync(n => n.State == NodeState.PendingTakeover, "PendingTakeover");
        Assert.Contains(node.Reasons, r => r.Contains(ErrorCodes.StaleMaster, StringComparison.Ordinal) || r.StartsWith("Stale master", StringComparison.Ordinal));
        var route = env.Cache.ResolveServedName("model-a");
        Assert.Null(route); // no models registered in this test; the node itself is blocked:
        Assert.False(env.Cache.GetNode(MonitorEnv.NodeId)!.IsServing);
        Assert.False(env.Cache.ReportAgentError(MonitorEnv.NodeId, new AgentException(404, new AgentError { Code = ErrorCodes.NotFound, Message = "x" })));
    }

    [Fact]
    public void FencingComparesTheAcceptedTripleWithTheMastersOwn()
    {
        var master = new MasterTriple("me", 5, "boot-1");

        Assert.Null(NodeStatusCache.FencingBlock(master, null));
        Assert.Null(NodeStatusCache.FencingBlock(master, new MasterTriple("me", 4, "boot-0")));
        Assert.Null(NodeStatusCache.FencingBlock(master, master));
        Assert.StartsWith("Stale master", NodeStatusCache.FencingBlock(master, new MasterTriple("other", 6, "b")), StringComparison.Ordinal);
        Assert.StartsWith("Competing master", NodeStatusCache.FencingBlock(master, new MasterTriple("other", 5, "b")), StringComparison.Ordinal);
        Assert.StartsWith("Competing master", NodeStatusCache.FencingBlock(master, new MasterTriple("me", 5, "boot-2")), StringComparison.Ordinal);
    }

    // ---- Operation references ----

    [Fact]
    public async Task OpenOperationReferencesAreResumedAfterTheSnapshot()
    {
        await using var env = await MonitorEnv.StartAsync(start: false);
        StopRequest stop = AgentContractSuite.Stop();
        OperationStatus submitted = await env.Runner.SubmitAsync(
            MonitorEnv.NodeId, AgentRoutes.RuntimeStop, "model-a", stop, (c, r, t) => c.StopAsync("model-a", r, t), ct: Ct);
        Assert.Equal(stop.OperationId, submitted.Id);
        DateTimeOffset now = env.Clock.GetUtcNow();
        var lost = new AgentOperationReference
        {
            OperationId = "never-arrived",
            NodeId = MonitorEnv.NodeId,
            NodeUid = env.Agent.NodeUid,
            Action = OperationKinds.Stop,
            Target = "model-b",
            PayloadDigest = "digest",
            IssuedAt = now - TimeSpan.FromHours(24),
            ExpiresAt = now - TimeSpan.FromSeconds(1), // expired, but still within the closed-reference retention
            CreatedAt = now - TimeSpan.FromHours(24),
        };
        await env.References.AddAsync(lost, Ct);
        Assert.NotEmpty(await env.References.ListOpenAsync(Ct));

        await env.StartMonitorAsync();

        await EventuallyAsync(async () => (await env.References.ListOpenAsync(Ct)).Count == 0, "every reference closed");
        Assert.Equal(AgentOperationOutcome.Succeeded, (await env.References.GetAsync(stop.OperationId, Ct))?.Outcome);
        Assert.Equal(AgentOperationOutcome.Expired, (await env.References.GetAsync(lost.OperationId, Ct))?.Outcome);
    }

    [Fact]
    public async Task ATerminalOperationEventClosesItsReference()
    {
        await using var env = await MonitorEnv.StartAsync(agentOptions: new FakeAgentOptions { StopDuration = TimeSpan.FromMilliseconds(200) });
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");
        await env.LaunchAsync("model-a");
        await env.WaitForAsync(_ => env.Cache.GetRuntime("model-a")?.State == ModelState.Ready, "model-a Ready");

        StopRequest stop = AgentContractSuite.Stop();
        await env.Runner.SubmitAsync(MonitorEnv.NodeId, AgentRoutes.RuntimeStop, "model-a", stop, (c, r, t) => c.StopAsync("model-a", r, t), ct: Ct);

        await EventuallyAsync(async () => (await env.References.GetAsync(stop.OperationId, Ct))?.Status == AgentOperationReferenceStatus.Closed, "the reference closed");
        Assert.Equal(AgentOperationOutcome.Succeeded, (await env.References.GetAsync(stop.OperationId, Ct))?.Outcome);
        await env.WaitForAsync(_ => env.Cache.GetRuntime("model-a") is null, "model-a gone");
    }

    // ---- Served names ----

    [Fact]
    public async Task AServedNameResolvesToItsPlacedModelNodeAndStates()
    {
        var models = new InMemoryModels([MonitorEnv.Model("model-a"), MonitorEnv.Model("model-b") with { Placement = null }]);
        await using var env = await MonitorEnv.StartAsync(models: models);
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");

        var stopped = env.Cache.ResolveServedName("model-a");
        Assert.Equal(new ServedModelRoute("model-a", MonitorEnv.NodeId, ModelState.Stopped, NodeState.Online, false, stopped!.UpdatedAt), stopped);

        await env.LaunchAsync("model-a");
        await env.WaitForAsync(_ => env.Cache.ResolveServedName("MODEL-A")?.ModelState == ModelState.Ready, "model-a Ready");

        var ready = env.Cache.ResolveServedName("Model-A")!;
        Assert.Equal("model-a", ready.ModelId);
        Assert.Equal(NodeState.Online, ready.NodeState);
        Assert.Null(env.Cache.ResolveServedName("model-b")); // unplaced: the router's own 503
        Assert.Null(env.Cache.ResolveServedName("nope"));
        var runtime = Assert.Single(env.Cache.ListRuntimes(MonitorEnv.NodeId), r => r.ModelId == "model-a");
        Assert.Equal("model-a", runtime.ModelId);
        Assert.Equal(MonitorEnv.NodeId, runtime.NodeId);
        Assert.NotNull(runtime.OperationId);
        Assert.Equal(runtime, env.Cache.GetRuntime("model-a"));

        await env.Cache.RefreshModelAsync("model-a", Ct);
        Assert.Equal(ModelState.Ready, env.Cache.GetRuntime("model-a")?.State);
    }

    [Fact]
    public async Task APlacementOnAGpuTheNodeDoesNotReportIsDegradedPlacedGpuMissingUntilItIsReselected()
    {
        // MN-124 (D73, design §5.1): master-computed from the placements and the node's last capability report.
        var gpus = FakeDiscreteGpu.TwoBy16;
        await using var env = await MonitorEnv.StartAsync(new FakeAgentOptions { DiscreteGpus = gpus });
        await env.WaitForAsync(n => n.State == NodeState.Online && n.Capabilities?.Gpus.Count == 2, "Online with two GPUs");
        const string gone = "GPU-00000000-0000-0000-0000-00000000dead";

        env.Cache.SetModels([MonitorEnv.Model("model-a") with { Placement = new ModelPlacement(MonitorEnv.NodeId, [gone], [0]) }]);
        var degraded = await env.WaitForAsync(n => n.State == NodeState.Degraded, "Degraded");
        string reason = Assert.Single(degraded.Reasons);
        Assert.StartsWith(HealthReasonCodes.PlacedGpuMissing + ": model 'model-a'", reason, StringComparison.Ordinal);
        Assert.Contains(gone, reason, StringComparison.Ordinal);
        Assert.True(degraded.IsServing); // flagged, still serving the other models

        // Re-selected onto a card the node reports: Online again without a new snapshot.
        env.Cache.SetModels([MonitorEnv.Model("model-a") with { Placement = new ModelPlacement(MonitorEnv.NodeId, [gpus[1].Uuid], [1]) }]);
        var online = await env.WaitForAsync(n => n.State == NodeState.Online, "Online after re-selecting");
        Assert.Empty(online.Reasons);
    }

    [Fact]
    public async Task CapabilitiesThatChangeAfterTheSnapshotAreReadAgainAtTheRefreshIntervalWithoutANewSnapshot()
    {
        // macOS Phase 3: an executable finishing its first verification changes the report without any event, so the
        // master re-reads it at a heartbeat once CapabilityRefreshInterval (60 s) has passed, and stores it in the registry.
        await using var env = await MonitorEnv.StartAsync(new FakeAgentOptions
        {
            DiscreteGpus = FakeDiscreteGpu.TwoBy16,
            HeartbeatInterval = TimeSpan.FromMilliseconds(100),
        });
        await env.WaitForAsync(n => n.State == NodeState.Online && n.Capabilities?.Gpus.Count == 2, "Online with two GPUs");
        await EventuallyAsync(() => Task.FromResult(env.Taps.Subscriptions.Count > 0), "the subscription");
        int snapshots = env.Taps.StatusCalls;

        env.Agent.AddGpu(new FakeDiscreteGpu("GPU-fake-d002-0000-0000-000000000000"));
        await Task.Delay(500, Ct); // heartbeats arrive; the interval has not passed on the master's clock
        Assert.Equal(2, env.Cache.GetNode(MonitorEnv.NodeId)!.Capabilities!.Gpus.Count);

        env.Clock.Advance(TimeSpan.FromSeconds(61));
        await env.WaitForAsync(n => n.Capabilities?.Gpus.Count == 3, "the refreshed report");
        await EventuallyAsync(
            async () => (await env.Registry.GetAsync(MonitorEnv.NodeId, Ct))?.LastCapabilities?.Gpus.Count == 3, "the registry write");
        Assert.Equal(snapshots, env.Taps.StatusCalls);
    }

    [Fact]
    public async Task ARevokedNodeLeavesTheCache()
    {
        await using var env = await MonitorEnv.StartAsync();
        await env.WaitForAsync(n => n.State == NodeState.Online, "Online");

        await env.Registry.RevokeAsync(MonitorEnv.NodeId, Ct);
        await env.Monitor.SyncRegistryAsync(Ct);

        Assert.Null(env.Cache.GetNode(MonitorEnv.NodeId));
        Assert.Empty(env.Cache.ListNodes());
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(10, Ct);
        }
    }
}

/// <summary>FakeAgent + registry + client factory (with taps) + cache + monitor on a manual clock.</summary>
file sealed class MonitorEnv : IAsyncDisposable
{
    public const string NodeId = "dgx";

    private readonly string _dataDir;
    private IssuedCredential _master = null!;
    private TrustAnchorSet _trust = null!;
    private bool _monitorStarted;

    private MonitorEnv(DateTimeOffset clockStart)
    {
        Clock = new ManualClock(clockStart);
        _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-monitor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataDir);
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataDir });
        Registry = new JsonNodeRegistry(options, TimeProvider.System);
        References = new JsonAgentOperationReferenceStore(options, Clock);
        Recovery = new ModelRecoveryState(options);
    }

    public ManualClock Clock { get; }

    public FakeAgent Agent { get; private set; } = null!;

    public JsonNodeRegistry Registry { get; }

    public JsonAgentOperationReferenceStore References { get; }

    public ModelRecoveryState Recovery { get; }

    public MonitorIdentity Identity { get; } = new();

    public AgentClientFactory Factory { get; private set; } = null!;

    public TapFactory Taps { get; private set; } = null!;

    public NodeStatusCache Cache { get; private set; } = null!;

    public NodeMonitor Monitor { get; private set; } = null!;

    public AgentOperationRunner Runner { get; private set; } = null!;

    public static ModelDefinition Model(string id) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = "/models/" + id,
        Placement = new ModelPlacement(NodeId),
    };

    public static async Task<MonitorEnv> StartAsync(
        FakeAgentOptions? agentOptions = null,
        bool start = true,
        bool approved = true,
        string? recordUid = null,
        DateTimeOffset? clockStart = null,
        DateTimeOffset? certNotAfter = null,
        IModelRegistry? models = null)
    {
        var env = new MonitorEnv(clockStart ?? DateTimeOffset.UtcNow);
        try
        {
            env.Agent = await FakeAgent.StartAsync(agentOptions);
            env._master = env.Agent.Ca.IssueMasterClientCertificate();
            env._trust = TrustAnchorSet.Create([env.Agent.Ca.Certificate]);
            await env.Registry.UpsertAsync(new NodeRecord
            {
                Id = NodeId,
                Uid = recordUid ?? env.Agent.NodeUid,
                Address = env.Agent.BaseAddress.AbsoluteUri,
                Approved = approved,
                CertSpkiSha256 = env.Agent.CurrentSpki,
                CertNotAfter = certNotAfter,
            });
            env.Factory = new AgentClientFactory(env.Registry, env.Identity, env._master, env._trust, TimeProvider.System);
            await env.Factory.RefreshAsync();
            env.Taps = new TapFactory(env.Factory);
            var options = new NodeMonitorOptions();
            env.Cache = new NodeStatusCache(env.Taps, env.Identity, env.Clock, models, options: options);
            env.Runner = new AgentOperationRunner(env.Taps, env.References, env.Registry, env.Clock);
            env.Monitor = new NodeMonitor(env.Registry, env.Cache, env.Clock, env.Runner, env.References, recovery: env.Recovery);
            if (start)
            {
                await env.StartMonitorAsync();
            }

            return env;
        }
        catch
        {
            await env.DisposeAsync();
            throw;
        }
    }

    public async Task StartMonitorAsync()
    {
        _monitorStarted = true;
        await Monitor.StartAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Waits (on cache changes, real-time bound) until the node matches; the manual clock does not move.</summary>
    public async Task<NodeLiveStatus> WaitForAsync(Func<NodeLiveStatus, bool> predicate, string what) =>
        await WaitAsync(predicate, what, pump: false);

    /// <summary>
    /// Like <see cref="WaitForAsync"/>, but whenever the monitor waits on a one-shot timer (a reconnect backoff or a
    /// blocked poll), the manual clock is advanced to it.
    /// </summary>
    public async Task<NodeLiveStatus> PumpUntilAsync(Func<NodeLiveStatus, bool> predicate, string what) =>
        await WaitAsync(predicate, what, pump: true);

    public Task<OperationStatus> StopAsync(string modelId) =>
        Factory.GetClient(NodeId).StopAsync(modelId, AgentContractSuite.Stop(), TestContext.Current.CancellationToken);

    public Task<OperationStatus> LaunchAsync(string modelId) =>
        Factory.GetClient(NodeId).LaunchAsync(modelId, AgentContractSuite.LaunchFor(modelId), TestContext.Current.CancellationToken);

    /// <summary>Another master (higher epoch) drives the agent once, so the agent remembers its triple.</summary>
    public async Task StopAsAnotherMasterAsync(long epoch)
    {
        var other = new MonitorIdentity { Instance = "other-master", Epoch = epoch, Boot = "other-boot" };
        await using var factory = new AgentClientFactory(Registry, other, _master, _trust, TimeProvider.System);
        await factory.RefreshAsync();
        await factory.GetClient(NodeId).StopAsync("model-z", AgentContractSuite.Stop(), TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_monitorStarted)
        {
            await Monitor.StopAsync(CancellationToken.None);
        }

        Monitor?.Dispose();
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (Agent is not null)
        {
            await Agent.DisposeAsync();
        }

        _master?.Dispose();
        _trust?.Dispose();
        References.Dispose();
        Registry.Dispose();
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string Describe()
    {
        NodeLiveStatus? node = Cache.GetNode(NodeId);
        return $"state {node?.State}, boot {node?.BootId}, seq {node?.Seq}, reasons [{string.Join(" | ", node?.Reasons ?? [])}], " +
            $"open ops [{string.Join(", ", node?.OpenOperations.Select(o => $"{o.Kind}:{o.State}") ?? [])}], " +
            $"snapshots [{string.Join(", ", Taps.Snapshots.Select(x => x.Seq))}], subscriptions [{string.Join(", ", Taps.Subscriptions.Select(x => x.AfterSeq))}], " +
            $"events [{string.Join(", ", Taps.Seen.Select(e => $"{e.EventType}:{e.Seq}"))}]";
    }

    private async Task<NodeLiveStatus> WaitAsync(Func<NodeLiveStatus, bool> predicate, string what, bool pump)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            long version = Cache.Version;
            if (Cache.GetNode(NodeId) is { } node && predicate(node))
            {
                return node;
            }

            TimeSpan left = deadline - DateTime.UtcNow;
            Assert.True(left > TimeSpan.Zero, $"Timed out waiting for {what}; {Describe()}");
            using var round = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            Task change = Cache.WaitForChangeAsync(version, round.Token);
            Task timer = pump ? Clock.WaitForOneShotAsync(round.Token) : Task.Delay(Timeout.Infinite, round.Token);
            Task first = await Task.WhenAny(change, timer, Task.Delay(left, round.Token));
            await round.CancelAsync();
            if (pump && first == timer && timer.IsCompletedSuccessfully && !change.IsCompletedSuccessfully)
            {
                Clock.AdvanceToNextOneShot();
            }
        }
    }
}

/// <summary>The master triple; tests raise the epoch to simulate a takeover.</summary>
file sealed class MonitorIdentity : IMasterIdentity
{
    public string Instance { get; init; } = "this-master";

    public string Boot { get; init; } = "this-boot";

    public long Epoch { get; set; } = 3;

    public string InstanceId => Instance;

    public string BootId => Boot;

    public MasterTriple Current => new(Instance, Epoch, Boot);

    public bool TakeoverRequested => false;

    public IReadOnlyList<TakeoverRecord> Takeovers => [];

    public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>A hand-written model registry.</summary>
file sealed class InMemoryModels(IEnumerable<ModelDefinition> models) : IModelRegistry
{
    private readonly List<ModelDefinition> _models = [.. models];

    public Task<IReadOnlyList<ModelDefinition>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelDefinition>>([.. _models]);

    public Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(_models.FirstOrDefault(m => m.Id == id));

    public Task UpsertAsync(ModelDefinition model, CancellationToken ct = default)
    {
        _models.RemoveAll(m => m.Id == model.Id);
        _models.Add(model);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(_models.RemoveAll(m => m.Id == id) > 0);
}

/// <summary>Wraps the real factory: counts calls, records snapshots and subscriptions, and can drop events.</summary>
file sealed class TapFactory(AgentClientFactory inner) : IAgentClientFactory
{
    private readonly Lock _lock = new();
    private readonly List<StatusSnapshot> _snapshots = [];
    private readonly List<EventsQuery> _subscriptions = [];
    private readonly List<AgentEvent> _seen = [];
    private int _infoCalls;
    private int _statusCalls;

    public int InfoCalls => Volatile.Read(ref _infoCalls);

    public int StatusCalls => Volatile.Read(ref _statusCalls);

    public IReadOnlyList<StatusSnapshot> Snapshots
    {
        get
        {
            lock (_lock)
            {
                return [.. _snapshots];
            }
        }
    }

    public IReadOnlyList<EventsQuery> Subscriptions
    {
        get
        {
            lock (_lock)
            {
                return [.. _subscriptions];
            }
        }
    }

    public IReadOnlyList<AgentEvent> Seen
    {
        get
        {
            lock (_lock)
            {
                return [.. _seen];
            }
        }
    }

    /// <summary>Events for which this returns true are not delivered (as if the client had skipped them).</summary>
    public Func<AgentEvent, bool>? Drop { get; set; }

    public IAgentClient GetClient(string nodeId) => new TapClient(inner.GetClient(nodeId), this);

    public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => inner.CreateFreshClientAsync(nodeId, ct);

    public void RebuildHandlers(string nodeId) => inner.RebuildHandlers(nodeId);

    public void SetNegotiatedProtocol(string nodeId, int version) => inner.SetNegotiatedProtocol(nodeId, version);

    public void CountInfo() => Interlocked.Increment(ref _infoCalls);

    public void Record(StatusSnapshot snapshot)
    {
        Interlocked.Increment(ref _statusCalls);
        lock (_lock)
        {
            _snapshots.Add(snapshot);
        }
    }

    public void Record(EventsQuery query)
    {
        lock (_lock)
        {
            _subscriptions.Add(query);
        }
    }

    public bool Deliver(AgentEvent agentEvent)
    {
        lock (_lock)
        {
            _seen.Add(agentEvent);
        }

        return Drop?.Invoke(agentEvent) != true;
    }
}

file sealed class TapClient(IAgentClient inner, TapFactory tap) : IAgentClient
{
    public string NodeId => inner.NodeId;

    public IAgentDataClient Data => inner.Data;

    public async Task<InfoResponse> GetInfoAsync(CancellationToken ct = default)
    {
        tap.CountInfo();
        return await inner.GetInfoAsync(ct);
    }

    public Task<NodeCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) => inner.GetCapabilitiesAsync(ct);

    public async Task<StatusSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        StatusSnapshot snapshot = await inner.GetStatusAsync(ct);
        tap.Record(snapshot);
        return snapshot;
    }

    public async IAsyncEnumerable<AgentEvent> SubscribeEventsAsync(EventsQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        tap.Record(query);
        await foreach (AgentEvent agentEvent in inner.SubscribeEventsAsync(query, ct))
        {
            if (tap.Deliver(agentEvent))
            {
                yield return agentEvent;
            }
        }
    }

    public Task<TelemetryPage> GetTelemetryAsync(TelemetryQuery query, CancellationToken ct = default) => inner.GetTelemetryAsync(query, ct);

    public Task<GpuIdleResponse> SampleGpuIdleAsync(GpuIdleQuery query, CancellationToken ct = default) => inner.SampleGpuIdleAsync(query, ct);

    public Task<IReadOnlyList<RuntimeStatus>> ListRuntimesAsync(RuntimesQuery query, CancellationToken ct = default) => inner.ListRuntimesAsync(query, ct);

    public Task<RuntimeStatus?> GetRuntimeAsync(string modelId, CancellationToken ct = default) => inner.GetRuntimeAsync(modelId, ct);

    public Task<OperationStatus> LaunchAsync(string modelId, LaunchRequest request, CancellationToken ct = default) => inner.LaunchAsync(modelId, request, ct);

    public Task<OperationStatus> StopAsync(string modelId, StopRequest request, CancellationToken ct = default) => inner.StopAsync(modelId, request, ct);

    public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => inner.GetLaunchConfigAsync(modelId, ct);

    public IAsyncEnumerable<string> StreamLogsAsync(string modelId, LogsQuery query, CancellationToken ct = default) => inner.StreamLogsAsync(modelId, query, ct);

    public Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default) => inner.ListImagesAsync(ct);

    public Task<OperationStatus> PullImageAsync(ImagePullRequest request, CancellationToken ct = default) => inner.PullImageAsync(request, ct);

    public Task<IReadOnlyList<ModelFolderEntry>> ListModelFoldersAsync(CancellationToken ct = default) => inner.ListModelFoldersAsync(ct);

    public Task<ModelFolderInspection?> InspectModelFolderAsync(string folder, CancellationToken ct = default) => inner.InspectModelFolderAsync(folder, ct);

    public Task<OperationStatus> DeleteModelFolderAsync(string folder, FolderDeleteRequest request, CancellationToken ct = default) => inner.DeleteModelFolderAsync(folder, request, ct);

    public Task<NodeDownloadStatus> StartDownloadAsync(DownloadStartRequest request, CancellationToken ct = default) => inner.StartDownloadAsync(request, ct);

    public Task<IReadOnlyList<NodeDownloadStatus>> ListDownloadsAsync(CancellationToken ct = default) => inner.ListDownloadsAsync(ct);

    public Task<NodeDownloadStatus?> GetDownloadAsync(string folder, CancellationToken ct = default) => inner.GetDownloadAsync(folder, ct);

    public Task<OperationStatus> CancelDownloadAsync(string folder, DownloadCancelRequest request, CancellationToken ct = default) => inner.CancelDownloadAsync(folder, request, ct);

    public Task<OperationStatus?> GetOperationAsync(string operationId, CancellationToken ct = default) => inner.GetOperationAsync(operationId, ct);

    public Task<IReadOnlyList<OperationStatus>> ListOperationsAsync(OperationsQuery query, CancellationToken ct = default) => inner.ListOperationsAsync(query, ct);

    public Task<OperationStatus> CancelOperationAsync(string operationId, OperationCancelRequest request, CancellationToken ct = default) => inner.CancelOperationAsync(operationId, request, ct);

    public Task<HostServicesResponse> ListHostServicesAsync(HostServicesQuery query, CancellationToken ct = default) => inner.ListHostServicesAsync(query, ct);

    public Task<OperationStatus> RunHostServiceAsync(string name, HostServiceAction action, HostServiceActionRequest request, CancellationToken ct = default) => inner.RunHostServiceAsync(name, action, request, ct);

    public Task<OperationStatus> SetHostServiceAutostartAsync(string name, HostServiceAutostartRequest request, CancellationToken ct = default) => inner.SetHostServiceAutostartAsync(name, request, ct);

    public Task<OperationStatus> AddToWhitelistAsync(HostWhitelistAddRequest request, CancellationToken ct = default) => inner.AddToWhitelistAsync(request, ct);

    public Task<OperationStatus> UpdateWhitelistAsync(string name, HostWhitelistUpdateRequest request, CancellationToken ct = default) => inner.UpdateWhitelistAsync(name, request, ct);

    public Task<OperationStatus> RemoveFromWhitelistAsync(string name, HostWhitelistRemoveRequest request, CancellationToken ct = default) => inner.RemoveFromWhitelistAsync(name, request, ct);

    public Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default) => inner.GetGpuProcessesAsync(ct);

    public Task<HostContainerInfo?> GetContainerAsync(string name, CancellationToken ct = default) => inner.GetContainerAsync(name, ct);

    public Task<OperationStatus> RunContainerAsync(string name, HostServiceAction action, HostContainerActionRequest request, CancellationToken ct = default) => inner.RunContainerAsync(name, action, request, ct);

    public Task<OperationStatus> SetContainerAutostartAsync(string name, HostContainerAutostartRequest request, CancellationToken ct = default) => inner.SetContainerAutostartAsync(name, request, ct);

    public Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default) => inner.GetBackupMountAsync(ct);

    public Task<OperationStatus> SetBackupMountAsync(BackupMountSetRequest request, CancellationToken ct = default) => inner.SetBackupMountAsync(request, ct);

    public Task<OperationStatus> RemoveBackupMountAsync(BackupMountRemoveRequest request, CancellationToken ct = default) => inner.RemoveBackupMountAsync(request, ct);

    public Task<BackupMountTestResult> TestBackupMountAsync(BackupMountTestRequest request, CancellationToken ct = default) => inner.TestBackupMountAsync(request, ct);

    public Task<OperationStatus> StartRotationAsync(PkiRotationStartRequest request, CancellationToken ct = default) => inner.StartRotationAsync(request, ct);

    public Task<OperationStatus> InstallRotationCertificateAsync(PkiRotationCertRequest request, CancellationToken ct = default) => inner.InstallRotationCertificateAsync(request, ct);

    public Task<OperationStatus> CommitRotationAsync(PkiRotationCommitRequest request, CancellationToken ct = default) => inner.CommitRotationAsync(request, ct);

    public Task<OperationStatus> AbortRotationAsync(PkiRotationAbortRequest request, CancellationToken ct = default) => inner.AbortRotationAsync(request, ct);

    public Task<PkiRotationStatus?> GetRotationAsync(string rotationId, CancellationToken ct = default) => inner.GetRotationAsync(rotationId, ct);

    public Task<PkiTrustResponse> GetTrustAsync(CancellationToken ct = default) => inner.GetTrustAsync(ct);

    public Task<OperationStatus> SetTrustAsync(PkiTrustSetRequest request, CancellationToken ct = default) => inner.SetTrustAsync(request, ct);
}

/// <summary>
/// A <see cref="TimeProvider"/> whose clock and timers move only on <see cref="Advance"/>. One-shot timers are the
/// monitor's backoffs and polls (<c>Task.Delay</c>); the periodic one is its liveness loop.
/// </summary>
file sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;
    private TaskCompletionSource _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _periodicFired = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        TaskCompletionSource? periodic = null;
        lock (_lock)
        {
            _now += by;
            due = [.. _timers.Where(t => t.DueAt <= _now)];
            foreach (var timer in due)
            {
                if (timer.Period > TimeSpan.Zero)
                {
                    timer.DueAt = _now + timer.Period;
                    periodic = _periodicFired;
                }
                else
                {
                    _timers.Remove(timer);
                }
            }

            if (periodic is not null)
            {
                _periodicFired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }

        periodic?.TrySetResult();
    }

    /// <summary>Advances to the earliest pending one-shot timer.</summary>
    public void AdvanceToNextOneShot()
    {
        TimeSpan by;
        lock (_lock)
        {
            var next = _timers.Where(t => t.Period == TimeSpan.Zero).OrderBy(t => t.DueAt).FirstOrDefault();
            if (next is null)
            {
                return;
            }

            by = next.DueAt > _now ? next.DueAt - _now : TimeSpan.Zero;
        }

        Advance(by);
    }

    /// <summary>Completes when a one-shot timer is pending.</summary>
    public async Task WaitForOneShotAsync(CancellationToken ct)
    {
        while (true)
        {
            Task signal;
            lock (_lock)
            {
                if (_timers.Any(t => t.Period == TimeSpan.Zero))
                {
                    return;
                }

                signal = _scheduled.Task;
            }

            await signal.WaitAsync(ct);
        }
    }

    /// <summary>Completes after the next advance that fires a periodic timer, once its callback ran.</summary>
    public Task WaitForPeriodicTickAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            return _periodicFired.Task.WaitAsync(ct);
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        TaskCompletionSource scheduled;
        lock (_lock)
        {
            _timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            timer.DueAt = _now + dueTime;
            timer.Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            _timers.Add(timer);
            scheduled = _scheduled;
            _scheduled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        scheduled.TrySetResult();
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public TimeSpan Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock.Schedule(this, dueTime, period);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
