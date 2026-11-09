using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Recovery;

/// <summary>
/// Agent startup reconciliation (design §4.4 items 5 and 8, §4.6, §4.8): called once by the host before the listener opens
/// (MN-026), it walks every non-terminal operation in the log, compares it with what exists on the node and finishes,
/// resumes or fails it, so no half-done work is left and no container is created twice.
/// <list type="number">
/// <item><b>Reservations</b> are rebuilt first from every non-terminal launch that recorded one, whatever its stage
/// (<c>admitted</c> … <c>ready</c>: a crash between the two writes of a finished launch leaves <c>ready</c>).</item>
/// <item><b>Launches</b>, by stage: <c>ready</c> → Succeeded; <c>started</c> → adopted (the runtime watcher settles it at
/// Ready or failure), or settled now when the instance already is Ready/Failed, or Failed when it is gone;
/// <c>created</c> with its instance → started if the master triple is still the accepted one, else the instance is
/// removed and the launch fails with <c>superseded</c>; before create (<c>accepted</c>, <c>admitted</c>, <c>pulling</c>,
/// <c>pulled</c>, <c>stopping</c>, <c>removed</c>, or <c>created</c> without an instance) → the driver phase re-runs from
/// the pull with its reservation (re-acquired when none was recorded), after the fencing and envelope checks. A launch
/// with a newer stop of the same model, or with an accepted cancel, ends Cancelled instead.</item>
/// <item><b>Stops</b> are finished (stop and remove; a launch of that model waiting for Ready ends Cancelled).</item>
/// <item><b>Other kinds</b> go to the <see cref="IOperationRecoveryHandler"/> claiming them: host services (desired-state
/// reconcile or <c>outcome_unknown</c>), downloads, image pulls, folder deletes. A PKI rotation no handler claims stays
/// pending (design §10.2); any other unclaimed kind fails with <c>internal_error</c>.</item>
/// <item><b>Accepted cancels</b> left active are settled last, through the target's <see cref="IOperationCanceller"/>,
/// with the same answers as <c>POST /operations/{id}/cancel</c>.</item>
/// <item><b>Adoption</b>: the instances are listed again and published on the <see cref="RuntimeStateBoard"/>; labelled
/// instances no active operation drives are adopted untouched (today's rule).</item>
/// </list>
/// If Docker cannot be listed at start, launches that would need it fail with <c>docker_unavailable</c> (their
/// reservations are released) and stops fail as a live stop would; nothing is guessed from a failed listing.
/// </summary>
public sealed partial class AgentReconciler
{
    private readonly RuntimeOperations _runtimes;
    private readonly MutationGate _gate;
    private readonly RuntimeStateBoard _board;
    private readonly IReservationManager _reservations;
    private readonly IReadOnlyList<IOperationRecoveryHandler> _handlers;
    private readonly IReadOnlyList<IOperationCanceller> _cancellers;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <summary>PKI kinds that stay pending when no handler claims them (design §4.4 item 5: "rotation pending → keep").</summary>
    private static readonly HashSet<string> PkiKinds = new(StringComparer.Ordinal)
    {
        OperationKinds.PkiRotationStart,
        OperationKinds.PkiRotationCert,
        OperationKinds.PkiRotationCommit,
        OperationKinds.PkiRotationAbort,
        OperationKinds.PkiTrustSet,
    };

    /// <summary>Creates the reconciler.</summary>
    public AgentReconciler(
        RuntimeOperations runtimes,
        MutationGate gate,
        RuntimeStateBoard board,
        IReservationManager reservations,
        IEnumerable<IOperationRecoveryHandler> handlers,
        IEnumerable<IOperationCanceller> cancellers,
        TimeProvider? time = null,
        ILogger<AgentReconciler>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(reservations);
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(cancellers);
        _runtimes = runtimes;
        _gate = gate;
        _board = board;
        _reservations = reservations;
        _handlers = [.. handlers];
        _cancellers = [.. cancellers];
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    private IOperationLog Log => _gate.Log;

    /// <summary>Runs the reconciliation once; call it before the listener opens.</summary>
    public async Task<ReconciliationReport> RunAsync(CancellationToken ct = default)
    {
        var recoverable = Log.ListRecoverable();
        LogStarting(_logger, recoverable.Count);

        int rebuilt = await RebuildReservationsAsync(recoverable, ct).ConfigureAwait(false);

        var (instances, listingError) = await TryListAsync(ct).ConfigureAwait(false);
        var cancelTargets = recoverable
            .Where(o => IsKind(o, OperationKinds.OperationCancel))
            .Select(o => o.Status.Target)
            .ToHashSet(StringComparer.Ordinal);

        var results = new List<ReconciledOperation>(recoverable.Count);
        var cancels = new List<RecoverableOperation>();
        for (int i = 0; i < recoverable.Count; i++)
        {
            var operation = recoverable[i];
            ReconciliationAction action;
            try
            {
                switch (operation.Status.Kind)
                {
                    case OperationKinds.Launch:
                        bool stoppedLater = recoverable.Skip(i + 1).Any(o =>
                            IsKind(o, OperationKinds.Stop) && string.Equals(o.Status.Target, operation.Status.Target, StringComparison.Ordinal));
                        action = await ReconcileLaunchAsync(operation, instances, listingError,
                            stoppedLater || cancelTargets.Contains(operation.Status.Id), ct).ConfigureAwait(false);
                        break;
                    case OperationKinds.Stop:
                        await _runtimes.ResumeStopAsync(operation.Status.Id, operation.Status.Target, operation.Master).ConfigureAwait(false);
                        action = ReconciliationAction.Completed;
                        break;
                    case OperationKinds.OperationCancel:
                        cancels.Add(operation);
                        continue;
                    default:
                        action = await DelegateAsync(operation, ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                action = await FailAfterErrorAsync(operation, ex).ConfigureAwait(false);
            }

            results.Add(Result(operation, action));
        }

        foreach (var cancel in cancels)
        {
            await SettleCancelAsync(cancel, ct).ConfigureAwait(false);
            results.Add(Result(cancel, ReconciliationAction.Completed));
        }

        var adopted = await AdoptAsync(ct).ConfigureAwait(false);
        var report = new ReconciliationReport
        {
            Operations = [.. results.OrderBy(r => IndexOf(recoverable, r.OperationId))],
            ReservationsRebuilt = rebuilt,
            Adopted = adopted,
            InstanceListingError = listingError?.Message,
        };
        LogFinished(_logger, recoverable.Count, rebuilt, adopted.Count);
        return report;
    }

    // ---------------------------------------------------------------- reservations

    private async Task<int> RebuildReservationsAsync(IReadOnlyList<RecoverableOperation> recoverable, CancellationToken ct)
    {
        var rebuilt = recoverable
            .Where(o => IsKind(o, OperationKinds.Launch) && o.Reservation is not null)
            .Select(o => new RebuiltReservation(o.Reservation!, ReadLaunch(o)?.Admission))
            .ToList();

        if (_reservations is ReservationManager manager)
        {
            // Keeps the admission inputs, so the re-probe before create uses the launch's own reserve and force flag.
            await manager.RebuildAsync(rebuilt, ct).ConfigureAwait(false);
        }
        else
        {
            await _reservations.RebuildAsync(rebuilt.Select(r => r.Reservation), ct).ConfigureAwait(false);
        }

        return rebuilt.Count;
    }

    // ---------------------------------------------------------------- launches

    private async Task<ReconciliationAction> ReconcileLaunchAsync(
        RecoverableOperation operation, IReadOnlyDictionary<string, RuntimeStatus>? instances, Exception? listingError, bool cancelled, CancellationToken ct)
    {
        var status = operation.Status;
        string id = status.Id;
        string modelId = status.Target;
        string stage = status.Stage ?? OperationStages.Accepted;
        bool beforeCreate = stage is not (OperationStages.Created or OperationStages.Started or OperationStages.Ready);

        if (cancelled && beforeCreate)
        {
            // A newer stop of the model, or an accepted cancel: a live agent would have ended this launch Cancelled too.
            await _runtimes.FinishLaunchAsync(id, OperationState.Cancelled, null, null).ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        if (instances is null)
        {
            await _runtimes.FinishLaunchAsync(id, OperationState.Failed, RuntimeOperations.DriverError(listingError!), null).ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        var instance = instances.GetValueOrDefault(modelId);
        switch (stage)
        {
            case OperationStages.Ready:
                await _runtimes.FinishLaunchAsync(id, OperationState.Succeeded, null, instance).ConfigureAwait(false);
                return ReconciliationAction.Completed;

            case OperationStages.Started:
                if (instance is null || !BelongsTo(instance, status))
                {
                    await _runtimes.FinishLaunchAsync(id, OperationState.Failed, Failure(ErrorCodes.InternalError,
                        "The instance of " + modelId + " disappeared before it was Ready."), null).ConfigureAwait(false);
                    return ReconciliationAction.Completed;
                }

                return await SettleStartedAsync(id, modelId, instance).ConfigureAwait(false);

            case OperationStages.Created when instance is not null && string.Equals(instance.OperationId, id, StringComparison.Ordinal):
                return await ReconcileCreatedAsync(operation, instance, ct).ConfigureAwait(false);

            default:
                return await ResumeFromPullAsync(operation, instance, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Launch <c>created</c> whose instance exists (the agent died between create and start, design §4.6): start it if the
    /// master that submitted it is still the accepted one; otherwise remove it and fail with <c>superseded</c>.
    /// </summary>
    private async Task<ReconciliationAction> ReconcileCreatedAsync(RecoverableOperation operation, RuntimeStatus instance, CancellationToken ct)
    {
        string id = operation.Status.Id;
        string modelId = operation.Status.Target;
        var driver = DriverFor(instance.Delivery);
        if (driver is null)
        {
            await _runtimes.FinishLaunchAsync(id, OperationState.Failed, Failure(ErrorCodes.InternalError,
                "No runtime driver for " + instance.Delivery + " instances is registered."), null).ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        if (!_gate.IsCurrent(operation.Master))
        {
            await driver.StopAsync(modelId, new RecordingSink(_gate, id), ct).ConfigureAwait(false);
            _board.Report(modelId, null);
            LogSupersededRemoved(_logger, id, modelId);
            await _runtimes.FinishLaunchAsync(id, OperationState.Failed, Superseded(id), null).ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        RuntimeStatus started;
        try
        {
            started = await driver.StartCreatedAsync(modelId, ct).ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            await _runtimes.FinishLaunchAsync(id, OperationState.Failed, ex.Error, null).ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        await _gate.RecordStageAsync(id, new OperationStageUpdate { Stage = OperationStages.Started }, CancellationToken.None).ConfigureAwait(false);
        _board.Report(modelId, started);
        return await SettleStartedAsync(id, modelId, started).ConfigureAwait(false);
    }

    /// <summary>A launch whose instance exists and was started: settle it now when it already ended, else wait for Ready.</summary>
    private async Task<ReconciliationAction> SettleStartedAsync(string id, string modelId, RuntimeStatus instance)
    {
        switch (instance.State)
        {
            case ModelState.Ready:
                await _runtimes.FinishLaunchAsync(id, OperationState.Succeeded, null, instance).ConfigureAwait(false);
                return ReconciliationAction.Completed;
            case ModelState.Failed:
            case ModelState.Stopped:
                await _runtimes.FinishLaunchAsync(id, OperationState.Failed, RuntimeOperations.InstanceFailed(instance), null).ConfigureAwait(false);
                return ReconciliationAction.Completed;
            default:
                // Starting (or Stopping): the runtime watcher settles it and releases the reservation (design §4.8).
                _runtimes.AdoptAwaiting(id, modelId);
                return ReconciliationAction.Resumed;
        }
    }

    /// <summary>
    /// A launch that had not created its instance yet (design §4.4 item 5: admitted/pulling with no container → re-run from
    /// the pull stage, reservation re-acquired). Nothing is created for a superseded master or an expired envelope.
    /// </summary>
    private async Task<ReconciliationAction> ResumeFromPullAsync(RecoverableOperation operation, RuntimeStatus? instance, CancellationToken ct)
    {
        var status = operation.Status;
        string id = status.Id;
        string modelId = status.Target;

        if (!_gate.IsCurrent(operation.Master))
        {
            await _runtimes.FinishLaunchAsync(id, OperationState.Failed, Superseded(id), null).ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        if (status.ExpiresAt != default && status.ExpiresAt <= _time.GetUtcNow())
        {
            await _runtimes.FinishLaunchAsync(id, OperationState.Failed, Failure(ErrorCodes.OperationExpired,
                "Launch " + id + " expired before the agent could finish it after a restart; nothing was created."), null).ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        var request = ReadLaunch(operation);
        if (request?.Spec is null || request.Admission is null)
        {
            await _runtimes.FinishLaunchAsync(id, OperationState.Failed, Failure(ErrorCodes.InternalError,
                "The stored request of launch " + id + " cannot be read; nothing was created."), null).ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        if (request.Spec.SecretEnvNames.Count > 0 || request.Registry is not null)
        {
            // Secret environment values and registry logins are never stored (design §10.4), so the launch cannot be rebuilt from the log.
            await _runtimes.FinishLaunchAsync(id, OperationState.Failed, Failure(ErrorCodes.InternalError,
                "Launch " + id + " carries secret values, which are not stored; start the model again."), null).ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        var driver = DriverFor(request.Spec.Kind);
        if (driver is null)
        {
            await _runtimes.FinishLaunchAsync(id, OperationState.Failed, Failure(ErrorCodes.SpecRejected,
                "No runtime driver for " + request.Spec.Kind + " instances is registered."), null).ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        var reservation = operation.Reservation
            ?? _reservations.List().FirstOrDefault(r => string.Equals(r.OperationId, id, StringComparison.Ordinal));
        if (reservation is null)
        {
            // Accepted but the admitted record (with the reservation) never made it to disk: reserve again under the
            // node-wide admission lock, as the live launch did before its 202.
            var reserved = await _reservations.TryReserveAsync(new StartReservationRequest
            {
                OperationId = id,
                ModelId = modelId,
                Admission = request.Admission,
                GpuUuids = request.Spec.Gpus.Mode == GpuSelectionMode.Uuids ? request.Spec.Gpus.Uuids ?? [] : [],
                ReleasedGiB = request.Mode == LaunchMode.Replace && instance is { State: not ModelState.Failed } ? BudgetOf(instance) : 0,
            }, ct).ConfigureAwait(false);
            if (reserved.Outcome != ReservationOutcome.Reserved || reserved.Reservation is null)
            {
                await _runtimes.FinishLaunchAsync(id, OperationState.Failed, reserved.Error ?? Failure(
                    reserved.Outcome == ReservationOutcome.StartConflict ? ErrorCodes.StartConflict : ErrorCodes.AdmissionRefused,
                    "The start was refused when the agent resumed it after a restart."), null).ConfigureAwait(false);
                return ReconciliationAction.Completed;
            }

            reservation = reserved.Reservation;
            await _gate.RecordStageAsync(id, new OperationStageUpdate
            {
                Stage = status.Stage is null or OperationStages.Accepted ? OperationStages.Admitted : null,
                Reservation = reservation,
            }, CancellationToken.None).ConfigureAwait(false);
        }

        _runtimes.ResumeLaunch(id, modelId, request, operation.Master, driver, reservation);
        LogLaunchResumed(_logger, id, modelId, status.Stage ?? OperationStages.Accepted);
        return ReconciliationAction.Resumed;
    }

    // ---------------------------------------------------------------- other kinds

    private async Task<ReconciliationAction> DelegateAsync(RecoverableOperation operation, CancellationToken ct)
    {
        var status = operation.Status;
        var handler = _handlers.FirstOrDefault(h => h.Kinds.Contains(status.Kind, StringComparer.Ordinal));
        if (handler is null)
        {
            if (PkiKinds.Contains(status.Kind))
            {
                LogKept(_logger, status.Id, status.Kind);
                return ReconciliationAction.Kept;
            }

            await _gate.CompleteAsync(status.Id, OperationState.Failed, Failure(ErrorCodes.InternalError,
                "The agent restarted and cannot recover '" + status.Kind + "' operations; nothing more was done."), null, CancellationToken.None)
                .ConfigureAwait(false);
            return ReconciliationAction.Completed;
        }

        await handler.RecoverAsync(operation, ct).ConfigureAwait(false);
        return ReconciliationAction.Delegated;
    }

    /// <summary>
    /// An accepted <c>operationCancel</c> left active (W2 review MN-092): answered as the cancel route would, through the
    /// target's canceller (a launch cancelled by the reconciler is already terminal here, so it succeeds with its status).
    /// </summary>
    private async Task SettleCancelAsync(RecoverableOperation cancel, CancellationToken ct)
    {
        string id = cancel.Status.Id;
        string targetId = cancel.Status.Target;
        try
        {
            if (Log.Find(targetId) is not { } target)
            {
                await CompleteCancelAsync(id, OperationState.Failed, Failure(ErrorCodes.NotFound,
                    "Operation " + targetId + " is not known on this node."), null).ConfigureAwait(false);
                return;
            }

            if (target.State.IsTerminal())
            {
                await CompleteCancelAsync(id, OperationState.Succeeded, null, target).ConfigureAwait(false);
                return;
            }

            var canceller = _cancellers.FirstOrDefault(c => c.Kinds.Contains(target.Kind, StringComparer.Ordinal));
            if (canceller is null)
            {
                await CompleteCancelAsync(id, OperationState.Failed, Failure(ErrorCodes.InvalidState,
                    "Operations of kind '" + target.Kind + "' cannot be cancelled."), null).ConfigureAwait(false);
                return;
            }

            if (canceller.CheckCancellable(target) is { } refused)
            {
                await CompleteCancelAsync(id, OperationState.Failed, refused.Error, null).ConfigureAwait(false);
                return;
            }

            var after = await canceller.CancelAsync(target, ct).ConfigureAwait(false);
            await CompleteCancelAsync(id, OperationState.Succeeded, null, after).ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            await CompleteCancelAsync(id, OperationState.Failed, ex.Error, null).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await FailAfterErrorAsync(cancel, ex).ConfigureAwait(false);
        }
    }

    private Task<OperationStatus> CompleteCancelAsync(string id, OperationState state, AgentError? failure, OperationStatus? target) =>
        _gate.CompleteAsync(id, state, failure, target is null ? null : AgentJson.ToElement(target), CancellationToken.None);

    // ---------------------------------------------------------------- adoption

    /// <summary>
    /// Lists the instances again (resumed work may have changed them), publishes them on the board so the first
    /// <c>/status</c> is complete, and returns those no active operation drives: adopted untouched (design §4.4 item 5).
    /// </summary>
    private async Task<IReadOnlyList<RuntimeStatus>> AdoptAsync(CancellationToken ct)
    {
        long version = _board.BeginObservation();
        IReadOnlyList<RuntimeStatus> listing;
        try
        {
            listing = await _runtimes.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogListingFailed(_logger, ex.Message);
            return [];
        }

        _board.Apply(listing, version);
        return [.. listing.Where(instance =>
            instance.OperationId is not { } creator || Log.Find(creator) is not { } operation || operation.State.IsTerminal())];
    }

    // ---------------------------------------------------------------- helpers

    private async Task<(IReadOnlyDictionary<string, RuntimeStatus>? Instances, Exception? Error)> TryListAsync(CancellationToken ct)
    {
        try
        {
            var listing = await _runtimes.ListAsync(ct).ConfigureAwait(false);
            var byModel = new Dictionary<string, RuntimeStatus>(StringComparer.Ordinal);
            foreach (var instance in listing)
            {
                byModel.TryAdd(instance.ModelId, instance);
            }

            return (byModel, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogListingFailed(_logger, ex.Message);
            return (null, ex);
        }
    }

    /// <summary>
    /// Anything unexpected while reconciling one operation fails it (an active operation is never pruned, so leaving it
    /// would hold its reservation forever); the next operation is still reconciled.
    /// </summary>
    private async Task<ReconciliationAction> FailAfterErrorAsync(RecoverableOperation operation, Exception ex)
    {
        string id = operation.Status.Id;
        LogOperationFailed(_logger, id, operation.Status.Kind, ex.Message);
        var error = ex is AgentException agent ? agent.Error : RuntimeOperations.DriverError(ex);
        try
        {
            if (IsKind(operation, OperationKinds.Launch))
            {
                await _runtimes.FinishLaunchAsync(id, OperationState.Failed, error, null).ConfigureAwait(false);
            }
            else if (Log.Find(id) is { } current && !current.State.IsTerminal())
            {
                await _gate.CompleteAsync(id, OperationState.Failed, error, null, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception record) when (record is not OperationCanceledException)
        {
            LogOperationFailed(_logger, id, operation.Status.Kind, record.Message);
        }

        return ReconciliationAction.Completed;
    }

    private ReconciledOperation Result(RecoverableOperation operation, ReconciliationAction action)
    {
        var status = operation.Status;
        return new ReconciledOperation(status.Id, status.Kind, status.Stage, action, Log.Find(status.Id)?.State ?? status.State);
    }

    private static int IndexOf(IReadOnlyList<RecoverableOperation> operations, string id)
    {
        for (int i = 0; i < operations.Count; i++)
        {
            if (string.Equals(operations[i].Status.Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    private IRuntimeDriver? DriverFor(LaunchDelivery delivery) => _runtimes.Drivers.FirstOrDefault(d => d.Delivery == delivery);

    private static bool IsKind(RecoverableOperation operation, string kind) =>
        string.Equals(operation.Status.Kind, kind, StringComparison.Ordinal);

    /// <summary>The runtime watcher's rule: the creating operation's label decides; an unlabelled instance counts once created.</summary>
    private static bool BelongsTo(RuntimeStatus instance, OperationStatus operation) =>
        instance.OperationId is { } creator
            ? string.Equals(creator, operation.Id, StringComparison.Ordinal)
            : operation.Stage is OperationStages.Created or OperationStages.Started;

    private static LaunchRequest? ReadLaunch(RecoverableOperation operation)
    {
        if (operation.Payload is not { ValueKind: JsonValueKind.Object } payload)
        {
            return null;
        }

        try
        {
            return payload.Deserialize<LaunchRequest>(AgentJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static double BudgetOf(RuntimeStatus instance) =>
        instance.Labels.TryGetValue(ContainerLabels.BudgetGiB, out string? text)
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double budget)
            ? budget
            : 0;

    private static AgentError Failure(string code, string message) => new() { Code = code, Message = message };

    private static AgentError Superseded(string id) => Failure(ErrorCodes.Superseded,
        "Operation " + id + " was submitted by a master that has since been superseded; the agent restarted and did not continue it.");

    /// <summary>Records the stages of a superseded launch's removal; no fencing check (removing its own instance is the point).</summary>
    private sealed class RecordingSink(MutationGate gate, string operationId) : IRuntimeStageSink
    {
        public Task BeforeStepAsync(string stage, CancellationToken ct) => Task.CompletedTask;

        public Task StageReachedAsync(string stage, OperationProgress? progress, CancellationToken ct) =>
            gate.RecordStageAsync(operationId, new OperationStageUpdate { Stage = stage, Progress = progress }, CancellationToken.None);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation: {Count} non-terminal operation(s) found at agent start.")]
    private static partial void LogStarting(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation done: {Count} operation(s), {Reservations} reservation(s) rebuilt, {Adopted} instance(s) adopted.")]
    private static partial void LogFinished(ILogger logger, int count, int reservations, int adopted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation: launch {OperationId} of {ModelId} resumed from stage {Stage}.")]
    private static partial void LogLaunchResumed(ILogger logger, string operationId, string modelId, string stage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation: launch {OperationId} was superseded; its created instance of {ModelId} was removed.")]
    private static partial void LogSupersededRemoved(ILogger logger, string operationId, string modelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation: {Kind} operation {OperationId} is kept pending.")]
    private static partial void LogKept(ILogger logger, string operationId, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconciliation: listing the runtime instances failed: {Message}")]
    private static partial void LogListingFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation of {Kind} operation {OperationId} failed: {Message}")]
    private static partial void LogOperationFailed(ILogger logger, string operationId, string kind, string message);
}
