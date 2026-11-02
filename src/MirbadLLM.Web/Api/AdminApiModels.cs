using System.Text.Json.Serialization;
using MirbadLLM.Core;

namespace MirbadLLM.Web.Api;

/// <summary>One row of <see cref="StatusResponse"/>: a model's definition, current runtime status and (if any) live metrics.</summary>
public sealed record ModelStatusView(ModelDefinition Definition, ModelStatus Status, ModelMetrics? Metrics);

/// <summary>Shape returned by <c>GET /api/status</c>: host memory, every model's definition/status/metrics, and router counters.</summary>
public sealed record StatusResponse(MemorySnapshot Memory, IReadOnlyList<ModelStatusView> Models, IReadOnlyList<RouterModelStats> Router)
{
    /// <summary>
    /// The non-revoked nodes (design §5.6 "adds <c>nodes: [...]</c>", D106 follow-up, MN-229); additive: every P2 field
    /// stays. Null (not written) when the master composes no node registry.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StatusNodeView>? Nodes { get; init; }
}

/// <summary>
/// One node in <see cref="StatusResponse.Nodes"/>: the registry record's id, name and flags with the live state, reasons and
/// memory (<see cref="INodeStatusCache"/>; null state when the cache does not know the node yet). <c>/api/nodes</c> has
/// the full view.
/// </summary>
public sealed record StatusNodeView(
    string Id,
    string? DisplayName,
    bool Approved,
    bool Draining,
    NodeState? State,
    IReadOnlyList<string> Reasons,
    NodeMemorySnapshot? Memory);
