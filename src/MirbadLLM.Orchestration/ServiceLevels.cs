namespace MirbadLLM.Orchestration;

/// <summary>Targets the Overview's service levels are measured against (<c>MirbadLLM:Overview</c>).</summary>
public sealed class OverviewOptions
{
    public const string SectionName = "MirbadLLM:Overview";

    /// <summary>Error budget: the failed share of requests the fleet may spend, in percent.</summary>
    public double ErrorBudgetPercent { get; set; } = 2;

    /// <summary>Time-to-first-token p95 target, in milliseconds.</summary>
    public double TtftTargetMs { get; set; } = 1000;
}

/// <summary>The Overview's service-level figures, every formula in one place (the page only formats what these return).</summary>
public static class ServiceLevels
{
    /// <summary>The share of the error budget left, 0–1: 1 − error% / budget%; null without requests.</summary>
    public static double? ErrorBudgetLeft(double? errorPercent, double budgetPercent) =>
        errorPercent is double e && double.IsFinite(e) && budgetPercent > 0 ? Math.Clamp(1 - (e / budgetPercent), 0, 1) : null;

    /// <summary>The latency budget left, 0–1: 1 − p95 / target; null without a reading.</summary>
    public static double? LatencyBudgetLeft(double? ttftP95Seconds, double targetMs) =>
        ttftP95Seconds is double t && double.IsFinite(t) && targetMs > 0 ? Math.Clamp(1 - (t * 1000 / targetMs), 0, 1) : null;

    /// <summary>Ready of the models that should run, 0–1; null when none should run.</summary>
    public static double? AvailabilityScore(int ready, int shouldRun) => shouldRun > 0 ? Math.Clamp((double)ready / shouldRun, 0, 1) : null;

    /// <summary>TTFT p95 against its target, 0–1: full marks at or under the target, falling linearly to zero at twice it.</summary>
    public static double? LatencyScore(double? ttftP95Seconds, double targetMs) =>
        ttftP95Seconds is double t && double.IsFinite(t) && targetMs > 0 ? Math.Clamp(2 - (t * 1000 / targetMs), 0, 1) : null;
}
