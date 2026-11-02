using System.Text.Json;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>Workload presets (R2-5): weighted, renormalized scores; SLO guards; invalid metrics; ranges; the 5 % rule; the schema validator.</summary>
public sealed class WorkloadScoringTests
{
    private static readonly WorkloadProfile Interactive = WorkloadProfiles.For(TuningGoal.InteractiveChat);
    private static readonly WorkloadProfile Agents = WorkloadProfiles.For(TuningGoal.AgentsTools);

    [Fact]
    public void Score_IsTheWeightedGeometricMeanOfRatios_LatenciesInverted()
    {
        var baseline = Results.Workload(n1: 50, n16: 400, long16: 2.0);
        var candidate = Results.Workload(n1: 60, n16: 400, long16: 1.6);

        // Interactive: N1 0.5, N16 0.2, 16K TTFT 0.2, mixed short TTFT 0.1 → exp(0.5 ln 1.2 + 0.2 ln 1 + 0.2 ln (2/1.6) + 0.1 ln 1).
        double expected = Math.Exp((0.5 * Math.Log(1.2)) + (0.2 * Math.Log(2.0 / 1.6)));
        Assert.Equal(expected, WorkloadScorer.Score(Interactive, baseline, candidate)!.Value, 9);
        Assert.Equal(1.0, WorkloadScorer.Score(Interactive, baseline, baseline)!.Value, 9);
    }

    [Fact]
    public void MixedShortTtft_ScoresLowerAsBetter_OnlyForPresetsThatWeighIt()
    {
        var baseline = Results.Workload(50, 400, mixed: 1.0);
        var fasterUnderLoad = Results.Workload(50, 400, mixed: 0.5);

        // Interactive: only the mixed component differs (weight 0.1 of 1.0) → exp(0.1 ln 2).
        Assert.Equal(Math.Exp(0.1 * Math.Log(2.0)), WorkloadScorer.Score(Interactive, baseline, fasterUnderLoad)!.Value, 9);
        Assert.Equal(1.0, WorkloadScorer.Score(WorkloadProfiles.For(TuningGoal.Throughput), baseline, fasterUnderLoad)!.Value, 9);

        // Per repetition too (the confirmation stage's range).
        Assert.NotNull(WorkloadScorer.Range(Interactive, baseline, fasterUnderLoad));
    }

    [Fact]
    public void MixedShortTtftGuard_RejectsASlowFirstTokenUnderALongRequest_AndRelaxesForABaselineThatBreaksIt()
    {
        var baseline = Results.Workload(50, 400);
        var slow = Results.Workload(90, 900, mixed: 3.5);

        var verdict = WorkloadScorer.Evaluate(Interactive, baseline, slow);
        Assert.False(verdict.Accepted);
        Assert.Contains("under a long request is 3.50 s, above the 3 s limit", verdict.Reason, StringComparison.Ordinal);
        Assert.True(WorkloadScorer.Evaluate(WorkloadProfiles.For(TuningGoal.Throughput), baseline, slow).Accepted); // guard off there

        var slowBaseline = Results.Workload(50, 400, mixed: 4.0);
        var (guards, notes) = WorkloadScorer.RelaxForBaseline(Interactive.Guards, slowBaseline);
        Assert.Equal(4.0, guards.MixedShortTtftP95Seconds);
        Assert.Contains(notes, n => n.Contains("under a long request is 4.00 s", StringComparison.Ordinal));
        Assert.True(WorkloadScorer.Evaluate(Interactive, slowBaseline, slow).Accepted);
    }

    [Fact]
    public void PreemptionGuard_FailsAboveZero_SkipsWhenUnread_AndRelaxesForABaselineThatPreempts()
    {
        var baseline = Results.Workload(50, 400);

        var preempting = WorkloadScorer.Evaluate(Interactive, baseline, Results.Workload(90, 900, preemptions: 2));
        Assert.False(preempting.Accepted);
        Assert.Equal("KV pool too small for this workload: 2 preemptions", preempting.Reason);

        Assert.True(WorkloadScorer.Evaluate(Interactive, baseline, Results.Workload(90, 900, preemptions: null)).Accepted); // /metrics unreadable: skipped
        var off = WorkloadProfiles.For(TuningGoal.InteractiveChat, new SloGuards(MaxPreemptions: null));
        Assert.True(WorkloadScorer.Evaluate(off, baseline, Results.Workload(90, 900, preemptions: 2)).Accepted);
        // Throughput hunts for the highest max-seqs that still pays off, which is where preemptions happen: its guard is off.
        Assert.True(WorkloadScorer.Evaluate(WorkloadProfiles.For(TuningGoal.Throughput), baseline, Results.Workload(90, 900, preemptions: 2)).Accepted);

        var preemptingBaseline = Results.Workload(50, 400, preemptions: 3);
        var (guards, notes) = WorkloadScorer.RelaxForBaseline(Interactive.Guards, preemptingBaseline);
        Assert.Equal(3, guards.MaxPreemptions);
        Assert.Contains(notes, n => n.Contains("3 preemptions, limit 0", StringComparison.Ordinal));
        Assert.True(WorkloadScorer.Evaluate(Interactive, preemptingBaseline, Results.Workload(90, 900, preemptions: 2)).Accepted);
        Assert.False(WorkloadScorer.Evaluate(Interactive, preemptingBaseline, Results.Workload(90, 900, preemptions: 4)).Accepted);
    }

    [Fact]
    public void Weights_AreRenormalizedOverTheComponentsMeasuredOnBothSides()
    {
        // No long prompt or mixed workload on either side (e.g. the context is too short): only N1 (0.5) and N16 (0.2) count.
        var baseline = Results.Workload(50, 400) with { LongPrompts = [], Mixed = null };
        var candidate = Results.Workload(60, 480) with { LongPrompts = [], Mixed = null };

        Assert.Equal(1.2, WorkloadScorer.Score(Interactive, baseline, candidate)!.Value, 9);

        // Throughput's max-seqs component is inactive when no level above 16 exists: N16 alone decides.
        var throughput = WorkloadProfiles.For(TuningGoal.Throughput);
        Assert.Equal(1.2, WorkloadScorer.Score(throughput, baseline, candidate)!.Value, 9);
        Assert.Equal(Math.Exp((0.4 * Math.Log(1.2)) + (0.6 * Math.Log(2.0))),
            WorkloadScorer.Score(throughput, Results.Workload(50, 400, nMax: 500), Results.Workload(60, 480, nMax: 1000))!.Value, 9);
    }

    [Fact]
    public void SharedPrefixTtft_CountsOnlyForPresetsThatDeclareSharedPrefixes()
    {
        var baseline = Results.Workload(50, 400, shared: 0.4);
        var slowerShared = Results.Workload(50, 400, shared: 0.8);

        Assert.Equal(1.0, WorkloadScorer.Score(Interactive, baseline, slowerShared)!.Value, 9);
        Assert.True(WorkloadScorer.Score(Agents, baseline, slowerShared) < 1);
    }

    [Theory]
    [InlineData("ttft1", "TTFT p95 at N=1")]
    [InlineData("ttft16", "TTFT p95 at N=16")]
    [InlineData("long", "16K-token prompt TTFT")]
    [InlineData("errors", "timed requests failed")]
    [InlineData("qa", "Objective answers")]
    public void Guards_RejectRegardlessOfSpeed(string guard, string reason)
    {
        var baseline = Results.Workload(50, 400, long16: 2.0);
        var candidate = guard switch
        {
            "ttft1" => Results.Workload(90, 900, ttftP95At1: 1.4),
            "ttft16" => Results.Workload(90, 900, ttftP95At16: 3.5),
            "long" => Results.Workload(90, 900, long16: 2.6),
            "errors" => Results.Workload(90, 900, errors: 1),
            _ => Results.Workload(90, 900, qa: 7),
        };

        var verdict = WorkloadScorer.Evaluate(Interactive, baseline, candidate);

        Assert.False(verdict.Accepted);
        Assert.Contains(reason, verdict.Reason, StringComparison.Ordinal);
        Assert.True(verdict.Score > 1); // fast, but rejected
    }

    [Fact]
    public void LongPromptGuard_AllowsUpTo25PercentSlower()
    {
        var baseline = Results.Workload(50, 400, long16: 2.0);
        Assert.True(WorkloadScorer.Evaluate(Interactive, baseline, Results.Workload(80, 600, long16: 2.49)).Accepted);
        Assert.False(WorkloadScorer.Evaluate(Interactive, baseline, Results.Workload(80, 600, long16: 2.51)).Accepted);
    }

    [Fact]
    public void ToolValidity_IsGuarded_OnlyWhereToolsAreUsed()
    {
        var baseline = Results.Workload(50, 400);
        var brokenTools = Results.Workload(60, 480, toolsValid: 2);

        Assert.True(WorkloadScorer.Evaluate(Interactive, baseline, brokenTools).Accepted);
        var agents = WorkloadScorer.Evaluate(Agents, baseline, brokenTools);
        Assert.False(agents.Accepted);
        Assert.Contains("Tool calls 2/3 valid", agents.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Guards_AreConfigurablePerGoal()
    {
        var relaxed = WorkloadProfiles.For(TuningGoal.InteractiveChat, new SloGuards(ShortTtftP95AtN1Seconds: 2.0));
        var candidate = Results.Workload(90, 900, ttftP95At1: 1.4);

        Assert.False(WorkloadScorer.Evaluate(Interactive, Results.Workload(50, 400), candidate).Accepted);
        Assert.True(WorkloadScorer.Evaluate(relaxed, Results.Workload(50, 400), candidate).Accepted);
    }

    [Fact]
    public void NonFiniteOrZeroMeasurements_Reject()
    {
        var baseline = Results.Workload(50, 400);
        var broken = Results.Workload(50, 400);
        broken = broken with { Levels = [broken.Levels[0] with { PerRequestTokS = double.NaN }, broken.Levels[1]] };

        var verdict = WorkloadScorer.Evaluate(Interactive, baseline, broken);
        Assert.False(verdict.Accepted);
        Assert.StartsWith("Invalid measurement: N=1 tok/s", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Range_IsThePerRepetitionScores_AndTheFivePercentRuleNeedsNonOverlap()
    {
        // Throughput without a max-seqs level: N=16 aggregate alone decides, so the ranges are easy to read.
        var throughput = WorkloadProfiles.For(TuningGoal.Throughput);
        var baseline = Results.Workload(50, 400, spread: 0.02);
        var clear = Results.Workload(60, 480, spread: 0.01);
        var overlapping = Results.Workload(53, 424, spread: 0.06);

        var baseRange = WorkloadScorer.Range(throughput, baseline, baseline)!.Value;
        Assert.Equal(0.98, baseRange.Min, 9);
        Assert.Equal(1.02, baseRange.Max, 9);

        var clearRange = WorkloadScorer.Range(throughput, baseline, clear)!.Value;
        Assert.Equal(1.2 * 0.99, clearRange.Min, 9);
        Assert.True(WorkloadScorer.BeatsBaseline(WorkloadScorer.Score(throughput, baseline, clear), clearRange, 1.0, baseRange));

        double overlapScore = WorkloadScorer.Score(throughput, baseline, overlapping)!.Value;
        Assert.True(overlapScore >= 1.05); // fast enough on the median …
        Assert.False(WorkloadScorer.BeatsBaseline(overlapScore, WorkloadScorer.Range(throughput, baseline, overlapping), 1.0, baseRange)); // … but the ranges overlap

        Assert.False(WorkloadScorer.BeatsBaseline(1.04, (1.035, 1.045), 1.0, (0.99, 1.01))); // under 5 %
    }

    [Fact]
    public void TuningScorer_DispatchesPresetsToTheWorkloadScorer()
    {
        var baseline = Results.Workload(50, 400);
        var candidate = Results.Workload(60, 480);
        Assert.Equal(WorkloadScorer.Score(Interactive, baseline, candidate), TuningScorer.Evaluate(TuningGoal.InteractiveChat, baseline, candidate).Score);
    }

    [Fact]
    public void PresetFor_MapsClassicGoalsAndPoolingModels()
    {
        Assert.Equal(TuningGoal.Embeddings, WorkloadProfiles.PresetFor(TuningGoal.InteractiveChat, pooling: true));
        Assert.Equal(TuningGoal.InteractiveChat, WorkloadProfiles.PresetFor(TuningGoal.Embeddings, pooling: false));
        Assert.Equal(TuningGoal.Throughput, WorkloadProfiles.PresetFor(TuningGoal.ManyAgents, pooling: false));
        Assert.Equal(TuningGoal.AgentsTools, WorkloadProfiles.PresetFor(TuningGoal.AgentsTools, pooling: false));
        Assert.All(WorkloadProfiles.Presets, p => Assert.True(WorkloadProfiles.IsPreset(p)));
        Assert.False(WorkloadProfiles.IsPreset(TuningGoal.Balanced));
    }

    // ------------------------------------------------------------ schema validator

    [Theory]
    [InlineData("""{"city":"Riyadh"}""", null)]
    [InlineData("""{}""", "$.city is required but missing")]
    [InlineData("""{"city":3}""", "$.city should be string, got number 3")]
    [InlineData("""[]""", "$ should be object, got array")]
    public void Validator_RequiredAndTypes(string args, string? error)
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""");
        using var value = JsonDocument.Parse(args);
        Assert.Equal(error, JsonSchemaValidator.Validate(schema.RootElement, value.RootElement));
    }

    [Theory]
    [InlineData("""{"unit":"kelvin"}""", "$.unit is \"kelvin\", not one of")]
    [InlineData("""{"n":2.5}""", "$.n should be integer")]
    [InlineData("""{"n":2.0,"tags":["a",1]}""", "$.tags[1] should be string")]
    [InlineData("""{"extra":true}""", "$.extra is not allowed")]
    [InlineData("""{"n":2,"tags":["a"],"unit":"celsius"}""", null)]
    public void Validator_EnumIntegerItemsAndClosedObjects(string args, string? error)
    {
        using var schema = JsonDocument.Parse("""
            {"type":"object","additionalProperties":false,"properties":{
              "unit":{"type":"string","enum":["celsius","fahrenheit"]},
              "n":{"type":"integer"},
              "tags":{"type":"array","items":{"type":"string"}}}}
            """);
        using var value = JsonDocument.Parse(args);
        string? actual = JsonSchemaValidator.Validate(schema.RootElement, value.RootElement);
        if (error is null)
        {
            Assert.Null(actual);
        }
        else
        {
            Assert.StartsWith(error, actual, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Validator_SchemaWithoutProperties_AcceptsAnyObject()
    {
        using var value = JsonDocument.Parse("""{"anything":[1,2]}""");
        Assert.Null(JsonSchemaValidator.Validate(WorkloadPrompts.ToolSchema("lookup"), value.RootElement));
    }
}
