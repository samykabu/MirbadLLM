using System.Globalization;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>What <see cref="ModelProgress.Assess"/> found wrong with a Ready model.</summary>
/// <param name="Stalled">True: requests are in flight and nothing moves (only a restart helps); false: it moves, slowly.</param>
/// <param name="Text">One line for the pages.</param>
public sealed record ModelProgressIssue(bool Stalled, string Text);

/// <summary>
/// Reads a Ready model's recent samples (<see cref="IMetricsHistory.GetModel"/>) for the two shapes <c>/health</c> cannot
/// see. 2026-10-18: a vLLM engine wedged with 5 requests in flight answered <c>/health</c> for 34 hours while every request
/// hung (stalled), and after its restart it served 13 requests at 2 tokens/s in total (slow).
/// <list type="bullet">
/// <item>Stalled: every sample in <see cref="Window"/> has requests running, no prompt or generated tokens, and the same
/// KV-cache usage (vLLM counts prompt tokens only when a prefill completes, so a long prefill shows as KV growth).</item>
/// <item>Slow: every sample has requests running or waiting and generates under <see cref="SlowTokensPerSecond"/>.</item>
/// </list>
/// Either needs samples covering the whole window, all with rates; one idle sample clears it.
/// </summary>
public static class ModelProgress
{
    /// <summary>How long the shape must hold.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>Total generation rate under which a busy model reads as slow.</summary>
    public const double SlowTokensPerSecond = 5;

    /// <summary>Samples may start this much after the window opens and still cover it (one sampler interval and change).</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(30);

    /// <summary>The issue in <paramref name="samples"/> (oldest first, as the history returns them), or null.</summary>
    public static ModelProgressIssue? Assess(IReadOnlyList<ModelSample> samples, DateTimeOffset now)
    {
        var recent = samples.Where(s => s.At >= now - Window).ToList();
        if (recent.Count < 2 || recent[0].At > now - Window + Slack || recent.Any(s => !s.HasRates))
        {
            return null;
        }

        var loads = recent.Select(s => s.Kpis.Load).ToList();
        if (loads.Any(l => !(l.RunningRequests + l.WaitingRequests > 0)))
        {
            return null;
        }

        double kv = loads[0].KvCacheUsage;
        bool stalled = loads.All(l =>
            l.RunningRequests > 0
            && !(l.GenerationTokensPerSecond > 0)
            && !(l.PromptTokensPerSecond > 0)
            && (double.IsNaN(kv) ? double.IsNaN(l.KvCacheUsage) : l.KvCacheUsage == kv));
        var last = loads[^1];
        string minutes = Window.TotalMinutes.ToString("0", CultureInfo.InvariantCulture);
        if (stalled)
        {
            return new ModelProgressIssue(true, string.Create(CultureInfo.InvariantCulture,
                $"Stalled: {last.RunningRequests:0} requests running and no tokens for {minutes} min, though its health check passes. Stop and start it."));
        }

        double average = loads.Average(l => double.IsFinite(l.GenerationTokensPerSecond) ? l.GenerationTokensPerSecond : 0);
        return loads.All(l => !(l.GenerationTokensPerSecond >= SlowTokensPerSecond))
            ? new ModelProgressIssue(false, string.Create(CultureInfo.InvariantCulture,
                $"Slow: {average:0.#} tokens/s for {minutes} min with {last.RunningRequests:0} running and {last.WaitingRequests:0} waiting."))
            : null;
    }
}
