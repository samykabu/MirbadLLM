using MirbadLLM.Core;

namespace MirbadLLM.Web;

/// <summary>
/// One node's card on Monitoring's node rail: where its model memory lives (unified memory on a DGX-like node,
/// VRAM on a discrete one), how full it is now and at its peak in the window, and the GPU readings it reports. Every
/// figure is measured; a reading the node does not report stays null (rendered "—" with "not reported").
/// </summary>
/// <param name="NodeId">Node id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Discrete">True when the node reports per-GPU VRAM (model memory is VRAM, not unified memory).</param>
/// <param name="GpuCount">GPUs in the newest sample of a discrete node; 0 on a unified node.</param>
/// <param name="UsedGiB">Model memory used now (unified memory, or VRAM summed over the GPUs).</param>
/// <param name="TotalGiB">Its total.</param>
/// <param name="PeakUsedGiB">Highest used value in the window.</param>
/// <param name="GpuUtilization">GPU utilization now (average over GPUs on a discrete node).</param>
/// <param name="PowerWatts">GPU power now.</param>
/// <param name="TemperatureC">GPU temperature now.</param>
/// <param name="Samples">Samples in the window.</param>
public sealed record NodeSummary(
    string NodeId,
    string Name,
    bool Discrete,
    int GpuCount,
    double? UsedGiB,
    double? TotalGiB,
    double? PeakUsedGiB,
    double? GpuUtilization,
    double? PowerWatts,
    double? TemperatureC,
    int Samples)
{
    /// <summary>"Unified memory" or "VRAM, 2 GPUs".</summary>
    public string MemoryLabel => Discrete ? $"VRAM, {GpuCount} GPU{(GpuCount == 1 ? "" : "s")}" : "Unified memory";
}

public static class MonitoringNodes
{
    /// <summary>The summary of <paramref name="samples"/> (oldest first); all-null figures when the window is empty.</summary>
    public static NodeSummary Summarize(string nodeId, string name, IReadOnlyList<HostSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return new NodeSummary(nodeId, name, false, 0, null, null, null, null, null, null, 0);
        }

        var latest = samples[^1];
        bool discrete = latest.Gpus is { Count: > 0 };
        double? peak = samples.Select(Used).Where(v => v is not null).Max();
        return new NodeSummary(
            nodeId,
            name,
            discrete,
            latest.Gpus?.Count ?? 0,
            Used(latest),
            Total(latest),
            peak,
            Utilization(latest),
            latest.Gpu?.PowerWatts,
            latest.Gpu?.TemperatureC,
            samples.Count);
    }

    /// <summary>Model memory used at <paramref name="sample"/>: VRAM summed over GPUs on a discrete node, unified memory otherwise.</summary>
    public static double? Used(HostSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Gpus is { Count: > 0 } gpus)
        {
            var used = gpus.Where(g => g.MemoryUsedGiB is not null).Select(g => g.MemoryUsedGiB!.Value).ToList();
            return used.Count == 0 ? null : used.Sum();
        }

        return sample.Memory.TotalGiB - sample.Memory.AvailableGiB;
    }

    /// <summary>Total behind <see cref="Used"/>.</summary>
    public static double? Total(HostSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Gpus is { Count: > 0 } gpus)
        {
            var totals = gpus.Where(g => g.MemoryTotalGiB is not null).Select(g => g.MemoryTotalGiB!.Value).ToList();
            return totals.Count == 0 ? null : totals.Sum();
        }

        return sample.Memory.TotalGiB;
    }

    /// <summary>GPU utilization at <paramref name="sample"/>: the average over the GPUs that report it, or the single GPU's.</summary>
    public static double? Utilization(HostSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (sample.Gpus is { Count: > 0 } gpus)
        {
            var values = gpus.Where(g => g.UtilizationPercent is not null).Select(g => g.UtilizationPercent!.Value).ToList();
            return values.Count == 0 ? null : values.Average();
        }

        return sample.Gpu?.UtilizationPercent;
    }
}
