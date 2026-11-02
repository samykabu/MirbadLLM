using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using FakeApiKeyStore = MirbadLLM.UnitTests.Router.Fakes.FakeApiKeyStore;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// The Overview as one "Command Deck" (Overview and Monitoring merged): node scope, the node panels' extra facts, the fleet
/// counters, the service levels and the old /monitoring links.
/// </summary>
public sealed partial class DashboardGroupingTests
{
    private static ModelKpis Gen(double tokensPerSecond) =>
        new(new ModelMetrics(0, 0, 0.2, tokensPerSecond, 0), 6, null, null, null, null, null, null, 0, new Dictionary<string, double>(), 0);

    [Fact]
    public void Deck_ScopingToANode_NarrowsTheKpis_TheSignal_AndTheLeaderboard()
    {
        var history = new NodeMetricsHistory();
        history.Hosts["dgx"] = [new HostSample(Now, new MemorySnapshot(119.7, 80), new GpuSnapshot(20, null, null, null))];
        history.Hosts["pc"] = [new HostSample(Now, new MemorySnapshot(64, 40), null) { Gpus = [new HostGpuSample(0, "GPU-a", 60, 10, 16), new HostGpuSample(1, "GPU-b", 90, 1, 16)] }];
        history.Kpis["qwen"] = Gen(10);
        history.Kpis["small"] = Gen(20);
        history.Kpis["coder"] = Gen(30);
        var (ctx, _, _) = TwoNodes(history: history);
        using (ctx)
        {
            var nav = ctx.Services.GetRequiredService<NavigationManager>();
            var cut = ctx.Render<Dashboard>();

            Assert.Equal("60", cut.Find("[data-testid=kpi-gen-value]").TextContent);
            Assert.Equal("Fleet, last 15 min", cut.Find("#kpi-heading").TextContent);
            // One heat row per node, per GPU on the discrete PC.
            var heat = cut.FindAll("[data-testid=signal-table] tbody th").Select(th => th.TextContent).ToList();
            Assert.Contains("DGX Spark, GPU %", heat);
            Assert.Contains("Office PC · GPU 1, GPU %", heat);
            Assert.Equal(3, cut.FindAll("tr.m").Count);
            Assert.Empty(cut.FindAll("[data-testid=host-history]")); // every node: no single node's history

            cut.Find("[data-testid=node-option-pc]").Click();

            Assert.Equal("50", cut.Find("[data-testid=kpi-gen-value]").TextContent);
            Assert.Equal("Office PC, last 15 min", cut.Find("#kpi-heading").TextContent);
            Assert.Equal("90", cut.Find("[data-testid=kpi-gpu-value]").TextContent);
            Assert.Contains("11.0 of 32.0 GiB used on Office PC", cut.Find("[data-testid=kpi-memory-detail]").TextContent, StringComparison.Ordinal);
            string legend = cut.Find("[data-testid=signal-legend]").TextContent;
            Assert.Contains("coder", legend, StringComparison.Ordinal);
            Assert.DoesNotContain("qwen", legend, StringComparison.Ordinal);
            Assert.Equal(["model-row-coder", "model-row-small"], cut.FindAll("tr.m").Select(r => r.GetAttribute("data-testid")));
            Assert.Equal(["node-group-pc"], GroupIds(cut));
            // The scoped node's history opens under its panel.
            Assert.True(cut.Find("[data-testid=host-history]").HasAttribute("open"));
            Assert.Contains("node=pc", nav.Uri, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Deck_NodePanels_ShowPeakMemory_ModelDisk_AndDownloads()
    {
        var history = new NodeMetricsHistory();
        history.Hosts["dgx"] =
        [
            new HostSample(Now.AddSeconds(-10), new MemorySnapshot(119.7, 80), new GpuSnapshot(20, 61, 52, null)),
            new HostSample(Now, new MemorySnapshot(119.7, 90), new GpuSnapshot(25, 64, 54, null)),
        ];
        var dgx = Dgx() with { Capabilities = Dgx().Capabilities! with { ModelRoots = [new ModelRootInfo("models", 1_288_490_188_800, 2_000_000_000_000, 7)] } };
        var runtime = new FakeModelRuntime();
        var cache = new FakeNodeStatusCache(dgx, Pc());
        using var ctx = Context([Model("qwen", "dgx")], runtime, cache, new ListOnlyNodeRegistry(Record("dgx", "DGX Spark"), Record("pc", "Office PC")), history);
        var downloads = new FakeHuggingFaceDownloader();
        downloads.Seed(new DownloadStatus("Qwen3-Coder", "org/Qwen3-Coder", null, DownloadState.Downloading, 50, 100, 2 * 1_048_576, null));
        ctx.Services.AddSingleton<IHuggingFaceDownloader>(downloads);

        var cut = ctx.Render<Dashboard>();

        Assert.Equal("39.7 GiB", cut.Find("[data-testid=node-peak-dgx]").TextContent.Trim()); // 119.7 − 80, the window's peak
        Assert.Equal("1200.00 GiB", cut.Find("[data-testid=node-disk-dgx]").TextContent.Trim());
        Assert.Contains("54 °C · 64 W", cut.Find("[data-testid=node-facts-dgx]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Downloading org/Qwen3-Coder · 50% · 2.0 MiB/s", cut.Find("[data-testid=node-downloads-dgx]").TextContent, StringComparison.Ordinal);
        // The PC reports no disk and its downloads cannot be read here: honest dashes.
        Assert.Equal("—", cut.Find("[data-testid=node-disk-pc]").TextContent.Trim());
        Assert.Contains("Downloads —", cut.Find("[data-testid=node-downloads-pc]").TextContent, StringComparison.Ordinal);
        Assert.Equal("—", cut.Find("[data-testid=node-peak-pc]").TextContent.Trim());
    }

    [Fact]
    public void Deck_ShowsTheFleetCounters_AndServiceLevels_WithoutAnyGameLayer()
    {
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("qwen", ModelState.Ready, HostPort: 8101));
        runtime.SetStatus(new ModelStatus("broken", ModelState.Failed, Error: "boom"));
        var history = new NodeMetricsHistory();
        history.Kpis["qwen"] = Gen(10);
        using var ctx = Context([Model("qwen", "dgx"), Model("broken", "dgx")], runtime, new FakeNodeStatusCache(Dgx()), history: history);
        var store = new FleetStatsStore(Options.Create(new MirbadLLMOptions { DataPath = "" }));
        store.AddRequests(150);
        store.AddTokens(50_000, DateTimeOffset.UtcNow);
        ctx.Services.AddSingleton(store);
        ctx.Services.AddSingleton<IApiKeyStore>(new FakeApiKeyStore());

        var cut = ctx.Render<Dashboard>();

        Assert.Equal("Fleet today", cut.Find("#fleet-stats-title").TextContent);
        Assert.Equal("150", cut.Find("[data-testid=fleet-requests]").TextContent);
        Assert.Equal("50 k", cut.Find("[data-testid=fleet-tokens-today]").TextContent);
        Assert.NotNull(cut.Find("[data-testid=fleet-crash-free]"));
        Assert.NotNull(cut.Find("[data-testid=fleet-error-budget]"));
        // 1 of 2 models that should run is Ready: availability half full, said in words.
        Assert.Equal("1 / 2", cut.Find("[data-testid=slo-availability-value]").TextContent);
        Assert.Contains("watch", cut.Find("[data-testid=slo-availability]").TextContent, StringComparison.Ordinal);
        Assert.Equal(5, cut.FindAll("[data-testid=slo-availability] .slo-segs .on").Count);
        Assert.Contains("Ready · 1/2", cut.Find("[data-testid=service-level]").TextContent, StringComparison.Ordinal);
        // No health ring, level, XP or quests anywhere on the page.
        Assert.Empty(cut.FindAll("[data-testid=health-ring], [data-testid=fleet-level], [data-testid=attention], [data-testid=next-level]"));
        Assert.Empty(cut.FindAll("[data-testid^=model-level-], [data-testid^=quest-]"));
        Assert.DoesNotContain("XP", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Quest", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Lv ", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost/monitoring", "/")]
    [InlineData("http://localhost/monitoring?node=pc", "/?node=pc")]
    [InlineData("http://localhost/monitoring?model=qwen&node=pc&x=1", "/?node=pc&model=qwen")]
    public void Monitoring_RedirectsToTheOverview_KeepingNodeAndModel(string from, string to) =>
        Assert.Equal(to, Monitoring.Target(from));
}
