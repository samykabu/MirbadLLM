using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore cancellation.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-038: the master half of the telemetry split (design §8.1). <see cref="RemoteTelemetryCollector"/> pulls each
/// node's ring through <see cref="IAgentClient.GetTelemetryAsync"/>, back-fills after partitions in time order, records
/// honest gaps after agent restarts and evictions, and computes the same KPIs as the pre-P1 single-host sampler did
/// from the same scrapes (recorded fixture, MN-047, D60); the remote probes answer from the latest fresh sample. The agent side here is the real
/// <see cref="TelemetryRing"/> behind a scripted client, plus one end-to-end run over <see cref="FakeAgent"/>.
/// </summary>
public sealed class TelemetryCollectorTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    // ---- Host series keyed by node ----

    [Fact]
    public async Task HostSamplesAreKeyedByNode_AndTheOldApiReadsTheDefaultNode()
    {
        var env = new Env();
        env.AddNode("dgx");
        env.AddNode("pc");
        env.Agent("dgx").Append(env.Time.GetUtcNow(), availableGiB: 70);
        env.Agent("pc").Append(env.Time.GetUtcNow(), availableGiB: 20);

        await env.Collector.CollectOnceAsync();

        Assert.Equal(70, Assert.Single(env.History.GetHost("dgx", Hour)).Memory.AvailableGiB);
        Assert.Equal(20, Assert.Single(env.History.GetHost("pc", Hour)).Memory.AvailableGiB);
        Assert.Equal(70, Assert.Single(env.History.GetHost(Hour)).Memory.AvailableGiB);
        Assert.Equal(["dgx", "pc"], env.History.HostNodeIds());
        Assert.Equal("dgx", env.History.DefaultNodeId);
    }

    [Fact]
    public async Task UnapprovedAndRevokedNodesAreNotPulled()
    {
        var env = new Env();
        env.AddNode("dgx");
        env.AddNode("pending", approved: false);
        env.AddNode("gone", revoked: true);
        foreach (string id in new[] { "dgx", "pending", "gone" })
        {
            env.Agent(id).Append(env.Time.GetUtcNow());
        }

        await env.Collector.CollectOnceAsync();

        Assert.Single(env.History.GetHost("dgx", Hour));
        Assert.Empty(env.Agent("pending").Queries);
        Assert.Empty(env.Agent("gone").Queries);
    }

    [Fact]
    public async Task AModelPlacedOnAnotherNodeIsIgnored_AndUnknownModelsAreIgnored()
    {
        var env = new Env();
        env.Registry.Add(new ModelDefinition { Id = "here", ServedName = "Here", ModelPath = "p", Placement = new ModelPlacement("dgx") });
        env.Registry.Add(new ModelDefinition { Id = "there", ServedName = "There", ModelPath = "p", Placement = new ModelPlacement("pc") });
        env.AddNode("dgx");
        env.Agent("dgx").Append(env.Time.GetUtcNow(), models: [("here", Text(1)), ("there", Text(1)), ("tuning-candidate", Text(1))]);

        await env.Collector.CollectOnceAsync();

        Assert.Single(env.History.GetModel("here", Hour));
        Assert.Empty(env.History.GetModel("there", Hour));
        Assert.Empty(env.History.GetModel("tuning-candidate", Hour));
    }

    // ---- Partitions, restarts, evictions ----

    [Fact]
    public async Task APartitionIsBackFilledWithTheRealSamples_InTimeOrder_WithoutAGap()
    {
        var env = new Env();
        env.Registry.Add(new ModelDefinition { Id = "m", ServedName = "M", ModelPath = "p", MemoryBudgetGiB = 10 });
        env.AddNode("dgx");
        var agent = env.Agent("dgx");
        var times = new List<DateTimeOffset>();

        times.Add(env.Time.GetUtcNow());
        agent.Append(times[^1], models: [("m", Text(1))]);
        await env.Collector.CollectOnceAsync();

        agent.Partitioned = true;
        for (int step = 2; step <= 7; step++)
        {
            env.Time.Advance(Interval);
            times.Add(env.Time.GetUtcNow());
            agent.Append(times[^1], models: [("m", Text(step))]);
            await env.Collector.CollectOnceAsync(); // fails: the cursor stays at seq 1
        }

        Assert.Single(env.History.GetHost("dgx", Hour));

        agent.Partitioned = false;
        await env.Collector.CollectOnceAsync();

        Assert.Equal(times, env.History.GetHost("dgx", Hour).Select(s => s.At));
        Assert.Empty(env.History.GetGaps("dgx", Hour));
        var model = env.History.GetModel("m", Hour);
        Assert.Equal(times, model.Select(s => s.At));
        Assert.Equal([false, true, true, true, true, true, true], model.Select(s => s.HasRates));
        Assert.All(model.Skip(1), s => Assert.Equal(100.0 / 5, s.Kpis.Load.GenerationTokensPerSecond));

        var last = agent.Queries[^1];
        Assert.Equal(agent.BootId, last.BootId);
        Assert.Equal(1, last.AfterSeq);
    }

    [Fact]
    public async Task AnAgentRestartIsAnHonestGap_AndRatesStartAFreshBaseline()
    {
        var env = new Env();
        env.Registry.Add(new ModelDefinition { Id = "m", ServedName = "M", ModelPath = "p" });
        env.AddNode("dgx");
        var agent = env.Agent("dgx");
        var t0 = env.Time.GetUtcNow();
        for (int i = 0; i < 3; i++)
        {
            agent.Append(t0 + (Interval * i), models: [("m", Text(i + 1))]);
        }

        await env.Collector.CollectOnceAsync();
        string oldBoot = agent.BootId;

        // The agent is down for a minute (nothing sampled), then a new boot samples again; counters keep growing.
        env.Time.Advance(TimeSpan.FromSeconds(75));
        agent.Restart();
        agent.Append(t0 + TimeSpan.FromSeconds(70), models: [("m", Text(20))]);
        agent.Append(t0 + TimeSpan.FromSeconds(75), models: [("m", Text(21))]);
        await env.Collector.CollectOnceAsync();

        var gap = Assert.Single(env.History.GetGaps("dgx", Hour));
        Assert.Equal(new MetricsGap(t0 + TimeSpan.FromSeconds(10), t0 + TimeSpan.FromSeconds(70), MetricsGap.AgentRestarted), gap);
        var hosts = env.History.GetHost("dgx", Hour);
        Assert.Equal(5, hosts.Count);
        Assert.DoesNotContain(hosts, h => h.At > gap.From && h.At < gap.To);
        Assert.Equal([false, true, true, false, true], env.History.GetModel("m", Hour).Select(s => s.HasRates));

        Assert.Equal((oldBoot, 3L), (agent.Queries[^1].BootId, agent.Queries[^1].AfterSeq));
        await env.Collector.CollectOnceAsync();
        Assert.Equal((agent.BootId, 2L), (agent.Queries[^1].BootId, agent.Queries[^1].AfterSeq));
        Assert.Single(env.History.GetGaps("dgx", Hour));
    }

    [Fact]
    public async Task ARestartSeenBeforeTheNewBootHasSamples_RecordsTheGapWithTheFirstSample()
    {
        var env = new Env();
        env.AddNode("dgx");
        var agent = env.Agent("dgx");
        var t0 = env.Time.GetUtcNow();
        agent.Append(t0);
        await env.Collector.CollectOnceAsync();

        agent.Restart();
        await env.Collector.CollectOnceAsync(); // new boot, empty ring
        Assert.Empty(env.History.GetGaps("dgx", Hour));

        env.Time.Advance(TimeSpan.FromSeconds(30));
        agent.Append(t0 + TimeSpan.FromSeconds(30));
        await env.Collector.CollectOnceAsync();

        Assert.Equal(new MetricsGap(t0, t0 + TimeSpan.FromSeconds(30), MetricsGap.AgentRestarted), Assert.Single(env.History.GetGaps("dgx", Hour)));
        Assert.Equal(2, env.History.GetHost("dgx", Hour).Count);
    }

    [Fact]
    public async Task SamplesEvictedDuringALongPartitionAreAGap_TheKeptOnesAreBackFilled()
    {
        var env = new Env(ringCapacity: 3);
        env.Registry.Add(new ModelDefinition { Id = "m", ServedName = "M", ModelPath = "p" });
        env.AddNode("dgx");
        var agent = env.Agent("dgx");
        var t0 = env.Time.GetUtcNow();
        agent.Append(t0, models: [("m", Text(1))]);
        await env.Collector.CollectOnceAsync();

        agent.Partitioned = true;
        for (int i = 1; i <= 5; i++)
        {
            agent.Append(t0 + (Interval * i), models: [("m", Text(i + 1))]); // the ring keeps seq 4..6
        }

        env.Time.Advance(Interval * 5);
        await env.Collector.CollectOnceAsync();
        agent.Partitioned = false;
        await env.Collector.CollectOnceAsync();

        Assert.Equal(new MetricsGap(t0, t0 + (Interval * 3), MetricsGap.SamplesDropped), Assert.Single(env.History.GetGaps("dgx", Hour)));
        Assert.Equal([t0, t0 + (Interval * 3), t0 + (Interval * 4), t0 + (Interval * 5)], env.History.GetHost("dgx", Hour).Select(s => s.At));
        Assert.Equal([false, false, true, true], env.History.GetModel("m", Hour).Select(s => s.HasRates));
    }

    [Fact]
    public async Task ALongBackFillIsPulledInPages_InOnePass()
    {
        var env = new Env();
        env.AddNode("dgx");
        var agent = env.Agent("dgx");
        var t0 = env.Time.GetUtcNow() - TimeSpan.FromMinutes(10);
        for (int i = 0; i < 450; i++)
        {
            agent.Append(t0 + TimeSpan.FromSeconds(i));
        }

        await env.Collector.CollectOnceAsync();

        var hosts = env.History.GetHost("dgx", Hour);
        Assert.Equal(450, hosts.Count);
        Assert.True(hosts.Zip(hosts.Skip(1)).All(p => p.First.At < p.Second.At));
        Assert.Equal([null, 200L, 400L], agent.Queries.Select(q => q.AfterSeq));
        Assert.All(agent.Queries, q => Assert.Equal(RemoteTelemetryCollector.PageSize, q.Max));
    }

    [Fact]
    public async Task ARegistryFailureSkipsThePass_AndTheNextPassBackFills()
    {
        var env = new Env();
        env.AddNode("dgx");
        var agent = env.Agent("dgx");
        agent.Append(env.Time.GetUtcNow());
        env.Registry.ListThrows = new IOException("disk");

        await env.Collector.CollectOnceAsync();
        Assert.Empty(env.History.GetHost("dgx", Hour));
        Assert.Empty(agent.Queries);

        env.Registry.ListThrows = null;
        env.Time.Advance(Interval);
        agent.Append(env.Time.GetUtcNow());
        await env.Collector.CollectOnceAsync();

        Assert.Equal(2, env.History.GetHost("dgx", Hour).Count);
    }

    [Fact]
    public async Task AModelRemovedFromTheRegistryLosesItsHistory()
    {
        var env = new Env();
        env.Registry.Add(new ModelDefinition { Id = "m", ServedName = "M", ModelPath = "p" });
        env.AddNode("dgx");
        env.Agent("dgx").Append(env.Time.GetUtcNow(), models: [("m", Text(1))]);
        await env.Collector.CollectOnceAsync();
        Assert.Single(env.History.GetModel("m", Hour));

        await env.Registry.DeleteAsync("m");
        await env.Collector.CollectOnceAsync();

        Assert.Empty(env.History.GetModel("m", Hour));
    }

    // ---- KPI and memory parity with the pre-P1 sampler (recorded) ----

    /// <summary>
    /// The pre-P1 single-host sampler (<c>MetricsSampler</c>, deleted by MN-047, D60) ran on the same five scrapes (one per
    /// 5 s pass from <c>2026-10-04T00:00:00Z</c>, model budget 12 GiB, memory 128/90 GiB, GPU 40 %/30 W/50 °C/1500 MHz);
    /// <c>Multinode/Fixtures/legacy-metrics-sampler-kpis.json</c> holds its model series, latest KPIs and host series,
    /// recorded before its removal.
    /// </summary>
    private static JsonNode LegacySamplerRecording([CallerFilePath] string sourceFile = "") =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "legacy-metrics-sampler-kpis.json")))!;

    [Fact]
    public async Task KpisEqualTheRecordedLegacySamplerFromTheSameScrapes()
    {
        var recording = LegacySamplerRecording();
        var start = DateTimeOffset.Parse(recording["start"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var registry = new FakeModelRegistry().Add(new ModelDefinition { Id = "m", ServedName = "M", ModelPath = "p", MemoryBudgetGiB = 12 });
        var time = new ManualTimeProvider();
        time.Advance(start - time.GetUtcNow());
        var texts = Enumerable.Range(1, 5).Select(RichText).ToArray();

        // The agent took the same scrapes at the same times; the master back-fills them in one pull.
        var env = new Env(time, registry);
        env.AddNode("dgx");
        for (int i = 0; i < texts.Length; i++)
        {
            env.Agent("dgx").Append(start + (Interval * i), totalGiB: 128, availableGiB: 90, models: [("m", texts[i])],
                gpus: [new GpuSample { Index = 0, UtilizationPercent = 40, PowerWatts = 30, TemperatureC = 50, SmClockMHz = 1500 }]);
        }

        time.Advance(Interval * texts.Length);
        await env.Collector.CollectOnceAsync();

        AssertRecorded(recording["model"]!, env.History.GetModel("m", Hour));
        AssertRecorded(recording["latest"]!, env.History.Latest());
        AssertRecorded(recording["host"]!, env.History.GetHost("dgx", Hour));
    }

    private static void AssertRecorded<T>(JsonNode expected, T actual)
    {
        var node = JsonNode.Parse(Json(actual))!;
        Assert.True(JsonNode.DeepEquals(expected, node), $"legacy:    {expected.ToJsonString()}\ncollector: {node.ToJsonString()}");
    }

    [Fact]
    public async Task AReadyModelThatIsNeverScraped_StillHoldsItsMemory()
    {
        // MLX, SGLang and NIM have no engine metrics, so a scrape never lists them.
        var registry = new FakeModelRegistry()
            .Add(new ModelDefinition { Id = "sg", ServedName = "SG", ModelPath = "p", MemoryBudgetGiB = 24, Runtime = RuntimeKind.Sglang });
        var cache = new FakeStatusCache();
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "sg", NodeId = "dgx", State = ModelState.Ready });
        var env = new Env(registry: registry, statusCache: cache);
        env.AddNode("dgx");
        env.Agent("dgx").Append(env.Time.GetUtcNow(), totalGiB: 121, availableGiB: 40, models: []);

        await env.Collector.CollectOnceAsync();

        var host = Assert.Single(env.History.GetHost("dgx", Hour));
        Assert.Equal(new Dictionary<string, double> { ["sg"] = 24 }, host.ModelMemoryGiB);
    }

    [Fact]
    public async Task HostSampleBudgetsEveryModelWhoseContainerHoldsMemory()
    {
        // Ported from MetricsSamplerPerModelTests: Ready (scraped), Starting and Stopping hold their budget; Stopped and
        // Failed do not.
        var registry = new FakeModelRegistry()
            .Add(new ModelDefinition { Id = "ready", ServedName = "Ready", ModelPath = "p", MemoryBudgetGiB = 48 })
            .Add(new ModelDefinition { Id = "starting", ServedName = "Starting", ModelPath = "p", MemoryBudgetGiB = 20 })
            .Add(new ModelDefinition { Id = "stopping", ServedName = "Stopping", ModelPath = "p", MemoryBudgetGiB = 8 })
            .Add(new ModelDefinition { Id = "stopped", ServedName = "Stopped", ModelPath = "p", MemoryBudgetGiB = 30 })
            .Add(new ModelDefinition { Id = "failed", ServedName = "Failed", ModelPath = "p", MemoryBudgetGiB = 12 });
        var cache = new FakeStatusCache();
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "ready", NodeId = "dgx", State = ModelState.Ready });
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "starting", NodeId = "dgx", State = ModelState.Starting });
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "stopping", NodeId = "dgx", State = ModelState.Stopping });
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "stopped", NodeId = "dgx", State = ModelState.Stopped });
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "failed", NodeId = "dgx", State = ModelState.Failed });
        var env = new Env(registry: registry, statusCache: cache);
        env.AddNode("dgx");
        env.Agent("dgx").Append(env.Time.GetUtcNow(), totalGiB: 121, availableGiB: 40, models: [("ready", Text(1))]);

        await env.Collector.CollectOnceAsync();

        var host = Assert.Single(env.History.GetHost("dgx", Hour));
        Assert.Equal(ModelMemorySource.Budget, host.MemorySource);
        Assert.Equal(new Dictionary<string, double> { ["ready"] = 48, ["starting"] = 20, ["stopping"] = 8 }, host.ModelMemoryGiB);
        Assert.Equal(81, host.Memory.TotalGiB - host.Memory.AvailableGiB);
    }

    [Fact]
    public async Task FirstScrapeHasNoRates_TheNextDoes_AndAModelRestartStartsAFreshBaseline()
    {
        // Ported from MetricsSamplerPerModelTests, with the values the pre-P1 sampler produced: one sample while the model
        // restarts (not scraped), then Ready again with reset counters, which are not diffed across the restart.
        var env = new Env(registry: new FakeModelRegistry().Add(new ModelDefinition { Id = "m", ServedName = "M", ModelPath = "p" }));
        env.AddNode("dgx");
        string?[] scrapes = [Tokens(100, 10), Tokens(600, 60), null, Tokens(50, 5), Tokens(250, 25)];
        foreach (string? scrape in scrapes)
        {
            env.Agent("dgx").Append(env.Time.GetUtcNow(), models: scrape is null ? [] : [("m", scrape)]);
            await env.Collector.CollectOnceAsync();
            env.Time.Advance(Interval);
        }

        var samples = env.History.GetModel("m", Hour);
        Assert.Equal([false, true, false, true], samples.Select(s => s.HasRates));
        Assert.Equal(100.0, samples[1].Kpis.Load.GenerationTokensPerSecond); // (600-100)/5
        Assert.Equal(40.0, samples[3].Kpis.Load.GenerationTokensPerSecond); // (250-50)/5, not diffed across the restart
        Assert.Equal(4.0, samples[3].Kpis.Load.PromptTokensPerSecond);
    }

    [Fact]
    public async Task AFailedScrapeOfOneModelDoesNotAffectTheOthers()
    {
        // Ported from MetricsSamplerTests: the failing model gets no sample, the healthy one and the host sample do.
        var registry = new FakeModelRegistry()
            .Add(new ModelDefinition { Id = "healthy", ServedName = "Healthy", ModelPath = "p" })
            .Add(new ModelDefinition { Id = "broken", ServedName = "Broken", ModelPath = "p" });
        var env = new Env(registry: registry);
        env.AddNode("dgx");
        env.Agent("dgx").Append(env.Time.GetUtcNow(), models: [("healthy", Text(1)), ("broken", null)]);

        await env.Collector.CollectOnceAsync();

        Assert.Single(env.History.GetModel("healthy", Hour));
        Assert.Empty(env.History.GetModel("broken", Hour));
        Assert.True(env.History.Latest().ContainsKey("healthy"));
        Assert.False(env.History.Latest().ContainsKey("broken"));
        Assert.Single(env.History.GetHost("dgx", Hour));
    }

    [Fact]
    public async Task ModelMemoryIsMeasuredWhileTheNodeReportsProcesses_ThenFallsBackToTheBudget()
    {
        // Ported from GpuProcessMemoryTests (the pre-P1 sampler with the host agent's process probe): a Starting model's
        // container processes are measured; a sample without a process readout shows the budget.
        const string container = "960769d2c75febb6efba9d52da72b559ec2d1165c8bfb0410b3052ec6b63ff91";
        var cache = new FakeStatusCache();
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "qwen", NodeId = "dgx", State = ModelState.Starting, InstanceId = container });
        var registry = new FakeModelRegistry().Add(new ModelDefinition { Id = "qwen", ServedName = "Qwen", ModelPath = "p", MemoryBudgetGiB = 48 });
        var env = new Env(registry: registry, statusCache: cache);
        env.AddNode("dgx");

        env.Agent("dgx").Append(env.Time.GetUtcNow(), processes: [new GpuProcess(3414913, "VLLM::EngineCore", 30720, container, null)]);
        await env.Collector.CollectOnceAsync();
        env.Time.Advance(Interval);
        env.Agent("dgx").Append(env.Time.GetUtcNow(), processes: null);
        await env.Collector.CollectOnceAsync();

        var host = env.History.GetHost("dgx", Hour);
        Assert.Equal(ModelMemorySource.Measured, host[0].MemorySource);
        Assert.Equal(30, host[0].ModelMemoryGiB!["qwen"]);
        Assert.Equal(ModelMemorySource.Budget, host[1].MemorySource);
        Assert.Equal(48, host[1].ModelMemoryGiB!["qwen"]);
    }

    [Fact]
    public async Task ModelMemoryIsMeasuredWhenTheStatusCacheKnowsTheContainers_ElseBudgeted()
    {
        const string container = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var registry = new FakeModelRegistry()
            .Add(new ModelDefinition { Id = "m", ServedName = "M", ModelPath = "p", MemoryBudgetGiB = 30 })
            .Add(new ModelDefinition { Id = "s", ServedName = "S", ModelPath = "p", MemoryBudgetGiB = 8 });
        GpuProcess[] processes =
        [
            new(100, "python3", 20 * 1024, container, null),
            new(200, "ollama", 2 * 1024, null, "ollama.service"),
        ];

        var cache = new FakeStatusCache();
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "m", NodeId = "dgx", State = ModelState.Ready, InstanceId = container[..12] });
        cache.Runtimes.Add(new NodeModelRuntime { ModelId = "s", NodeId = "dgx", State = ModelState.Starting, InstanceId = "fedcba9876543210" });
        var measured = new Env(registry: registry, statusCache: cache);
        measured.AddNode("dgx");
        measured.Agent("dgx").Append(measured.Time.GetUtcNow(), models: [("m", Text(1))], processes: processes);
        await measured.Collector.CollectOnceAsync();

        var host = Assert.Single(measured.History.GetHost("dgx", Hour));
        Assert.Equal(ModelMemorySource.Measured, host.MemorySource);
        Assert.Equal(new Dictionary<string, double> { ["m"] = 20, ["s"] = 0 }, host.ModelMemoryGiB);
        Assert.Equal(new Dictionary<string, double> { ["ollama"] = 2 }, host.OtherGpuGiB);

        var budgeted = new Env(registry: registry);
        budgeted.AddNode("dgx");
        budgeted.Agent("dgx").Append(budgeted.Time.GetUtcNow(), models: [("m", Text(1))], processes: processes);
        await budgeted.Collector.CollectOnceAsync();

        host = Assert.Single(budgeted.History.GetHost("dgx", Hour));
        Assert.Equal(ModelMemorySource.Budget, host.MemorySource);
        Assert.Equal(new Dictionary<string, double> { ["m"] = 30 }, host.ModelMemoryGiB);
    }

    // ---- History: time order ----

    [Fact]
    public void HistoryInsertsLateSamplesInTimeOrder_AndLatestIsTheNewest()
    {
        var time = new ManualTimeProvider();
        var history = new InMemoryMetricsHistory(Options.Create(new MetricsOptions()), time);
        var t0 = time.GetUtcNow();
        foreach (int offset in new[] { 0, 20, 10, 5, 15 })
        {
            history.AddHost("pc", new HostSample(t0 + TimeSpan.FromSeconds(offset), new MemorySnapshot(64, offset), null));
            history.AddModel("m", new ModelSample(t0 + TimeSpan.FromSeconds(offset), Kpis(offset)));
        }

        Assert.Equal([0.0, 5, 10, 15, 20], history.GetHost("pc", Hour).Select(s => s.Memory.AvailableGiB));
        Assert.Equal([0.0, 5, 10, 15, 20], history.GetModel("m", Hour).Select(s => s.Kpis.Load.RunningRequests));
        Assert.Equal(20, history.Latest()["m"].Load.RunningRequests);
        Assert.Empty(history.GetHost(Hour)); // the default node (dgx) has no samples
    }

    // ---- Remote probes ----

    [Fact]
    public async Task RemoteProbesAnswerFromTheLatestFreshSample()
    {
        var cache = new FakeStatusCache();
        var env = new Env(statusCache: cache);
        env.AddNode("dgx");
        var memory = new RemoteMemoryProbe(env.Collector, "dgx", env.Time, cache);
        var gpu = new RemoteGpuProbe(env.Collector, "dgx", env.Time);
        var processes = new RemoteGpuProcessProbe(env.Collector, "dgx", env.Time);

        await Assert.ThrowsAsync<NodeTelemetryUnavailableException>(() => memory.GetAsync());
        Assert.Null(await gpu.GetAsync());
        Assert.Null(await processes.GetAsync());

        env.Agent("dgx").Append(env.Time.GetUtcNow(), totalGiB: 128, availableGiB: 77,
            gpus: [new GpuSample { Index = 1, UtilizationPercent = 99 }, new GpuSample { Index = 0, UtilizationPercent = 12, PowerWatts = 40 }],
            processes: [new GpuProcess(1, "python3", 1024, null, null)]);
        await env.Collector.CollectOnceAsync();

        Assert.Equal(new MemorySnapshot(128, 77), await memory.GetAsync());
        Assert.Equal(new GpuSnapshot(12, 40, null, null), await gpu.GetAsync());
        Assert.Equal(1024, Assert.Single((await processes.GetAsync())!).UsedMiB);

        // The agent stops reporting: a stale sample is not shown as current.
        env.Time.Advance(env.Collector.MaxSampleAge + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<NodeTelemetryUnavailableException>(() => memory.GetAsync());
        Assert.Null(await gpu.GetAsync());
        Assert.Null(await processes.GetAsync());

        // Memory falls back to the status cache while the node is serving.
        cache.Nodes["dgx"] = new NodeLiveStatus
        {
            NodeId = "dgx", NodeUid = "uid", State = NodeState.Online,
            Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 128, HostAvailableGiB = 50 },
        };
        Assert.Equal(new MemorySnapshot(128, 50), await memory.GetAsync());
        cache.Nodes["dgx"] = cache.Nodes["dgx"] with { State = NodeState.Offline };
        await Assert.ThrowsAsync<NodeTelemetryUnavailableException>(() => memory.GetAsync());
    }

    [Fact]
    public void AddRemoteTelemetryReplacesTheLocalProbes_WithTheBootstrapNodes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(1, 1));
        services.AddSingleton<IGpuProbe>(new FakeGpuProbe(null));
        services.AddSingleton<InMemoryMetricsHistory>();
        services.AddSingleton<INodeRegistry>(new FakeNodeRegistry());
        services.AddSingleton<IModelRegistry>(new FakeModelRegistry());
        services.AddSingleton<IAgentClientFactory>(new ScriptedAgents(TimeProvider.System, 1000));
        services.Configure<RegistryMigrationOptions>(o => o.Nodes.BootstrapNodeId = "spark");

        services.AddRemoteTelemetry();
        using var provider = services.BuildServiceProvider();

        Assert.Equal("spark", Assert.IsType<RemoteMemoryProbe>(provider.GetRequiredService<IMemoryProbe>()).NodeId);
        Assert.Equal("spark", Assert.IsType<RemoteGpuProbe>(provider.GetRequiredService<IGpuProbe>()).NodeId);
        Assert.Equal("spark", Assert.IsType<RemoteGpuProcessProbe>(provider.GetRequiredService<IGpuProcessProbe>()).NodeId);
        Assert.Equal("spark", provider.GetRequiredService<InMemoryMetricsHistory>().DefaultNodeId);
        var collector = provider.GetRequiredService<RemoteTelemetryCollector>();
        Assert.Same(collector, provider.GetRequiredService<INodeTelemetry>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => ReferenceEquals(s, collector));
    }

    // ---- End to end over FakeAgent (real mTLS + HTTP/2 client) ----

    [Fact]
    public async Task EndToEnd_OverFakeAgent_BackFillsAPartition_AndRecordsARestartGap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions { TelemetryInterval = TimeSpan.FromMilliseconds(40) }, ct);
        string dataDir = Path.Combine(Path.GetTempPath(), "mn038-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            using var nodes = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), TimeProvider.System);
            await nodes.UpsertAsync(new NodeRecord
            {
                Id = agent.NodeId, Uid = agent.NodeUid, Address = agent.BaseAddress.AbsoluteUri, Approved = true, CertSpkiSha256 = agent.CurrentSpki,
            }, ct);
            using var master = agent.Ca.IssueMasterClientCertificate();
            using var trust = MirbadLLM.Pki.TrustAnchorSet.Create([agent.Ca.Certificate]);
            await using var factory = new AgentClientFactory(nodes, new StaticMasterIdentity(), master, trust, TimeProvider.System);
            await factory.RefreshAsync(ct);
            var history = new InMemoryMetricsHistory(Options.Create(new MetricsOptions()), TimeProvider.System);
            using var collector = new RemoteTelemetryCollector(nodes, factory, new FakeModelRegistry(), history,
                Options.Create(new MetricsOptions()), TimeProvider.System, NullLogger<RemoteTelemetryCollector>.Instance);

            await collector.CollectOnceAsync(ct);
            int before = history.GetHost(Hour).Count;
            Assert.True(before >= 1);

            agent.SetPartitioned(true);
            await collector.CollectOnceAsync(ct); // cut at the transport
            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
            agent.SetPartitioned(false);
            await collector.CollectOnceAsync(ct);

            var hosts = history.GetHost(Hour);
            Assert.True(hosts.Count >= before + 5, $"expected the partition to be back-filled, got {hosts.Count} sample(s)");
            Assert.True(hosts.Zip(hosts.Skip(1)).All(p => p.First.At <= p.Second.At));
            Assert.True(hosts.Zip(hosts.Skip(1)).Max(p => p.Second.At - p.First.At) < TimeSpan.FromSeconds(2));
            Assert.Empty(history.GetGaps("dgx", Hour));
            Assert.Equal(128, hosts[^1].Memory.TotalGiB);
            Assert.NotNull(collector.GetLatest("dgx"));

            await agent.RestartAsync();
            await collector.CollectOnceAsync(ct);

            var gap = Assert.Single(history.GetGaps("dgx", Hour));
            Assert.Equal(MetricsGap.AgentRestarted, gap.Reason);
            Assert.Equal(agent.BootId, collector.GetLatest("dgx")!.BootId);
        }
        finally
        {
            try
            {
                Directory.Delete(dataDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    // ---- Helpers ----

    /// <summary>Counters that grow by 100 generated / 10 prompt tokens per step.</summary>
    private static string Text(int step) => string.Create(CultureInfo.InvariantCulture,
        $"vllm:num_requests_running{{model_name=\"m\"}} 1\nvllm:generation_tokens_total{{model_name=\"m\"}} {step * 100}\nvllm:prompt_tokens_total{{model_name=\"m\"}} {step * 10}\n");

    /// <summary>Generation and prompt token counters at the given totals.</summary>
    private static string Tokens(double generated, double prompt) =>
        FormattableString.Invariant($"vllm:generation_tokens_total {generated}\nvllm:prompt_tokens_total {prompt}\n");

    /// <summary>A scrape with counters, gauges and histograms, so every KPI path is exercised.</summary>
    private static string RichText(int step) => string.Create(CultureInfo.InvariantCulture, $$"""
        vllm:num_requests_running{model_name="m"} {{step % 3}}
        vllm:num_requests_waiting{model_name="m"} {{step % 2}}
        vllm:kv_cache_usage_perc{model_name="m"} {{0.1 * step}}
        vllm:generation_tokens_total{model_name="m"} {{1000 * step * step}}
        vllm:prompt_tokens_total{model_name="m"} {{500 * step}}
        vllm:request_success_total{model_name="m",finished_reason="stop"} {{10 * step}}
        vllm:request_success_total{model_name="m",finished_reason="length"} {{2 * step}}
        vllm:prefix_cache_hits_total{model_name="m"} {{40 * step}}
        vllm:prefix_cache_queries_total{model_name="m"} {{55 * step}}
        vllm:num_preemptions_total{model_name="m"} {{step}}
        vllm:tool_call_parser_invocations_total{model_name="m",outcome="tool_call"} {{3 * step}}
        vllm:time_to_first_token_seconds_bucket{model_name="m",le="0.1"} {{step}}
        vllm:time_to_first_token_seconds_bucket{model_name="m",le="0.5"} {{4 * step}}
        vllm:time_to_first_token_seconds_bucket{model_name="m",le="1.0"} {{9 * step}}
        vllm:time_to_first_token_seconds_bucket{model_name="m",le="+Inf"} {{10 * step}}
        vllm:time_to_first_token_seconds_sum{model_name="m"} {{3 * step}}
        vllm:time_to_first_token_seconds_count{model_name="m"} {{10 * step}}
        vllm:e2e_request_latency_seconds_bucket{model_name="m",le="1.0"} {{2 * step}}
        vllm:e2e_request_latency_seconds_bucket{model_name="m",le="5.0"} {{8 * step}}
        vllm:e2e_request_latency_seconds_bucket{model_name="m",le="+Inf"} {{12 * step}}
        vllm:e2e_request_latency_seconds_sum{model_name="m"} {{30 * step}}
        vllm:e2e_request_latency_seconds_count{model_name="m"} {{12 * step}}

        """);

    private static ModelKpis Kpis(double running) => new(
        new ModelMetrics(running, 0, 0, 0, 0), 0, null, null, null, null, null, null, 0, new Dictionary<string, double>(), 0);

    private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>Collector, history, registries and scripted agents on one manual clock.</summary>
    private sealed class Env
    {
        public Env(ManualTimeProvider? time = null, FakeModelRegistry? registry = null, INodeStatusCache? statusCache = null, int ringCapacity = 1000)
        {
            Time = time ?? new ManualTimeProvider();
            Registry = new ThrowingModelRegistry(registry ?? new FakeModelRegistry());
            Agents = new ScriptedAgents(Time, ringCapacity);
            History = new InMemoryMetricsHistory(Options.Create(new MetricsOptions()), Time);
            Collector = new RemoteTelemetryCollector(Nodes, Agents, Registry, History, Options.Create(new MetricsOptions()), Time,
                NullLogger<RemoteTelemetryCollector>.Instance, statusCache);
        }

        public ManualTimeProvider Time { get; }

        public ThrowingModelRegistry Registry { get; }

        public FakeNodeRegistry Nodes { get; } = new();

        public ScriptedAgents Agents { get; }

        public InMemoryMetricsHistory History { get; }

        public RemoteTelemetryCollector Collector { get; }

        public void AddNode(string id, bool approved = true, bool revoked = false)
        {
            Nodes.Records[id] = new NodeRecord { Id = id, Uid = "uid-" + id, Address = "https://127.0.0.1:9443", Approved = approved, Revoked = revoked };
            Agents.Add(id);
        }

        public ScriptedAgent Agent(string id) => Agents.Nodes[id];
    }

    /// <summary>A model registry whose list can be made to fail.</summary>
    private sealed class ThrowingModelRegistry(FakeModelRegistry inner) : IModelRegistry
    {
        public Exception? ListThrows { get; set; }

        public ThrowingModelRegistry Add(ModelDefinition model)
        {
            inner.Add(model);
            return this;
        }

        public Task<IReadOnlyList<ModelDefinition>> ListAsync(CancellationToken ct = default) =>
            ListThrows is { } error ? throw error : inner.ListAsync(ct);

        public Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);

        public Task UpsertAsync(ModelDefinition model, CancellationToken ct = default) => inner.UpsertAsync(model, ct);

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
    }

    private sealed class StaticMasterIdentity : IMasterIdentity
    {
        public string InstanceId => "instance-mn038";

        public long Epoch => 1;

        public string BootId => "boot-mn038";

        public MasterTriple Current => new(InstanceId, Epoch, BootId);

        public bool TakeoverRequested => false;

        public IReadOnlyList<TakeoverRecord> Takeovers => [];

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }
}

/// <summary>Node records in memory; only listing is used by the collector.</summary>
internal sealed class FakeNodeRegistry : INodeRegistry
{
    public Dictionary<string, NodeRecord> Records { get; } = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodeRecord>>([.. Records.Values]);

    public Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(Records.GetValueOrDefault(id));

    public Task UpsertAsync(NodeRecord node, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>A status cache with settable nodes and runtimes; only reads are used.</summary>
internal sealed class FakeStatusCache : INodeStatusCache
{
    public Dictionary<string, NodeLiveStatus> Nodes { get; } = new(StringComparer.Ordinal);

    public List<NodeModelRuntime> Runtimes { get; } = [];

    public long Version => 0;

    public IReadOnlyList<NodeLiveStatus> ListNodes() => [.. Nodes.Values];

    public NodeLiveStatus? GetNode(string nodeId) => Nodes.GetValueOrDefault(nodeId);

    public NodeModelRuntime? GetRuntime(string modelId) => Runtimes.FirstOrDefault(r => r.ModelId == modelId);

    public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) => [.. Runtimes.Where(r => nodeId is null || r.NodeId == nodeId)];

    public ServedModelRoute? ResolveServedName(string servedName) => null;

    public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Scripted agents by node id.</summary>
internal sealed class ScriptedAgents(TimeProvider time, int ringCapacity) : IAgentClientFactory
{
    public Dictionary<string, ScriptedAgent> Nodes { get; } = new(StringComparer.Ordinal);

    public void Add(string nodeId) => Nodes[nodeId] = new ScriptedAgent(nodeId, time, ringCapacity);

    public IAgentClient GetClient(string nodeId) =>
        Nodes.TryGetValue(nodeId, out var agent) ? agent : throw new KeyNotFoundException(nodeId);

    public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

    public void RebuildHandlers(string nodeId) => throw new NotSupportedException();

    public void SetNegotiatedProtocol(string nodeId, int version) => throw new NotSupportedException();
}

/// <summary>
/// An agent whose telemetry is the real <see cref="TelemetryRing"/> (cursor rules, eviction, boot-aware gaps); a
/// partition fails the pull at the transport. Every other route is not used by the collector.
/// </summary>
internal sealed class ScriptedAgent(string nodeId, TimeProvider time, int ringCapacity) : IAgentClient
{
    private TelemetryRing _ring = NewRing(ringCapacity);

    public string NodeId { get; } = nodeId;

    public bool Partitioned { get; set; }

    public List<TelemetryQuery> Queries { get; } = [];

    public string BootId => _ring.BootId;

    public IAgentDataClient Data => throw new NotSupportedException();

    /// <summary>A new agent boot: new boot id, empty ring, seq from 1.</summary>
    public void Restart() => _ring = NewRing(ringCapacity);

    public NodeSample Append(
        DateTimeOffset at,
        double totalGiB = 128,
        double availableGiB = 64,
        IReadOnlyList<(string Model, string? Text)>? models = null,
        IReadOnlyList<GpuSample>? gpus = null,
        IReadOnlyList<GpuProcess>? processes = null) =>
        _ring.Append(new NodeSample
        {
            Seq = 0,
            At = at,
            Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = totalGiB, HostAvailableGiB = availableGiB },
            Gpus = gpus ?? [],
            GpuProcesses = processes,
            Models = [.. (models ?? []).Select(m => new ModelMetricsScrape { ModelId = m.Model, At = at, Text = m.Text, Error = m.Text is null ? "scrape failed" : null })],
        });

    public Task<TelemetryPage> GetTelemetryAsync(TelemetryQuery query, CancellationToken ct = default)
    {
        if (Partitioned)
        {
            throw new AgentUnreachableException(NodeId, "Partitioned.", null);
        }

        Queries.Add(query);
        _ = time;
        return Task.FromResult(_ring.Read(query.BootId, query.AfterSeq, query.Max ?? 180));
    }

    private static TelemetryRing NewRing(int capacity) =>
        new("boot-" + Guid.NewGuid().ToString("N")[..8], Options.Create(new TelemetryOptions { RingCapacity = capacity, RingWindow = TimeSpan.FromHours(1) }));

    public Task<InfoResponse> GetInfoAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<NodeCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<StatusSnapshot> GetStatusAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public IAsyncEnumerable<AgentEvent> SubscribeEventsAsync(EventsQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<GpuIdleResponse> SampleGpuIdleAsync(GpuIdleQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<RuntimeStatus>> ListRuntimesAsync(RuntimesQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<RuntimeStatus?> GetRuntimeAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> LaunchAsync(string modelId, LaunchRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> StopAsync(string modelId, StopRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

    public IAsyncEnumerable<string> StreamLogsAsync(string modelId, LogsQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ImageInfo>> ListImagesAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> PullImageAsync(ImagePullRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ModelFolderEntry>> ListModelFoldersAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<ModelFolderInspection?> InspectModelFolderAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> DeleteModelFolderAsync(string folder, FolderDeleteRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<NodeDownloadStatus> StartDownloadAsync(DownloadStartRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<NodeDownloadStatus>> ListDownloadsAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<NodeDownloadStatus?> GetDownloadAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> CancelDownloadAsync(string folder, DownloadCancelRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus?> GetOperationAsync(string operationId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<OperationStatus>> ListOperationsAsync(OperationsQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> CancelOperationAsync(string operationId, OperationCancelRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<HostServicesResponse> ListHostServicesAsync(HostServicesQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> RunHostServiceAsync(string name, HostServiceAction action, HostServiceActionRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> SetHostServiceAutostartAsync(string name, HostServiceAutostartRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> AddToWhitelistAsync(HostWhitelistAddRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> UpdateWhitelistAsync(string name, HostWhitelistUpdateRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> RemoveFromWhitelistAsync(string name, HostWhitelistRemoveRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<HostContainerInfo?> GetContainerAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> RunContainerAsync(string name, HostServiceAction action, HostContainerActionRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> SetContainerAutostartAsync(string name, HostContainerAutostartRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> SetBackupMountAsync(BackupMountSetRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> RemoveBackupMountAsync(BackupMountRemoveRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<BackupMountTestResult> TestBackupMountAsync(BackupMountTestRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> StartRotationAsync(PkiRotationStartRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> InstallRotationCertificateAsync(PkiRotationCertRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> CommitRotationAsync(PkiRotationCommitRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> AbortRotationAsync(PkiRotationAbortRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<PkiRotationStatus?> GetRotationAsync(string rotationId, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<PkiTrustResponse> GetTrustAsync(CancellationToken ct = default) => throw new NotSupportedException();

    public Task<OperationStatus> SetTrustAsync(PkiTrustSetRequest request, CancellationToken ct = default) => throw new NotSupportedException();
}
