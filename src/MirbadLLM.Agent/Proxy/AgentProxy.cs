using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Proxy;

/// <summary>
/// The agent's inference proxy (design §7.2, §7.3, §7.4): forwards <c>/agent/v1/proxy/{modelId}/{**path}</c> from the
/// master's mTLS data channel to the model's loopback/node-network port.
/// <list type="bullet">
/// <item>Allow-listed paths only (<see cref="AgentProxyOptions.AllowedPaths"/>), else 404 <c>not_found</c>.</item>
/// <item>Bounded: at most <see cref="AgentProxyOptions.MaxInFlight"/> requests, else 503 <c>server_busy</c> (<c>Retry-After</c>).</item>
/// <item>The request body is <b>streamed</b> to the model (<see cref="StreamContent"/> over <c>Request.Body</c>), never
/// buffered again; over <see cref="AgentProxyOptions.MaxRequestBodyBytes"/> → 413.</item>
/// <item>Header rules as the master's <c>ProxyHandler</c>: hop-by-hop, <c>Host</c>, <c>Authorization</c> and
/// <c>Content-Length</c> are not copied (the length is set on the content); the agent protocol headers
/// (<c>MirbadLLM-*</c>) and <c>Expect</c> are not forwarded either.</item>
/// <item>Upstream key (design-p3p4 §A.8, MN-222): when a runtime driver answers a key for the model
/// (<see cref="IRuntimeDriver.GetUpstreamApiKeyAsync"/>: a native llama-server's per-instance <c>LLAMA_API_KEY</c>), the
/// request carries <c>Authorization: Bearer {key}</c> instead of the client's header. Container instances have no key, so
/// their requests are unchanged. The key is never logged.</item>
/// <item>Body rewrite (docs/macos/implementation-plan.md, Phase 3): when a driver answers one for the model
/// (<see cref="IRuntimeDriver.GetUpstreamBodyRewriteAsync"/>: an MLX server), a JSON object body is read (still bounded by
/// <see cref="AgentProxyOptions.MaxRequestBodyBytes"/>), its <c>model</c> set and the named fields removed, and the result
/// sent with its own length. Every other model's body is streamed unchanged.</item>
/// <item>Not running, or no response from the model at all (refused, reset, connect timeout) → 503
/// <c>model_not_running</c> in the agent error shape, always <b>before</b> any response header is sent.</item>
/// <item>Once the model answered, its status, headers and body pass through unchanged (vLLM's own 4xx/5xx included);
/// response buffering is off, headers are sent at once and every read is written then flushed (16 KiB buffer):
/// SSE events arrive per chunk, and a slow reader slows the upstream read (backpressure).</item>
/// <item>A failure after the response started is never turned into JSON: the client stream is aborted
/// (<see cref="HttpContext.Abort"/>, an RST_STREAM on HTTP/2), as a dying vLLM does today.</item>
/// <item>A client abort (<see cref="HttpContext.RequestAborted"/>) cancels the upstream request; the upstream handler
/// does not drain (<see cref="SocketsHttpHandler.MaxResponseDrainSize"/> = 0), so the model sees the disconnect at once.</item>
/// </list>
/// </summary>
internal sealed partial class AgentProxy : IDisposable
{
    private static readonly FrozenSet<string> SkippedRequestHeaders = new[]
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Trailers", "Transfer-Encoding", "Upgrade", "Host", "Authorization", "Content-Length", "Expect",
        Protocol.ProtocolHeader, Protocol.MasterHeader, Protocol.RequestTimeHeader,
        Protocol.LegacyHeaderOf(Protocol.ProtocolHeader)!, Protocol.LegacyHeaderOf(Protocol.MasterHeader)!, Protocol.LegacyHeaderOf(Protocol.RequestTimeHeader)!,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SkippedResponseHeaders = new[]
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Trailers", "Transfer-Encoding", "Upgrade",
        Protocol.AgentErrorHeader, // a model can never make its answer look like an agent error (D40)
        Protocol.LegacyHeaderOf(Protocol.AgentErrorHeader)!,
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly IProxyUpstreamResolver _resolver;
    private readonly ILogger<AgentProxy> _logger;
    private readonly AgentProxyOptions _options;
    private readonly ProxyPathPolicy _paths;
    private readonly ProxyInFlightLimiter _limiter;
    private readonly HttpClient _upstream;
    private readonly IRuntimeDriver[] _drivers;

    public AgentProxy(
        IProxyUpstreamResolver resolver, IOptions<AgentProxyOptions> options, ILogger<AgentProxy> logger, IEnumerable<IRuntimeDriver>? drivers = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _resolver = resolver;
        _logger = logger;
        _drivers = drivers?.ToArray() ?? [];
        _options = options.Value;
        if (_options.CopyBufferBytes <= 0 || _options.MaxRequestBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Proxy buffer and body limits must be positive.");
        }

        _paths = new ProxyPathPolicy(_options.AllowedPaths);
        _limiter = new ProxyInFlightLimiter(_options.MaxInFlight);
        _upstream = new HttpClient(CreateUpstreamHandler(_options.UpstreamConnectTimeout), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan, // no overall timeout (design §7.2): generations can run for minutes
        };
    }

    /// <summary>Requests currently proxied (diagnostics and tests).</summary>
    public int InFlight => _limiter.InFlight;

    /// <summary>
    /// The plain-HTTP handler to the model. It never uses an environment proxy, never follows redirects or decompresses,
    /// and does not drain an abandoned response: disposing an unfinished response closes the connection, which is how a
    /// client abort reaches vLLM.
    /// </summary>
    internal static SocketsHttpHandler CreateUpstreamHandler(TimeSpan connectTimeout) => new()
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = connectTimeout,
        MaxResponseDrainSize = 0,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    };

    /// <summary>Handles one proxied request; never throws for client or upstream failures.</summary>
    public async Task HandleAsync(HttpContext context, string modelId, string? path)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(modelId) || !_paths.TryGetUpstreamPath(path, out string upstreamPath))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, ErrorCodes.NotFound,
                $"Path '{path}' is not proxied by this agent.").ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength > _options.MaxRequestBodyBytes)
        {
            await WriteBodyTooLargeAsync(context).ConfigureAwait(false);
            return;
        }

        if (!_limiter.TryAcquire())
        {
            context.Response.Headers.RetryAfter = _options.BusyRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, ErrorCodes.ServerBusy,
                $"The agent is already proxying {_limiter.Limit} requests.").ConfigureAwait(false);
            return;
        }

        try
        {
            await ForwardAsync(context, modelId, upstreamPath).ConfigureAwait(false);
        }
        finally
        {
            _limiter.Release();
        }
    }

    public void Dispose() => _upstream.Dispose();

    private void ArmIdle(CancellationTokenSource idle)
    {
        if (_options.UpstreamIdleTimeout > TimeSpan.Zero)
        {
            idle.CancelAfter(_options.UpstreamIdleTimeout);
        }
    }

    private async Task ForwardAsync(HttpContext context, string modelId, string upstreamPath)
    {
        CancellationToken ct = context.RequestAborted;
        Uri? upstreamBase;
        try
        {
            upstreamBase = await _resolver.ResolveAsync(modelId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // any resolver failure means "cannot reach the instance": answered, not thrown
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogResolveFailed(modelId, ex);
            await WriteNotRunningAsync(context, modelId, "its instance could not be looked up").ConfigureAwait(false);
            return;
        }

        if (upstreamBase is null)
        {
            await WriteNotRunningAsync(context, modelId, "it has no running instance").ConfigureAwait(false);
            return;
        }

        string? apiKey;
        try
        {
            apiKey = await UpstreamApiKeyAsync(modelId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // a failing key lookup means the instance cannot be reached as itself: answered, not thrown
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogKeyLookupFailed(modelId, ex.GetType().Name);
            await WriteNotRunningAsync(context, modelId, "its instance key could not be read").ConfigureAwait(false);
            return;
        }

        UpstreamBodyRewrite? rewrite;
        try
        {
            rewrite = await UpstreamBodyRewriteAsync(modelId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // without its rewrite the request must not reach the instance: answered, not thrown
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogResolveFailed(modelId, ex);
            await WriteNotRunningAsync(context, modelId, "its instance could not be looked up").ConfigureAwait(false);
            return;
        }

        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodyLimit)
        {
            bodyLimit.MaxRequestBodySize = _options.MaxRequestBodyBytes;
        }

        var target = new Uri(upstreamBase, upstreamPath + context.Request.QueryString.ToUriComponent());
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? context.Request.ContentLength > 0)
        {
            var body = new LimitedReadStream(context.Request.Body, _options.MaxRequestBodyBytes);
            if (rewrite is not null)
            {
                byte[]? rewritten;
                try
                {
                    rewritten = await RewriteBodyAsync(body, rewrite, ct).ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (IsBodyTooLarge(ex))
                {
                    await WriteBodyTooLargeAsync(context).ConfigureAwait(false);
                    return;
                }

                if (rewritten is null)
                {
                    await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.InvalidRequest,
                        $"Requests to model '{modelId}' must have a JSON object body (with unique field names).").ConfigureAwait(false);
                    return;
                }

                request.Content = new ByteArrayContent(rewritten);
                request.Content.Headers.ContentLength = rewritten.Length;
            }
            else
            {
                request.Content = new StreamContent(body, _options.CopyBufferBytes);
                request.Content.Headers.ContentLength = context.Request.ContentLength;
            }
        }

        CopyRequestHeaders(context.Request.Headers, request);
        if (apiKey is not null)
        {
            // The client's Authorization was not copied (above); the instance's own key replaces it (§A.8).
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        // Idle limit on the model (not on the client): a wedged engine keeps /health green but never answers (2026-10-18).
        using var idle = new CancellationTokenSource();
        using var upstreamCt = CancellationTokenSource.CreateLinkedTokenSource(ct, idle.Token);
        ArmIdle(idle);
        HttpResponseMessage response;
        try
        {
            response = await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamCt.Token).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            return; // the master (client) went away before the model answered; nothing to write
        }
        catch (Exception) when (idle.IsCancellationRequested)
        {
            LogUpstreamIdle(modelId, _options.UpstreamIdleTimeout.TotalSeconds);
            await WriteErrorAsync(context, StatusCodes.Status504GatewayTimeout, ErrorCodes.Timeout,
                string.Create(CultureInfo.InvariantCulture,
                    $"Model '{modelId}' sent no response for {_options.UpstreamIdleTimeout.TotalSeconds:0} s; it may be stalled or overloaded.")).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (IsBodyTooLarge(ex))
        {
            await WriteBodyTooLargeAsync(context).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Refused, reset or timed out before any response: the instance is not (or no longer) serving (§7.2).
            _resolver.Invalidate(modelId, upstreamBase);
            LogUpstreamUnavailable(modelId, upstreamBase, ex.Message);
            await WriteNotRunningAsync(context, modelId, "its instance did not answer").ConfigureAwait(false);
            return;
        }

        using (response)
        {
            var rewriter = rewrite is { ResponseModel: { Length: > 0 } served }
                ? new ResponseModelRewriter(rewrite.Model, served)
                : null;
            await CopyResponseAsync(context, modelId, response, rewriter, idle, upstreamCt.Token, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The first key a driver answers for <paramref name="modelId"/>; null for containers (no driver has one).</summary>
    private async Task<string?> UpstreamApiKeyAsync(string modelId, CancellationToken ct)
    {
        foreach (var driver in _drivers)
        {
            if (await driver.GetUpstreamApiKeyAsync(modelId, ct).ConfigureAwait(false) is { Length: > 0 } key)
            {
                return key;
            }
        }

        return null;
    }

    /// <summary>The first body rewrite a driver answers for <paramref name="modelId"/>; null for every runtime but MLX.</summary>
    private async Task<UpstreamBodyRewrite?> UpstreamBodyRewriteAsync(string modelId, CancellationToken ct)
    {
        foreach (var driver in _drivers)
        {
            if (await driver.GetUpstreamBodyRewriteAsync(modelId, ct).ConfigureAwait(false) is { } rewrite)
            {
                return rewrite;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the whole (bounded) body; a JSON object gets <c>model</c> = <see cref="UpstreamBodyRewrite.Model"/> and loses
    /// <see cref="UpstreamBodyRewrite.RemovedFields"/>. Null (the request is refused) for anything that is not one JSON object
    /// with unique field names: the server's Python <c>json</c> reads what .NET refuses differently (a repeated
    /// <c>model</c> keeps its last value, <c>NaN</c> is a number), so such a body is never forwarded unrewritten.
    /// </summary>
    internal static async Task<byte[]?> RewriteBodyAsync(Stream body, UpstreamBodyRewrite rewrite, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        System.Text.Json.Nodes.JsonObject json;
        try
        {
            if (buffer.Length == 0 ||
                System.Text.Json.Nodes.JsonNode.Parse(buffer.ToArray()) is not System.Text.Json.Nodes.JsonObject parsed)
            {
                return null;
            }

            _ = parsed.Count; // a JsonObject reads its properties lazily: a repeated name throws here
            json = parsed;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }

        json["model"] = rewrite.Model;
        foreach (string field in rewrite.RemovedFields)
        {
            json.Remove(field);
        }

        return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json);
    }

    private async Task CopyResponseAsync(
        HttpContext context, string modelId, HttpResponseMessage response, ResponseModelRewriter? rewriter,
        CancellationTokenSource idle, CancellationToken upstreamCt, CancellationToken ct)
    {
        context.Response.StatusCode = (int)response.StatusCode;
        CopyResponseHeaders(response, context.Response.Headers);
        if (rewriter is not null)
        {
            // The served name is longer or shorter than the server's: the length is no longer the upstream's.
            context.Response.Headers.ContentLength = null;
        }
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.CopyBufferBytes);
        try
        {
            // Headers go out now (the model sent its own already); for SSE the client sees 200 before the first token.
            await context.Response.StartAsync(ct).ConfigureAwait(false);
            Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (source.ConfigureAwait(false))
            {
                Memory<byte> chunk = buffer.AsMemory(0, _options.CopyBufferBytes);
                int read;
                while (true)
                {
                    ArmIdle(idle);
                    read = await source.ReadAsync(chunk, upstreamCt).ConfigureAwait(false);
                    idle.CancelAfter(Timeout.InfiniteTimeSpan); // a slow client is not an idle model
                    if (read == 0)
                    {
                        break;
                    }

                    // Await write, then flush, then read again: a slow client slows the upstream read (§7.2 backpressure).
                    ReadOnlyMemory<byte> data = rewriter is null ? chunk[..read] : rewriter.Process(chunk.Span[..read]);
                    if (data.Length > 0)
                    {
                        await context.Response.Body.WriteAsync(data, ct).ConfigureAwait(false);
                        await context.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    }
                }

                if (rewriter?.Flush() is { Length: > 0 } rest)
                {
                    await context.Response.Body.WriteAsync(rest, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Client abort: disposing the unfinished response closes the upstream connection (no drain), so vLLM stops.
        }
        catch (Exception) when (idle.IsCancellationRequested)
        {
            LogUpstreamIdle(modelId, _options.UpstreamIdleTimeout.TotalSeconds);
            context.Abort();
        }
#pragma warning disable CA1031 // after the response started nothing can be answered; the stream is aborted instead
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The model died (or the stream broke) mid-response: no JSON after a partial body (§7.4). Abort the stream.
            LogAbortedAfterStart(modelId, ex.Message);
            context.Abort();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CopyRequestHeaders(IHeaderDictionary source, HttpRequestMessage destination)
    {
        foreach (var header in source)
        {
            if (header.Key.StartsWith(':') || SkippedRequestHeaders.Contains(header.Key))
            {
                continue;
            }

            string?[] values = header.Value.ToArray();
            if (!destination.Headers.TryAddWithoutValidation(header.Key, values!) && destination.Content is not null)
            {
                destination.Content.Headers.TryAddWithoutValidation(header.Key, values!);
            }
        }
    }

    private static void CopyResponseHeaders(HttpResponseMessage response, IHeaderDictionary destination)
    {
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (!SkippedResponseHeaders.Contains(header.Key))
            {
                destination[header.Key] = new StringValues(header.Value.ToArray());
            }
        }
    }

    private static bool IsBodyTooLarge(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProxyBodyTooLargeException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
            {
                return true;
            }
        }

        return false;
    }

    private Task WriteBodyTooLargeAsync(HttpContext context) =>
        WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.InvalidRequest,
            $"Request body exceeds the {_options.MaxRequestBodyBytes} byte limit.");

    private static Task WriteNotRunningAsync(HttpContext context, string modelId, string reason) =>
        WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, ErrorCodes.ModelNotRunning,
            $"Model '{modelId}' is not running on this node: {reason}.");

    /// <summary>The agent error shape (design §4.3); only ever written before the response started.</summary>
    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.StatusCode = status;
        context.Response.Headers[Protocol.AgentErrorHeader] = code;
        try
        {
            await context.Response.WriteAsJsonAsync(
                new AgentErrorResponse(new AgentError { Code = code, Message = message }),
                AgentJson.Options,
                AgentJson.JsonContentType,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client is gone; nothing to answer.
        }
    }

    [LoggerMessage(EventId = 1901, Level = LogLevel.Warning, Message = "Proxy: could not resolve the instance of {ModelId}.")]
    private partial void LogResolveFailed(string modelId, Exception exception);

    [LoggerMessage(EventId = 1902, Level = LogLevel.Information, Message = "Proxy: {ModelId} at {Upstream} did not answer ({Reason}); answered model_not_running.")]
    private partial void LogUpstreamUnavailable(string modelId, Uri upstream, string reason);

    [LoggerMessage(EventId = 1904, Level = LogLevel.Warning, Message = "Proxy: the upstream key of {ModelId} could not be read ({Error}); answered model_not_running.")]
    private partial void LogKeyLookupFailed(string modelId, string error);

    [LoggerMessage(EventId = 1905, Level = LogLevel.Warning, Message = "Proxy: {ModelId} sent nothing for {Seconds} s; request ended (the model may be stalled).")]
    private partial void LogUpstreamIdle(string modelId, double seconds);

    [LoggerMessage(EventId = 1903, Level = LogLevel.Warning, Message = "Proxy: {ModelId} failed after the response started ({Reason}); client stream aborted.")]
    private partial void LogAbortedAfterStart(string modelId, string reason);
}
