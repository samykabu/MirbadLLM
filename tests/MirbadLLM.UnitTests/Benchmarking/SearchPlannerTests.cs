using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Orchestration;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>
/// The workload-preset search (plan B + D): capability gates, phase order, both prefix-cache configurations,
/// speculative × FULL_AND_PIECEWISE with PIECEWISE follow-ups, refinement, de-duplication, Quick/Full candidate counts
/// for the live models, the description catalog, runtime-log parsing, ETA math and the relative guard fallback.
/// </summary>
public sealed class SearchPlannerTests
{
    // ------------------------------------------------------------ live-model fixtures

    public static readonly ModelDefinition Qwen35B = VllmArgsNormalizer.Apply(LiveDefinitions.Qwen35B);

    public static readonly ModelFolderInspection Qwen35BInspection = Inspection("Qwen3_5MoeForConditionalGeneration", weightsGiB: 21, mtp: true, moe: true);

    /// <summary>The 27B: dense, NVFP4 (about 32 B parameters by weight size), MTP layers, saved MTP × 2.</summary>
    public static readonly ModelDefinition Qwen27B = new()
    {
        Id = "qwen38-27b-nvfp4",
        ServedName = "qwen38-27b",
        ModelPath = "Qwen3.8-27B-NVFP4",
        MemoryBudgetGiB = 48,
        MaxNumSeqs = 4,
        Speculative = new SpeculativeDecoding("mtp", 2),
        CudaGraphMode = "PIECEWISE",
    };

    public static readonly ModelFolderInspection Qwen27BInspection = Inspection("Qwen3_5ForConditionalGeneration", weightsGiB: 17, mtp: true, moe: false);

    public static readonly ModelDefinition Nemotron = VllmArgsNormalizer.Apply(LiveDefinitions.Nemotron);

    public static readonly ModelFolderInspection NemotronInspection = Inspection("NemotronHForCausalLM", weightsGiB: 18, mtp: false, moe: true, hybrid: true);

    public static readonly ModelDefinition Embedding = LiveDefinitions.Embedding;

    public static ModelFolderInspection Inspection(string architecture, double weightsGiB, bool mtp, bool moe, bool hybrid = false, int? blockSize = null) =>
        new("ckpt", architecture, 262_144, "pre-quantized (modelopt, NVFP4)", weightsGiB, mtp,
            new ModelFolderSuggestion("m", null, null, null, 40, ""), KvBytesPerToken: 16_384, IsMoe: moe, IsMambaHybrid: hybrid, SpeculativeBlockSize: blockSize);

    private static TuningPlan Plan(ModelDefinition model, ModelFolderInspection? inspection, IReadOnlyCollection<TuningDimension>? dims = null,
        Func<string, ModelFolderInspection?>? draft = null, IReadOnlyCollection<string>? registry = null) =>
        SearchPlanner.Plan(model, inspection, dims, draft, registry ?? []);

    // ------------------------------------------------------------ gates

    [Fact]
    public void MtpGate_UsesTheImageRegistry_WhenKnown()
    {
        var allowed = TuningGates.Compute(Qwen27B with { Speculative = null }, Qwen27BInspection, null, ["Qwen3_5MTP", "DeepSeekMTP"]);
        Assert.Equal([1, 2, 3, 5], allowed.MtpDepths);

        var missing = TuningGates.Compute(Qwen27B, Qwen27BInspection, null, ["DeepSeekMTP"]);
        Assert.Empty(missing.MtpDepths);
        Assert.Contains("registers no MTP architecture for Qwen3_5ForConditionalGeneration", missing.MtpSkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void MtpGate_WithAnUnknownRegistry_OnlyTrustsASavedMtpConfig()
    {
        Assert.Equal([1, 2, 3, 5], TuningGates.Compute(Qwen27B, Qwen27BInspection, null, []).MtpDepths);

        var unknown = TuningGates.Compute(Qwen27B with { Speculative = null }, Qwen27BInspection, null, []);
        Assert.Empty(unknown.MtpDepths);
        Assert.Contains("can't confirm the image registers an MTP architecture", unknown.MtpSkipReason, StringComparison.Ordinal);

        Assert.Contains("no MTP layers", TuningGates.Compute(Qwen27B, Qwen27BInspection with { MaySupportMtp = false }, null, []).MtpSkipReason, StringComparison.Ordinal);
        Assert.Contains("could not be read", TuningGates.Compute(Qwen27B, null, null, []).MtpSkipReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Qwen3_5MoeForConditionalGeneration", "Qwen3_5MoeMTP", true)]
    [InlineData("Qwen3_5MoeForConditionalGeneration", "Qwen3_5MTP", true)]
    [InlineData("DeepseekV3ForCausalLM", "DeepSeekMTP", false)]
    [InlineData("Glm4MoeForCausalLM", "Glm4MoeMTP", true)]
    [InlineData("Qwen3_5ForConditionalGeneration", "Qwen3_5Moe", false)]
    public void MtpMatches(string architecture, string registered, bool expected) =>
        Assert.Equal(expected, TuningGates.MtpMatches(architecture, registered));

    [Fact]
    public void DraftGate_FollowsTheTrainedBlockSize_OrOnlyTheSavedDepth()
    {
        string? inspected = null;
        var known = TuningGates.Compute(Nemotron, NemotronInspection,
            folder => { inspected = folder; return NemotronInspection with { SpeculativeBlockSize = 8 }; }, []);
        Assert.Equal("Nemotron-3.5-Lightning-30B-A3B-NVFP4-DSpark", inspected);
        Assert.Equal([3, 7], known.DraftDepths); // 11 is beyond the trained block size

        var unknown = TuningGates.Compute(Nemotron, NemotronInspection, _ => NemotronInspection, []);
        Assert.Equal([3], unknown.DraftDepths);
        Assert.Contains(unknown.Notes, n => n.Contains("only the saved depth", StringComparison.Ordinal));

        Assert.Contains("no draft model", TuningGates.Compute(Qwen27B, Qwen27BInspection, null, []).DraftSkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Concurrency_DenseModelsOver20B_AlsoTry1And2_AndBatchedKeepsTheSavedValue()
    {
        Assert.Equal([1, 2, 4, 8, 16, 32, 64], TuningGates.Compute(Qwen27B, Qwen27BInspection, null, []).MaxNumSeqsValues);
        Assert.Equal([4, 8, 16, 32, 64], TuningGates.Compute(Qwen35B, Qwen35BInspection, null, []).MaxNumSeqsValues); // MoE

        var saved = TuningGates.Compute(Qwen35B with { MaxNumBatchedTokens = 6000 }, Qwen35BInspection, null, []);
        Assert.Equal([2048, 4096, 6000, 8192, 16384, 32768], saved.BatchedTokenValues);

        var noChunked = TuningGates.Compute(Qwen35B with { MaxModelLen = 16384, ExtraArgs = ["--no-enable-chunked-prefill"] }, Qwen35BInspection, null, []);
        Assert.Equal([16384, 32768], noChunked.BatchedTokenValues);
    }

    [Fact]
    public void KvAuto_IsGatedByMemory_Fp8AlwaysEligible()
    {
        var tight = Plan(Qwen35B with { MemoryBudgetGiB = 30 }, Qwen35BInspection, SearchPlanner.Full);
        var memory = tight.Steps[0];
        Assert.All(memory.Values, v => Assert.Equal("fp8", v.Text));
        Assert.Contains("auto (BF16) KV cache needs", memory.Note, StringComparison.Ordinal);

        var roomy = Plan(Qwen35B, Qwen35BInspection, SearchPlanner.Full);
        Assert.Equal(["fp8", "fp8", "auto", "auto"], roomy.Steps[0].Values.Select(v => v.Text));
    }

    // ------------------------------------------------------------ phases

    [Fact]
    public void Full_RunsThePhasesInOrder()
    {
        var plan = Plan(Qwen35B, Qwen35BInspection, SearchPlanner.Full);

        Assert.Equal(
            [TuningDimension.KvCacheDtype, TuningDimension.MaxNumSeqs, TuningDimension.MaxNumBatchedTokens, TuningDimension.SpeculativeDecoding,
             TuningDimension.AsyncScheduling, TuningDimension.Refinement, TuningDimension.Refinement],
            plan.Steps.Select(s => s.Dimension));
        Assert.Equal(
            [TuningDescriptions.PhaseMemory, TuningDescriptions.PhaseConcurrency, TuningDescriptions.PhaseConcurrency, TuningDescriptions.PhaseDecode,
             TuningDescriptions.PhaseDecode, TuningDescriptions.PhaseRefinement, TuningDescriptions.PhaseRefinement],
            plan.Steps.Select(s => s.Phase));
        Assert.All(plan.Steps, s => Assert.Null(s.SkipReason));
        Assert.NotNull(plan.Search);
    }

    [Fact]
    public void Phase1_AlwaysTriesBothPrefixConfigurations_PairedWithMambaModeForHybrids()
    {
        var plan = Plan(Nemotron, NemotronInspection, SearchPlanner.Full);
        var memory = plan.Steps[0];
        Assert.Equal([true, false, true, false], memory.Values.Select(v => v.Flag));

        var on = SearchPlanner.Apply(memory.Dimension, memory.Values[0], Nemotron with { EnablePrefixCaching = false, MambaCacheMode = "none" }, plan.Search!);
        var off = SearchPlanner.Apply(memory.Dimension, memory.Values[1], Nemotron, plan.Search!);
        Assert.Equal((true, "align"), (on.EnablePrefixCaching, on.MambaCacheMode));
        Assert.Equal((false, "none"), (off.EnablePrefixCaching, off.MambaCacheMode));

        // A non-hybrid model never gets a mamba cache mode.
        var qwen = Plan(Qwen35B, Qwen35BInspection, SearchPlanner.Full);
        Assert.Null(SearchPlanner.Apply(qwen.Steps[0].Dimension, qwen.Steps[0].Values[1], Qwen35B, qwen.Search!).MambaCacheMode);
    }

    [Fact]
    public void Phase3_SpeculativeValuesRunWithFullAndPiecewise_AndNoPiecewiseIsForced()
    {
        var plan = Plan(Qwen35B, Qwen35BInspection, SearchPlanner.Full);
        var decode = plan.Steps.Single(s => s.Dimension == TuningDimension.SpeculativeDecoding);

        Assert.Equal(["off", "saved", "mtp", "mtp", "mtp", "mtp"], decode.Values.Select(v => v.Method));
        Assert.Equal([1, 2, 3, 5], decode.Values.Where(v => v.Method == "mtp").Select(v => v.Number!.Value));
        Assert.All(decode.Values, v => Assert.Equal(SearchPlanner.FullAndPiecewise, v.Graph));

        var candidates = SearchPlanner.CandidatesFor(decode, Qwen35B, plan.Search!);
        Assert.All(candidates, c => Assert.Equal(SearchPlanner.FullAndPiecewise, c.Settings.CudaGraphMode));
        var mtp5 = candidates.Single(c => c.Label.StartsWith("MTP × 5", StringComparison.Ordinal)).Settings.Speculative!;
        Assert.Equal("""{"method":"mtp","moe_backend":"triton","num_speculative_tokens":5}""", mtp5.ToJson()); // extra keys kept
        Assert.Null(candidates.Single(c => c.Label.StartsWith("Speculative off", StringComparison.Ordinal)).Settings.Speculative);
    }

    [Fact]
    public void Phase3_DraftModels_TryTheDrafterDepths()
    {
        var plan = Plan(Nemotron, NemotronInspection, SearchPlanner.Full, _ => NemotronInspection with { SpeculativeBlockSize = 16 });
        var decode = plan.Steps.Single(s => s.Dimension == TuningDimension.SpeculativeDecoding);

        Assert.Equal([3, 7, 11], decode.Values.Where(v => v.Method == "draft").Select(v => v.Number!.Value));
        var draft7 = SearchPlanner.CandidatesFor(decode, Nemotron, plan.Search!).Single(c => c.Label.StartsWith("Draft × 7", StringComparison.Ordinal));
        Assert.Equal("/models/Nemotron-3.5-Lightning-30B-A3B-NVFP4-DSpark", draft7.Settings.Speculative!.DraftModel);
        Assert.Equal(7, draft7.Settings.Speculative.NumSpeculativeTokens);
    }

    [Fact]
    public void FollowUps_RetryPiecewise_ForFailedFullGraphs_AndTheTopTwoDepths()
    {
        var plan = Plan(Qwen35B, Qwen35BInspection, SearchPlanner.Full);
        var decode = plan.Steps.Single(s => s.Dimension == TuningDimension.SpeculativeDecoding);
        var candidates = SearchPlanner.CandidatesFor(decode, Qwen35B, plan.Search!);
        TuningCandidateResult Result(int i, CandidateOutcome outcome, double? score) =>
            new(i, decode.Title, candidates[i].Label, candidates[i].Settings, "h", outcome, null, null, score, 60);

        var results = new[]
        {
            Result(0, CandidateOutcome.Accepted, 0.9),  // off
            Result(2, CandidateOutcome.Accepted, 1.10), // MTP × 1
            Result(3, CandidateOutcome.Accepted, 1.20), // MTP × 2
            Result(4, CandidateOutcome.Accepted, 1.15), // MTP × 3
            Result(5, CandidateOutcome.Failed, null),    // MTP × 5 failed with full graphs
        };

        var followUps = SearchPlanner.FollowUps(decode, results, plan.Search!);

        Assert.Equal(["MTP × 5 · PIECEWISE", "MTP × 2 · PIECEWISE", "MTP × 3 · PIECEWISE"], followUps.Select(f => f.Label));
        Assert.All(followUps, f => Assert.Equal(SearchPlanner.Piecewise, f.Settings.CudaGraphMode));
        Assert.Contains("failed", followUps[0].Description, StringComparison.Ordinal);

        // Quick does not select CUDA graph mode: no follow-ups.
        var quick = Plan(Qwen35B, Qwen35BInspection, SearchPlanner.Quick);
        Assert.Empty(SearchPlanner.FollowUps(quick.Steps.Single(s => s.Dimension == TuningDimension.SpeculativeDecoding), results, quick.Search!));
    }

    [Fact]
    public void Refinement_RevisitsNeighboursOfTheWinners_ThenPrefixOnAndOff()
    {
        var plan = Plan(Qwen35B, Qwen35BInspection, SearchPlanner.Full);
        var refine = plan.Steps.Single(s => s.Key == SearchPlanner.RefineKey);
        var check = plan.Steps.Single(s => s.Key == SearchPlanner.PrefixCheckKey);
        var winner = Qwen35B with { MaxNumSeqs = 16, Speculative = Qwen35B.Speculative! with { NumSpeculativeTokens = 2 }, CudaGraphMode = SearchPlanner.FullAndPiecewise };

        var neighbours = SearchPlanner.CandidatesFor(refine, winner, plan.Search!);
        Assert.Equal(["Refine: max num seqs 8", "Refine: max num seqs 32", "Refine: depth 1", "Refine: depth 3"], neighbours.Select(n => n.Label));
        Assert.All(neighbours, n => Assert.Equal(SearchPlanner.FullAndPiecewise, n.Settings.CudaGraphMode)); // other winners fixed

        var prefix = SearchPlanner.CandidatesFor(check, winner, plan.Search!);
        Assert.Equal([true, false], prefix.Select(p => p.Settings.EnablePrefixCaching));
        Assert.All(prefix, p => Assert.Equal(winner.Speculative, p.Settings.Speculative));
    }

    [Fact]
    public void Preview_NeverListsTheSameEffectiveConfigurationTwice()
    {
        foreach (var plan in new[] { Plan(Qwen35B, Qwen35BInspection, SearchPlanner.Full), Plan(Nemotron, NemotronInspection, SearchPlanner.Full) })
        {
            var fingerprints = plan.PreviewCandidates.Select(c => TuningPlanner.Fingerprint(c.Settings)).ToList();
            Assert.Equal(fingerprints.Count, fingerprints.Distinct(StringComparer.Ordinal).Count());
        }
    }

    [Fact]
    public void PoolingModels_KeepThePhaseAPlan()
    {
        var plan = Plan(Embedding, Inspection("Qwen3ForCausalLM", 15, mtp: false, moe: false), SearchPlanner.Full);
        Assert.Null(plan.Search);
        Assert.Equal(TuningPlanner.PoolingNotApplicable, plan.Steps.Single(s => s.Dimension == TuningDimension.SpeculativeDecoding).SkipReason);
    }

    /// <summary>
    /// Candidate counts for the live models (preview = if the baseline stays best; expected adds the PIECEWISE
    /// follow-ups and refinement allowance; the confirmation stage adds 3 more re-measurements on top).
    /// </summary>
    [Theory]
    [InlineData("qwen35b", false, 10, 10)]
    [InlineData("qwen35b", true, 20, 24)]
    [InlineData("qwen27b", false, 12, 12)]
    [InlineData("qwen27b", true, 24, 28)]
    [InlineData("nemotron", false, 7, 7)]
    [InlineData("nemotron", true, 17, 21)]
    [InlineData("embedding", false, 5, 5)]
    [InlineData("embedding", true, 10, 10)]
    public void CandidateCounts_QuickAndFull_ForTheLiveModels(string model, bool full, int preview, int expected)
    {
        var (definition, inspection) = model switch
        {
            "qwen35b" => (Qwen35B, Qwen35BInspection),
            "qwen27b" => (Qwen27B, Qwen27BInspection),
            "nemotron" => (Nemotron, NemotronInspection),
            _ => (Embedding, Inspection("Qwen3ForCausalLM", 15, mtp: false, moe: false)),
        };

        var plan = Plan(definition, inspection, full ? SearchPlanner.Full : SearchPlanner.Quick, _ => inspection);

        Assert.Equal(preview, plan.PreviewCandidates.Count);
        Assert.Equal(expected, plan.ExpectedCandidates);
    }

    // ------------------------------------------------------------ D1 description catalog

    [Fact]
    public void Catalog_CoversEveryPhaseDimensionAndValue()
    {
        Assert.All(TuningDescriptions.Phases, p => AssertDescribed(TuningDescriptions.Phase(p)));
        Assert.All(Enum.GetValues<TuningDimension>(), d => AssertDescribed(TuningDescriptions.Dimension(d)));

        var plans = new[]
        {
            Plan(Qwen35B, Qwen35BInspection, SearchPlanner.Full),
            Plan(Qwen27B, Qwen27BInspection, SearchPlanner.Full),
            Plan(Nemotron, NemotronInspection, SearchPlanner.Full, _ => NemotronInspection with { SpeculativeBlockSize = 16 }),
            Plan(Qwen35B, Qwen35BInspection, [TuningDimension.CudaGraphMode, TuningDimension.PrefixCaching]),
        };
        foreach (var plan in plans)
        {
            Assert.All(plan.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Description)));
            foreach (var step in plan.Steps)
            {
                Assert.All(step.Values, v => AssertDescribed(TuningDescriptions.Value(step.Dimension, v)));
            }

            Assert.All(plan.PreviewCandidates, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
        }

        foreach (var method in new[] { "seqs", "depth", "prefix" })
        {
            AssertDescribed(TuningDescriptions.Value(TuningDimension.Refinement, new TuningValue("r", 4, Flag: true, Method: method)));
        }
    }

    private static void AssertDescribed(TuningDescription description)
    {
        Assert.False(string.IsNullOrWhiteSpace(description.Goal));
        Assert.False(string.IsNullOrWhiteSpace(description.Tradeoff));
        Assert.Contains("Tradeoff:", description.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveSummary_ShowsEveryOwnedSetting()
    {
        Assert.Equal("MTP × 3 · graphs PIECEWISE · seqs 8 · batched 8,192 · KV fp8 · prefix on · async on", TuningDescriptions.Summary(LiveDefinitions.Qwen35B));
        Assert.EndsWith("· mamba align", TuningDescriptions.Summary(LiveDefinitions.Nemotron), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ runtime log parsing

    [Fact]
    public void RuntimeLog_ResolvedGraphModeSpeculativeAndAsync_AreReadAndCompared()
    {
        string[] log =
        [
            "INFO 10-03 10:00:01 [core.py:75] Initializing a V1 LLM engine (v0.1.dev1) with config: model='/models/q', speculative_config=SpeculativeConfig(method='mtp', model='/models/q', num_spec_tokens=3), compilation_config={'level': 3, 'cudagraph_mode': <CUDAGraphMode.FULL_AND_PIECEWISE: (2, 1)>}, async_scheduling=True",
            "WARNING 10-03 10:00:02 [config.py:500] cudagraph_mode is set to PIECEWISE because speculative decoding with this backend does not support full graphs",
            "WARNING 10-03 10:00:03 [scheduler.py:80] max_num_scheduled_tokens is set to 2048 based on the speculative decoding settings. Consider increasing max_num_batched_tokens",
        ];

        var report = RuntimeLogParser.Parse(log);
        Assert.Equal("PIECEWISE", report.CudaGraphMode); // the last report wins
        Assert.Equal(("mtp", 3), (report.SpeculativeMethod, report.SpeculativeTokens!.Value));
        Assert.True(report.AsyncScheduling);

        var requested = Qwen35B with { CudaGraphMode = SearchPlanner.FullAndPiecewise };
        var notes = RuntimeLogParser.Compare(requested, report);
        Assert.Contains("CUDA graph mode: requested FULL_AND_PIECEWISE → ran PIECEWISE", notes);
        Assert.Contains(notes, n => n.StartsWith("max_num_scheduled_tokens is set to 2048", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeLog_NothingDifferent_IsAsRequested()
    {
        Assert.Equal([RuntimeLogParser.AsRequested], RuntimeLogParser.Compare(Qwen35B, RuntimeLogParser.Parse([])));

        var asyncOff = RuntimeLogParser.Parse(["WARNING Async scheduling is not supported with this speculative method; disabling it."]);
        Assert.False(asyncOff.AsyncScheduling);
        Assert.Contains("Async scheduling: requested on → ran off", RuntimeLogParser.Compare(Qwen35B, asyncOff));
    }

    [Theory]
    [InlineData("507,425", "65,536")]
    [InlineData("507425", "65536")]
    public void RuntimeLog_KvPoolLine_ParsesWithAndWithoutThousandsSeparators(string tokens, string len)
    {
        var report = RuntimeLogParser.Parse(
        [
            $"INFO 10-03 10:00:05 [kv_cache_utils.py:1087] GPU KV cache size: {tokens} tokens",
            $"INFO 10-03 10:00:05 [kv_cache_utils.py:1091] Maximum concurrency for {len} tokens per request: 7.74x",
        ]);

        Assert.Equal(507_425, report.KvCacheTokens);
        Assert.Equal(7.74, report.MaxConcurrency);
        Assert.Equal(65_536, report.ConcurrencyTokens);
        Assert.Equal("KV pool 507k tokens = 7.7 concurrent requests at 65,536 max model len", report.KvPoolLine);
        Assert.Equal([RuntimeLogParser.AsRequested], RuntimeLogParser.Compare(Qwen35B, report)); // report only, not a downgrade
        Assert.Null(RuntimeLogParser.Parse([]).KvPoolLine);
    }

    // ------------------------------------------------------------ ETA

    [Fact]
    public void Estimate_CandidatesTimesStartPlusBenchmark_MinusTheReusedStart_PlusConfirmations()
    {
        var startup = TimeSpan.FromSeconds(300);
        var bench = TimeSpan.FromSeconds(600);

        Assert.Equal(TimeSpan.FromSeconds((10 * 900) - 300 + (3 * (300 + 1000))), TuningEstimator.Estimate(10, true, 3, startup, bench, 5.0 / 3));
        Assert.Equal(TimeSpan.FromSeconds(10 * 900), TuningEstimator.Estimate(10, false, 0, startup, bench, 5.0 / 3));
        Assert.Equal(TimeSpan.FromSeconds(200), TuningEstimator.Median([100, 300, 200, double.NaN, 0], TimeSpan.FromMinutes(5)));
        Assert.Equal(TimeSpan.FromMinutes(5), TuningEstimator.Median([], TimeSpan.FromMinutes(5)));
    }

    // ------------------------------------------------------------ relative guard fallback

    [Fact]
    public void BaselineBreakingAnAbsoluteGuard_MakesItRelative()
    {
        var profile = WorkloadProfiles.For(TuningGoal.InteractiveChat);
        var baseline = Results.Workload(50, 400, ttftP95At1: 1.4);

        var (guards, notes) = WorkloadScorer.RelaxForBaseline(profile.Guards, baseline);
        Assert.Equal(1.4, guards.ShortTtftP95AtN1Seconds);
        Assert.Contains("no worse than the baseline", Assert.Single(notes), StringComparison.Ordinal);

        Assert.True(WorkloadScorer.Evaluate(profile, baseline, Results.Workload(55, 420, ttftP95At1: 1.3)).Accepted);
        Assert.True(WorkloadScorer.Evaluate(profile, baseline, baseline).Accepted);
        var worse = WorkloadScorer.Evaluate(profile, baseline, Results.Workload(80, 600, ttftP95At1: 1.5));
        Assert.False(worse.Accepted);
        Assert.Contains("above the 1.4 s limit", worse.Reason, StringComparison.Ordinal);

        Assert.Empty(WorkloadScorer.RelaxForBaseline(profile.Guards, Results.Workload(50, 400)).Notes);
    }
}
