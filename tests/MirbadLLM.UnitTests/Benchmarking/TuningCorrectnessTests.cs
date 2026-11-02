using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Orchestration;
using MirbadLLM.UnitTests.Web.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>
/// Tuning correctness: apply writes the normalized definition (the live 35B bug), bounded recovery from candidate
/// failures (launch error, start conflict, readiness, benchmark, failed cleanup, restore not Ready, cancellation),
/// and pooling models (plan, embeddings target, scoring, rerankers refused).
/// </summary>
public sealed class TuningCorrectnessTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-tuning-a-" + Guid.NewGuid().ToString("N"));

    private static readonly ModelDefinition Saved = new()
    {
        Id = "qwen",
        ServedName = "Qwen",
        ModelPath = "Qwen-NVFP4",
        MemoryBudgetGiB = 44,
        MaxNumSeqs = 16,
        Speculative = new SpeculativeDecoding("mtp", 2),
        CudaGraphMode = "PIECEWISE",
    };

    private static readonly ModelDefinition Embedder = new()
    {
        Id = "embed",
        ServedName = "Embed",
        ModelPath = "Qwen3-Embedding-8B",
        MemoryBudgetGiB = 24,
        MaxModelLen = 32_768,
        MaxNumSeqs = 16,
        ToolCallParser = null,
        ReasoningParser = null,
        ExtraArgs = ["--runner", "pooling"],
    };

    private static readonly ModelFolderInspection WithMtp = new(
        "Qwen-NVFP4", "Qwen3_5ForConditionalGeneration", 262_144, "NVFP4", 24, MaySupportMtp: true,
        new ModelFolderSuggestion("Qwen", "qwen3_coder", "qwen3", null, 44, ""), KvBytesPerToken: 32_768);

    private sealed record Harness(ModelJobService Jobs, FakeCluster Cluster, FakeSuite Suite, FakeModelRegistry Registry, BenchmarkStore Store);

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private Harness Create(ModelDefinition? model = null, TimeSpan? otherWait = null)
    {
        model ??= Saved;
        var registry = new FakeModelRegistry().Add(model);
        var cluster = new FakeCluster().Running(model);
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
                CleanupTimeout = TimeSpan.FromMilliseconds(300),
                OtherModelStartWait = otherWait ?? TimeSpan.FromMilliseconds(100),
            },
            NullLogger<ModelJobService>.Instance,
            new ContainerNetworkTargets(),
            _ => WithMtp);
        return new Harness(jobs, cluster, suite, registry, store);
    }

    private static async Task<TuningRun> TuneAsync(Harness h, string id = "qwen", TuningGoal goal = TuningGoal.ManyAgents, params TuningDimension[] steps)
    {
        await h.Jobs.StartTuningAsync(id, goal, steps.Length == 0 ? [TuningDimension.MaxNumSeqs] : steps, TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();
        return Assert.Single(await h.Store.ListTuningAsync(id, TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------ A3: apply writes the normalized definition

    [Fact]
    public async Task Apply_LegacyTypedMtp2WithRawMtp3_EffectiveAfterApplyIsTheWinner()
    {
        // The live 35B: typed MTP × 2, but a raw --speculative-config (MTP × 3) in ExtraArgs won every launch.
        var legacy = LiveDefinitions.Qwen35B;
        var h = Create(legacy);
        var winner = VllmArgsNormalizer.Apply(legacy) with { Speculative = new SpeculativeDecoding("mtp", 1) { Extra = JsonFields.Parse("""{"moe_backend":"triton"}""") }, MaxNumSeqs = 32 };
        await SaveRunAsync(h, legacy, winner);

        var applied = await h.Jobs.ApplyTuningCandidateAsync(legacy.Id, "20261002T000000Z", 1, TestContext.Current.CancellationToken);

        var stored = (await h.Registry.GetAsync(legacy.Id, TestContext.Current.CancellationToken))!;
        Assert.Equal(applied.Speculative, stored.Speculative);
        Assert.DoesNotContain(stored.ExtraArgs, a => a.Contains("speculative", StringComparison.Ordinal));
        Assert.DoesNotContain("--async-scheduling", stored.ExtraArgs);
        Assert.True(stored.AsyncScheduling);

        // What vLLM will actually run: exactly one --speculative-config, the winner's.
        var effective = VllmArgsNormalizer.Apply(stored);
        Assert.Equal(new SpeculativeDecoding("mtp", 1) { Extra = JsonFields.Parse("""{"moe_backend":"triton"}""") }, effective.Speculative);
        Assert.Equal(32, effective.MaxNumSeqs);
        var args = VllmArgsBuilder.Build(stored, 120);
        Assert.Equal(1, args.Count(a => a == "--speculative-config"));
        Assert.Equal("""{"method":"mtp","moe_backend":"triton","num_speculative_tokens":1}""", args[args.ToList().IndexOf("--speculative-config") + 1]);
    }

    [Fact]
    public async Task Apply_ARunRecordedBeforeTheFix_AppliesWhatWasActuallyMeasured()
    {
        // A pre-fix candidate labelled "MTP × 2" still carried the raw MTP × 3 in its ExtraArgs, so MTP × 3 was measured.
        var legacy = LiveDefinitions.Qwen35B;
        var h = Create(legacy);
        await SaveRunAsync(h, legacy, legacy with { MaxNumSeqs = 32 });

        var applied = await h.Jobs.ApplyTuningCandidateAsync(legacy.Id, "20261002T000000Z", 1, TestContext.Current.CancellationToken);

        Assert.Equal(3, applied.Speculative!.NumSpeculativeTokens);
        Assert.Equal(32, applied.MaxNumSeqs);
        Assert.DoesNotContain(applied.ExtraArgs, a => a.Contains("speculative", StringComparison.Ordinal));
    }

    private static async Task SaveRunAsync(Harness h, ModelDefinition baseline, ModelDefinition winner)
    {
        var now = DateTimeOffset.UtcNow;
        var result = Results.Make(10, 100);
        var run = new TuningRun("20261002T000000Z", baseline.Id, TuningGoal.Balanced, now, now, TuningRunStatus.Completed, baseline,
        [
            new TuningCandidateResult(0, "Baseline", "Baseline (saved settings)", baseline, "h0", CandidateOutcome.Accepted, null, result, 1, null),
            new TuningCandidateResult(1, "Speculative decoding", "MTP × 1", winner, "h1", CandidateOutcome.Accepted, null, result, 1.2, 60),
        ], 1, 20, null, []);
        await h.Store.SaveTuningAsync(run, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tuning_TheLive35B_MeasuresTheTypedCandidates_NotTheRawOverride()
    {
        var h = Create(LiveDefinitions.Qwen35B);

        var run = await TuneAsync(h, LiveDefinitions.Qwen35B.Id, TuningGoal.SingleUser, TuningDimension.SpeculativeDecoding);

        // Every launch carries exactly the candidate's speculative config (no raw --speculative-config can follow it).
        Assert.All(h.Cluster.Launches, l => Assert.DoesNotContain(l.Definition.ExtraArgs, a => a.Contains("speculative", StringComparison.Ordinal)));
        var off = Assert.Single(run.Candidates, c => c.Label == "Speculative off");
        Assert.Null(off.Settings.Speculative);
        Assert.Equal(new SpeculativeDecoding("mtp", 1) { Extra = JsonFields.Parse("""{"moe_backend":"triton"}""") },
            Assert.Single(run.Candidates, c => c.Label == "MTP × 1").Settings.Speculative);

        // Baseline = the effective saved config (MTP × 3), so "MTP × 3" is not measured twice.
        Assert.Equal(3, run.Candidates[0].Settings.Speculative!.NumSpeculativeTokens);
        Assert.DoesNotContain(run.Candidates, c => c.Label == "MTP × 3");
    }

    // ------------------------------------------------------------ A4: bounded recovery

    [Fact]
    public async Task LaunchError_FailsTheCandidate_CleansUp_AndTheRunContinues()
    {
        var h = Create();
        h.Cluster.LaunchThrows = d => d.MaxNumSeqs == 8 ? new HttpRequestException("Docker hiccup") : null;

        var run = await TuneAsync(h);

        var failed = Assert.Single(run.Candidates, c => c.Label == "Max num seqs 8");
        Assert.Equal(CandidateOutcome.Failed, failed.Outcome);
        Assert.Equal("Launch failed: Docker hiccup", failed.Reason);
        Assert.Equal(["qwen"], h.Cluster.Stops);
        Assert.Contains(run.Candidates, c => c.Label == "Max num seqs 32" && c.Outcome == CandidateOutcome.Accepted);
        Assert.Equal(TuningRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task StartConflictWithAnotherModel_WaitsThenFailsTheCandidate_NotTheRun()
    {
        var h = Create();
        h.Cluster.StartingForever(new ModelDefinition { Id = "other", ServedName = "Other", ModelPath = "o" });
        h.Cluster.LaunchThrows = d => d.MaxNumSeqs == 8
            ? new ModelStartConflictException("Model 'other' is still starting.") { StartingModelId = "other" }
            : null;

        var run = await TuneAsync(h);

        var skipped = Assert.Single(run.Candidates, c => c.Label == "Max num seqs 8");
        Assert.Equal(CandidateOutcome.Failed, skipped.Outcome);
        Assert.Contains("other was still starting", skipped.Reason, StringComparison.Ordinal);
        Assert.Empty(h.Cluster.Stops); // nothing was launched, so nothing to clean up
        Assert.Contains(run.Candidates, c => c.Label == "Max num seqs 32" && c.Outcome == CandidateOutcome.Accepted);
        Assert.Equal(TuningRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task StartConflictThatClears_IsRetried_AndMeasured()
    {
        var h = Create(otherWait: TimeSpan.FromSeconds(5));
        h.Cluster.StartingForever(new ModelDefinition { Id = "other", ServedName = "Other", ModelPath = "o" });
        int conflicts = 0;
        h.Cluster.LaunchThrows = d =>
        {
            if (d.MaxNumSeqs != 8 || conflicts++ > 0)
            {
                return null;
            }

            h.Cluster.MarkReady("other");
            return new ModelStartConflictException("Model 'other' is still starting.") { StartingModelId = "other" };
        };

        var run = await TuneAsync(h);

        Assert.Equal(CandidateOutcome.Accepted, Assert.Single(run.Candidates, c => c.Label == "Max num seqs 8").Outcome);
    }

    [Fact]
    public async Task ReadinessFailure_CleansUpTheCandidateContainer()
    {
        var h = Create();
        h.Cluster.FailWith = d => d.MaxNumSeqs == 8 ? "Cause: CUDA error: illegal instruction" : null;

        var run = await TuneAsync(h);

        Assert.Equal("Failed to start: CUDA error: illegal instruction", Assert.Single(run.Candidates, c => c.Label == "Max num seqs 8").Reason);
        Assert.Equal(["qwen"], h.Cluster.Stops);
        Assert.Equal(TuningRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task BenchmarkFailure_CleansUpTheCandidateContainer()
    {
        var h = Create();
        h.Suite.Score = d => d.MaxNumSeqs == 8 ? throw new BenchmarkRequestException(500, "engine dead") : Results.Make(10, 100);

        var run = await TuneAsync(h);

        var failed = Assert.Single(run.Candidates, c => c.Label == "Max num seqs 8");
        Assert.StartsWith("Benchmark failed:", failed.Reason, StringComparison.Ordinal);
        Assert.Equal(["qwen"], h.Cluster.Stops);
        Assert.Equal(TuningRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task CleanupThatFails_StopsTheRun_RecordsTheCandidate_AndRestoresWithAReadyCheck()
    {
        var h = Create();
        h.Cluster.FailWith = d => d.MaxNumSeqs == 8 ? "Cause: boom" : null;
        h.Cluster.StopThrows = new HttpRequestException("Docker is unreachable");

        var run = await TuneAsync(h);

        Assert.Equal(TuningRunStatus.Failed, run.Status);
        Assert.Contains("could not remove the failed candidate's container: Docker is unreachable", run.Error, StringComparison.Ordinal);
        Assert.Equal(CandidateOutcome.Failed, Assert.Single(run.Candidates, c => c.Label == "Max num seqs 8").Outcome);
        Assert.DoesNotContain(run.Candidates, c => c.Label == "Max num seqs 32");

        var last = h.Cluster.Launches[^1];
        Assert.Equal(Saved, last.Definition);
        Assert.False(last.Options.Tuning);
        Assert.Contains("Restored the saved settings; the model is Ready again.", run.Notes);
    }

    [Fact]
    public async Task CleanupThatNeverFinishes_StopsTheRunWithinTheTimeout()
    {
        var h = Create();
        h.Cluster.FailWith = d => d.MaxNumSeqs == 8 ? "Cause: boom" : null;
        h.Cluster.StopKeepsContainer = true;

        var run = await TuneAsync(h);

        Assert.Equal(TuningRunStatus.Failed, run.Status);
        Assert.StartsWith("Tuning stopped: ", run.Error, StringComparison.Ordinal); // "still there" or "did not finish within", whichever bound trips first
    }

    [Fact]
    public async Task RestoreThatDoesNotBecomeReady_IsReported()
    {
        var h = Create();
        int launches = 0;
        h.Cluster.FailWith = d => ++launches > 1 && d == Saved ? "Cause: out of memory" : null;

        var run = await TuneAsync(h);

        Assert.Equal(TuningRunStatus.Completed, run.Status);
        Assert.Contains(run.Notes, n => n.Contains("did not become Ready: Failed to start: out of memory", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_WhileWaitingForAnotherModel_CancelsTheRun()
    {
        var h = Create(otherWait: TimeSpan.FromSeconds(30));
        h.Cluster.StartingForever(new ModelDefinition { Id = "other", ServedName = "Other", ModelPath = "o" });
        h.Cluster.LaunchThrows = d => d.MaxNumSeqs == 8
            ? new ModelStartConflictException("Model 'other' is still starting.") { StartingModelId = "other" }
            : null;

        await h.Jobs.StartTuningAsync("qwen", TuningGoal.ManyAgents, [TuningDimension.MaxNumSeqs], TestContext.Current.CancellationToken);
        while (h.Jobs.Current?.Phase.StartsWith("Waiting for other", StringComparison.Ordinal) != true)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.True(h.Jobs.Cancel(JobKind.Tuning));
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListTuningAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Equal(TuningRunStatus.Cancelled, run.Status);
    }

    // ------------------------------------------------------------ A5: pooling models

    [Fact]
    public void Plan_PoolingModel_OnlyVariesSeqsAndBatchedTokens()
    {
        var plan = TuningPlanner.Plan(Embedder, WithMtp);

        Assert.Equal(TuningPlanner.PoolingNotApplicable, plan.Steps.Single(s => s.Dimension == TuningDimension.SpeculativeDecoding).SkipReason);
        Assert.Equal(TuningPlanner.PoolingNotApplicable, plan.Steps.Single(s => s.Dimension == TuningDimension.KvCacheDtype).SkipReason);
        Assert.Equal([4, 8, 16, 32, 64], plan.Steps.Single(s => s.Dimension == TuningDimension.MaxNumSeqs).Values.Select(v => v.Number!.Value));
        Assert.Equal([2048, 4096, 8192, 16384, 32768], plan.Steps.Single(s => s.Dimension == TuningDimension.MaxNumBatchedTokens).Values.Select(v => v.Number!.Value));
        Assert.All(plan.PreviewCandidates, c =>
        {
            Assert.Null(c.Settings.Speculative);
            Assert.Null(c.Settings.CudaGraphMode);
            Assert.Null(c.Settings.AsyncScheduling);
            Assert.Equal(Embedder.KvCacheDtype, c.Settings.KvCacheDtype);
        });
        Assert.Equal(1 + 4 + 5, plan.PreviewCandidates.Count); // baseline + seqs (16 is the baseline) + batched
    }

    [Fact]
    public async Task Tuning_PoolingModel_UsesTheEmbeddingsBenchmark_AndScoresInputTokS()
    {
        var h = Create(Embedder);
        h.Suite.ScoreTarget = (d, target) =>
        {
            Assert.Equal(PoolingMode.Embedding, target.Pooling);
            Assert.Equal(d.MaxNumSeqs, target.MaxNumSeqs);
            return Results.Embeddings(in1: 1000, in16: 4000 + (d.MaxNumSeqs * 10), p95At16: d.MaxNumSeqs == 64 ? 5 : 0.5);
        };

        var run = await TuneAsync(h, "embed", TuningGoal.ManyAgents, TuningDimension.MaxNumSeqs, TuningDimension.SpeculativeDecoding);

        Assert.DoesNotContain(run.Candidates, c => c.Step == "Speculative decoding");
        var guarded = Assert.Single(run.Candidates, c => c.Label == "Max num seqs 64");
        Assert.Equal(CandidateOutcome.Rejected, guarded.Outcome);
        Assert.Contains("Latency p95 at N=16", guarded.Reason, StringComparison.Ordinal);
        Assert.Equal("Max num seqs 32", run.Winner!.Label);
        Assert.Equal(4320, run.Winner.Score);
    }

    [Fact]
    public async Task Tuning_Reranker_IsRefused()
    {
        var reranker = Embedder with { Id = "rerank", ServedName = "Rerank", ExtraArgs = ["--convert", "classify"] };
        var h = Create(reranker);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Jobs.StartTuningAsync("rerank", TuningGoal.Balanced, null, TestContext.Current.CancellationToken));
        Assert.Equal(BenchmarkSuite.RerankerUnsupported, ex.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Jobs.StartBenchmarkAsync("rerank", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Scorer_Pooling_RejectsInvalidVectors_AndANormalizationChange()
    {
        var baseline = Results.Embeddings(1000, 4000);

        var invalid = TuningScorer.Evaluate(TuningGoal.Balanced, baseline, Results.Embeddings(2000, 8000, valid: false));
        Assert.False(invalid.Accepted);
        Assert.Contains("Invalid embeddings", invalid.Reason, StringComparison.Ordinal);

        var unnormalized = TuningScorer.Evaluate(TuningGoal.Balanced, baseline, Results.Embeddings(2000, 8000, normalized: false));
        Assert.False(unnormalized.Accepted);

        var better = TuningScorer.Evaluate(TuningGoal.Balanced, baseline, Results.Embeddings(1000, 9000));
        Assert.True(better.Accepted);
        Assert.Equal(Math.Sqrt(9000.0 / 4000), better.Score!.Value, 6);
        Assert.Equal(1000, TuningScorer.Score(TuningGoal.SingleUser, baseline, baseline));
    }
}
