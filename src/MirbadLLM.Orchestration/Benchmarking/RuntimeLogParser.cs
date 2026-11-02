using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>What vLLM reported resolving at startup, read from the container log. Null fields = not reported.</summary>
/// <param name="Notices">Runtime warnings that change the effective configuration (e.g. a capped token budget).</param>
/// <param name="KvCacheTokens">"GPU KV cache size: 507,425 tokens".</param>
/// <param name="MaxConcurrency">"Maximum concurrency for 65,536 tokens per request: 7.74x" (the factor; <paramref name="ConcurrencyTokens"/> the request size).</param>
public sealed record RuntimeReport(
    string? CudaGraphMode, string? SpeculativeMethod, int? SpeculativeTokens, bool? AsyncScheduling, IReadOnlyList<string> Notices,
    long? KvCacheTokens = null, double? MaxConcurrency = null, int? ConcurrencyTokens = null)
{
    public bool Empty => CudaGraphMode is null && SpeculativeMethod is null && SpeculativeTokens is null && AsyncScheduling is null && Notices.Count == 0;

    /// <summary>"KV pool 507k tokens = 7.7 concurrent requests at 65,536 max model len"; null until both lines were logged.</summary>
    public string? KvPoolLine =>
        KvCacheTokens is { } kv && MaxConcurrency is { } factor && ConcurrencyTokens is { } len
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"KV pool {kv / 1000.0:0}k tokens = {factor:0.#} concurrent requests at {len:N0} max model len")
            : null;
}

/// <summary>
/// Reads the effective runtime settings out of a vLLM container log — the resolved CUDA graph mode
/// (<c>'cudagraph_mode': &lt;CUDAGraphMode.PIECEWISE: 1&gt;</c>, "cudagraph_mode is set to …", "Overriding … to PIECEWISE"),
/// the speculative config (<c>SpeculativeConfig(method='mtp', … num_spec_tokens=3)</c>), async scheduling
/// (<c>async_scheduling=True</c>, "Asynchronous scheduling is enabled/disabled") and notices such as
/// "max_num_scheduled_tokens is set to 2048 based on the speculative decoding settings" — and compares them with what
/// was requested. The last report of each wins (vLLM logs the final value last).
/// </summary>
public static partial class RuntimeLogParser
{
    public const string AsRequested = "as requested";

    public static RuntimeReport Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        string? graph = null;
        string? method = null;
        int? tokens = null;
        bool? async = null;
        long? kvTokens = null;
        double? concurrency = null;
        int? concurrencyTokens = null;
        var notices = new List<string>();
        foreach (string line in lines)
        {
            foreach (Match m in GraphMode().Matches(line))
            {
                graph = m.Groups["mode"].Value;
            }

            if (KvCacheSize().Match(line) is { Success: true } kv)
            {
                kvTokens = long.Parse(kv.Groups["tokens"].Value.Replace(",", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);
            }

            if (Concurrency().Match(line) is { Success: true } cc)
            {
                concurrencyTokens = int.Parse(cc.Groups["len"].Value.Replace(",", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);
                concurrency = double.Parse(cc.Groups["factor"].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            if (SpecConfig().Match(line) is { Success: true } spec)
            {
                method = spec.Groups["method"].Success ? spec.Groups["method"].Value : method;
                tokens = spec.Groups["n"].Success ? int.Parse(spec.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : tokens;
            }

            if (AsyncFlag().Match(line) is { Success: true } flag)
            {
                async = flag.Groups["value"].Value == "True";
            }
            else if (AsyncEnabled().IsMatch(line))
            {
                async = true;
            }
            else if (AsyncDisabled().IsMatch(line))
            {
                async = false;
                notices.Add(Clean(line));
            }

            if (ScheduledTokens().Match(line) is { Success: true } capped)
            {
                notices.Add(Clean(capped.Value));
            }
        }

        return new RuntimeReport(graph, method, tokens, async, notices.Distinct(StringComparer.Ordinal).ToList(), kvTokens, concurrency, concurrencyTokens);
    }

    /// <summary>
    /// Differences between what was requested and what vLLM reported ("CUDA graph mode: requested
    /// FULL_AND_PIECEWISE → ran PIECEWISE"), plus notices; <see cref="AsRequested"/> when there are none.
    /// </summary>
    public static IReadOnlyList<string> Compare(ModelDefinition requested, RuntimeReport report)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(report);
        var notes = new List<string>();
        if (report.CudaGraphMode is { } graph && requested.CudaGraphMode is { } wantedGraph &&
            !string.Equals(graph, wantedGraph, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"CUDA graph mode: requested {wantedGraph} → ran {graph}");
        }
        else if (report.CudaGraphMode is { } resolved && requested.CudaGraphMode is null)
        {
            notes.Add($"CUDA graph mode: vLLM default resolved to {resolved}");
        }

        if (requested.Speculative is { } speculative && report.SpeculativeTokens is { } ran && speculative.NumSpeculativeTokens > 0 && ran != speculative.NumSpeculativeTokens)
        {
            notes.Add($"Speculative tokens: requested {speculative.NumSpeculativeTokens} → ran {ran}");
        }

        if (requested.AsyncScheduling is { } wantedAsync && report.AsyncScheduling is { } ranAsync && wantedAsync != ranAsync)
        {
            notes.Add($"Async scheduling: requested {(wantedAsync ? "on" : "off")} → ran {(ranAsync ? "on" : "off")}");
        }

        notes.AddRange(report.Notices);
        return notes.Count == 0 ? [AsRequested] : notes;
    }

    private static string Clean(string line)
    {
        string text = line.Trim();
        int warn = text.IndexOf("] ", StringComparison.Ordinal);
        text = warn >= 0 && warn < 80 ? text[(warn + 2)..] : text;
        return text.Length <= 200 ? text : string.Concat(text.AsSpan(0, 199), "…");
    }

    [GeneratedRegex(@"cudagraph_mode['""]?\s*(?:[:=]|is set to|to)\s*<?(?:CUDAGraphMode\.)?(?<mode>FULL_AND_PIECEWISE|FULL_DECODE_ONLY|PIECEWISE|FULL|NONE)\b", RegexOptions.CultureInvariant)]
    private static partial Regex GraphMode();

    [GeneratedRegex(@"SpeculativeConfig\((?:[^)]*?method=['""](?<method>[\w-]+)['""])?(?:[^)]*?num_spec(?:ulative)?_tokens=(?<n>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex SpecConfig();

    [GeneratedRegex(@"async_scheduling['""]?\s*[:=]\s*(?<value>True|False)", RegexOptions.CultureInvariant)]
    private static partial Regex AsyncFlag();

    [GeneratedRegex(@"[Aa]sync(?:hronous)? scheduling (?:is|will be) enabled", RegexOptions.CultureInvariant)]
    private static partial Regex AsyncEnabled();

    [GeneratedRegex(@"[Aa]sync(?:hronous)? scheduling (?:is|will be) (?:disabled|not supported)", RegexOptions.CultureInvariant)]
    private static partial Regex AsyncDisabled();

    [GeneratedRegex(@"max_num_scheduled_tokens is set to \d+[^.]*", RegexOptions.CultureInvariant)]
    private static partial Regex ScheduledTokens();

    [GeneratedRegex(@"GPU KV cache size:\s*(?<tokens>\d[\d,]*) tokens", RegexOptions.CultureInvariant)]
    private static partial Regex KvCacheSize();

    [GeneratedRegex(@"Maximum concurrency for (?<len>\d[\d,]*) tokens per request:\s*(?<factor>\d+(?:\.\d+)?)x", RegexOptions.CultureInvariant)]
    private static partial Regex Concurrency();
}

/// <summary>Tuning time estimates (D2): from the candidate count and this model's measured startup and benchmark durations.</summary>
public static class TuningEstimator
{
    /// <summary>
    /// <paramref name="candidates"/> starts + benchmarks (minus one start when the running baseline is reused), plus
    /// <paramref name="confirmations"/> re-measurements whose benchmarks run <paramref name="confirmationFactor"/> times
    /// as long (5 repetitions instead of 3).
    /// </summary>
    public static TimeSpan Estimate(int candidates, bool reusesBaseline, int confirmations, TimeSpan startup, TimeSpan benchmark, double confirmationFactor)
    {
        var total = (startup + benchmark) * Math.Max(0, candidates);
        if (reusesBaseline && candidates > 0)
        {
            total -= startup;
        }

        return total + ((startup + (benchmark * confirmationFactor)) * Math.Max(0, confirmations));
    }

    /// <summary>The median of the measured values, else <paramref name="fallback"/>.</summary>
    public static TimeSpan Median(IEnumerable<double> seconds, TimeSpan fallback)
    {
        var values = seconds.Where(s => s > 0 && double.IsFinite(s)).Order().ToList();
        if (values.Count == 0)
        {
            return fallback;
        }

        double median = values.Count % 2 == 1 ? values[values.Count / 2] : (values[(values.Count / 2) - 1] + values[values.Count / 2]) / 2;
        return TimeSpan.FromSeconds(median);
    }
}
