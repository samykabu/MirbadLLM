using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// Computes <see cref="ModelKpis"/> from two consecutive Prometheus scrapes of one model's <c>/metrics</c>: vLLM's families
/// as before, or llama.cpp's (<c>--metrics</c>, <see cref="LlamaCppMetrics"/>) mapped as design-p3p4 §A.1.9 says.
/// <para>A scrape is read as llama.cpp when it has a <c>llamacpp:</c> sample and no <c>vllm:</c> one, so every vLLM scrape
/// takes exactly the P1/P2 path. llama.cpp has no source for KV-cache usage, the latency histograms, finished requests,
/// preemptions or tool calls: the nullable KPIs are null and the non-nullable ones (<see cref="ModelMetrics.KvCacheUsage"/>,
/// <see cref="ModelKpis.RequestsPerMinute"/>, <see cref="ModelKpis.PreemptionsPerMinute"/>,
/// <see cref="ModelKpis.ToolCallsPerMinute"/>) are <see cref="double.NaN"/>, the value every page and the master's
/// <c>/metrics</c> already show as "—" or leave out (requests are counted by the router's stats instead).</para>
/// </summary>
internal static class ModelKpiCalculator
{
    private const string RunningMetric = "vllm:num_requests_running";
    private const string WaitingMetric = "vllm:num_requests_waiting";
    private const string KvCacheMetric = "vllm:kv_cache_usage_perc";
    private const string KvCacheFallbackMetric = "vllm:gpu_cache_usage_perc";
    private const string GenerationTokensMetric = "vllm:generation_tokens_total";
    private const string PromptTokensMetric = "vllm:prompt_tokens_total";
    private const string RequestSuccessMetric = "vllm:request_success_total";
    private const string PrefixCacheHitsMetric = "vllm:prefix_cache_hits_total";
    private const string PrefixCacheQueriesMetric = "vllm:prefix_cache_queries_total";
    private const string SpecDecodeAcceptedMetric = "vllm:spec_decode_num_accepted_tokens_total";
    private const string SpecDecodeDraftMetric = "vllm:spec_decode_num_draft_tokens_total";
    private const string PreemptionsMetric = "vllm:num_preemptions_total";
    private const string ToolCallsMetric = "vllm:tool_call_parser_invocations_total";
    private const string TimeToFirstTokenHistogram = "vllm:time_to_first_token_seconds";
    private const string InterTokenLatencyHistogram = "vllm:inter_token_latency_seconds";
    private const string E2eLatencyHistogram = "vllm:e2e_request_latency_seconds";
    private const string QueueTimeHistogram = "vllm:request_queue_time_seconds";
    private const string FinishedReasonLabel = "finished_reason";

    // llama.cpp (design-p3p4 §A.1.9); the agent's MetricFamilyFilter forwards exactly LlamaCppMetrics.Families.
    private const string LlamaRunningMetric = LlamaCppMetrics.RequestsProcessing;
    private const string LlamaWaitingMetric = LlamaCppMetrics.RequestsDeferred;
    private const string LlamaPromptTokensMetric = LlamaCppMetrics.PromptTokensTotal;
    private const string LlamaGenerationTokensMetric = LlamaCppMetrics.TokensPredictedTotal;
    private const string LlamaPromptTokensCachedMetric = LlamaCppMetrics.PromptTokensCachedTotal;
    private const string LlamaSpecDecodeAcceptedMetric = LlamaCppMetrics.SpecDecodeAcceptedTotal;
    private const string LlamaSpecDecodeDraftMetric = LlamaCppMetrics.SpecDecodeDraftTotal;

    /// <summary>
    /// <paramref name="previous"/> is null on the first scrape of a model (no baseline yet): every rate,
    /// latency and ratio is then zero/null rather than computed.
    /// </summary>
    public static ModelKpis Compute(IReadOnlyList<PrometheusSample> current, IReadOnlyList<PrometheusSample>? previous, double elapsedSeconds)
    {
        if (IsLlamaCpp(current))
        {
            return ComputeLlamaCpp(current, previous, elapsedSeconds);
        }

        double running = PrometheusText.Sum(current, RunningMetric);
        double waiting = PrometheusText.Sum(current, WaitingMetric);
        double kv = PrometheusText.HasMetric(current, KvCacheMetric)
            ? PrometheusText.Sum(current, KvCacheMetric)
            : PrometheusText.Sum(current, KvCacheFallbackMetric);

        double generationRate = CounterMath.PerSecond(SumOrNull(previous, GenerationTokensMetric), PrometheusText.Sum(current, GenerationTokensMetric), elapsedSeconds);
        double promptRate = CounterMath.PerSecond(SumOrNull(previous, PromptTokensMetric), PrometheusText.Sum(current, PromptTokensMetric), elapsedSeconds);
        var load = new ModelMetrics(running, waiting, kv, generationRate, promptRate);

        double requestsPerMinute = CounterMath.PerMinute(SumOrNull(previous, RequestSuccessMetric), PrometheusText.Sum(current, RequestSuccessMetric), elapsedSeconds);

        var timeToFirstToken = Latency(current, previous, TimeToFirstTokenHistogram);
        var interTokenLatency = Latency(current, previous, InterTokenLatencyHistogram);
        var endToEndLatency = Latency(current, previous, E2eLatencyHistogram);
        var queueTime = Latency(current, previous, QueueTimeHistogram);

        double? prefixCacheHitRate = Ratio(previous, current, PrefixCacheHitsMetric, PrefixCacheQueriesMetric);
        double? specDecodeAcceptanceRate = PrometheusText.HasMetric(current, SpecDecodeDraftMetric)
            ? Ratio(previous, current, SpecDecodeAcceptedMetric, SpecDecodeDraftMetric)
            : null;

        double preemptionsPerMinute = CounterMath.PerMinute(SumOrNull(previous, PreemptionsMetric), PrometheusText.Sum(current, PreemptionsMetric), elapsedSeconds);
        double toolCallsPerMinute = CounterMath.PerMinute(SumOrNull(previous, ToolCallsMetric), PrometheusText.Sum(current, ToolCallsMetric), elapsedSeconds);

        var finishedByReasonPerMinute = FinishedByReason(current, previous, elapsedSeconds);

        return new ModelKpis(
            load,
            requestsPerMinute,
            timeToFirstToken,
            interTokenLatency,
            endToEndLatency,
            queueTime,
            prefixCacheHitRate,
            specDecodeAcceptanceRate,
            preemptionsPerMinute,
            finishedByReasonPerMinute,
            toolCallsPerMinute);
    }

    /// <summary>True for a llama.cpp scrape: a <c>llamacpp:</c> sample and no <c>vllm:</c> sample.</summary>
    internal static bool IsLlamaCpp(IReadOnlyList<PrometheusSample> samples)
    {
        bool llama = false;
        foreach (var sample in samples)
        {
            if (sample.Name.StartsWith("vllm:", StringComparison.Ordinal))
            {
                return false;
            }

            llama |= sample.Name.StartsWith(LlamaCppMetrics.Prefix, StringComparison.Ordinal);
        }

        return llama;
    }

    /// <summary>
    /// The §A.1.9 mapping: running = requests processing, waiting = requests deferred, prompt/generation tokens per second
    /// from the prompt/predicted token counters, prefix-cache hit rate = Δcached ÷ Δprompt tokens, speculative acceptance =
    /// Δaccepted ÷ Δdraft tokens. A ratio is null without a baseline, when either family is missing or when its
    /// denominator did not grow; everything without a llama.cpp source is null or NaN (see the class remarks).
    /// </summary>
    private static ModelKpis ComputeLlamaCpp(IReadOnlyList<PrometheusSample> current, IReadOnlyList<PrometheusSample>? previous, double elapsedSeconds)
    {
        double running = PrometheusText.Sum(current, LlamaRunningMetric);
        double waiting = PrometheusText.Sum(current, LlamaWaitingMetric);
        double generationRate = CounterMath.PerSecond(SumOrNull(previous, LlamaGenerationTokensMetric), PrometheusText.Sum(current, LlamaGenerationTokensMetric), elapsedSeconds);
        double promptRate = CounterMath.PerSecond(SumOrNull(previous, LlamaPromptTokensMetric), PrometheusText.Sum(current, LlamaPromptTokensMetric), elapsedSeconds);
        var load = new ModelMetrics(running, waiting, double.NaN, generationRate, promptRate);

        return new ModelKpis(
            load,
            RequestsPerMinute: double.NaN,
            TimeToFirstToken: null,
            InterTokenLatency: null,
            EndToEndLatency: null,
            QueueTime: null,
            PrefixCacheHitRate: RatioOfPresent(previous, current, LlamaPromptTokensCachedMetric, LlamaPromptTokensMetric),
            SpecDecodeAcceptanceRate: RatioOfPresent(previous, current, LlamaSpecDecodeAcceptedMetric, LlamaSpecDecodeDraftMetric),
            PreemptionsPerMinute: double.NaN,
            FinishedByReasonPerMinute: new Dictionary<string, double>(StringComparer.Ordinal),
            ToolCallsPerMinute: double.NaN);
    }

    /// <summary>
    /// <see cref="Ratio"/>, capped at 1, when both families are in both scrapes, else null (a missing family is not a
    /// zero). Only the llama.cpp ratios use this.
    /// </summary>
    private static double? RatioOfPresent(IReadOnlyList<PrometheusSample>? previous, IReadOnlyList<PrometheusSample> current, string numeratorMetric, string denominatorMetric)
    {
        if (previous is null
            || !PrometheusText.HasMetric(current, numeratorMetric) || !PrometheusText.HasMetric(current, denominatorMetric)
            || !PrometheusText.HasMetric(previous, numeratorMetric) || !PrometheusText.HasMetric(previous, denominatorMetric))
        {
            return null;
        }

        // Capped at 1 (MN-230, D130): the two llama.cpp ratios divide by a counter that is assumed to include the
        // numerator (cached prompt tokens inside prompt_tokens_total, accepted drafts inside the drafted ones). If a
        // llama.cpp build counts only the tokens it actually processed, the raw ratio can exceed 1 and the page would
        // read "900%". The assumption itself is on the live checklist (plan-p3p4 §4.2, W16).
        return Ratio(previous, current, numeratorMetric, denominatorMetric) is { } ratio ? Math.Min(1, ratio) : null;
    }

    private static double? SumOrNull(IReadOnlyList<PrometheusSample>? samples, string name)
        => samples is null ? null : PrometheusText.Sum(samples, name);

    private static LatencyStats? Latency(IReadOnlyList<PrometheusSample> current, IReadOnlyList<PrometheusSample>? previous, string baseName)
    {
        if (previous is null)
        {
            return null;
        }

        var currentHistogram = PrometheusText.Histogram(current, baseName);
        if (currentHistogram is null)
        {
            return null;
        }

        var previousHistogram = PrometheusText.Histogram(previous, baseName);
        var delta = HistogramMath.Delta(previousHistogram, currentHistogram);
        if (delta is null || delta.Count <= 0)
        {
            return null;
        }

        double? p50 = HistogramMath.Quantile(delta, 0.50);
        double? p95 = HistogramMath.Quantile(delta, 0.95);
        if (p50 is null || p95 is null)
        {
            return null;
        }

        return new LatencyStats(p50.Value, p95.Value);
    }

    private static double? Ratio(IReadOnlyList<PrometheusSample>? previous, IReadOnlyList<PrometheusSample> current, string numeratorMetric, string denominatorMetric)
    {
        if (previous is null)
        {
            return null;
        }

        double currentNumerator = PrometheusText.Sum(current, numeratorMetric);
        double currentDenominator = PrometheusText.Sum(current, denominatorMetric);
        double previousNumerator = PrometheusText.Sum(previous, numeratorMetric);
        double previousDenominator = PrometheusText.Sum(previous, denominatorMetric);

        double deltaDenominator = CounterMath.Delta(previousDenominator, currentDenominator);
        if (deltaDenominator <= 0)
        {
            return null;
        }

        double deltaNumerator = CounterMath.Delta(previousNumerator, currentNumerator);
        return deltaNumerator / deltaDenominator;
    }

    private static Dictionary<string, double> FinishedByReason(IReadOnlyList<PrometheusSample> current, IReadOnlyList<PrometheusSample>? previous, double elapsedSeconds)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        if (previous is null || elapsedSeconds <= 0)
        {
            return result;
        }

        var currentByReason = PrometheusText.SumByLabel(current, RequestSuccessMetric, FinishedReasonLabel);
        var previousByReason = PrometheusText.SumByLabel(previous, RequestSuccessMetric, FinishedReasonLabel);

        foreach (var (reason, currentValue) in currentByReason)
        {
            double previousValue = previousByReason.GetValueOrDefault(reason);
            result[reason] = CounterMath.Delta(previousValue, currentValue) / elapsedSeconds * 60.0;
        }

        return result;
    }
}
