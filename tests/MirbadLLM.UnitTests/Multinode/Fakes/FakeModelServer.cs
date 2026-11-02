using Microsoft.AspNetCore.Hosting;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Orchestration.Fakes;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// In-process vLLM-shaped HTTP server for testing. Binds to 127.0.0.1:0 and serves OpenAI-compatible
/// endpoints: /v1/chat/completions (streaming and non-streaming), /v1/embeddings, /v1/models, /tokenize,
/// /health, and /metrics (vLLM Prometheus format).
/// </summary>
internal sealed class FakeModelServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ServerState _state;

    private FakeModelServer(WebApplication app, ServerState state)
    {
        _app = app;
        _state = state;
    }

    /// <summary>Gets the base URI of the running server, e.g., <c>http://127.0.0.1:12345/</c>.</summary>
    public Uri BaseUri { get; private set; } = null!;

    /// <summary>
    /// Creates and starts a fake model server on a random loopback port. The server serves vLLM-shaped
    /// endpoints with configurable behavior: token rate, time to first token, silent gaps, reset mid-stream,
    /// failure injection, and not-ready periods.
    /// </summary>
    public static async Task<FakeModelServer> CreateAsync(TimeProvider? timeProvider = null, CancellationToken ct = default)
    {
        var state = new ServerState(timeProvider ?? TimeProvider.System);
        var builder = WebApplication.CreateBuilder();

        // Loopback on an ephemeral port, always: never the environment's ASPNETCORE_URLS/HTTP_PORTS (the CI SDK image sets
        // ASPNETCORE_HTTP_PORTS=8080, which binds "*:8080" — unusable as a client target and shared between parallel tests).
        builder.WebHost.UseUrls();
        builder.WebHost.ConfigureKestrel(o => o.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Services.AddSingleton(state);

        var app = builder.Build();

        // Map all endpoints
        app.MapPost("/v1/chat/completions", HandleChatCompletions);
        app.MapPost("/v1/embeddings", HandleEmbeddings);
        app.MapGet("/v1/models", HandleListModels);
        app.MapPost("/tokenize", HandleTokenize);
        app.MapGet("/health", HandleHealth);
        app.MapGet("/metrics", HandleMetrics);

        await app.StartAsync(ct);

        // Extract the actual listening URL
        var addresses = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses ?? [];
        var url = addresses.FirstOrDefault() ?? throw new InvalidOperationException("The fake model server did not report a listening address.");
        var baseUri = new Uri(url.EndsWith('/') ? url : url + '/');

        var server = new FakeModelServer(app, state);
        server.BaseUri = baseUri;
        return server;
    }

    /// <summary>Sets the time to wait before returning a ready response. While in this period, returns 503.</summary>
    public void SetNotReadyPeriod(TimeSpan duration) => _state.NotReadyUntil = _state.TimeProvider.GetUtcNow().Add(duration);

    /// <summary>Sets the per-token generation rate (milliseconds per token). Default is 10ms.</summary>
    public void SetTokenRateMs(int ms) => _state.TokenRateMs = ms;

    /// <summary>Sets time to first token (milliseconds). Default is 50ms.</summary>
    public void SetTimeToFirstTokenMs(int ms) => _state.TimeToFirstTokenMs = ms;

    /// <summary>Sets silent gap (milliseconds) before starting token generation. Default is 0ms.</summary>
    public void SetSilentGapMs(int ms) => _state.SilentGapMs = ms;

    /// <summary>Configures the server to reset mid-stream (stop generating after N tokens). Default is infinite.</summary>
    public void SetResetAfterTokenCount(int count) => SetResetAfterTokenCount(count, resetWhen: null);

    /// <summary>
    /// As <see cref="SetResetAfterTokenCount(int)"/>, but after the Nth token the stream waits for
    /// <paramref name="resetWhen"/> before it aborts, so a test can first read those tokens at the client: an abort right
    /// after a write can drop bytes the transport has not sent yet (the client then sees no response at all).
    /// </summary>
    public void SetResetAfterTokenCount(int count, Task? resetWhen)
    {
        _state.ResetWhen = resetWhen;
        _state.ResetAfterTokenCount = count;
    }

    /// <summary>Configures the server to fail with a specific status code. Default is 200 OK.</summary>
    public void SetFailureStatus(int statusCode) => _state.FailureStatusCode = statusCode;

    /// <summary>
    /// MN-043: answers POSTs to <paramref name="path"/> (<c>/v1/chat/completions</c> or <c>/v1/embeddings</c>) with exactly
    /// this content type and body, e.g. the upstream answer a <c>/v1</c> golden recording was made with.
    /// </summary>
    public void SetCannedResponse(string path, string contentType, string body) => _state.Canned[path] = (contentType, body);

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // ---- Endpoint handlers ----

    private static Task HandleChatCompletions(HttpContext context, ServerState state)
    {
        return state.Canned.TryGetValue("/v1/chat/completions", out var canned)
            ? WriteCanned(context, canned)
            : state.IsNotReady()
            ? ReturnServiceUnavailable(context, "Server is warming up")
            : state.FailureStatusCode > 0
            ? ReturnFailure(context, state.FailureStatusCode)
            : HandleChatCompletionsOk(context, state);
    }

    private static async Task HandleChatCompletionsOk(HttpContext context, ServerState state)
    {
        using var sr = new StreamReader(context.Request.Body);
        var bodyText = await sr.ReadToEndAsync(context.RequestAborted);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(bodyText);
        }
        catch
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = new { message = "Invalid JSON", type = "invalid_request_error" } },
                options: JsonOptions, cancellationToken: context.RequestAborted);
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var stream = root.TryGetProperty("stream", out var streamProp) && streamProp.ValueKind == JsonValueKind.True;

            if (stream)
            {
                await StreamChatCompletions(context, state);
            }
            else
            {
                await NonStreamChatCompletions(context, state);
            }
        }
    }

    /// <summary>
    /// vLLM-shaped SSE (MN-090 fixes): <c>chat.completion.chunk</c> objects, a role chunk, one chunk per token with
    /// <c>finish_reason: null</c>, a final chunk with <c>finish_reason: "stop"</c> and usage, then <c>data: [DONE]</c>. Waits
    /// go through <see cref="ServerState.TimeProvider"/>: silent gap + time to first token once, then the token rate per
    /// token. "Reset mid-stream" aborts the connection after N tokens (no final chunk, no <c>[DONE]</c>), like a crashed
    /// model.
    /// </summary>
    private static async Task StreamChatCompletions(HttpContext context, ServerState state)
    {
        context.Response.ContentType = "text/event-stream";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var started = state.TimeProvider.GetUtcNow();
        const string completionId = "chatcmpl-fake-123";
        const int totalTokens = 20; // Generate a fixed number of tokens
        var ct = context.RequestAborted;

        await SendSseEvent(context, new
        {
            id = completionId,
            @object = "chat.completion.chunk",
            created = started.ToUnixTimeSeconds(),
            model = "fake-model",
            choices = new[] { new { index = 0, delta = new { role = "assistant", content = "" }, finish_reason = (string?)null } },
        });

        await Wait(state, state.SilentGapMs + state.TimeToFirstTokenMs, ct);
        for (int tokenCount = 0; tokenCount < totalTokens; tokenCount++)
        {
            if (tokenCount > 0)
            {
                await Wait(state, state.TokenRateMs, ct);
            }

            await SendSseEvent(context, new
            {
                id = completionId,
                @object = "chat.completion.chunk",
                created = state.TimeProvider.GetUtcNow().ToUnixTimeSeconds(),
                model = "fake-model",
                choices = new[] { new { index = 0, delta = new { content = $"token_{tokenCount}" }, finish_reason = (string?)null } },
            });
            state.IncrementMetric("vllm:generation_tokens_total", 1);

            if (state.ResetAfterTokenCount > 0 && tokenCount + 1 >= state.ResetAfterTokenCount)
            {
                if (state.ResetWhen is { } resetWhen)
                {
                    await resetWhen.WaitAsync(ct);
                }

                context.Abort();
                return;
            }
        }

        await SendSseEvent(context, new
        {
            id = completionId,
            @object = "chat.completion.chunk",
            created = state.TimeProvider.GetUtcNow().ToUnixTimeSeconds(),
            model = "fake-model",
            choices = new[] { new { index = 0, delta = new { }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 10, completion_tokens = totalTokens, total_tokens = 10 + totalTokens },
        });

        await context.Response.WriteAsync("data: [DONE]\n\n", ct);
        await context.Response.Body.FlushAsync(ct);

        state.IncrementMetric("vllm:request_success_total", 1);
    }

    private static async Task NonStreamChatCompletions(HttpContext context, ServerState state)
    {
        var now = state.TimeProvider.GetUtcNow();
        const string completionId = "chatcmpl-fake-456";
        const int completionTokens = 20;

        await Wait(state, state.SilentGapMs + state.TimeToFirstTokenMs + (completionTokens * state.TokenRateMs), context.RequestAborted);

        var response = new
        {
            id = completionId,
            @object = "chat.completion",
            created = now.ToUnixTimeSeconds(),
            model = "fake-model",
            choices = new[]
            {
                new { index = 0, message = new { role = "assistant", content = "The quick brown fox jumps over the lazy dog." }, finish_reason = "stop" }
            },
            usage = new { prompt_tokens = 10, completion_tokens = completionTokens, total_tokens = 30 },
        };

        await context.Response.WriteAsJsonAsync(response, options: JsonOptions, cancellationToken: context.RequestAborted);

        state.IncrementMetric("vllm:generation_tokens_total", completionTokens);
        state.IncrementMetric("vllm:request_success_total", 1);
    }

    private static async Task WriteCanned(HttpContext context, (string ContentType, string Body) canned)
    {
        context.Response.ContentType = canned.ContentType;
        await context.Response.WriteAsync(canned.Body, context.RequestAborted);
    }

    /// <summary>A wait on the server's <see cref="TimeProvider"/> (a fake clock with timers makes it deterministic).</summary>
    private static Task Wait(ServerState state, int milliseconds, CancellationToken ct) =>
        milliseconds <= 0 ? Task.CompletedTask : Task.Delay(TimeSpan.FromMilliseconds(milliseconds), state.TimeProvider, ct);

    private static Task HandleEmbeddings(HttpContext context, ServerState state)
    {
        return state.Canned.TryGetValue("/v1/embeddings", out var canned)
            ? WriteCanned(context, canned)
            : state.IsNotReady()
            ? ReturnServiceUnavailable(context, "Server is warming up")
            : state.FailureStatusCode > 0
            ? ReturnFailure(context, state.FailureStatusCode)
            : HandleEmbeddingsOk(context, state);
    }

    private static async Task HandleEmbeddingsOk(HttpContext context, ServerState state)
    {
        var now = state.TimeProvider.GetUtcNow();
        const int embeddingDim = 384;
        var embedding = Enumerable.Range(0, embeddingDim).Select(i => 0.1f * i).ToArray();

        var response = new
        {
            @object = "list",
            data = new[]
            {
                new { @object = "embedding", index = 0, embedding }
            },
            model = "fake-embedding-model",
            usage = new { prompt_tokens = 5, total_tokens = 5 },
        };

        await context.Response.WriteAsJsonAsync(response, options: JsonOptions, cancellationToken: context.RequestAborted);
        state.IncrementMetric("vllm:prompt_tokens_total", 5);
    }

    private static Task HandleListModels(HttpContext context, ServerState state)
    {
        return state.IsNotReady()
            ? ReturnServiceUnavailable(context, "Server is warming up")
            : HandleListModelsOk(context);
    }

    private static async Task HandleListModelsOk(HttpContext context)
    {
        var now = DateTimeOffset.UtcNow;
        var response = new
        {
            @object = "list",
            data = new[]
            {
                new { id = "fake-model", @object = "model", created = now.ToUnixTimeSeconds(), owned_by = "mirbadllm" },
                new { id = "fake-embedding-model", @object = "model", created = now.ToUnixTimeSeconds(), owned_by = "mirbadllm" }
            },
        };

        await context.Response.WriteAsJsonAsync(response, options: JsonOptions, cancellationToken: context.RequestAborted);
    }

    private static async Task HandleTokenize(HttpContext context, ServerState state)
    {
        using var sr = new StreamReader(context.Request.Body);
        var bodyText = await sr.ReadToEndAsync(context.RequestAborted);

        // Return a simple token count
        var tokenCount = bodyText.Split(' ').Length;
        var response = new { tokens = Enumerable.Range(0, tokenCount).ToArray() };

        await context.Response.WriteAsJsonAsync(response, options: JsonOptions, cancellationToken: context.RequestAborted);
    }

    private static Task HandleHealth(HttpContext context, ServerState state)
    {
        return state.IsNotReady()
            ? ReturnServiceUnavailable(context, "Server is warming up")
            : HandleHealthOk(context);
    }

    private static async Task HandleHealthOk(HttpContext context)
    {
        var response = new { status = "ok" };
        await context.Response.WriteAsJsonAsync(response, options: JsonOptions, cancellationToken: context.RequestAborted);
    }

    private static Task HandleMetrics(HttpContext context, ServerState state)
    {
        return state.IsNotReady()
            ? ReturnServiceUnavailable(context, "Server is warming up")
            : HandleMetricsOk(context, state);
    }

    private static async Task HandleMetricsOk(HttpContext context, ServerState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# HELP vllm:num_requests_running Current number of running requests");
        sb.AppendLine("# TYPE vllm:num_requests_running gauge");
        sb.AppendLine("vllm:num_requests_running 2.0");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:num_requests_waiting Current number of waiting requests");
        sb.AppendLine("# TYPE vllm:num_requests_waiting gauge");
        sb.AppendLine("vllm:num_requests_waiting 1.0");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:kv_cache_usage_perc KV cache usage percentage");
        sb.AppendLine("# TYPE vllm:kv_cache_usage_perc gauge");
        sb.AppendLine("vllm:kv_cache_usage_perc 0.455"); // a 0–1 fraction, as vLLM reports it
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:generation_tokens_total Total generation tokens");
        sb.AppendLine("# TYPE vllm:generation_tokens_total counter");
        sb.AppendLine($"vllm:generation_tokens_total {state.GetMetric("vllm:generation_tokens_total")}");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:prompt_tokens_total Total prompt tokens");
        sb.AppendLine("# TYPE vllm:prompt_tokens_total counter");
        sb.AppendLine($"vllm:prompt_tokens_total {state.GetMetric("vllm:prompt_tokens_total")}");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:request_success_total Total successful requests");
        sb.AppendLine("# TYPE vllm:request_success_total counter");
        sb.AppendLine($"vllm:request_success_total {state.GetMetric("vllm:request_success_total")}");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:prefix_cache_hits_total Total prefix cache hits");
        sb.AppendLine("# TYPE vllm:prefix_cache_hits_total counter");
        sb.AppendLine($"vllm:prefix_cache_hits_total {state.GetMetric("vllm:prefix_cache_hits_total")}");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:prefix_cache_queries_total Total prefix cache queries");
        sb.AppendLine("# TYPE vllm:prefix_cache_queries_total counter");
        sb.AppendLine($"vllm:prefix_cache_queries_total {state.GetMetric("vllm:prefix_cache_queries_total")}");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:spec_decode_num_accepted_tokens_total Total spec-decode accepted tokens");
        sb.AppendLine("# TYPE vllm:spec_decode_num_accepted_tokens_total counter");
        sb.AppendLine($"vllm:spec_decode_num_accepted_tokens_total {state.GetMetric("vllm:spec_decode_num_accepted_tokens_total")}");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:spec_decode_num_draft_tokens_total Total spec-decode draft tokens");
        sb.AppendLine("# TYPE vllm:spec_decode_num_draft_tokens_total counter");
        sb.AppendLine($"vllm:spec_decode_num_draft_tokens_total {state.GetMetric("vllm:spec_decode_num_draft_tokens_total")}");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:num_preemptions_total Total preemptions");
        sb.AppendLine("# TYPE vllm:num_preemptions_total counter");
        sb.AppendLine($"vllm:num_preemptions_total {state.GetMetric("vllm:num_preemptions_total")}");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:tool_call_parser_invocations_total Total tool call parser invocations");
        sb.AppendLine("# TYPE vllm:tool_call_parser_invocations_total counter");
        sb.AppendLine($"vllm:tool_call_parser_invocations_total {state.GetMetric("vllm:tool_call_parser_invocations_total")}");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:time_to_first_token_seconds Time to first token histogram");
        sb.AppendLine("# TYPE vllm:time_to_first_token_seconds histogram");
        sb.AppendLine("vllm:time_to_first_token_seconds_bucket{le=\"0.005\"} 0");
        sb.AppendLine("vllm:time_to_first_token_seconds_bucket{le=\"0.01\"} 0");
        sb.AppendLine("vllm:time_to_first_token_seconds_bucket{le=\"0.025\"} 1");
        sb.AppendLine("vllm:time_to_first_token_seconds_bucket{le=\"0.05\"} 2");
        sb.AppendLine("vllm:time_to_first_token_seconds_bucket{le=\"0.075\"} 2");
        sb.AppendLine("vllm:time_to_first_token_seconds_bucket{le=\"0.1\"} 2");
        sb.AppendLine("vllm:time_to_first_token_seconds_bucket{le=\"+Inf\"} 2");
        sb.AppendLine("vllm:time_to_first_token_seconds_sum 0.08");
        sb.AppendLine("vllm:time_to_first_token_seconds_count 2");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:inter_token_latency_seconds Inter-token latency histogram");
        sb.AppendLine("# TYPE vllm:inter_token_latency_seconds histogram");
        sb.AppendLine("vllm:inter_token_latency_seconds_bucket{le=\"0.01\"} 10");
        sb.AppendLine("vllm:inter_token_latency_seconds_bucket{le=\"0.025\"} 15");
        sb.AppendLine("vllm:inter_token_latency_seconds_bucket{le=\"0.05\"} 18");
        sb.AppendLine("vllm:inter_token_latency_seconds_bucket{le=\"+Inf\"} 20");
        sb.AppendLine("vllm:inter_token_latency_seconds_sum 0.35");
        sb.AppendLine("vllm:inter_token_latency_seconds_count 20");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:e2e_request_latency_seconds End-to-end request latency histogram");
        sb.AppendLine("# TYPE vllm:e2e_request_latency_seconds histogram");
        sb.AppendLine("vllm:e2e_request_latency_seconds_bucket{le=\"0.1\"} 1");
        sb.AppendLine("vllm:e2e_request_latency_seconds_bucket{le=\"0.5\"} 2");
        sb.AppendLine("vllm:e2e_request_latency_seconds_bucket{le=\"1.0\"} 2");
        sb.AppendLine("vllm:e2e_request_latency_seconds_bucket{le=\"+Inf\"} 2");
        sb.AppendLine("vllm:e2e_request_latency_seconds_sum 1.2");
        sb.AppendLine("vllm:e2e_request_latency_seconds_count 2");
        sb.AppendLine();

        sb.AppendLine("# HELP vllm:request_queue_time_seconds Request queue time histogram");
        sb.AppendLine("# TYPE vllm:request_queue_time_seconds histogram");
        sb.AppendLine("vllm:request_queue_time_seconds_bucket{le=\"0.01\"} 5");
        sb.AppendLine("vllm:request_queue_time_seconds_bucket{le=\"0.05\"} 8");
        sb.AppendLine("vllm:request_queue_time_seconds_bucket{le=\"0.1\"} 10");
        sb.AppendLine("vllm:request_queue_time_seconds_bucket{le=\"+Inf\"} 10");
        sb.AppendLine("vllm:request_queue_time_seconds_sum 0.4");
        sb.AppendLine("vllm:request_queue_time_seconds_count 10");

        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(sb.ToString(), context.RequestAborted);
    }

    // ---- Helpers ----

    private static async Task SendSseEvent(HttpContext context, object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        await context.Response.WriteAsync($"data: {json}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static async Task ReturnServiceUnavailable(HttpContext context, string reason)
    {
        context.Response.StatusCode = 503;
        context.Response.Headers["Retry-After"] = "30";
        await context.Response.WriteAsJsonAsync(
            new { error = new { message = reason, type = "server_error" } },
            options: JsonOptions,
            cancellationToken: context.RequestAborted);
    }

    private static async Task ReturnFailure(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(
            new { error = new { message = "Server error", type = "server_error" } },
            options: JsonOptions,
            cancellationToken: context.RequestAborted);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    // ---- Server state ----

    private sealed class ServerState
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, double> _metrics = new(StringComparer.Ordinal);

        public ServerState(TimeProvider timeProvider)
        {
            TimeProvider = timeProvider;
        }

        public TimeProvider TimeProvider { get; }

        public DateTimeOffset NotReadyUntil { get; set; } = DateTimeOffset.MinValue;
        public int TokenRateMs { get; set; } = 10;
        public int TimeToFirstTokenMs { get; set; } = 50;
        public int SilentGapMs { get; set; }
        public int ResetAfterTokenCount { get; set; } = -1;
        public Task? ResetWhen { get; set; }
        public int FailureStatusCode { get; set; }
        public System.Collections.Concurrent.ConcurrentDictionary<string, (string ContentType, string Body)> Canned { get; } = new(StringComparer.Ordinal);

        public bool IsNotReady() => TimeProvider.GetUtcNow() < NotReadyUntil;

        public void IncrementMetric(string name, double delta)
        {
            lock (_lock)
            {
                if (!_metrics.ContainsKey(name))
                {
                    _metrics[name] = 0;
                }
                _metrics[name] += delta;
            }
        }

        public double GetMetric(string name)
        {
            lock (_lock)
            {
                return _metrics.TryGetValue(name, out var value) ? value : 0;
            }
        }
    }
}
