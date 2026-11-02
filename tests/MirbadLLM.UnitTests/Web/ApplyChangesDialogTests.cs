using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Ui;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// MN-132 (W9 review): "Apply changes now?" refuses the restart of a model a job is measuring by the job on the model's
/// own node (D83: with several nodes the oldest job overall is another node's), and a model another record holds (409
/// <c>model_busy</c>, D101) is refused with the holder named, never an exception text. Nothing is restarted.
/// </summary>
public sealed class ApplyChangesDialogTests : IDisposable
{
    private static readonly ModelDefinition Qwen = Model("qwen", "dgx");
    private static readonly ModelDefinition Gemma = Model("gemma", "pc-office");

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-apply-dialog-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private static ModelDefinition Model(string id, string node) => new()
    {
        Id = id, ServedName = id, ModelPath = id, MemoryBudgetGiB = 10, Placement = new ModelPlacement(node),
    };

    [Fact]
    public async Task Restart_WhileABenchmarkMeasuresTheModelOnItsOwnNode_IsRefused_EvenWhenAnOlderJobRunsElsewhere()
    {
        var registry = new FakeModelRegistry().Add(Qwen).Add(Gemma);
        var cluster = new FakeCluster().Running(Qwen).Running(Gemma);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var suite = new FakeSuite(cluster) { Gate = gate };
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, VllmImage = "default-image" });
        var jobs = new ModelJobService(registry, cluster, cluster, suite, new BenchmarkStore(options), new MaintenanceModeService(),
            new FakeMetricsHistory(), new FakeGpu(0), new StartupTimeTracker(), options,
            new ModelJobOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                DrainTimeout = TimeSpan.FromMilliseconds(10),
                GpuIdleSampleDuration = TimeSpan.FromMilliseconds(1),
                GpuIdleSampleInterval = TimeSpan.FromMilliseconds(1),
            },
            NullLogger<ModelJobService>.Instance, new ContainerNetworkTargets(), bootstrapNodeId: "dgx", nodeScopedMaintenance: true);
        try
        {
            await jobs.StartBenchmarkAsync(Qwen.Id, Ct);
            await jobs.StartBenchmarkAsync(Gemma.Id, Ct);
            Assert.Equal(Qwen.Id, jobs.Current?.ModelId); // the oldest job is dgx's, not the one on gemma's node

            var runtime = new FakeModelRuntime();
            using var ctx = Context(runtime);
            ctx.Services.AddSingleton(jobs);
            var cut = ctx.Render<ApplyChangesDialog>();

            await cut.InvokeAsync(() => cut.Instance.OpenAsync(Gemma, [], 10));
            cut.Find("[data-testid=apply-dialog-confirm]").Click();

            Assert.Equal("A benchmark is measuring gemma. Wait for it to finish or cancel it, then restart; the settings are saved.",
                cut.Find("[data-testid=apply-error]").TextContent.Trim());
            Assert.Equal("true", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open"));
            Assert.Empty(runtime.StopCalls);
            Assert.Empty(runtime.StartCalls);
        }
        finally
        {
            gate.TrySetResult();
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        }
    }

    [Fact]
    public async Task Restart_OfAModelAMoveHolds_NamesTheMove_AndRestartsNothing()
    {
        var runtime = new FakeModelRuntime();
        using var ctx = Context(runtime);
        ctx.Services.AddSingleton<IOwnershipStore>(new DashboardGroupingTests.OneRecordStore(DashboardGroupingTests.Holder(OwnershipKind.Move, Qwen.Id)));
        var cut = ctx.Render<ApplyChangesDialog>();

        await cut.InvokeAsync(() => cut.Instance.OpenAsync(Qwen, [], 10));
        cut.Find("[data-testid=apply-dialog-confirm]").Click();

        Assert.Equal(
            "Couldn't restart qwen: a move to node pc (stage downloading) is running on it. Wait for the move to finish, or cancel it " +
            "in the model's editor. The settings are saved.",
            cut.Find("[data-testid=apply-error]").TextContent.Trim());
        Assert.Empty(runtime.StopCalls);
        Assert.Empty(runtime.StartCalls);
    }

    private static BunitContext Context(FakeModelRuntime runtime)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRuntime>(runtime);
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        return ctx;
    }
}
