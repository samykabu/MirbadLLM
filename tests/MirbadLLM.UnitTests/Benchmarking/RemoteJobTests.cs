using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>
/// MN-033: benchmarks and tuning through the agent, end to end against <see cref="FakeAgent"/> + <see cref="FakeModelServer"/>
/// over real mTLS + HTTP/2 (<see cref="AgentClientFactory"/>): the load goes through the node's data client to the agent
/// proxy (the direct client is never used), the GPU idle check is the node's <c>/gpu/idle</c>, the run records its
/// <c>proxy</c> provenance, and boot recovery of an interrupted tuning run waits until the node is reconciled.
/// <see cref="AgentCluster"/> stands in for <c>RemoteModelRuntime</c> (MN-028, built in parallel).
/// </summary>
public sealed class RemoteJobTests
{
    private static readonly ModelDefinition Saved = new()
    {
        Id = "qwen",
        ServedName = "qwen",
        ModelPath = AgentContractSeed.ModelFolder,
        MemoryBudgetGiB = 1,
        MaxModelLen = 8192,
        MaxNumSeqs = 16,
        Placement = new ModelPlacement("dgx"),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ABenchmarkRunsThroughTheAgentProxyAndRecordsItsDataPath()
    {
        await using var env = await Env.StartAsync();
        await env.Cluster.LaunchAsync(Saved, new LaunchOptions(), Ct);
        await env.WaitReadyAsync(Saved.Id);

        await env.Jobs.StartBenchmarkAsync(Saved.Id, Ct);
        await env.Jobs.WhenIdleAsync();

        Assert.Equal("Completed", env.Jobs.LastOutcome?.Status);
        var run = Assert.Single(await env.Store.ListBenchmarksAsync(Saved.Id, Ct));
        Assert.Null(run.Error);
        Assert.Equal(BenchmarkDataPaths.AgentLoopback, run.Proxy);
        var result = Assert.IsType<BenchmarkResult>(run.Result);
        Assert.All(result.Levels, l => Assert.True(l.AggregateTokS > 0, $"N={l.Concurrency} measured nothing."));

        // The GPU idle reading is the node's /gpu/idle (FakeAgent: 2 % with a Ready instance), not the local probe (77 %).
        Assert.Equal(2, result.Noise.IdleGpuPercent);
        Assert.Equal(0, env.LocalGpu.Calls);
        Assert.Equal(0, env.DirectCalls);
    }

    [Theory]
    [InlineData(true, BenchmarkDataPaths.AgentLoopback)]
    [InlineData(false, BenchmarkDataPaths.AgentLan)]
    public async Task TheCoLocatedBootstrapNodeIsLabelledLoopbackAlthoughItsAddressIsAContainerName(bool coLocated, string expected)
    {
        // The DGX's agent is reached as https://mirbadllm-agent:9443 over the host's Docker network (W4 review MN-096).
        string dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-colocated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataPath);
        try
        {
            const string Address = "https://mirbadllm-agent:9443/";
            var options = Options.Create(new MirbadLLMOptions { DataPath = dataPath });
            using var ca = CertificateAuthority.Create();
            using var master = ca.IssueMasterClientCertificate();
            using var trust = TrustAnchorSet.Create([ca.Certificate]);
            using var registry = new JsonNodeRegistry(options, TimeProvider.System);
            await registry.UpsertAsync(new NodeRecord
            {
                Id = "dgx", Uid = "0123456789abcdef0123456789abcdef", Address = Address, Approved = true,
                CertSpkiSha256 = Convert.ToBase64String(new byte[32]),
            }, Ct);
            await using var factory = new AgentClientFactory(registry, new MasterIdentity(), master, trust, TimeProvider.System);
            await factory.RefreshAsync(Ct);
            var cache = new FakeNodeCache(new NodeLiveStatus
            {
                NodeId = "dgx", NodeUid = "0123456789abcdef0123456789abcdef", State = NodeState.Online, BootId = "boot", Address = Address,
            });
            using var references = new JsonAgentOperationReferenceStore(options, TimeProvider.System);
            using var targets = new AgentJobTargets(factory, cache, new FakeModelRegistry().Add(Saved), references, TimeProvider.System,
                new AgentJobTargetsOptions { CoLocatedNodeIds = coLocated ? ["dgx"] : [] });

            var endpoint = await targets.ResolveAsync(Saved.Id, new ModelStatus(Saved.Id, ModelState.Ready), Ct);

            Assert.Equal("mirbadllm-agent", endpoint.BaseUri.Host);
            Assert.Equal(expected, endpoint.DataPath);
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }

    [Fact]
    public async Task ATwoCandidateTuningRunLaunchesCandidatesOnTheAgentAndRestoresTheSavedSettings()
    {
        await using var env = await Env.StartAsync();
        await env.Cluster.LaunchAsync(Saved, new LaunchOptions(), Ct);
        await env.WaitReadyAsync(Saved.Id);

        await env.Jobs.StartTuningAsync(Saved.Id, TuningGoal.Balanced, [TuningDimension.MaxNumSeqs], Ct);
        await env.Jobs.WhenIdleAsync();

        var run = Assert.Single(await env.Store.ListTuningAsync(Saved.Id, Ct));
        Assert.Null(run.Error);
        Assert.Equal(TuningRunStatus.Completed, run.Status);
        Assert.Equal(BenchmarkDataPaths.AgentLoopback, run.Proxy);

        // Baseline (reused: the running instance has the saved settings) + max num seqs 8 and 32, each measured through the proxy.
        Assert.Equal(3, run.Candidates.Count);
        Assert.All(run.Candidates, c => Assert.NotNull(c.Result));
        Assert.Equal([8, 32], env.Cluster.Launches.Where(l => l.Options.Tuning).Select(l => l.Definition.MaxNumSeqs).ToArray());

        // Restored: the saved settings run again, without the tuning label.
        var final = await env.Client.GetRuntimeAsync(Saved.Id, Ct);
        Assert.NotNull(final);
        Assert.Equal(ModelState.Ready, final.State);
        Assert.False(AgentCluster.IsTuning(final));
        Assert.Equal(AgentCluster.Hash(Saved), final.Labels[ContainerLabels.ConfigHash]);
        Assert.Equal(0, env.DirectCalls);
        Assert.Equal(0, env.LocalGpu.Calls);
    }

    [Fact]
    public async Task RecoveryOfAnInterruptedTuningRunWaitsUntilTheNodeIsReconciled()
    {
        await using var env = await Env.StartAsync();

        // A candidate left running by an interrupted run, and a master that has not reconciled the node yet.
        await env.Cluster.LaunchAsync(Saved with { MaxNumSeqs = 32 }, new LaunchOptions(Tuning: true), Ct);
        await env.WaitReadyAsync(Saved.Id);
        env.Nodes.Set(env.Nodes.Node with { BootId = null });

        using var recovery = new ModelJobRecoveryService(env.Jobs, env.Targets);
        await recovery.StartAsync(Ct);
        try
        {
            await Task.Delay(300, Ct);
            Assert.True(AgentCluster.IsTuning((await env.Client.GetRuntimeAsync(Saved.Id, Ct))!), "Recovery acted before the node was reconciled.");
            Assert.DoesNotContain(env.Cluster.Launches, l => !l.Options.Tuning);

            env.Nodes.Set(env.Nodes.Node with { BootId = env.Agent.BootId });

            // The per-node gate keeps the service watching for new nodes until shutdown: wait until the saved settings run
            // again (not merely until the relaunch was asked for: stopping the service mid-launch would cancel it).
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while ((!env.Cluster.Launches.Any(l => !l.Options.Tuning) ||
                    await env.Client.GetRuntimeAsync(Saved.Id, Ct) is not { } now || AgentCluster.IsTuning(now)) &&
                   DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(20, Ct);
            }
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
        }

        var relaunched = Assert.Single(env.Cluster.Launches, l => !l.Options.Tuning);
        Assert.True(relaunched.Options.Force);
        Assert.Equal(Saved.MaxNumSeqs, relaunched.Definition.MaxNumSeqs);
        var runtime = await env.WaitReadyAsync(Saved.Id);
        Assert.False(AgentCluster.IsTuning(runtime));
    }

    [Fact]
    public async Task TheGateWaitsForOpenRuntimeOperationsOnTheNodeAndOnTheMaster()
    {
        await using var env = await Env.StartAsync();
        Assert.True(await env.Targets.IsReconciledAsync(Ct));

        // The node lists an open launch.
        var node = env.Nodes.Node;
        env.Nodes.Set(node with { OpenOperations = [new NodeOperationSummary("op-1", OperationKinds.Launch, "qwen", OperationState.Running, "pulling", DateTimeOffset.UtcNow)] });
        Assert.False(await env.Targets.IsReconciledAsync(Ct));

        // An open host-service operation does not hold recovery back (an `outcome_unknown` one may stay open).
        env.Nodes.Set(node with { OpenOperations = [new NodeOperationSummary("op-2", OperationKinds.ServiceRestart, "comfyui", OperationState.Running, "dispatched", DateTimeOffset.UtcNow)] });
        Assert.True(await env.Targets.IsReconciledAsync(Ct));

        // The master still holds an open launch reference for the node (not reconciled by its observer yet).
        env.Nodes.Set(node);
        var now = DateTimeOffset.UtcNow;
        await env.References.AddAsync(new AgentOperationReference
        {
            OperationId = "op-3", NodeId = "dgx", NodeUid = env.Agent.NodeUid, Action = OperationKinds.Launch, Target = "qwen",
            PayloadDigest = "sha256:x", IssuedAt = now, ExpiresAt = now.AddMinutes(10),
        }, Ct);
        Assert.False(await env.Targets.IsReconciledAsync(Ct));
        await env.References.CloseAsync("op-3", AgentOperationOutcome.Succeeded, null, Ct);
        Assert.True(await env.Targets.IsReconciledAsync(Ct));

        // Offline node.
        env.Nodes.Set(node with { State = NodeState.Offline });
        Assert.False(await env.Targets.IsReconciledAsync(Ct));
    }

    [Fact]
    public async Task TheGpuIdleReadingMapsTheAgentAnswerOneToOne()
    {
        await using var env = await Env.StartAsync();

        var idle = await env.Targets.SampleAsync(Saved.Id, TimeSpan.FromMilliseconds(10), Ct);
        Assert.Equal(0, idle.AveragePercent);
        Assert.Equal(0, idle.MaxPercent);
        Assert.Equal(1, idle.Samples);

        // An unknown node gives "no reading" rather than an error.
        var unplaced = await env.Targets.SampleAsync("nowhere", TimeSpan.FromSeconds(1), Ct);
        Assert.Equal(new GpuIdleReading(null, null, 0), unplaced);
    }

    [Fact]
    public async Task TheTargetIsTheAgentProxyOfTheModelsNodeAndOnlyThatAddressGetsTheAgentClient()
    {
        await using var env = await Env.StartAsync();

        var endpoint = await env.Targets.ResolveAsync(Saved.Id, new ModelStatus(Saved.Id, ModelState.Ready), Ct);
        Assert.Equal("dgx", endpoint.NodeId);
        Assert.Equal(new Uri(env.Agent.BaseAddress, "agent/v1/proxy/qwen/"), endpoint.BaseUri);
        Assert.NotNull(env.Targets.GetClient(endpoint.BaseUri));
        Assert.Null(env.Targets.GetClient(new Uri("http://ss-qwen:8000/")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => env.Targets.ResolveAsync("nowhere", new ModelStatus("nowhere", ModelState.Ready), Ct));
    }

    [Fact]
    public async Task ABenchmarkOfAModelTheAgentDoesNotRunFailsWithTheAgentsMessage()
    {
        await using var env = await Env.StartAsync();

        // The status says Ready (stale view), but the agent has no instance: the proxy answers model_not_running.
        env.Cluster.ForceReady = true;
        await env.Jobs.StartBenchmarkAsync(Saved.Id, Ct);
        await env.Jobs.WhenIdleAsync();

        Assert.Equal("Failed", env.Jobs.LastOutcome?.Status);
        Assert.Contains(ErrorCodes.ModelNotRunning, env.Jobs.LastOutcome?.Error, StringComparison.Ordinal);
        Assert.Equal(0, env.DirectCalls);
    }

    /// <summary>FakeAgent + mTLS client factory + the job service wired with <see cref="AgentJobTargets"/>.</summary>
    private sealed class Env : IAsyncDisposable
    {
        private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-remote-jobs-" + Guid.NewGuid().ToString("N"));
        private readonly IssuedCredential _master;
        private readonly TrustAnchorSet _trust;
        private readonly JsonNodeRegistry _registry;
        private readonly HttpClient _direct;
        private int _directCalls;

        private Env(FakeAgent agent)
        {
            Agent = agent;
            Directory.CreateDirectory(_dataPath);
            var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, UseContainerNetwork = true });
            _master = agent.Ca.IssueMasterClientCertificate();
            _trust = TrustAnchorSet.Create([agent.Ca.Certificate]);
            _registry = new JsonNodeRegistry(options, TimeProvider.System);
            Factory = new AgentClientFactory(_registry, new MasterIdentity(), _master, _trust, TimeProvider.System);
            References = new JsonAgentOperationReferenceStore(options, TimeProvider.System);
            Nodes = new FakeNodeCache(new NodeLiveStatus
            {
                NodeId = agent.NodeId, NodeUid = agent.NodeUid, State = NodeState.Online, BootId = agent.BootId,
                Address = agent.BaseAddress.AbsoluteUri,
            });
            Registry = new FakeModelRegistry().Add(Saved);
            Targets = new AgentJobTargets(Factory, Nodes, Registry, References, TimeProvider.System,
                new AgentJobTargetsOptions { ReconcilePollInterval = TimeSpan.FromMilliseconds(20) });
            Cluster = new AgentCluster(() => Client);
            Store = new BenchmarkStore(options);
            _direct = new HttpClient(new CountingHandler(() => Interlocked.Increment(ref _directCalls)));
            var suite = new BenchmarkSuite(_direct, history: null,
                new BenchmarkSuiteOptions { Levels = [1, 2], Runs = 1, MaxTokens = 4, IncludePrefill = false, RequestTimeout = TimeSpan.FromSeconds(30) },
                targets: Targets);
            Jobs = new ModelJobService(
                Registry, Cluster, Cluster, suite, Store, new MaintenanceModeService(options), new FakeMetricsHistory(), LocalGpu,
                new StartupTimeTracker(), options,
                new ModelJobOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    DrainTimeout = TimeSpan.FromMilliseconds(30),
                    GpuIdleSampleDuration = TimeSpan.FromSeconds(1),
                    CandidateStartTimeout = TimeSpan.FromSeconds(30),
                    CleanupTimeout = TimeSpan.FromSeconds(30),
                },
                NullLogger<ModelJobService>.Instance,
                targets: Targets,
                gpuIdle: Targets);
        }

        public FakeAgent Agent { get; }

        public AgentClientFactory Factory { get; }

        public IAgentClient Client => Factory.GetClient(Agent.NodeId);

        public JsonAgentOperationReferenceStore References { get; }

        public FakeNodeCache Nodes { get; }

        public FakeModelRegistry Registry { get; }

        public AgentJobTargets Targets { get; }

        public AgentCluster Cluster { get; }

        public BenchmarkStore Store { get; }

        public CountingGpu LocalGpu { get; } = new(77);

        public ModelJobService Jobs { get; }

        public int DirectCalls => Volatile.Read(ref _directCalls);

        public static async Task<Env> StartAsync()
        {
            var agent = await FakeAgent.StartAsync(new FakeAgentOptions { StartDuration = TimeSpan.FromMilliseconds(20) });
            var server = await agent.GetModelServerAsync();
            server.SetTokenRateMs(1);
            server.SetTimeToFirstTokenMs(1);
            var env = new Env(agent);
            await env._registry.UpsertAsync(new NodeRecord
            {
                Id = agent.NodeId,
                Uid = agent.NodeUid,
                Address = agent.BaseAddress.AbsoluteUri,
                Approved = true,
                CertSpkiSha256 = agent.CurrentSpki,
            });
            await env.Factory.RefreshAsync();
            return env;
        }

        public async Task<RuntimeStatus> WaitReadyAsync(string modelId)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                if (await Client.GetRuntimeAsync(modelId, Ct) is { State: ModelState.Ready } ready)
                {
                    return ready;
                }

                Assert.True(DateTime.UtcNow < deadline, $"{modelId} did not become Ready.");
                await Task.Delay(10, Ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Jobs.WhenIdleAsync();
            Targets.Dispose();
            await Factory.DisposeAsync();
            await Agent.DisposeAsync();
            References.Dispose();
            _registry.Dispose();
            _direct.Dispose();
            _master.Dispose();
            _trust.Dispose();
            try
            {
                Directory.Delete(_dataPath, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    /// <summary>The direct client: counts calls and fails them (the agent path must be used).</summary>
    private sealed class CountingHandler(Action onCall) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onCall();
            throw new HttpRequestException("The direct path must not be used.");
        }
    }

    /// <summary>The local GPU probe: counts calls (the agent's <c>/gpu/idle</c> must be used).</summary>
    internal sealed class CountingGpu(double utilization) : IGpuProbe
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<GpuSnapshot?> GetAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return new FakeGpu(utilization).GetAsync(ct);
        }
    }

    private sealed class MasterIdentity : IMasterIdentity
    {
        public string InstanceId => "instance-1";

        public long Epoch => 1;

        public string BootId => "boot-1";

        public MasterTriple Current => new(InstanceId, Epoch, BootId);

        public bool TakeoverRequested => false;

        public IReadOnlyList<TakeoverRecord> Takeovers => [];

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>One node whose view the test sets; every model's instance is on it (stands in for MN-027's cache).</summary>
    internal sealed class FakeNodeCache(NodeLiveStatus node) : INodeStatusCache
    {
        private NodeLiveStatus _node = node;
        private long _version;

        public NodeLiveStatus Node => Volatile.Read(ref _node);

        public long Version => Interlocked.Read(ref _version);

        public void Set(NodeLiveStatus node)
        {
            Volatile.Write(ref _node, node);
            Interlocked.Increment(ref _version);
        }

        public IReadOnlyList<NodeLiveStatus> ListNodes() => [Node];

        public NodeLiveStatus? GetNode(string nodeId) => nodeId == Node.NodeId ? Node : null;

        public NodeModelRuntime? GetRuntime(string modelId) =>
            modelId == Saved.Id ? new NodeModelRuntime { ModelId = modelId, NodeId = Node.NodeId, State = ModelState.Ready } : null;

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) => [];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>
    /// A minimal <see cref="IModelRuntime"/> + <see cref="IModelLauncher"/> over the agent client (launch = replace, stop,
    /// runtimes, logs), standing in for <c>RemoteModelRuntime</c> (MN-028).
    /// </summary>
    internal sealed class AgentCluster(Func<IAgentClient> client) : IModelRuntime, IModelLauncher
    {
        private readonly Dictionary<string, ModelDefinition> _launched = new(StringComparer.Ordinal);
        private readonly Lock _gate = new();

        public List<(ModelDefinition Definition, LaunchOptions Options)> Launches { get; } = [];

        /// <summary>Report Ready whatever the agent says (a stale status view).</summary>
        public bool ForceReady { get; set; }

        public static string Hash(ModelDefinition definition) => FakeCluster.Hash(definition);

        public static bool IsTuning(RuntimeStatus runtime) =>
            runtime.Tuning || (runtime.Labels.TryGetValue(ContainerLabels.Tuning, out string? value) && value == "1");

        public async Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) =>
            [.. (await client().ListRuntimesAsync(new RuntimesQuery(), ct)).Select(ToStatus)];

        public async Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default)
        {
            if (ForceReady)
            {
                return new ModelStatus(id, ModelState.Ready);
            }

            return await client().GetRuntimeAsync(id, ct) is { } runtime ? ToStatus(runtime) : new ModelStatus(id, ModelState.Stopped);
        }

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task StopAsync(string id, CancellationToken ct = default)
        {
            var accepted = await client().StopAsync(id, AgentContractSuite.Stop(), ct);
            await WaitAsync(accepted, ct);
        }

        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (string line in client().StreamLogsAsync(id, new LogsQuery { Tail = tail, Follow = follow }, ct))
            {
                yield return line;
            }
        }

        public async Task<ModelStatus> LaunchAsync(ModelDefinition definition, LaunchOptions options, CancellationToken ct = default)
        {
            lock (_gate)
            {
                Launches.Add((definition, options));
            }

            string hash = Hash(definition);
            var labels = new Dictionary<string, string> { [ContainerLabels.ConfigHash] = hash };
            if (options.Tuning)
            {
                labels[ContainerLabels.Tuning] = "1";
            }

            var request = AgentContractSuite.LaunchFor(definition.Id, LaunchMode.Replace, force: options.Force,
                extraArgs: ["--max-num-seqs", definition.MaxNumSeqs.ToString(CultureInfo.InvariantCulture)], extraLabels: labels) with
            {
                ExpectedConfigHash = hash,
            };
            var accepted = await client().LaunchAsync(definition.Id, request, ct);
            await WaitAsync(accepted, ct);
            lock (_gate)
            {
                _launched[definition.Id] = definition;
            }

            return await GetStatusAsync(definition.Id, ct);
        }

        public Task<ModelDefinition?> GetLaunchedDefinitionAsync(string id, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_launched.GetValueOrDefault(id));
            }
        }

        public async Task<string?> GetLaunchedConfigHashAsync(string id, CancellationToken ct = default) =>
            await client().GetRuntimeAsync(id, ct) is { } runtime && runtime.Labels.TryGetValue(ContainerLabels.ConfigHash, out string? hash) ? hash : null;

        public Task<string> ComputeConfigHashAsync(ModelDefinition definition, CancellationToken ct = default) => Task.FromResult(Hash(definition));

        public async Task<IReadOnlyList<string>> ListTuningContainersAsync(CancellationToken ct = default) =>
            [.. (await client().ListRuntimesAsync(new RuntimesQuery { Label = RuntimesQuery.TuningLabel }, ct)).Where(IsTuning).Select(r => r.ModelId)];

        private static ModelStatus ToStatus(RuntimeStatus r) => new(r.ModelId, r.State, r.InstanceId, r.HostPort, r.Since, r.Error);

        private async Task WaitAsync(OperationStatus accepted, CancellationToken ct)
        {
            var status = accepted;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!status.State.IsTerminal())
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Operation {status.Id} did not finish.");
                }

                await Task.Delay(10, ct);
                status = await client().GetOperationAsync(status.Id, ct) ?? status;
            }

            if (status.State != OperationState.Succeeded)
            {
                throw new InvalidOperationException($"Operation {status.Id} ended {status.State}: {status.Error?.Message}");
            }
        }
    }
}
