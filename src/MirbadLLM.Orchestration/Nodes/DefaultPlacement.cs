using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Nodes;

/// <summary>
/// Design §5.6: a model created without a placement (<c>POST /api/models</c>, the model editor's create path) goes to
/// the only node when exactly one exists; with several nodes the caller must choose (400
/// <c>placement_required</c>). Added by MN-090 so a P1 master never creates an unplaced model by omission (an
/// unplaced model cannot start through <c>RemoteModelRuntime</c>).
/// </summary>
public static class DefaultPlacement
{
    /// <summary>
    /// The placement for a new model that has none: the single non-revoked node in <paramref name="nodes"/>; the
    /// bootstrap node when the registry is not composed yet (until MN-040 registers <see cref="INodeRegistry"/>) or
    /// holds no node yet (first boot, before the bootstrap node record is written); null when several nodes exist and
    /// the caller must ask for one.
    /// </summary>
    public static async Task<ModelPlacement?> ResolveAsync(INodeRegistry? nodes, string bootstrapNodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapNodeId);
        if (nodes is null)
        {
            return new ModelPlacement(bootstrapNodeId);
        }

        var all = await nodes.ListAsync(ct).ConfigureAwait(false);

        // The fleet's members decide (approved, non-revoked; W9 review MN-132, D102): a node waiting for approval does not
        // make a create on the single DGX ask for a placement. Before any approval (first boot) the non-revoked nodes do.
        var members = NodeFleet.Members(all).ToList();
        var live = members.Count > 0 ? members : all.Where(n => !n.Revoked).ToList();
        return live.Count switch
        {
            0 => new ModelPlacement(bootstrapNodeId),
            1 => new ModelPlacement(live[0].Id),
            _ => null,
        };
    }

    /// <summary>
    /// <paramref name="model"/> with a default placement when it has none (see <see cref="ResolveAsync"/>); null when a
    /// placement is required and missing.
    /// </summary>
    public static async Task<ModelDefinition?> ApplyAsync(ModelDefinition model, INodeRegistry? nodes, string bootstrapNodeId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Placement is not null)
        {
            return model;
        }

        return await ResolveAsync(nodes, bootstrapNodeId, ct).ConfigureAwait(false) is { } placement
            ? model with { Placement = placement }
            : null;
    }
}
