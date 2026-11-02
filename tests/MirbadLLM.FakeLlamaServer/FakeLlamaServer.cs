using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MirbadLLM.FakeLlamaServer;

/// <summary>
/// A fake <c>llama-server</c> (MN-211; design-p3p4 §A.1.3, §A.1.9, spike S1's endpoint table): <c>/health</c> is open and
/// answers 503 <c>Loading model</c> while it "loads", then <c>{"status":"ok"}</c>; with <c>LLAMA_API_KEY</c> set,
/// <c>/v1/*</c> and <c>/metrics</c> answer 401 without the bearer key (C5); <c>/v1/models</c> lists the <c>--alias</c>;
/// <c>/v1/embeddings</c> returns deterministic unit vectors of the native length, ignores <c>dimensions</c> and echoes the
/// request <c>model</c> unvalidated (C8), refusing an input over <c>-c / -np</c> tokens with 400
/// <c>exceed_context_size_error</c>; <c>/v1/chat/completions</c> streams, with a final usage chunk when
/// <c>stream_options.include_usage</c> asks for one (MN-236, as llama-server does); <c>/metrics</c> exposes every
/// <c>LlamaCppMetrics.Families</c> name (<see cref="MetricFamilies"/>). It logs to stderr only (stdout stays empty, as llama-server's does).
/// <para>It runs as its own executable (<see cref="FakeLlamaServerProgram"/>, for the Windows host tests and the dev
/// setup) or in-process (the in-memory <c>FakeProcessHost</c> of the unit tests): <see cref="StartAsync"/> takes the
/// arguments, the environment block and the log writer explicitly and <see cref="Exited"/> completes with the exit code a
/// real process would have.</para>
/// </summary>
public sealed class FakeLlamaServer : IAsyncDisposable
{
    private static readonly byte[] LoadingBody =
        Encoding.UTF8.GetBytes("""{"error":{"message":"Loading model","type":"unavailable_error","code":503}}""");

    private static readonly byte[] UnauthorizedBody =
        Encoding.UTF8.GetBytes("""{"error":{"message":"Invalid API Key","type":"authentication_error","code":401}}""");

    /// <summary>Gauge: requests being processed (<c>LlamaCppMetrics.RequestsProcessing</c>).</summary>
    public const string RequestsProcessing = "llamacpp:requests_processing";

    /// <summary>Gauge: requests waiting for a slot (<c>LlamaCppMetrics.RequestsDeferred</c>).</summary>
    public const string RequestsDeferred = "llamacpp:requests_deferred";

    /// <summary>Counter: prompt tokens (<c>LlamaCppMetrics.PromptTokensTotal</c>).</summary>
    public const string PromptTokensTotal = "llamacpp:prompt_tokens_total";

    /// <summary>Counter: generated tokens (<c>LlamaCppMetrics.TokensPredictedTotal</c>).</summary>
    public const string TokensPredictedTotal = "llamacpp:tokens_predicted_total";

    /// <summary>
    /// The families <c>/metrics</c> serves: the frozen <c>MirbadLLM.Agent.Contracts.LlamaCppMetrics.Families</c> (this
    /// project does not reference the contracts; a unit test keeps the two lists equal), as llama-server <c>b11476</c>
    /// names them (spike S1).
    /// </summary>
    public static IReadOnlyList<string> MetricFamilies { get; } =
    [
        RequestsProcessing, RequestsDeferred, PromptTokensTotal, "llamacpp:prompt_seconds_total", TokensPredictedTotal,
        "llamacpp:tokens_predicted_seconds_total", "llamacpp:prompt_tokens_cached_total",
        "llamacpp:spec_decode_num_accepted_tokens_total", "llamacpp:spec_decode_num_draft_tokens_total",
    ];

    private readonly FakeLlamaOptions _options;
    private readonly TextWriter _log;
    private readonly Lock _logGate = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    private WebApplication? _app;
    private volatile bool _loaded;
    private long _promptTokens;
    private long _predictedTokens;
    private long _processing;
    private int _disposed;

    private FakeLlamaServer(FakeLlamaOptions options, TextWriter log)
    {
        _options = options;
        _log = log;
    }

    /// <summary>The parsed options.</summary>
    public FakeLlamaOptions Options => _options;

    /// <summary>Completes with the exit code when the server stops (a knob-driven crash, <see cref="ExitAsync"/>, or a failed start).</summary>
    public Task<int> Exited => _exited.Task;

    /// <summary>True once the model is "loaded" (<c>/health</c> 200).</summary>
    public bool IsLoaded => _loaded;

    /// <summary>
    /// Starts the server: parses <paramref name="args"/> (an argument error is logged and the returned server has already
    /// exited with code 1, like llama-server), binds <c>--host:--port</c>, then loads for <c>FAKE_LLAMA_LOAD_MS</c>.
    /// </summary>
    /// <param name="args">llama-server arguments (no <c>argv[0]</c>).</param>
    /// <param name="environment">The process's environment block (the knobs and <c>LLAMA_API_KEY</c> are read from it).</param>
    /// <param name="log">Where the server logs (stderr, or the in-process host's log file).</param>
    /// <param name="ct">Cancels the start.</param>
    public static async Task<FakeLlamaServer> StartAsync(
        IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment, TextWriter log, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(log);

        FakeLlamaOptions options;
        try
        {
            options = FakeLlamaOptions.Parse(args, environment);
        }
        catch (ArgumentException ex)
        {
            var failed = new FakeLlamaServer(new FakeLlamaOptions(), log);
            failed.Log("E " + ex.Message);
            failed._exited.TrySetResult(FakeLlamaOptions.ArgumentErrorExitCode);
            return failed;
        }

        var server = new FakeLlamaServer(options, log);
        server.Log($"I build: 0 (fake) with argv: {string.Join(' ', args)}");
        try
        {
            await server.ListenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            server.Log($"E main: couldn't bind HTTP server socket, hostname: {options.Host}, port: {options.Port}: {ex.Message}");
            server._exited.TrySetResult(1);
            return server;
        }

        server.Log($"I main: server is listening on http://{options.Host}:{options.Port}");
        server.Schedule();
        return server;
    }

    /// <summary>Stops the server now and completes <see cref="Exited"/> with <paramref name="exitCode"/> (a kill or a crash).</summary>
    public async Task ExitAsync(int exitCode)
    {
        if (_exited.Task.IsCompleted)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (_app is { } app)
        {
            try
            {
                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _exited.TrySetResult(exitCode);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await ExitAsync(0).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void Log(string line)
    {
        lock (_logGate)
        {
            try
            {
                _log.WriteLine(line);
                _log.Flush();
            }
            catch (ObjectDisposedException)
            {
                // The log file was closed (the process is gone).
            }
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Parse(_options.Host), _options.Port);
            k.AddServerHeader = false;
        });
        builder.Services.AddRoutingCore();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.Use(GateAsync);
        app.MapGet("/health", HealthAsync);
        app.MapGet("/v1/models", ModelsAsync);
        app.MapGet("/models", ModelsAsync);
        app.MapGet("/metrics", MetricsAsync);
        app.MapPost("/v1/embeddings", EmbeddingsAsync);
        app.MapPost("/embeddings", EmbeddingsAsync);
        app.MapPost("/v1/chat/completions", ChatAsync);
        app.MapPost("/chat/completions", ChatAsync);
        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;
    }

    /// <summary>Loading, then the crash knobs.</summary>
    private void Schedule()
    {
        var token = _lifetime.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                var load = Task.Delay(_options.LoadTime, token);
                var crash = _options.ExitAfter is { } after ? Task.Delay(after, token) : Task.Delay(Timeout.Infinite, token);
                if (await Task.WhenAny(load, crash).ConfigureAwait(false) == crash)
                {
                    await CrashAsync("before the model was loaded").ConfigureAwait(false);
                    return;
                }

                if (_options.GpuOutOfMemory)
                {
                    Log("E ggml_metal_synchronize: error: command buffer 0 failed with status 5");
                    Log("E error: Insufficient Memory (00000008:kIOGPUCommandBufferCallbackErrorOutOfMemory)");
                    Log("E ggml_metal_graph_compute: backend is in error state from a previous command buffer failure - recreate the backend to recover");
                    Log("E graph_compute: ggml_backend_sched_graph_compute_async failed with error -1");
                }

                _loaded = true;
                Log($"I srv  load_model: loaded model '{_options.ModelFile}' as '{_options.ServedName}'");
                Log("I main: model loaded");
                var afterReady = _options.ExitAfterReady is { } ready ? Task.Delay(ready, token) : Task.Delay(Timeout.Infinite, token);
                if (await Task.WhenAny(afterReady, crash).ConfigureAwait(false) is var done && !token.IsCancellationRequested)
                {
                    await done.ConfigureAwait(false);
                    await CrashAsync("after the model was loaded").ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private async Task CrashAsync(string when)
    {
        Log($"E fake: simulated crash {when} (exit code {_options.ExitCode})");
        Log("terminate called after throwing an instance of 'std::runtime_error'");
        Log("  what():  RuntimeError: simulated failure");
        await ExitAsync(_options.ExitCode).ConfigureAwait(false);
    }

    /// <summary>503 while loading on every endpoint; then the API key on everything but <c>/health</c>.</summary>
    private async Task GateAsync(HttpContext context, RequestDelegate next)
    {
        if (!_loaded)
        {
            await WriteAsync(context, HttpStatusCode.ServiceUnavailable, LoadingBody).ConfigureAwait(false);
            return;
        }

        if (_options.ApiKey is { } key && !string.Equals(context.Request.Path.Value, "/health", StringComparison.Ordinal))
        {
            string header = context.Request.Headers.Authorization.ToString();
            string expected = "Bearer " + key;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes(expected)))
            {
                await WriteAsync(context, HttpStatusCode.Unauthorized, UnauthorizedBody).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static Task HealthAsync(HttpContext context) =>
        WriteAsync(context, HttpStatusCode.OK, """{"status":"ok"}"""u8.ToArray());

    private Task ModelsAsync(HttpContext context)
    {
        long created = _started.ToUnixTimeSeconds();
        var body = new
        {
            models = new[] { new { name = _options.ServedName, model = _options.ServedName, type = "model" } },
            @object = "list",
            data = new[]
            {
                new
                {
                    id = _options.ServedName,
                    aliases = new[] { _options.ServedName },
                    @object = "model",
                    created,
                    owned_by = "llamacpp",
                    meta = new { n_ctx = _options.SlotContext, n_embd = _options.EmbeddingLength, n_vocab = 151665 },
                },
            },
        };
        return WriteJsonAsync(context, HttpStatusCode.OK, body);
    }

    private Task MetricsAsync(HttpContext context)
    {
        var text = new StringBuilder();
        foreach (string family in MetricFamilies)
        {
            bool gauge = family is RequestsProcessing or RequestsDeferred;
            long value = family switch
            {
                PromptTokensTotal => Interlocked.Read(ref _promptTokens),
                TokensPredictedTotal => Interlocked.Read(ref _predictedTokens),
                RequestsProcessing => Interlocked.Read(ref _processing),
                _ => 0,
            };
            text.Append("# HELP ").Append(family).Append(" fake ").Append(family).Append('\n');
            text.Append("# TYPE ").Append(family).Append(gauge ? " gauge\n" : " counter\n");
            text.Append(family).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; version=0.0.4";
        return context.Response.WriteAsync(text.ToString());
    }

    private async Task EmbeddingsAsync(HttpContext context)
    {
        if (!_options.Embedding)
        {
            await WriteErrorAsync(context, HttpStatusCode.NotImplemented, "not_supported_error",
                "This server does not support embeddings. Start it with `--embeddings`").ConfigureAwait(false);
            return;
        }

        using var request = await ReadBodyAsync(context).ConfigureAwait(false);
        if (request is null || !request.RootElement.TryGetProperty("input", out var input))
        {
            await WriteErrorAsync(context, HttpStatusCode.BadRequest, "invalid_request_error", "\"input\" must be provided").ConfigureAwait(false);
            return;
        }

        var texts = new List<string>();
        if (input.ValueKind == JsonValueKind.String)
        {
            texts.Add(input.GetString() ?? "");
        }
        else if (input.ValueKind == JsonValueKind.Array)
        {
            texts.AddRange(input.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()));
        }
        else
        {
            await WriteErrorAsync(context, HttpStatusCode.BadRequest, "invalid_request_error", "\"input\" must be a string or an array").ConfigureAwait(false);
            return;
        }

        bool base64 = request.RootElement.TryGetProperty("encoding_format", out var format) &&
            format.ValueKind == JsonValueKind.String && format.GetString() == "base64";

        // The request model is echoed back unvalidated (spike S1 / C8); dimensions is ignored (native length returned).
        string? model = request.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

        int promptTokens = 0;
        var data = new List<object>(texts.Count);
        for (int i = 0; i < texts.Count; i++)
        {
            int tokens = CountTokens(texts[i]);
            if (tokens > _options.SlotContext)
            {
                await WriteJsonAsync(context, HttpStatusCode.BadRequest, new
                {
                    error = new
                    {
                        code = 400,
                        message = "the request exceeds the available context size, try increasing it",
                        type = "exceed_context_size_error",
                        n_prompt_tokens = tokens,
                        n_ctx = _options.SlotContext,
                    },
                }).ConfigureAwait(false);
                return;
            }

            promptTokens += tokens;
            float[] vector = Vector(texts[i], _options.EmbeddingLength);
            data.Add(new
            {
                @object = "embedding",
                index = i,
                embedding = base64 ? (object)ToBase64(vector) : vector,
            });
        }

        Interlocked.Add(ref _promptTokens, promptTokens);
        await WriteJsonAsync(context, HttpStatusCode.OK, new
        {
            model = model ?? _options.ServedName,
            @object = "list",
            usage = new { prompt_tokens = promptTokens, total_tokens = promptTokens },
            data,
        }).ConfigureAwait(false);
    }

    private async Task ChatAsync(HttpContext context)
    {
        using var request = await ReadBodyAsync(context).ConfigureAwait(false);
        if (request is null || !request.RootElement.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            await WriteErrorAsync(context, HttpStatusCode.BadRequest, "invalid_request_error", "\"messages\" must be an array").ConfigureAwait(false);
            return;
        }

        string? model = request.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        bool stream = request.RootElement.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        int promptTokens = messages.EnumerateArray()
            .Select(msg => msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? CountTokens(c.GetString() ?? "") : 0)
            .Sum();
        string[] words = ["This", " is", " a", " fake", " llama-server", " reply."];
        string id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        model ??= _options.ServedName;
        Interlocked.Add(ref _promptTokens, promptTokens);
        Interlocked.Increment(ref _processing);
        try
        {
            if (!stream)
            {
                Interlocked.Add(ref _predictedTokens, words.Length);
                await WriteJsonAsync(context, HttpStatusCode.OK, new
                {
                    id,
                    @object = "chat.completion",
                    created,
                    model,
                    choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = string.Concat(words) } } },
                    usage = new { prompt_tokens = promptTokens, completion_tokens = words.Length, total_tokens = promptTokens + words.Length },
                }).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            foreach (string word in words)
            {
                Interlocked.Increment(ref _predictedTokens);
                string chunk = JsonSerializer.Serialize(new
                {
                    id,
                    @object = "chat.completion.chunk",
                    created,
                    model,
                    choices = new[] { new { index = 0, finish_reason = (string?)null, delta = new { content = word } } },
                });
                await context.Response.WriteAsync("data: " + chunk + "\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }

            string last = JsonSerializer.Serialize(new
            {
                id,
                @object = "chat.completion.chunk",
                created,
                model,
                choices = new[] { new { index = 0, finish_reason = "stop", delta = new { } } },
            });
            await context.Response.WriteAsync("data: " + last + "\n\n", context.RequestAborted).ConfigureAwait(false);

            // stream_options.include_usage: a final chunk with no choices and the token counts, as llama-server sends it
            // (MN-236). Without it a benchmark through the dev setup reads 0 completion tokens, so every tuning
            // candidate is rejected with "Missing measurements for this preset" although the run itself succeeded.
            if (request.RootElement.TryGetProperty("stream_options", out var streamOptions) &&
                streamOptions.ValueKind == JsonValueKind.Object &&
                streamOptions.TryGetProperty("include_usage", out var includeUsage) && includeUsage.ValueKind == JsonValueKind.True)
            {
                string final = JsonSerializer.Serialize(new
                {
                    id,
                    @object = "chat.completion.chunk",
                    created,
                    model,
                    choices = Array.Empty<object>(),
                    usage = new { prompt_tokens = promptTokens, completion_tokens = words.Length, total_tokens = promptTokens + words.Length },
                });
                await context.Response.WriteAsync("data: " + final + "\n\n", context.RequestAborted).ConfigureAwait(false);
            }

            await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _processing);
        }
    }

    /// <summary>Whitespace-separated words: a stable stand-in for the tokenizer.</summary>
    internal static int CountTokens(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>A deterministic unit vector for <paramref name="text"/>.</summary>
    internal static float[] Vector(string text, int length)
    {
        var vector = new float[Math.Max(1, length)];
        byte[] seed = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        double norm = 0;
        for (int i = 0; i < vector.Length; i++)
        {
            int b = seed[i % seed.Length] ^ (i * 31 & 0xFF);
            vector[i] = (b - 127.5f) / 127.5f + 0.001f;
            norm += vector[i] * (double)vector[i];
        }

        float scale = (float)(1 / Math.Sqrt(norm));
        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] *= scale;
        }

        return vector;
    }

    private static string ToBase64(float[] vector)
    {
        var bytes = new byte[vector.Length * 4];
        for (int i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), vector[i]);
        }

        return Convert.ToBase64String(bytes);
    }

    private static async Task<JsonDocument?> ReadBodyAsync(HttpContext context)
    {
        try
        {
            return await JsonDocument.ParseAsync(context.Request.Body, default, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task WriteErrorAsync(HttpContext context, HttpStatusCode status, string type, string message) =>
        WriteJsonAsync(context, status, new { error = new { code = (int)status, message, type } });

    private static Task WriteJsonAsync(HttpContext context, HttpStatusCode status, object body) =>
        WriteAsync(context, status, JsonSerializer.SerializeToUtf8Bytes(body));

    private static async Task WriteAsync(HttpContext context, HttpStatusCode status, byte[] body)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }
}
