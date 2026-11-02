using MirbadLLM.Core;

namespace MirbadLLM.Agent.Events;

/// <summary>
/// The node's memory for <see cref="StatusSnapshot"/>.<c>Memory</c> (design §4.3 <c>NodeMemorySnapshot</c>). The composition
/// (MN-026) registers the node's implementation; <see cref="ProbeNodeMemoryReader"/> adapts today's host
/// <see cref="IMemoryProbe"/>.
/// </summary>
public interface INodeMemoryReader
{
    /// <summary>Memory now. Should not throw; return the last known value when a probe fails.</summary>
    Task<NodeMemorySnapshot> ReadAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="INodeMemoryReader"/> over the host <see cref="IMemoryProbe"/> (<c>/proc/meminfo</c>), with fixed GPU
/// identities. Enough for the unified DGX in P1; discrete per-GPU memory comes with the GPU sampler.
/// </summary>
/// <param name="probe">Host memory probe.</param>
/// <param name="model">The node's memory model.</param>
/// <param name="gpus">GPU identities (memory fields null on unified nodes).</param>
public sealed class ProbeNodeMemoryReader(IMemoryProbe probe, MemoryModel model, IReadOnlyList<GpuMemorySnapshot>? gpus = null) : INodeMemoryReader
{
    private readonly IMemoryProbe _probe = probe ?? throw new ArgumentNullException(nameof(probe));
    private readonly IReadOnlyList<GpuMemorySnapshot> _gpus = gpus ?? [];

    /// <inheritdoc/>
    public async Task<NodeMemorySnapshot> ReadAsync(CancellationToken ct = default)
    {
        var host = await _probe.GetAsync(ct).ConfigureAwait(false);
        return new NodeMemorySnapshot
        {
            Model = model,
            HostTotalGiB = host.TotalGiB,
            HostAvailableGiB = host.AvailableGiB,
            Gpus = _gpus,
        };
    }
}
