using System.Globalization;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>What a phase, step or candidate tries to achieve, and what it may cost. No promised gain.</summary>
public sealed record TuningDescription(string Goal, string Tradeoff)
{
    public string Text => $"{Goal} Tradeoff: {Tradeoff}";

    public override string ToString() => Text;
}

/// <summary>
/// The one catalog of tuning explanations (D1): every phase, every dimension and every value a search can try has a
/// "what we try to achieve" line and its tradeoff, shown in the plan preview, the live progress and each result row.
/// Also renders a definition's effective settings in one line.
/// </summary>
public static class TuningDescriptions
{
    public const string PhaseMemory = "Phase 1 · Memory & cache";
    public const string PhaseConcurrency = "Phase 2 · Concurrency";
    public const string PhaseDecode = "Phase 3 · Decode acceleration";
    public const string PhaseRefinement = "Refinement";

    public static readonly IReadOnlyList<string> Phases = [PhaseMemory, PhaseConcurrency, PhaseDecode, PhaseRefinement];

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static TuningDescription Phase(string phase) => phase switch
    {
        PhaseMemory => new("Find the KV-cache format and prefix-cache setting that leave the most room for requests and reuse repeated context.",
            "FP8 KV halves cache memory but can cost some speed; prefix caching helps repeated prompts and costs a little bookkeeping otherwise."),
        PhaseConcurrency => new("Size how many requests run together and how many tokens one scheduler step may process.",
            "More concurrency raises total throughput but lowers per-request speed and can delay first tokens."),
        PhaseDecode => new("Speed up token generation with speculative decoding, CUDA graphs and asynchronous scheduling.",
            "Speculation only pays off when drafts are accepted; graph and scheduler modes can fail to start on some models (the candidate then just fails)."),
        _ => new("Re-check the neighbours of the winners with everything else fixed, and compare prefix caching on and off with the final decode settings.",
            "A few extra restarts; settings interact, so a value that lost earlier can win now."),
    };

    public static TuningDescription Dimension(TuningDimension dimension) => dimension switch
    {
        TuningDimension.MlxParallel => new("Set how many requests mlx_lm.server decodes together.",
            "More raises total throughput, and each request gets slower and needs its own KV cache."),
        TuningDimension.MlxKvBits => new("Quantize the KV cache to fit longer contexts in less memory.",
            "Quantized caches make the server handle one request at a time."),
        TuningDimension.MlxDraftTokens => new("Set how many tokens the draft model proposes per step.",
            "Too many waste work when guesses miss."),
        TuningDimension.MlxPromptCache => new("Size the cache of prompts seen before.",
            "It only helps requests that share a prefix, and it uses memory all the time."),
        TuningDimension.SglangMaxRunningRequests => new("Set how many requests SGLang runs at once.",
            "More raises total throughput until memory runs short; each request gets slower."),
        TuningDimension.SglangSchedulePolicy => new("Choose the order waiting requests start in: first come, or longest shared prefix.",
            "Longest prefix reuses the cache more, and a request with no shared prefix can wait longer."),
        TuningDimension.SglangDraftDepth => new("Try drafting fewer tokens per step than the saved depth.",
            "Fewer wastes less when guesses miss, and gains less when they hit. Deeper isn't tried, because the drafter's block size limits it."),
        TuningDimension.KvCacheDtype => new("Choose the KV-cache data type: FP8 or the model's own (auto, usually BF16).",
            "Auto doubles KV memory per token, so fewer or shorter requests fit; FP8 may cost a little speed or accuracy."),
        TuningDimension.PrefixCaching => new("Reuse the KV cache of prompt prefixes seen before instead of recomputing them.",
            "Big savings on repeated system prompts; a small overhead and (with speculative decoding) possible interactions otherwise."),
        TuningDimension.MaxNumSeqs => new("Set how many requests are decoded together.",
            "Higher raises box throughput; each request gets slower and memory per request shrinks."),
        TuningDimension.MaxNumBatchedTokens => new("Set the tokens one scheduler step may process (the chunked-prefill budget).",
            "Larger prefills long prompts faster but can stall decoding of other requests during that step."),
        TuningDimension.SpeculativeDecoding => new("Draft several tokens cheaply and verify them in one pass (MTP layers or a draft model).",
            "Pays off only when drafts are accepted; deeper drafts waste more work when rejected and cap scheduled tokens."),
        TuningDimension.CudaGraphMode => new("Capture the forward pass as replayable CUDA graphs (FULL_AND_PIECEWISE, or PIECEWISE).",
            "Full graphs cut launch overhead most but take longer to capture and fail on some kernels; PIECEWISE is the safer fallback."),
        TuningDimension.AsyncScheduling => new("Overlap scheduling of the next step with the GPU's current step.",
            "Usually helps at higher concurrency; can be unsupported with some speculative or structured-output paths."),

        // llama.cpp small tune (design-p3p4 §A.6, MN-225). Every step says what it tries to achieve (MN-230, D130).
        TuningDimension.Parallel => new("Set how many requests llama-server serves at once (-np).",
            "Each slot keeps its own full context, so the KV cache grows with the slots; more slots raise total throughput and cost memory."),
        TuningDimension.FlashAttention => new("Turn flash attention on or off (-fa): usually faster and smaller on recent GPUs.",
            "Off needs an f16 V cache (a quantized one then refuses to start); on can be slower on older kernels."),
        TuningDimension.KvCacheTypes => new("Store the KV cache as f16 or q8_0 (K and V together, -ctk/-ctv).",
            "q8_0 roughly halves the cache so more context or slots fit, at a small quality cost and needing flash attention."),
        TuningDimension.BatchSizes => new("Size prompt processing: the logical batch and the micro-batch (-b, -ub).",
            "Larger micro-batches prefill faster and cost compute memory linearly; an embedding model's vectors do not change."),
        _ => Phase(PhaseRefinement),
    };

    /// <summary>
    /// The llama.cpp small tune's step description (MN-225's step rows): <see cref="Dimension"/>'s text, with the
    /// embedding wording for <see cref="TuningDimension.BatchSizes"/> (a causal embedding model varies <c>ub = b</c> only).
    /// </summary>
    public static TuningDescription LlamaCppDimension(TuningDimension dimension, bool embedding) =>
        dimension == TuningDimension.BatchSizes && embedding
            ? new("Set the micro-batch (= batch) of a causal embedding model: larger ones prefill longer inputs in fewer passes.",
                "Compute memory grows with the micro-batch; the vectors stay the same (spike S1).")
            : Dimension(dimension);

    /// <summary>The description of one value of <paramref name="dimension"/>.</summary>
    public static TuningDescription Value(TuningDimension dimension, TuningValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (dimension)
        {
            case TuningDimension.KvCacheDtype or TuningDimension.PrefixCaching:
                string kv = value.Text switch
                {
                    "auto" => "an auto (BF16) KV cache for full precision",
                    "fp8" => "an FP8 KV cache for the most room",
                    _ => "the current KV cache",
                };
                string prefix = value.Flag switch
                {
                    true => "with prefix caching on to reuse repeated prompts",
                    false => "with prefix caching off to avoid its overhead",
                    _ => "with the current prefix-cache setting",
                };
                return new($"Try {kv}, {prefix}.",
                    value.Text == "auto" ? "Twice the KV memory per token: fewer requests fit." : "FP8 can cost some speed; prefix caching only helps when prompts repeat.");
            case TuningDimension.MaxNumSeqs:
                int seqs = value.Number ?? 0;
                return new(string.Create(Culture, $"Decode up to {seqs} requests together."),
                    seqs <= 4 ? "Little concurrency: fastest single stream, lowest box throughput."
                    : seqs >= 32 ? "High concurrency: more total tokens, slower streams and later first tokens under load."
                    : "A middle ground between single-stream speed and box throughput.");
            case TuningDimension.MaxNumBatchedTokens:
                int tokens = value.Number ?? 0;
                return new(string.Create(Culture, $"Let one scheduler step process up to {tokens:N0} tokens."),
                    tokens >= 16_384 ? "Long prompts prefill faster, but decoding of other requests stalls longer during those steps."
                    : "Decoding stays smooth, but long prompts take more steps to prefill.");
            case TuningDimension.SpeculativeDecoding or TuningDimension.CudaGraphMode:
                return Decode(value);
            case TuningDimension.AsyncScheduling:
                return value.Flag == true
                    ? new("Turn asynchronous scheduling on to overlap CPU scheduling with GPU work.", "May be unsupported with some speculative paths; then vLLM falls back or the start fails.")
                    : new("Turn asynchronous scheduling off for strictly sequential scheduling.", "Leaves CPU scheduling time between GPU steps.");
            default:
                return RefinementValue(value);
        }
    }

    private static TuningDescription Decode(TuningValue value)
    {
        string graph = value.Graph switch
        {
            "FULL_AND_PIECEWISE" => " with FULL_AND_PIECEWISE CUDA graphs (vLLM's default, the most launch overhead removed)",
            "PIECEWISE" => " with PIECEWISE CUDA graphs (the safer capture mode)",
            _ => "",
        };
        string tradeoffGraph = value.Graph == "PIECEWISE"
            ? " PIECEWISE leaves some launch overhead in place."
            : value.Graph is null ? "" : " Full graphs can fail to capture on some kernels; that candidate then fails.";
        return value.Method switch
        {
            "off" => new($"Decode without speculation{graph}.", "No draft overhead, but one token per forward pass." + tradeoffGraph),
            "saved" => new($"Keep the saved speculative configuration{graph}.", "Measures the graph mode on its own." + tradeoffGraph),
            "mtp" => new(string.Create(Culture, $"Let the model's MTP layers draft {value.Number} tokens per step{graph}."),
                (value.Number >= 3 ? "Deeper drafts waste more work when rejected." : "Shallow drafts gain less per accepted step.") + tradeoffGraph),
            "draft" => new(string.Create(Culture, $"Let the draft model propose {value.Number} tokens per step{graph}."),
                "The drafter costs memory and compute; deeper proposals are rejected more often and cap scheduled tokens." + tradeoffGraph),
            _ => new($"Run the current speculative settings{graph}.", "Only the graph capture mode changes." + tradeoffGraph),
        };
    }

    private static TuningDescription RefinementValue(TuningValue value) => value.Method switch
    {
        "seqs" => new(string.Create(Culture, $"Re-check max num seqs {value.Number}, next to the winner, with every other winner fixed."),
            "One more restart; the best concurrency can move once speculation and cache settings changed."),
        "depth" => new(string.Create(Culture, $"Re-check speculative depth {value.Number}, next to the winner, with every other winner fixed."),
            "One more restart; acceptance depends on the final concurrency."),
        _ => new($"Compare prefix caching {(value.Flag == true ? "on" : "off")} with the winning decode settings.",
            "Prefix caching interacts with speculative decoding; the earlier comparison ran with other settings."),
    };

    /// <summary>One line of the settings a candidate runs with, e.g. "MTP × 3 · graphs FULL_AND_PIECEWISE · seqs 16 · …".</summary>
    public static string Summary(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Runtime == RuntimeKind.Mlx && model.Mlx is { } mlx)
        {
            return string.Create(Culture, $"parallel {mlx.Parallel} · KV bits {(mlx.KvBits is { } bits ? bits.ToString(Culture) : "off")} · prompt cache {mlx.PromptCacheGiB:0.##} GiB{(mlx.NumDraftTokens is { } draft ? $" · draft {draft}" : "")}");
        }

        if (model.Runtime is RuntimeKind.Sglang or RuntimeKind.Nim)
        {
            string args = model.Container is { ExtraArgs.Count: > 0 } container ? string.Join(' ', container.ExtraArgs) : "no extra arguments";
            return model.Speculative is { } draft ? string.Create(Culture, $"{draft.Method} × {draft.NumSpeculativeTokens} · {args}") : args;
        }

        var m = VllmArgsNormalizer.Apply(model);
        string speculative = m.Speculative switch
        {
            null => "speculative off",
            { Method: "mtp" } s => string.Create(Culture, $"MTP × {s.NumSpeculativeTokens}"),
            { Method: "" } s => string.Create(Culture, $"draft × {s.NumSpeculativeTokens}"),
            var s => string.Create(Culture, $"{s.Method} × {s.NumSpeculativeTokens}"),
        };
        return string.Join(" · ",
            speculative,
            "graphs " + (m.CudaGraphMode ?? "default"),
            string.Create(Culture, $"seqs {m.MaxNumSeqs}"),
            "batched " + (m.MaxNumBatchedTokens is { } b ? b.ToString("N0", Culture) : "default"),
            "KV " + m.KvCacheDtype,
            "prefix " + OnOff(m.EnablePrefixCaching),
            "async " + OnOff(m.AsyncScheduling)) + (m.MambaCacheMode is { } mamba ? $" · mamba {mamba}" : "");
    }

    private static string OnOff(bool? value) => value switch
    {
        true => "on",
        false => "off",
        _ => "default",
    };
}
