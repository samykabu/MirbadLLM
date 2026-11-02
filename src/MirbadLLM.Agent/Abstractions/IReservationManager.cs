using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Abstractions;

/// <summary>A start reservation request (design §4.8): what a launch needs before it may be answered 202.</summary>
public sealed record StartReservationRequest
{
    /// <summary>The launch operation.</summary>
    public required string OperationId { get; init; }

    /// <summary>Model being started.</summary>
    public required string ModelId { get; init; }

    /// <summary>Admission inputs from the launch request.</summary>
    public required AdmissionRequest Admission { get; init; }

    /// <summary>GPU UUIDs of the placement (discrete); empty on unified nodes (the whole node is one start slot).</summary>
    public IReadOnlyList<string> GpuUuids { get; init; } = [];

    /// <summary>
    /// Memory to count as released because the launch replaces an existing instance. Only for the pre-removal decision;
    /// after the removal the real figure comes from a re-probe (<see cref="IReservationManager.RecheckAsync"/>).
    /// </summary>
    public double ReleasedGiB { get; init; }
}

/// <summary>Outcome of a reservation attempt or re-check.</summary>
public enum ReservationOutcome
{
    /// <summary>Reserved (or re-check passed).</summary>
    Reserved,

    /// <summary>Another start holds the node (unified) or one of the GPUs (discrete): 409 <c>start_conflict</c>.</summary>
    StartConflict,

    /// <summary>Memory admission refused: 409 <c>admission_refused</c>.</summary>
    AdmissionRefused,
}

/// <summary>Result of <see cref="IReservationManager.TryReserveAsync"/> or <see cref="IReservationManager.RecheckAsync"/>.</summary>
public sealed record ReservationResult
{
    /// <summary>What happened.</summary>
    public required ReservationOutcome Outcome { get; init; }

    /// <summary>The reservation when <see cref="ReservationOutcome.Reserved"/>.</summary>
    public ReservationInfo? Reservation { get; init; }

    /// <summary>The agent error to answer when refused (code, today's message text + "on node {id}", details).</summary>
    public AgentError? Error { get; init; }
}

/// <summary>
/// Start reservations, admission and the node-wide port lock (design §4.8, §6.1), implemented in
/// <c>MirbadLLM.Agent.Admission</c> (MN-016).
/// <para>Invariants:</para>
/// <list type="bullet">
/// <item>One node-wide admission lock serializes the start rule, the memory probe, the admission decision, port allocation
/// and recording the reservation, so the result is atomic.</item>
/// <item>Start rule: unified node → at most one start reservation on the node; discrete node → at most one per GPU (a
/// CPU-only process uses the pseudo-GPU <c>host</c>); host memory is always node-wide (P2). Configurable per node
/// (<c>maxConcurrentStarts</c>).</item>
/// <item>Admission compares the budget with free memory from a fresh probe minus the memory held by every other
/// reservation (a start in progress has not taken its memory yet); <c>force</c> skips memory admission, never the start
/// rule. Refusal messages keep the pre-P1 <c>DockerModelRuntime</c>'s texts plus "on node {id}".</item>
/// <item>A reservation is held until the instance is Ready or the operation is terminal or cancelled; the reservation is
/// persisted with the operation (<see cref="IOperationLog.RecordStageAsync"/>) and rebuilt after a restart.</item>
/// <item>Ports are allocated under the same lock and never handed out twice while reserved or in use.</item>
/// <item>Discrete nodes (P2, MN-105; contract frozen by MN-101, D73): per placed GPU the check is
/// <c>budget ÷ n ≤ gpuFree − AdmissionRequest.ReserveForGpu(uuid) − Σ reservations on that GPU</c>; the host part is
/// <c>hostBudget ≤ hostAvailable − ReserveGiB − Σ host reservations</c>, always under the node-wide lock (two starts on
/// different GPUs are serialized only for that part). A refusal names the GPU (<see cref="AdmissionRefusedDetails.Gpu"/>,
/// message "… on node {id} (GPU {index})") or the host part (<see cref="AdmissionRefusedDetails.Host"/>); a start conflict
/// lists the GPUs in <see cref="StartConflictDetails.Gpus"/>. <see cref="StartReservationRequest.GpuUuids"/> empty on a
/// discrete node means a CPU-only process (pseudo-GPU <see cref="ReservationInfo.HostPseudoGpu"/>).</item>
/// </list>
/// </summary>
public interface IReservationManager : IPortAllocator
{
    /// <summary>Checks the start rule and admission, allocates the port and records the reservation, atomically.</summary>
    Task<ReservationResult> TryReserveAsync(StartReservationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Re-probes memory and re-checks admission for a held reservation under the lock: after a pull that took longer than
    /// 30 s, and after a replaced instance was removed (the released memory is what the probe shows, design §4.8).
    /// </summary>
    Task<ReservationResult> RecheckAsync(string operationId, CancellationToken ct = default);

    /// <summary>Releases a reservation (instance Ready, operation failed or cancelled). Unknown ids are ignored.</summary>
    Task ReleaseAsync(string operationId, CancellationToken ct = default);

    /// <summary>Reservations held now.</summary>
    IReadOnlyList<ReservationInfo> List();

    /// <summary>Rebuilds the reservations after an agent restart from non-terminal launch operations and Starting instances.</summary>
    Task RebuildAsync(IEnumerable<ReservationInfo> reservations, CancellationToken ct = default);
}
