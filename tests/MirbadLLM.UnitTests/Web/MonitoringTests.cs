using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>bUnit tests for what the Overview took over from the Monitoring page: the leaderboard and the inline readout
/// from a fake <see cref="IMetricsHistory"/>, "—" for null KPIs, empty states, and the "backend not available" fallback.</summary>
public sealed class MonitoringTests
{
    private static void RegisterBaseFakes(
        Bunit.BunitContext ctx, FakeModelRegistry? registry = null, FakeRouterStats? routerStats = null, FakeModelRuntime? runtime = null)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(registry ?? new FakeModelRegistry());
        ctx.Services.AddSingleton<IModelRuntime>(runtime ?? new FakeModelRuntime());
        ctx.Services.AddSingleton<IRouterStats>(routerStats ?? new FakeRouterStats([]));
    }

    [Fact]
    public void MetricsHistoryNotRegisteredShowsBackendNotAvailable()
    {
        using var ctx = new Bunit.BunitContext();
        RegisterBaseFakes(ctx);

        var cut = ctx.Render<Dashboard>();

        Assert.NotNull(cut.Find("[data-testid=monitoring-unavailable]"));
    }

    [Fact]
    public void EmptyHistoryShowsCollectingAndNoModelsEmptyStates()
    {
        using var ctx = new Bunit.BunitContext();
        RegisterBaseFakes(ctx);
        ctx.Services.AddSingleton<IMetricsHistory>(new FakeMetricsHistory());

        var cut = ctx.Render<Dashboard>();

        Assert.NotNull(cut.Find("[data-testid=monitoring-collecting]"));
        Assert.NotNull(cut.Find("[data-testid=monitoring-no-models]"));
    }

    private static ModelKpis MakeKpis() => new(
        Load: new ModelMetrics(RunningRequests: 2, WaitingRequests: 1, KvCacheUsage: 0.42, GenerationTokensPerSecond: 55.5, PromptTokensPerSecond: 120.0),
        RequestsPerMinute: 3.2,
        TimeToFirstToken: null,
        InterTokenLatency: null,
        EndToEndLatency: null,
        QueueTime: null,
        PrefixCacheHitRate: null,
        SpecDecodeAcceptanceRate: null,
        PreemptionsPerMinute: 0,
        FinishedByReasonPerMinute: new Dictionary<string, double>(),
        ToolCallsPerMinute: 0);

    [Fact]
    public void AReadyModelWithoutEngineMetrics_IsOnTheLeaderboard_NotUnderNotRunning()
    {
        using var ctx = new Bunit.BunitContext();
        var model = new ModelDefinition { Id = "sg", ServedName = "SG", ModelPath = "checkpoint", Runtime = RuntimeKind.Sglang };
        RegisterBaseFakes(ctx, new FakeModelRegistry([model]));
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("sg", ModelState.Ready));
        ctx.Services.AddSingleton<IModelRuntime>(runtime);
        ctx.Services.AddSingleton<IMetricsHistory>(new FakeMetricsHistory
        {
            HostSamples = [new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(128, 90), Gpu: null)],
        });

        var cut = ctx.Render<Dashboard>();

        Assert.Contains("Ready", cut.Find("[data-testid=model-row-state-sg]").TextContent, StringComparison.Ordinal);
        Assert.Equal(MirbadLLM.Web.UiFormat.Missing, cut.Find("[data-testid=model-tps-sg]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=model-table-fold]"));

        cut.Find("[data-testid=model-row-open-sg]").Click();
        Assert.Contains("SGLang has no engine metrics", cut.Find("[data-testid=readout-no-metrics]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletedModel_WithRecentSamples_HasNoRunningRow()
    {
        // The history still holds the deleted model's last samples; only registered models get a row.
        using var ctx = new Bunit.BunitContext();
        var kept = new ModelDefinition { Id = "model-a", ServedName = "Model-A", ModelPath = "checkpoint" };
        RegisterBaseFakes(ctx, new FakeModelRegistry([kept]));
        var kpis = MakeKpis();
        var history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(128, 90), Gpu: null)],
            LatestKpis = new Dictionary<string, ModelKpis> { ["model-a"] = kpis, ["deleted-model"] = kpis },
        };
        history.ModelSamplesById["model-a"] = [new ModelSample(DateTimeOffset.UtcNow, kpis)];
        history.ModelSamplesById["deleted-model"] = [new ModelSample(DateTimeOffset.UtcNow, kpis)];
        ctx.Services.AddSingleton<IMetricsHistory>(history);

        var cut = ctx.Render<Dashboard>();

        Assert.NotNull(cut.Find("[data-testid=model-row-model-a]"));
        Assert.Empty(cut.FindAll("[data-testid=model-row-deleted-model]"));
    }

    [Fact]
    public void RendersKpiTilesWithDashesForNullValues()
    {
        using var ctx = new Bunit.BunitContext();
        var model = new ModelDefinition { Id = "model-a", ServedName = "Model-A", ModelPath = "checkpoint" };
        RegisterBaseFakes(ctx, new FakeModelRegistry([model]));

        var kpis = MakeKpis();
        var history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(128, 90), Gpu: null)],
            LatestKpis = new Dictionary<string, ModelKpis> { ["model-a"] = kpis },
        };
        history.ModelSamplesById["model-a"] = [new ModelSample(DateTimeOffset.UtcNow, kpis)];
        ctx.Services.AddSingleton<IMetricsHistory>(history);

        var cut = ctx.Render<Dashboard>();

        Assert.Contains("—", cut.Find("[data-testid=host-gpu-util]").TextContent);
        Assert.Contains("38", cut.Find("[data-testid=host-memory]").TextContent);
        Assert.Equal("—", cut.Find("[data-testid=model-ttft-model-a]").TextContent.Trim());
        Assert.Contains("no data", cut.Find("[data-testid=model-req-model-a]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=model-row-open-model-a]").Click();

        var ttft = cut.Find("[data-testid=kpi-ttft-model-a]");
        Assert.Contains("—", ttft.TextContent);
        Assert.DoesNotContain("NaN", ttft.TextContent);

        // Spec-decode acceptance is null -> the whole row is hidden, not shown as "—".
        Assert.Empty(cut.FindAll("[data-testid=kpi-spec-decode-model-a]"));

        Assert.Contains("Model-A", cut.Markup);
    }

    [Fact]
    public void RendersRouterCountersPerModel_AndFlagsAnErrorRateAboveFivePercent()
    {
        using var ctx = new Bunit.BunitContext();
        var model = new ModelDefinition { Id = "model-a", ServedName = "Model-A", ModelPath = "checkpoint" };
        RegisterBaseFakes(ctx, new FakeModelRegistry([model]), new FakeRouterStats([new RouterModelStats("Model-A", 10, 2, 1)]));

        var kpis = MakeKpis();
        var history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(128, 90), Gpu: null)],
            LatestKpis = new Dictionary<string, ModelKpis> { ["model-a"] = kpis },
        };
        ctx.Services.AddSingleton<IMetricsHistory>(history);

        var cut = ctx.Render<Dashboard>();

        // 2 of 10 failed: amber, with an icon and the word, in the row and in the pulse.
        var req = cut.Find("[data-testid=model-req-model-a]");
        Assert.Contains("10", req.TextContent, StringComparison.Ordinal);
        Assert.Contains("20% err", req.TextContent, StringComparison.Ordinal);
        Assert.NotNull(req.QuerySelector(".warn svg"));
        Assert.Contains("pulse-warn", cut.Find("[data-testid=pulse-errors]").GetAttribute("class"), StringComparison.Ordinal);
        Assert.Equal("1", cut.Find("[data-testid=pulse-in-flight]").TextContent);

        cut.Find("[data-testid=model-row-open-model-a]").Click();

        var router = cut.Find("[data-testid=kpi-router-model-a]");
        Assert.Contains("10", router.TextContent);
        Assert.Contains("2", router.TextContent);
        Assert.Contains("20", router.TextContent);
    }

    [Fact]
    public void ShowsSpecDecodeRowWhenNotNull()
    {
        using var ctx = new Bunit.BunitContext();
        var model = new ModelDefinition { Id = "model-a", ServedName = "Model-A", ModelPath = "checkpoint" };
        RegisterBaseFakes(ctx, new FakeModelRegistry([model]));

        var kpis = MakeKpis() with { SpecDecodeAcceptanceRate = 0.75 };
        var history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(128, 90), Gpu: null)],
            LatestKpis = new Dictionary<string, ModelKpis> { ["model-a"] = kpis },
        };
        ctx.Services.AddSingleton<IMetricsHistory>(history);
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("http://localhost/?model=model-a");

        var cut = ctx.Render<Dashboard>();

        var row = cut.Find("[data-testid=kpi-spec-decode-model-a]");
        Assert.Contains("75", row.TextContent);
    }

    [Fact]
    public void EachReadyModel_GetsARow_AndARowOpensItsReadout_InTheUrl()
    {
        using var ctx = TwoModelContext(out _);
        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = ctx.Render<Dashboard>();

        Assert.Contains("Model-A", cut.Find("[data-testid=model-row-model-a]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Model-B", cut.Find("[data-testid=model-row-model-b]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid^=model-kpis-]"));
        Assert.NotNull(cut.Find("[data-testid=readout-empty]"));

        cut.Find("[data-testid=model-row-model-b]").Click();

        Assert.NotNull(cut.Find("[data-testid=model-kpis-model-b]"));
        Assert.Empty(cut.FindAll("[data-testid=model-kpis-model-a]"));
        Assert.Equal("true", cut.Find("[data-testid=model-row-open-model-b]").GetAttribute("aria-expanded"));
        Assert.Contains("model=model-b", new Uri(nav.Uri).Query, StringComparison.Ordinal);

        // Another row switches the readout; the open row's own button closes it.
        cut.Find("[data-testid=model-row-open-model-a]").Click();
        Assert.NotNull(cut.Find("[data-testid=model-kpis-model-a]"));
        cut.Find("[data-testid=model-row-open-model-a]").Click();
        Assert.Empty(cut.FindAll("[data-testid^=model-kpis-]"));
        Assert.DoesNotContain("model=", new Uri(nav.Uri).Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Readout_IsInlineUnderTheLeaderboard_EscClosesIt_AndFocusGoesBackToTheRow()
    {
        using var ctx = ReadyModelContext();
        var cut = ctx.Render<Dashboard>();

        cut.Find("[data-testid=model-row-open-model-a]").Click();

        var readout = cut.Find("[data-testid=model-readout]");
        Assert.Equal("section", readout.TagName, ignoreCase: true);
        var heading = cut.Find("#mon-model-a");
        Assert.Equal(heading.Id, readout.GetAttribute("aria-labelledby"));
        Assert.Contains("Model-A", heading.TextContent, StringComparison.Ordinal);
        // The fixture KPIs have RunningRequests > 0, so the state reads "Busy" (icon + word).
        Assert.Contains("Busy", cut.Find("[data-testid=model-state-model-a]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=model-state-model-a] svg"));
        // Stamina: KV headroom is what the cache leaves (42% used); the latency budget has no TTFT to measure.
        Assert.Contains("58%", cut.Find("[data-testid=stamina-model-a]").TextContent, StringComparison.Ordinal);
        Assert.Contains("—", cut.Find("[data-testid=stamina-model-a]").TextContent, StringComparison.Ordinal);
        // The leaderboard stays above it.
        Assert.NotNull(cut.Find("[data-testid=model-row-model-a]"));

        cut.Find("[data-testid=model-readout]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Empty(cut.FindAll("[data-testid=model-kpis-model-a]"));
        // Focus moved to the readout's heading on open, and back to the row's button on close.
        Assert.Equal(2, ctx.JSInterop.Invocations.Count(i => i.Identifier.EndsWith("focus", StringComparison.Ordinal)));

        // The close button does the same.
        cut.Find("[data-testid=model-row-open-model-a]").Click();
        cut.Find("[data-testid=readout-close]").Click();
        Assert.NotNull(cut.Find("[data-testid=readout-empty]"));
    }

    [Fact]
    public void ModelRows_ShowBusy_WhenRunningRequestsPositive_AndRestartPendingWhenSet()
    {
        using var ctx = new Bunit.BunitContext();
        var busyModel = new ModelDefinition { Id = "busy-model", ServedName = "Busy-Model", ModelPath = "checkpoint" };
        var pendingModel = new ModelDefinition { Id = "pending-model", ServedName = "Pending-Model", ModelPath = "checkpoint" };
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(pendingModel.Id, ModelState.Ready, RestartPending: true));
        RegisterBaseFakes(ctx, new FakeModelRegistry([busyModel, pendingModel]), runtime: runtime);

        var busyKpis = MakeKpis() with { Load = new ModelMetrics(RunningRequests: 3, WaitingRequests: 0, KvCacheUsage: 0, GenerationTokensPerSecond: 1, PromptTokensPerSecond: 1) };
        var idleKpis = MakeKpis() with { Load = new ModelMetrics(RunningRequests: 0, WaitingRequests: 0, KvCacheUsage: 0, GenerationTokensPerSecond: 0, PromptTokensPerSecond: 0) };
        var history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(128, 90), Gpu: null)],
            LatestKpis = new Dictionary<string, ModelKpis> { [busyModel.Id] = busyKpis, [pendingModel.Id] = idleKpis },
        };
        ctx.Services.AddSingleton<IMetricsHistory>(history);

        var cut = ctx.Render<Dashboard>();

        Assert.Contains("Busy", cut.Find("[data-testid=model-row-state-busy-model]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Restart pending", cut.Find("[data-testid=model-row-state-pending-model]").TextContent, StringComparison.Ordinal);
        Assert.Equal("3 · 0", cut.Find("[data-testid=model-load-busy-model] .num").TextContent);
        Assert.Equal(3, cut.FindAll("[data-testid=model-load-busy-model] .pip--run").Count);
    }

    [Fact]
    public void InitialReadout_IsReadFromTheQueryString()
    {
        using var ctx = TwoModelContext(out _);

        // Setting the initial URL is itself a service lookup, so it must be the last thing done before
        // rendering (bUnit freezes the service collection once anything has been resolved from it).
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("http://localhost/?model=model-b");

        var cut = ctx.Render<Dashboard>();

        Assert.NotNull(cut.Find("[data-testid=model-kpis-model-b]"));
        Assert.Empty(cut.FindAll("[data-testid=model-kpis-model-a]"));
    }

    [Fact]
    public void InitialReadout_WaitsForTheFirstSnapshotBeforeOpening()
    {
        using var ctx = new Bunit.BunitContext();
        var registry = new FakeModelRegistry([new ModelDefinition { Id = "model-a", ServedName = "Model-A", ModelPath = "checkpoint" }]);
        var delayed = new DelayedRegistry(registry);
        RegisterBaseFakes(ctx, registry);
        ctx.Services.AddSingleton<IModelRegistry>(delayed);
        ctx.Services.AddSingleton<IMetricsHistory>(new FakeMetricsHistory
        {
            LatestKpis = new Dictionary<string, ModelKpis> { ["model-a"] = MakeKpis() },
        });
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("http://localhost/?model=model-a");

        var cut = ctx.Render<Dashboard>();

        Assert.NotNull(cut.Find("[data-testid=readout-empty]"));
        Assert.Empty(cut.FindAll("[data-testid=model-kpis-model-a]"));
        delayed.Ready.SetResult();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=model-kpis-model-a]")), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Rows_KeepTheirOrderOnRefresh_AndReSortOnlyOnAHeaderClick()
    {
        using var ctx = TwoModelContext(out var history);
        var cut = ctx.Render<Dashboard>();
        string[] Order() => [.. cut.FindAll("tr.m").Select(r => r.GetAttribute("data-testid")!)];

        // Sorted by tok/s, highest first: A (55.5) before B (10).
        Assert.Equal(["model-row-model-a", "model-row-model-b"], Order());
        Assert.Equal("descending", cut.Find("[data-testid=model-sort-tps]").ParentElement!.GetAttribute("aria-sort"));

        // B overtakes A; a refresh (here the window change reloads) updates the figures but not the order.
        history.LatestKpis = new Dictionary<string, ModelKpis>
        {
            ["model-a"] = MakeKpis() with { Load = MakeKpis().Load with { GenerationTokensPerSecond = 1 } },
            ["model-b"] = MakeKpis() with { Load = MakeKpis().Load with { GenerationTokensPerSecond = 99 } },
        };
        cut.Find("[data-testid=window-15]").Change(true);
        cut.WaitForAssertion(() => Assert.Equal("99", cut.Find("[data-testid=model-tps-model-b]").TextContent.Trim()));
        Assert.Equal(["model-row-model-a", "model-row-model-b"], Order());

        // A header click re-sorts: the same column flips to ascending, again to descending.
        cut.Find("[data-testid=model-sort-tps]").Click();
        Assert.Equal("ascending", cut.Find("[data-testid=model-sort-tps]").ParentElement!.GetAttribute("aria-sort"));
        Assert.Equal(["model-row-model-a", "model-row-model-b"], Order());
        cut.Find("[data-testid=model-sort-tps]").Click();
        Assert.Equal(["model-row-model-b", "model-row-model-a"], Order());
        Assert.Null(cut.Find("[data-testid=model-sort-name]").ParentElement!.GetAttribute("aria-sort"));
    }

    [Fact]
    public void ModelsNotRunning_FoldIntoOneRow_ThatOpensToListThem()
    {
        using var ctx = new Bunit.BunitContext();
        var ready = new ModelDefinition { Id = "model-a", ServedName = "Model-A", ModelPath = "checkpoint" };
        var stopped = new ModelDefinition { Id = "model-c", ServedName = "Model-C", ModelPath = "checkpoint" };
        RegisterBaseFakes(ctx, new FakeModelRegistry([ready, stopped]));
        ctx.Services.AddSingleton<IMetricsHistory>(new FakeMetricsHistory
        {
            LatestKpis = new Dictionary<string, ModelKpis> { ["model-a"] = MakeKpis() },
        });

        var cut = ctx.Render<Dashboard>();

        var fold = cut.Find("[data-testid=model-table-fold]");
        Assert.Contains("1 not running", fold.TextContent, StringComparison.Ordinal);
        Assert.Equal("false", fold.GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("[data-testid=model-off-model-c]"));
        Assert.Equal("1/2", cut.Find("[data-testid=pulse-ready]").TextContent);

        fold.Click();

        Assert.Contains("Model-C", cut.Find("[data-testid=model-off-model-c]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Stopped", cut.Find("[data-testid=model-off-model-c]").TextContent, StringComparison.Ordinal);
    }

    private sealed class FixedMaintenance(MaintenanceState? state) : IMaintenanceMode
    {
        public MaintenanceState? Current { get; } = state;
    }

    private static Bunit.BunitContext TwoModelContext(out FakeMetricsHistory history)
    {
        var ctx = new Bunit.BunitContext();
        var modelA = new ModelDefinition { Id = "model-a", ServedName = "Model-A", ModelPath = "checkpoint" };
        var modelB = new ModelDefinition { Id = "model-b", ServedName = "Model-B", ModelPath = "checkpoint" };
        RegisterBaseFakes(ctx, new FakeModelRegistry([modelA, modelB]));
        history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(128, 90), Gpu: null)],
            LatestKpis = new Dictionary<string, ModelKpis>
            {
                ["model-a"] = MakeKpis(),
                ["model-b"] = MakeKpis() with { Load = MakeKpis().Load with { GenerationTokensPerSecond = 10 } },
            },
        };
        ctx.Services.AddSingleton<IMetricsHistory>(history);
        return ctx;
    }

    private static Bunit.BunitContext ReadyModelContext(IMaintenanceMode? maintenance = null)
    {
        var ctx = new Bunit.BunitContext();
        var model = new ModelDefinition { Id = "model-a", ServedName = "Model-A", ModelPath = "checkpoint" };
        RegisterBaseFakes(ctx, new FakeModelRegistry([model]));
        var kpis = MakeKpis();
        var history = new FakeMetricsHistory
        {
            HostSamples = [new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(128, 90), Gpu: null)],
            LatestKpis = new Dictionary<string, ModelKpis> { ["model-a"] = kpis },
        };
        ctx.Services.AddSingleton<IMetricsHistory>(history);
        if (maintenance is not null)
        {
            ctx.Services.AddSingleton(maintenance);
        }

        return ctx;
    }

    [Fact]
    public void WindowSelector_IsARadioGroup_OfNativeRadios()
    {
        using var ctx = ReadyModelContext();
        var cut = ctx.Render<Dashboard>();

        var group = cut.Find("[data-testid=window-selector]");
        Assert.Equal("radiogroup", group.GetAttribute("role"));
        Assert.Equal("Time window", group.GetAttribute("aria-label"));
        var radios = group.QuerySelectorAll("input[type=radio]");
        Assert.Equal(3, radios.Length);
        Assert.All(radios, r => Assert.Equal("overview-window", r.GetAttribute("name")));
        Assert.True(cut.Find("[data-testid=window-15]").HasAttribute("checked"));
        Assert.Equal("overview", group.GetAttribute("data-tip"));

        cut.Find("[data-testid=window-5]").Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("[data-testid=window-5]").HasAttribute("checked"));
            Assert.False(cut.Find("[data-testid=window-15]").HasAttribute("checked"));
            Assert.Contains("Last 5 min", cut.Markup, StringComparison.Ordinal);
            // The KPI strip follows the window too.
            Assert.Equal("This host, last 5 min", cut.Find("#kpi-heading").TextContent);
        });
    }

    [Fact]
    public void AModelBusyWithABenchmark_SaysSo_AndLinksToTheRunningJob()
    {
        var job = new MaintenanceState("job-1", "benchmark", "model-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
        using var ctx = ReadyModelContext(new FixedMaintenance(job));
        var cut = ctx.Render<Dashboard>();

        Assert.Contains("Benchmarking", cut.Find("[data-testid=model-row-state-model-a]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=model-row-open-model-a]").Click();

        Assert.Contains("Benchmarking", cut.Find("[data-testid=model-state-model-a]").TextContent, StringComparison.Ordinal);
        var link = cut.Find("[data-testid=model-job-link-model-a]");
        Assert.Equal("models/model-a/benchmark", link.GetAttribute("href"));
        Assert.Contains("view progress", link.TextContent, StringComparison.Ordinal);
    }

    private sealed class DelayedRegistry(IModelRegistry inner) : IModelRegistry
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<ModelDefinition>> ListAsync(CancellationToken ct = default)
        {
            await Ready.Task.WaitAsync(ct);
            return await inner.ListAsync(ct);
        }

        public Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public Task UpsertAsync(ModelDefinition model, CancellationToken ct = default) => inner.UpsertAsync(model, ct);
        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
    }

    [Fact]
    public void Tuning_LinksToTheTunePage()
    {
        var job = new MaintenanceState("job-2", "tuning", "model-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30));
        using var ctx = ReadyModelContext(new FixedMaintenance(job));
        var cut = ctx.Render<Dashboard>();

        cut.Find("[data-testid=model-row-open-model-a]").Click();

        Assert.Contains("Tuning", cut.Find("[data-testid=model-state-model-a]").TextContent, StringComparison.Ordinal);
        Assert.Equal("models/model-a/tune", cut.Find("[data-testid=model-job-link-model-a]").GetAttribute("href"));
        Assert.NotNull(cut.Find("[data-testid=dashboard-updated]").GetAttribute("data-live-at"));
    }
}
