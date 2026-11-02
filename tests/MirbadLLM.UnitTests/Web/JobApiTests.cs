using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Api;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;
using FakeHttpMessageHandler = MirbadLLM.UnitTests.Orchestration.Fakes.FakeHttpMessageHandler;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// <c>POST /api/models/{id}/restart</c>, the benchmark and tuning endpoints (start, current, cancel, history),
/// the one-job-at-a-time rule over HTTP, and that the admin API keeps working in maintenance mode.
/// </summary>
public sealed class JobApiTests : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly ModelDefinition Model = new() { Id = "qwen", ServedName = "Qwen", ModelPath = "Qwen", MemoryBudgetGiB = 44 };

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-jobapi-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync(TestContext.Current.CancellationToken);
            await _app.DisposeAsync();
        }

        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private async Task<(HttpClient Client, FakeCluster Cluster, FakeSuite Suite, ModelJobService Jobs, MaintenanceModeService Maintenance)> StartAsync(double availableGiB = 64)
    {
        var registry = new FakeModelRegistry().Add(Model);
        var cluster = new FakeCluster().Running(Model);
        var suite = new FakeSuite(cluster);
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, VllmImage = "default-image" });
        var maintenance = new MaintenanceModeService(options);
        var store = new BenchmarkStore(options);
        var jobs = new ModelJobService(registry, cluster, cluster, suite, store, maintenance, new FakeMetricsHistory(), new FakeGpu(0),
            new StartupTimeTracker(), options,
            new ModelJobOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                DrainTimeout = TimeSpan.FromMilliseconds(10),
                GpuIdleSampleDuration = TimeSpan.FromMilliseconds(1),
                GpuIdleSampleInterval = TimeSpan.FromMilliseconds(1),
            },
            NullLogger<ModelJobService>.Instance, new ContainerNetworkTargets());

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<IModelRegistry>(registry);
        builder.Services.AddSingleton<IModelRuntime>(new RestartingRuntime(cluster, availableGiB));
        builder.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(121, availableGiB)));
        builder.Services.AddSingleton<IModelMetricsReader>(new FakeModelMetricsReader());
        builder.Services.AddSingleton<IRouterStats>(new FakeRouterStats([]));
        builder.Services.AddSingleton<IHuggingFaceDownloader>(new FakeHuggingFaceDownloader());
        builder.Services.AddSingleton<IHuggingFaceRepoInspector>(new FakeHuggingFaceRepoInspector());
        builder.Services.AddNodeSourcing(_dataPath, options);
        builder.Services.AddSingleton(jobs);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IMaintenanceMode>(maintenance);

        _app = builder.Build();
        _app.MapGet("/healthz", () => Microsoft.AspNetCore.Http.Results.Ok(new { status = "ok" }));
        _app.MapAdminApi();
        _app.MapJobApi();
        await _app.StartAsync(TestContext.Current.CancellationToken);
        return (_app.GetTestClient(), cluster, suite, jobs, maintenance);
    }

    /// <summary>A runtime whose restart follows the documented admission rule: own budget counts as released.</summary>
    private sealed class RestartingRuntime(FakeCluster cluster, double availableGiB) : IModelRuntime
    {
        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) => cluster.ListStatusAsync(ct);

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) => cluster.GetStatusAsync(id, ct);

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default) => cluster.GetStatusAsync(id, ct);

        public Task StopAsync(string id, CancellationToken ct = default) => cluster.StopAsync(id, ct);

        public IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, CancellationToken ct = default) =>
            cluster.StreamLogsAsync(id, tail, follow, ct);

        public Task<ModelStatus> RestartAsync(string id, bool force = false, CancellationToken ct = default)
        {
            var saved = Model;
            double free = availableGiB + (cluster.CurrentDefinition(id)?.MemoryBudgetGiB ?? 0) - 8;
            if (saved.MemoryBudgetGiB > free && !force)
            {
                throw new ModelMemoryAdmissionException($"Model '{id}' needs {saved.MemoryBudgetGiB:F1} GiB but only {free:F1} GiB is free.");
            }

            return cluster.LaunchAsync(saved, new LaunchOptions(force), ct);
        }
    }

    [Fact]
    public async Task Restart_Returns200_AndRecreatesTheContainer()
    {
        var (client, cluster, _, _, _) = await StartAsync();

        using var response = await client.PostAsync(new Uri("/api/models/qwen/restart", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(cluster.Launches);
    }

    [Fact]
    public async Task Restart_AdmissionRefused_Is409_AndForceOverrides()
    {
        // 0 GiB available + 44 released − 8 reserve = 36 < 44.
        var (client, cluster, _, _, _) = await StartAsync(availableGiB: 0);

        using var refused = await client.PostAsync(new Uri("/api/models/qwen/restart", UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Empty(cluster.Launches);

        using var forced = await client.PostAsync(new Uri("/api/models/qwen/restart?force=true", UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, forced.StatusCode);
    }

    [Fact]
    public async Task Restart_UnknownModel_Is404()
    {
        var (client, _, _, _, _) = await StartAsync();
        using var response = await client.PostAsync(new Uri("/api/models/nope/restart", UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Benchmark_Start_Current_History()
    {
        var (client, _, suite, jobs, _) = await StartAsync();
        suite.Gate = new TaskCompletionSource();

        using var started = await client.PostAsync(new Uri("/api/models/qwen/benchmark", UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        var current = await client.GetFromJsonAsync<JobStatusResponse>("/api/benchmark", JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal("qwen", current!.Current!.ModelId);
        Assert.Equal(JobKind.Benchmark, current.Current.Kind);

        suite.Gate.SetResult();
        await jobs.WhenIdleAsync();

        var history = await client.GetFromJsonAsync<List<BenchmarkRun>>("/api/models/qwen/benchmarks", JsonOptions, TestContext.Current.CancellationToken);
        var run = Assert.Single(history!);
        Assert.NotNull(run.Result);
        var after = await client.GetFromJsonAsync<JobStatusResponse>("/api/benchmark", JsonOptions, TestContext.Current.CancellationToken);
        Assert.Null(after!.Current);
        Assert.Equal("Completed", after.Last!.Status);
    }

    [Fact]
    public async Task OneJobAtATime_SecondStartIs409_WithTheReason()
    {
        var (client, _, suite, jobs, _) = await StartAsync();
        suite.Gate = new TaskCompletionSource();

        using var tune = await client.PostAsJsonAsync("/api/models/qwen/tune", new TuneRequest(TuningGoal.SingleUser, [TuningDimension.MaxNumSeqs]),
            JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, tune.StatusCode);

        using var bench = await client.PostAsync(new Uri("/api/models/qwen/benchmark", UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, bench.StatusCode);
        Assert.Contains("A tuning run on qwen is in progress", await bench.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);

        var current = await client.GetFromJsonAsync<JobStatusResponse>("/api/tuning", JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(TuningGoal.SingleUser, current!.Current!.Goal);

        using var cancel = await client.DeleteAsync(new Uri("/api/tuning", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        suite.Gate.SetResult();
        await jobs.WhenIdleAsync();

        using var nothing = await client.DeleteAsync(new Uri("/api/tuning", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, nothing.StatusCode);

        var history = await client.GetFromJsonAsync<List<TuningRun>>("/api/models/qwen/tuning", JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(TuningRunStatus.Cancelled, Assert.Single(history!).Status);
    }

    [Fact]
    public async Task Benchmark_OnAStoppedModel_Is409()
    {
        var (client, cluster, _, _, _) = await StartAsync();
        await cluster.StopAsync("qwen", TestContext.Current.CancellationToken);

        using var response = await client.PostAsync(new Uri("/api/models/qwen/benchmark", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("not Ready", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdminApiAndHealthz_KeepWorking_DuringMaintenance()
    {
        var (client, _, suite, jobs, maintenance) = await StartAsync();
        suite.Gate = new TaskCompletionSource();
        await jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken);
        Assert.NotNull(maintenance.Current);

        using var models = await client.GetAsync(new Uri("/api/models", UriKind.Relative), TestContext.Current.CancellationToken);
        using var health = await client.GetAsync(new Uri("/healthz", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        using var cancel = await client.DeleteAsync(new Uri("/api/benchmark", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        suite.Gate.SetResult();
        await jobs.WhenIdleAsync();
        Assert.Null(maintenance.Current);
    }

    // ---- POST /api/models/{id}/tuning/{runId}/candidates/{index}/apply ----

    private BenchmarkStore StoreOverSameData() => new(Options.Create(new MirbadLLMOptions { DataPath = _dataPath }));

    private static TuningCandidateResult MakeCandidate(int index, CandidateOutcome outcome) => new(
        Index: index,
        Step: "Speculative decoding",
        Label: index == 0 ? "Baseline" : $"MTP × {index}",
        Settings: index == 0 ? Model : Model with { Speculative = new SpeculativeDecoding("mtp", index) },
        ConfigHash: $"hash-{index}",
        Outcome: outcome,
        Reason: outcome == CandidateOutcome.Accepted ? null : "did not clear the guardrail",
        Result: null,
        Score: outcome == CandidateOutcome.Accepted ? 1.0 : null,
        StartupSeconds: null);

    private static TuningRun MakeRun(string id, string modelId, params TuningCandidateResult[] candidates) => new(
        id, modelId, TuningGoal.Balanced, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow,
        TuningRunStatus.Completed, Model, candidates, WinnerIndex: candidates.Length > 0 ? candidates[^1].Index : null,
        WinnerGainPercent: 12.5, Error: null, Notes: []);

    [Fact]
    public async Task ApplyTuningCandidate_Accepted_SavesOnlyTheTunedFields_NotMaxModelLenOrBudget()
    {
        var (client, _, _, _, _) = await StartAsync();
        var store = StoreOverSameData();
        var run = MakeRun("run-1", "qwen", MakeCandidate(0, CandidateOutcome.Accepted), MakeCandidate(2, CandidateOutcome.Accepted));
        await store.SaveTuningAsync(run, TestContext.Current.CancellationToken);

        using var response = await client.PostAsync(new Uri("/api/models/qwen/tuning/run-1/candidates/2/apply", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var merged = await response.Content.ReadFromJsonAsync<ModelDefinition>(JsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(new SpeculativeDecoding("mtp", 2), merged!.Speculative);
        // Never touched by an apply: max model len and the memory budget stay whatever was already saved.
        Assert.Equal(Model.MaxModelLen, merged.MaxModelLen);
        Assert.Equal(Model.MemoryBudgetGiB, merged.MemoryBudgetGiB);
    }

    [Fact]
    public async Task ApplyTuningCandidate_RejectedOrFailed_Is409()
    {
        var (client, _, _, _, _) = await StartAsync();
        var store = StoreOverSameData();
        var run = MakeRun("run-2", "qwen",
            MakeCandidate(0, CandidateOutcome.Accepted),
            MakeCandidate(1, CandidateOutcome.Rejected),
            MakeCandidate(2, CandidateOutcome.Failed));
        await store.SaveTuningAsync(run, TestContext.Current.CancellationToken);

        using var rejected = await client.PostAsync(new Uri("/api/models/qwen/tuning/run-2/candidates/1/apply", UriKind.Relative), null, TestContext.Current.CancellationToken);
        using var failed = await client.PostAsync(new Uri("/api/models/qwen/tuning/run-2/candidates/2/apply", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
    }

    [Fact]
    public async Task ApplyTuningCandidate_UnknownRunOrCandidateIndex_Is404()
    {
        var (client, _, _, _, _) = await StartAsync();
        var store = StoreOverSameData();
        await store.SaveTuningAsync(MakeRun("run-3", "qwen", MakeCandidate(0, CandidateOutcome.Accepted)), TestContext.Current.CancellationToken);

        using var noRun = await client.PostAsync(new Uri("/api/models/qwen/tuning/no-such-run/candidates/0/apply", UriKind.Relative), null, TestContext.Current.CancellationToken);
        using var noIndex = await client.PostAsync(new Uri("/api/models/qwen/tuning/run-3/candidates/99/apply", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, noRun.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noIndex.StatusCode);
    }

    [Fact]
    public async Task ApplyTuningCandidate_RunBelongsToAnotherModel_Is409()
    {
        var (client, _, _, _, _) = await StartAsync();
        var store = StoreOverSameData();
        await store.SaveTuningAsync(MakeRun("run-4", "some-other-model", MakeCandidate(1, CandidateOutcome.Accepted)), TestContext.Current.CancellationToken);

        using var response = await client.PostAsync(new Uri("/api/models/qwen/tuning/run-4/candidates/1/apply", UriKind.Relative), null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task ApplyTuningCandidate_WhileAJobIsRunning_Is409()
    {
        var (client, _, suite, jobs, _) = await StartAsync();
        var store = StoreOverSameData();
        await store.SaveTuningAsync(MakeRun("run-5", "qwen", MakeCandidate(1, CandidateOutcome.Accepted)), TestContext.Current.CancellationToken);
        suite.Gate = new TaskCompletionSource();
        await jobs.StartBenchmarkAsync("qwen", TestContext.Current.CancellationToken);

        using var response = await client.PostAsync(new Uri("/api/models/qwen/tuning/run-5/candidates/1/apply", UriKind.Relative), null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        suite.Gate.SetResult();
        await jobs.WhenIdleAsync();
    }
}
