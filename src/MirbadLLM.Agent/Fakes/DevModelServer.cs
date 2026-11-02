using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;

namespace MirbadLLM.Agent.Fakes;

/// <summary>
/// A small vLLM-shaped HTTP server for the development fake runtime (MN-042): a copy of the test suite's
/// <c>FakeModelServer</c> (MN-008), trimmed to what the dev setup needs, because the agent and the tests must not
/// reference each other. One instance per fake model, on <c>127.0.0.1:{hostPort}</c> (the port the agent's reservation
/// handed out), serving <c>/health</c> (503 until <see cref="ReadyAt"/>), <c>/v1/models</c>, <c>/v1/chat/completions</c>
/// and <c>/v1/completions</c> (buffered and SSE), <c>/v1/embeddings</c>, <c>/tokenize</c> and a vLLM-shaped
/// <c>/metrics</c> whose counters follow the requests served. Answers name the model by its served name.
/// </summary>
internal sealed class DevModelServer : IAsyncDisposable
{
    private const int CompletionTokens = 16;
    private const int PromptTokens = 8;

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly WebApplication _app;
    private readonly Counters _counters;

    private DevModelServer(WebApplication app, Counters counters, Uri baseUri, DateTimeOffset readyAt)
    {
        _app = app;
        _counters = counters;
        BaseUri = baseUri;
        ReadyAt = readyAt;
    }

    /// <summary>Base address, e.g. <c>http://127.0.0.1:8101/</c>.</summary>
    public Uri BaseUri { get; }

    /// <summary>When <c>/health</c> starts answering 200 (the fake "model load" time).</summary>
    public DateTimeOffset ReadyAt { get; }

    /// <summary>Generation requests served so far.</summary>
    public long RequestsServed => Interlocked.Read(ref _counters.Requests);

    /// <summary>
    /// Starts a server on <c>127.0.0.1:<paramref name="port"/></c>. Throws <see cref="IOException"/> when the port is taken
    /// (another process, or another agent of the dev setup with an overlapping port range).
    /// </summary>
    public static async Task<DevModelServer> StartAsync(
        int port, string servedName, TimeSpan startDelay, TimeSpan tokenDelay, TimeProvider time, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(servedName);
        ArgumentNullException.ThrowIfNull(time);
        var counters = new Counters();
        DateTimeOffset readyAt = time.GetUtcNow() + startDelay;
        var settings = new Settings(servedName, readyAt, tokenDelay, time, counters);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();

        // Loopback on the reserved port only; never the environment's ASPNETCORE_URLS / HTTP_PORTS.
        builder.WebHost.UseUrls();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, port));
        WebApplication app = builder.Build();
        app.MapGet("/health", (HttpContext context) => Health(context, settings));
        app.MapGet("/v1/models", (HttpContext context) => Models(context, settings));
        app.MapPost("/v1/chat/completions", (HttpContext context) => GenerateAsync(context, settings, chat: true));
        app.MapPost("/v1/completions", (HttpContext context) => GenerateAsync(context, settings, chat: false));
        app.MapPost("/v1/embeddings", (HttpContext context) => EmbeddingsAsync(context, settings));
        app.MapPost("/tokenize", (HttpContext context) => TokenizeAsync(context, settings));
        app.MapGet("/metrics", (HttpContext context) => MetricsAsync(context, settings));
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new DevModelServer(app, counters, new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/"), readyAt);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _app.StopAsync(stop.Token).ConfigureAwait(false);
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Task Health(HttpContext context, Settings settings)
    {
        if (!settings.IsReady)
        {
            return NotReadyAsync(context);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        return Task.CompletedTask;
    }

    private static Task Models(HttpContext context, Settings settings)
    {
        if (!settings.IsReady)
        {
            return NotReadyAsync(context);
        }

        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            @object = "list",
            data = new[] { new { id = settings.ServedName, @object = "model", created = settings.Created, owned_by = "mirbadllm-dev" } },
        }, Json), context.RequestAborted);
    }

    private static async Task GenerateAsync(HttpContext context, Settings settings, bool chat)
    {
        if (!settings.IsReady)
        {
            await NotReadyAsync(context).ConfigureAwait(false);
            return;
        }

        bool stream;
        int maxTokens = CompletionTokens;
        try
        {
            using JsonDocument body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
            stream = body.RootElement.TryGetProperty("stream", out JsonElement s) && s.ValueKind == JsonValueKind.True;
            if (body.RootElement.TryGetProperty("max_tokens", out JsonElement max) && max.TryGetInt32(out int requested) && requested > 0)
            {
                maxTokens = Math.Min(requested, CompletionTokens * 8);
            }
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("""{"error":{"message":"Invalid JSON","type":"invalid_request_error"}}""", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        CancellationToken ct = context.RequestAborted;
        string id = (chat ? "chatcmpl-dev-" : "cmpl-dev-") + Guid.NewGuid().ToString("N")[..12];
        string kind = chat ? "chat.completion" : "text_completion";
        Interlocked.Add(ref settings.Counters.PromptTokens, PromptTokens);
        if (!stream)
        {
            await Task.Delay(settings.TokenDelay * maxTokens, settings.Time, ct).ConfigureAwait(false);
            string text = string.Join(' ', Enumerable.Range(0, maxTokens).Select(Word));
            object choice = chat
                ? new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" }
                : new { index = 0, text, finish_reason = "stop" };
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                id,
                @object = kind,
                created = settings.Time.GetUtcNow().ToUnixTimeSeconds(),
                model = settings.ServedName,
                choices = new[] { choice },
                usage = new { prompt_tokens = PromptTokens, completion_tokens = maxTokens, total_tokens = PromptTokens + maxTokens },
            }, Json), ct).ConfigureAwait(false);
            settings.Counters.Served(maxTokens);
            return;
        }

        context.Response.ContentType = "text/event-stream";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        string chunkKind = chat ? "chat.completion.chunk" : "text_completion";
        if (chat)
        {
            await SendAsync(context, new
            {
                id,
                @object = chunkKind,
                created = settings.Time.GetUtcNow().ToUnixTimeSeconds(),
                model = settings.ServedName,
                choices = new[] { new { index = 0, delta = new { role = "assistant", content = "" }, finish_reason = (string?)null } },
            }).ConfigureAwait(false);
        }

        for (int i = 0; i < maxTokens; i++)
        {
            await Task.Delay(settings.TokenDelay, settings.Time, ct).ConfigureAwait(false);
            string piece = (i == 0 ? "" : " ") + Word(i);
            object choice = chat
                ? new { index = 0, delta = new { content = piece }, finish_reason = (string?)null }
                : new { index = 0, text = piece, finish_reason = (string?)null };
            await SendAsync(context, new
            {
                id,
                @object = chunkKind,
                created = settings.Time.GetUtcNow().ToUnixTimeSeconds(),
                model = settings.ServedName,
                choices = new[] { choice },
            }).ConfigureAwait(false);
        }

        object last = chat
            ? new { index = 0, delta = new { }, finish_reason = "stop" }
            : new { index = 0, text = "", finish_reason = "stop" };
        await SendAsync(context, new
        {
            id,
            @object = chunkKind,
            created = settings.Time.GetUtcNow().ToUnixTimeSeconds(),
            model = settings.ServedName,
            choices = new[] { last },
            usage = new { prompt_tokens = PromptTokens, completion_tokens = maxTokens, total_tokens = PromptTokens + maxTokens },
        }).ConfigureAwait(false);
        await context.Response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        settings.Counters.Served(maxTokens);
    }

    private static async Task EmbeddingsAsync(HttpContext context, Settings settings)
    {
        if (!settings.IsReady)
        {
            await NotReadyAsync(context).ConfigureAwait(false);
            return;
        }

        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            @object = "list",
            data = new[] { new { @object = "embedding", index = 0, embedding = Enumerable.Range(0, 16).Select(i => i / 16.0).ToArray() } },
            model = settings.ServedName,
            usage = new { prompt_tokens = PromptTokens, total_tokens = PromptTokens },
        }, Json), context.RequestAborted).ConfigureAwait(false);
        Interlocked.Add(ref settings.Counters.PromptTokens, PromptTokens);
    }

    private static async Task TokenizeAsync(HttpContext context, Settings settings)
    {
        using var reader = new StreamReader(context.Request.Body);
        string body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        int count = Math.Max(1, body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            count,
            max_model_len = 8192,
            tokens = Enumerable.Range(0, count).ToArray(),
            model = settings.ServedName,
        }, Json), context.RequestAborted).ConfigureAwait(false);
    }

    private static Task MetricsAsync(HttpContext context, Settings settings)
    {
        if (!settings.IsReady)
        {
            return NotReadyAsync(context);
        }

        Counters c = settings.Counters;
        string label = "{model_name=\"" + settings.ServedName + "\"}";
        var text = new StringBuilder();
        Gauge(text, "vllm:num_requests_running", label, 0);
        Gauge(text, "vllm:num_requests_waiting", label, 0);
        Gauge(text, "vllm:kv_cache_usage_perc", label, 0.05);
        Counter(text, "vllm:prompt_tokens_total", label, Interlocked.Read(ref c.PromptTokens));
        Counter(text, "vllm:generation_tokens_total", label, Interlocked.Read(ref c.GenerationTokens));
        Counter(text, "vllm:request_success_total", label, Interlocked.Read(ref c.Requests));
        Counter(text, "vllm:prefix_cache_queries_total", label, 0);
        Counter(text, "vllm:prefix_cache_hits_total", label, 0);
        Counter(text, "vllm:num_preemptions_total", label, 0);
        context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        return context.Response.WriteAsync(text.ToString(), context.RequestAborted);
    }

    private static void Gauge(StringBuilder text, string name, string label, double value)
    {
        text.Append("# TYPE ").Append(name).Append(" gauge\n");
        text.Append(name).Append(label).Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
    }

    private static void Counter(StringBuilder text, string name, string label, long value)
    {
        text.Append("# TYPE ").Append(name).Append(" counter\n");
        text.Append(name).Append(label).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
    }

    private static string Word(int index) => FakeWords[index % FakeWords.Length];

    private static readonly string[] FakeWords = ["this", "is", "a", "fake", "answer", "from", "the", "mirbadllm", "dev", "runtime"];

    private static Task NotReadyAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync("""{"error":{"message":"The fake model is still loading","type":"server_error"}}""", context.RequestAborted);
    }

    private static async Task SendAsync(HttpContext context, object data)
    {
        await context.Response.WriteAsync("data: " + JsonSerializer.Serialize(data, Json) + "\n\n", context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private sealed class Counters
    {
        public long Requests;
        public long PromptTokens;
        public long GenerationTokens;

        public void Served(int tokens)
        {
            Interlocked.Increment(ref Requests);
            Interlocked.Add(ref GenerationTokens, tokens);
        }
    }

    private sealed record Settings(string ServedName, DateTimeOffset ReadyAt, TimeSpan TokenDelay, TimeProvider Time, Counters Counters)
    {
        public long Created { get; } = ReadyAt.ToUnixTimeSeconds();

        public bool IsReady => Time.GetUtcNow() >= ReadyAt;
    }
}
