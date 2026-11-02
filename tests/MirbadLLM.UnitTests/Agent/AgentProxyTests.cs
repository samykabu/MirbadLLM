using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Proxy;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Pki;
using ManualClock = MirbadLLM.UnitTests.Orchestration.Fakes.ManualTimeProvider;

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-019: the agent inference proxy (design §7.2–7.4) end to end over the production data-channel shape: an in-process
/// Kestrel agent with the mTLS listener settings MN-014 uses (<see cref="PkiTls"/>, HTTP/1.1 + HTTP/2, 64 MiB + headroom
/// body limit), a master-shaped client (client certificate, pinned agent, HTTP/2 exact) and the MN-008
/// <see cref="FakeModelServer"/> (or a recording upstream where the test must observe the model side directly).
/// </summary>
public sealed class AgentProxyTests : IDisposable
{
    private const string ModelId = "qwen";
    private readonly PkiMaterial _pki = new();

    public void Dispose() => _pki.Dispose();

    // ---- Streaming ----

    [Fact]
    public async Task SseChunksArriveIncrementallyThroughTheProxy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.CreateAsync(ct: ct);
        model.SetTimeToFirstTokenMs(50);
        model.SetTokenRateMs(50); // 20 tokens over ≈ 1 s
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, model.BaseUri), ct: ct);

        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/chat/completions", ChatBody(stream: true));
        using var response = await agent.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new Version(2, 0), response.Version);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var arrivals = new List<(TimeSpan At, string Line)>();
        var clock = Stopwatch.StartNew();
        using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct)))
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    arrivals.Add((clock.Elapsed, line));
                }
            }
        }

        // role chunk + 20 token chunks + final chunk + [DONE], in order, byte for byte.
        Assert.Equal(23, arrivals.Count);
        Assert.Contains("\"chat.completion.chunk\"", arrivals[0].Line, StringComparison.Ordinal);
        Assert.Contains("token_0", arrivals[1].Line, StringComparison.Ordinal);
        Assert.Contains("token_19", arrivals[20].Line, StringComparison.Ordinal);
        Assert.Equal("data: [DONE]", arrivals[^1].Line);

        // Unbuffered: the tokens were spread over the generation, not delivered in one burst at the end.
        TimeSpan spread = arrivals[20].At - arrivals[1].At;
        Assert.True(spread >= TimeSpan.FromMilliseconds(500), $"Token chunks arrived within {spread.TotalMilliseconds} ms: the proxy buffered.");
    }

    [Fact]
    public async Task ModelResetMidStreamAbortsTheClientStreamWithoutAnErrorBody()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.CreateAsync(ct: ct);
        model.SetTimeToFirstTokenMs(10);
        model.SetTokenRateMs(10);
        // Reset once the client has read the third token (an abort right after a write can drop unsent bytes, headers too).
        var reset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.SetResetAfterTokenCount(3, reset.Task);
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, model.BaseUri), ct: ct);

        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/chat/completions", ChatBody(stream: true));
        using var response = await agent.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var received = new StringBuilder();
        var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[4096];
        Exception? failure = null;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                received.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (received.ToString().Contains("token_2", StringComparison.Ordinal))
                {
                    reset.TrySetResult();
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            failure = ex;
        }

        Assert.Contains("token_2", received.ToString(), StringComparison.Ordinal);

        // §7.4: after the response started a failure is an abort the client can see, never a clean end or JSON.
        Assert.NotNull(failure);
        string text = received.ToString();
        Assert.DoesNotContain("[DONE]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain(ErrorCodes.ModelNotRunning, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonStreamingAndPlainPathsPassThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.CreateAsync(ct: ct);
        model.SetTimeToFirstTokenMs(0);
        model.SetTokenRateMs(0);
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, model.BaseUri), ct: ct);

        using (var request = agent.Request(HttpMethod.Post, ModelId, "v1/chat/completions", ChatBody(stream: false)))
        using (var response = await agent.Client.SendAsync(request, ct))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        }

        foreach (string path in new[] { "health", "metrics", "v1/models" })
        {
            using var request = agent.Request(HttpMethod.Get, ModelId, path);
            using var response = await agent.Client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var request = agent.Request(HttpMethod.Get, ModelId, "metrics"))
        using (var response = await agent.Client.SendAsync(request, ct))
        {
            Assert.Contains("vllm:generation_tokens_total", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ModelErrorResponsesPassThroughUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.CreateAsync(ct: ct);
        model.SetFailureStatus(500);
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, model.BaseUri), ct: ct);

        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/chat/completions", ChatBody(stream: false));
        using var response = await agent.Client.SendAsync(request, ct);

        // vLLM's own error is the model's answer, not an agent error: status and body unchanged (§7.4).
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("server_error", body, StringComparison.Ordinal);
        Assert.DoesNotContain(ErrorCodes.ModelNotRunning, body, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains(Protocol.AgentErrorHeader));
    }

    // ---- Allow-list and headers ----

    [Theory]
    [InlineData("dev/sleep")]
    [InlineData("openapi.json")]
    [InlineData("v1")]
    [InlineData("health/extra")]
    [InlineData("start_profile")]
    [InlineData("V1/models")]
    public async Task PathsOutsideTheAllowListAre404NotFound(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await RecordingUpstream.StartAsync(ct);
        var resolver = FakeResolver.For(ModelId, upstream.BaseUri);
        await using var agent = await ProxyHarness.StartAsync(_pki, resolver, ct: ct);

        using var request = agent.Request(HttpMethod.Post, ModelId, path, new StringContent("{}"));
        using var response = await agent.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(ErrorCodes.NotFound, (await ReadAgentErrorAsync(response, ct)).Code);
        Assert.Equal(ErrorCodes.NotFound, Assert.Single(response.Headers.GetValues(Protocol.AgentErrorHeader)));
        Assert.Equal(0, resolver.Resolves);
        Assert.Equal(0, upstream.Requests);
    }

    [Fact]
    public async Task HeaderRulesMatchTheMasterProxyAndQueryIsForwarded()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await RecordingUpstream.StartAsync(ct);
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, upstream.BaseUri), ct: ct);

        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/echo?a=1&b=two%20words", new StringContent("{\"x\":1}", Encoding.UTF8, "application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "client-api-key");
        request.Headers.Add("X-Request-Id", "abc-123");
        request.Headers.Add(Protocol.ProtocolHeader, "1");
        request.Headers.Add(Protocol.MasterHeader, "m/1/b");
        using var response = await agent.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("X-Upstream", out var marker) && marker.Single() == "yes", "Response headers were not copied.");
        Assert.False(response.Headers.Contains(Protocol.AgentErrorHeader), "The agent-error marker must be stripped from model responses.");
        var seen = Assert.Single(upstream.Seen);
        Assert.Equal("POST", seen.Method);
        Assert.Equal("?a=1&b=two%20words", seen.Query);
        Assert.Equal(7, seen.BodyBytes);
        Assert.Equal("abc-123", seen.Headers.GetValueOrDefault("X-Request-Id"));
        Assert.StartsWith("application/json", seen.Headers.GetValueOrDefault("Content-Type"), StringComparison.Ordinal);
        Assert.False(seen.Headers.ContainsKey("Authorization"), "Authorization must not reach the model.");
        Assert.False(seen.Headers.ContainsKey(Protocol.ProtocolHeader));
        Assert.False(seen.Headers.ContainsKey(Protocol.MasterHeader));
        // Host is the model's own address (set by the upstream client), never the agent's.
        Assert.Equal($"127.0.0.1:{upstream.BaseUri.Port}", seen.Headers.GetValueOrDefault("Host"));
    }

    // ---- Not running ----

    [Fact]
    public async Task NoInstanceIs503ModelNotRunningBeforeHeaders()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeResolver(); // knows no model
        await using var agent = await ProxyHarness.StartAsync(_pki, resolver, ct: ct);

        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/chat/completions", ChatBody(stream: true));
        using var response = await agent.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var error = await ReadAgentErrorAsync(response, ct);
        Assert.Equal(ErrorCodes.ModelNotRunning, error.Code);
        Assert.Equal(ErrorCodes.ModelNotRunning, Assert.Single(response.Headers.GetValues(Protocol.AgentErrorHeader)));
        Assert.Contains(ModelId, error.Message, StringComparison.Ordinal);
        // The master turns it into the client's 503 model_not_ready (§7.1).
        Assert.Equal(ErrorCodes.ModelNotReady, ErrorMappings.TranslateProxyError(error.Code).ClientCode);
    }

    [Fact]
    public async Task RefusedConnectionIs503ModelNotRunningAndDropsTheCachedUpstream()
    {
        var ct = TestContext.Current.CancellationToken;
        var dead = new Uri($"http://127.0.0.1:{TestPorts.Reserve()}/");
        var resolver = FakeResolver.For(ModelId, dead);
        await using var agent = await ProxyHarness.StartAsync(_pki, resolver, ct: ct);

        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/chat/completions", ChatBody(stream: false));
        using var response = await agent.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ErrorCodes.ModelNotRunning, (await ReadAgentErrorAsync(response, ct)).Code);
        Assert.Contains((ModelId, dead), resolver.Invalidated);
    }

    [Fact]
    public async Task ResolverFailureIs503ModelNotRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeResolver { Failure = new InvalidOperationException("docker is down") };
        await using var agent = await ProxyHarness.StartAsync(_pki, resolver, ct: ct);

        using var request = agent.Request(HttpMethod.Get, ModelId, "health");
        using var response = await agent.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ErrorCodes.ModelNotRunning, (await ReadAgentErrorAsync(response, ct)).Code);
    }

    // ---- Abort propagation and backpressure ----

    [Fact]
    public async Task ClientAbortMidStreamStopsTheModelGenerating()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.CreateAsync(ct: ct);
        model.SetTimeToFirstTokenMs(10);
        model.SetTokenRateMs(100); // 20 tokens over ≈ 2 s
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, model.BaseUri), ct: ct);

        using (var abort = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            using var request = agent.Request(HttpMethod.Post, ModelId, "v1/chat/completions", ChatBody(stream: true));
            using var response = await agent.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(abort.Token));
            int tokens = 0;
            while (tokens < 2 && await reader.ReadLineAsync(abort.Token) is { } line)
            {
                if (line.Contains("token_", StringComparison.Ordinal))
                {
                    tokens++;
                }
            }

            await abort.CancelAsync(); // cancel rather than dispose: no drain hides the abort (MN-090)
        }

        await WaitUntilAsync(() => agent.Proxy.InFlight == 0, "the proxy released the request", ct);
        double before = await ReadMetricAsync(model, "vllm:generation_tokens_total", ct);
        await Task.Delay(600, ct); // ≥ 5 token intervals
        double after = await ReadMetricAsync(model, "vllm:generation_tokens_total", ct);

        Assert.Equal(before, after);
        Assert.True(after < 20, $"The model generated all {after} tokens after the client left.");
        Assert.Equal(0, await ReadMetricAsync(model, "vllm:request_success_total", ct));
    }

    [Fact]
    public async Task ClientAbortBeforeHeadersCancelsTheUpstreamRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await RecordingUpstream.StartAsync(ct);
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, upstream.BaseUri), ct: ct);

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/hang", new StringContent("{}"));
        Task<HttpResponseMessage> pending = agent.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        await WaitUntilAsync(() => upstream.HangArrived == 1, "the request reached the model", ct);

        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await WaitUntilAsync(() => upstream.HangAborted == 1, "the model saw the cancellation", ct);
        await WaitUntilAsync(() => agent.Proxy.InFlight == 0, "the proxy released the request", ct);
    }

    [Fact]
    public async Task ModelSilentBeforeHeadersIs504TimeoutAndTheModelSeesTheCancel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await RecordingUpstream.StartAsync(ct);
        await using var agent = await ProxyHarness.StartAsync(
            _pki, FakeResolver.For(ModelId, upstream.BaseUri), o => o.UpstreamIdleTimeout = TimeSpan.FromMilliseconds(300), ct);

        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/hang", new StringContent("{}"));
        using var response = await agent.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(ErrorCodes.Timeout, (await ReadAgentErrorAsync(response, ct)).Code);
        await WaitUntilAsync(() => upstream.HangAborted == 1, "the model saw the cancellation", ct);
        await WaitUntilAsync(() => agent.Proxy.InFlight == 0, "the proxy released the request", ct);
    }

    [Fact]
    public async Task ModelSilentMidStreamAbortsTheClientStream()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var model = await FakeModelServer.CreateAsync(ct: ct);
        model.SetTimeToFirstTokenMs(10);
        model.SetTokenRateMs(30_000); // first token, then silence far longer than the idle limit
        await using var agent = await ProxyHarness.StartAsync(
            _pki, FakeResolver.For(ModelId, model.BaseUri), o => o.UpstreamIdleTimeout = TimeSpan.FromMilliseconds(500), ct);

        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/chat/completions", ChatBody(stream: true));
        using var response = await agent.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var started = Stopwatch.StartNew();
        Exception? failure = null;
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[4096];
            while (await stream.ReadAsync(buffer, ct) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            failure = ex;
        }

        Assert.NotNull(failure); // an abort the client can see, never a clean end
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15), $"The stalled stream ran {started.Elapsed} before ending.");
    }

    [Fact]
    public async Task SlowReaderSlowsTheUpstreamAndAbortReachesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await RecordingUpstream.StartAsync(ct);
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, upstream.BaseUri), ct: ct);

        using (var abort = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            using var request = agent.Request(HttpMethod.Get, ModelId, "v1/flood");
            using var response = await agent.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, abort.Token);
            var stream = await response.Content.ReadAsStreamAsync(abort.Token);
            await stream.ReadExactlyAsync(new byte[1024], abort.Token);

            // The client stops reading: every hop's window/buffer fills and the model's writes stall.
            await Task.Delay(1000, ct);
            long stalledAt = upstream.FloodBytes;
            await Task.Delay(500, ct);
            long later = upstream.FloodBytes;

            Assert.True(stalledAt < RecordingUpstream.FloodLimit, "The proxy did not apply backpressure: the model wrote everything.");
            Assert.True(later - stalledAt <= 1024 * 1024, $"The model kept writing ({later - stalledAt} bytes in 0.5 s) while nobody read.");
            Assert.True(stalledAt < 64L * 1024 * 1024, $"{stalledAt} bytes were buffered between the model and a stalled client.");

            await abort.CancelAsync();
        }

        await WaitUntilAsync(() => upstream.FloodAborted == 1, "the model saw the client abort", ct);
        await WaitUntilAsync(() => agent.Proxy.InFlight == 0, "the proxy released the request", ct);
    }

    // ---- Bounded resources ----

    [Fact]
    public async Task InFlightLimitAnswers503ServerBusyAndReleasesSlots()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await RecordingUpstream.StartAsync(ct);
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, upstream.BaseUri), o => o.MaxInFlight = 2, ct);

        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var first = agent.Request(HttpMethod.Post, ModelId, "v1/hang", new StringContent("{}"));
        using var second = agent.Request(HttpMethod.Post, ModelId, "v1/hang", new StringContent("{}"));
        Task<HttpResponseMessage> a = agent.Client.SendAsync(first, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        Task<HttpResponseMessage> b = agent.Client.SendAsync(second, HttpCompletionOption.ResponseHeadersRead, abort.Token);
        await WaitUntilAsync(() => upstream.HangArrived == 2, "both requests reached the model", ct);

        using (var third = agent.Request(HttpMethod.Get, ModelId, "v1/echo"))
        using (var busy = await agent.Client.SendAsync(third, ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.Equal(ErrorCodes.ServerBusy, (await ReadAgentErrorAsync(busy, ct)).Code);
            Assert.Equal(TimeSpan.FromSeconds(5), busy.Headers.RetryAfter?.Delta);
        }

        await abort.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        await WaitUntilAsync(() => agent.Proxy.InFlight == 0, "the slots were released", ct);
        await WaitUntilAsync(() => upstream.HangAborted == 2, "the model saw both cancellations", ct);

        using var fourth = agent.Request(HttpMethod.Get, ModelId, "v1/echo");
        using var ok = await agent.Client.SendAsync(fourth, ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task SixtyFourMiBBodyIsStreamedToTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await RecordingUpstream.StartAsync(ct);
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, upstream.BaseUri), ct: ct);

        byte[] body = new byte[Protocol.MaxProxyRequestBodyBytes];
        RandomNumberGenerator.Fill(body.AsSpan(0, 4096));
        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/echo", new ByteArrayContent(body));
        using var response = await agent.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(upstream.Seen);
        Assert.Equal(body.LongLength, seen.BodyBytes);
        Assert.Equal(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), seen.Headers.GetValueOrDefault("Content-Length"));
    }

    [Fact]
    public async Task RequestBodyReachesTheModelBeforeTheClientFinishedSending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await RecordingUpstream.StartAsync(ct);
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, upstream.BaseUri), ct: ct);

        const int firstPart = 1024 * 1024;
        // The second half is only sent once the model has the first: a proxy that re-buffered the body would deadlock.
        using var content = new GatedContent(firstPart, firstPart, () => WaitUntilAsync(() => upstream.BodyBytesReceived >= firstPart, "the first half reached the model", ct));
        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/echo", content);
        using var response = await agent.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2L * firstPart, Assert.Single(upstream.Seen).BodyBytes);
    }

    [Fact]
    public async Task DeclaredBodyOverTheLimitIs413WithoutReachingTheModel()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await RecordingUpstream.StartAsync(ct);
        var resolver = FakeResolver.For(ModelId, upstream.BaseUri);
        await using var agent = await ProxyHarness.StartAsync(_pki, resolver, o => o.MaxRequestBodyBytes = 1024 * 1024, ct);

        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/echo", new ByteArrayContent(new byte[(1024 * 1024) + 1]));
        using var response = await agent.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidRequest, (await ReadAgentErrorAsync(response, ct)).Code);
        Assert.Equal(0, upstream.Requests);
        Assert.Equal(0, resolver.Resolves);
    }

    [Fact]
    public async Task UndeclaredBodyOverTheLimitIs413()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var upstream = await RecordingUpstream.StartAsync(ct);
        await using var agent = await ProxyHarness.StartAsync(_pki, FakeResolver.For(ModelId, upstream.BaseUri), o => o.MaxRequestBodyBytes = 1024 * 1024, ct);

        // No Content-Length (HTTP/2 DATA frames until END_STREAM): the limit is enforced while streaming.
        using var content = new GatedContent(1024 * 1024, 1024 * 1024, () => Task.CompletedTask, declareLength: false);
        using var request = agent.Request(HttpMethod.Post, ModelId, "v1/echo", content);
        using var response = await agent.Client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidRequest, (await ReadAgentErrorAsync(response, ct)).Code);
        await WaitUntilAsync(() => agent.Proxy.InFlight == 0, "the proxy released the request", ct);
    }

    // ---- Units ----

    [Theory]
    [InlineData("v1/chat/completions", true, "v1/chat/completions")]
    [InlineData("v1/models", true, "v1/models")]
    [InlineData("v1/a b", true, "v1/a%20b")]
    [InlineData("health", true, "health")]
    [InlineData("metrics", true, "metrics")]
    [InlineData("tokenize", true, "tokenize")]
    [InlineData("classify", true, "classify")]
    [InlineData("v1", false, "")]
    [InlineData("v1/", false, "")]
    [InlineData("", false, "")]
    [InlineData(null, false, "")]
    [InlineData("v1/../dev/sleep", false, "")]
    [InlineData("v1/./models", false, "")]
    [InlineData("v1//models", false, "")]
    [InlineData("v1/%2e%2e/dev", false, "")]
    [InlineData("v1/a%2Fb", false, "")]
    [InlineData("v1\\..\\dev", false, "")]
    [InlineData("health/x", false, "")]
    [InlineData("Health", false, "")]
    [InlineData("reset_prefix_cache", false, "")]
    [InlineData("sleep", false, "")]
    public void PathPolicyAllowsOnlyPlainAllowListedPaths(string? path, bool allowed, string upstreamPath)
    {
        var policy = new ProxyPathPolicy(Protocol.DefaultProxyAllowedPaths);

        Assert.Equal(allowed, policy.TryGetUpstreamPath(path, out string actual));
        Assert.Equal(upstreamPath, actual);
    }

    [Fact]
    public void PathPolicyHonoursAConfiguredList()
    {
        var policy = new ProxyPathPolicy(["/v2/*", "ping"]);

        Assert.True(policy.TryGetUpstreamPath("v2/x/y", out _));
        Assert.True(policy.TryGetUpstreamPath("ping", out _));
        Assert.False(policy.TryGetUpstreamPath("v1/chat/completions", out _));
        Assert.False(policy.TryGetUpstreamPath("health", out _));
    }

    [Fact]
    public async Task InFlightLimiterNeverExceedsItsLimitUnderContention()
    {
        var limiter = new ProxyInFlightLimiter(8);
        int peak = 0;
        int admitted = 0;
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < 200; i++)
            {
                if (limiter.TryAcquire())
                {
                    Interlocked.Increment(ref admitted);
                    int now = limiter.InFlight;
                    int seen;
                    while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
                    {
                    }

                    await Task.Yield();
                    limiter.Release();
                }
            }
        })));

        Assert.True(peak <= 8, $"Peak {peak} exceeded the limit.");
        Assert.True(admitted > 0);
        Assert.Equal(0, limiter.InFlight);
    }

    [Fact]
    public async Task DriverResolverCachesPositiveAnswersBrieflyAndForgetsFailedOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        var driver = new StubDriver { Upstream = new Uri("http://127.0.0.1:20001/") };
        var clock = new ManualClock();
        var resolver = new RuntimeDriverUpstreamResolver(
            [driver], Microsoft.Extensions.Options.Options.Create(new AgentProxyOptions()), clock, new UpstreamChanges());

        Assert.Equal(driver.Upstream, await resolver.ResolveAsync(ModelId, ct));
        Assert.Equal(driver.Upstream, await resolver.ResolveAsync(ModelId, ct));
        Assert.Equal(1, driver.Calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(driver.Upstream, await resolver.ResolveAsync(ModelId, ct));
        Assert.Equal(2, driver.Calls);

        // A failed connection drops the entry: a replaced instance on a new port is found on the next request.
        resolver.Invalidate(ModelId, new Uri("http://127.0.0.1:20001/"));
        driver.Upstream = new Uri("http://127.0.0.1:20002/");
        Assert.Equal(driver.Upstream, await resolver.ResolveAsync(ModelId, ct));
        Assert.Equal(3, driver.Calls);

        // "Not running" is never cached.
        driver.Upstream = null;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(await resolver.ResolveAsync(ModelId, ct));
        Assert.Null(await resolver.ResolveAsync(ModelId, ct));
        Assert.Equal(5, driver.Calls);
    }

    [Fact]
    public async Task DriverResolverForgetsACachedUpstreamAsSoonAsTheDriverReplacesTheInstance()
    {
        // MN-239 bug B. The 1 s cache was only ever invalidated by a failed connection, so a relaunch that moved
        // llama-server to a new port inside the TTL was proxied to the old port, answered 503 model_not_running and
        // made a tuning run record the candidate Failed:
        //   AgentProxy[1902] Proxy: ov-intent-analysis-win at http://127.0.0.1:22577/ did not answer
        //   (… actively refused it.); answered model_not_running.
        // The clock does not move at all here: only the instance changes.
        var ct = TestContext.Current.CancellationToken;
        var driver = new StubDriver { Upstream = new Uri("http://127.0.0.1:22577/") };
        var clock = new ManualClock();
        var changes = new UpstreamChanges();
        var resolver = new RuntimeDriverUpstreamResolver(
            [driver], Microsoft.Extensions.Options.Options.Create(new AgentProxyOptions()), clock, changes);

        Assert.Equal(new Uri("http://127.0.0.1:22577/"), await resolver.ResolveAsync(ModelId, ct));
        Assert.Equal(1, driver.Calls);

        // The driver replaces the instance: stop (the record goes), then a launch on the next free port.
        changes.Changed(ModelId);
        driver.Upstream = new Uri("http://127.0.0.1:22578/");
        changes.Changed(ModelId);

        Assert.Equal(new Uri("http://127.0.0.1:22578/"), await resolver.ResolveAsync(ModelId, ct));
        Assert.Equal(2, driver.Calls);

        // And it is cached again from there, so the steady state still costs one driver call per TTL.
        Assert.Equal(new Uri("http://127.0.0.1:22578/"), await resolver.ResolveAsync(ModelId, ct));
        Assert.Equal(2, driver.Calls);

        // A stop with no replacement is the same signal, and "not running" is the honest answer immediately.
        changes.Changed(ModelId);
        driver.Upstream = null;
        Assert.Null(await resolver.ResolveAsync(ModelId, ct));
        Assert.Equal(3, driver.Calls);

        // Another model's instance changing never touches this one's entry.
        driver.Upstream = new Uri("http://127.0.0.1:22579/");
        Assert.Equal(new Uri("http://127.0.0.1:22579/"), await resolver.ResolveAsync(ModelId, ct));
        changes.Changed("another-model");
        Assert.Equal(new Uri("http://127.0.0.1:22579/"), await resolver.ResolveAsync(ModelId, ct));
        Assert.Equal(4, driver.Calls);
    }

    // ---- Helpers ----

    private static StringContent ChatBody(bool stream) => new(
        $$"""{"model":"{{ModelId}}","stream":{{(stream ? "true" : "false")}},"messages":[{"role":"user","content":"hi"}]}""",
        Encoding.UTF8,
        "application/json");

    private static async Task<AgentError> ReadAgentErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadFromJsonAsync<AgentErrorResponse>(AgentJson.Options, ct);
        return Assert.IsType<AgentErrorResponse>(body).Error;
    }

    private static async Task<double> ReadMetricAsync(FakeModelServer model, string name, CancellationToken ct)
    {
        using var client = new HttpClient();
        string text = await client.GetStringAsync(new Uri(model.BaseUri, "metrics"), ct);
        string line = text.Split('\n').Single(l => l.StartsWith(name + " ", StringComparison.Ordinal));
        return double.Parse(line[(name.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException($"Timed out waiting until {what}.");
            }

            await Task.Delay(10, ct);
        }
    }

    private static async Task<Uri> StartAsync(WebApplication app, CancellationToken ct)
    {
        await app.StartAsync(ct);
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address.EndsWith('/') ? address : address + "/");
    }

    /// <summary>CA, agent certificate (SAN node <c>dgx</c>, 127.0.0.1) and master client certificate, as MN-004 issues them.</summary>
    private sealed class PkiMaterial : IDisposable
    {
        private readonly CertificateAuthority _ca = CertificateAuthority.Create();
        private readonly ECDsa _agentKey;
        private readonly IssuedCertificate _agentCert;
        private readonly IssuedCredential _master;

        public PkiMaterial()
        {
            Trust = TrustAnchorSet.Create([_ca.Certificate]);
            string csr = PkiTestSupport.NewCsr(out _agentKey);
            _agentCert = _ca.IssueAgentCertificate(csr, "dgx", ["127.0.0.1"]);
            AgentTls = TlsCredentials.FromPem(_agentCert.CertificatePem, _agentKey.ExportPkcs8PrivateKeyPem());
            _master = _ca.IssueMasterClientCertificate();
            MasterTls = _master.CreateTlsCertificate();
        }

        public TrustAnchorSet Trust { get; }

        public X509Certificate2 AgentTls { get; }

        public X509Certificate2 MasterTls { get; }

        public string AgentPin => _agentCert.SpkiSha256;

        public void Dispose()
        {
            AgentTls.Dispose();
            MasterTls.Dispose();
            _agentCert.Dispose();
            _agentKey.Dispose();
            _master.Dispose();
            Trust.Dispose();
            _ca.Dispose();
        }
    }

    /// <summary>The agent side: Kestrel with the MN-014 listener shape, the proxy route, and a master-shaped data client.</summary>
    private sealed class ProxyHarness : IAsyncDisposable
    {
        private ProxyHarness(WebApplication app, Uri baseUri, HttpClient client)
        {
            App = app;
            BaseUri = baseUri;
            Client = client;
        }

        public WebApplication App { get; }

        public Uri BaseUri { get; }

        public HttpClient Client { get; }

        public AgentProxy Proxy => App.Services.GetRequiredService<AgentProxy>();

        public static async Task<ProxyHarness> StartAsync(
            PkiMaterial pki, IProxyUpstreamResolver resolver, Action<AgentProxyOptions>? configure = null, CancellationToken ct = default)
        {
            var serverContext = PkiTls.CreateContext(pki.AgentTls);
            var masterValidators = new PeerValidatorSource(PeerValidator.ForMasterClient(pki.Trust));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.MaxRequestBodySize = Protocol.MaxProxyRequestBodyBytes + (16 * 1024 * 1024); // as MN-014
                k.Listen(IPAddress.Loopback, 0, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(new TlsHandshakeCallbackOptions
                    {
                        OnConnection = _ => ValueTask.FromResult(PkiTls.CreateServerOptions(serverContext, masterValidators)),
                    });
                });
            });
            builder.Services.AddSingleton(resolver);
            builder.Services.AddAgentProxy(configure);
            var app = builder.Build();
            app.MapAgentProxyEndpoints();
            Uri listening = await AgentProxyTests.StartAsync(app, ct);

            var handler = new SocketsHttpHandler
            {
                SslOptions = PkiTls.CreateClientOptions(
                    PkiTls.CreateContext(pki.MasterTls),
                    new PeerValidatorSource(PeerValidator.ForAgent("dgx", pki.Trust, [pki.AgentPin]))),
                UseProxy = false,
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                EnableMultipleHttp2Connections = true,
                MaxResponseDrainSize = 0,
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            return new ProxyHarness(app, new Uri($"https://127.0.0.1:{listening.Port}/"), client);
        }

        /// <summary>A data-channel request: HTTP/2 exact, as the master's data handler sends it (finding 14).</summary>
        public HttpRequestMessage Request(HttpMethod method, string modelId, string path, HttpContent? content = null) =>
            new(method, new Uri(BaseUri, $"agent/v1/proxy/{modelId}/{path}"))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = content,
            };

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    /// <summary>A fixed model → upstream map; records lookups and invalidations.</summary>
    private sealed class FakeResolver : IProxyUpstreamResolver
    {
        private int _resolves;

        public ConcurrentDictionary<string, Uri> Upstreams { get; } = new(StringComparer.Ordinal);

        public ConcurrentQueue<(string ModelId, Uri Upstream)> Invalidated { get; } = new();

        public Exception? Failure { get; init; }

        public int Resolves => Volatile.Read(ref _resolves);

        public static FakeResolver For(string modelId, Uri upstream)
        {
            var resolver = new FakeResolver();
            resolver.Upstreams[modelId] = upstream;
            return resolver;
        }

        public ValueTask<Uri?> ResolveAsync(string modelId, CancellationToken ct)
        {
            Interlocked.Increment(ref _resolves);
            return Failure is not null
                ? ValueTask.FromException<Uri?>(Failure)
                : ValueTask.FromResult(Upstreams.TryGetValue(modelId, out var uri) ? uri : null);
        }

        public void Invalidate(string modelId, Uri upstream) => Invalidated.Enqueue((modelId, upstream));
    }

    /// <summary>A model-side server that shows what the proxy did: echo (headers, query, body size), hang, flood.</summary>
    private sealed class RecordingUpstream : IAsyncDisposable
    {
        public const long FloodLimit = 512L * 1024 * 1024;

        private readonly WebApplication _app;
        private int _requests;
        private int _hangArrived;
        private int _hangAborted;
        private int _floodAborted;
        private long _floodBytes;
        private long _bodyBytesReceived;

        private RecordingUpstream(WebApplication app) => _app = app;

        public Uri BaseUri { get; private set; } = null!;

        public ConcurrentQueue<SeenRequest> Seen { get; } = new();

        public int Requests => Volatile.Read(ref _requests);

        public int HangArrived => Volatile.Read(ref _hangArrived);

        public int HangAborted => Volatile.Read(ref _hangAborted);

        public int FloodAborted => Volatile.Read(ref _floodAborted);

        public long FloodBytes => Interlocked.Read(ref _floodBytes);

        public long BodyBytesReceived => Interlocked.Read(ref _bodyBytesReceived);

        public static async Task<RecordingUpstream> StartAsync(CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.Limits.MaxRequestBodySize = null;
                k.Listen(IPAddress.Loopback, 0);
            });
            var app = builder.Build();
            var self = new RecordingUpstream(app);
            app.Use((context, next) =>
            {
                Interlocked.Increment(ref self._requests);
                return next(context);
            });
            app.Map("/v1/echo", self.EchoAsync);
            app.Map("/v1/hang", self.HangAsync);
            app.Map("/v1/flood", self.FloodAsync);
            self.BaseUri = await AgentProxyTests.StartAsync(app, ct);
            return self;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private async Task EchoAsync(HttpContext context)
        {
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                total += read;
                Interlocked.Add(ref _bodyBytesReceived, read);
            }

            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            Seen.Enqueue(new SeenRequest(context.Request.Method, context.Request.QueryString.Value ?? "", headers, total));
            context.Response.Headers["X-Upstream"] = "yes";
            context.Response.Headers[Protocol.AgentErrorHeader] = "forged"; // a model must not pass as the agent (D40)
            await context.Response.WriteAsJsonAsync(new { bytes = total }, context.RequestAborted);
        }

        private async Task HangAsync(HttpContext context)
        {
            Interlocked.Increment(ref _hangArrived);
            try
            {
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _hangAborted);
            }
        }

        private async Task FloodAsync(HttpContext context)
        {
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.ContentType = "application/octet-stream";
            var chunk = new byte[64 * 1024];
            try
            {
                while (Interlocked.Read(ref _floodBytes) < FloodLimit && !context.RequestAborted.IsCancellationRequested)
                {
                    await context.Response.Body.WriteAsync(chunk, context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                    Interlocked.Add(ref _floodBytes, chunk.Length);
                }

                // A closed connection does not always fail the writes: once Kestrel aborted the connection they complete
                // without sending anything, and RequestAborted is cancelled later on a thread-pool work item. Under load
                // the loop above can therefore "finish" on a dead connection (MN-047). Seeing the abort means seeing
                // RequestAborted, so wait for it rather than count a write failure.
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                Interlocked.Increment(ref _floodAborted);
            }
        }
    }

    private sealed record SeenRequest(string Method, string Query, Dictionary<string, string> Headers, long BodyBytes);

    /// <summary>Writes the first part, waits on a gate, then writes the second part.</summary>
    private sealed class GatedContent(int firstPart, int secondPart, Func<Task> gate, bool declareLength = true) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await WriteAsync(stream, firstPart, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            await gate();
            await WriteAsync(stream, secondPart, cancellationToken);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = (long)firstPart + secondPart;
            return declareLength;
        }

        private static async Task WriteAsync(Stream stream, int count, CancellationToken ct)
        {
            var chunk = new byte[16 * 1024];
            for (int written = 0; written < count; written += chunk.Length)
            {
                await stream.WriteAsync(chunk.AsMemory(0, Math.Min(chunk.Length, count - written)), ct);
            }
        }
    }

    /// <summary>Only <see cref="IRuntimeDriver.GetUpstreamAsync"/> is used by the resolver.</summary>
    private sealed class StubDriver : IRuntimeDriver
    {
        public Uri? Upstream { get; set; }

        public int Calls { get; private set; }

        public LaunchDelivery Delivery => LaunchDelivery.Container;

        public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Upstream);
        }

        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
