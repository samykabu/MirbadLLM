using System.Globalization;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// bUnit tests for the per-candidate "Apply" action on the Tune page: shown only on accepted rows, a "Current"
/// badge driven by settings/config-hash equality (not the "Baseline" label), and that applying saves only the
/// tuned fields and opens the existing restart dialog with the changes.
/// </summary>
public sealed class ModelTuneApplyTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-tune-apply-" + Guid.NewGuid().ToString("N"));

    private static readonly ModelDefinition Baseline = new() { Id = "qwen", ServedName = "Qwen", ModelPath = "Qwen", MemoryBudgetGiB = 44 };

    private static readonly ModelDefinition Tuned = Baseline with { Speculative = new SpeculativeDecoding("mtp", 2) };

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private static TuningCandidateResult MakeCandidate(int index, CandidateOutcome outcome, ModelDefinition settings) => new(
        Index: index,
        Step: "Speculative decoding",
        Label: index == 0 ? "Baseline" : $"MTP × {index}",
        Settings: settings,
        ConfigHash: FakeCluster.Hash(settings),
        Outcome: outcome,
        Reason: outcome == CandidateOutcome.Accepted ? null : "did not clear the guardrail",
        Result: null,
        Score: outcome == CandidateOutcome.Accepted ? 1.0 : null,
        StartupSeconds: null);

    private static TuningRun MakeRun(string id, params TuningCandidateResult[] candidates) => new(
        id, "qwen", TuningGoal.Balanced, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow,
        TuningRunStatus.Completed, Baseline, candidates, WinnerIndex: candidates[^1].Index, WinnerGainPercent: 12.5, Error: null, Notes: []);

    private (Bunit.BunitContext Ctx, FakeModelRegistry Registry, FakeCluster Cluster, BenchmarkStore Store) MakeContext(ModelDefinition saved, TuningRun run)
    {
        var registry = new FakeModelRegistry().Add(saved);
        var cluster = new FakeCluster().Running(saved);
        var suite = new FakeSuite(cluster);
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, VllmImage = "default-image" });
        var maintenance = new MaintenanceModeService(options);
        var store = new BenchmarkStore(options);
        store.SaveTuningAsync(run).GetAwaiter().GetResult();

        var jobs = new ModelJobService(registry, cluster, cluster, suite, store, maintenance, new FakeMetricsHistory(), new FakeGpu(0),
            new StartupTimeTracker(), options, new ModelJobOptions(), NullLogger<ModelJobService>.Instance, new ContainerNetworkTargets());

        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(registry);
        ctx.Services.AddSingleton<IModelRuntime>(cluster);
        ctx.Services.AddSingleton<IModelLauncher>(cluster);
        ctx.Services.AddSingleton(jobs);
        ctx.Services.AddSingleton(store);
        return (ctx, registry, cluster, store);
    }

    /// <summary>Renders the page and waits for the async-loaded results table (history, preview, config hash
    /// all come from real file/async I/O against the fakes, so the first synchronous render is a loading
    /// state) before the caller asserts on it.</summary>
    private static Bunit.IRenderedComponent<ModelTune> RenderReady(Bunit.BunitContext ctx, string id)
    {
        var cut = ctx.Render<ModelTune>(p => p.Add(x => x.Id, id));
        cut.WaitForState(() => cut.FindAll("[data-testid=tune-results]").Count > 0, TimeSpan.FromSeconds(10)); // MN-090: the 1 s default flakes under CI load
        return cut;
    }

    /// <summary>A llama.cpp model whose tune page shows the small tune's four steps (MN-230).</summary>
    private static readonly ModelDefinition LlamaCppModel = new()
    {
        Id = "qwen3-8b-win",
        ServedName = "qwen3-8b",
        ModelPath = "qwen3-8b",
        MemoryBudgetGiB = 20,
        Runtime = RuntimeKind.LlamaCpp,
        Placement = new ModelPlacement("win-1", ["GPU-4090"]),
        LlamaCpp = new LlamaCppSettings
        {
            ModelFile = "qwen3-8b/Qwen3-8B-Q4_K_M.gguf",
            ContextSize = 8192,
            Parallel = 4,
            GpuLayers = 999,
            Delivery = LaunchDelivery.Process,
            ExecutableVersion = "b11476",
        },
    };

    [Fact]
    public void LlamaCppModel_StartsWithEveryStepTicked_AndUntickingOneNarrowsTheRest()
    {
        // MN-230 (D130): _steps started at SearchPlanner.Quick (two vLLM dimensions), which names no llama.cpp
        // dimension, so LlamaCppTuning.Included ran the whole small tune while all four checkboxes read unticked —
        // and ticking one narrowed the plan to just that one. The page now ticks the small tune's own steps.
        var (ctx, _, _, _) = MakeContext(LlamaCppModel, MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, LlamaCppModel)));
        using var _ = ctx;
        // The history belongs to another model, so this page has no results table: wait for the plan itself.
        var cut = ctx.Render<ModelTune>(p => p.Add(x => x.Id, LlamaCppModel.Id));
        cut.WaitForElement("[data-testid=tune-plan]", TimeSpan.FromSeconds(10));

        Assert.All(LlamaCppTuning.Dimensions, d => Assert.True(cut.Find($"[data-testid=step-{d}]").HasAttribute("checked"), d.ToString()));
        Assert.Empty(cut.FindAll("[data-testid=search-Quick]")); // no Quick preset for the small tune
        Assert.True(cut.Find("[data-testid=search-Full]").HasAttribute("checked"));
        Assert.Contains("whole small tune", cut.Find("[data-testid=search-description]").TextContent, StringComparison.Ordinal);
        int all = int.Parse(cut.Find("[data-testid=tune-candidates] .num").TextContent, CultureInfo.InvariantCulture);

        cut.ChangeAt($"[data-testid=step-{TuningDimension.Parallel}]", false);

        cut.WaitForAssertion(
            () => Assert.True(int.Parse(cut.Find("[data-testid=tune-candidates] .num").TextContent, CultureInfo.InvariantCulture) < all),
            TimeSpan.FromSeconds(5));
        Assert.False(cut.Find($"[data-testid=step-{TuningDimension.Parallel}]").HasAttribute("checked"));
        Assert.All(LlamaCppTuning.Dimensions.Where(d => d != TuningDimension.Parallel),
            d => Assert.True(cut.Find($"[data-testid=step-{d}]").HasAttribute("checked"), d.ToString()));
    }

    [Fact]
    public void CustomSearch_TicksEachValue_AndUntickingOneLeavesItOutOfItsStep()
    {
        var (ctx, _, _, _) = MakeContext(LlamaCppModel, MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, LlamaCppModel)));
        using var _ = ctx;
        var cut = ctx.Render<ModelTune>(p => p.Add(x => x.Id, LlamaCppModel.Id));
        cut.WaitForElement("[data-testid=tune-plan]", TimeSpan.FromSeconds(10));
        string values = $"[data-testid^=value-{TuningDimension.Parallel}-]";

        // A preset tries every value of its steps, so it offers none to untick.
        Assert.Empty(cut.FindAll("[data-testid^=value-]"));

        cut.ChangeAt("[data-testid=search-Custom]", true);

        cut.WaitForAssertion(() => Assert.True(cut.FindAll(values).Count > 1), TimeSpan.FromSeconds(5));
        Assert.All(cut.FindAll(values), v => Assert.True(v.HasAttribute("checked")));
        Assert.Contains("steps and values ticked", cut.Find("[data-testid=search-description]").TextContent, StringComparison.Ordinal);
        int offered = cut.FindAll(values).Count;
        int all = int.Parse(cut.Find("[data-testid=tune-candidates] .num").TextContent, CultureInfo.InvariantCulture);
        string first = cut.FindAll(values)[0].GetAttribute("data-testid")!;

        cut.ChangeAt($"[data-testid='{first}']", false);

        cut.WaitForAssertion(
            () => Assert.Equal(all - 1, int.Parse(cut.Find("[data-testid=tune-candidates] .num").TextContent, CultureInfo.InvariantCulture)),
            TimeSpan.FromSeconds(5));
        // The unticked value stays on the page, so it can be ticked again.
        Assert.Equal(offered, cut.FindAll(values).Count);
        Assert.False(cut.Find($"[data-testid='{first}']").HasAttribute("checked"));
        Assert.Equal(offered - 1, cut.FindAll(values).Count(v => v.HasAttribute("checked")));
        Assert.True(cut.Find($"[data-testid=step-{TuningDimension.Parallel}]").HasAttribute("checked"));

        // Back on a preset every value runs again.
        cut.ChangeAt("[data-testid=search-Full]", true);
        cut.WaitForAssertion(
            () => Assert.Equal(all, int.Parse(cut.Find("[data-testid=tune-candidates] .num").TextContent, CultureInfo.InvariantCulture)),
            TimeSpan.FromSeconds(5));
        Assert.Empty(cut.FindAll("[data-testid^=value-]"));
    }

    [Theory]
    [InlineData(RuntimeKind.Mlx)]
    [InlineData(RuntimeKind.Sglang)]
    [InlineData(RuntimeKind.Nim)]
    public async Task RuntimeTheSearchCannotChange_IsExplained_AndCanNeitherStartNorApply(RuntimeKind runtime)
    {
        var model = Baseline with { Runtime = runtime };
        var run = MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, model), MakeCandidate(1, CandidateOutcome.Accepted, model with { MaxNumSeqs = 4 }));
        var (ctx, registry, _, _) = MakeContext(model, run);
        using var _ = ctx;

        var cut = ctx.Render<ModelTune>(p => p.Add(x => x.Id, model.Id));
        cut.WaitForElement("[data-testid=tune-unavailable]", TimeSpan.FromSeconds(10));
        Assert.Empty(cut.FindAll("[data-testid=start-tuning]"));
        Assert.Empty(cut.FindAll("[data-testid=tune-results]"));
        Assert.Empty(cut.FindAll("[data-testid=apply-best]"));
        Assert.Empty(cut.FindAll("[data-testid=tune-history]"));

        var jobs = ctx.Services.GetRequiredService<ModelJobService>();
        var ct = Xunit.TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.StartTuningAsync(model.Id, TuningGoal.InteractiveChat, ct: ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => jobs.ApplyTuningCandidateAsync(model.Id, run.Id, 1, ct));
        Assert.Equal(16, (await registry.GetAsync(model.Id, ct))!.MaxNumSeqs);
    }

    [Fact]
    public void ApplyButton_OnlyOnAcceptedRows()
    {
        var run = MakeRun("run-1",
            MakeCandidate(0, CandidateOutcome.Accepted, Baseline),
            MakeCandidate(1, CandidateOutcome.Rejected, Baseline with { MaxNumSeqs = 8 }),
            MakeCandidate(2, CandidateOutcome.Failed, Baseline with { MaxNumSeqs = 16 }),
            MakeCandidate(3, CandidateOutcome.Accepted, Tuned));
        var (ctx, _, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;

        var cut = RenderReady(ctx, "qwen");

        // Candidate 0 (accepted, equals the saved definition) shows "Current", not Apply.
        Assert.NotNull(cut.Find("[data-testid=candidate-current-0]"));
        Assert.Empty(cut.FindAll("[data-testid=candidate-apply-0]"));

        // Candidate 3 (accepted, different from the saved definition) gets an Apply button.
        Assert.NotNull(cut.Find("[data-testid=candidate-apply-3]"));

        // Rejected and Failed rows get neither.
        Assert.Empty(cut.FindAll("[data-testid=candidate-apply-1]"));
        Assert.Empty(cut.FindAll("[data-testid=candidate-current-1]"));
        Assert.Empty(cut.FindAll("[data-testid=candidate-apply-2]"));
        Assert.Empty(cut.FindAll("[data-testid=candidate-current-2]"));
    }

    [Fact]
    public void CurrentBadge_IsDrivenBySettingsHash_NotByTheBaselineLabel()
    {
        // The saved definition already carries the tuned (non-baseline) settings, as if a previous run's
        // winner was applied earlier. The row labelled "Baseline" is therefore NOT current, and the
        // non-baseline row IS — proving the badge follows the settings/hash, not the "Baseline" label.
        var run = MakeRun("run-2",
            MakeCandidate(0, CandidateOutcome.Accepted, Baseline),
            MakeCandidate(1, CandidateOutcome.Accepted, Tuned));
        var (ctx, _, _, _) = MakeContext(Tuned, run);
        using var _ = ctx;

        var cut = RenderReady(ctx, "qwen");

        Assert.NotNull(cut.Find("[data-testid=candidate-apply-0]"));
        Assert.Empty(cut.FindAll("[data-testid=candidate-current-0]"));
        Assert.NotNull(cut.Find("[data-testid=candidate-current-1]"));
        Assert.Empty(cut.FindAll("[data-testid=candidate-apply-1]"));
    }

    [Fact]
    public async Task ClickingApply_SavesOnlyTunedFields_AndOpensTheDialogWithTheChanges()
    {
        var run = MakeRun("run-3",
            MakeCandidate(0, CandidateOutcome.Accepted, Baseline),
            MakeCandidate(1, CandidateOutcome.Accepted, Tuned));
        var (ctx, registry, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");

        cut.ClickAt("[data-testid=candidate-apply-1]");

        // The click's handler does real async file I/O (the store lookup and registry upsert), so wait for
        // it to settle rather than reading the registry immediately after the synchronous Click() returns.
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open")));

        var saved = await registry.GetAsync("qwen", Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(new SpeculativeDecoding("mtp", 2), saved!.Speculative);
        Assert.Equal(Baseline.MaxModelLen, saved.MaxModelLen);
        Assert.Equal(Baseline.MemoryBudgetGiB, saved.MemoryBudgetGiB);
        var items = cut.FindAll("[data-testid=apply-changes] li").Select(li => li.TextContent).ToList();
        Assert.Contains(items, i => i.Contains("Speculative", StringComparison.Ordinal));
    }

    [Fact]
    public void GoalSelector_IsARadioGroup_DescribedByTheGoalText()
    {
        var run = MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, Baseline));
        var (ctx, _, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");

        var group = cut.Find("[data-testid=goal-selector]");
        Assert.Equal("radiogroup", group.GetAttribute("role"));
        Assert.Equal("goal-description", group.GetAttribute("aria-describedby"));
        // The goals are the four generation workload presets (R2-5) for a chat model.
        Assert.Equal(4, group.QuerySelectorAll("input[type=radio][name=tuning-goal]").Length);
        Assert.True(cut.Find("[data-testid=goal-InteractiveChat]").HasAttribute("checked"));
        Assert.Contains("One person chatting", cut.Find("[data-testid=goal-description]").TextContent, StringComparison.Ordinal);

        cut.ChangeAt("[data-testid=goal-AgentsTools]", true);

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("[data-testid=goal-AgentsTools]").HasAttribute("checked"));
            Assert.Contains("tool calls", cut.Find("[data-testid=goal-description]").TextContent, StringComparison.Ordinal);
            Assert.Contains("shared-prefix TTFT", cut.Find("[data-testid=goal-scoring]").TextContent, StringComparison.Ordinal);
            Assert.Contains("tool calls 100 % valid", cut.Find("[data-testid=goal-scoring]").TextContent, StringComparison.Ordinal);
        });
        Assert.Equal("tune", cut.Find("[data-testid=start-tuning]").GetAttribute("data-shortcut"));
    }

    [Fact]
    public void CandidateTable_HeadersExplainTheColumns_AndALegendFollows()
    {
        var run = MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, Baseline));
        var (ctx, _, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");

        foreach (string key in new[] { "settings", "n1", "n16", "ttft95", "answers", "score" })
        {
            Assert.NotNull(cut.Find($"[data-testid=colhelp-tune-run-1-{key}]"));
        }

        Assert.Contains("N = concurrent streaming requests", cut.Find("[data-testid=tune-legend]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void LongFailureReasons_ShowOneLineInline_AndTheFullOutputBehindDetails()
    {
        const string raw = "Traceback (most recent call last):\n  File \"vllm/engine.py\", line 12\nRuntimeError: CUDA error: out of memory";
        var failed = MakeCandidate(1, CandidateOutcome.Failed, Baseline with { MaxNumSeqs = 8 }) with { Reason = raw };
        var rejected = MakeCandidate(2, CandidateOutcome.Rejected, Baseline with { MaxNumSeqs = 16 }) with { Reason = "Answered 6/8, below the baseline's 8/8." };
        var run = MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, Baseline), failed, rejected);
        var (ctx, _, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");

        var reason = cut.Find("[data-testid=candidate-reason-1]");
        Assert.Equal("RuntimeError: CUDA error: out of memory", reason.QuerySelector(".outcome-reason-summary")!.TextContent);
        Assert.Equal("Full output", reason.QuerySelector("details summary")!.TextContent);
        Assert.Contains("Traceback", reason.QuerySelector("details pre")!.TextContent, StringComparison.Ordinal);

        // A short reason stays inline with no disclosure.
        var shortReason = cut.Find("[data-testid=candidate-reason-2]");
        Assert.Null(shortReason.QuerySelector("details"));
        Assert.Contains("below the baseline", shortReason.TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("did not clear the guardrail", "did not clear the guardrail")]
    [InlineData("line one\nValueError: bad value", "ValueError: bad value")]
    public void ShortReason_PicksTheCause(string reason, string expected) =>
        Assert.Equal(expected, ModelTune.ShortReason(reason));

    [Fact]
    public async Task CandidatesAndPastRuns_ArePaged_WithSeparateUrlKeys()
    {
        var candidates = Enumerable.Range(0, 30)
            .Select(i => MakeCandidate(i, CandidateOutcome.Accepted, Baseline with { MaxNumSeqs = 4 + i }))
            .ToArray();
        var (ctx, _, _, store) = MakeContext(Baseline, MakeRun("run-00", candidates));
        using var _ = ctx;
        for (int i = 1; i < 30; i++)
        {
            await store.SaveTuningAsync(MakeRun($"run-{i:00}", MakeCandidate(0, CandidateOutcome.Accepted, Baseline)), Xunit.TestContext.Current.CancellationToken);
        }

        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = RenderReady(ctx, "qwen");

        Assert.Equal(25, cut.FindAll("[data-testid=tune-history] tbody tr").Count);
        Assert.Equal("1–25 of 30 runs", cut.Find("[data-testid=tune-history-pager-range]").TextContent);

        cut.ClickAt("[data-testid=tune-history-pager-next]");
        cut.WaitForAssertion(() => Assert.Contains("runs-page=2", nav.Uri, StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("cand-page", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Candidates_ArePaged_InTheSelectedRun()
    {
        var candidates = Enumerable.Range(0, 30)
            .Select(i => MakeCandidate(i, CandidateOutcome.Accepted, Baseline with { MaxNumSeqs = 4 + i }))
            .ToArray();
        var (ctx, _, _, _) = MakeContext(Baseline, MakeRun("run-1", candidates));
        using var _ = ctx;

        var cut = RenderReady(ctx, "qwen");

        Assert.Equal(25, cut.FindAll("[data-testid=tune-results] tbody tr").Count);
        Assert.Equal("1–25 of 30 candidates", cut.Find("[data-testid=tune-results-pager-range]").TextContent);
        cut.ClickAt("[data-testid=tune-results-pager-page-2]");
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid=tune-results] tbody tr").Count), TimeSpan.FromSeconds(5));
        Assert.NotNull(cut.Find("[data-testid=candidate-29]"));
    }

    // ------------------------------------------------------------ search presets and descriptions (D1/D2)

    [Fact]
    public void SearchSelector_DefaultsToQuick_ListsPhasesWithDescriptions_AndFullTicksEveryStep()
    {
        var run = MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, Baseline));
        var (ctx, _, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=tune-plan]")), TimeSpan.FromSeconds(5));
        Assert.True(cut.Find("[data-testid=search-Quick]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=step-MaxNumSeqs]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=step-SpeculativeDecoding]").HasAttribute("checked"));
        Assert.False(cut.Find("[data-testid=step-KvCacheDtype]").HasAttribute("checked"));
        Assert.Contains("Quick", cut.Find("[data-testid=search-description]").TextContent, StringComparison.Ordinal);
        Assert.Contains(cut.FindAll("[data-testid^=plan-phase-]"), p => p.TextContent.Contains("Phase 2 · Concurrency", StringComparison.Ordinal));
        Assert.Contains(cut.FindAll("[data-testid^=step-desc-]"), d => d.TextContent.Contains("Tradeoff:", StringComparison.Ordinal));
        Assert.Contains("reference", cut.Find("[data-testid=preview-candidate-0]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Decode up to", cut.Find("[data-testid=tune-candidate-list]").TextContent, StringComparison.Ordinal);

        cut.ChangeAt("[data-testid=search-Full]", true);

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("[data-testid=search-Full]").HasAttribute("checked"));
            Assert.All(SearchPlanner.Full, d => Assert.True(cut.Find($"[data-testid=step-{d}]").HasAttribute("checked"), d.ToString()));
            Assert.Contains("every phase", cut.Find("[data-testid=search-description]").TextContent, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));

        cut.ChangeAt("[data-testid=step-AsyncScheduling]", false);
        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=search-Custom]").HasAttribute("checked")), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void SearchCustom_IsARealChoice_KeepsTheTicks_AndSurvivesAReload()
    {
        var run = MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, Baseline));
        var (ctx, _, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=tune-plan]")), TimeSpan.FromSeconds(5));
        string quickCount = cut.Find("[data-testid=tune-candidates] .num").TextContent;
        var quickTicks = SearchPlanner.Full.Where(d => cut.Find($"[data-testid=step-{d}]").HasAttribute("checked")).ToList();

        Assert.False(cut.Find("[data-testid=search-Custom]").HasAttribute("disabled"));
        cut.ChangeAt("[data-testid=search-Custom]", true);

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("[data-testid=search-Custom]").HasAttribute("checked"));
            Assert.False(cut.Find("[data-testid=search-Quick]").HasAttribute("checked"));
            Assert.StartsWith("Custom:", cut.Find("[data-testid=search-description]").TextContent, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));
        // The ticks are kept, and stay editable.
        Assert.Equal(quickTicks, SearchPlanner.Full.Where(d => cut.Find($"[data-testid=step-{d}]").HasAttribute("checked")).ToList());
        Assert.All(SearchPlanner.Full, d => Assert.False(cut.Find($"[data-testid=step-{d}]").HasAttribute("disabled")));
        Assert.Equal(quickCount, cut.Find("[data-testid=tune-candidates] .num").TextContent);

        // Adding a step updates the candidate count, and the plan stays Custom.
        cut.ChangeAt("[data-testid=step-MaxNumBatchedTokens]", true);
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("[data-testid=step-MaxNumBatchedTokens]").HasAttribute("checked"));
            Assert.NotEqual(quickCount, cut.Find("[data-testid=tune-candidates] .num").TextContent);
            Assert.True(cut.Find("[data-testid=search-Custom]").HasAttribute("checked"));
        }, TimeSpan.FromSeconds(5));

        // Unticking it again matches Quick's steps, but a hand-made plan stays Custom.
        cut.ChangeAt("[data-testid=step-MaxNumBatchedTokens]", false);
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(quickCount, cut.Find("[data-testid=tune-candidates] .num").TextContent);
            Assert.True(cut.Find("[data-testid=search-Custom]").HasAttribute("checked"));
        }, TimeSpan.FromSeconds(5));

        // Choosing Quick again resets to its preset.
        cut.ChangeAt("[data-testid=search-Quick]", true);
        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=search-Quick]").HasAttribute("checked")), TimeSpan.FromSeconds(5));
        Assert.False(cut.Find("[data-testid=search-Custom]").HasAttribute("checked"));
    }

    [Fact]
    public void TickingAStep_UnderQuick_SwitchesToCustom()
    {
        var run = MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, Baseline));
        var (ctx, _, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");
        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=search-Quick]").HasAttribute("checked")), TimeSpan.FromSeconds(5));

        cut.ChangeAt("[data-testid=step-KvCacheDtype]", true);

        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("[data-testid=search-Custom]").HasAttribute("checked"));
            Assert.True(cut.Find("[data-testid=step-KvCacheDtype]").HasAttribute("checked"));
            Assert.True(cut.Find("[data-testid=step-MaxNumSeqs]").HasAttribute("checked"));
        }, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ResultRows_ShowTheDescription_EffectiveConfig_AndRuntimeNotes()
    {
        var described = MakeCandidate(1, CandidateOutcome.Accepted, Tuned) with
        {
            Description = "Let the model's MTP layers draft 2 tokens per step. Tradeoff: shallow drafts gain less.",
            EffectiveConfig = "MTP × 2 · graphs FULL_AND_PIECEWISE · seqs 16",
            RuntimeNotes = ["CUDA graph mode: requested FULL_AND_PIECEWISE → ran PIECEWISE"],
            KvPool = "KV pool 507k tokens = 7.7 concurrent requests at 65,536 max model len",
        };
        var run = MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, Baseline), described);
        var (ctx, _, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");

        Assert.Contains("draft 2 tokens", cut.Find("[data-testid=candidate-desc-1]").TextContent, StringComparison.Ordinal);
        Assert.Equal("MTP × 2 · graphs FULL_AND_PIECEWISE · seqs 16", cut.Find("[data-testid=candidate-effective-1]").TextContent);
        Assert.Contains("ran PIECEWISE", cut.Find("[data-testid=candidate-runtime-1]").TextContent, StringComparison.Ordinal);
        Assert.Equal("KV pool 507k tokens = 7.7 concurrent requests at 65,536 max model len", cut.Find("[data-testid=candidate-kv-1]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=candidate-runtime-0]"));
        Assert.Empty(cut.FindAll("[data-testid=candidate-kv-0]"));
    }

    [Fact]
    public void ProgressPanel_ShowsWhatTheCurrentCandidateTriesToAchieve()
    {
        using var ctx = new Bunit.BunitContext();
        var job = new JobSnapshot("j", JobKind.Tuning, "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "Starting MTP × 3",
            0, 0, "MTP × 3 · FULL_AND_PIECEWISE", 4, 10, [], TuningGoal.InteractiveChat, false, "Let the model's MTP layers draft 3 tokens per step.");

        var cut = ctx.Render<MirbadLLM.Web.Components.Ui.JobProgressPanel>(p => p.Add(x => x.Job, job));

        Assert.Contains("draft 3 tokens", cut.Find("[data-testid=job-candidate-description]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Intro_LeadsWithGoalCostAndStart_SearchAndPhasesWaitUnderCustomize()
    {
        var run = MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, Baseline));
        var (ctx, _, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=tune-cost-value]")), TimeSpan.FromSeconds(5));
        string markup = cut.Markup;
        int goal = markup.IndexOf("data-testid=\"goal-selector\"", StringComparison.Ordinal);
        int cost = markup.IndexOf("data-testid=\"tune-cost-value\"", StringComparison.Ordinal);
        int start = markup.IndexOf("data-testid=\"start-tuning\"", StringComparison.Ordinal);
        int search = markup.IndexOf("data-testid=\"search-selector\"", StringComparison.Ordinal);
        Assert.True(goal < cost && cost < start && start < search, "goal, then cost, then Start, then the search options");
        Assert.StartsWith("about", cut.Find("[data-testid=tune-cost-value]").TextContent, StringComparison.Ordinal);

        // Search mode and the phase checkboxes live in a closed native disclosure.
        var customize = cut.Find("details[data-testid=tune-customize]");
        Assert.False(customize.HasAttribute("open"));
        Assert.NotNull(customize.QuerySelector("[data-testid=search-selector]"));
        Assert.NotNull(customize.QuerySelector("[data-testid=tune-plan]"));
        Assert.Null(customize.QuerySelector("[data-testid=start-tuning]"));
    }

    [Fact]
    public void Plan_CombinedStepTitleLeadsItsRow_AndNoStepRepeatsItsPhaseLine()
    {
        var run = MakeRun("run-1", MakeCandidate(0, CandidateOutcome.Accepted, Baseline));
        var (ctx, _, _, _) = MakeContext(Baseline, run);
        using var _ = ctx;
        var cut = RenderReady(ctx, "qwen");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=tune-plan]")), TimeSpan.FromSeconds(5));

        // "KV cache × prefix caching" heads its own row, ahead of the two dimensions it ticks, not under one of them.
        var combined = cut.FindAll("[data-testid=tune-plan] li.plan-step--multi");
        Assert.NotEmpty(combined);
        foreach (var row in combined)
        {
            var first = row.FirstElementChild!;
            Assert.Contains("plan-step-title", first.ClassName, StringComparison.Ordinal);
            Assert.Contains("×", first.TextContent, StringComparison.Ordinal);
            Assert.Null(first.Closest("label"));
            Assert.Equal(2, row.QuerySelectorAll(".plan-step-checks input[type=checkbox]").Length);
        }

        // A phase's line is printed once, on the phase heading: no step under it repeats it.
        foreach (string phase in TuningDescriptions.Phases)
        {
            string text = TuningDescriptions.Phase(phase).Text;
            Assert.DoesNotContain(cut.FindAll("[data-testid^=step-desc-]"), d => d.TextContent == text);
        }

        Assert.Single(cut.FindAll("[data-testid^=plan-phase-]"), p => p.TextContent.Contains(TuningDescriptions.PhaseRefinement, StringComparison.Ordinal));
    }
}
