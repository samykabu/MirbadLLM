using Bunit;
using TestContext = Xunit.TestContext;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>
/// The benchmark/tuning runner with fakes: one job at a time, the maintenance window around every job,
/// restart → Ready → benchmark → next candidate, failed candidates recorded with their cause, cancel restoring
/// the saved definition, persistence, draining and the GPU idle check.
/// </summary>
public sealed class ModelJobServiceTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-jobs-" + Guid.NewGuid().ToString("N"));

    private static readonly ModelDefinition Saved = new()
    {
        Id = "qwen",
        ServedName = "Qwen",
        ModelPath = "Qwen-NVFP4",
        MemoryBudgetGiB = 44,
        MaxModelLen = 262_144,
        MaxNumSeqs = 16,
        Speculative = new SpeculativeDecoding("mtp", 2),
        CudaGraphMode = "PIECEWISE",
    };

    private static readonly ModelFolderInspection WithMtp = new(
        "Qwen-NVFP4", "Qwen3_5ForConditionalGeneration", 262_144, "NVFP4", 24, MaySupportMtp: true,
        new ModelFolderSuggestion("Qwen", "qwen3_coder", "qwen3", null, 44, ""), KvBytesPerToken: 32_768);

    private sealed record Harness(
        ModelJobService Jobs, FakeCluster Cluster, FakeSuite Suite, FakeModelRegistry Registry,
        MaintenanceModeService Maintenance, BenchmarkStore Store, FakeMetricsHistory History);

    public void Dispose()
    {
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private Harness Create(double? gpu = 2, ModelFolderInspection? inspection = null, TimeSpan? drainTimeout = null)
    {
        var registry = new FakeModelRegistry().Add(Saved);
        var cluster = new FakeCluster().Running(Saved);
        var suite = new FakeSuite(cluster);
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, UseContainerNetwork = true, VllmImage = "default-image" });
        var maintenance = new MaintenanceModeService(options);
        var store = new BenchmarkStore(options);
        var history = new FakeMetricsHistory();
        var jobs = new ModelJobService(
            registry, cluster, cluster, suite, store, maintenance, history, new FakeGpu(gpu), new StartupTimeTracker(), options,
            new ModelJobOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                DrainTimeout = drainTimeout ?? TimeSpan.FromMilliseconds(30),
                GpuIdleSampleDuration = TimeSpan.FromMilliseconds(3),
                GpuIdleSampleInterval = TimeSpan.FromMilliseconds(1),
                CandidateStartTimeout = TimeSpan.FromSeconds(10),
            },
            NullLogger<ModelJobService>.Instance,
            new ContainerNetworkTargets(),
            _ => inspection ?? WithMtp);
        return new Harness(jobs, cluster, suite, registry, maintenance, store, history);
    }

    // ------------------------------------------------------------ on-demand benchmark

    [Fact]
    public async Task Benchmark_HoldsMaintenanceWhileRunning_PersistsWithSettingsAndHash_ThenClearsIt()
    {
        var h = Create();
        h.Suite.Gate = new TaskCompletionSource();

        var job = await h.Jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken);

        Assert.Equal(JobKind.Benchmark, job.Kind);
        Assert.NotNull(h.Maintenance.Current);
        Assert.Equal("benchmark", h.Maintenance.Current!.JobKind);
        Assert.True(File.Exists(Path.Combine(_dataPath, "maintenance.json")));

        h.Suite.Gate.SetResult();
        await h.Jobs.WhenIdleAsync();

        Assert.Null(h.Maintenance.Current);
        Assert.False(File.Exists(Path.Combine(_dataPath, "maintenance.json")));
        Assert.Null(h.Jobs.Current);
        Assert.Equal("Completed", h.Jobs.LastOutcome!.Status);
        Assert.Empty(h.Cluster.Launches); // no restart, no setting change

        var runs = await h.Store.ListBenchmarksAsync("qwen", TestContext.Current.CancellationToken);
        var run = Assert.Single(runs);
        Assert.Equal(BenchmarkOrigin.OnDemand, run.Origin);
        Assert.Equal(Saved.Speculative, run.Settings.Speculative);
        Assert.Equal(FakeCluster.Hash(Saved), run.ConfigHash);
        Assert.NotNull(run.Result);
        Assert.True(File.Exists(Path.Combine(_dataPath, "benchmarks", "qwen", run.Id + ".json")));
        Assert.Equal(new Uri("http://ss-qwen:8000/"), h.Suite.Targets[0].BaseUri);
    }

    [Fact]
    public async Task Benchmark_RefusedWhenModelIsNotReady()
    {
        var h = Create();
        await h.Cluster.StopAsync("qwen", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Contains("not Ready", ex.Message, StringComparison.Ordinal);
        Assert.Null(h.Maintenance.Current);
    }

    [Fact]
    public async Task Benchmark_RefusedWhileTuningRuns_WithAClearMessage()
    {
        var h = Create();
        h.Suite.Gate = new TaskCompletionSource();
        await h.Jobs.StartTuningAsync("qwen", TuningGoal.Balanced, [TuningDimension.MaxNumSeqs], TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<JobConflictException>(() => h.Jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Equal("A tuning run on qwen is in progress, so a benchmark can't start. Wait for it to finish or cancel it first.", ex.Message);

        h.Jobs.Cancel();
        h.Suite.Gate.SetResult();
        await h.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task Tuning_RefusedWhileBenchmarkRuns()
    {
        var h = Create();
        h.Suite.Gate = new TaskCompletionSource();
        await h.Jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<JobConflictException>(() => h.Jobs.StartTuningAsync("qwen", TuningGoal.Balanced, null, TestContext.Current.CancellationToken));
        Assert.Contains("A benchmark on qwen is in progress", ex.Message, StringComparison.Ordinal);

        h.Suite.Gate.SetResult();
        await h.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task Benchmark_Cancelled_ClearsMaintenance_AndIsNotPersisted()
    {
        var h = Create();
        h.Suite.Gate = new TaskCompletionSource();
        await h.Jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken);

        Assert.True(h.Jobs.Cancel(JobKind.Benchmark));
        await h.Jobs.WhenIdleAsync();

        Assert.Null(h.Maintenance.Current);
        Assert.Equal("Cancelled", h.Jobs.LastOutcome!.Status);
        Assert.Empty(await h.Store.ListBenchmarksAsync("qwen", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Benchmark_SuiteFailure_IsRecorded_AndMaintenanceCleared()
    {
        var h = Create();
        h.Suite.Score = _ => throw new BenchmarkRequestException(500, "boom");

        await h.Jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();

        Assert.Null(h.Maintenance.Current);
        Assert.Equal("Failed", h.Jobs.LastOutcome!.Status);
        var run = Assert.Single(await h.Store.ListBenchmarksAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Null(run.Result);
        Assert.Contains("HTTP 500", run.Error, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ quiet box

    [Fact]
    public async Task Drain_ReturnsTrue_OnceEveryReadyModelReportsNoRunningOrWaitingRequests()
    {
        var h = Create();
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        h.History.ModelSamplesById["qwen"] = [new ModelSample(DateTimeOffset.UtcNow, Results.Kpis(0, 0))];

        Assert.True(await h.Jobs.DrainAsync(since, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Drain_TimesOut_WhenRequestsStayInFlight()
    {
        var h = Create();
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        h.History.ModelSamplesById["qwen"] = [new ModelSample(DateTimeOffset.UtcNow, Results.Kpis(running: 2, waiting: 1))];

        Assert.False(await h.Jobs.DrainAsync(since, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Drain_IgnoresSamplesTakenBeforeThePause()
    {
        var h = Create();
        var since = DateTimeOffset.UtcNow;
        h.History.ModelSamplesById["qwen"] = [new ModelSample(since.AddSeconds(-10), Results.Kpis(running: 5))];

        // Only a stale busy sample: nothing fresh says busy, so the drain completes at the timeout without flagging.
        Assert.True(await h.Jobs.DrainAsync(since, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InFlightRequestsAtStart_MarkTheResultNoisy()
    {
        var h = Create();
        h.History.ModelSamplesById["qwen"] = [new ModelSample(DateTimeOffset.UtcNow.AddMinutes(1), Results.Kpis(running: 3))];

        await h.Jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListBenchmarksAsync("qwen", TestContext.Current.CancellationToken));
        Assert.True(run.Result!.Noise.Noisy);
        Assert.Contains(run.Result.Noise.Reasons, r => r.StartsWith("noisy: in-flight requests at start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GpuIdleCheck_WarnsWhenOtherGpuWorkIsActive_AndTheResultRecordsIt()
    {
        var h = Create(gpu: 35);

        var reading = await h.Jobs.SampleGpuIdleAsync(TestContext.Current.CancellationToken);
        Assert.True(reading.Busy);
        Assert.Equal(35, reading.AveragePercent);

        await h.Jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();
        var run = Assert.Single(await h.Store.ListBenchmarksAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Equal(35, run.Result!.Noise.IdleGpuPercent);
        Assert.Contains(run.Result.Noise.Reasons, r => r.Contains("GPU was 35% busy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartJobDialog_SaysTheWholeApiPauses_OrOnlyTheModelsNodeWhenTheWindowIsNodeScoped(bool nodeScoped)
    {
        // MN-124: the benchmark/tune dialog said the whole API pauses even when the job's window is node-scoped (D83/D101).
        var h = Create(gpu: 4);
        h.Registry.Add(Saved with { Placement = new ModelPlacement("pc") });
        using var nodes = new MirbadLLM.Orchestration.Nodes.JsonNodeRegistry(
            Options.Create(new MirbadLLMOptions { DataPath = _dataPath }), TimeProvider.System);
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(h.Jobs);
        ctx.Services.AddSingleton<IModelRegistry>(h.Registry);
        ctx.Services.AddSingleton(new MirbadLLM.Orchestration.Multinode.NodeScopedMaintenancePolicy(nodeScoped, nodes));
        var cut = ctx.Render<MirbadLLM.Web.Components.Ui.StartJobDialog>(p => p
            .Add(d => d.Title, "Benchmark Qwen?").Add(d => d.ConfirmLabel, "Run").Add(d => d.Estimate, "about 1 min")
            .Add(d => d.ModelId, "qwen").Add(d => d.TestId, "job"));

        await cut.InvokeAsync(() => cut.Instance.Show());
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=job-gpu-idle]")), TimeSpan.FromSeconds(10));
        string text = cut.Find("[data-testid=job-maintenance]").TextContent;
        if (nodeScoped)
        {
            Assert.Contains("models on node pc will be refused with 503", text, StringComparison.Ordinal);
            Assert.Contains("Other nodes keep serving", text, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("External API calls will be refused with 503", text, StringComparison.Ordinal);
            Assert.DoesNotContain("node", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GpuIdleCheck_QuietGpu_IsNotBusy()
    {
        var h = Create(gpu: 4);
        var reading = await h.Jobs.SampleGpuIdleAsync(TestContext.Current.CancellationToken);
        Assert.False(reading.Busy);
    }

    [Fact]
    public void MaintenanceFlag_LeftByAPreviousProcess_IsClearedOnBoot()
    {
        Directory.CreateDirectory(_dataPath);
        string file = Path.Combine(_dataPath, "maintenance.json");
        File.WriteAllText(file, """{"JobId":"old","JobKind":"tuning","ModelId":"qwen"}""");

        var maintenance = new MaintenanceModeService(Options.Create(new MirbadLLMOptions { DataPath = _dataPath }));

        Assert.True(maintenance.ClearedStaleFlagOnBoot);
        Assert.Null(maintenance.Current);
        Assert.False(File.Exists(file));
    }

    // ------------------------------------------------------------ tuning

    [Fact]
    public async Task Tuning_RestartsEachCandidate_WaitsForReady_Benchmarks_ThenRestoresTheSavedDefinition()
    {
        var h = Create();

        // Faster with more sequences; speculative off is slowest.
        h.Suite.Score = d => Results.Make(
            n1: d.Speculative is null ? 8 : 10 + d.Speculative.NumSpeculativeTokens,
            n16: 100 + d.MaxNumSeqs);

        await h.Jobs.StartTuningAsync("qwen", TuningGoal.Balanced, [TuningDimension.SpeculativeDecoding, TuningDimension.MaxNumSeqs],
            TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListTuningAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Equal(TuningRunStatus.Completed, run.Status);

        // Baseline reused (running container already has the saved hash); MTP × 2 and seqs 16 are duplicates.
        Assert.Equal(
            ["Baseline (saved settings)", "Speculative off", "MTP × 1", "MTP × 3", "Max num seqs 8", "Max num seqs 32"],
            run.Candidates.Select(c => c.Label));
        Assert.Null(run.Candidates[0].StartupSeconds);

        // Every candidate launched as a labelled tuning container; the saved registry entry never changed.
        var tuningLaunches = h.Cluster.Launches.Take(5).ToList();
        Assert.All(tuningLaunches, l => Assert.True(l.Options.Tuning));
        Assert.Equal(Saved, await h.Registry.GetAsync("qwen", TestContext.Current.CancellationToken));

        // Coordinate descent: seqs candidates build on the best speculative setting (MTP × 3).
        Assert.Equal(new SpeculativeDecoding("mtp", 3), run.Candidates[4].Settings.Speculative);

        // Winner: MTP × 3 with 32 seqs; gain reported against the baseline.
        Assert.Equal("Max num seqs 32", run.Winner!.Label);
        Assert.True(run.WinnerGainPercent > 0);

        // Restored: the last launch is the saved definition, unlabelled.
        var last = h.Cluster.Launches[^1];
        Assert.Equal(Saved, last.Definition);
        Assert.False(last.Options.Tuning);
        Assert.Null(h.Maintenance.Current);
        Assert.Empty(await h.Cluster.ListTuningContainersAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tuning_FailedCandidate_IsRecordedWithItsCause_AndTheRunContinues()
    {
        var h = Create();
        h.Cluster.FailWith = d => d.Speculative is { NumSpeculativeTokens: 1 }
            ? "Cause: RuntimeError: CUBLAS_STATUS_INTERNAL_ERROR when calling cublasGemmEx\n\nlog tail"
            : null;

        await h.Jobs.StartTuningAsync("qwen", TuningGoal.SingleUser, [TuningDimension.SpeculativeDecoding], TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListTuningAsync("qwen", TestContext.Current.CancellationToken));
        var failed = Assert.Single(run.Candidates, c => c.Label == "MTP × 1");
        Assert.Equal(CandidateOutcome.Failed, failed.Outcome);
        Assert.Equal("Failed to start: RuntimeError: CUBLAS_STATUS_INTERNAL_ERROR when calling cublasGemmEx", failed.Reason);
        Assert.Null(failed.Result);
        Assert.Contains(run.Candidates, c => c.Label == "MTP × 3" && c.Outcome == CandidateOutcome.Accepted);
        Assert.Equal(TuningRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Tuning_GuardrailFailure_RejectsTheCandidateRegardlessOfSpeed()
    {
        var h = Create();
        h.Suite.Score = d => d.MaxNumSeqs == 32 ? Results.Make(50, 900, qa: 5) : Results.Make(10, 100);

        await h.Jobs.StartTuningAsync("qwen", TuningGoal.ManyAgents, [TuningDimension.MaxNumSeqs], TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListTuningAsync("qwen", TestContext.Current.CancellationToken));
        var fast = Assert.Single(run.Candidates, c => c.Label == "Max num seqs 32");
        Assert.Equal(CandidateOutcome.Rejected, fast.Outcome);
        Assert.Contains("Objective answers 5/8", fast.Reason, StringComparison.Ordinal);
        Assert.NotEqual("Max num seqs 32", run.Winner!.Label);
    }

    [Fact]
    public async Task Tuning_Cancel_RestoresTheSavedDefinition_AndClearsMaintenance()
    {
        var h = Create();
        await h.Cluster.StopAsync("qwen", TestContext.Current.CancellationToken);
        h.Cluster.Running(Saved); // running with the saved settings
        h.Suite.Gate = new TaskCompletionSource();

        await h.Jobs.StartTuningAsync("qwen", TuningGoal.Balanced, [TuningDimension.MaxNumSeqs], TestContext.Current.CancellationToken);
        while (h.Suite.Runs == 0)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(h.Maintenance.Current);
        Assert.Equal("tuning", h.Maintenance.Current!.JobKind);
        Assert.True(h.Jobs.Cancel(JobKind.Tuning));
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListTuningAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Equal(TuningRunStatus.Cancelled, run.Status);
        Assert.Null(run.WinnerIndex);
        Assert.Null(h.Maintenance.Current);
        Assert.Equal(FakeCluster.Hash(Saved), await h.Cluster.GetLaunchedConfigHashAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Empty(await h.Cluster.ListTuningContainersAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tuning_BaselineThatCannotBeMeasured_FailsTheRun_AndStillRestores()
    {
        var h = Create();
        h.Suite.Score = _ => throw new BenchmarkRequestException(503, "down");

        await h.Jobs.StartTuningAsync("qwen", TuningGoal.Balanced, [TuningDimension.MaxNumSeqs], TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListTuningAsync("qwen", TestContext.Current.CancellationToken));
        Assert.Equal(TuningRunStatus.Failed, run.Status);
        Assert.Contains("baseline", run.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(h.Maintenance.Current);
        Assert.Equal("Failed", h.Jobs.LastOutcome!.Status);
    }

    [Fact]
    public async Task Tuning_ModelThatWasStopped_IsStoppedAgainAtTheEnd()
    {
        var h = Create();
        await h.Cluster.StopAsync("qwen", TestContext.Current.CancellationToken);
        h.Cluster.Stops.Clear();

        await h.Jobs.StartTuningAsync("qwen", TuningGoal.Balanced, [TuningDimension.MaxNumSeqs], TestContext.Current.CancellationToken);
        await h.Jobs.WhenIdleAsync();

        var run = Assert.Single(await h.Store.ListTuningAsync("qwen", TestContext.Current.CancellationToken));
        Assert.True(h.Cluster.Launches[0].Options.Tuning); // baseline had to be started
        Assert.NotNull(run.Candidates[0].StartupSeconds);
        Assert.Equal(["qwen"], h.Cluster.Stops);
    }

    [Fact]
    public async Task Recovery_RelaunchesModelsLeftRunningATuningCandidate()
    {
        var h = Create();
        h.Cluster.Running(Saved with { MaxNumSeqs = 8 }, tuning: true);

        await h.Jobs.RecoverAsync(TestContext.Current.CancellationToken);

        var launch = Assert.Single(h.Cluster.Launches);
        Assert.Equal(Saved, launch.Definition);
        Assert.False(launch.Options.Tuning);
    }

    [Fact]
    public async Task Preview_ShowsThePlan_AndReusesTheRunningBaselineWhenItMatches()
    {
        var h = Create();

        var preview = await h.Jobs.PreviewTuningAsync("qwen", null, TestContext.Current.CancellationToken);

        Assert.True(preview.ReusesRunningBaseline);
        Assert.Equal(
            ["Baseline (saved settings)", "Speculative off", "MTP × 1", "MTP × 3", "Max num seqs 8", "Max num seqs 32",
             "Batched tokens 2,048", "Batched tokens 8,192", "Batched tokens 16,384"],
            preview.Plan.PreviewCandidates.Select(c => c.Label));
        Assert.True(preview.Estimate > TimeSpan.Zero);
    }
}
