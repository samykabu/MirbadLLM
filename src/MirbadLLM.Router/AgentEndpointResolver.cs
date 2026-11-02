using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>What the router may do with a request for a served name (design §7.1).</summary>
public enum AgentRouteOutcome
{
    /// <summary>The model is Ready on a serving node: proxy through the node's agent.</summary>
    Ready,

    /// <summary>No model has that served name: 404 <c>model_not_found</c> (unchanged).</summary>
    NotFound,

    /// <summary>The model has no placement: 503 <c>model_unplaced</c>.</summary>
    Unplaced,

    /// <summary>The model's node is not serving (Offline, pending approval or takeover, revoked, unknown): 503 <c>node_offline</c>.</summary>
    NodeOffline,

    /// <summary>The model's node shares no protocol version with the master: 503 <c>node_incompatible</c>.</summary>
    NodeIncompatible,

    /// <summary>The node serves but the instance is not Ready: 503 <c>model_not_ready</c>, or auto-start.</summary>
    NotReady,
}

/// <summary>A resolved served name (design §7.1), read from memory: the registry's committed placement and the status cache.</summary>
/// <param name="Outcome">What the router does with it.</param>
/// <param name="ServedName">The served name as the registry spells it (the request's name for <see cref="AgentRouteOutcome.NotFound"/>).</param>
/// <param name="ModelId">Model id; null for <see cref="AgentRouteOutcome.NotFound"/>.</param>
/// <param name="NodeId">Placed node; null when not found or unplaced.</param>
/// <param name="ModelState">Instance state on the placed node (Stopped when the node reports none).</param>
/// <param name="NodeState">Node state; null when not found, unplaced or unknown to the cache.</param>
public sealed record AgentModelRoute(
    AgentRouteOutcome Outcome,
    string ServedName,
    string? ModelId,
    string? NodeId,
    ModelState ModelState,
    NodeState? NodeState);

/// <summary>
/// The router's resolver for the multi-node master (design §7.1): a served name resolves against the model registry
/// (existence, committed placement) and the runtime status cache (<see cref="INodeStatusCache"/>: node and instance state),
/// never calling an agent per request. Traffic goes over the placed node's data client
/// (<see cref="IAgentDataClient"/>, <c>https://{agent}/agent/v1/proxy/{modelId}/v1/…</c>).
/// <para>Also an <see cref="IModelEndpointResolver"/> for <c>GET /v1/models</c>, the Chat and Connect pages: a model is
/// Ready there only when its route is <see cref="AgentRouteOutcome.Ready"/>. <see cref="ModelEndpoint.BaseUri"/> is the
/// model's agent proxy base (informational; requests must use <see cref="GetDataClient"/>).</para>
/// <para>The master composition registers this class (<see cref="RouterExtensions.AddMirbadLLMAgentRouting"/>, MN-040);
/// <c>ProxyHandler</c> and <see cref="RouterChatClient"/> take the agent path whenever it is registered, else the upstream
/// <see cref="HttpClient"/> to the resolved base URI (router tests with a fake resolver). The pre-P1
/// <c>ModelEndpointResolver</c> was deleted by MN-047 (D60).</para>
/// </summary>
public sealed class AgentEndpointResolver(
    IModelRegistry registry,
    INodeStatusCache cache,
    IAgentClientFactory clients) : IModelEndpointResolver
{
    /// <summary>Resolves a served name (case-insensitive, as today) to its route.</summary>
    public async Task<AgentModelRoute> ResolveRouteAsync(string servedName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(servedName);
        var model = await FindAsync(servedName, ct).ConfigureAwait(false);
        return model is null
            ? new AgentModelRoute(AgentRouteOutcome.NotFound, servedName, null, null, ModelState.Stopped, null)
            : Route(model);
    }

    /// <summary>The instance state of <paramref name="modelId"/> on <paramref name="nodeId"/> as the cache has it now (Stopped when none).</summary>
    public ModelState GetModelState(string modelId, string nodeId) =>
        cache.GetRuntime(modelId) is { } runtime && string.Equals(runtime.NodeId, nodeId, StringComparison.Ordinal)
            ? runtime.State
            : ModelState.Stopped;

    /// <summary>
    /// The data client of the node's current handler generation; get one per request (never cache it across a handler
    /// rebuild). Throws <see cref="KeyNotFoundException"/> when the factory has no client for the node.
    /// </summary>
    public IAgentDataClient GetDataClient(string nodeId) => clients.GetClient(nodeId).Data;

    /// <summary>Re-reads one model's instance from its agent (after the agent answered <c>model_not_running</c>, design §7.1).</summary>
    public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => cache.RefreshModelAsync(modelId, ct);

    /// <inheritdoc />
    public async Task<ModelEndpoint?> ResolveAsync(string servedName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(servedName);
        var model = await FindAsync(servedName, ct).ConfigureAwait(false);
        return model is null ? null : ToEndpoint(model, Route(model));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelEndpoint>> ListAsync(CancellationToken ct = default)
    {
        var models = await registry.ListAsync(ct).ConfigureAwait(false);
        return [.. models.Select(m => ToEndpoint(m, Route(m)))];
    }

    private async Task<ModelDefinition?> FindAsync(string servedName, CancellationToken ct)
    {
        var models = await registry.ListAsync(ct).ConfigureAwait(false);
        return models.FirstOrDefault(m => string.Equals(m.ServedName, servedName, StringComparison.OrdinalIgnoreCase));
    }

    private AgentModelRoute Route(ModelDefinition model)
    {
        if (model.Placement is not { } placement)
        {
            return new AgentModelRoute(AgentRouteOutcome.Unplaced, model.ServedName, model.Id, null, ModelState.Stopped, null);
        }

        var node = cache.GetNode(placement.NodeId);
        var state = GetModelState(model.Id, placement.NodeId);
        var outcome = node switch
        {
            { IsServing: true } => state == ModelState.Ready ? AgentRouteOutcome.Ready : AgentRouteOutcome.NotReady,
            { State: NodeState.Incompatible } => AgentRouteOutcome.NodeIncompatible,
            _ => AgentRouteOutcome.NodeOffline,
        };
        return new AgentModelRoute(outcome, model.ServedName, model.Id, placement.NodeId, state, node?.State);
    }

    private ModelEndpoint ToEndpoint(ModelDefinition model, AgentModelRoute route)
    {
        // Never Ready unless traffic can flow: a last-known Ready instance on a node that is not serving shows as Stopped.
        var state = route.Outcome == AgentRouteOutcome.Ready ? ModelState.Ready
            : route.ModelState == ModelState.Ready ? ModelState.Stopped
            : route.ModelState;
        return new ModelEndpoint(model.Id, model.ServedName, ProxyBase(model.Id, route.NodeId), state);
    }

    private Uri ProxyBase(string modelId, string? nodeId)
    {
        if (nodeId is not null)
        {
            try
            {
                return GetDataClient(nodeId).BuildProxyUri(modelId, "");
            }
            catch (KeyNotFoundException)
            {
                // No client for the node (not enrolled yet, revoked): fall through to a placeholder.
            }
        }

        return new Uri($"urn:mirbadllm:model:{Uri.EscapeDataString(modelId)}");
    }
}
