using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// Per-model memory of a host sample (Monitoring's host charts): measured from the node's GPU processes when every running
/// model's container is known, else each running model's budget. Used by <see cref="Multinode.RemoteTelemetryCollector"/>;
/// the rule is the pre-P1 single-host sampler's, unchanged for containers (MN-047, D60).
/// <para>Process instances (<c>delivery: process</c>, llama.cpp; MN-223, D129 (1)): their instance id is a PID and WDDM
/// <c>nvidia-smi</c> reports no per-process memory (<see cref="GpuProcess.UsedMiB"/> 0), so they never take GPU process
/// rows. Each shows <see cref="RunningModel.MeasuredAtStartGiB"/> (the agent's <c>memory.used</c> delta across the start,
/// <c>RuntimeStatus.MeasuredGpuGiB</c>, "measured at start" on the pages), else its budget, in either branch.</para>
/// </summary>
public static class ModelMemoryAttribution
{
    private const double MiBPerGiB = 1024;

    /// <summary>A model whose container holds memory at this sample: its container id and configured budget.</summary>
    public sealed record RunningModel(string? ContainerId, double BudgetGiB)
    {
        /// <summary>
        /// True for a process instance (<see cref="IsProcessDelivery"/>): never matched against GPU process rows; its
        /// figure is <see cref="MeasuredAtStartGiB"/>, else <see cref="BudgetGiB"/>.
        /// </summary>
        public bool Process { get; init; }

        /// <summary>A process instance's GPU memory measured at its start (<c>RuntimeStatus.MeasuredGpuGiB</c>), when known.</summary>
        public double? MeasuredAtStartGiB { get; init; }

        /// <summary>The figure of a process instance: measured at start, else the budget.</summary>
        internal double ProcessGiB => MeasuredAtStartGiB ?? BudgetGiB;
    }

    /// <summary>
    /// True when <paramref name="model"/> runs as a host process (a llama.cpp model with <c>delivery: process</c>, or an MLX
    /// model, which always is one; its instance id is a PID, not a container id).
    /// </summary>
    public static bool IsProcessDelivery(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return (model.Runtime == RuntimeKind.LlamaCpp && model.LlamaCpp is { Delivery: LaunchDelivery.Process }) ||
            (model.Runtime == RuntimeKind.Mlx && model.Mlx is not null);
    }

    /// <summary>
    /// The running entry for <paramref name="model"/> with the status cache's instance (null when unknown): a container's id
    /// and budget, or a process instance's measured-at-start figure and budget.
    /// </summary>
    public static RunningModel Running(ModelDefinition model, NodeModelRuntime? instance)
    {
        ArgumentNullException.ThrowIfNull(model);
        return IsProcessDelivery(model)
            ? new RunningModel(null, model.MemoryBudgetGiB) { Process = true, MeasuredAtStartGiB = instance?.MeasuredGpuGiB }
            : new RunningModel(instance?.InstanceId, model.MemoryBudgetGiB);
    }

    /// <summary>
    /// True when GPU process rows can be attributed: every running container model's container id is known (process
    /// instances never take rows, so they do not count).
    /// </summary>
    public static bool ContainersKnown(IReadOnlyDictionary<string, RunningModel> running)
    {
        ArgumentNullException.ThrowIfNull(running);
        return running.Values.All(r => r.Process || !string.IsNullOrEmpty(r.ContainerId));
    }

    /// <summary>
    /// Per-model memory for a host sample. With the node's GPU processes: each running model's measured GPU memory (its
    /// container's processes; 0 while it hasn't allocated yet) and the identifiable non-model consumers by name (a unit's
    /// display name, else the unit without ".service"). Without them: each running model's budget. A process instance shows
    /// its measured-at-start figure, else its budget, in both cases.
    /// </summary>
    public static HostSample WithModelMemory(HostSample host, IReadOnlyDictionary<string, RunningModel> running, IReadOnlyList<GpuProcess>? processes)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(running);
        if (processes is null)
        {
            return host with
            {
                ModelMemoryGiB = running.ToDictionary(kv => kv.Key, kv => kv.Value.Process ? kv.Value.ProcessGiB : kv.Value.BudgetGiB, StringComparer.Ordinal),
                MemorySource = ModelMemorySource.Budget,
            };
        }

        var perModel = running.ToDictionary(kv => kv.Key, kv => kv.Value.Process ? kv.Value.ProcessGiB : 0.0, StringComparer.Ordinal);
        var others = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var process in processes)
        {
            double gib = process.UsedMiB / MiBPerGiB;
            string? model = process.ContainerId is { Length: > 0 } container
                ? running.FirstOrDefault(kv => !kv.Value.Process && SameContainer(kv.Value.ContainerId, container)).Key
                : null;
            if (model is not null)
            {
                perModel[model] += gib;
            }
            else if (process.ContainerId is null && process.Unit is { Length: > 0 } unit)
            {
                string name = !string.IsNullOrWhiteSpace(process.DisplayName) ? process.DisplayName
                    : unit.EndsWith(".service", StringComparison.Ordinal) ? unit[..^".service".Length] : unit;
                others[name] = others.GetValueOrDefault(name) + gib;
            }
        }

        return host with { ModelMemoryGiB = perModel, MemorySource = ModelMemorySource.Measured, OtherGpuGiB = others };
    }

    /// <summary>Docker ids: full 64-hex from the cgroup; the runtime may hold the full or a 12+ character short id.</summary>
    private static bool SameContainer(string? modelContainer, string processContainer) =>
        modelContainer is { Length: >= 12 } id &&
        (processContainer.StartsWith(id, StringComparison.OrdinalIgnoreCase) || id.StartsWith(processContainer, StringComparison.OrdinalIgnoreCase));
}
