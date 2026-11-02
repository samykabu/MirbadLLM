using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// The interaction-aware search of the workload presets (plan section B). Coordinate descent over phases, each step
/// trying its values on top of the best result so far, with gates (<see cref="TuningGates"/>) deciding what is tried:
/// <list type="number">
/// <item>Memory &amp; cache: KV {fp8, auto (memory-gated)} × prefix caching {on, off} — both prefix configurations are
/// always tried; Mamba hybrids pair on with <c>--mamba-cache-mode align</c> and off with <c>none</c>.</item>
/// <item>Concurrency: max num seqs {4, 8, 16, 32, 64} (+{1, 2} for dense models over 20 B), then max batched tokens
/// {2048 … 32768} plus the saved value.</item>
/// <item>Decode acceleration: speculative {off, saved, MTP 1/2/3/5 or the drafter's depths} with FULL_AND_PIECEWISE
/// graphs; PIECEWISE follow-ups only for a full variant that failed and for the top two depths; then async scheduling
/// {on, off}.</item>
/// <item>Refinement: the neighbours of the winning max num seqs and speculative depth with everything else fixed, then
/// prefix caching on/off with the winning decode settings.</item>
/// </list>
/// Pooling models keep the classic pooling plan (seqs and batched tokens only). Quick = max num seqs + speculative
/// (with FULL_AND_PIECEWISE); Full = every step. Duplicates of a measured effective configuration are skipped.
/// A llama.cpp model gets the small tune of <see cref="LlamaCppTuning"/> for every goal (design-p3p4 §A.6, MN-225): its plan
/// carries <see cref="SearchContext.LlamaCpp"/>, so <see cref="CandidatesFor"/> builds its candidates and there are no
/// follow-ups or refinement. vLLM plans are unchanged.
/// </summary>
public static class SearchPlanner
{
    public const string FullAndPiecewise = "FULL_AND_PIECEWISE";
    public const string Piecewise = "PIECEWISE";
    public const string RefineKey = "refine";
    public const string PrefixCheckKey = "prefix-check";

    /// <summary>Gates + phase 2 max num seqs + phase 3 speculative × FULL_AND_PIECEWISE.</summary>
    public static readonly IReadOnlyList<TuningDimension> Quick = [TuningDimension.MaxNumSeqs, TuningDimension.SpeculativeDecoding];

    /// <summary>
    /// Every phase, follow-ups and refinement: the vLLM dimensions. The llama.cpp ones (MN-225) are left out, so this list
    /// and every vLLM plan stay as they were; for a llama.cpp model a selection without llama.cpp dimensions means all of
    /// them (<see cref="LlamaCppTuning.Included"/>).
    /// </summary>
    public static readonly IReadOnlyList<TuningDimension> Full = [.. Enum.GetValues<TuningDimension>().Where(d => !LlamaCppTuning.IsLlamaCppDimension(d))];

    /// <param name="gguf">A llama.cpp model's GGUF summary (its candidates' memory checks); ignored for vLLM.</param>
    public static TuningPlan Plan(
        ModelDefinition baseline, ModelFolderInspection? inspection, IReadOnlyCollection<TuningDimension>? dimensions,
        Func<string, ModelFolderInspection?>? inspectFolder, IReadOnlyCollection<string> mtpArchitectures, GgufSummary? gguf = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (LlamaCppTuning.Applies(baseline))
        {
            return LlamaCppTuning.Plan(baseline, gguf, dimensions);
        }

        baseline = VllmArgsNormalizer.Apply(baseline);
        if (PoolingDetection.Detect(baseline) != PoolingMode.None)
        {
            // Pooling models keep the phase-A plan: max num seqs and max batched tokens only.
            return TuningPlanner.Plan(baseline, inspection, dimensions);
        }

        var dims = dimensions is null || dimensions.Count == 0 ? Full : dimensions;
        var gates = TuningGates.Compute(baseline, inspection, inspectFolder, mtpArchitectures);
        var context = new SearchContext(gates, baseline, dims.ToList());
        bool Included(TuningDimension d) => dims.Contains(d);
        const string NotSelected = "Not selected.";
        var steps = new List<TuningStepPlan>();

        // Phase 1: memory & cache.
        bool kv = Included(TuningDimension.KvCacheDtype);
        bool prefix = Included(TuningDimension.PrefixCaching);
        IReadOnlyList<string?> kvValues = kv ? (gates.KvAutoSkipReason is null ? ["fp8", "auto"] : ["fp8"]) : [null];
        IReadOnlyList<bool?> prefixValues = prefix ? [true, false] : [null];
        var memory = kvValues.SelectMany(k => prefixValues.Select(p => MemoryValue(k, p, gates.MambaHybrid))).ToList();
        var memoryDimension = kv ? TuningDimension.KvCacheDtype : TuningDimension.PrefixCaching;
        steps.Add(Step(memoryDimension, "KV cache × prefix caching", kv || prefix ? memory : [], kv || prefix ? null : NotSelected,
            kv && gates.KvAutoSkipReason is not null ? gates.KvAutoSkipReason : gates.MambaHybrid && prefix ? "Mamba hybrid: prefix caching on runs with mamba cache mode align, off with none." : null,
            TuningDescriptions.PhaseMemory));

        // Phase 2: concurrency.
        steps.Add(Step(TuningDimension.MaxNumSeqs, "Max num seqs",
            gates.MaxNumSeqsValues.Select(n => new TuningValue(n.ToString(Culture), Number: n)).ToList(),
            Included(TuningDimension.MaxNumSeqs) ? null : NotSelected,
            gates.Notes.FirstOrDefault(n => n.StartsWith("Max num seqs", StringComparison.Ordinal)), TuningDescriptions.PhaseConcurrency));
        steps.Add(Step(TuningDimension.MaxNumBatchedTokens, "Max batched tokens",
            gates.BatchedTokenValues.Select(n => new TuningValue(n.ToString("N0", Culture), Number: n)).ToList(),
            Included(TuningDimension.MaxNumBatchedTokens) ? null : NotSelected,
            gates.Notes.FirstOrDefault(n => n.StartsWith("Max batched", StringComparison.Ordinal)), TuningDescriptions.PhaseConcurrency));

        // Phase 3: decode acceleration.
        bool speculative = Included(TuningDimension.SpeculativeDecoding);
        bool graphs = Included(TuningDimension.CudaGraphMode);
        if (speculative)
        {
            var values = new List<TuningValue> { new($"Speculative off · {FullAndPiecewise}", Graph: FullAndPiecewise, Method: "off") };
            if (baseline.Speculative is { } saved)
            {
                values.Add(new($"Saved ({SpeculativeLabel(saved)}) · {FullAndPiecewise}", saved.NumSpeculativeTokens, Graph: FullAndPiecewise, Method: "saved"));
            }

            values.AddRange(gates.MtpDepths.Select(d => new TuningValue(string.Create(Culture, $"MTP × {d} · {FullAndPiecewise}"), d, Graph: FullAndPiecewise, Method: "mtp")));
            values.AddRange(gates.DraftDepths.Select(d => new TuningValue(string.Create(Culture, $"Draft × {d} · {FullAndPiecewise}"), d, Graph: FullAndPiecewise, Method: "draft")));
            string note = string.Join(" ", new[] { gates.MtpSkipReason, gates.DraftSkipReason, gates.Notes.FirstOrDefault(n => n.StartsWith("Draft model", StringComparison.Ordinal)),
                graphs ? "PIECEWISE is retried only for a full-graph candidate that fails and for the top two depths." : null }.Where(n => n is not null));
            steps.Add(Step(TuningDimension.SpeculativeDecoding, "Speculative × CUDA graphs", values, null, note.Length == 0 ? null : note, TuningDescriptions.PhaseDecode));
        }
        else
        {
            steps.Add(Step(graphs ? TuningDimension.CudaGraphMode : TuningDimension.SpeculativeDecoding, graphs ? "CUDA graph mode" : "Speculative × CUDA graphs",
                graphs ? [new(FullAndPiecewise, Graph: FullAndPiecewise), new(Piecewise, Graph: Piecewise)] : [],
                graphs ? null : NotSelected, null, TuningDescriptions.PhaseDecode));
        }

        steps.Add(Step(TuningDimension.AsyncScheduling, "Async scheduling", [new("Async on", Flag: true), new("Async off", Flag: false)],
            Included(TuningDimension.AsyncScheduling) ? null : NotSelected, null, TuningDescriptions.PhaseDecode));

        // Refinement: computed from the winners at the end.
        bool refine = Included(TuningDimension.Refinement);
        steps.Add(Step(TuningDimension.Refinement, "Refine neighbours", [], refine ? null : NotSelected,
            "Max num seqs and speculative-depth neighbours of the winners, everything else fixed.", TuningDescriptions.PhaseRefinement, RefineKey));
        steps.Add(Step(TuningDimension.Refinement, "Final prefix-cache check", [], refine ? null : NotSelected,
            "Prefix caching on and off with the winning decode settings.", TuningDescriptions.PhaseRefinement, PrefixCheckKey));

        // Preview: every step applied to the baseline (refinement too), duplicates removed.
        var preview = new List<TuningCandidatePlan> { new("Baseline", "Baseline (saved settings)", baseline, "Measure the saved settings as the reference every candidate is compared with.") };
        var seen = new HashSet<string>(StringComparer.Ordinal) { TuningPlanner.Fingerprint(baseline) };
        int max = 1;
        foreach (var step in steps.Where(s => s.SkipReason is null))
        {
            var candidates = CandidatesFor(step, baseline, context);
            max += step.Key is null ? candidates.Count : step.Key == RefineKey ? 4 : 2;
            preview.AddRange(candidates.Where(c => seen.Add(TuningPlanner.Fingerprint(c.Settings))));
        }

        int followUps = speculative && graphs ? 2 : 0;
        max += followUps + (speculative && graphs ? steps.First(s => s.Dimension == TuningDimension.SpeculativeDecoding).Values.Count : 0);
        int refinementExtra = refine ? 2 : 0; // neighbours not already measured from the baseline's point of view
        return new TuningPlan(steps, preview, Math.Max(max, preview.Count), context, preview.Count + followUps + refinementExtra);
    }

    /// <summary>The candidates of one step on top of <paramref name="best"/>.</summary>
    /// <param name="skipped">Receives the llama.cpp candidates skipped with their numbers (they do not fit, or llama.cpp
    /// refuses them); unused for vLLM.</param>
    public static IReadOnlyList<TuningCandidatePlan> CandidatesFor(TuningStepPlan step, ModelDefinition best, SearchContext context, ICollection<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(context);
        if (context.LlamaCpp is { } llama)
        {
            return LlamaCppTuning.CandidatesFor(step, best, llama, skipped);
        }

        if (step.SkipReason is not null)
        {
            return [];
        }

        if (step.Dimension == TuningDimension.Refinement)
        {
            return step.Key == PrefixCheckKey ? PrefixCheck(best, context) : Neighbours(best, context);
        }

        return step.Values
            .Select(v => new TuningCandidatePlan(step.Title, Label(step.Dimension, v), Apply(step.Dimension, v, best, context), TuningDescriptions.Value(step.Dimension, v).Text))
            .ToList();
    }

    /// <summary>
    /// Extra candidates a step earns from its own results: in the decode step (with CUDA graph mode selected), a
    /// PIECEWISE variant of every FULL_AND_PIECEWISE candidate that failed and of the two best speculative depths.
    /// </summary>
    public static IReadOnlyList<TuningCandidatePlan> FollowUps(TuningStepPlan step, IReadOnlyList<TuningCandidateResult> stepResults, SearchContext context)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(stepResults);
        ArgumentNullException.ThrowIfNull(context);
        if (context.LlamaCpp is not null)
        {
            return [];
        }

        if (step.Dimension != TuningDimension.SpeculativeDecoding || step.SkipReason is not null || !context.Includes(TuningDimension.CudaGraphMode))
        {
            return [];
        }

        var full = stepResults.Where(r => r.Settings.CudaGraphMode == FullAndPiecewise).ToList();
        var failed = full.Where(r => r.Outcome == CandidateOutcome.Failed);
        var top = full
            .Where(r => r.Outcome == CandidateOutcome.Accepted && r.Score is not null && r.Settings.Speculative is not null)
            .OrderByDescending(r => r.Score)
            .Take(2);
        var value = new TuningValue(Piecewise, Graph: Piecewise);
        return failed.Concat(top)
            .DistinctBy(r => r.Index)
            .Select(r => new TuningCandidatePlan(step.Title, r.Label.Replace(FullAndPiecewise, Piecewise, StringComparison.Ordinal),
                r.Settings with { CudaGraphMode = Piecewise },
                (r.Outcome == CandidateOutcome.Failed ? "The full-graph variant failed; retry with the safer capture mode. " : "One of the two best depths; check whether PIECEWISE does better. ")
                + TuningDescriptions.Value(TuningDimension.CudaGraphMode, value).Tradeoff))
            .ToList();
    }

    /// <summary>Applies one value of <paramref name="dimension"/> to <paramref name="best"/>.</summary>
    public static ModelDefinition Apply(TuningDimension dimension, TuningValue value, ModelDefinition best, SearchContext context)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(context);
        switch (dimension)
        {
            case TuningDimension.KvCacheDtype or TuningDimension.PrefixCaching:
                var memory = value.Text is { } kv ? best with { KvCacheDtype = kv } : best;
                return value.Flag is { } on ? WithPrefix(memory, on, context.Gates.MambaHybrid) : memory;
            case TuningDimension.MaxNumSeqs:
                return best with { MaxNumSeqs = value.Number ?? best.MaxNumSeqs };
            case TuningDimension.MaxNumBatchedTokens:
                return best with { MaxNumBatchedTokens = value.Number };
            case TuningDimension.SpeculativeDecoding or TuningDimension.CudaGraphMode:
                var decoded = value.Method switch
                {
                    "off" => best with { Speculative = null },
                    "saved" => best with { Speculative = context.Baseline.Speculative },
                    "mtp" => best with { Speculative = WithDepth(best.Speculative is { Method: "mtp" } m ? m : context.Baseline.Speculative is { Method: "mtp" } b ? b : new SpeculativeDecoding("mtp", 1), value.Number!.Value) },
                    "draft" => best with { Speculative = WithDepth((best.Speculative is { DraftModel.Length: > 0 } d ? d : context.Baseline.Speculative)!, value.Number!.Value) },
                    _ => best,
                };
                return value.Graph is { } graph ? decoded with { CudaGraphMode = graph } : decoded;
            case TuningDimension.AsyncScheduling:
                return best with { AsyncScheduling = value.Flag };
            default:
                return best;
        }
    }

    /// <summary>Prefix caching on/off; a Mamba hybrid pairs on with mamba cache mode align and off with none.</summary>
    public static ModelDefinition WithPrefix(ModelDefinition model, bool on, bool mambaHybrid)
    {
        ArgumentNullException.ThrowIfNull(model);
        return mambaHybrid
            ? model with { EnablePrefixCaching = on, MambaCacheMode = on ? "align" : "none" }
            : model with { EnablePrefixCaching = on };
    }

    private static List<TuningCandidatePlan> Neighbours(ModelDefinition best, SearchContext context)
    {
        var result = new List<TuningCandidatePlan>();
        const string Title = "Refine neighbours";
        if (context.Includes(TuningDimension.MaxNumSeqs))
        {
            var seqs = context.Gates.MaxNumSeqsValues.Append(context.Baseline.MaxNumSeqs).Append(best.MaxNumSeqs).Distinct().Order().ToList();
            int i = seqs.IndexOf(best.MaxNumSeqs);
            foreach (int n in new[] { i - 1, i + 1 }.Where(j => j >= 0 && j < seqs.Count).Select(j => seqs[j]))
            {
                var value = new TuningValue(string.Create(Culture, $"Refine: max num seqs {n}"), n, Method: "seqs");
                result.Add(new(Title, value.Label, best with { MaxNumSeqs = n }, TuningDescriptions.Value(TuningDimension.Refinement, value).Text));
            }
        }

        if (context.Includes(TuningDimension.SpeculativeDecoding) && best.Speculative is { } speculative)
        {
            IReadOnlyList<int> depths = speculative.Method == "mtp" ? context.Gates.MtpDepths
                : !string.IsNullOrEmpty(speculative.DraftModel) ? context.Gates.DraftDepths
                : [];
            var list = depths.Append(speculative.NumSpeculativeTokens).Where(d => d > 0).Distinct().Order().ToList();
            int i = list.IndexOf(speculative.NumSpeculativeTokens);
            if (depths.Count > 0 && i >= 0)
            {
                foreach (int d in new[] { i - 1, i + 1 }.Where(j => j >= 0 && j < list.Count).Select(j => list[j]))
                {
                    var value = new TuningValue(string.Create(Culture, $"Refine: depth {d}"), d, Method: "depth");
                    result.Add(new(Title, value.Label, best with { Speculative = WithDepth(speculative, d) }, TuningDescriptions.Value(TuningDimension.Refinement, value).Text));
                }
            }
        }

        return result;
    }

    private static List<TuningCandidatePlan> PrefixCheck(ModelDefinition best, SearchContext context) =>
        OnOff
            .Select(on =>
            {
                var value = new TuningValue(on ? "Final check: prefix caching on" : "Final check: prefix caching off", Flag: on, Method: "prefix");
                return new TuningCandidatePlan("Final prefix-cache check", value.Label, WithPrefix(best, on, context.Gates.MambaHybrid),
                    TuningDescriptions.Value(TuningDimension.Refinement, value).Text);
            })
            .ToList();

    /// <summary>"Max num seqs 32", "Batched tokens 8,192"; the other values carry a full label already.</summary>
    private static string Label(TuningDimension dimension, TuningValue value) =>
        dimension is TuningDimension.MaxNumSeqs or TuningDimension.MaxNumBatchedTokens ? TuningPlanner.CandidateLabel(dimension, value) : value.Label;

    private static SpeculativeDecoding WithDepth(SpeculativeDecoding speculative, int depth) => speculative with { NumSpeculativeTokens = depth };

    private static TuningValue MemoryValue(string? kv, bool? prefix, bool hybrid)
    {
        var parts = new List<string>();
        if (kv is not null)
        {
            parts.Add($"KV {kv}");
        }

        if (prefix is { } on)
        {
            parts.Add(on ? (hybrid ? "prefix on (mamba align)" : "prefix on") : (hybrid ? "prefix off (mamba none)" : "prefix off"));
        }

        return new TuningValue(string.Join(" · ", parts), Text: kv, Flag: prefix);
    }

    private static string SpeculativeLabel(SpeculativeDecoding s) => s.Method switch
    {
        "mtp" => string.Create(Culture, $"MTP × {s.NumSpeculativeTokens}"),
        "" => string.Create(Culture, $"draft × {s.NumSpeculativeTokens}"),
        _ => string.Create(Culture, $"{s.Method} × {s.NumSpeculativeTokens}"),
    };

    private static TuningStepPlan Step(TuningDimension dimension, string title, IReadOnlyList<TuningValue> values, string? skip, string? note, string phase, string? key = null) =>
        new(dimension, title, values, skip, note, phase,
            dimension == TuningDimension.Refinement ? TuningDescriptions.Phase(TuningDescriptions.PhaseRefinement).Text : TuningDescriptions.Dimension(dimension).Text, key);

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private static readonly bool[] OnOff = [true, false];
}
