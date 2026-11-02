using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

public sealed class TuningPlannerTests
{
    private static readonly ModelDefinition Baseline = new()
    {
        Id = "m",
        ServedName = "M",
        ModelPath = "M",
        MemoryBudgetGiB = 44,
        MaxModelLen = 262_144,
        MaxNumSeqs = 16,
    };

    private static ModelFolderInspection Inspection(bool mtp, double weightsGiB = 24, double kvBytesPerToken = 32_768) => new(
        "M", "Qwen3_5ForConditionalGeneration", 262_144, "NVFP4", weightsGiB, mtp,
        new ModelFolderSuggestion("M", null, null, null, 44, ""), kvBytesPerToken);

    [Fact]
    public void Steps_AreInTheFixedOrder()
    {
        var plan = TuningPlanner.Plan(Baseline, Inspection(mtp: true));
        Assert.Equal(
            [TuningDimension.SpeculativeDecoding, TuningDimension.MaxNumSeqs, TuningDimension.MaxNumBatchedTokens, TuningDimension.KvCacheDtype],
            plan.Steps.Select(s => s.Dimension));
    }

    [Fact]
    public void Mtp_IsOnlyTried_WhenTheCheckpointHasMtpLayers()
    {
        var with = TuningPlanner.Plan(Baseline, Inspection(mtp: true)).Steps[0];
        var without = TuningPlanner.Plan(Baseline, Inspection(mtp: false)).Steps[0];

        Assert.Equal(["Off", "MTP × 1", "MTP × 2", "MTP × 3"], with.Values.Select(v => v.Label));
        Assert.Equal(["Off"], without.Values.Select(v => v.Label));
        Assert.Contains("no MTP layers", without.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Mtp_NoLongerForcesPiecewiseCudaGraphs()
    {
        // Plan B removed the GB10 workaround: a graph mode that crashes just fails that candidate.
        var mtp2 = TuningPlanner.Apply(TuningDimension.SpeculativeDecoding, new TuningValue("MTP × 2", Number: 2), Baseline with { CudaGraphMode = "FULL" });
        Assert.Equal(new SpeculativeDecoding("mtp", 2), mtp2.Speculative);
        Assert.Equal("FULL", mtp2.CudaGraphMode);
    }

    [Fact]
    public void KvCacheAuto_IsGatedByTheBudget()
    {
        // 24 GiB weights + 7 overhead + 2 × 8 GiB KV at auto = 47 GiB > 44 GiB budget.
        var gated = TuningPlanner.Plan(Baseline, Inspection(mtp: false)).Steps[3];
        Assert.NotNull(gated.SkipReason);
        Assert.Contains("47 GiB", gated.SkipReason, StringComparison.Ordinal);

        var roomy = TuningPlanner.Plan(Baseline with { MemoryBudgetGiB = 60 }, Inspection(mtp: false)).Steps[3];
        Assert.Null(roomy.SkipReason);
        Assert.Equal(["fp8", "auto"], roomy.Values.Select(v => v.Text));
    }

    [Fact]
    public void KvStep_IsSkipped_WhenTheCheckpointCannotBeInspected()
    {
        var plan = TuningPlanner.Plan(Baseline, inspection: null);
        Assert.NotNull(plan.Steps[3].SkipReason);
        Assert.Contains("could not be read", plan.Steps[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_DropsCandidatesIdenticalToTheBaseline_AndNeverTouchesContextOrBudget()
    {
        var plan = TuningPlanner.Plan(Baseline, Inspection(mtp: true), [TuningDimension.SpeculativeDecoding, TuningDimension.MaxNumSeqs]);

        // "Off" and "seqs 16" equal the baseline and are skipped.
        Assert.Equal(["Baseline (saved settings)", "MTP × 1", "MTP × 2", "MTP × 3", "Max num seqs 8", "Max num seqs 32"],
            plan.PreviewCandidates.Select(c => c.Label));
        Assert.All(plan.PreviewCandidates, c =>
        {
            Assert.Equal(Baseline.MaxModelLen, c.Settings.MaxModelLen);
            Assert.Equal(Baseline.MemoryBudgetGiB, c.Settings.MemoryBudgetGiB);
        });
        Assert.Equal("Not selected.", plan.Steps[2].SkipReason);
    }

    [Fact]
    public void CandidatesFor_BuildOnTheBestSoFar()
    {
        var plan = TuningPlanner.Plan(Baseline, Inspection(mtp: true));
        var best = Baseline with { Speculative = new SpeculativeDecoding("mtp", 3), CudaGraphMode = "PIECEWISE" };

        var seqs = TuningPlanner.CandidatesFor(plan.Steps[1], best);

        Assert.All(seqs, c => Assert.Equal(best.Speculative, c.Settings.Speculative));
        Assert.Equal([8, 16, 32], seqs.Select(c => c.Settings.MaxNumSeqs));
    }

    [Fact]
    public void Plan_IsDeterministic()
    {
        var a = TuningPlanner.Plan(Baseline, Inspection(mtp: true));
        var b = TuningPlanner.Plan(Baseline, Inspection(mtp: true));
        Assert.Equal(a.PreviewCandidates.Select(c => TuningPlanner.Fingerprint(c.Settings)), b.PreviewCandidates.Select(c => TuningPlanner.Fingerprint(c.Settings)));
    }
}

public sealed class TuningScorerTests
{
    [Fact]
    public void SingleUser_ScoresMedianN1TokensPerSecond()
    {
        var baseline = Results.Make(10, 100);
        var eval = TuningScorer.Evaluate(TuningGoal.SingleUser, baseline, Results.Make(12.5, 80));
        Assert.True(eval.Accepted);
        Assert.Equal(12.5, eval.Score);
    }

    [Fact]
    public void SingleUser_TiesBreakOnLowerTtft()
    {
        var a = Candidate(1, Results.Make(12, 100, ttftP50At1: 0.40), 12);
        var b = Candidate(2, Results.Make(12, 100, ttftP50At1: 0.25), 12);
        Assert.Equal(2, TuningScorer.PickBest([a, b])!.Index);
    }

    [Fact]
    public void ManyAgents_ScoresN16Aggregate_WithinTheTtftGuard()
    {
        var baseline = Results.Make(10, 400, ttftP95At16: 2.0);

        var ok = TuningScorer.Evaluate(TuningGoal.ManyAgents, baseline, Results.Make(8, 520, ttftP95At16: 3.9));
        Assert.True(ok.Accepted);
        Assert.Equal(520, ok.Score);

        var slow = TuningScorer.Evaluate(TuningGoal.ManyAgents, baseline, Results.Make(8, 700, ttftP95At16: 4.5));
        Assert.False(slow.Accepted);
        Assert.Equal("TTFT p95 at N=16 is 4.50 s, more than 2× the baseline's 2.00 s", slow.Reason);
    }

    [Fact]
    public void Balanced_IsTheGeometricMeanOfBothGains_WithTheSameGuard()
    {
        var baseline = Results.Make(10, 400, ttftP95At16: 1.0);

        var eval = TuningScorer.Evaluate(TuningGoal.Balanced, baseline, Results.Make(12, 300, ttftP95At16: 1.5));
        Assert.True(eval.Accepted);
        Assert.Equal(Math.Sqrt(1.2 * 0.75), eval.Score!.Value, 9);

        Assert.Equal(1.0, TuningScorer.Evaluate(TuningGoal.Balanced, baseline, baseline).Score!.Value, 9);
        Assert.False(TuningScorer.Evaluate(TuningGoal.Balanced, baseline, Results.Make(20, 800, ttftP95At16: 2.5)).Accepted);
    }

    [Theory]
    [InlineData(TuningGoal.Balanced)]
    [InlineData(TuningGoal.ManyAgents)]
    [InlineData(TuningGoal.SingleUser)]
    public void Guardrails_RejectRegardlessOfSpeed(TuningGoal goal)
    {
        var baseline = Results.Make(10, 400, qa: 7);

        var noTool = TuningScorer.Evaluate(goal, baseline, Results.Make(99, 9999, tool: false));
        Assert.False(noTool.Accepted);
        Assert.StartsWith("Tool-call check failed", noTool.Reason, StringComparison.Ordinal);

        var worseAnswers = TuningScorer.Evaluate(goal, baseline, Results.Make(99, 9999, qa: 6));
        Assert.False(worseAnswers.Accepted);
        Assert.Equal("Objective answers 6/8, below the baseline's 7/8", worseAnswers.Reason);

        Assert.True(TuningScorer.Evaluate(goal, baseline, Results.Make(11, 410, qa: 7)).Accepted);
    }

    [Fact]
    public void PickBest_IgnoresRejectedAndFailedCandidates_AndGainIsRelativeToTheBaseline()
    {
        var baseline = Candidate(0, Results.Make(10, 100), 10);
        var rejected = Candidate(1, Results.Make(50, 100), 50) with { Outcome = CandidateOutcome.Rejected };
        var failed = Candidate(2, null, null) with { Outcome = CandidateOutcome.Failed };
        var better = Candidate(3, Results.Make(12, 100), 12);

        var best = TuningScorer.PickBest([baseline, rejected, failed, better]);

        Assert.Equal(3, best!.Index);
        Assert.Equal(20, TuningScorer.GainPercent(best.Score, baseline.Score)!.Value, 9);
    }

    private static TuningCandidateResult Candidate(int index, BenchmarkResult? result, double? score) =>
        new(index, "step", $"c{index}", new ModelDefinition { Id = "m", ServedName = "M", ModelPath = "M" }, "hash",
            CandidateOutcome.Accepted, null, result, score, null);
}
