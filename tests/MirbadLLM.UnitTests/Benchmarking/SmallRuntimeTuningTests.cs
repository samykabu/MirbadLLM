using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Benchmarking;

public sealed class SmallRuntimeTuningTests
{
    private static readonly ModelDefinition Sglang = new()
    {
        Id = "sg",
        ServedName = "sg",
        ModelPath = "sg",
        Runtime = RuntimeKind.Sglang,
        ToolCallParser = null,
        ReasoningParser = null,
        Container = new ContainerRuntimeSettings { Image = "sglang:1", ExtraArgs = ["--tool-call-parser", "qwen3_coder", "--max-running-requests=8"] },
        Speculative = new SpeculativeDecoding("DFLASH", 8) { DraftModel = "/models/d" },
    };

    private static readonly ModelDefinition Mlx = new()
    {
        Id = "mx",
        ServedName = "mx",
        ModelPath = "mx",
        Runtime = RuntimeKind.Mlx,
        ToolCallParser = null,
        ReasoningParser = null,
        Mlx = new MlxSettings { ModelFolder = "mx", Parallel = 4 },
    };

    private static TuningPlan PlanOf(ModelDefinition model, params TuningDimension[] dimensions) =>
        TuningPlanner.Plan(model, null, dimensions.Length == 0 ? null : dimensions);

    [Fact]
    public void Sglang_Candidates_RunDifferentServers_WithoutTheBaselinesOwnValue_NorADeeperDraft()
    {
        var plan = PlanOf(Sglang);

        var labels = plan.PreviewCandidates.Select(c => c.Label).ToList();
        Assert.Equal(
            ["Baseline (saved settings)", "Max running requests 4", "Max running requests 16", "Max running requests 32", "Schedule policy lpm", "Draft depth 4", "Draft depth 6"],
            labels);
        Assert.Equal(labels.Count, plan.PreviewCandidates.Select(c => SmallRuntimeTuning.Fingerprint(c.Settings)).Distinct().Count());

        var four = plan.PreviewCandidates.Single(c => c.Label == "Max running requests 4").Settings;
        Assert.Equal(["--tool-call-parser", "qwen3_coder", "--max-running-requests", "4"], four.Container!.ExtraArgs);
    }

    [Fact]
    public void Sglang_WithoutSpeculativeDecoding_SkipsTheDepthStep_AndAnUnrelatedSelectionMeansAllSteps()
    {
        var plan = PlanOf(Sglang with { Speculative = null }, TuningDimension.MaxNumSeqs);

        Assert.Contains("no speculative decoding", plan.Steps.Single(s => s.Dimension == TuningDimension.SglangDraftDepth).SkipReason, StringComparison.Ordinal);
        Assert.Null(plan.Steps.Single(s => s.Dimension == TuningDimension.SglangSchedulePolicy).SkipReason);
    }

    [Fact]
    public void Sglang_Merge_TakesOnlyTheTunedSettings_AndKeepsOtherArguments()
    {
        var candidate = Sglang with
        {
            Container = Sglang.Container! with { ExtraArgs = ["--stale", "1", "--max-running-requests", "16", "--schedule-policy", "lpm"] },
            Speculative = Sglang.Speculative! with { NumSpeculativeTokens = 4, DraftModel = "/models/other" },
        };
        var saved = Sglang with { Container = Sglang.Container with { ExtraArgs = ["--reasoning-parser", "qwen3", "--max-running-requests", "8"] } };

        var merged = SmallRuntimeTuning.MergeTuned(saved, candidate);

        Assert.Equal(["--reasoning-parser", "qwen3", "--max-running-requests", "16", "--schedule-policy", "lpm"], merged.Container!.ExtraArgs);
        Assert.Equal((4, "/models/d"), (merged.Speculative!.NumSpeculativeTokens, merged.Speculative.DraftModel));

        var plain = SmallRuntimeTuning.MergeTuned(saved, Sglang with { Container = Sglang.Container with { ExtraArgs = [] } });
        Assert.Equal(["--reasoning-parser", "qwen3"], plain.Container!.ExtraArgs);
    }

    [Fact]
    public void Mlx_Candidates_ChangeWhatTheServerIsStartedWith_AndAQuantizedCacheStaysSerial()
    {
        var plan = PlanOf(Mlx);

        Assert.Contains(plan.PreviewCandidates, c => c.Settings.Mlx!.Parallel == 8);
        Assert.Contains(plan.PreviewCandidates, c => c.Settings.Mlx!.KvBits == 4);
        Assert.Contains("no draft model", plan.Steps.Single(s => s.Dimension == TuningDimension.MlxDraftTokens).SkipReason, StringComparison.Ordinal);
        Assert.Equal(plan.PreviewCandidates.Count, plan.PreviewCandidates.Select(c => TuningPlanner.Fingerprint(c.Settings)).Distinct().Count());

        var quantized = PlanOf(Mlx with { Mlx = Mlx.Mlx! with { KvBits = 8, Parallel = 1 } });
        Assert.Contains("one request at a time", quantized.Steps.Single(s => s.Dimension == TuningDimension.MlxParallel).SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Mlx_Merge_TakesOnlyTheTunedSettings()
    {
        var candidate = Mlx with { Mlx = Mlx.Mlx! with { Parallel = 2, PromptCacheGiB = 2, ModelFolder = "other", ContextSize = 1 } };

        var merged = SmallRuntimeTuning.MergeTuned(Mlx, candidate);

        Assert.Equal((2, 2d, "mx", 8_192), (merged.Mlx!.Parallel, merged.Mlx.PromptCacheGiB, merged.Mlx.ModelFolder, merged.Mlx.ContextSize));
    }

    [Fact]
    public void TheVllmSearch_NeverGetsTheseSteps_AndNimStaysRefused()
    {
        Assert.DoesNotContain(SearchPlanner.Full, SmallRuntimeTuning.IsSmallDimension);
        Assert.Null(TuningSupport.RefusalFor(Sglang));
        Assert.Null(TuningSupport.RefusalFor(Mlx));
        Assert.Contains("isn't available for NIM", TuningSupport.RefusalFor(Sglang with { Runtime = RuntimeKind.Nim }), StringComparison.Ordinal);
    }

    [Fact]
    public void BenchmarkTargets_AreSizedForTheirRuntime()
    {
        var uri = new Uri("http://x/");
        var mlx = BenchmarkSuite.TargetFor("mx", Mlx, uri, null, null);
        Assert.Equal((RuntimeKind.Mlx, 8_192, 4), (mlx.Runtime, mlx.MaxModelLen, mlx.MaxNumSeqs));
        Assert.Equal(1, BenchmarkSuite.TargetFor("mx", Mlx with { Mlx = Mlx.Mlx! with { KvBits = 8 } }, uri, null, null).MaxNumSeqs);

        var context = Sglang with { Container = Sglang.Container! with { ExtraArgs = ["--context-length", "4096"] } };
        var sglang = BenchmarkSuite.TargetFor("sg", context, uri, null, null);
        Assert.Equal((RuntimeKind.Sglang, 4096), (sglang.Runtime, sglang.MaxModelLen));
    }
}
