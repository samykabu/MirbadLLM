using System.Runtime.CompilerServices;
using System.Text.Json;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-224 (design-p3p4 §A.1.5, D116, D129 (4)): per-GPU budgets in discrete admission. A launch that sends
/// <see cref="AdmissionRequest.GpuBudgetsGiB"/> (a llama.cpp model on the PC's 12 + 16 GB cards) is admitted on each GPU
/// against its own budget (<see cref="AdmissionRequest.BudgetForGpu"/>); refusals name the GPU and carry its own need;
/// held reservations count their per-GPU budgets (also after a rebuild from the operation log); the released memory of a
/// replaced instance follows the same split; without budgets everything is P2's even split, and a unified node (the DGX)
/// never looks at them. <see cref="GpuBudgetsContributor"/> reports <c>admission.gpu-budgets</c> only where it applies.
/// </summary>
public sealed class GpuBudgetAdmissionTests
{
    private const string Node = "win-1";

    /// <summary>RTX 5070, 12 GB, index 0 (the fixture node's GPUs).</summary>
    private const string Small = "GPU-0a4f2d61-8b3c-4e7a-b1d9-2c5e8f0a3b17";

    /// <summary>RTX 5060 Ti, 16 GB, index 1.</summary>
    private const string Large = "GPU-7c1e9a52-3f60-4d2b-9a8e-5b0f2c6d1e44";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------------------------
    // Each GPU against its own budget
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnequalGpus_AreAdmittedPerGpu_AgainstTheirOwnBudgets_WhereTheEvenSplitIsRefused()
    {
        // 8 GiB free on the 12 GB card, 13 GiB on the 16 GB card with its 2.5 GiB reserve: 7.5 + 10.5 fits exactly.
        using var withBudgets = Create(Memory(smallFree: 8, largeFree: 13));
        var admitted = await withBudgets.TryReserveAsync(Request("op-1", Budgets(7.5, 10.5)), Ct);

        Assert.Equal(ReservationOutcome.Reserved, admitted.Outcome);
        Assert.Equal(new[] { Small, Large }, admitted.Reservation!.Gpus);
        Assert.Equal(18, admitted.Reservation.BudgetGiB);

        // The same launch without budgets (P2, or a node without the flag) asks 9 GiB of the 12 GB card: refused there.
        using var even = Create(Memory(smallFree: 8, largeFree: 13));
        var refused = await even.TryReserveAsync(Request("op-1", Even()), Ct);

        Assert.Equal(ReservationOutcome.AdmissionRefused, refused.Outcome);
        Assert.Equal(
            "Model 'coder-14b-split' needs 9.0 GiB but only 8.0 GiB is free (8.0 GiB available minus a 0.0 GiB reserve) on node win-1 (GPU 0).",
            refused.Error!.Message);
        Assert.Equal(9, refused.Error.ReadDetails<AdmissionRefusedDetails>()!.NeedGiB);
    }

    [Fact]
    public async Task ARefusal_NamesTheGpu_WithItsOwnBudget()
    {
        // The 16 GB card has 12 GiB free − 2.5 reserve = 9.5 < its 10.5 budget; the 12 GB card (7.5 ≤ 8) passes.
        using var manager = Create(Memory(smallFree: 8, largeFree: 12));

        var refused = await manager.TryReserveAsync(Request("op-1", Budgets(7.5, 10.5)), Ct);

        Assert.Equal(ReservationOutcome.AdmissionRefused, refused.Outcome);
        Assert.Equal(ErrorCodes.AdmissionRefused, refused.Error!.Code);
        Assert.Equal(
            "Model 'coder-14b-split' needs 10.5 GiB but only 9.5 GiB is free (12.0 GiB available minus a 2.5 GiB reserve) on node win-1 (GPU 1).",
            refused.Error.Message);
        var details = refused.Error.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.Equal(Large, details.Gpu);
        Assert.Equal(10.5, details.NeedGiB);
        Assert.Equal(9.5, details.FreeGiB);
        Assert.Equal(2.5, details.ReserveGiB);
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task TheFrozenWireFixture_IsAdmittedPerGpu()
    {
        // AdmissionRequest.gpu-budgets.json (MN-202): 7.5 on the 12 GB card (reserve 2.5 there), 10.5 on the 16 GB card
        // (default GPU reserve 1). 10 GiB free on the small card: 7.5 ≤ 10 − 2.5; 11.5 on the large: 10.5 ≤ 11.5 − 1.
        var admission = JsonSerializer.Deserialize<AdmissionRequest>(
            File.ReadAllText(Path.Combine(P3Fixtures(), "AdmissionRequest.gpu-budgets.json")), AgentJson.Options)!;
        using var manager = Create(Memory(smallFree: 10, largeFree: 11.5));

        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("op-1", admission), Ct)).Outcome);

        // 0.1 GiB less on the large card: refused there, by its own budget.
        using var tight = Create(Memory(smallFree: 10, largeFree: 11.4));
        var refused = await tight.TryReserveAsync(Request("op-1", admission), Ct);
        Assert.Equal(ReservationOutcome.AdmissionRefused, refused.Outcome);
        Assert.EndsWith(" on node win-1 (GPU 1).", refused.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(10.5, refused.Error.ReadDetails<AdmissionRefusedDetails>()!.NeedGiB);
    }

    [Fact]
    public async Task TheRecheckAfterRemoval_UsesTheHeldLaunchsBudgets()
    {
        var memory = Memory(smallFree: 8, largeFree: 13);
        using var manager = Create(memory);
        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("op-1", Budgets(7.5, 10.5)), Ct)).Outcome);

        Assert.Equal(ReservationOutcome.Reserved, (await manager.RecheckAsync("op-1", Ct)).Outcome);

        // The probe now shows 7 GiB on the small card: its own 7.5 does not fit (refused there); at 7.5 free it does (the
        // even split's 9 would not).
        memory.SetFree(Small, 7);
        var refused = await manager.RecheckAsync("op-1", Ct);
        Assert.Equal(ReservationOutcome.AdmissionRefused, refused.Outcome);
        Assert.Equal(7.5, refused.Error!.ReadDetails<AdmissionRefusedDetails>()!.NeedGiB);
        Assert.EndsWith(" on node win-1 (GPU 0).", refused.Error.Message, StringComparison.Ordinal);

        memory.SetFree(Small, 7.5);
        Assert.Equal(ReservationOutcome.Reserved, (await manager.RecheckAsync("op-1", Ct)).Outcome);
    }

    [Fact]
    public async Task TheReleasedMemory_FollowsTheBudgetSplit()
    {
        // A replace of the same model releases 18 GiB: 7.5 of it on the small card and 10.5 on the large one. The large
        // card shows 3.5 GiB free (−2.5 reserve) + 10.5 released = 11.5 ≥ 10.5; split evenly it would be 1 + 9 = 10 < 10.5.
        using var manager = Create(Memory(smallFree: 1, largeFree: 3.5));

        var replaced = await manager.TryReserveAsync(Request("op-1", Budgets(7.5, 10.5)) with { ReleasedGiB = 18 }, Ct);

        Assert.Equal(ReservationOutcome.Reserved, replaced.Outcome);
        Assert.Equal(7.5, DiscreteAdmission.ReleasedShareGiB(Budgets(7.5, 10.5), 18, 18, Small, 2), 9);
        Assert.Equal(10.5, DiscreteAdmission.ReleasedShareGiB(Budgets(7.5, 10.5), 18, 18, Large, 2), 9);
        Assert.Equal(9, DiscreteAdmission.ReleasedShareGiB(Even(), 18, 18, Large, 2));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Held reservations count their own per-GPU budgets
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHeldReservationRebuiltWithItsLaunch_CountsItsPerGpuBudget_OnEachGpu()
    {
        // Records are facts after a restart: a split model holding 2 + 10 GiB and a small model on the large card. The
        // small model's re-check sees 10 GiB held on the large card (not the even 6): 12 − 10 = 2 < 3, refused.
        var at = DateTimeOffset.UnixEpoch;
        using var manager = Create(Memory(smallFree: 12, largeFree: 12));
        await manager.RebuildAsync(
        [
            new RebuiltReservation(Info("split", "coder-14b-split", 12, [Small, Large], at), Budgets(2, 10) with { BudgetGiB = 12, GpuReservesGiB = null }),
            new RebuiltReservation(Info("small", "embedder", 3, [Large], at.AddSeconds(1)), Admission(3, budgets: null) with { GpuReservesGiB = null }),
        ], Ct);

        var refused = await manager.RecheckAsync("small", Ct);

        Assert.Equal(ReservationOutcome.AdmissionRefused, refused.Outcome);
        var details = refused.Error!.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.Equal(10, details.ReservedByOthersGiB);
        Assert.Equal(Large, details.Gpu);
        Assert.Contains("10.0 GiB held by other starts", refused.Error.Message, StringComparison.Ordinal);

        Assert.Equal(10, DiscreteAdmission.HeldOnGpuGiB(Info("split", "m", 12, [Small, Large], at), Budgets(2, 10) with { BudgetGiB = 12 }, Large));
        Assert.Equal(2, DiscreteAdmission.HeldOnGpuGiB(Info("split", "m", 12, [Small, Large], at), Budgets(2, 10) with { BudgetGiB = 12 }, Small));
        Assert.Equal(0, DiscreteAdmission.HeldOnGpuGiB(Info("small", "m", 3, [Large], at), null, Small));
    }

    [Fact]
    public async Task AHeldReservationRebuiltWithoutItsLaunch_CountsTheEvenSplit()
    {
        // No admission inputs (a record without its launch): the split model counts 12 ÷ 2 = 6 on the large card, so the
        // small model's 3 GiB fit in 12 − 6.
        var at = DateTimeOffset.UnixEpoch;
        using var manager = Create(Memory(smallFree: 12, largeFree: 12));
        await manager.RebuildAsync(
        [
            new RebuiltReservation(Info("split", "coder-14b-split", 12, [Small, Large], at)),
            new RebuiltReservation(Info("small", "embedder", 3, [Large], at.AddSeconds(1)), Admission(3, budgets: null) with { GpuReservesGiB = null }),
        ], Ct);

        Assert.Equal(ReservationOutcome.Reserved, (await manager.RecheckAsync("small", Ct)).Outcome);
        Assert.Equal(6, DiscreteAdmission.HeldOnGpuGiB(Info("split", "m", 12, [Small, Large], at), null, Large));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // P2 and the DGX unchanged
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutBudgets_TheDecisionIsP2sEvenSplit_ToTheLastDigit()
    {
        // budget ÷ n exactly: the share of a 10 GiB start on three GPUs is 10 / 3, as in P2.
        var admission = Admission(10, budgets: null);
        Assert.Equal(10d / 3, DiscreteAdmission.GpuShareGiB(admission, 10, Small, 3));
        Assert.Equal(admission.BudgetForGpu(Small, 3), DiscreteAdmission.GpuShareGiB(admission, 10, Small, 3));
        Assert.Equal(10d / 3, DiscreteAdmission.ReleasedShareGiB(admission, 10, 10, Small, 3));

        // A budget map that does not name a placed GPU leaves that GPU on the even split (BudgetForGpu's contract).
        var partial = Admission(10, budgets: new Dictionary<string, double> { [Large] = 8 });
        Assert.Equal(5, DiscreteAdmission.GpuShareGiB(partial, 10, Small, 2));
        Assert.Equal(8, DiscreteAdmission.GpuShareGiB(partial, 10, Large, 2));

        // The refusal text and details of an even-split start are P2's.
        using var manager = Create(Memory(smallFree: 4, largeFree: 16));
        var refused = await manager.TryReserveAsync(Request("op-1", Admission(10, budgets: null)), Ct);
        Assert.Equal(
            "Model 'coder-14b-split' needs 5.0 GiB but only 4.0 GiB is free (4.0 GiB available minus a 0.0 GiB reserve) on node win-1 (GPU 0).",
            refused.Error!.Message);
        var details = refused.Error.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.Equal((5d, 4d, 0d), (details.NeedGiB, details.FreeGiB, details.ReleasedGiB));
    }

    [Fact]
    public async Task AUnifiedNode_NeverLooksAtPerGpuBudgets()
    {
        // The DGX: one pool, the whole budget; the same request with and without budgets decides and answers the same.
        using var dgx = new ReservationManager(
            new AdmissionOptions { NodeId = "dgx", MemoryModel = MemoryModel.Unified }, new FixedHost(128, 20), nodeMemory: Memory(1, 1));
        var with = await dgx.TryReserveAsync(Request("a", Budgets(7.5, 10.5) with { ReserveGiB = 8 }), Ct);
        await dgx.ReleaseAsync("a", Ct);
        var without = await dgx.TryReserveAsync(Request("a", Even() with { ReserveGiB = 8 }), Ct);

        Assert.Equal(ReservationOutcome.AdmissionRefused, with.Outcome);
        Assert.Equal(without.Error!.Message, with.Error!.Message);
        Assert.Equal("Model 'coder-14b-split' needs 18.0 GiB but only 12.0 GiB is free (20.0 GiB available minus a 8.0 GiB reserve) on node dgx.", with.Error.Message);
        Assert.Equal(without.Error.Details!.Value.GetRawText(), with.Error.Details!.Value.GetRawText());
        Assert.False(dgx.PerGpuAdmission);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(double.PositiveInfinity)]
    public async Task ABudgetThatIsNotAFiniteNonNegativeNumber_IsRejected(double bad)
    {
        using var manager = Create(Memory(smallFree: 16, largeFree: 16));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.TryReserveAsync(Request("op-1", Admission(18, new Dictionary<string, double> { [Small] = bad, [Large] = 10.5 })), Ct));
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task BudgetsThatMissAPlacedGpu_AreRejected_NamingThatGpu()
    {
        // MN-230: the missing GPU fell back to an even split of the whole budget (9 of 18 GiB), so the start was
        // admitted for 7.5 + 9 = 16.5 GiB against a 18 GiB budget it had already split differently — and held
        // reservations counted the same wrong share. Refuse the map instead.
        using var manager = Create(Memory(smallFree: 16, largeFree: 16));

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.TryReserveAsync(Request("op-1", Admission(18, new Dictionary<string, double> { [Small] = 7.5 })), Ct));

        Assert.Contains(Large, error.Message, StringComparison.Ordinal);
        Assert.Empty(manager.List());

        // A map for every placed GPU is reserved as before.
        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("op-2", Budgets(7.5, 10.5)), Ct)).Outcome);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // admission.gpu-budgets
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Contributor_ReportsTheFlag_OnlyOnADiscreteNodeWithPerGpuAdmission_Once()
    {
        using var perGpu = Create(Memory(smallFree: 16, largeFree: 16));
        using var p1Shape = new ReservationManager(new AdmissionOptions { NodeId = Node, MemoryModel = MemoryModel.Discrete }, new FixedHost(64, 64));
        using var unified = new ReservationManager(new AdmissionOptions { NodeId = "dgx" }, new FixedHost(64, 64), nodeMemory: Memory(1, 1));

        var discrete = Report(MemoryModel.Discrete, "telemetry.v1", AgentFeatures.AdmissionDiscrete);
        var reported = await new GpuBudgetsContributor(perGpu).ContributeAsync(discrete, Ct);
        Assert.Equal(new[] { "telemetry.v1", AgentFeatures.AdmissionDiscrete, AgentFeatures.AdmissionGpuBudgets }, reported.Features);
        Assert.Same(reported, await new GpuBudgetsContributor(perGpu).ContributeAsync(reported, Ct));

        Assert.Same(discrete, await new GpuBudgetsContributor(p1Shape).ContributeAsync(discrete, Ct));
        Assert.Same(discrete, await new GpuBudgetsContributor(unified).ContributeAsync(discrete, Ct));

        // A report that says unified (the DGX, or a failed GPU read): no flag, so its report stays P1 exactly.
        var dgx = Report(MemoryModel.Unified, "telemetry.v1");
        Assert.Same(dgx, await new GpuBudgetsContributor(perGpu).ContributeAsync(dgx, Ct));
        Assert.Throws<ArgumentNullException>(() => new GpuBudgetsContributor(null!));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers and fakes
    // ---------------------------------------------------------------------------------------------------------------

    private static ReservationManager Create(FakeNodeMemory memory) =>
        new(new AdmissionOptions { NodeId = Node, MemoryModel = MemoryModel.Discrete, PortRangeStart = 8101, PortRangeEnd = 8999 },
            new FixedHost(0, 0), nodeMemory: memory);

    /// <summary>The PC's two cards with free VRAM (the large card's 2.5 GiB reserve rides in the requests).</summary>
    private static FakeNodeMemory Memory(double smallFree, double largeFree) =>
        new() { HostAvailable = 120, Gpus = { [Small] = (0, smallFree), [Large] = (1, largeFree) } };

    /// <summary>The split model's admission: 18 GiB, host 4, the large card's 2.5 GiB reserve, and the given per-GPU budgets.</summary>
    private static AdmissionRequest Budgets(double small, double large) =>
        Admission(18, new Dictionary<string, double> { [Small] = small, [Large] = large });

    /// <summary>The same admission without per-GPU budgets (P2, or a node without the flag).</summary>
    private static AdmissionRequest Even() => Admission(18, budgets: null);

    private static AdmissionRequest Admission(double budget, IReadOnlyDictionary<string, double>? budgets) => new()
    {
        BudgetGiB = budget,
        HostBudgetGiB = 4,
        ReserveGiB = 0,
        GpuReservesGiB = new Dictionary<string, double> { [Large] = 2.5 },
        GpuBudgetsGiB = budgets,
    };

    private static StartReservationRequest Request(string op, AdmissionRequest admission) => new()
    {
        OperationId = op,
        ModelId = "coder-14b-split",
        Admission = admission,
        GpuUuids = [Small, Large],
    };

    private static ReservationInfo Info(string op, string model, double budget, string[] gpus, DateTimeOffset at) => new()
    {
        OperationId = op,
        ModelId = model,
        Gpus = gpus,
        BudgetGiB = budget,
        HostBudgetGiB = 1,
        CreatedAt = at,
    };

    private static NodeCapabilities Report(MemoryModel model, params string[] features) => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "1.3.0",
        Features = features,
        Os = new NodeOsInfo("windows", "Windows 11"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(model, 127),
    };

    private static string P3Fixtures([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "Contracts", "Fixtures", "P3");

    /// <summary>Host and per-GPU free memory as the reservation manager reads it (16 GiB cards).</summary>
    private sealed class FakeNodeMemory : INodeMemoryReader
    {
        private readonly Lock _lock = new();

        public double HostAvailable { get; init; }

        public Dictionary<string, (int Index, double Free)> Gpus { get; } = new(StringComparer.Ordinal);

        public void SetFree(string uuid, double free)
        {
            lock (_lock)
            {
                Gpus[uuid] = (Gpus[uuid].Index, free);
            }
        }

        public Task<NodeMemorySnapshot> ReadAsync(CancellationToken ct = default)
        {
            lock (_lock)
            {
                return Task.FromResult(new NodeMemorySnapshot
                {
                    Model = MemoryModel.Discrete,
                    HostTotalGiB = 127,
                    HostAvailableGiB = HostAvailable,
                    Gpus = [.. Gpus.OrderBy(g => g.Value.Index).Select(g => new GpuMemorySnapshot(g.Value.Index, g.Key, 16, 16 - g.Value.Free, g.Value.Free))],
                });
            }
        }
    }

    private sealed class FixedHost(double total, double available) : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new MemorySnapshot(total, available));
    }
}
