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

/// <summary>A workload-preset tuning run end to end with fakes: phase order, follow-ups, runtime notes, descriptions, live description, ETA and the relative guard.</summary>
public sealed class SearchRunTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-search-" + Guid.NewGuid().ToString("N"));

    private static readonly ModelDefinition Saved = new()
    {
        Id = "qwen",
        ServedName = "Qwen",
        ModelPath = "Qwen-NVFP4",
        MemoryBudgetGiB = 60,
        MaxNumSeqs = 16,
        Speculative = new SpeculativeDecoding("mtp", 2),
        CudaGraphMode = "PIECEWISE",
    };

    private static readonly string[] QuickSteps = ["Max num seqs", "Speculative × CUDA graphs"];

    private sealed record Harness(ModelJobService Jobs, FakeCluster Cluster, FakeSuite Suite, BenchmarkStore Store);

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private Harness Create()
    {
        var registry = new FakeModelRegistry().Add(Saved);
        var cluster = new FakeCluster().Running(Saved);
        var suite = new FakeSuite(cluster);
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, UseContainerNetwork = true, VllmImage = "default-image" });
        var store = new BenchmarkStore(options);
        var jobs = new ModelJobService(
            registry, cluster, cluster, suite, store, new MaintenanceModeService(options), new FakeMetricsHistory(), new FakeGpu(2),
            new StartupTimeTracker(), options,
            new ModelJobOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                DrainTimeout = TimeSpan.FromMilliseconds(30),
                GpuIdleSampleDuration = TimeSpan.FromMilliseconds(3),
                GpuIdleSampleInterval = TimeSpan.FromMilliseconds(1),
                CandidateStartTimeout = TimeSpan.FromSeconds(10),
                ConfirmationRandom = new Random(3),
            },
            NullLogger<ModelJobService>.Instance,
            new ContainerNetworkTargets(),
            _ => SearchPlannerTests.Inspection("Qwen3_5MoeForConditionalGeneration", 21, mtp: true, moe: true));
        suite.ScoreTarget = (d, target) => Results.Workload(50 + (d.Speculative?.NumSpeculativeTokens ?? 0), 400 + d.MaxNumSeqs, reps: target.Repetitions ?? 3,
            preset: target.Preset ?? TuningGoal.InteractiveChat);
        return new Harness(jobs, cluster, suite, store);
    }

    private static async Task<TuningRun> TuneAsync(Harness h, IReadOnlyCollection<TuningDimension> steps, TuningGoal goal = TuningGoal.InteractiveChat)
    {
        await h.Jobs.StartTuningAsync("qwen", goal, steps, TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();
        return Assert.Single(await h.Store.ListTuningAsync("qwen", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Full_MeasuresThePhasesInOrder_ThenConfirms()
    {
        var h = Create();

        var run = await TuneAsync(h, SearchPlanner.Full);

        Assert.Equal(TuningRunStatus.Completed, run.Status);
        var order = run.Candidates.Select(c => c.Step).Distinct().ToList();
        string[] expected = ["Baseline", "KV cache × prefix caching", "Max num seqs", "Max batched tokens", "Speculative × CUDA graphs", "Async scheduling"];
        Assert.Equal(expected, order.Take(expected.Length));
        Assert.Equal(ModelJobService.ConfirmationStep, order[^1]);
        Assert.Contains(run.Candidates, c => c.Step == "Refine neighbours");

        // Every candidate carries its description and the effective configuration; no forced PIECEWISE.
        Assert.All(run.Candidates, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
            Assert.False(string.IsNullOrWhiteSpace(c.EffectiveConfig));
        });
        Assert.All(run.Candidates.Where(c => c.Label.StartsWith("MTP ×", StringComparison.Ordinal) && c.Label.EndsWith("FULL_AND_PIECEWISE", StringComparison.Ordinal)),
            c => Assert.Equal(SearchPlanner.FullAndPiecewise, c.Settings.CudaGraphMode));

        // No configuration measured twice in the search.
        var search = run.Candidates.Where(c => c.Step != ModelJobService.ConfirmationStep).Select(c => TuningPlanner.Fingerprint(c.Settings)).ToList();
        Assert.Equal(search.Count, search.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task FailedFullGraphs_AreRetriedWithPiecewise_AndRuntimeDowngradesAreRecorded()
    {
        var h = Create();
        h.Cluster.FailWith = d => d.Speculative is { NumSpeculativeTokens: 5 } && d.CudaGraphMode == SearchPlanner.FullAndPiecewise
            ? "Cause: CUDA error: illegal instruction" : null;
        h.Cluster.Logs = d => d.CudaGraphMode == SearchPlanner.FullAndPiecewise && d.Speculative is { NumSpeculativeTokens: 3 }
            ? ["WARNING [config.py:500] cudagraph_mode is set to PIECEWISE because this attention backend does not support full graphs"]
            : [];

        var run = await TuneAsync(h, [TuningDimension.SpeculativeDecoding, TuningDimension.CudaGraphMode]);

        Assert.Equal(CandidateOutcome.Failed, run.Candidates.Single(c => c.Label == "MTP × 5 · FULL_AND_PIECEWISE").Outcome);
        Assert.Equal(CandidateOutcome.Accepted, run.Candidates.Single(c => c.Label == "MTP × 5 · PIECEWISE").Outcome);
        Assert.Contains("CUDA graph mode: requested FULL_AND_PIECEWISE → ran PIECEWISE",
            run.Candidates.Single(c => c.Label == "MTP × 3 · FULL_AND_PIECEWISE").RuntimeNotes!);
        Assert.Equal([RuntimeLogParser.AsRequested], run.Candidates.Single(c => c.Label == "MTP × 1 · FULL_AND_PIECEWISE").RuntimeNotes);
    }

    [Fact]
    public async Task Quick_OnlySearchesSeqsAndSpeculative_WithoutFollowUpsOrRefinement()
    {
        var h = Create();

        var run = await TuneAsync(h, SearchPlanner.Quick);

        Assert.All(run.Candidates.Where(c => c.Step is not ("Baseline" or ModelJobService.ConfirmationStep)),
            c => Assert.Contains(c.Step, QuickSteps));
        Assert.DoesNotContain(run.Candidates, c => c.Label.EndsWith("· PIECEWISE", StringComparison.Ordinal));
        Assert.Equal(1 + 4 + 5, run.Candidates.Count(c => c.Step != ModelJobService.ConfirmationStep)); // baseline + seqs 4/8/32/64 + off/saved/MTP 1/3/5
    }

    [Fact]
    public async Task LiveSnapshot_ShowsWhatTheCurrentCandidateTriesToAchieve()
    {
        var h = Create();
        h.Suite.Gate = new TaskCompletionSource();

        await h.Jobs.StartTuningAsync("qwen", TuningGoal.InteractiveChat, SearchPlanner.Quick, TestContext.Current.CancellationToken);
        while (h.Suite.Runs == 0)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.Contains("reference", h.Jobs.Current!.CandidateDescription, StringComparison.Ordinal); // the baseline's description
        h.Suite.Gate.SetResult();
        await h.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task BaselineBreakingAGuard_IsNotedOnTheRun()
    {
        var h = Create();
        h.Suite.ScoreTarget = (d, target) => Results.Workload(50 + d.MaxNumSeqs, 400, reps: target.Repetitions ?? 3, ttftP95At1: 1.4);

        var run = await TuneAsync(h, SearchPlanner.Quick);

        Assert.Contains(run.Notes, n => n.StartsWith("Guard made relative: the baseline's short-prompt TTFT p95 at N=1 is 1.40 s", StringComparison.Ordinal));
        Assert.Equal(CandidateOutcome.Accepted, run.Candidates[0].Outcome);
    }

    [Fact]
    public async Task Preview_EstimatesFromTheCandidateListAndThisModelsMeasuredDurations()
    {
        var h = Create();
        var empty = await h.Jobs.PreviewTuningAsync("qwen", SearchPlanner.Quick, TuningGoal.InteractiveChat, TestContext.Current.CancellationToken);
        Assert.Contains("default estimate", empty.EstimateBasis, StringComparison.Ordinal);

        var now = DateTimeOffset.UtcNow;
        var past = new TuningRun("20261001T000000Z", "qwen", TuningGoal.InteractiveChat, now, now, TuningRunStatus.Completed, Saved,
        [
            new TuningCandidateResult(0, "Baseline", "Baseline", Saved, "h", CandidateOutcome.Accepted, null, null, 1, 200, BenchmarkSeconds: 100),
            new TuningCandidateResult(1, "Max num seqs", "Max num seqs 8", Saved, "h", CandidateOutcome.Accepted, null, null, 1, 220, BenchmarkSeconds: 140),
            new TuningCandidateResult(2, "Max num seqs", "Max num seqs 32", Saved, "h", CandidateOutcome.Accepted, null, null, 1, 240, BenchmarkSeconds: 120),
        ], 0, 0, null, [], 2);
        await h.Store.SaveTuningAsync(past, TestContext.Current.CancellationToken);

        var preview = await h.Jobs.PreviewTuningAsync("qwen", SearchPlanner.Quick, TuningGoal.InteractiveChat, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(220), preview.StartupEstimate);
        Assert.Equal(TimeSpan.FromSeconds(120), preview.BenchmarkEstimate);
        Assert.Contains("measured in the last tuning run", preview.EstimateBasis, StringComparison.Ordinal);
        Assert.Equal(
            TuningEstimator.Estimate(preview.Plan.ExpectedCandidates, preview.ReusesRunningBaseline, 3, preview.StartupEstimate, preview.BenchmarkEstimate, 5.0 / 3),
            preview.Estimate);
        Assert.Equal(10, preview.Plan.ExpectedCandidates);
    }

    [Fact]
    public void RuntimeLogParser_ReadsTheImagesRealLogLines()
    {
        // Verbatim (trimmed) lines from the DGX image local/qwen38-vllm-dflash2:66e5414c6d75.
        var spec = RuntimeLogParser.Parse(
        [
            "(EngineCore pid=179) INFO 10-01 16:09:34 [core.py:122] Initializing a V1 LLM engine (v0.1.dev1+g66e5414c6) with config: model='/models/Qwen3.6-35B-A3B-NVFP4', speculative_config=SpeculativeConfig(method='mtp', model='/models/Qwen3.6-35B-A3B-NVFP4', num_spec_tokens=3), tokenizer='/models/Qwen3.6-35B-A3B-NVFP4'",
        ]);
        Assert.Equal("mtp", spec.SpeculativeMethod);
        Assert.Equal(3, spec.SpeculativeTokens);

        var pooling = RuntimeLogParser.Parse(
        [
            "(APIServer pid=1) WARNING 10-01 01:13:31 [vllm.py:1485] Pooling models do not support full cudagraphs. Overriding cudagraph_mode to PIECEWISE.",
        ]);
        Assert.Equal("PIECEWISE", pooling.CudaGraphMode);
    }
}
