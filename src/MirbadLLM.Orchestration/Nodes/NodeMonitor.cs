using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Orchestration.Nodes;

/// <summary>
/// The master's node monitor (design §3.4–3.6, §4.6, §4.7), a hosted service that feeds <see cref="NodeStatusCache"/>.
/// <para><b>Per node</b> one loop: take a client from the factory (a new one on every reconnect, so certificate rotations
/// and handler rebuilds apply), <see cref="NodeStatusCache.SynchronizeAsync"/> (<c>/info</c>, negotiation, identity and
/// fencing checks, capabilities, snapshot), then <c>/events?bootId={boot}&amp;afterSeq={seq}</c> from the snapshot cursor.
/// A reset, overflow, boot change or seq gap drops the stream and re-snapshots at once (with backoff when it repeats
/// without progress); a dropped stream or failed call reconnects after an exponential backoff (1 s → 30 s). A blocked
/// node (pending approval, incompatible, identity mismatch, pending takeover) gets only <c>/info</c>, every
/// <see cref="NodeMonitorOptions.BlockedPollInterval"/>.</para>
/// <para><b>Operation references</b> (design §4.4 "Master side", §4.6): after every snapshot the monitor closes the open
/// references whose operation the snapshot shows terminal, and observes (<c>GET /operations/{id}</c>) the node's other
/// open references that the snapshot does not list as active; terminal <see cref="OperationEvent"/>s close references as
/// they arrive. So open references are resumed when the master boots and after every reconnect.</para>
/// <para><b>Shared loop</b>: every <see cref="NodeMonitorOptions.LivenessInterval"/> the Offline rule is re-evaluated;
/// every <see cref="NodeMonitorOptions.RegistrySyncInterval"/> the node registry and the model placements are re-read
/// (nodes added, approved, re-enrolled, revoked). All timing goes through the <see cref="TimeProvider"/>.</para>
/// <para>Not registered in DI here (MN-040). The client factory must be refreshed from the registry by its owner
/// (<c>AgentClientFactory.RefreshAsync</c>); the monitor reads the registry but does not rebuild handlers.</para>
/// </summary>
public sealed partial class NodeMonitor : BackgroundService
{
    private readonly INodeRegistry _registry;
    private readonly NodeStatusCache _cache;
    private readonly TimeProvider _time;
    private readonly AgentOperationRunner? _runner;
    private readonly IAgentOperationReferenceStore? _references;
    private readonly ILogger _logger;
    private readonly NodeMonitorOptions _options;
    private readonly ModelRecoveryState? _recovery;
    private readonly object _gate = new();
    private readonly Dictionary<string, NodeWorker> _workers = new(StringComparer.Ordinal);
    private CancellationToken _stopping;
    private bool _running;

    /// <summary>Creates the monitor.</summary>
    /// <param name="registry">Node registry (node set, approval, uid; last contact and capabilities are written back on connect).</param>
    /// <param name="cache">The cache it feeds (its <see cref="NodeStatusCache.Options"/> are used).</param>
    /// <param name="timeProvider">Clock for backoff, polls and the liveness loop.</param>
    /// <param name="runner">Operation runner closing and observing references; null = references are not followed.</param>
    /// <param name="references">The reference store (open references to observe after a snapshot); null = only close from statuses.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="recovery">Durable running-model intent; null disables restart recovery.</param>
    public NodeMonitor(
        INodeRegistry registry,
        NodeStatusCache cache,
        TimeProvider timeProvider,
        AgentOperationRunner? runner = null,
        IAgentOperationReferenceStore? references = null,
        ILogger<NodeMonitor>? logger = null,
        ModelRecoveryState? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _registry = registry;
        _cache = cache;
        _time = timeProvider;
        _runner = runner;
        _references = references;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _options = cache.Options;
        _recovery = recovery;
        _cache.ResyncRequested = RequestResync;
        if (_runner is not null)
        {
            // Fencing and protocol refusals of mutations sent by any caller reach the node state (W3 review MN-094).
            _runner.AgentErrorObserver = (nodeId, error) => _cache.ReportAgentError(nodeId, error);
        }
    }

    /// <summary>
    /// Re-reads the registry now (call after enrolling, approving, revoking or re-addressing a node): updates the cache's
    /// node set and model index, starts loops for new nodes and stops those of removed or re-enrolled ones.
    /// </summary>
    public async Task SyncRegistryAsync(CancellationToken ct = default)
    {
        IReadOnlyList<NodeRecord> records = await _registry.ListAsync(ct).ConfigureAwait(false);
        _cache.SetNodes(records);
        try
        {
            await _cache.RefreshModelsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NodeMonitorLog.ModelIndexFailed(_logger, ex.Message);
        }

        List<NodeWorker> stopped = [];
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            // A record without a pin (the bootstrap node before its agent enrolled) has no client and could only log failed
            // attempts with a growing backoff; its worker starts once enrollment writes the pin (W4 review MN-096).
            Dictionary<string, NodeRecord> active = records
                .Where(r => !r.Revoked && (r.CertSpkiSha256 is not null || r.PendingCertSpkiSha256 is not null))
                .ToDictionary(r => r.Id, StringComparer.Ordinal);
            foreach ((string nodeId, NodeWorker worker) in _workers.ToList())
            {
                if (!active.TryGetValue(nodeId, out NodeRecord? record) || !string.Equals(record.Uid, worker.Uid, StringComparison.Ordinal))
                {
                    _workers.Remove(nodeId);
                    stopped.Add(worker);
                }
            }

            foreach (NodeRecord record in active.Values)
            {
                if (!_workers.ContainsKey(record.Id))
                {
                    _workers[record.Id] = new NodeWorker(record.Id, record.Uid, w => RunNodeAsync(w), _stopping);
                    NodeMonitorLog.WorkerStarted(_logger, record.Id);
                }
            }
        }

        foreach (NodeWorker worker in stopped)
        {
            await worker.StopAsync().ConfigureAwait(false);
            NodeMonitorLog.WorkerStopped(_logger, worker.NodeId);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            _stopping = stoppingToken;
            _running = true;
        }

        try
        {
            await SyncRegistrySafeAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_options.LivenessInterval, _time);
            DateTimeOffset lastSync = _time.GetUtcNow();
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                _cache.EvaluateLiveness();
                DateTimeOffset now = _time.GetUtcNow();
                if (now - lastSync >= _options.RegistrySyncInterval)
                {
                    lastSync = now;
                    await SyncRegistrySafeAsync(stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        finally
        {
            List<NodeWorker> workers;
            lock (_gate)
            {
                _running = false;
                workers = [.. _workers.Values];
                _workers.Clear();
            }

            foreach (NodeWorker worker in workers)
            {
                await worker.StopAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task SyncRegistrySafeAsync(CancellationToken ct)
    {
        try
        {
            await SyncRegistryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NodeMonitorLog.RegistrySyncFailed(_logger, ex.Message);
        }
    }

    private void RequestResync(string nodeId)
    {
        lock (_gate)
        {
            if (_workers.TryGetValue(nodeId, out NodeWorker? worker))
            {
                worker.Wake();
            }
        }
    }

    /// <summary>One node's loop: synchronize, stream, reconnect with backoff.</summary>
    private async Task RunNodeAsync(NodeWorker worker)
    {
        CancellationToken ct = worker.Token;
        string nodeId = worker.NodeId;
        TimeSpan backoff = _options.ReconnectInitialDelay;
        int idleResyncs = 0;
        while (!ct.IsCancellationRequested)
        {
            NodeSyncResult result;
            try
            {
                result = await _cache.SynchronizeAsync(nodeId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _cache.RecordFailure(nodeId, ex);
                NodeMonitorLog.SyncFailed(_logger, nodeId, ex.Message, backoff);
                await WaitAsync(worker, backoff).ConfigureAwait(false);
                backoff = Next(backoff);
                continue;
            }

            if (result.Status == NodeSyncStatus.Blocked)
            {
                await WaitAsync(worker, _options.BlockedPollInterval).ConfigureAwait(false);
                continue;
            }

            backoff = _options.ReconnectInitialDelay;
            await OnSynchronizedAsync(nodeId, result, ct).ConfigureAwait(false);

            (StreamEnd end, bool progressed) = await StreamAsync(worker, result).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                break;
            }

            if (end == StreamEnd.Resync)
            {
                // Reset, overflow, boot change or gap: re-snapshot now, unless that keeps happening without progress.
                idleResyncs = progressed ? 0 : idleResyncs + 1;
                if (idleResyncs <= 1)
                {
                    continue;
                }
            }

            await WaitAsync(worker, backoff).ConfigureAwait(false);
            backoff = Next(backoff);
        }
    }

    private TimeSpan Next(TimeSpan delay)
    {
        TimeSpan doubled = delay + delay;
        return doubled > _options.ReconnectMaxDelay ? _options.ReconnectMaxDelay : doubled;
    }

    /// <summary>Waits on the monitor's clock; a resync request (<see cref="NodeWorker.Wake"/>) ends the wait early.</summary>
    private async Task WaitAsync(NodeWorker worker, TimeSpan delay)
    {
        CancellationToken token = worker.BeginWait();
        try
        {
            await Task.Delay(delay, _time, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!worker.Token.IsCancellationRequested)
        {
            // Woken.
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        finally
        {
            worker.EndWait();
        }
    }

    private async Task<(StreamEnd End, bool Progressed)> StreamAsync(NodeWorker worker, NodeSyncResult result)
    {
        string nodeId = worker.NodeId;
        bool progressed = false;
        CancellationToken token = worker.BeginWait();
        try
        {
            var query = new EventsQuery { BootId = result.BootId, AfterSeq = result.Seq };
            DateTimeOffset capabilitiesReadAt = _time.GetUtcNow();
            await foreach (AgentEvent agentEvent in result.Client.SubscribeEventsAsync(query, token).ConfigureAwait(false))
            {
                NodeEventResult applied = _cache.ApplyEvent(nodeId, agentEvent);
                if (applied == NodeEventResult.Resync)
                {
                    NodeMonitorLog.Resync(_logger, nodeId, agentEvent.EventType, agentEvent.BootId, agentEvent.Seq);
                    return (StreamEnd.Resync, progressed);
                }

                // At a heartbeat (every queued event is applied by then), now and then: capabilities change without an
                // event, e.g. when an executable version finishes verifying after the connect snapshot.
                if (agentEvent is HeartbeatEvent && _options.CapabilityRefreshInterval > TimeSpan.Zero &&
                    _time.GetUtcNow() - capabilitiesReadAt >= _options.CapabilityRefreshInterval)
                {
                    capabilitiesReadAt = _time.GetUtcNow();
                    await RefreshCapabilitiesAsync(nodeId, result, token).ConfigureAwait(false);
                }

                progressed = true;
                if (applied == NodeEventResult.Applied && agentEvent is RuntimeEvent runtime && _cache.GetNode(nodeId) is { } live)
                {
                    _recovery?.Observe(live, runtime.ModelId);
                }

                if (applied == NodeEventResult.Applied && agentEvent is OperationEvent { Operation: { } operation } && operation.State.IsTerminal())
                {
                    await CloseReferenceAsync(operation, worker.Token).ConfigureAwait(false);
                }
            }

            NodeMonitorLog.StreamClosed(_logger, nodeId);
            return (StreamEnd.Closed, progressed);
        }
        catch (OperationCanceledException) when (!worker.Token.IsCancellationRequested)
        {
            return (StreamEnd.Resync, progressed); // Woken by a resync request.
        }
        catch (OperationCanceledException)
        {
            return (StreamEnd.Closed, progressed);
        }
        catch (Exception ex)
        {
            _cache.RecordFailure(nodeId, ex);
            NodeMonitorLog.StreamFailed(_logger, nodeId, ex.Message);
            return (StreamEnd.Failed, progressed);
        }
        finally
        {
            worker.EndWait();
        }
    }

    /// <summary>
    /// Reads the node's capabilities again; when they changed, the cache and the registry take them (placement, the editor's
    /// runtime choices and the fit read the registry's copy). A failed read is logged and left for the next interval.
    /// </summary>
    private async Task RefreshCapabilitiesAsync(string nodeId, NodeSyncResult result, CancellationToken ct)
    {
        NodeCapabilities capabilities;
        try
        {
            capabilities = await result.Client.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NodeMonitorLog.CapabilityRefreshFailed(_logger, nodeId, ex.Message);
            return;
        }

        if (!_cache.UpdateCapabilities(nodeId, capabilities))
        {
            return;
        }

        NodeMonitorLog.CapabilitiesChanged(_logger, nodeId);
        try
        {
            await _registry.UpdateAsync(
                nodeId,
                r => string.Equals(r.Uid, result.NodeUid, StringComparison.Ordinal) ? r with { LastCapabilities = capabilities } : r,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NodeMonitorLog.RegistryWriteFailed(_logger, nodeId, ex.Message);
        }
    }

    /// <summary>After a snapshot: persist contact and capabilities, close and observe the node's open references.</summary>
    private async Task OnSynchronizedAsync(string nodeId, NodeSyncResult result, CancellationToken ct)
    {
        if (_cache.GetNode(nodeId) is { } live)
        {
            _recovery?.Observe(live);
        }

        DateTimeOffset now = _time.GetUtcNow();
        try
        {
            await _registry.UpdateAsync(
                nodeId,
                r => string.Equals(r.Uid, result.NodeUid, StringComparison.Ordinal)
                    ? r with { LastContact = now, LastCapabilities = result.Capabilities ?? r.LastCapabilities }
                    : r,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NodeMonitorLog.RegistryWriteFailed(_logger, nodeId, ex.Message);
        }

        if (_runner is null || result.Snapshot is not { } snapshot)
        {
            return;
        }

        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (OperationStatus operation in snapshot.Operations)
        {
            if (operation.State.IsTerminal())
            {
                await CloseReferenceAsync(operation, ct).ConfigureAwait(false);
            }
            else
            {
                active.Add(operation.Id);
            }
        }

        if (_references is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<AgentOperationReference> open = await _references.ListOpenAsync(ct).ConfigureAwait(false);
            foreach (AgentOperationReference reference in open)
            {
                if (string.Equals(reference.NodeId, nodeId, StringComparison.Ordinal) && !active.Contains(reference.OperationId))
                {
                    await _runner.ObserveAsync(reference, ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NodeMonitorLog.ReferencesFailed(_logger, nodeId, ex.Message);
        }
    }

    private async Task CloseReferenceAsync(OperationStatus operation, CancellationToken ct)
    {
        if (_runner is null)
        {
            return;
        }

        try
        {
            await _runner.CloseFromStatusAsync(operation, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NodeMonitorLog.ReferencesFailed(_logger, operation.Id, ex.Message);
        }
    }

    private enum StreamEnd
    {
        /// <summary>Re-snapshot (reset, overflow, boot change, gap, or a resync request).</summary>
        Resync,

        /// <summary>The agent closed the stream.</summary>
        Closed,

        /// <summary>The stream failed (transport or agent error).</summary>
        Failed,
    }

    /// <summary>A node's loop and its wake-up handle (a resync request cancels the current stream or wait).</summary>
    private sealed class NodeWorker : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly object _lock = new();
        private CancellationTokenSource? _wait;
        private bool _wakePending;

        public NodeWorker(string nodeId, string uid, Func<NodeWorker, Task> run, CancellationToken stopping)
        {
            NodeId = nodeId;
            Uid = uid;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            Token = _stop.Token;
            Task = Task.Run(() => run(this), CancellationToken.None);
        }

        public string NodeId { get; }

        public string Uid { get; }

        public CancellationToken Token { get; }

        public Task Task { get; }

        /// <summary>A token for the next stream or wait: cancelled by <see cref="Wake"/> or by stopping.</summary>
        public CancellationToken BeginWait()
        {
            lock (_lock)
            {
                _wait?.Dispose();
                _wait = CancellationTokenSource.CreateLinkedTokenSource(Token);
                if (_wakePending)
                {
                    _wakePending = false;
                    _wait.Cancel();
                }

                return _wait.Token;
            }
        }

        public void EndWait()
        {
            lock (_lock)
            {
                _wait?.Dispose();
                _wait = null;
            }
        }

        public void Wake()
        {
            lock (_lock)
            {
                if (_wait is null)
                {
                    _wakePending = true;
                }
                else
                {
                    _wait.Cancel();
                }
            }
        }

        public async Task StopAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }

            Dispose();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _wait?.Dispose();
                _wait = null;
            }

            _stop.Dispose();
        }
    }

    private static partial class NodeMonitorLog
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Node monitor: watching node {NodeId}.")]
        public static partial void WorkerStarted(ILogger logger, string nodeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Node monitor: stopped watching node {NodeId}.")]
        public static partial void WorkerStopped(ILogger logger, string nodeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId}: synchronization failed ({Reason}); retrying in {Delay}.")]
        public static partial void SyncFailed(ILogger logger, string nodeId, string reason, TimeSpan delay);

        [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId}: event stream failed ({Reason}); reconnecting.")]
        public static partial void StreamFailed(ILogger logger, string nodeId, string reason);

        [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId}: the agent closed the event stream; reconnecting.")]
        public static partial void StreamClosed(ILogger logger, string nodeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId}: re-snapshot after {EventType} (boot {BootId}, seq {Seq}).")]
        public static partial void Resync(ILogger logger, string nodeId, string eventType, string bootId, long seq);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Node monitor: reading the node registry failed: {Reason}")]
        public static partial void RegistrySyncFailed(ILogger logger, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Node monitor: reading the model placements failed: {Reason}")]
        public static partial void ModelIndexFailed(ILogger logger, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId}: writing last contact to the registry failed: {Reason}")]
        public static partial void RegistryWriteFailed(ILogger logger, string nodeId, string reason);

        [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId}: its capabilities changed since the last read; stored the new report.")]
        public static partial void CapabilitiesChanged(ILogger logger, string nodeId);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Node {NodeId}: re-reading its capabilities failed: {Reason}")]
        public static partial void CapabilityRefreshFailed(ILogger logger, string nodeId, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Operation references ({Subject}): {Reason}")]
        public static partial void ReferencesFailed(ILogger logger, string subject, string reason);
    }
}
