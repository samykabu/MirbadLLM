using System.Globalization;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;

namespace MirbadLLM.Web;

/// <summary>
/// Number and state formatting shared by every page, so a measurement reads the same everywhere. Missing
/// values (null or NaN) always render as an em dash, never as 0 or "NaN".
/// </summary>
public static class UiFormat
{
    public const string Missing = "—";

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static bool HasValue(double? value) => value is double v && !double.IsNaN(v) && !double.IsInfinity(v);

    /// <summary>One decimal ("57.3"), or <see cref="Missing"/>.</summary>
    public static string Decimal1(double? value) => HasValue(value) ? value!.Value.ToString("0.0", Culture) : Missing;

    /// <summary>Up to one decimal ("54", "54.2"), or <see cref="Missing"/>.</summary>
    public static string Number(double? value) => HasValue(value) ? value!.Value.ToString("0.#", Culture) : Missing;

    /// <summary>Whole number with thousands separators ("1,204"), or <see cref="Missing"/>.</summary>
    public static string Whole(double? value) => HasValue(value) ? value!.Value.ToString("#,0", Culture) : Missing;

    /// <summary>"A", "A or B", "A, B or C", with <paramref name="word"/> before the last name.</summary>
    public static string JoinNames(IReadOnlyList<string> names, string word) =>
        names.Count < 2 ? string.Concat(names) : $"{string.Join(", ", names.Take(names.Count - 1))} {word} {names[^1]}";

    public static string GiB(double? value) => HasValue(value) ? $"{Decimal1(value)} GiB" : Missing;

    public static string Percent(double? value) => HasValue(value) ? $"{Number(value)}%" : Missing;

    /// <summary>A latency in seconds as the pair (number, unit): "412"/"ms" below one second, else "1.24"/"s".</summary>
    public static (string Value, string Unit) Latency(double? seconds)
    {
        if (!HasValue(seconds))
        {
            return (Missing, "");
        }

        double s = seconds!.Value;
        return s < 1
            ? ((s * 1000).ToString("0", Culture), "ms")
            : (s.ToString("0.00", Culture), "s");
    }

    public static string LatencyText(double? seconds)
    {
        var (value, unit) = Latency(seconds);
        return unit.Length == 0 ? value : $"{value} {unit}";
    }

    /// <summary>"4s", "1m 05s", "1h 02m".</summary>
    public static string Elapsed(DateTimeOffset since, DateTimeOffset now)
    {
        var elapsed = now - since;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalHours >= 1)
        {
            return string.Create(Culture, $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m");
        }

        return elapsed.TotalMinutes >= 1
            ? string.Create(Culture, $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:00}s")
            : string.Create(Culture, $"{elapsed.Seconds}s");
    }

    /// <summary>A measured duration: "42s", "4m 12s", "1h 05m".</summary>
    public static string Duration(TimeSpan duration) => Elapsed(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch + duration);

    /// <summary>An estimate: "under a minute", "about 7 min", "about 1 h 20 min".</summary>
    public static string About(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMinutes(1))
        {
            return "under a minute";
        }

        int minutes = (int)Math.Ceiling(duration.TotalMinutes);
        return minutes < 60
            ? string.Create(Culture, $"about {minutes} min")
            : string.Create(Culture, $"about {minutes / 60} h {minutes % 60:00} min");
    }

    /// <summary>A signed change in percent: "+12.3%", "−4%" (true minus sign), or <see cref="Missing"/>.</summary>
    public static string SignedPercent(double? value)
    {
        if (!HasValue(value))
        {
            return Missing;
        }

        double v = value!.Value;
        string magnitude = Math.Abs(v).ToString("0.#", Culture);
        return v > 0.05 ? $"+{magnitude}%" : v < -0.05 ? $"−{magnitude}%" : "0%";
    }

    /// <summary>Seconds with two decimals below 10 s ("0.84 s"), else one ("12.3 s"), or <see cref="Missing"/>.</summary>
    public static string Seconds(double? value) =>
        !HasValue(value) ? Missing : value!.Value < 10 ? $"{value.Value.ToString("0.00", Culture)} s" : $"{value.Value.ToString("0.0", Culture)} s";

    /// <summary>When a run happened, in the controller's local time: "2026-09-29 14:05" (" UTC" when that is UTC).</summary>
    public static string RunTime(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm", Culture) + (local.Offset == TimeSpan.Zero ? " UTC" : "");
    }

    /// <summary>
    /// ISO-8601 UTC instant for a <c>&lt;time datetime=…&gt;</c> element's <c>datetime</c> attribute, formatted
    /// client-side (see <c>wwwroot/js/shell.js</c>'s <c>formatLocalTimes</c>) into the viewer's local time —
    /// the controller always runs in UTC, so a server-rendered clock time is otherwise the controller's time
    /// zone, not the person looking at the page.
    /// </summary>
    public static string IsoUtc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Culture);

    /// <summary>Server-rendered fallback for a "until ~HH:mm" clock time, shown until client-side JS
    /// reformats the paired <c>&lt;time&gt;</c> element into the viewer's local time. Always explicit about
    /// being UTC, since unlike the reformatted value this one is not the viewer's own time zone.</summary>
    public static string ClockTimeUtc(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm", Culture) + " UTC";

    public static string Bytes(long bytes)
    {
        double gib = bytes / 1_073_741_824.0;
        return gib >= 1
            ? string.Create(Culture, $"{gib:0.00} GiB")
            : string.Create(Culture, $"{bytes / 1_048_576.0:0.0} MiB");
    }

    /// <summary>Context length with thousands separators ("262,144"), or <see cref="Missing"/>.</summary>
    public static string ContextLength(int maxModelLen) => Whole(maxModelLen);

    /// <summary>"MTP × 2", "n-gram × 4", or "Off" when speculative decoding is not configured.</summary>
    public static string SpeculativeLabel(SpeculativeDecoding? speculative)
    {
        if (speculative is null)
        {
            return "Off";
        }

        string name = speculative.Method.Equals("mtp", StringComparison.OrdinalIgnoreCase) ? "MTP"
            : speculative.Method.Equals("ngram", StringComparison.OrdinalIgnoreCase) ? "n-gram"
            : speculative.Method.Length == 0 ? "Draft"
            : speculative.Method;
        return $"{name} × {speculative.NumSpeculativeTokens}";
    }

    /// <summary>The single display name for each state, used by every page.</summary>
    public static string StateName(ModelState state) => state switch
    {
        ModelState.Stopped => "Stopped",
        ModelState.Starting => "Starting",
        ModelState.Ready => "Ready",
        ModelState.Stopping => "Stopping",
        ModelState.Failed => "Failed",
        _ => state.ToString(),
    };

    /// <summary>The status line of a model the health monitor restarts after a crash, or null when it has no crash record.</summary>
    public static string? CrashNote(CrashRestart? crash) => crash switch
    {
        null => null,
        { Paused: true } => $"Crashed {crash.Crashes} times, automatic restarts paused.",
        _ => $"Crashed, restarting (attempt {crash.Crashes} of {crash.Limit - 1}).",
    };

    /// <summary>
    /// The most useful single line of a failure dump (usually container log output): the last line that
    /// names an error or exception, else the last non-empty line. Trimmed to <paramref name="maxLength"/>.
    /// </summary>
    public static string FailureSummary(string? error, int maxLength = 200)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "No details were reported.";
        }

        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // The runtime puts the extracted root cause first ("Cause: ..."); prefer it over scanning the tail.
        const string causePrefix = "Cause: ";
        if (lines.Length > 0 && lines[0].StartsWith(causePrefix, StringComparison.Ordinal))
        {
            string cause = lines[0][causePrefix.Length..];
            return cause.Length <= maxLength ? cause : string.Concat(cause.AsSpan(0, maxLength - 1), "…");
        }

        string? pick = lines.LastOrDefault(l =>
            l.Contains("Error", StringComparison.Ordinal) ||
            l.Contains("error:", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Exception", StringComparison.Ordinal));
        pick ??= lines.Length > 0 ? lines[^1] : error.Trim();

        return pick.Length <= maxLength ? pick : string.Concat(pick.AsSpan(0, maxLength - 1), "…");
    }
}
