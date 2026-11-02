using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;

#pragma warning disable CA1707, CA1861 // underscore test names; inline expected arrays.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-105: per-GPU and host reservations on discrete nodes (design §4.8, §6.1): one start per GPU, the host part
/// serialized node-wide, per-GPU reserves, host-memory estimates, refusals naming the GPU, the <c>admission.discrete</c>
/// contributor, and unified nodes unchanged.
/// </summary>
public sealed class DiscreteReservationTests
{
    private const string Node = "pc";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Uuid(int i) => $"GPU-00000000-0000-0000-0000-{i:D12}";

    // ---------------------------------------------------------------------------------------------------------------
    // Start rule: one start per GPU, concurrent across GPUs
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task StartsOnDifferentGpus_RunConcurrently_AndAreAllHeld()
    {
        var memory = Gpus(8, free: 16).WithHost(available: 512);
        memory.Delay = TimeSpan.FromMilliseconds(2);
        using var manager = Create(memory);

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            start.Wait(Ct);
            return await manager.TryReserveAsync(Request($"op-{i}", $"m-{i}", budget: 10, gpus: [Uuid(i)], hostBudget: 4), Ct);
        }, Ct)).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(ReservationOutcome.Reserved, r.Outcome));
        var held = manager.List();
        Assert.Equal(8, held.Count);
        Assert.Equal(Enumerable.Range(0, 8).Select(Uuid).Order(StringComparer.Ordinal), held.SelectMany(r => r.Gpus).Order(StringComparer.Ordinal));
        Assert.Equal(8, held.Select(r => r.HostPort).Distinct().Count());

        // Each decision (with its host part) ran alone under the node-wide lock: never two memory reads at once.
        Assert.Equal(8, memory.Reads);
        Assert.Equal(1, memory.MaxConcurrentReads);
    }

    [Fact]
    public async Task SameGpu_IsSerialized_OneHolder_RestStartConflictNamingTheGpuByIndex()
    {
        var memory = Gpus(2, free: 16).WithHost(available: 512);
        using var manager = Create(memory);

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            start.Wait(Ct);
            return await manager.TryReserveAsync(Request($"op-{i}", $"m-{i}", budget: 4, gpus: [Uuid(1)], hostBudget: 1), Ct);
        }, Ct)).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);

        var winner = Assert.Single(results, r => r.Outcome == ReservationOutcome.Reserved);
        var losers = results.Where(r => r.Outcome != ReservationOutcome.Reserved).ToArray();
        Assert.Equal(19, losers.Length);
        foreach (var loser in losers)
        {
            Assert.Equal(ReservationOutcome.StartConflict, loser.Outcome);
            Assert.Equal(ErrorCodes.StartConflict, loser.Error!.Code);
            var details = loser.Error.ReadDetails<StartConflictDetails>()!;
            Assert.Equal(winner.Reservation!.ModelId, details.StartingModelId);
            Assert.Equal(new[] { Uuid(1) }, details.Gpus);
            Assert.Equal(
                AdmissionTexts.StartConflict(winner.Reservation.ModelId, "x")[..^1].Replace("'x'", "'" + ModelOf(loser) + "'", StringComparison.Ordinal) + " on node pc (GPU 1).",
                loser.Error.Message);
        }

        // The other GPU is free meanwhile, and the slot comes back once the holder is released.
        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("other", "other", budget: 4, gpus: [Uuid(0)], hostBudget: 1), Ct)).Outcome);
        await manager.ReleaseAsync(winner.Reservation!.OperationId, Ct);
        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("next", "next", budget: 4, gpus: [Uuid(1)], hostBudget: 1), Ct)).Outcome);
    }

    [Fact]
    public async Task ManyStartsOverThreeGpus_ExactlyOneHolderPerGpu_AndOverlappingSetsConflict()
    {
        var memory = Gpus(3, free: 24).WithHost(available: 1024);
        using var manager = Create(memory);

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
        {
            start.Wait(Ct);
            return await manager.TryReserveAsync(Request($"op-{i}", $"m-{i}", budget: 2, gpus: [Uuid(i % 3)], hostBudget: 1), Ct);
        }, Ct)).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(3, results.Count(r => r.Outcome == ReservationOutcome.Reserved));
        Assert.Equal(27, results.Count(r => r.Outcome == ReservationOutcome.StartConflict));
        Assert.Equal(3, manager.List().SelectMany(r => r.Gpus).Distinct().Count());

        // A two-GPU start overlapping one holder names only the GPU in conflict.
        var holderOfGpu2 = manager.List().Single(r => r.Gpus.Contains(Uuid(2)));
        await manager.ReleaseAsync(manager.List().Single(r => r.Gpus.Contains(Uuid(1))).OperationId, Ct);
        var overlap = await manager.TryReserveAsync(Request("tp2", "tp2", budget: 4, gpus: [Uuid(1), Uuid(2)], hostBudget: 1), Ct);
        Assert.Equal(ReservationOutcome.StartConflict, overlap.Outcome);
        Assert.Equal(new[] { Uuid(2) }, overlap.Error!.ReadDetails<StartConflictDetails>()!.Gpus);
        Assert.Equal(holderOfGpu2.ModelId, overlap.Error.ReadDetails<StartConflictDetails>()!.StartingModelId);
        Assert.EndsWith(" on node pc (GPU 2).", overlap.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConflictWhileTheGpuReadFails_KeepsTheUuidInTheText()
    {
        var memory = Gpus(2, free: 16).WithHost(available: 64);
        using var manager = Create(memory);
        await manager.TryReserveAsync(Request("a", "a", budget: 4, gpus: [Uuid(0)], hostBudget: 1), Ct);

        memory.Fail = new IOException("nvidia-smi failed");
        var clash = await manager.TryReserveAsync(Request("b", "b", budget: 4, gpus: [Uuid(0)], hostBudget: 1), Ct);

        Assert.Equal(ReservationOutcome.StartConflict, clash.Outcome);
        Assert.EndsWith($" on node pc (GPU {Uuid(0)}).", clash.Error!.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Per-GPU admission
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PerGpu_BudgetSplitOverThePlacedGpus_PerGpuReserves_RefusalNamesTheGpu()
    {
        // budget 24 on two GPUs = 12 each. GPU 0: 14 free − 1 (its own reserve) = 13 ≥ 12. GPU 1: 13 free − 2 (default) = 11 < 12.
        var memory = new FakeNodeMemory { HostAvailable = 128 }.Gpu(0, Uuid(0), 14).Gpu(1, Uuid(1), 13);
        using var manager = Create(memory);
        var admission = Admission(budget: 24, hostBudget: 4, gpuReserve: 2, perGpu: new Dictionary<string, double> { [Uuid(0)] = 1 });

        var refused = await manager.TryReserveAsync(Request("op", "qwen", admission, gpus: [Uuid(0), Uuid(1)]), Ct);

        Assert.Equal(ReservationOutcome.AdmissionRefused, refused.Outcome);
        Assert.Null(refused.Reservation);
        Assert.Empty(manager.List());
        Assert.Equal(ErrorCodes.AdmissionRefused, refused.Error!.Code);
        Assert.Equal("Model 'qwen' needs 12.0 GiB but only 11.0 GiB is free (13.0 GiB available minus a 2.0 GiB reserve) on node pc (GPU 1).", refused.Error.Message);
        var details = refused.Error.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.Equal(Uuid(1), details.Gpu);
        Assert.Null(details.Host);
        Assert.Equal(12, details.NeedGiB);
        Assert.Equal(11, details.FreeGiB);
        Assert.Equal(13, details.AvailableGiB);
        Assert.Equal(2, details.ReserveGiB);

        // One more GiB on GPU 1 and it fits; GPU 0's own reserve (1, not the default 2) is what lets it pass.
        memory.SetFree(Uuid(1), 14);
        var reserved = await manager.TryReserveAsync(Request("op", "qwen", admission, gpus: [Uuid(0), Uuid(1)]), Ct);
        Assert.Equal(ReservationOutcome.Reserved, reserved.Outcome);
        Assert.Equal(new[] { Uuid(0), Uuid(1) }, reserved.Reservation!.Gpus);
        Assert.Equal(24, reserved.Reservation.BudgetGiB);

        // With GPU 0 at the default reserve it would not have fitted: 14 − 2 = 12 ≥ 12 fits exactly, 13.5 − 2 does not.
        await manager.ReleaseAsync("op", Ct);
        memory.SetFree(Uuid(0), 13.5);
        var withDefault = await manager.TryReserveAsync(Request("op2", "qwen", admission with { GpuReservesGiB = null }, gpus: [Uuid(0), Uuid(1)]), Ct);
        Assert.Equal(Uuid(0), withDefault.Error!.ReadDetails<AdmissionRefusedDetails>()!.Gpu);
        Assert.EndsWith(" on node pc (GPU 0).", withDefault.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PerGpu_TheRefusalUsesTheCurrentIndex_NotThePlacementOrder()
    {
        // The card that is now index 0 has the UUID placed second: the text follows nvidia-smi's index, details the UUID.
        var memory = new FakeNodeMemory { HostAvailable = 128 }.Gpu(1, Uuid(0), 16).Gpu(0, Uuid(1), 3);
        using var manager = Create(memory);

        var refused = await manager.TryReserveAsync(Request("op", "m", budget: 8, gpus: [Uuid(0), Uuid(1)], hostBudget: 1), Ct);

        Assert.EndsWith(" on node pc (GPU 0).", refused.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(Uuid(1), refused.Error.ReadDetails<AdmissionRefusedDetails>()!.Gpu);
    }

    [Fact]
    public async Task PerGpu_APlacedGpuTheProbeDoesNotReport_IsRefusedNotGuessed()
    {
        var memory = Gpus(1, free: 16).WithHost(available: 64);
        using var manager = Create(memory);

        var missing = await manager.TryReserveAsync(Request("op", "m", budget: 4, gpus: [Uuid(7)], hostBudget: 1), Ct);
        Assert.Equal(ReservationOutcome.AdmissionRefused, missing.Outcome);
        Assert.Equal($"Model 'm' needs 4.0 GiB but the GPU's free memory could not be read on node pc (GPU {Uuid(7)}).", missing.Error!.Message);
        Assert.Equal(Uuid(7), missing.Error.ReadDetails<AdmissionRefusedDetails>()!.Gpu);

        memory.Gpu(3, Uuid(7), free: null);
        var noFree = await manager.TryReserveAsync(Request("op", "m", budget: 4, gpus: [Uuid(7)], hostBudget: 1), Ct);
        Assert.EndsWith(" could not be read on node pc (GPU 3).", noFree.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(manager.List());

        // "Start anyway" skips memory admission, so it is not stopped by an unreadable GPU (the start rule still applies).
        var forced = await manager.TryReserveAsync(Request("op", "m", Admission(budget: 4, hostBudget: 1) with { Force = true }, gpus: [Uuid(7)]), Ct);
        Assert.Equal(ReservationOutcome.Reserved, forced.Outcome);
    }

    [Fact]
    public async Task PerGpu_SharesOfOtherReservationsOnTheSameGpu_AreCounted()
    {
        // Records are facts: a rebuild may hold two reservations on one GPU. A re-check counts the other's share there.
        var memory = Gpus(2, free: 20).WithHost(available: 256);
        using var manager = Create(memory);
        var t0 = DateTimeOffset.UnixEpoch;
        await manager.RebuildAsync(
        [
            new RebuiltReservation(Info("tp2", "tp2", budget: 16, [Uuid(0), Uuid(1)], hostBudget: 4, t0), Admission(budget: 16, hostBudget: 4)),
            new RebuiltReservation(Info("solo", "solo", budget: 14, [Uuid(1)], hostBudget: 4, t0.AddSeconds(1)), Admission(budget: 14, hostBudget: 4)),
        ], Ct);

        // solo on GPU 1: 20 free − 0 reserve − 8 (tp2's half) = 12 < 14.
        var solo = await manager.RecheckAsync("solo", Ct);
        Assert.Equal(ReservationOutcome.AdmissionRefused, solo.Outcome);
        var details = solo.Error!.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.Equal(8, details.ReservedByOthersGiB);
        Assert.Equal(12, details.FreeGiB);
        Assert.Equal(Uuid(1), details.Gpu);
        Assert.Equal("solo", solo.Reservation!.OperationId); // still held; the caller fails the operation and releases it
        Assert.EndsWith("minus a 0.0 GiB reserve and 8.0 GiB held by other starts) on node pc (GPU 1).", solo.Error.Message, StringComparison.Ordinal);

        // tp2: GPU 0 has 20 free and nothing else, GPU 1 has 20 − 14 (solo, all on GPU 1) = 6 < 8.
        var tp2 = await manager.RecheckAsync("tp2", Ct);
        Assert.Equal(Uuid(1), tp2.Error!.ReadDetails<AdmissionRefusedDetails>()!.Gpu);
        Assert.Equal(14, tp2.Error.ReadDetails<AdmissionRefusedDetails>()!.ReservedByOthersGiB);

        await manager.ReleaseAsync("solo", Ct);
        Assert.Equal(ReservationOutcome.Reserved, (await manager.RecheckAsync("tp2", Ct)).Outcome);
    }

    [Fact]
    public async Task PerGpu_ReleasedMemoryOfAReplacedInstance_IsSplitOverItsGpus()
    {
        // budget 20 on two GPUs (10 each), 6 free each, 10 released by the old instance (5 each): 6 + 5 = 11 ≥ 10.
        var memory = Gpus(2, free: 6).WithHost(available: 64);
        using var manager = Create(memory);

        var replaced = await manager.TryReserveAsync(Request("op", "m", budget: 20, gpus: [Uuid(0), Uuid(1)], hostBudget: 4) with { ReleasedGiB = 10 }, Ct);
        Assert.Equal(ReservationOutcome.Reserved, replaced.Outcome);

        // Split, not counted whole on every GPU: 4 free + 5 = 9 < 10 (the whole 10 would wrongly give 14). The host part
        // of a GPU start gets nothing released.
        using (var tight = Create(Gpus(2, free: 4).WithHost(available: 64)))
        {
            var refused = await tight.TryReserveAsync(Request("op", "m", budget: 20, gpus: [Uuid(0), Uuid(1)], hostBudget: 4) with { ReleasedGiB = 10 }, Ct);
            Assert.Equal(ReservationOutcome.AdmissionRefused, refused.Outcome);
            var details = refused.Error!.ReadDetails<AdmissionRefusedDetails>()!;
            Assert.Equal(5, details.ReleasedGiB);
            Assert.Equal(9, details.FreeGiB);
            Assert.EndsWith("(4.0 GiB available plus 5.0 GiB released by its current container, minus a 0.0 GiB reserve) on node pc (GPU 0).", refused.Error.Message, StringComparison.Ordinal);
        }

        // After the removal the re-check counts nothing as released: only what the probe shows.
        var recheck = await manager.RecheckAsync("op", Ct);
        Assert.Equal(ReservationOutcome.AdmissionRefused, recheck.Outcome);
        Assert.Equal(0, recheck.Error!.ReadDetails<AdmissionRefusedDetails>()!.ReleasedGiB);
        memory.SetFree(Uuid(0), 11);
        memory.SetFree(Uuid(1), 11);
        Assert.Equal(ReservationOutcome.Reserved, (await manager.RecheckAsync("op", Ct)).Outcome);
    }

    [Fact]
    public async Task Recheck_AfterASlowPull_SeesVramTakenMeanwhile()
    {
        var memory = Gpus(1, free: 16).WithHost(available: 64);
        using var manager = Create(memory);
        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("op", "m", budget: 12, gpus: [Uuid(0)], hostBudget: 4), Ct)).Outcome);

        memory.SetFree(Uuid(0), 9); // another process took VRAM while the image was pulling
        var recheck = await manager.RecheckAsync("op", Ct);

        Assert.Equal(ReservationOutcome.AdmissionRefused, recheck.Outcome);
        Assert.Equal("Model 'm' needs 12.0 GiB but only 9.0 GiB is free (9.0 GiB available minus a 0.0 GiB reserve) on node pc (GPU 0).", recheck.Error!.Message);
        await manager.ReleaseAsync("op", Ct);
        Assert.Empty(manager.List());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Host part
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task HostPart_IsNodeWide_AStartOnAnotherGpuIsRefusedForHostMemory()
    {
        // Host: 40 available − 8 reserve = 32. GPU 0's start holds 20; GPU 1's needs 16 > 12.
        var memory = Gpus(2, free: 16).WithHost(available: 40);
        using var manager = Create(memory);
        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("a", "a", budget: 8, gpus: [Uuid(0)], hostBudget: 20, reserve: 8), Ct)).Outcome);

        var b = await manager.TryReserveAsync(Request("b", "b", budget: 8, gpus: [Uuid(1)], hostBudget: 16, reserve: 8), Ct);

        Assert.Equal(ReservationOutcome.AdmissionRefused, b.Outcome);
        Assert.Equal("Model 'b' needs 16.0 GiB but only 12.0 GiB is free (40.0 GiB available minus a 8.0 GiB reserve and 20.0 GiB held by other starts) on node pc (host memory).", b.Error!.Message);
        var details = b.Error.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.True(details.Host);
        Assert.Null(details.Gpu);
        Assert.Equal(20, details.ReservedByOthersGiB);

        await manager.ReleaseAsync("a", Ct);
        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("b", "b", budget: 8, gpus: [Uuid(1)], hostBudget: 16, reserve: 8), Ct)).Outcome);
    }

    [Fact]
    public async Task HostPart_ConcurrentStartsOnDisjointGpus_NeverOvercommitHostMemory()
    {
        // 16 GPUs with plenty of VRAM, host room for exactly 3 starts of 10 GiB (34 − 4 = 30). Every round: 16 parallel
        // starts on 16 different GPUs; the start rule lets all of them through, the host part must admit exactly three.
        var memory = Gpus(16, free: 80).WithHost(available: 34);
        memory.Delay = TimeSpan.FromMilliseconds(1);
        using var manager = Create(memory);

        for (int round = 0; round < 10; round++)
        {
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () =>
            {
                start.Wait(Ct);
                return await manager.TryReserveAsync(Request($"r{round}-{i}", $"m-{i}", budget: 8, gpus: [Uuid(i)], hostBudget: 10, reserve: 4), Ct);
            }, Ct)).ToArray();
            start.Set();
            var results = await Task.WhenAll(tasks);

            Assert.Equal(3, results.Count(r => r.Outcome == ReservationOutcome.Reserved));
            Assert.All(results.Where(r => r.Outcome != ReservationOutcome.Reserved), r =>
            {
                Assert.Equal(ReservationOutcome.AdmissionRefused, r.Outcome);
                Assert.True(r.Error!.ReadDetails<AdmissionRefusedDetails>()!.Host);
            });
            Assert.True(manager.List().Sum(r => r.HostBudgetGiB!.Value) <= 30);

            foreach (var held in manager.List())
            {
                await manager.ReleaseAsync(held.OperationId, Ct);
            }
        }

        Assert.Empty(manager.List());
        Assert.Equal(1, memory.MaxConcurrentReads);
    }

    [Fact]
    public async Task HostEstimate_UsedWhenTheLaunchNamesNoHostBudget_AndRecorded()
    {
        Assert.Equal(10, DiscreteAdmission.HostEstimateGiB(2));
        Assert.Equal(6, DiscreteAdmission.HostEstimateGiB(1));
        Assert.Equal(18, DiscreteAdmission.HostEstimateGiB(2, swapSpaceGiB: 8));
        Assert.Equal(2, DiscreteAdmission.HostEstimateGiB(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiscreteAdmission.HostEstimateGiB(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiscreteAdmission.HostEstimateGiB(1, double.NaN));

        // Host 30 − 8 reserve = 22. A two-GPU vLLM start without a host budget holds 4 × 2 + 2 = 10.
        var memory = Gpus(3, free: 24).WithHost(available: 30);
        using var manager = Create(memory);
        var tp2 = await manager.TryReserveAsync(Request("tp2", "tp2", budget: 16, gpus: [Uuid(0), Uuid(1)], hostBudget: null, reserve: 8), Ct);
        Assert.Equal(10, tp2.Reservation!.HostBudgetGiB);

        // A CPU-only start (no GPUs) needs its whole budget in host memory: 13 > 22 − 10 = 12.
        var cpu = await manager.TryReserveAsync(Request("cpu", "cpu", budget: 13, gpus: [], hostBudget: null, reserve: 8), Ct);
        Assert.Equal(ReservationOutcome.AdmissionRefused, cpu.Outcome);
        var details = cpu.Error!.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.True(details.Host);
        Assert.Equal(13, details.NeedGiB);
        Assert.Equal(10, details.ReservedByOthersGiB);

        var cpuFits = await manager.TryReserveAsync(Request("cpu", "cpu", budget: 12, gpus: [], hostBudget: null, reserve: 8), Ct);
        Assert.Equal(new[] { ReservationInfo.HostPseudoGpu }, cpuFits.Reservation!.Gpus);
        Assert.Equal(12, cpuFits.Reservation.HostBudgetGiB);

        // A one-GPU start now needs 6 of the 0 left.
        var one = await manager.TryReserveAsync(Request("one", "one", budget: 4, gpus: [Uuid(2)], hostBudget: null, reserve: 8), Ct);
        Assert.Equal(6, one.Error!.ReadDetails<AdmissionRefusedDetails>()!.NeedGiB);
        Assert.Equal(22, one.Error.ReadDetails<AdmissionRefusedDetails>()!.ReservedByOthersGiB);
    }

    [Fact]
    public async Task HostEstimate_ForARebuiltRecordWithoutHostBudget_CountsForOthers()
    {
        var memory = Gpus(2, free: 24).WithHost(available: 20);
        using var manager = Create(memory);
        await manager.RebuildAsync([new ReservationInfo
        {
            OperationId = "old",
            ModelId = "old",
            Gpus = [Uuid(0)],
            BudgetGiB = 12,
            CreatedAt = DateTimeOffset.UnixEpoch,
        }], Ct);

        // 20 − 0 reserve − 6 (the old record's 1-GPU estimate) = 14 < 15.
        var next = await manager.TryReserveAsync(Request("new", "new", budget: 4, gpus: [Uuid(1)], hostBudget: 15, reserve: 0), Ct);
        Assert.Equal(6, next.Error!.ReadDetails<AdmissionRefusedDetails>()!.ReservedByOthersGiB);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Force, failures, cancellation, leaks
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Force_SkipsMemoryAdmission_NotTheStartRule_AndDoesNotReadMemory()
    {
        var memory = Gpus(1, free: 1).WithHost(available: 1);
        using var manager = Create(memory);

        var forced = await manager.TryReserveAsync(Request("op", "big", Admission(budget: 60, hostBudget: 60) with { Force = true }, gpus: [Uuid(0)]), Ct);
        Assert.Equal(ReservationOutcome.Reserved, forced.Outcome);
        Assert.Equal(0, memory.Reads);

        var second = await manager.TryReserveAsync(Request("op2", "other", Admission(budget: 1, hostBudget: 1) with { Force = true }, gpus: [Uuid(0)]), Ct);
        Assert.Equal(ReservationOutcome.StartConflict, second.Outcome);
    }

    [Fact]
    public async Task AFailingMemoryRead_ReservesNothing_AndFreesTheLock()
    {
        var memory = Gpus(2, free: 16).WithHost(available: 64);
        using var manager = Create(memory);
        memory.Fail = new InvalidOperationException("probe down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TryReserveAsync(Request("op", "m", budget: 4, gpus: [Uuid(0)], hostBudget: 1), Ct));
        Assert.Empty(manager.List());

        memory.Fail = null;
        var retry = await manager.TryReserveAsync(Request("op", "m", budget: 4, gpus: [Uuid(0)], hostBudget: 1), Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(ReservationOutcome.Reserved, retry.Outcome);
    }

    [Fact]
    public async Task ACancelledStartWaitingBehindAnotherGpusDecision_LeavesNoReservation()
    {
        var memory = Gpus(2, free: 16).WithHost(available: 64);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        memory.Gate = gate.Task;
        using var manager = Create(memory);

        var first = manager.TryReserveAsync(Request("a", "a", budget: 4, gpus: [Uuid(0)], hostBudget: 1), Ct);
        await memory.WaitForReadAsync(1);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var second = manager.TryReserveAsync(Request("b", "b", budget: 4, gpus: [Uuid(1)], hostBudget: 1), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);

        gate.SetResult();
        Assert.Equal(ReservationOutcome.Reserved, (await first).Outcome);
        Assert.Equal("a", Assert.Single(manager.List()).OperationId);
        Assert.Equal(1, memory.Reads);
    }

    [Fact]
    public async Task ParallelReserveReleaseCycles_AcrossGpus_LeakNothing()
    {
        var memory = Gpus(4, free: 32).WithHost(available: 1024);
        var bus = new CountingEventBus();
        using var manager = Create(memory, events: bus);
        var heldPorts = new ConcurrentDictionary<int, string>();
        int duplicates = 0;
        int reserved = 0;

        var workers = Enumerable.Range(0, 12).Select(w => Task.Run(async () =>
        {
            for (int i = 0; i < 40; i++)
            {
                string op = $"w{w}-{i}";
                var result = await manager.TryReserveAsync(Request(op, $"m-{w}", budget: 4, gpus: [Uuid((w + i) % 4)], hostBudget: 2), Ct);
                if (result.Outcome != ReservationOutcome.Reserved)
                {
                    Assert.Equal(ReservationOutcome.StartConflict, result.Outcome);
                    continue;
                }

                Interlocked.Increment(ref reserved);
                if (!heldPorts.TryAdd(result.Reservation!.HostPort!.Value, op))
                {
                    Interlocked.Increment(ref duplicates);
                }

                await Task.Yield();
                Assert.True(heldPorts.TryRemove(result.Reservation.HostPort.Value, out _));
                await manager.ReleaseAsync(op, Ct);
            }
        }, Ct)).ToArray();
        await Task.WhenAll(workers);

        Assert.True(reserved > 0);
        Assert.Equal(0, duplicates);
        Assert.Empty(manager.List());
        Assert.Equal(bus.Reserved, bus.Released);
        Assert.Equal(reserved, bus.Reserved);

        // Every GPU is free again.
        for (int g = 0; g < 4; g++)
        {
            Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request($"end-{g}", $"end-{g}", budget: 4, gpus: [Uuid(g)], hostBudget: 2), Ct)).Outcome);
        }
    }

    [Fact]
    public async Task NegativeGpuReserves_AreRejected()
    {
        using var manager = Create(Gpus(1, free: 16).WithHost(available: 64));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.TryReserveAsync(Request("op", "m", Admission(budget: 1, hostBudget: 1, gpuReserve: -1), gpus: [Uuid(0)]), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.TryReserveAsync(
            Request("op", "m", Admission(budget: 1, hostBudget: 1, perGpu: new Dictionary<string, double> { [Uuid(0)] = double.NaN }), gpus: [Uuid(0)]), Ct));
        Assert.Empty(manager.List());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The real reader: a fresh nvidia-smi read per decision
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithTheRealReader_AdmissionUsesTheDriversFreeVram_ReadAfreshEachDecision()
    {
        // Recorded 2× RTX 4060 Ti: GPU 0 has 1873 MiB free (busy), GPU 1 15973 MiB.
        var runner = new CountingNvidiaSmi(Fixture("rtx4060ti-x2.linux.telemetry.csv"));
        var telemetry = new NvidiaSmiGpuTelemetryProbe(runner, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance);
        using var reader = DiscreteAdmission.CreateMemoryReader(new FixedHost(64, 48), telemetry, TimeProvider.System);
        using var manager = new ReservationManager(Options(), new FixedHost(64, 48), nodeMemory: reader);

        var gpu0 = await manager.TryReserveAsync(Request("op-0", "a", budget: 4, gpus: ["GPU-0a1b2c3d-1111-2222-3333-444455556666"], hostBudget: 4), Ct);
        var gpu1 = await manager.TryReserveAsync(Request("op-1", "b", budget: 4, gpus: ["GPU-7e8f9a0b-aaaa-bbbb-cccc-ddddeeeeffff"], hostBudget: 4), Ct);

        Assert.Equal(ReservationOutcome.AdmissionRefused, gpu0.Outcome);
        Assert.EndsWith(" on node pc (GPU 0).", gpu0.Error!.Message, StringComparison.Ordinal);
        Assert.Equal(1873 / 1024d, gpu0.Error.ReadDetails<AdmissionRefusedDetails>()!.AvailableGiB, precision: 6);
        Assert.Equal(ReservationOutcome.Reserved, gpu1.Outcome);
        Assert.Equal(2, runner.Calls); // no reuse window between two decisions
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Unified nodes and the P1 shape stay as they were
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unified_NeverReadsTheNodeReader_AndAnswersExactlyAsWithoutIt()
    {
        var reader = Gpus(2, free: 0).WithHost(available: 0);
        using var withReader = new ReservationManager(Options() with { NodeId = "dgx", MemoryModel = MemoryModel.Unified }, new FixedHost(128, 40), nodeMemory: reader);
        using var without = new ReservationManager(Options() with { NodeId = "dgx", MemoryModel = MemoryModel.Unified }, new FixedHost(128, 40));

        Assert.False(withReader.PerGpuAdmission);
        string[] a = await ScriptAsync(withReader);
        string[] b = await ScriptAsync(without);
        Assert.Equal(b, a);
        Assert.Equal(0, reader.Reads);
        var refusal = JsonSerializer.Deserialize<ReservationResult>(a[0], AgentJson.Options)!;
        Assert.Equal("Model 'big' needs 50.0 GiB but only 32.0 GiB is free (40.0 GiB available minus a 8.0 GiB reserve) on node dgx.", refusal.Error!.Message);
        Assert.Null(refusal.Error.ReadDetails<AdmissionRefusedDetails>()!.Gpu);
        Assert.Null(refusal.Error.ReadDetails<AdmissionRefusedDetails>()!.Host);
        Assert.Null(JsonSerializer.Deserialize<ReservationResult>(a[1], AgentJson.Options)!.Reservation!.HostBudgetGiB);
        Assert.EndsWith(" on node dgx.", JsonSerializer.Deserialize<ReservationResult>(a[2], AgentJson.Options)!.Error!.Message, StringComparison.Ordinal);

        static async Task<string[]> ScriptAsync(ReservationManager m)
        {
            var steps = new List<ReservationResult>
            {
                await m.TryReserveAsync(Request("big", "big", budget: 50, gpus: ["GPU-x"], hostBudget: 1, reserve: 8)),
                await m.TryReserveAsync(Request("ok", "ok", budget: 30, gpus: ["GPU-x"], hostBudget: 1, reserve: 8)),
                await m.TryReserveAsync(Request("two", "two", budget: 1, gpus: ["GPU-y"], hostBudget: 1, reserve: 8)),
                await m.RecheckAsync("ok"),
            };
            return [.. steps.Select(r => JsonSerializer.Serialize(r with { Reservation = r.Reservation is null ? null : r.Reservation with { CreatedAt = default } }, AgentJson.Options))];
        }
    }

    [Fact]
    public async Task Discrete_WithoutAReader_KeepsTheP1Shape()
    {
        using var manager = new ReservationManager(Options(), new FixedHost(64, 64));
        Assert.False(manager.PerGpuAdmission);

        // The P1 shape: host-only admission with the host budget (else the budget), the UUID in the conflict text.
        var a = await manager.TryReserveAsync(Request("a", "a", budget: 20, gpus: [Uuid(0)], hostBudget: null, reserve: 8), Ct);
        Assert.Null(a.Reservation!.HostBudgetGiB);
        var b = await manager.TryReserveAsync(Request("b", "b", budget: 40, gpus: [Uuid(1)], hostBudget: null, reserve: 8), Ct);
        Assert.Equal(ReservationOutcome.AdmissionRefused, b.Outcome);
        Assert.EndsWith(" on node pc (host memory).", b.Error!.Message, StringComparison.Ordinal);
        var clash = await manager.TryReserveAsync(Request("c", "c", budget: 1, gpus: [Uuid(0)], hostBudget: 1, reserve: 0), Ct);
        Assert.EndsWith($" on node pc (GPU {Uuid(0)}).", clash.Error!.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // admission.discrete contributor
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Contributor_ReportsTheFlag_OnlyOnADiscreteNodeWithPerGpuAdmission()
    {
        using var perGpu = Create(Gpus(2, free: 16).WithHost(available: 64));
        using var p1Shape = new ReservationManager(Options(), new FixedHost(64, 64));
        using var unified = new ReservationManager(Options() with { MemoryModel = MemoryModel.Unified }, new FixedHost(64, 64), nodeMemory: Gpus(1, free: 1));

        var discreteReport = Report(MemoryModel.Discrete, "telemetry.v1");
        var withFlag = await new DiscreteAdmissionContributor(perGpu).ContributeAsync(discreteReport, Ct);
        Assert.Equal(new[] { "telemetry.v1", AgentFeatures.AdmissionDiscrete }, withFlag.Features);
        Assert.Equal(withFlag.Features, (await new DiscreteAdmissionContributor(perGpu).ContributeAsync(withFlag, Ct)).Features); // once

        Assert.Same(discreteReport, await new DiscreteAdmissionContributor(p1Shape).ContributeAsync(discreteReport, Ct));
        Assert.Same(discreteReport, await new DiscreteAdmissionContributor(unified).ContributeAsync(discreteReport, Ct));

        // The report says unified (a GB10, or the GPU read failed): no flag even with per-GPU admission.
        var unifiedReport = Report(MemoryModel.Unified, "telemetry.v1");
        Assert.Same(unifiedReport, await new DiscreteAdmissionContributor(perGpu).ContributeAsync(unifiedReport, Ct));
    }

    [Fact]
    public async Task Contributor_AfterTheTelemetryContributor_FollowsTheRecordedInventories()
    {
        using var perGpu = Create(Gpus(2, free: 16).WithHost(available: 64));
        var contributor = new DiscreteAdmissionContributor(perGpu);

        var pc = await new TelemetryCapabilityContributor(new NvidiaSmiGpuInventory(new CountingNvidiaSmi(Fixture("rtx4060ti-x2.linux.inventory.csv"))))
            .ContributeAsync(Report(MemoryModel.Unified), Ct);
        Assert.Contains(AgentFeatures.AdmissionDiscrete, (await contributor.ContributeAsync(pc, Ct)).Features);

        var dgx = await new TelemetryCapabilityContributor(new NvidiaSmiGpuInventory(new CountingNvidiaSmi(Fixture("gb10.linux.inventory.csv"))))
            .ContributeAsync(Report(MemoryModel.Unified), Ct);
        Assert.DoesNotContain(AgentFeatures.AdmissionDiscrete, (await contributor.ContributeAsync(dgx, Ct)).Features);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers and fakes
    // ---------------------------------------------------------------------------------------------------------------

    private static AdmissionOptions Options() => new() { NodeId = Node, MemoryModel = MemoryModel.Discrete, PortRangeStart = 8101, PortRangeEnd = 8999 };

    private static ReservationManager Create(FakeNodeMemory memory, IEventBus? events = null) =>
        new(Options(), new FixedHost(0, 0), events: events, nodeMemory: memory);

    private static FakeNodeMemory Gpus(int count, double free)
    {
        var memory = new FakeNodeMemory();
        for (int i = 0; i < count; i++)
        {
            memory.Gpu(i, Uuid(i), free);
        }

        return memory;
    }

    private static AdmissionRequest Admission(double budget, double? hostBudget, double reserve = 0, double? gpuReserve = null, IReadOnlyDictionary<string, double>? perGpu = null) => new()
    {
        BudgetGiB = budget,
        HostBudgetGiB = hostBudget,
        ReserveGiB = reserve,
        GpuReserveGiB = gpuReserve,
        GpuReservesGiB = perGpu,
    };

    private static StartReservationRequest Request(string op, string model, double budget, string[] gpus, double? hostBudget, double reserve = 0) =>
        Request(op, model, Admission(budget, hostBudget, reserve), gpus);

    private static StartReservationRequest Request(string op, string model, AdmissionRequest admission, string[] gpus) => new()
    {
        OperationId = op,
        ModelId = model,
        Admission = admission,
        GpuUuids = gpus,
    };

    private static ReservationInfo Info(string op, string model, double budget, string[] gpus, double? hostBudget, DateTimeOffset at) => new()
    {
        OperationId = op,
        ModelId = model,
        Gpus = gpus,
        BudgetGiB = budget,
        HostBudgetGiB = hostBudget,
        CreatedAt = at,
    };

    private static string ModelOf(ReservationResult loser) =>
        loser.Error!.Message.Split('\'')[3]; // "Model '{holder}' is still starting. Start '{model}' once …"

    private static NodeCapabilities Report(MemoryModel model, params string[] features) => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "1.0.0",
        Features = features,
        Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(model, 64),
    };

    private static string Fixture(string name, [CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "nvidia-smi", name)).ReplaceLineEndings("\n");

    /// <summary>
    /// Host and per-GPU memory as the reservation manager reads it, with knobs for free VRAM, failures, a delay or a gate,
    /// and counters for reads and for reads running at the same time.
    /// </summary>
    private sealed class FakeNodeMemory : INodeMemoryReader
    {
        private readonly Lock _lock = new();
        private readonly List<GpuMemorySnapshot> _gpus = [];
        private int _reads;
        private int _active;
        private int _maxActive;

        public double HostAvailable { get; set; }

        public TimeSpan Delay { get; set; }

        public Task? Gate { get; set; }

        public Exception? Fail { get; set; }

        public int Reads => Volatile.Read(ref _reads);

        public int MaxConcurrentReads => Volatile.Read(ref _maxActive);

        public FakeNodeMemory WithHost(double available)
        {
            HostAvailable = available;
            return this;
        }

        public FakeNodeMemory Gpu(int index, string uuid, double? free)
        {
            lock (_lock)
            {
                _gpus.RemoveAll(g => g.Uuid == uuid);
                _gpus.Add(new GpuMemorySnapshot(index, uuid, 16, free is null ? null : 16 - free, free));
            }

            return this;
        }

        public void SetFree(string uuid, double free)
        {
            lock (_lock)
            {
                int at = _gpus.FindIndex(g => g.Uuid == uuid);
                _gpus[at] = _gpus[at] with { FreeGiB = free, UsedGiB = 16 - free };
            }
        }

        public async Task WaitForReadAsync(int count)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (Reads < count)
            {
                Assert.True(DateTime.UtcNow < deadline, "timed out waiting for a memory read");
                await Task.Delay(5, TestContext.Current.CancellationToken);
            }
        }

        public async Task<NodeMemorySnapshot> ReadAsync(CancellationToken ct = default)
        {
            int active = Interlocked.Increment(ref _active);
            int max;
            while (active > (max = Volatile.Read(ref _maxActive)) && Interlocked.CompareExchange(ref _maxActive, active, max) != max)
            {
            }

            Interlocked.Increment(ref _reads);
            try
            {
                if (Gate is { } gate)
                {
                    await gate.WaitAsync(ct);
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct);
                }

                if (Fail is { } fail)
                {
                    throw fail;
                }

                lock (_lock)
                {
                    return new NodeMemorySnapshot
                    {
                        Model = MemoryModel.Discrete,
                        HostTotalGiB = 1024,
                        HostAvailableGiB = HostAvailable,
                        Gpus = [.. _gpus.OrderBy(g => g.Index)],
                    };
                }
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class FixedHost(double total, double available) : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new MemorySnapshot(total, available));
    }

    private sealed class CountingNvidiaSmi(string output) : INvidiaSmiRunner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new NvidiaSmiResult(true, 0, output, ""));
        }
    }

    /// <summary>Counts reservation events published by the manager (it is their only publisher).</summary>
    private sealed class CountingEventBus : IEventBus
    {
        private readonly Lock _stateLock = new();
        private long _seq;
        private int _reserved;
        private int _released;

        public string BootId => "boot-1";

        public long CurrentSeq
        {
            get
            {
                lock (_stateLock)
                {
                    return _seq;
                }
            }
        }

        public int Reserved => Volatile.Read(ref _reserved);

        public int Released => Volatile.Read(ref _released);

        public AgentEvent Publish(AgentEvent evt) => Publish(() => { }, evt);

        public AgentEvent Publish(Action stateChange, AgentEvent evt)
        {
            lock (_stateLock)
            {
                stateChange();
                if (evt is ReservationEvent reservation)
                {
                    if (reservation.Released)
                    {
                        _released++;
                    }
                    else
                    {
                        _reserved++;
                    }
                }

                return evt with { Seq = ++_seq, BootId = BootId, At = DateTimeOffset.UnixEpoch };
            }
        }

        public T Snapshot<T>(Func<long, T> build)
        {
            lock (_stateLock)
            {
                return build(_seq);
            }
        }

        public IEventSubscription Subscribe(string? bootId, long? afterSeq) => throw new NotSupportedException();
    }
}
