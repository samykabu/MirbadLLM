using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// GPU processes with owners for the sampler, from hostd's <c>GET /gpu-processes</c> through <see cref="IHostdClient"/>
/// (design §8.1). Bounded like today's <c>HostAgentGpuProcessProbe</c>: no answer within
/// <see cref="TelemetryOptions.GpuProcessTimeout"/> counts as unavailable (null), as do a missing hostd, an older one
/// and a failing call. Windows (<c>nvidia-smi --query-compute-apps</c>) comes with P4.
/// </summary>
public sealed class HostdGpuProcessProbe(IHostdClient? hostd, IOptions<TelemetryOptions> options) : IGpuProcessProbe
{
    private readonly TimeSpan _timeout = options.Value.GpuProcessTimeout;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GpuProcess>?> GetAsync(CancellationToken ct = default)
    {
        if (hostd is null || !hostd.IsInstalled)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        try
        {
            return await hostd.GetGpuProcessesAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
