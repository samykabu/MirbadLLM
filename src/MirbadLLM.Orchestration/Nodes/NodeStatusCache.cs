using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Nodes;

/// <summary>Tunables of <see cref="NodeStatusCache"/> and <see cref="NodeMonitor"/> (defaults = design §3.4–3.6).</summary>
public sealed class NodeMonitorOptions
{
    /// <summary>No contact for longer than this makes a node Offline (design §3.5: 15 s, 3 missed heartbeats).</summary>
    public TimeSpan OfflineAfter { get; init; } = Protocol.OfflineAfter;

    /// <summary>First reconnect delay after a failure (design §3.4: 1 s).</summary>
    public TimeSpan ReconnectInitialDelay { get; init; } = Protocol.ReconnectInitialDelay;

    /// <summary>Largest reconnect delay (design §3.4: 30 s).</summary>
    public TimeSpan ReconnectMaxDelay { get; init; } = Protocol.ReconnectMaxDelay;

    /// <summary>
    /// How often a blocked node (pending approval, incompatible, identity mismatch, pending takeover) is asked for
    /// <c>/info</c> again. Shorter than <see cref="OfflineAfter"/> so a reachable blocked node does not show Offline.
    /// </summary>
    public TimeSpan BlockedPollInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often a connected node's capabilities are read again while its event stream is up (docs/macos/implementation-plan.md,
    /// Phase 3). Capabilities change after the connect snapshot without any event: an executable version finishes its first
    /// verification (the MLX folder takes seconds, llama.cpp on Windows minutes), or a runtime is installed with
    /// <c>update-mlx</c>. Zero turns the refresh off.
    /// </summary>
    public TimeSpan CapabilityRefreshInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the monitor re-evaluates liveness (the Offline rule).</summary>
    public TimeSpan LivenessInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often the monitor re-reads the node registry and the model placements.</summary>
    public TimeSpan RegistrySyncInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>An agent certificate expiring within this makes the node Degraded (design §3.5: 14 days).</summary>
    public TimeSpan CertificateExpiryWarning { get; init; } = TimeSpan.FromDays(14);

    /// <summary>Clock skew above which the node is Degraded (design §3.5: 30 s).</summary>
    public TimeSpan DegradedClockSkew { get; init; } = Protocol.DegradedClockSkew;

    /// <summary>The master's protocol range used for negotiation (design §3.6).</summary>
    public ProtocolRange MasterProtocol { get; init; } = Protocol.Range;
}

/// <summary>What <see cref="NodeStatusCache.ApplyEvent"/> did with an event.</summary>
public enum NodeEventResult
{
    /// <summary>A state event with the next seq: applied, cursor advanced.</summary>
    Applied,

    /// <summary>Contact only: a heartbeat at or below the cursor, or a state event already applied (<c>seq ≤</c> cursor).</summary>
    Ignored,

    /// <summary>The stream must be dropped and a new snapshot taken: reset, overflow, other boot, or a seq gap.</summary>
    Resync,
}

/// <summary>Outcome of <see cref="NodeStatusCache.SynchronizeAsync"/>.</summary>
public enum NodeSyncStatus
{
    /// <summary>Protocol negotiated and a snapshot applied: subscribe to events from <see cref="NodeSyncResult.Seq"/>.</summary>
    Synchronized,

    /// <summary>
    /// Only <c>/info</c> was called: the node is pending approval, incompatible, reports another identity, or needs a
    /// fencing takeover. Poll <c>/info</c> again later.
    /// </summary>
    Blocked,
}

/// <summary>A synchronization of one node.</summary>
/// <param name="Status">Synchronized or blocked.</param>
/// <param name="Client">The client used (one handler generation); subscribe with it, then get a new one on reconnect.</param>
/// <param name="Snapshot">The applied snapshot when synchronized.</param>
/// <param name="Capabilities">The capability report when synchronized.</param>
/// <param name="NodeUid">The registry uid the sync ran for.</param>
/// <param name="BootId">Agent boot of the cache cursor after the sync.</param>
/// <param name="Seq">Cache cursor after the sync (the snapshot's seq, or later when events were applied concurrently).</param>
public sealed record NodeSyncResult(
    NodeSyncStatus Status,
    IAgentClient Client,
    StatusSnapshot? Snapshot,
    NodeCapabilities? Capabilities,
    string NodeUid,
    string? BootId,
    long Seq);

/// <summary>
/// The master's runtime status cache (design §3.4–3.6, §4.7, §7.1), fed by <see cref="NodeMonitor"/>. Reads never call an
/// agent: every view is an immutable copy published under one lock, so the router can resolve a served name per request.
/// <para><b>Per node</b> it keeps the registry record, the last <c>/info</c> (version, protocol, accepted master triple,
/// clock skew), the last snapshot (memory, health, runtimes, open operations) and the event cursor
/// <c>(bootId, seq)</c>. <see cref="SynchronizeAsync"/> negotiates the protocol, checks identity and fencing, and applies
/// a snapshot; <see cref="ApplyEvent"/> applies events strictly in order: a state event must carry <c>seq = cursor + 1</c>
/// of the same boot, else the caller re-snapshots (gaps include events the client skipped as unreadable). The cursor never
/// moves on <c>reset</c> or <c>overflow</c>, and a heartbeat above the cursor is a gap (the agent sends a heartbeat only
/// after every queued event). An operation that ended is never re-opened by a later event.</para>
/// <para><b>Node state</b> (design §3.5): PendingApproval while the record is not approved; Offline after
/// <see cref="NodeMonitorOptions.OfflineAfter"/> without contact (or before the first snapshot of this master boot);
/// Incompatible when no protocol version overlaps or the agent reports another node id/uid; PendingTakeover when the agent
/// accepted a higher epoch, or the same epoch from another master (design §4.2), or a mutation was refused with
/// <c>stale_master</c>/<c>competing_master</c> (<see cref="ReportAgentError"/>); Degraded when the agent reports health
/// reasons, the clock skew exceeds 30 s or the certificate expires within 14 days; Online otherwise. Draining is the
/// record's flag, independent of the state.</para>
/// <para><b>Pending takeover</b> (design §4.2 step 3, §3.5; MN-115): the nodes that did not answer the latest takeover
/// (<see cref="MarkPendingTakeover"/>, seeded from <c>master.json</c>'s latest record at construction) are PendingTakeover
/// whether or not they are reachable, until a <c>/info</c> shows an accepted epoch below the master's (the node then leaves
/// the mark and connects normally: it accepts the master on its next mutation). One that returns holding an epoch not
/// below the master's stays blocked and needs the operator's continuation (<see cref="NodeFencingState.NeedsContinuation"/>
/// in <see cref="ListFencing"/>); there is no automatic continuation.</para>
/// <para><b>Served names</b> come from the placed models of the model registry (<see cref="RefreshModelsAsync"/>, called by
/// the monitor every registry sync; call it after a model write to see it at once). Without a model registry the index
/// falls back to the instances' served-name labels.</para>
/// <para>Not registered in DI here (MN-040).</para>
/// </summary>
public sealed partial class NodeStatusCache : INodeStatusCache
{
    private const int EndedOperationsKept = 2_000;

    private readonly IAgentClientFactory _clients;
    private readonly IMasterIdentity _identity;
    private readonly TimeProvider _time;
    private readonly IModelRegistry? _models;
    private readonly ILogger _logger;
    private readonly Func<string, string?>? _degradedReason;
    private readonly object _gate = new();
    private readonly Dictionary<string, NodeEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingTakeoverMark> _pendingTakeover = new(StringComparer.Ordinal);
    private ImmutableSortedDictionary<string, NodeView> _view = ImmutableSortedDictionary.Create<string, NodeView>(StringComparer.Ordinal);
    private ModelIndex? _modelIndex;
    private long _version;
    private TaskCompletionSource _changed = NewSignal();

    /// <summary>Creates the cache.</summary>
    /// <param name="clients">Agent clients (a client is taken per synchronization, never cached across reconnects).</param>
    /// <param name="identity">The master triple compared with the agents' accepted triple (fencing, design §4.2).</param>
    /// <param name="timeProvider">Clock for contact times, liveness, clock skew and certificate expiry.</param>
    /// <param name="models">Model registry for the served-name index; null = index from instance labels.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="options">Tunables; defaults per design.</param>
    /// <param name="degradedReason">
    /// A master-side Degraded reason per node id, or null (MN-130: the open CA rotation's <c>ca_rotation_pending</c>,
    /// <c>MasterTrustStore.PendingReason</c>); null = none (P1).
    /// </param>
    public NodeStatusCache(
        IAgentClientFactory clients,
        IMasterIdentity identity,
        TimeProvider timeProvider,
        IModelRegistry? models = null,
        ILogger<NodeStatusCache>? logger = null,
        NodeMonitorOptions? options = null,
        Func<string, string?>? degradedReason = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _clients = clients;
        _identity = identity;
        _time = timeProvider;
        _models = models;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        Options = options ?? new NodeMonitorOptions();
        _degradedReason = degradedReason;

        // A restarted master keeps the latest takeover's unreachable nodes pending (fail-safe: one that returned since and
        // holds a lower epoch leaves the mark on its first /info).
        if (identity.Takeovers is { Count: > 0 } takeovers)
        {
            SeedPendingTakeover(takeovers[^1]);
        }
    }

    /// <summary>The tunables (shared with the monitor).</summary>
    public NodeMonitorOptions Options { get; }

    /// <summary>Called (outside the lock) when a node's event stream should be dropped and the node re-synchronized.</summary>
    internal Action<string>? ResyncRequested { get; set; }

    /// <inheritdoc />
    public long Version => Interlocked.Read(ref _version);

    // ---- Reads (INodeStatusCache) ----

    /// <inheritdoc />
    public IReadOnlyList<NodeLiveStatus> ListNodes() => [.. Volatile.Read(ref _view).Values.Select(v => v.Status)];

    /// <inheritdoc />
    public NodeLiveStatus? GetNode(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        return Volatile.Read(ref _view).TryGetValue(nodeId, out NodeView? view) ? view.Status : null;
    }

    /// <inheritdoc />
    public NodeModelRuntime? GetRuntime(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        ImmutableSortedDictionary<string, NodeView> view = Volatile.Read(ref _view);
        if (Volatile.Read(ref _modelIndex) is { } index && index.NodeByModel.TryGetValue(modelId, out string? placed))
        {
            return view.TryGetValue(placed, out NodeView? node) && node.Runtimes.TryGetValue(modelId, out NodeModelRuntime? runtime) ? runtime : null;
        }

        foreach (NodeView node in view.Values)
        {
            if (node.Runtimes.TryGetValue(modelId, out NodeModelRuntime? runtime))
            {
                return runtime;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null)
    {
        ImmutableSortedDictionary<string, NodeView> view = Volatile.Read(ref _view);
        if (nodeId is not null)
        {
            return view.TryGetValue(nodeId, out NodeView? node) ? node.Status.Runtimes : [];
        }

        return [.. view.Values.SelectMany(v => v.Status.Runtimes)];
    }

    /// <inheritdoc />
    public ServedModelRoute? ResolveServedName(string servedName)
    {
        ArgumentNullException.ThrowIfNull(servedName);
        ImmutableSortedDictionary<string, NodeView> view = Volatile.Read(ref _view);
        if (Volatile.Read(ref _modelIndex) is { } index)
        {
            if (!index.ByServedName.TryGetValue(servedName, out PlacedModel? placed))
            {
                return null;
            }

            view.TryGetValue(placed.NodeId, out NodeView? node);
            NodeModelRuntime? runtime = null;
            node?.Runtimes.TryGetValue(placed.ModelId, out runtime);
            return Route(placed.ModelId, placed.NodeId, node, runtime);
        }

        // No model registry: the instances' served-name labels.
        foreach (NodeView node in view.Values)
        {
            foreach (NodeModelRuntime runtime in node.Status.Runtimes)
            {
                if (string.Equals(runtime.ServedName, servedName, StringComparison.OrdinalIgnoreCase))
                {
                    return Route(runtime.ModelId, node.Status.NodeId, node, runtime);
                }
            }
        }

        return null;
    }

    /// <summary>Completes when <see cref="Version"/> is above <paramref name="version"/> (pages and tests wait on it).</summary>
    public async Task WaitForChangeAsync(long version, CancellationToken ct = default)
    {
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (_version > version)
                {
                    return;
                }

                signal = _changed.Task;
            }

            await signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    // ---- Refreshes (INodeStatusCache) ----

    /// <summary>
    /// Re-reads one model's instance from its node (after a <c>model_not_running</c> answer, design §7.1). Best effort:
    /// an unreachable node is recorded as the node's last error, never thrown. An event for the model applied while the
    /// call ran wins over the read.
    /// </summary>
    public async Task RefreshModelAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        string? nodeId = NodeOfModel(modelId);
        if (nodeId is null)
        {
            return;
        }

        NodeEntry entry;
        string? bootId;
        long seqBefore;
        lock (_gate)
        {
            if (!_entries.TryGetValue(nodeId, out NodeEntry? found) || found.Phase != NodePhase.Connected)
            {
                return;
            }

            entry = found;
            bootId = entry.BootId;
            seqBefore = entry.Seq ?? 0;
        }

        RuntimeStatus? runtime;
        try
        {
            runtime = await _clients.GetClient(nodeId).GetRuntimeAsync(modelId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNodeFailure(ex, ct))
        {
            RecordFailure(nodeId, ex);
            return;
        }

        lock (_gate)
        {
            if (!IsCurrent(entry) || entry.BootId != bootId)
            {
                return;
            }

            DateTimeOffset now = _time.GetUtcNow();
            Contact(entry, now);
            if (!entry.RuntimeSeq.TryGetValue(modelId, out long lastSeq) || lastSeq <= seqBefore)
            {
                SetRuntime(entry, modelId, runtime, seqBefore, now);
            }

            PublishLocked(entry, now);
        }
    }

    /// <summary>
    /// Takes a full snapshot of one node now (<see cref="SynchronizeAsync"/>). Throws what the agent call threw (also
    /// recorded as the node's last error); <see cref="KeyNotFoundException"/> for a node the cache does not know.
    /// </summary>
    public async Task RefreshNodeAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        NodePhase before;
        lock (_gate)
        {
            before = Entry(nodeId).Phase;
        }

        NodeSyncResult result;
        try
        {
            result = await SynchronizeAsync(nodeId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNodeFailure(ex, ct))
        {
            RecordFailure(nodeId, ex);
            throw;
        }

        if (result.Status == NodeSyncStatus.Synchronized && before != NodePhase.Connected)
        {
            // The monitor may be waiting in a blocked poll: let it subscribe now.
            ResyncRequested?.Invoke(nodeId);
        }
    }

    // ---- Feeding (NodeMonitor) ----

    /// <summary>
    /// Synchronizes the node set with the registry: adds and updates non-revoked records, drops revoked and deleted ones.
    /// A record whose uid changed (re-enrolled) starts from an empty view.
    /// </summary>
    public void SetNodes(IReadOnlyList<NodeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (NodeRecord record in records.Where(r => !r.Revoked))
            {
                seen.Add(record.Id);
                if (_entries.TryGetValue(record.Id, out NodeEntry? entry) && string.Equals(entry.Record.Uid, record.Uid, StringComparison.Ordinal))
                {
                    bool changed = RecordChanged(entry.Record, record);
                    entry.Record = record;
                    if (changed)
                    {
                        PublishLocked(entry, now);
                    }

                    continue;
                }

                var fresh = new NodeEntry(record) { LastContact = record.LastContact, LastCapabilities = record.LastCapabilities };
                _entries[record.Id] = fresh;
                PublishLocked(fresh, now);
            }

            foreach (string removed in _entries.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                _entries.Remove(removed);
                _view = _view.Remove(removed);
                BumpLocked();
            }
        }
    }

    /// <summary>Rebuilds the served-name index from the model registry (no-op without one).</summary>
    public async Task RefreshModelsAsync(CancellationToken ct = default)
    {
        if (_models is null)
        {
            return;
        }

        SetModels(await _models.ListAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Replaces the served-name index with the placed models of <paramref name="models"/>.</summary>
    public void SetModels(IEnumerable<ModelDefinition> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var byServedName = ImmutableDictionary.CreateBuilder<string, PlacedModel>(StringComparer.OrdinalIgnoreCase);
        var nodeByModel = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        var gpus = new SortedDictionary<string, List<PlacedGpus>>(StringComparer.Ordinal);
        foreach (ModelDefinition model in models)
        {
            if (model.Placement is not { NodeId: { Length: > 0 } nodeId })
            {
                continue; // Unplaced: the router's own check (503 model_unplaced).
            }

            nodeByModel[model.Id] = nodeId;
            byServedName.TryAdd(model.ServedName, new PlacedModel(model.Id, nodeId));
            if (model.Placement.GpuUuids is { Count: > 0 } uuids)
            {
                if (!gpus.TryGetValue(nodeId, out List<PlacedGpus>? list))
                {
                    gpus[nodeId] = list = [];
                }

                list.Add(new PlacedGpus(model.Id, string.Join(',', uuids), [.. uuids]));
            }
        }

        var gpusByNode = gpus.ToImmutableDictionary(
            p => p.Key, p => p.Value.OrderBy(g => g.ModelId, StringComparer.Ordinal).ToImmutableArray(), StringComparer.Ordinal);
        ModelIndex? previous;
        lock (_gate)
        {
            previous = _modelIndex;
            Volatile.Write(ref _modelIndex, new ModelIndex(byServedName.ToImmutable(), nodeByModel.ToImmutable(), gpusByNode));

            // placed_gpu_missing depends on the placements: republish only the nodes whose placed GPUs changed (MN-124).
            DateTimeOffset now = _time.GetUtcNow();
            foreach (NodeEntry entry in _entries.Values)
            {
                if (!SamePlacedGpus(previous, gpusByNode, entry.Record.Id))
                {
                    PublishLocked(entry, now);
                }
            }
        }
    }

    private static bool SamePlacedGpus(ModelIndex? previous, ImmutableDictionary<string, ImmutableArray<PlacedGpus>> next, string nodeId)
    {
        ImmutableArray<PlacedGpus> before = previous?.GpusByNode.GetValueOrDefault(nodeId) ?? [];
        ImmutableArray<PlacedGpus> after = next.GetValueOrDefault(nodeId);
        after = after.IsDefault ? [] : after;
        before = before.IsDefault ? [] : before;
        return before.Length == after.Length &&
            before.Zip(after).All(p => p.First.ModelId == p.Second.ModelId && p.First.Key == p.Second.Key);
    }

    /// <summary>
    /// <see cref="HealthReasonCodes.PlacedGpuMissing"/> (design §5.1, D73, MN-124): one reason per model placed on this node
    /// by GPU UUID whose GPUs the node's last capability report does not all list (card removed or replaced). Its start is
    /// refused with 422 <c>gpu_missing</c>; the operator re-selects its GPUs in the editor. None before a capability report.
    /// </summary>
    private List<string> PlacedGpuMissingReasons(NodeEntry entry)
    {
        if (entry.LastCapabilities is not { } capabilities || Volatile.Read(ref _modelIndex) is not { } index
            || !index.GpusByNode.TryGetValue(entry.Record.Id, out ImmutableArray<PlacedGpus> placed))
        {
            return [];
        }

        var reported = new HashSet<string>(capabilities.Gpus.Select(g => g.Uuid).OfType<string>(), StringComparer.Ordinal);
        List<string> reasons = [];
        foreach (PlacedGpus model in placed)
        {
            var missing = model.Uuids.Where(u => !reported.Contains(u)).ToList();
            if (missing.Count > 0)
            {
                reasons.Add($"{HealthReasonCodes.PlacedGpuMissing}: model '{model.ModelId}' is placed on GPU(s) {string.Join(", ", missing)}, " +
                    "which the node no longer reports; its start is refused until its GPUs are selected again.");
            }
        }

        return reasons;
    }

    /// <summary>
    /// Design §3.6 + §4.7 for one node: <c>/info</c> (contact, version, clock skew, identity and fencing checks), protocol
    /// negotiation (recorded in the client factory), then <c>/capabilities</c> and <c>/status</c>, and the snapshot is
    /// applied. A snapshot older than the cursor of the same boot is not applied (events already moved past it).
    /// A blocked node gets only <c>/info</c>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The cache or the client factory does not know the node.</exception>
    /// <exception cref="AgentUnreachableException">The agent could not be reached.</exception>
    /// <exception cref="AgentException">The agent answered an error.</exception>
    public async Task<NodeSyncResult> SynchronizeAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        NodeEntry entry;
        NodeRecord record;
        lock (_gate)
        {
            entry = Entry(nodeId);
            record = entry.Record;
        }

        IAgentClient client = _clients.GetClient(nodeId);
        InfoResponse info = await client.GetInfoAsync(ct).ConfigureAwait(false);
        DateTimeOffset now = _time.GetUtcNow();
        string? block = null;
        NodePhase blockPhase = NodePhase.Unknown;
        int? selected = Protocol.Negotiate(Options.MasterProtocol, info.Protocol);
        if (!string.Equals(info.NodeId, nodeId, StringComparison.Ordinal) || !string.Equals(info.NodeUid, record.Uid, StringComparison.Ordinal))
        {
            blockPhase = NodePhase.IdentityMismatch;
            block = $"Identity mismatch: the agent reports node '{info.NodeId}' with uid '{info.NodeUid}', the registry holds uid '{record.Uid}' for '{nodeId}'. Re-enroll the node.";
        }
        else if (selected is null)
        {
            blockPhase = NodePhase.Incompatible;
            block = string.Create(CultureInfo.InvariantCulture,
                $"No common protocol version: the master speaks {Options.MasterProtocol.Min}–{Options.MasterProtocol.Current}, the agent {info.Protocol.Min}–{info.Protocol.Current}. Update the agent or the master.");
        }

        lock (_gate)
        {
            if (!IsCurrent(entry))
            {
                return new NodeSyncResult(NodeSyncStatus.Blocked, client, null, null, record.Uid, null, 0);
            }

            // Fencing is judged against the triple current now, under the lock (MN-126): a takeover or continuation that
            // raised the epoch while this /info was in flight must not leave a block decided against the old epoch.
            MasterTriple master = _identity.Current;
            string? fencing = FencingBlock(master, info.AcceptedMaster);
            if (block is null && fencing is not null)
            {
                blockPhase = NodePhase.PendingTakeover;
                block = fencing;
            }

            // Another agent answering at the node's address says nothing about this node's epoch: it keeps its mark.
            if (blockPhase != NodePhase.IdentityMismatch && _pendingTakeover.TryGetValue(nodeId, out PendingTakeoverMark? mark))
            {
                if (fencing is null)
                {
                    // Its epoch is below the master's (or it holds none): it accepts the master on the next mutation.
                    _pendingTakeover.Remove(nodeId);
                    NodeStatusCacheLog.PendingTakeoverCleared(_logger, nodeId, mark.TakeoverId, info.AcceptedMaster?.Epoch, master.Epoch);
                }
                else if (blockPhase == NodePhase.PendingTakeover)
                {
                    block = string.Create(CultureInfo.InvariantCulture,
                        $"Returned after takeover {mark.TakeoverId} holding epoch {info.AcceptedMaster!.Epoch} (master {info.AcceptedMaster.InstanceId}), not below this master's epoch {master.Epoch}. It stays blocked until the operator continues the takeover for this node (design §4.2).");
                }
            }

            Contact(entry, now);
            entry.AgentVersion = info.AgentVersion;
            entry.AgentProtocol = info.Protocol;
            entry.AcceptedMaster = info.AcceptedMaster;
            entry.ClockSkew = (info.Time - now).Duration();
            entry.SelectedProtocol = block is null || blockPhase == NodePhase.PendingTakeover ? selected : null;
            if (block is not null || !entry.Record.Approved)
            {
                if (block is not null)
                {
                    entry.Phase = blockPhase;
                    entry.BlockReason = block;
                    entry.BlockedAtEpoch = master.Epoch;
                }

                PublishLocked(entry, now);
                if (block is not null)
                {
                    NodeStatusCacheLog.NodeBlocked(_logger, nodeId, blockPhase, block);
                }

                return new NodeSyncResult(NodeSyncStatus.Blocked, client, null, null, record.Uid, entry.BootId, entry.Seq ?? 0);
            }
        }

        _clients.SetNegotiatedProtocol(nodeId, selected!.Value);
        NodeCapabilities capabilities = await client.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        StatusSnapshot snapshot = await client.GetStatusAsync(ct).ConfigureAwait(false);
        now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!IsCurrent(entry))
            {
                return new NodeSyncResult(NodeSyncStatus.Blocked, client, null, null, record.Uid, null, 0);
            }

            Contact(entry, now);
            entry.LastCapabilities = capabilities;
            ApplySnapshotLocked(entry, snapshot, now);
            entry.Phase = NodePhase.Connected;
            entry.BlockReason = null;
            PublishLocked(entry, now);
            return new NodeSyncResult(NodeSyncStatus.Synchronized, client, snapshot, capabilities, record.Uid, entry.BootId, entry.Seq ?? snapshot.Seq);
        }
    }

    /// <summary>
    /// Applies one event of the node's stream (design §4.7). Every event counts as contact. See <see cref="NodeEventResult"/>.
    /// </summary>
    public NodeEventResult ApplyEvent(string nodeId, AgentEvent agentEvent)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(agentEvent);
        lock (_gate)
        {
            if (!_entries.TryGetValue(nodeId, out NodeEntry? entry))
            {
                return NodeEventResult.Resync;
            }

            DateTimeOffset now = _time.GetUtcNow();
            Contact(entry, now);
            NodeEventResult result = ApplyLocked(entry, agentEvent, now);
            PublishLocked(entry, now);
            return result;
        }
    }

    /// <summary>
    /// Replaces a connected node's capabilities with a fresher read (<see cref="NodeMonitorOptions.CapabilityRefreshInterval"/>).
    /// True when they changed (compared as JSON); false when they are the same or the node is not connected.
    /// </summary>
    public bool UpdateCapabilities(string nodeId, NodeCapabilities capabilities)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(capabilities);
        lock (_gate)
        {
            if (!_entries.TryGetValue(nodeId, out NodeEntry? entry) || entry.Phase != NodePhase.Connected)
            {
                return false;
            }

            if (entry.LastCapabilities is { } current &&
                string.Equals(
                    System.Text.Json.JsonSerializer.Serialize(current, AgentJson.Options),
                    System.Text.Json.JsonSerializer.Serialize(capabilities, AgentJson.Options),
                    StringComparison.Ordinal))
            {
                return false;
            }

            DateTimeOffset now = _time.GetUtcNow();
            entry.LastCapabilities = capabilities;
            PublishLocked(entry, now);
            return true;
        }
    }

    /// <summary>Records why the last call to a node failed (shown with Offline). No contact is recorded.</summary>
    public void RecordFailure(string nodeId, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            if (_entries.TryGetValue(nodeId, out NodeEntry? entry))
            {
                entry.LastError = Describe(failure);
                PublishLocked(entry, _time.GetUtcNow());
            }
        }
    }

    /// <summary>
    /// Lets callers that send mutations (MN-028, MN-032, …) surface what the agent answered: <c>stale_master</c> or
    /// <c>competing_master</c> blocks the node as PendingTakeover until <c>/info</c> shows an epoch below the master's
    /// (design §4.2); <c>protocol_mismatch</c> makes the monitor renegotiate. Returns true when the error was one of these.
    /// </summary>
    public bool ReportAgentError(string nodeId, AgentException error)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(error);
        bool fencing = error.Code is ErrorCodes.StaleMaster or ErrorCodes.CompetingMaster;
        if (!fencing && error.Code != ErrorCodes.ProtocolMismatch)
        {
            return false;
        }

        if (fencing)
        {
            lock (_gate)
            {
                // A node marked Pending takeover is already blocked; only its next /info decides whether it needs a
                // continuation (the epoch it holds must be read after the takeover, MN-126), so the resync below is enough.
                if (_entries.TryGetValue(nodeId, out NodeEntry? entry) && !_pendingTakeover.ContainsKey(nodeId))
                {
                    entry.Phase = NodePhase.PendingTakeover;
                    entry.BlockReason = $"The agent refused this master ({error.Code}): {error.Message} A takeover is needed (design §4.2).";
                    entry.BlockedAtEpoch = _identity.Epoch;
                    PublishLocked(entry, _time.GetUtcNow());
                    NodeStatusCacheLog.NodeBlocked(_logger, nodeId, NodePhase.PendingTakeover, entry.BlockReason);
                }
            }
        }

        ResyncRequested?.Invoke(nodeId);
        return true;
    }

    /// <summary>
    /// Marks the nodes that did not answer <paramref name="record"/> (a takeover just persisted, design §4.2 step 3) as
    /// Pending takeover and clears the mark of every other node; nodes with a continuation in the record are not marked.
    /// A marked node forgets its fencing block until its next <c>/info</c>, so <see cref="NodeFencingState.NeedsContinuation"/>
    /// always reflects an epoch read after the takeover.
    /// </summary>
    public void MarkPendingTakeover(TakeoverRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        List<string> marked = [];
        lock (_gate)
        {
            List<string> affected = [.. _pendingTakeover.Keys];
            SeedPendingTakeover(record);
            affected.AddRange(_pendingTakeover.Keys);
            DateTimeOffset now = _time.GetUtcNow();
            foreach (string nodeId in affected.Distinct(StringComparer.Ordinal))
            {
                if (!_entries.TryGetValue(nodeId, out NodeEntry? entry))
                {
                    continue;
                }

                if (_pendingTakeover.ContainsKey(nodeId))
                {
                    // Not seen since the takeover: an older fencing block or a stream that outlived a failed /info no
                    // longer counts; the next synchronization decides (events re-snapshot until then).
                    entry.Phase = NodePhase.Unknown;
                    entry.BlockReason = null;
                    marked.Add(nodeId);
                }

                PublishLocked(entry, now);
            }
        }

        foreach (string nodeId in marked)
        {
            ResyncRequested?.Invoke(nodeId);
        }
    }

    /// <summary>
    /// Every monitored node's standing towards this master's epoch (design §4.2; the source of <see cref="FencingStatus.Nodes"/>,
    /// MN-115): <see cref="NodeFencingState.PendingTakeover"/> while marked and not seen since the takeover,
    /// <see cref="NodeFencingState.NeedsContinuation"/> once a marked node answered with an epoch not below the master's,
    /// <see cref="NodeFencingState.Stale"/>/<see cref="NodeFencingState.Competing"/> for an unmarked node blocked by fencing,
    /// <see cref="NodeFencingState.Unknown"/> before the first <c>/info</c> of this master boot.
    /// A fencing block decided against an epoch the master has since raised does not count (MN-126): a marked node reads
    /// <see cref="NodeFencingState.PendingTakeover"/> and an unmarked one <see cref="NodeFencingState.Unknown"/> until
    /// its next <c>/info</c>, so a takeover or continuation never shows a transient NeedsContinuation or Stale. Otherwise
    /// <see cref="NodeFencingState.Current"/>. Ordered by node id. No agent call.
    /// </summary>
    public IReadOnlyList<NodeFencingStatus> ListFencing()
    {
        MasterTriple master = _identity.Current;
        lock (_gate)
        {
            return
            [
                .. _entries.Values
                    .OrderBy(e => e.Record.Id, StringComparer.Ordinal)
                    .Select(e => new NodeFencingStatus(e.Record.Id, FencingStateLocked(e, master), e.AcceptedMaster?.Epoch)),
            ];
        }
    }

    /// <summary>
    /// Republishes every node after the master's epoch was raised (a takeover or continuation, MN-126), so a node whose
    /// fencing block was decided against the old epoch stops showing it before its next <c>/info</c>. No agent call.
    /// </summary>
    public void EpochRaised()
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (NodeEntry entry in _entries.Values.Where(e => e.Phase == NodePhase.PendingTakeover))
            {
                PublishLocked(entry, now);
            }
        }
    }

    /// <summary>The takeover id that marked <paramref name="nodeId"/> Pending takeover; null when it is not marked.</summary>
    public string? PendingTakeoverOf(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        lock (_gate)
        {
            return _pendingTakeover.TryGetValue(nodeId, out PendingTakeoverMark? mark) ? mark.TakeoverId : null;
        }
    }

    /// <summary>Re-evaluates every node's state at the current time (the Offline rule); publishes the nodes that changed.</summary>
    public void EvaluateLiveness()
    {
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            foreach (NodeEntry entry in _entries.Values)
            {
                if (ComputeState(entry, now) != entry.State)
                {
                    PublishLocked(entry, now);
                }
            }
        }
    }

    // ---- Internals ----

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsNodeFailure(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is AgentUnreachableException or AgentException or KeyNotFoundException or HttpRequestException or IOException or OperationCanceledException;

    private static ServedModelRoute Route(string modelId, string nodeId, NodeView? node, NodeModelRuntime? runtime)
    {
        DateTimeOffset updated = node?.Status.UpdatedAt ?? DateTimeOffset.MinValue;
        if (runtime is not null && runtime.UpdatedAt > updated)
        {
            updated = runtime.UpdatedAt;
        }

        return new ServedModelRoute(
            modelId,
            nodeId,
            runtime?.State ?? ModelState.Stopped,
            node?.Status.State ?? NodeState.Offline,
            node?.Status.Draining ?? false,
            updated);
    }

    /// <summary>
    /// Why the agent's accepted master triple blocks this master (design §4.2): a higher epoch (stale master), or the same
    /// epoch from another instance or boot (competing master). Null when this master may drive the node.
    /// </summary>
    internal static string? FencingBlock(MasterTriple master, MasterTriple? accepted)
    {
        if (accepted is null)
        {
            return null;
        }

        if (accepted.Epoch > master.Epoch)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Stale master: the agent accepted epoch {accepted.Epoch} (master {accepted.InstanceId}); this master runs epoch {master.Epoch}. A takeover is needed (design §4.2).");
        }

        if (accepted.Epoch == master.Epoch && !master.SameBoot(accepted))
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Competing master: another master ({accepted.InstanceId}, boot {accepted.BootId}) drives this node at epoch {accepted.Epoch}. A takeover is needed (design §4.2).");
        }

        return null;
    }

    private static string Describe(Exception failure) => failure switch
    {
        AgentException agent => $"{agent.Code}: {agent.Message}",
        AgentUnreachableException { Http2NotNegotiated: true } unreachable => $"{HealthReasonCodes.Http2NotNegotiated}: {unreachable.Message}",
        KeyNotFoundException missing => $"No agent client: {missing.Message}",
        _ => failure.Message,
    };

    private static bool RecordChanged(NodeRecord a, NodeRecord b) =>
        a.Approved != b.Approved ||
        a.Draining != b.Draining ||
        !string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal) ||
        !string.Equals(a.Address, b.Address, StringComparison.Ordinal) ||
        a.CertNotAfter != b.CertNotAfter ||
        a.Rotation?.Stage != b.Rotation?.Stage;

    private static NodeModelRuntime ToRuntime(string nodeId, RuntimeStatus runtime, DateTimeOffset now) => new()
    {
        ModelId = runtime.ModelId,
        NodeId = nodeId,
        State = runtime.State,
        ServedName = runtime.ServedName,
        InstanceId = runtime.InstanceId,
        HostPort = runtime.HostPort,
        Since = runtime.Since,
        Error = runtime.Error,
        RestartCount = runtime.RestartCount,
        ConfigHash = runtime.ConfigHash,
        ExecutionFingerprint = runtime.ExecutionFingerprint,
        Tuning = runtime.Tuning,
        OperationId = runtime.OperationId,
        MeasuredGpuGiB = runtime.MeasuredGpuGiB,
        GpuUuids = [.. runtime.Gpus.Select(g => g.Uuid)],
        Labels = runtime.Labels,
        UpdatedAt = now,
    };

    private static NodeOperationSummary ToSummary(OperationStatus operation) =>
        new(operation.Id, operation.Kind, operation.Target, operation.State, operation.Stage, operation.StartedAt);

    private string? NodeOfModel(string modelId)
    {
        if (Volatile.Read(ref _modelIndex) is { } index && index.NodeByModel.TryGetValue(modelId, out string? nodeId))
        {
            return nodeId;
        }

        foreach (NodeView node in Volatile.Read(ref _view).Values)
        {
            if (node.Runtimes.ContainsKey(modelId))
            {
                return node.Status.NodeId;
            }
        }

        return null;
    }

    /// <summary>Must hold <see cref="_gate"/>.</summary>
    private NodeEntry Entry(string nodeId) =>
        _entries.TryGetValue(nodeId, out NodeEntry? entry)
            ? entry
            : throw new KeyNotFoundException($"Node '{nodeId}' is not monitored (unknown, revoked, or the registry was not synchronized yet).");

    /// <summary>Must hold <see cref="_gate"/>. False when the entry was removed or replaced (re-enrolled) meanwhile.</summary>
    private bool IsCurrent(NodeEntry entry) =>
        _entries.TryGetValue(entry.Record.Id, out NodeEntry? current) && ReferenceEquals(current, entry);

    private static void Contact(NodeEntry entry, DateTimeOffset now)
    {
        entry.LastContact = now;
        entry.LastError = null;
    }

    /// <summary>Must hold <see cref="_gate"/>.</summary>
    private void ApplySnapshotLocked(NodeEntry entry, StatusSnapshot snapshot, DateTimeOffset now)
    {
        entry.Memory = snapshot.Memory;
        entry.Health = snapshot.Health.Reasons;
        if (string.Equals(entry.BootId, snapshot.BootId, StringComparison.Ordinal) && entry.Seq is { } cursor && snapshot.Seq < cursor)
        {
            // Events of this boot already moved past this snapshot: keep the newer view and cursor.
            return;
        }

        if (!string.Equals(entry.BootId, snapshot.BootId, StringComparison.Ordinal) && entry.BootId is not null)
        {
            NodeStatusCacheLog.NewBoot(_logger, entry.Record.Id, entry.BootId, snapshot.BootId);
        }

        entry.BootId = snapshot.BootId;
        entry.Seq = snapshot.Seq;
        entry.Runtimes.Clear();
        entry.RuntimeSeq.Clear();
        foreach (RuntimeStatus runtime in snapshot.Runtimes)
        {
            SetRuntime(entry, runtime.ModelId, runtime, snapshot.Seq, now);
        }

        entry.Operations.Clear();
        foreach (OperationStatus operation in snapshot.Operations)
        {
            ApplyOperation(entry, operation);
        }
    }

    /// <summary>Must hold <see cref="_gate"/>.</summary>
    private static NodeEventResult ApplyLocked(NodeEntry entry, AgentEvent agentEvent, DateTimeOffset now)
    {
        if (agentEvent is ResetEvent or OverflowEvent)
        {
            return NodeEventResult.Resync; // Never moves the cursor (W2 review MN-092).
        }

        if (entry.Phase != NodePhase.Connected || entry.BootId is null || !string.Equals(agentEvent.BootId, entry.BootId, StringComparison.Ordinal))
        {
            return NodeEventResult.Resync;
        }

        long cursor = entry.Seq ?? 0;
        if (agentEvent is HeartbeatEvent)
        {
            // The agent sends a heartbeat only after every queued event, so a seq above the cursor means events arrived
            // that were not applied (the client skips unreadable ones): a gap.
            return agentEvent.Seq > cursor ? NodeEventResult.Resync : NodeEventResult.Ignored;
        }

        if (agentEvent.Seq <= cursor)
        {
            return NodeEventResult.Ignored;
        }

        if (agentEvent.Seq != cursor + 1)
        {
            return NodeEventResult.Resync;
        }

        switch (agentEvent)
        {
            case RuntimeEvent runtime:
                SetRuntime(entry, runtime.ModelId, runtime.Runtime, agentEvent.Seq, now);
                break;
            case OperationEvent operation:
                ApplyOperation(entry, operation.Operation);
                break;
            case HealthEvent health:
                // Online ↔ Degraded while the stream stays up (MN-047 / D68); a snapshot sets it too.
                entry.Health = health.Health.Reasons;
                break;
            default:
                // Downloads and reservations are not part of the cached view; they only move the cursor.
                break;
        }

        entry.Seq = agentEvent.Seq;
        return NodeEventResult.Applied;
    }

    private static void SetRuntime(NodeEntry entry, string modelId, RuntimeStatus? runtime, long seq, DateTimeOffset now)
    {
        entry.RuntimeSeq[modelId] = seq;
        if (runtime is null)
        {
            entry.Runtimes.Remove(modelId);
        }
        else
        {
            entry.Runtimes[modelId] = ToRuntime(entry.Record.Id, runtime, now);
        }
    }

    /// <summary>Open operations; an operation that ended is never re-opened (W2 review MN-092).</summary>
    private static void ApplyOperation(NodeEntry entry, OperationStatus operation)
    {
        if (operation.State.IsTerminal())
        {
            entry.Operations.Remove(operation.Id);
            if (entry.Ended.Add(operation.Id))
            {
                entry.EndedOrder.Enqueue(operation.Id);
                while (entry.EndedOrder.Count > EndedOperationsKept)
                {
                    entry.Ended.Remove(entry.EndedOrder.Dequeue());
                }
            }

            return;
        }

        if (!entry.Ended.Contains(operation.Id))
        {
            entry.Operations[operation.Id] = ToSummary(operation);
        }
    }

    private NodeState ComputeState(NodeEntry entry, DateTimeOffset now)
    {
        if (!entry.Record.Approved)
        {
            return NodeState.PendingApproval;
        }

        if (_pendingTakeover.ContainsKey(entry.Record.Id))
        {
            return NodeState.PendingTakeover; // Reachable or not (design §3.5): blocked until it shows a lower epoch.
        }

        if (entry.LastContact is not { } contact || now - contact > Options.OfflineAfter)
        {
            return NodeState.Offline;
        }

        return entry.Phase switch
        {
            NodePhase.Incompatible or NodePhase.IdentityMismatch => NodeState.Incompatible,
            NodePhase.PendingTakeover => NodeState.PendingTakeover,
            NodePhase.Connected => DegradedReasons(entry, now).Count > 0 ? NodeState.Degraded : NodeState.Online,
            _ => NodeState.Offline, // Contacted, but no snapshot yet in this master boot.
        };
    }

    private List<string> DegradedReasons(NodeEntry entry, DateTimeOffset now)
    {
        List<string> reasons = [.. entry.Health.Select(h => h.Message)];
        if (entry.ClockSkew is { } skew && skew > Options.DegradedClockSkew)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture,
                $"Clock skew of {skew.TotalSeconds:0} s between the master and the agent (more than {Options.DegradedClockSkew.TotalSeconds:0} s)."));
        }

        // One wording with the rotation service (it adds the last renewal error), W3 review MN-094.
        if (Pki.CertificateRotationService.RenewalProblem(entry.Record, now,
                new Pki.CertificateRotationOptions { DegradedBeforeExpiry = Options.CertificateExpiryWarning }) is { } renewal)
        {
            reasons.Add(renewal);
        }

        if (_degradedReason?.Invoke(entry.Record.Id) is { Length: > 0 } extra)
        {
            reasons.Add(extra);
        }

        reasons.AddRange(PlacedGpuMissingReasons(entry));
        return reasons;
    }

    private List<string> Reasons(NodeEntry entry, NodeState state, DateTimeOffset now)
    {
        switch (state)
        {
            case NodeState.PendingApproval:
                return ["Waiting for operator approval."];
            case NodeState.Offline:
                List<string> offline =
                [
                    entry.LastContact is { } contact && now - contact > Options.OfflineAfter
                        ? string.Create(CultureInfo.InvariantCulture, $"No contact for more than {Options.OfflineAfter.TotalSeconds:0} s (last contact {contact:u}).")
                        : "Not synchronized since the master started.",
                ];
                if (entry.LastError is { } error)
                {
                    offline.Add("Last error: " + error);
                }

                return offline;
            case NodeState.PendingTakeover when !FencingBlockIsCurrent(entry, _identity.Epoch)
                && _pendingTakeover.TryGetValue(entry.Record.Id, out PendingTakeoverMark? mark):
                List<string> pending =
                [
                    string.Create(CultureInfo.InvariantCulture,
                        $"Unreachable during takeover {mark.TakeoverId} ({mark.At:u}). Blocked until it answers with an epoch below the master's, or the operator continues the takeover for it (design §4.2)."),
                ];
                if (entry.LastContact is { } seen)
                {
                    pending.Add(string.Create(CultureInfo.InvariantCulture, $"Last contact {seen:u}."));
                }

                if (entry.LastError is { } lastError)
                {
                    pending.Add("Last error: " + lastError);
                }

                return pending;
            case NodeState.Incompatible or NodeState.PendingTakeover:
                return entry.BlockReason is { } reason ? [reason] : [];
            case NodeState.Degraded:
                return DegradedReasons(entry, now);
            default:
                return [];
        }
    }

    /// <summary>Must hold <see cref="_gate"/> (or run in the constructor). Replaces the marks with the record's unreachable nodes.</summary>
    private void SeedPendingTakeover(TakeoverRecord record)
    {
        _pendingTakeover.Clear();
        var continued = new HashSet<string>((record.Continuations ?? []).Select(c => c.NodeId), StringComparer.Ordinal);
        foreach (string nodeId in record.Unreachable ?? [])
        {
            if (!continued.Contains(nodeId))
            {
                _pendingTakeover[nodeId] = new PendingTakeoverMark(record.TakeoverId, record.At);
            }
        }
    }

    /// <summary>Must hold <see cref="_gate"/>.</summary>
    private NodeFencingState FencingStateLocked(NodeEntry entry, MasterTriple master)
    {
        bool blocked = FencingBlockIsCurrent(entry, master.Epoch);
        if (_pendingTakeover.ContainsKey(entry.Record.Id))
        {
            return blocked ? NodeFencingState.NeedsContinuation : NodeFencingState.PendingTakeover;
        }

        if (entry.Phase == NodePhase.PendingTakeover && !blocked)
        {
            // Blocked against an epoch the master has since raised (takeover or continuation): not judged again until
            // its next /info, which the takeover service and the monitor ask for (MN-126: no transient Stale or
            // NeedsContinuation while that re-synchronization is in flight).
            return NodeFencingState.Unknown;
        }

        if (blocked)
        {
            return entry.AcceptedMaster is { } accepted && accepted.Epoch == master.Epoch && !master.SameBoot(accepted)
                ? NodeFencingState.Competing
                : NodeFencingState.Stale;
        }

        return entry.AgentProtocol is null ? NodeFencingState.Unknown : NodeFencingState.Current;
    }

    /// <summary>
    /// True when the node is blocked by fencing (<see cref="NodePhase.PendingTakeover"/>) decided against
    /// <paramref name="epoch"/> or later; a block decided before a takeover or continuation raised the epoch is stale.
    /// </summary>
    private static bool FencingBlockIsCurrent(NodeEntry entry, long epoch) =>
        entry.Phase == NodePhase.PendingTakeover && entry.BlockedAtEpoch is { } at && at >= epoch;

    /// <summary>Must hold <see cref="_gate"/>. Rebuilds the node's immutable view and publishes it.</summary>
    private void PublishLocked(NodeEntry entry, DateTimeOffset now)
    {
        if (!IsCurrent(entry))
        {
            return;
        }

        NodeState state = ComputeState(entry, now);
        entry.State = state;
        ImmutableDictionary<string, NodeModelRuntime> runtimes = entry.Runtimes.ToImmutableDictionary(StringComparer.Ordinal);
        NodeRecord record = entry.Record;
        var status = new NodeLiveStatus
        {
            NodeId = record.Id,
            NodeUid = record.Uid,
            DisplayName = record.DisplayName ?? record.Id,
            Address = record.Address,
            State = state,
            Draining = record.Draining,
            Reasons = Reasons(entry, state, now),
            LastContact = entry.LastContact,
            BootId = entry.BootId,
            Seq = entry.Seq,
            AgentVersion = entry.AgentVersion,
            AgentProtocol = entry.AgentProtocol,
            SelectedProtocol = entry.SelectedProtocol,
            AcceptedMaster = entry.AcceptedMaster,
            Memory = entry.Memory,
            Capabilities = entry.LastCapabilities,
            Runtimes = [.. runtimes.Values.OrderBy(r => r.ModelId, StringComparer.Ordinal)],
            OpenOperations = [.. entry.Operations.Values.OrderBy(o => o.StartedAt).ThenBy(o => o.OperationId, StringComparer.Ordinal)],
            CertNotAfter = record.CertNotAfter,
            RotationStage = record.Rotation?.Stage,
            UpdatedAt = now,
        };

        if (entry.PublishedState is { } previous && previous != state)
        {
            NodeStatusCacheLog.StateChanged(_logger, record.Id, previous, state, status.Reasons);
        }

        entry.PublishedState = state;
        _view = _view.SetItem(record.Id, new NodeView(status, runtimes));
        BumpLocked();
    }

    /// <summary>Must hold <see cref="_gate"/>.</summary>
    private void BumpLocked()
    {
        Interlocked.Increment(ref _version);
        TaskCompletionSource signal = _changed;
        _changed = NewSignal();
        signal.TrySetResult();
    }

    /// <summary>Where a node stands in the protocol, independent of contact time.</summary>
    internal enum NodePhase
    {
        /// <summary>No snapshot yet in this master boot.</summary>
        Unknown,

        /// <summary>Negotiated, identity and fencing checked, snapshot applied.</summary>
        Connected,

        /// <summary>No common protocol version.</summary>
        Incompatible,

        /// <summary>The agent reports another node id or uid than the registry.</summary>
        IdentityMismatch,

        /// <summary>The agent accepted a higher epoch or a competing master (design §4.2).</summary>
        PendingTakeover,
    }

    /// <summary>Mutable per-node state; guarded by <see cref="_gate"/>.</summary>
    private sealed class NodeEntry(NodeRecord record)
    {
        public NodeRecord Record { get; set; } = record;

        public NodePhase Phase { get; set; }

        public string? BlockReason { get; set; }

        /// <summary>The master epoch a fencing block (<see cref="NodePhase.PendingTakeover"/>) was decided against.</summary>
        public long? BlockedAtEpoch { get; set; }

        public string? LastError { get; set; }

        public DateTimeOffset? LastContact { get; set; }

        public string? BootId { get; set; }

        public long? Seq { get; set; }

        public string? AgentVersion { get; set; }

        public ProtocolRange? AgentProtocol { get; set; }

        public int? SelectedProtocol { get; set; }

        public MasterTriple? AcceptedMaster { get; set; }

        public TimeSpan? ClockSkew { get; set; }

        public NodeMemorySnapshot? Memory { get; set; }

        public NodeCapabilities? LastCapabilities { get; set; }

        public IReadOnlyList<HealthReason> Health { get; set; } = [];

        public Dictionary<string, NodeModelRuntime> Runtimes { get; } = new(StringComparer.Ordinal);

        /// <summary>Seq of the last snapshot or event that set each model's instance (a refresh never overwrites a newer one).</summary>
        public Dictionary<string, long> RuntimeSeq { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, NodeOperationSummary> Operations { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Ended { get; } = new(StringComparer.Ordinal);

        public Queue<string> EndedOrder { get; } = new();

        public NodeState State { get; set; } = NodeState.Offline;

        public NodeState? PublishedState { get; set; }
    }

    private sealed record NodeView(NodeLiveStatus Status, ImmutableDictionary<string, NodeModelRuntime> Runtimes);

    private sealed record PlacedModel(string ModelId, string NodeId);

    private sealed record PendingTakeoverMark(string TakeoverId, DateTimeOffset At);

    private sealed record ModelIndex(
        ImmutableDictionary<string, PlacedModel> ByServedName,
        ImmutableDictionary<string, string> NodeByModel,
        ImmutableDictionary<string, ImmutableArray<PlacedGpus>> GpusByNode);

    /// <summary>A model placed by GPU UUID; <paramref name="Key"/> is the ordered UUIDs joined, for change detection.</summary>
    private sealed record PlacedGpus(string ModelId, string Key, ImmutableArray<string> Uuids);

    private static partial class NodeStatusCacheLog
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId}: {Previous} -> {Current}. {Reasons}")]
        public static partial void StateChanged(ILogger logger, string nodeId, NodeState previous, NodeState current, IReadOnlyList<string> reasons);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Node {NodeId} is blocked ({Phase}): {Reason}")]
        public static partial void NodeBlocked(ILogger logger, string nodeId, NodePhase phase, string reason);

        [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} left Pending takeover ({TakeoverId}): it holds epoch {AgentEpoch}, below the master's {Epoch}, and accepts the master on its next command.")]
        public static partial void PendingTakeoverCleared(ILogger logger, string nodeId, string takeoverId, long? agentEpoch, long epoch);

        [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId}: agent boot changed from {OldBoot} to {NewBoot}; snapshot applied.")]
        public static partial void NewBoot(ILogger logger, string nodeId, string oldBoot, string newBoot);
    }
}
