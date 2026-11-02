using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Agent.RuntimeOps;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// MN-031: the runtime watcher publishes runtime state changes in order and settles launches and reservations (design
/// §4.7, §4.8): released on Ready, on failure and on cancel; never on a failed listing.
/// </summary>
public sealed class RuntimeWatcherTests
{
    [Fact]
    public async Task Ready_SucceedsTheLaunchAndReleasesTheReservation_EventsInOrder()
    {
        await using var h = await RuntimeHarness.StartAsync();
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();
        long before = h.Events.CurrentSeq;

        h.Driver.SetState("qwen", ModelState.Ready);
        await h.Watcher.PollOnceAsync(TestContext.Current.CancellationToken);

        var status = h.Log.Find("op-1")!;
        Assert.Equal(OperationState.Succeeded, status.State);
        Assert.Equal(ModelState.Ready, status.ReadResult<RuntimeStatus>()!.State);
        Assert.Empty(h.Reservations.List());

        // runtime(Ready) → operation(ready) → operation(succeeded) → reservation(released), seq strictly increasing.
        var after = h.Events.Published.Where(e => e.Seq > before).ToList();
        Assert.Collection(after,
            e => Assert.Equal(ModelState.Ready, Assert.IsType<RuntimeEvent>(e).Runtime!.State),
            e => Assert.Equal(OperationStages.Ready, Assert.IsType<OperationEvent>(e).Operation.Stage),
            e => Assert.Equal(OperationState.Succeeded, Assert.IsType<OperationEvent>(e).Operation.State),
            e => Assert.True(Assert.IsType<ReservationEvent>(e).Released));
        Assert.True(after.Zip(after.Skip(1)).All(p => p.Second.Seq == p.First.Seq + 1));
    }

    [Fact]
    public async Task Failed_FailsTheLaunchAndReleasesTheReservation()
    {
        await using var h = await RuntimeHarness.StartAsync();
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();

        h.Driver.SetState("qwen", ModelState.Failed, "CUDA out of memory");
        await h.Watcher.PollOnceAsync(TestContext.Current.CancellationToken);

        var status = h.Log.Find("op-1")!;
        Assert.Equal(OperationState.Failed, status.State);
        Assert.Contains("CUDA out of memory", status.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(h.Reservations.List());
        Assert.Equal(ModelState.Failed, h.Events.Of<RuntimeEvent>()[^1].Runtime!.State);
    }

    [Fact]
    public async Task Vanished_FailsTheLaunchAndReleasesTheReservation()
    {
        await using var h = await RuntimeHarness.StartAsync();
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();

        h.Driver.Remove("qwen");
        await h.Watcher.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OperationState.Failed, h.Log.Find("op-1")!.State);
        Assert.Empty(h.Reservations.List());
        Assert.Null(h.Events.Of<RuntimeEvent>()[^1].Runtime);
    }

    [Fact]
    public async Task StopWhileStarting_CancelsTheLaunchAndReleasesTheReservation()
    {
        await using var h = await RuntimeHarness.StartAsync();
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();
        h.Driver.StopGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await h.PostStopAsync("qwen", h.Stop("stop-1"));
        await h.Driver.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        h.Driver.Remove("qwen"); // the instance vanishes while the stop is still running
        await h.Watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OperationState.Running, h.Log.Find("op-1")!.State); // not failed: a stop is in progress
        h.Driver.StopGate.SetResult();
        await h.Operations.WhenIdleAsync();

        Assert.Equal(OperationState.Succeeded, h.Log.Find("stop-1")!.State);
        Assert.Equal(OperationState.Cancelled, h.Log.Find("op-1")!.State);
        Assert.Empty(h.Reservations.List());
    }

    [Fact]
    public async Task EnsureStartedOnAStartingInstance_CompletesAtReady()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.Seed("qwen", ModelState.Starting);
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));

        await h.Watcher.PollOnceAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OperationState.Running, h.Log.Find("op-1")!.State);
        h.Driver.SetState("qwen", ModelState.Ready);
        await h.Watcher.PollOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(OperationState.Succeeded, h.Log.Find("op-1")!.State);
    }

    [Fact]
    public async Task Changes_ArePublishedOncePerChange_AndRemovalsAsNull()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        h.Driver.Seed("qwen", ModelState.Starting);

        await h.Watcher.PollOnceAsync(ct);
        await h.Watcher.PollOnceAsync(ct); // unchanged: no event
        h.Driver.SetState("qwen", ModelState.Ready);
        await h.Watcher.PollOnceAsync(ct);
        h.Driver.Remove("qwen");
        await h.Watcher.PollOnceAsync(ct);

        var events = h.Events.Of<RuntimeEvent>();
        Assert.Equal([ModelState.Starting, ModelState.Ready, (ModelState?)null], events.Select(e => e.Runtime?.State));
        Assert.Empty(h.Board.Current);
    }

    [Fact]
    public async Task FailedListing_ChangesNothing()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();
        int events = h.Events.Published.Count;

        h.Driver.ListFailure = new HttpRequestException("docker down");
        await h.Watcher.PollOnceAsync(ct);

        Assert.Equal(events, h.Events.Published.Count);
        Assert.Equal(OperationState.Running, h.Log.Find("op-1")!.State);
        Assert.Single(h.Reservations.List());
        Assert.NotNull(h.Board.Find("qwen"));
    }

    [Fact]
    public async Task RebuiltReservationOfAStartedLaunch_IsAdoptedAndReleasedAtReady()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var now = h.Time.GetUtcNow();
        // State after an agent restart: the launch reached "started", its reservation was rebuilt, the instance starts.
        await h.Log.SeedAsync("op-1", OperationKinds.Launch, "qwen", OperationStages.Started, now);
        await h.Reservations.RebuildAsync([RuntimeHarness.ReservationFor("op-1", "qwen", now)], ct);
        h.Driver.Seed("qwen", ModelState.Starting, operationId: "op-1");

        await h.Watcher.PollOnceAsync(ct);
        Assert.Single(h.Reservations.List());
        h.Driver.SetState("qwen", ModelState.Ready);
        await h.Watcher.PollOnceAsync(ct);

        Assert.Equal(OperationState.Succeeded, h.Log.Find("op-1")!.State);
        Assert.Empty(h.Reservations.List());
    }

    [Fact]
    public async Task ReservationWithATerminalOrUnknownOperation_IsReleased()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var now = h.Time.GetUtcNow();
        await h.Log.SeedAsync("op-done", OperationKinds.Launch, "qwen", OperationStages.Started, now);
        await h.Log.CompleteAsync("op-done", OperationState.Failed, new AgentError { Code = ErrorCodes.InternalError, Message = "x" }, null, ct);
        await h.Reservations.RebuildAsync([RuntimeHarness.ReservationFor("op-done", "qwen", now), RuntimeHarness.ReservationFor("op-ghost", "gemma", now)], ct);

        await h.Watcher.PollOnceAsync(ct);

        Assert.Empty(h.Reservations.List());
        Assert.Equal(2, h.Events.Of<ReservationEvent>().Count(e => e.Released));
    }

    [Fact]
    public async Task ReservationOfAReplaceNotYetResumed_IsKeptWhileTheOldInstanceIsReady()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        var now = h.Time.GetUtcNow();
        await h.Log.SeedAsync("op-replace", OperationKinds.Launch, "qwen", OperationStages.Admitted, now);
        await h.Reservations.RebuildAsync([RuntimeHarness.ReservationFor("op-replace", "qwen", now)], ct);
        h.Driver.Seed("qwen", ModelState.Ready, operationId: "op-older");

        await h.Watcher.PollOnceAsync(ct);

        Assert.Single(h.Reservations.List());
        Assert.Equal(OperationState.Running, h.Log.Find("op-replace")!.State);
    }

    [Fact]
    public async Task ExecutingLaunch_IsNeverSettledByTheWatcher()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        h.Driver.PullGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Driver.PullStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        await h.Watcher.PollOnceAsync(ct);

        Assert.Single(h.Reservations.List());
        Assert.Equal(OperationState.Running, h.Log.Find("op-1")!.State);
        h.Driver.PullGate.SetResult();
        await h.Operations.WhenIdleAsync();
    }

    [Fact]
    public async Task Board_ListingTakenBeforeAReport_DoesNotOverwriteIt()
    {
        await using var h = await RuntimeHarness.StartAsync();
        long listedAt = h.Board.Version;
        var started = h.Driver.Seed("qwen", ModelState.Starting);

        h.Board.Report("qwen", started);   // a launch reports its new instance…
        h.Board.Apply([], listedAt);       // …while a listing from before it says "nothing there"

        Assert.NotNull(h.Board.Find("qwen"));
        Assert.Single(h.Events.Of<RuntimeEvent>());
    }

    [Fact]
    public async Task ListingOfAnInstanceItsLaunchHasNotReported_IsNotPublished_SoReadyNeverPrecedesStarting()
    {
        // MN-047: the driver starts the container before the launch reports it Starting; a watcher round in between can
        // already see it Ready (fast /health). Publishing that would show the master Ready → Starting → Ready.
        await using var h = await RuntimeHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        long before = h.Events.CurrentSeq;
        h.Driver.AfterStart = async () =>
        {
            h.Driver.SetState("qwen", ModelState.Ready);
            await h.Watcher.PollOnceAsync(ct); // before this fix: published Ready here, then the launch's Starting
        };

        await h.PostLaunchAsync("qwen", h.Launch("op-1"));
        await h.Operations.WhenIdleAsync();
        await h.Watcher.PollOnceAsync(ct);

        var states = h.Events.Published.Where(e => e.Seq > before).OfType<RuntimeEvent>().Select(e => e.Runtime?.State).ToList();
        Assert.Equal([ModelState.Starting, ModelState.Ready], states);
        Assert.Equal(OperationState.Succeeded, h.Log.Find("op-1")!.State);
    }

    [Fact]
    public async Task LaunchThatStartsWaitingWhileTheDriversAreListed_IsNotFailedAsVanished()
    {
        // MN-047: the listing is taken, then the launch creates its instance and starts waiting for Ready. The listing
        // does not hold the instance, but the instance did not disappear: the launch and its reservation stay.
        await using var h = await RuntimeHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        h.Driver.WhileListing = async () =>
        {
            await h.PostLaunchAsync("qwen", h.Launch("op-1"));
            await h.Operations.WhenIdleAsync();
        };

        await h.Watcher.PollOnceAsync(ct);

        Assert.Equal(OperationState.Running, h.Log.Find("op-1")!.State);
        Assert.Single(h.Reservations.List());
        h.Driver.SetState("qwen", ModelState.Ready);
        await h.Watcher.PollOnceAsync(ct);
        Assert.Equal(OperationState.Succeeded, h.Log.Find("op-1")!.State);
        Assert.Empty(h.Reservations.List());
    }

    /// <summary>
    /// W8 review (MN-128): a read of one instance that started before a watcher round (a launch's look at the existing
    /// instance, a GET) and finishes after it must not take back what that round published (Ready → Starting).
    /// </summary>
    [Fact]
    public async Task Board_AnObservationStartedBeforeAWatcherRound_NeverOverwritesIt()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var starting = h.Driver.Seed("qwen", ModelState.Starting);
        h.Board.Apply([starting], h.Board.BeginObservation());

        long earlier = h.Board.BeginObservation();   // reads Starting…
        var ready = starting with { State = ModelState.Ready };
        h.Board.Apply([ready], h.Board.BeginObservation()); // …a watcher round started later publishes Ready first
        h.Board.ApplyOne("qwen", starting, earlier);   // …then the earlier read arrives

        Assert.Equal(ModelState.Ready, h.Board.Find("qwen")?.State);
        Assert.Equal([ModelState.Starting, ModelState.Ready], h.Events.Of<RuntimeEvent>().Select(e => e.Runtime!.State));
    }

    /// <summary>
    /// W8 review (MN-128): <c>GET /runtimes/{id}</c> answers the driver's live state and applies it to the board, so a
    /// <c>/status</c> taken right after never says less than the answer did (the master took a Ready back to Starting).
    /// </summary>
    [Fact]
    public async Task Get_AppliesTheLiveInstanceToTheBoard_SoStatusIsNeverBehindIt()
    {
        await using var h = await RuntimeHarness.StartAsync();
        var ct = TestContext.Current.CancellationToken;
        h.Driver.Seed("qwen", ModelState.Starting);
        await h.Watcher.PollOnceAsync(ct);
        Assert.Equal(ModelState.Starting, h.Board.Find("qwen")?.State);
        h.Driver.SetState("qwen", ModelState.Ready);

        var answered = await h.Operations.ObserveAsync("qwen", ct);

        Assert.Equal(ModelState.Ready, answered?.State);
        Assert.Equal(ModelState.Ready, h.Board.Find("qwen")?.State);
        Assert.Equal(ModelState.Ready, h.Events.Of<RuntimeEvent>()[^1].Runtime!.State);
        await h.Watcher.PollOnceAsync(ct);
        Assert.Equal(2, h.Events.Of<RuntimeEvent>().Count); // the next round finds nothing new to publish
    }

    [Fact]
    public void Board_ClaimedModel_IsSkippedByListingsUntilReleased()
    {
        var board = new RuntimeStateBoard(new FakeEventBus(TimeProvider.System));
        var ready = new RuntimeStatus { ModelId = "m", State = ModelState.Ready, InstanceId = "c1" };

        using (board.Claim("m"))
        using (board.Claim("m"))
        {
            board.Apply([ready], board.Version);
            Assert.Null(board.Find("m"));
        }

        board.Apply([ready], board.Version);
        Assert.Equal(ModelState.Ready, board.Find("m")?.State);
    }

    [Fact]
    public void SameObservable_IgnoresSinceAndMeasuredMemory()
    {
        var a = new RuntimeStatus { ModelId = "m", State = ModelState.Ready, InstanceId = "c1", Since = DateTimeOffset.UnixEpoch, MeasuredGpuGiB = 1 };

        Assert.True(RuntimeStateBoard.SameObservable(a, a with { Since = DateTimeOffset.UnixEpoch.AddDays(1), MeasuredGpuGiB = 2 }));
        Assert.False(RuntimeStateBoard.SameObservable(a, a with { RestartCount = 1 }));
        Assert.False(RuntimeStateBoard.SameObservable(a, null));
        Assert.True(RuntimeStateBoard.SameObservable(null, null));
    }

    [Fact]
    public async Task HostedLoop_PollsUntilStopped()
    {
        await using var h = await RuntimeHarness.StartAsync();
        h.Driver.Seed("qwen", ModelState.Ready);
        using var watcher = new RuntimeWatcher(h.Operations, h.Board, h.Reservations, TimeProvider.System,
            new AgentRuntimeOptions { WatchInterval = TimeSpan.FromMilliseconds(20) });

        await watcher.StartAsync(TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (h.Board.Find("qwen") is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await watcher.StopAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(h.Board.Find("qwen"));
    }
}
