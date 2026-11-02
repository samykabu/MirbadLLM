using MirbadLLM.Core;

namespace MirbadLLM.Agent.Admission;

/// <summary>
/// Settings of the node's <see cref="ReservationManager"/> (design §4.8, §6.1). Composed by the agent host (MN-026) from
/// the agent configuration and the node identity.
/// </summary>
public sealed record AdmissionOptions
{
    /// <summary>Node id used in refusal messages ("… on node dgx.").</summary>
    public required string NodeId { get; init; }

    /// <summary>
    /// How the node's memory is organized. <see cref="MemoryModel.Unified"/> (the DGX): one start at a time on the whole
    /// node. <see cref="MemoryModel.Discrete"/>: one start per GPU; with a node memory reader also per-GPU and host admission
    /// (P2, MN-105; see <see cref="ReservationManager"/>). The P1 composition sets unified; MN-130 sets it per node.
    /// </summary>
    public MemoryModel MemoryModel { get; init; } = MemoryModel.Unified;

    /// <summary>
    /// Concurrent starts allowed on the node (design §4.8, <c>NodeRecord.MaxConcurrentStarts</c>). Null = the default:
    /// unified 1; discrete one per GPU with no node-wide cap.
    /// </summary>
    public int? MaxConcurrentStarts { get; init; }

    /// <summary>First loopback port handed out (today's <c>MirbadLLMOptions.PortRangeStart</c>).</summary>
    public int PortRangeStart { get; init; } = 8101;

    /// <summary>Last loopback port handed out (inclusive).</summary>
    public int PortRangeEnd { get; init; } = 65535;

    /// <summary>
    /// True for a port another program holds (a listener on any address, or a failed loopback test-bind), so the
    /// reservation skips it under the admission lock (MN-219, D127; design-p3p4 §A.1.4). MN-232 sets
    /// <c>ProcessPorts.IsTaken</c> on a process (Windows) node; null = not checked, the container nodes (the DGX) as before.
    /// </summary>
    public Func<int, bool>? PortInUse { get; init; }

    /// <summary>
    /// Called with every launch's <see cref="Contracts.AdmissionRequest"/> as the reservation is requested, before the
    /// decision (MN-232, D129 (2)). The composition on a process node remembers the node's per-GPU reserves from it
    /// (<c>ExternalGpuUseContributor.RememberReserves</c>) and persists them, so <c>gpu_external_use_high</c> is judged
    /// right after a restart and not only after the next launch. Must not throw. Null (the DGX and every P1/P2 node) =
    /// nothing observes admission, exactly as before.
    /// <para>It runs <b>before</b> the admission semaphore, not under it (unlike <see cref="PortInUse"/>, which is
    /// deliberately inside the lock, D127): concurrent launches may therefore reach it in either order, so an observer
    /// must be safe to call from several threads and must not assume it sees the admitted order. MN-234 left it outside
    /// on purpose — a refusal should teach the reserves too, and the observer must never hold the lock while it does
    /// I/O.</para>
    /// </summary>
    public Action<Contracts.AdmissionRequest>? OnAdmission { get; init; }

    /// <summary>
    /// Reserve used when re-checking a reservation rebuilt after a restart without its admission inputs (today's
    /// <c>MirbadLLMOptions.MemoryReserveGiB</c> default).
    /// </summary>
    public double DefaultReserveGiB { get; init; } = 8;

    /// <summary>
    /// True on a node that delivers its models as native processes (<c>Agent:Docker:Enabled</c> false), so a start
    /// conflict is explained with the runtime that node actually runs (MN-239 H2:
    /// <see cref="AdmissionTexts.StartConflict"/> told a Windows llama.cpp operator that "vLLM measures memory while it
    /// loads"). False on the DGX and every container node, where the refusal keeps the pre-P1 text word for word.
    /// <para>The node, not the instance: the admission contract carries no delivery, and adding one would be a wire
    /// change this late. A node with both drivers (MN-236's dev setup) is worded for its process half, which is the
    /// half whose operator reads the message.</para>
    /// </summary>
    public bool ProcessDelivery { get; init; }
}
