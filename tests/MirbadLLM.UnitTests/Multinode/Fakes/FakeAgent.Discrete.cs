using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// One simulated discrete GPU of a <see cref="FakeAgent"/> (P2, MN-104). Its index is its position in the fake's current
/// GPU order, so <see cref="FakeAgent.RenumberGpus"/> changes indices while the UUID stays the card's identity (design §5.1).
/// </summary>
/// <param name="Uuid">GPU UUID.</param>
/// <param name="Name">Product name as <c>nvidia-smi</c> reports it.</param>
/// <param name="TotalGiB">VRAM total.</param>
/// <param name="UsedGiB">VRAM used with no instance running (desktop, driver).</param>
/// <param name="ComputeCapability">CUDA compute capability.</param>
internal sealed record FakeDiscreteGpu(string Uuid, string Name = "NVIDIA GeForce RTX 5060 Ti", double TotalGiB = 16, double UsedGiB = 0.5, string ComputeCapability = "12.0")
{
    /// <summary>Two equal 16 GiB cards: the dev setup's <c>discrete:2x16</c> (design §13 item 5).</summary>
    public static IReadOnlyList<FakeDiscreteGpu> TwoBy16 { get; } =
    [
        new("GPU-fake-d000-0000-0000-000000000000"),
        new("GPU-fake-d001-0000-0000-000000000000"),
    ];
}

/// <summary>
/// Discrete mode of the fake (P2, MN-104): N GPUs with their own VRAM (<see cref="FakeAgentOptions.DiscreteGpus"/>), knobs
/// to renumber and remove cards, and the per-GPU views the capability report, <c>/status</c>, telemetry, <c>/gpu/idle</c>
/// and GPU selection use. Without <see cref="FakeAgentOptions.DiscreteGpus"/> every view is exactly the P1 unified one.
/// Features are not changed by discrete mode: P2 flags are reported only when <see cref="FakeAgentOptions.Features"/> lists
/// them. Admission is the fake's node-wide rule unless the fake also reports <see cref="AgentFeatures.AdmissionDiscrete"/>:
/// then it follows the real agent's discrete rules (MN-105, W8 review MN-128): one start per GPU, per-GPU VRAM with the
/// per-GPU reserves, the host part, and refusals in the agent's texts and details naming the GPU.
/// </summary>
internal sealed partial class FakeAgent
{
    private List<FakeDiscreteGpu>? _discreteGpus;

    /// <summary>True when the fake simulates a discrete node.</summary>
    public bool IsDiscrete => _options.DiscreteGpus is not null;

    /// <summary>The discrete GPUs in current index order. Throws on a unified fake.</summary>
    public IReadOnlyList<FakeDiscreteGpu> DiscreteGpus
    {
        get
        {
            lock (_gate)
            {
                return [.. DiscreteGpusLocked()];
            }
        }
    }

    /// <summary>
    /// Renumbers the GPUs (a driver update or a card moved to another slot): <paramref name="uuidsInIndexOrder"/> lists
    /// every current UUID once, in the new index order. Visible on the next read, as after an agent restart.
    /// </summary>
    public void RenumberGpus(params string[] uuidsInIndexOrder)
    {
        ArgumentNullException.ThrowIfNull(uuidsInIndexOrder);
        lock (_gate)
        {
            var gpus = DiscreteGpusLocked();
            var byUuid = gpus.ToDictionary(g => g.Uuid, StringComparer.Ordinal);
            if (uuidsInIndexOrder.Length != gpus.Count || uuidsInIndexOrder.Distinct(StringComparer.Ordinal).Count() != gpus.Count
                || uuidsInIndexOrder.Any(u => !byUuid.ContainsKey(u)))
            {
                throw new ArgumentException("List every current GPU UUID exactly once.", nameof(uuidsInIndexOrder));
            }

            _discreteGpus = [.. uuidsInIndexOrder.Select(u => byUuid[u])];
        }
    }

    /// <summary>Removes a card (failed or replaced); later GPUs move down one index. A launch naming it gets <c>gpu_missing</c>.</summary>
    public void RemoveGpu(string uuid)
    {
        lock (_gate)
        {
            var gpus = DiscreteGpusLocked();
            if (gpus.RemoveAll(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal)) == 0)
            {
                throw new ArgumentException($"GPU {uuid} is not on the fake.", nameof(uuid));
            }
        }
    }

    /// <summary>Adds a card at the next index (a replacement card has a new UUID).</summary>
    public void AddGpu(FakeDiscreteGpu gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        lock (_gate)
        {
            var gpus = DiscreteGpusLocked();
            if (gpus.Any(g => string.Equals(g.Uuid, gpu.Uuid, StringComparison.Ordinal)))
            {
                throw new ArgumentException($"GPU {gpu.Uuid} is already on the fake.", nameof(gpu));
            }

            gpus.Add(gpu);
        }
    }

    /// <summary>Sets a card's baseline VRAM use (another process on the GPU).</summary>
    public void SetGpuUsed(string uuid, double usedGiB)
    {
        lock (_gate)
        {
            var gpus = DiscreteGpusLocked();
            int at = gpus.FindIndex(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal));
            if (at < 0)
            {
                throw new ArgumentException($"GPU {uuid} is not on the fake.", nameof(uuid));
            }

            gpus[at] = gpus[at] with { UsedGiB = usedGiB };
        }
    }

    private List<FakeDiscreteGpu> DiscreteGpusLocked() =>
        _discreteGpus ??= _options.DiscreteGpus is { } configured
            ? [.. configured]
            : throw new InvalidOperationException("The fake is unified; set FakeAgentOptions.DiscreteGpus for discrete mode.");

    /// <summary>The memory model the fake reports. Caller holds the state lock.</summary>
    private MemoryModel MemoryModelLocked() => IsDiscrete ? MemoryModel.Discrete : MemoryModel.Unified;

    /// <summary>The capability report's GPUs (unified: P1's one GB10-like GPU). Caller holds the state lock.</summary>
    private IReadOnlyList<GpuCapability> CapabilityGpusLocked() => IsDiscrete
        ? [.. DiscreteGpusLocked().Select((g, i) => new GpuCapability
        {
            Index = i,
            Uuid = g.Uuid,
            Name = g.Name,
            MemoryTotalGiB = g.TotalGiB,
            ComputeCapability = g.ComputeCapability,
            Driver = "580.00",
        })]
        : [new GpuCapability { Index = 0, Uuid = _options.GpuUuid, Name = "Fake GB10", SharesHostMemory = true, ComputeCapability = "12.1", Driver = "580.00" }];

    /// <summary>
    /// Host memory available. Unified: the shared pool minus Ready budgets (P1). Discrete: model budgets live in VRAM, so
    /// the host pool is the configured available memory. Caller holds the state lock.
    /// </summary>
    private double HostAvailableGiBLocked() => IsDiscrete
        ? double.IsNaN(_baseAvailableGiB) ? _options.AvailableMemoryGiB : _baseAvailableGiB
        : AvailableGiBLocked();

    /// <summary><c>NodeMemorySnapshot.Gpus</c> (unified: P1's identity-only entry). Caller holds the state lock.</summary>
    private IReadOnlyList<GpuMemorySnapshot> MemoryGpusLocked() => IsDiscrete
        ? [.. DiscreteGpusLocked().Select((g, i) =>
        {
            double used = Math.Min(g.TotalGiB, g.UsedGiB + InstanceVramLocked(g.Uuid));
            return new GpuMemorySnapshot(i, g.Uuid, g.TotalGiB, used, Math.Max(0, g.TotalGiB - used));
        })]
        : [new GpuMemorySnapshot(0, _options.GpuUuid)];

    /// <summary>Per-GPU telemetry of one sample (unified: P1's one GPU without memory). Caller holds the state lock.</summary>
    private IReadOnlyList<GpuSample> SampleGpusLocked(double utilization) => IsDiscrete
        ? [.. DiscreteGpusLocked().Select((g, i) =>
        {
            double used = Math.Min(g.TotalGiB, g.UsedGiB + InstanceVramLocked(g.Uuid));
            double busy = InstanceVramLocked(g.Uuid) > 0 ? utilization : 0;
            return new GpuSample
            {
                Index = i,
                Uuid = g.Uuid,
                Name = g.Name,
                UtilizationPercent = busy,
                PowerWatts = 15 + busy,
                TemperatureC = 40,
                SmClockMHz = 1500,
                MemoryUsedGiB = used,
                MemoryTotalGiB = g.TotalGiB,
                MemoryFreeGiB = Math.Max(0, g.TotalGiB - used),
            };
        })]
        : [new GpuSample { Index = 0, Uuid = _options.GpuUuid, UtilizationPercent = utilization, PowerWatts = 20 + utilization, TemperatureC = 45, SmClockMHz = 1500 }];

    /// <summary>
    /// <c>/gpu/idle</c> per-GPU rows for <paramref name="indices"/> (null or empty = all); throws <c>invalid_request</c> for an
    /// unknown index. Caller holds the state lock.
    /// </summary>
    private IReadOnlyList<GpuIdleSample> IdleGpusLocked(IReadOnlyList<int>? indices, double utilization, int samples)
    {
        var gpus = IsDiscrete
            ? DiscreteGpusLocked().Select((g, i) => (Index: i, g.Uuid, Busy: InstanceVramLocked(g.Uuid) > 0 ? utilization : 0)).ToList()
            : [(0, _options.GpuUuid, utilization)];
        if (indices is { Count: 0 })
        {
            indices = null; // an empty list means all, as before discrete mode
        }

        if (indices is not null && indices.Any(i => gpus.All(g => g.Index != i)))
        {
            throw Fail(ErrorCodes.InvalidRequest, "Unknown GPU index.");
        }

        return [.. gpus.Where(g => indices is null || indices.Contains(g.Index))
            .Select(g => new GpuIdleSample(g.Index, g.Uuid, g.Busy, samples) { MaxPercent = g.Busy })];
    }

    /// <summary>
    /// GPU selection of a launch: <c>all</c> → none recorded; <c>uuids</c> → every UUID must be on the node (else 422
    /// <c>gpu_missing</c> listing the missing ones), mapped to its current index. Caller holds the state lock.
    /// </summary>
    private IReadOnlyList<GpuRef> ResolveGpusLocked(GpuSelection selection)
    {
        if (selection.Mode != GpuSelectionMode.Uuids)
        {
            return [];
        }

        var uuids = selection.Uuids ?? [];
        var present = CurrentGpuRefsLocked();
        var missing = uuids.Where(u => present.All(p => !string.Equals(p.Uuid, u, StringComparison.Ordinal))).ToList();
        if (missing.Count > 0 || uuids.Count == 0)
        {
            throw Fail(ErrorCodes.GpuMissing, $"GPU(s) {string.Join(", ", missing)} are not on node {NodeId}.", new GpuMissingDetails(missing));
        }

        return [.. uuids.Select(u => present.First(p => string.Equals(p.Uuid, u, StringComparison.Ordinal)))];
    }

    /// <summary>A reconciled launch's GPUs after a restart: the requested UUIDs still present, at their current index. Caller holds the lock.</summary>
    private IReadOnlyList<GpuRef> ReconciledGpusLocked(GpuSelection selection)
    {
        if (selection.Mode != GpuSelectionMode.Uuids)
        {
            return [];
        }

        if (!IsDiscrete)
        {
            return [new GpuRef(0, _options.GpuUuid)];
        }

        var present = CurrentGpuRefsLocked();
        return [.. (selection.Uuids ?? []).SelectMany(u => present.Where(p => string.Equals(p.Uuid, u, StringComparison.Ordinal)))];
    }

    private List<GpuRef> CurrentGpuRefsLocked() => IsDiscrete
        ? [.. DiscreteGpusLocked().Select((g, i) => new GpuRef(i, g.Uuid))]
        : [new GpuRef(0, _options.GpuUuid)];

    /// <summary>True when the fake admits per GPU (discrete and reporting <see cref="AgentFeatures.AdmissionDiscrete"/>, as the real agent).</summary>
    private bool PerGpuAdmissionLocked => IsDiscrete && _options.Features.Contains(AgentFeatures.AdmissionDiscrete, StringComparer.Ordinal);

    /// <summary>
    /// The real agent's discrete start rule and admission (<c>ReservationManager</c>, MN-105): a start conflicts only with a
    /// start holding one of its GPUs (an <c>all</c> launch holds every GPU); then per-GPU and host admission. Caller holds
    /// the lock.
    /// </summary>
    private ReservationInfo ReserveDiscreteLocked(string operationId, string modelId, AdmissionRequest admission, double releasedGiB, IReadOnlyList<GpuRef> gpus)
    {
        string[] slots = gpus.Count > 0 ? [.. gpus.Select(g => g.Uuid)] : [.. DiscreteGpusLocked().Select(g => g.Uuid)];
        if (_reservations.Values.FirstOrDefault(r => r.OperationId != operationId && r.Gpus.Intersect(slots, StringComparer.Ordinal).Any()) is { } other)
        {
            string[] shared = [.. other.Gpus.Intersect(slots, StringComparer.Ordinal)];
            throw Fail(ErrorCodes.StartConflict,
                MirbadLLM.Agent.Admission.AdmissionTexts.OnNode(
                    MirbadLLM.Agent.Admission.AdmissionTexts.StartConflict(other.ModelId, modelId), NodeId,
                    MirbadLLM.Agent.Admission.AdmissionTexts.GpuPart(shared, IndexMapLocked())),
                new StartConflictDetails { StartingModelId = other.ModelId, Gpus = shared });
        }

        double hostBudget = MirbadLLM.Agent.Admission.DiscreteAdmission.EffectiveHostBudgetGiB(admission.BudgetGiB, admission.HostBudgetGiB, slots.Length);
        CheckDiscreteAdmissionLocked(modelId, admission, releasedGiB, operationId, slots, hostBudget);
        return new ReservationInfo
        {
            OperationId = operationId,
            ModelId = modelId,
            Gpus = slots,
            BudgetGiB = admission.BudgetGiB,
            HostBudgetGiB = hostBudget,
            HostPort = AllocatePortLocked(),
            CreatedAt = Now,
        };
    }

    /// <summary>
    /// Per GPU: <c>budget / n &lt;= free + released / n - ReserveForGpu - shares of other reservations on it</c>; then the host
    /// part <c>hostBudget &lt;= hostAvailable - reserve - host budgets of other reservations</c>. Forced starts skip it.
    /// </summary>
    private void CheckDiscreteAdmissionLocked(
        string modelId, AdmissionRequest admission, double releasedGiB, string operationId, IReadOnlyList<string> slots, double hostBudgetGiB)
    {
        if (admission.Force)
        {
            return;
        }

        var others = _reservations.Values.Where(r => r.OperationId != operationId).ToList();
        var indices = IndexMapLocked();
        double share = admission.BudgetGiB / Math.Max(1, slots.Count);
        double releasedShare = releasedGiB / Math.Max(1, slots.Count);
        foreach (string uuid in slots)
        {
            var gpu = DiscreteGpusLocked().First(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal));
            double available = Math.Max(0, gpu.TotalGiB - Math.Min(gpu.TotalGiB, gpu.UsedGiB + InstanceVramLocked(uuid)));
            double reserve = admission.ReserveForGpu(uuid);
            double held = others.Where(r => r.Gpus.Contains(uuid, StringComparer.Ordinal)).Sum(r => r.BudgetGiB / Math.Max(1, r.Gpus.Count));
            double free = available + releasedShare - reserve - held;
            if (share > free)
            {
                throw Fail(ErrorCodes.AdmissionRefused,
                    MirbadLLM.Agent.Admission.AdmissionTexts.OnNode(
                        MirbadLLM.Agent.Admission.AdmissionTexts.MemoryRefused(modelId, share, free, available, reserve, releasedShare, held), NodeId,
                        MirbadLLM.Agent.Admission.AdmissionTexts.GpuPart([uuid], indices)),
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

        double hostAvailable = HostAvailableGiBLocked();
        double hostHeld = others.Sum(r => r.HostBudgetGiB ?? 0);
        double hostFree = hostAvailable - admission.ReserveGiB - hostHeld;
        if (hostBudgetGiB > hostFree)
        {
            throw Fail(ErrorCodes.AdmissionRefused,
                MirbadLLM.Agent.Admission.AdmissionTexts.OnNode(
                    MirbadLLM.Agent.Admission.AdmissionTexts.MemoryRefused(modelId, hostBudgetGiB, hostFree, hostAvailable, admission.ReserveGiB, 0, hostHeld), NodeId,
                    MirbadLLM.Agent.Admission.AdmissionTexts.HostPart),
                new AdmissionRefusedDetails
                {
                    NeedGiB = hostBudgetGiB,
                    FreeGiB = hostFree,
                    AvailableGiB = hostAvailable,
                    ReserveGiB = admission.ReserveGiB,
                    ReservedByOthersGiB = hostHeld,
                    Host = true,
                });
        }
    }

    private Dictionary<string, int> IndexMapLocked() =>
        DiscreteGpusLocked().Select((g, i) => (g.Uuid, Index: i)).ToDictionary(x => x.Uuid, x => x.Index, StringComparer.Ordinal);

    /// <summary>VRAM taken on <paramref name="uuid"/> by Ready instances: each takes budget ÷ n on each of its n GPUs (§5.1).</summary>
    private double InstanceVramLocked(string uuid) =>
        _instances.Values
            .Where(i => i.State == ModelState.Ready && i.Gpus.Count > 0 && i.Gpus.Any(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal)))
            .Sum(i => i.BudgetGiB / i.Gpus.Count);
}
