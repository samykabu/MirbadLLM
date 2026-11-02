using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>bUnit tests for the Models list and the Logs page.</summary>
public sealed class PagesTests
{
    [Fact]
    public void Models_ListsEveryModelWithStateAndEditLink()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var a = new ModelDefinition { Id = "alpha", ServedName = "Alpha-9B", ModelPath = "Alpha-9B", MemoryBudgetGiB = 24, MaxModelLen = 131_072 };
        var b = new ModelDefinition { Id = "beta", ServedName = "Beta-4B", ModelPath = "Beta-4B" };
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("alpha", ModelState.Ready));
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry([a, b]));
        ctx.Services.AddSingleton<IModelRuntime>(runtime);

        var cut = ctx.Render<Models>();

        var alpha = cut.Find("[data-testid=models-row-alpha]");
        Assert.Contains("Alpha-9B", alpha.TextContent, StringComparison.Ordinal);
        Assert.Contains("Ready", alpha.TextContent, StringComparison.Ordinal);
        alpha.Click();
        Assert.Contains("131,072", cut.Find("[data-testid=models-drawer]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Stopped", cut.Find("[data-testid=models-row-beta]").TextContent, StringComparison.Ordinal);
        Assert.Equal("models/beta", cut.Find("[data-testid=models-edit-beta]").GetAttribute("href"));
        // Repeated actions have distinct accessible names.
        Assert.Equal("Edit Beta-4B", cut.Find("[data-testid=models-edit-beta]").GetAttribute("aria-label"));
    }

    [Fact]
    public void Models_EmptyStateOffersBothSources()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry());
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());

        var cut = ctx.Render<Models>();

        Assert.NotNull(cut.Find("[data-testid=models-empty] a[href='models/new?source=download']"));
    }

    [Fact]
    public void Logs_StreamsLinesIntoALogRegion()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var model = new ModelDefinition { Id = "alpha", ServedName = "Alpha-9B", ModelPath = "p" };
        var runtime = new FakeModelRuntime();
        runtime.SetLogs("alpha", "INFO starting", "INFO loading weights", "INFO ready");
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry([model]));
        ctx.Services.AddSingleton<IModelRuntime>(runtime);

        var cut = ctx.Render<Logs>(p => p.Add(x => x.Id, "alpha"));

        cut.WaitForAssertion(() => Assert.Contains("INFO ready", cut.Find("[data-testid=log-container]").TextContent, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
        var log = cut.Find("[data-testid=log-container]");
        Assert.Equal("log", log.GetAttribute("role"));
        Assert.Contains("INFO starting\nINFO loading weights\nINFO ready", log.TextContent, StringComparison.Ordinal);
        Assert.Contains("3 lines", cut.Find("[data-testid=log-count]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Logs_UnknownModelShowsNotFound()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry());
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());

        var cut = ctx.Render<Logs>(p => p.Add(x => x.Id, "ghost"));

        Assert.NotNull(cut.Find("[data-testid=logs-not-found]"));
        Assert.Empty(cut.FindAll("[data-testid=log-container]"));
    }
}
