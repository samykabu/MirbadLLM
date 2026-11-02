using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Orchestration;
using MirbadLLM.UnitTests.Web.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>
/// The llama.cpp small tune (design-p3p4 §A.6, MN-225): at most 12 candidates, one dimension at a time from the baseline;
/// context, layers, quant and split never varied; typed settings only (D126); the causal-embedding micro-batch (D118);
/// candidates that do not fit skipped with their numbers, at planning and during the run; the apply into
/// <see cref="LlamaCppSettings"/>; and the vLLM plans of every existing fixture unchanged (golden recorded before MN-225).
/// </summary>
public sealed class LlamaCppTuningTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-llamacpp-tune-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    // ------------------------------------------------------------ fixtures

    /// <summary>A Qwen3-8B-like chat GGUF: 36 layers, GQA 32/8, head 128, 152 K vocabulary, 4.7 GiB of tensors.</summary>
    public static readonly GgufSummary ChatGguf = new()
    {
        Folder = "qwen3-8b",
        File = "Qwen3-8B-Q4_K_M.gguf",
        GgufVersion = 3,
        Architecture = "qwen3",
        BlockCount = 36,
        ContextLength = 40_960,
        EmbeddingLength = 4096,
        HeadCount = 32,
        HeadCountKv = 8,
        KeyLength = 128,
        ValueLength = 128,
        VocabSize = 151_936,
        TensorBytes = 5_000_000_000,
        FileBytes = 5_030_000_000,
    };

    /// <summary>A Qwen3-Embedding-4B-like GGUF (causal, last-token pooling).</summary>
    public static readonly GgufSummary EmbeddingGguf = ChatGguf with
    {
        Folder = "qwen3-embedding-4b",
        File = "Qwen3-Embedding-4B-Q8_0.gguf",
        EmbeddingLength = 2560,
        VocabSize = 151_665,
        TensorBytes = 4_280_000_000,
        FileBytes = 4_300_000_000,
        PoolingType = "last",
    };

    public static readonly ModelDefinition Chat = new()
    {
        Id = "qwen3-8b-win",
        ServedName = "qwen3-8b",
        ModelPath = "qwen3-8b",
        MemoryBudgetGiB = 40,
        Runtime = RuntimeKind.LlamaCpp,
        Placement = new ModelPlacement("win-1", ["GPU-4090"]),
        LlamaCpp = new LlamaCppSettings
        {
            ModelFile = "qwen3-8b/Qwen3-8B-Q4_K_M.gguf",
            ContextSize = 8192,
            Parallel = 4,
            GpuLayers = 999,
            Delivery = LaunchDelivery.Process,
            ExecutableVersion = "b11476",
            ExtraArgs = ["--top-k", "40"],
        },
    };

    public static readonly ModelDefinition Embedder = new()
    {
        Id = "qwen3-embedding-4b-win",
        ServedName = "qwen3-embedding-4b",
        ModelPath = "qwen3-embedding-4b",
        MemoryBudgetGiB = 40,
        Runtime = RuntimeKind.LlamaCpp,
        Placement = new ModelPlacement("win-1", ["GPU-4090"]),
        LlamaCpp = new LlamaCppSettings
        {
            ModelFile = "qwen3-embedding-4b/Qwen3-Embedding-4B-Q8_0.gguf",
            ContextSize = 8192,
            Parallel = 1,
            Delivery = LaunchDelivery.Process,
            Embedding = true,
            Pooling = "last",
        },
    };

    private static ModelDefinition With(ModelDefinition model, Func<LlamaCppSettings, LlamaCppSettings> change) =>
        model with { LlamaCpp = change(model.LlamaCpp!) };

    private static double Estimate(ModelDefinition model, GgufSummary gguf) =>
        LlamaCppMemoryEstimate.Estimate(gguf, model.LlamaCpp!, Math.Max(1, model.Placement?.GpuUuids?.Count ?? 0)).BudgetGiB;

    /// <summary>
    /// The job's loop (<see cref="ModelJobService"/>): the baseline, then each step's candidates on the best so far, measured
    /// once per effective configuration (<see cref="TuningPlanner.Fingerprint"/>); <paramref name="pick"/> chooses the next
    /// best among a step's candidates (null = keep).
    /// </summary>
    private static List<ModelDefinition> Run(TuningPlan plan, Func<IReadOnlyList<TuningCandidatePlan>, TuningCandidatePlan?> pick, List<string>? skipped = null)
    {
        var baseline = plan.PreviewCandidates[0].Settings;
        var measured = new HashSet<string>(StringComparer.Ordinal) { TuningPlanner.Fingerprint(baseline) };
        var all = new List<ModelDefinition> { baseline };
        var best = baseline;
        foreach (var step in plan.Steps.Where(s => s.SkipReason is null))
        {
            var fresh = SearchPlanner.CandidatesFor(step, best, plan.Search!, skipped)
                .Where(c => measured.Add(TuningPlanner.Fingerprint(c.Settings)))
                .ToList();
            Assert.Empty(SearchPlanner.FollowUps(step, [], plan.Search!));
            all.AddRange(fresh.Select(c => c.Settings));
            best = pick(fresh)?.Settings ?? best;
        }

        return all;
    }

    // ------------------------------------------------------------ vLLM unchanged

    [Fact]
    public void VllmPlans_ForEveryFixture_AreUnchanged()
    {
        var golden = VllmPlanSnapshot.Parse(File.ReadAllText(VllmPlanSnapshot.GoldenPath()));
        var now = VllmPlanSnapshot.Compute();
        Assert.Equal(golden.Count, now.Count);
        var changed = golden.Where(kv => !now.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key).ToList();
        Assert.Empty(changed);
    }

    [Fact]
    public void Full_StillListsOnlyTheVllmDimensions()
    {
        Assert.Equal(
            [TuningDimension.SpeculativeDecoding, TuningDimension.MaxNumSeqs, TuningDimension.MaxNumBatchedTokens, TuningDimension.KvCacheDtype,
             TuningDimension.PrefixCaching, TuningDimension.CudaGraphMode, TuningDimension.AsyncScheduling, TuningDimension.Refinement],
            SearchPlanner.Full);
    }

    // ------------------------------------------------------------ the plan

    [Fact]
    public void EveryLlamaCppDimension_HasItsOwnGoalAndTradeoff_NotTheRefinementText()
    {
        // MN-230 (D130): the four llama.cpp dimensions fell through TuningDescriptions.Dimension's default, so every
        // llama.cpp step read "Re-check the neighbours of the winners ..." — the refinement text, not what it tries.
        string refinement = TuningDescriptions.Phase(TuningDescriptions.PhaseRefinement).Text;
        var flags = new Dictionary<TuningDimension, string>
        {
            [TuningDimension.Parallel] = "-np",
            [TuningDimension.FlashAttention] = "-fa",
            [TuningDimension.KvCacheTypes] = "-ctk",
            [TuningDimension.BatchSizes] = "-ub",
        };
        foreach (var (dimension, flag) in flags)
        {
            var description = TuningDescriptions.Dimension(dimension);
            Assert.NotEqual(refinement, description.Text);
            Assert.Contains(flag, description.Text, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(description.Goal), dimension.ToString());
            Assert.False(string.IsNullOrWhiteSpace(description.Tradeoff), dimension.ToString());
        }

        // The step rows of a real plan carry the same text, with the embedding wording for the micro-batch step.
        foreach (var step in TuningPlanner.Plan(Chat, null, null, ChatGguf).Steps)
        {
            Assert.Equal(TuningDescriptions.LlamaCppDimension(step.Dimension, embedding: false).Text, step.Description);
            Assert.NotEqual(refinement, step.Description);
        }

        var embeddingBatches = TuningPlanner.Plan(Embedder, null, null, EmbeddingGguf).Steps
            .Single(st => st.Dimension == TuningDimension.BatchSizes);
        Assert.Equal(TuningDescriptions.LlamaCppDimension(TuningDimension.BatchSizes, embedding: true).Text, embeddingBatches.Description);
        Assert.Contains("embedding model", embeddingBatches.Description!, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_VariesOneDimensionAtATime_InTheDesignOrder_WithoutTheBaselinesOwnValues()
    {
        var plan = TuningPlanner.Plan(Chat, null, null, ChatGguf);

        Assert.Equal(
            [TuningDimension.Parallel, TuningDimension.FlashAttention, TuningDimension.KvCacheTypes, TuningDimension.BatchSizes],
            plan.Steps.Select(s => s.Dimension));
        Assert.All(plan.Steps, s => Assert.Null(s.SkipReason));
        Assert.Equal([1, 2, 8], plan.Steps[0].Values.Select(v => v.Number!.Value)); // 4 is the baseline's
        Assert.Equal([true, false], plan.Steps[1].Values.Select(v => v.Flag!.Value)); // unset = auto, its own configuration
        Assert.Equal(["q8_0"], plan.Steps[2].Values.Select(v => v.Text)); // unset = f16
        Assert.Equal(["Batch 512 · micro-batch 256", "Batch 512 · micro-batch 512", "Batch 2048 · micro-batch 256"],
            plan.Steps[3].Values.Select(v => v.Label)); // 2048 × 512 is llama.cpp's default

        // Preview: each candidate differs from the baseline in exactly one setting.
        var baseline = plan.PreviewCandidates[0].Settings.LlamaCpp!;
        Assert.Equal(1 + 3 + 2 + 1 + 3, plan.PreviewCandidates.Count);
        Assert.Equal(plan.PreviewCandidates.Count, plan.MaxCandidates);
        foreach (var candidate in plan.PreviewCandidates.Skip(1))
        {
            var c = candidate.Settings.LlamaCpp!;
            int changed = new[]
            {
                c.Parallel != baseline.Parallel,
                c.FlashAttention != baseline.FlashAttention,
                c.CacheTypeK != baseline.CacheTypeK || c.CacheTypeV != baseline.CacheTypeV,
                c.BatchSize != baseline.BatchSize || c.MicroBatchSize != baseline.MicroBatchSize,
            }.Count(x => x);
            Assert.Equal(1, changed);
            Assert.Equal(c.CacheTypeK, c.CacheTypeV); // K and V together
        }

        Assert.NotNull(plan.Search!.LlamaCpp);
        Assert.Same(ChatGguf, plan.Search.LlamaCpp!.Gguf);
    }

    [Theory]
    [InlineData(TuningGoal.Balanced)]
    [InlineData(TuningGoal.InteractiveChat)]
    [InlineData(TuningGoal.Embeddings)]
    public void SearchPlanner_GivesALlamaCppModelTheSameSmallTune(TuningGoal goal)
    {
        _ = goal; // the plan does not depend on the goal; the job picks the planner by it
        var classic = TuningPlanner.Plan(Chat, null, null, ChatGguf);
        var search = SearchPlanner.Plan(Chat, null, null, null, [], ChatGguf);

        Assert.Equal(VllmPlanSnapshot.Render(classic), VllmPlanSnapshot.Render(search));
    }

    [Fact]
    public void Selection_WithoutLlamaCppDimensions_RunsTheWholeTune_ElseOnlyTheNamedOnes()
    {
        foreach (var vllmSelection in new[] { SearchPlanner.Quick, SearchPlanner.Full, [TuningDimension.MaxNumSeqs] })
        {
            var plan = SearchPlanner.Plan(Chat, null, vllmSelection, null, [], ChatGguf);
            Assert.All(plan.Steps, s => Assert.Null(s.SkipReason));
        }

        var only = TuningPlanner.Plan(Chat, null, [TuningDimension.Parallel, TuningDimension.KvCacheTypes], ChatGguf);
        Assert.Null(only.Steps[0].SkipReason);
        Assert.Equal(LlamaCppTuning.NotSelected, only.Steps[1].SkipReason);
        Assert.Null(only.Steps[2].SkipReason);
        Assert.Equal(LlamaCppTuning.NotSelected, only.Steps[3].SkipReason);
        Assert.Equal(1 + 3 + 1, only.PreviewCandidates.Count);
        Assert.Equal([TuningDimension.Parallel, TuningDimension.KvCacheTypes], only.Search!.Dimensions);
    }

    // ------------------------------------------------------------ at most 12 candidates

    public static TheoryData<string> Picks => ["keep", "first", "last"];

    [Theory]
    [MemberData(nameof(Picks))]
    public void AtMostTwelveCandidates_ForEveryBaseline_EvenWhenTheBestMoves(string pickName)
    {
        Func<IReadOnlyList<TuningCandidatePlan>, TuningCandidatePlan?> pick = pickName switch
        {
            "first" => c => c.Count > 0 ? c[0] : null,
            "last" => c => c.Count > 0 ? c[^1] : null,
            _ => _ => null,
        };

        int largest = 0;
        foreach (var model in Baselines())
        {
            var gguf = model.LlamaCpp!.Embedding ? EmbeddingGguf : ChatGguf;
            foreach (var g in new[] { gguf, null })
            {
                var plan = TuningPlanner.Plan(model, null, null, g);
                Assert.InRange(plan.MaxCandidates, 1, LlamaCppTuning.MaxCandidates);
                Assert.InRange(plan.PreviewCandidates.Count, 1, LlamaCppTuning.MaxCandidates);
                var measured = Run(plan, pick);
                Assert.InRange(measured.Count, 1, LlamaCppTuning.MaxCandidates);
                largest = Math.Max(largest, measured.Count);
            }
        }

        Assert.Equal(LlamaCppTuning.MaxCandidates, largest); // the bound is reached, not just respected
    }

    [Fact]
    public void TheWorstCaseBaseline_IsCutToTwelve_WithANote()
    {
        // Parallel 3, flash attention auto, bf16 KV, b 1024 × ub 128: no value equals the baseline's (4 + 2 + 2 + 4 = 12 + 1).
        var odd = With(Chat, s => s with { Parallel = 3, CacheTypeK = "bf16", CacheTypeV = "bf16", BatchSize = 1024, MicroBatchSize = 128 });
        var plan = TuningPlanner.Plan(odd, null, null, ChatGguf);

        Assert.Equal(LlamaCppTuning.MaxCandidates, plan.MaxCandidates);
        Assert.Equal(LlamaCppTuning.MaxCandidates, plan.PreviewCandidates.Count);
        var batch = plan.Steps[3];
        Assert.Equal(3, batch.Values.Count);
        Assert.Contains("Left out to stay within 12 candidates (the baseline included): Batch 2048 · micro-batch 512.", batch.Note, StringComparison.Ordinal);
    }

    private static IEnumerable<ModelDefinition> Baselines()
    {
        foreach (int parallel in new[] { 1, 2, 3, 4, 8, 16 })
        {
            foreach (bool? fa in new bool?[] { null, true, false })
            {
                foreach (string? kv in new[] { null, "f16", "q8_0", "q4_0", "bf16" })
                {
                    foreach (var (b, ub) in new (int?, int?)[] { (null, null), (512, 256), (1024, 128), (4096, 1024) })
                    {
                        yield return With(Chat, s => s with { Parallel = parallel, FlashAttention = fa, CacheTypeK = kv, CacheTypeV = kv, BatchSize = b, MicroBatchSize = ub });
                    }

                    foreach (int? ub in new int?[] { null, 512, 1024, 4096 })
                    {
                        yield return With(Embedder, s => s with { Parallel = parallel, FlashAttention = fa, CacheTypeK = kv, CacheTypeV = kv, MicroBatchSize = ub });
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------ never varied, typed settings only

    [Fact]
    public void ContextLayersQuantAndSplit_AreNeverVaried_AndCandidatesWriteTypedSettingsOnly()
    {
        // Two GPUs with per-GPU budgets: the split must stay exactly as saved.
        var split = With(Chat with { Placement = new ModelPlacement("win-1", ["GPU-0", "GPU-1"]), MemoryBudgetGiB = 40 }, s => s with
        {
            GpuLayers = 30,
            ProjectorFile = "qwen3-8b/mmproj-F16.gguf",
            GpuBudgetsGiB = [24, 16],
            SplitMode = LlamaCppSplitMode.Layer,
            Threads = 8,
        });

        foreach (var baseline in new[] { Chat, split, Embedder })
        {
            var gguf = baseline.LlamaCpp!.Embedding ? EmbeddingGguf : ChatGguf;
            var plan = TuningPlanner.Plan(baseline, null, null, gguf);
            var candidates = plan.PreviewCandidates.Select(c => c.Settings).Concat(Run(plan, c => c.Count > 0 ? c[^1] : null)).ToList();
            Assert.True(candidates.Count > 5);
            var b = baseline.LlamaCpp!;
            foreach (var candidate in candidates)
            {
                var c = candidate.LlamaCpp!;
                Assert.Equal(b.ContextSize, c.ContextSize);
                Assert.Equal(b.GpuLayers, c.GpuLayers);
                Assert.Equal(b.ModelFile, c.ModelFile);
                Assert.Equal(b.ProjectorFile, c.ProjectorFile);
                Assert.Equal(b.SplitMode, c.SplitMode);
                Assert.Equal(b.GpuBudgetsGiB, c.GpuBudgetsGiB);
                Assert.Equal(b.ExecutableVersion, c.ExecutableVersion);
                Assert.Equal(b.Delivery, c.Delivery);
                Assert.Equal(b.Embedding, c.Embedding);
                Assert.Equal(b.Pooling, c.Pooling);
                Assert.Equal(b.Threads, c.Threads);
                Assert.Equal(b.ExtraArgs, c.ExtraArgs);
                Assert.Equal(baseline.Placement, candidate.Placement);
                Assert.Equal(baseline.MemoryBudgetGiB, candidate.MemoryBudgetGiB);
                Assert.Equal(baseline with { LlamaCpp = null }, candidate with { LlamaCpp = null });

                // Typed settings only: launchable, and every typed flag appears once in the argv (none came from ExtraArgs).
                Assert.Empty(LlamaCppArgsBuilder.Problems(candidate));
                int gpus = candidate.Placement!.GpuUuids!.Count;
                var argv = LlamaCppArgsBuilder.Build(candidate, gpus, gpus > 1 ? b.GpuBudgetsGiB : null);
                foreach (string flag in new[] { "-c", "-np", "-ngl", "-fa", "-ctk", "-ctv", "-b", "-ub", "--tensor-split" })
                {
                    Assert.True(argv.Count(a => a == flag) <= 1, flag);
                }

                Assert.Equal(
                    LlamaCppArgsBuilder.Build(baseline, gpus, gpus > 1 ? b.GpuBudgetsGiB : null).SkipWhile(a => a != "--tensor-split").Take(2),
                    argv.SkipWhile(a => a != "--tensor-split").Take(2));
            }
        }
    }

    [Fact]
    public void Fingerprints_TellCandidatesApart_AndACandidateEqualsItsAppliedDefinition()
    {
        var plan = TuningPlanner.Plan(Chat, null, null, ChatGguf);
        var prints = plan.PreviewCandidates.Select(c => TuningPlanner.Fingerprint(c.Settings)).ToList();
        Assert.Equal(prints.Count, prints.Distinct(StringComparer.Ordinal).Count());

        // The vLLM config hash ignores llama.cpp settings: it would have called every candidate the same.
        Assert.Single(plan.PreviewCandidates.Select(c => ModelConfigHash.Compute(c.Settings, "", 100)).Distinct(StringComparer.Ordinal));

        foreach (var candidate in plan.PreviewCandidates)
        {
            var applied = ModelJobService.MergeTuned(Chat, candidate.Settings);
            Assert.Equal(TuningPlanner.Fingerprint(candidate.Settings), TuningPlanner.Fingerprint(applied));
            Assert.Equal(candidate.Settings, applied);
        }
    }

    [Fact]
    public void MergeTuned_WritesOnlyTheTunedLlamaCppSettings()
    {
        var candidate = With(Chat, s => s with
        {
            Parallel = 8, FlashAttention = true, CacheTypeK = "q8_0", CacheTypeV = "q8_0", BatchSize = 512, MicroBatchSize = 256,
            ContextSize = 1, GpuLayers = 1, ModelFile = "other/other.gguf", ExtraArgs = ["--other"],
        }) with { MemoryBudgetGiB = 1, MaxNumSeqs = 99, ServedName = "other" };
        var saved = Chat with { AutoStart = true };

        var merged = ModelJobService.MergeTuned(saved, candidate);

        Assert.Equal(saved with
        {
            LlamaCpp = saved.LlamaCpp! with { Parallel = 8, FlashAttention = true, CacheTypeK = "q8_0", CacheTypeV = "q8_0", BatchSize = 512, MicroBatchSize = 256 },
        }, merged);
    }

    // ------------------------------------------------------------ embeddings (D118)

    [Fact]
    public void CausalEmbedding_TriesUbEqualsB_UpToTheContext_WritingMicroBatchSize()
    {
        var plan = TuningPlanner.Plan(Embedder, null, null, EmbeddingGguf);
        var batch = plan.Steps.Single(s => s.Dimension == TuningDimension.BatchSizes);
        Assert.Null(batch.SkipReason);
        Assert.Equal([1024, 4096], batch.Values.Select(v => v.Number!.Value)); // 2048 = the causal default the builder emits

        var candidates = plan.PreviewCandidates.Where(c => c.Step == batch.Title).ToList();
        Assert.Equal([1024, 4096], candidates.Select(c => c.Settings.LlamaCpp!.MicroBatchSize!.Value));
        Assert.All(candidates, c => Assert.Null(c.Settings.LlamaCpp!.BatchSize));
        Assert.All(candidates, c =>
        {
            var argv = LlamaCppArgsBuilder.Build(c.Settings, 1);
            string ub = c.Settings.LlamaCpp!.MicroBatchSize!.Value.ToString(CultureInfo.InvariantCulture);
            Assert.Equal(ub, argv[argv.ToList().IndexOf("-b") + 1]);
            Assert.Equal(ub, argv[argv.ToList().IndexOf("-ub") + 1]);
        });

        // A short context bounds the values: 2048 tokens leave only 1024 (and 2048, the baseline's).
        var shortContext = TuningPlanner.Plan(With(Embedder, s => s with { ContextSize = 2048 }), null, null, EmbeddingGguf);
        Assert.Equal([1024], shortContext.Steps[3].Values.Select(v => v.Number!.Value));
        var tiny = TuningPlanner.Plan(With(Embedder, s => s with { ContextSize = 512 }), null, null, EmbeddingGguf);
        Assert.StartsWith("Skipped: every micro-batch tried", tiny.Steps[3].SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void NonCausalEmbedding_KeepsUbEqualsContext_AndAnUnreadGguf_DoesNotVaryIt()
    {
        var bert = With(Embedder, s => s with { MicroBatchSize = 8192 });
        var nonCausal = TuningPlanner.Plan(bert, null, null, EmbeddingGguf with { CausalAttention = false });
        Assert.StartsWith("Not varied: a non-causal embedding model keeps micro-batch = batch = context size (8,192)", nonCausal.Steps[3].SkipReason, StringComparison.Ordinal);
        Assert.All(nonCausal.PreviewCandidates, c => Assert.Equal(8192, c.Settings.LlamaCpp!.MicroBatchSize));

        var unknown = TuningPlanner.Plan(Embedder, null, null, gguf: null);
        Assert.StartsWith("Skipped: the GGUF header could not be read", unknown.Steps[3].SkipReason, StringComparison.Ordinal);
        Assert.StartsWith("Memory estimates not checked", unknown.Steps[0].Note, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ fit: skipped with numbers

    [Fact]
    public void ACandidateThatDoesNotFit_IsSkippedAtPlanning_WithItsNumbers()
    {
        // The budget holds Parallel 4 but not 8 (the KV cache doubles with the slots).
        double atFour = Estimate(Chat, ChatGguf);
        double atEight = Estimate(With(Chat, s => s with { Parallel = 8 }), ChatGguf);
        Assert.True(atEight > atFour + 1);
        var tight = Chat with { MemoryBudgetGiB = Math.Round(atFour + 0.5, 1) };

        var plan = TuningPlanner.Plan(tight, null, null, ChatGguf);

        var parallel = plan.Steps[0];
        Assert.Equal([1, 2], parallel.Values.Select(v => v.Number!.Value));
        var fit = LlamaCppTuning.Fit(With(tight, s => s with { Parallel = 8 }), ChatGguf);
        Assert.False(fit.Fits);
        Assert.Equal(atEight, fit.Estimate!.BudgetGiB);
        string expected = string.Create(CultureInfo.InvariantCulture,
            $"Parallel 8 skipped: needs {atEight:0.00} GiB (weights {fit.Estimate.WeightsGiB:0.00} + KV {fit.Estimate.KvGiB:0.00} + compute {fit.Estimate.ComputeGiB:0.00} GiB, × 1.10, + CUDA 0.45 GiB), more than the {tight.MemoryBudgetGiB:0.##} GiB budget");
        Assert.Contains(expected, parallel.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.PreviewCandidates, c => c.Settings.LlamaCpp!.Parallel == 8);
    }

    [Fact]
    public void ACandidateThatAnEarlierWinnerMakesTooBig_IsSkippedDuringTheRun_WithItsNumbers()
    {
        // Baseline: 4 slots, q8_0 KV. The budget holds 8 slots at q8_0 and 4 slots at f16, but not 8 slots at f16.
        var baseline = With(Chat, s => s with { CacheTypeK = "q8_0", CacheTypeV = "q8_0" });
        double eightQ8 = Estimate(With(baseline, s => s with { Parallel = 8 }), ChatGguf);
        double fourF16 = Estimate(With(baseline, s => s with { CacheTypeK = "f16", CacheTypeV = "f16" }), ChatGguf);
        double eightF16 = Estimate(With(baseline, s => s with { Parallel = 8, CacheTypeK = "f16", CacheTypeV = "f16" }), ChatGguf);
        double budget = Math.Ceiling((Math.Max(eightQ8, fourF16) + 0.01) * 100) / 100;
        Assert.True(eightF16 > budget);
        baseline = baseline with { MemoryBudgetGiB = budget };

        var plan = TuningPlanner.Plan(baseline, null, null, ChatGguf);
        Assert.Equal(["f16"], plan.Steps[2].Values.Select(v => v.Text)); // fits on the baseline: kept at planning
        Assert.Null(plan.Steps[2].Note);

        var skipped = new List<string>();
        var measured = Run(plan, c => c.FirstOrDefault(x => x.Settings.LlamaCpp!.Parallel == 8), skipped);

        Assert.Contains(measured, m => m.LlamaCpp!.Parallel == 8);
        Assert.DoesNotContain(measured, m => m.LlamaCpp!.Parallel == 8 && m.LlamaCpp.CacheTypeK == "f16");
        string note = Assert.Single(skipped);
        Assert.StartsWith(string.Create(CultureInfo.InvariantCulture, $"KV cache f16 skipped: needs {eightF16:0.00} GiB (weights "), note, StringComparison.Ordinal);
        Assert.EndsWith(string.Create(CultureInfo.InvariantCulture, $"more than the {budget:0.##} GiB budget"), note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEstimate_CountsEveryPlacedGpu_AndAnUnreadableSummaryIsNotChecked()
    {
        var two = With(Chat with { Placement = new ModelPlacement("win-1", ["GPU-0", "GPU-1"]) }, s => s with { GpuBudgetsGiB = [24, 16] });
        var fit = LlamaCppTuning.Fit(two, ChatGguf);
        Assert.True(fit.Fits);
        Assert.Equal(2 * LlamaCppMemoryEstimate.CudaContextGiBPerGpu, fit.Estimate!.CudaContextGiB);
        Assert.Equal(2, fit.Estimate.PerGpuGiB.Count);

        var noHeads = LlamaCppTuning.Fit(Chat, ChatGguf with { HeadCount = null });
        Assert.Null(noHeads.Fits);
        Assert.StartsWith("not checked: ", noHeads.Text, StringComparison.Ordinal);
        Assert.Null(LlamaCppTuning.Fit(Chat, null).Fits);
    }

    [Fact]
    public void AQuantizedVCache_WithFlashAttentionOff_IsSkipped()
    {
        // Baseline flash attention off: q8_0 KV would not start.
        var off = With(Chat, s => s with { FlashAttention = false });
        var plan = TuningPlanner.Plan(off, null, null, ChatGguf);
        Assert.Empty(plan.Steps[2].Values);
        Assert.Contains("KV cache q8_0 skipped: a quantized V cache (q8_0) needs flash attention, which is off", plan.Steps[2].Note, StringComparison.Ordinal);

        // Baseline q8_0: "flash attention off" is skipped, "on" is kept.
        var q8 = With(Chat, s => s with { CacheTypeK = "q8_0", CacheTypeV = "q8_0" });
        var q8Plan = TuningPlanner.Plan(q8, null, null, ChatGguf);
        Assert.Equal([true], q8Plan.Steps[1].Values.Select(v => v.Flag!.Value));
    }

    // ------------------------------------------------------------ through the job: notes, target, apply

    private (ModelJobService Jobs, FakeCluster Cluster, FakeSuite Suite, FakeModelRegistry Registry, BenchmarkStore Store) Jobs(
        ModelDefinition model, GgufSummary? gguf)
    {
        var registry = new FakeModelRegistry().Add(model);
        var cluster = new FakeCluster().Running(model);
        var suite = new FakeSuite(cluster);
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, UseContainerNetwork = true, VllmImage = "default-image" });
        var store = new BenchmarkStore(options);
        var jobs = new ModelJobService(
            registry, cluster, cluster, suite, store, new MaintenanceModeService(options), new FakeMetricsHistory(), new FakeGpu(2), new StartupTimeTracker(), options,
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
            inspectGguf: (node, m) =>
            {
                Assert.Equal("win-1", node);
                Assert.Equal(model.Id, m.Id);
                return gguf;
            });
        return (jobs, cluster, suite, registry, store);
    }

    [Fact]
    public async Task ATuningRun_MeasuresTheSmallTune_NotesSkips_AndAppliesTheWinnerIntoLlamaCppSettings()
    {
        var ct = TestContext.Current.CancellationToken;

        // As above: 8 slots fit at q8_0 but not at f16.
        var baseline = With(Chat, s => s with { CacheTypeK = "q8_0", CacheTypeV = "q8_0" });
        double eightQ8 = Estimate(With(baseline, s => s with { Parallel = 8 }), ChatGguf);
        double fourF16 = Estimate(With(baseline, s => s with { CacheTypeK = "f16", CacheTypeV = "f16" }), ChatGguf);
        baseline = baseline with { MemoryBudgetGiB = Math.Ceiling((Math.Max(eightQ8, fourF16) + 0.01) * 100) / 100 };
        var (jobs, cluster, suite, registry, store) = Jobs(baseline, ChatGguf);

        // More slots are faster at N=16; everything else is neutral, so Parallel 8 wins.
        suite.ScoreTarget = (definition, target) =>
        {
            Assert.Equal(RuntimeKind.LlamaCpp, target.Runtime);
            Assert.Equal(definition.LlamaCpp!.Parallel, target.MaxNumSeqs);
            Assert.Equal(definition.LlamaCpp.ContextSize, target.MaxModelLen);
            return Results.Make(10, 100 + (10 * definition.LlamaCpp.Parallel));
        };

        var preview = await jobs.PreviewTuningAsync(baseline.Id, null, TuningGoal.Balanced, ct);
        Assert.NotNull(preview.Plan.Search!.LlamaCpp);
        await jobs.StartTuningAsync(baseline.Id, TuningGoal.Balanced, null, ct);
        await jobs.WhenIdleAsync();

        var run = Assert.Single(await store.ListTuningAsync(baseline.Id, ct));
        Assert.Equal(TuningRunStatus.Completed, run.Status);
        Assert.Equal(TuningGoal.InteractiveChat, run.Goal); // a classic goal maps to the matching preset (MN-230, D130)
        Assert.DoesNotContain(run.Candidates, c => c.Reason is not null && c.Reason.Contains("No N=16", StringComparison.Ordinal));
        // The search stage measures each configuration once; the preset's confirmation stage re-measures the top two
        // and the baseline, so those repeat a fingerprint on purpose (MN-230: llama.cpp always runs on a preset).
        var searched = run.Candidates.Where(c => c.Step != ModelJobService.ConfirmationStep).ToList();
        Assert.Equal(searched.Count, searched.Select(c => TuningPlanner.Fingerprint(c.Settings)).Distinct(StringComparer.Ordinal).Count());
        Assert.All(cluster.Launches, l => Assert.Equal(RuntimeKind.LlamaCpp, l.Definition.Runtime));
        Assert.Contains(run.Notes, n => n.StartsWith("KV cache f16 skipped: needs ", StringComparison.Ordinal) && n.EndsWith("GiB budget", StringComparison.Ordinal));

        var winner = run.Candidates.Single(c => c.Index == run.WinnerIndex);
        Assert.Equal(8, winner.Settings.LlamaCpp!.Parallel);

        var applied = await jobs.ApplyTuningCandidateAsync(baseline.Id, run.Id, winner.Index, ct);
        Assert.Equal(LlamaCppTuning.MergeTuned(baseline, winner.Settings), applied); // only the tuned llama.cpp fields
        Assert.Equal(8, applied.LlamaCpp!.Parallel);
        Assert.Equal(baseline.LlamaCpp!.ContextSize, applied.LlamaCpp.ContextSize);
        Assert.Equal(applied, await registry.GetAsync(baseline.Id, ct));
        Assert.Equal(TuningPlanner.Fingerprint(winner.Settings), TuningPlanner.Fingerprint(applied));
    }
}


/// <summary>
/// A deterministic snapshot of the vLLM tuning plans (MN-225 acceptance: "vLLM tuning plans for every existing fixture
/// unchanged"). Every vLLM fixture (the live-shaped <c>models.live-v1.json</c>, the in-code live definitions and the
/// search-planner fixtures) × folder inspections × dimension selections × both planners is rendered to text (steps,
/// values, notes, preview candidates with their fingerprints and settings, counts, search context) and hashed. The
/// golden file was recorded from the code before MN-225 (<c>caeedfa</c>); <see cref="Record"/> rewrites it.
/// </summary>
internal static class VllmPlanSnapshot
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>The golden file next to this source file.</summary>
    public static string GoldenPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "vllm-tuning-plans.golden.txt");

    private static string LiveFixture([CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "Multinode", "Fixtures", "models.live-v1.json"));

    /// <summary>Every vLLM fixture definition, by a stable key.</summary>
    public static IReadOnlyList<(string Key, ModelDefinition Model)> Models()
    {
        var models = new List<(string, ModelDefinition)>();
        foreach (var m in JsonSerializer.Deserialize<List<ModelDefinition>>(LiveFixture())!)
        {
            models.Add(("live-v1:" + m.Id, m));
        }

        models.Add(("live:qwen35b", LiveDefinitions.Qwen35B));
        models.Add(("live:nemotron", LiveDefinitions.Nemotron));
        models.Add(("live:embedding", LiveDefinitions.Embedding));
        models.Add(("search:qwen27b", SearchPlannerTests.Qwen27B));
        models.Add(("search:qwen27b-draft", SearchPlannerTests.Qwen27B with
        {
            Speculative = new SpeculativeDecoding("", 7) { DraftModel = "/models/Qwen3.8-27B-DSpark" },
        }));
        models.Add(("search:no-chunked", SearchPlannerTests.Qwen27B with { ExtraArgs = ["--no-enable-chunked-prefill"], MaxNumBatchedTokens = 3000 }));
        return models;
    }

    private static IReadOnlyList<(string Key, ModelFolderInspection? Inspection)> Inspections() =>
    [
        ("none", null),
        ("moe-mtp", SearchPlannerTests.Qwen35BInspection),
        ("dense-mtp", SearchPlannerTests.Qwen27BInspection),
        ("hybrid", SearchPlannerTests.NemotronInspection),
        ("tight-kv", SearchPlannerTests.Inspection("Qwen3_5ForConditionalGeneration", weightsGiB: 60, mtp: false, moe: false)),
    ];

    private static List<(string Key, IReadOnlyCollection<TuningDimension>? Dimensions)> Selections()
    {
        var all = new List<(string, IReadOnlyCollection<TuningDimension>?)>
        {
            ("null", null),
            ("empty", []),
            ("quick", SearchPlanner.Quick),
            ("full", SearchPlanner.Full),
        };

        // The eight vLLM dimensions, one at a time (fixed list: the values present before MN-225).
        foreach (var d in new[]
        {
            TuningDimension.SpeculativeDecoding, TuningDimension.MaxNumSeqs, TuningDimension.MaxNumBatchedTokens, TuningDimension.KvCacheDtype,
            TuningDimension.PrefixCaching, TuningDimension.CudaGraphMode, TuningDimension.AsyncScheduling, TuningDimension.Refinement,
        })
        {
            all.Add((d.ToString(), [d]));
        }

        return all;
    }

    /// <summary>Key → SHA-256 of the rendered plan, for every fixture × inspection × selection × planner.</summary>
    public static SortedDictionary<string, string> Compute()
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Func<string, ModelFolderInspection?> draft = folder => folder.EndsWith("DSpark", StringComparison.Ordinal)
            ? SearchPlannerTests.Inspection("DFlashDraftModel", weightsGiB: 2, mtp: false, moe: false, blockSize: 8)
            : null;
        foreach (var (modelKey, model) in Models())
        {
            foreach (var (inspectionKey, inspection) in Inspections())
            {
                foreach (var (selectionKey, dims) in Selections())
                {
                    string prefix = $"{modelKey}|{inspectionKey}|{selectionKey}";
                    result[prefix + "|classic"] = Hash(Render(TuningPlanner.Plan(model, inspection, dims)));
                    result[prefix + "|search"] = Hash(Render(SearchPlanner.Plan(model, inspection, dims, draft, [])));
                    result[prefix + "|search-registry"] = Hash(Render(SearchPlanner.Plan(model, inspection, dims, draft, ["Qwen3_5MoeMTP", "Qwen3_5MTP"])));
                }
            }

            result[modelKey + "|fingerprint"] = TuningPlanner.Fingerprint(model);
        }

        return result;
    }

    public static string Format(SortedDictionary<string, string> snapshot) =>
        string.Concat(snapshot.Select(kv => kv.Key + " " + kv.Value + "\n"));

    public static SortedDictionary<string, string> Parse(string text)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int space = line.LastIndexOf(' ');
            result[line[..space]] = line[(space + 1)..].TrimEnd('\r');
        }

        return result;
    }

    /// <summary>Rewrites the golden file from the current code (run once, before the change it guards).</summary>
    public static void Record() => File.WriteAllText(GoldenPath(), Format(Compute()));

    public static string Render(TuningPlan plan)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"max={plan.MaxCandidates} expected={plan.ExpectedCandidates} estimated={plan.EstimatedCandidates}\n");
        foreach (var step in plan.Steps)
        {
            text.Append(CultureInfo.InvariantCulture,
                $"step {step.Dimension}|{step.Title}|{step.SkipReason}|{step.Note}|{step.Phase}|{step.Description}|{step.Key}\n");
            foreach (var v in step.Values)
            {
                text.Append(CultureInfo.InvariantCulture, $"  value {v.Label}|{v.Number}|{v.Text}|{v.Flag}|{v.Graph}|{v.Method}\n");
            }
        }

        foreach (var c in plan.PreviewCandidates)
        {
            text.Append(CultureInfo.InvariantCulture, $"candidate {c.Step}|{c.Label}|{c.Description}|{TuningPlanner.Fingerprint(c.Settings)}\n");
            text.Append("  ").Append(JsonSerializer.Serialize(c.Settings, Json)).Append('\n');
        }

        if (plan.Search is { } search)
        {
            text.Append("search ").Append(string.Join(",", search.Dimensions)).Append('\n');
            text.Append("  gates ").Append(JsonSerializer.Serialize(search.Gates, Json)).Append('\n');
            text.Append("  baseline ").Append(JsonSerializer.Serialize(search.Baseline, Json)).Append('\n');
        }

        return text.ToString();
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
