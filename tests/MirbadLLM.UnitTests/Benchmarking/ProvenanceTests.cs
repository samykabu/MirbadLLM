using System.Net;
using Bunit;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>
/// MN-112 (design §6.5 items 4–5): noise scoped to the resources the measured model shares (its node; its GPUs on a
/// discrete node), contention read from the request counters as well as the running count, incomplete telemetry reported
/// as "noise unknown", provenance recorded on every benchmark and tuning run, comparisons and ETAs limited to runs on the
/// same hardware, and results stored before MN-112 still loading unchanged.
/// </summary>
public sealed class ProvenanceTests : IDisposable
{
    private const string Dgx = "dgx";
    private const string Office = "pc-office";
    private const string GpuA = "GPU-00000000-0000-0000-0000-000000005070";
    private const string GpuB = "GPU-00000000-0000-0000-0000-000000005060";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-provenance-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    // ================================================================ the suite's noise check

    private static readonly BenchmarkTarget Target = new("qwen", "Qwen", new Uri("http://ss-qwen:8000/"), 8192);

    /// <summary>Long gaps allowed: the samples straddle the (instant) fake runs by minutes.</summary>
    private static BenchmarkSuiteOptions Options(TimeSpan? maxGap = null, TimeSpan? settle = null) => new()
    {
        Levels = [1, 2],
        Runs = 2,
        MaxTokens = 4,
        IncludePrefill = false,
        TelemetryMaxGap = maxGap ?? TimeSpan.FromHours(2),
        TelemetrySettleTimeout = settle ?? TimeSpan.Zero,
        TelemetrySettlePoll = TimeSpan.FromMilliseconds(10),
    };

    private static NoiseScope Scope(params string[] running) => new(Dgx, running, running, []);

    private static ModelKpis Kpis(double running = 0, double requestsPerMinute = 0, double generationTokS = 0, double promptTokS = 0) =>
        new(new ModelMetrics(running, 0, 0.1, generationTokS, promptTokS), requestsPerMinute, null, null, null, null, null, null, 0,
            new Dictionary<string, double>(), 0);

    /// <summary>A scrape a minute before the runs and one long after (rates covering the runs): complete telemetry.</summary>
    private static IReadOnlyList<ModelSample> Around(ModelKpis during, ModelKpis? before = null) =>
    [
        new ModelSample(DateTimeOffset.UtcNow.AddMinutes(-1), before ?? Kpis()),
        new ModelSample(DateTimeOffset.UtcNow.AddMinutes(30), during),
    ];

    private static FakeMetricsHistory History(params (string Id, IReadOnlyList<ModelSample> Samples)[] series)
    {
        var history = new FakeMetricsHistory
        {
            LatestKpis = series.ToDictionary(s => s.Id, s => s.Samples[^1].Kpis, StringComparer.Ordinal),
        };
        foreach (var (id, samples) in series)
        {
            history.ModelSamplesById[id] = samples;
        }

        return history;
    }

    private static BenchmarkSuite Suite(IMetricsHistory? history, BenchmarkSuiteOptions? options = null) =>
        new(new HttpClient(new FakeVllm()), history, options ?? Options());

    [Fact]
    public async Task LoadOnAnotherNodeIsNotNoiseOnlyTheModelsSharingTheNodeAreRead()
    {
        // gemma shares the node and is quiet; phi runs on another node and is busy.
        var history = History(("qwen", Around(Kpis(running: 1))), ("gemma", Around(Kpis())), ("phi", Around(Kpis(running: 5, requestsPerMinute: 60))));

        var scoped = await Suite(history).RunAsync(Target with { Scope = Scope("gemma") }, null, Ct);

        Assert.False(scoped.Noise.Noisy);
        Assert.Equal(BenchmarkNoise.Quiet, scoped.Noise.Verdict);
        Assert.Empty(scoped.Noise.Reasons);
        Assert.Null(scoped.Noise.Unknown);
        Assert.Equal(["gemma"], scoped.Noise.Considered);
        Assert.Equal(["gemma"], scoped.Noise.OtherModelsMaxRunning.Keys);
        Assert.All(scoped.Levels, l => Assert.False(l.Noisy));

        // Without a scope (P1 wiring) every model in the history counts, so phi's load marks the same runs noisy.
        var fleetWide = await Suite(history).RunAsync(Target, null, Ct);
        Assert.Equal(BenchmarkNoise.Noisy, fleetWide.Noise.Verdict);
        Assert.Contains("phi up to 5 running", fleetWide.Noise.Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABurstSeenOnlyByTheCountersIsNoise()
    {
        // Never more than 0 running at a scrape, but the scrape after the runs shows finished requests: a short burst.
        var history = History(("qwen", Around(Kpis(running: 1))), ("gemma", Around(Kpis(running: 0, requestsPerMinute: 12))));

        var result = await Suite(history).RunAsync(Target with { Scope = Scope("gemma") }, null, Ct);

        Assert.True(result.Noise.Noisy);
        Assert.Equal(BenchmarkNoise.Noisy, result.Noise.Verdict);
        Assert.Equal("noisy: other models on node dgx served requests during the runs (gemma)", Assert.Single(result.Noise.Reasons));
        Assert.Equal(0, result.Noise.OtherModelsMaxRunning["gemma"]);
        Assert.All(result.Levels.SelectMany(l => l.Runs), r => Assert.True(r.OtherModelsServed));
        Assert.All(result.Levels, l => Assert.True(l.Noisy));
    }

    [Fact]
    public async Task TokenCountersAloneAreNoiseTooAndOnADiscreteNodeTheReasonNamesTheGpus()
    {
        var history = History(("qwen", Around(Kpis(running: 1))), ("gemma", Around(Kpis(generationTokS: 40))));

        var result = await Suite(history).RunAsync(Target with { Scope = new NoiseScope(Office, ["gemma"], ["gemma"], [GpuA]) }, null, Ct);

        Assert.Equal(BenchmarkNoise.Noisy, result.Noise.Verdict);
        Assert.Contains("other models sharing its GPUs served requests", Assert.Single(result.Noise.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunningNeighbourWithoutTelemetryIsNoiseUnknownNotQuiet()
    {
        var history = History(("qwen", Around(Kpis(running: 1))));

        var result = await Suite(history).RunAsync(Target with { Scope = Scope("gemma") }, null, Ct);

        Assert.False(result.Noise.Noisy);
        Assert.Empty(result.Noise.Reasons);
        Assert.Equal(BenchmarkNoise.Unknown, result.Noise.Verdict);
        Assert.Equal("noise unknown: incomplete telemetry for gemma during the runs", Assert.Single(result.Noise.Unknown!));
    }

    [Fact]
    public async Task AGapOrAScrapeWithoutRatesDuringTheRunsIsNoiseUnknown()
    {
        // A 31-minute gap with a 15-minute limit.
        var gap = History(("qwen", Around(Kpis(running: 1))), ("gemma", Around(Kpis())));
        var gapped = await Suite(gap, Options(maxGap: TimeSpan.FromMinutes(15))).RunAsync(Target with { Scope = Scope("gemma") }, null, Ct);
        Assert.Equal(BenchmarkNoise.Unknown, gapped.Noise.Verdict);
        Assert.Contains(gapped.Noise.Unknown!, u => u.Contains("gemma", StringComparison.Ordinal));

        // The scrape covering the runs has no rates (the first after a restart): its counters say nothing.
        var noRates = History(("qwen", Around(Kpis(running: 1))),
            ("gemma", [new ModelSample(DateTimeOffset.UtcNow.AddMinutes(-1), Kpis()), new ModelSample(DateTimeOffset.UtcNow.AddMinutes(30), Kpis()) { HasRates = false }]));
        var restarted = await Suite(noRates).RunAsync(Target with { Scope = Scope("gemma") }, null, Ct);
        Assert.Equal(BenchmarkNoise.Unknown, restarted.Noise.Verdict);
    }

    [Fact]
    public async Task AStoppedNeighbourIsNeitherNoiseNorUnknown()
    {
        // gemma shares the node but has no instance (not running when the scope was taken, never in the history).
        var history = History(("qwen", Around(Kpis(running: 1))));

        var result = await Suite(history).RunAsync(Target with { Scope = new NoiseScope(Dgx, ["gemma"], [], []) }, null, Ct);

        Assert.Equal(BenchmarkNoise.Quiet, result.Noise.Verdict);
        Assert.Empty(result.Noise.OtherModelsMaxRunning);
        Assert.Equal(["gemma"], result.Noise.Considered);
    }

    [Fact]
    public async Task NoMetricsHistoryOrNoMetricsOfTheMeasuredModelIsNoiseUnknown()
    {
        var none = await Suite(history: null).RunAsync(Target with { Scope = Scope() }, null, Ct);
        Assert.Equal(BenchmarkNoise.Unknown, none.Noise.Verdict);
        Assert.Equal("noise unknown: no metrics history to check the runs against", Assert.Single(none.Noise.Unknown!));

        var notSelf = await Suite(History(("gemma", Around(Kpis())))).RunAsync(Target with { Scope = Scope("gemma") }, null, Ct);
        Assert.Equal(BenchmarkNoise.Unknown, notSelf.Noise.Verdict);
        Assert.Equal("noise unknown: the metrics of qwen do not cover every run", Assert.Single(notSelf.Noise.Unknown!));
    }

    [Fact]
    public async Task TheSuiteWaitsForTheScrapeAfterTheLastRunSoTheTailOfTheRunIsCovered()
    {
        // The sampler is live (a scrape just before the runs) but its next scrape comes only after the suite asks again.
        var lagging = new LaggingHistory("qwen");
        var waited = await Suite(lagging, Options(maxGap: TimeSpan.FromMinutes(5), settle: TimeSpan.FromSeconds(30)))
            .RunAsync(Target with { Scope = Scope() }, null, Ct);
        Assert.Equal(BenchmarkNoise.Quiet, waited.Noise.Verdict);
        Assert.True(lagging.Appended);

        // Without the wait the last run is not covered yet: noise unknown, not quiet.
        var notWaited = await Suite(new LaggingHistory("qwen"), Options(maxGap: TimeSpan.FromMinutes(5)))
            .RunAsync(Target with { Scope = Scope() }, null, Ct);
        Assert.Equal(BenchmarkNoise.Unknown, notWaited.Noise.Verdict);
    }

    [Fact]
    public async Task TheEmbeddingsBenchmarkIsScopedAndCounterCheckedToo()
    {
        var history = History(("emb", Around(Kpis(running: 4))), ("gemma", Around(Kpis(promptTokS: 900))), ("phi", Around(Kpis(running: 9))));
        var target = new BenchmarkTarget("emb", "Emb", new Uri("http://ss-emb:8000/"), 8192, PoolingMode.Embedding, 16) { Scope = Scope("gemma") };
        var suite = new BenchmarkSuite(new HttpClient(new FakeVllm()), history, new BenchmarkSuiteOptions
        {
            Runs = 1, EmbeddingLevels = [1], EmbeddingMaxConcurrency = 1, EmbeddingInputsPerRequest = 4, EmbeddingInputWords = [4],
            TelemetryMaxGap = TimeSpan.FromHours(2), TelemetrySettleTimeout = TimeSpan.Zero,
        });

        var result = await suite.RunAsync(target, null, Ct);

        Assert.Equal(BenchmarkNoise.Noisy, result.Noise.Verdict);
        Assert.DoesNotContain("phi", string.Join(" ", result.Noise.Reasons), StringComparison.Ordinal);
        var run = Assert.Single(Assert.Single(result.Embeddings!.Levels).Runs);
        Assert.True(run.OtherModelsServed);
        Assert.True(result.Embeddings.Levels[0].Noisy);
    }

    // ================================================================ the provenance source

    private static NodeCapabilities Capabilities(MemoryModel model, params GpuCapability[] gpus) => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "1.0.0",
        Os = new NodeOsInfo("linux", "Ubuntu 24.04"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(model, 64),
        Gpus = gpus,
    };

    private static NodeLiveStatus Node(string id, string uid, NodeCapabilities capabilities) =>
        new() { NodeId = id, NodeUid = uid, State = NodeState.Online, Capabilities = capabilities };

    private static NodeModelRuntime Instance(string modelId, string nodeId, ModelState state = ModelState.Ready, params string[] gpus) =>
        new() { ModelId = modelId, NodeId = nodeId, State = state, GpuUuids = gpus };

    private static ModelDefinition Model(string id, string nodeId, params string[] gpus) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = id,
        Placement = new ModelPlacement(nodeId, gpus.Length > 0 ? gpus : null),
    };

    private static readonly NodeLiveStatus DgxNode = Node(Dgx, "uid-dgx", Capabilities(MemoryModel.Unified,
        new GpuCapability { Index = 0, Uuid = "GPU-gb10", Name = "NVIDIA GB10", SharesHostMemory = true }));

    private static readonly NodeLiveStatus OfficeNode = Node(Office, "uid-office", Capabilities(MemoryModel.Discrete,
        new GpuCapability { Index = 0, Uuid = GpuB, Name = "NVIDIA GeForce RTX 5060 Ti", MemoryTotalGiB = 16 },
        new GpuCapability { Index = 1, Uuid = GpuA, Name = "NVIDIA GeForce RTX 5070", MemoryTotalGiB = 12 }));

    [Fact]
    public async Task OnAUnifiedNodeTheProvenanceHasNoGpusAndTheScopeIsEveryOtherModelOnTheNode()
    {
        var cache = new Cache([DgxNode, OfficeNode],
        [
            Instance("qwen", Dgx) with
            {
                ConfigHash = "hash-qwen",
                ExecutionFingerprint = "fp-qwen",
                Labels = new Dictionary<string, string> { [ContainerLaunchValidator.ImageDigestLabel] = "sha256:" + new string('a', 64) },
            },
            Instance("gemma", Dgx),
            Instance("leftover", Dgx),
            Instance("embed", Dgx, ModelState.Stopped),
            Instance("phi", Office, ModelState.Ready, GpuA),
        ]);
        var registry = new FakeModelRegistry().Add(Model("qwen", Dgx)).Add(Model("gemma", Dgx)).Add(Model("embed", Dgx)).Add(Model("phi", Office, GpuA))
            .Add(new ModelDefinition { Id = "unplaced", ServedName = "u", ModelPath = "u" });
        var source = new NodeBenchmarkProvenanceSource(cache, registry, Dgx);

        var measured = await source.DescribeAsync("qwen", new BenchmarkEndpoint(new Uri("https://dgx/"), BenchmarkDataPaths.AgentLoopback, Dgx), Ct);

        var p = measured.Provenance;
        Assert.Equal(Dgx, p.NodeId);
        Assert.Equal("uid-dgx", p.NodeUid);
        Assert.Empty(p.Gpus);
        Assert.Equal(RuntimeKind.Vllm, p.Runtime);
        Assert.Equal(LaunchDelivery.Container, p.Delivery);
        Assert.Equal("sha256:" + new string('a', 64), p.ImageDigest);
        Assert.Equal("hash-qwen", p.ConfigHash);
        Assert.Equal("fp-qwen", p.ExecutionFingerprint);
        Assert.Equal(BenchmarkDataPaths.AgentLoopback, p.Proxy);
        Assert.Equal(BenchmarkNoise.Unknown, p.Noise);

        // An unplaced model counts as the bootstrap node's; an instance the registry does not know still loads the node.
        Assert.Equal(Dgx, measured.Scope.NodeId);
        Assert.Equal(["embed", "gemma", "leftover", "unplaced"], measured.Scope.SharedWith);
        Assert.Equal(["gemma", "leftover"], measured.Scope.Running);
        Assert.False(measured.Scope.PerGpu);
    }

    [Fact]
    public async Task OnADiscreteNodeTheProvenanceNamesTheGpusAndTheScopeIsTheModelsSharingThemPlusHostOnlyOnes()
    {
        var cache = new Cache([DgxNode, OfficeNode],
        [
            Instance("llama", Office, ModelState.Ready, GpuA),
            Instance("mate", Office, ModelState.Ready, GpuA),
            Instance("other-gpu", Office, ModelState.Ready, GpuB),
            Instance("cpu", Office),
            Instance("qwen", Dgx),
        ]);
        var registry = new FakeModelRegistry().Add(Model("llama", Office, GpuA)).Add(Model("mate", Office, GpuA, GpuB))
            .Add(Model("other-gpu", Office, GpuB)).Add(Model("cpu", Office)).Add(Model("qwen", Dgx));
        var source = new NodeBenchmarkProvenanceSource(cache, registry, Dgx);

        var measured = await source.DescribeAsync("llama", new BenchmarkEndpoint(new Uri("http://ss-llama:8000/"), BenchmarkDataPaths.Direct), Ct);

        // Name and current index from the capability report; the direct path is stored as null (D55).
        var gpu = Assert.Single(measured.Provenance.Gpus);
        Assert.Equal(new ProvenanceGpu(GpuA, "NVIDIA GeForce RTX 5070", 1), gpu);
        Assert.Equal(Office, measured.Provenance.NodeId);
        Assert.Equal("uid-office", measured.Provenance.NodeUid);
        Assert.Null(measured.Provenance.Proxy);

        // mate's instance runs on GPU A only (the instance wins over the placement); other-gpu never touches GPU A.
        Assert.Equal(["cpu", "mate"], measured.Scope.SharedWith);
        Assert.Equal([GpuA], measured.Scope.GpuUuids);
        Assert.True(measured.Scope.PerGpu);

        var expected = source.Expected(Model("llama", Office, GpuA));
        Assert.Equal(Office, expected.NodeId);
        Assert.Equal("uid-office", expected.NodeUid);
        Assert.Equal([GpuA], expected.Gpus.Select(g => g.Uuid));
    }

    // ================================================================ comparisons

    private static BenchmarkProvenance On(string nodeId, string? uid, params string[] gpus) =>
        new() { NodeId = nodeId, NodeUid = uid, Gpus = [.. gpus.Select(g => new ProvenanceGpu(g))] };

    [Fact]
    public void RunsCompareOnlyOnTheSameHardwareAndOldRunsAreTheBootstrapNodes()
    {
        var dgxNew = On(Dgx, "uid-dgx");

        // A run stored before MN-112 (null) was the bootstrap node, direct: comparable with the DGX's new runs.
        Assert.True(BenchmarkComparisons.Comparable(null, dgxNew, Dgx));
        Assert.True(BenchmarkComparisons.Comparable(null, null, Dgx));
        Assert.Null(BenchmarkComparisons.Label(null, dgxNew, Dgx));

        // Another node, a re-installed machine with the same id, other GPUs or GPU order, another runtime: not comparable.
        Assert.False(BenchmarkComparisons.Comparable(null, On(Office, "uid-office", GpuA), Dgx));
        Assert.False(BenchmarkComparisons.Comparable(dgxNew, On(Dgx, "uid-reinstalled"), Dgx));
        Assert.False(BenchmarkComparisons.Comparable(On(Office, "u", GpuA, GpuB), On(Office, "u", GpuB, GpuA), Dgx));
        Assert.False(BenchmarkComparisons.Comparable(dgxNew, dgxNew with { Runtime = RuntimeKind.LlamaCpp }, Dgx));
        Assert.Equal(BenchmarkComparisons.DifferentHardware, BenchmarkComparisons.Label(dgxNew, On(Office, "uid-office"), Dgx));

        // Settings, image, hashes, proxy and noise may differ (that is what tuning compares).
        Assert.True(BenchmarkComparisons.Comparable(dgxNew, dgxNew with { ImageDigest = "sha256:x", ConfigHash = "h", Proxy = "agent-mtls-lan", Noise = BenchmarkNoise.Noisy }, Dgx));

        // With another bootstrap node id, the old runs belong to that node.
        Assert.False(BenchmarkComparisons.Comparable(null, dgxNew, "spark"));
    }

    [Fact]
    public void ATuningRunsVerdictIsTheWorstOfItsCandidates()
    {
        Assert.Equal(BenchmarkNoise.Quiet, BenchmarkComparisons.Combine([BenchmarkNoise.Quiet, BenchmarkNoise.Quiet]));
        Assert.Equal(BenchmarkNoise.Unknown, BenchmarkComparisons.Combine([BenchmarkNoise.Quiet, BenchmarkNoise.Unknown]));
        Assert.Equal(BenchmarkNoise.Noisy, BenchmarkComparisons.Combine([BenchmarkNoise.Unknown, BenchmarkNoise.Noisy, BenchmarkNoise.Quiet]));
        Assert.Equal(BenchmarkNoise.Unknown, BenchmarkComparisons.Combine([]));
    }

    // ================================================================ stored results

    /// <summary>A benchmark file as P1 wrote it (classic suite, no provenance, no counters).</summary>
    private const string StoredV1 = """
        {
          "id": "20260101T000000Z",
          "modelId": "qwen",
          "origin": "OnDemand",
          "startedAt": "2026-01-01T00:00:00+00:00",
          "finishedAt": "2026-01-01T00:10:00+00:00",
          "settings": { "id": "qwen", "servedName": "Qwen", "modelPath": "Qwen-NVFP4" },
          "configHash": "abc",
          "result": {
            "levels": [ { "concurrency": 1, "runs": [ { "concurrency": 1, "completionTokens": 256, "wallSeconds": 4, "perRequestTokS": 64,
              "aggregateTokS": 64, "ttftP50": 0.2, "ttftP95": 0.3, "otherModelsRunningMax": 2, "sameModelRunningMax": 1 } ],
              "perRequestTokS": 64, "aggregateTokS": 64, "ttftP50": 0.2, "ttftP95": 0.3, "noisy": true } ],
            "prefill": null,
            "guardrails": { "toolCallValid": true, "toolCallDetail": "ok", "qaCorrect": 8, "qaTotal": 8, "answers": [] },
            "noise": { "noisy": true, "reasons": [ "noisy: other models were busy (gemma up to 2 running)" ], "idleGpuPercent": 1, "otherModelsMaxRunning": { "gemma": 2 } }
          },
          "error": null,
          "cancelled": false
        }
        """;

    /// <summary>A workload-preset (v2) file as P1 wrote it, through the agent (proxy recorded, no provenance).</summary>
    private const string StoredV2 = """
        {
          "id": "20260201T000000Z", "modelId": "qwen", "origin": "OnDemand",
          "startedAt": "2026-02-01T00:00:00+00:00", "finishedAt": "2026-02-01T00:20:00+00:00",
          "settings": { "id": "qwen", "servedName": "Qwen", "modelPath": "Qwen-NVFP4" }, "configHash": "abc",
          "result": { "levels": [], "prefill": null,
            "guardrails": { "toolCallValid": true, "toolCallDetail": "ok", "qaCorrect": 8, "qaTotal": 8, "answers": [] },
            "noise": { "noisy": false, "reasons": [], "idleGpuPercent": null, "otherModelsMaxRunning": {} },
            "schemaVersion": 2, "preset": "InteractiveChat", "repetitions": 3 },
          "error": null, "cancelled": false, "preset": "InteractiveChat", "proxy": "agent-mtls-loopback"
        }
        """;

    private const string StoredTuning = """
        {
          "id": "20260301T000000Z", "modelId": "qwen", "goal": "Balanced",
          "startedAt": "2026-03-01T00:00:00+00:00", "finishedAt": "2026-03-01T01:00:00+00:00", "status": "Completed",
          "baseline": { "id": "qwen", "servedName": "Qwen", "modelPath": "Qwen-NVFP4" },
          "candidates": [], "winnerIndex": null, "winnerGainPercent": null, "error": null, "notes": []
        }
        """;

    private BenchmarkStore Store() => new(Microsoft.Extensions.Options.Options.Create(new MirbadLLMOptions { DataPath = _dataPath }));

    private void WriteFile(string kind, string id, string json)
    {
        string directory = Path.Combine(_dataPath, kind, "qwen");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), json);
    }

    [Fact]
    public async Task ResultsStoredBeforeProvenanceStillLoadAsTheBootstrapNodesDirectRuns()
    {
        WriteFile("benchmarks", "20260101T000000Z", StoredV1);
        WriteFile("benchmarks", "20260201T000000Z", StoredV2);
        WriteFile("tuning", "20260301T000000Z", StoredTuning);
        var store = Store();

        var runs = await store.ListBenchmarksAsync("qwen", Ct);
        Assert.Equal(["20260201T000000Z", "20260101T000000Z"], runs.Select(r => r.Id));
        var v2 = runs[0];
        var v1 = runs[1];
        Assert.Null(v1.Provenance);
        Assert.Null(v2.Provenance);
        Assert.Equal(BenchmarkNoise.Noisy, v1.Result!.Noise.Verdict);
        Assert.Equal(BenchmarkNoise.Quiet, v2.Result!.Noise.Verdict);
        Assert.Null(v1.Result.Noise.Unknown);
        Assert.False(v1.Result.Levels[0].Runs[0].OtherModelsServed);
        Assert.Equal(2, v1.Result.Levels[0].Runs[0].OtherModelsRunningMax);

        var legacy = BenchmarkComparisons.Of(v2, Dgx);
        Assert.Equal(Dgx, legacy.NodeId);
        Assert.Null(legacy.NodeUid);
        Assert.Equal("abc", legacy.ConfigHash);
        Assert.Equal(BenchmarkDataPaths.AgentLoopback, legacy.Proxy);
        Assert.Equal(BenchmarkNoise.Quiet, legacy.Noise);

        var tuning = Assert.Single(await store.ListTuningAsync("qwen", Ct));
        Assert.Null(tuning.Provenance);
        Assert.Equal(Dgx, BenchmarkComparisons.Of(tuning, Dgx).NodeId);
    }

    [Fact]
    public async Task ProvenanceIsStoredInItsContractShapeAndARunWithoutItKeepsTodaysShape()
    {
        var store = Store();
        var started = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var settings = Model("qwen", Dgx);
        var plain = new BenchmarkRun("20261007T120000Z", "qwen", BenchmarkOrigin.OnDemand, started, started.AddMinutes(5), settings, "h1",
            Results.Make(10, 100), null, false);
        var withProvenance = plain with
        {
            Id = "20261007T130000Z",
            Proxy = BenchmarkDataPaths.AgentLan,
            Provenance = new BenchmarkProvenance
            {
                NodeId = Office,
                NodeUid = "uid-office",
                Gpus = [new ProvenanceGpu(GpuA, "NVIDIA GeForce RTX 5070", 1)],
                ConfigHash = "h1",
                Proxy = BenchmarkDataPaths.AgentLan,
                Noise = BenchmarkNoise.Quiet,
            },
        };
        await store.SaveBenchmarkAsync(plain, Ct);
        await store.SaveBenchmarkAsync(withProvenance, Ct);

        string plainJson = await File.ReadAllTextAsync(Path.Combine(_dataPath, "benchmarks", "qwen", plain.Id + ".json"), Ct);
        foreach (string added in new[] { "\"provenance\"", "\"unknown\"", "\"considered\"", "\"otherModelsServed\"", "\"verdict\"" })
        {
            Assert.DoesNotContain(added, plainJson, StringComparison.Ordinal);
        }

        string json = await File.ReadAllTextAsync(Path.Combine(_dataPath, "benchmarks", "qwen", withProvenance.Id + ".json"), Ct);
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement.GetProperty("provenance");
        Assert.Equal("quiet", p.GetProperty("noise").GetString());
        Assert.Equal("vllm", p.GetProperty("runtime").GetString());
        Assert.Equal("container", p.GetProperty("delivery").GetString());
        Assert.Equal(GpuA, p.GetProperty("gpus")[0].GetProperty("uuid").GetString());
        Assert.False(p.TryGetProperty("imageDigest", out _));
        Assert.False(p.TryGetProperty("executionFingerprint", out _));

        // Read back: the same provenance (and a unified run writes no empty GPU list).
        var read = (await store.ListBenchmarksAsync("qwen", Ct)).First(r => r.Id == withProvenance.Id);
        Assert.Equal(withProvenance.Provenance.NodeUid, read.Provenance!.NodeUid);
        Assert.Equal(withProvenance.Provenance.Gpus, read.Provenance.Gpus);
        Assert.Equal(BenchmarkNoise.Quiet, read.Provenance.Noise);
        Assert.True(read.Provenance.IsComparableWith(withProvenance.Provenance));

        string unified = JsonSerializer.Serialize(new BenchmarkProvenance { NodeId = Dgx, NodeUid = "uid-dgx" }, BenchmarkStore.JsonOptions);
        Assert.DoesNotContain("gpus", unified, StringComparison.Ordinal);
        Assert.DoesNotContain("null", unified, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheStoreListsOnlyComparableRunsForAReference()
    {
        var store = Store();
        var t = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        BenchmarkRun Run(string id, BenchmarkProvenance? provenance) =>
            new(id, "qwen", BenchmarkOrigin.OnDemand, t, t.AddMinutes(1), Model("qwen", Dgx), null, Results.Make(1, 1), null, false) { Provenance = provenance };
        await store.SaveBenchmarkAsync(Run("20261007T120000Z", null), Ct);
        await store.SaveBenchmarkAsync(Run("20261007T130000Z", On(Office, "uid-office", GpuA)), Ct);
        await store.SaveBenchmarkAsync(Run("20261007T140000Z", On(Dgx, "uid-dgx")), Ct);

        var onDgx = await store.ListComparableBenchmarksAsync("qwen", On(Dgx, "uid-dgx"), Dgx, Ct);
        Assert.Equal(["20261007T140000Z", "20261007T120000Z"], onDgx.Select(r => r.Id));
        var onOffice = await store.ListComparableBenchmarksAsync("qwen", On(Office, "uid-office", GpuA), Dgx, Ct);
        Assert.Equal(["20261007T130000Z"], onOffice.Select(r => r.Id));
        Assert.Equal(3, (await store.ListComparableBenchmarksAsync("qwen", null, Dgx, Ct)).Count);
    }

    // ================================================================ jobs record it

    private sealed record JobHarness(ModelJobService Jobs, FakeSuite Suite, BenchmarkStore Store, FakeCluster Cluster, FakeMetricsHistory History);

    private JobHarness Jobs(IBenchmarkProvenanceSource? provenance, params ModelDefinition[] models)
    {
        var registry = new FakeModelRegistry();
        var cluster = new FakeCluster();
        foreach (var model in models)
        {
            registry.Add(model);
            cluster.Running(model);
        }

        var suite = new FakeSuite(cluster);
        var options = Microsoft.Extensions.Options.Options.Create(new MirbadLLMOptions { DataPath = _dataPath, UseContainerNetwork = true, VllmImage = "default-image" });
        var store = new BenchmarkStore(options);
        var history = new FakeMetricsHistory();
        var jobs = new ModelJobService(
            registry, cluster, cluster, suite, store, new MaintenanceModeService(options), history, new FakeGpu(2), new StartupTimeTracker(), options,
            new ModelJobOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                DrainTimeout = TimeSpan.FromMilliseconds(30),
                GpuIdleSampleDuration = TimeSpan.FromMilliseconds(3),
                GpuIdleSampleInterval = TimeSpan.FromMilliseconds(1),
                CandidateStartTimeout = TimeSpan.FromSeconds(10),
            },
            NullLogger<ModelJobService>.Instance,
            new ContainerNetworkTargets(),
            _ => Inspection,
            bootstrapNodeId: Dgx,
            provenance: provenance);
        return new JobHarness(jobs, suite, store, cluster, history);
    }

    private static readonly ModelDefinition Qwen = Model("qwen", Dgx) with { ModelPath = "Qwen-NVFP4", MaxModelLen = 262_144, MaxNumSeqs = 16, MemoryBudgetGiB = 44 };

    private static readonly ModelFolderInspection Inspection = new(
        "Qwen-NVFP4", "Qwen3_5ForConditionalGeneration", 262_144, "NVFP4", 24, MaySupportMtp: true,
        new ModelFolderSuggestion("Qwen", "qwen3_coder", "qwen3", null, 44, ""), KvBytesPerToken: 32_768);

    private static readonly ModelDefinition Gemma = Model("gemma", Dgx) with { MaxModelLen = 8192, MaxNumSeqs = 16, MemoryBudgetGiB = 20 };

    [Fact]
    public async Task ABenchmarkRecordsItsProvenanceAndHandsTheSuiteTheNodeScope()
    {
        var cache = new Cache([DgxNode], [Instance("qwen", Dgx) with { ExecutionFingerprint = "fp-1" }, Instance("gemma", Dgx)]);
        var source = new NodeBenchmarkProvenanceSource(cache, new FakeModelRegistry().Add(Qwen).Add(Gemma), Dgx);
        var h = Jobs(source, Qwen, Gemma);

        await h.Jobs.StartBenchmarkAsync("qwen", Ct);
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListBenchmarksAsync("qwen", Ct));
        Assert.Null(run.Error);
        var p = Assert.IsType<BenchmarkProvenance>(run.Provenance);
        Assert.Equal(Dgx, p.NodeId);
        Assert.Equal("uid-dgx", p.NodeUid);
        Assert.Equal("fp-1", p.ExecutionFingerprint);
        Assert.Equal(run.ConfigHash, p.ConfigHash);
        Assert.Null(p.Proxy);
        Assert.Equal(BenchmarkNoise.Quiet, p.Noise);

        var scope = Assert.IsType<NoiseScope>(Assert.Single(h.Suite.Targets).Scope);
        Assert.Equal(["gemma"], scope.SharedWith);
        Assert.Equal(["gemma"], scope.Running);
    }

    [Fact]
    public async Task ATuningRunRecordsItsProvenanceWithTheWorstVerdictOfItsCandidates()
    {
        var cache = new Cache([DgxNode], [Instance("qwen", Dgx)]);
        var h = Jobs(new NodeBenchmarkProvenanceSource(cache, new FakeModelRegistry().Add(Qwen), Dgx), Qwen);
        int measured = 0;
        h.Suite.Score = _ => Interlocked.Increment(ref measured) == 2
            ? Results.Make(10, 100) with { Noise = NoiseReport.Quiet with { Unknown = ["noise unknown: test"] } }
            : Results.Make(10, 100);

        await h.Jobs.StartTuningAsync("qwen", TuningGoal.Balanced, [TuningDimension.MaxNumSeqs], Ct);
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListTuningAsync("qwen", Ct));
        Assert.True(run.Candidates.Count >= 2);
        var p = Assert.IsType<BenchmarkProvenance>(run.Provenance);
        Assert.Equal("uid-dgx", p.NodeUid);
        Assert.Equal(BenchmarkNoise.Unknown, p.Noise);
    }

    /// <summary>
    /// W8 review (MN-128): on a discrete node the drain before measuring waits only for the models sharing the measured GPUs
    /// (the noise check's scope), so a busy keep-serving model on another GPU does not make the run noisy; one on the same
    /// GPU still does.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnADiscreteNodeTheDrainWaitsOnlyForTheModelsSharingTheMeasuredGpus(bool sameGpu)
    {
        var measured = Qwen with { Placement = new ModelPlacement(Office, [GpuA]) };
        var neighbour = Model("other", Office, sameGpu ? GpuA : GpuB);
        var cache = new Cache([OfficeNode], [Instance("qwen", Office, gpus: GpuA), Instance("other", Office, gpus: sameGpu ? GpuA : GpuB)]);
        var h = Jobs(new NodeBenchmarkProvenanceSource(cache, new FakeModelRegistry().Add(measured).Add(neighbour), Dgx), measured, neighbour);
        h.History.ModelSamplesById["other"] = [new ModelSample(DateTimeOffset.UtcNow.AddMinutes(1), Results.Kpis(running: 3))];

        await h.Jobs.StartBenchmarkAsync("qwen", Ct);
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListBenchmarksAsync("qwen", Ct));
        bool inFlight = run.Result!.Noise.Reasons.Any(r => r.StartsWith("noisy: in-flight requests at start", StringComparison.Ordinal));
        Assert.Equal(sameGpu, inFlight);
    }

    [Fact]
    public async Task WithoutAProvenanceSourceRunsAreStoredAsBefore()
    {
        var h = Jobs(null, Qwen);

        await h.Jobs.StartBenchmarkAsync("qwen", Ct);
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListBenchmarksAsync("qwen", Ct));
        Assert.Null(run.Provenance);
        Assert.Null(Assert.Single(h.Suite.Targets).Scope);
    }

    [Fact]
    public async Task TheBenchmarkEtaUsesOnlyRunsMeasuredOnTheModelsCurrentHardware()
    {
        var cache = new Cache([DgxNode, OfficeNode], [Instance("qwen", Dgx)]);
        var source = new NodeBenchmarkProvenanceSource(cache, new FakeModelRegistry().Add(Qwen), Dgx);
        var h = Jobs(source, Qwen);
        var t = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        // The newest run was on the office PC (2 h); the older one on the DGX before provenance existed (10 min).
        await h.Store.SaveBenchmarkAsync(new BenchmarkRun("20261007T100000Z", "qwen", BenchmarkOrigin.OnDemand, t.AddHours(-2), t.AddHours(-2).AddMinutes(10),
            Qwen, null, Results.Make(1, 1), null, false), Ct);
        await h.Store.SaveBenchmarkAsync(new BenchmarkRun("20261007T120000Z", "qwen", BenchmarkOrigin.OnDemand, t, t.AddHours(2),
            Qwen, null, Results.Make(1, 1), null, false) { Provenance = On(Office, "uid-office", GpuA) }, Ct);

        Assert.Equal(TimeSpan.FromMinutes(10), await h.Jobs.EstimateBenchmarkAsync("qwen", Ct));

        // Without a source (P1 wiring) the newest run is used, whatever its hardware.
        Assert.Equal(TimeSpan.FromHours(2), await Jobs(null, Qwen).Jobs.EstimateBenchmarkAsync("qwen", Ct));
    }

    // ================================================================ shown honestly

    [Fact]
    public void AResultWithIncompleteTelemetryReadsNoiseUnknownNotQuietBox()
    {
        using var ctx = new Bunit.BunitContext();
        var unknown = Results.Make(10, 100) with
        {
            Noise = NoiseReport.Quiet with { Unknown = ["noise unknown: incomplete telemetry for gemma during the runs"] },
        };

        var cut = ctx.Render<MirbadLLM.Web.Components.Ui.BenchmarkResultView>(p => p.Add(x => x.Result, unknown).Add(x => x.TestId, "bench"));

        Assert.Empty(cut.FindAll("[data-testid=bench-quiet]"));
        Assert.Contains("Noise unknown: incomplete telemetry for gemma during the runs", cut.Find("[data-testid=bench-unknown]").TextContent, StringComparison.Ordinal);

        var quiet = ctx.Render<MirbadLLM.Web.Components.Ui.BenchmarkResultView>(p => p.Add(x => x.Result, Results.Make(10, 100)).Add(x => x.TestId, "bench"));
        Assert.Single(quiet.FindAll("[data-testid=bench-quiet]"));
        Assert.Empty(quiet.FindAll("[data-testid=bench-unknown]"));
    }

    [Fact]
    public void ComparingRunsFromDifferentHardwareIsLabelled()
    {
        using var ctx = new Bunit.BunitContext();
        var t = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var old = new BenchmarkRun("a", "qwen", BenchmarkOrigin.OnDemand, t, t.AddMinutes(1), Qwen, null, Results.Make(1, 1), null, false);
        var dgx = old with { Id = "b", Provenance = On(Dgx, "uid-dgx") };
        var office = old with
        {
            Id = "c",
            Provenance = On(Office, "uid-office") with { Gpus = [new ProvenanceGpu(GpuA, "NVIDIA GeForce RTX 5070", 1)] },
        };

        var same = ctx.Render<MirbadLLM.Web.Components.Ui.BenchmarkCompare>(p => p.Add(x => x.A, old).Add(x => x.B, dgx));
        Assert.Empty(same.FindAll("[data-testid=compare-hardware]"));

        var different = ctx.Render<MirbadLLM.Web.Components.Ui.BenchmarkCompare>(p => p.Add(x => x.A, old).Add(x => x.B, office));
        string label = different.Find("[data-testid=compare-hardware]").TextContent;
        Assert.Contains("different hardware (dgx vs pc-office, NVIDIA GeForce RTX 5070)", label, StringComparison.Ordinal);
    }

    // ================================================================ GPU idle on a discrete node

    [Fact]
    public async Task TheGpuIdleCheckOnADiscreteNodeSamplesOnlyTheModelsGpusByTheirCurrentIndex()
    {
        await using var env = await AgentEnv.StartAsync();
        var model = Model("llama", env.Agent.NodeId, FakeDiscreteGpu.TwoBy16[1].Uuid);
        env.Registry.Add(model);

        // The capability report maps the UUID to index 1: the agent accepts the GPU list.
        env.SetCapabilities(Capabilities(MemoryModel.Discrete,
            new GpuCapability { Index = 0, Uuid = FakeDiscreteGpu.TwoBy16[0].Uuid, Name = "a" },
            new GpuCapability { Index = 1, Uuid = FakeDiscreteGpu.TwoBy16[1].Uuid, Name = "b" }));
        var idle = await env.Targets.SampleAsync(model.Id, TimeSpan.FromSeconds(1), Ct);
        Assert.Equal(1, idle.Samples);

        // A report that maps it to an index the agent does not have: the index list is what is sent (refused: no reading).
        env.SetCapabilities(Capabilities(MemoryModel.Discrete,
            new GpuCapability { Index = 7, Uuid = FakeDiscreteGpu.TwoBy16[1].Uuid, Name = "b" }));
        Assert.Equal(new GpuIdleReading(null, null, 0), await env.Targets.SampleAsync(model.Id, TimeSpan.FromSeconds(1), Ct));

        // A UUID the report does not list, or a unified report: every GPU is sampled (conservative).
        env.SetCapabilities(Capabilities(MemoryModel.Discrete));
        Assert.Equal(1, (await env.Targets.SampleAsync(model.Id, TimeSpan.FromSeconds(1), Ct)).Samples);
        env.SetCapabilities(Capabilities(MemoryModel.Unified, new GpuCapability { Index = 7, Uuid = FakeDiscreteGpu.TwoBy16[1].Uuid, Name = "b" }));
        Assert.Equal(1, (await env.Targets.SampleAsync(model.Id, TimeSpan.FromSeconds(1), Ct)).Samples);
    }

    // ================================================================ fakes

    /// <summary>A vLLM stand-in for the classic suite and the embeddings benchmark (no metrics endpoint needed).</summary>
    private sealed class FakeVllm : HttpMessageHandler
    {
        private static readonly double[] UnitVector = [0.6, 0.8];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (path == "/tokenize")
            {
                return Json("""{"count": 100}""");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (path == "/v1/embeddings")
            {
                int inputs = root.GetProperty("input").GetArrayLength();
                var data = Enumerable.Range(0, inputs).Select(i => new { index = i, embedding = UnitVector });
                return Json(JsonSerializer.Serialize(new { data, usage = new { prompt_tokens = inputs * 8 } }));
            }

            if (root.TryGetProperty("stream", out var stream) && stream.GetBoolean())
            {
                int tokens = root.GetProperty("max_tokens").GetInt32();
                var sse = new StringBuilder();
                for (int i = 0; i < tokens; i++)
                {
                    sse.Append("data: {\"choices\":[{\"delta\":{\"content\":\"t\"}}]}\n\n");
                }

                sse.Append($"data: {{\"choices\":[],\"usage\":{{\"prompt_tokens\":10,\"completion_tokens\":{tokens}}}}}\n\n");
                sse.Append("data: [DONE]\n\n");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
            }

            if (root.TryGetProperty("tools", out _))
            {
                return Json("""{"choices":[{"message":{"content":null,"tool_calls":[{"function":{"name":"get_time","arguments":"{\"city\":\"Riyadh\"}"}}]}}]}""");
            }

            string question = root.GetProperty("messages")[0].GetProperty("content").GetString()!;
            string answer = BenchmarkPrompts.Questions.First(q => q.Question == question).Expected;
            return Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer } } } }));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>
    /// A live sampler that is one scrape behind: the measured model has a scrape a minute before the runs and one just
    /// before they start; the next one (after the runs) appears on the second read after the runs, as the sampler's next
    /// tick would.
    /// </summary>
    private sealed class LaggingHistory(string modelId) : IMetricsHistory
    {
        private readonly Lock _gate = new();
        private readonly List<ModelSample> _samples =
        [
            new ModelSample(DateTimeOffset.UtcNow.AddMinutes(-1), Kpis()),
            new ModelSample(DateTimeOffset.UtcNow.AddSeconds(-1), Kpis()),
        ];

        private int _reads;

        public bool Appended { get; private set; }

        public IReadOnlyList<HostSample> GetHost(TimeSpan window) => [];

        public IReadOnlyList<ModelSample> GetModel(string id, TimeSpan window)
        {
            lock (_gate)
            {
                if (id != modelId)
                {
                    return [];
                }

                if (++_reads == 2)
                {
                    _samples.Add(new ModelSample(DateTimeOffset.UtcNow, Kpis(running: 1)));
                    Appended = true;
                }

                return [.. _samples];
            }
        }

        public IReadOnlyDictionary<string, ModelKpis> Latest() => new Dictionary<string, ModelKpis> { [modelId] = Kpis() };
    }

    /// <summary>The master's node status cache with fixed nodes and instances.</summary>
    private sealed class Cache(IReadOnlyList<NodeLiveStatus> nodes, IReadOnlyList<NodeModelRuntime> runtimes) : INodeStatusCache
    {
        private IReadOnlyList<NodeLiveStatus> _nodes = nodes;

        public long Version => 0;

        public void Set(NodeLiveStatus node) =>
            _nodes = [.. _nodes.Where(n => n.NodeId != node.NodeId), node];

        public IReadOnlyList<NodeLiveStatus> ListNodes() => _nodes;

        public NodeLiveStatus? GetNode(string nodeId) => _nodes.FirstOrDefault(n => n.NodeId == nodeId);

        public NodeModelRuntime? GetRuntime(string modelId) =>
            runtimes.FirstOrDefault(r => r.ModelId == modelId && r.State != ModelState.Stopped);

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) =>
            [.. runtimes.Where(r => nodeId is null || r.NodeId == nodeId)];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A discrete <see cref="FakeAgent"/> (two 16 GiB cards) behind the real agent client factory and <see cref="AgentJobTargets"/>.</summary>
    private sealed class AgentEnv : IAsyncDisposable
    {
        private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-provenance-agent-" + Guid.NewGuid().ToString("N"));
        private readonly IssuedCredential _master;
        private readonly TrustAnchorSet _trust;
        private readonly JsonNodeRegistry _nodeRegistry;
        private readonly JsonAgentOperationReferenceStore _references;
        private readonly Cache _cache;

        private AgentEnv(FakeAgent agent)
        {
            Agent = agent;
            Directory.CreateDirectory(_dataPath);
            var options = Microsoft.Extensions.Options.Options.Create(new MirbadLLMOptions { DataPath = _dataPath });
            _master = agent.Ca.IssueMasterClientCertificate();
            _trust = TrustAnchorSet.Create([agent.Ca.Certificate]);
            _nodeRegistry = new JsonNodeRegistry(options, TimeProvider.System);
            Factory = new AgentClientFactory(_nodeRegistry, new MirbadLLM.UnitTests.Multinode.TestMasterIdentity(), _master, _trust, TimeProvider.System);
            _references = new JsonAgentOperationReferenceStore(options, TimeProvider.System);
            _cache = new Cache([new NodeLiveStatus { NodeId = agent.NodeId, NodeUid = agent.NodeUid, State = NodeState.Online, BootId = agent.BootId }], []);
            Targets = new AgentJobTargets(Factory, _cache, Registry, _references, TimeProvider.System);
        }

        public FakeAgent Agent { get; }

        public AgentClientFactory Factory { get; }

        public FakeModelRegistry Registry { get; } = new();

        public AgentJobTargets Targets { get; }

        public void SetCapabilities(NodeCapabilities capabilities) =>
            _cache.Set(_cache.GetNode(Agent.NodeId)! with { Capabilities = capabilities });

        public static async Task<AgentEnv> StartAsync()
        {
            var agent = await FakeAgent.StartAsync(new FakeAgentOptions { NodeId = Office, DiscreteGpus = FakeDiscreteGpu.TwoBy16 });
            var env = new AgentEnv(agent);
            await env._nodeRegistry.UpsertAsync(new NodeRecord
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

        public async ValueTask DisposeAsync()
        {
            Targets.Dispose();
            await Factory.DisposeAsync();
            await Agent.DisposeAsync();
            _references.Dispose();
            _nodeRegistry.Dispose();
            _master.Dispose();
            _trust.Dispose();
            try
            {
                Directory.Delete(_dataPath, recursive: true);
            }
            catch (IOException)
            {
                // A handle still closing: the temp folder is cleaned up later.
            }
        }
    }
}
