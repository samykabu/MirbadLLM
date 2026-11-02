using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>What the llama.cpp candidates of a tuning run need besides the baseline (MN-225): the GGUF header summary.</summary>
/// <param name="Gguf">The model file's GGUF summary (the agent's <c>gguf.inspect</c> route); null when it could not be read:
/// memory estimates are then not checked (a candidate that does not fit fails at launch) and a causal embedding model's
/// micro-batch is not varied.</param>
public sealed record LlamaCppTuningContext(GgufSummary? Gguf);

/// <summary>A candidate's memory check (design-p3p4 §A.6): its GGUF estimate against the model's budget.</summary>
/// <param name="Fits">True/false when estimated; null when not checked (no GGUF summary, or one missing a size).</param>
/// <param name="Estimate">The estimate; null when not checked.</param>
/// <param name="Text">The numbers ("needs 9.42 GiB (…), more than the 8 GiB budget"), or why it was not checked.</param>
public sealed record LlamaCppFit(bool? Fits, GgufMemoryEstimate? Estimate, string Text);

/// <summary>
/// The small tune of a llama.cpp model (design-p3p4 §A.6, owner decision D11; MN-225). One dimension at a time from the
/// baseline (the saved definition), each step trying its values on top of the best result so far (the P2
/// <see cref="SearchPlanner"/> pattern), in this order:
/// <list type="number">
/// <item><see cref="TuningDimension.Parallel"/>: <c>-np</c> {1, 2, 4, 8} (<see cref="LlamaCppSettings.ContextSize"/> stays
/// per slot, so <c>-c</c> and the KV cache grow with the slots).</item>
/// <item><see cref="TuningDimension.FlashAttention"/>: <c>-fa</c> {on, off}.</item>
/// <item><see cref="TuningDimension.KvCacheTypes"/>: <c>-ctk</c> = <c>-ctv</c> ∈ {f16, q8_0} (K and V together).</item>
/// <item><see cref="TuningDimension.BatchSizes"/>: chat models <c>-b</c> × <c>-ub</c> ∈ {512, 2048} × {256, 512}; a causal
/// embedding model <c>ub = b</c> ∈ {1024, 2048, 4096} up to its <see cref="LlamaCppSettings.ContextSize"/> (spike S1:
/// identical vectors, a compute buffer linear in <c>ub</c>), written as <see cref="LlamaCppSettings.MicroBatchSize"/>; a
/// non-causal embedding model keeps <c>ub = b = ContextSize</c> and is not varied (D118).</item>
/// </list>
/// Context size, GPU layers, the quant (model and projector files), the split (mode, per-GPU budgets, placement) and the
/// memory budget are never varied. Candidates write typed settings only (the typed flags are refused in ExtraArgs, D126),
/// so <see cref="LlamaCppArgsBuilder.Build"/> emits each of them once and the execution fingerprint tells them apart
/// (D125). A value equal to the baseline's is not a candidate (llama.cpp defaults: f16 KV, <c>-b</c> 2048, <c>-ub</c> 512;
/// <c>-fa</c> unset is "auto", a configuration of its own). At most <see cref="MaxCandidates"/> configurations are measured,
/// the baseline included: values beyond that are left out of the last steps, with a note.
/// <para>Every candidate's GGUF estimate (<see cref="LlamaCppMemoryEstimate"/>) is checked against the model's budget first:
/// one that does not fit is skipped with its numbers (in the step's note at planning, against the baseline; in the run's
/// notes when a later best-so-far makes it too big). A quantized V cache with flash attention off is skipped too
/// (llama.cpp refuses to start it).</para>
/// <para>A dimension selection without any llama.cpp dimension (the vLLM Quick/Full selections, the tuning page's default)
/// runs the whole small tune; otherwise only the llama.cpp dimensions it names.</para>
/// </summary>
public static class LlamaCppTuning
{
    /// <summary>Configurations measured at most, the baseline included (design-p3p4 §A.6).</summary>
    public const int MaxCandidates = 12;

    /// <summary>llama.cpp's default logical batch (<c>-b</c>) of a chat model.</summary>
    public const int DefaultBatch = 2048;

    /// <summary>llama.cpp's KV cache type when none is set.</summary>
    public const string DefaultCacheType = "f16";

    public static readonly IReadOnlyList<int> ParallelValues = [1, 2, 4, 8];

    public static readonly IReadOnlyList<string> CacheTypes = ["f16", "q8_0"];

    public static readonly IReadOnlyList<int> ChatBatches = [512, 2048];

    public static readonly IReadOnlyList<int> ChatMicroBatches = [256, 512];

    public static readonly IReadOnlyList<int> CausalEmbeddingMicroBatches = [1024, 2048, 4096];

    /// <summary>The llama.cpp dimensions, in search order.</summary>
    public static readonly IReadOnlyList<TuningDimension> Dimensions =
        [TuningDimension.Parallel, TuningDimension.FlashAttention, TuningDimension.KvCacheTypes, TuningDimension.BatchSizes];

    public const string NotSelected = "Not selected.";

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    /// <summary>True when <paramref name="model"/> is tuned by this class (a llama.cpp model with settings).</summary>
    public static bool Applies(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Runtime == RuntimeKind.LlamaCpp && model.LlamaCpp is not null;
    }

    /// <summary>True when <paramref name="dimension"/> belongs to the llama.cpp small tune.</summary>
    public static bool IsLlamaCppDimension(TuningDimension dimension) => Dimensions.Contains(dimension);

    /// <summary>Whether <paramref name="dimension"/> runs for the selection <paramref name="dimensions"/> (see the class remarks).</summary>
    public static bool Included(IReadOnlyCollection<TuningDimension>? dimensions, TuningDimension dimension) =>
        dimensions is null || dimensions.Count == 0 || dimensions.Contains(dimension) || !dimensions.Any(IsLlamaCppDimension);

    public static string StepTitle(TuningDimension dimension) => dimension switch
    {
        TuningDimension.Parallel => "Parallel slots",
        TuningDimension.FlashAttention => "Flash attention",
        TuningDimension.KvCacheTypes => "KV cache types",
        TuningDimension.BatchSizes => "Batch × micro-batch",
        _ => dimension.ToString(),
    };

    /// <summary>The plan of <paramref name="baseline"/> (a llama.cpp model, <see cref="Applies"/>).</summary>
    /// <param name="baseline">The saved definition.</param>
    /// <param name="gguf">The model file's GGUF summary; null when it could not be read.</param>
    /// <param name="dimensions">The selection (see the class remarks); null = all.</param>
    /// <exception cref="ArgumentException"><paramref name="baseline"/> is not a llama.cpp model.</exception>
    public static TuningPlan Plan(ModelDefinition baseline, GgufSummary? gguf, IReadOnlyCollection<TuningDimension>? dimensions = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!Applies(baseline))
        {
            throw new ArgumentException($"Model '{baseline.Id}' is not a llama.cpp model.", nameof(baseline));
        }

        var context = new LlamaCppTuningContext(gguf);
        var s = baseline.LlamaCpp!;
        var steps = new List<TuningStepPlan>(Dimensions.Count);
        int budget = MaxCandidates - 1;
        foreach (var dimension in Dimensions)
        {
            var notes = new List<string>();
            string? skip = Included(dimensions, dimension) ? null : NotSelected;
            var values = new List<TuningValue>();
            if (skip is null)
            {
                var (all, reason) = Values(dimension, baseline, context);
                skip = reason;
                foreach (var value in all)
                {
                    var candidate = Apply(dimension, value, baseline);
                    if (Same(candidate, baseline))
                    {
                        continue; // the baseline's own value: already measured
                    }

                    if (Refusal(candidate) is { } refused)
                    {
                        notes.Add($"{CandidateLabel(dimension, value)} skipped: {refused}");
                        continue;
                    }

                    var fit = Fit(candidate, gguf);
                    if (fit.Fits == false)
                    {
                        notes.Add($"{CandidateLabel(dimension, value)} skipped: {fit.Text}");
                        continue;
                    }

                    values.Add(value);
                }

                bool trimmed = values.Count > budget;
                if (trimmed)
                {
                    var dropped = values.Skip(budget).Select(v => CandidateLabel(dimension, v)).ToList();
                    values = values.Take(budget).ToList();
                    notes.Add(string.Create(Culture,
                        $"Left out to stay within {MaxCandidates} candidates (the baseline included): {string.Join(", ", dropped)}."));
                }

                if (skip is null && values.Count == 0)
                {
                    skip = trimmed ? string.Create(Culture, $"Skipped: left out to stay within {MaxCandidates} candidates.")
                        : notes.Count > 0 ? "Skipped: no other value fits or starts."
                        : "Skipped: the baseline already has the only value tried.";
                }

                budget -= values.Count;
            }

            if (dimension == Dimensions[0] && gguf is null)
            {
                notes.Insert(0, "Memory estimates not checked: the GGUF header could not be read, so a candidate that does not fit fails at launch.");
            }

            steps.Add(new TuningStepPlan(dimension, StepTitle(dimension), values, skip, notes.Count == 0 ? null : string.Join(" ", notes),
                Description: Description(dimension, s.Embedding)));
        }

        var preview = new List<TuningCandidatePlan> { new("Baseline", "Baseline (saved settings)", baseline, "Measure the saved settings as the reference every candidate is compared with.") };
        var seen = new HashSet<string>(StringComparer.Ordinal) { Fingerprint(baseline) };
        foreach (var step in steps.Where(st => st.SkipReason is null))
        {
            preview.AddRange(CandidatesFor(step, baseline, context).Where(c => seen.Add(Fingerprint(c.Settings))));
        }

        int max = 1 + steps.Where(st => st.SkipReason is null).Sum(st => st.Values.Count);
        var search = new SearchContext(TuningGates.NotApplicable, baseline, [.. Dimensions.Where(d => Included(dimensions, d))]) { LlamaCpp = context };
        return new TuningPlan(steps, preview, max, search);
    }

    /// <summary>
    /// The candidates of <paramref name="step"/> on top of <paramref name="best"/>: each value applied, without the ones
    /// equal to <paramref name="best"/> and the ones that do not fit or that llama.cpp refuses (each named in
    /// <paramref name="skipped"/> with its numbers).
    /// </summary>
    public static IReadOnlyList<TuningCandidatePlan> CandidatesFor(
        TuningStepPlan step, ModelDefinition best, LlamaCppTuningContext context, ICollection<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(context);
        if (step.SkipReason is not null || !Applies(best))
        {
            return [];
        }

        var result = new List<TuningCandidatePlan>(step.Values.Count);
        foreach (var value in step.Values)
        {
            var candidate = Apply(step.Dimension, value, best);
            if (Same(candidate, best))
            {
                continue;
            }

            string label = CandidateLabel(step.Dimension, value);
            if (Refusal(candidate) is { } refused)
            {
                skipped?.Add($"{label} skipped: {refused}");
                continue;
            }

            var fit = Fit(candidate, context.Gguf);
            if (fit.Fits == false)
            {
                skipped?.Add($"{label} skipped: {fit.Text}");
                continue;
            }

            result.Add(new TuningCandidatePlan(step.Title, label, candidate, ValueDescription(step.Dimension, value)));
        }

        return result;
    }

    /// <summary>Applies one value to <paramref name="best"/>'s llama.cpp settings; nothing else changes.</summary>
    public static ModelDefinition Apply(TuningDimension dimension, TuningValue value, ModelDefinition best)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(best);
        if (best.LlamaCpp is not { } s)
        {
            return best;
        }

        var applied = dimension switch
        {
            TuningDimension.Parallel when value.Number is { } n => s with { Parallel = n },
            TuningDimension.FlashAttention when value.Flag is { } on => s with { FlashAttention = on },
            TuningDimension.KvCacheTypes when value.Text is { } type => s with { CacheTypeK = type, CacheTypeV = type },
            TuningDimension.BatchSizes when value.Method == EmbeddingBatchMethod && value.Number is { } ub => s with { MicroBatchSize = ub },
            TuningDimension.BatchSizes when value.Number is { } b && int.TryParse(value.Text, NumberStyles.None, Culture, out int ub) =>
                s with { BatchSize = b, MicroBatchSize = ub },
            _ => s,
        };
        return ReferenceEquals(applied, s) ? best : best with { LlamaCpp = applied };
    }

    /// <summary>The <see cref="TuningValue.Method"/> of a causal embedding model's <c>ub = b</c> value.</summary>
    public const string EmbeddingBatchMethod = "ub=b";

    public static string CandidateLabel(TuningDimension dimension, TuningValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return dimension switch
        {
            TuningDimension.Parallel => "Parallel " + value.Label,
            TuningDimension.FlashAttention => "Flash attention " + value.Label,
            TuningDimension.KvCacheTypes => "KV cache " + value.Label,
            TuningDimension.BatchSizes => value.Label,
            _ => value.Label,
        };
    }

    /// <summary>
    /// The memory check of <paramref name="candidate"/>: its GGUF estimate on its placed GPUs (at least one) against
    /// <see cref="ModelDefinition.MemoryBudgetGiB"/>, the budget admission reserves. (Per-GPU budgets need no check of their
    /// own: the estimate splits by them, and they sum to the budget.)
    /// </summary>
    public static LlamaCppFit Fit(ModelDefinition candidate, GgufSummary? gguf)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.LlamaCpp is not { } s)
        {
            return new LlamaCppFit(null, null, "not a llama.cpp model");
        }

        if (gguf is null)
        {
            return new LlamaCppFit(null, null, "not checked: the GGUF header could not be read");
        }

        int gpus = Math.Max(1, candidate.Placement?.GpuUuids?.Count ?? 0);
        GgufMemoryEstimate estimate;
        try
        {
            estimate = LlamaCppMemoryEstimate.Estimate(gguf, s, gpus);
        }
        catch (ArgumentException ex)
        {
            return new LlamaCppFit(null, null, "not checked: " + ex.Message);
        }

        string numbers = string.Create(Culture,
            $"needs {estimate.BudgetGiB:0.00} GiB (weights {estimate.WeightsGiB:0.00} + KV {estimate.KvGiB:0.00}{(estimate.SsmStateGiB > 0 ? string.Create(Culture, $" + SSM {estimate.SsmStateGiB:0.00}") : "")} + compute {estimate.ComputeGiB:0.00} GiB, × {LlamaCppMemoryEstimate.Overhead:0.00}, + CUDA {estimate.CudaContextGiB:0.00} GiB)");
        double budget = candidate.MemoryBudgetGiB;
        if (estimate.BudgetGiB > budget + 1e-9)
        {
            return new LlamaCppFit(false, estimate, string.Create(Culture, $"{numbers}, more than the {budget:0.##} GiB budget"));
        }

        return new LlamaCppFit(true, estimate, string.Create(Culture, $"{numbers}, within the {budget:0.##} GiB budget"));
    }

    /// <summary>
    /// Why llama.cpp refuses to start <paramref name="candidate"/>, or null: a quantized V cache needs flash attention, so
    /// one with flash attention explicitly off fails at start.
    /// </summary>
    public static string? Refusal(ModelDefinition candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.LlamaCpp is { FlashAttention: false } s && !IsF16Like(s.CacheTypeV)
            ? $"a quantized V cache ({s.CacheTypeV}) needs flash attention, which is off"
            : null;
    }

    /// <summary>
    /// The identity of a llama.cpp launch for de-duplication (<see cref="TuningPlanner.Fingerprint"/>): the
    /// <see cref="LlamaCppArgsBuilder.Build"/> argv on the placed GPUs plus the delivery, image and pinned executable version,
    /// hashed. The split weights are the per-GPU budgets, else equal (the split never varies within a run).
    /// </summary>
    public static string Fingerprint(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var s = model.LlamaCpp;
        var text = new StringBuilder("llamacpp");
        text.Append('\0').Append(s?.Delivery.ToString() ?? "").Append('\0').Append(s?.Image ?? "").Append('\0').Append(s?.ExecutableVersion ?? "");
        int gpus = model.Placement?.GpuUuids?.Count ?? 0;
        try
        {
            IReadOnlyList<double>? weights = gpus >= 2 ? s?.GpuBudgetsGiB ?? [.. Enumerable.Repeat(1.0, gpus)] : null;
            foreach (string arg in LlamaCppArgsBuilder.Build(model, gpus, weights))
            {
                text.Append('\0').Append(arg);
            }
        }
        catch (ArgumentException)
        {
            // Not launchable (it fails at start anyway): identify it by its settings instead.
            text.Append('\0').Append(JsonSerializer.Serialize(s));
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())).AsSpan(0, 8));
    }

    /// <summary>
    /// <paramref name="saved"/> with a candidate's tuned llama.cpp settings laid over it (the apply of a tuning result,
    /// design-p3p4 §A.6): <see cref="LlamaCppSettings.Parallel"/>, <see cref="LlamaCppSettings.FlashAttention"/>, the KV cache
    /// types, <see cref="LlamaCppSettings.BatchSize"/> and <see cref="LlamaCppSettings.MicroBatchSize"/>. Nothing else is
    /// taken from the candidate (the context, layers, files, split, budget and the vLLM fields stay as saved), so a changed
    /// value shows as restart pending through the execution fingerprint. Equal to <paramref name="saved"/> when either side
    /// has no llama.cpp settings.
    /// </summary>
    public static ModelDefinition MergeTuned(ModelDefinition saved, ModelDefinition candidate)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(candidate);
        if (saved.LlamaCpp is not { } s || candidate.LlamaCpp is not { } c)
        {
            return saved;
        }

        return saved with
        {
            LlamaCpp = s with
            {
                Parallel = c.Parallel,
                FlashAttention = c.FlashAttention,
                CacheTypeK = c.CacheTypeK,
                CacheTypeV = c.CacheTypeV,
                BatchSize = c.BatchSize,
                MicroBatchSize = c.MicroBatchSize,
            },
        };
    }

    /// <summary>
    /// Where the agent's <c>gguf.inspect</c> route finds <paramref name="model"/>'s GGUF: the top-level folder of
    /// <see cref="LlamaCppSettings.ModelFile"/> and the file name inside it. Null when the model has no llama.cpp settings or
    /// its file is not directly inside a top-level folder (the route takes a plain file name).
    /// </summary>
    public static (string Folder, string File)? GgufLocation(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.LlamaCpp?.ModelFile is not { Length: > 0 } file)
        {
            return null;
        }

        var parts = file.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 ? (parts[0], parts[1]) : null;
    }

    // ---------------------------------------------------------------- values

    private static (List<TuningValue> Values, string? Skip) Values(TuningDimension dimension, ModelDefinition baseline, LlamaCppTuningContext context)
    {
        var s = baseline.LlamaCpp!;
        switch (dimension)
        {
            case TuningDimension.Parallel:
                return ([.. ParallelValues.Select(n => new TuningValue(n.ToString(Culture), Number: n))], null);
            case TuningDimension.FlashAttention:
                return ([new("on", Flag: true), new("off", Flag: false)], null);
            case TuningDimension.KvCacheTypes:
                return ([.. CacheTypes.Select(t => new TuningValue(t, Text: t))], null);
            case TuningDimension.BatchSizes when !s.Embedding:
                return ([.. ChatBatches.SelectMany(b => ChatMicroBatches.Select(ub => new TuningValue(
                    string.Create(Culture, $"Batch {b} · micro-batch {ub}"), Number: b, Text: ub.ToString(Culture))))], null);
            case TuningDimension.BatchSizes:
                if (context.Gguf is null)
                {
                    return ([], "Skipped: the GGUF header could not be read, so it is unknown whether this embedding model is causal (a non-causal one must keep micro-batch = batch = context size).");
                }

                if (context.Gguf.CausalAttention == false)
                {
                    return ([], string.Create(Culture,
                        $"Not varied: a non-causal embedding model keeps micro-batch = batch = context size ({s.ContextSize:N0}), its whole input in one micro-batch."));
                }

                var fitting = CausalEmbeddingMicroBatches.Where(ub => ub <= s.ContextSize).ToList();
                return fitting.Count == 0
                    ? ([], string.Create(Culture, $"Skipped: every micro-batch tried (1,024 to 4,096) is above the context size ({s.ContextSize:N0})."))
                    : ([.. fitting.Select(ub => new TuningValue(string.Create(Culture, $"Micro-batch = batch {ub}"), Number: ub, Method: EmbeddingBatchMethod))], null);
            default:
                return ([], null);
        }
    }

    /// <summary>True when the two launch the same configuration (defaults resolved: f16 KV, chat <c>-b</c> 2048, <c>-ub</c> 512).</summary>
    private static bool Same(ModelDefinition a, ModelDefinition b)
    {
        var x = a.LlamaCpp!;
        var y = b.LlamaCpp!;
        return x.Parallel == y.Parallel &&
            x.FlashAttention == y.FlashAttention &&
            string.Equals(x.CacheTypeK ?? DefaultCacheType, y.CacheTypeK ?? DefaultCacheType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.CacheTypeV ?? DefaultCacheType, y.CacheTypeV ?? DefaultCacheType, StringComparison.OrdinalIgnoreCase) &&
            (x.Embedding
                ? x.EmbeddingMicroBatch() == y.EmbeddingMicroBatch()
                : (x.BatchSize ?? DefaultBatch) == (y.BatchSize ?? DefaultBatch) &&
                  (x.MicroBatchSize ?? LlamaCppMemoryEstimate.DefaultChatMicroBatch) == (y.MicroBatchSize ?? LlamaCppMemoryEstimate.DefaultChatMicroBatch));
    }

    private static bool IsF16Like(string? type) =>
        type is null || type.Equals("f16", StringComparison.OrdinalIgnoreCase) || type.Equals("bf16", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("f32", StringComparison.OrdinalIgnoreCase);

    /// <summary>The step's "what this tries to achieve" line, from the one description catalog (D1, MN-230).</summary>
    private static string Description(TuningDimension dimension, bool embedding) =>
        IsLlamaCppDimension(dimension) ? TuningDescriptions.LlamaCppDimension(dimension, embedding).Text : "";

    private static string ValueDescription(TuningDimension dimension, TuningValue value) => dimension switch
    {
        TuningDimension.Parallel => $"Serve {value.Label} request(s) at once.",
        TuningDimension.FlashAttention => $"Flash attention {value.Label}.",
        TuningDimension.KvCacheTypes => $"K and V cache as {value.Label}.",
        _ => value.Label + ".",
    };
}
