using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Ui;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// bUnit tests for the model workspace: the persistent <see cref="ModelHeader"/> (identity, live state, chips, Start/Stop
/// through admission, Chat, the line tabs) and the Overview page.
/// </summary>
public sealed class ModelWorkspaceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static readonly ModelDefinition Alpha = new()
    {
        Id = "alpha", ServedName = "Alpha-9B", ModelPath = "Alpha-9B", MemoryBudgetGiB = 24, MaxModelLen = 32_768, MaxNumSeqs = 8,
    };

    private static Bunit.BunitContext MakeContext(FakeModelRuntime runtime, params ModelDefinition[] models)
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(models));
        ctx.Services.AddSingleton<IModelRuntime>(runtime);
        return ctx;
    }

    [Fact]
    public void Header_ShowsServedNameIdStateChipsAndTheCurrentTab()
    {
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("alpha", ModelState.Ready));
        using var ctx = MakeContext(runtime, Alpha);

        var cut = ctx.Render<ModelHeader>(p => p.Add(x => x.ModelId, "alpha").Add(x => x.Current, "logs"));

        Assert.Equal("Alpha-9B", cut.Find("h1[data-testid=page-title]").TextContent);
        Assert.Equal("alpha", cut.Find("[data-testid=model-header-id]").TextContent);
        Assert.Equal("ready", cut.Find("[data-testid=model-header-state]").GetAttribute("data-state"));
        Assert.Equal("models", cut.Find("[data-testid=model-header-back]").GetAttribute("href"));
        Assert.Contains("vLLM", cut.Find("[data-testid=model-header-runtime]").TextContent, StringComparison.Ordinal);
        Assert.Contains("24.0 GiB", cut.Find("[data-testid=model-header-budget]").TextContent, StringComparison.Ordinal);

        // One tab bar for every page of the model; only the current page is marked, and the tour can find both groups.
        var tabs = cut.Find("[data-testid=model-tabs]");
        Assert.Equal("model-tabs", tabs.GetAttribute("data-tour"));
        Assert.Equal(["overview", "settings", "logs", "benchmark", "tune"],
            cut.FindAll("[data-testid=model-tabs] a").Select(a => a.GetAttribute("data-testid")!["model-tab-".Length..]).ToArray());
        Assert.Equal("page", cut.Find("[data-testid=model-tab-logs]").GetAttribute("aria-current"));
        Assert.Null(cut.Find("[data-testid=model-tab-settings]").GetAttribute("aria-current"));
        Assert.Equal("models/alpha", cut.Find("[data-testid=model-tab-settings]").GetAttribute("href"));
        Assert.Equal("models/alpha/overview", cut.Find("[data-testid=model-tab-overview]").GetAttribute("href"));
        Assert.NotNull(cut.Find("[data-tour=model-actions] [data-testid=header-stop]"));

        // Ready: Chat opens the Playground with this model preselected.
        Assert.Equal("chat?model=Alpha-9B", cut.Find("[data-testid=header-chat]").GetAttribute("href"));
    }

    [Fact]
    public void Header_StoppedModel_OffersStart_AndChatWaitsForReady()
    {
        using var ctx = MakeContext(new FakeModelRuntime(), Alpha);

        var cut = ctx.Render<ModelHeader>(p => p.Add(x => x.ModelId, "alpha").Add(x => x.Current, "overview"));

        Assert.False(cut.Find("[data-testid=header-start]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=header-chat]"));
        Assert.True(cut.Find("[data-testid=header-chat-disabled]").HasAttribute("disabled"));
    }

    [Fact]
    public void Header_Start_RefusedForMemory_AsksBeforeForcing_ThenForces()
    {
        var runtime = new FakeModelRuntime { RefuseUnlessForce = true };
        using var ctx = MakeContext(runtime, Alpha);
        var cut = ctx.Render<ModelHeader>(p => p.Add(x => x.ModelId, "alpha").Add(x => x.Current, "overview"));

        cut.Find("[data-testid=header-start]").Click();

        // The plain start went through admission and was refused: nothing forced yet, the dialog asks first.
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=header-force-start-dialog]").GetAttribute("data-open")), Wait);
        Assert.Equal([("alpha", false)], runtime.StartCalls);

        cut.Find("[data-testid=header-force-start-dialog-confirm]").Click();

        cut.WaitForAssertion(() => Assert.Equal([("alpha", false), ("alpha", true)], runtime.StartCalls), Wait);
        cut.WaitForAssertion(() => Assert.Equal("starting", cut.Find("[data-testid=model-header-state]").GetAttribute("data-state")), Wait);
    }

    [Fact]
    public void Header_Stop_StopsTheModel()
    {
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("alpha", ModelState.Ready));
        using var ctx = MakeContext(runtime, Alpha);
        var cut = ctx.Render<ModelHeader>(p => p.Add(x => x.ModelId, "alpha").Add(x => x.Current, "overview"));

        cut.Find("[data-testid=header-stop]").Click();

        cut.WaitForAssertion(() => Assert.Equal(["alpha"], runtime.StopCalls), Wait);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=header-start]")), Wait);
    }

    [Fact]
    public void Header_Starting_ShowsTheElapsedTimerInPlace()
    {
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("alpha", ModelState.Starting, Since: DateTimeOffset.UtcNow.AddSeconds(-95)));
        using var ctx = MakeContext(runtime, Alpha);

        var cut = ctx.Render<ModelHeader>(p => p.Add(x => x.ModelId, "alpha").Add(x => x.Current, "overview"));

        Assert.StartsWith("1m 3", cut.Find("[data-testid=model-header-state-elapsed]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=header-stop]"));
    }

    [Fact]
    public void Header_ReportsTheStateToThePage()
    {
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("alpha", ModelState.Failed, Error: "boom"));
        using var ctx = MakeContext(runtime, Alpha);
        ModelStatus? seen = null;

        ctx.Render<ModelHeader>(p => p.Add(x => x.ModelId, "alpha").Add(x => x.Current, "overview")
            .Add(x => x.StatusChanged, (ModelStatus s) => seen = s));

        Assert.Equal(ModelState.Failed, seen?.State);
    }

    [Fact]
    public void Overview_FailedModel_ShowsCauseAndLogLink_AndNoLiveNumbers()
    {
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("alpha", ModelState.Failed, Error: "CUDA out of memory while loading weights"));
        using var ctx = MakeContext(runtime, Alpha);

        var cut = ctx.Render<ModelOverview>(p => p.Add(x => x.Id, "alpha"));

        cut.WaitForAssertion(() => Assert.Contains("CUDA out of memory", cut.Find("[data-testid=overview-failure]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal("models/alpha/logs", cut.Find("[data-testid=overview-failure-logs]").GetAttribute("href"));
        Assert.Equal("—", cut.Find("[data-testid=overview-tok-s]").TextContent);
        Assert.Equal("—", cut.Find("[data-testid=overview-kv]").TextContent);
        Assert.Equal("Alpha-9B", cut.Find("[data-testid=overview-served-name]").TextContent);
        Assert.Contains("32,768", cut.Find("[data-testid=overview-context]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=overview-benchmark-empty]"));
        Assert.Equal("page", cut.Find("[data-testid=model-tab-overview]").GetAttribute("aria-current"));
    }

    [Theory]
    [InlineData(RuntimeKind.Vllm, "50%")]
    [InlineData(RuntimeKind.LlamaCpp, "—")]
    public void Overview_ReadyModel_ShowsLiveNumbers_KvCacheOnlyForVllm(RuntimeKind kind, string expectedKv)
    {
        var model = Alpha with { Runtime = kind };
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("alpha", ModelState.Ready));
        using var ctx = MakeContext(runtime, model);
        ctx.Services.AddSingleton<IMetricsHistory>(new FakeMetricsHistory
        {
            LatestKpis = new Dictionary<string, ModelKpis>
            {
                ["alpha"] = new(new ModelMetrics(3, 1, 0.5, 42.5, 900), 12, null, null, null, null, null, null, 0, new Dictionary<string, double>(), 0),
            },
        });

        var cut = ctx.Render<ModelOverview>(p => p.Add(x => x.Id, "alpha"));

        cut.WaitForAssertion(() => Assert.Equal("42.5", cut.Find("[data-testid=overview-tok-s]").TextContent), Wait);
        Assert.Equal("3 / 1", cut.Find("[data-testid=overview-running-waiting]").TextContent);
        Assert.Equal(expectedKv, cut.Find("[data-testid=overview-kv]").TextContent);
        Assert.Equal(kind == RuntimeKind.LlamaCpp ? "llama.cpp" : "vLLM", cut.Find("[data-testid=overview-runtime]").TextContent);
    }

    [Fact]
    public void Overview_UnknownModel_ShowsNotFound()
    {
        using var ctx = MakeContext(new FakeModelRuntime());

        var cut = ctx.Render<ModelOverview>(p => p.Add(x => x.Id, "ghost"));

        Assert.NotNull(cut.Find("[data-testid=overview-not-found]"));
        Assert.Empty(cut.FindAll("[data-testid=model-header]"));
    }
}
