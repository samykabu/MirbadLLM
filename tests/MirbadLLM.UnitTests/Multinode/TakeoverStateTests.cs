using System.Reflection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using Microsoft.Extensions.Options;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.Orchestration.Nodes;

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-126 (W7 review of MN-115): the cache's Pending takeover marks and fencing states around epoch raises, driven
/// deterministically (a hand-written agent client whose <c>/info</c> can be held in flight, a mutable master identity and
/// a manual clock): a block decided against an epoch the master has since raised never reads NeedsContinuation or Stale
/// (the takeover banner must not flash), an <c>/info</c> in flight across a raise is judged against the new epoch, another
/// agent at the node's address does not clear the mark, a mutation refusal does not stand in for the post-takeover
/// <c>/info</c>, and a marked node that is away reads Pending takeover, not Offline (design §3.5).
/// </summary>
public sealed class TakeoverStateTests
{
    private const string Node = "pc";
    private const string Uid = "uid-pc";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AContinuationThatRaisedTheEpochShowsPendingTakeoverNotNeedsContinuationUntilTheNextInfo()
    {
        var (cache, identity, agent, _) = Create(markedEpoch: 10);
        agent.AcceptedEpoch = 20;
        await cache.SynchronizeAsync(Node, Ct);
        Assert.Equal(new NodeFencingStatus(Node, NodeFencingState.NeedsContinuation, 20), Assert.Single(cache.ListFencing()));

        // The continuation persisted epoch 21; the node's block was read at epoch 10 and is not re-judged yet.
        identity.Epoch = 21;
        long version = cache.Version;
        cache.EpochRaised();
        Assert.True(cache.Version > version);
        Assert.Equal(NodeFencingState.PendingTakeover, Assert.Single(cache.ListFencing()).State);
        NodeLiveStatus pending = cache.GetNode(Node)!;
        Assert.Equal(NodeState.PendingTakeover, pending.State); // still blocked for routing until it shows the lower epoch
        Assert.StartsWith("Unreachable during takeover tk-1", pending.Reasons[0], StringComparison.Ordinal);

        // Its next /info shows 20 < 21: it leaves the mark and connects.
        Assert.Equal(NodeSyncStatus.Synchronized, (await cache.SynchronizeAsync(Node, Ct)).Status);
        Assert.Null(cache.PendingTakeoverOf(Node));
        Assert.Equal(new NodeFencingStatus(Node, NodeFencingState.Current, 20), Assert.Single(cache.ListFencing()));
        Assert.Equal(NodeState.Online, cache.GetNode(Node)!.State);
    }

    [Fact]
    public async Task AnInfoInFlightAcrossAnEpochRaiseIsJudgedAgainstTheNewEpoch()
    {
        var (cache, identity, agent, _) = Create(markedEpoch: 10);
        agent.AcceptedEpoch = 20;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.HoldInfo = release.Task;
        Task<NodeSyncResult> sync = cache.SynchronizeAsync(Node, Ct);
        await agent.InfoStarted.Task.WaitAsync(Ct);

        // A continuation to 21 completes while /info (answered with epoch 20) is still in flight.
        identity.Epoch = 21;
        cache.EpochRaised();
        release.SetResult();

        Assert.Equal(NodeSyncStatus.Synchronized, (await sync).Status);
        Assert.Null(cache.PendingTakeoverOf(Node));
        Assert.Equal(NodeFencingState.Current, Assert.Single(cache.ListFencing()).State);
    }

    [Fact]
    public async Task AStaleBlockOfAnUnmarkedNodeIsUnknownAfterATakeoverRaisedTheEpoch()
    {
        var (cache, identity, agent, _) = Create(markedEpoch: null);
        agent.AcceptedEpoch = 9;
        identity.Epoch = 1;
        await cache.SynchronizeAsync(Node, Ct);
        Assert.Equal(NodeFencingState.Stale, Assert.Single(cache.ListFencing()).State);

        identity.Epoch = 10; // the takeover answered by this node
        cache.EpochRaised();
        Assert.Equal(NodeFencingState.Unknown, Assert.Single(cache.ListFencing()).State);

        await cache.SynchronizeAsync(Node, Ct);
        Assert.Equal(NodeFencingState.Current, Assert.Single(cache.ListFencing()).State);
    }

    [Fact]
    public async Task AnotherAgentAnsweringAtTheAddressDoesNotClearTheMark()
    {
        var (cache, _, agent, _) = Create(markedEpoch: 10);
        agent.AcceptedEpoch = null;
        agent.ReportedUid = "someone-else";
        Assert.Equal(NodeSyncStatus.Blocked, (await cache.SynchronizeAsync(Node, Ct)).Status);
        Assert.Equal("tk-1", cache.PendingTakeoverOf(Node));
        Assert.Equal(NodeState.PendingTakeover, cache.GetNode(Node)!.State);

        // The real agent (holding no epoch) clears it.
        agent.ReportedUid = Uid;
        Assert.Equal(NodeSyncStatus.Synchronized, (await cache.SynchronizeAsync(Node, Ct)).Status);
        Assert.Null(cache.PendingTakeoverOf(Node));
    }

    [Fact]
    public void AMutationRefusalOfAMarkedNodeDoesNotStandInForItsInfo()
    {
        var (cache, _, _, _) = Create(markedEpoch: 10);
        var resyncs = new List<string>();
        cache.ResyncRequested = resyncs.Add;

        Assert.True(cache.ReportAgentError(Node, new AgentException(409, new AgentError { Code = ErrorCodes.StaleMaster, Message = "stale" })));
        Assert.Equal(NodeFencingState.PendingTakeover, Assert.Single(cache.ListFencing()).State);
        Assert.Equal([Node], resyncs);
    }

    [Fact]
    public async Task AMarkedNodeThatIsAwayReadsPendingTakeoverNotOffline()
    {
        var (cache, _, agent, clock) = Create(markedEpoch: 10);
        agent.AcceptedEpoch = 20;
        await cache.SynchronizeAsync(Node, Ct); // contact, then blocked as needing a continuation
        clock.Now += cache.Options.OfflineAfter + TimeSpan.FromSeconds(1);
        cache.EvaluateLiveness();
        Assert.Equal(NodeState.PendingTakeover, cache.GetNode(Node)!.State);

        // Never contacted in this master boot: Pending takeover as well.
        var (fresh, _, _, freshClock) = Create(markedEpoch: 10);
        freshClock.Now += cache.Options.OfflineAfter * 4;
        fresh.EvaluateLiveness();
        Assert.Equal(NodeState.PendingTakeover, fresh.GetNode(Node)!.State);
    }

    [Fact]
    public async Task TheHostedLoopRunsNoSecondTakeoverForARequestATakeoverInFlightAlreadyServed()
    {
        // MN-115: the loop saw the flag file while another takeover (here the API's) held the gate in its /info probe.
        // That takeover deletes the flag; the loop must re-check under the gate instead of taking over a second time.
        var (cache, identity, agent, _) = Create(markedEpoch: null);
        identity.Requested = true;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.HoldInfo = release.Task;
        string dir = P1Exit.ExitEnv.NewDirectory("takeover-gate");
        try
        {
            using var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = dir }), TimeProvider.System);
            await registry.UpsertAsync(new NodeRecord { Id = Node, Uid = Uid, Address = "https://pc:7443/", Approved = true }, Ct);
            using var service = new TakeoverService(identity, cache, registry, new Factory(agent), TimeProvider.System,
                options: new TakeoverServiceOptions { RequestPollInterval = TimeSpan.FromMilliseconds(10) });

            Task<TakeoverRecord> api = service.TakeOverAsync("admin:test", Ct);
            await agent.InfoStarted.Task.WaitAsync(Ct);

            // The loop sees the request while the API's takeover is still probing.
            await service.StartAsync(Ct);
            await identity.RequestRead(1).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            release.SetResult();
            TakeoverRecord record = await api;

            // Two more looks at the request (the one under the gate, then the next poll's): still the API's takeover only.
            await identity.RequestRead(3).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await service.StopAsync(Ct);
            Assert.Equal(record.TakeoverId, Assert.Single(identity.Records).TakeoverId);
            Assert.Equal(2, identity.Epoch);
        }
        finally
        {
            P1Exit.ExitEnv.DeleteDirectory(dir);
        }
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static (NodeStatusCache Cache, Identity Identity, Agent Agent, Clock Clock) Create(long? markedEpoch)
    {
        var identity = new Identity { Epoch = markedEpoch ?? 1 };
        if (markedEpoch is { } epoch)
        {
            identity.Records = [new TakeoverRecord { TakeoverId = "tk-1", Epoch = epoch, PreviousEpoch = 1, At = DateTimeOffset.UnixEpoch, Unreachable = [Node] }];
        }

        var clock = new Clock();
        var agent = new Agent(clock);
        var cache = new NodeStatusCache(new Factory(agent), identity, clock);
        cache.SetNodes([new NodeRecord { Id = Node, Uid = Uid, Address = "https://pc:7443/", Approved = true }]);
        return (cache, identity, agent, clock);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Identity : IMasterIdentity
    {
        public long Epoch { get; set; }

        public IReadOnlyList<TakeoverRecord> Records { get; set; } = [];

        public string InstanceId => "restored";

        public string BootId => "boot";

        public MasterTriple Current => new(InstanceId, Epoch, BootId);

        /// <summary>The flag file: set by the test, cleared by a completed takeover.</summary>
        public bool Requested { get; set; }

        public int RequestReads => Volatile.Read(ref _reads);

        public bool TakeoverRequested
        {
            get
            {
                bool requested = Requested;
                int reads = Interlocked.Increment(ref _reads);
                lock (_waiters)
                {
                    foreach (var waiter in _waiters.Where(w => w.Reads <= reads).ToList())
                    {
                        _waiters.Remove(waiter);
                        waiter.Done.TrySetResult();
                    }
                }

                return requested;
            }
        }

        public IReadOnlyList<TakeoverRecord> Takeovers => Records;

        private readonly List<(int Reads, TaskCompletionSource Done)> _waiters = [];
        private int _reads;

        /// <summary>Completes once <see cref="TakeoverRequested"/> has been read <paramref name="reads"/> times.</summary>
        public Task RequestRead(int reads)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waiters)
            {
                if (RequestReads >= reads)
                {
                    return Task.CompletedTask;
                }

                _waiters.Add((reads, done));
            }

            return done.Task;
        }

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        /// <summary>Like <c>MasterIdentity</c>: persists the epoch with the record and deletes the flag file.</summary>
        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default)
        {
            Epoch = record.Epoch;
            Records = [.. Records.Where(r => r.TakeoverId != record.TakeoverId), record];
            Requested = false;
            return Task.FromResult(Current);
        }
    }

    private sealed class Factory(Agent agent) : IAgentClientFactory
    {
        public IAgentClient GetClient(string nodeId) => nodeId == Node ? agent.Client : throw new KeyNotFoundException(nodeId);

        public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

        public void RebuildHandlers(string nodeId) => throw new NotSupportedException();

        public void SetNegotiatedProtocol(string nodeId, int version)
        {
        }
    }

    /// <summary>The agent: <c>/info</c> with a settable accepted epoch (another master's), capabilities and an empty status.</summary>
    private sealed class Agent
    {
        private readonly Clock _clock;

        public Agent(Clock clock)
        {
            _clock = clock;
            Client = AgentProxy.Create(this);
        }

        public IAgentClient Client { get; }

        public long? AcceptedEpoch { get; set; }

        public string ReportedUid { get; set; } = Uid;

        public Task? HoldInfo { get; set; }

        public TaskCompletionSource InfoStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<InfoResponse> InfoAsync()
        {
            var info = new InfoResponse
            {
                NodeId = Node,
                NodeUid = ReportedUid,
                AgentVersion = "1.0.0",
                Protocol = new ProtocolRange(Protocol.MinVersion, Protocol.CurrentVersion),
                BootId = "agent-boot",
                StartedAt = _clock.Now,
                Time = _clock.Now,
                AcceptedMaster = AcceptedEpoch is { } epoch ? new MasterTriple("old-master", epoch, "old-boot") : null,
            };
            InfoStarted.TrySetResult();
            if (HoldInfo is { } hold)
            {
                await hold;
            }

            return info;
        }

        public static NodeCapabilities Capabilities() => new()
        {
            Protocol = new ProtocolRange(Protocol.MinVersion, Protocol.CurrentVersion),
            AgentVersion = "1.0.0",
            Features = [.. AgentFeatures.P1],
            Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
            Arch = "x86_64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 64),
        };

        public StatusSnapshot Status() => new()
        {
            BootId = "agent-boot",
            Seq = 1,
            Time = _clock.Now,
            Health = new NodeHealth(),
            Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 64, HostAvailableGiB = 60 },
        };
    }

    /// <summary>An <see cref="IAgentClient"/> answering the three synchronization calls; every other call fails.</summary>
    public class AgentProxy : DispatchProxy
    {
        private object? _agent;

        private Agent Target => (Agent)_agent!;

        internal static IAgentClient Create(object agent)
        {
            IAgentClient client = DispatchProxy.Create<IAgentClient, AgentProxy>();
            ((AgentProxy)(object)client)._agent = agent;
            return client;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IAgentClient.GetInfoAsync) => Target.InfoAsync(),
            nameof(IAgentClient.GetCapabilitiesAsync) => Task.FromResult(Agent.Capabilities()),
            nameof(IAgentClient.GetStatusAsync) => Task.FromResult(Target.Status()),
            nameof(IAgentClient.NodeId) or "get_NodeId" => Node,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
