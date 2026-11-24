using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Router.Fakes;
using System.Net.Http.Json;
using System.Net;

namespace MirbadLLM.UnitTests.Router;

public sealed class RouterStatsTests
{
    [Fact]
    public async Task Stats_CountTotalFailedAndInFlight()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        var responses = new Queue<HttpStatusCode>([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.InternalServerError]);
        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(responses.Dequeue()) { Content = new StringContent("body") }),
        };

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);
        }

        var stats = app.Services.GetRequiredService<IRouterStats>().Snapshot();
        var chatStats = Assert.Single(stats, s => s.ServedName == "chat-model");
        Assert.Equal(3, chatStats.TotalRequests);
        Assert.Equal(1, chatStats.FailedRequests);
        Assert.Equal(0, chatStats.InFlight);
    }

    [Fact]
    public async Task Stats_ExceptionCountsAsFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        var handler = new FakeUpstreamHandler { Handler = (_, _) => throw new HttpRequestException("down") };

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        var stats = app.Services.GetRequiredService<IRouterStats>().Snapshot();
        var chatStats = Assert.Single(stats, s => s.ServedName == "chat-model");
        Assert.Equal(1, chatStats.TotalRequests);
        Assert.Equal(1, chatStats.FailedRequests);
        Assert.Equal(0, chatStats.InFlight);
    }

    [Fact]
    public void FinishedIn_CountsOnlyRequestsThatFinishedInsideTheWindow()
    {
        var clock = new StepClock();
        var stats = new MirbadLLM.Router.RouterStats(clock);

        // 20 min ago: 3 finished, all failed.
        for (int i = 0; i < 3; i++)
        {
            stats.RequestStarted("chat-model");
            stats.RequestCompleted("chat-model", failed: true);
        }

        clock.Now += TimeSpan.FromMinutes(20);

        // Now: 4 finished, 1 failed, and 1 still in flight.
        for (int i = 0; i < 4; i++)
        {
            stats.RequestStarted("chat-model");
            stats.RequestCompleted("chat-model", failed: i == 0);
        }

        stats.RequestStarted("chat-model");

        var window = Assert.Single(stats.FinishedIn(TimeSpan.FromMinutes(15)));
        Assert.Equal(4, window.TotalRequests);
        Assert.Equal(1, window.FailedRequests);
        Assert.Equal(1, window.InFlight);

        var hour = Assert.Single(stats.FinishedIn(TimeSpan.FromMinutes(60)));
        Assert.Equal(7, hour.TotalRequests);
        Assert.Equal(4, hour.FailedRequests);

        // An hour later the old buckets have aged out, and a model with nothing finished has no row.
        clock.Now += TimeSpan.FromMinutes(61);
        Assert.Empty(stats.FinishedIn(TimeSpan.FromMinutes(60)));

        // Lifetime counters are unchanged by the window.
        var lifetime = Assert.Single(stats.Snapshot());
        Assert.Equal(8, lifetime.TotalRequests);
        Assert.Equal(4, lifetime.FailedRequests);
    }

    private sealed class StepClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
