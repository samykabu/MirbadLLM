using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Nodes;

/// <summary>
/// Which registered nodes make the master a multi-node fleet (W9 review MN-132, D102): the <b>approved, non-revoked</b>
/// ones. A node that enrolled but waits for approval (or was never approved) is not part of the fleet: it serves nothing,
/// takes no placement and cannot be the target of a move, so it must not switch on any P2 behaviour on the single DGX —
/// the automatic node-scoped maintenance (D101), the stale-instance sweep (D92), the editor's Placement section and the
/// default placement of a new model all count members only.
/// </summary>
public static class NodeFleet
{
    /// <summary>Fewest members for which the master behaves as a multi-node fleet.</summary>
    public const int MultiNodeMinimum = 2;

    /// <summary>True for an approved, non-revoked node.</summary>
    public static bool IsMember(NodeRecord node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return node.Approved && !node.Revoked;
    }

    /// <summary>The members of <paramref name="nodes"/>.</summary>
    public static IEnumerable<NodeRecord> Members(IEnumerable<NodeRecord> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return nodes.Where(IsMember);
    }

    /// <summary>True when <paramref name="nodes"/> holds at least <see cref="MultiNodeMinimum"/> members.</summary>
    public static bool IsMultiNode(IEnumerable<NodeRecord> nodes) => Members(nodes).Take(MultiNodeMinimum).Count() >= MultiNodeMinimum;

    /// <summary>True when the registry holds at least <see cref="MultiNodeMinimum"/> members.</summary>
    public static async Task<bool> IsMultiNodeAsync(INodeRegistry nodes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return IsMultiNode(await nodes.ListAsync(ct).ConfigureAwait(false));
    }
}
