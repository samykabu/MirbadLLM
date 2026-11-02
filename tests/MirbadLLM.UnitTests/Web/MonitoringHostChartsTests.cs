using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Widgets;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// Monitoring's host-level per-model charts: memory stacked by model (budget while running) over other processes and
/// free memory, and tokens per second per model with gaps (never zeros) when a model isn't measured; one colour per
/// model across both, stable by model id.
/// </summary>
public sealed class MonitoringHostChartsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static ModelKpis Kpis(double generated, double prompt = 0) => new(
        new ModelMetrics(0, 0, 0, generated, prompt), 0, null, null, null, null, null, null, 0, new Dictionary<string, double>(), 0);

    private static HostSample Host(int seconds, double total, double available, Dictionary<string, double>? models) =>
        new(At(seconds), new MemorySnapshot(total, available), null) { ModelMemoryGiB = models };

    // ---------------------------------------------------------------- colours

    [Fact]
    public void Colors_FollowTheModelId_NotWhichModelsRun()
    {
        var all = ModelColors.Assign(["zeta", "alpha", "mid"]);
        Assert.Equal("var(--ss-cat-1)", all["alpha"]);
        Assert.Equal("var(--ss-cat-2)", all["mid"]);
        Assert.Equal("var(--ss-cat-3)", all["zeta"]);

        // The same registry in another order gives the same mapping.
        Assert.Equal(all, ModelColors.Assign(["mid", "zeta", "alpha", "alpha"]));

        // A ninth model folds into the neutral tone rather than a generated hue.
        var nine = ModelColors.Assign(Enumerable.Range(1, 9).Select(i => $"m{i}"));
        Assert.Equal("var(--ss-cat-8)", nine["m8"]);
        Assert.Equal("var(--ss-series-other)", nine["m9"]);
    }

    // ---------------------------------------------------------------- memory

    [Fact]
    public void Memory_StacksModelBudgets_ThenOtherProcesses_ThenFree()
    {
        var colors = ModelColors.Assign(["big", "small"]);
        IReadOnlyList<HostSample> host =
        [
            Host(0, 121, 10, new() { ["big"] = 48, ["small"] = 8 }),
            Host(5, 121, 30, new() { ["big"] = 48 }),
            Host(10, 121, 100, null),
        ];

        var series = MonitoringSeries.Memory(host, colors, id => id.ToUpperInvariant());

        Assert.Equal(["BIG", "SMALL", MonitoringSeries.OtherProcesses, MonitoringSeries.Free], series.Select(s => s.Name));
        Assert.Equal([48, 48, null], series[0].Points.Select(p => p.Value));
        Assert.Equal([8, null, null], series[1].Points.Select(p => p.Value)); // a gap while it isn't running
        Assert.Equal([55, 43, 21], series[2].Points.Select(p => p.Value)); // used − budgets
        Assert.Equal([10, 30, 100], series[3].Points.Select(p => p.Value));
        Assert.Equal("var(--ss-cat-1)", series[0].Color);
        Assert.Equal("var(--ss-cat-2)", series[1].Color);

        // Each sample's stack reaches the host total.
        for (int i = 0; i < host.Count; i++)
        {
            Assert.Equal(121, series.Sum(s => s.Points[i].Value ?? 0), 6);
        }
    }

    [Fact]
    public void Memory_Measured_NamesHostServices_BetweenTheModelsAndOtherProcesses()
    {
        var colors = ModelColors.Assign(["qwen"], ["ComfyUI"]);
        Assert.Equal("var(--ss-cat-2)", colors[ModelColors.OtherPrefix + "ComfyUI"]); // after the models' slots
        IReadOnlyList<HostSample> host =
        [
            Host(0, 121, 10, new() { ["qwen"] = 56.7 }) with { MemorySource = ModelMemorySource.Measured, OtherGpuGiB = new Dictionary<string, double> { ["ComfyUI"] = 12 } },
        ];

        var series = MonitoringSeries.Memory(host, colors, id => id);

        Assert.Equal(["qwen", "ComfyUI", MonitoringSeries.OtherProcesses, MonitoringSeries.Free], series.Select(s => s.Name));
        Assert.Equal(12, series[1].Points[0].Value);
        Assert.Equal("var(--ss-cat-2)", series[1].Color);
        Assert.Equal(121 - 10 - 56.7 - 12, series[2].Points[0].Value!.Value, 6);
        Assert.Contains("measured by nvidia-smi", MonitoringSeries.MemorySourceNote(host), StringComparison.Ordinal);
    }

    [Fact]
    public void MemorySourceNote_SaysBudget_OrMixed()
    {
        var budget = Host(0, 121, 10, new() { ["m"] = 48 });
        var measured = Host(5, 121, 10, new() { ["m"] = 50 }) with { MemorySource = ModelMemorySource.Measured };

        Assert.Contains("configured memory budget", MonitoringSeries.MemorySourceNote([budget]), StringComparison.Ordinal);
        Assert.Contains("where the host agent reported it", MonitoringSeries.MemorySourceNote([budget, measured]), StringComparison.Ordinal);
        Assert.Null(MonitoringSeries.MemorySourceNote([]));
    }

    [Fact]
    public void Memory_OtherProcessesNeverGoesNegative_WhileAModelIsStillLoading()
    {
        var series = MonitoringSeries.Memory([Host(0, 121, 100, new() { ["m"] = 48 })], ModelColors.Assign(["m"]), id => id);

        Assert.Equal(0, series.Single(s => s.Name == MonitoringSeries.OtherProcesses).Points[0].Value);
    }

    // ---------------------------------------------------------------- throughput

    [Fact]
    public void Throughput_OneSeriesPerModel_GapsNotZeros_PoolingShowsInput_TotalSumsGeneration()
    {
        var samples = new Dictionary<string, IReadOnlyList<ModelSample>>
        {
            ["chat-a"] =
            [
                new ModelSample(At(0), Kpis(0)) { HasRates = false },
                new ModelSample(At(5), Kpis(40)),
                new ModelSample(At(10), Kpis(60)),
                // Not Ready from 10 s to 40 s: no samples.
                new ModelSample(At(40), Kpis(0)) { HasRates = false },
                new ModelSample(At(45), Kpis(30)),
            ],
            ["chat-b"] = [new ModelSample(At(5), Kpis(10)), new ModelSample(At(10), Kpis(5))],
            ["embed"] = [new ModelSample(At(5), Kpis(0, prompt: 900))],
            ["stopped"] = [],
        };
        var colors = ModelColors.Assign(["chat-a", "chat-b", "embed", "stopped"]);

        var series = MonitoringSeries.Throughput(samples, colors, id => id, id => id == "embed", TimeSpan.FromSeconds(12.5));

        Assert.Equal(["chat-a", "chat-b", "embed (input)", MonitoringSeries.Total], series.Select(s => s.Name));
        var a = series[0].Points;
        Assert.Equal([null, 40, 60, null, null, 30], a.Select(p => p.Value)); // first-after-start and the downtime are gaps
        Assert.Equal(At(25), a[3].Time); // the inserted break sits inside the gap
        Assert.Equal([900.0], series[2].Points.Select(p => p.Value)); // input tokens for the embedding model
        Assert.Equal("var(--ss-cat-3)", series[2].Color);

        var total = series[3];
        Assert.Equal(LineStyle.Dotted, total.Style);
        Assert.Equal([null, 50, 65, null, null, 30], total.Points.Select(p => p.Value)); // embedding input not added
    }

    [Fact]
    public void Throughput_OneGenerationModel_HasNoTotalLine()
    {
        var samples = new Dictionary<string, IReadOnlyList<ModelSample>> { ["m"] = [new ModelSample(At(0), Kpis(12))] };

        var series = MonitoringSeries.Throughput(samples, ModelColors.Assign(["m"]), id => id, _ => false, TimeSpan.FromSeconds(12.5));

        Assert.Equal(["m"], series.Select(s => s.Name));
    }

    // ---------------------------------------------------------------- chart component

    [Fact]
    public void StackedChart_DrawsAnAreaPerSeries_SharesTheTimeAxis_AndHasATableAndHoverValues()
    {
        using var ctx = new BunitContext();
        IReadOnlyList<LineChartSeries> series =
        [
            new("Model A", "var(--ss-cat-1)", [new LineChartPoint(At(5), 10), new LineChartPoint(At(10), 10)]),
            new("Free", "var(--ss-series-reserve)", [new LineChartPoint(At(0), 30), new LineChartPoint(At(5), 20), new LineChartPoint(At(10), 20)]),
        ];

        var cut = ctx.Render<LineChart>(p => p.Add(x => x.Series, series).Add(x => x.Title, "Mem").Add(x => x.Unit, "GiB").Add(x => x.Stacked, true));

        var areaA = cut.Find("[data-testid=line-chart-mem-area-model-a]").GetAttribute("d")!;
        // Model A starts at 5 s of a 0–10 s axis: half way, not at the left edge.
        Assert.StartsWith("M50,", areaA, StringComparison.Ordinal);
        Assert.EndsWith("Z", areaA, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=line-chart-mem-area-free]"));
        Assert.Equal("max 30", cut.Find("[data-testid=line-chart-mem-max]").TextContent); // the stack's top
        Assert.Equal("min 0", cut.Find("[data-testid=line-chart-mem-min]").TextContent);

        var rows = cut.FindAll("[data-testid=line-chart-mem-table] tbody tr");
        Assert.Equal(2, rows.Count);
        Assert.Contains("10 GiB", rows[0].TextContent, StringComparison.Ordinal);
        var titles = cut.FindAll("rect.chart-hover title").Select(t => t.TextContent).ToList();
        Assert.Equal(3, titles.Count);
        Assert.Equal("12:00:00 UTC · Model A — · Free 30 GiB", titles[0]);
        Assert.Contains("Model A 10 GiB · Free 20 GiB", titles[2], StringComparison.Ordinal);
        Assert.Contains("10 GiB", cut.Find("[data-testid=line-chart-mem-legend]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Chart_Help_IsAnInfoButtonWithAPopover()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<LineChart>(p => p
            .Add(x => x.Series, [new LineChartSeries("A", "red", [new LineChartPoint(At(0), 1)])])
            .Add(x => x.Title, "GPU utilization")
            .Add(x => x.Help, "Not per model on the GB10."));

        var button = cut.Find("[data-testid=line-chart-gpu-utilization-help]");
        var panel = cut.Find("[data-testid=line-chart-gpu-utilization-help-panel]");
        Assert.Equal(panel.Id, button.GetAttribute("popovertarget"));
        Assert.Contains("Not per model", panel.TextContent, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- page

    [Fact]
    public void Overview_RendersMemoryByModel_AndTheSignal_WithTheSameColourPerModel()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var chat = new ModelDefinition { Id = "qwen36", ServedName = "Qwen3.6-35B", ModelPath = "p", MemoryBudgetGiB = 48 };
        var embed = new ModelDefinition { Id = "embed-4b", ServedName = "Qwen3-Embedding-4B", ModelPath = "p", MemoryBudgetGiB = 10, ExtraArgs = ["--runner", "pooling"] };
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry([chat, embed]));
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        ctx.Services.AddSingleton<IRouterStats>(new FakeRouterStats([]));
        var history = new FakeMetricsHistory
        {
            HostSamples =
            [
                Host(0, 121, 20, new() { ["qwen36"] = 48, ["embed-4b"] = 10 }),
                Host(5, 121, 18, new() { ["qwen36"] = 48, ["embed-4b"] = 10 }),
            ],
            LatestKpis = new Dictionary<string, ModelKpis> { ["qwen36"] = Kpis(80), ["embed-4b"] = Kpis(0, 500) },
        };
        history.ModelSamplesById["qwen36"] = [new ModelSample(At(0), Kpis(70)), new ModelSample(At(5), Kpis(80))];
        history.ModelSamplesById["embed-4b"] = [new ModelSample(At(5), Kpis(0, 500))];
        ctx.Services.AddSingleton<IMetricsHistory>(history);

        var cut = ctx.Render<Dashboard>();

        var memory = cut.Find("[data-testid=line-chart-memory-by-model]");
        Assert.Contains("Memory by model (GiB)", memory.QuerySelector(".chart-title")!.TextContent, StringComparison.Ordinal);
        string memoryLegend = memory.QuerySelector("[data-testid=line-chart-memory-by-model-legend]")!.TextContent;
        Assert.Contains("Qwen3.6-35B", memoryLegend, StringComparison.Ordinal);
        Assert.Contains("48 GiB", memoryLegend, StringComparison.Ordinal);
        Assert.Contains("Other processes", memoryLegend, StringComparison.Ordinal);
        Assert.Contains("45 GiB", memoryLegend, StringComparison.Ordinal); // 121 − 18 − 58
        Assert.Contains("Free", memoryLegend, StringComparison.Ordinal);
        Assert.Contains("budget", cut.Find("[data-testid=line-chart-memory-by-model-help-panel]").TextContent, StringComparison.Ordinal);
        // These samples carry no measurement, so the chart says it shows budgets.
        Assert.Contains("configured memory budget", cut.Find("[data-testid=line-chart-memory-by-model-note]").TextContent, StringComparison.Ordinal);

        var signal = cut.Find("[data-testid=signal]");
        string signalLegend = cut.Find("[data-testid=signal-legend]").TextContent;
        Assert.Contains("Qwen3.6-35B", signalLegend, StringComparison.Ordinal);
        // The canvas is decoration; its numbers are text in a table: now 80, average 75, peak 80.
        Assert.Equal("true", cut.Find("[data-testid=signal-canvas]").GetAttribute("aria-hidden"));
        var row = cut.FindAll("[data-testid=signal-table] tbody tr").First(r => r.TextContent.Contains("Qwen3.6-35B", StringComparison.Ordinal));
        Assert.Equal(["80", "75", "80"], row.QuerySelectorAll("td").Select(td => td.TextContent));
        // The signal stacks generation only: the embedding model shows its input rate in the table, marked "in".
        Assert.DoesNotContain("Qwen3-Embedding-4B", signalLegend, StringComparison.Ordinal);
        Assert.Equal("80", cut.Find("[data-testid=pulse-total]").TextContent.Trim());
        var embedRate = cut.Find("[data-testid=model-tps-embed-4b]");
        Assert.Equal("500in", embedRate.TextContent.Replace(" ", "", StringComparison.Ordinal).Trim());

        // GPU stays host-level and says why.
        Assert.Contains("per-process GPU utilization", cut.Find("[data-testid=line-chart-gpu-utilization-help-panel]").TextContent, StringComparison.Ordinal);

        // One colour per model in both charts and the table swatch (embed-4b sorts first).
        string Swatch(AngleSharp.Dom.IElement chart, string name) => chart.QuerySelectorAll(".legend-item")
            .First(li => li.TextContent.Contains(name, StringComparison.Ordinal)).QuerySelector(".swatch")!.GetAttribute("style")!;
        Assert.Equal("color:var(--ss-cat-2)", Swatch(memory, "Qwen3.6-35B"));
        Assert.Equal("background:var(--ss-cat-2)", signal.QuerySelector(".signal-legend .signal-swatch")!.GetAttribute("style"));
        Assert.Equal("color:var(--ss-cat-1)", Swatch(memory, "Qwen3-Embedding-4B"));
        Assert.Equal("background:var(--ss-cat-1)", cut.Find("[data-testid=model-row-embed-4b] .sw").GetAttribute("style"));
    }
}
