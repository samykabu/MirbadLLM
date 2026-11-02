using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Web.Fakes;

public sealed class FakeModelMetricsReader : IModelMetricsReader
{
    private readonly Dictionary<string, ModelMetrics?> _metrics = new(StringComparer.Ordinal);

    public void Set(string id, ModelMetrics? metrics) => _metrics[id] = metrics;

    public Task<ModelMetrics?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_metrics.GetValueOrDefault(id));
}
