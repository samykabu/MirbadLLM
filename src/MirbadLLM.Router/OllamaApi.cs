using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>
/// The Ollama-compatible routes on the master's main port, for clients that only speak Ollama's API. Each one is a
/// translation of the <c>/v1</c> router: <c>/api/chat</c>, <c>/api/generate</c>, <c>/api/embed</c> and
/// <c>/api/embeddings</c> rewrite their body (<see cref="OllamaRequestTranslator"/>) and run through
/// <see cref="ProxyHandler"/>, so model resolution, the "-nothink" names, maintenance, not-ready handling,
/// start-on-request, the router statistics and the upload budget are the <c>/v1</c> ones; the answer is rewritten on its
/// way out (<see cref="OllamaResponseTranslator"/>). <c>/api/tags</c>, <c>/api/ps</c> and <c>/api/show</c> read the list
/// <c>GET /v1/models</c> reads. Model management (<c>/api/pull</c>, <c>/api/push</c>, <c>/api/create</c>,
/// <c>/api/copy</c>, <c>/api/delete</c>) is not offered and answers 501 with where models are managed.
/// <para>The paths are exact, so no admin route under <c>/api</c> is shadowed, and the group carries the <c>/v1</c>
/// maintenance gate and API-key filter (same key, same Inference scope, same "open until the first key").</para>
/// </summary>
internal static class OllamaApi
{
    private const string TagSuffix = ":latest";

    // What Go writes for a time or a list it has no value for; Ollama's clients expect the fields to be there.
    private const string NoTime = "0001-01-01T00:00:00Z";

    // Ollama's keep-alive unloads nothing here, and "ollama ps" prints "Stopping..." for an expiry in the past.
    private const string NoExpiry = "2262-01-01T00:00:00Z";

    private static readonly JsonSerializerOptions Json = new();
    private static readonly string[] NotOffered = ["pull", "push", "create", "copy", "delete"];

    /// <summary>This build's version number (the informational version without its commit), for <c>GET /api/version</c>.</summary>
    internal static string Version { get; } =
        (typeof(OllamaApi).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>Maps the routes into <paramref name="group"/> (<c>/api</c>, anonymous to the admin login, filters already added).</summary>
    public static void Map(RouteGroupBuilder group)
    {
        group.MapGet("/version", () => Results.Json(new VersionResponse(Version), Json));
        group.MapGet("/tags", TagsAsync);
        group.MapGet("/ps", PsAsync);
        group.MapPost("/show", ShowAsync);
        group.MapPost("/chat", Proxy(OllamaRoute.Chat));
        group.MapPost("/generate", Proxy(OllamaRoute.Generate));
        group.MapPost("/embed", Proxy(OllamaRoute.Embed));
        group.MapPost("/embeddings", Proxy(OllamaRoute.Embeddings));
        foreach (string action in NotOffered)
        {
            group.Map("/" + action, () => Results.Json(
                new ErrorResponse($"MirbadLLM does not offer /api/{action}. Models are added, started and removed on the Models page of the MirbadLLM UI."),
                Json, statusCode: StatusCodes.Status501NotImplemented));
        }
    }

    /// <summary>
    /// Outermost filter of the group: everything the inner filters and <see cref="ProxyHandler"/> write (the 401, the 503
    /// of a maintenance window, router and upstream errors, the proxied answer) passes through
    /// <see cref="OllamaResponseStream"/> and leaves in Ollama's shape, with the status code and headers they set.
    /// </summary>
    public static async ValueTask<object?> TranslateResponsesAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var context = invocation.HttpContext;
        var original = context.Features.Get<IHttpResponseBodyFeature>()!;
        var exchange = new OllamaExchange(context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
        await using var stream = new OllamaResponseStream(context.Response, original.Stream, exchange);
        var feature = new StreamResponseBodyFeature(stream, original);
        context.Features.Set(exchange);
        context.Features.Set<IHttpResponseBodyFeature>(feature);
        try
        {
            object? result = await next(invocation);
            await feature.CompleteAsync();
            // After a client disconnect or an aborted connection there is nobody to finish the answer for.
            if (!context.RequestAborted.IsCancellationRequested)
            {
                await stream.FinishAsync(context.RequestAborted);
            }

            return result;
        }
        finally
        {
            context.Features.Set(original);
        }
    }

    /// <summary>
    /// The served name behind the name a client sent. Ollama clients append <c>:latest</c> to a name without a tag, so
    /// <c>name:latest</c> means <c>name</c> when nothing is served as <c>name:latest</c> and <c>name</c> is (or is a
    /// "-nothink" name of a served model).
    /// </summary>
    internal static async Task<string> ResolveNameAsync(IModelEndpointResolver resolver, string requested, CancellationToken ct)
    {
        if (requested.Length <= TagSuffix.Length || !requested.EndsWith(TagSuffix, StringComparison.OrdinalIgnoreCase) ||
            await IsServedAsync(resolver, requested, ct))
        {
            return requested;
        }

        string bare = requested[..^TagSuffix.Length];
        return await IsServedAsync(resolver, bare, ct) ? bare : requested;
    }

    private static async Task<bool> IsServedAsync(IModelEndpointResolver resolver, string name, CancellationToken ct) =>
        await resolver.ResolveAsync(name, ct) is not null ||
        (NoThinking.BaseName(name) is { } baseName && await resolver.ResolveAsync(baseName, ct) is not null);

    private static Delegate Proxy(OllamaRoute route) => (
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        IModelEndpointResolver resolver,
        IModelRuntime runtime,
        IOptions<MirbadLLMOptions> options,
        RouterStats stats,
        UploadBudget uploadBudget,
        CancellationToken ct) =>
    {
        var exchange = context.Features.Get<OllamaExchange>()!;
        exchange.Route = route;
        context.Features.Set<IProxyRequestTranslation>(exchange);
        // The body that goes upstream is JSON whatever the client declared (curl -d declares a form), and the answer
        // must come back uncompressed to be translated.
        context.Request.ContentType = "application/json";
        context.Request.Headers.Remove("Accept-Encoding");
        return ProxyHandler.HandleAsync(
            context, OllamaRequestTranslator.PathOf(route), httpClientFactory, resolver, runtime, options, stats, uploadBudget, ct);
    };

    // ---- The model list ----

    private static async Task<IResult> TagsAsync(HttpContext context, IModelEndpointResolver resolver, CancellationToken ct)
    {
        var models = (await ModelsEndpoint.ListServedAsync(context.RequestServices, resolver, ct))
            .Select(m => new TagEntry(m.Name, m.Name, NoTime, 0, DigestOf(m.Name), DetailsOf(m.Definition)))
            .ToArray();
        return Results.Json(new ListResponse<TagEntry>(models), Json);
    }

    /// <summary>The models that are loaded: the served ones, without the "-nothink" names (the same instance).</summary>
    private static async Task<IResult> PsAsync(HttpContext context, IModelEndpointResolver resolver, CancellationToken ct)
    {
        var models = (await ModelsEndpoint.ListServedAsync(context.RequestServices, resolver, ct, loadedOnly: true))
            .Where(m => !m.IsNoThinking)
            .Select(m => new PsEntry(
                m.Name, m.Name, 0, DigestOf(m.Name), DetailsOf(m.Definition), NoExpiry, 0,
                m.Definition is null ? 0 : ModelCapabilities.MaxModelLen(m.Definition)))
            .ToArray();
        return Results.Json(new ListResponse<PsEntry>(models), Json);
    }

    private static async Task<IResult> ShowAsync(HttpContext context, IModelEndpointResolver resolver, CancellationToken ct)
    {
        string? requested = null;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                // "name" is the field's older spelling. "ollama show" sends it next to an empty "model".
                requested = StringOf(document.RootElement, "model") is { Length: > 0 } model ? model : StringOf(document.RootElement, "name");
            }
        }
        catch (JsonException)
        {
            // Answered below: the request names no model.
        }

        if (string.IsNullOrEmpty(requested))
        {
            return Results.Json(new ErrorResponse("Request does not name a model."), Json, statusCode: StatusCodes.Status400BadRequest);
        }

        string name = await ResolveNameAsync(resolver, requested, ct);
        bool noThinking = false;
        var endpoint = await resolver.ResolveAsync(name, ct);
        if (endpoint is null && NoThinking.BaseName(name) is { } baseName)
        {
            endpoint = await resolver.ResolveAsync(baseName, ct);
            noThinking = endpoint is not null;
        }

        if (endpoint is null)
        {
            return Results.Json(new ErrorResponse($"Model '{requested}' is not registered."), Json, statusCode: StatusCodes.Status404NotFound);
        }

        var definition = context.RequestServices.GetService<IModelRegistry>() is { } registry ? await registry.GetAsync(endpoint.ModelId, ct) : null;
        return Results.Json(new ShowResponse(DetailsOf(definition), new JsonObject(), CapabilitiesOf(definition, noThinking)), Json);
    }

    /// <summary>
    /// Only what the registry holds: <c>gguf</c> for a llama.cpp model (its definition names a GGUF file) and the
    /// definition's quantization. It stores no family and no parameter count, so those stay empty.
    /// </summary>
    private static ModelDetails DetailsOf(ModelDefinition? definition) => new(
        "",
        definition?.Runtime == RuntimeKind.LlamaCpp ? "gguf" : "",
        "",
        null,
        "",
        definition?.Quantization ?? "");

    private static string[]? CapabilitiesOf(ModelDefinition? definition, bool noThinking)
    {
        if (definition is null)
        {
            return null;
        }

        var capabilities = new List<string>();
        if (ModelCapabilities.IsChat(definition))
        {
            capabilities.Add("completion");
        }

        if (PoolingDetection.Detect(definition) == PoolingMode.Embedding)
        {
            capabilities.Add("embedding");
        }

        if (ModelCapabilities.ToolCalling(definition))
        {
            capabilities.Add("tools");
        }

        if (ModelCapabilities.Vision(definition))
        {
            capabilities.Add("vision");
        }

        if (NoThinking.Offered(definition) && !noThinking)
        {
            capabilities.Add("thinking");
        }

        return [.. capabilities];
    }

    /// <summary>
    /// The registry stores no digest. "ollama list" and "ollama ps" print a digest's first 12 characters and crash on
    /// a shorter one, so each name gets a stand-in of a digest's length: the SHA-256 of the name.
    /// </summary>
    private static string DigestOf(string name) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));

    private static string? StringOf(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record VersionResponse([property: JsonPropertyName("version")] string Version);

    private sealed record ErrorResponse([property: JsonPropertyName("error")] string Error);

    private sealed record ListResponse<T>([property: JsonPropertyName("models")] IReadOnlyList<T> Models);

    private sealed record TagEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("modified_at")] string ModifiedAt,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string Digest,
        [property: JsonPropertyName("details")] ModelDetails Details);

    private sealed record PsEntry(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string Digest,
        [property: JsonPropertyName("details")] ModelDetails Details,
        [property: JsonPropertyName("expires_at")] string ExpiresAt,
        [property: JsonPropertyName("size_vram")] long SizeVram,
        [property: JsonPropertyName("context_length")] int ContextLength);

    private sealed record ModelDetails(
        [property: JsonPropertyName("parent_model")] string ParentModel,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("family")] string Family,
        [property: JsonPropertyName("families")] string[]? Families,
        [property: JsonPropertyName("parameter_size")] string ParameterSize,
        [property: JsonPropertyName("quantization_level")] string QuantizationLevel);

    private sealed record ShowResponse(
        [property: JsonPropertyName("details")] ModelDetails Details,
        [property: JsonPropertyName("model_info")] JsonObject ModelInfo,
        [property: JsonPropertyName("capabilities"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Capabilities);
}

/// <summary>
/// One Ollama request on its way through <see cref="ProxyHandler"/>: translates the body once it was read
/// (<see cref="IProxyRequestTranslation"/>) and holds what the answer's translation needs (the route, the model name as
/// the client sent it, whether it streams, when the request arrived).
/// </summary>
internal sealed class OllamaExchange(TimeProvider time) : IProxyRequestTranslation
{
    private readonly long _start = time.GetTimestamp();
    private bool _completions;
    private string _model = "";

    /// <summary>The route, set by its handler before <see cref="ProxyHandler"/> runs.</summary>
    public OllamaRoute Route { get; set; }

    /// <summary>The translator of the answer; null until the request was translated (an earlier error needs none).</summary>
    public OllamaResponseTranslator? Translator { get; private set; }

    /// <summary>True when the exchange wrote an Ollama answer itself (a load request), which goes out as it is.</summary>
    public bool Direct { get; private set; }

    /// <summary>A fresh translator: the response was cleared before anything left (an upstream failure answered as an error).</summary>
    public void Restart()
    {
        if (Translator is not null)
        {
            Translator = NewTranslator();
        }
    }

    /// <inheritdoc />
    public async ValueTask<(string Path, ArraySegment<byte> Body)?> TranslateAsync(HttpContext context, ArraySegment<byte> body, CancellationToken ct)
    {
        string path = OllamaRequestTranslator.PathOf(Route);
        if (ParseObject(body) is not { } request || OllamaRequestTranslator.Text(request["model"]) is not { Length: > 0 } requested)
        {
            return (path, body); // ProxyHandler answers 400 model_required, as on /v1
        }

        _model = requested;
        var resolver = context.RequestServices.GetRequiredService<IModelEndpointResolver>();
        string name = await OllamaApi.ResolveNameAsync(resolver, requested, ct);

        if (OllamaRequestTranslator.IsLoad(Route, request))
        {
            await AnswerLoadAsync(context, resolver, request, name, ct);
            return null;
        }

        if (Route == OllamaRoute.Generate && OllamaRequestTranslator.Text(request["suffix"]) is { Length: > 0 })
        {
            // Fill-in-the-middle needs the model's own template, which only the runtime has: refused, not silently dropped.
            await OpenAiError.WriteAsync(
                context, StatusCodes.Status400BadRequest,
                "MirbadLLM's Ollama API does not support 'suffix' (fill-in-the-middle).", "invalid_request_error", "unsupported_parameter");
            return null;
        }

        // shortcut: the parsed request and the translated body exist next to the buffered one for a moment, outside the
        // upload budget (which counts the bytes received); count them if large image requests ever press on memory.
        var translated = OllamaRequestTranslator.Translate(Route, request, name);
        _completions = translated.Path == OllamaRequestTranslator.CompletionsPath;
        Translator = NewTranslator();
        return (translated.Path, JsonSerializer.SerializeToUtf8Bytes(translated.Body, OllamaRequestTranslator.Json));
    }

    /// <summary>
    /// A request without a prompt or messages asks Ollama to load the model. Here a model is loaded by starting it, so the
    /// answer says "loaded" only when the model serves now; otherwise the <c>/v1</c> answer for its state (404, 503
    /// maintenance, 503 not ready), without starting it. With <c>keep_alive: 0</c> the request asks to unload, which is refused.
    /// </summary>
    private async Task AnswerLoadAsync(HttpContext context, IModelEndpointResolver resolver, JsonObject request, string name, CancellationToken ct)
    {
        if (OllamaRequestTranslator.AsksToUnload(request))
        {
            await OpenAiError.WriteAsync(
                context, StatusCodes.Status400BadRequest,
                "MirbadLLM does not unload models through the Ollama API. Stop a model on the Models page of the MirbadLLM UI.",
                "invalid_request_error", "unsupported_parameter");
            return;
        }

        var endpoint = await resolver.ResolveAsync(name, ct)
            ?? (NoThinking.BaseName(name) is { } baseName ? await resolver.ResolveAsync(baseName, ct) : null);
        if (endpoint is null)
        {
            await ProxyHandler.WriteModelNotFoundAsync(context, _model);
            return;
        }

        var services = context.RequestServices;
        var window = await RouterMaintenance.ForModelAsync(
            services.GetService<IMaintenanceMode>(), RouterMaintenance.NodeScoped(services), services.GetService<IModelRegistry>(), endpoint.ModelId, ct);
        if (window is not null)
        {
            await RouterMaintenance.WriteAsync(context, window);
            return;
        }

        if (endpoint.State != ModelState.Ready)
        {
            await ProxyHandler.WriteNotReadyAsync(context, _model, endpoint.State.ToString());
            return;
        }

        Direct = true;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(NewTranslator().Loaded(), ct);
    }

    private OllamaResponseTranslator NewTranslator() => new(Route, _completions, _model, time, _start);

    private static JsonObject? ParseObject(ArraySegment<byte> body)
    {
        if (body.Count == 0)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body.AsSpan()) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The response body of an Ollama route while the <c>/v1</c> code writes to it. A streamed answer
/// (<c>text/event-stream</c>) leaves line by line as <c>application/x-ndjson</c>; anything else is held until the handler
/// is done and leaves as one JSON object: the translated answer, or <c>{"error":"..."}</c> for a status outside 2xx.
/// Nothing reaches the client before the first translated byte, so the status and headers the <c>/v1</c> code set stand,
/// and a response it clears (<see cref="SetLength"/>) starts over.
/// </summary>
internal sealed class OllamaResponseStream(HttpResponse response, Stream inner, OllamaExchange exchange) : Stream
{
    private enum Mode
    {
        Undecided,
        Direct,
        Lines,
        Held,
    }

    private readonly MemoryStream _held = new();
    private Mode _mode;
    private bool _started;
    private bool _tooLarge;

    /// <summary>True until the first byte left: <c>HttpResponse.Clear()</c> resets such a response through <see cref="SetLength"/>.</summary>
    public override bool CanSeek => !_started;

    public override bool CanRead => false;

    public override bool CanWrite => true;

    public override long Length => _held.Length;

    public override long Position
    {
        get => _held.Length;
        set => throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        if (_started || value != 0)
        {
            throw new NotSupportedException();
        }

        _held.SetLength(0);
        _mode = Mode.Undecided;
        _tooLarge = false;
        exchange.Restart();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_mode == Mode.Undecided)
        {
            bool success = response.StatusCode is >= 200 and < 300;
            _mode = exchange.Direct || (success && exchange.Translator is null) ? Mode.Direct
                : success && response.ContentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true ? Mode.Lines
                : Mode.Held;
        }

        switch (_mode)
        {
            case Mode.Direct:
                _started = true;
                await inner.WriteAsync(buffer, cancellationToken);
                break;
            case Mode.Lines:
                await SendLinesAsync(exchange.Translator!.Process(buffer.Span), cancellationToken);
                break;
            default:
                if (_held.Length + buffer.Length > EmbeddingsCompat.MaxResponseBytes)
                {
                    _tooLarge = true;
                }
                else if (!_tooLarge)
                {
                    _held.Write(buffer.Span);
                }

                break;
        }
    }

    /// <summary>Called once the handler returned: ends a stream with its last object, or translates and sends what was held.</summary>
    public async Task FinishAsync(CancellationToken ct)
    {
        if (_mode == Mode.Lines)
        {
            await SendLinesAsync(exchange.Translator!.Complete(), ct);
            await FlushAsync(ct);
            return;
        }

        // An error status the /v1 code set without a body still gets Ollama's error object. (A route's own result, an
        // IResult, is written after this filter and never passes here.)
        bool success = response.StatusCode is >= 200 and < 300;
        if (_mode != Mode.Held && (_mode != Mode.Undecided || success))
        {
            return;
        }

        var held = _held.GetBuffer().AsMemory(0, (int)_held.Length);
        byte[]? body = !success ? OllamaResponseTranslator.Error(held, response.StatusCode)
            : _tooLarge ? null
            : exchange.Translator?.Convert(held);
        if (body is null)
        {
            // The model's answer cannot be given in Ollama's shape; it must not go out as a success with another shape.
            response.StatusCode = StatusCodes.Status502BadGateway;
            body = OllamaResponseTranslator.ErrorObject("Upstream request failed: the answer is not an OpenAI response this route can translate.", line: false);
        }

        response.ContentType = "application/json";
        response.Headers.ContentEncoding = default;
        response.ContentLength = body.Length;
        _started = true;
        await inner.WriteAsync(body, ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => _started ? inner.FlushAsync(cancellationToken) : Task.CompletedTask;

    public override void Flush()
    {
    }

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("Write asynchronously.");

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _held.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task SendLinesAsync(byte[] lines, CancellationToken ct)
    {
        if (lines.Length == 0)
        {
            return;
        }

        if (!_started)
        {
            _started = true;
            response.ContentType = "application/x-ndjson";
            response.ContentLength = null;
        }

        await inner.WriteAsync(lines, ct);
    }
}
