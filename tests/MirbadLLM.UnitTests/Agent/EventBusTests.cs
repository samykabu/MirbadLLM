using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Agent;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// MN-017: the agent event bus (design §4.7) — per-boot seq, replay ring, snapshot watermark, reset, bounded subscriber
/// queues with overflow, heartbeats — and the <c>/status</c> + <c>/events</c> SSE endpoints.
/// </summary>
public sealed class EventBusTests
{
    private const string Boot = "b11ac10b-58cc-4372-a567-0e02b2c3d479";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Publishing ----

    [Fact]
    public void Publish_StampsBootSeqAndTime_InOrder()
    {
        var clock = new ManualClock();
        var bus = new EventBus(Boot, time: clock);

        var first = bus.Publish(Runtime("a"));
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = bus.Publish(Runtime("b"));

        Assert.Equal(Boot, first.BootId);
        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.Equal(clock.GetUtcNow(), second.At);
        Assert.Equal(2, bus.CurrentSeq);
        Assert.Equal(2, bus.RingCount);
    }

    [Fact]
    public void Publish_WithoutBootId_GeneratesOne()
    {
        var bus = new EventBus();

        Assert.True(Guid.TryParse(bus.BootId, out _));
        Assert.Equal(0, bus.CurrentSeq);
    }

    [Theory]
    [InlineData(AgentEventTypes.Heartbeat)]
    [InlineData(AgentEventTypes.Reset)]
    [InlineData(AgentEventTypes.Overflow)]
    public void Publish_RefusesNonStateEvents(string type)
    {
        var bus = new EventBus(Boot);
        AgentEvent evt = type switch
        {
            AgentEventTypes.Heartbeat => new HeartbeatEvent(),
            AgentEventTypes.Reset => new ResetEvent { Reason = ResetEvent.Evicted },
            _ => new OverflowEvent(),
        };

        Assert.Throws<ArgumentException>(() => bus.Publish(evt));
        Assert.Equal(0, bus.CurrentSeq);
    }

    [Fact]
    public void Publish_ThrowingStateChange_PublishesNothing()
    {
        var bus = new EventBus(Boot);
        bus.Publish(Runtime("a"));

        Assert.Throws<InvalidOperationException>(() => bus.Publish(() => throw new InvalidOperationException("boom"), Runtime("b")));

        Assert.Equal(1, bus.CurrentSeq);
        Assert.Equal(1, bus.RingCount);
        Assert.Equal(2, bus.Publish(Runtime("c")).Seq);
    }

    [Fact]
    public void Snapshot_SeesTheStateChangeAndItsSeqTogether()
    {
        var bus = new EventBus(Boot);
        var applied = new List<string>();

        bus.Publish(() => applied.Add("a"), Runtime("a"));
        bus.Publish(() => applied.Add("b"), Runtime("b"));
        var (seq, state) = bus.Snapshot(s => (s, applied.ToArray()));

        Assert.Equal(2, seq);
        Assert.Equal(["a", "b"], state);
    }

    // ---- Snapshot + subscribe under concurrent publishing ----

    [Fact]
    public async Task SnapshotThenSubscribe_UnderConcurrentPublishing_LosesAndDoublesNothing()
    {
        const int publishers = 4;
        const int perPublisher = 750;
        const int total = publishers * perPublisher;
        var view = new AgentStatusView();
        var bus = new EventBus(Boot, new EventBusOptions { SubscriberQueueCapacity = total }, view: view);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var memory = Memory();
        var health = new NodeHealth();

        using var start = new ManualResetEventSlim();
        var publishing = Enumerable.Range(0, publishers).Select(p => Task.Run(() =>
        {
            start.Wait(timeout.Token);
            for (int i = 0; i < perPublisher; i++)
            {
                bus.Publish(Runtime($"m-{p}-{i}"));
            }
        })).ToArray();

        var checks = new List<Task>();
        start.Set();
        long lastWatermark = -1;
        while (!publishing.All(t => t.IsCompleted) || checks.Count < 3)
        {
            var snapshot = bus.Snapshot(seq => view.Build(bus.BootId, seq, DateTimeOffset.UtcNow, health, memory));
            if (snapshot.Seq == lastWatermark && !publishing.All(t => t.IsCompleted))
            {
                await Task.Yield();
                continue;
            }

            lastWatermark = snapshot.Seq;
            var subscription = bus.Subscribe(snapshot.BootId, snapshot.Seq);
            checks.Add(VerifyAsync(snapshot, subscription));
            if (checks.Count > 40)
            {
                break;
            }

            await Task.Delay(1, timeout.Token);
        }

        await Task.WhenAll(publishing);
        await Task.WhenAll(checks).WaitAsync(timeout.Token);
        Assert.Equal(total, bus.CurrentSeq);
        Assert.True(checks.Count >= 3);

        async Task VerifyAsync(StatusSnapshot snapshot, IEventSubscription subscription)
        {
            await using var _ = subscription;
            var fromSnapshot = snapshot.Runtimes.Select(r => r.ModelId).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(snapshot.Seq, fromSnapshot.Count); // one runtime per event: the watermark is exact

            var fromEvents = new List<string>();
            long expected = snapshot.Seq + 1;
            if (snapshot.Seq < total)
            {
                await foreach (var evt in subscription.ReadAllAsync(timeout.Token))
                {
                    if (evt is HeartbeatEvent)
                    {
                        continue;
                    }

                    var runtime = Assert.IsType<RuntimeEvent>(evt);
                    Assert.Equal(expected, runtime.Seq);
                    expected++;
                    fromEvents.Add(runtime.ModelId);
                    if (runtime.Seq == total)
                    {
                        break;
                    }
                }
            }

            Assert.Empty(fromSnapshot.Intersect(fromEvents));
            Assert.Equal(total, fromSnapshot.Count + fromEvents.Count);
            Assert.Equal(total, fromSnapshot.Union(fromEvents).Count());
        }
    }

    // ---- Replay and reset ----

    [Fact]
    public async Task Subscribe_WithCursorInRing_ReplaysThenGoesLive()
    {
        var bus = new EventBus(Boot);
        for (int i = 0; i < 5; i++)
        {
            bus.Publish(Runtime($"m{i}"));
        }

        await using var subscription = bus.Subscribe(Boot, 2);
        await using var reader = subscription.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);

        Assert.Equal(3, (await NextAsync(reader)).Seq);
        Assert.Equal(4, (await NextAsync(reader)).Seq);
        Assert.Equal(5, (await NextAsync(reader)).Seq);

        var next = reader.MoveNextAsync().AsTask();
        bus.Publish(Runtime("live"));
        Assert.True(await next.WaitAsync(Wait, Ct));
        Assert.Equal(6, reader.Current.Seq);
        Assert.Equal("live", Assert.IsType<RuntimeEvent>(reader.Current).ModelId);
    }

    [Fact]
    public async Task Subscribe_AtCurrentSeq_IsLiveOnly()
    {
        var bus = new EventBus(Boot);
        bus.Publish(Runtime("a"));

        await using var subscription = bus.Subscribe(Boot, 1);
        await using var reader = subscription.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);
        var next = reader.MoveNextAsync().AsTask();
        Assert.False(next.IsCompleted);

        bus.Publish(Runtime("b"));
        Assert.True(await next.WaitAsync(Wait, Ct));
        Assert.Equal(2, reader.Current.Seq);
    }

    [Fact]
    public async Task Subscribe_FromSeqZero_ReplaysEverythingWhileTheRingHoldsIt()
    {
        var bus = new EventBus(Boot);
        bus.Publish(Runtime("a"));
        bus.Publish(Runtime("b"));

        await using var subscription = bus.Subscribe(Boot, 0);
        await using var reader = subscription.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);

        Assert.Equal(1, (await NextAsync(reader)).Seq);
        Assert.Equal(2, (await NextAsync(reader)).Seq);
    }

    [Theory]
    [InlineData("0e9d1f7a-0000-4000-8000-000000000000", 1L)]
    [InlineData(null, 1L)]
    public async Task Subscribe_OtherBoot_YieldsOneResetAndCompletes(string? bootId, long afterSeq)
    {
        var bus = new EventBus(Boot);
        bus.Publish(Runtime("a"));
        bus.Publish(Runtime("b"));

        var events = await ReadAllAsync(bus.Subscribe(bootId, afterSeq));

        var reset = Assert.IsType<ResetEvent>(Assert.Single(events));
        Assert.Equal(ResetEvent.BootChanged, reset.Reason);
        Assert.Equal(Boot, reset.BootId);
        Assert.Equal(2, reset.Seq);
        Assert.Equal(0, bus.SubscriberCount);
    }

    [Fact]
    public async Task Subscribe_EvictedByCapacity_YieldsReset()
    {
        var bus = new EventBus(Boot, new EventBusOptions { RingCapacity = 5 });
        for (int i = 0; i < 8; i++)
        {
            bus.Publish(Runtime($"m{i}"));
        }

        // Ring now holds 4..8: cursor 3 replays from 4, cursor 2 needs 3 (evicted).
        var replayed = await ReadUntilAsync(bus.Subscribe(Boot, 3), 8);
        Assert.Equal([4L, 5L, 6L, 7L, 8L], replayed.Select(e => e.Seq));

        var reset = Assert.IsType<ResetEvent>(Assert.Single(await ReadAllAsync(bus.Subscribe(Boot, 2))));
        Assert.Equal(ResetEvent.Evicted, reset.Reason);
        Assert.Equal(8, reset.Seq);
    }

    [Fact]
    public async Task Subscribe_EvictedByAge_YieldsReset()
    {
        var clock = new ManualClock();
        var bus = new EventBus(Boot, time: clock);
        bus.Publish(Runtime("a"));
        bus.Publish(Runtime("b"));
        clock.Advance(Protocol.EventRingMaxAge + TimeSpan.FromSeconds(1));
        bus.Publish(Runtime("c"));

        Assert.Equal(1, bus.RingCount);
        Assert.Equal([3L], (await ReadUntilAsync(bus.Subscribe(Boot, 2), 3)).Select(e => e.Seq));
        var reset = Assert.IsType<ResetEvent>(Assert.Single(await ReadAllAsync(bus.Subscribe(Boot, 1))));
        Assert.Equal(ResetEvent.Evicted, reset.Reason);

        // Ageing also applies without new events.
        clock.Advance(Protocol.EventRingMaxAge + TimeSpan.FromSeconds(1));
        Assert.Equal(0, bus.RingCount);
        Assert.IsType<ResetEvent>(Assert.Single(await ReadAllAsync(bus.Subscribe(Boot, 2))));
    }

    [Theory]
    [InlineData(3L)]
    [InlineData(-1L)]
    public async Task Subscribe_ImpossibleCursor_YieldsReset(long afterSeq)
    {
        var bus = new EventBus(Boot);
        bus.Publish(Runtime("a"));

        var reset = Assert.IsType<ResetEvent>(Assert.Single(await ReadAllAsync(bus.Subscribe(Boot, afterSeq))));
        Assert.Equal(ResetEvent.Evicted, reset.Reason);
    }

    [Fact]
    public async Task Subscribe_NoEventsYet_AfterSeqZeroIsLive()
    {
        var bus = new EventBus(Boot);

        await using var subscription = bus.Subscribe(Boot, 0);
        await using var reader = subscription.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);
        var next = reader.MoveNextAsync().AsTask();
        bus.Publish(Runtime("a"));

        Assert.True(await next.WaitAsync(Wait, Ct));
        Assert.Equal(1, reader.Current.Seq);
    }

    // ---- Overflow ----

    [Fact]
    public async Task SlowSubscriber_GetsOverflowAndIsClosed_FastOneKeepsEverything()
    {
        var bus = new EventBus(Boot);
        var slow = bus.Subscribe(null, null);
        await using var fast = bus.Subscribe(null, null);
        await using var fastReader = fast.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);
        var fastSeen = new List<long>();

        for (int i = 0; i < 600; i++)
        {
            bus.Publish(Runtime($"m{i}"));
        }

        for (int i = 0; i < 600; i++)
        {
            fastSeen.Add((await NextAsync(fastReader)).Seq);
        }

        Assert.Equal(2, bus.SubscriberCount);
        for (int i = 600; i < Protocol.EventSubscriberQueueCapacity; i++)
        {
            bus.Publish(Runtime($"m{i}"));
        }

        Assert.Equal(2, bus.SubscriberCount); // exactly full is not an overflow
        bus.Publish(Runtime("one-too-many"));
        Assert.Equal(1, bus.SubscriberCount);

        for (int i = 600; i <= Protocol.EventSubscriberQueueCapacity; i++)
        {
            fastSeen.Add((await NextAsync(fastReader)).Seq);
        }

        Assert.Equal(Enumerable.Range(1, Protocol.EventSubscriberQueueCapacity + 1).Select(i => (long)i), fastSeen);

        // The slow one gets the overflow at once (not after its 1,000 queued events) and then completes.
        var slowEvents = await ReadAllAsync(slow);
        var overflow = Assert.IsType<OverflowEvent>(Assert.Single(slowEvents));
        Assert.Equal(Protocol.EventSubscriberQueueCapacity + 1, overflow.Seq);
        Assert.Equal(Boot, overflow.BootId);

        // Later events do not resurrect it.
        bus.Publish(Runtime("after"));
        Assert.Equal(1, bus.SubscriberCount);
    }

    [Fact]
    public async Task Overflow_WakesAWaitingReader()
    {
        var bus = new EventBus(Boot, new EventBusOptions { SubscriberQueueCapacity = 2 });
        await using var subscription = bus.Subscribe(null, null);
        await using var reader = subscription.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);

        // The reader takes one event, then the bus outruns it.
        var first = reader.MoveNextAsync().AsTask();
        bus.Publish(Runtime("a"));
        Assert.True(await first.WaitAsync(Wait, Ct));
        for (int i = 0; i < 3; i++)
        {
            bus.Publish(Runtime($"m{i}"));
        }

        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(Wait, Ct));
        Assert.IsType<OverflowEvent>(reader.Current);
        Assert.False(await reader.MoveNextAsync().AsTask().WaitAsync(Wait, Ct));
    }

    // ---- Heartbeats and disposal ----

    [Fact]
    public async Task Heartbeat_EveryFiveSeconds_CarriesCurrentSeq_NotStored()
    {
        var clock = new ManualClock();
        var bus = new EventBus(Boot, time: clock);
        await using var subscription = bus.Subscribe(null, null);
        await using var reader = subscription.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);

        var next = reader.MoveNextAsync().AsTask();
        await clock.WaitForTimersAsync(1);
        clock.Advance(Protocol.HeartbeatInterval - TimeSpan.FromMilliseconds(100));
        await Task.Delay(50, Ct);
        Assert.False(next.IsCompleted);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.True(await next.WaitAsync(Wait, Ct));
        var heartbeat = Assert.IsType<HeartbeatEvent>(reader.Current);
        Assert.Equal(0, heartbeat.Seq);
        Assert.Equal(Boot, heartbeat.BootId);
        Assert.Equal(clock.GetUtcNow(), heartbeat.At);

        bus.Publish(Runtime("a"));
        Assert.Equal(1, (await NextAsync(reader)).Seq);

        next = reader.MoveNextAsync().AsTask();
        await clock.WaitForTimersAsync(1);
        clock.Advance(Protocol.HeartbeatInterval);
        Assert.True(await next.WaitAsync(Wait, Ct));
        Assert.Equal(1, Assert.IsType<HeartbeatEvent>(reader.Current).Seq);

        // Heartbeats are not in the ring: a replay from 0 yields only the state change.
        Assert.Equal(1, bus.RingCount);
        Assert.Equal([1L], (await ReadUntilAsync(bus.Subscribe(Boot, 0), 1)).Select(e => e.Seq));
    }

    [Fact]
    public async Task Heartbeat_WithBusyStream_StillComesOnTime()
    {
        var clock = new ManualClock();
        var bus = new EventBus(Boot, time: clock);
        await using var subscription = bus.Subscribe(null, null);
        await using var reader = subscription.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);

        // An event every second: the heartbeat still arrives after 5 s of stream time.
        var types = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            bus.Publish(Runtime($"m{i}"));
            types.Add((await NextAsync(reader)).EventType);
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        bus.Publish(Runtime("last"));
        types.Add((await NextAsync(reader)).EventType);
        types.Add((await NextAsync(reader)).EventType);

        Assert.Contains(AgentEventTypes.Heartbeat, types);
    }

    [Fact]
    public async Task Dispose_CompletesAWaitingReader_AndUnsubscribes()
    {
        var bus = new EventBus(Boot);
        var subscription = bus.Subscribe(null, null);
        var reader = subscription.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);
        var next = reader.MoveNextAsync().AsTask();
        Assert.Equal(1, bus.SubscriberCount);

        await subscription.DisposeAsync();

        Assert.False(await next.WaitAsync(Wait, Ct));
        Assert.Equal(0, bus.SubscriberCount);
        await reader.DisposeAsync();
        bus.Publish(Runtime("a")); // no subscriber, no error
    }

    [Fact]
    public async Task Cancellation_EndsTheReadWithOperationCanceled()
    {
        var bus = new EventBus(Boot);
        await using var subscription = bus.Subscribe(null, null);
        using var cts = new CancellationTokenSource();
        await using var reader = subscription.ReadAllAsync(cts.Token).GetAsyncEnumerator(Ct);
        var next = reader.MoveNextAsync().AsTask();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(Wait, Ct));
    }

    [Fact]
    public async Task Subscription_HasASingleReader()
    {
        var bus = new EventBus(Boot);
        var subscription = bus.Subscribe(null, null);
        var first = subscription.ReadAllAsync(Ct).GetAsyncEnumerator(Ct);
        var pending = first.MoveNextAsync().AsTask();

        await using (var second = subscription.ReadAllAsync(Ct).GetAsyncEnumerator(Ct))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask());
        }

        await subscription.DisposeAsync();
        Assert.False(await pending.WaitAsync(Wait, Ct));
        await first.DisposeAsync();
    }

    // ---- Status view ----

    [Fact]
    public void StatusView_FollowsEvents()
    {
        var view = new AgentStatusView();
        var bus = new EventBus(Boot, view: view);
        var t0 = DateTimeOffset.Parse("2026-10-03T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        bus.Publish(Runtime("a"));
        bus.Publish(Runtime("b"));
        bus.Publish(new RuntimeEvent { ModelId = "a", Runtime = null });
        bus.Publish(new ReservationEvent { Reservation = Reservation("op-1", t0) });
        bus.Publish(new ReservationEvent { Reservation = Reservation("op-2", t0.AddSeconds(1)) });
        bus.Publish(new ReservationEvent { Reservation = Reservation("op-1", t0), Released = true });
        bus.Publish(new DownloadEvent { Download = Download("f1", DownloadState.Downloading) });
        bus.Publish(new DownloadEvent { Download = Download("f1", DownloadState.Completed) });
        bus.Publish(new OperationEvent { Operation = Operation("op-2", OperationState.Running, t0) });

        var snapshot = bus.Snapshot(seq => view.Build(bus.BootId, seq, t0, new NodeHealth(), Memory()));

        Assert.Equal(9, snapshot.Seq);
        Assert.Equal(Boot, snapshot.BootId);
        Assert.Equal(["b"], snapshot.Runtimes.Select(r => r.ModelId));
        Assert.Equal(["op-2"], snapshot.Reservations.Select(r => r.OperationId));
        Assert.Equal(DownloadState.Completed, Assert.Single(snapshot.Downloads).State);
        Assert.Equal(OperationState.Running, Assert.Single(snapshot.Operations).State);
    }

    [Fact]
    public void StatusView_ListsActiveOperationsThenTheLast100Terminal()
    {
        var view = new AgentStatusView();
        var bus = new EventBus(Boot, view: view);
        var t0 = DateTimeOffset.Parse("2026-10-03T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        bus.Publish(new OperationEvent { Operation = Operation("active-2", OperationState.Running, t0.AddMinutes(2)) });
        bus.Publish(new OperationEvent { Operation = Operation("active-1", OperationState.Accepted, t0.AddMinutes(1)) });
        for (int i = 0; i < 105; i++)
        {
            bus.Publish(new OperationEvent { Operation = Operation($"done-{i:D3}", OperationState.Running, t0) });
            bus.Publish(new OperationEvent { Operation = Operation($"done-{i:D3}", OperationState.Succeeded, t0) });
        }

        // A terminal operation updated again moves to the front, not duplicated.
        bus.Publish(new OperationEvent { Operation = Operation("done-104", OperationState.Succeeded, t0) with { Stage = "again" } });

        var operations = bus.Snapshot(seq => view.Build(bus.BootId, seq, t0, new NodeHealth(), Memory())).Operations;

        Assert.Equal(2 + OperationRetention.StatusRecentCount, operations.Count);
        Assert.Equal(["active-1", "active-2"], operations.Take(2).Select(o => o.Id));
        Assert.Equal("done-104", operations[2].Id);
        Assert.Equal("again", operations[2].Stage);
        Assert.Equal("done-103", operations[3].Id);
        Assert.Equal("done-005", operations[^1].Id);
        Assert.Equal(operations.Count, operations.Select(o => o.Id).Distinct().Count());
    }

    [Fact]
    public void StatusView_ALateNonTerminalUpdate_NeverReopensAFinishedOperation()
    {
        var view = new AgentStatusView();
        var bus = new EventBus(Boot, view: view);
        var t0 = DateTimeOffset.Parse("2026-10-03T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        bus.Publish(new OperationEvent { Operation = Operation("pull", OperationState.Running, t0) });
        bus.Publish(new OperationEvent { Operation = Operation("pull", OperationState.Cancelled, t0) });
        bus.Publish(new OperationEvent { Operation = Operation("pull", OperationState.Running, t0) with { Stage = "late progress" } });

        var operation = Assert.Single(bus.Snapshot(seq => view.Build(bus.BootId, seq, t0, new NodeHealth(), Memory())).Operations);
        Assert.Equal(OperationState.Cancelled, operation.State);
    }

    [Fact]
    public void StatusView_Seed_LoadsStartupStateWithoutASeq()
    {
        var view = new AgentStatusView();
        var bus = new EventBus(Boot, view: view);
        var t0 = DateTimeOffset.Parse("2026-10-03T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        view.Seed(bus,
            runtimes: [new RuntimeStatus { ModelId = "adopted", State = ModelState.Ready }],
            operations: [Operation("old", OperationState.Failed, t0), Operation("open", OperationState.Running, t0)],
            reservations: [Reservation("open", t0)]);

        var snapshot = bus.Snapshot(seq => view.Build(bus.BootId, seq, t0, new NodeHealth(), Memory()));
        Assert.Equal(0, snapshot.Seq);
        Assert.Equal(["adopted"], snapshot.Runtimes.Select(r => r.ModelId));
        Assert.Equal(["open", "old"], snapshot.Operations.Select(o => o.Id));
        Assert.Single(snapshot.Reservations);
    }

    // ---- SSE and /status over HTTP ----

    [Fact]
    public async Task Sse_FramesEachEventAsEventNamePlusOneJsonDataLine()
    {
        await using var host = await AgentHost.StartAsync();
        using var response = await host.OpenEventsAsync("");
        var reader = new SseReader(await response.Content.ReadAsStreamAsync(Ct));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(AgentJson.EventStreamContentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());

        await host.WaitForSubscribersAsync(1);
        host.Bus.Publish(Runtime("qwen"));
        host.Bus.Publish(new OperationEvent { Operation = Operation("op-1", OperationState.Running, DateTimeOffset.UnixEpoch) });

        var frame = await reader.NextAsync();
        Assert.Equal(["event: runtime", $"data: {frame.Data}"], frame.Lines);
        using (var json = JsonDocument.Parse(frame.Data))
        {
            Assert.Equal("runtime", json.RootElement.GetProperty("type").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("seq").GetInt64());
            Assert.Equal(Boot, json.RootElement.GetProperty("bootId").GetString());
            Assert.False(json.RootElement.TryGetProperty("eventType", out _));
        }

        var runtime = Assert.IsType<RuntimeEvent>(frame.Event);
        Assert.Equal("qwen", runtime.ModelId);
        Assert.Equal(ModelState.Ready, runtime.Runtime?.State);

        var second = await reader.NextAsync();
        Assert.Equal("operation", second.Name);
        Assert.Equal(2, Assert.IsType<OperationEvent>(second.Event).Seq);
    }

    [Fact]
    public async Task Sse_StatusThenEvents_ReplaysExactlyWhatFollowsTheWatermark()
    {
        await using var host = await AgentHost.StartAsync();
        host.Bus.Publish(Runtime("a"));
        host.Bus.Publish(Runtime("b"));

        var snapshot = await host.Client.GetFromJsonAsync<StatusSnapshot>(new Uri("/agent/v1/status", UriKind.Relative), AgentJson.Options, Ct);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.Seq);
        Assert.Equal(Boot, snapshot.BootId);
        Assert.Equal(["a", "b"], snapshot.Runtimes.Select(r => r.ModelId));
        Assert.Equal(64, snapshot.Memory.HostTotalGiB);
        Assert.Equal(
            [HealthReasonCodes.DockerUnreachable, HealthReasonCodes.ModelRootLow],
            snapshot.Health.Reasons.Select(r => r.Code)); // union, duplicate removed, registration order

        host.Bus.Publish(Runtime("c")); // after the snapshot, before the subscription: must be replayed
        using var response = await host.OpenEventsAsync($"?bootId={Boot}&afterSeq={snapshot.Seq}");
        var reader = new SseReader(await response.Content.ReadAsStreamAsync(Ct));

        var replayed = await reader.NextAsync();
        Assert.Equal(3, replayed.Event.Seq);
        Assert.Equal("c", Assert.IsType<RuntimeEvent>(replayed.Event).ModelId);

        host.Bus.Publish(Runtime("d"));
        Assert.Equal(4, (await reader.NextAsync()).Event.Seq);
    }

    [Fact]
    public async Task Sse_Reconnect_WithSameBoot_Replays_WithOtherBoot_ResetsAndCloses()
    {
        await using var host = await AgentHost.StartAsync();
        for (int i = 0; i < 4; i++)
        {
            host.Bus.Publish(Runtime($"m{i}"));
        }

        // Reconnect after applying seq 2: 3 and 4 are replayed.
        using (var response = await host.OpenEventsAsync($"?bootId={Boot}&afterSeq=2"))
        {
            var reader = new SseReader(await response.Content.ReadAsStreamAsync(Ct));
            Assert.Equal(3, (await reader.NextAsync()).Event.Seq);
            Assert.Equal(4, (await reader.NextAsync()).Event.Seq);
        }

        // The agent restarted (other boot): one reset, then the stream ends.
        using (var response = await host.OpenEventsAsync("?bootId=previous-boot&afterSeq=2"))
        {
            var reader = new SseReader(await response.Content.ReadAsStreamAsync(Ct));
            var frame = await reader.NextAsync();
            Assert.Equal("reset", frame.Name);
            var reset = Assert.IsType<ResetEvent>(frame.Event);
            Assert.Equal(ResetEvent.BootChanged, reset.Reason);
            Assert.Equal(4, reset.Seq);
            Assert.Null(await reader.TryNextAsync());
        }

        // A cursor ahead of the agent is not replayable either.
        using (var response = await host.OpenEventsAsync($"?bootId={Boot}&afterSeq=99"))
        {
            var reader = new SseReader(await response.Content.ReadAsStreamAsync(Ct));
            Assert.Equal(ResetEvent.Evicted, Assert.IsType<ResetEvent>((await reader.NextAsync()).Event).Reason);
            Assert.Null(await reader.TryNextAsync());
        }

        // The first stream was closed by the client; the server notices that asynchronously (request abort), and the
        // reset streams unsubscribe after their last frame, so wait instead of reading the count once (MN-094).
        await host.WaitForSubscribersAsync(0);
    }

    [Fact]
    public async Task Sse_Overflow_IsFramedAndClosesTheStream()
    {
        var bus = new ScriptedBus(
            new RuntimeEvent { BootId = Boot, Seq = 7, ModelId = "a" },
            new OverflowEvent { BootId = Boot, Seq = 1_008 });
        await using var host = await AgentHost.StartAsync(bus);
        using var response = await host.OpenEventsAsync("");
        var reader = new SseReader(await response.Content.ReadAsStreamAsync(Ct));

        Assert.Equal("runtime", (await reader.NextAsync()).Name);
        var frame = await reader.NextAsync();
        Assert.Equal(["event: overflow", $"data: {frame.Data}"], frame.Lines);
        Assert.Equal(1_008, Assert.IsType<OverflowEvent>(frame.Event).Seq);
        Assert.Null(await reader.TryNextAsync());
        Assert.True(bus.Disposed);
    }

    [Fact]
    public async Task Sse_ClientDisconnect_Unsubscribes()
    {
        await using var host = await AgentHost.StartAsync();
        var response = await host.OpenEventsAsync("");
        await host.WaitForSubscribersAsync(1);

        response.Dispose();

        await host.WaitForSubscribersAsync(0);
    }

    [Fact]
    public async Task Sse_Heartbeat_IsFramed()
    {
        await using var host = await AgentHost.StartAsync(options: new EventBusOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(50) });
        host.Bus.Publish(Runtime("a"));
        using var response = await host.OpenEventsAsync("");
        var reader = new SseReader(await response.Content.ReadAsStreamAsync(Ct));

        var frame = await reader.NextAsync();
        Assert.Equal(["event: heartbeat", $"data: {frame.Data}"], frame.Lines);
        Assert.Equal(1, Assert.IsType<HeartbeatEvent>(frame.Event).Seq);
    }

    [Theory]
    [InlineData("?afterSeq=abc")]
    [InlineData("?afterSeq=-1")]
    public async Task Sse_BadCursor_Is400InvalidRequest(string query)
    {
        await using var host = await AgentHost.StartAsync();

        using var response = await host.Client.GetAsync(new Uri("/agent/v1/events" + query, UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<AgentErrorResponse>(AgentJson.Options, Ct);
        Assert.Equal(ErrorCodes.InvalidRequest, error?.Error.Code);
    }

    [Fact]
    public void Sse_Format_KeepsMultiLineStringsOnOneDataLine()
    {
        var evt = new RuntimeEvent
        {
            BootId = Boot,
            Seq = 1,
            ModelId = "a",
            Runtime = new RuntimeStatus { ModelId = "a", State = ModelState.Failed, Error = "line 1\nline 2\r\nline 3" },
        };

        string frame = AgentEventSse.Format(evt);

        Assert.EndsWith("\n\n", frame, StringComparison.Ordinal);
        var lines = frame.TrimEnd('\n').Split('\n');
        Assert.Equal(2, lines.Length);
        var parsed = Assert.IsType<RuntimeEvent>(JsonSerializer.Deserialize<AgentEvent>(lines[1]["data: ".Length..], AgentJson.Options));
        Assert.Equal("line 1\nline 2\r\nline 3", parsed.Runtime?.Error);
    }

    // ---- Helpers ----

    private static RuntimeEvent Runtime(string modelId) => new()
    {
        ModelId = modelId,
        Runtime = new RuntimeStatus { ModelId = modelId, State = ModelState.Ready },
    };

    private static ReservationInfo Reservation(string operationId, DateTimeOffset at) => new()
    {
        OperationId = operationId,
        ModelId = "m",
        BudgetGiB = 10,
        CreatedAt = at,
    };

    private static NodeDownloadStatus Download(string folder, DownloadState state) => new()
    {
        Folder = folder,
        RepoId = "org/repo",
        State = state,
    };

    private static OperationStatus Operation(string id, OperationState state, DateTimeOffset startedAt) => new()
    {
        Id = id,
        Kind = OperationKinds.Launch,
        Target = "m",
        PayloadDigest = "digest",
        State = state,
        StartedAt = startedAt,
        EndedAt = state.IsTerminal() ? startedAt : null,
    };

    private static NodeMemorySnapshot Memory() => new() { Model = MemoryModel.Unified, HostTotalGiB = 64, HostAvailableGiB = 40 };

    private static async Task<AgentEvent> NextAsync(IAsyncEnumerator<AgentEvent> reader)
    {
        Assert.True(await reader.MoveNextAsync().AsTask().WaitAsync(Wait, Ct));
        return reader.Current;
    }

    /// <summary>Reads a subscription to completion (reset/overflow subscriptions complete by themselves).</summary>
    private static async Task<List<AgentEvent>> ReadAllAsync(IEventSubscription subscription)
    {
        await using var _ = subscription;
        var events = new List<AgentEvent>();
        using var cts = new CancellationTokenSource(Wait);
        await foreach (var evt in subscription.ReadAllAsync(cts.Token))
        {
            events.Add(evt);
        }

        return events;
    }

    /// <summary>Reads until the event with <paramref name="lastSeq"/>, skipping heartbeats.</summary>
    private static async Task<List<AgentEvent>> ReadUntilAsync(IEventSubscription subscription, long lastSeq)
    {
        await using var _ = subscription;
        var events = new List<AgentEvent>();
        using var cts = new CancellationTokenSource(Wait);
        await foreach (var evt in subscription.ReadAllAsync(cts.Token))
        {
            Assert.IsNotType<ResetEvent>(evt);
            if (evt is HeartbeatEvent)
            {
                continue;
            }

            events.Add(evt);
            if (evt.Seq >= lastSeq)
            {
                break;
            }
        }

        return events;
    }
}

/// <summary>An in-process agent with only the status endpoints mapped under <c>/agent/v1</c>.</summary>
file sealed class AgentHost : IAsyncDisposable
{
    private const string Boot = "b11ac10b-58cc-4372-a567-0e02b2c3d479";
    private readonly WebApplication _app;

    private AgentHost(WebApplication app, EventBus bus)
    {
        _app = app;
        Bus = bus;
        Client = app.GetTestClient();
    }

    public EventBus Bus { get; }

    public HttpClient Client { get; }

    public static async Task<AgentHost> StartAsync(IEventBus? replaceBus = null, EventBusOptions? options = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAgentEvents(Boot, options);
        if (replaceBus is not null)
        {
            builder.Services.AddSingleton<IEventBus>(replaceBus);
        }

        builder.Services.AddSingleton<INodeMemoryReader>(new FixedMemoryReader());
        builder.Services.AddSingleton<IHealthContributor>(new FixedHealth(
            new HealthReason(HealthReasonCodes.DockerUnreachable, "Docker is unreachable."),
            new HealthReason(HealthReasonCodes.ModelRootLow, "Model root below 5 %.")));
        builder.Services.AddSingleton<IHealthContributor>(new FixedHealth(
            new HealthReason(HealthReasonCodes.ModelRootLow, "Model root below 5 %.")));

        var app = builder.Build();
        app.MapGroup(Protocol.BasePath).MapStatusEndpoints();
        await app.StartAsync();
        return new AgentHost(app, app.Services.GetRequiredService<EventBus>());
    }

    public async Task<HttpResponseMessage> OpenEventsAsync(string query)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await Client.GetAsync(new Uri("/agent/v1/events" + query, UriKind.Relative), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.EnsureSuccessStatusCode();
        return response;
    }

    public async Task WaitForSubscribersAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Bus.SubscriberCount != count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"Expected {count} subscriber(s), have {Bus.SubscriberCount}.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>One parsed SSE frame.</summary>
file sealed record SseFrame(string Name, string Data, IReadOnlyList<string> Lines, AgentEvent Event);

/// <summary>Reads SSE frames: lines up to a blank line; checks the event name matches the JSON discriminator.</summary>
#pragma warning disable CA1001 // the StreamReader wraps the response stream, which the test's HttpResponseMessage owns and disposes.
file sealed class SseReader(Stream stream)
{
    private readonly StreamReader _reader = new(stream);

    public async Task<SseFrame> NextAsync() =>
        await TryNextAsync() ?? throw new InvalidOperationException("The event stream ended.");

    public async Task<SseFrame?> TryNextAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        while (true)
        {
            string? line = await _reader.ReadLineAsync(cts.Token);
            if (line is null)
            {
                Assert.Empty(lines); // never a truncated frame
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            lines.Add(line);
        }

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("event: ", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("data: ", lines[1], StringComparison.Ordinal);
        string name = lines[0]["event: ".Length..];
        string data = lines[1]["data: ".Length..];
        var evt = JsonSerializer.Deserialize<AgentEvent>(data, AgentJson.Options)!;
        Assert.Equal(name, evt.EventType);
        return new SseFrame(name, data, lines, evt);
    }
}
#pragma warning restore CA1001

file sealed class FixedMemoryReader : INodeMemoryReader
{
    public Task<NodeMemorySnapshot> ReadAsync(CancellationToken ct = default) =>
        Task.FromResult(new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 64, HostAvailableGiB = 40 });
}

file sealed class FixedHealth(params HealthReason[] reasons) : IHealthContributor
{
    public Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HealthReason>>(reasons);
}

/// <summary>An <see cref="IEventBus"/> whose every subscription yields a fixed script and completes.</summary>
file sealed class ScriptedBus(params AgentEvent[] script) : IEventBus
{
    public bool Disposed { get; set; }

    public string BootId => "b11ac10b-58cc-4372-a567-0e02b2c3d479";

    public long CurrentSeq => 0;

    public AgentEvent Publish(AgentEvent evt) => throw new NotSupportedException();

    public AgentEvent Publish(Action stateChange, AgentEvent evt) => throw new NotSupportedException();

    public T Snapshot<T>(Func<long, T> build) => build(0);

    public IEventSubscription Subscribe(string? bootId, long? afterSeq) => new ScriptedSubscription(this, script);
}

file sealed class ScriptedSubscription(ScriptedBus bus, AgentEvent[] script) : IEventSubscription
{
    public async IAsyncEnumerable<AgentEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var evt in script)
        {
            await Task.Yield();
            yield return evt;
        }
    }

    public ValueTask DisposeAsync()
    {
        bus.Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A <see cref="TimeProvider"/> whose clock and timers move only on <see cref="Advance"/>.</summary>
file sealed class ManualClock : TimeProvider
{
    private readonly Lock _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt <= _now).ToList();
            foreach (var timer in due)
            {
                if (timer.Period > TimeSpan.Zero)
                {
                    timer.DueAt = _now + timer.Period;
                }
                else
                {
                    _timers.Remove(timer);
                }
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public async Task WaitForTimersAsync(int count)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            lock (_lock)
            {
                if (_timers.Count >= count)
                {
                    return;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, "Timer was not scheduled.");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                return;
            }

            timer.DueAt = _now + dueTime;
            timer.Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            _timers.Add(timer);
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public TimeSpan Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock.Schedule(this, dueTime, period);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => clock.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
