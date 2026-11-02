using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.UnitTests.Recovery;
using MirbadLLM.UnitTests.Router.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Web.Fakes.FakeModelRegistry;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// Design §4.8 (MN-034): boot auto-start (<see cref="ModelHealthMonitor"/>) and restore auto-start
/// (<see cref="RestoreJobService"/>) run one lane per node — sequential within a node, parallel across nodes — and map the
/// agent's <c>start_conflict</c> (<see cref="ModelStartConflictException"/>) to waiting and retrying.
/// </summary>
public sealed class PerNodeAutoStartTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ModelDefinition Model(string id, string? node, bool autoStart = true) => new()
    {
        Id = id,
        ServedName = id.ToUpperInvariant(),
        ModelPath = id + "-folder",
        MemoryBudgetGiB = 10,
        AutoStart = autoStart,
        Placement = node is null ? null : new ModelPlacement(node),
    };

    private static ModelHealthMonitor Monitor(IModelRegistry registry, IModelRuntime runtime, TimeSpan? startupTimeout = null) =>
        new(registry, runtime,
            Options.Create(new MirbadLLMOptions { HealthPollInterval = TimeSpan.FromHours(1), StartupTimeout = startupTimeout ?? TimeSpan.FromMinutes(1) }),
            NullLogger<ModelHealthMonitor>.Instance);

    [Fact]
    public async Task Reboot_RestoresOnlyThePreviousRunningSet_SequentiallyPerNode_WithoutRestartingTheController()
    {
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(5);
        var models = new[] { Model("a", "pc", false), Model("b", "pc", false), Model("off", "pc", false), Model("c", "other", false) };
        var runtime = new NodeAwareRuntime(models) { RejectOverlapOnNode = true };
        runtime.HoldStarting("a");
        var state = new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = string.Empty }));
        var before = ModelRecoveryStateTests.Node("before", "a", "b");
        var other = ModelRecoveryStateTests.Node("before", "c") with { NodeId = "other", NodeUid = "other-uid" };
        state.Observe(before);
        state.Observe(other);
        var cache = new RecoveryCache(before, other);
        using var monitor = RecoveryMonitor(new FakeModelRegistry(models), runtime, state, cache);
        await monitor.StartAsync(TestContext.Current.CancellationToken);

        var rebooted = ModelRecoveryStateTests.Node("after");
        var otherRebooted = rebooted with { NodeId = "other", NodeUid = "other-uid" };
        cache.Set(rebooted);
        cache.Set(otherRebooted);
        state.Observe(rebooted);
        state.Observe(otherRebooted);
        await runtime.StartedTask("a").WaitAsync(Wait);
        await runtime.StartedTask("c").WaitAsync(Wait);
        Assert.DoesNotContain("b", runtime.Started);
        Assert.DoesNotContain("off", runtime.Started);

        runtime.Release("a");
        await runtime.StartedTask("b").WaitAsync(Wait);
        await WaitUntilAsync(() => state.Pending(rebooted).Count == 0);
        await monitor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["a", "b"], runtime.Started.Where(id => id is "a" or "b"));
        Assert.Equal(1, runtime.MaxStartingPerNode);
        Assert.Empty(runtime.Conflicts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_DoesNotStartAQueuedModelThatWasStoppedOrMoved(bool moved)
    {
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(5);
        var models = new[] { Model("a", "pc", false), Model("b", "pc", false) };
        var registry = new FakeModelRegistry(models);
        var runtime = new NodeAwareRuntime(models);
        runtime.HoldStarting("a");
        var state = new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = string.Empty }));
        state.Observe(ModelRecoveryStateTests.Node("before", "a", "b"));
        var node = ModelRecoveryStateTests.Node("after");
        state.Observe(node);
        using var monitor = RecoveryMonitor(registry, runtime, state, new RecoveryCache(node));
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await runtime.StartedTask("a").WaitAsync(Wait);
        if (moved)
        {
            await registry.UpsertAsync(models[1] with { Placement = new ModelPlacement("other") });
        }
        else
        {
            state.Observe(node, "b", ModelState.Stopped);
        }

        runtime.Release("a");
        await WaitUntilAsync(() => state.Pending(node).Count == 0);
        await monitor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["a"], runtime.Started);
    }

    [Fact]
    public async Task Recovery_DoesNotOverlapALoadPastTheWaitBound_AndContinuesOnceItIsReady()
    {
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(5);
        var models = new[] { Model("a", "pc", false), Model("b", "pc", false) };
        var runtime = new NodeAwareRuntime(models); // No agent-side guard: the master must enforce the sequencing.
        runtime.HoldStarting("a");
        var state = new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = string.Empty }));
        state.Observe(ModelRecoveryStateTests.Node("before", "a", "b"));
        var node = ModelRecoveryStateTests.Node("after");
        state.Observe(node);
        using var monitor = RecoveryMonitor(new FakeModelRegistry(models), runtime, state, new RecoveryCache(node), TimeSpan.FromMilliseconds(40));
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await runtime.StartedTask("a").WaitAsync(Wait);
        await WaitUntilAsync(() => !state.Pending(node).Contains("a", StringComparer.Ordinal));
        Assert.DoesNotContain("b", runtime.Started);
        Assert.Contains("b", state.Pending(node));

        runtime.Release("a");
        await runtime.StartedTask("b").WaitAsync(Wait);
        await monitor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, runtime.MaxStartingPerNode);
    }

    [Fact]
    public async Task Recovery_KeepsQueuedStartWhenPreviousLoadFinishesAfterItsWaitExpires()
    {
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(5);
        var models = new[] { Model("a", "pc", false), Model("b", "pc", false) };
        var runtime = new NodeAwareRuntime(models);
        runtime.HoldStarting("a");
        int reads = 0;
        runtime.BeforeGetStatus = id =>
        {
            if (id == "b" && Interlocked.Increment(ref reads) == 2)
            {
                runtime.Release("a"); // The previous load finishes just after b's bounded wait expires.
            }
        };
        var state = new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = string.Empty }));
        state.Observe(ModelRecoveryStateTests.Node("before", "a", "b"));
        var node = ModelRecoveryStateTests.Node("after");
        state.Observe(node);
        using var monitor = RecoveryMonitor(new FakeModelRegistry(models), runtime, state, new RecoveryCache(node), TimeSpan.FromMilliseconds(40));

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await runtime.StartedTask("b").WaitAsync(Wait);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], runtime.Started);
        Assert.Equal(1, runtime.MaxStartingPerNode);
    }

    [Fact]
    public async Task Recovery_StartsAFormerlyRunningModelThatTheRebootLeftFailed()
    {
        var model = Model("a", "pc", false);
        var runtime = new NodeAwareRuntime([model]);
        runtime.Fail("a");
        var state = new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = string.Empty }));
        state.Observe(ModelRecoveryStateTests.Node("before", "a"));
        var node = ModelRecoveryStateTests.Node("after");
        state.Observe(node);
        using var monitor = RecoveryMonitor(new FakeModelRegistry([model]), runtime, state, new RecoveryCache(node));
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await runtime.StartedTask("a").WaitAsync(Wait);
        await monitor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["a"], runtime.Started);
    }

    private static ModelHealthMonitor RecoveryMonitor(IModelRegistry registry, IModelRuntime runtime, ModelRecoveryState state,
        INodeStatusCache cache, TimeSpan? timeout = null, bool crashRestart = true) => new(registry, runtime,
            Options.Create(new MirbadLLMOptions { HealthPollInterval = TimeSpan.FromMilliseconds(10), StartupTimeout = timeout ?? TimeSpan.FromSeconds(5), CrashRestart = crashRestart }),
            NullLogger<ModelHealthMonitor>.Instance, recovery: state, nodes: cache);

    // ---- Crash auto-restart: the monitor relaunches a model whose container died on its own; ModelRecoveryState pauses it
    // after CrashRestartLimit crashes in a row (the owner's "unless it gets crashing a couple of times in a row").

    /// <summary>A Ready model "a" on node pc, listed by the recovery state; restart backoff and settle period shortened.</summary>
    private static (ModelRecoveryState State, NodeLiveStatus Node, RecoveryCache Cache, NodeAwareRuntime Runtime) CrashScenario(
        string dataPath = "", TimeSpan? settle = null)
    {
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(5);
        ModelHealthMonitor.CrashRestartBackoff = [TimeSpan.FromMilliseconds(20)];
        ModelHealthMonitor.CrashSettlePeriod = settle ?? TimeSpan.FromMinutes(10);
        var state = new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = dataPath, CrashRestartLimit = 3 }));
        var node = ModelRecoveryStateTests.Node("boot", "a");
        state.Observe(node);
        var runtime = new NodeAwareRuntime([Model("a", "pc", false)]);
        runtime.Set("a", ModelState.Ready);
        return (state, node, new RecoveryCache(node), runtime);
    }

    /// <summary>Waits until the monitor polled the runtime <paramref name="polls"/> more times (it has seen the current states).</summary>
    private static Task PolledAsync(NodeAwareRuntime runtime, int polls = 2)
    {
        int seen = runtime.ListCalls;
        return WaitUntilAsync(() => runtime.ListCalls >= seen + polls);
    }

    [Fact]
    public async Task Crash_RestartsTheModelThroughItsNodeLane_AuditedAsSystemAutoStart()
    {
        var (state, _, cache, runtime) = CrashScenario();
        using var monitor = RecoveryMonitor(new FakeModelRegistry([Model("a", "pc", false)]), runtime, state, cache);
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await PolledAsync(runtime);

        runtime.Set("a", ModelState.Failed, "CUDA error: an illegal memory access was encountered");
        await runtime.StartedTask("a").WaitAsync(Wait);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a"], runtime.Started);
        Assert.Equal([AuditActors.SystemAutoStart], runtime.StartActors);
        var crash = Assert.IsType<CrashRestart>(state.CrashRestartOf("a"));
        Assert.Equal((1, 3, false, "pc"), (crash.Crashes, crash.Limit, crash.Paused, crash.NodeId));
        Assert.Equal("CUDA error: an illegal memory access was encountered", crash.LastError);
    }

    [Fact]
    public async Task Crash_ThreeTimesInARow_PausesAutomaticRestarts_Durably_AndMakesNoFurtherStart()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "crash-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (state, node, cache, runtime) = CrashScenario(dataPath);
            using var monitor = RecoveryMonitor(new FakeModelRegistry([Model("a", "pc", false)]), runtime, state, cache);
            await monitor.StartAsync(TestContext.Current.CancellationToken);
            await PolledAsync(runtime);

            for (int crash = 1; crash <= 2; crash++)
            {
                runtime.Set("a", ModelState.Failed, $"crash {crash}");
                await WaitUntilAsync(() => runtime.Started.Count == crash); // The fake's start reports Ready at once.
                await PolledAsync(runtime);
            }

            runtime.Set("a", ModelState.Failed, "crash 3");
            await PolledAsync(runtime, 10);
            await monitor.StopAsync(TestContext.Current.CancellationToken);

            Assert.Equal(2, runtime.Started.Count);
            var paused = Assert.IsType<CrashRestart>(state.CrashRestartOf("a"));
            Assert.Equal((3, true, "crash 3"), (paused.Crashes, paused.Paused, paused.LastError));
            Assert.False(state.CanAutoRestart(node, "a"));

            // The pause survives a controller restart; a start by hand (ResetCrashes, see RemoteModelRuntime) clears it for good.
            var reopened = new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = dataPath }));
            Assert.True(reopened.CrashRestartOf("a")?.Paused);
            reopened.ResetCrashes("a");
            Assert.Null(new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = dataPath })).CrashRestartOf("a"));
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Crash_ReadyForTheSettlePeriod_ForgetsEarlierCrashes()
    {
        var (state, _, cache, runtime) = CrashScenario(settle: TimeSpan.FromMilliseconds(30));
        using var monitor = RecoveryMonitor(new FakeModelRegistry([Model("a", "pc", false)]), runtime, state, cache);
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await PolledAsync(runtime);

        runtime.Set("a", ModelState.Failed, "crash 1");
        await runtime.StartedTask("a").WaitAsync(Wait);
        Assert.NotNull(state.CrashRestartOf("a"));
        await WaitUntilAsync(() => state.CrashRestartOf("a") is null);
        await monitor.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(ModelState.Stopped)]
    [InlineData(ModelState.Failed)]
    public async Task Crash_AfterAnExplicitStop_IsNeverRestarted(ModelState after)
    {
        var (state, node, cache, runtime) = CrashScenario();
        using var monitor = RecoveryMonitor(new FakeModelRegistry([Model("a", "pc", false)]), runtime, state, cache);
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await PolledAsync(runtime);

        state.Observe(node, "a", ModelState.Stopped); // The acknowledged Stop the runtime records.
        runtime.Set("a", after, after == ModelState.Failed ? "exit 137" : null);
        await PolledAsync(runtime, 10);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Empty(runtime.Started);
        Assert.Null(state.CrashRestartOf("a"));
    }

    [Fact]
    public async Task Crash_WithTheSwitchOff_LeavesTheModelFailed()
    {
        var (state, _, cache, runtime) = CrashScenario();
        using var monitor = RecoveryMonitor(new FakeModelRegistry([Model("a", "pc", false)]), runtime, state, cache, crashRestart: false);
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await PolledAsync(runtime);

        runtime.Set("a", ModelState.Failed, "crash");
        await PolledAsync(runtime, 10);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Empty(runtime.Started);
    }

    [Fact]
    public async Task Monitor_StartsSequentiallyWithinANode_AndInParallelAcrossNodes()
    {
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(5);
        var models = new[] { Model("dgx-a", "dgx"), Model("dgx-b", "dgx"), Model("pc-a", "pc-office"), Model("pc-b", "pc-office") };
        var runtime = new NodeAwareRuntime(models) { RejectOverlapOnNode = true };
        runtime.HoldStarting("dgx-a");
        using var monitor = Monitor(new FakeModelRegistry(models), runtime);

        await monitor.StartAsync(TestContext.Current.CancellationToken);

        // While dgx-a holds the dgx start slot, both pc-office models start one after the other.
        await runtime.StartedTask("pc-b").WaitAsync(Wait);
        Assert.Equal(ModelState.Starting, (await runtime.GetStatusAsync("dgx-a")).State);
        Assert.DoesNotContain("dgx-b", runtime.Started);

        runtime.Release("dgx-a");
        await runtime.StartedTask("dgx-b").WaitAsync(Wait);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["dgx-a", "dgx-b"], runtime.Started.Where(id => id.StartsWith("dgx", StringComparison.Ordinal)));
        Assert.Equal(["pc-a", "pc-b"], runtime.Started.Where(id => id.StartsWith("pc", StringComparison.Ordinal)));
        Assert.Empty(runtime.Conflicts); // the monitor waited for its node instead of colliding
        Assert.Equal(1, runtime.MaxStartingPerNode);
    }

    [Fact]
    public async Task Monitor_UnplacedModelsShareTheBootstrapNodesLane()
    {
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(5);
        var models = new[] { Model("placed", "dgx"), Model("unplaced", node: null) };
        var runtime = new NodeAwareRuntime(models) { RejectOverlapOnNode = true };
        runtime.HoldStarting("placed");
        using var monitor = Monitor(new FakeModelRegistry(models), runtime);

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await runtime.StartedTask("placed").WaitAsync(Wait);
        await Task.Delay(100);
        Assert.DoesNotContain("unplaced", runtime.Started);

        runtime.Release("placed");
        await runtime.StartedTask("unplaced").WaitAsync(Wait);
        await monitor.StopAsync(TestContext.Current.CancellationToken);
        Assert.Empty(runtime.Conflicts);
    }

    [Fact]
    public async Task Monitor_StartConflict_IsRetriedUntilTheSlotFrees()
    {
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(5);
        var models = new[] { Model("a", "dgx") };
        var runtime = new NodeAwareRuntime(models) { ConflictsBeforeStart = 2 };
        using var monitor = Monitor(new FakeModelRegistry(models), runtime);

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await runtime.StartedTask("a").WaitAsync(Wait);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a", "a", "a"], runtime.Attempts);
    }

    [Fact]
    public async Task Monitor_WaitsForANodeThatIsNotSynchronizedYet_InsteadOfGivingUp()
    {
        // W3 review MN-094: at master boot the node monitor has not synchronized the node yet, so the multi-node runtime
        // refuses commands (NodeUnavailableException, nothing sent); boot auto-start must wait for the node.
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(5);
        var models = new[] { Model("a", "dgx") };
        var runtime = new NodeAwareRuntime(models) { UnsyncedBeforeStart = 3 };
        using var monitor = Monitor(new FakeModelRegistry(models), runtime);

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await runtime.StartedTask("a").WaitAsync(Wait);
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["a", "a", "a", "a"], runtime.Attempts);
    }

    [Fact]
    public async Task Monitor_StartConflictPastTheStartupTimeout_GivesUpOnThatModelOnly()
    {
        ModelHealthMonitor.AutoStartPollInterval = TimeSpan.FromMilliseconds(5);
        var models = new[] { Model("stuck", "dgx"), Model("next", "dgx"), Model("elsewhere", "pc-office") };
        var runtime = new NodeAwareRuntime(models);
        runtime.AlwaysConflict.Add("stuck");
        using var monitor = Monitor(new FakeModelRegistry(models), runtime, startupTimeout: TimeSpan.FromMilliseconds(200));

        await monitor.StartAsync(TestContext.Current.CancellationToken);
        await runtime.StartedTask("next").WaitAsync(Wait);
        await runtime.StartedTask("elsewhere").WaitAsync(Wait);
        await Task.WhenAny(monitor.ExecuteTask!, Task.Delay(100));
        await monitor.StopAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("stuck", runtime.Started);
        Assert.True(runtime.Attempts.Count(id => id == "stuck") > 1, "the conflict was retried before giving up");
        Assert.False(monitor.ExecuteTask!.IsFaulted);
    }

    [Fact]
    public async Task Restore_AutoStartIsSerializedPerNode_AndAnotherNodeDoesNotWait()
    {
        var a = Model("a", "dgx");
        var b = Model("b", "dgx");
        var c = Model("c", "pc-office");
        var runtime = new NodeAwareRuntime([a, b, c]) { RejectOverlapOnNode = true };
        runtime.HoldStarting("a");
        var folders = new FakeCheckpointFolders("a-folder", "b-folder", "c-folder");
        var service = new RestoreJobService(new FakeModelRegistry(), new FakeApiKeyStore(), runtime, new SequencedDownloader(folders), folders,
            new RestoreJobOptions { PollInterval = TimeSpan.FromMilliseconds(1), StartRetryInterval = TimeSpan.FromMilliseconds(5), StartRetryTimeout = Wait },
            TimeProvider.System, NullLogger<RestoreJobService>.Instance);
        var backup = new BackupDocument { SchemaVersion = BackupDocument.MultiNodeSchemaVersion, Models = [a, b, c] };

        service.Start(new RestoreRequest(backup, null, ["a", "b", "c"], true, false, false));

        // c (pc-office) starts while b waits behind a on dgx.
        await runtime.StartedTask("c").WaitAsync(Wait);
        await WaitUntilAsync(() => Row(service, "b").State == RestoreModelState.WaitingToStart && runtime.Conflicts.Contains("b"));
        Assert.Equal(RestoreModelState.Started, Row(service, "c").State);
        Assert.DoesNotContain("b", runtime.Started);
        Assert.True(service.Current!.IsRunning);

        runtime.Release("a");
        await service.Completion.WaitAsync(Wait);

        Assert.Equal(RestoreJobState.Completed, service.Current!.State);
        Assert.All(service.Current.Models, m => Assert.Equal(RestoreModelState.Started, m.State));
        Assert.Equal(["a", "c", "b"], runtime.Started);
        Assert.Equal(1, runtime.MaxStartingPerNode);
        Assert.DoesNotContain("c", runtime.Conflicts);
    }

    [Fact]
    public async Task Restore_StartCallsNeverOverlapWithinANode()
    {
        var models = new[] { Model("a", "dgx"), Model("b", "dgx"), Model("c", "pc-office"), Model("d", "pc-office") };
        var runtime = new NodeAwareRuntime(models) { StartCallDelay = TimeSpan.FromMilliseconds(100) };
        var folders = new FakeCheckpointFolders("a-folder", "b-folder", "c-folder", "d-folder");
        var service = new RestoreJobService(new FakeModelRegistry(), new FakeApiKeyStore(), runtime, new SequencedDownloader(folders), folders,
            new RestoreJobOptions { PollInterval = TimeSpan.FromMilliseconds(1), StartRetryInterval = TimeSpan.FromMilliseconds(5), StartRetryTimeout = Wait },
            TimeProvider.System, NullLogger<RestoreJobService>.Instance);

        service.Start(new RestoreRequest(new BackupDocument { Models = models }, null, ["a", "b", "c", "d"], true, false, false));
        await service.Completion.WaitAsync(Wait);

        Assert.Equal(1, runtime.MaxCallsInFlightPerNode);
        Assert.Equal(2, runtime.MaxCallsInFlight);
        Assert.All(service.Current!.Models, m => Assert.Equal(RestoreModelState.Started, m.State));
    }

    [Fact]
    public async Task Lanes_AFailedStartDoesNotBlockTheNextOnTheSameNode()
    {
        var lanes = new PerNodeStartLanes();
        var order = new List<string>();

        var failed = lanes.Enqueue("dgx", () => Task.FromException(new InvalidOperationException("boom")));
        var next = lanes.Enqueue("dgx", () =>
        {
            order.Add("next");
            return Task.CompletedTask;
        });

        await next.WaitAsync(Wait);
        await lanes.WhenAllSettledAsync();
        Assert.True(failed.IsFaulted);
        Assert.Equal(["next"], order);
        Assert.Equal("dgx", PerNodeStartLanes.NodeOf(Model("x", null), "dgx"));
        Assert.Equal("pc-office", PerNodeStartLanes.NodeOf(Model("x", "pc-office"), "dgx"));
    }

    private static RestoreModelProgress Row(RestoreJobService service, string id) => service.Current!.Models.Single(m => m.Id == id);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    /// A runtime that knows each model's node, like the agent behind <c>RemoteModelRuntime</c>: a started model stays
    /// Starting until released (when held) or reports Ready right away; with <see cref="RejectOverlapOnNode"/> a start
    /// while another model on the same node is Starting is refused with <see cref="ModelStartConflictException"/>.
    /// </summary>
    private sealed class NodeAwareRuntime(IEnumerable<ModelDefinition> models) : IModelRuntime
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, string> _nodeOf = models.ToDictionary(m => m.Id, m => PerNodeStartLanes.NodeOf(m, "dgx"));
        private readonly Dictionary<string, ModelState> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string?> _errors = new(StringComparer.Ordinal);
        private readonly HashSet<string> _held = new(StringComparer.Ordinal);
        private int _listCalls;
        private readonly Dictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _callsInFlight = new(StringComparer.Ordinal);

        public bool RejectOverlapOnNode { get; init; }

        public int ConflictsBeforeStart { get; set; }

        public TimeSpan StartCallDelay { get; init; }

        public HashSet<string> AlwaysConflict { get; } = new(StringComparer.Ordinal);

        /// <summary>Start attempts answered with a transient <see cref="NodeUnavailableException"/> (node not synchronized yet).</summary>
        public int UnsyncedBeforeStart { get; set; }

        public Action<string>? BeforeGetStatus { get; set; }

        public List<string> Attempts { get; } = [];

        public List<string> Started { get; } = [];

        /// <summary>The ambient audit actor of each successful start (null outside a scope).</summary>
        public List<string?> StartActors { get; } = [];

        public List<string> Conflicts { get; } = [];

        public int ListCalls => Volatile.Read(ref _listCalls);

        public int MaxStartingPerNode { get; private set; }

        public int MaxCallsInFlightPerNode { get; private set; }

        public int MaxCallsInFlight { get; private set; }

        public void HoldStarting(string id)
        {
            lock (_gate)
            {
                _held.Add(id);
            }
        }

        public void Fail(string id) => Set(id, ModelState.Failed);

        /// <summary>What the node reports for the model from now on, e.g. a container that died with <paramref name="error"/>.</summary>
        public void Set(string id, ModelState state, string? error = null)
        {
            lock (_gate)
            {
                _states[id] = state;
                _errors[id] = error;
            }
        }

        public void Release(string id)
        {
            lock (_gate)
            {
                _held.Remove(id);
                if (_states.GetValueOrDefault(id) == ModelState.Starting)
                {
                    _states[id] = ModelState.Ready;
                }
            }
        }

        public Task StartedTask(string id)
        {
            lock (_gate)
            {
                return Tcs(id).Task;
            }
        }

        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                var list = _states.Select(kv => new ModelStatus(kv.Key, kv.Value, Error: _errors.GetValueOrDefault(kv.Key))).ToList();
                Interlocked.Increment(ref _listCalls);
                return Task.FromResult<IReadOnlyList<ModelStatus>>(list);
            }
        }

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default)
        {
            BeforeGetStatus?.Invoke(id);
            lock (_gate)
            {
                return Task.FromResult(new ModelStatus(id, _states.GetValueOrDefault(id, ModelState.Stopped), Error: _errors.GetValueOrDefault(id)));
            }
        }

        public async Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
        {
            string node = _nodeOf[id];
            lock (_gate)
            {
                Attempts.Add(id);
                _callsInFlight[node] = _callsInFlight.GetValueOrDefault(node) + 1;
                MaxCallsInFlightPerNode = Math.Max(MaxCallsInFlightPerNode, _callsInFlight[node]);
                MaxCallsInFlight = Math.Max(MaxCallsInFlight, _callsInFlight.Values.Sum());
            }

            try
            {
                if (StartCallDelay > TimeSpan.Zero)
                {
                    await Task.Delay(StartCallDelay, ct);
                }

                lock (_gate)
                {
                    if (UnsyncedBeforeStart > 0)
                    {
                        UnsyncedBeforeStart--;
                        throw new NodeUnavailableException($"Node '{node}' is offline: the command for model '{id}' was not sent.")
                        {
                            Reason = NodeUnavailableReason.NotServing,
                            NodeId = node,
                        };
                    }

                    var starting = _states.Where(kv => kv.Value == ModelState.Starting && _nodeOf[kv.Key] == node).Select(kv => kv.Key).FirstOrDefault();
                    if (AlwaysConflict.Contains(id) || ConflictsBeforeStart > 0 || (RejectOverlapOnNode && starting is not null))
                    {
                        ConflictsBeforeStart = Math.Max(0, ConflictsBeforeStart - 1);
                        Conflicts.Add(id);
                        throw new ModelStartConflictException($"Model '{starting ?? "other"}' is still starting on node {node}.")
                        {
                            StartingModelId = starting ?? "other",
                        };
                    }

                    _states[id] = _held.Contains(id) ? ModelState.Starting : ModelState.Ready;
                    _errors.Remove(id);
                    StartActors.Add(AuditScope.Current?.Actor);
                    MaxStartingPerNode = Math.Max(MaxStartingPerNode,
                        _states.Count(kv => kv.Value == ModelState.Starting && _nodeOf[kv.Key] == node));
                    Started.Add(id);
                    Tcs(id).TrySetResult();
                    return new ModelStatus(id, ModelState.Starting);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _callsInFlight[node]--;
                }
            }
        }

        public Task StopAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998

        private TaskCompletionSource Tcs(string id)
        {
            if (!_started.TryGetValue(id, out var tcs))
            {
                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _started[id] = tcs;
            }

            return tcs;
        }
    }

    private sealed class RecoveryCache(params NodeLiveStatus[] nodes) : INodeStatusCache
    {
        private readonly ConcurrentDictionary<string, NodeLiveStatus> _nodes = new(nodes.ToDictionary(n => n.NodeId));

        public long Version => 0;

        public void Set(NodeLiveStatus node) => _nodes[node.NodeId] = node;

        public IReadOnlyList<NodeLiveStatus> ListNodes() => [.. _nodes.Values];

        public NodeLiveStatus? GetNode(string nodeId) => _nodes.GetValueOrDefault(nodeId);

        public NodeModelRuntime? GetRuntime(string modelId) => ListRuntimes().FirstOrDefault(r => r.ModelId == modelId);

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) =>
            [.. _nodes.Values.Where(n => nodeId is null || n.NodeId == nodeId).SelectMany(n => n.Runtimes)];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
