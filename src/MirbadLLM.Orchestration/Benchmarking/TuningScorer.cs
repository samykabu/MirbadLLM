using System.Globalization;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>A candidate's verdict against the baseline.</summary>
public sealed record CandidateEvaluation(bool Accepted, double? Score, string? Reason);

/// <summary>
/// Goal scores and guardrails for tuning candidates. The workload presets (<see cref="WorkloadProfiles"/>) are scored by
/// <see cref="WorkloadScorer"/>; what follows describes the classic goals, kept for stored runs and API clients.
/// <list type="bullet">
/// <item><b>Single user</b>: median per-request tok/s at N=1 (ties: lower TTFT p50 at N=1).</item>
/// <item><b>Many agents</b>: median aggregate tok/s at N=16, provided TTFT p95 at N=16 is at most 2× the baseline's.</item>
/// <item><b>Balanced</b>: geometric mean of (N=1 tok/s ÷ baseline) and (N=16 aggregate ÷ baseline), same TTFT guard.</item>
/// </list>
/// Guardrails apply to every goal: a valid tool call, and an objective-question score at least the baseline's.
/// A candidate failing any of them is rejected regardless of speed.
/// Pooling (embedding) models are scored on the embeddings benchmark instead: input tok/s in place of decode tok/s
/// (N=1, N=16, or the geometric mean of both ratios), guarded by latency p95 at N=16 (at most 2× the baseline's),
/// valid vector shapes, and the same normalization as the baseline.
/// </summary>
public static class TuningScorer
{
    public const int SingleConcurrency = 1;
    public const int ManyConcurrency = 16;
    public const double TtftGuardFactor = 2.0;

    public static CandidateEvaluation Evaluate(TuningGoal goal, BenchmarkResult baseline, BenchmarkResult candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);

        if (WorkloadProfiles.IsPreset(goal))
        {
            return WorkloadScorer.Evaluate(WorkloadProfiles.For(goal), baseline, candidate);
        }

        if (baseline.Embeddings is not null || candidate.Embeddings is not null)
        {
            return EvaluatePooling(goal, baseline, candidate);
        }

        var g = candidate.Guardrails;
        if (!g.ToolCallValid)
        {
            return new CandidateEvaluation(false, Score(goal, baseline, candidate), $"Tool-call check failed: {g.ToolCallDetail}");
        }

        if (g.QaCorrect < baseline.Guardrails.QaCorrect)
        {
            return new CandidateEvaluation(false, Score(goal, baseline, candidate), string.Create(CultureInfo.InvariantCulture,
                $"Objective answers {g.QaCorrect}/{g.QaTotal}, below the baseline's {baseline.Guardrails.QaCorrect}/{baseline.Guardrails.QaTotal}"));
        }

        if (goal is TuningGoal.ManyAgents or TuningGoal.Balanced)
        {
            var baseMany = baseline.Level(ManyConcurrency);
            var candMany = candidate.Level(ManyConcurrency);
            if (baseMany is null || candMany is null)
            {
                return new CandidateEvaluation(false, null, "No N=16 measurement");
            }

            if (candMany.TtftP95 > TtftGuardFactor * baseMany.TtftP95)
            {
                return new CandidateEvaluation(false, Score(goal, baseline, candidate), string.Create(CultureInfo.InvariantCulture,
                    $"TTFT p95 at N=16 is {candMany.TtftP95:0.00} s, more than 2× the baseline's {baseMany.TtftP95:0.00} s"));
            }
        }

        double? score = Score(goal, baseline, candidate);
        return score is null
            ? new CandidateEvaluation(false, null, "Missing measurements for this goal")
            : new CandidateEvaluation(true, score, null);
    }

    private static CandidateEvaluation EvaluatePooling(TuningGoal goal, BenchmarkResult baseline, BenchmarkResult candidate)
    {
        if (baseline.Embeddings is not { } baseEmb || candidate.Embeddings is not { } candEmb)
        {
            return new CandidateEvaluation(false, null, "No embeddings measurement");
        }

        if (!candEmb.Check.Valid)
        {
            return new CandidateEvaluation(false, Score(goal, baseline, candidate), candEmb.Check.Detail);
        }

        if (baseEmb.Check.Valid && candEmb.Check.Normalized != baseEmb.Check.Normalized)
        {
            return new CandidateEvaluation(false, Score(goal, baseline, candidate),
                candEmb.Check.Normalized ? "Vectors are normalized, unlike the baseline's" : "Vectors are no longer normalized (the baseline's are)");
        }

        if (goal is TuningGoal.ManyAgents or TuningGoal.Balanced)
        {
            var baseMany = baseEmb.Level(ManyConcurrency);
            var candMany = candEmb.Level(ManyConcurrency);
            if (baseMany is null || candMany is null)
            {
                return new CandidateEvaluation(false, null, "No N=16 measurement");
            }

            if (candMany.LatencyP95 > TtftGuardFactor * baseMany.LatencyP95)
            {
                return new CandidateEvaluation(false, Score(goal, baseline, candidate), string.Create(CultureInfo.InvariantCulture,
                    $"Latency p95 at N=16 is {candMany.LatencyP95:0.00} s, more than 2× the baseline's {baseMany.LatencyP95:0.00} s"));
            }
        }

        double? score = Score(goal, baseline, candidate);
        return score is null
            ? new CandidateEvaluation(false, null, "Missing measurements for this goal")
            : new CandidateEvaluation(true, score, null);
    }

    /// <summary>The goal's raw score (higher is better), or null when a needed level is missing.</summary>
    public static double? Score(TuningGoal goal, BenchmarkResult baseline, BenchmarkResult candidate)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        if (WorkloadProfiles.IsPreset(goal))
        {
            return WorkloadScorer.Score(WorkloadProfiles.For(goal), baseline, candidate);
        }

        if (baseline.Embeddings is not null || candidate.Embeddings is not null)
        {
            return PoolingScore(goal, baseline.Embeddings, candidate.Embeddings);
        }

        var c1 = candidate.Level(SingleConcurrency);
        var c16 = candidate.Level(ManyConcurrency);
        switch (goal)
        {
            case TuningGoal.SingleUser:
                return c1?.PerRequestTokS;
            case TuningGoal.ManyAgents:
                return c16?.AggregateTokS;
            default:
                var b1 = baseline.Level(SingleConcurrency);
                var b16 = baseline.Level(ManyConcurrency);
                if (c1 is null || c16 is null || b1 is null || b16 is null || b1.PerRequestTokS <= 0 || b16.AggregateTokS <= 0)
                {
                    return null;
                }

                return Math.Sqrt(c1.PerRequestTokS / b1.PerRequestTokS * (c16.AggregateTokS / b16.AggregateTokS));
        }
    }

    /// <summary>Pooling score: input tok/s at N=1 (single user), at N=16 (many agents), or the geometric mean of both ratios.</summary>
    private static double? PoolingScore(TuningGoal goal, EmbeddingResult? baseline, EmbeddingResult? candidate)
    {
        var c1 = candidate?.Level(SingleConcurrency);
        var c16 = candidate?.Level(ManyConcurrency);
        switch (goal)
        {
            case TuningGoal.SingleUser:
                return c1?.InputTokS;
            case TuningGoal.ManyAgents:
                return c16?.InputTokS;
            default:
                var b1 = baseline?.Level(SingleConcurrency);
                var b16 = baseline?.Level(ManyConcurrency);
                if (c1 is null || c16 is null || b1 is null || b16 is null || b1.InputTokS <= 0 || b16.InputTokS <= 0)
                {
                    return null;
                }

                return Math.Sqrt(c1.InputTokS / b1.InputTokS * (c16.InputTokS / b16.InputTokS));
        }
    }

    /// <summary>
    /// The best accepted candidate: highest score; ties broken by lower TTFT p50 at N=1, then by run order
    /// (earlier wins, so the baseline wins a tie against anything).
    /// </summary>
    public static TuningCandidateResult? PickBest(IEnumerable<TuningCandidateResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates
            .Where(c => c.Outcome == CandidateOutcome.Accepted && c.Score is not null)
            .OrderByDescending(c => Math.Round(c.Score!.Value, 9))
            .ThenBy(c => c.Result?.Level(SingleConcurrency)?.TtftP50 ?? c.Result?.Embeddings?.Level(SingleConcurrency)?.LatencyP50 ?? double.MaxValue)
            .ThenBy(c => c.Index)
            .FirstOrDefault();
    }

    /// <summary>Percent gain of <paramref name="winner"/> over the baseline's score for the goal.</summary>
    public static double? GainPercent(double? winnerScore, double? baselineScore) =>
        winnerScore is { } w && baselineScore is { } b && b > 0 ? ((w / b) - 1) * 100 : null;
}
