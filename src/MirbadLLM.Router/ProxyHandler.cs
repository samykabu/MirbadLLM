using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>Catch-all <c>/v1/{**path}</c> handler: resolves the target model, optionally auto-starts it,
/// and proxies the request/response (including SSE streaming) to its upstream.
/// <para>Two paths: with an <see cref="AgentEndpointResolver"/> registered (multi-node master, design §7) the request goes
/// through the placed node's agent over its data client, agent errors are translated before response headers (§7.4) and a
/// failure after headers aborts the client connection; otherwise (until MN-040 switches the composition) the old path
/// sends it to the vLLM upstream through <see cref="RouterExtensions.UpstreamHttpClientName"/>. Both read the body into one
/// buffer under the global <see cref="UploadBudget"/>.</para></summary>
internal static partial class ProxyHandler
{
    private const int MaxBodyBytes = (int)Protocol.MaxProxyRequestBodyBytes;
    private const int InitialChunkedBufferBytes = 16 * 1024;
    private static readonly TimeSpan AutoStartPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);

    private static readonly FrozenSet<string> HopByHopHeaders = new[]
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Trailers", "Transfer-Encoding", "Upgrade",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static async Task HandleAsync(
        HttpContext context,
        string path,
        IHttpClientFactory httpClientFactory,
        IModelEndpointResolver resolver,
        IModelRuntime runtime,
        IOptions<MirbadLLMOptions> options,
        RouterStats stats,
        UploadBudget uploadBudget,
        CancellationToken ct)
    {
        RaiseBodyLimit(context);
        using var lease = uploadBudget.CreateLease();
        var (body, outcome) = await ReadBodyAsync(context, lease, ct);
        if (outcome == BodyOutcome.TooLarge)
        {
            await OpenAiError.WriteAsync(
                context, StatusCodes.Status413PayloadTooLarge,
                $"Request body exceeds the {MaxBodyBytes} byte limit.", "invalid_request_error", "request_too_large");
            return;
        }

        if (outcome == BodyOutcome.Busy)
        {
            await WriteClientErrorAsync(
                context, ErrorMappings.ClientErrors[ErrorCodes.ServerBusy],
                "The server is busy receiving other requests; try again shortly.");
            return;
        }

        // A route that speaks another API (OllamaApi) turns the body, read above under the upload budget, into its /v1 form.
        if (context.Features.Get<IProxyRequestTranslation>() is { } translation)
        {
            if (await translation.TranslateAsync(context, body, ct) is not { } translated)
            {
                return; // the translation answered the request itself
            }

            (path, body) = translated;
        }

        var modelName = TryReadModelName(body);
        if (modelName is null)
        {
            await OpenAiError.WriteAsync(
                context, StatusCodes.Status400BadRequest,
                "Request does not name a model.", "invalid_request_error", "model_required");
            return;
        }

        // "<served>-nothink" (NoThinking): the same model with thinking off, unless a model is really served under that name.
        if (NoThinking.BaseName(modelName) is { } baseName &&
            await resolver.ResolveAsync(modelName, ct) is null &&
            await resolver.ResolveAsync(baseName, ct) is not null)
        {
            body = NoThinking.Rewrite(body, baseName, string.Equals(path, "chat/completions", StringComparison.OrdinalIgnoreCase));
            modelName = baseName;
        }

        if (context.RequestServices.GetService<AgentEndpointResolver>() is { } agentResolver)
        {
            await HandleThroughAgentAsync(context, path, modelName, body, lease, agentResolver, runtime, options.Value, stats, ct);
            return;
        }

        var endpoint = await resolver.ResolveAsync(modelName, ct);
        if (endpoint is null)
        {
            await WriteModelNotFoundAsync(context, modelName);
            return;
        }

        if (await PausedByMaintenanceAsync(context, endpoint.ModelId, ct))
        {
            return;
        }

        if (endpoint.State != ModelState.Ready)
        {
            if (options.Value.AutoStartOnRequest &&
                await StartAndWaitAsync(endpoint.ModelId, endpoint.State, runtime, options.Value, ct))
            {
                endpoint = await resolver.ResolveAsync(endpoint.ServedName, ct);
            }

            if (endpoint is null || endpoint.State != ModelState.Ready)
            {
                await WriteNotReadyAsync(context, modelName, endpoint?.State.ToString() ?? "Unknown");
                return;
            }
        }

        stats.RequestStarted(endpoint.ServedName);
        var failed = true;
        try
        {
            failed = !await ProxyRequestAsync(context, endpoint, path, body, lease, httpClientFactory, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected; the upstream call was cancelled with it. Nothing left to write.
            failed = true;
        }
        finally
        {
            stats.RequestCompleted(endpoint.ServedName, failed);
        }
    }

    // ---- Agent path (design §7.1, §7.2, §7.4) ----

    private static async Task HandleThroughAgentAsync(
        HttpContext context,
        string path,
        string modelName,
        ArraySegment<byte> body,
        UploadBudget.Lease lease,
        AgentEndpointResolver resolver,
        IModelRuntime runtime,
        MirbadLLMOptions options,
        RouterStats stats,
        CancellationToken ct)
    {
        var route = await resolver.ResolveRouteAsync(modelName, ct);
        if (route.Outcome == AgentRouteOutcome.NotFound)
        {
            await WriteModelNotFoundAsync(context, modelName);
            return;
        }

        // P2 order (design §6.5 item 2): maintenance after 404, before the node states, not-ready and any auto-start.
        if (await PausedByMaintenanceAsync(context, route.ModelId!, ct))
        {
            return;
        }

        if (route.Outcome == AgentRouteOutcome.NotReady && options.AutoStartOnRequest &&
            await StartAndWaitAsync(route.ModelId!, route.ModelState, runtime, options, ct))
        {
            route = await resolver.ResolveRouteAsync(modelName, ct);
        }

        if (route.Outcome != AgentRouteOutcome.Ready)
        {
            await WriteRouteErrorAsync(context, modelName, route);
            return;
        }

        string modelId = route.ModelId!;
        string nodeId = route.NodeId!;
        IAgentDataClient data;
        try
        {
            data = resolver.GetDataClient(nodeId);
        }
        catch (KeyNotFoundException)
        {
            await WriteClientErrorAsync(
                context, ErrorMappings.ClientErrors[ErrorCodes.NodeUnreachable],
                $"Node '{nodeId}' of model '{modelName}' has no agent connection.");
            return;
        }

        // MN-217 (design-p3p4 §A.3, D119): `dimensions` emulation, only for a request that carries the field and goes to a
        // llama.cpp embedding model; every other request is forwarded and answered byte for byte.
        int emulatedDimensions = 0;
        if (EmbeddingsCompat.Carries(context.Request.Method, path, body) &&
            context.RequestServices.GetService<IModelRegistry>() is { } registry &&
            EmbeddingDimensions.Applies(await registry.GetAsync(modelId, ct)))
        {
            var prepared = EmbeddingsCompat.Prepare(body);
            if (prepared.IsRefused)
            {
                await EmbeddingsCompat.WriteRefusalAsync(context, prepared.Error!);
                return;
            }

            emulatedDimensions = prepared.Dimensions;
            body = prepared.Body!;
        }

        using var upstreamRequest = new HttpRequestMessage(
            new HttpMethod(context.Request.Method),
            data.BuildProxyUri(modelId, $"v1/{path}{context.Request.QueryString}"))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        if (body.Count > 0)
        {
            upstreamRequest.Content = new ByteArrayContent(body.Array!, body.Offset, body.Count);
        }

        CopyRequestHeaders(context.Request.Headers, upstreamRequest, dropProtocolHeaders: true);

        stats.RequestStarted(route.ServedName);
        var failed = true;
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await data.SendAsync(upstreamRequest, ct);
            }
            catch (AgentUnreachableException ex) when (!ct.IsCancellationRequested)
            {
                LogNodeUnreachable(Logger(context), nodeId, modelId, ex.Message);
                // The cause (address, TLS refusal detail) is logged above, not sent to API clients (W3 review MN-094).
                await WriteClientErrorAsync(
                    context, ErrorMappings.ClientErrors[ErrorCodes.NodeUnreachable],
                    $"Node '{nodeId}' of model '{modelName}' could not be reached; try again shortly.");
                return;
            }

            // Every response is disposed, translated errors included: the node's in-flight slot is released on dispose.
            using (response)
            {
                lease.Dispose(); // the body is with the agent now
                if (response.Headers.TryGetValues(Protocol.AgentErrorHeader, out var codes))
                {
                    await WriteAgentErrorAsync(context, response, codes.FirstOrDefault() ?? "", modelName, modelId, nodeId, resolver, ct);
                    return;
                }

                failed = emulatedDimensions > 0 && EmbeddingsCompat.CanRewrite(response)
                    ? !await RelayEmulatedEmbeddingsAsync(context, response, emulatedDimensions, ct)
                    : !await RelayResponseAsync(context, response, modelId, nodeId, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client disconnected; the agent request was cancelled with it. Nothing left to write.
            failed = true;
        }
        finally
        {
            stats.RequestCompleted(route.ServedName, failed);
        }
    }

    /// <summary>
    /// Passes the model's own answer through (status, headers, body), whatever its status. A transport failure before the
    /// first byte reaches the client becomes 502 <c>upstream_unavailable</c>; after that the client connection is aborted
    /// (no JSON appended to a started response, design §7.4). False when the request counts as failed.
    /// </summary>
    private static async Task<bool> RelayResponseAsync(HttpContext context, HttpResponseMessage response, string modelId, string nodeId, CancellationToken ct)
    {
        try
        {
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response, context.Response.Headers);
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
            await CopyStreamAsync(upstreamStream, context.Response, response, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                await WriteClientErrorAsync(
                    context, ErrorMappings.ClientErrors[ErrorCodes.UpstreamUnavailable], $"Upstream request failed: {ex.Message}");
                return false;
            }

            LogAbortedAfterHeaders(Logger(context), nodeId, modelId, ex.Message);
            context.Abort();
            return false;
        }
    }

    /// <summary>
    /// Relays a 200 JSON <c>/v1/embeddings</c> answer through <see cref="EmbeddingsCompat.Rewrite"/> (MN-217): the
    /// vectors truncated to <paramref name="dimensions"/> and renormalised, the upstream headers kept except the length.
    /// <c>dimensions</c> above the returned vector length → 400 in the OpenAI shape; an answer that is not the OpenAI
    /// embeddings shape, or larger than <see cref="EmbeddingsCompat.MaxResponseBytes"/>, → 502 <c>upstream_unavailable</c>.
    /// </summary>
    private static async Task<bool> RelayEmulatedEmbeddingsAsync(HttpContext context, HttpResponseMessage response, int dimensions, CancellationToken ct)
    {
        byte[] original;
        try
        {
            original = await ReadBoundedAsync(response.Content, EmbeddingsCompat.MaxResponseBytes, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException && !ct.IsCancellationRequested)
        {
            await WriteClientErrorAsync(
                context, ErrorMappings.ClientErrors[ErrorCodes.UpstreamUnavailable], $"Upstream request failed: {ex.Message}");
            return false;
        }

        var rewritten = EmbeddingsCompat.Rewrite(original, dimensions);
        if (rewritten.ClientError is { } refusal)
        {
            await EmbeddingsCompat.WriteRefusalAsync(context, refusal);
            return false;
        }

        if (rewritten.Body is not { } body)
        {
            await WriteClientErrorAsync(
                context, ErrorMappings.ClientErrors[ErrorCodes.UpstreamUnavailable], $"Upstream request failed: {rewritten.Malformed}");
            return false;
        }

        context.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, context.Response.Headers);
        context.Response.Headers.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, ct);
        return true;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[Protocol.ProxyCopyBufferBytes];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException($"The embeddings answer exceeds {maxBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Translates an answer marked with <see cref="Protocol.AgentErrorHeader"/> (design §7.4, D40).</summary>
    private static async Task WriteAgentErrorAsync(
        HttpContext context,
        HttpResponseMessage response,
        string code,
        string modelName,
        string modelId,
        string nodeId,
        AgentEndpointResolver resolver,
        CancellationToken ct)
    {
        string? agentMessage = await TryReadAgentMessageAsync(response, ct);
        if (code.Length == 0)
        {
            code = ErrorCodes.InternalError;
        }

        var translation = ErrorMappings.TranslateProxyError(code);
        var logger = Logger(context);
        if (logger.IsEnabled(LogLevel.Information))
        {
            LogAgentError(logger, nodeId, modelId, code, translation.ClientCode);
        }

        string message;
        if (translation.ClientCode == ErrorCodes.ModelNotReady)
        {
            // The cache was one event late (the instance just stopped or crashed): refresh it at once (design §7.1).
            await TryRefreshAsync(context, resolver, modelId, ct);
            var state = resolver.GetModelState(modelId, nodeId);
            message = NotReadyMessage(modelName, (state == ModelState.Ready ? ModelState.Stopped : state).ToString());
        }
        else if (translation.ClientCode == ErrorCodes.UpstreamUnavailable)
        {
            message = $"Upstream request failed: {agentMessage ?? code}";
        }
        else
        {
            message = agentMessage ?? $"Node '{nodeId}' answered {code}.";
        }

        await WriteClientErrorAsync(context, translation, message);
    }

    private static async Task<string?> TryReadAgentMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<AgentErrorResponse>(AgentJson.Options, ct);
            return string.IsNullOrWhiteSpace(error?.Error?.Message) ? null : error.Error.Message;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or IOException)
        {
            return null;
        }
    }

    private static async Task TryRefreshAsync(HttpContext context, AgentEndpointResolver resolver, string modelId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RefreshTimeout);
        try
        {
            await resolver.RefreshModelAsync(modelId, timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The answer to the client does not depend on the refresh; the node monitor catches up on its own.
            var logger = Logger(context);
            if (logger.IsEnabled(LogLevel.Debug))
            {
                LogRefreshFailed(logger, modelId, ex.Message);
            }
        }
    }

    private static Task WriteRouteErrorAsync(HttpContext context, string modelName, AgentModelRoute route) => route.Outcome switch
    {
        AgentRouteOutcome.NotFound => WriteModelNotFoundAsync(context, modelName),
        AgentRouteOutcome.Unplaced => WriteClientErrorAsync(
            context, ErrorMappings.ClientErrors[ErrorCodes.ModelUnplaced], $"Model '{modelName}' is not placed on a node."),
        AgentRouteOutcome.NodeOffline => WriteClientErrorAsync(
            context, ErrorMappings.ClientErrors[ErrorCodes.NodeOffline],
            route.NodeState is null or NodeState.Offline
                ? $"Node '{route.NodeId}' of model '{modelName}' is offline (state: {route.NodeState?.ToString() ?? "Unknown"})."
                : $"Node '{route.NodeId}' of model '{modelName}' is not serving requests (state: {route.NodeState})."),
        AgentRouteOutcome.NodeIncompatible => WriteClientErrorAsync(
            context, ErrorMappings.ClientErrors[ErrorCodes.NodeIncompatible],
            $"Node '{route.NodeId}' of model '{modelName}' speaks no protocol version this master supports."),
        _ => WriteNotReadyAsync(context, modelName, route.ModelState.ToString()),
    };

    private static Task WriteClientErrorAsync(HttpContext context, ClientErrorTranslation error, string message)
    {
        if (error.RetryAfterSeconds is { } retryAfter)
        {
            context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        }

        return OpenAiError.WriteAsync(context, error.StatusCode, message, error.ErrorType ?? DefaultErrorType(error.StatusCode), error.ClientCode);
    }

    private static string DefaultErrorType(int statusCode) => statusCode switch
    {
        StatusCodes.Status503ServiceUnavailable => "service_unavailable_error",
        >= 500 => "api_error",
        _ => "invalid_request_error",
    };

    private static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(typeof(ProxyHandler).FullName!) ?? NullLogger.Instance;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Router: node {NodeId} unreachable for model {ModelId}: {Reason}")]
    private static partial void LogNodeUnreachable(ILogger logger, string nodeId, string modelId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Router: request for model {ModelId} on node {NodeId} failed after response headers; client connection aborted: {Reason}")]
    private static partial void LogAbortedAfterHeaders(ILogger logger, string nodeId, string modelId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Router: node {NodeId} answered {AgentCode} for model {ModelId}; client gets {ClientCode}")]
    private static partial void LogAgentError(ILogger logger, string nodeId, string modelId, string agentCode, string clientCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Router: status refresh of model {ModelId} failed: {Reason}")]
    private static partial void LogRefreshFailed(ILogger logger, string modelId, string reason);

    // ---- Shared ----

    /// <summary>
    /// The P2 maintenance step (design §6.5 item 2): answers 503 <c>maintenance</c> and returns true when a window pauses
    /// the resolved model. A no-op in the P1 order (the gate in front of the API key already answered for every window).
    /// </summary>
    private static async Task<bool> PausedByMaintenanceAsync(HttpContext context, string modelId, CancellationToken ct)
    {
        var services = context.RequestServices;
        var window = await RouterMaintenance.ForModelAsync(
            services.GetService<IMaintenanceMode>(), RouterMaintenance.NodeScoped(services), services.GetService<IModelRegistry>(), modelId, ct);
        if (window is null)
        {
            return false;
        }

        await RouterMaintenance.WriteAsync(context, window);
        return true;
    }

    internal static Task WriteModelNotFoundAsync(HttpContext context, string modelName) =>
        OpenAiError.WriteAsync(
            context, StatusCodes.Status404NotFound,
            $"Model '{modelName}' is not registered.", "invalid_request_error", "model_not_found");

    internal static Task WriteNotReadyAsync(HttpContext context, string modelName, string state)
    {
        context.Response.Headers["Retry-After"] = "30";
        return OpenAiError.WriteAsync(
            context, StatusCodes.Status503ServiceUnavailable,
            NotReadyMessage(modelName, state), "service_unavailable_error", "model_not_ready");
    }

    private static string NotReadyMessage(string modelName, string state) => $"Model '{modelName}' is not ready (state: {state}).";

    /// <summary>
    /// Starts a Stopped/Failed model and waits until it settles. False when no start was made (memory refused, or another
    /// start held the node until the deadline): the caller keeps what it resolved; true: re-resolve.
    /// </summary>
    private static async Task<bool> StartAndWaitAsync(
        string modelId, ModelState state, IModelRuntime runtime, MirbadLLMOptions options, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + options.StartupTimeout;
        bool needsStart = state is ModelState.Stopped or ModelState.Failed;
        while (needsStart)
        {
            try
            {
                await runtime.StartAsync(modelId, force: false, ct);
                needsStart = false;
            }
            catch (ModelMemoryAdmissionException)
            {
                return false;
            }
            catch (ModelStartConflictException) when (DateTimeOffset.UtcNow < deadline)
            {
                // Another model is loading; starts run one at a time. Queue behind it.
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (ModelStartConflictException)
            {
                return false;
            }
        }

        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await runtime.GetStatusAsync(modelId, ct);
            if (status.State is ModelState.Ready or ModelState.Failed or ModelState.Stopped)
            {
                break;
            }

            await Task.Delay(AutoStartPollInterval, ct);
        }

        return true;
    }

    private enum BodyOutcome
    {
        Ok,
        TooLarge,
        Busy,
    }

    /// <summary>
    /// Raises the server's request-body limit for this request to the router's own (MN-047): Kestrel's default
    /// (30,000,000 bytes) is below <see cref="Protocol.MaxProxyRequestBodyBytes"/> (64 MiB), so bodies between the two were
    /// refused before the router read them (MN-046), chunked ones even earlier because Kestrel counts the chunk framing
    /// too. The server limit is the router's plus <see cref="BodyLimitHeadroomBytes"/> (the agent's rule, MN-010), so the
    /// router itself enforces the exact 64 MiB on the decoded body and answers its OpenAI-shaped 413. Only the proxied
    /// <c>/v1</c> routes call this; every other endpoint keeps Kestrel's default.
    /// </summary>
    internal static void RaiseBodyLimit(HttpContext context)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes + BodyLimitHeadroomBytes;
        }
    }

    /// <summary>Server-limit headroom above the router's body limit for HTTP framing (chunk headers), as on the agent.</summary>
    internal const long BodyLimitHeadroomBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Reads the request body into one buffer (design §7.2): with a <c>Content-Length</c> the buffer is allocated once at
    /// that size; a chunked body grows by doubling. The upload budget is taken before the bytes are buffered.
    /// </summary>
    private static async Task<(ArraySegment<byte> Body, BodyOutcome Outcome)> ReadBodyAsync(HttpContext context, UploadBudget.Lease lease, CancellationToken ct)
    {
        var request = context.Request;
        if (context.Features.Get<IHttpRequestBodyDetectionFeature>() is { CanHaveBody: false } || request.ContentLength == 0)
        {
            return (ArraySegment<byte>.Empty, BodyOutcome.Ok);
        }

        if (request.ContentLength is { } declared)
        {
            if (declared > MaxBodyBytes)
            {
                return (default, BodyOutcome.TooLarge);
            }

            if (!await lease.EnsureAsync(declared, ct))
            {
                return (default, BodyOutcome.Busy);
            }

            var exact = new byte[declared];
            int filled = 0;
            int read;
            while (filled < exact.Length && (read = await request.Body.ReadAsync(exact.AsMemory(filled), ct)) > 0)
            {
                filled += read;
            }

            return (new ArraySegment<byte>(exact, 0, filled), BodyOutcome.Ok);
        }

        if (!await lease.EnsureAsync(InitialChunkedBufferBytes, ct))
        {
            return (default, BodyOutcome.Busy);
        }

        var buffer = new byte[InitialChunkedBufferBytes];
        int length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length > MaxBodyBytes)
                {
                    return (default, BodyOutcome.TooLarge);
                }

                // One byte past the limit is enough to tell "too large" apart from "exactly at the limit".
                int size = (int)Math.Min((long)buffer.Length * 2, MaxBodyBytes + 1L);
                if (!await lease.EnsureAsync(size, ct))
                {
                    return (default, BodyOutcome.Busy);
                }

                Array.Resize(ref buffer, size);
            }

            int read = await request.Body.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
            {
                return (new ArraySegment<byte>(buffer, 0, length), BodyOutcome.Ok);
            }

            length += read;
        }
    }

    private static string? TryReadModelName(ArraySegment<byte> body)
    {
        if (body.Count == 0)
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(body);
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("model", out var modelProperty) &&
                modelProperty.ValueKind == JsonValueKind.String)
            {
                return modelProperty.GetString();
            }
        }
        catch (JsonException)
        {
            // Not a JSON object, or malformed; treated as naming no model.
        }

        return null;
    }

    private static async Task<bool> ProxyRequestAsync(
        HttpContext context, ModelEndpoint endpoint, string path, ArraySegment<byte> body, UploadBudget.Lease lease,
        IHttpClientFactory httpClientFactory, CancellationToken ct)
    {
        var targetUri = new Uri(endpoint.BaseUri, $"v1/{path}{context.Request.QueryString}");
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);
        if (body.Count > 0)
        {
            upstreamRequest.Content = new ByteArrayContent(body.Array!, body.Offset, body.Count);
        }

        CopyRequestHeaders(context.Request.Headers, upstreamRequest, dropProtocolHeaders: false);

        var client = httpClientFactory.CreateClient(RouterExtensions.UpstreamHttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            await OpenAiError.WriteAsync(
                context, StatusCodes.Status502BadGateway,
                $"Upstream request failed: {ex.Message}", "api_error", "upstream_unavailable");
            return false;
        }

        using (response)
        {
            lease.Dispose(); // the body is with the upstream now
            context.Response.StatusCode = (int)response.StatusCode;
            CopyResponseHeaders(response, context.Response.Headers);
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
            await CopyStreamAsync(upstreamStream, context.Response, response, ct);
            return response.IsSuccessStatusCode;
        }
    }

    private static void CopyRequestHeaders(IHeaderDictionary source, HttpRequestMessage destination, bool dropProtocolHeaders)
    {
        foreach (var header in source)
        {
            if (HopByHopHeaders.Contains(header.Key) ||
                string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase) ||
                // A client must not be able to set the master↔agent protocol headers (MirbadLLM-Protocol, -Master, …).
                (dropProtocolHeaders && (Protocol.LegacyHeaderOf(header.Key) is not null || Protocol.CurrentHeaderOf(header.Key) is not null)))
            {
                continue;
            }

            var values = header.Value.ToArray();
            if (!destination.Headers.TryAddWithoutValidation(header.Key, values) && destination.Content is not null)
            {
                destination.Content.Headers.TryAddWithoutValidation(header.Key, values);
            }
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage response, IHeaderDictionary destination)
    {
        foreach (var header in response.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                destination[header.Key] = new StringValues(header.Value.ToArray());
            }
        }

        foreach (var header in response.Content.Headers)
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                destination[header.Key] = new StringValues(header.Value.ToArray());
            }
        }
    }

    /// <summary>
    /// Await-write-then-flush per read (backpressure: a slow client slows the upstream read), 16 KiB buffer (design §7.2). A
    /// streamed answer (<c>text/event-stream</c>) goes through <see cref="SseReasoningNullFilter"/>, which can change its
    /// length, so its Content-Length is dropped.
    /// </summary>
    private static async Task CopyStreamAsync(Stream source, HttpResponse destination, HttpResponseMessage upstream, CancellationToken ct)
    {
        var filter = string.Equals(upstream.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase)
            ? new SseReasoningNullFilter()
            : null;
        if (filter is not null)
        {
            destination.ContentLength = null;
        }

        var buffer = new byte[Protocol.ProxyCopyBufferBytes];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (filter is null)
            {
                await destination.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            else if (filter.Process(buffer.AsSpan(0, read)) is { Length: > 0 } lines)
            {
                await destination.Body.WriteAsync(lines, ct);
            }

            await destination.Body.FlushAsync(ct);
        }

        if (filter?.Flush() is { Length: > 0 } rest)
        {
            await destination.Body.WriteAsync(rest, ct);
            await destination.Body.FlushAsync(ct);
        }
    }
}

/// <summary>
/// The seam for a route that accepts another API's request and serves it through <see cref="ProxyHandler"/>: set as a
/// request feature before <see cref="ProxyHandler.HandleAsync"/> runs, it is called once the body was read.
/// </summary>
internal interface IProxyRequestTranslation
{
    /// <summary>The <c>/v1</c> path (without the prefix) and body to continue with; null when it wrote the answer itself.</summary>
    ValueTask<(string Path, ArraySegment<byte> Body)?> TranslateAsync(HttpContext context, ArraySegment<byte> body, CancellationToken ct);
}
