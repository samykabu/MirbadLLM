using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>
/// <see cref="IChatClient"/> implementation used by the browser Chat page. Resolves the served model, streams an
/// OpenAI-shaped <c>chat/completions</c> request from it and records the request in <see cref="RouterStats"/> so browser
/// chats show up in router counters alongside proxied traffic.
/// <para>With an <see cref="AgentEndpointResolver"/> registered (multi-node master, design §7.2) the request goes through
/// the placed node's agent data client (HTTP/2 exact, mTLS); an answer marked as an agent error becomes an
/// <see cref="InvalidOperationException"/> with the router's client text, and <c>model_not_running</c> refreshes the status
/// cache. Otherwise it resolves through <see cref="IModelEndpointResolver"/> and uses the router's upstream
/// <see cref="HttpClient"/> (old path, until MN-040).</para>
/// </summary>
public sealed class RouterChatClient(
    IHttpClientFactory httpClientFactory,
    IModelEndpointResolver resolver,
    RouterStats stats,
    IMaintenanceMode? maintenance = null,
    AgentEndpointResolver? agentResolver = null,
    IModelRegistry? registry = null,
    Microsoft.Extensions.Options.IOptions<RouterMaintenanceOptions>? maintenanceOptions = null) : IChatClient
{
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(5);

    private bool NodeScoped => maintenanceOptions?.Value.NodeScoped ?? false;

    public async IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Same precedence as /v1 (MN-110): a global window blocks every chat; a node-scoped one only the models it pauses.
        if (RouterMaintenance.GateWindow(maintenance, NodeScoped) is { } window)
        {
            throw new InvalidOperationException(window.Describe(DateTimeOffset.UtcNow) + ".");
        }

        if (agentResolver is not null)
        {
            await foreach (var chunk in StreamThroughAgentAsync(request, agentResolver, ct))
            {
                yield return chunk;
            }

            yield break;
        }

        var endpoint = await resolver.ResolveAsync(request.ServedName, ct);
        if (endpoint is not null)
        {
            await ThrowIfPausedAsync(endpoint.ModelId, ct);
        }

        if (endpoint is null || endpoint.State != ModelState.Ready)
        {
            var state = endpoint?.State.ToString() ?? "not registered";
            throw new InvalidOperationException($"Model '{request.ServedName}' is not ready (state: {state}).");
        }

        var client = httpClientFactory.CreateClient(RouterExtensions.UpstreamHttpClientName);
        var targetUri = new Uri(endpoint.BaseUri, "v1/chat/completions");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, targetUri)
        {
            Content = JsonContent.Create(BuildBody(request)),
        };

        stats.RequestStarted(endpoint.ServedName);
        var failed = true;
        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Upstream returned {(int)response.StatusCode} for model '{request.ServedName}'.");
            }

            var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var chunk in ReadChunksAsync(upstreamStream, ct))
            {
                yield return chunk;
            }

            failed = false;
        }
        finally
        {
            response?.Dispose();
            stats.RequestCompleted(endpoint.ServedName, failed);
        }
    }

    private async IAsyncEnumerable<ChatChunk> StreamThroughAgentAsync(
        ChatRequest request, AgentEndpointResolver agent, [EnumeratorCancellation] CancellationToken ct)
    {
        var route = await agent.ResolveRouteAsync(request.ServedName, ct);
        if (route.ModelId is { } pausedCandidate)
        {
            await ThrowIfPausedAsync(pausedCandidate, ct);
        }

        if (route.Outcome != AgentRouteOutcome.Ready)
        {
            throw new InvalidOperationException(RouteMessage(request.ServedName, route));
        }

        string modelId = route.ModelId!;
        string nodeId = route.NodeId!;
        IAgentDataClient data;
        try
        {
            data = agent.GetDataClient(nodeId);
        }
        catch (KeyNotFoundException ex)
        {
            throw new InvalidOperationException($"Node '{nodeId}' of model '{request.ServedName}' has no agent connection.", ex);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, data.BuildProxyUri(modelId, "v1/chat/completions"))
        {
            Content = JsonContent.Create(BuildBody(request)),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        stats.RequestStarted(route.ServedName);
        var failed = true;
        HttpResponseMessage? response = null;
        try
        {
            try
            {
                response = await data.SendAsync(httpRequest, ct);
            }
            catch (AgentUnreachableException ex)
            {
                throw new InvalidOperationException($"Node '{nodeId}' could not be reached: {ex.Message}", ex);
            }

            // Disposed in finally, translated errors included: the node's in-flight slot is released on dispose.
            if (response.Headers.TryGetValues(Protocol.AgentErrorHeader, out var codes))
            {
                string code = codes.FirstOrDefault() ?? ErrorCodes.InternalError;
                var translation = ErrorMappings.TranslateProxyError(code);
                if (translation.ClientCode == ErrorCodes.ModelNotReady)
                {
                    await RefreshQuietlyAsync(agent, modelId, ct);
                    throw new InvalidOperationException($"Model '{request.ServedName}' is not ready (state: {ModelState.Stopped}).");
                }

                throw new InvalidOperationException(
                    $"Node '{nodeId}' refused the request for model '{request.ServedName}' ({translation.ClientCode}).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Upstream returned {(int)response.StatusCode} for model '{request.ServedName}'.");
            }

            var upstreamStream = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var chunk in ReadChunksAsync(upstreamStream, ct))
            {
                yield return chunk;
            }

            failed = false;
        }
        finally
        {
            response?.Dispose();
            stats.RequestCompleted(route.ServedName, failed);
        }
    }

    private async Task ThrowIfPausedAsync(string modelId, CancellationToken ct)
    {
        if (await RouterMaintenance.ForModelAsync(maintenance, NodeScoped, registry, modelId, ct) is { } window)
        {
            throw new InvalidOperationException(window.Describe(DateTimeOffset.UtcNow) + ".");
        }
    }

    private static string RouteMessage(string servedName, AgentModelRoute route) => route.Outcome switch
    {
        AgentRouteOutcome.NotFound => $"Model '{servedName}' is not ready (state: not registered).",
        AgentRouteOutcome.Unplaced => $"Model '{servedName}' is not placed on a node.",
        AgentRouteOutcome.NodeOffline => $"Node '{route.NodeId}' of model '{servedName}' is offline.",
        AgentRouteOutcome.NodeIncompatible => $"Node '{route.NodeId}' of model '{servedName}' speaks no protocol version this master supports.",
        _ => $"Model '{servedName}' is not ready (state: {route.ModelState}).",
    };

    private static async Task RefreshQuietlyAsync(AgentEndpointResolver agent, string modelId, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RefreshTimeout);
        try
        {
            await agent.RefreshModelAsync(modelId, timeout.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Best effort: the node monitor catches up on its own.
        }
    }

    private static async IAsyncEnumerable<ChatChunk> ReadChunksAsync(Stream upstreamStream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(upstreamStream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].TrimStart();
            if (data.Length == 0)
            {
                continue;
            }

            if (data == "[DONE]")
            {
                break;
            }

            var chunk = TryParseChunk(data);
            if (chunk is not null)
            {
                yield return chunk;
            }
        }
    }

    private static object BuildBody(ChatRequest request) => new
    {
        model = request.ServedName,
        messages = request.Messages.Select(m => new { role = m.Role, content = Content(m) }),
        temperature = request.Temperature,
        max_tokens = request.MaxTokens,
        stream = true,
        stream_options = new { include_usage = true },
        chat_template_kwargs = new { enable_thinking = request.EnableThinking },
    };

    /// <summary>The OpenAI content: the text alone, or text and image parts when the message carries images.</summary>
    public static object Content(ChatMessage message)
    {
        if (message.Images is not { Count: > 0 } images)
        {
            return message.Content;
        }

        List<object> parts = message.Content.Length == 0 ? [] : [new { type = "text", text = message.Content }];
        parts.AddRange(images.Select(url => new { type = "image_url", image_url = new { url } }));
        return parts;
    }

    private static ChatChunk? TryParseChunk(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            string? content = null;
            string? reasoning = null;
            string? finishReason = null;
            ChatUsage? usage = null;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    content = GetString(delta, "content");
                    reasoning = GetString(delta, "reasoning_content") ?? GetString(delta, "reasoning");
                }

                finishReason = GetString(choice, "finish_reason");
            }

            if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
            {
                var promptTokens = GetInt(usageElement, "prompt_tokens");
                var completionTokens = GetInt(usageElement, "completion_tokens");
                usage = new ChatUsage(promptTokens, completionTokens);
            }

            if (content is null && reasoning is null && finishReason is null && usage is null)
            {
                return null;
            }

            return new ChatChunk(content, reasoning, usage, finishReason);
        }
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;
}
