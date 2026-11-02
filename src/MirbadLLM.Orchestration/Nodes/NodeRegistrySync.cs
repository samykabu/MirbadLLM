using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Orchestration.Nodes;

/// <summary>
/// Keeps the master's <see cref="AgentClientFactory"/> in line with the node registry (MN-040). The factory only learns
/// about enrollments, approvals, pin and address changes when its owner calls <see cref="AgentClientFactory.RefreshAsync"/>
/// (<see cref="NodeMonitor"/> reads the registry but never rebuilds handlers), and nothing in the registry raises an event,
/// so this service re-reads <c>nodes.json</c> every <see cref="NodeMonitorOptions.RegistrySyncInterval"/> and refreshes the
/// factory, then the monitor, when a connection-relevant field of any record changed (id, uid, addresses, pins, approval,
/// revocation).
/// <para>Its <see cref="StartAsync"/> refreshes the factory once before returning, so registered ahead of
/// <see cref="NodeMonitor"/> the monitor's first loops already find a client for every enrolled node.</para>
/// </summary>
public sealed partial class NodeRegistrySync : IHostedService, IDisposable
{
    private readonly INodeRegistry _registry;
    private readonly AgentClientFactory _clients;
    private readonly NodeMonitor? _monitor;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task _loop = Task.CompletedTask;
    private string? _fingerprint;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    /// <param name="registry">The node registry.</param>
    /// <param name="clients">The factory to refresh.</param>
    /// <param name="timeProvider">Clock of the poll loop.</param>
    /// <param name="monitor">The node monitor, re-synchronized after a change; null = factory only.</param>
    /// <param name="interval">Poll interval; null = <see cref="NodeMonitorOptions.RegistrySyncInterval"/>'s default.</param>
    /// <param name="logger">Logger.</param>
    public NodeRegistrySync(
        INodeRegistry registry,
        AgentClientFactory clients,
        TimeProvider timeProvider,
        NodeMonitor? monitor = null,
        TimeSpan? interval = null,
        ILogger<NodeRegistrySync>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _registry = registry;
        _clients = clients;
        _time = timeProvider;
        _monitor = monitor;
        _interval = interval is { } given && given > TimeSpan.Zero ? given : new NodeMonitorOptions().RegistrySyncInterval;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Re-reads the registry and, when a connection-relevant field changed since the last pass (or on the first pass),
    /// refreshes the factory and then the monitor. Returns true when it refreshed.
    /// </summary>
    public async Task<bool> SyncAsync(CancellationToken ct = default)
    {
        IReadOnlyList<NodeRecord> records = await _registry.ListAsync(ct).ConfigureAwait(false);
        string fingerprint = Fingerprint(records);
        if (string.Equals(fingerprint, _fingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        await _clients.RefreshAsync(ct).ConfigureAwait(false);
        if (_monitor is not null)
        {
            await _monitor.SyncRegistryAsync(ct).ConfigureAwait(false);
        }

        _fingerprint = fingerprint;
        return true;
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await SyncSafeAsync(cancellationToken).ConfigureAwait(false);
        _loop = RunAsync(_stopping.Token);
    }

    /// <summary>
    /// Stops the poll loop and waits for it. Safe after <see cref="Dispose"/> and when called twice: a host whose
    /// provider was disposed first (a crashed test master, a failed start) still stops cleanly (MN-044 note, MN-047).
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancelLoop();
        await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        CancelLoop(); // a loop still running ends instead of polling a disposed registry
        _stopping.Dispose();
    }

    private void CancelLoop()
    {
        try
        {
            _stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed meanwhile: the loop was cancelled before the source was disposed.
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval, _time);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await SyncSafeAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task SyncSafeAsync(CancellationToken ct)
    {
        try
        {
            await SyncAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Text.Json.JsonException or ObjectDisposedException)
        {
            // The next pass retries; a node without a client meanwhile reads Offline in the cache.
            Log.SyncFailed(_logger, ex);
        }
    }

    /// <summary>The fields that decide a node's client (address, identity, pins) and whether the monitor follows it.</summary>
    private static string Fingerprint(IReadOnlyList<NodeRecord> records)
    {
        var text = new StringBuilder();
        foreach (NodeRecord r in records.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            text.Append(r.Id).Append('|').Append(r.Uid).Append('|').Append(r.Address).Append('|')
                .AppendJoin(',', r.AltAddresses).Append('|')
                .Append(r.CertSpkiSha256).Append('|').Append(r.PendingCertSpkiSha256).Append('|')
                .Append(r.Approved ? 'a' : '-').Append(r.Revoked ? 'r' : '-').Append('\n');
        }

        return text.ToString();
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not synchronize the agent clients with the node registry; retrying.")]
        public static partial void SyncFailed(ILogger logger, Exception exception);
    }
}
