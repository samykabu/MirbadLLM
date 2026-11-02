using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Admission;

/// <summary>
/// MN-105's part of the capability report (design §3.3, D73): <see cref="AgentFeatures.AdmissionDiscrete"/> when the node
/// reports a discrete memory model and its <see cref="ReservationManager"/> runs per-GPU and host admission
/// (<see cref="ReservationManager.PerGpuAdmission"/>). The master sends <see cref="AdmissionRequest.GpuReservesGiB"/> only
/// to nodes with the flag. A unified node (the DGX) never reports it, so its report stays P1. Never removes what other
/// contributors added and never throws. Registered by the P2 composition (MN-130).
/// </summary>
/// <param name="reservations">The node's reservation manager.</param>
public sealed class DiscreteAdmissionContributor(ReservationManager reservations) : ICapabilityContributor
{
    private readonly ReservationManager _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));

    /// <inheritdoc />
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!_reservations.PerGpuAdmission || current.Memory.Model != MemoryModel.Discrete
            || current.Features.Contains(AgentFeatures.AdmissionDiscrete, StringComparer.Ordinal))
        {
            return Task.FromResult(current);
        }

        return Task.FromResult(current with { Features = [.. current.Features, AgentFeatures.AdmissionDiscrete] });
    }
}
