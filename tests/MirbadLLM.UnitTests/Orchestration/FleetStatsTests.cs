using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Web.Fakes;

namespace MirbadLLM.UnitTests.Orchestration;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>The Overview's service-level figures, and the persisted fleet counters with the collector that feeds them.</summary>
public sealed class FleetStatsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fleet-stats-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private sealed class ManualTime(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone { get; } = zone ?? TimeZoneInfo.Utc;
    }

    private sealed class MutableRouterStats : IRouterStats
    {
        public List<RouterModelStats> Stats { get; set; } = [];

        public IReadOnlyList<RouterModelStats> Snapshot() => Stats;
    }

    private FleetStatsStore Store(TimeProvider time) =>
        new(Options.Create(new MirbadLLMOptions { DataPath = _dir }), time);

    // ---------------------------------------------------------------- service levels

    [Fact]
    public void Availability_IsReadyOfWhatShouldRun()
    {
        Assert.Equal(0.8, ServiceLevels.AvailabilityScore(4, 5)!.Value, 6);
        Assert.Null(ServiceLevels.AvailabilityScore(0, 0));
    }

    [Fact]
    public void Latency_FullMarksAtTheTarget_ZeroAtTwiceIt_AndBudgetsClamp()
    {
        Assert.Equal(1, ServiceLevels.LatencyScore(0.9, 1000));
        Assert.Equal(0.5, ServiceLevels.LatencyScore(1.5, 1000)!.Value, 6);
        Assert.Equal(0, ServiceLevels.LatencyScore(3, 1000));
        Assert.Null(ServiceLevels.LatencyScore(null, 1000));
        Assert.Equal(0.84, ServiceLevels.ErrorBudgetLeft(0.32, 2)!.Value, 6);
        Assert.Equal(0, ServiceLevels.ErrorBudgetLeft(7, 2));
        Assert.Equal(0.31, ServiceLevels.LatencyBudgetLeft(0.69, 1000)!.Value, 6);
        Assert.Null(ServiceLevels.ErrorBudgetLeft(null, 2));
    }

    // ---------------------------------------------------------------- store

    [Fact]
    public void Store_CountsPerLocalDay_AndSurvivesARestart()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");
        var time = new ManualTime(new DateTimeOffset(2026, 11, 2, 20, 30, 0, TimeSpan.Zero), zone); // 23:30 local
        var store = Store(time);
        store.AddRequests(10);
        store.AddRequests(0);
        store.AddTokens(5000, time.Now);
        store.AddTokens(3000, time.Now.AddHours(1)); // 00:30 local: the next day
        store.AddTokens(double.NaN, time.Now);
        store.RecordCrash(time.Now.AddMinutes(-5));
        store.RecordCrash(time.Now.AddMinutes(-10)); // older: ignored
        store.Flush();

        var reloaded = Store(time).Snapshot();

        Assert.Equal(10, reloaded.Requests);
        Assert.Equal(8000, reloaded.Tokens);
        Assert.Equal(5000, reloaded.TokensByDay["2026-11-02"]);
        Assert.Equal(3000, reloaded.TokensOn(time.Now.AddHours(1), zone));
        Assert.Equal(time.Now.AddMinutes(-5), reloaded.LastCrashAt);
        Assert.True(File.Exists(Path.Combine(_dir, FleetStatsStore.FileName)));
        Assert.False(File.Exists(Path.Combine(_dir, FleetStatsStore.FileName + ".tmp")));
    }

    [Fact]
    public void Store_ReadsAFileWithTheRemovedQuestAndPerModelFields_AndNoLongerWritesThem()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, FleetStatsStore.FileName), """
            {
              "startedAt": "2026-10-01T00:00:00+00:00",
              "requests": 42,
              "tokens": 9000,
              "models": { "chat": { "requests": 42, "tokens": 9000 } },
              "tokensByDay": { "2026-11-02": 9000 },
              "lastCrashAt": null,
              "questXp": 80,
              "quests": { "lock-v1": "2026-10-02T00:00:00+00:00" }
            }
            """);
        var time = new ManualTime(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));

        var store = Store(time);
        Assert.Equal(42, store.Snapshot().Requests);
        Assert.Equal(9000, store.Snapshot().TokensOn(time.Now, TimeZoneInfo.Utc));

        store.AddRequests(1);
        store.Flush();
        string json = File.ReadAllText(Path.Combine(_dir, FleetStatsStore.FileName));
        Assert.DoesNotContain("\"quest", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"models\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(43, Store(time).Snapshot().Requests);
    }

    [Fact]
    public void Store_WithoutADataPath_KeepsCountsInMemoryOnly()
    {
        var store = new FleetStatsStore(Options.Create(new MirbadLLMOptions { DataPath = "" }));
        store.AddRequests(3);
        store.Flush();

        Assert.Equal(3, store.Snapshot().Requests);
        Assert.False(File.Exists(FleetStatsStore.FileName));
    }

    [Fact]
    public void Store_AnUnreadableFile_StartsFresh()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, FleetStatsStore.FileName), "{ not json");

        var store = Store(new ManualTime(DateTimeOffset.UtcNow));

        Assert.Equal(0, store.Snapshot().Requests);
    }

    // ---------------------------------------------------------------- collector

    [Fact]
    public async Task Collector_CountsRouterGrowth_TokensFromNewSamples_AndCrashes()
    {
        var t0 = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var time = new ManualTime(t0);
        var router = new MutableRouterStats { Stats = [new RouterModelStats("Chat", 10, 2, 0)] };
        var history = new FakeMetricsHistory();
        var model = new ModelDefinition { Id = "chat", ServedName = "Chat", ModelPath = "p" };
        var recovery = new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = string.Empty, CrashRestartLimit = 3 }));
        var services = new ServiceCollection()
            .AddSingleton<IRouterStats>(router)
            .AddSingleton<IMetricsHistory>(history)
            .AddSingleton<IModelRegistry>(new FakeModelRegistry([model]))
            .AddSingleton(recovery)
            .BuildServiceProvider();
        var store = Store(time);
        var collector = new FleetStatsCollector(services, store, time);

        static ModelKpis Rate(double tokensPerSecond) =>
            new(new ModelMetrics(0, 0, 0, tokensPerSecond, 0), 0, null, null, null, null, null, null, 0, new Dictionary<string, double>(), 0);

        // First pass: 8 served so far (10 − 2 failed); two samples since the collector started, the first without rates.
        history.ModelSamplesById["chat"] = [new ModelSample(t0, Rate(100)) { HasRates = false }, new ModelSample(t0.AddSeconds(1), Rate(20))];
        await collector.CollectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(8, store.Snapshot().Requests);
        Assert.Equal(20 * 5, store.Snapshot().Tokens); // 20 tok/s × the 5 s sampling interval

        // Second pass: the same samples are not counted again; a new one is, and the router grew by 5.
        time.Now = t0.AddSeconds(5);
        history.ModelSamplesById["chat"] = [.. history.ModelSamplesById["chat"], new ModelSample(t0.AddSeconds(6), Rate(10))];
        router.Stats = [new RouterModelStats("Chat", 15, 2, 0)];
        recovery.RecordCrash("dgx", "chat", "boom");
        await collector.CollectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(13, store.Snapshot().Requests);
        Assert.Equal(150, store.Snapshot().Tokens);
        Assert.NotNull(store.Snapshot().LastCrashAt);

        // The controller restarted: the router counts from zero again; that is new traffic, not a negative delta.
        router.Stats = [new RouterModelStats("Chat", 3, 0, 0)];
        await collector.CollectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(16, store.Snapshot().Requests);
    }
}
