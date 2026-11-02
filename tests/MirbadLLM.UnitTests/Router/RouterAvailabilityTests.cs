using Microsoft.AspNetCore.TestHost;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Router.Fakes;
using System.Net.Http.Json;
using System.Net;

namespace MirbadLLM.UnitTests.Router;

public sealed class RouterAvailabilityTests
{
    [Fact]
    public async Task ModelStarting_NoAutoStart_Returns503WithRetryAfter()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Starting));

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), o => o.AutoStartOnRequest = false);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("30", response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("F0"));
    }

    [Fact]
    public async Task AutoStartOnRequest_StartsAndWaitsUntilReady_ThenForwards()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        var upstreamUri = new Uri("http://upstream/");
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", upstreamUri, ModelState.Stopped));

        var runtime = new FakeModelRuntime();
        runtime.SetStatus("chat-1", new ModelStatus("chat-1", ModelState.Stopped));
        runtime.StartHandler = (id, force) =>
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(200, ct);
                runtime.SetStatus(id, new ModelStatus(id, ModelState.Ready));
                resolver.Set(new ModelEndpoint(id, "chat-model", upstreamUri, ModelState.Ready));
            }, ct);
            return new ModelStatus(id, ModelState.Starting);
        };

        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }),
        };

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            runtime,
            o =>
            {
                o.AutoStartOnRequest = true;
                o.StartupTimeout = TimeSpan.FromSeconds(5);
            },
            services => RouterProxyTests.ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, runtime.StartCallCount);
    }

    [Fact]
    public async Task AutoStartOnRequest_TimesOut_Returns503()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Stopped));

        var runtime = new FakeModelRuntime();
        runtime.SetStatus("chat-1", new ModelStatus("chat-1", ModelState.Stopped));
        runtime.StartHandler = (id, _) => new ModelStatus(id, ModelState.Starting); // never becomes Ready

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            runtime,
            o =>
            {
                o.AutoStartOnRequest = true;
                o.StartupTimeout = TimeSpan.FromMilliseconds(700);
            });
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, runtime.StartCallCount);
    }

    [Fact]
    public async Task AutoStartOnRequest_MemoryRefused_Returns503WithoutWaiting()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Stopped));

        var runtime = new FakeModelRuntime();
        runtime.StartHandler = (_, _) => throw new ModelMemoryAdmissionException("not enough memory");

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            runtime,
            o =>
            {
                o.AutoStartOnRequest = true;
                o.StartupTimeout = TimeSpan.FromMinutes(5);
            });
        using var client = app.GetTestClient();

        var started = DateTimeOffset.UtcNow;
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));
    }
}
