namespace MirbadLLM.Core;

public enum ModelState
{
    Stopped,
    Starting,
    Ready,
    Stopping,
    Failed,
}

/// <summary>Runtime view of one model. <see cref="HostPort"/> is the published loopback port, when any.</summary>
/// <param name="RestartPending">True while the container (Starting or Ready) was launched with a configuration
/// that differs from the saved definition, so the saved settings only take effect after a restart. False when
/// unknown (e.g. a container started before launch configurations were recorded).</param>
public sealed record ModelStatus(
    string Id,
    ModelState State,
    string? ContainerId = null,
    int? HostPort = null,
    DateTimeOffset? Since = null,
    string? Error = null,
    bool RestartPending = false);

/// <summary>Where the router sends traffic for a served model name.</summary>
public sealed record ModelEndpoint(string ModelId, string ServedName, Uri BaseUri, ModelState State);

/// <summary>Host unified-memory snapshot (DGX Spark: CPU and GPU share it).</summary>
public sealed record MemorySnapshot(double TotalGiB, double AvailableGiB);

/// <summary>Live numbers scraped from a vLLM <c>/metrics</c> endpoint.</summary>
public sealed record ModelMetrics(
    double RunningRequests,
    double WaitingRequests,
    double KvCacheUsage,
    double GenerationTokensPerSecond,
    double PromptTokensPerSecond);

/// <summary>Per-served-model counters kept by the router.</summary>
public sealed record RouterModelStats(string ServedName, long TotalRequests, long FailedRequests, int InFlight);
