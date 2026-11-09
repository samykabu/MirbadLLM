using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// The Models list is where models are operated (UI redesign 2026-10-15): one inline Start or Stop per row through the
/// same start / force-start / stop paths the Dashboard's cards used, state in place (live elapsed timer while Starting,
/// cause on Failed), live tok/s, and the row menu saying why an action is unavailable.
/// </summary>
public sealed class ModelsPageTests
{
    [Fact]
    public async Task SeveralStartClicks_ShowQueued_AllowCancel_AndStartInNodeOrder()
    {
        var runtime = new FakeModelRuntime();
        using var ctx = Context(runtime, models: [MakeModel("a"), MakeModel("b"), MakeModel("c")]);
        using var host = new HostBuilder().Build();
        ctx.Services.AddSingleton(sp => new ModelStartService(sp.GetRequiredService<IModelRegistry>(), runtime,
            host.Services.GetRequiredService<IHostApplicationLifetime>()));
        var cut = ctx.Render<Models>();
        await cut.Find("[data-testid=start-a]").ClickAsync(new MouseEventArgs());
        // Find and click on the renderer's thread: a re-render between the two would drop the found button's handler.
        Task b = Task.CompletedTask, c = Task.CompletedTask;
        await cut.InvokeAsync(() => { b = cut.Find("[data-testid=start-b]").ClickAsync(new MouseEventArgs()); });
        await cut.InvokeAsync(() => { c = cut.Find("[data-testid=start-c]").ClickAsync(new MouseEventArgs()); });
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Queued", cut.Find("[data-testid=start-b]").TextContent, StringComparison.Ordinal);
            Assert.True(cut.Find("[data-testid=start-b]").HasAttribute("disabled"));
            Assert.NotNull(cut.Find("[data-testid=cancel-start-c]"));
        });
        Task cancel = Task.CompletedTask;
        await cut.InvokeAsync(() => { cancel = cut.Find("[data-testid=cancel-start-c]").ClickAsync(new MouseEventArgs()); });
        await cancel;
        await c.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(["a"], runtime.StartCalls.Select(s => s.Id));
        runtime.SetStatus(new("a", ModelState.Ready));
        await b.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(["a", "b"], runtime.StartCalls.Select(s => s.Id));
        Assert.Empty(cut.FindAll("[data-testid=models-error]"));
    }

    private static ModelDefinition MakeModel(string id, double budget = 36) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = "checkpoint",
        MemoryBudgetGiB = budget,
    };

    private static BunitContext Context(FakeModelRuntime runtime, MemorySnapshot? memory = null, IMetricsHistory? history = null, params ModelDefinition[] models)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(models));
        ctx.Services.AddSingleton<IModelRuntime>(runtime);
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(memory ?? new MemorySnapshot(128, 90)));
        ctx.Services.AddSingleton<IModelMetricsReader>(new FakeModelMetricsReader());
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions { MemoryReserveGiB = 8 }));
        if (history is not null)
        {
            ctx.Services.AddSingleton(history);
        }

        return ctx;
    }

    [Fact]
    public void RunningAndStoppedGroups_CollapseIndependently()
    {
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new("ready", ModelState.Ready));
        using var ctx = Context(runtime, models: [MakeModel("ready"), MakeModel("stopped")]);
        var cut = ctx.Render<Models>();
        cut.Find("[data-testid=models-running-group]").Click();
        Assert.Empty(cut.FindAll("[data-testid=models-row-ready]"));
        Assert.Single(cut.FindAll("[data-testid=models-row-stopped]"));
        cut.Find("[data-testid=models-running-group]").Click();
        Assert.Single(cut.FindAll("[data-testid=models-row-ready]"));
    }

    [Fact]
    public void RowKeyboardOpensDetails_WithBudgetAndContext_AndEscapeClearsTheQuery()
    {
        using var ctx = Context(new FakeModelRuntime(), models: [MakeModel("alpha", budget: 12)]);
        var cut = ctx.Render<Models>();
        cut.Find("[data-testid=models-row-alpha]").KeyDown(new KeyboardEventArgs { Key = "Enter" });
        var detail = cut.Find("[data-testid=models-drawer]");
        Assert.Contains("12.0 GiB", detail.TextContent, StringComparison.Ordinal);
        Assert.Contains("Context", detail.TextContent, StringComparison.Ordinal);
        Assert.Contains("Memory reserved", detail.TextContent, StringComparison.Ordinal);
        Assert.Equal("fp8", cut.Find("[data-testid=models-detail-kv]").TextContent);
        Assert.Equal("Off", cut.Find("[data-testid=models-detail-spec]").TextContent);
        Assert.Contains("model=alpha", ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
        detail.KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(cut.FindAll("[data-testid=models-drawer]"));
        Assert.DoesNotContain("model=", ctx.Services.GetRequiredService<NavigationManager>().Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_ShowMemoryContextKvAndSpeculative_AndALongImageWrapsInThePanel()
    {
        var sglang = MakeModel("sg", budget: 84) with
        {
            Runtime = RuntimeKind.Sglang,
            Container = new ContainerRuntimeSettings { Image = "lmsysorg/sglang@sha256:" + new string('a', 64), ExtraArgs = ["--kv-cache-dtype", "fp8_e4m3", "--context-length", "131072"] },
            Speculative = new SpeculativeDecoding("DFLASH", 8),
        };
        using var ctx = Context(new FakeModelRuntime(), models: [sglang]);
        var cut = ctx.Render<Models>();

        Assert.Equal("84.0 GiB", cut.Find("[data-testid=models-memory-sg]").TextContent);
        Assert.Equal("131,072", cut.Find("[data-testid=models-context-sg]").TextContent);
        Assert.Equal("fp8_e4m3", cut.Find("[data-testid=models-kv-sg]").TextContent);
        Assert.Equal("DFLASH × 8", cut.Find("[data-testid=models-spec-sg]").TextContent);

        cut.Find("[data-testid=models-row-sg]").Click();
        Assert.Contains("kv-full", cut.Find("[data-testid=models-detail-image]").ClassName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RuntimeKind.Sglang, "auto (default)")]
    [InlineData(RuntimeKind.Mlx, "Full precision")]
    [InlineData(RuntimeKind.LlamaCpp, "f16 (default)")]
    [InlineData(RuntimeKind.Nim, "NIM default")]
    public void KvCacheLabel_NamesEachRuntimesDefault(RuntimeKind runtime, string label)
        => Assert.Equal(label, UiFormat.KvCacheLabel(MakeModel("m") with { Runtime = runtime }));

    [Fact]
    public void KvCacheLabel_ReadsEachRuntimesSetting()
    {
        Assert.Equal("q8_0", UiFormat.KvCacheLabel(MakeModel("m") with { Runtime = RuntimeKind.LlamaCpp, LlamaCpp = new LlamaCppSettings { ModelFile = "m.gguf", CacheTypeK = "q8_0", CacheTypeV = "q8_0" } }));
        Assert.Equal("K q8_0 · V f16", UiFormat.KvCacheLabel(MakeModel("m") with { Runtime = RuntimeKind.LlamaCpp, LlamaCpp = new LlamaCppSettings { ModelFile = "m.gguf", CacheTypeK = "q8_0", CacheTypeV = "f16" } }));
        Assert.Equal("4-bit", UiFormat.KvCacheLabel(MakeModel("m") with { Runtime = RuntimeKind.Mlx, Mlx = new MlxSettings { ModelFolder = "m", KvBits = 4 } }));
        Assert.Equal("fp8_e5m2", UiFormat.KvCacheLabel(MakeModel("m") with { Runtime = RuntimeKind.Sglang, Container = new ContainerRuntimeSettings { Image = "i", ExtraArgs = ["--kv-cache-dtype=fp8_e5m2"] } }));
    }

    [Fact]
    public void Start_StartsAStoppedModel_AndStop_StopsAReadyOne()
    {
        var stopped = MakeModel("stopped-model");
        var ready = MakeModel("ready-model");
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(ready.Id, ModelState.Ready));
        using var ctx = Context(runtime, models: [stopped, ready]);

        var cut = ctx.Render<Models>();
        Assert.Equal("Start stopped-model", cut.Find("[data-testid=start-stopped-model]").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("[data-testid=stop-stopped-model]"));
        Assert.Empty(cut.FindAll("[data-testid=start-ready-model]"));

        cut.Find("[data-testid=start-stopped-model]").Click();
        cut.Find("[data-testid=stop-ready-model]").Click();

        Assert.Contains(("stopped-model", false), runtime.StartCalls);
        Assert.Contains("ready-model", runtime.StopCalls);
    }

    [Fact]
    public void StartOverMemory_OpensDialogWithExactNumbers_AndForceStartsOnConfirm()
    {
        var model = MakeModel("big-model", budget: 36);
        var runtime = new FakeModelRuntime { RefuseUnlessForce = true };
        using var ctx = Context(runtime, new MemorySnapshot(128, 29.4), models: [model]);

        var cut = ctx.Render<Models>();
        Assert.Equal("false", cut.Find("[data-testid=force-start-dialog]").GetAttribute("data-open"));

        cut.Find($"[data-testid=start-{model.Id}]").Click();

        Assert.Equal("true", cut.Find("[data-testid=force-start-dialog]").GetAttribute("data-open"));
        var numbers = cut.Find("[data-testid=force-start-numbers]").TextContent;
        Assert.Contains("36.0 GiB", numbers, StringComparison.Ordinal);
        Assert.Contains("21.4 GiB", numbers, StringComparison.Ordinal); // 29.4 free minus the 8 GiB reserve
        Assert.Contains("8.0 GiB", numbers, StringComparison.Ordinal);
        Assert.DoesNotContain((model.Id, true), runtime.StartCalls);

        cut.Find("[data-testid=force-start-dialog-confirm]").Click();

        Assert.Contains((model.Id, true), runtime.StartCalls);
        Assert.Equal("false", cut.Find("[data-testid=force-start-dialog]").GetAttribute("data-open"));
    }

    [Fact]
    public void StartOverMemory_CancelDoesNotStart()
    {
        var model = MakeModel("big-model", budget: 64);
        var runtime = new FakeModelRuntime { RefuseUnlessForce = true };
        using var ctx = Context(runtime, new MemorySnapshot(128, 40), models: [model]);

        var cut = ctx.Render<Models>();
        cut.Find($"[data-testid=start-{model.Id}]").Click();
        cut.Find("[data-testid=force-start-dialog-cancel]").Click();

        Assert.DoesNotContain((model.Id, true), runtime.StartCalls);
        Assert.Equal("false", cut.Find("[data-testid=force-start-dialog]").GetAttribute("data-open"));
    }

    [Fact]
    public void StartFailure_IsNamedInAnAlert()
    {
        var model = MakeModel("m");
        var runtime = new FakeModelRuntime { Failure = new InvalidOperationException("the node refused the container") };
        using var ctx = Context(runtime, models: [model]);

        var cut = ctx.Render<Models>();
        cut.Find("[data-testid=start-m]").Click();

        Assert.Equal("Couldn't start m: the node refused the container", cut.Find("[data-testid=models-error]").TextContent.Trim());
    }

    [Fact]
    public void States_ShowInPlace_StartingWithElapsedTime_FailedWithItsCauseAndLogs_StoppingDisabled()
    {
        var starting = MakeModel("starting-model");
        var failed = MakeModel("failed-model");
        var stopping = MakeModel("stopping-model");
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(starting.Id, ModelState.Starting, Since: DateTimeOffset.UtcNow.AddSeconds(-65)));
        runtime.SetStatus(new ModelStatus(failed.Id, ModelState.Failed,
            Error: "INFO loading\nTraceback (most recent call last):\ntorch.OutOfMemoryError: CUDA out of memory\nINFO shutting down"));
        runtime.SetStatus(new ModelStatus(stopping.Id, ModelState.Stopping, Since: DateTimeOffset.UtcNow));
        using var ctx = Context(runtime, models: [starting, failed, stopping]);

        var cut = ctx.Render<Models>();

        Assert.Contains("1m 0", cut.Find("[data-testid=state-starting-model-elapsed]").TextContent, StringComparison.Ordinal);
        Assert.Equal("starting", cut.Find("[data-testid=models-row-starting-model]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=stop-starting-model]")); // a start can be stopped

        var failure = cut.Find("[data-testid=failure-failed-model]");
        Assert.Contains("torch.OutOfMemoryError: CUDA out of memory", failure.QuerySelector(".failure-cause")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("models/failed-model/logs", failure.QuerySelector("a")!.GetAttribute("href"));
        Assert.NotNull(cut.Find("[data-testid=start-failed-model]"));

        var stoppingRow = cut.Find("[data-testid=models-row-stopping-model]");
        Assert.True(stoppingRow.QuerySelector(".models-act")!.HasAttribute("disabled"));
    }

    [Fact]
    public void TokS_IsLiveForReadyModels_AndADashOtherwise()
    {
        var ready = MakeModel("ready-model");
        var stopped = MakeModel("stopped-model");
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(ready.Id, ModelState.Ready));
        var kpis = new ModelKpis(
            Load: new ModelMetrics(2, 1, 0.25, GenerationTokensPerSecond: 54.2, PromptTokensPerSecond: 100),
            RequestsPerMinute: 6, TimeToFirstToken: null, InterTokenLatency: null, EndToEndLatency: null, QueueTime: null,
            PrefixCacheHitRate: null, SpecDecodeAcceptanceRate: null, PreemptionsPerMinute: 0,
            FinishedByReasonPerMinute: new Dictionary<string, double>(), ToolCallsPerMinute: 0);
        var history = new FakeMetricsHistory { LatestKpis = new Dictionary<string, ModelKpis> { [ready.Id] = kpis, [stopped.Id] = kpis } };
        using var ctx = Context(runtime, history: history, models: [ready, stopped]);

        var cut = ctx.Render<Models>();

        Assert.Equal("54.2", cut.Find("[data-testid=tok-s-ready-model]").TextContent);
        Assert.Equal("—", cut.Find("[data-testid=tok-s-stopped-model]").TextContent); // a stale sample is not shown
    }

    [Fact]
    public void PoolingWithoutHistory_ShowsInputThroughput()
    {
        var model = MakeModel("embedding") with { Tags = [ModelTags.Embedding] };
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new(model.Id, ModelState.Ready));
        using var ctx = Context(runtime, models: [model]);
        var reader = (FakeModelMetricsReader)ctx.Services.GetRequiredService<IModelMetricsReader>();
        reader.Set(model.Id, new(0, 0, 0, GenerationTokensPerSecond: 7, PromptTokensPerSecond: 123));

        var cut = ctx.Render<Models>();

        Assert.Equal("123", cut.Find("[data-testid=tok-s-embedding]").TextContent);
        Assert.Contains("input tok/s", cut.Find("[data-testid=models-row-embedding]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ServedName_LinksToTheModel_AndTheTourAnchorsAreInPlace()
    {
        var a = MakeModel("alpha");
        var b = MakeModel("beta");
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("alpha", ModelState.Ready));
        using var ctx = Context(runtime, models: [a, b]);

        var cut = ctx.Render<Models>();

        Assert.Equal("models/alpha/overview", cut.Find("[data-testid=models-link-alpha]").GetAttribute("href"));
        Assert.Equal("models-list", cut.Find("[data-testid=models-table]").GetAttribute("data-tour"));
        Assert.Equal("add-model", cut.Find("[data-testid=add-model]").GetAttribute("data-tour"));
        // Only the first row's Start/Stop is the tour's anchor.
        Assert.Equal("model-start", cut.Find("[data-testid=stop-alpha]").GetAttribute("data-tour"));
        Assert.Null(cut.Find("[data-testid=start-beta]").GetAttribute("data-tour"));
        Assert.Single(cut.FindAll("[data-tour=model-start]"));
    }
}
