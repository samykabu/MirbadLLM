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
}
