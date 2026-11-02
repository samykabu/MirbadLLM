using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.NodeRuntime.Runtimes.Process;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// MN-106's part of the capability report (design §3.3, §5.1, D73): <see cref="AgentFeatures.GpuSelectionUuids"/> while
/// the container driver runs this node's instances and selects GPUs by UUID (it has the node's GPU inventory): it then
/// launches <c>gpus.mode = uuids</c> with exactly those GPUs, refuses a UUID that is not on the node with 422
/// <c>gpu_missing</c> before reserving, and writes the read-back labels. Only then does the master send a placement's GPUs
/// (and does its placement validator accept discrete placements on the node, MN-108). A node whose instances run
/// elsewhere (the dev setup's fake driver) or whose driver has no inventory reports nothing. Never removes what other
/// contributors added and never throws. Registered by the P2 composition (MN-130).
/// <para>The process driver (MN-211) selects GPUs by UUID the same way — it checks a spec's UUIDs against the node's
/// inventory and refuses an unknown one with 422 <c>gpu_missing</c> — so a Windows/process node reports the flag too
/// (MN-232; the frozen <c>NodeCapabilities.windows.json</c> carries it, and the master's placement validator refuses
/// every GPU placement on a node without it).</para>
/// </summary>
public sealed class GpuSelectionContributor(IEnumerable<IRuntimeDriver> drivers) : ICapabilityContributor
{
    private readonly IRuntimeDriver[] _drivers = [.. drivers];

    /// <inheritdoc />
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        bool byUuid =
            _drivers.OfType<ContainerRuntimeDriver>().Any(d => d.Delivery == LaunchDelivery.Container && d.SelectsGpusByUuid) ||
            _drivers.OfType<ProcessRuntimeDriver>().Any(d => d.Delivery == LaunchDelivery.Process && d.SelectsGpusByUuid);
        if (!byUuid || current.Features.Contains(AgentFeatures.GpuSelectionUuids, StringComparer.Ordinal))
        {
            return Task.FromResult(current);
        }

        return Task.FromResult(current with { Features = [.. current.Features, AgentFeatures.GpuSelectionUuids] });
    }
}
