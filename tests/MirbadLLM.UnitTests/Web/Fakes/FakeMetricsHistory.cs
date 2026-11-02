using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Web.Fakes;

/// <summary>Hand-written fake <see cref="IMetricsHistory"/> with settable canned data.</summary>
public sealed class FakeMetricsHistory : IMetricsHistory
{
    public IReadOnlyList<HostSample> HostSamples { get; set; } = [];

    public Dictionary<string, IReadOnlyList<ModelSample>> ModelSamplesById { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, ModelKpis> LatestKpis { get; set; } = new Dictionary<string, ModelKpis>();

    public IReadOnlyList<HostSample> GetHost(TimeSpan window) => HostSamples;

    public IReadOnlyList<ModelSample> GetModel(string modelId, TimeSpan window) =>
        ModelSamplesById.TryGetValue(modelId, out var samples) ? samples : [];

    public IReadOnlyDictionary<string, ModelKpis> Latest() => LatestKpis;
}
