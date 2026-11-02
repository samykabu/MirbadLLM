using System.Runtime.CompilerServices;
using System.Text.Json;
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
/// "Apply best" on a tuning run recorded before the canonical config (the live 35B run of 2026-09-30 23:39 UTC,
/// verbatim from the DGX). Its candidates carry a typed MTP × N and, in ExtraArgs, the raw
/// <c>--speculative-config {mtp, 3, moe_backend triton}</c> that the old builder emitted last — so every MTP row
/// actually ran MTP × 3. Applying used to save MTP × 3 while the page kept offering "Apply best … MTP × 2"
/// (the stored hash never matches a canonical one), and a refused restart was reported outside the modal.
/// </summary>
public sealed class ModelTuneLegacyApplyTests : IDisposable
{
    private const string ModelId = "qwen36-35b-a3b-nvfp4";
    private const string RunId = "20260930T233921Z";

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-tune-legacy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // The page's poll loop can still be reading a run file for a moment after the context is disposed.
        for (int attempt = 0; attempt < 20 && Directory.Exists(_dataPath); attempt++)
        {
            try
            {
                Directory.Delete(_dataPath, recursive: true);
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static string FixturePath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", $"tuning-legacy-qwen36-35b-{RunId}.json");

    private static TuningRun LoadRun() =>
        JsonSerializer.Deserialize<TuningRun>(File.ReadAllText(FixturePath()), BenchmarkStore.JsonOptions)!;

    /// <summary>The saved definition before the apply: the run's baseline as the registry migrates it (normalized).</summary>
    private static ModelDefinition SavedBefore(TuningRun run) => VllmArgsNormalizer.Apply(run.Baseline);

    private sealed record Setup(BunitContext Ctx, FakeModelRegistry Registry, FakeCluster Cluster, TuningRun Run);

    private Setup MakeContext(TimeSpan? restartWait = null)
    {
        var run = LoadRun();
        var registry = new FakeModelRegistry().Add(SavedBefore(run));
        var cluster = new FakeCluster().Running(SavedBefore(run));
        cluster.RestartDefinition = id => registry.GetAsync(id).GetAwaiter().GetResult();
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, VllmImage = "default-image" });
        var store = new BenchmarkStore(options);
        Directory.CreateDirectory(Path.Combine(_dataPath, "tuning", ModelId));
        File.Copy(FixturePath(), Path.Combine(_dataPath, "tuning", ModelId, RunId + ".json"));

        var jobs = new ModelJobService(registry, cluster, cluster, new FakeSuite(cluster), store, new MaintenanceModeService(options),
            new FakeMetricsHistory(), new FakeGpu(0), new StartupTimeTracker(), options, new ModelJobOptions(), NullLogger<ModelJobService>.Instance, new ContainerNetworkTargets());

        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(registry);
        ctx.Services.AddSingleton<IModelRuntime>(cluster);
        ctx.Services.AddSingleton<IModelLauncher>(cluster);
        ctx.Services.AddSingleton(jobs);
        ctx.Services.AddSingleton(store);
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(121, 30)));
        if (restartWait is { } wait)
        {
            ctx.Services.AddSingleton(new ApplyRestartOptions(wait));
        }

        return new Setup(ctx, registry, cluster, run);
    }

    private static IRenderedComponent<ModelTune> RenderReady(BunitContext ctx)
    {
        var cut = ctx.Render<ModelTune>(p => p.Add(x => x.Id, ModelId));
        cut.WaitForState(() => cut.FindAll("[data-testid=tune-winner]").Count > 0, TimeSpan.FromSeconds(5));
        return cut;
    }

    private static void OpenApplyDialog(IRenderedComponent<ModelTune> cut)
    {
        cut.ClickAt("[data-testid=apply-best]");
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open")), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Fixture_IsTheLegacyShape_TypedMtp2_WithARawMtp3OverrideInExtraArgs()
    {
        var run = LoadRun();
        var winner = run.Winner!;
        Assert.Equal(7, winner.Index);
        Assert.Equal(new SpeculativeDecoding("mtp", 2), winner.Settings.Speculative! with { Extra = JsonFields.Empty });
        Assert.Contains("--speculative-config", winner.Settings.ExtraArgs);
        Assert.Null(winner.EffectiveConfig);
    }

    [Fact]
    public void MergeTuned_LegacyCandidate_SavesWhatItMeasured_TheRawFlagWins()
    {
        var run = LoadRun();
        var merged = ModelJobService.MergeTuned(SavedBefore(run), run.Winner!.Settings);

        Assert.Equal("mtp", merged.Speculative!.Method);
        Assert.Equal(3, merged.Speculative.NumSpeculativeTokens);
        Assert.Equal("triton", merged.Speculative.Extra.GetString("moe_backend"));
        Assert.Equal(32, merged.MaxNumSeqs);
        Assert.Equal(16_384, merged.MaxNumBatchedTokens);
        Assert.Equal("PIECEWISE", merged.CudaGraphMode);
        Assert.Equal("fp8", merged.KvCacheDtype);
        Assert.DoesNotContain(merged.ExtraArgs, a => VllmArgsNormalizer.IsOwnedFlag(a));

        // Idempotent: merging the winner onto the result changes nothing (the "Current" check relies on it).
        Assert.Equal(FakeCluster.Hash(merged), FakeCluster.Hash(ModelJobService.MergeTuned(merged, run.Winner.Settings)));
    }

    [Fact]
    public void LegacyWinner_SaysWhatItActuallyRan_AndThatApplySavesIt()
    {
        var setup = MakeContext();
        using var ctx = setup.Ctx;
        var cut = RenderReady(ctx);

        Assert.Equal("MTP × 3 · graphs PIECEWISE · seqs 32 · batched 16,384 · KV fp8", cut.Find("[data-testid=tune-winner-summary]").TextContent);
        string note = cut.Find("[data-testid=tune-winner-legacy]").TextContent;
        Assert.Contains("--speculative-config", note, StringComparison.Ordinal);
        Assert.Contains("measured MTP × 3", note, StringComparison.Ordinal);
        Assert.Contains("not MTP × 2", note, StringComparison.Ordinal);
        Assert.Equal("MTP × 3 · graphs PIECEWISE · seqs 32 · batched 16,384 · KV fp8", cut.Find("[data-testid=candidate-effective-7]").TextContent);
    }

    [Fact]
    public async Task ApplyBest_RestartNow_SavesTheWinner_RestartsOnce_ClosesTheDialog_AndTheWinnerIsThenCurrent()
    {
        var setup = MakeContext();
        using var ctx = setup.Ctx;
        var cut = RenderReady(ctx);

        OpenApplyDialog(cut);
        var saved = (await setup.Registry.GetAsync(ModelId, Xunit.TestContext.Current.CancellationToken))!;
        Assert.Equal(32, saved.MaxNumSeqs);
        Assert.Equal(16_384, saved.MaxNumBatchedTokens);
        Assert.Equal("PIECEWISE", saved.CudaGraphMode);
        Assert.Equal(3, saved.Speculative!.NumSpeculativeTokens);

        cut.ClickAt("[data-testid=apply-dialog-confirm]");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("false", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open"));
            Assert.Contains("restarting", cut.Find("[data-testid=tune-info]").TextContent, StringComparison.Ordinal);
            // Applied means applied: no second "Apply best" for the settings that now run.
            Assert.Contains("Already the model's current settings", cut.Find("[data-testid=tune-winner]").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=apply-best]"));
            Assert.NotNull(cut.Find("[data-testid=candidate-current-7]"));
        }, TimeSpan.FromSeconds(5));
        Assert.Equal([(ModelId, false)], setup.Cluster.Restarts);
        Assert.Equal(32, setup.Cluster.CurrentDefinition(ModelId)!.MaxNumSeqs);
        Assert.Empty(cut.FindAll("[data-testid=apply-error]"));
    }

    [Fact]
    public void RestartRefused_AnotherModelStarting_ShowsWhyInsideTheDialog_AndTheConfirmButtonWorksAgain()
    {
        var setup = MakeContext();
        using var ctx = setup.Ctx;
        setup.Cluster.Restart = (_, _) => throw new ModelStartConflictException("Model 'qwen3-embedding-4b' is still starting.") { StartingModelId = "qwen3-embedding-4b" };
        var cut = RenderReady(ctx);
        OpenApplyDialog(cut);

        cut.ClickAt("[data-testid=apply-dialog-confirm]");

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.Find("[data-testid=apply-dialog]");
            Assert.Equal("true", dialog.GetAttribute("data-open"));
            var error = dialog.QuerySelector("[data-testid=apply-error]");
            Assert.NotNull(error);
            Assert.Equal("alert", error!.GetAttribute("role"));
            Assert.Contains("qwen3-embedding-4b is still starting", error.TextContent, StringComparison.Ordinal);
            Assert.Contains("the settings are saved", error.TextContent, StringComparison.Ordinal);
            Assert.False(cut.Find("[data-testid=apply-dialog-confirm]").HasAttribute("disabled"));
        }, TimeSpan.FromSeconds(5));

        // Once the other model is Ready, the same dialog restarts.
        setup.Cluster.Restart = null;
        cut.ClickAt("[data-testid=apply-dialog-confirm]");
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open")), TimeSpan.FromSeconds(5));
        Assert.Equal(2, setup.Cluster.Restarts.Count);
    }

    [Fact]
    public async Task RestartThatNeverAnswers_IsBounded_TheDialogSaysSo_AndDoesNotHang()
    {
        var setup = MakeContext(restartWait: TimeSpan.FromSeconds(1));
        using var ctx = setup.Ctx;
        var never = new TaskCompletionSource<ModelStatus>();
        setup.Cluster.Restart = (_, _) => never.Task;
        var cut = RenderReady(ctx);
        OpenApplyDialog(cut);

        // Not awaited: the handler is still waiting on Docker while the progress line shows.
        var click = cut.ClickAtAsync("[data-testid=apply-dialog-confirm]");

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Stopping", cut.Find("[data-testid=apply-progress]").TextContent, StringComparison.Ordinal);
            Assert.True(cut.Find("[data-testid=apply-dialog-confirm]").HasAttribute("disabled"));
        }, TimeSpan.FromSeconds(5));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open"));
            Assert.Contains("hasn't confirmed the restart", cut.Find("[data-testid=apply-error]").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=apply-progress]"));
            Assert.False(cut.Find("[data-testid=apply-dialog-confirm]").HasAttribute("disabled"));
        }, TimeSpan.FromSeconds(5));
        await click.WaitAsync(TimeSpan.FromSeconds(5), Xunit.TestContext.Current.CancellationToken);
    }

    [Fact]
    public void RestartDockerError_IsShownInsideTheDialog()
    {
        var setup = MakeContext();
        using var ctx = setup.Ctx;
        setup.Cluster.Restart = (_, _) => throw new HttpRequestException("Connection refused (docker.sock)");
        var cut = RenderReady(ctx);
        OpenApplyDialog(cut);

        cut.ClickAt("[data-testid=apply-dialog-confirm]");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("true", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open"));
            Assert.Contains("Connection refused", cut.Find("[data-testid=apply-dialog] [data-testid=apply-error]").TextContent, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void RestartRefusedForMemory_MovesToTheForceDialog()
    {
        var setup = MakeContext();
        using var ctx = setup.Ctx;
        setup.Cluster.Restart = (_, force) => force
            ? Task.FromResult(new ModelStatus(ModelId, ModelState.Starting))
            : throw new ModelMemoryAdmissionException("needs 48 GiB");
        var cut = RenderReady(ctx);
        OpenApplyDialog(cut);

        cut.ClickAt("[data-testid=apply-dialog-confirm]");

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("false", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open"));
            Assert.Equal("true", cut.Find("[data-testid=force-restart-dialog]").GetAttribute("data-open"));
        }, TimeSpan.FromSeconds(5));

        cut.ClickAt("[data-testid=force-restart-dialog-confirm]");
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid=force-restart-dialog]").GetAttribute("data-open")), TimeSpan.FromSeconds(5));
        Assert.Equal([(ModelId, false), (ModelId, true)], setup.Cluster.Restarts);
    }

    [Fact]
    public void SaveFailure_IsShownOnThePage_AndNoDialogOpens()
    {
        var setup = MakeContext();
        using var ctx = setup.Ctx;
        setup.Registry.UpsertThrows = new IOException("Disk full");
        var cut = RenderReady(ctx);

        cut.ClickAt("[data-testid=apply-best]");

        cut.WaitForAssertion(() => Assert.Contains("Disk full", cut.Find("[data-testid=tune-error]").TextContent, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.Equal("false", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open"));
        Assert.Empty(setup.Cluster.Restarts);
    }
}
