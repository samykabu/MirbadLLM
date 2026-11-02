using System.Globalization;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// Scores a candidate against the baseline for a workload preset (R2-5): the weighted geometric mean of the
/// candidate ÷ baseline ratios of the preset's components (inverted for latencies), weights renormalized over the
/// components measured on both sides; 1.0 = the baseline. Guards (<see cref="SloGuards"/>) reject regardless of
/// speed, and a non-finite or non-positive measurement of an active component rejects too. Per-repetition scores
/// give each candidate a range for the confirmation stage's non-overlap test.
/// </summary>
public static class WorkloadScorer
{
    /// <summary>A candidate must beat the baseline by this factor in the confirmation stage.</summary>
    public const double MinimumGain = 1.05;

    public static CandidateEvaluation Evaluate(WorkloadProfile profile, BenchmarkResult baseline, BenchmarkResult candidate)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        var guards = RelaxForBaseline(profile.Guards, baseline).Guards;
        string? rejected = profile.Embeddings ? EmbeddingGuards(guards, baseline, candidate) : GenerationGuards(guards, baseline, candidate);
        rejected ??= InvalidComponent(profile, baseline, candidate);
        double? score = Score(profile, baseline, candidate);
        if (rejected is not null)
        {
            return new CandidateEvaluation(false, score, rejected);
        }

        return score is null
            ? new CandidateEvaluation(false, null, "Missing measurements for this preset")
            : new CandidateEvaluation(true, score, null);
    }

    /// <summary>
    /// An absolute guard the baseline itself breaks (e.g. short-prompt TTFT p95 above 1 s at N=1) becomes relative for the
    /// run — "no worse than the baseline" — instead of making every candidate unconfirmable. Returns the guards to apply
    /// and one note per relaxed guard, for the run's notes.
    /// </summary>
    public static (SloGuards Guards, IReadOnlyList<string> Notes) RelaxForBaseline(SloGuards guards, BenchmarkResult baseline)
    {
        ArgumentNullException.ThrowIfNull(guards);
        ArgumentNullException.ThrowIfNull(baseline);
        var notes = new List<string>();
        int shortest = baseline.Levels.Select(l => l.OutputTokens).DefaultIfEmpty(0).Min();
        var relaxed = guards;
        if (guards.ShortTtftP95AtN1Seconds is { } n1 && baseline.Level(1, shortest) is { TtftP95: var b1 } && b1 > n1)
        {
            relaxed = relaxed with { ShortTtftP95AtN1Seconds = b1 };
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Guard made relative: the baseline's short-prompt TTFT p95 at N=1 is {b1:0.00} s, above the {n1:0.##} s limit, so candidates must be no worse than the baseline."));
        }

        if (guards.ShortTtftP95AtN16Seconds is { } n16 && baseline.Level(16, shortest) is { TtftP95: var b16 } && b16 > n16)
        {
            relaxed = relaxed with { ShortTtftP95AtN16Seconds = b16 };
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Guard made relative: the baseline's short-prompt TTFT p95 at N=16 is {b16:0.00} s, above the {n16:0.##} s limit, so candidates must be no worse than the baseline."));
        }

        if (guards.MixedShortTtftP95Seconds is { } mixed && baseline.Mixed is { ShortTtftP95Runs.Count: > 0, ShortTtftP95: var bm } && bm > mixed)
        {
            relaxed = relaxed with { MixedShortTtftP95Seconds = bm };
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Guard made relative: the baseline's short-prompt TTFT p95 under a long request is {bm:0.00} s, above the {mixed:0.##} s limit, so candidates must be no worse than the baseline."));
        }

        if (guards.MaxPreemptions is { } maxPreemptions && baseline.Preemptions is { } preempted && preempted > maxPreemptions)
        {
            relaxed = relaxed with { MaxPreemptions = preempted };
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Guard made relative: the baseline's KV pool is too small for this workload ({preempted} preemptions, limit {maxPreemptions}), so candidates may preempt at most as often."));
        }

        int timed = baseline.TimedRequests;
        if (timed > 0 && (double)baseline.Errors / timed > guards.MaxErrorRate)
        {
            double rate = (double)baseline.Errors / timed;
            relaxed = relaxed with { MaxErrorRate = rate };
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Guard made relative: {baseline.Errors} of the baseline's {timed} timed requests failed, so candidates may fail at most as often ({rate:P0})."));
        }

        return (relaxed, notes);
    }

    /// <summary>
    /// The score: candidate medians (or repetition <paramref name="repetition"/>'s values) against the baseline's
    /// medians. Null when no component is measured on both sides.
    /// </summary>
    public static double? Score(WorkloadProfile profile, BenchmarkResult baseline, BenchmarkResult candidate, int? repetition = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        double weighted = 0;
        double weights = 0;
        foreach (var (component, weight) in profile.Weights)
        {
            double? b = Component(baseline, component, null);
            double? c = Component(candidate, component, repetition);
            if (!Usable(b) || !Usable(c) || weight <= 0)
            {
                continue;
            }

            double ratio = WorkloadProfiles.HigherIsBetter(component) ? c!.Value / b!.Value : b!.Value / c!.Value;
            weighted += weight * Math.Log(ratio);
            weights += weight;
        }

        return weights > 0 ? Math.Exp(weighted / weights) : null;
    }

    /// <summary>The lowest and highest per-repetition score; null without repetitions.</summary>
    public static (double Min, double Max)? Range(WorkloadProfile profile, BenchmarkResult baseline, BenchmarkResult candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var scores = Enumerable.Range(0, Repetitions(candidate))
            .Select(r => Score(profile, baseline, candidate, r))
            .Where(s => s is { } v && double.IsFinite(v))
            .Select(s => s!.Value)
            .ToList();
        return scores.Count == 0 ? null : (scores.Min(), scores.Max());
    }

    /// <summary>
    /// The confirmation-stage verdict: the candidate beats the baseline by at least <see cref="MinimumGain"/> and the
    /// ranges do not overlap (its lowest repetition is above the baseline's highest).
    /// </summary>
    public static bool BeatsBaseline(double? score, (double Min, double Max)? range, double? baselineScore, (double Min, double Max)? baselineRange) =>
        score is { } s && baselineScore is { } b && b > 0 && s >= b * MinimumGain &&
        range is { } r && baselineRange is { } br && r.Min > br.Max;

    /// <summary>A component's value: the median, or one repetition's value. Null when not measured.</summary>
    public static double? Component(BenchmarkResult result, ScoreComponent component, int? repetition)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (component)
        {
            case ScoreComponent.ChatN1:
                return Mean(result.Levels.Where(l => l.Concurrency == 1), l => Pick(l.PerRequestTokS, l.Runs, repetition, r => r.PerRequestTokS));
            case ScoreComponent.ChatN16:
                return Mean(result.Levels.Where(l => l.Concurrency == 16), l => Pick(l.AggregateTokS, l.Runs, repetition, r => r.AggregateTokS));
            case ScoreComponent.ChatNMax:
                int max = result.Levels.Select(l => l.Concurrency).DefaultIfEmpty(0).Max();
                return max <= 16 ? null : Mean(result.Levels.Where(l => l.Concurrency == max), l => Pick(l.AggregateTokS, l.Runs, repetition, r => r.AggregateTokS));
            case ScoreComponent.LongPrompt16K or ScoreComponent.LongPrompt64K:
                int target = component == ScoreComponent.LongPrompt16K ? WorkloadProfiles.Long16K : WorkloadProfiles.Long64K;
                var lp = result.LongPrompts?.FirstOrDefault(l => l.TargetTokens == target);
                return lp is null || lp.TtftRuns.Count == 0 ? null : Pick(lp.TtftMedian, lp.TtftRuns, repetition, t => t);
            case ScoreComponent.SharedPrefix:
                var sp = result.SharedPrefix;
                return sp is null || sp.TtftRuns.Count == 0 ? null : Pick(sp.TtftMedian, sp.TtftRuns, repetition, t => t);
            case ScoreComponent.MixedShortTtft:
                var mixed = result.Mixed;
                return mixed is null || mixed.ShortTtftP95Runs.Count == 0 ? null : Pick(mixed.ShortTtftP95, mixed.ShortTtftP95Runs, repetition, t => t);
            case ScoreComponent.EmbeddingN1 or ScoreComponent.EmbeddingN16:
                var level = result.Embeddings?.Level(component == ScoreComponent.EmbeddingN1 ? 1 : 16);
                return level is null ? null : Pick(level.InputTokS, level.Runs, repetition, r => r.InputTokS);
            default:
                return null;
        }
    }

    private static string? GenerationGuards(SloGuards guards, BenchmarkResult baseline, BenchmarkResult candidate)
    {
        int timed = candidate.TimedRequests;
        if (timed > 0 && (double)candidate.Errors / timed > guards.MaxErrorRate)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{candidate.Errors} of {timed} timed requests failed");
        }

        if (guards.RequireToolValidity && candidate.Tools is { } tools && tools.Valid < tools.Total)
        {
            var bad = tools.Cases.FirstOrDefault(c => !c.Valid);
            return string.Create(CultureInfo.InvariantCulture,
                $"Tool calls {tools.Valid}/{tools.Total} valid{(bad is null ? "" : $": {bad.Tool}: {bad.Detail}")}");
        }

        if (guards.ObjectiveAnswers && candidate.Guardrails.QaTotal > 0 && candidate.Guardrails.QaCorrect < baseline.Guardrails.QaCorrect)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Objective answers {candidate.Guardrails.QaCorrect}/{candidate.Guardrails.QaTotal}, below the baseline's {baseline.Guardrails.QaCorrect}/{baseline.Guardrails.QaTotal}");
        }

        int shortest = candidate.Levels.Select(l => l.OutputTokens).DefaultIfEmpty(0).Min();
        if (guards.ShortTtftP95AtN1Seconds is { } n1Limit && candidate.Level(1, shortest) is { } l1 && l1.TtftP95 > n1Limit)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Short-prompt TTFT p95 at N=1 is {l1.TtftP95:0.00} s, above the {n1Limit:0.##} s limit");
        }

        if (guards.ShortTtftP95AtN16Seconds is { } n16Limit && candidate.Level(16, shortest) is { } l16 && l16.TtftP95 > n16Limit)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Short-prompt TTFT p95 at N=16 is {l16.TtftP95:0.00} s, above the {n16Limit:0.##} s limit");
        }

        if (guards.MixedShortTtftP95Seconds is { } mixedLimit && candidate.Mixed is { ShortTtftP95Runs.Count: > 0 } mixed && mixed.ShortTtftP95 > mixedLimit)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Short-prompt TTFT p95 under a long request is {mixed.ShortTtftP95:0.00} s, above the {mixedLimit:0.##} s limit");
        }

        // Unknown preemptions (/metrics unreadable) skip the guard; the result view says so.
        if (guards.MaxPreemptions is { } maxPreemptions && candidate.Preemptions is { } preempted && preempted > maxPreemptions)
        {
            return string.Create(CultureInfo.InvariantCulture, $"KV pool too small for this workload: {preempted} preemptions");
        }

        if (guards.LongTtftMaxRatio is { } ratio)
        {
            foreach (var c in candidate.LongPrompts ?? [])
            {
                var b = baseline.LongPrompts?.FirstOrDefault(l => l.TargetTokens == c.TargetTokens);
                if (b is { TtftRuns.Count: > 0 } && c.TtftRuns.Count > 0 && c.TtftMedian > b.TtftMedian * ratio)
                {
                    return string.Create(CultureInfo.InvariantCulture,
                        $"{c.TargetTokens / 1024}K-token prompt TTFT is {c.TtftMedian:0.00} s, more than {(ratio - 1) * 100:0} % above the baseline's {b.TtftMedian:0.00} s");
                }
            }
        }

        return null;
    }

    private static string? EmbeddingGuards(SloGuards guards, BenchmarkResult baseline, BenchmarkResult candidate)
    {
        if (baseline.Embeddings is not { } b || candidate.Embeddings is not { } c)
        {
            return "No embeddings measurement";
        }

        if (!c.Check.Valid)
        {
            return c.Check.Detail;
        }

        if (b.Check.Valid && c.Check.Normalized != b.Check.Normalized)
        {
            return c.Check.Normalized ? "Vectors are normalized, unlike the baseline's" : "Vectors are no longer normalized (the baseline's are)";
        }

        if (guards.LatencyP95MaxRatio is { } ratio && b.Level(16) is { } b16 && c.Level(16) is { } c16 && c16.LatencyP95 > ratio * b16.LatencyP95)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Latency p95 at N=16 is {c16.LatencyP95:0.00} s, more than {ratio:0.#}× the baseline's {b16.LatencyP95:0.00} s");
        }

        return null;
    }

    /// <summary>An active component (measured by the baseline) whose candidate value is non-finite or not positive.</summary>
    private static string? InvalidComponent(WorkloadProfile profile, BenchmarkResult baseline, BenchmarkResult candidate)
    {
        foreach (var component in profile.Weights.Keys)
        {
            if (!Usable(Component(baseline, component, null)))
            {
                continue;
            }

            double? c = Component(candidate, component, null);
            if (c is { } v && !Usable(v))
            {
                return $"Invalid measurement: {WorkloadProfiles.ComponentName(component)} is {v.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        return null;
    }

    private static int Repetitions(BenchmarkResult result) =>
        new[]
        {
            result.Levels.Select(l => l.Runs.Count).DefaultIfEmpty(0).Max(),
            result.LongPrompts?.Select(l => l.TtftRuns.Count).DefaultIfEmpty(0).Max() ?? 0,
            result.SharedPrefix?.TtftRuns.Count ?? 0,
            result.Mixed?.ShortTtftP95Runs.Count ?? 0,
            result.Embeddings?.Levels.Select(l => l.Runs.Count).DefaultIfEmpty(0).Max() ?? 0,
        }.Max();

    private static double Pick<T>(double median, IReadOnlyList<T> runs, int? repetition, Func<T, double> value) =>
        repetition is { } r && runs.Count > 0 ? value(runs[Math.Min(r, runs.Count - 1)]) : median;

    private static double? Mean<T>(IEnumerable<T> items, Func<T, double> value)
    {
        var values = items.Select(value).ToList();
        return values.Count == 0 ? null : values.Average();
    }

    private static bool Usable(double? value) => value is { } v && double.IsFinite(v) && v > 0;
}
