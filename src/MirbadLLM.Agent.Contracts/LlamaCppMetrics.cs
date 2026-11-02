namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// The <c>llama-server --metrics</c> families mirbadllm reads (design-p3p4 §A.1.9; frozen by MN-202, D116). The agent's
/// <c>MetricFamilyFilter</c> keeps exactly <see cref="Families"/> for llama.cpp instances (MN-223) and the master's KPI
/// calculator and benchmark map them onto <c>ModelKpis</c>: running = <see cref="RequestsProcessing"/>, waiting =
/// <see cref="RequestsDeferred"/>, prompt tokens/s = Δ<see cref="PromptTokensTotal"/>, generation tokens/s =
/// Δ<see cref="TokensPredictedTotal"/>, prefix-cache hit rate = Δ<see cref="PromptTokensCachedTotal"/> ÷
/// Δ<see cref="PromptTokensTotal"/>, speculative acceptance = Δ<see cref="SpecDecodeAcceptedTotal"/> ÷
/// Δ<see cref="SpecDecodeDraftTotal"/>. KV-cache usage and TTFT/ITL histograms have no llama.cpp source and read null
/// ("—"). Names were checked against <c>tools/server/server-task.cpp</c> on 2026-10-07; spike S1 confirms them on the
/// pinned build (a renamed family is an additive change through the wave review).
/// </summary>
public static class LlamaCppMetrics
{
    /// <summary>Prefix of every llama.cpp family.</summary>
    public const string Prefix = "llamacpp:";

    /// <summary>Gauge: requests being processed.</summary>
    public const string RequestsProcessing = Prefix + "requests_processing";

    /// <summary>Gauge: requests waiting for a free slot.</summary>
    public const string RequestsDeferred = Prefix + "requests_deferred";

    /// <summary>Counter: prompt tokens processed.</summary>
    public const string PromptTokensTotal = Prefix + "prompt_tokens_total";

    /// <summary>Counter: seconds spent on prompt processing (the server's own prefill speed).</summary>
    public const string PromptSecondsTotal = Prefix + "prompt_seconds_total";

    /// <summary>Counter: tokens generated.</summary>
    public const string TokensPredictedTotal = Prefix + "tokens_predicted_total";

    /// <summary>Counter: seconds spent generating (the server's own decode speed).</summary>
    public const string TokensPredictedSecondsTotal = Prefix + "tokens_predicted_seconds_total";

    /// <summary>Counter: prompt tokens served from the prompt cache.</summary>
    public const string PromptTokensCachedTotal = Prefix + "prompt_tokens_cached_total";

    /// <summary>Counter: speculative-decoding draft tokens accepted.</summary>
    public const string SpecDecodeAcceptedTotal = Prefix + "spec_decode_num_accepted_tokens_total";

    /// <summary>Counter: speculative-decoding draft tokens proposed.</summary>
    public const string SpecDecodeDraftTotal = Prefix + "spec_decode_num_draft_tokens_total";

    /// <summary>Every family the agent forwards for a llama.cpp instance.</summary>
    public static IReadOnlyList<string> Families { get; } =
    [
        RequestsProcessing, RequestsDeferred, PromptTokensTotal, PromptSecondsTotal, TokensPredictedTotal,
        TokensPredictedSecondsTotal, PromptTokensCachedTotal, SpecDecodeAcceptedTotal, SpecDecodeDraftTotal,
    ];
}
