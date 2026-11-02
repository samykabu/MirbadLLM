using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Admission;

/// <summary>
/// Start reservations, memory admission and the node-wide port lock (design §4.8, §6.1; <see cref="IReservationManager"/>).
/// <para>Locking: one asynchronous admission lock serializes <see cref="TryReserveAsync"/>, <see cref="RecheckAsync"/>,
/// <see cref="AllocatePortAsync"/> and <see cref="RebuildAsync(IEnumerable{ReservationInfo}, CancellationToken)"/> — start
/// rule, instance scan, memory probe, decision, port choice and recording are one atomic step. The reservation set itself
/// is an immutable map swapped under a short monitor (also taken by <see cref="ReleaseAsync"/>, which therefore never
/// waits for a probe), and every change is published as a <see cref="ReservationEvent"/> inside
/// <see cref="IEventBus.Publish(Action, AgentEvent)"/>, so a status snapshot sees the reservations exactly as of its
/// watermark. <see cref="List"/> takes no lock and is safe inside <see cref="IEventBus.Snapshot{T}"/>.</para>
/// <para>Start rule: the slot holders are the reservations plus instances that are Starting without a reservation
/// (adopted or crash-restarting instances: today's <c>EnsureNoOtherModelStartingAsync</c>, which ignores the model itself
/// and tuning candidates). Unified: at most <see cref="AdmissionOptions.MaxConcurrentStarts"/> (default 1) holders; a
/// second operation for a model that already holds a reservation also conflicts. Discrete (P1 shape): the requested GPUs
/// (or the pseudo-GPU <c>host</c>) must not intersect any holder's, plus the optional node-wide cap.</para>
/// <para>Memory: free = probe available + released − reserve − budgets of all other reservations; <c>force</c> skips only
/// this. On a discrete node without a node memory reader (the P1 shape) the same check runs on the host pool with
/// <see cref="AdmissionRequest.HostBudgetGiB"/> (else the budget).</para>
/// <para>Discrete admission (P2, MN-105; <see cref="PerGpuAdmission"/>): a discrete node built with an
/// <see cref="INodeMemoryReader"/> reads host and per-GPU memory once per decision under the lock and checks, for every
/// placed GPU, <c>share ≤ gpuFree + released share − ReserveForGpu(uuid) − Σ shares of other reservations on that
/// GPU</c>, then the host part <c>hostBudget ≤ hostAvailable − ReserveGiB − Σ host budgets of other reservations</c>.
/// The share is <see cref="AdmissionRequest.BudgetForGpu"/>: the GPU's entry of <see cref="AdmissionRequest.GpuBudgetsGiB"/>
/// when the launch sends per-GPU budgets (a llama.cpp model on unequal GPUs; MN-224, design-p3p4 §A.1.5, reported as
/// <see cref="AgentFeatures.AdmissionGpuBudgets"/> by <see cref="GpuBudgetsContributor"/>), else <c>budget ÷ n</c> (P2);
/// another reservation's share comes from the launch it was admitted with (a rebuild without one: the even split).
/// A null <see cref="AdmissionRequest.HostBudgetGiB"/> is the design §6.1 estimate
/// (<see cref="DiscreteAdmission.HostEstimateGiB"/>; a CPU-only start's whole budget is host memory), and the reservation
/// records the host budget it was admitted with, so later decisions and a rebuild count the same figure. Refusals name
/// the GPU by its current index ("… on node pc (GPU 1)"; details carry the UUID) or the host part. The start rule is
/// unchanged: one start per GPU, so starts on disjoint GPUs are held at the same time while each decision (and with it
/// the host part) is serialized by the node-wide lock. Unified nodes never use the reader: P1 exactly.</para>
/// <para>Ports: lowest free port from <see cref="AdmissionOptions.PortRangeStart"/>, skipping ports of reservations,
/// leases and instances of other models. Ports of the model's own instance are reusable because a launch that creates an
/// instance removes the old one first, as the pre-P1 <c>DockerModelRuntime</c> allocated after removal.</para>
/// </summary>
public sealed partial class ReservationManager : IReservationManager, IDisposable
{
    private readonly AdmissionOptions _options;
    private readonly IMemoryProbe _memory;
    private readonly IRuntimeInstanceSource? _instances;
    private readonly IReservationPersistence? _persistence;
    private readonly IEventBus? _events;
    private readonly INodeMemoryReader? _nodeMemory;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _admission = new(1, 1);
    private readonly Lock _gate = new();
    private volatile State _state = State.Empty;

    /// <summary>Creates the manager. Optional dependencies may be null (no instances, no persistence, no events).</summary>
    /// <param name="options">Node settings.</param>
    /// <param name="memory">Host memory probe (unified admission, and the discrete P1 shape).</param>
    /// <param name="instances">Runtime instances seen under the lock.</param>
    /// <param name="persistence">Reservation persistence.</param>
    /// <param name="events">Event bus (the reservation events' only publisher).</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="nodeMemory">
    /// Host and per-GPU memory for discrete admission (P2, MN-105): with <see cref="AdmissionOptions.MemoryModel"/> =
    /// discrete it turns on <see cref="PerGpuAdmission"/>; ignored on unified nodes. Use
    /// <see cref="DiscreteAdmission.CreateMemoryReader"/> (a fresh GPU read per decision).
    /// </param>
    public ReservationManager(
        AdmissionOptions options,
        IMemoryProbe memory,
        IRuntimeInstanceSource? instances = null,
        IReservationPersistence? persistence = null,
        IEventBus? events = null,
        TimeProvider? time = null,
        ILogger<ReservationManager>? logger = null,
        INodeMemoryReader? nodeMemory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.NodeId, nameof(options));
        if (options.PortRangeStart is < 1 or > 65535 || options.PortRangeEnd < options.PortRangeStart || options.PortRangeEnd > 65535)
        {
            throw new ArgumentException("The port range must lie within 1–65535 and not be empty.", nameof(options));
        }

        if (options.MaxConcurrentStarts is <= 0)
        {
            throw new ArgumentException("MaxConcurrentStarts must be positive when set.", nameof(options));
        }

        _options = options;
        _memory = memory;
        _instances = instances;
        _persistence = persistence;
        _events = events;
        _nodeMemory = options.MemoryModel == MemoryModel.Discrete ? nodeMemory : null;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// True on a discrete node built with a node memory reader: per-GPU and host admission (P2, MN-105). The
    /// <see cref="AgentFeatures.AdmissionDiscrete"/> flag is reported only then (<see cref="DiscreteAdmissionContributor"/>).
    /// </summary>
    public bool PerGpuAdmission => _nodeMemory is not null;

    /// <inheritdoc/>
    public IReadOnlyList<ReservationInfo> List() =>
        [.. _state.Entries.Values.Select(e => e.Info).OrderBy(r => r.CreatedAt).ThenBy(r => r.OperationId, StringComparer.Ordinal)];

    /// <inheritdoc/>
    public async Task<ReservationResult> TryReserveAsync(StartReservationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId, nameof(request));
        ArgumentNullException.ThrowIfNull(request.Admission, nameof(request));
        ValidateGiB(request.Admission.BudgetGiB, nameof(request));
        ValidateGiB(request.Admission.ReserveGiB, nameof(request));
        ValidateGiB(request.ReleasedGiB, nameof(request));
        if (request.Admission.HostBudgetGiB is { } hostBudget)
        {
            ValidateGiB(hostBudget, nameof(request));
        }

        if (PerGpuAdmission)
        {
            ValidateReserves(request.Admission, nameof(request));
            ValidateGpuBudgets(request.Admission, request.GpuUuids, nameof(request));
        }

        // The node's reserves travel with every launch (design §6.1); a process node learns and persists them here, so a
        // refusal teaches them too (MN-232, D129 (2)). Null on every P1/P2 node.
        _options.OnAdmission?.Invoke(request.Admission);

        await _admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = _state;
            if (state.Entries.TryGetValue(request.OperationId, out var existing))
            {
                // A repeated call for the same operation (handler retry): the reservation it already holds.
                return Reserved(existing.Info);
            }

            string[] slots = RequestedSlots(request.GpuUuids);
            var instances = _instances is null ? [] : await _instances.ListAsync(ct).ConfigureAwait(false);

            if (CheckStartRule(request.ModelId, slots, state, instances) is { } conflict)
            {
                LogStartConflict(_logger, request.OperationId, request.ModelId, conflict.StartingModelId);
                return PerGpuAdmission && conflict.Gpus is not null
                    ? await NameConflictGpusAsync(conflict, request.ModelId, ct).ConfigureAwait(false)
                    : conflict.Result;
            }

            double? heldHostBudget = PerGpuAdmission
                ? DiscreteAdmission.EffectiveHostBudgetGiB(request.Admission.BudgetGiB, request.Admission.HostBudgetGiB, GpuCount(slots))
                : _options.MemoryModel == MemoryModel.Discrete ? request.Admission.HostBudgetGiB : null;

            if (!request.Admission.Force)
            {
                ReservationResult? refusal;
                if (PerGpuAdmission)
                {
                    var snapshot = await _nodeMemory!.ReadAsync(ct).ConfigureAwait(false);
                    refusal = DecideDiscrete(request.OperationId, request.ModelId, slots, request.Admission.BudgetGiB, heldHostBudget!.Value, request.Admission, request.ReleasedGiB, snapshot, state);
                }
                else
                {
                    var memory = await _memory.GetAsync(ct).ConfigureAwait(false);
                    refusal = Decide(request.ModelId, NeedOf(request.Admission), request.Admission.ReserveGiB, request.ReleasedGiB, memory, ReservedByOthers(state, request.OperationId));
                }

                if (refusal is not null)
                {
                    LogAdmissionRefused(_logger, request.OperationId, request.ModelId, refusal.Error!.Message);
                    return refusal;
                }
            }

            int port = state.Leases.TryGetValue(request.OperationId, out var lease)
                ? lease.Port
                : FreePort(request.ModelId, state, instances);

            var info = new ReservationInfo
            {
                OperationId = request.OperationId,
                ModelId = request.ModelId,
                Gpus = _options.MemoryModel == MemoryModel.Discrete ? slots : [],
                BudgetGiB = request.Admission.BudgetGiB,
                HostBudgetGiB = heldHostBudget,
                HostPort = port,
                CreatedAt = _time.GetUtcNow(),
            };

            if (_persistence is not null)
            {
                await _persistence.SaveAsync(info, ct).ConfigureAwait(false);
            }

            Commit(s => s with
            {
                Entries = s.Entries.SetItem(info.OperationId, new Entry(info, request.Admission)),
                Leases = s.Leases.Remove(info.OperationId),
            }, info, released: false);
            LogReserved(_logger, info.OperationId, info.ModelId, info.BudgetGiB, port);
            return Reserved(info);
        }
        finally
        {
            _admission.Release();
        }
    }

    /// <inheritdoc/>
    /// <exception cref="KeyNotFoundException">No reservation is held for <paramref name="operationId"/>.</exception>
    public async Task<ReservationResult> RecheckAsync(string operationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await _admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = _state;
            if (!state.Entries.TryGetValue(operationId, out var entry))
            {
                throw new KeyNotFoundException($"No start reservation is held for operation '{operationId}'.");
            }

            var admission = entry.Admission ?? new AdmissionRequest
            {
                BudgetGiB = entry.Info.BudgetGiB,
                HostBudgetGiB = entry.Info.HostBudgetGiB,
                ReserveGiB = _options.DefaultReserveGiB,
            };
            if (admission.Force)
            {
                return Reserved(entry.Info);
            }

            // Fresh probe: memory released by a removed instance is whatever the probe now shows (design §4.8).
            ReservationResult? refusal;
            if (PerGpuAdmission)
            {
                var snapshot = await _nodeMemory!.ReadAsync(ct).ConfigureAwait(false);
                refusal = DecideDiscrete(operationId, entry.Info.ModelId, [.. SlotsOf(entry.Info.Gpus)], entry.Info.BudgetGiB, HostBudgetOf(entry.Info), admission, releasedGiB: 0, snapshot, state);
            }
            else
            {
                var memory = await _memory.GetAsync(ct).ConfigureAwait(false);
                refusal = Decide(entry.Info.ModelId, NeedOf(admission), admission.ReserveGiB, releasedGiB: 0, memory, ReservedByOthers(state, operationId));
            }

            if (refusal is not null)
            {
                // The reservation stays held; the caller fails the operation and releases it.
                LogAdmissionRefused(_logger, operationId, entry.Info.ModelId, refusal.Error!.Message);
                return refusal with { Reservation = entry.Info };
            }

            return Reserved(entry.Info);
        }
        finally
        {
            _admission.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<int> AllocatePortAsync(string modelId, string operationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        await _admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = _state;
            if (state.Entries.TryGetValue(operationId, out var entry) && entry.Info.HostPort is { } reserved)
            {
                return reserved;
            }

            if (state.Leases.TryGetValue(operationId, out var lease))
            {
                return lease.Port;
            }

            var instances = _instances is null ? [] : await _instances.ListAsync(ct).ConfigureAwait(false);
            int port = FreePort(entry?.Info.ModelId ?? modelId, state, instances);

            if (entry is not null)
            {
                // A reservation rebuilt without a port: give it one, persisted like a new reservation.
                var info = entry.Info with { HostPort = port };
                if (_persistence is not null)
                {
                    await _persistence.SaveAsync(info, ct).ConfigureAwait(false);
                }

                Commit(s => s.Entries.ContainsKey(operationId)
                    ? s with { Entries = s.Entries.SetItem(operationId, entry with { Info = info }) }
                    : s, info, released: false);
            }
            else
            {
                // No reservation (e.g. a reconciliation start): a lease keeps the port taken until ReleaseAsync or a
                // reservation for the operation picks it up.
                lock (_gate)
                {
                    _state = _state with { Leases = _state.Leases.SetItem(operationId, new Lease(modelId, port)) };
                }
            }

            return port;
        }
        finally
        {
            _admission.Release();
        }
    }

    /// <inheritdoc/>
    public Task ReleaseAsync(string operationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        lock (_gate)
        {
            var state = _state;
            if (state.Entries.TryGetValue(operationId, out var entry))
            {
                var next = state with { Entries = state.Entries.Remove(operationId), Leases = state.Leases.Remove(operationId) };
                PublishLocked(next, entry.Info, released: true);
                LogReleased(_logger, operationId, entry.Info.ModelId);
            }
            else if (state.Leases.ContainsKey(operationId))
            {
                _state = state with { Leases = state.Leases.Remove(operationId) };
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RebuildAsync(IEnumerable<ReservationInfo> reservations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        return RebuildAsync(reservations.Select(r => new RebuiltReservation(r)), ct);
    }

    /// <summary>
    /// Rebuilds the reservations after an agent restart (design §4.4 item 5, §4.8), keeping the admission inputs for later
    /// re-checks. Replaces every held reservation and lease; meant for reconciliation before the listener opens. Records
    /// are facts: they are taken as given even if they break the start rule (new starts then wait for their release).
    /// Repeated operation ids keep the last record.
    /// </summary>
    public async Task RebuildAsync(IEnumerable<RebuiltReservation> reservations, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reservations);
        var byOperation = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var rebuilt in reservations)
        {
            ArgumentNullException.ThrowIfNull(rebuilt);
            ArgumentNullException.ThrowIfNull(rebuilt.Reservation);
            byOperation[rebuilt.Reservation.OperationId] = new Entry(rebuilt.Reservation, rebuilt.Admission);
        }

        await _admission.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                foreach (var old in _state.Entries.Values)
                {
                    if (!byOperation.ContainsKey(old.Info.OperationId))
                    {
                        PublishLocked(_state with { Entries = _state.Entries.Remove(old.Info.OperationId) }, old.Info, released: true);
                    }
                }

                _state = _state with { Leases = _state.Leases.Clear() };
                foreach (var entry in byOperation.Values.OrderBy(e => e.Info.CreatedAt).ThenBy(e => e.Info.OperationId, StringComparer.Ordinal))
                {
                    PublishLocked(_state with { Entries = _state.Entries.SetItem(entry.Info.OperationId, entry) }, entry.Info, released: false);
                }
            }

            LogRebuilt(_logger, byOperation.Count);
        }
        finally
        {
            _admission.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _admission.Dispose();

    private static void ValidateGiB(double value, string paramName)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            throw new ArgumentException("Memory figures must be finite, non-negative numbers.", paramName);
        }
    }

    private static void ValidateReserves(AdmissionRequest admission, string paramName)
    {
        if (admission.GpuReserveGiB is { } gpuReserve)
        {
            ValidateGiB(gpuReserve, paramName);
        }

        if (admission.GpuReservesGiB is { } reserves)
        {
            foreach (var reserve in reserves)
            {
                ValidateGiB(reserve.Value, paramName);
            }
        }
    }

    /// <summary>
    /// Per-GPU budgets (MN-224): finite and non-negative, and one for every GPU of the start. A map that misses a
    /// placed GPU is refused instead of letting that GPU fall back to an even split of the whole budget: the start
    /// would then be admitted for more than its budget on that GPU (MN-230, D130).
    /// </summary>
    private static void ValidateGpuBudgets(AdmissionRequest admission, IReadOnlyList<string>? gpuUuids, string paramName)
    {
        if (admission.GpuBudgetsGiB is not { } budgets)
        {
            return;
        }

        foreach (var budget in budgets)
        {
            ValidateGiB(budget.Value, paramName);
        }

        foreach (string gpu in gpuUuids ?? [])
        {
            if (!budgets.ContainsKey(gpu))
            {
                throw new ArgumentException(
                    $"Per-GPU budgets were given without one for GPU {gpu}; every GPU of the start needs its own budget.", paramName);
            }
        }
    }

    /// <summary>Real GPUs among the slots (the pseudo-GPU <c>host</c> is none).</summary>
    private static int GpuCount(IReadOnlyList<string> slots) =>
        slots.Count(s => !string.Equals(s, ReservationInfo.HostPseudoGpu, StringComparison.Ordinal));

    /// <summary>
    /// The host budget a reservation holds: what it was admitted with, else (a record from the P1 shape or a rebuild
    /// without it) the design §6.1 estimate for its GPUs.
    /// </summary>
    private static double HostBudgetOf(ReservationInfo info) =>
        DiscreteAdmission.EffectiveHostBudgetGiB(info.BudgetGiB, info.HostBudgetGiB, GpuCount(info.Gpus));

    /// <summary>
    /// Per-GPU, then host admission (design §6.1 discrete). <paramref name="slots"/> are the start's GPUs or the pseudo-GPU
    /// <c>host</c>. The memory a replaced instance releases counts on the start's GPUs, split evenly (a CPU-only start: on
    /// the host); the re-check after the removal passes 0 and relies on the probe (design §4.8).
    /// </summary>
    private ReservationResult? DecideDiscrete(
        string operationId, string modelId, IReadOnlyList<string> slots, double budgetGiB, double hostBudgetGiB,
        AdmissionRequest admission, double releasedGiB, NodeMemorySnapshot snapshot, State state)
    {
        string[] gpus = [.. slots.Where(s => !string.Equals(s, ReservationInfo.HostPseudoGpu, StringComparison.Ordinal))];
        Entry[] otherEntries =
        [
            .. state.Entries.Values.Where(e => !string.Equals(e.Info.OperationId, operationId, StringComparison.Ordinal)),
        ];
        ReservationInfo[] others = [.. otherEntries.Select(e => e.Info)];

        if (gpus.Length > 0)
        {
            var indices = IndexMap(snapshot);
            foreach (string uuid in gpus)
            {
                // Each GPU against its own budget (MN-224, design-p3p4 §A.1.5): the launch's gpuBudgetsGiB entry, else
                // budget ÷ n (P2). The memory a replaced instance releases follows the same split.
                double share = DiscreteAdmission.GpuShareGiB(admission, budgetGiB, uuid, gpus.Length);
                double releasedShare = DiscreteAdmission.ReleasedShareGiB(admission, budgetGiB, releasedGiB, uuid, gpus.Length);
                var gpu = snapshot.Gpus.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal));
                double reserve = admission.ReserveForGpu(uuid);
                double held = otherEntries.Sum(o => DiscreteAdmission.HeldOnGpuGiB(o.Info, o.Admission, uuid));
                if (gpu?.FreeGiB is not { } available)
                {
                    // Not listed by the probe, or listed without free memory (failed or partial read): refuse rather than
                    // guess. A GPU that is gone is refused earlier with gpu_missing (MN-106, IRuntimeDriver.CheckSpec).
                    return Refused(
                        AdmissionTexts.GpuMemoryUnknown(modelId, share),
                        AdmissionTexts.GpuPart([uuid], indices),
                        new AdmissionRefusedDetails
                        {
                            NeedGiB = share,
                            FreeGiB = 0,
                            AvailableGiB = 0,
                            ReserveGiB = reserve,
                            ReservedByOthersGiB = held,
                            Gpu = uuid,
                        });
                }

                double free = available + releasedShare - reserve - held;
                if (share > free)
                {
                    return Refused(
                        AdmissionTexts.MemoryRefused(modelId, share, free, available, reserve, releasedShare, held),
                        AdmissionTexts.GpuPart([uuid], indices),
                        new AdmissionRefusedDetails
                        {
                            NeedGiB = share,
                            FreeGiB = free,
                            AvailableGiB = available,
                            ReserveGiB = reserve,
                            ReleasedGiB = releasedShare,
                            ReservedByOthersGiB = held,
                            Gpu = uuid,
                        });
                }
            }
        }

        double hostReleased = gpus.Length == 0 ? releasedGiB : 0;
        double hostHeld = others.Sum(HostBudgetOf);
        double hostFree = snapshot.HostAvailableGiB + hostReleased - admission.ReserveGiB - hostHeld;
        if (hostBudgetGiB > hostFree)
        {
            return Refused(
                AdmissionTexts.MemoryRefused(modelId, hostBudgetGiB, hostFree, snapshot.HostAvailableGiB, admission.ReserveGiB, hostReleased, hostHeld),
                AdmissionTexts.HostPart,
                new AdmissionRefusedDetails
                {
                    NeedGiB = hostBudgetGiB,
                    FreeGiB = hostFree,
                    AvailableGiB = snapshot.HostAvailableGiB,
                    ReserveGiB = admission.ReserveGiB,
                    ReleasedGiB = hostReleased,
                    ReservedByOthersGiB = hostHeld,
                    Host = true,
                });
        }

        return null;
    }

    private ReservationResult Refused(string text, string part, AdmissionRefusedDetails details) => new()
    {
        Outcome = ReservationOutcome.AdmissionRefused,
        Error = new AgentError
        {
            Code = ErrorCodes.AdmissionRefused,
            Message = AdmissionTexts.OnNode(text, _options.NodeId, part),
            Details = JsonSerializer.SerializeToElement(details, AgentJson.Options),
        },
    };

    private static Dictionary<string, int> IndexMap(NodeMemorySnapshot snapshot)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var gpu in snapshot.Gpus)
        {
            map.TryAdd(gpu.Uuid, gpu.Index);
        }

        return map;
    }

    /// <summary>
    /// A discrete start conflict with the GPUs named by their current index (details keep the UUIDs). A failing read keeps
    /// the UUIDs in the text; it never turns a conflict into another error.
    /// </summary>
    private async Task<ReservationResult> NameConflictGpusAsync(Conflict conflict, string modelId, CancellationToken ct)
    {
        Dictionary<string, int> indices;
        try
        {
            indices = IndexMap(await _nodeMemory!.ReadAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            indices = [];
        }

        var error = conflict.Result.Error! with
        {
            Message = AdmissionTexts.OnNode(
                AdmissionTexts.StartConflict(conflict.StartingModelId, modelId, _options.ProcessDelivery),
                _options.NodeId,
                AdmissionTexts.GpuPart(conflict.Gpus!, indices)),
        };
        return conflict.Result with { Error = error };
    }

    private string[] RequestedSlots(IReadOnlyList<string>? gpuUuids)
    {
        if (_options.MemoryModel == MemoryModel.Unified)
        {
            return [];
        }

        string[] gpus = gpuUuids is null ? [] : [.. gpuUuids.Where(g => !string.IsNullOrWhiteSpace(g)).Distinct(StringComparer.Ordinal)];
        return gpus.Length == 0 ? [ReservationInfo.HostPseudoGpu] : gpus;
    }

    private static IReadOnlyList<string> SlotsOf(IReadOnlyList<string> gpus) => gpus.Count == 0 ? [ReservationInfo.HostPseudoGpu] : gpus;

    private double NeedOf(AdmissionRequest admission) =>
        _options.MemoryModel == MemoryModel.Discrete ? admission.HostBudgetGiB ?? admission.BudgetGiB : admission.BudgetGiB;

    private double ReservedByOthers(State state, string operationId) =>
        state.Entries.Values
            .Where(e => !string.Equals(e.Info.OperationId, operationId, StringComparison.Ordinal))
            .Sum(e => _options.MemoryModel == MemoryModel.Discrete ? e.Info.HostBudgetGiB ?? e.Info.BudgetGiB : e.Info.BudgetGiB);

    private Conflict? CheckStartRule(string modelId, string[] slots, State state, IReadOnlyList<RuntimeStatus> instances)
    {
        // Holders: every reservation, plus Starting instances of other models without one (today's rule).
        var holders = new List<(string ModelId, IReadOnlyList<string> Slots)>();
        var reservedModels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in state.Entries.Values.OrderBy(e => e.Info.CreatedAt).ThenBy(e => e.Info.OperationId, StringComparer.Ordinal))
        {
            holders.Add((entry.Info.ModelId, SlotsOf(entry.Info.Gpus)));
            reservedModels.Add(entry.Info.ModelId);
        }

        foreach (var instance in instances)
        {
            if (instance.State == ModelState.Starting && !instance.Tuning &&
                !string.Equals(instance.ModelId, modelId, StringComparison.Ordinal) && reservedModels.Add(instance.ModelId))
            {
                holders.Add((instance.ModelId, SlotsOf([.. instance.Gpus.Select(g => g.Uuid)])));
            }
        }

        if (holders.Count == 0)
        {
            return null;
        }

        if (_options.MemoryModel == MemoryModel.Unified)
        {
            var sameModel = holders.FirstOrDefault(h => string.Equals(h.ModelId, modelId, StringComparison.Ordinal));
            if (sameModel.ModelId is not null)
            {
                return StartConflict(sameModel.ModelId, modelId, gpus: null);
            }

            return holders.Count >= (_options.MaxConcurrentStarts ?? 1) ? StartConflict(holders[0].ModelId, modelId, gpus: null) : null;
        }

        foreach (var holder in holders)
        {
            string[] overlap = [.. holder.Slots.Intersect(slots, StringComparer.Ordinal)];
            if (overlap.Length > 0 || string.Equals(holder.ModelId, modelId, StringComparison.Ordinal))
            {
                return StartConflict(holder.ModelId, modelId, overlap.Length > 0 ? overlap : [.. slots]);
            }
        }

        return _options.MaxConcurrentStarts is { } max && holders.Count >= max ? StartConflict(holders[0].ModelId, modelId, gpus: null) : null;
    }

    private Conflict StartConflict(string startingModelId, string modelId, string[]? gpus)
    {
        string? part = gpus is null ? null : gpus.Length == 1 ? $"GPU {gpus[0]}" : $"GPUs {string.Join(", ", gpus)}";
        var error = new AgentError
        {
            Code = ErrorCodes.StartConflict,
            Message = AdmissionTexts.OnNode(
                AdmissionTexts.StartConflict(startingModelId, modelId, _options.ProcessDelivery), _options.NodeId, part),
            Details = JsonSerializer.SerializeToElement(new StartConflictDetails { StartingModelId = startingModelId, Gpus = gpus }, AgentJson.Options),
        };
        return new Conflict(startingModelId, gpus, new ReservationResult { Outcome = ReservationOutcome.StartConflict, Error = error });
    }

    private ReservationResult? Decide(string modelId, double needGiB, double reserveGiB, double releasedGiB, MemorySnapshot memory, double reservedByOthersGiB)
    {
        double free = memory.AvailableGiB + releasedGiB - reserveGiB - reservedByOthersGiB;
        if (needGiB <= free)
        {
            return null;
        }

        bool discrete = _options.MemoryModel == MemoryModel.Discrete;
        var details = new AdmissionRefusedDetails
        {
            NeedGiB = needGiB,
            FreeGiB = free,
            AvailableGiB = memory.AvailableGiB,
            ReserveGiB = reserveGiB,
            ReleasedGiB = releasedGiB,
            ReservedByOthersGiB = reservedByOthersGiB,
            Host = discrete ? true : null,
        };
        var error = new AgentError
        {
            Code = ErrorCodes.AdmissionRefused,
            Message = AdmissionTexts.OnNode(
                AdmissionTexts.MemoryRefused(modelId, needGiB, free, memory.AvailableGiB, reserveGiB, releasedGiB, reservedByOthersGiB),
                _options.NodeId,
                discrete ? "host memory" : null),
            Details = JsonSerializer.SerializeToElement(details, AgentJson.Options),
        };
        return new ReservationResult { Outcome = ReservationOutcome.AdmissionRefused, Error = error };
    }

    private int FreePort(string modelId, State state, IReadOnlyList<RuntimeStatus> instances)
    {
        var used = new HashSet<int>();
        foreach (var entry in state.Entries.Values)
        {
            if (entry.Info.HostPort is { } port)
            {
                used.Add(port);
            }
        }

        foreach (var lease in state.Leases.Values)
        {
            used.Add(lease.Port);
        }

        foreach (var instance in instances)
        {
            if (instance.HostPort is { } port && !string.Equals(instance.ModelId, modelId, StringComparison.Ordinal))
            {
                used.Add(port);
            }
        }

        for (int candidate = _options.PortRangeStart; candidate <= _options.PortRangeEnd; candidate++)
        {
            if (!used.Contains(candidate) && _options.PortInUse?.Invoke(candidate) != true)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No free host port between {_options.PortRangeStart} and {_options.PortRangeEnd} on node {_options.NodeId}.");
    }

    private void Commit(Func<State, State> change, ReservationInfo info, bool released)
    {
        lock (_gate)
        {
            PublishLocked(change(_state), info, released);
        }
    }

    // Caller holds _gate. The state swap runs inside the event bus's state lock so snapshots and the event stream agree.
    private void PublishLocked(State next, ReservationInfo info, bool released)
    {
        if (_events is null)
        {
            _state = next;
            return;
        }

        _events.Publish(() => _state = next, new ReservationEvent { Reservation = info, Released = released });
    }

    private static ReservationResult Reserved(ReservationInfo info) => new() { Outcome = ReservationOutcome.Reserved, Reservation = info };

    private sealed record Entry(ReservationInfo Info, AdmissionRequest? Admission);

    private sealed record Lease(string ModelId, int Port);

    private sealed record Conflict(string StartingModelId, string[]? Gpus, ReservationResult Result);

    private sealed record State(ImmutableDictionary<string, Entry> Entries, ImmutableDictionary<string, Lease> Leases)
    {
        public static State Empty { get; } = new(
            ImmutableDictionary.Create<string, Entry>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, Lease>(StringComparer.Ordinal));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Start reservation {OperationId} for {ModelId}: {BudgetGiB} GiB, port {Port}")]
    private static partial void LogReserved(ILogger logger, string operationId, string modelId, double budgetGiB, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "Start reservation {OperationId} for {ModelId} released")]
    private static partial void LogReleased(ILogger logger, string operationId, string modelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Start {OperationId} for {ModelId} refused: {StartingModelId} is still starting")]
    private static partial void LogStartConflict(ILogger logger, string operationId, string modelId, string startingModelId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Start {OperationId} for {ModelId} refused by memory admission: {Reason}")]
    private static partial void LogAdmissionRefused(ILogger logger, string operationId, string modelId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Rebuilt {Count} start reservations")]
    private static partial void LogRebuilt(ILogger logger, int count);
}
