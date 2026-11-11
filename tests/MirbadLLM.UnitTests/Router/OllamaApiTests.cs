using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirbadLLM.Core;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Router.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Router;

/// <summary>
/// The Ollama-compatible routes through the router, against the suite's fake upstream: the request reaches the upstream
/// as its <c>/v1</c> form, the answer comes back in Ollama's shape, and the <c>/v1</c> rules (API key, model resolution,
/// not-ready, start-on-request, maintenance, statistics, cancellation) apply unchanged.
/// </summary>
public sealed class OllamaApiTests
{
    private const string ChatSse =
        "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"hm\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}\n\n" +
        "data: [DONE]\n\n";

    private const string ChatJson =
        """{"choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"Hello"}}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the fake upstream received.</summary>
    private sealed record Sent(string Host, string Path, string? ContentType, bool HasAuthorization, bool AsksForCompression, string Body)
    {
        public JsonElement Json => JsonDocument.Parse(Body).RootElement;
    }

    private sealed class Env : IAsyncDisposable
    {
        public FakeModelEndpointResolver Resolver { get; } = new();

        public FakeModelRuntime Runtime { get; } = new();

        public List<Sent> Requests { get; } = [];

        public Func<Sent, HttpResponseMessage> Respond { get; set; } = sent => Text(
            sent.Body.Contains("\"stream\":true", StringComparison.Ordinal) ? ChatSse : ChatJson,
            sent.Body.Contains("\"stream\":true", StringComparison.Ordinal) ? "text/event-stream" : "application/json");

        public WebApplication App { get; private set; } = null!;

        public HttpClient Client { get; private set; } = null!;

        public static async Task<Env> StartAsync(
            Action<MirbadLLMOptions>? options = null,
            Action<IServiceCollection>? services = null,
            FakeApiKeyStore? keys = null,
            Action<AuthOptions>? auth = null,
            Action<Env>? setup = null)
        {
            var env = new Env();
            env.Resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream-chat/"), ModelState.Ready));
            setup?.Invoke(env);
            var handler = new FakeUpstreamHandler
            {
                Handler = async (request, ct) =>
                {
                    var sent = new Sent(
                        request.RequestUri!.Host,
                        request.RequestUri.AbsolutePath,
                        request.Content?.Headers.ContentType?.MediaType,
                        request.Headers.Authorization is not null,
                        request.Headers.AcceptEncoding.Count > 0,
                        request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
                    lock (env.Requests)
                    {
                        env.Requests.Add(sent);
                    }

                    return env.Respond(sent);
                },
            };
            env.App = await RouterHostFactory.CreateAsync(
                env.Resolver, env.Runtime, options,
                s =>
                {
                    RouterProxyTests.ConfigureUpstream(s, handler);
                    services?.Invoke(s);
                },
                keys, auth);
            env.Client = env.App.GetTestClient();
            return env;
        }

        public Task<HttpResponseMessage> PostAsync(string path, string json) =>
            Client.PostAsync(new Uri(path, UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"), Ct);

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private static HttpResponseMessage Text(string body, string contentType, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    private static async Task<List<JsonElement>> LinesAsync(HttpResponseMessage response) =>
        [.. (await response.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line).RootElement)];

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string message)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        // Ollama's error shape: one string, no OpenAI error object.
        Assert.Equal(JsonValueKind.String, body.GetProperty("error").ValueKind);
        Assert.Contains(message, body.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // ---- Chat ----

    [Fact]
    public async Task Chat_StreamsNdjsonByDefault_ThroughTheV1ChatCompletionsPath()
    {
        await using var env = await Env.StartAsync();

        using var response = await env.PostAsync("/api/chat", """{"model":"chat-model","messages":[{"role":"user","content":"hi"}],"options":{"temperature":0.1,"num_ctx":4096}}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var lines = await LinesAsync(response);
        Assert.Equal(3, lines.Count);
        Assert.Equal("hm", lines[0].GetProperty("message").GetProperty("thinking").GetString());
        Assert.Equal("Hello", lines[1].GetProperty("message").GetProperty("content").GetString());
        Assert.True(lines[2].GetProperty("done").GetBoolean());
        Assert.Equal("stop", lines[2].GetProperty("done_reason").GetString());
        Assert.Equal(3, lines[2].GetProperty("prompt_eval_count").GetInt64());
        Assert.Equal(2, lines[2].GetProperty("eval_count").GetInt64());
        Assert.True(lines[2].GetProperty("total_duration").GetInt64() > 0);
        Assert.All(lines, line => Assert.Equal("chat-model", line.GetProperty("model").GetString()));

        var sent = Assert.Single(env.Requests);
        Assert.Equal("upstream-chat", sent.Host);
        Assert.Equal("/v1/chat/completions", sent.Path);
        Assert.Equal("chat-model", sent.Json.GetProperty("model").GetString());
        Assert.True(sent.Json.GetProperty("stream").GetBoolean());
        Assert.True(sent.Json.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(0.1, sent.Json.GetProperty("temperature").GetDouble());
        Assert.False(sent.Json.TryGetProperty("options", out _));
    }

    [Fact]
    public async Task Chat_StreamFalse_AnswersOneJsonObject()
    {
        await using var env = await Env.StartAsync();

        using var response = await env.PostAsync("/api/chat", """{"model":"chat-model","messages":[{"role":"user","content":"hi"}],"stream":false}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        string text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(Encoding.UTF8.GetByteCount(text), response.Content.Headers.ContentLength);
        var body = JsonDocument.Parse(text).RootElement;
        Assert.Equal("Hello", body.GetProperty("message").GetProperty("content").GetString());
        Assert.True(body.GetProperty("done").GetBoolean());
        Assert.Equal(1, body.GetProperty("eval_count").GetInt64());
        Assert.False(Assert.Single(env.Requests).Json.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task ARequestSentAsAForm_AsCurlDoes_ReachesTheUpstreamAsJson_WithoutTheClientsKey()
    {
        var keys = new FakeApiKeyStore();
        keys.AddActiveKey("ssk_inference", ApiKeyScope.Inference);
        await using var env = await Env.StartAsync(keys: keys);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent("""{"model":"chat-model","messages":[{"role":"user","content":"hi"}],"stream":false}""", Encoding.UTF8, "application/x-www-form-urlencoded"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "ssk_inference");
        request.Headers.AcceptEncoding.ParseAdd("gzip");

        using var response = await env.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(env.Requests);
        Assert.Equal("application/json", sent.ContentType);
        Assert.False(sent.HasAuthorization);
        Assert.False(sent.AsksForCompression); // a compressed answer could not be translated
    }

    [Fact]
    public async Task Chat_DeliversTheStreamAsItArrives()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.WebHost.UseTestServer();
        await using var upstream = upstreamBuilder.Build();
        upstream.Run(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"first\"}}]}\n\n");
            await context.Response.Body.FlushAsync();
            await gate.Task;
            await context.Response.WriteAsync("data: {\"choices\":[{\"delta\":{\"content\":\"second\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n");
        });
        await upstream.StartAsync(Ct);

        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("sse-1", "sse-model", new Uri("http://upstream/"), ModelState.Ready));
        await using var app = await RouterHostFactory.CreateAsync(
            resolver, new FakeModelRuntime(),
            configureServices: services => RouterProxyTests.ConfigureUpstream(services, upstream.GetTestServer().CreateHandler()));
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent("""{"model":"sse-model","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"),
        };

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));

        string? first = await reader.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains("first", first, StringComparison.Ordinal);

        gate.SetResult();
        string rest = await reader.ReadToEndAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Contains("second", rest, StringComparison.Ordinal);
        Assert.Contains("\"done\":true", rest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AClientDisconnect_CancelsTheUpstream()
    {
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeUpstreamHandler
        {
            Handler = async (_, upstreamCt) =>
            {
                using var registration = upstreamCt.Register(() => cancelled.TrySetResult());
                entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, upstreamCt);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        await using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();
        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = new StringContent("""{"model":"chat-model","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"),
        };

        var send = client.SendAsync(request, cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    // ---- Model names ----

    [Fact]
    public async Task NameWithLatest_MeansTheBareName_UnlessThatTagIsItselfServed()
    {
        await using var env = await Env.StartAsync(setup: e =>
            e.Resolver.Set(new ModelEndpoint("tagged-1", "tagged:latest", new Uri("http://upstream-tagged/"), ModelState.Ready)));

        using (var response = await env.PostAsync("/api/chat", """{"model":"chat-model:latest","messages":[{"role":"user","content":"hi"}],"stream":false}"""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // The answer names the model as the client did.
            Assert.Equal("chat-model:latest", (await JsonAsync(response)).GetProperty("model").GetString());
        }

        using (var response = await env.PostAsync("/api/chat", """{"model":"tagged:latest","messages":[{"role":"user","content":"hi"}],"stream":false}"""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal("upstream-chat", env.Requests[0].Host);
        Assert.Equal("chat-model", env.Requests[0].Json.GetProperty("model").GetString());
        Assert.Equal("upstream-tagged", env.Requests[1].Host);
        Assert.Equal("tagged:latest", env.Requests[1].Json.GetProperty("model").GetString());
    }

    [Fact]
    public async Task UnknownNameWithLatest_Is404_NamingWhatTheClientSent()
    {
        await using var env = await Env.StartAsync();

        using var response = await env.PostAsync("/api/chat", """{"model":"nobody:latest","messages":[{"role":"user","content":"hi"}]}""");

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "Model 'nobody:latest' is not registered.");
        Assert.Empty(env.Requests);
    }

    [Fact]
    public async Task TheNoThinkName_WorksWithLatest_AndSwitchesThinkingOff()
    {
        await using var env = await Env.StartAsync();

        using var response = await env.PostAsync("/api/chat", """{"model":"chat-model-nothink:latest","messages":[{"role":"user","content":"hi"}],"think":true,"stream":false}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(env.Requests).Json;
        Assert.Equal("chat-model", sent.GetProperty("model").GetString());
        Assert.False(sent.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    // ---- The /v1 rules ----

    [Fact]
    public async Task ARequestWithoutAModel_Is400_AsOnV1()
    {
        await using var env = await Env.StartAsync();

        using var noModel = await env.PostAsync("/api/chat", """{"messages":[{"role":"user","content":"hi"}]}""");
        await AssertErrorAsync(noModel, HttpStatusCode.BadRequest, "Request does not name a model.");

        using var notJson = await env.PostAsync("/api/generate", "model=chat-model");
        await AssertErrorAsync(notJson, HttpStatusCode.BadRequest, "Request does not name a model.");
        Assert.Empty(env.Requests);
    }

    [Fact]
    public async Task AModelThatIsNotReady_Is503WithRetryAfter()
    {
        await using var env = await Env.StartAsync(setup: e =>
            e.Resolver.Set(new ModelEndpoint("slow-1", "slow-model", new Uri("http://upstream-slow/"), ModelState.Starting)));

        using var response = await env.PostAsync("/api/chat", """{"model":"slow-model","messages":[{"role":"user","content":"hi"}]}""");

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "Model 'slow-model' is not ready (state: Starting).");
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
        Assert.Empty(env.Requests);
    }

    [Fact]
    public async Task StartOnRequest_StartsAStoppedModel_ThenAnswers()
    {
        var upstream = new Uri("http://upstream-cold/");
        await using var env = await Env.StartAsync(
            options: o =>
            {
                o.AutoStartOnRequest = true;
                o.StartupTimeout = TimeSpan.FromSeconds(10);
            },
            setup: e =>
            {
                e.Resolver.Set(new ModelEndpoint("cold-1", "cold-model", upstream, ModelState.Stopped));
                e.Runtime.SetStatus("cold-1", new ModelStatus("cold-1", ModelState.Stopped));
                e.Runtime.StartHandler = (id, _) =>
                {
                    e.Runtime.SetStatus(id, new ModelStatus(id, ModelState.Ready));
                    e.Resolver.Set(new ModelEndpoint(id, "cold-model", upstream, ModelState.Ready));
                    return new ModelStatus(id, ModelState.Ready);
                };
            });

        using var response = await env.PostAsync("/api/generate", """{"model":"cold-model:latest","prompt":"hi","stream":false}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, env.Runtime.StartCallCount);
        Assert.Equal("Hello", (await JsonAsync(response)).GetProperty("response").GetString());
        Assert.Equal("upstream-cold", Assert.Single(env.Requests).Host);
    }

    private sealed class FakeMaintenance : IMaintenanceMode
    {
        public MaintenanceState? Current { get; set; }
    }

    [Fact]
    public async Task AMaintenanceWindow_Answers503_BeforeTheApiKey_OnEveryRoute()
    {
        var maintenance = new FakeMaintenance
        {
            Current = new MaintenanceState("job-1", "benchmark", "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5)),
        };
        await using var env = await Env.StartAsync(o => o.ApiKey = "secret", s => s.AddSingleton<IMaintenanceMode>(maintenance));

        using var tags = await env.Client.GetAsync(new Uri("/api/tags", UriKind.Relative), Ct);
        await AssertErrorAsync(tags, HttpStatusCode.ServiceUnavailable, "mirbadllm is running a benchmark on qwen");
        Assert.True(int.Parse(tags.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) > 0);

        using var chat = await env.PostAsync("/api/chat", """{"model":"chat-model","messages":[{"role":"user","content":"hi"}]}""");
        await AssertErrorAsync(chat, HttpStatusCode.ServiceUnavailable, "API paused");
        Assert.Empty(env.Requests);
    }

    [Fact]
    public async Task UpstreamErrors_KeepTheirStatus_InOllamasShape()
    {
        await using var env = await Env.StartAsync();
        env.Respond = _ => Text(
            """{"error":{"message":"This model's maximum context length is 4096 tokens.","type":"BadRequestError","param":null,"code":400}}""",
            "application/json", HttpStatusCode.BadRequest);

        using var refused = await env.PostAsync("/api/chat", """{"model":"chat-model","messages":[{"role":"user","content":"hi"}]}""");
        await AssertErrorAsync(refused, HttpStatusCode.BadRequest, "This model's maximum context length is 4096 tokens.");

        env.Respond = _ => throw new HttpRequestException("connection refused");
        using var down = await env.PostAsync("/api/chat", """{"model":"chat-model","messages":[{"role":"user","content":"hi"}]}""");
        await AssertErrorAsync(down, HttpStatusCode.BadGateway, "Upstream request failed: connection refused");
    }

    [Fact]
    public async Task AnAnswerThatIsNotAnOpenAiResponse_Is502_NotASuccessInAnotherShape()
    {
        await using var env = await Env.StartAsync();
        env.Respond = _ => Text("""{"unexpected":true}""", "application/json");

        using var response = await env.PostAsync("/api/chat", """{"model":"chat-model","messages":[{"role":"user","content":"hi"}],"stream":false}""");

        await AssertErrorAsync(response, HttpStatusCode.BadGateway, "Upstream request failed");
    }

    [Fact]
    public async Task Requests_CountInTheRouterStatistics()
    {
        await using var env = await Env.StartAsync();
        using (await env.PostAsync("/api/chat", """{"model":"chat-model:latest","messages":[{"role":"user","content":"hi"}]}"""))
        {
        }

        env.Respond = _ => Text("boom", "text/plain", HttpStatusCode.InternalServerError);
        using (var failed = await env.PostAsync("/api/generate", """{"model":"chat-model","prompt":"hi"}"""))
        {
            await AssertErrorAsync(failed, HttpStatusCode.InternalServerError, "boom");
        }

        var stats = Assert.Single(env.App.Services.GetRequiredService<IRouterStats>().Snapshot(), s => s.ServedName == "chat-model");
        Assert.Equal(2, stats.TotalRequests);
        Assert.Equal(1, stats.FailedRequests);
        Assert.Equal(0, stats.InFlight);
    }

    // ---- API key (the /v1 rule and scope) ----

    [Fact]
    public async Task WithoutAnyKey_TheRoutesAreOpen_AsV1Is()
    {
        await using var env = await Env.StartAsync();

        using var tags = await env.Client.GetAsync(new Uri("/api/tags", UriKind.Relative), Ct);
        using var chat = await env.PostAsync("/api/chat", """{"model":"chat-model","messages":[{"role":"user","content":"hi"}]}""");

        Assert.Equal(HttpStatusCode.OK, tags.StatusCode);
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/tags")]
    [InlineData("GET", "/api/ps")]
    [InlineData("GET", "/api/version")]
    [InlineData("POST", "/api/show")]
    [InlineData("POST", "/api/chat")]
    [InlineData("POST", "/api/generate")]
    [InlineData("POST", "/api/embed")]
    [InlineData("POST", "/api/embeddings")]
    [InlineData("POST", "/api/pull")]
    [InlineData("DELETE", "/api/delete")]
    public async Task RequireApiKey_ClosesEveryRoute_EvenBeforeTheFirstKeyExists(string method, string path)
    {
        await using var env = await Env.StartAsync(auth: a => a.RequireApiKey = true);
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" ? null : new StringContent("""{"model":"chat-model"}""", Encoding.UTF8, "application/json"),
        };

        using var response = await env.Client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "Missing or invalid API key.");
        Assert.Empty(env.Requests);
    }

    [Fact]
    public async Task OnceAKeyExists_OnlyAnInferenceKeyOpensTheRoutes()
    {
        var keys = new FakeApiKeyStore();
        keys.AddActiveKey("ssk_inference", ApiKeyScope.Inference);
        keys.AddActiveKey("ssk_metrics", ApiKeyScope.Metrics);
        keys.AddRevokedKey("ssk_revoked", ApiKeyScope.Inference);
        await using var env = await Env.StartAsync(keys: keys);

        async Task<HttpStatusCode> ChatAsync(string? key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
            {
                Content = new StringContent("""{"model":"chat-model","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"),
            };
            if (key is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            using var response = await env.Client.SendAsync(request, Ct);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await ChatAsync(null));
        Assert.Equal(HttpStatusCode.Unauthorized, await ChatAsync("ssk_metrics"));
        Assert.Equal(HttpStatusCode.Unauthorized, await ChatAsync("ssk_revoked"));
        Assert.Equal(HttpStatusCode.Unauthorized, await ChatAsync("ssk_unknown"));
        Assert.Empty(env.Requests);
        Assert.Equal(HttpStatusCode.OK, await ChatAsync("ssk_inference"));
    }

    [Fact]
    public async Task TheLegacyStaticKey_GatesTheRoutesToo()
    {
        await using var env = await Env.StartAsync(o => o.ApiKey = "secret");

        using var denied = await env.Client.GetAsync(new Uri("/api/tags", UriKind.Relative), Ct);
        await AssertErrorAsync(denied, HttpStatusCode.Unauthorized, "Missing or invalid API key.");

        env.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        using var allowed = await env.Client.GetAsync(new Uri("/api/tags", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    // ---- Generate, load and embeddings ----

    [Fact]
    public async Task Generate_IsAChatCompletion_AndRawIsACompletion()
    {
        await using var env = await Env.StartAsync();

        using (var response = await env.PostAsync("/api/generate", """{"model":"chat-model","prompt":"Why?","system":"Be brief.","stream":false}"""))
        {
            var body = await JsonAsync(response);
            Assert.Equal("Hello", body.GetProperty("response").GetString());
            Assert.True(body.GetProperty("done").GetBoolean());
            Assert.False(body.TryGetProperty("message", out _));
        }

        env.Respond = _ => Text("""{"choices":[{"index":0,"text":" blue","finish_reason":"length"}]}""", "application/json");
        using (var response = await env.PostAsync("/api/generate", """{"model":"chat-model","prompt":"The sky is","raw":true,"stream":false}"""))
        {
            Assert.Equal(" blue", (await JsonAsync(response)).GetProperty("response").GetString());
        }

        Assert.Equal("/v1/chat/completions", env.Requests[0].Path);
        Assert.Equal("system", env.Requests[0].Json.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("Why?", env.Requests[0].Json.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal("/v1/completions", env.Requests[1].Path);
        Assert.Equal("The sky is", env.Requests[1].Json.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task ALoadRequest_AnswersLoad_ForAServingModel_WithoutCallingIt()
    {
        await using var env = await Env.StartAsync();

        using var generate = await env.PostAsync("/api/generate", """{"model":"chat-model:latest"}""");
        Assert.Equal(HttpStatusCode.OK, generate.StatusCode);
        Assert.Equal("application/json", generate.Content.Headers.ContentType?.MediaType);
        var loaded = await JsonAsync(generate);
        Assert.Equal("chat-model:latest", loaded.GetProperty("model").GetString());
        Assert.Equal("", loaded.GetProperty("response").GetString());
        Assert.True(loaded.GetProperty("done").GetBoolean());
        Assert.Equal("load", loaded.GetProperty("done_reason").GetString());

        using var chat = await env.PostAsync("/api/chat", """{"model":"chat-model","messages":[]}""");
        Assert.Equal("load", (await JsonAsync(chat)).GetProperty("done_reason").GetString());
        Assert.Empty(env.Requests);
    }

    [Fact]
    public async Task ALoadRequest_DoesNotClaimALoad_ForAModelThatIsNotServing()
    {
        await using var env = await Env.StartAsync(
            options: o => o.AutoStartOnRequest = true,
            setup: e => e.Resolver.Set(new ModelEndpoint("cold-1", "cold-model", new Uri("http://upstream-cold/"), ModelState.Stopped)));

        using var stopped = await env.PostAsync("/api/generate", """{"model":"cold-model"}""");
        await AssertErrorAsync(stopped, HttpStatusCode.ServiceUnavailable, "Model 'cold-model' is not ready (state: Stopped).");
        Assert.Equal(0, env.Runtime.StartCallCount);

        using var unknown = await env.PostAsync("/api/chat", """{"model":"nobody","messages":[]}""");
        await AssertErrorAsync(unknown, HttpStatusCode.NotFound, "Model 'nobody' is not registered.");

        using var unload = await env.PostAsync("/api/generate", """{"model":"chat-model","keep_alive":0}""");
        await AssertErrorAsync(unload, HttpStatusCode.BadRequest, "does not unload models");
        Assert.Empty(env.Requests);
    }

    [Fact]
    public async Task Generate_WithSuffix_IsRefused_NotAnsweredWithoutIt()
    {
        await using var env = await Env.StartAsync();

        using var response = await env.PostAsync("/api/generate", """{"model":"chat-model","prompt":"def add(a, b):","suffix":"    return c"}""");

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "suffix");
        Assert.Empty(env.Requests);
    }

    [Fact]
    public async Task Embed_AndTheOlderRoute_GoToV1Embeddings()
    {
        await using var env = await Env.StartAsync(setup: e =>
            e.Resolver.Set(new ModelEndpoint("embed-1", "embed-model", new Uri("http://upstream-embed/"), ModelState.Ready)));
        env.Respond = _ => Text(
            """{"object":"list","data":[{"index":0,"embedding":[0.25,-0.5]},{"index":1,"embedding":[0.125,0.75]}],"usage":{"prompt_tokens":6,"total_tokens":6}}""",
            "application/json");

        using (var response = await env.PostAsync("/api/embed", """{"model":"embed-model:latest","input":["a","b"]}"""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await JsonAsync(response);
            Assert.Equal("embed-model:latest", body.GetProperty("model").GetString());
            Assert.Equal(2, body.GetProperty("embeddings").GetArrayLength());
            Assert.Equal(-0.5, body.GetProperty("embeddings")[0][1].GetDouble());
            Assert.Equal(6, body.GetProperty("prompt_eval_count").GetInt64());
        }

        using (var response = await env.PostAsync("/api/embeddings", """{"model":"embed-model","prompt":"a"}"""))
        {
            Assert.Equal("""{"embedding":[0.25,-0.5]}""", await response.Content.ReadAsStringAsync(Ct));
        }

        Assert.All(env.Requests, sent => Assert.Equal("/v1/embeddings", sent.Path));
        Assert.Equal("embed-model", env.Requests[0].Json.GetProperty("model").GetString());
        Assert.Equal(2, env.Requests[0].Json.GetProperty("input").GetArrayLength());
        Assert.Equal("a", env.Requests[1].Json.GetProperty("input").GetString());
    }

    // ---- The model list ----

    private static FakeModelRegistry Registry() => new FakeModelRegistry()
        .Add(new ModelDefinition
        {
            Id = "chat-1", ServedName = "chat-model", ModelPath = "q", MaxModelLen = 131_072, ToolCallParser = "qwen3_coder",
            Quantization = "fp8", Tags = [ModelTags.Chat, ModelTags.Vision, ModelTags.Reasoning],
        })
        .Add(new ModelDefinition
        {
            Id = "embed-1", ServedName = "embed-model", ModelPath = "e", Runtime = RuntimeKind.LlamaCpp, ToolCallParser = null,
            LlamaCpp = new LlamaCppSettings { ModelFile = "e.gguf", ContextSize = 8192, Embedding = true },
        });

    private static Task<Env> ListEnvAsync() => Env.StartAsync(
        services: s => s.AddSingleton<IModelRegistry>(Registry()),
        setup: e =>
        {
            e.Resolver.Set(new ModelEndpoint("embed-1", "embed-model", new Uri("http://upstream-embed/"), ModelState.Ready));
            e.Resolver.Set(new ModelEndpoint("stopped-1", "stopped-model", new Uri("http://upstream-stopped/"), ModelState.Stopped));
        });

    [Fact]
    public async Task Tags_ListsTheNamesV1ModelsLists_WithOnlyTheDetailsTheRegistryHas()
    {
        await using var env = await ListEnvAsync();

        using var v1 = await env.Client.GetAsync(new Uri("/v1/models", UriKind.Relative), Ct);
        using var response = await env.Client.GetAsync(new Uri("/api/tags", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var models = (await JsonAsync(response)).GetProperty("models");
        var names = models.EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToArray();
        Assert.Equal((await JsonAsync(v1)).GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()), names);
        Assert.Equal(["chat-model", "chat-model-nothink", "embed-model"], names.Order(StringComparer.Ordinal));
        Assert.All(models.EnumerateArray(), m => Assert.Equal(m.GetProperty("name").GetString(), m.GetProperty("model").GetString()));

        var chat = models.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "chat-model").GetProperty("details");
        Assert.Equal("fp8", chat.GetProperty("quantization_level").GetString());
        Assert.Equal("", chat.GetProperty("format").GetString());
        var embed = models.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "embed-model");
        Assert.Equal("gguf", embed.GetProperty("details").GetProperty("format").GetString());
        Assert.Equal("", embed.GetProperty("details").GetProperty("quantization_level").GetString());
        // Nothing the registry does not hold is filled in.
        Assert.Equal("", embed.GetProperty("details").GetProperty("family").GetString());
        Assert.Equal("", embed.GetProperty("details").GetProperty("parameter_size").GetString());
        Assert.Equal("", embed.GetProperty("details").GetProperty("parent_model").GetString());
        Assert.Equal(JsonValueKind.Null, embed.GetProperty("details").GetProperty("families").ValueKind);
        Assert.Equal(0, embed.GetProperty("size").GetInt64());
        Assert.Equal("", embed.GetProperty("digest").GetString());
        Assert.Equal("0001-01-01T00:00:00Z", embed.GetProperty("modified_at").GetString());
    }

    [Fact]
    public async Task Ps_ListsTheServingModels_WithTheirContextLength()
    {
        await using var env = await ListEnvAsync();

        using var response = await env.Client.GetAsync(new Uri("/api/ps", UriKind.Relative), Ct);

        var models = (await JsonAsync(response)).GetProperty("models");
        Assert.Equal(["chat-model", "embed-model"], models.EnumerateArray().Select(m => m.GetProperty("name").GetString()).Order(StringComparer.Ordinal));
        var embed = models.EnumerateArray().Single(m => m.GetProperty("name").GetString() == "embed-model");
        Assert.Equal(8192, embed.GetProperty("context_length").GetInt32());
        Assert.Equal(0, embed.GetProperty("size_vram").GetInt64());
        Assert.Equal("0001-01-01T00:00:00Z", embed.GetProperty("expires_at").GetString());
    }

    [Fact]
    public async Task Show_DescribesAModel_FromItsDefinition()
    {
        await using var env = await ListEnvAsync();

        using (var response = await env.PostAsync("/api/show", """{"model":"chat-model:latest"}"""))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await JsonAsync(response);
            Assert.Equal(["completion", "tools", "vision", "thinking"], body.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
            Assert.Equal("fp8", body.GetProperty("details").GetProperty("quantization_level").GetString());
            Assert.Equal(JsonValueKind.Object, body.GetProperty("model_info").ValueKind);
            Assert.False(body.TryGetProperty("modelfile", out _));
        }

        using (var response = await env.PostAsync("/api/show", """{"name":"chat-model-nothink"}"""))
        {
            Assert.Equal(["completion", "tools", "vision"], (await JsonAsync(response)).GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        }

        using (var response = await env.PostAsync("/api/show", """{"model":"embed-model"}"""))
        {
            Assert.Equal(["embedding"], (await JsonAsync(response)).GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        }

        using var unknown = await env.PostAsync("/api/show", """{"model":"nobody"}""");
        await AssertErrorAsync(unknown, HttpStatusCode.NotFound, "Model 'nobody' is not registered.");
        using var empty = await env.PostAsync("/api/show", "");
        await AssertErrorAsync(empty, HttpStatusCode.BadRequest, "Request does not name a model.");
    }

    [Fact]
    public async Task Version_IsThisBuildsVersion()
    {
        await using var env = await Env.StartAsync();

        using var response = await env.Client.GetAsync(new Uri("/api/version", UriKind.Relative), Ct);

        string? version = (await JsonAsync(response)).GetProperty("version").GetString();
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
        Assert.DoesNotContain("+", version, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST", "/api/pull")]
    [InlineData("POST", "/api/push")]
    [InlineData("POST", "/api/create")]
    [InlineData("POST", "/api/copy")]
    [InlineData("DELETE", "/api/delete")]
    public async Task ModelManagement_IsNotOffered_AndSaysWhereModelsAreManaged(string method, string path)
    {
        await using var env = await Env.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("""{"model":"llama3.2"}""", Encoding.UTF8, "application/json"),
        };

        using var response = await env.Client.SendAsync(request, Ct);

        await AssertErrorAsync(response, HttpStatusCode.NotImplemented, "Models page");
        Assert.Contains(path, (await JsonAsync(response)).GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The same routes on Kestrel over a loopback socket, where the response really starts on its first byte: a stream is
    /// chunked NDJSON, and everything that is one object carries its length.
    /// </summary>
    [Fact]
    public async Task OverARealSocket_StreamsAreChunkedNdjson_AndSingleAnswersCarryTheirLength()
    {
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream-chat/"), ModelState.Ready));
        var handler = new FakeUpstreamHandler
        {
            Handler = async (request, ct) =>
            {
                bool stream = (await request.Content!.ReadAsStringAsync(ct)).Contains("\"stream\":true", StringComparison.Ordinal);
                return Text(stream ? ChatSse : ChatJson, stream ? "text/event-stream" : "application/json");
            },
        };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services.AddMirbadLLMRouter();
        builder.Services.AddSingleton<IModelEndpointResolver>(resolver);
        builder.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        builder.Services.AddSingleton<IApiKeyStore>(new FakeApiKeyStore());
        builder.Services.Configure<MirbadLLMOptions>(o => o.ApiKey = "secret");
        RouterProxyTests.ConfigureUpstream(builder.Services, handler);
        await using var app = builder.Build();
        app.MapMirbadLLMRouter();
        await app.StartAsync(Ct);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using (var denied = await client.GetAsync(new Uri("/api/tags", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.Equal("""{"error":"Missing or invalid API key."}""", await denied.Content.ReadAsStringAsync(Ct));
            Assert.Equal(39, denied.Content.Headers.ContentLength); // the whole object, not a chunked body
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        using (var tags = await client.GetAsync(new Uri("/api/tags", UriKind.Relative), Ct))
        {
            Assert.Equal("chat-model", (await JsonAsync(tags)).GetProperty("models")[0].GetProperty("name").GetString());
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = Body("""{"model":"chat-model","messages":[{"role":"user","content":"hi"}]}""") })
        using (var streamed = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
            Assert.Equal("application/x-ndjson", streamed.Content.Headers.ContentType?.MediaType);
            Assert.True(streamed.Headers.TransferEncodingChunked);
            Assert.Null(streamed.Content.Headers.ContentLength);
            var lines = await LinesAsync(streamed);
            Assert.Equal(3, lines.Count);
            Assert.True(lines[2].GetProperty("done").GetBoolean());
        }

        using (var single = await client.PostAsync(new Uri("/api/chat", UriKind.Relative), Body("""{"model":"chat-model","messages":[{"role":"user","content":"hi"}],"stream":false}"""), Ct))
        {
            string text = await single.Content.ReadAsStringAsync(Ct);
            Assert.Equal("application/json", single.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Encoding.UTF8.GetByteCount(text), single.Content.Headers.ContentLength);
            Assert.Equal("Hello", JsonDocument.Parse(text).RootElement.GetProperty("message").GetProperty("content").GetString());
        }

        using (var load = await client.PostAsync(new Uri("/api/generate", UriKind.Relative), Body("""{"model":"chat-model"}"""), Ct))
        {
            Assert.Equal("load", (await JsonAsync(load)).GetProperty("done_reason").GetString());
        }

        using (var missing = await client.PostAsync(new Uri("/api/chat", UriKind.Relative), Body("""{"model":"nobody","messages":[{"role":"user","content":"hi"}]}"""), Ct))
        {
            await AssertErrorAsync(missing, HttpStatusCode.NotFound, "Model 'nobody' is not registered.");
        }

        static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");
    }

    [Fact]
    public async Task V1_IsUntouched_ByTheTranslation()
    {
        await using var env = await Env.StartAsync();

        using var response = await env.Client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model", stream = true }, Ct);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(ChatSse, await response.Content.ReadAsStringAsync(Ct));
        using var missing = await env.Client.PostAsJsonAsync("/v1/chat/completions", new { model = "nobody" }, Ct);
        Assert.Equal(JsonValueKind.Object, (await JsonAsync(missing)).GetProperty("error").ValueKind);
    }
}
