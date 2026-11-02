using System.Text.Json.Serialization;

namespace MirbadLLM.Core;

/// <summary>Latency percentiles in seconds, estimated from a vLLM histogram over the sampling window.</summary>
public sealed record LatencyStats(double P50, double P95);

/// <summary>
/// KPIs for one model over the last sampling window (rates and percentiles are computed from the
/// difference between two consecutive <c>/metrics</c> scrapes). Nullable values mean "no data in the
/// window" (e.g. no finished requests), not zero.
/// </summary>
public sealed record ModelKpis(
    ModelMetrics Load,
    double RequestsPerMinute,
    LatencyStats? TimeToFirstToken,
    LatencyStats? InterTokenLatency,
    LatencyStats? EndToEndLatency,
    LatencyStats? QueueTime,
    double? PrefixCacheHitRate,
    double? SpecDecodeAcceptanceRate,
    double PreemptionsPerMinute,
    IReadOnlyDictionary<string, double> FinishedByReasonPerMinute,
    double ToolCallsPerMinute);

/// <summary>GPU telemetry. Any value can be null when the driver does not report it (GB10 reports no memory fields).</summary>
public sealed record GpuSnapshot(double? UtilizationPercent, double? PowerWatts, double? TemperatureC, double? SmClockMHz);

/// <summary>Where <see cref="HostSample.ModelMemoryGiB"/> comes from.</summary>
public enum ModelMemorySource
{
    /// <summary>Each running model's configured memory budget (no GPU process readout was available).</summary>
    Budget,

    /// <summary>GPU memory of the model container's processes, measured by nvidia-smi through the host agent.</summary>
    Measured,
}

public sealed record HostSample(DateTimeOffset At, MemorySnapshot Memory, GpuSnapshot? Gpu)
{
    /// <summary>
    /// Memory held per mirbadllm model at this sample, by model id, for every model whose container was running
    /// (Starting, Ready or Stopping): measured GPU memory of its container's processes when the host agent reports
    /// GPU processes, otherwise its configured budget (see <see cref="MemorySource"/>). Null when not sampled.
    /// </summary>
    public IReadOnlyDictionary<string, double>? ModelMemoryGiB { get; init; }

    public ModelMemorySource MemorySource { get; init; } = ModelMemorySource.Budget;

    /// <summary>
    /// GPU memory of identifiable non-model consumers (a host service such as ComfyUI or Ollama), by display name.
    /// Only with <see cref="ModelMemorySource.Measured"/>; anything unidentified stays in "other processes".
    /// </summary>
    public IReadOnlyDictionary<string, double>? OtherGpuGiB { get; init; }

    /// <summary>
    /// Per-GPU readings of a discrete node at this sample (design §8.3 "one chart per GPU plus host RAM"; P2, frozen by
    /// MN-101, D73; filled from the agent's telemetry by the master's collector, MN-120). Null on unified nodes and on
    /// samples from before P2 (<see cref="Gpu"/> keeps the single-GPU view).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<HostGpuSample>? Gpus { get; init; }
}

/// <summary>One GPU of a <see cref="HostSample"/> on a discrete node. Any value can be null when the driver does not report it.</summary>
/// <param name="Index"><c>nvidia-smi</c> index (display).</param>
/// <param name="Uuid">GPU UUID (the identity).</param>
/// <param name="UtilizationPercent">Utilization percent.</param>
/// <param name="MemoryUsedGiB">VRAM used.</param>
/// <param name="MemoryTotalGiB">VRAM total.</param>
/// <param name="ModelMemoryGiB">VRAM held per model id on this GPU (measured, or the per-GPU budget share); null when unknown.</param>
public sealed record HostGpuSample(
    int Index,
    string Uuid,
    double? UtilizationPercent,
    double? MemoryUsedGiB,
    double? MemoryTotalGiB,
    IReadOnlyDictionary<string, double>? ModelMemoryGiB = null);

/// <summary>
/// One GPU compute process on the host (host agent <c>GET /gpu-processes</c>): nvidia-smi's PID, name and memory, and
/// the Docker container (full id) or systemd unit that owns it, with the whitelist's display name for a managed unit.
/// <paramref name="GpuUuid"/> (P2, MN-106, D91) is the GPU the memory is on (<c>--query-compute-apps=gpu_uuid,…</c>), so a
/// model's memory can be shown per GPU; null from a host agent that predates it (the per-GPU figures are then an even
/// split) and omitted from JSON when null, so P1 payloads are unchanged. A process on several GPUs has one row per GPU.
/// </summary>
public sealed record GpuProcess(
    int Pid,
    string Name,
    long UsedMiB,
    string? ContainerId,
    string? Unit,
    string? DisplayName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GpuUuid = null);

/// <summary>Lists GPU compute processes with their owners. Null when unavailable (no host agent, an older one, no nvidia-smi).</summary>
public interface IGpuProcessProbe
{
    Task<IReadOnlyList<GpuProcess>?> GetAsync(CancellationToken ct = default);
}

public sealed record ModelSample(DateTimeOffset At, ModelKpis Kpis)
{
    /// <summary>
    /// False for the first scrape after a model became Ready: there is no previous scrape to diff against, so its
    /// per-second rates (tokens, requests) are not measurements and a chart shows a gap, not a zero.
    /// </summary>
    public bool HasRates { get; init; } = true;
}

public interface IGpuProbe
{
    /// <summary>Null when no GPU telemetry is available (e.g. dev box without <c>nvidia-smi</c>).</summary>
    Task<GpuSnapshot?> GetAsync(CancellationToken ct = default);
}

/// <summary>Rolling in-memory history filled by a background sampler (default: every 5 s, kept 1 h).</summary>
public interface IMetricsHistory
{
    IReadOnlyList<HostSample> GetHost(TimeSpan window);

    /// <summary>
    /// Host samples of one node (design §8.1 "host samples keyed by node"; frozen in Core by MN-101 for the P2 node
    /// selectors, MN-119/MN-120). Default: <see cref="GetHost(TimeSpan)"/> (the single node of P1).
    /// </summary>
    IReadOnlyList<HostSample> GetHost(string nodeId, TimeSpan window) => GetHost(window);
    IReadOnlyList<ModelSample> GetModel(string modelId, TimeSpan window);

    /// <summary>Most recent KPIs per model id (only models that were Ready at the last sample).</summary>
    IReadOnlyDictionary<string, ModelKpis> Latest();
}
