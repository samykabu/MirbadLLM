using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Admission;

/// <summary>
/// MN-224's part of the capability report (design-p3p4 §A.1.5, D116): <see cref="AgentFeatures.AdmissionGpuBudgets"/> when
/// the node reports a discrete memory model and its <see cref="ReservationManager"/> runs per-GPU admission
/// (<see cref="ReservationManager.PerGpuAdmission"/>), which admits each placed GPU against its own entry of
/// <see cref="AdmissionRequest.GpuBudgetsGiB"/>. The master sends per-GPU budgets only to nodes with the flag; without it
/// the agent splits the budget evenly (P2). A unified node (the DGX) never reports it, so its report stays P1. Never
/// removes what other contributors added and never throws. Registered by the P3 agent composition (MN-232).
/// </summary>
/// <param name="reservations">The node's reservation manager.</param>
public sealed class GpuBudgetsContributor(ReservationManager reservations) : ICapabilityContributor
{
    private readonly ReservationManager _reservations = reservations ?? throw new ArgumentNullException(nameof(reservations));

    /// <inheritdoc />
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!_reservations.PerGpuAdmission || current.Memory.Model != MemoryModel.Discrete
            || current.Features.Contains(AgentFeatures.AdmissionGpuBudgets, StringComparer.Ordinal))
        {
            return Task.FromResult(current);
        }

        return Task.FromResult(current with { Features = [.. current.Features, AgentFeatures.AdmissionGpuBudgets] });
    }
}
