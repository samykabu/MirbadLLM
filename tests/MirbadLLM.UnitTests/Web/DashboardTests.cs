using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>bUnit tests for the Dashboard page, using the same hand-written fakes as <see cref="AdminApiTests"/>.</summary>
public sealed class DashboardTests
{
    private static ModelDefinition MakeModel(string id, double budget = 36) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = "checkpoint",
        MemoryBudgetGiB = budget,
    };

    private static void RegisterCoreFakes(
        Bunit.BunitContext ctx,
        FakeModelRegistry registry,
        FakeModelRuntime runtime,
        MemorySnapshot? memory = null,
        IMetricsHistory? history = null)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(registry);
        ctx.Services.AddSingleton<IModelRuntime>(runtime);
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(memory ?? new MemorySnapshot(128, 90)));
        ctx.Services.AddSingleton<IModelMetricsReader>(new FakeModelMetricsReader());
        ctx.Services.AddSingleton<IRouterStats>(new FakeRouterStats([]));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions { MemoryReserveGiB = 8 }));
        if (history is not null)
        {
            ctx.Services.AddSingleton(history);
        }
    }

    private static void AssertBadge(IRenderedComponent<Dashboard> cut, string id, string cssClass, string text)
    {
        var badge = cut.Find($"[data-testid=state-{id}]");
        Assert.Contains(cssClass, badge.GetAttribute("class"), StringComparison.Ordinal);
        Assert.Contains(text, badge.TextContent, StringComparison.Ordinal);
        Assert.NotNull(badge.QuerySelector("svg"));
    }

    [Fact]
    public void Overview_ListsNoModelCards_AndLinksToAllModels()
    {
        using var ctx = new Bunit.BunitContext();
        RegisterCoreFakes(ctx, new FakeModelRegistry([MakeModel("a"), MakeModel("b")]), new FakeModelRuntime());

        var cut = ctx.Render<Dashboard>();

        Assert.Empty(cut.FindAll("article.model-card"));
        Assert.Empty(cut.FindAll("[data-testid=start-a]"));
        Assert.Equal("models", cut.Find("[data-testid=all-models]").GetAttribute("href"));
        Assert.Equal("models/new", cut.Find("[data-testid=add-model]").GetAttribute("href"));
        Assert.NotNull(cut.Find("[data-tour=overview-headroom] [data-testid=memory-bar]"));
    }

    [Fact]
    public void NoModels_ShowsEmptyStateThatTeachesBothWaysToAdd()
    {
        using var ctx = new Bunit.BunitContext();
        RegisterCoreFakes(ctx, new FakeModelRegistry(), new FakeModelRuntime());

        var cut = ctx.Render<Dashboard>();

        var empty = cut.Find("[data-testid=dashboard-empty]");
        Assert.Contains("folder", empty.TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hugging Face", empty.TextContent, StringComparison.Ordinal);
        Assert.NotNull(empty.QuerySelector("a[href='models/new?source=download']"));
    }

    [Fact]
    public void KpiStrip_ShowsFleetNumbersFromHistory_AndDashesWhenNothingIsReady()
    {
        using var ctx = new Bunit.BunitContext();
        var ready = MakeModel("ready-model");
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(ready.Id, ModelState.Ready, HostPort: 8101));

        var now = DateTimeOffset.UtcNow;
        var kpis = new ModelKpis(
            Load: new ModelMetrics(2, 1, 0.25, GenerationTokensPerSecond: 54.2, PromptTokensPerSecond: 100),
            RequestsPerMinute: 6,
            TimeToFirstToken: new LatencyStats(0.2, 0.412),
            InterTokenLatency: null,
            EndToEndLatency: null,
            QueueTime: null,
            PrefixCacheHitRate: null,
            SpecDecodeAcceptanceRate: null,
            PreemptionsPerMinute: 0,
            FinishedByReasonPerMinute: new Dictionary<string, double>(),
            ToolCallsPerMinute: 0);
        var history = new FakeMetricsHistory
        {
            HostSamples =
            [
                new HostSample(now.AddSeconds(-5), new MemorySnapshot(128, 60), new GpuSnapshot(10, 40, 50, null)),
                new HostSample(now, new MemorySnapshot(128, 58), new GpuSnapshot(12, 41, 51, null)),
            ],
            LatestKpis = new Dictionary<string, ModelKpis> { [ready.Id] = kpis },
        };
        history.ModelSamplesById[ready.Id] = [new ModelSample(now.AddSeconds(-5), kpis), new ModelSample(now, kpis)];
        RegisterCoreFakes(ctx, new FakeModelRegistry([ready]), runtime, history: history);

        var cut = ctx.Render<Dashboard>();

        Assert.Equal("54.2", cut.Find("[data-testid=kpi-gen-value]").TextContent);
        Assert.Equal("6", cut.Find("[data-testid=kpi-req-value]").TextContent);
        Assert.Equal("412", cut.Find("[data-testid=kpi-ttft-value]").TextContent);
        Assert.Equal("ms", cut.Find("[data-testid=kpi-ttft-unit]").TextContent);
        Assert.Equal("12", cut.Find("[data-testid=kpi-gpu-value]").TextContent);
        // No routed requests yet: an honest dash, not 0%.
        Assert.Equal("—", cut.Find("[data-testid=kpi-errors-value]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=kpi-traffic]"));
    }

    [Fact]
    public void KpiStrip_KeepsTheVllmRequestRate_WhenALlamaCppModelReportsNone()
    {
        // MN-230: a llama.cpp model has no requests/min counter, so its KPI is NaN. The fleet tile summed every Ready
        // model, and one NaN made the sum NaN: the tile read "—" and hid the vLLM model's rate.
        using var ctx = new Bunit.BunitContext();
        var vllm = MakeModel("vllm-model");
        var llama = MakeModel("llamacpp-model") with { Runtime = RuntimeKind.LlamaCpp };
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(vllm.Id, ModelState.Ready, HostPort: 8101));
        runtime.SetStatus(new ModelStatus(llama.Id, ModelState.Ready, HostPort: 8102));

        var now = DateTimeOffset.UtcNow;
        var withRate = Kpis(12);
        var withoutRate = Kpis(double.NaN);
        var history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(now, new MemorySnapshot(128, 58), new GpuSnapshot(12, 41, 51, null))],
            LatestKpis = new Dictionary<string, ModelKpis> { [vllm.Id] = withRate, [llama.Id] = withoutRate },
        };
        history.ModelSamplesById[vllm.Id] = [new ModelSample(now, withRate)];
        history.ModelSamplesById[llama.Id] = [new ModelSample(now, withoutRate)];
        RegisterCoreFakes(ctx, new FakeModelRegistry([vllm, llama]), runtime, history: history);

        var cut = ctx.Render<Dashboard>();

        Assert.Equal("12", cut.Find("[data-testid=kpi-req-value]").TextContent);
        Assert.DoesNotContain("NaN", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void KpiStrip_KeepsTheVllmGenerationRate_WhenAnotherModelReportsNone()
    {
        // MN-234: the request tiles went through FiniteSum (MN-230) but the generation tiles still summed plainly, so a
        // runtime that leaves its load counters out would blank the fleet's tok/s and draw M12,NaN in its trend — the
        // same bug MN-230 fixed one tile over. llama.cpp does report tok/s today, which is why this was latent.
        using var ctx = new Bunit.BunitContext();
        var vllm = MakeModel("vllm-model");
        var other = MakeModel("no-load-model") with { Runtime = RuntimeKind.LlamaCpp };
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(vllm.Id, ModelState.Ready, HostPort: 8101));
        runtime.SetStatus(new ModelStatus(other.Id, ModelState.Ready, HostPort: 8102));

        var now = DateTimeOffset.UtcNow;
        var withLoad = Kpis(12);
        var withoutLoad = Kpis(12) with { Load = new ModelMetrics(0, 0, double.NaN, double.NaN, double.NaN) };
        var history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(now, new MemorySnapshot(128, 58), new GpuSnapshot(12, 41, 51, null))],
            LatestKpis = new Dictionary<string, ModelKpis> { [vllm.Id] = withLoad, [other.Id] = withoutLoad },
        };
        history.ModelSamplesById[vllm.Id] = [new ModelSample(now, withLoad)];
        history.ModelSamplesById[other.Id] = [new ModelSample(now, withoutLoad)];
        RegisterCoreFakes(ctx, new FakeModelRegistry([vllm, other]), runtime, history: history);

        var cut = ctx.Render<Dashboard>();

        Assert.Equal("54.2", cut.Find("[data-testid=kpi-gen-value]").TextContent);
        Assert.DoesNotContain("NaN", cut.Markup, StringComparison.Ordinal);
    }

    private static ModelKpis Kpis(double requestsPerMinute) => new(
        Load: new ModelMetrics(2, 1, 0.25, GenerationTokensPerSecond: 54.2, PromptTokensPerSecond: 100),
        RequestsPerMinute: requestsPerMinute,
        TimeToFirstToken: new LatencyStats(0.2, 0.412),
        InterTokenLatency: null,
        EndToEndLatency: null,
        QueueTime: null,
        PrefixCacheHitRate: null,
        SpecDecodeAcceptanceRate: null,
        PreemptionsPerMinute: 0,
        FinishedByReasonPerMinute: new Dictionary<string, double>(),
        ToolCallsPerMinute: 0);

    private static ModelKpis MakeKpisWithReasons(Dictionary<string, double> reasons) => new(
        Load: new ModelMetrics(0, 0, 0, GenerationTokensPerSecond: 0, PromptTokensPerSecond: 0),
        RequestsPerMinute: 0,
        TimeToFirstToken: null,
        InterTokenLatency: null,
        EndToEndLatency: null,
        QueueTime: null,
        PrefixCacheHitRate: null,
        SpecDecodeAcceptanceRate: null,
        PreemptionsPerMinute: 0,
        FinishedByReasonPerMinute: reasons,
        ToolCallsPerMinute: 0);

    [Fact]
    public void ErrorRateTile_ComputesFromVllmFinishReasons_WhenTheRouterHasNothingRouted()
    {
        // Router stats carry no history; the error-rate tile's value and trend fall back to vLLM's own
        // finish reasons (error + abort, over every finished request) while the router total stays 0.
        using var ctx = new Bunit.BunitContext();
        var ready = MakeModel("ready-model");
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(ready.Id, ModelState.Ready));

        var now = DateTimeOffset.UtcNow;
        var early = MakeKpisWithReasons(new Dictionary<string, double> { ["stop"] = 8, ["error"] = 2 });    // 20%
        var latest = MakeKpisWithReasons(new Dictionary<string, double> { ["stop"] = 6, ["abort"] = 2, ["error"] = 2 }); // 40%
        var history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(now.AddSeconds(-5), new MemorySnapshot(128, 60), Gpu: null), new HostSample(now, new MemorySnapshot(128, 60), Gpu: null)],
            LatestKpis = new Dictionary<string, ModelKpis> { [ready.Id] = latest },
        };
        history.ModelSamplesById[ready.Id] = [new ModelSample(now.AddSeconds(-5), early), new ModelSample(now, latest)];
        RegisterCoreFakes(ctx, new FakeModelRegistry([ready]), runtime, history: history);

        var cut = ctx.Render<Dashboard>();

        Assert.Equal("40", cut.Find("[data-testid=kpi-errors-value]").TextContent);
        Assert.Contains("vLLM finish reasons", cut.Find("[data-testid=kpi-errors-detail]").TextContent, StringComparison.Ordinal);

        var trend = cut.Find("[data-testid=kpi-errors-trend]");
        Assert.Contains("low 20", trend.GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Contains("latest 40", trend.GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    [Fact]
    public void IdleReadyModel_NoFinishedRequests_SaysNoTrafficOnce()
    {
        using var ctx = new Bunit.BunitContext();
        var ready = MakeModel("ready-model");
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(ready.Id, ModelState.Ready));

        var now = DateTimeOffset.UtcNow;
        var idle = MakeKpisWithReasons(new Dictionary<string, double>());
        var history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(now, new MemorySnapshot(128, 60), Gpu: null)],
            LatestKpis = new Dictionary<string, ModelKpis> { [ready.Id] = idle },
        };
        history.ModelSamplesById[ready.Id] = [new ModelSample(now, idle)];
        RegisterCoreFakes(ctx, new FakeModelRegistry([ready]), runtime, history: history);

        var cut = ctx.Render<Dashboard>();

        // Ready but idle: no traffic is said once, not as four tiles of dashes and zeros; memory and GPU stay.
        Assert.Empty(cut.FindAll("[data-testid=kpi-errors]"));
        Assert.Empty(cut.FindAll("[data-testid=kpi-gen]"));
        Assert.Contains("1 Ready model has served nothing", cut.Find("[data-testid=kpi-traffic-detail]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=kpi-memory]"));
        Assert.NotNull(cut.Find("[data-testid=kpi-gpu]"));
    }

    [Fact]
    public void Kpis_NoReadyModel_SayNoTrafficOnce()
    {
        // No IMetricsHistory registered and nothing Ready: one traffic tile says why there are no traffic figures.
        using var ctx = new Bunit.BunitContext();
        var model = MakeModel("stopped-model");
        RegisterCoreFakes(ctx, new FakeModelRegistry([model]), new FakeModelRuntime());

        var cut = ctx.Render<Dashboard>();

        Assert.Single(cut.FindAll("[data-testid=kpi-traffic]"));
        Assert.Contains("No model is Ready", cut.Find("[data-testid=kpi-traffic-detail]").TextContent, StringComparison.Ordinal);
        Assert.Equal("kpis", cut.Find("[data-testid=kpi-strip]").GetAttribute("data-tour"));
    }

    [Fact]
    public void MemoryBar_PrintsEveryValueInItsLegend()
    {
        using var ctx = new Bunit.BunitContext();
        var ready = MakeModel("ready-model", budget: 36);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(ready.Id, ModelState.Ready));
        RegisterCoreFakes(ctx, new FakeModelRegistry([ready]), runtime, memory: new MemorySnapshot(128, 60));

        var cut = ctx.Render<Dashboard>();

        var legend = cut.Find("[data-testid=memory-legend]").TextContent;
        Assert.Contains("36.0 GiB", legend, StringComparison.Ordinal);   // models
        Assert.Contains("32.0 GiB", legend, StringComparison.Ordinal);   // other = 68 used - 36
        Assert.Contains("60.0 GiB", legend, StringComparison.Ordinal);   // free
        Assert.Equal("52.0 GiB", cut.Find("[data-testid=memory-admittable]").TextContent);
        Assert.Contains("128.0 GiB", cut.Find("[data-testid=memory-bar-track]").GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryBar_BudgetsAboveWhatIsMeasured_SaysWhichIsWhich_AndNeverDrawsPastTheTotal()
    {
        // The critique's case: a Ready model budgeted 40 GiB on a 64 GiB node that measures (almost) nothing in use.
        using var ctx = new Bunit.BunitContext();
        var ready = MakeModel("ready-model", budget: 40);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(ready.Id, ModelState.Ready));
        RegisterCoreFakes(ctx, new FakeModelRegistry([ready]), runtime, memory: new MemorySnapshot(64, 63));

        var cut = ctx.Render<Dashboard>();

        var legend = cut.Find("[data-testid=memory-legend]").TextContent;
        Assert.Contains("1.0 GiB · budget 40.0 GiB", legend, StringComparison.Ordinal);
        Assert.Contains("63.0 GiB", legend, StringComparison.Ordinal);
        Assert.Contains("Budgets total 40.0 GiB, but 1.0 GiB is measured in use", cut.Find("[data-testid=memory-mismatch]").TextContent, StringComparison.Ordinal);
        Assert.True(DrawnPercent(cut) <= 100.0);
    }

    [Fact]
    public void MemoryBar_FreeAboveTheTotal_IsCappedAndSaid()
    {
        using var ctx = new Bunit.BunitContext();
        RegisterCoreFakes(ctx, new FakeModelRegistry([MakeModel("m")]), new FakeModelRuntime(), memory: new MemorySnapshot(64, 70));

        var cut = ctx.Render<Dashboard>();

        Assert.Contains("64.0 GiB", cut.Find("[data-testid=memory-legend]").TextContent, StringComparison.Ordinal);
        Assert.Contains("reports 70.0 GiB free of 64.0 GiB total", cut.Find("[data-testid=memory-mismatch]").TextContent, StringComparison.Ordinal);
        Assert.Equal("56.0 GiB", cut.Find("[data-testid=memory-admittable]").TextContent);
        Assert.True(DrawnPercent(cut) <= 100.0);
    }

    [Fact]
    public void MemoryBar_ReadingsThatAgree_HaveNoNote()
    {
        using var ctx = new Bunit.BunitContext();
        var ready = MakeModel("ready-model", budget: 36);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(ready.Id, ModelState.Ready));
        RegisterCoreFakes(ctx, new FakeModelRegistry([ready]), runtime, memory: new MemorySnapshot(128, 60));

        var cut = ctx.Render<Dashboard>();

        Assert.Empty(cut.FindAll("[data-testid=memory-mismatch]"));
    }

    /// <summary>Sum of the drawn segments' widths (models + other + reserve; free is the flexible rest).</summary>
    private static double DrawnPercent(IRenderedComponent<Dashboard> cut) =>
        cut.FindAll("[data-testid=memory-bar-track] .headroom-seg")
            .Sum(s => double.Parse(s.GetAttribute("style")!["width:".Length..].TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture));
}
