using MirbadLLM.Core;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.Web.Components.Ui;

/// <summary>
/// The fleet's nodes as the Overview and the Models list read them: the node registry and the status cache merged (the
/// Nodes page's merge: the live view wins, the record fills in), and one node's headroom built from its models.
/// Moved out of the Dashboard so the two pages read the same nodes the same way.
/// </summary>
public static class FleetNodes
{
    /// <summary>The node rows plus the registry records by id (for per-GPU reserves) and a read error, if any.</summary>
    public sealed record Reading(List<Nodes.NodeRow> Rows, Dictionary<string, NodeRecord> Records, string? Error);

    /// <summary>
    /// Nodes that hold (or can hold) models: every non-revoked node except those still pending approval, by name then id.
    /// No node services at all (a single-box composition) reads as no nodes.
    /// </summary>
    public static async Task<Reading> ReadAsync(
        INodeStatusCache? cache, INodeRegistry? registry, IReadOnlyList<ModelDefinition> definitions, CancellationToken ct)
    {
        if (cache is null && registry is null)
        {
            return new Reading([], new Dictionary<string, NodeRecord>(StringComparer.Ordinal), null);
        }

        string? error = null;
        IReadOnlyList<NodeRecord> records = [];
        if (registry is not null)
        {
            try
            {
                records = await registry.ListAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = $"Could not read the node list: {ex.Message}";
            }
        }

        var live = (cache?.ListNodes() ?? []).GroupBy(n => n.NodeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var byId = records.GroupBy(r => r.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var rows = records.Where(r => !r.Revoked).Select(r => r.Id)
            .Concat(live.Keys)
            .Distinct(StringComparer.Ordinal)
            .Select(id => Nodes.Merge(byId.GetValueOrDefault(id), live.GetValueOrDefault(id), definitions))
            .Where(n => n.State is not (NodeState.Revoked or NodeState.PendingApproval))
            .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Id, StringComparer.Ordinal)
            .ToList();
        return new Reading(rows, byId, error);
    }

    /// <summary>A model on a node with its current state (what <see cref="ViewFor"/> needs of it).</summary>
    public readonly record struct PlacedModel(ModelDefinition Definition, ModelState State)
    {
        /// <summary>Starting, Ready and Stopping models hold their budget.</summary>
        public bool HoldsMemory => State is ModelState.Starting or ModelState.Ready or ModelState.Stopping;
    }

    /// <summary>One node's head line and headroom from its merged row, its models and its latest host sample.</summary>
    public static NodeGroupView ViewFor(Nodes.NodeRow node, IReadOnlyList<PlacedModel> models, NodeRecord? record, HostSample? latest)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(models);
        var memory = node.Memory;
        var active = models.Where(m => m.HoldsMemory).Select(m => m.Definition).ToList();
        var model = memory?.Model ?? node.Capabilities?.Memory.Model;
        IReadOnlyList<GpuHeadroom> gpus = model == MemoryModel.Discrete && memory is { Gpus.Count: > 0 }
            ? GpusFor(node, memory, active, record, latest)
            : [];
        return new NodeGroupView
        {
            Id = node.Id,
            Name = node.Name,
            State = node.State,
            Draining = node.Draining,
            Reasons = node.Reasons,
            LastContact = node.LastContact,
            ModelCount = models.Count,
            ReadyCount = models.Count(m => m.State == ModelState.Ready),
            MemoryModel = model,
            TotalGiB = memory?.HostTotalGiB,
            AvailableGiB = memory?.HostAvailableGiB,
            ModelsGiB = active.Sum(d => d.MemoryBudgetGiB),
            ReserveGiB = node.ReserveGiB,
            Gpus = gpus,
            Os = node.Capabilities?.Os.Family,
            GpuUtilization = latest?.Gpu?.UtilizationPercent,
            Runtimes = (node.Capabilities?.Runtimes ?? []).Select(r => RuntimeLabel(r.Kind)).Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>
    /// Per-GPU headroom of a discrete node: VRAM from the node's memory snapshot; the models segment from the collector's
    /// per-GPU model memory when the latest host sample has it, else each running model's budget split over its GPUs
    /// (all of the node's GPUs when its placement names none); the reserve from the record's per-GPU reserves.
    /// </summary>
    private static List<GpuHeadroom> GpusFor(Nodes.NodeRow node, NodeMemorySnapshot memory, List<ModelDefinition> active, NodeRecord? record, HostSample? latest)
    {
        var names = (node.Capabilities?.Gpus ?? []).GroupBy(g => g.Uuid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        var measured = (latest?.Gpus ?? []).Where(g => g.ModelMemoryGiB is not null)
            .GroupBy(g => g.Uuid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().ModelMemoryGiB!.Values.Sum(), StringComparer.Ordinal);
        var result = new List<GpuHeadroom>(memory.Gpus.Count);
        foreach (var gpu in memory.Gpus.OrderBy(g => g.Index))
        {
            double models = measured.TryGetValue(gpu.Uuid, out var m)
                ? m
                : active.Sum(d => BudgetShare(d, gpu.Uuid, memory.Gpus.Count));
            double? free = gpu.FreeGiB ?? (gpu.TotalGiB is { } total && gpu.UsedGiB is { } used ? total - used : null);
            result.Add(new GpuHeadroom(
                gpu.Index,
                gpu.Uuid,
                names.GetValueOrDefault(gpu.Uuid),
                gpu.TotalGiB,
                free,
                models,
                record?.GpuReserveGiB.GetValueOrDefault(gpu.Uuid) ?? 0));
        }

        return result;
    }

    /// <summary>A model's budget on one GPU: its total budget split evenly over the GPUs its placement names.</summary>
    private static double BudgetShare(ModelDefinition model, string uuid, int nodeGpuCount)
    {
        var uuids = model.Placement?.GpuUuids;
        if (uuids is not { Count: > 0 })
        {
            return nodeGpuCount > 0 ? model.MemoryBudgetGiB / nodeGpuCount : 0;
        }

        return uuids.Contains(uuid, StringComparer.Ordinal) ? model.MemoryBudgetGiB / uuids.Count : 0;
    }

    /// <summary>"vLLM", "llama.cpp" or "MLX".</summary>
    public static string RuntimeLabel(RuntimeKind runtime) => LlamaCppEditing.RuntimeName(runtime);

    /// <summary>
    /// The GPUs a model's placement names, by their index on its node ("GPU 0", "GPUs 0, 1"); null when the placement
    /// names none (a unified node, or any GPU of a discrete one). A GPU the node no longer reports keeps its UUID's head.
    /// </summary>
    public static string? GpuText(ModelDefinition model, Nodes.NodeRow? node)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Placement?.GpuUuids is not { Count: > 0 } uuids)
        {
            return null;
        }

        var indexes = (node?.Capabilities?.Gpus ?? []).GroupBy(g => g.Uuid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Index, StringComparer.Ordinal);
        var parts = uuids.Select(u => indexes.TryGetValue(u, out int i)
            ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : (u.Length > 12 ? u[..12] + "…" : u)).ToList();
        return $"{(parts.Count == 1 ? "GPU" : "GPUs")} {string.Join(", ", parts)}";
    }
}
