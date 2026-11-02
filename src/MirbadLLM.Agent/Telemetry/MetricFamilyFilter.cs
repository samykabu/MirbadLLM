using System.Collections.Frozen;
using System.Text;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// Reduces a model's raw Prometheus <c>/metrics</c> text to the metric families the master reads (design §8.1):
/// exactly the names <c>ModelKpiCalculator</c> and <c>BenchmarkSuite</c> use for vLLM, plus the llama.cpp
/// (<c>--metrics</c>) families that map to the same KPIs (<see cref="LlamaCppMetrics.Families"/>, design-p3p4 §A.1.9).
/// Lines are kept byte for byte (no parsing or reformatting; <c>PrometheusText</c> stays on the master), so the master
/// computes identical KPIs from the filtered text.
/// <para>A sample line is kept when its metric name is listed, or is a listed histogram's <c>_bucket</c>, <c>_sum</c> or
/// <c>_count</c> series (<c>_created</c> is dropped: nothing reads it). <c># HELP</c>/<c># TYPE</c> lines are kept for
/// kept families so the text stays a valid exposition; other comments and blank lines are dropped.</para>
/// <para>Keep in sync with <c>ModelKpiCalculator</c> and <c>BenchmarkSuite.PrefixCacheHitMetrics</c>: a unit test
/// (<c>TelemetryTests</c>) fails when they read a name this filter drops.</para>
/// </summary>
public static class MetricFamilyFilter
{
    /// <summary>Gauges and counters read by name (sample names as exposed, counters with <c>_total</c>).</summary>
    public static IReadOnlySet<string> Metrics { get; } = new[]
    {
        // ModelKpiCalculator (vLLM)
        "vllm:num_requests_running",
        "vllm:num_requests_waiting",
        "vllm:kv_cache_usage_perc",
        "vllm:gpu_cache_usage_perc",
        "vllm:generation_tokens_total",
        "vllm:prompt_tokens_total",
        "vllm:request_success_total",
        "vllm:prefix_cache_hits_total",
        "vllm:prefix_cache_queries_total",
        "vllm:spec_decode_num_accepted_tokens_total",
        "vllm:spec_decode_num_draft_tokens_total",
        "vllm:num_preemptions_total",
        "vllm:tool_call_parser_invocations_total",

        // BenchmarkSuite prefix-cache hit counter (names across vLLM versions)
        "vllm:prefix_cache_hits",
        "vllm:gpu_prefix_cache_hits_total",
        "vllm:gpu_prefix_cache_hits",

        // llama.cpp --metrics (design-p3p4 §A.1.9): exactly the frozen LlamaCppMetrics.Families, appended below. The
        // P2 list's llamacpp:kv_cache_usage_ratio is gone from current llama.cpp, and the *_tokens_seconds gauges are
        // read by nothing (the master computes rates from the counters), so neither is forwarded.
    }.Concat(LlamaCppMetrics.Families).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Histogram base names (their <c>_bucket</c>, <c>_sum</c> and <c>_count</c> series are kept).</summary>
    public static IReadOnlySet<string> Histograms { get; } = new[]
    {
        "vllm:time_to_first_token_seconds",
        "vllm:inter_token_latency_seconds",
        "vllm:e2e_request_latency_seconds",
        "vllm:request_queue_time_seconds",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] HistogramSuffixes = ["_bucket", "_sum", "_count"];

    /// <summary>True when a sample with metric name <paramref name="name"/> is forwarded.</summary>
    public static bool KeepsSample(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (Metrics.Contains(name))
        {
            return true;
        }

        foreach (string suffix in HistogramSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && Histograms.Contains(name[..^suffix.Length]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a <c># HELP</c>/<c># TYPE</c> line names a kept family. Exposition family names omit a counter's
    /// <c>_total</c> (<c># TYPE vllm:prompt_tokens counter</c>), so <c>{family}_total</c> also counts.
    /// </summary>
    public static bool KeepsFamily(string family)
    {
        ArgumentNullException.ThrowIfNull(family);
        return Metrics.Contains(family) || Metrics.Contains(family + "_total") || Histograms.Contains(family);
    }

    /// <summary>Returns <paramref name="text"/> with only the kept lines, each terminated by <c>\n</c>.</summary>
    public static string Filter(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new StringBuilder(Math.Min(text.Length, 64 * 1024));
        foreach (var rawLine in text.AsSpan().EnumerateLines())
        {
            var line = rawLine.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            bool keep = line[0] == '#' ? KeepsComment(line) : KeepsSample(SampleName(line).ToString());
            if (keep)
            {
                result.Append(line).Append('\n');
            }
        }

        return result.ToString();
    }

    private static bool KeepsComment(ReadOnlySpan<char> line)
    {
        // "# HELP <family> <doc>" / "# TYPE <family> <type>"
        var rest = line[1..].TrimStart();
        if (!rest.StartsWith("HELP ", StringComparison.Ordinal) && !rest.StartsWith("TYPE ", StringComparison.Ordinal))
        {
            return false;
        }

        rest = rest[5..].TrimStart();
        int end = rest.IndexOfAny(' ', '\t');
        var family = end < 0 ? rest : rest[..end];
        return KeepsFamily(family.ToString());
    }

    private static ReadOnlySpan<char> SampleName(ReadOnlySpan<char> line)
    {
        int end = line.IndexOfAny('{', ' ', '\t');
        return end < 0 ? line : line[..end];
    }
}
