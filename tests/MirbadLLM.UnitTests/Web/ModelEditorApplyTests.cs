using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;
using FakeHttpMessageHandler = MirbadLLM.UnitTests.Orchestration.Fakes.FakeHttpMessageHandler;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// Save on a running model offers to apply the change (restart) with the changed settings listed; saving a
/// stopped model or only AutoStart just saves. Also the MTP → PIECEWISE advisory.
/// </summary>
public sealed class ModelEditorApplyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-apply-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly ModelDefinition Saved = new()
    {
        Id = "qwen",
        ServedName = "Qwen",
        ModelPath = "Qwen-NVFP4",
        MemoryBudgetGiB = 44,
        Speculative = new SpeculativeDecoding("mtp", 2),
    };

    public ModelEditorApplyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private BunitContext MakeContext(FakeModelRegistry registry, FakeModelRuntime runtime)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(registry);
        ctx.Services.AddSingleton<IModelRuntime>(runtime);
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(121, 30)));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        ctx.Services.AddNodeSourcing(_root);
        ctx.Services.AddSingleton<IHuggingFaceDownloader>(new FakeHuggingFaceDownloader());
        ctx.Services.AddSingleton<IHuggingFaceRepoInspector>(new FakeHuggingFaceRepoInspector());
        return ctx;
    }

    [Fact]
    public async Task RunningModel_RestartNeedingChange_ShowsTheApplyDialog_WithOldToNew()
    {
        var registry = new FakeModelRegistry([Saved]);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("qwen", ModelState.Ready, HostPort: 8102));
        using var ctx = MakeContext(registry, runtime);
        var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "qwen"));

        cut.Find("[data-testid=field-cuda-graph-mode]").Change("PIECEWISE");
        cut.Find("[data-testid=model-form]").Submit();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open")));
        Assert.Equal("Apply changes now?", cut.Find("[data-testid=apply-dialog] h2").TextContent);
        var items = cut.FindAll("[data-testid=apply-changes] li").Select(li => string.Join(' ', li.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))).ToList();
        Assert.Equal(["CUDA graph mode: default → PIECEWISE"], items);
        Assert.Contains("clients get 503 'starting' until it is Ready", cut.Find("[data-testid=apply-estimate]").TextContent, StringComparison.Ordinal);
        Assert.Equal("Restart now", cut.Find("[data-testid=apply-dialog-confirm]").TextContent.Trim());
        Assert.Equal("Later", cut.Find("[data-testid=apply-dialog-cancel]").TextContent.Trim());

        // Saved before asking.
        Assert.Equal("PIECEWISE", (await registry.GetAsync("qwen", Xunit.TestContext.Current.CancellationToken))!.CudaGraphMode);
    }

    [Fact]
    public void RestartNow_RestartsTheModel()
    {
        var registry = new FakeModelRegistry([Saved]);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("qwen", ModelState.Ready, HostPort: 8102));
        using var ctx = MakeContext(registry, runtime);
        var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "qwen"));

        cut.Find("[data-testid=field-max-num-seqs]").Change("32");
        cut.Find("[data-testid=model-form]").Submit();
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open")));
        cut.Find("[data-testid=apply-dialog-confirm]").Click();

        // The fake's default RestartAsync is stop + start.
        cut.WaitForAssertion(() => Assert.Equal(["qwen"], runtime.StopCalls));
        Assert.Equal([("qwen", false)], runtime.StartCalls);
    }

    [Fact]
    public async Task OnlyAutoStartChanged_JustSaves()
    {
        var registry = new FakeModelRegistry([Saved]);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("qwen", ModelState.Ready, HostPort: 8102));
        using var ctx = MakeContext(registry, runtime);
        var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "qwen"));

        cut.Find("#autoStart").Change(true);
        cut.Find("[data-testid=model-form]").Submit();

        Assert.Equal("false", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open"));
        Assert.True((await registry.GetAsync("qwen", Xunit.TestContext.Current.CancellationToken))!.AutoStart);
        Assert.Empty(runtime.StopCalls);
    }

    [Fact]
    public async Task StoppedModel_JustSaves()
    {
        var registry = new FakeModelRegistry([Saved]);
        using var ctx = MakeContext(registry, new FakeModelRuntime());
        var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "qwen"));

        cut.Find("[data-testid=field-cuda-graph-mode]").Change("PIECEWISE");
        cut.Find("[data-testid=model-form]").Submit();

        Assert.Equal("false", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open"));
        Assert.Equal("PIECEWISE", (await registry.GetAsync("qwen", Xunit.TestContext.Current.CancellationToken))!.CudaGraphMode);
    }

    [Fact]
    public void Mtp_WithoutPiecewise_ShowsTheAdvisory_AndOneClickFixesIt()
    {
        using var ctx = MakeContext(new FakeModelRegistry([Saved]), new FakeModelRuntime());
        var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "qwen"));

        var advice = cut.Find("[data-testid=mtp-piecewise-advice]");
        Assert.Contains("MTP on the GB10 needs PIECEWISE; FULL crashed in cuBLAS in Phase 0.", advice.TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=set-piecewise]").Click();

        Assert.Empty(cut.FindAll("[data-testid=mtp-piecewise-advice]"));
        Assert.Equal("PIECEWISE", cut.Find("[data-testid=field-cuda-graph-mode]").GetAttribute("value"));
    }

    [Fact]
    public async Task BatchedTokens_IsEditable_AndSaved()
    {
        var registry = new FakeModelRegistry([Saved with { CudaGraphMode = "PIECEWISE" }]);
        using var ctx = MakeContext(registry, new FakeModelRuntime());
        var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "qwen"));

        cut.Find("[data-testid=field-max-num-batched-tokens]").Change("8192");
        cut.Find("[data-testid=model-form]").Submit();

        Assert.Equal(8192, (await registry.GetAsync("qwen", Xunit.TestContext.Current.CancellationToken))!.MaxNumBatchedTokens);
    }

    private sealed class FakeMaintenanceMode(MaintenanceState? current) : IMaintenanceMode
    {
        public MaintenanceState? Current { get; } = current;
    }

    [Fact]
    public async Task RunningModel_JobActiveOnIt_SavesButStaysOnThePage_WithTheDeferredRestartNotice()
    {
        var registry = new FakeModelRegistry([Saved]);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("qwen", ModelState.Ready, HostPort: 8102));
        using var ctx = MakeContext(registry, runtime);
        ctx.Services.AddSingleton<IMaintenanceMode>(new FakeMaintenanceMode(
            new MaintenanceState("job-1", "tuning", "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5))));
        var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "qwen"));

        cut.Find("[data-testid=field-cuda-graph-mode]").Change("PIECEWISE");
        cut.Find("[data-testid=model-form]").Submit();

        // Saved, but the apply dialog never opens and the page doesn't navigate away.
        Assert.Equal("PIECEWISE", (await registry.GetAsync("qwen", Xunit.TestContext.Current.CancellationToken))!.CudaGraphMode);
        Assert.Equal("false", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open"));
        Assert.NotNull(cut.Find("[data-testid=field-cuda-graph-mode]"));

        Assert.Equal(
            "Saved. The restart is blocked while tuning runs; apply it from the Dashboard when the job finishes.",
            cut.Find("[data-testid=save-notice]").TextContent.Trim());
    }

    [Fact]
    public async Task RunningModel_JobActiveOnAnotherModel_StillOffersTheApplyDialog()
    {
        var registry = new FakeModelRegistry([Saved]);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("qwen", ModelState.Ready, HostPort: 8102));
        using var ctx = MakeContext(registry, runtime);
        ctx.Services.AddSingleton<IMaintenanceMode>(new FakeMaintenanceMode(
            new MaintenanceState("job-1", "benchmark", "some-other-model", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5))));
        var cut = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "qwen"));

        cut.Find("[data-testid=field-cuda-graph-mode]").Change("PIECEWISE");
        cut.Find("[data-testid=model-form]").Submit();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open")));
        Assert.Empty(cut.FindAll("[data-testid=save-notice]"));
    }
}
