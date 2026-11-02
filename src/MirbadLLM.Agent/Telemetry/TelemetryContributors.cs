using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// The telemetry package's part of the capability report (design §3.3): the GPUs from <c>nvidia-smi</c>, the memory
/// model they imply (discrete when a GPU has its own VRAM) and the <see cref="AgentFeatures.TelemetryV1"/> feature.
/// Never removes what other contributors added and never throws for a failing <c>nvidia-smi</c> (the GPUs are then left
/// as they were; <see cref="GpuHealthContributor"/> says why).
/// </summary>
public sealed class TelemetryCapabilityContributor(NvidiaSmiGpuInventory inventory) : ICapabilityContributor
{
    /// <inheritdoc />
    public async Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        var gpus = await inventory.GetAsync(ct).ConfigureAwait(false);

        var features = current.Features.Contains(AgentFeatures.TelemetryV1, StringComparer.Ordinal)
            ? current.Features
            : [.. current.Features, AgentFeatures.TelemetryV1];

        if (gpus.Count == 0)
        {
            return current with { Features = features };
        }

        var memory = NvidiaSmiGpuInventory.MemoryModelOf(gpus) == MemoryModel.Discrete
            ? current.Memory with { Model = MemoryModel.Discrete }
            : current.Memory;
        return current with { Features = features, Gpus = gpus, Memory = memory };
    }
}

/// <summary>
/// Degraded reason <see cref="HealthReasonCodes.GpuProbeFailing"/> (design §3.5) from the last GPU read: reported while
/// <c>nvidia-smi</c> exists but fails. A node without <c>nvidia-smi</c> is healthy. Reuses the sampler's last read
/// (one every 5 s), so it is fast and never runs the probe itself unless no read happened yet.
/// </summary>
public sealed class GpuHealthContributor(IGpuTelemetryProbe probe) : IHealthContributor
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default)
    {
        var reading = probe.Last;
        if (reading is null)
        {
            try
            {
                reading = await probe.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return [new HealthReason(HealthReasonCodes.GpuProbeFailing, $"nvidia-smi fails: {ex.Message}")];
            }
        }

        return reading.Status == GpuProbeStatus.Failed
            ? [new HealthReason(HealthReasonCodes.GpuProbeFailing, $"GPU telemetry unavailable: {reading.Error ?? "nvidia-smi fails."}")]
            : [];
    }
}
