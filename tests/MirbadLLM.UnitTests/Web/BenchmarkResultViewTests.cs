using Bunit;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Web.Components.Ui;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>bUnit tests for <see cref="BenchmarkResultView"/>'s density fix: the concurrency levels render
/// as one table (rows N = 1/4/8/16, columns Per request/Aggregate/TTFT p50/TTFT p95) instead of the old
/// two-column grid with a wide "Aggregate per run" column, so the table holds its own panel width.</summary>
public sealed class BenchmarkResultViewTests
{
    private static RunMeasurement MakeRun(int n, double perRequest, double aggregate) =>
        new(n, 256, 10, perRequest, aggregate, 0.4, 0.5, 0, n);

    private static LevelResult MakeLevel(int n, double perRequest, double aggregate) =>
        new(n, [MakeRun(n, perRequest, aggregate), MakeRun(n, perRequest, aggregate), MakeRun(n, perRequest, aggregate)],
            perRequest, aggregate, 0.4, 0.5, Noisy: false);

    private static BenchmarkResult MakeResult() => new(
        Levels: [MakeLevel(1, 15.7, 15.7), MakeLevel(4, 12.1, 48.4), MakeLevel(8, 11.0, 88.0), MakeLevel(16, 10.5, 159.0)],
        Prefill: new PrefillResult(65_443, 44.1, 44.1),
        Guardrails: new GuardrailResult(true, "get_time({\"city\": \"Riyadh\"})", 8, 8, []),
        Noise: NoiseReport.Quiet);

    [Fact]
    public void RendersOneRowPerConcurrencyLevel_WithTheFourDensifiedColumns()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<BenchmarkResultView>(p => p
            .Add(x => x.Result, MakeResult())
            .Add(x => x.TestId, "bench"));

        var table = cut.Find("[data-testid=bench-levels]");
        var rows = table.QuerySelectorAll("tbody tr");
        Assert.Equal(4, rows.Length);

        // Exactly the four data columns the fix calls for (plus the row's own N = concurrency label) —
        // no more "Aggregate per run" wide column. Each header's visible label is ColumnHelp's own
        // ".th-help > span" (the <th> itself also holds the popover's hidden-until-opened help text).
        var headers = table.QuerySelectorAll("thead th .th-help > span").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(["Concurrent", "Per request", "Aggregate", "TTFT p50", "TTFT p95"], headers);
        Assert.DoesNotContain(table.QuerySelectorAll("thead th").Select(h => h.TextContent), h => h.Contains("per run", StringComparison.OrdinalIgnoreCase));

        var firstRowLabels = rows[0].QuerySelectorAll("td").Select(td => td.GetAttribute("data-label")).ToList();
        Assert.Equal([null, "Per request", "Aggregate", "TTFT p50", "TTFT p95"], firstRowLabels);

        Assert.Contains("N = 1", rows[0].QuerySelector("td.col-primary")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("N = 16", rows[3].QuerySelector("td.col-primary")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("159", rows[3].QuerySelector("[data-label=Aggregate]")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void AThinkingResult_ShowsWhatThinkingCosts_AndNothingWithoutOne()
    {
        using var ctx = new Bunit.BunitContext();
        var withThinking = MakeResult() with { Thinking = new ThinkingResult(8, 7, 1, 0, 9.4, 8.1, 212, 24.5, 4096) };

        var cut = ctx.Render<BenchmarkResultView>(p => p.Add(x => x.Result, withThinking).Add(x => x.TestId, "bench"));

        string text = cut.Find("[data-testid=bench-thinking]").TextContent;
        Assert.Contains("7 / 8", text, StringComparison.Ordinal);
        Assert.Contains("212", text, StringComparison.Ordinal);
        Assert.Contains("24.5", text, StringComparison.Ordinal);
        Assert.Contains("still thinking at 4,096 tokens", text, StringComparison.Ordinal);

        var plain = ctx.Render<BenchmarkResultView>(p => p.Add(x => x.Result, MakeResult()).Add(x => x.TestId, "plain"));
        Assert.Empty(plain.FindAll("[data-testid=plain-thinking]"));
    }

    [Fact]
    public void BarListStillRenders_BelowTheTable_NotBesideIt()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<BenchmarkResultView>(p => p
            .Add(x => x.Result, MakeResult())
            .Add(x => x.TestId, "bench"));

        var bars = cut.Find("[data-testid=bench-bars]");
        Assert.NotNull(bars);

        // No leftover two-column density-grid wrapper around the table and the bars.
        Assert.Empty(cut.FindAll(".bench-grid"));
    }

    [Fact]
    public void LongPromptAndGuardrailSections_AreUnchanged()
    {
        using var ctx = new Bunit.BunitContext();

        var cut = ctx.Render<BenchmarkResultView>(p => p
            .Add(x => x.Result, MakeResult())
            .Add(x => x.TestId, "bench"));

        Assert.Contains("65,443", cut.Find("[data-testid=bench-prefill]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Valid", cut.Find("[data-testid=bench-tool]").TextContent, StringComparison.Ordinal);
        Assert.Contains("8 / 8", cut.Find("[data-testid=bench-qa]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaV2_ShowsOutputLengthsErrors_LongPrompts_SharedPrefix_AndToolCalls()
    {
        using var ctx = new Bunit.BunitContext();
        var result = MirbadLLM.UnitTests.Benchmarking.Fakes.Results.Workload(50, 400, errors: 2, toolsValid: 2, long64: 9.5,
            preset: TuningGoal.AgentsTools);

        var cut = ctx.Render<BenchmarkResultView>(p => p.Add(x => x.Result, result).Add(x => x.TestId, "bench"));

        var headers = cut.FindAll("[data-testid=bench-levels] thead th").Select(th => th.TextContent).ToList();
        Assert.Contains(headers, h => h.Contains("Output", StringComparison.Ordinal));
        Assert.Contains(headers, h => h.Contains("Errors", StringComparison.Ordinal));
        Assert.Contains("2 failed", cut.Find("[data-testid=bench-levels]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Agents & tools", cut.Find("[data-testid=bench-preset]").TextContent, StringComparison.Ordinal);
        Assert.Contains("16,384", cut.Find("[data-testid=bench-long]").TextContent, StringComparison.Ordinal);
        Assert.Contains("65,536", cut.Find("[data-testid=bench-long]").TextContent, StringComparison.Ordinal);
        Assert.Contains("shared prefix (cache reuse observed)", cut.Find("[data-testid=bench-shared]").TextContent, StringComparison.Ordinal);
        Assert.Contains("2 / 3", cut.Find("[data-testid=bench-tool]").TextContent, StringComparison.Ordinal);
        Assert.Contains("$.city is required but missing", cut.Find("[data-testid=bench-tool]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=bench-prefill]")); // the v1 single long prompt row
    }

    [Fact]
    public void SchemaV1_KeepsTheClassicLayout()
    {
        using var ctx = new Bunit.BunitContext();
        var cut = ctx.Render<BenchmarkResultView>(p => p.Add(x => x.Result, MakeResult()).Add(x => x.TestId, "bench"));

        Assert.DoesNotContain(cut.FindAll("[data-testid=bench-levels] thead th"), th => th.TextContent.Contains("Output", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid=bench-shared]"));
        Assert.NotNull(cut.Find("[data-testid=bench-prefill]"));
    }

    [Fact]
    public void Compare_V2_HasRowsPerOutputLength_LongPrompts_AndSharedPrefix()
    {
        var a = MirbadLLM.UnitTests.Benchmarking.Fakes.Results.Workload(50, 400, long16: 2.0) with
        {
            Levels = [.. MirbadLLM.UnitTests.Benchmarking.Fakes.Results.Workload(50, 400).Levels,
                MirbadLLM.UnitTests.Benchmarking.Fakes.Results.Workload(40, 300).Levels[0] with { OutputTokens = 1024 }],
        };
        var b = MirbadLLM.UnitTests.Benchmarking.Fakes.Results.Workload(60, 480, long16: 1.5);

        var rows = BenchmarkCompare.BuildRows(a, b);

        Assert.Contains(rows, r => r.Key == "n1-per-request" && r.B == 60);
        Assert.Contains(rows, r => r.Key == "t1024-n1-aggregate" && r.A == 40 && r.B is null);
        Assert.Contains(rows, r => r.Key == "long-16384" && r.A == 2.0 && r.B == 1.5 && !r.HigherIsBetter);
        Assert.Contains(rows, r => r.Key == "shared-prefix");
        Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());
    }
}
