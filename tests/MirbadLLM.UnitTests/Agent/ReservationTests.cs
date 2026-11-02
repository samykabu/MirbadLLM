using System.Collections.Concurrent;
using System.Text.Json;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

#pragma warning disable CA1707, CA1861, xUnit1051 // underscore test names; inline expected arrays; hand-written fakes do not need the test cancellation token.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>MN-016: start reservations, admission and the node-wide port lock (design §4.8).</summary>
public sealed class ReservationTests
{
    private const string Node = "dgx";

    // ---------------------------------------------------------------------------------------------------------------
    // Start rule (unified)
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Unified_TwentyConcurrentLaunches_ExactlyOneReservation_RestStartConflict()
    {
        var probe = new FakeMemoryProbe(available: 1000) { Delay = TimeSpan.FromMilliseconds(2) };
        using var manager = Create(probe: probe);

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            start.Wait();
            return await manager.TryReserveAsync(Request($"op-{i}", $"model-{i}", budget: 10));
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);

        var winners = results.Where(r => r.Outcome == ReservationOutcome.Reserved).ToArray();
        var winner = Assert.Single(winners);
        Assert.Single(manager.List());
        Assert.Equal(winner.Reservation!.OperationId, manager.List()[0].OperationId);

        var losers = results.Where(r => r.Outcome != ReservationOutcome.Reserved).ToArray();
        Assert.Equal(19, losers.Length);
        foreach (var loser in losers)
        {
            Assert.Equal(ReservationOutcome.StartConflict, loser.Outcome);
            Assert.Null(loser.Reservation);
            Assert.Equal(ErrorCodes.StartConflict, loser.Error!.Code);
            var details = loser.Error.ReadDetails<StartConflictDetails>()!;
            Assert.Equal(winner.Reservation.ModelId, details.StartingModelId);
            Assert.Null(details.Gpus);
            Assert.EndsWith(" on node dgx.", loser.Error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Unified_StressRounds_NeverMoreThanOneHolder_AndEventsMatchState()
    {
        var bus = new RecordingEventBus();
        using var manager = Create(probe: new FakeMemoryProbe(available: 1000), events: bus);

        for (int round = 0; round < 30; round++)
        {
            using var start = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            {
                start.Wait();
                var result = await manager.TryReserveAsync(Request($"r{round}-op-{i}", $"model-{i}", budget: 5));
                if (result.Outcome == ReservationOutcome.Reserved && i % 3 == 0)
                {
                    // Some winners finish fast (Ready) while others still race for the slot.
                    await manager.ReleaseAsync(result.Reservation!.OperationId);
                }

                return result;
            })).ToArray();
            start.Set();
            await Task.WhenAll(tasks);

            Assert.True(manager.List().Count <= 1);
            foreach (var r in manager.List())
            {
                await manager.ReleaseAsync(r.OperationId);
            }
        }

        // Replaying the event stream never shows two outstanding reservations, and ends where the state ends.
        int outstanding = 0;
        int maxOutstanding = 0;
        foreach (var evt in bus.Events.OfType<ReservationEvent>())
        {
            outstanding += evt.Released ? -1 : 1;
            maxOutstanding = Math.Max(maxOutstanding, outstanding);
        }

        Assert.Equal(1, maxOutstanding);
        Assert.Equal(0, outstanding);
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task Unified_SecondModel_StartConflict_HasTodaysTextPlusNode()
    {
        using var manager = Create();
        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("op-a", "alpha"))).Outcome);

        var result = await manager.TryReserveAsync(Request("op-b", "beta"));

        Assert.Equal(ReservationOutcome.StartConflict, result.Outcome);
        Assert.Equal(
            "Model 'alpha' is still starting. Start 'beta' once it is Ready: vLLM measures memory while it loads, " +
            "so two models starting together both fail on node dgx.",
            result.Error!.Message);
        Assert.Equal("alpha", result.Error.ReadDetails<StartConflictDetails>()!.StartingModelId);
    }

    [Fact]
    public async Task Unified_SameModelOtherOperation_Conflicts_SameOperation_IsIdempotent()
    {
        var persistence = new RecordingPersistence();
        using var manager = Create(persistence: persistence);
        var first = await manager.TryReserveAsync(Request("op-1", "alpha"));
        var again = await manager.TryReserveAsync(Request("op-1", "alpha"));
        var other = await manager.TryReserveAsync(Request("op-2", "alpha"));

        Assert.Equal(ReservationOutcome.Reserved, again.Outcome);
        Assert.Same(first.Reservation, again.Reservation);
        Assert.Single(persistence.Saved);
        Assert.Equal(ReservationOutcome.StartConflict, other.Outcome);
        Assert.Equal("alpha", other.Error!.ReadDetails<StartConflictDetails>()!.StartingModelId);
    }

    [Fact]
    public async Task StartingInstanceWithoutReservation_HoldsTheSlot_LikeToday()
    {
        var instances = new FakeInstances(Instance("adopted", ModelState.Starting, port: 8101));
        using var manager = Create(instances: instances);

        var result = await manager.TryReserveAsync(Request("op-1", "alpha"));

        Assert.Equal(ReservationOutcome.StartConflict, result.Outcome);
        Assert.Equal("adopted", result.Error!.ReadDetails<StartConflictDetails>()!.StartingModelId);
    }

    [Theory]
    [InlineData(ModelState.Ready, false, "other")]
    [InlineData(ModelState.Failed, false, "other")]
    [InlineData(ModelState.Stopping, false, "other")]
    [InlineData(ModelState.Starting, true, "other")] // tuning candidates are not registry models: today's rule ignores them
    [InlineData(ModelState.Starting, false, "alpha")] // the model itself is ignored (ensure-started returns it)
    public async Task InstancesThatDoNotHoldTheSlot(ModelState state, bool tuning, string modelId)
    {
        var instances = new FakeInstances(Instance(modelId, state, port: 8101, tuning: tuning));
        using var manager = Create(instances: instances);

        var result = await manager.TryReserveAsync(Request("op-1", "alpha"));

        Assert.Equal(ReservationOutcome.Reserved, result.Outcome);
    }

    [Fact]
    public async Task Unified_MaxConcurrentStartsTwo_SecondCountsFirstsMemory_ThirdConflicts()
    {
        using var manager = Create(probe: new FakeMemoryProbe(available: 60), options: Options() with { MaxConcurrentStarts = 2 });

        var a = await manager.TryReserveAsync(Request("op-a", "alpha", budget: 30, reserve: 8));
        var b = await manager.TryReserveAsync(Request("op-b", "beta", budget: 30, reserve: 8));

        Assert.Equal(ReservationOutcome.Reserved, a.Outcome);
        Assert.Equal(ReservationOutcome.AdmissionRefused, b.Outcome);
        Assert.Equal(
            "Model 'beta' needs 30.0 GiB but only 22.0 GiB is free (60.0 GiB available minus a 8.0 GiB reserve and 30.0 GiB held by other starts) on node dgx.",
            b.Error!.Message);
        var details = b.Error.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.Equal(30, details.ReservedByOthersGiB);
        Assert.Equal(22, details.FreeGiB);

        var b2 = await manager.TryReserveAsync(Request("op-b2", "beta", budget: 10, reserve: 8));
        Assert.Equal(ReservationOutcome.Reserved, b2.Outcome);
        var c2 = await manager.TryReserveAsync(Request("op-c2", "gamma", budget: 1, reserve: 0));
        Assert.Equal(ReservationOutcome.StartConflict, c2.Outcome);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Memory admission (today's texts)
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AdmissionRefused_StartForm_TodaysTextPlusNode_AndDetails()
    {
        using var manager = Create(probe: new FakeMemoryProbe(available: 40));

        var result = await manager.TryReserveAsync(Request("op-1", "big", budget: 40, reserve: 8));

        Assert.Equal(ReservationOutcome.AdmissionRefused, result.Outcome);
        Assert.Null(result.Reservation);
        Assert.Empty(manager.List());
        Assert.Equal(ErrorCodes.AdmissionRefused, result.Error!.Code);
        Assert.Equal(
            "Model 'big' needs 40.0 GiB but only 32.0 GiB is free (40.0 GiB available minus a 8.0 GiB reserve) on node dgx.",
            result.Error.Message);
        var details = result.Error.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.Equal(40, details.NeedGiB);
        Assert.Equal(32, details.FreeGiB);
        Assert.Equal(40, details.AvailableGiB);
        Assert.Equal(8, details.ReserveGiB);
        Assert.Equal(0, details.ReleasedGiB);
        Assert.Equal(0, details.ReservedByOthersGiB);
        Assert.Null(details.Gpu);
        Assert.Null(details.Host);
    }

    [Fact]
    public async Task AdmissionRefused_ReplaceForm_TodaysLaunchTextPlusNode()
    {
        using var manager = Create(probe: new FakeMemoryProbe(available: 20.3));

        var result = await manager.TryReserveAsync(Request("op-1", "big", budget: 40, reserve: 8) with { ReleasedGiB = 12.5 });

        Assert.Equal(
            "Model 'big' needs 40.0 GiB but only 24.8 GiB is free (20.3 GiB available plus 12.5 GiB released by its current container, " +
            "minus a 8.0 GiB reserve) on node dgx.",
            result.Error!.Message);
        Assert.Equal(12.5, result.Error.ReadDetails<AdmissionRefusedDetails>()!.ReleasedGiB);
    }

    [Fact]
    public async Task Admission_BudgetEqualToFree_IsAdmitted_LikeToday()
    {
        using var manager = Create(probe: new FakeMemoryProbe(available: 48));
        var result = await manager.TryReserveAsync(Request("op-1", "exact", budget: 40, reserve: 8));
        Assert.Equal(ReservationOutcome.Reserved, result.Outcome);
    }

    [Fact]
    public async Task Admission_ReleasedMemoryCounts_ForThePreRemovalDecision()
    {
        using var manager = Create(probe: new FakeMemoryProbe(available: 20));
        var result = await manager.TryReserveAsync(Request("op-1", "m", budget: 30, reserve: 8) with { ReleasedGiB = 20 });
        Assert.Equal(ReservationOutcome.Reserved, result.Outcome);
    }

    [Fact]
    public async Task Force_SkipsMemoryAdmission_ButNotTheStartRule()
    {
        var probe = new FakeMemoryProbe(available: 1);
        using var manager = Create(probe: probe);

        var forced = await manager.TryReserveAsync(Request("op-1", "huge", budget: 500, force: true));
        var forcedSecond = await manager.TryReserveAsync(Request("op-2", "other", budget: 1, force: true));

        Assert.Equal(ReservationOutcome.Reserved, forced.Outcome);
        Assert.Equal(500, forced.Reservation!.BudgetGiB);
        Assert.Equal(ReservationOutcome.StartConflict, forcedSecond.Outcome);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task Admission_UsesAFreshProbeEveryTime()
    {
        var probe = new FakeMemoryProbe(available: 100);
        using var manager = Create(probe: probe);

        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("op-1", "a", budget: 50))).Outcome);
        await manager.ReleaseAsync("op-1");
        probe.Available = 30; // "a" took its memory on Ready
        Assert.Equal(ReservationOutcome.AdmissionRefused, (await manager.TryReserveAsync(Request("op-2", "b", budget: 50))).Outcome);
        Assert.Equal(2, probe.Calls);
    }

    [Fact]
    public async Task InvalidInputs_AreRejected()
    {
        using var manager = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => manager.TryReserveAsync(Request("op", "m", budget: double.NaN)));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.TryReserveAsync(Request("op", "m", budget: -1)));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.TryReserveAsync(Request("op", "m") with { ReleasedGiB = double.PositiveInfinity }));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.TryReserveAsync(Request("", "m")));
        Assert.Throws<ArgumentException>(() => new ReservationManager(Options() with { PortRangeStart = 9000, PortRangeEnd = 8000 }, new FakeMemoryProbe(1)));
        Assert.Throws<ArgumentException>(() => new ReservationManager(Options() with { MaxConcurrentStarts = 0 }, new FakeMemoryProbe(1)));
        Assert.ThrowsAny<ArgumentException>(() => new ReservationManager(new AdmissionOptions { NodeId = " " }, new FakeMemoryProbe(1)));
        Assert.Empty(manager.List());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Release
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("ready")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task Release_FreesTheSlot_AndPublishesReleased(string why)
    {
        var bus = new RecordingEventBus();
        using var manager = Create(events: bus);
        // The handler (MN-031) calls ReleaseAsync on Ready, on failure and on cancel alike.
        var a = await manager.TryReserveAsync(Request($"op-a-{why}", "alpha"));
        Assert.Equal(ReservationOutcome.StartConflict, (await manager.TryReserveAsync(Request("op-b", "beta"))).Outcome);

        await manager.ReleaseAsync($"op-a-{why}");

        Assert.Empty(manager.List());
        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("op-b", "beta"))).Outcome);
        var events = bus.Events.OfType<ReservationEvent>().ToArray();
        Assert.Equal(3, events.Length);
        Assert.False(events[0].Released);
        Assert.True(events[1].Released);
        Assert.Equal(a.Reservation, events[1].Reservation);
        Assert.Equal("op-b", events[2].Reservation.OperationId);
    }

    [Fact]
    public async Task Release_UnknownOrTwice_IsIgnored()
    {
        var bus = new RecordingEventBus();
        using var manager = Create(events: bus);
        await manager.ReleaseAsync("nope");
        await manager.TryReserveAsync(Request("op-a", "alpha"));
        await manager.ReleaseAsync("op-a");
        await manager.ReleaseAsync("op-a");

        Assert.Equal(2, bus.Events.Count);
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task Release_DoesNotWaitForAProbeInProgress()
    {
        var probe = new FakeMemoryProbe(available: 100);
        using var manager = Create(probe: probe, options: Options() with { MaxConcurrentStarts = 2 });
        await manager.TryReserveAsync(Request("op-a", "alpha"));

        probe.Block();
        var pending = manager.TryReserveAsync(Request("op-b", "beta"));
        await probe.WaitUntilBlockedAsync();

        await manager.ReleaseAsync("op-a").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(manager.List(), r => r.OperationId == "op-a");

        probe.Unblock();
        Assert.Equal(ReservationOutcome.Reserved, (await pending).Outcome);
        Assert.Equal("op-b", Assert.Single(manager.List()).OperationId);
    }

    [Fact]
    public async Task ConcurrentReleaseAndReserve_NoLostUpdates()
    {
        using var manager = Create(probe: new FakeMemoryProbe(available: 1000));
        for (int i = 0; i < 200; i++)
        {
            string holder = $"hold-{i}";
            Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request(holder, $"h{i}"))).Outcome);

            var release = Task.Run(() => manager.ReleaseAsync(holder));
            var reserve = Task.Run(() => manager.TryReserveAsync(Request($"next-{i}", $"n{i}")));
            await release;
            var result = await reserve;

            Assert.DoesNotContain(manager.List(), r => r.OperationId == holder);
            if (result.Outcome == ReservationOutcome.Reserved)
            {
                Assert.Equal($"next-{i}", Assert.Single(manager.List()).OperationId);
                await manager.ReleaseAsync($"next-{i}");
            }
            else
            {
                Assert.Equal(ReservationOutcome.StartConflict, result.Outcome);
                Assert.Empty(manager.List());
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Ports
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ports_LowestFree_SkippingOtherInstances_ReusingTheModelsOwn()
    {
        var instances = new FakeInstances(
            Instance("alpha", ModelState.Ready, port: 8101),
            Instance("old", ModelState.Failed, port: 8102), // exited containers keep their port, as today
            Instance("beta", ModelState.Ready, port: 8104));
        using var manager = Create(instances: instances);

        var other = await manager.TryReserveAsync(Request("op-1", "gamma"));
        Assert.Equal(8103, other.Reservation!.HostPort);
        await manager.ReleaseAsync("op-1");

        // Replacing alpha: its own instance is removed before create, so its port is free again (today's order).
        var replace = await manager.TryReserveAsync(Request("op-2", "alpha"));
        Assert.Equal(8101, replace.Reservation!.HostPort);
    }

    [Fact]
    public async Task Ports_HeldByAnotherProgram_AreSkippedUnderTheLock_WhenTheNodeChecksThem()
    {
        // MN-219 (D127): on a desktop PC another program can hold a port of the range (any address). The process driver
        // used to bump to the next port itself, outside the reservation lock, so two concurrent launches could both
        // land on the same next port. With PortInUse (the process configuration) the reservation skips it.
        int first = TestPorts.ReserveBlock(3);
        using var holder = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, first);
        holder.Start();
        var range = Options() with { PortRangeStart = first, PortRangeEnd = first + 2 };

        using (var containers = Create(options: range))
        {
            Assert.Equal(first, (await containers.TryReserveAsync(Request("op-0", "alpha"))).Reservation!.HostPort); // containers: as before
        }

        using var manager = Create(options: range with { PortInUse = MirbadLLM.NodeRuntime.Runtimes.Process.ProcessPorts.IsTaken });
        var a = await manager.TryReserveAsync(Request("op-1", "alpha", budget: 1));
        int leased = await manager.AllocatePortAsync("beta", "rec-1");

        Assert.Equal(first + 1, a.Reservation!.HostPort);
        Assert.Equal(first + 2, leased);
    }

    [Fact]
    public async Task AllocatePort_ReturnsTheReservationsPort_AndLeasesOtherwise()
    {
        using var manager = Create();
        var reserved = await manager.TryReserveAsync(Request("op-1", "alpha"));
        Assert.Equal(reserved.Reservation!.HostPort, await manager.AllocatePortAsync("alpha", "op-1"));

        int lease = await manager.AllocatePortAsync("beta", "rec-1");
        Assert.Equal(8102, lease);
        Assert.Equal(lease, await manager.AllocatePortAsync("beta", "rec-1"));
        Assert.Equal(8103, await manager.AllocatePortAsync("gamma", "rec-2"));

        await manager.ReleaseAsync("rec-1");
        Assert.Equal(8102, await manager.AllocatePortAsync("delta", "rec-3"));
    }

    [Fact]
    public async Task AllocatePort_ThenReserve_KeepsTheLeasedPort()
    {
        using var manager = Create();
        int port = await manager.AllocatePortAsync("x", "filler");
        int leased = await manager.AllocatePortAsync("alpha", "op-1");
        var reserved = await manager.TryReserveAsync(Request("op-1", "alpha"));

        Assert.Equal(8101, port);
        Assert.Equal(leased, reserved.Reservation!.HostPort);
        await manager.ReleaseAsync("op-1");
        Assert.Equal(8102, await manager.AllocatePortAsync("y", "again"));
    }

    [Fact]
    public async Task Ports_UniqueUnderConcurrency_ReservationsAndLeases()
    {
        var instances = new FakeInstances(Instance("existing", ModelState.Ready, port: 8105)) { Delay = TimeSpan.FromMilliseconds(1) };
        using var manager = Create(
            probe: new FakeMemoryProbe(available: 100_000),
            instances: instances,
            options: Options() with { MaxConcurrentStarts = 100 });

        using var start = new ManualResetEventSlim(false);
        var reserveTasks = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
        {
            start.Wait();
            var r = await manager.TryReserveAsync(Request($"op-{i}", $"model-{i}", budget: 1));
            return r.Reservation!.HostPort!.Value;
        }));
        var leaseTasks = Enumerable.Range(0, 30).Select(i => Task.Run(async () =>
        {
            start.Wait();
            return await manager.AllocatePortAsync($"lease-model-{i}", $"lease-{i}");
        }));
        var all = reserveTasks.Concat(leaseTasks).ToArray();
        start.Set();
        int[] ports = await Task.WhenAll(all);

        Assert.Equal(ports.Length, ports.Distinct().Count());
        Assert.DoesNotContain(8105, ports);
        Assert.All(ports, p => Assert.InRange(p, 8101, 8101 + 70));
    }

    [Fact]
    public async Task Ports_Exhausted_Throws_AndHoldsNothing()
    {
        var instances = new FakeInstances(Instance("a", ModelState.Ready, port: 9000), Instance("b", ModelState.Ready, port: 9001));
        using var manager = Create(instances: instances, options: Options() with { PortRangeStart = 9000, PortRangeEnd = 9001 });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TryReserveAsync(Request("op-1", "c")));
        Assert.Contains("on node dgx", ex.Message, StringComparison.Ordinal);
        Assert.Empty(manager.List());
        Assert.Equal(9000, (await manager.TryReserveAsync(Request("op-2", "a"))).Reservation!.HostPort); // "a" reuses its own
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Re-check (slow pull, replacement)
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Recheck_AfterSlowPull_MemoryShrank_Refused_ReservationStillHeld()
    {
        var probe = new FakeMemoryProbe(available: 60);
        using var manager = Create(probe: probe);
        await manager.TryReserveAsync(Request("op-1", "alpha", budget: 40, reserve: 8));

        probe.Available = 30;
        var result = await manager.RecheckAsync("op-1");

        Assert.Equal(ReservationOutcome.AdmissionRefused, result.Outcome);
        Assert.Equal("op-1", result.Reservation!.OperationId);
        Assert.Equal(
            "Model 'alpha' needs 40.0 GiB but only 22.0 GiB is free (30.0 GiB available minus a 8.0 GiB reserve) on node dgx.",
            result.Error!.Message);
        Assert.Single(manager.List());
        await manager.ReleaseAsync("op-1");
        Assert.Empty(manager.List());
    }

    [Fact]
    public async Task Recheck_AfterReplacementRemoval_UsesTheMeasuredRelease_NotTheDeclaredOne()
    {
        var probe = new FakeMemoryProbe(available: 10);
        using var manager = Create(probe: probe);
        // Pre-removal decision: 10 available + 40 declared released − 8 reserve = 42 ≥ 40.
        var reserved = await manager.TryReserveAsync(Request("op-1", "alpha", budget: 40, reserve: 8) with { ReleasedGiB = 40 });
        Assert.Equal(ReservationOutcome.Reserved, reserved.Outcome);

        probe.Available = 50; // the old instance really freed 40 GiB: 50 − 8 = 42 ≥ 40
        Assert.Equal(ReservationOutcome.Reserved, (await manager.RecheckAsync("op-1")).Outcome);

        probe.Available = 30; // it freed less than declared
        var refused = await manager.RecheckAsync("op-1");
        Assert.Equal(ReservationOutcome.AdmissionRefused, refused.Outcome);
        Assert.Equal(0, refused.Error!.ReadDetails<AdmissionRefusedDetails>()!.ReleasedGiB);
    }

    [Fact]
    public async Task Recheck_Force_DoesNotProbe_UnknownThrows_OthersCount()
    {
        var probe = new FakeMemoryProbe(available: 100);
        using var manager = Create(probe: probe, options: Options() with { MaxConcurrentStarts = 2 });
        await manager.TryReserveAsync(Request("op-f", "forced", budget: 500, force: true));
        int calls = probe.Calls;
        Assert.Equal(ReservationOutcome.Reserved, (await manager.RecheckAsync("op-f")).Outcome);
        Assert.Equal(calls, probe.Calls);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.RecheckAsync("missing"));

        await manager.ReleaseAsync("op-f");
        await manager.TryReserveAsync(Request("op-a", "a", budget: 40, reserve: 0));
        await manager.TryReserveAsync(Request("op-b", "b", budget: 40, reserve: 0));
        probe.Available = 70;
        var recheck = await manager.RecheckAsync("op-b");
        Assert.Equal(ReservationOutcome.AdmissionRefused, recheck.Outcome);
        Assert.Equal(40, recheck.Error!.ReadDetails<AdmissionRefusedDetails>()!.ReservedByOthersGiB);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Persistence, crash and rebuild
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Persistence_IsWrittenBeforeTheReservationIsVisible()
    {
        ReservationManager? manager = null;
        var persistence = new RecordingPersistence
        {
            OnSave = _ => Assert.Empty(manager!.List()),
        };
        manager = Create(persistence: persistence);
        using (manager)
        {
            var result = await manager.TryReserveAsync(Request("op-1", "alpha"));
            Assert.Equal(result.Reservation, Assert.Single(persistence.Saved));
        }
    }

    [Fact]
    public async Task PersistenceFailure_TakesNothing_AndReleasesTheLock()
    {
        var persistence = new RecordingPersistence { Fail = true };
        using var manager = Create(persistence: persistence);

        await Assert.ThrowsAsync<IOException>(() => manager.TryReserveAsync(Request("op-1", "alpha")));
        Assert.Empty(manager.List());

        persistence.Fail = false;
        var result = await manager.TryReserveAsync(Request("op-2", "beta"));
        Assert.Equal(ReservationOutcome.Reserved, result.Outcome);
        Assert.Equal(8101, result.Reservation!.HostPort);
    }

    [Fact]
    public async Task Crash_ThenRebuildFromPersistedRecords_RestoresSlotsPortsAndRechecks()
    {
        var store = new RecordingPersistence();
        var probe = new FakeMemoryProbe(available: 200);
        var options = Options() with { MaxConcurrentStarts = 2 };
        using (var before = Create(probe: probe, persistence: store, options: options))
        {
            await before.TryReserveAsync(Request("op-a", "alpha", budget: 50, reserve: 8));
            await before.TryReserveAsync(Request("op-b", "beta", budget: 30, reserve: 8));
            await before.ReleaseAsync("op-b"); // Ready before the crash: its operation is terminal, not rebuilt
        }

        // Agent restarts: reconciliation rebuilds from non-terminal launch operations (op-a) and instances still
        // Starting without one (gamma, derived from its labels, no port recorded).
        var bus = new RecordingEventBus();
        using var after = Create(probe: probe, persistence: store, options: options, events: bus,
            instances: new FakeInstances(Instance("gamma", ModelState.Starting, port: 8102)));
        var persistedA = store.Saved.Single(r => r.OperationId == "op-a");
        await after.RebuildAsync([
            persistedA,
            new ReservationInfo { OperationId = "op-g", ModelId = "gamma", BudgetGiB = 20, CreatedAt = persistedA.CreatedAt.AddSeconds(1) },
        ]);

        Assert.Equal(new[] { "op-a", "op-g" }, after.List().Select(r => r.OperationId));
        Assert.Equal(2, bus.Events.OfType<ReservationEvent>().Count(e => !e.Released));

        // Both slots are held again.
        Assert.Equal(ReservationOutcome.StartConflict, (await after.TryReserveAsync(Request("op-new", "delta"))).Outcome);

        // Ports: op-a keeps 8101; op-g gets one now (persisted), not colliding with the persisted ones.
        Assert.Equal(8101, await after.AllocatePortAsync("alpha", "op-a"));
        int gammaPort = await after.AllocatePortAsync("gamma", "op-g");
        Assert.Equal(8102, gammaPort); // its own Starting instance's port is reusable by itself
        Assert.Equal(gammaPort, store.Saved[^1].HostPort);

        // Re-check of a record rebuilt without admission inputs uses the default reserve: 200 − 8 − 50 (alpha) = 142.
        probe.Available = 77; // 77 − 8 − 50 = 19 < 20
        var recheck = await after.RecheckAsync("op-g");
        Assert.Equal(ReservationOutcome.AdmissionRefused, recheck.Outcome);
        Assert.Equal(8, recheck.Error!.ReadDetails<AdmissionRefusedDetails>()!.ReserveGiB);

        await after.ReleaseAsync("op-a");
        await after.ReleaseAsync("op-g");
        Assert.Equal(ReservationOutcome.Reserved, (await after.TryReserveAsync(Request("op-new", "delta", budget: 1))).Outcome);
    }

    [Fact]
    public async Task Rebuild_ReplacesHeldState_KeepsAdmissionInputs_LastDuplicateWins()
    {
        var bus = new RecordingEventBus();
        var probe = new FakeMemoryProbe(available: 5);
        using var manager = Create(probe: probe, events: bus);
        await manager.TryReserveAsync(Request("stale", "old", budget: 1, reserve: 0));
        await manager.AllocatePortAsync("leased", "lease-1");

        var at = DateTimeOffset.UnixEpoch;
        await manager.RebuildAsync([
            new RebuiltReservation(new ReservationInfo { OperationId = "op-x", ModelId = "x", BudgetGiB = 99, HostPort = 8200, CreatedAt = at }),
            new RebuiltReservation(
                new ReservationInfo { OperationId = "op-x", ModelId = "x", BudgetGiB = 50, HostPort = 8201, CreatedAt = at },
                new AdmissionRequest { BudgetGiB = 50, ReserveGiB = 0, Force = true }),
        ]);

        var held = Assert.Single(manager.List());
        Assert.Equal(50, held.BudgetGiB);
        Assert.Equal(8201, held.HostPort);
        Assert.Contains(bus.Events.OfType<ReservationEvent>(), e => e.Released && e.Reservation.OperationId == "stale");
        Assert.Equal(ReservationOutcome.Reserved, (await manager.RecheckAsync("op-x")).Outcome); // force survived
        Assert.Equal(8101, await manager.AllocatePortAsync("y", "lease-2")); // old lease dropped
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Cancellation and lock discipline
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelled_WhileWaitingForTheLock_TakesNothing()
    {
        var probe = new FakeMemoryProbe(available: 100);
        using var manager = Create(probe: probe, options: Options() with { MaxConcurrentStarts = 5 });
        probe.Block();
        var first = manager.TryReserveAsync(Request("op-1", "alpha"));
        await probe.WaitUntilBlockedAsync();

        using var cts = new CancellationTokenSource();
        var waiting = manager.TryReserveAsync(Request("op-2", "beta"), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        probe.Unblock();
        Assert.Equal(ReservationOutcome.Reserved, (await first).Outcome);
        Assert.Equal(new[] { "op-1" }, manager.List().Select(r => r.OperationId));
    }

    [Fact]
    public async Task Cancelled_DuringTheProbe_TakesNothing_AndFreesTheLock()
    {
        var probe = new FakeMemoryProbe(available: 100);
        using var manager = Create(probe: probe);
        probe.Block();

        using var cts = new CancellationTokenSource();
        var pending = manager.TryReserveAsync(Request("op-1", "alpha"), cts.Token);
        await probe.WaitUntilBlockedAsync();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        probe.Unblock();

        Assert.Empty(manager.List());
        var next = await manager.TryReserveAsync(Request("op-2", "beta")).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ReservationOutcome.Reserved, next.Outcome);
        Assert.Equal(8101, next.Reservation!.HostPort);
    }

    [Fact]
    public async Task ProbeFailure_PropagatesAndFreesTheLock()
    {
        var probe = new FakeMemoryProbe(available: 100) { Fail = true };
        using var manager = Create(probe: probe);
        await Assert.ThrowsAsync<IOException>(() => manager.TryReserveAsync(Request("op-1", "alpha")));
        probe.Fail = false;
        Assert.Equal(ReservationOutcome.Reserved, (await manager.TryReserveAsync(Request("op-1", "alpha"))).Outcome);
    }

    [Fact]
    public async Task ProbesAndInstanceScans_AreNeverConcurrent()
    {
        var probe = new FakeMemoryProbe(available: 100_000) { Delay = TimeSpan.FromMilliseconds(1) };
        var instances = new FakeInstances { Delay = TimeSpan.FromMilliseconds(1) };
        using var manager = Create(probe: probe, instances: instances, options: Options() with { MaxConcurrentStarts = 1000 });

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 60).Select(i => Task.Run(async () =>
        {
            start.Wait();
            switch (i % 3)
            {
                case 0:
                    await manager.TryReserveAsync(Request($"op-{i}", $"m-{i}", budget: 1));
                    break;
                case 1:
                    await manager.AllocatePortAsync($"l-{i}", $"lease-{i}");
                    break;
                default:
                    var r = await manager.TryReserveAsync(Request($"op-{i}", $"m-{i}", budget: 1));
                    await manager.RecheckAsync(r.Reservation!.OperationId);
                    await manager.ReleaseAsync(r.Reservation.OperationId);
                    break;
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(1, probe.MaxConcurrent);
        Assert.Equal(1, instances.MaxConcurrent);
    }

    [Fact]
    public async Task List_IsSafeInsideAnEventBusSnapshot_WhileReservationsChange()
    {
        var bus = new RecordingEventBus();
        using var manager = Create(probe: new FakeMemoryProbe(available: 100_000), events: bus, options: Options() with { MaxConcurrentStarts = 1000 });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var writer = Task.Run(async () =>
        {
            for (int i = 0; i < 300; i++)
            {
                await manager.TryReserveAsync(Request($"op-{i}", $"m-{i}", budget: 1));
                if (i % 2 == 0)
                {
                    await manager.ReleaseAsync($"op-{i}");
                }
            }
        });

        while (!writer.IsCompleted)
        {
            // The snapshot must equal the replay of every reservation event up to its watermark.
            var (seq, list) = bus.Snapshot(w => (w, manager.List()));
            var replay = new HashSet<string>(StringComparer.Ordinal);
            foreach (var evt in bus.Events.Where(e => e.Seq <= seq).OfType<ReservationEvent>())
            {
                if (evt.Released)
                {
                    replay.Remove(evt.Reservation.OperationId);
                }
                else
                {
                    replay.Add(evt.Reservation.OperationId);
                }
            }

            Assert.Equal(replay.Order(StringComparer.Ordinal), list.Select(r => r.OperationId).Order(StringComparer.Ordinal));
            await Task.Yield();
        }

        await writer.WaitAsync(cts.Token);
        Assert.Equal(150, manager.List().Count);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Discrete shape (P2; present but inert in P1)
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Discrete_OneStartPerGpu_HostPseudoGpu_AndHostAdmission()
    {
        var probe = new FakeMemoryProbe(available: 64);
        using var manager = Create(probe: probe, options: Options() with { MemoryModel = MemoryModel.Discrete });

        var g0 = await manager.TryReserveAsync(Request("op-0", "a", budget: 20, reserve: 8, gpus: ["GPU-0"], hostBudget: 16));
        var g1 = await manager.TryReserveAsync(Request("op-1", "b", budget: 20, reserve: 8, gpus: ["GPU-1"], hostBudget: 16));
        var clash = await manager.TryReserveAsync(Request("op-2", "c", budget: 1, reserve: 0, gpus: ["GPU-1", "GPU-2"], hostBudget: 1));
        var cpu = await manager.TryReserveAsync(Request("op-3", "cpu", budget: 0, reserve: 8, hostBudget: 30));

        Assert.Equal(ReservationOutcome.Reserved, g0.Outcome);
        Assert.Equal(new[] { "GPU-0" }, g0.Reservation!.Gpus);
        Assert.Equal(16, g0.Reservation.HostBudgetGiB);
        Assert.Equal(ReservationOutcome.Reserved, g1.Outcome);

        Assert.Equal(ReservationOutcome.StartConflict, clash.Outcome);
        var details = clash.Error!.ReadDetails<StartConflictDetails>()!;
        Assert.Equal("b", details.StartingModelId);
        Assert.Equal(new[] { "GPU-1" }, details.Gpus);
        Assert.EndsWith(" on node dgx (GPU GPU-1).", clash.Error.Message, StringComparison.Ordinal);

        // Host memory is node-wide: 64 − 8 − (16 + 16) = 24 < 30.
        Assert.Equal(ReservationOutcome.AdmissionRefused, cpu.Outcome);
        var refused = cpu.Error!.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.True(refused.Host);
        Assert.Equal(32, refused.ReservedByOthersGiB);
        Assert.EndsWith(" on node dgx (host memory).", cpu.Error.Message, StringComparison.Ordinal);

        var cpuSmall = await manager.TryReserveAsync(Request("op-4", "cpu", budget: 0, reserve: 8, hostBudget: 10));
        Assert.Equal(new[] { ReservationInfo.HostPseudoGpu }, cpuSmall.Reservation!.Gpus);
        var cpuSecond = await manager.TryReserveAsync(Request("op-5", "cpu2", budget: 0, reserve: 0, hostBudget: 1));
        Assert.Equal(new[] { ReservationInfo.HostPseudoGpu }, cpuSecond.Error!.ReadDetails<StartConflictDetails>()!.Gpus);
    }

    [Fact]
    public async Task Discrete_NodeWideCap_AndConcurrentDisjointGpus()
    {
        using var manager = Create(probe: new FakeMemoryProbe(available: 10_000), options: Options() with { MemoryModel = MemoryModel.Discrete });
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 24).Select(i => Task.Run(async () =>
        {
            start.Wait();
            return await manager.TryReserveAsync(Request($"op-{i}", $"m-{i}", budget: 1, reserve: 0, gpus: [$"GPU-{i % 8}"]));
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(8, results.Count(r => r.Outcome == ReservationOutcome.Reserved));
        Assert.Equal(8, manager.List().SelectMany(r => r.Gpus).Distinct().Count());

        using var capped = Create(probe: new FakeMemoryProbe(available: 10_000), options: Options() with { MemoryModel = MemoryModel.Discrete, MaxConcurrentStarts = 2 });
        await capped.TryReserveAsync(Request("a", "a", budget: 1, reserve: 0, gpus: ["GPU-0"]));
        await capped.TryReserveAsync(Request("b", "b", budget: 1, reserve: 0, gpus: ["GPU-1"]));
        var third = await capped.TryReserveAsync(Request("c", "c", budget: 1, reserve: 0, gpus: ["GPU-2"]));
        Assert.Equal(ReservationOutcome.StartConflict, third.Outcome);
        Assert.Null(third.Error!.ReadDetails<StartConflictDetails>()!.Gpus);
    }

    [Fact]
    public async Task Unified_IgnoresGpuUuids_ForTheSlotAndTheRecord()
    {
        using var manager = Create();
        var a = await manager.TryReserveAsync(Request("op-a", "a", gpus: ["GPU-0"]));
        var b = await manager.TryReserveAsync(Request("op-b", "b", gpus: ["GPU-1"]));
        Assert.Empty(a.Reservation!.Gpus);
        Assert.Null(a.Reservation.HostBudgetGiB);
        Assert.Equal(ReservationOutcome.StartConflict, b.Outcome);
    }

    [Fact]
    public async Task RuntimeDriverInstanceSource_ConcatenatesEveryDriver()
    {
        var source = new RuntimeDriverInstanceSource([
            new FakeDriver(Instance("a", ModelState.Ready, port: 8101)),
            new FakeDriver(Instance("b", ModelState.Starting, port: 8102)),
        ]);
        var all = await source.ListAsync();
        Assert.Equal(new[] { "a", "b" }, all.Select(i => i.ModelId));

        using var manager = Create(instances: source);
        var result = await manager.TryReserveAsync(Request("op", "c"));
        Assert.Equal("b", result.Error!.ReadDetails<StartConflictDetails>()!.StartingModelId);
    }

    [Fact]
    public void Texts_MatchDockerModelRuntime()
    {
        Assert.Equal(
            "Model 'm' needs 1.0 GiB but only 0.5 GiB is free (8.5 GiB available minus a 8.0 GiB reserve).",
            AdmissionTexts.MemoryRefused("m", 1, 0.5, 8.5, 8, 0, 0));
        Assert.Equal(
            "Model 'm' needs 1.0 GiB but only 0.5 GiB is free (0.5 GiB available plus 8.0 GiB released by its current container, minus a 8.0 GiB reserve).",
            AdmissionTexts.MemoryRefused("m", 1, 0.5, 0.5, 8, 8, 0));
        Assert.Equal("x on node n.", AdmissionTexts.OnNode("x", "n"));
        Assert.Equal("x on node n (GPU 0).", AdmissionTexts.OnNode("x.", "n", "GPU 0"));

        // A container node (the DGX, and every P1/P2 node) keeps the pre-P1 sentence, word for word.
        Assert.Equal(
            "Model 'a' is still starting. Start 'b' once it is Ready: vLLM measures memory while it loads, " +
            "so two models starting together both fail.",
            AdmissionTexts.StartConflict("a", "b"));
    }

    [Fact]
    public void StartConflict_OnAProcessNode_NamesTheRuntimeThatNodeRuns()
    {
        // MN-239 H2: the text told the operator of a Windows llama.cpp node that "vLLM measures memory while it loads"
        // — a runtime that PC does not have and cannot run, the same class of wrong wording as the hostd/systemd texts
        // of MN-239 item 1. AdmissionOptions.ProcessDelivery (set by the composition on a Docker-off node) picks it.
        string process = AdmissionTexts.StartConflict("a", "b", processDelivery: true);

        Assert.Equal(
            "Model 'a' is still starting. Start 'b' once it is Ready: llama.cpp takes its weights and KV cache while " +
            "it loads, so two models starting together both fail.",
            process);
        Assert.DoesNotContain("vLLM", process, StringComparison.Ordinal);

        // Same shape, so OnNode and the GPU part still compose onto it unchanged.
        Assert.EndsWith(" on node pc (GPU 1).", AdmissionTexts.OnNode(process, "pc", "GPU 1"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers and fakes
    // ---------------------------------------------------------------------------------------------------------------

    private static AdmissionOptions Options() => new() { NodeId = Node };

    private static ReservationManager Create(
        FakeMemoryProbe? probe = null,
        IRuntimeInstanceSource? instances = null,
        IReservationPersistence? persistence = null,
        IEventBus? events = null,
        AdmissionOptions? options = null) =>
        new(options ?? Options(), probe ?? new FakeMemoryProbe(available: 100), instances, persistence, events);

    private static StartReservationRequest Request(
        string operationId,
        string modelId,
        double budget = 10,
        double reserve = 8,
        bool force = false,
        IReadOnlyList<string>? gpus = null,
        double? hostBudget = null) => new()
        {
            OperationId = operationId,
            ModelId = modelId,
            Admission = new AdmissionRequest { BudgetGiB = budget, ReserveGiB = reserve, Force = force, HostBudgetGiB = hostBudget },
            GpuUuids = gpus ?? [],
        };

    private static RuntimeStatus Instance(string modelId, ModelState state, int? port, bool tuning = false) => new()
    {
        ModelId = modelId,
        State = state,
        HostPort = port,
        Tuning = tuning,
    };

    private sealed class FakeMemoryProbe(double available) : IMemoryProbe
    {
        private readonly Lock _gate = new();
        private TaskCompletionSource? _block;
        private TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _calls;
        private int _max;

        public double Available { get; set; } = available;

        public TimeSpan Delay { get; init; }

        public bool Fail { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public int MaxConcurrent => Volatile.Read(ref _max);

        public void Block()
        {
            lock (_gate)
            {
                _block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Task WaitUntilBlockedAsync()
        {
            lock (_gate)
            {
                return _blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        public void Unblock()
        {
            lock (_gate)
            {
                _block?.TrySetResult();
                _block = null;
            }
        }

        public async Task<MemorySnapshot> GetAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            int now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            try
            {
                Task? wait;
                lock (_gate)
                {
                    wait = _block?.Task;
                    _blocked.TrySetResult();
                }

                if (wait is not null)
                {
                    await wait.WaitAsync(ct);
                }

                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct);
                }

                if (Fail)
                {
                    throw new IOException("probe failed");
                }

                return new MemorySnapshot(128, Available);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class FakeInstances(params RuntimeStatus[] instances) : IRuntimeInstanceSource
    {
        private int _active;
        private int _max;

        public TimeSpan Delay { get; init; }

        public int MaxConcurrent => Volatile.Read(ref _max);

        public async Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default)
        {
            int now = Interlocked.Increment(ref _active);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct);
                }

                return instances;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class RecordingPersistence : IReservationPersistence
    {
        private readonly ConcurrentQueue<ReservationInfo> _saved = new();

        public bool Fail { get; set; }

        public Action<ReservationInfo>? OnSave { get; init; }

        public IReadOnlyList<ReservationInfo> Saved => [.. _saved];

        public Task SaveAsync(ReservationInfo reservation, CancellationToken ct = default)
        {
            if (Fail)
            {
                throw new IOException("disk full");
            }

            OnSave?.Invoke(reservation);
            _saved.Enqueue(reservation);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingEventBus : IEventBus
    {
        private readonly Lock _stateLock = new();
        private readonly List<AgentEvent> _events = [];
        private long _seq;

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

        public IReadOnlyList<AgentEvent> Events
        {
            get
            {
                lock (_stateLock)
                {
                    return [.. _events];
                }
            }
        }

        public AgentEvent Publish(AgentEvent evt) => Publish(() => { }, evt);

        public AgentEvent Publish(Action stateChange, AgentEvent evt)
        {
            lock (_stateLock)
            {
                stateChange();
                var stamped = evt with { Seq = ++_seq, BootId = BootId, At = DateTimeOffset.UnixEpoch };
                _events.Add(stamped);
                return stamped;
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

    private sealed class FakeDriver(params RuntimeStatus[] instances) : IRuntimeDriver
    {
        public LaunchDelivery Delivery => LaunchDelivery.Container;

        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RuntimeStatus>>(instances);

        public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
