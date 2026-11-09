using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Router.Fakes;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using System.Text;

namespace MirbadLLM.UnitTests.Router;

public sealed class RouterProxyTests
{
    [Fact]
    public async Task RoutesByModel_ToTheRightUpstream_WithPathAndQuery()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream-chat/"), ModelState.Ready));
        resolver.Set(new ModelEndpoint("embed-1", "embed-model", new Uri("http://upstream-embed/"), ModelState.Ready));

        var handler = new FakeUpstreamHandler
        {
            Handler = (request, _) =>
            {
                var body = new
                {
                    host = request.RequestUri!.Host,
                    path = request.RequestUri!.AbsolutePath,
                    query = request.RequestUri!.Query,
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(body),
                });
            },
        };

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), configureServices: services => ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions?stream=true", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("upstream-chat", document.RootElement.GetProperty("host").GetString());
        Assert.Equal("/v1/chat/completions", document.RootElement.GetProperty("path").GetString());
        Assert.Equal("?stream=true", document.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task UnknownModel_Returns404WithOpenAiErrorShape()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime());
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "does-not-exist" }, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("model_not_found", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RequestNamingNoModel_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime());
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/v1/models-that-are-not-the-list-endpoint", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("invalid_request_error", document.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task HopByHopHeadersStripped_AndAuthorizationNotForwarded()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok"),
                Headers = { { "Connection", "close" }, { "X-Upstream", "yes" } },
            }),
        };

        using var app = await RouterHostFactory.CreateAsync(
            resolver,
            new FakeModelRuntime(),
            configureOptions: o => o.ApiKey = "secret",
            configureServices: services => ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"chat-model"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret");
        request.Headers.Add("Connection", "keep-alive");
        request.Headers.Add("X-Test", "1");

        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Connection"));
        Assert.True(response.Headers.Contains("X-Upstream"));

        var forwarded = handler.LastRequest!;
        Assert.False(forwarded.Headers.Contains("Authorization"));
        Assert.False(forwarded.Headers.Contains("Connection"));
        Assert.True(forwarded.Headers.Contains("X-Test"));
    }

    [Fact]
    public async Task BodyOverLimit_Returns413()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime());
        using var client = app.GetTestClient();

        var oversized = new byte[(64 * 1024 * 1024) + 1];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new ByteArrayContent(oversized),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        using var response = await client.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("request_too_large", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UpstreamDown_Returns502()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => throw new HttpRequestException("Connection refused"),
        };

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), configureServices: services => ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("api_error", document.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ANoThinkName_ReachesTheSameModelWithThinkingOff_AndARealModelOfThatNameWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("q", "qwen", new Uri("http://upstream-q/"), ModelState.Ready));
        resolver.Set(new ModelEndpoint("r", "real-nothink", new Uri("http://upstream-real/"), ModelState.Ready));
        resolver.Set(new ModelEndpoint("b", "real", new Uri("http://upstream-base/"), ModelState.Ready));
        var sent = new List<(string Host, string Body)>();
        var handler = new FakeUpstreamHandler
        {
            Handler = async (request, handlerCt) =>
            {
                sent.Add((request.RequestUri!.Host, await request.Content!.ReadAsStringAsync(handlerCt)));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { }) };
            },
        };
        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), configureServices: services => ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using (await client.PostAsJsonAsync("/v1/chat/completions", new { model = "qwen-nothink", messages = Array.Empty<object>(), chat_template_kwargs = new { other = 1 } }, ct))
        {
        }

        using (await client.PostAsJsonAsync("/v1/chat/completions", new { model = "real-nothink" }, ct))
        {
        }

        Assert.Equal("upstream-q", sent[0].Host);
        using (var body = JsonDocument.Parse(sent[0].Body))
        {
            Assert.Equal("qwen", body.RootElement.GetProperty("model").GetString());
            var kwargs = body.RootElement.GetProperty("chat_template_kwargs");
            Assert.False(kwargs.GetProperty("enable_thinking").GetBoolean());
            Assert.Equal(1, kwargs.GetProperty("other").GetInt32());
        }

        Assert.Equal("upstream-real", sent[1].Host);
        Assert.Contains("\"real-nothink\"", sent[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStreamedAnswer_LosesSglangsNullReasoningField_AndOtherBodiesPassAsTheyAre()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("q", "qwen", new Uri("http://upstream-q/"), ModelState.Ready));
        const string Sse = "data: {\"choices\":[{\"delta\":{\"reasoning_content\":null,\"content\":\"Hi\"}}]}\n\ndata: [DONE]\n\n";
        bool stream = true;
        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = stream
                    ? new StringContent(Sse, Encoding.UTF8, "text/event-stream")
                    : new StringContent("{\"reasoning_content\":null}", Encoding.UTF8, "application/json"),
            }),
        };
        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), configureServices: services => ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using (var streamed = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "qwen", stream = true }, ct))
        {
            Assert.Equal("data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: [DONE]\n\n", await streamed.Content.ReadAsStringAsync(ct));
        }

        stream = false;
        using var plain = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "qwen" }, ct);
        Assert.Equal("{\"reasoning_content\":null}", await plain.Content.ReadAsStringAsync(ct));
    }

    internal static void ConfigureUpstream(IServiceCollection services, HttpMessageHandler handler)
        => services.AddHttpClient(RouterExtensions.UpstreamHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
}
