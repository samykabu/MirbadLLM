using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Router.Fakes;

namespace MirbadLLM.UnitTests.Router;

public sealed class RouterStreamingTests
{
    [Fact]
    public async Task SseStreaming_DeliversChunksIncrementally()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.WebHost.UseTestServer();
        var upstreamApp = upstreamBuilder.Build();
        upstreamApp.Run(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await context.Response.WriteAsync("data: first\n\n");
            await context.Response.Body.FlushAsync();
            await gate.Task;
            await context.Response.WriteAsync("data: second\n\n");
            await context.Response.Body.FlushAsync();
        });
        await upstreamApp.StartAsync(ct);
        await using var upstreamAppDisposable = upstreamApp;

        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("sse-1", "sse-model", new Uri("http://upstream/"), ModelState.Ready));

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            new FakeModelRuntime(),
            configureServices: services => RouterProxyTests.ConfigureUpstream(services, upstreamApp.GetTestServer().CreateHandler()));
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"sse-model"}""", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stream = await response.Content.ReadAsStreamAsync(ct);

        var first = await ReadAvailableAsync(stream, ct);
        Assert.Contains("first", first);
        Assert.DoesNotContain("second", first);

        gate.SetResult();

        var second = await ReadAvailableAsync(stream, ct);
        Assert.Contains("second", second);
    }

    [Fact]
    public async Task ClientCancellation_PropagatesToUpstream()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        var upstreamEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeUpstreamHandler
        {
            Handler = async (_, upstreamCt) =>
            {
                using var registration = upstreamCt.Register(() => upstreamCancelled.TrySetResult());
                upstreamEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, upstreamCt);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"chat-model"}""", Encoding.UTF8, "application/json"),
        };

        var sendTask = client.SendAsync(request, cts.Token);
        // Cancel only once the upstream call is in flight. A fixed 100 ms delay (the old code) raced a loaded machine:
        // cancelled before the proxy reached the upstream, the handler never ran and the wait below timed out (~5 %).
        await upstreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sendTask);
        await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    private static async Task<string> ReadAvailableAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        var read = await stream.ReadAsync(buffer, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), ct);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
