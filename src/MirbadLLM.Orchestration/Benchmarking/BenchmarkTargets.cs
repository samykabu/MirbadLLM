using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// The data path a benchmark's load took (design §6.5 item 5 "provenance"), recorded on <see cref="BenchmarkRun.Proxy"/>
/// and <see cref="TuningRun.Proxy"/>. A run recorded before the field existed (null) was <see cref="Direct"/>.
/// </summary>
public static class BenchmarkDataPaths
{
    /// <summary>
    /// Straight to the model's container (published loopback port or container network), as before P1: what a run recorded
    /// before the field existed means. The master composition never sends load this way.
    /// </summary>
    public const string Direct = "direct";

    /// <summary>
    /// Through the agent's inference proxy over mTLS, the agent on the master's own host: a loopback agent address, or the
    /// co-located bootstrap node reached over the host's Docker network (<see cref="AgentJobTargetsOptions.CoLocatedNodeIds"/>).
    /// </summary>
    public const string AgentLoopback = "agent-mtls-loopback";

    /// <summary>Through the agent's inference proxy over mTLS across the LAN.</summary>
    public const string AgentLan = "agent-mtls-lan";
}

/// <summary>Where a benchmark sends its requests: the base URI the suite resolves relative paths against, and the data path.</summary>
/// <param name="BaseUri">Ends with <c>/</c>; <c>v1/chat/completions</c>, <c>tokenize</c>, <c>metrics</c>… are resolved against it.</param>
/// <param name="DataPath">One of <see cref="BenchmarkDataPaths"/>.</param>
/// <param name="NodeId">The node the model runs on; null on the direct path.</param>
public sealed record BenchmarkEndpoint(Uri BaseUri, string DataPath, string? NodeId = null);

/// <summary>
/// Resolves a Ready model to the address a benchmark targets and the HTTP client to use for it (MN-033). The master's
/// implementation (<see cref="AgentJobTargets"/>) sends the load through the node's data client to the agent proxy, the
/// path clients use (design §6.5 item 6). The pre-P1 direct resolver (the container's own upstream) was deleted by MN-047
/// (D60).
/// </summary>
public interface IBenchmarkTargetResolver
{
    /// <summary>The endpoint for <paramref name="modelId"/>, whose current status is <paramref name="status"/>.</summary>
    /// <exception cref="InvalidOperationException">The model has no reachable address (no published port, unplaced).</exception>
    Task<BenchmarkEndpoint> ResolveAsync(string modelId, ModelStatus status, CancellationToken ct = default);

    /// <summary>
    /// The client for requests under <paramref name="baseUri"/> (a <see cref="BenchmarkEndpoint.BaseUri"/> this resolver
    /// returned); null = the suite's own client.
    /// </summary>
    HttpClient? GetClient(Uri baseUri);
}

/// <summary>Samples GPU utilization on the node of a model before measuring (design §4.3 <c>/gpu/idle</c>).</summary>
public interface IGpuIdleSampler
{
    /// <summary>
    /// Samples for about <paramref name="duration"/> on the node that runs <paramref name="modelId"/> (null = the
    /// bootstrap node). A reading with no samples when telemetry is unavailable; never throws for transport errors.
    /// </summary>
    Task<GpuIdleReading> SampleAsync(string? modelId, TimeSpan duration, CancellationToken ct = default);
}

/// <summary>
/// Boot recovery of interrupted tuning runs waits on this (design §6.5 item 7, §6.7): candidates are cleaned up and the
/// saved settings restored only after the nodes' open operations are reconciled, never blindly on reconnect.
/// <para>Per node (P2, MN-111; W3 review MN-094): when <see cref="NodeIds"/> lists nodes, each node's clean-up waits only
/// on <see cref="WaitUntilNodeReconciledAsync"/> for that node, so an Offline node no longer holds the others back. The
/// default members keep the single global wait (an implementation with no node list is a valid one).</para>
/// </summary>
public interface IJobRecoveryGate
{
    /// <summary>Completes once recovery may act on every node; cancelled with <paramref name="ct"/>.</summary>
    Task WaitUntilReconciledAsync(CancellationToken ct = default);

    /// <summary>
    /// The nodes boot recovery runs for, each gated on its own. Empty (the default) = one global
    /// <see cref="WaitUntilReconciledAsync"/>, then recovery of every node.
    /// </summary>
    IReadOnlyList<string> NodeIds => [];

    /// <summary>
    /// True when the gate is per node (<see cref="NodeIds"/> may grow after boot as the node set is loaded or nodes are
    /// enrolled): recovery then watches the list and recovers each node once, as it appears. False (the default): one
    /// global wait, then recovery of every node.
    /// </summary>
    bool IsPerNode => false;

    /// <summary>
    /// Completes once recovery may act on <paramref name="nodeId"/> (that node's open operations are reconciled),
    /// whatever the other nodes' state; cancelled with <paramref name="ct"/>. Default: <see cref="WaitUntilReconciledAsync"/>.
    /// </summary>
    Task WaitUntilNodeReconciledAsync(string nodeId, CancellationToken ct = default) => WaitUntilReconciledAsync(ct);
}
