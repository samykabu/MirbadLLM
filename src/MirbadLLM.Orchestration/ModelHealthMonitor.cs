using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Orchestration;

/// <summary>
/// Background loop: on boot, starts every <see cref="ModelDefinition.AutoStart"/> model that isn't already
/// running; then refreshes state, logs transitions, queues the previous running set after a node restarts and restarts a
/// model whose container died on its own (<see cref="MirbadLLMOptions.CrashRestart"/>).
/// </summary>
/// <remarks>
/// Boot auto-start runs as one queue per node (design §4.8, <see cref="PerNodeStartLanes"/>): sequential within a node,
/// waiting for the node's previous start to leave Starting, and parallel across nodes. A start the runtime refuses
/// because another model on the node is still starting (<see cref="ModelStartConflictException"/>, the agent's
/// <c>start_conflict</c> reservation answer) is retried until <see cref="MirbadLLMOptions.StartupTimeout"/>; a memory
/// admission refusal leaves the model stopped.
/// A crash (Ready/Starting -> Failed, or Ready -> Stopped without an acknowledged Stop) of a model the recovery state
/// still lists is restarted through the same lane after <see cref="CrashRestartBackoff"/>; after
/// <see cref="MirbadLLMOptions.CrashRestartLimit"/> crashes in a row <see cref="ModelRecoveryState"/> pauses the model
/// until a human starts it. <see cref="CrashSettlePeriod"/> of Ready forgets earlier crashes.
/// </remarks>
/// <param name="migrationOptions">Supplies the bootstrap node id that unplaced models are queued on; null = the default.</param>
public sealed partial class ModelHealthMonitor(
    IModelRegistry registry,
    IModelRuntime runtime,
    IOptions<MirbadLLMOptions> options,
    ILogger<ModelHealthMonitor> logger,
    StartupTimeTracker? startupTimes = null,
    IOptions<RegistryMigrationOptions>? migrationOptions = null,
    ModelRecoveryState? recovery = null,
    INodeStatusCache? nodes = null,
    IOwnershipStore? ownership = null) : BackgroundService
{
    private readonly MirbadLLMOptions _options = options.Value;
    private readonly Dictionary<string, ModelState> _lastKnownState = [];
    private readonly Dictionary<string, DateTimeOffset> _readySince = [];
    private readonly PerNodeStartLanes _starts = new();
    private readonly ConcurrentDictionary<(string Uid, string Boot, string Model), byte> _recovering = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Keep monitoring while a node's start lane is busy or waiting for an offline agent.
            using (AuditScope.Begin(AuditActors.SystemAutoStart))
            {
                await AutoStartModelsAsync(stoppingToken).ConfigureAwait(false);
            }

            await RefreshAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_options.HealthPollInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await _starts.WhenAllSettledAsync().ConfigureAwait(false);
        }
    }

    private async Task AutoStartModelsAsync(CancellationToken ct)
    {
        IReadOnlyList<ModelDefinition> models;
        string bootstrapNodeId;
        try
        {
            models = await registry.ListAsync(ct).ConfigureAwait(false);
            bootstrapNodeId = migrationOptions?.Value.EffectiveBootstrapNodeId ?? RegistryMigrationOptions.DefaultBootstrapNodeId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.RefreshFailed(logger, ex);
            return;
        }

        // Every model's node (auto-start or not): a model that is Starting holds its node's start slot either way.
        var nodeOf = models.ToDictionary(m => m.Id, m => PerNodeStartLanes.NodeOf(m, bootstrapNodeId), StringComparer.Ordinal);
        foreach (var model in models.Where(m => m.AutoStart))
        {
            string nodeId = nodeOf[model.Id];
            _ = _starts.Enqueue(nodeId, () => AutoStartAsync(model, nodeId, id => NodeOfStatus(id, nodeOf, bootstrapNodeId), ct));
        }
    }

    /// <summary>
    /// One model's auto-start, inside its node's queue. Everything sits inside the try: Docker or an agent being slow or
    /// down at boot must cost one log line, not the whole controller (an escaping exception stops the host).
    /// False keeps recovery pending when another start held the slot until the wait expired.
    /// </summary>
    /// <param name="guard">For a recovery or crash restart: re-checked before every send, and the start also goes ahead
    /// from Failed.</param>
    private async Task<bool> AutoStartAsync(ModelDefinition model, string nodeId, Func<string, string> nodeOfStatus, CancellationToken ct,
        Func<CancellationToken, Task<bool>>? guard = null)
    {
        bool started = false;
        try
        {
            var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
            while (true)
            {
                try
                {
                    if (guard is not null && !await guard(ct).ConfigureAwait(false))
                    {
                        return true;
                    }

                    // Re-read every round: with the multi-node runtime the state is only known once the node is
                    // synchronized (design §4.6: auto-start runs after the snapshot, for models still Stopped).
                    var status = await runtime.GetStatusAsync(model.Id, ct).ConfigureAwait(false);
                    if (status.State != ModelState.Stopped && !(guard is not null && status.State == ModelState.Failed))
                    {
                        Log.ModelAlreadyRunning(logger, model.Id, status.State);
                        return true;
                    }

                    // One at a time per node: vLLM measures memory while it loads (see ModelStartConflictException).
                    await WaitWhileNodeStartingAsync(nodeId, nodeOfStatus, ct).ConfigureAwait(false);
                    if (guard is not null && !await guard(ct).ConfigureAwait(false))
                    {
                        return true;
                    }

                    // Read again after the wait: the node may have synchronized meanwhile (master restart with the agent
                    // up), and its snapshot shows the model running; a start now would be a redundant ensureStarted (MN-044
                    // note, MN-047).
                    status = await runtime.GetStatusAsync(model.Id, ct).ConfigureAwait(false);
                    if (status.State != ModelState.Stopped && !(guard is not null && status.State == ModelState.Failed))
                    {
                        Log.ModelAlreadyRunning(logger, model.Id, status.State);
                        return true;
                    }

                    await runtime.StartAsync(model.Id, force: false, ct).ConfigureAwait(false);
                    started = true;
                    break;
                }
                catch (ModelStartConflictException ex) when (DateTimeOffset.UtcNow < deadline)
                {
                    // The node's start slot is taken (a start from the UI, or one the agent still holds a reservation for).
                    MonitorLog.AutoStartWaiting(logger, model.Id, nodeId, ex.StartingModelId ?? "another model");
                    await Task.Delay(AutoStartPollInterval, ct).ConfigureAwait(false);
                }
                catch (NodeUnavailableException ex) when (ex.IsTransient && DateTimeOffset.UtcNow < deadline)
                {
                    // The node is not synchronized yet (master boot) or briefly offline: nothing was sent; wait for it
                    // (W3 review MN-094). Never thrown by today's single-host runtime.
                    MonitorLog.AutoStartWaitingForNode(logger, model.Id, nodeId, ex.Message);
                    await Task.Delay(AutoStartPollInterval, ct).ConfigureAwait(false);
                }
            }

            Log.AutoStarted(logger, model.Id);
            await WaitWhileNodeStartingAsync(nodeId, nodeOfStatus, ct).ConfigureAwait(false);
        }
        catch (ModelMemoryAdmissionException ex)
        {
            Log.AutoStartRefused(logger, model.Id, ex.Message);
        }
        catch (ModelStartConflictException ex)
        {
            Log.AutoStartRefused(logger, model.Id, ex.Message);
            return started;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.AutoStartFailed(logger, ex, model.Id);
        }

        return true;
    }

    /// <summary>A status's node: the registry's placement, or the bootstrap node for a model the registry does not know.</summary>
    private static string NodeOfStatus(string modelId, Dictionary<string, string> nodeOf, string bootstrapNodeId) =>
        nodeOf.TryGetValue(modelId, out string? nodeId) ? nodeId : bootstrapNodeId;

    /// <summary>
    /// Polls until no model on <paramref name="nodeId"/> is Starting, for at most <see cref="MirbadLLMOptions.StartupTimeout"/>.
    /// Models on other nodes do not hold this node's start slot.
    /// </summary>
    private async Task WaitWhileNodeStartingAsync(string nodeId, Func<string, string> nodeOfStatus, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _options.StartupTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var statuses = await runtime.ListStatusAsync(ct).ConfigureAwait(false);
            if (!statuses.Any(s => s.State == ModelState.Starting && string.Equals(nodeOfStatus(s.Id), nodeId, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(AutoStartPollInterval, ct).ConfigureAwait(false);
        }

        throw new ModelStartConflictException($"Timed out waiting for the previous model to finish starting on node '{nodeId}'.");
    }

    internal static TimeSpan AutoStartPollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Wait before the n-th restart after a crash; the last value repeats.</summary>
    internal static IReadOnlyList<TimeSpan> CrashRestartBackoff { get; set; } = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];

    /// <summary>Ready for this long forgets a model's earlier crashes.</summary>
    internal static TimeSpan CrashSettlePeriod { get; set; } = TimeSpan.FromMinutes(10);

    private async Task RefreshAsync(CancellationToken ct)
    {
        IReadOnlyList<ModelStatus> statuses;
        try
        {
            statuses = await runtime.ListStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.RefreshFailed(logger, ex);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var crashed = new List<ModelStatus>();
        foreach (var status in statuses)
        {
            if (!_lastKnownState.TryGetValue(status.Id, out var previous) || previous != status.State)
            {
                if (previous == ModelState.Starting && status.State == ModelState.Ready && status.Since is { } since)
                {
                    startupTimes?.Record(status.Id, now - since);
                }

                Log.StateTransition(logger, status.Id, previous, status.State);
                _lastKnownState[status.Id] = status.State;
                if (status.State == ModelState.Ready)
                {
                    _readySince[status.Id] = now;
                }
                else if (IsCrash(previous, status.State))
                {
                    crashed.Add(status);
                }
            }
            else if (status.State == ModelState.Ready && _readySince.TryGetValue(status.Id, out var readySince) && now - readySince >= CrashSettlePeriod)
            {
                _readySince.Remove(status.Id);
                recovery?.ResetCrashes(status.Id);
            }
        }

        try
        {
            foreach (var status in crashed)
            {
                await QueueCrashRestartAsync(status, ct).ConfigureAwait(false);
            }

            await QueueRecoveriesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.RefreshFailed(logger, ex);
        }
    }

    /// <summary>A container that died on its own; a Stop in flight goes through Stopping first, a failed load from Starting.</summary>
    private static bool IsCrash(ModelState previous, ModelState current) =>
        (current == ModelState.Failed && previous is ModelState.Ready or ModelState.Starting) ||
        (current == ModelState.Stopped && previous == ModelState.Ready);

    private async Task QueueCrashRestartAsync(ModelStatus status, CancellationToken ct)
    {
        if (!_options.CrashRestart || recovery is null || nodes is null)
        {
            return;
        }

        var definitions = await registry.ListAsync(ct).ConfigureAwait(false);
        string bootstrap = migrationOptions?.Value.EffectiveBootstrapNodeId ?? RegistryMigrationOptions.DefaultBootstrapNodeId;
        var nodeOf = definitions.ToDictionary(m => m.Id, m => PerNodeStartLanes.NodeOf(m, bootstrap), StringComparer.Ordinal);
        var model = definitions.FirstOrDefault(m => m.Id == status.Id);
        if (model is null || nodes.GetNode(nodeOf[model.Id]) is not { } node || !recovery.CanAutoRestart(node, model.Id))
        {
            return; // Stopped by a human, held by a window or a tuning run, paused, or the node is gone: nothing to restart.
        }

        var crash = recovery.RecordCrash(node.NodeId, model.Id, status.Error);
        if (crash.Paused)
        {
            MonitorLog.CrashRestartsPaused(logger, model.Id, node.NodeId, crash.Crashes);
            return;
        }

        var delay = CrashRestartBackoff[Math.Min(crash.Crashes, CrashRestartBackoff.Count) - 1];
        MonitorLog.CrashRestartQueued(logger, model.Id, node.NodeId, crash.Crashes, crash.Limit - 1, delay);
        _ = _starts.Enqueue(node.NodeId, async () =>
        {
            try
            {
                using var audit = AuditScope.Begin(AuditActors.SystemAutoStart);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                await AutoStartAsync(model, node.NodeId, id => NodeOfStatus(id, nodeOf, bootstrap), ct,
                    token => CanRestartAsync(model, node, token)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.AutoStartFailed(logger, ex, model.Id);
            }
        });
    }

    /// <summary>Still the same node boot the model crashed on, still restartable, same placement and not owned by a job.</summary>
    private async Task<bool> CanRestartAsync(ModelDefinition model, NodeLiveStatus crashedOn, CancellationToken ct)
    {
        var current = nodes?.GetNode(crashedOn.NodeId);
        return current is not null && current.NodeUid == crashedOn.NodeUid && current.BootId == crashedOn.BootId &&
            recovery?.CanAutoRestart(current, model.Id) == true && await IsUnchangedAndFreeAsync(model, ct).ConfigureAwait(false);
    }

    private async Task QueueRecoveriesAsync(CancellationToken ct)
    {
        if (recovery is null || nodes is null)
        {
            return;
        }

        var definitions = await registry.ListAsync(ct).ConfigureAwait(false);
        string bootstrap = migrationOptions?.Value.EffectiveBootstrapNodeId ?? RegistryMigrationOptions.DefaultBootstrapNodeId;
        var nodeOf = definitions.ToDictionary(m => m.Id, m => PerNodeStartLanes.NodeOf(m, bootstrap), StringComparer.Ordinal);
        foreach (var node in nodes.ListNodes())
        {
            recovery.RefreshAfterMaintenance(node);
            foreach (string id in recovery.Pending(node))
            {
                var model = definitions.FirstOrDefault(m => m.Id == id && nodeOf[m.Id] == node.NodeId);
                if (model is null)
                {
                    recovery.Observe(node, id, ModelState.Stopped); // Deleted or moved models do not return to the old node.
                    continue;
                }

                var key = (node.NodeUid, node.BootId!, id);
                if (_recovering.TryAdd(key, 0))
                {
                    _ = _starts.Enqueue(node.NodeId, async () =>
                    {
                        try
                        {
                            using var audit = AuditScope.Begin(AuditActors.SystemAutoStart);
                            if (!await AutoStartAsync(model, node.NodeId, modelId => NodeOfStatus(modelId, nodeOf, bootstrap), ct,
                                    token => CanRecoverAsync(model, node, token)).ConfigureAwait(false))
                            {
                                return;
                            }
                            if (await CanRecoverAsync(model, node, ct).ConfigureAwait(false))
                            {
                                var status = await runtime.GetStatusAsync(id, ct).ConfigureAwait(false);
                                recovery.Observe(node, id, status.State);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.AutoStartFailed(logger, ex, id);
                        }
                        finally
                        {
                            _recovering.TryRemove(key, out _);
                        }
                    });
                }
            }
        }
    }

    private async Task<bool> CanRecoverAsync(ModelDefinition model, NodeLiveStatus previous, CancellationToken ct)
    {
        var current = nodes?.GetNode(previous.NodeId);
        if (current is null || current.NodeUid != previous.NodeUid || current.BootId != previous.BootId ||
            recovery?.Pending(current).Contains(model.Id, StringComparer.Ordinal) != true)
        {
            return false;
        }

        return await IsUnchangedAndFreeAsync(model, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsUnchangedAndFreeAsync(ModelDefinition model, CancellationToken ct)
    {
        var saved = await registry.GetAsync(model.Id, ct).ConfigureAwait(false);
        return saved is not null && saved.Placement == model.Placement &&
            (ownership is null || await ownership.GetForModelAsync(model.Id, ct).ConfigureAwait(false) is null);
    }

    private static partial class MonitorLog
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Auto-start of {ModelId} waits: {StartingModelId} is still starting on node {NodeId} (one start at a time per node).")]
        public static partial void AutoStartWaiting(ILogger logger, string modelId, string nodeId, string startingModelId);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Auto-start of {ModelId} waits for node {NodeId}: {Reason}")]
        public static partial void AutoStartWaitingForNode(ILogger logger, string modelId, string nodeId, string reason);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Model {ModelId} crashed on node {NodeId}; auto-restart after crash in {Delay} (attempt {Attempt} of {Attempts}).")]
        public static partial void CrashRestartQueued(ILogger logger, string modelId, string nodeId, int attempt, int attempts, TimeSpan delay);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Model {ModelId} crashed {Crashes} times in a row on node {NodeId}; automatic restarts are paused until it is started by hand.")]
        public static partial void CrashRestartsPaused(ILogger logger, string modelId, string nodeId, int crashes);
    }
}
