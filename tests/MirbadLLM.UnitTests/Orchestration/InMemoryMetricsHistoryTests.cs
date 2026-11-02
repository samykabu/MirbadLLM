using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Orchestration;

public sealed class InMemoryMetricsHistoryTests
{
    private static InMemoryMetricsHistory Create(ManualTimeProvider timeProvider, int retentionMinutes = 60)
        => new(Options.Create(new MetricsOptions { MetricsRetentionMinutes = retentionMinutes }), timeProvider);

    private static HostSample HostSampleAt(DateTimeOffset at) => new(at, new MemorySnapshot(100, 50), null);

    private static ModelSample ModelSampleAt(DateTimeOffset at, double runningRequests = 0)
        => new(at, new ModelKpis(
            new ModelMetrics(runningRequests, 0, 0, 0, 0),
            RequestsPerMinute: 0,
            TimeToFirstToken: null,
            InterTokenLatency: null,
            EndToEndLatency: null,
            QueueTime: null,
            PrefixCacheHitRate: null,
            SpecDecodeAcceptanceRate: null,
            PreemptionsPerMinute: 0,
            FinishedByReasonPerMinute: new Dictionary<string, double>(),
            ToolCallsPerMinute: 0));

    [Fact]
    public void GetHost_ReturnsOnlySamplesWithinWindow()
    {
        var time = new ManualTimeProvider();
        var history = Create(time);

        history.AddHost(HostSampleAt(time.GetUtcNow()));
        time.Advance(TimeSpan.FromMinutes(5));
        history.AddHost(HostSampleAt(time.GetUtcNow()));

        Assert.Equal(2, history.GetHost(TimeSpan.FromMinutes(10)).Count);
        Assert.Single(history.GetHost(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void AddHost_TrimsSamplesOlderThanRetentionWindow()
    {
        var time = new ManualTimeProvider();
        var history = Create(time, retentionMinutes: 1);

        history.AddHost(HostSampleAt(time.GetUtcNow()));
        time.Advance(TimeSpan.FromMinutes(2));
        history.AddHost(HostSampleAt(time.GetUtcNow())); // triggers a trim past the 1-minute retention

        // A very wide window should still only see what retention kept, not the purged first sample.
        Assert.Single(history.GetHost(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void AddModel_AndGetModel_ReturnsSamplesWithinWindow()
    {
        var time = new ManualTimeProvider();
        var history = Create(time);

        history.AddModel("m1", ModelSampleAt(time.GetUtcNow(), runningRequests: 1));
        time.Advance(TimeSpan.FromSeconds(30));
        history.AddModel("m1", ModelSampleAt(time.GetUtcNow(), runningRequests: 2));

        var window = history.GetModel("m1", TimeSpan.FromMinutes(5));
        Assert.Equal(2, window.Count);
        Assert.Equal(2, window[^1].Kpis.Load.RunningRequests);
    }

    [Fact]
    public void GetModel_UnknownModel_ReturnsEmpty()
    {
        var history = Create(new ManualTimeProvider());

        Assert.Empty(history.GetModel("unknown", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Latest_ReturnsOnlyTheMostRecentSamplePerModel()
    {
        var time = new ManualTimeProvider();
        var history = Create(time);

        history.AddModel("m1", ModelSampleAt(time.GetUtcNow(), runningRequests: 1));
        time.Advance(TimeSpan.FromSeconds(5));
        history.AddModel("m1", ModelSampleAt(time.GetUtcNow(), runningRequests: 9));
        history.AddModel("m2", ModelSampleAt(time.GetUtcNow(), runningRequests: 3));

        var latest = history.Latest();

        Assert.Equal(9.0, latest["m1"].Load.RunningRequests);
        Assert.Equal(3.0, latest["m2"].Load.RunningRequests);
    }

    [Fact]
    public void Latest_LeavesOutAModelWhoseSamplesStopped()
    {
        // Stopped, crashed or deleted: the newest sample ages past LatestFreshness (60 s at the 5 s interval) and the
        // model is no longer "running now", although retention still keeps its series for the charts.
        var time = new ManualTimeProvider();
        var history = Create(time);
        history.AddModel("m1", ModelSampleAt(time.GetUtcNow()));
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.True(history.Latest().ContainsKey("m1"));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(history.Latest().ContainsKey("m1"));
        Assert.Single(history.GetModel("m1", TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void RemoveModel_DropsItsHistoryAndLatest()
    {
        var time = new ManualTimeProvider();
        var history = Create(time);

        history.AddModel("m1", ModelSampleAt(time.GetUtcNow()));
        history.RemoveModel("m1");

        Assert.Empty(history.GetModel("m1", TimeSpan.FromMinutes(5)));
        Assert.False(history.Latest().ContainsKey("m1"));
    }
}
