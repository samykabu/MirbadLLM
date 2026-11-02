using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// <see cref="IModelMetricsReader"/> backed by <see cref="IMetricsHistory"/> instead of scraping directly:
/// <see cref="Multinode.RemoteTelemetryCollector"/> already records every Ready model on its own cadence, so the Dashboard just
/// reads the latest computed sample.
/// </summary>
public sealed class HistoryModelMetricsReader(IMetricsHistory history) : IModelMetricsReader
{
    public Task<ModelMetrics?> GetAsync(string id, CancellationToken ct = default)
    {
        var latest = history.Latest();
        return Task.FromResult(latest.TryGetValue(id, out var kpis) ? kpis.Load : null);
    }
}
