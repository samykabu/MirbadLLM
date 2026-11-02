using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Pages;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// bUnit tests for the Benchmark page's History table: the settings summary renders as a small, muted
/// second line under the "When" cell (the same treatment ModelTune's candidate table gives a candidate's
/// settings), instead of its own wrapped "Settings" column.
/// </summary>
public sealed class ModelBenchmarkHistoryTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-bench-history-" + Guid.NewGuid().ToString("N"));

    private static readonly ModelDefinition Model = new() { Id = "qwen", ServedName = "Qwen", ModelPath = "Qwen", MemoryBudgetGiB = 44 };

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private static BenchmarkRun MakeRun(string id, ModelDefinition settings, BenchmarkResult? result = null) => new(
        id, "qwen", BenchmarkOrigin.OnDemand, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow,
        settings, FakeCluster.Hash(settings), result ?? Results.Make(15.7, 159), Error: null, Cancelled: false);

    private (Bunit.BunitContext Ctx, BenchmarkStore Store) MakeContext(BenchmarkRun run, ModelDefinition? model = null)
    {
        var registry = new FakeModelRegistry().Add(model ?? Model);
        var cluster = new FakeCluster().Running(model ?? Model);
        var suite = new FakeSuite(cluster);
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, VllmImage = "default-image" });
        var maintenance = new MaintenanceModeService(options);
        var store = new BenchmarkStore(options);
        store.SaveBenchmarkAsync(run).GetAwaiter().GetResult();

        var jobs = new ModelJobService(registry, cluster, cluster, suite, store, maintenance, new FakeMetricsHistory(), new FakeGpu(0),
            new StartupTimeTracker(), options, new ModelJobOptions(), NullLogger<ModelJobService>.Instance, new ContainerNetworkTargets());

        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(registry);
        ctx.Services.AddSingleton<IModelRuntime>(cluster);
        ctx.Services.AddSingleton<IModelLauncher>(cluster);
        ctx.Services.AddSingleton(jobs);
        ctx.Services.AddSingleton(store);
        return (ctx, store);
    }

    private static Bunit.IRenderedComponent<ModelBenchmark> RenderReady(Bunit.BunitContext ctx, string id)
    {
        var cut = ctx.Render<ModelBenchmark>(p => p.Add(x => x.Id, id));
        cut.WaitForState(() => cut.FindAll("[data-testid=benchmark-history]").Count > 0, TimeSpan.FromSeconds(10)); // MN-090: the 1 s default flakes under CI load
        return cut;
    }

    [Fact]
    public void HistoryRow_ShowsSettingsAsAMutedSecondLine_UnderWhen_NotItsOwnColumn()
    {
        var tuned = Model with { Speculative = new SpeculativeDecoding("mtp", 2), MaxNumSeqs = 16 };
        var run = MakeRun("run-1", tuned);
        var (ctx, _) = MakeContext(run);
        using var _ = ctx;

        var cut = RenderReady(ctx, "qwen");

        var table = cut.Find("[data-testid=benchmark-history]");

        // No standalone "Settings" column header or col-settings cell (and its 22ch wrap workaround) any more.
        Assert.Empty(table.QuerySelectorAll("td.col-settings"));
        var headerLabels = table.QuerySelectorAll("thead th span").Select(s => s.TextContent).ToList();
        Assert.DoesNotContain("Settings", headerLabels);

        var row = cut.Find("[data-testid=history-row-run-1]");
        var whenCell = row.QuerySelector("td.col-primary")!;

        var timestamp = whenCell.QuerySelector(".num")!;
        Assert.Equal(UiFormat.RunTime(run.StartedAt), timestamp.TextContent);

        // The settings summary sits as a second line, styled the same as Tune's candidate settings line.
        var settingsLine = whenCell.QuerySelector(".model-card-id")!;
        Assert.Equal(SettingChanges.Summary(tuned), settingsLine.TextContent);
        Assert.Contains("MTP", settingsLine.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryTable_KeepsColumnHelp_ForTheMergedWhenColumn()
    {
        var run = MakeRun("run-1", Model);
        var (ctx, _) = MakeContext(run);
        using var _ = ctx;

        var cut = RenderReady(ctx, "qwen");

        Assert.NotNull(cut.Find("[data-testid=colhelp-bench-history-settings]"));
        Assert.NotNull(cut.Find("[data-testid=colhelp-bench-history-n1]"));
    }

    [Fact]
    public async Task History_IsPaged_AndTheComparePickSurvivesAPageChange()
    {
        var (ctx, store) = MakeContext(MakeRun("run-00", Model));
        using var _ = ctx;
        for (int i = 1; i < 30; i++)
        {
            await store.SaveBenchmarkAsync(MakeRun($"run-{i:00}", Model), Xunit.TestContext.Current.CancellationToken);
        }

        var cut = RenderReady(ctx, "qwen");

        Assert.Equal(25, cut.FindAll("[data-testid=benchmark-history] tbody tr").Count);
        Assert.Equal("1–25 of 30 runs", cut.Find("[data-testid=benchmark-history-pager-range]").TextContent);
        Assert.Equal("Pagination", cut.Find("[data-testid=benchmark-history-pager]").GetAttribute("aria-label"));

        var first = cut.Find("[data-testid=benchmark-history] tbody tr input[type=checkbox]");
        string firstId = first.GetAttribute("data-testid")!;
        cut.ChangeAt($"[data-testid={firstId}]", true);
        cut.ClickAt("[data-testid=benchmark-history-pager-next]");
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid=benchmark-history] tbody tr").Count), TimeSpan.FromSeconds(5));
        cut.ClickAt("[data-testid=benchmark-history-pager-prev]");
        cut.WaitForAssertion(() => Assert.True(cut.Find($"[data-testid={firstId}]").HasAttribute("checked")), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void EmbeddingModel_HistoryShowsInputTokS_LatencyAndVectors_NotGenerationColumns()
    {
        var embedder = Model with { ExtraArgs = ["--runner", "pooling"], ToolCallParser = null, ReasoningParser = null };
        var run = MakeRun("run-1", embedder, Results.Embeddings(in1: 1234, in16: 5678, p95At16: 0.25));
        var (ctx, _) = MakeContext(run, embedder);
        using var _ = ctx;

        var cut = RenderReady(ctx, "qwen");

        var headers = cut.FindAll("[data-testid=benchmark-history] thead th").Select(th => th.TextContent).ToList();
        Assert.Contains(headers, h => h.Contains("N=1 input", StringComparison.Ordinal));
        Assert.Contains(headers, h => h.Contains("Latency p95 N=16", StringComparison.Ordinal));
        Assert.Contains(headers, h => h.Contains("Vectors", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, h => h.Contains("TTFT", StringComparison.Ordinal) || h.Contains("Answers", StringComparison.Ordinal));

        var row = cut.Find("[data-testid=history-row-run-1]").TextContent;
        Assert.Contains("1234 tok/s", row, StringComparison.Ordinal);
        Assert.Contains("5678 tok/s", row, StringComparison.Ordinal);
        Assert.Contains("Valid", row, StringComparison.Ordinal);

        Assert.NotNull(cut.Find("[data-testid=benchmark-embedding-intro]"));
        Assert.NotNull(cut.Find("[data-testid=benchmark-result-embeddings]"));
        Assert.Empty(cut.FindAll("[data-testid=benchmark-result-levels]"));
        Assert.Empty(cut.FindAll("[data-testid=benchmark-result-tool]"));
        Assert.Contains("Valid", cut.Find("[data-testid=benchmark-result-vectors]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void PresetPicker_OffersTheGenerationPresets_PlusTheClassicSuite()
    {
        var (ctx, _) = MakeContext(MakeRun("run-1", Model));
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");

        var options = cut.FindAll("[data-testid=benchmark-preset] option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Equal(["InteractiveChat", "AgentsTools", "Throughput", "LongDocuments", "classic"], options);
        Assert.Contains("One person chatting", cut.Find("[data-testid=benchmark-workload-intro]").TextContent, StringComparison.Ordinal);

        cut.ChangeAt("[data-testid=benchmark-preset]", "AgentsTools");
        // The intro re-renders after the change event; wait for it instead of reading the previous markup.
        cut.WaitForAssertion(() => Assert.Contains("JSON schema", cut.Find("[data-testid=benchmark-workload-intro]").TextContent, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        Assert.Contains("shared ~8K-token system prompt", cut.Find("[data-testid=benchmark-workload-intro]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void PresetPicker_ForAnEmbeddingModel_OffersEmbeddingsAndClassic()
    {
        var embedder = Model with { ExtraArgs = ["--runner", "pooling"], ToolCallParser = null, ReasoningParser = null };
        var (ctx, _) = MakeContext(MakeRun("run-1", embedder, Results.Embeddings(1000, 4000)), embedder);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");

        var options = cut.FindAll("[data-testid=benchmark-preset] option").Select(o => o.GetAttribute("value")).ToList();
        Assert.Equal(["Embeddings", "classic"], options);
    }
}
