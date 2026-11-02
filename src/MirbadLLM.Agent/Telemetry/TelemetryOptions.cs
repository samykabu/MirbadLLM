using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// Settings of the agent's telemetry package (design §8.1). Defaults match today's <c>MetricsOptions</c> on the master
/// (5 s interval, 3 s per-model scrape timeout) and the protocol's 15-minute ring.
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>How often the sampler takes a <see cref="NodeSample"/> (<c>MetricsSampleIntervalSeconds</c>, sent by the master on connect).</summary>
    public TimeSpan SampleInterval { get; set; } = Protocol.TelemetryInterval;

    /// <summary>Per-model <c>/metrics</c> scrape timeout; one slow model must not delay the others.</summary>
    public TimeSpan ScrapeTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Bound on the hostd GPU-process call, so a slow hostd never stalls a pass (as <c>HostAgentGpuProcessProbe</c>).</summary>
    public TimeSpan GpuProcessTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the ring keeps samples.</summary>
    public TimeSpan RingWindow { get; set; } = Protocol.TelemetryRingWindow;

    /// <summary>Hard cap on ring entries whatever the interval (15 min at 1 s = 900).</summary>
    public int RingCapacity { get; set; } = 1_000;

    /// <summary>Samples per <c>/telemetry</c> page when the query has no <c>max</c> (15 min at 5 s).</summary>
    public int DefaultPageSize { get; set; } = 180;

    /// <summary>Largest <c>max</c> a <c>/telemetry</c> query may ask for.</summary>
    public int MaxPageSize { get; set; } = 1_000;

    /// <summary>Spacing of the readings a <c>/gpu/idle</c> call takes (today's <c>GpuIdleSampleInterval</c>).</summary>
    public TimeSpan GpuIdleSampleInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest <c>/gpu/idle</c> window in seconds (the master's call policy allows seconds + 10 s).</summary>
    public int GpuIdleMaxSeconds { get; set; } = 60;

    /// <summary>
    /// Forces the node's memory model. Null = derived from the GPUs: discrete when any GPU reports its own VRAM total,
    /// unified otherwise (GB10 reports no memory fields).
    /// </summary>
    public MemoryModel? MemoryModel { get; set; }
}
