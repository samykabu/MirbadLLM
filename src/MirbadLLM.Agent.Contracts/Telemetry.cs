using System.Text.Json.Serialization;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// Query of <c>GET /agent/v1/telemetry</c> (design §8.1). The cursor is boot-aware: a different <see cref="BootId"/>
/// returns the oldest samples of the current boot (the master records a gap for the downtime).
/// </summary>
public sealed record TelemetryQuery
{
    /// <summary>Boot id of the last sample the master has.</summary>
    public string? BootId { get; init; }

    /// <summary>Sequence number of the last sample the master has.</summary>
    public long? AfterSeq { get; init; }

    /// <summary>Maximum samples to return. Null = agent default.</summary>
    public int? Max { get; init; }
}

/// <summary>
/// <c>GET /agent/v1/telemetry</c> response (design §8.1): samples from the 15-minute ring in time order.
/// <see cref="Gap"/> is true when samples after the cursor were already evicted (or the boot changed), so the master
/// records an honest gap instead of interpolating.
/// </summary>
public sealed record TelemetryPage
{
    /// <summary>Agent boot id the samples belong to.</summary>
    public required string BootId { get; init; }

    /// <summary>Samples after the cursor, oldest first.</summary>
    public IReadOnlyList<NodeSample> Samples { get; init; } = [];

    /// <summary>True when samples were dropped between the cursor and the first returned sample.</summary>
    public bool Gap { get; init; }
}

/// <summary>
/// One telemetry sample (design §8.1), taken every 5 s by the agent: host memory, per-GPU telemetry, GPU processes and
/// the filtered raw <c>/metrics</c> text of every Ready model. Parsing and KPI math stay on the master
/// (<c>PrometheusText</c>, <c>ModelKpiCalculator</c>).
/// </summary>
public sealed record NodeSample
{
    /// <summary>Per-boot sample sequence number.</summary>
    public required long Seq { get; init; }

    /// <summary>When the sample was taken.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Memory at the sample.</summary>
    public required NodeMemorySnapshot Memory { get; init; }

    /// <summary>Per-GPU telemetry; empty when unavailable.</summary>
    public IReadOnlyList<GpuSample> Gpus { get; init; } = [];

    /// <summary>GPU compute processes with owners (hostd on Linux). Null when unavailable (no hostd, older hostd, no <c>nvidia-smi</c>).</summary>
    public IReadOnlyList<GpuProcess>? GpuProcesses { get; init; }

    /// <summary>One scrape per Ready model.</summary>
    public IReadOnlyList<ModelMetricsScrape> Models { get; init; } = [];
}

/// <summary>Telemetry of one GPU at a sample (design §8.1). Any value can be null when the driver does not report it.</summary>
public sealed record GpuSample
{
    /// <summary><c>nvidia-smi</c> index.</summary>
    public required int Index { get; init; }

    /// <summary>GPU UUID.</summary>
    public string? Uuid { get; init; }

    /// <summary>Utilization percent.</summary>
    public double? UtilizationPercent { get; init; }

    /// <summary>Power draw in watts.</summary>
    public double? PowerWatts { get; init; }

    /// <summary>Temperature in °C.</summary>
    public double? TemperatureC { get; init; }

    /// <summary>SM clock in MHz.</summary>
    public double? SmClockMHz { get; init; }

    /// <summary>VRAM used (discrete GPUs).</summary>
    public double? MemoryUsedGiB { get; init; }

    /// <summary>VRAM total (discrete GPUs).</summary>
    public double? MemoryTotalGiB { get; init; }

    /// <summary>
    /// VRAM free as <c>nvidia-smi memory.free</c> reports it (discrete GPUs; MN-101 for MN-104/MN-105, D73). Omitted when
    /// null, so P1 samples are unchanged; a reader without it derives free from total − used.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MemoryFreeGiB { get; init; }

    /// <summary>Product name, e.g. <c>NVIDIA GeForce RTX 4060 Ti</c> (MN-101, D73). Omitted when null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    /// <summary>Today's single-GPU view (<see cref="GpuSnapshot"/>), for code that predates nodes.</summary>
    public GpuSnapshot ToSnapshot() => new(UtilizationPercent, PowerWatts, TemperatureC, SmClockMHz);
}

/// <summary>
/// The raw, filtered Prometheus text of one model's <c>/metrics</c> at a sample (design §8.1): only the metric families
/// <c>ModelKpiCalculator</c> and <c>BenchmarkSuite</c> use (<c>vllm:*</c> today). One failing scrape does not affect the
/// others.
/// </summary>
public sealed record ModelMetricsScrape
{
    /// <summary>Model id.</summary>
    public required string ModelId { get; init; }

    /// <summary>When the scrape completed.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Filtered Prometheus text exposition. Null when the scrape failed.</summary>
    public string? Text { get; init; }

    /// <summary>Why the scrape failed. Null on success.</summary>
    public string? Error { get; init; }
}

/// <summary>Query of <c>GET /agent/v1/gpu/idle</c> (design §4.3): sample GPU utilization for a few seconds before a benchmark.</summary>
public sealed record GpuIdleQuery
{
    /// <summary>Sampling duration in seconds (default 5).</summary>
    public int Seconds { get; init; } = 5;

    /// <summary>GPU indices to sample; null = all.</summary>
    public IReadOnlyList<int>? Gpus { get; init; }
}

/// <summary><c>GET /agent/v1/gpu/idle</c> response (design §4.3).</summary>
public sealed record GpuIdleResponse
{
    /// <summary>Average utilization across the sampled GPUs; null when no GPU telemetry is available.</summary>
    public double? AveragePercent { get; init; }

    /// <summary>
    /// Highest single reading across the sampled GPUs; null when no GPU telemetry is available. Parity with today's
    /// <c>GpuIdleReading.MaxPercent</c> (W2 review MN-092).
    /// </summary>
    public double? MaxPercent { get; init; }

    /// <summary>Number of samples taken.</summary>
    public int Samples { get; init; }

    /// <summary>Per-GPU averages.</summary>
    public IReadOnlyList<GpuIdleSample> PerGpu { get; init; } = [];
}

/// <summary>One GPU's average utilization over a <c>/gpu/idle</c> window.</summary>
/// <param name="Index"><c>nvidia-smi</c> index.</param>
/// <param name="Uuid">GPU UUID, when known.</param>
/// <param name="AveragePercent">Average utilization; null when not reported.</param>
/// <param name="Samples">Samples taken for this GPU.</param>
public sealed record GpuIdleSample(int Index, string? Uuid, double? AveragePercent, int Samples)
{
    /// <summary>Highest reading of this GPU in the window; null when not reported (W2 review MN-092).</summary>
    public double? MaxPercent { get; init; }
}
