using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>
/// Tuning on workload presets (C4): search with 3 repetitions, then the top two and the baseline re-measured with 5
/// in randomized order; a winner must beat the baseline by 5 % with non-overlapping ranges, else the baseline stays.
/// Also on-demand benchmarks with a preset, and stored v1 results still loading.
/// </summary>
public sealed class PresetTuningTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-presets-" + Guid.NewGuid().ToString("N"));

    private static readonly ModelDefinition Saved = new()
    {
        Id = "qwen",
        ServedName = "Qwen",
        ModelPath = "Qwen-NVFP4",
        MemoryBudgetGiB = 44,
        MaxNumSeqs = 16,
    };

    private sealed record Harness(ModelJobService Jobs, FakeCluster Cluster, FakeSuite Suite, BenchmarkStore Store, ModelJobOptions Options);

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private Harness Create(ModelDefinition? model = null, int seed = 1)
    {
        model ??= Saved;
        var registry = new FakeModelRegistry().Add(model);
        var cluster = new FakeCluster().Running(model);
        var suite = new FakeSuite(cluster);
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, UseContainerNetwork = true, VllmImage = "default-image" });
        var store = new BenchmarkStore(options);
        var jobOptions = new ModelJobOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(1),
            DrainTimeout = TimeSpan.FromMilliseconds(30),
            GpuIdleSampleDuration = TimeSpan.FromMilliseconds(3),
            GpuIdleSampleInterval = TimeSpan.FromMilliseconds(1),
            CandidateStartTimeout = TimeSpan.FromSeconds(10),
            ConfirmationRandom = new Random(seed),
        };
        var jobs = new ModelJobService(
            registry, cluster, cluster, suite, store, new MaintenanceModeService(options), new FakeMetricsHistory(), new FakeGpu(2),
            new StartupTimeTracker(), options, jobOptions, NullLogger<ModelJobService>.Instance, new ContainerNetworkTargets(), _ => null);
        return new Harness(jobs, cluster, suite, store, jobOptions);
    }

    /// <summary>
    /// N=16 aggregate by max num seqs (the Throughput preset scores N=16 alone here); 400 = the baseline. The phase-2
    /// values outside 8/16/32 (4, 64) score low so the confirmation lineup stays the one each test describes.
    /// </summary>
    private static void ScoreBySeqs(Harness h, Func<int, double> n16, Func<int, double>? spread = null) =>
        h.Suite.ScoreTarget = (d, target) =>
            Results.Workload(50, d.MaxNumSeqs is 8 or 16 or 32 ? n16(d.MaxNumSeqs) : 300, spread: spread?.Invoke(d.MaxNumSeqs) ?? 0.01, reps: target.Repetitions ?? 3, preset: target.Preset ?? TuningGoal.Throughput);

    private static async Task<TuningRun> TuneAsync(Harness h, TuningGoal goal = TuningGoal.Throughput)
    {
        await h.Jobs.StartTuningAsync("qwen", goal, [TuningDimension.MaxNumSeqs], TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();
        return Assert.Single(await h.Store.ListTuningAsync("qwen", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Confirmation_RemeasuresTopTwoAndBaseline_WithFiveRepetitions_AndConfirmsAClearWinner()
    {
        var h = Create();
        ScoreBySeqs(h, seqs => seqs switch { 32 => 480, 8 => 380, _ => 400 });

        var run = await TuneAsync(h);

        Assert.Equal(2, run.SchemaVersion);
        Assert.Equal(TuningGoal.Throughput, run.Goal);
        var confirm = run.Candidates.Where(c => c.Step == ModelJobService.ConfirmationStep).ToList();
        Assert.Equal(3, confirm.Count);
        Assert.Equal(
            ["Baseline (saved settings) (confirm)", "Max num seqs 32 (confirm)", "Max num seqs 8 (confirm)"],
            confirm.Select(c => c.Label).Order(StringComparer.Ordinal));
        Assert.All(confirm, c => Assert.NotNull(c.ScoreMin));

        // Search with 3 repetitions, confirmation with 5; every target runs the preset's workloads.
        Assert.All(h.Suite.Targets, t => Assert.Equal(TuningGoal.Throughput, t.Preset));
        Assert.Equal([3, 3, 3, 3, 3, 5, 5, 5], h.Suite.Targets.Select(t => t.Repetitions!.Value)); // baseline + seqs 4/8/32/64, then 3 confirmations

        Assert.Equal("Max num seqs 32 (confirm)", run.Winner!.Label);
        Assert.Equal(32, run.Winner.Settings.MaxNumSeqs);
        Assert.Equal(20, run.WinnerGainPercent!.Value, 6);
        Assert.Contains(run.Notes, n => n.StartsWith("Confirmed: Max num seqs 32", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirmation_KeepsTheBaseline_WhenTheGainIsUnderFivePercent()
    {
        var h = Create();
        ScoreBySeqs(h, seqs => seqs == 32 ? 412 : 400);

        var run = await TuneAsync(h);

        Assert.Equal("Baseline (saved settings) (confirm)", run.Winner!.Label);
        Assert.Equal(Saved.MaxNumSeqs, run.Winner.Settings.MaxNumSeqs);
        Assert.Equal(0, run.WinnerGainPercent!.Value, 6);
        Assert.Contains(run.Notes, n => n.StartsWith("Kept the saved settings: the best re-measured candidate (Max num seqs 32", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Confirmation_KeepsTheBaseline_WhenTheRangesOverlap()
    {
        var h = Create();
        ScoreBySeqs(h, seqs => seqs == 32 ? 424 : 400, seqs => seqs == 32 ? 0.08 : 0.02);

        var run = await TuneAsync(h);

        var best = run.Candidates.Single(c => c.Label == "Max num seqs 32 (confirm)");
        Assert.True(best.Score >= 1.05);
        Assert.True(best.ScoreMin < run.Candidates.Single(c => c.Label == "Baseline (saved settings) (confirm)").ScoreMax);
        Assert.Equal("Baseline (saved settings) (confirm)", run.Winner!.Label);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public async Task Confirmation_OrderIsRandomized_WithTheConfiguredRandom(int seed)
    {
        var h = Create(seed: seed);
        ScoreBySeqs(h, seqs => seqs switch { 32 => 480, 8 => 440, _ => 400 });

        var run = await TuneAsync(h);

        // The lineup is baseline, best, second best; the order is OrderBy(random.Next()) with the configured seed.
        string[] lineup = ["Baseline (saved settings) (confirm)", "Max num seqs 32 (confirm)", "Max num seqs 8 (confirm)"];
        var random = new Random(seed);
        var expected = lineup.OrderBy(_ => random.Next()).ToList();
        Assert.Equal(expected, run.Candidates.Where(c => c.Step == ModelJobService.ConfirmationStep).Select(c => c.Label));
    }

    [Fact]
    public async Task NoAcceptedCandidate_SkipsTheConfirmation()
    {
        var h = Create();
        h.Suite.ScoreTarget = (d, target) => Results.Workload(50, 400, reps: target.Repetitions ?? 3, errors: d.MaxNumSeqs == 16 ? 0 : 1);

        var run = await TuneAsync(h);

        Assert.DoesNotContain(run.Candidates, c => c.Step == ModelJobService.ConfirmationStep);
        Assert.Contains(run.Notes, n => n.Contains("nothing to confirm", StringComparison.Ordinal));
        Assert.Equal(0, run.WinnerIndex);
    }

    [Fact]
    public async Task Guards_FromTheOptions_ReplaceThePresetDefaults()
    {
        var h = Create();
        h.Options.Guards[TuningGoal.Throughput] = new SloGuards(null, null, null, MaxErrorRate: 0.5);
        h.Suite.ScoreTarget = (d, target) => Results.Workload(50, d.MaxNumSeqs == 32 ? 480 : 400, reps: target.Repetitions ?? 3, errors: d.MaxNumSeqs == 32 ? 1 : 0);

        var run = await TuneAsync(h);

        Assert.Equal(CandidateOutcome.Accepted, run.Candidates.Single(c => c.Label == "Max num seqs 32").Outcome);
    }

    [Fact]
    public async Task PoolingModel_IsAlwaysTunedOnTheEmbeddingsPreset()
    {
        var embedder = Saved with { ExtraArgs = ["--runner", "pooling"], ToolCallParser = null, ReasoningParser = null };
        var h = Create(embedder);
        h.Suite.ScoreTarget = (_, target) => Results.Embeddings(1000, 4000);

        var run = await TuneAsync(h, TuningGoal.InteractiveChat);

        Assert.Equal(TuningGoal.Embeddings, run.Goal);
        Assert.All(h.Suite.Targets, t => Assert.Equal(TuningGoal.Embeddings, t.Preset));
    }

    [Fact]
    public async Task ClassicGoals_KeepTheClassicSuiteAndScorer_WithoutConfirmation()
    {
        var h = Create();
        h.Suite.Score = d => Results.Make(10, d.MaxNumSeqs * 10);

        var run = await TuneAsync(h, TuningGoal.ManyAgents);

        Assert.Equal(1, run.SchemaVersion);
        Assert.All(h.Suite.Targets, t => Assert.Null(t.Preset));
        Assert.DoesNotContain(run.Candidates, c => c.Step == ModelJobService.ConfirmationStep);
        Assert.Equal("Max num seqs 32", run.Winner!.Label);
    }

    [Fact]
    public async Task Benchmark_WithAPreset_RunsItsWorkloads_AndRecordsThePreset()
    {
        var h = Create();
        h.Suite.ScoreTarget = (_, target) => Results.Workload(50, 400, reps: target.Repetitions ?? 3, preset: target.Preset!.Value);

        await h.Jobs.StartBenchmarkAsync("qwen", TuningGoal.AgentsTools, TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();
        var run = Assert.Single(await h.Store.ListBenchmarksAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Equal(TuningGoal.AgentsTools, run.Preset);
        Assert.Equal(BenchmarkResult.CurrentSchemaVersion, run.Result!.SchemaVersion);

        await h.Jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();
        Assert.Equal([TuningGoal.AgentsTools, null], h.Suite.Targets.Select(t => t.Preset));
    }

    // ------------------------------------------------------------ stored v1 results

    [Fact]
    public async Task StoredV1Results_StillLoad_AsSchemaVersion1()
    {
        string benchDir = Path.Combine(_dataPath, "benchmarks", "qwen");
        string tuneDir = Path.Combine(_dataPath, "tuning", "qwen");
        Directory.CreateDirectory(benchDir);
        Directory.CreateDirectory(tuneDir);
        const string Settings = """{"id":"qwen","servedName":"Qwen","modelPath":"Qwen-NVFP4","kvCacheDtype":"fp8","maxModelLen":262144,"maxNumSeqs":16,"memoryBudgetGiB":44,"enablePrefixCaching":true,"speculative":{"method":"mtp","numSpeculativeTokens":2},"extraArgs":[]}""";
        const string Result = """
            {"levels":[{"concurrency":1,"runs":[{"concurrency":1,"completionTokens":256,"wallSeconds":5,"perRequestTokS":51,"aggregateTokS":51,"ttftP50":0.1,"ttftP95":0.1,"otherModelsRunningMax":0,"sameModelRunningMax":1}],
              "perRequestTokS":51,"aggregateTokS":51,"ttftP50":0.1,"ttftP95":0.1,"noisy":false}],
             "prefill":{"promptTokens":60000,"coldSeconds":30,"cachedSeconds":1},
             "guardrails":{"toolCallValid":true,"toolCallDetail":"get_time","qaCorrect":8,"qaTotal":8,"answers":[]},
             "noise":{"noisy":false,"reasons":[],"idleGpuPercent":2,"otherModelsMaxRunning":{}}}
            """;
        await File.WriteAllTextAsync(Path.Combine(benchDir, "20260901T000000Z.json"),
            $$"""{"id":"20260901T000000Z","modelId":"qwen","origin":"OnDemand","startedAt":"2026-09-01T00:00:00Z","finishedAt":"2026-09-01T00:04:00Z","settings":{{Settings}},"configHash":"abc","result":{{Result}},"error":null,"cancelled":false}""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(tuneDir, "20260901T010000Z.json"),
            $$"""{"id":"20260901T010000Z","modelId":"qwen","goal":"Balanced","startedAt":"2026-09-01T01:00:00Z","finishedAt":"2026-09-01T02:00:00Z","status":"Completed","baseline":{{Settings}},"candidates":[{"index":0,"step":"Baseline","label":"Baseline (saved settings)","settings":{{Settings}},"configHash":"abc","outcome":"Accepted","reason":null,"result":{{Result}},"score":1,"startupSeconds":null}],"winnerIndex":0,"winnerGainPercent":0,"error":null,"notes":[]}""",
            TestContext.Current.CancellationToken);
        var store = new BenchmarkStore(Options.Create(new MirbadLLMOptions { DataPath = _dataPath }));

        var bench = Assert.Single(await store.ListBenchmarksAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Equal(1, bench.Result!.SchemaVersion);
        Assert.Null(bench.Preset);
        Assert.Equal(256, bench.Result.Levels[0].OutputTokens);
        Assert.Equal(0, bench.Result.Errors);
        Assert.Equal(new SpeculativeDecoding("mtp", 2), bench.Settings.Speculative);

        var tune = Assert.Single(await store.ListTuningAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Equal(1, tune.SchemaVersion);
        Assert.Equal(TuningGoal.Balanced, tune.Goal);
        Assert.Null(tune.Candidates[0].ScoreMin);
        Assert.Equal(51, tune.Winner!.Result!.Level(1)!.PerRequestTokS);
    }
}
