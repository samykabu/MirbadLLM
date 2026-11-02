using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Web.Components.Layout;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Web;

/// <summary>MN-110 (design §12.3): the maintenance banner names the node of a node-scoped window and lists the exempt models still serving.</summary>
public sealed class MaintenanceBannerNodeScopeTests
{
    private static ModelDefinition Model(string id, string nodeId, bool keepServing) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = "/models/" + id,
        Placement = new ModelPlacement(nodeId),
        KeepServingDuringJobs = keepServing,
    };

    [Fact]
    public void NodeWindow_NamesTheNode_AndListsTheExemptModelsStillServing()
    {
        using var ctx = new BunitContext();
        var maintenance = new MaintenanceModeService();
        maintenance.Enter(new MaintenanceState("job-1", "benchmark", "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "dgx"));
        var registry = new FakeModelRegistry()
            .Add(Model("qwen", "dgx", keepServing: true)) // under test: never listed
            .Add(Model("ss-qwen3-embedding-4b", "dgx", keepServing: true))
            .Add(Model("other", "dgx", keepServing: false))
            .Add(Model("remote", "pc", keepServing: true));
        ctx.Services.AddSingleton<IMaintenanceMode>(maintenance);
        ctx.Services.AddSingleton<IModelRegistry>(registry);

        var cut = ctx.Render<MaintenanceBanner>();

        var banner = cut.Find("[data-testid=maintenance-banner]");
        Assert.Equal("dgx", banner.GetAttribute("data-node"));
        Assert.Equal("dgx", cut.Find("[data-testid=maintenance-node]").TextContent);
        Assert.Contains("Node dgx paused for benchmarking", banner.TextContent, StringComparison.Ordinal);
        Assert.Equal("Still serving on dgx: ss-qwen3-embedding-4b.", cut.Find("[data-testid=maintenance-exempt]").TextContent);
    }

    [Fact]
    public void TwoWindows_RenderOneNoticeEach_GlobalOneKeepsTodaysText()
    {
        using var ctx = new BunitContext();
        var maintenance = new MaintenanceModeService();
        maintenance.Enter(new MaintenanceState("job-1", "tuning", "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)));
        maintenance.Enter(new MaintenanceState("job-2", "benchmark", "remote", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "pc"));
        ctx.Services.AddSingleton<IMaintenanceMode>(maintenance);
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry());

        var cut = ctx.Render<MaintenanceBanner>();

        var banners = cut.FindAll("[data-testid=maintenance-banner]");
        Assert.Equal(2, banners.Count);
        Assert.Contains("API paused for tuning", banners[0].TextContent, StringComparison.Ordinal);
        Assert.Empty(banners[0].QuerySelectorAll("[data-testid=maintenance-node]"));
        Assert.Contains("on node pc", banners[1].TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=maintenance-exempt]"));
    }
}
