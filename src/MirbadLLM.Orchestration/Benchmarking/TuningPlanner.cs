using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// Deterministic coordinate-descent search plan. Starting from the saved settings (the baseline), each step
/// varies one setting in a fixed order while keeping the best result so far: (1) speculative decoding,
/// (2) max num seqs, (3) max num batched tokens, (4) KV cache dtype. Max model len and the memory budget are
/// the user's constraints and never change. Candidates identical to one already measured are skipped.
/// The baseline is the normalized saved definition (<see cref="VllmArgsNormalizer"/>), so no raw flag in ExtraArgs can
/// override what a candidate sets. Pooling (embedding) models only vary max num seqs and max batched tokens.
/// A llama.cpp model gets the small tune of <see cref="LlamaCppTuning"/> instead (design-p3p4 §A.6, MN-225); vLLM plans are
/// unchanged.
/// </summary>
public static class TuningPlanner
{
    public static readonly IReadOnlyList<int> MtpTokenCounts = [1, 2, 3];
    public static readonly IReadOnlyList<int> MaxNumSeqsValues = [8, 16, 32];
    public static readonly IReadOnlyList<int> MaxNumBatchedTokensValues = [2048, 8192, 16384];

    /// <summary>Pooling models: concurrency values (vLLM's DGX Spark guidance spans 4–64).</summary>
    public static readonly IReadOnlyList<int> PoolingMaxNumSeqsValues = [4, 8, 16, 32, 64];

    /// <summary>Pooling models: per-step token budgets.</summary>
    public static readonly IReadOnlyList<int> PoolingMaxNumBatchedTokensValues = [2048, 4096, 8192, 16384, 32768];

    public const string PoolingNotApplicable = "Not applicable: pooling (embedding) models are tuned on max num seqs and max batched tokens only.";

    public static string StepTitle(TuningDimension dimension) => dimension switch
    {
        TuningDimension.SpeculativeDecoding => "Speculative decoding",
        TuningDimension.MaxNumSeqs => "Max num seqs",
        TuningDimension.MaxNumBatchedTokens => "Max batched tokens",
        TuningDimension.KvCacheDtype => "KV cache dtype",
        _ when LlamaCppTuning.IsLlamaCppDimension(dimension) => LlamaCppTuning.StepTitle(dimension),
        _ => dimension.ToString(),
    };

    /// <param name="baseline">The saved definition.</param>
    /// <param name="inspection">The checkpoint inspection (MTP layers, KV size); null when the folder can't be read.</param>
    /// <param name="dimensions">Steps to include; null = all four.</param>
    /// <param name="gguf">A llama.cpp model's GGUF summary (its candidates' memory checks); ignored for vLLM.</param>
    public static TuningPlan Plan(
        ModelDefinition baseline, ModelFolderInspection? inspection, IReadOnlyCollection<TuningDimension>? dimensions = null, GgufSummary? gguf = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (LlamaCppTuning.Applies(baseline))
        {
            return LlamaCppTuning.Plan(baseline, gguf, dimensions);
        }

        baseline = VllmArgsNormalizer.Apply(baseline);
        bool Included(TuningDimension d) => dimensions is null || dimensions.Count == 0 || dimensions.Contains(d);

        var steps = PoolingDetection.Detect(baseline) == PoolingMode.None
            ? GenerationSteps(baseline, inspection, Included)
            : PoolingSteps(Included);

        // Preview: every step applied to the baseline, duplicates removed.
        var preview = new List<TuningCandidatePlan> { new("Baseline", "Baseline (saved settings)", baseline) };
        var seen = new HashSet<string>(StringComparer.Ordinal) { Fingerprint(baseline) };
        int max = 1;
        foreach (var step in steps.Where(s => s.SkipReason is null))
        {
            max += step.Values.Count;
            foreach (var value in step.Values)
            {
                var candidate = Apply(step.Dimension, value, baseline);
                if (seen.Add(Fingerprint(candidate)))
                {
                    preview.Add(new TuningCandidatePlan(step.Title, CandidateLabel(step.Dimension, value), candidate));
                }
            }
        }

        return new TuningPlan(steps, preview, max);
    }

    /// <summary>Pooling models decode nothing: only the concurrency and the token budget are varied.</summary>
    private static List<TuningStepPlan> PoolingSteps(Func<TuningDimension, bool> included) =>
    [
        Step(TuningDimension.SpeculativeDecoding, [], PoolingNotApplicable),
        Step(TuningDimension.MaxNumSeqs,
            PoolingMaxNumSeqsValues.Select(n => new TuningValue(n.ToString(CultureInfo.InvariantCulture), Number: n)).ToList(),
            included(TuningDimension.MaxNumSeqs) ? null : "Not selected."),
        Step(TuningDimension.MaxNumBatchedTokens,
            PoolingMaxNumBatchedTokensValues.Select(n => new TuningValue(n.ToString("N0", CultureInfo.InvariantCulture), Number: n)).ToList(),
            included(TuningDimension.MaxNumBatchedTokens) ? null : "Not selected.",
            "A budget below max model len only starts when the model supports chunked prefill; otherwise that candidate fails and is skipped."),
        Step(TuningDimension.KvCacheDtype, [], PoolingNotApplicable),
    ];

    private static List<TuningStepPlan> GenerationSteps(ModelDefinition baseline, ModelFolderInspection? inspection, Func<TuningDimension, bool> included)
    {
        var steps = new List<TuningStepPlan>(4);

        // (1) Speculative decoding.
        var spec = new List<TuningValue> { new("Off", Number: 0) };
        string? specNote = null;
        if (inspection is { MaySupportMtp: true })
        {
            spec.AddRange(MtpTokenCounts.Select(n => new TuningValue(string.Create(CultureInfo.InvariantCulture, $"MTP × {n}"), Number: n)));
        }
        else
        {
            specNote = inspection is null
                ? "MTP skipped: the checkpoint's config.json could not be read."
                : "MTP skipped: the checkpoint has no MTP layers (mtp_num_hidden_layers is 0).";
        }

        steps.Add(Step(TuningDimension.SpeculativeDecoding, spec, included(TuningDimension.SpeculativeDecoding) ? null : "Not selected.", specNote));

        // (2) Max num seqs.
        steps.Add(Step(TuningDimension.MaxNumSeqs,
            MaxNumSeqsValues.Select(n => new TuningValue(n.ToString(CultureInfo.InvariantCulture), Number: n)).ToList(),
            included(TuningDimension.MaxNumSeqs) ? null : "Not selected."));

        // (3) Chunked-prefill token budget.
        steps.Add(Step(TuningDimension.MaxNumBatchedTokens,
            MaxNumBatchedTokensValues.Select(n => new TuningValue(n.ToString("N0", CultureInfo.InvariantCulture), Number: n)).ToList(),
            included(TuningDimension.MaxNumBatchedTokens) ? null : "Not selected."));

        // (4) KV cache dtype: auto (BF16) doubles KV memory, so only when the budget still admits one full-length request.
        var kv = new List<TuningValue> { new("FP8", Text: "fp8"), new("Auto", Text: "auto") };
        string? kvSkip = !included(TuningDimension.KvCacheDtype) ? "Not selected." : KvAutoSkipReason(baseline, inspection);
        steps.Add(Step(TuningDimension.KvCacheDtype, kv, kvSkip));
        return steps;
    }

    /// <summary>The definitions one step tries, built on <paramref name="best"/>.</summary>
    public static IReadOnlyList<TuningCandidatePlan> CandidatesFor(TuningStepPlan step, ModelDefinition best)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(best);
        if (LlamaCppTuning.Applies(best))
        {
            // Without the plan's context there is no GGUF summary, so no memory check (SearchPlanner.CandidatesFor has it).
            return LlamaCppTuning.CandidatesFor(step, best, new LlamaCppTuningContext(null));
        }

        return step.SkipReason is not null
            ? []
            : step.Values.Select(v => new TuningCandidatePlan(step.Title, CandidateLabel(step.Dimension, v), Apply(step.Dimension, v, best))).ToList();
    }

    public static string CandidateLabel(TuningDimension dimension, TuningValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return dimension switch
        {
            TuningDimension.SpeculativeDecoding => value.Number is 0 ? "Speculative off" : value.Label,
            TuningDimension.MaxNumSeqs => $"Max num seqs {value.Label}",
            TuningDimension.MaxNumBatchedTokens => $"Batched tokens {value.Label}",
            TuningDimension.KvCacheDtype => $"KV cache {value.Label}",
            _ when LlamaCppTuning.IsLlamaCppDimension(dimension) => LlamaCppTuning.CandidateLabel(dimension, value),
            _ => value.Label,
        };
    }

    /// <summary>
    /// Applies one value to <paramref name="best"/>. An MTP depth keeps the other keys of an existing MTP config (e.g.
    /// <c>moe_backend</c>) and leaves the CUDA graph mode alone (the old forced PIECEWISE is gone: a graph mode that
    /// crashes just fails that candidate, and the workload-preset search tests graph modes explicitly).
    /// </summary>
    public static ModelDefinition Apply(TuningDimension dimension, TuningValue value, ModelDefinition best)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(best);
        if (LlamaCppTuning.IsLlamaCppDimension(dimension))
        {
            return LlamaCppTuning.Apply(dimension, value, best);
        }

        return dimension switch
        {
            TuningDimension.SpeculativeDecoding when value.Number is > 0 => best with
            {
                Speculative = best.Speculative is { Method: "mtp" } mtp
                    ? mtp with { NumSpeculativeTokens = value.Number.Value }
                    : new SpeculativeDecoding("mtp", value.Number.Value),
            },
            TuningDimension.SpeculativeDecoding => best with { Speculative = null },
            TuningDimension.MaxNumSeqs => best with { MaxNumSeqs = value.Number ?? best.MaxNumSeqs },
            TuningDimension.MaxNumBatchedTokens => best with { MaxNumBatchedTokens = value.Number },
            TuningDimension.KvCacheDtype => best with { KvCacheDtype = value.Text ?? best.KvCacheDtype },
            _ => best,
        };
    }

    /// <summary>
    /// Identity of a launch configuration for de-duplication: the vLLM arguments and image (the same inputs as
    /// the config hash, independent of the host's memory size); for a llama.cpp model its <c>llama-server</c> argv
    /// (<see cref="LlamaCppTuning.Fingerprint"/>: the vLLM arguments ignore its settings, so every candidate looked alike).
    /// </summary>
    public static string Fingerprint(ModelDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return LlamaCppTuning.Applies(definition) ? LlamaCppTuning.Fingerprint(definition) : ModelConfigHash.Compute(definition, "", 100);
    }

    /// <summary>Memory one full-length request needs with an unquantized (auto, BF16) KV cache.</summary>
    public static double MinimumBudgetAtAutoGiB(ModelFolderInspection inspection, int maxModelLen)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        // The inspector's KV estimate is at fp8 (1 byte per element); auto keeps the model's 2-byte dtype.
        return inspection.WeightsGiB + ModelFolderInspector.RuntimeOverheadGiB + (2 * inspection.KvGiB(maxModelLen));
    }

    /// <summary>Why an auto (BF16) KV cache is not tried: unknown KV size, or one full-length request at auto exceeds the budget. Null = try it.</summary>
    public static string? KvAutoSkipReason(ModelDefinition baseline, ModelFolderInspection? inspection)
    {
        if (inspection is null || inspection.KvBytesPerToken <= 0)
        {
            return "Skipped: the checkpoint's KV cache size could not be estimated.";
        }

        double needed = MinimumBudgetAtAutoGiB(inspection, baseline.MaxModelLen);
        return baseline.MemoryBudgetGiB >= needed
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"Skipped: an auto (BF16) KV cache needs about {Math.Ceiling(needed):0} GiB for one {baseline.MaxModelLen:N0}-token request; the budget is {baseline.MemoryBudgetGiB:0.#} GiB.");
    }

    private static TuningStepPlan Step(TuningDimension dimension, IReadOnlyList<TuningValue> values, string? skipReason, string? note = null) =>
        new(dimension, StepTitle(dimension), values, skipReason, note);
}
