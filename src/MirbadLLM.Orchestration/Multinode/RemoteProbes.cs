using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>A node has not reported telemetry recently enough to answer from it (design §8.1, product principle 2).</summary>
public sealed class NodeTelemetryUnavailableException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public NodeTelemetryUnavailableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public NodeTelemetryUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner exception.</summary>
    public NodeTelemetryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Base of the master's remote probes: answers from one node's latest telemetry sample
/// (<see cref="INodeTelemetry.GetLatest"/>), never from a sample older than <see cref="INodeTelemetry.MaxSampleAge"/>
/// by the master's clock (a stale number is not shown as a current one). No agent call is made.
/// </summary>
public abstract class RemoteNodeProbe
{
    private readonly INodeTelemetry _telemetry;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a probe of <paramref name="nodeId"/>.</summary>
    protected RemoteNodeProbe(INodeTelemetry telemetry, string nodeId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _telemetry = telemetry;
        _timeProvider = timeProvider;
        NodeId = nodeId;
    }

    /// <summary>The node this probe reads.</summary>
    public string NodeId { get; }

    /// <summary>The latest sample when it is fresh, else null.</summary>
    protected NodeTelemetryReading? FreshReading() =>
        _telemetry.GetLatest(NodeId) is { } reading && _timeProvider.GetUtcNow() - reading.ReceivedAt <= _telemetry.MaxSampleAge
            ? reading
            : null;
}

/// <summary>
/// <see cref="IMemoryProbe"/> of one node from its latest telemetry (the host part, <see cref="NodeMemorySnapshot.ToHostSnapshot"/>).
/// Without a fresh sample it falls back to the memory of the node status cache while the node is serving (Online or
/// Degraded, i.e. contact within 15 s); otherwise it throws <see cref="NodeTelemetryUnavailableException"/>, as
/// <c>ProcMeminfoMemoryProbe</c> throws when <c>/proc/meminfo</c> cannot be read.
/// </summary>
public sealed class RemoteMemoryProbe(INodeTelemetry telemetry, string nodeId, TimeProvider timeProvider, INodeStatusCache? statusCache = null)
    : RemoteNodeProbe(telemetry, nodeId, timeProvider), IMemoryProbe
{
    /// <inheritdoc />
    public Task<MemorySnapshot> GetAsync(CancellationToken ct = default)
    {
        if (FreshReading() is { } reading)
        {
            return Task.FromResult(reading.Sample.Memory.ToHostSnapshot());
        }

        if (statusCache?.GetNode(NodeId) is { IsServing: true, Memory: { } memory })
        {
            return Task.FromResult(memory.ToHostSnapshot());
        }

        throw new NodeTelemetryUnavailableException($"Node '{NodeId}' has not reported memory recently.");
    }
}

/// <summary><see cref="IGpuProbe"/> of one node from its latest telemetry; null when stale or the node reports no GPU telemetry.</summary>
public sealed class RemoteGpuProbe(INodeTelemetry telemetry, string nodeId, TimeProvider timeProvider)
    : RemoteNodeProbe(telemetry, nodeId, timeProvider), IGpuProbe
{
    /// <inheritdoc />
    public Task<GpuSnapshot?> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(FreshReading() is { } reading ? RemoteTelemetryCollector.ToGpuSnapshot(reading.Sample.Gpus) : null);
}

/// <summary>
/// <see cref="IGpuProcessProbe"/> of one node from its latest telemetry; null when stale or unavailable on the node (no
/// hostd, older hostd, no <c>nvidia-smi</c>).
/// </summary>
public sealed class RemoteGpuProcessProbe(INodeTelemetry telemetry, string nodeId, TimeProvider timeProvider)
    : RemoteNodeProbe(telemetry, nodeId, timeProvider), IGpuProcessProbe
{
    /// <inheritdoc />
    public Task<IReadOnlyList<GpuProcess>?> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(FreshReading()?.Sample.GpuProcesses);
}

/// <summary>DI helpers for the master's telemetry split (registered by the master composition, MN-040).</summary>
public static class RemoteTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RemoteTelemetryCollector"/> (singleton, <see cref="INodeTelemetry"/>, hosted service) and
    /// the remote <see cref="IMemoryProbe"/>, <see cref="IGpuProbe"/> and <see cref="IGpuProcessProbe"/> of the
    /// bootstrap node (<c>MirbadLLM:Nodes:BootstrapNodeId</c>, the <see cref="InMemoryMetricsHistory.DefaultNodeId"/>),
    /// replacing local probe registrations. Expects <see cref="InMemoryMetricsHistory"/>, <see cref="INodeRegistry"/>,
    /// <see cref="IModelRegistry"/>, <c>IAgentClientFactory</c> and a <see cref="TimeProvider"/> to be registered;
    /// <see cref="INodeStatusCache"/> is used when present.
    /// </summary>
    public static IServiceCollection AddRemoteTelemetry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<MetricsOptions>();
        services.TryAddSingleton<RemoteTelemetryCollector>();
        services.TryAddSingleton<INodeTelemetry>(sp => sp.GetRequiredService<RemoteTelemetryCollector>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RemoteTelemetryCollector>());

        services.RemoveAll<IMemoryProbe>();
        services.RemoveAll<IGpuProbe>();
        services.RemoveAll<IGpuProcessProbe>();
        services.AddSingleton<IMemoryProbe>(sp => new RemoteMemoryProbe(
            sp.GetRequiredService<INodeTelemetry>(), BootstrapNodeId(sp), sp.GetRequiredService<TimeProvider>(), sp.GetService<INodeStatusCache>()));
        services.AddSingleton<IGpuProbe>(sp => new RemoteGpuProbe(
            sp.GetRequiredService<INodeTelemetry>(), BootstrapNodeId(sp), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IGpuProcessProbe>(sp => new RemoteGpuProcessProbe(
            sp.GetRequiredService<INodeTelemetry>(), BootstrapNodeId(sp), sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    private static string BootstrapNodeId(IServiceProvider services) =>
        services.GetRequiredService<IOptions<RegistryMigrationOptions>>().Value.EffectiveBootstrapNodeId;
}
