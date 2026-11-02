namespace MirbadLLM.Orchestration;

/// <summary>
/// Orchestration-only monitoring knobs, bound from the same <c>MirbadLLM</c> configuration section as
/// <see cref="MirbadLLM.Core.MirbadLLMOptions"/> (kept separate because Core is frozen for Phase 1).
/// </summary>
public sealed class MetricsOptions
{
    /// <summary>How often <see cref="Multinode.RemoteTelemetryCollector"/> pulls the nodes' host, GPU and per-model samples.</summary>
    public int MetricsSampleIntervalSeconds { get; set; } = 5;

    /// <summary>How much sampled history <see cref="InMemoryMetricsHistory"/> keeps.</summary>
    public int MetricsRetentionMinutes { get; set; } = 60;

    /// <summary>
    /// How old a model's newest sample may be for <see cref="IMetricsHistory.Latest"/> to still report it: 60 s, or
    /// twelve sample intervals when those are longer. A model that stopped, crashed or was deleted leaves the
    /// "running now" set once its samples stop, instead of lingering for the retention hour.
    /// </summary>
    public TimeSpan LatestFreshness => TimeSpan.FromSeconds(Math.Max(60, 12 * MetricsSampleIntervalSeconds));

    public TimeSpan SampleInterval => TimeSpan.FromSeconds(MetricsSampleIntervalSeconds);

    public TimeSpan RetentionWindow => TimeSpan.FromMinutes(MetricsRetentionMinutes);
}
