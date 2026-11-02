using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>The circuit-wide refresh source: one timer however many components subscribe, one load shared by
/// all of them, and only the sources somebody needs.</summary>
public sealed class LiveDataServiceTests
{
    private sealed class CountingRuntime : IModelRuntime
    {
        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<ModelStatus>>([new ModelStatus("m", ModelState.Ready)]);
        }

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) => Task.FromResult(new ModelStatus(id, ModelState.Ready));

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static (ServiceProvider Provider, CountingRuntime Runtime) Build()
    {
        var runtime = new CountingRuntime();
        var services = new ServiceCollection();
        services.AddSingleton<IModelRegistry>(new FakeModelRegistry([new ModelDefinition { Id = "m", ServedName = "M", ModelPath = "m" }]));
        services.AddSingleton<IModelRuntime>(runtime);
        services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(128, 64)));
        return (services.BuildServiceProvider(), runtime);
    }

    [Fact]
    public async Task ManySubscribers_ShareOneTimer_AndOneLoadPerRefresh()
    {
        var (provider, runtime) = Build();
        await using var _ = provider;
        await using var live = new LiveDataService(provider);

        var seen = new List<string>();
        using var statusBar = live.Subscribe(LiveNeeds.Models, s => { seen.Add("bar:" + s.Models!.Count); return Task.CompletedTask; });
        using var page = live.Subscribe(LiveNeeds.Models | LiveNeeds.Statuses | LiveNeeds.Memory, s =>
        {
            seen.Add("page:" + s.Statuses!.Count + ":" + s.Memory!.AvailableGiB);
            return Task.CompletedTask;
        });

        Assert.Equal(1, live.TimerStarts);
        Assert.True(live.IsRunning);
        Assert.Equal(2, live.SubscriberCount);

        var snapshot = await live.RefreshAsync(TestContext.Current.CancellationToken);

        // One load covering the union of what both need, published to both.
        Assert.Equal(1, live.Fetches);
        Assert.Equal(1, runtime.ListCalls);
        Assert.True(snapshot.Covers(LiveNeeds.Models | LiveNeeds.Statuses | LiveNeeds.Memory));
        Assert.Equal(["bar:1", "page:1:64"], seen);
    }

    [Fact]
    public async Task GetAsync_ReusesAFreshSnapshot_InsteadOfLoadingAgain()
    {
        var (provider, runtime) = Build();
        await using var _ = provider;
        await using var live = new LiveDataService(provider);
        var ct = TestContext.Current.CancellationToken;

        var first = await live.GetAsync(LiveNeeds.Models | LiveNeeds.Statuses, ct);
        var second = await live.GetAsync(LiveNeeds.Models, ct);

        Assert.Same(first, second);
        Assert.Equal(1, live.Fetches);
        Assert.Equal(1, runtime.ListCalls);
    }

    [Fact]
    public async Task OnlyRequestedSources_AreRead()
    {
        var (provider, runtime) = Build();
        await using var _ = provider;
        await using var live = new LiveDataService(provider);

        var snapshot = await live.GetAsync(LiveNeeds.Models, TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot.Models);
        Assert.Null(snapshot.Statuses);
        Assert.Null(snapshot.Memory);
        Assert.Equal(0, runtime.ListCalls); // the status bar alone never asks Docker
    }

    [Fact]
    public async Task TimerStops_WithTheLastSubscriber_AndRestartsForTheNext()
    {
        var (provider, _) = Build();
        await using var _ = provider;
        await using var live = new LiveDataService(provider);

        var a = live.Subscribe(LiveNeeds.Models, _ => Task.CompletedTask);
        var b = live.Subscribe(LiveNeeds.Models, _ => Task.CompletedTask);
        a.Dispose();
        Assert.True(live.IsRunning);
        b.Dispose();
        Assert.False(live.IsRunning);

        using var c = live.Subscribe(LiveNeeds.Models, _ => Task.CompletedTask);
        Assert.True(live.IsRunning);
        Assert.Equal(2, live.TimerStarts);
    }

    [Fact]
    public async Task AFailingSubscriber_DoesNotStopTheOthers()
    {
        var (provider, _) = Build();
        await using var _ = provider;
        await using var live = new LiveDataService(provider);
        bool reached = false;
        using var bad = live.Subscribe(LiveNeeds.Models, _ => throw new InvalidOperationException("boom"));
        using var good = live.Subscribe(LiveNeeds.Models, _ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        await live.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(reached);
    }

    [Fact]
    public async Task Resolve_PrefersTheSharedInstance()
    {
        var services = new ServiceCollection();
        services.AddScoped<LiveDataService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var one = LiveDataService.Resolve(scope.ServiceProvider, out bool ownedOne);
        var two = LiveDataService.Resolve(scope.ServiceProvider, out bool ownedTwo);

        Assert.Same(one, two);
        Assert.False(ownedOne);
        Assert.False(ownedTwo);
    }

    [Fact]
    public async Task Snapshot_CarriesEveryMaintenanceWindow_AndFindsAModelsOwnJob()
    {
        // MN-126: with one job per node, readers asking about a model must see its own job's window, not just the oldest.
        var runtime = new CountingRuntime();
        var maintenance = new MaintenanceModeService();
        var now = DateTimeOffset.UtcNow;
        maintenance.Enter(new MaintenanceState("job-a", MaintenanceJobKinds.Benchmark, "qwen", now, now.AddMinutes(5), "dgx"));
        maintenance.Enter(new MaintenanceState("job-b", MaintenanceJobKinds.Tuning, "gemma", now, now.AddMinutes(9), "pc"));
        var services = new ServiceCollection();
        services.AddSingleton<IModelRegistry>(new FakeModelRegistry([new ModelDefinition { Id = "m", ServedName = "M", ModelPath = "m" }]));
        services.AddSingleton<IModelRuntime>(runtime);
        services.AddSingleton<IMaintenanceMode>(maintenance);
        await using var provider = services.BuildServiceProvider();
        await using var live = new LiveDataService(provider);

        var snapshot = await live.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["job-a", "job-b"], snapshot.Windows.Select(w => w.JobId));
        Assert.Equal("job-a", snapshot.Maintenance?.JobId);
        Assert.Equal("job-b", snapshot.JobOn("gemma")?.JobId);
        Assert.Equal("job-a", snapshot.JobOn("qwen")?.JobId);
        Assert.Null(snapshot.JobOn("m"));
    }
}
