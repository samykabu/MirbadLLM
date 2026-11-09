using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// The agent's launch and stop operations (design §4.3, §4.4, §4.8, §4.9), behind <c>POST /runtimes/{modelId}/launch</c>
/// and <c>/stop</c>, and the canceller of launches (§4.4 item 7).
/// <para>Launch, synchronously (before 202): fencing check → envelope and spec checks → for <c>ensureStarted</c> an
/// existing Starting/Ready/Stopping instance is returned as it is → start reservation (start rule, admission, port) →
/// durable acceptance → <c>admitted</c> stage with the reservation. Then, in the background under the model's execution
/// lock: the driver pulls, (replace) stops and removes, creates and starts, calling back before every destructive step,
/// where fencing is re-checked and, before create, admission is re-probed after a slow pull or a removal. The launch
/// stays Running until the instance is Ready (Succeeded, reservation released) or fails (Failed, released); the
/// <see cref="RuntimeWatcher"/> observes that.</para>
/// <para>GPU selection by UUID (design §5.1, MN-106): a launch with <c>gpus.mode = uuids</c> re-reads the node's GPU
/// inventory before the driver's checks, so the driver's synchronous <see cref="IRuntimeDriver.CheckSpec"/> sees a card
/// that was renumbered, removed or replaced since the last read and refuses a missing UUID with 422 <c>gpu_missing</c>
/// before anything is reserved or recorded.</para>
/// <para>Stop: fencing → acceptance → 202; then stop and remove (no instance → Succeeded). A launch still waiting for
/// Ready on that model ends Cancelled.</para>
/// </summary>
public sealed partial class RuntimeOperations : IOperationCanceller, IDisposable
{
    private readonly IReadOnlyList<IRuntimeDriver> _drivers;
    private readonly MutationGate _gate;
    private readonly IReservationManager _reservations;
    private readonly RuntimeStateBoard _board;
    private readonly IEventBus _events;
    private readonly TimeProvider _time;
    private readonly AgentRuntimeOptions _options;
    private readonly ILogger _logger;
    private readonly IGpuInventory? _gpus;

    private readonly SemaphoreSlim _acceptLock = new(1, 1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LaunchRun> _executing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _awaitingReady = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _stopping = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _background = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _finishing = new(StringComparer.Ordinal);

    /// <summary>Creates the runtime operations.</summary>
    public RuntimeOperations(
        IEnumerable<IRuntimeDriver> drivers,
        MutationGate gate,
        IReservationManager reservations,
        RuntimeStateBoard board,
        IEventBus events,
        TimeProvider? time = null,
        AgentRuntimeOptions? options = null,
        ILogger<RuntimeOperations>? logger = null,
        IGpuInventory? gpuInventory = null)
    {
        ArgumentNullException.ThrowIfNull(drivers);
        _drivers = [.. drivers];
        _gate = gate;
        _reservations = reservations;
        _board = board;
        _events = events;
        _time = time ?? TimeProvider.System;
        _options = options ?? new AgentRuntimeOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _gpus = gpuInventory;
    }

    /// <summary>
    /// Releases the acceptance lock (requests are drained before singletons are disposed). The per-model execution locks are
    /// left to the GC: background operations may still be unwinding at shutdown (see <see cref="WhenIdleAsync"/>).
    /// </summary>
    public void Dispose() => _acceptLock.Dispose();

    /// <inheritdoc/>
    public IReadOnlyList<string> Kinds { get; } = [OperationKinds.Launch];

    /// <summary>The registered drivers.</summary>
    public IReadOnlyList<IRuntimeDriver> Drivers => _drivers;

    /// <summary>The operation log behind the gate.</summary>
    internal IOperationLog Log => _gate.Log;

    // ---------------------------------------------------------------- reads

    /// <summary>Every instance of every driver.</summary>
    public async Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default)
    {
        var all = new List<RuntimeStatus>();
        foreach (var driver in _drivers)
        {
            all.AddRange(await driver.ListAsync(ct).ConfigureAwait(false));
        }

        return all;
    }

    /// <summary>
    /// <c>GET /runtimes/{modelId}</c>: the instance as its driver reports it now, also applied to the board (an observation,
    /// <see cref="RuntimeStateBoard.ApplyOne"/>), so <c>/status</c> and the event stream are never behind an answer this
    /// agent already gave (W8 review MN-128: the master read Ready here, then a <c>/status</c> taken before the watcher's next
    /// round still said Starting and took it back).
    /// </summary>
    public async Task<RuntimeStatus?> ObserveAsync(string modelId, CancellationToken ct = default)
    {
        long observation = _board.BeginObservation();
        var (_, instance) = await FindAsync(modelId, ct).ConfigureAwait(false);
        _board.ApplyOne(modelId, instance, observation);
        return instance;
    }

    /// <summary>The instance of a model and the driver that owns it; (null, null) when stopped.</summary>
    public async Task<(IRuntimeDriver? Driver, RuntimeStatus? Instance)> FindAsync(string modelId, CancellationToken ct = default)
    {
        foreach (var driver in _drivers)
        {
            if (await driver.GetAsync(modelId, ct).ConfigureAwait(false) is { } instance)
            {
                return (driver, instance);
            }
        }

        return (null, null);
    }

    // ---------------------------------------------------------------- launch

    /// <summary>Handles <c>POST /runtimes/{modelId}/launch</c>; 202 with the operation, or the refusal.</summary>
    public async Task<MutationResult> LaunchAsync(
        string modelId, LaunchRequest request, JsonElement payload, MasterTriple master, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(request);

        if (await _gate.CheckFencingAsync(master, ct).ConfigureAwait(false) is { } fenced)
        {
            return MutationResult.Refused(fenced);
        }

        LaunchSpec? requested = request.Spec; // null in a malformed body: refused below under the lock
        if (requested?.Gpus is { Mode: GpuSelectionMode.Uuids } && _gpus is not null)
        {
            // A fresh inventory read (MN-104/MN-106): the driver's synchronous check below then sees the GPUs as they are
            // now, so a renumbered card is still found by its UUID and a removed one is gpu_missing. Read before the
            // node-wide accept lock (W8 review MN-128): nvidia-smi takes up to its 5 s timeout per run, and every launch
            // and stop on the node waits for that lock.
            await _gpus.RefreshAsync(ct).ConfigureAwait(false);
        }

        await _acceptLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gate.IsKnown(request.OperationId))
            {
                return FromGate(await _gate.AcceptAsync(request, OperationKinds.Launch, modelId, master, payload, ct).ConfigureAwait(false));
            }

            if (_gate.CheckEnvelope(request) is { } envelope)
            {
                return MutationResult.Refused(envelope);
            }

            var driver = _drivers.FirstOrDefault(d => request.Spec is not null && d.Delivery == request.Spec.Kind);
            if (LaunchSpecGuard.Check(request.Spec, driver is not null) is { } rejected)
            {
                return MutationResult.Refused(rejected);
            }

            try
            {
                // The driver's own guardrails (approved digest, read-only models mount, fixed port, placed GPUs present,
                // ...) before anything is reserved or recorded, so a spec it would refuse is a synchronous 422
                // (W2 review MN-092; gpu_missing, MN-106).
                driver!.CheckSpec(modelId, request.Spec);
            }
            catch (AgentException ex)
            {
                return MutationResult.Refused(ex);
            }

            RuntimeStatus? existing;
            long observation = _board.BeginObservation();
            try
            {
                existing = await driver!.GetAsync(modelId, ct).ConfigureAwait(false);
            }
            catch (AgentException ex)
            {
                return MutationResult.Refused(ex);
            }
            catch (Exception ex) when (IsDriverUnavailable(ex))
            {
                return MutationResult.Refused(new AgentException(StatusCodes.Status503ServiceUnavailable, DriverError(ex)));
            }

            if (existing is not null)
            {
                // So its later removal is published even before the watcher saw it. An observation, not a report: a
                // watcher round that started after this read (and saw the instance Ready) is never overwritten by it.
                _board.ApplyOne(modelId, existing, observation);
            }

            if (request.Mode == LaunchMode.EnsureStarted && existing is { State: ModelState.Starting or ModelState.Ready or ModelState.Stopping })
            {
                return await AcceptExistingAsync(modelId, request, payload, master, existing, ct).ConfigureAwait(false);
            }

            return await AcceptNewLaunchAsync(modelId, request, payload, master, driver!, existing, ct).ConfigureAwait(false);
        }
        finally
        {
            _acceptLock.Release();
        }
    }

    /// <summary><c>ensureStarted</c> on a Starting/Ready/Stopping instance: nothing to create, no reservation (design §4.3).</summary>
    private async Task<MutationResult> AcceptExistingAsync(
        string modelId, LaunchRequest request, JsonElement payload, MasterTriple master, RuntimeStatus existing, CancellationToken ct)
    {
        var accepted = await _gate.AcceptAsync(request, OperationKinds.Launch, modelId, master, payload, ct).ConfigureAwait(false);
        if (accepted.Error is not null || accepted.Outcome != OperationAcceptOutcome.Accepted)
        {
            return FromGate(accepted);
        }

        string id = request.OperationId;
        try
        {
            if (existing.State == ModelState.Starting)
            {
                // The instance exists; this operation completes when it reports Ready (or fails).
                var started = await _gate.RecordStageAsync(id, new OperationStageUpdate { Stage = OperationStages.Started }, CancellationToken.None).ConfigureAwait(false);
                _awaitingReady[id] = modelId;
                return MutationResult.Ok(StatusCodes.Status202Accepted, started);
            }

            if (existing.State == ModelState.Ready)
            {
                await _gate.RecordStageAsync(id, new OperationStageUpdate { Stage = OperationStages.Ready }, CancellationToken.None).ConfigureAwait(false);
            }

            var done = await _gate.CompleteAsync(id, OperationState.Succeeded, null, AgentJson.ToElement(existing), CancellationToken.None).ConfigureAwait(false);
            return MutationResult.Ok(StatusCodes.Status202Accepted, done);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MutationResult.Refused(StateUnavailable(ex));
        }
    }

    private async Task<MutationResult> AcceptNewLaunchAsync(
        string modelId, LaunchRequest request, JsonElement payload, MasterTriple master, IRuntimeDriver driver,
        RuntimeStatus? existing, CancellationToken ct)
    {
        string id = request.OperationId;
        var run = new LaunchRun(id, modelId, request, master, driver);

        // Registered before the reservation exists, so the watcher never mistakes it for an orphan.
        _executing[id] = run;
        bool handedOff = false;
        try
        {
            ReservationResult reservation;
            try
            {
                reservation = await _reservations.TryReserveAsync(new StartReservationRequest
                {
                    OperationId = id,
                    ModelId = modelId,
                    Admission = request.Admission,
                    GpuUuids = request.Spec.Gpus.Mode == GpuSelectionMode.Uuids ? request.Spec.Gpus.Uuids ?? [] : [],
                    ReleasedGiB = request.Mode == LaunchMode.Replace && existing is { State: not ModelState.Failed } ? BudgetOf(existing) : 0,
                }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsDriverUnavailable(ex))
            {
                // Docker down while listing instances under the admission lock (W2 review MN-092): 503, not 500.
                return MutationResult.Refused(new AgentException(StatusCodes.Status503ServiceUnavailable, DriverError(ex)));
            }
            catch (InvalidOperationException ex)
            {
                // Port range exhausted (or a probe that cannot answer): nothing was reserved.
                return MutationResult.Refused(new AgentException(StatusCodes.Status503ServiceUnavailable, new AgentError
                {
                    Code = ErrorCodes.StateUnavailable,
                    Message = "The start could not be admitted: " + ex.Message,
                }));
            }

            if (reservation.Outcome != ReservationOutcome.Reserved || reservation.Reservation is null)
            {
                return MutationResult.Refused(new AgentException(StatusCodes.Status409Conflict, reservation.Error ?? new AgentError
                {
                    Code = reservation.Outcome == ReservationOutcome.StartConflict ? ErrorCodes.StartConflict : ErrorCodes.AdmissionRefused,
                    Message = "The start was refused.",
                }));
            }

            run.Reservation = reservation.Reservation; // the reservation manager publishes the event (single publisher, MN-092)

            GateResult accepted;
            try
            {
                accepted = await _gate.AcceptAsync(request, OperationKinds.Launch, modelId, master, payload, ct).ConfigureAwait(false);
            }
            catch
            {
                // e.g. the client aborted while the log's write lock was held: nothing was accepted, so nothing is held.
                await ReleaseReservationAsync(id).ConfigureAwait(false);
                throw;
            }

            if (accepted.Error is not null || accepted.Outcome != OperationAcceptOutcome.Accepted)
            {
                await ReleaseReservationAsync(id).ConfigureAwait(false);
                return FromGate(accepted);
            }

            OperationStatus admitted;
            try
            {
                admitted = await _gate.RecordStageAsync(id, new OperationStageUpdate
                {
                    Stage = OperationStages.Admitted,
                    Reservation = reservation.Reservation,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var error = StateUnavailable(ex);
                await TryCompleteAsync(id, OperationState.Failed, error.Error, null).ConfigureAwait(false);
                await ReleaseReservationAsync(id).ConfigureAwait(false);
                return MutationResult.Refused(error);
            }

            handedOff = true;
            Track(id, Task.Run(() => ExecuteLaunchAsync(run), CancellationToken.None));
            return MutationResult.Ok(StatusCodes.Status202Accepted, admitted);
        }
        finally
        {
            if (!handedOff)
            {
                _executing.TryRemove(id, out _);
                run.Completed.TrySetResult();
            }
        }
    }

    private async Task ExecuteLaunchAsync(LaunchRun run)
    {
        var ct = run.Cancellation.Token;
        var modelLock = ModelLock(run.ModelId);
        bool locked = false;
        try
        {
            await modelLock.WaitAsync(ct).ConfigureAwait(false);
            locked = true;

            int port = run.Reservation?.HostPort
                ?? await _reservations.AllocatePortAsync(run.ModelId, run.OperationId, ct).ConfigureAwait(false);
            var result = await run.Driver.LaunchAsync(new RuntimeLaunch
            {
                ModelId = run.ModelId,
                OperationId = run.OperationId,
                Mode = run.Request.Mode,
                Spec = run.Request.Spec,
                HostPort = port,
                Registry = run.Request.Registry,
            }, new LaunchStageSink(this, run), ct).ConfigureAwait(false);

            var instance = result.Instance;
            _board.Report(run.ModelId, instance);
            run.ReleaseBoardClaim();
            switch (instance.State)
            {
                case ModelState.Ready:
                    await FinishLaunchAsync(run.OperationId, OperationState.Succeeded, null, instance).ConfigureAwait(false);
                    break;
                case ModelState.Stopping when result.Outcome == RuntimeLaunchOutcome.AlreadyRunning:
                    await FinishLaunchAsync(run.OperationId, OperationState.Succeeded, null, instance).ConfigureAwait(false);
                    break;
                case ModelState.Failed:
                case ModelState.Stopped:
                    await FinishLaunchAsync(run.OperationId, OperationState.Failed, InstanceFailed(instance), null).ConfigureAwait(false);
                    break;
                default:
                    // Starting: the reservation is held until the watcher sees Ready or a failure (design §4.8).
                    _awaitingReady[run.OperationId] = run.ModelId;
                    break;
            }
        }
        catch (OperationCanceledException) when (run.CancelRequested)
        {
            await FinishLaunchAsync(run.OperationId, OperationState.Cancelled, null, null).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            // Nothing cancels a launch but a cancel request (no shutdown token reaches here), so this is a collaborator's
            // timeout (e.g. an HttpClient TaskCanceledException). Leaving the operation active would hold its reservation
            // until the agent restarts and block every later start on a unified node (W2 review MN-092).
            RuntimeLog.LaunchFailed(_logger, run.OperationId, run.ModelId, ErrorCodes.InternalError, ex.Message);
            await FinishLaunchAsync(run.OperationId, OperationState.Failed, new AgentError
            {
                Code = ErrorCodes.InternalError,
                Message = "The launch was interrupted: " + ex.Message,
            }, null).ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            RuntimeLog.LaunchFailed(_logger, run.OperationId, run.ModelId, ex.Code, ex.Message);
            await FinishLaunchAsync(run.OperationId, OperationState.Failed, ex.Error, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.LaunchFailed(_logger, run.OperationId, run.ModelId, ErrorCodes.InternalError, ex.Message);
            await FinishLaunchAsync(run.OperationId, OperationState.Failed, DriverError(ex), null).ConfigureAwait(false);
        }
        finally
        {
            if (locked)
            {
                modelLock.Release();
            }

            run.ReleaseBoardClaim();
            _executing.TryRemove(run.OperationId, out _);
            run.Completed.TrySetResult();
        }
    }

    // ---------------------------------------------------------------- stop

    /// <summary>Handles <c>POST /runtimes/{modelId}/stop</c>; 202 with the operation, or the refusal.</summary>
    public async Task<MutationResult> StopAsync(
        string modelId, StopRequest request, JsonElement payload, MasterTriple master, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(request);

        if (await _gate.CheckFencingAsync(master, ct).ConfigureAwait(false) is { } fenced)
        {
            return MutationResult.Refused(fenced);
        }

        await _acceptLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_gate.IsKnown(request.OperationId) && _gate.CheckEnvelope(request) is { } envelope)
            {
                return MutationResult.Refused(envelope);
            }

            var accepted = await _gate.AcceptAsync(request, OperationKinds.Stop, modelId, master, payload, ct).ConfigureAwait(false);
            if (accepted.Error is not null || accepted.Outcome != OperationAcceptOutcome.Accepted)
            {
                return FromGate(accepted);
            }

            string id = request.OperationId;
            _stopping.AddOrUpdate(modelId, 1, (_, n) => n + 1);
            Track(id, Task.Run(() => ExecuteStopAsync(id, modelId, master), CancellationToken.None));
            return MutationResult.Ok(StatusCodes.Status202Accepted, accepted.Status!);
        }
        finally
        {
            _acceptLock.Release();
        }
    }

    private async Task ExecuteStopAsync(string operationId, string modelId, MasterTriple master)
    {
        var modelLock = ModelLock(modelId);
        await modelLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var (driver, instance) = await FindAsync(modelId, CancellationToken.None).ConfigureAwait(false);
            if (driver is not null && instance is not null)
            {
                _board.Report(modelId, instance);
                await driver.StopAsync(modelId, new StopStageSink(this, operationId, modelId, master), CancellationToken.None).ConfigureAwait(false);
            }

            _board.Report(modelId, null);
            await TryCompleteAsync(operationId, OperationState.Succeeded, null, null).ConfigureAwait(false);

            // A launch still waiting for Ready on this model was ended by this stop.
            foreach (var (launchId, launchModel) in _awaitingReady.ToArray())
            {
                if (string.Equals(launchModel, modelId, StringComparison.Ordinal))
                {
                    await FinishLaunchAsync(launchId, OperationState.Cancelled, null, null).ConfigureAwait(false);
                }
            }
        }
        catch (AgentException ex)
        {
            RuntimeLog.StopFailed(_logger, operationId, modelId, ex.Code, ex.Message);
            await TryCompleteAsync(operationId, OperationState.Failed, ex.Error, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.StopFailed(_logger, operationId, modelId, ErrorCodes.InternalError, ex.Message);
            await TryCompleteAsync(operationId, OperationState.Failed, DriverError(ex), null).ConfigureAwait(false);
        }
        finally
        {
            modelLock.Release();
            _stopping.AddOrUpdate(modelId, 0, (_, n) => Math.Max(0, n - 1));
        }
    }

    // ---------------------------------------------------------------- cancel (design §4.4 item 7)

    /// <inheritdoc/>
    public AgentException? CheckCancellable(OperationStatus target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_executing.TryGetValue(target.Id, out var run))
        {
            return run.CreateCommitted ? InstanceExists(target.Id) : null;
        }

        if (_awaitingReady.ContainsKey(target.Id) || target.Stage is OperationStages.Created or OperationStages.Started or OperationStages.Ready)
        {
            return InstanceExists(target.Id);
        }

        return null;
    }

    /// <inheritdoc/>
    public async Task<OperationStatus> CancelAsync(OperationStatus target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_executing.TryGetValue(target.Id, out var run))
        {
            if (!run.TryRequestCancel())
            {
                throw InstanceExists(target.Id);
            }

            try
            {
                await run.Completed.Task.WaitAsync(_options.CancelWait, _time, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Still unwinding; the caller gets the current (active) status and observes the rest by events.
            }

            return Log.Find(target.Id) ?? target;
        }

        if (CheckCancellable(target) is { } refused)
        {
            throw refused;
        }

        // Accepted earlier (e.g. before an agent restart) and not executing here: nothing runs, so cancel it outright.
        await FinishLaunchAsync(target.Id, OperationState.Cancelled, null, null).ConfigureAwait(false);
        return Log.Find(target.Id) ?? target;
    }

    // ---------------------------------------------------------------- watcher hand-off

    /// <summary>Launches waiting for their instance to report Ready: operation id → model id.</summary>
    internal IReadOnlyList<KeyValuePair<string, string>> AwaitingReady => [.. _awaitingReady];

    /// <summary>True while the launch's synchronous or driver phase runs in this process.</summary>
    internal bool IsExecuting(string operationId) => _executing.ContainsKey(operationId);

    /// <summary>True while a stop of the model is accepted and not finished (its instance may vanish meanwhile).</summary>
    internal bool IsStopping(string modelId) => _stopping.TryGetValue(modelId, out int n) && n > 0;

    /// <summary>Adopts an active launch whose instance exists (stage <c>started</c>, e.g. after an agent restart).</summary>
    internal bool AdoptAwaiting(string operationId, string modelId) =>
        !_executing.ContainsKey(operationId) && _awaitingReady.TryAdd(operationId, modelId);

    /// <summary>
    /// Ends a launch once: records <c>ready</c> before a success, the terminal state with its error or result, and releases
    /// the reservation (publishing the release). A launch that is already terminal only has its reservation released.
    /// </summary>
    internal async Task FinishLaunchAsync(string operationId, OperationState state, AgentError? failure, RuntimeStatus? result)
    {
        if (!_finishing.TryAdd(operationId, 0))
        {
            return;
        }

        try
        {
            _awaitingReady.TryRemove(operationId, out _);
            if (Log.Find(operationId) is { } current && !IsTerminal(current.State))
            {
                try
                {
                    if (state == OperationState.Succeeded)
                    {
                        await _gate.RecordStageAsync(operationId, new OperationStageUpdate { Stage = OperationStages.Ready }, CancellationToken.None).ConfigureAwait(false);
                    }

                    await _gate.CompleteAsync(operationId, state, failure, result is null ? null : AgentJson.ToElement(result), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RuntimeLog.RecordFailed(_logger, operationId, ex.Message);
                }
            }

            await ReleaseReservationAsync(operationId).ConfigureAwait(false);
        }
        finally
        {
            _finishing.TryRemove(operationId, out _);
        }
    }

    /// <summary>
    /// Releases an operation's reservation, if any. The reservation manager publishes the release under the event-bus
    /// state lock (single publisher, W2 review MN-092), so a snapshot never disagrees with its watermark.
    /// </summary>
    internal async Task ReleaseReservationAsync(string operationId)
    {
        try
        {
            await _reservations.ReleaseAsync(operationId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RuntimeLog.ReleaseFailed(_logger, operationId, ex.Message);
        }
    }

    /// <summary>Waits for every background launch and stop started so far (tests, graceful shutdown).</summary>
    internal Task WhenIdleAsync() => Task.WhenAll(_background.Values);

    // ---------------------------------------------------------------- startup reconciliation (MN-025)

    /// <summary>
    /// Re-runs the driver phase of a launch accepted before an agent restart (design §4.4 item 5: admitted/pulling → resume
    /// from the pull), in the background exactly as a live launch: fencing before every destructive step, admission
    /// re-probed before create (memory may have changed while the agent was down), cancellable until create. False when the
    /// launch already runs in this process.
    /// </summary>
    internal bool ResumeLaunch(string operationId, string modelId, LaunchRequest request, MasterTriple master, IRuntimeDriver driver, ReservationInfo? reservation)
    {
        var run = new LaunchRun(operationId, modelId, request, master, driver) { Reservation = reservation, RecheckBeforeCreate = true };
        if (!_executing.TryAdd(operationId, run))
        {
            return false;
        }

        Track(operationId, Task.Run(() => ExecuteLaunchAsync(run), CancellationToken.None));
        return true;
    }

    /// <summary>
    /// Finishes a stop accepted before an agent restart (design §4.4 item 5: stop running → finish it) as a live stop does,
    /// including cancelling a launch of that model still waiting for Ready. Completes when the stop is terminal.
    /// </summary>
    internal Task ResumeStopAsync(string operationId, string modelId, MasterTriple master)
    {
        _stopping.AddOrUpdate(modelId, 1, (_, n) => n + 1);
        var task = Task.Run(() => ExecuteStopAsync(operationId, modelId, master), CancellationToken.None);
        Track(operationId, task);
        return task;
    }

    // ---------------------------------------------------------------- helpers

    internal static bool IsTerminal(OperationState state) =>
        state is OperationState.Succeeded or OperationState.Failed or OperationState.Cancelled or OperationState.Unknown;

    internal static AgentError InstanceFailed(RuntimeStatus instance) => new()
    {
        Code = ErrorCodes.InternalError,
        Message = "The instance of " + instance.ModelId + " failed before it was Ready"
            + (string.IsNullOrEmpty(instance.Error) ? "." : ": " + instance.Error),
    };

    private static AgentException InstanceExists(string operationId) =>
        AgentHttp.Failure(StatusCodes.Status409Conflict, ErrorCodes.InvalidState,
            "The instance of launch " + operationId + " already exists; stop the model instead of cancelling the launch.");

    private static AgentException StateUnavailable(Exception ex) =>
        AgentHttp.Failure(StatusCodes.Status503ServiceUnavailable, ErrorCodes.StateUnavailable,
            "The operation could not be recorded durably. " + ex.Message);

    internal static bool IsDriverUnavailable(Exception ex) =>
        ex is HttpRequestException or SocketException or TimeoutException;

    internal static AgentError DriverError(Exception ex) => IsDriverUnavailable(ex)
        ? new AgentError { Code = ErrorCodes.DockerUnavailable, Message = "The container runtime is unreachable: " + ex.Message }
        : new AgentError { Code = ErrorCodes.InternalError, Message = ex.Message };

    private static double BudgetOf(RuntimeStatus instance) =>
        instance.Labels.TryGetValue(ContainerLabels.BudgetGiB, out string? text)
        && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double budget)
            ? budget
            : 0;

    private static MutationResult FromGate(GateResult result) =>
        result.Error is not null
            ? MutationResult.Refused(result.Error)
            : MutationResult.Ok(StatusCodes.Status202Accepted, result.Status!);

    private async Task TryCompleteAsync(string operationId, OperationState state, AgentError? failure, JsonElement? result)
    {
        try
        {
            await _gate.CompleteAsync(operationId, state, failure, result, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RuntimeLog.RecordFailed(_logger, operationId, ex.Message);
        }
    }

    private SemaphoreSlim ModelLock(string modelId) => _modelLocks.GetOrAdd(modelId, _ => new SemaphoreSlim(1, 1));

    private void Track(string operationId, Task task)
    {
        _background[operationId] = task;
        _ = task.ContinueWith(_ => _background.TryRemove(operationId, out Task? _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- stage sinks

    /// <summary>A launch in flight: its request, reservation, cancellation and the stage facts admission needs.</summary>
    private sealed class LaunchRun(string operationId, string modelId, LaunchRequest request, MasterTriple master, IRuntimeDriver driver)
    {
        private readonly Lock _gate = new();
        private bool _createCommitted;
        private bool _cancelRequested;

        public string OperationId { get; } = operationId;

        public string ModelId { get; } = modelId;

        public LaunchRequest Request { get; } = request;

        public MasterTriple Master { get; } = master;

        public IRuntimeDriver Driver { get; } = driver;

        public ReservationInfo? Reservation { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset? PullStartedAt { get; set; }

        public DateTimeOffset? PullEndedAt { get; set; }

        public bool Removed { get; set; }

        /// <summary>Resumed after an agent restart: admission is re-probed before create whatever the pull took.</summary>
        public bool RecheckBeforeCreate { get; init; }

        /// <summary>The model's <see cref="RuntimeStateBoard.Claim"/>, held from create until the launch reports its instance.</summary>
        public IDisposable? BoardClaim { get; set; }

        public void ReleaseBoardClaim()
        {
            BoardClaim?.Dispose();
            BoardClaim = null;
        }

        public bool CreateCommitted
        {
            get
            {
                lock (_gate)
                {
                    return _createCommitted;
                }
            }
        }

        public bool CancelRequested
        {
            get
            {
                lock (_gate)
                {
                    return _cancelRequested;
                }
            }
        }

        /// <summary>Requests cancellation unless the create step was already committed.</summary>
        public bool TryRequestCancel()
        {
            lock (_gate)
            {
                if (_createCommitted)
                {
                    return false;
                }

                _cancelRequested = true;
            }

            Cancellation.Cancel();
            return true;
        }

        /// <summary>Commits to creating the instance; false when a cancel came first.</summary>
        public bool TryCommitCreate()
        {
            lock (_gate)
            {
                if (_cancelRequested)
                {
                    return false;
                }

                _createCommitted = true;
                return true;
            }
        }
    }

    private sealed class LaunchStageSink(RuntimeOperations owner, LaunchRun run) : IRuntimeStageSink
    {
        public async Task BeforeStepAsync(string stage, CancellationToken ct)
        {
            if (!owner._gate.IsCurrent(run.Master))
            {
                throw Superseded(run.OperationId);
            }

            ct.ThrowIfCancellationRequested();
            if (!string.Equals(stage, OperationStages.Created, StringComparison.Ordinal))
            {
                return;
            }

            var now = owner._time.GetUtcNow();
            bool slowPull = run.PullStartedAt is { } started && (run.PullEndedAt ?? now) - started > owner._options.SlowPullThreshold;
            if (run.Reservation is not null && (run.Removed || slowPull || run.RecheckBeforeCreate))
            {
                var recheck = await owner._reservations.RecheckAsync(run.OperationId, ct).ConfigureAwait(false);
                if (recheck.Outcome != ReservationOutcome.Reserved)
                {
                    throw new AgentException(StatusCodes.Status409Conflict, recheck.Error ?? new AgentError
                    {
                        Code = ErrorCodes.AdmissionRefused,
                        Message = "Admission was refused when re-checked before create.",
                    });
                }
            }

            if (!run.TryCommitCreate())
            {
                throw new OperationCanceledException(ct);
            }

            // From here the driver creates and starts the instance before the launch reports it: until that report the
            // watcher must not publish the model (it could already list the new instance Ready, see RuntimeStateBoard.Claim).
            run.BoardClaim ??= owner._board.Claim(run.ModelId);
        }

        public async Task StageReachedAsync(string stage, OperationProgress? progress, CancellationToken ct)
        {
            var now = owner._time.GetUtcNow();
            switch (stage)
            {
                case OperationStages.Pulling:
                    run.PullStartedAt ??= now;
                    break;
                case OperationStages.Pulled:
                    run.PullEndedAt = now;
                    break;
                case OperationStages.Removed:
                    run.Removed = true;
                    owner._board.Report(run.ModelId, null);
                    break;
            }

            await owner._gate.RecordStageAsync(run.OperationId, new OperationStageUpdate { Stage = stage, Progress = progress }, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private sealed class StopStageSink(RuntimeOperations owner, string operationId, string modelId, MasterTriple master) : IRuntimeStageSink
    {
        public Task BeforeStepAsync(string stage, CancellationToken ct)
        {
            if (!owner._gate.IsCurrent(master))
            {
                throw Superseded(operationId);
            }

            return Task.CompletedTask;
        }

        public async Task StageReachedAsync(string stage, OperationProgress? progress, CancellationToken ct)
        {
            if (string.Equals(stage, OperationStages.Removed, StringComparison.Ordinal))
            {
                owner._board.Report(modelId, null);
            }

            await owner._gate.RecordStageAsync(operationId, new OperationStageUpdate { Stage = stage, Progress = progress }, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static AgentException Superseded(string operationId) =>
        AgentHttp.Failure(StatusCodes.Status409Conflict, ErrorCodes.Superseded,
            "Operation " + operationId + " was submitted by a master that has since been superseded; it stopped before this step.");
}

/// <summary>What a runtime or operation mutation answers: a status with its HTTP code, or a refusal.</summary>
/// <param name="StatusCode">HTTP status (202 for launch/stop, 200 for cancel, or the error's).</param>
/// <param name="Status">The operation status when not refused.</param>
/// <param name="Error">The refusal.</param>
public sealed record MutationResult(int StatusCode, OperationStatus? Status, AgentException? Error)
{
    /// <summary>A status answer.</summary>
    public static MutationResult Ok(int statusCode, OperationStatus status) => new(statusCode, status, null);

    /// <summary>A refusal.</summary>
    public static MutationResult Refused(AgentException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(error.StatusCode, null, error);
    }

    /// <summary>The HTTP result.</summary>
    internal IResult ToHttp() => Error is not null ? AgentHttp.Error(Error) : AgentHttp.Json(Status, StatusCode);

    /// <summary>
    /// Runs a mutation handler and turns an <see cref="AgentException"/> from a collaborator into its error response and
    /// anything else into 500 <c>internal_error</c>, always in the agent error shape.
    /// </summary>
    internal static async Task<IResult> GuardAsync(Func<Task<MutationResult>> handler)
    {
        try
        {
            return (await handler().ConfigureAwait(false)).ToHttp();
        }
        catch (AgentException ex)
        {
            return AgentHttp.Error(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentHttp.Error(StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, ex.Message);
        }
    }
}

/// <summary>Source-generated log messages of the runtime operations and watcher.</summary>
internal static partial class RuntimeLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Launch {OperationId} of {ModelId} failed ({Code}): {Message}")]
    public static partial void LaunchFailed(ILogger logger, string operationId, string modelId, string code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stop {OperationId} of {ModelId} failed ({Code}): {Message}")]
    public static partial void StopFailed(ILogger logger, string operationId, string modelId, string code, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Operation {OperationId}: the state change could not be recorded: {Message}")]
    public static partial void RecordFailed(ILogger logger, string operationId, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reservation of {OperationId} could not be released: {Message}")]
    public static partial void ReleaseFailed(ILogger logger, string operationId, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Runtime watcher: listing the instances failed: {Message}")]
    public static partial void ListFailed(ILogger logger, string message);
}
