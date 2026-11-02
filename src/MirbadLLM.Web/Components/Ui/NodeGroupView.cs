using MirbadLLM.Core;

namespace MirbadLLM.Web.Components.Ui;

/// <summary>
/// One node's group on the Dashboard (design §8.3): the head line (name, state, Ready count, last seen, reasons) and the
/// node's memory headroom. Built by the Dashboard from the node registry, the status cache and the model list.
/// </summary>
public sealed record NodeGroupView
{
    /// <summary>Node id; null for the group of models that are not placed on any node.</summary>
    public string? Id { get; init; }

    /// <summary>Display name (or the id).</summary>
    public required string Name { get; init; }

    /// <summary>Node state; null for the unplaced group and for a placement naming a node the master does not know.</summary>
    public NodeState? State { get; init; }

    /// <summary>Operator draining flag.</summary>
    public bool Draining { get; init; }

    /// <summary>Why the node is Degraded / Incompatible / Offline / Pending takeover.</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Last event or successful call.</summary>
    public DateTimeOffset? LastContact { get; init; }

    /// <summary>Models in the group (all of them, not only the current page).</summary>
    public int ModelCount { get; init; }

    /// <summary>Models in the group that read Ready.</summary>
    public int ReadyCount { get; init; }

    /// <summary>Memory model of the node; null when unknown.</summary>
    public MemoryModel? MemoryModel { get; init; }

    /// <summary>Host memory total; null when the node sent no memory reading.</summary>
    public double? TotalGiB { get; init; }

    /// <summary>Host memory available; null when the node sent no memory reading.</summary>
    public double? AvailableGiB { get; init; }

    /// <summary>Budgets of the group's Starting, Ready and Stopping models (the unified bar's models segment).</summary>
    public double ModelsGiB { get; init; }

    /// <summary>The node's memory reserve (unified pool, or host RAM on a discrete node).</summary>
    public double ReserveGiB { get; init; }

    /// <summary>Per-GPU headroom of a discrete node; empty on unified nodes.</summary>
    public IReadOnlyList<GpuHeadroom> Gpus { get; init; } = [];

    public string? Os { get; init; }

    public double? GpuUtilization { get; init; }

    public IReadOnlyList<string> Runtimes { get; init; } = [];

    /// <summary>True for a node the registry or cache knows (false for the unplaced group and unknown placements).</summary>
    public bool Known => Id is not null && State is not null;

    /// <summary>
    /// True when the model states shown are the cache's last view rather than live ones: a known node that is not serving
    /// (Offline, Incompatible, Pending takeover; design §3.5 "last-known state greyed with last seen").
    /// </summary>
    public bool IsStale => Known && State is not (NodeState.Online or NodeState.Degraded);
}
