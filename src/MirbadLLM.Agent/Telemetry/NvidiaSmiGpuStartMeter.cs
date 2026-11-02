using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// The "measured at start" figure (design-p3p4 §A.1.9, MN-214): <c>memory.used</c> of the placed GPUs right before the
/// create (<see cref="MarkAsync"/>) and right after Ready (<see cref="MeasureAsync"/>), read with the agent's own
/// <c>nvidia-smi --query-gpu</c> (<see cref="IGpuTelemetryProbe"/>), never from CUDA or llama.cpp, which over-report
/// free memory under WDDM (spike S1 / C9, D118). The process driver (MN-211) stores the result in
/// <see cref="ProcessRecord.MeasuredGpuGiB"/>.
/// <para>The figure is approximate: other programs allocate and release memory while a model starts (S1's cold start
/// measured 470 MiB below the sum of llama-server's buffers), so the UI labels it "measured at start" and admission never
/// uses it (it keeps the declared budget). A GPU that is missing from the read or reports no memory makes the whole mark
/// or measurement null (not measured), never a partial sum.</para>
/// </summary>
public sealed class NvidiaSmiGpuStartMeter(IGpuTelemetryProbe probe, TimeProvider timeProvider) : IGpuStartMeter
{
    /// <inheritdoc />
    public async Task<GpuStartMark?> MarkAsync(IReadOnlyList<string> gpuUuids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gpuUuids);
        if (gpuUuids.Count == 0)
        {
            return null;
        }

        var used = await ReadUsedAsync(gpuUuids, ct).ConfigureAwait(false);
        return used is null ? null : new GpuStartMark(timeProvider.GetUtcNow(), used);
    }

    /// <inheritdoc />
    public async Task<double?> MeasureAsync(GpuStartMark mark, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mark);
        if (mark.UsedGiB.Count == 0)
        {
            return null;
        }

        var now = await ReadUsedAsync([.. mark.UsedGiB.Keys], ct).ConfigureAwait(false);
        if (now is null)
        {
            return null;
        }

        double delta = mark.UsedGiB.Sum(kv => now[kv.Key] - kv.Value);
        return Math.Max(0, delta);
    }

    /// <summary><c>memory.used</c> (GiB) of each GPU; null when the read failed or a GPU is missing or reports none.</summary>
    private async Task<IReadOnlyDictionary<string, double>?> ReadUsedAsync(IReadOnlyList<string> gpuUuids, CancellationToken ct)
    {
        var reading = await probe.ReadAsync(ct).ConfigureAwait(false);
        if (reading.Status != GpuProbeStatus.Ok)
        {
            return null;
        }

        var used = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string uuid in gpuUuids)
        {
            var gpu = reading.Gpus.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.OrdinalIgnoreCase));
            if (gpu?.MemoryUsedGiB is not { } gib)
            {
                return null;
            }

            used[uuid] = gib;
        }

        return used;
    }
}
