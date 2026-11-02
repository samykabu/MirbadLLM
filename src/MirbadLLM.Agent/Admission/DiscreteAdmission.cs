using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Admission;

/// <summary>
/// Discrete admission helpers (design §4.8, §6.1; MN-105): the host-memory estimate used when a launch names no host
/// budget, the per-GPU shares of a start and of held reservations (per-GPU budgets: design-p3p4 §A.1.5, MN-224), and the
/// node memory reader the <see cref="ReservationManager"/> decides with.
/// </summary>
public static class DiscreteAdmission
{
    /// <summary>vLLM's default <c>--swap-space</c> per GPU (GiB): the per-GPU part of the host estimate.</summary>
    public const double DefaultSwapSpaceGiB = 4;

    /// <summary>Host memory of the runtime process itself (GiB), on top of the swap space.</summary>
    public const double ProcessOverheadGiB = 2;

    /// <summary>
    /// The design §6.1 host-memory estimate for a vLLM start on <paramref name="gpuCount"/> discrete GPUs:
    /// <c>swapSpace × n + 2 GiB</c>, with <paramref name="swapSpaceGiB"/> (an explicit <c>--swap-space</c>) replacing the
    /// default 4 GiB.
    /// </summary>
    public static double HostEstimateGiB(int gpuCount, double? swapSpaceGiB = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(gpuCount);
        double swap = swapSpaceGiB ?? DefaultSwapSpaceGiB;
        if (double.IsNaN(swap) || double.IsInfinity(swap) || swap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(swapSpaceGiB), "The swap space must be a finite, non-negative number.");
        }

        return (swap * gpuCount) + ProcessOverheadGiB;
    }

    /// <summary>
    /// The host budget a discrete start is admitted with: the launch's <paramref name="hostBudgetGiB"/> when given; else a
    /// CPU-only start (<paramref name="gpuCount"/> 0) needs its whole <paramref name="budgetGiB"/> in host memory, and a GPU
    /// start the vLLM estimate (<see cref="HostEstimateGiB"/>). The launch request carries no runtime arguments, so an
    /// explicit <c>--swap-space</c> or a llama.cpp partial offload must reach the agent as <c>hostBudgetGiB</c>.
    /// </summary>
    public static double EffectiveHostBudgetGiB(double budgetGiB, double? hostBudgetGiB, int gpuCount) =>
        hostBudgetGiB ?? (gpuCount == 0 ? budgetGiB : HostEstimateGiB(gpuCount));

    /// <summary>
    /// The VRAM a start needs on <paramref name="gpuUuid"/>, one of its <paramref name="gpuCount"/> placed GPUs (design-p3p4
    /// §A.1.5, MN-224): <see cref="AdmissionRequest.BudgetForGpu"/>, i.e. the GPU's entry of
    /// <see cref="AdmissionRequest.GpuBudgetsGiB"/> when the launch sends per-GPU budgets (a llama.cpp model on unequal
    /// GPUs), else <paramref name="budgetGiB"/> ÷ n exactly as in P2.
    /// </summary>
    public static double GpuShareGiB(AdmissionRequest admission, double budgetGiB, string gpuUuid, int gpuCount)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentOutOfRangeException.ThrowIfLessThan(gpuCount, 1);
        return admission.GpuBudgetsGiB?.ContainsKey(gpuUuid) == true ? admission.BudgetForGpu(gpuUuid, gpuCount) : budgetGiB / gpuCount;
    }

    /// <summary>
    /// The part of <paramref name="releasedGiB"/> (memory a replaced instance gives back) counted on
    /// <paramref name="gpuUuid"/>: in the proportion of the GPU's own budget when the launch sends per-GPU budgets, else
    /// split evenly (P2).
    /// </summary>
    public static double ReleasedShareGiB(AdmissionRequest admission, double budgetGiB, double releasedGiB, string gpuUuid, int gpuCount)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentOutOfRangeException.ThrowIfLessThan(gpuCount, 1);
        return admission.GpuBudgetsGiB?.ContainsKey(gpuUuid) == true && budgetGiB > 0
            ? releasedGiB * admission.BudgetForGpu(gpuUuid, gpuCount) / budgetGiB
            : releasedGiB / gpuCount;
    }

    /// <summary>
    /// The VRAM a held reservation counts on <paramref name="gpuUuid"/>: nothing when it is not on that GPU; else its
    /// per-GPU budget from the launch it was admitted with (<paramref name="admission"/>, kept across a rebuild from the
    /// operation log) when that sent one, else its budget split evenly over its GPUs (P2, and a record rebuilt without its
    /// launch).
    /// </summary>
    public static double HeldOnGpuGiB(ReservationInfo reservation, AdmissionRequest? admission, string gpuUuid)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        int count = reservation.Gpus.Count(g => !string.Equals(g, ReservationInfo.HostPseudoGpu, StringComparison.Ordinal));
        if (count == 0 || !reservation.Gpus.Contains(gpuUuid, StringComparer.Ordinal))
        {
            return 0;
        }

        return admission?.GpuBudgetsGiB is { } budgets && budgets.TryGetValue(gpuUuid, out double budget)
            ? budget
            : reservation.BudgetGiB / count;
    }

    /// <summary>
    /// The reader for <see cref="ReservationManager"/>'s <c>nodeMemory</c> on a discrete node: host memory from
    /// <paramref name="host"/> and every GPU's driver <c>memory.free</c> from <paramref name="gpus"/>, read afresh for each
    /// decision (no reuse window: "a fresh probe under its start lock", design §6.2). Use a dedicated instance, not the
    /// <c>/status</c> reader, whose short reuse window would let a decision see VRAM from before an instance took it.
    /// </summary>
    public static DiscreteNodeMemoryReader CreateMemoryReader(IMemoryProbe host, IGpuTelemetryProbe gpus, TimeProvider time) =>
        new(host, gpus, time, MemoryModel.Discrete, TimeSpan.Zero);
}
