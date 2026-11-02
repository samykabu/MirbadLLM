using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// <see cref="IGpuProcessProbe"/> over the host agent, bounded so a slow agent never stalls a 5-second sampling pass:
/// no answer within <see cref="Timeout"/> counts as unavailable (null), like a missing or older agent.
/// </summary>
public sealed class HostAgentGpuProcessProbe(HostAgentClient client) : IGpuProcessProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<GpuProcess>?> GetAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            return await client.GetGpuProcessesAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
