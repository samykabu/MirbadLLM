using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Events;

/// <summary>
/// One subscriber of <see cref="EventBus"/> (design §4.7): the replayed events taken under the state lock, then the live
/// ones from a bounded queue, with a heartbeat every <see cref="EventBusOptions.HeartbeatInterval"/>. Completes after a
/// <see cref="ResetEvent"/> or an <see cref="OverflowEvent"/>, or when disposed. One reader only.
/// </summary>
internal sealed class EventSubscription : IEventSubscription
{
    private readonly EventBus _bus;
    private readonly AgentEvent[] _replay;
    private readonly ResetEvent? _reset;
    private readonly Channel<AgentEvent> _queue;
    private readonly CancellationTokenSource _disposed = new();
    private OverflowEvent? _overflow;
    private int _readStarted;
    private int _disposeStarted;

    internal EventSubscription(EventBus bus, AgentEvent[] replay, ResetEvent? reset = null)
    {
        _bus = bus;
        _replay = replay;
        _reset = reset;
        _queue = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(bus.Options.SubscriberQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true, // every write happens under the bus's state lock
            FullMode = BoundedChannelFullMode.Wait, // TryWrite then fails instead of dropping silently
        });

        if (reset is not null)
        {
            _queue.Writer.TryComplete();
        }
    }

    /// <summary>True while live events wait in the queue. Called under the bus's state lock.</summary>
    internal bool HasQueuedEvents => _queue.Reader.Count > 0;

    /// <summary>A subscription that yields only <paramref name="reset"/> and completes; it is never registered.</summary>
    internal static EventSubscription Terminal(EventBus bus, ResetEvent reset) => new(bus, [], reset);

    /// <summary>
    /// Queues a live event. Called under the bus's state lock. False when the subscription is finished (overflowed now or
    /// earlier, or disposed): the bus then drops it from its fan-out list.
    /// </summary>
    internal bool Offer(AgentEvent evt, long seq)
    {
        if (Volatile.Read(ref _overflow) is not null || _disposed.IsCancellationRequested)
        {
            return false;
        }

        if (_queue.Writer.TryWrite(evt))
        {
            return true;
        }

        // Queue full: the subscriber is too slow. It gets one overflow event (carrying the seq it missed) and closes.
        Volatile.Write(ref _overflow, _bus.CreateOverflow(seq));
        _queue.Writer.TryComplete();
        return false;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<AgentEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _readStarted, 1) != 0)
        {
            throw new InvalidOperationException("An event subscription has a single reader.");
        }

        foreach (var replayed in _replay)
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed.IsCancellationRequested)
            {
                yield break;
            }

            yield return replayed;
        }

        if (_reset is not null)
        {
            yield return _reset;
            yield break;
        }

        var time = _bus.Time;
        var interval = _bus.Options.HeartbeatInterval;
        var reader = _queue.Reader;
        var nextHeartbeat = time.GetUtcNow() + interval;
        Task<bool>? pendingRead = null;
        Task? pendingDelay = null;

        using var waits = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposed.Token);
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _overflow) is { } overflow)
                {
                    yield return overflow;
                    yield break;
                }

                if (reader.TryRead(out var evt))
                {
                    yield return evt;
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                if (_disposed.IsCancellationRequested)
                {
                    yield break;
                }

                var now = time.GetUtcNow();
                if (now >= nextHeartbeat)
                {
                    // Null when an event slipped into the queue meanwhile: deliver it first, heartbeat on the next pass.
                    if (_bus.TryCreateHeartbeat(this) is { } heartbeat)
                    {
                        nextHeartbeat = now + interval;
                        yield return heartbeat;
                    }

                    continue;
                }

                pendingRead ??= reader.WaitToReadAsync(waits.Token).AsTask();
                pendingDelay ??= Task.Delay(nextHeartbeat - now, time, waits.Token);
                await Task.WhenAny(pendingRead, pendingDelay).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
                if (_disposed.IsCancellationRequested)
                {
                    yield break;
                }

                if (pendingRead.IsCompleted)
                {
                    bool more = pendingRead.IsCompletedSuccessfully && pendingRead.Result;
                    pendingRead = null;
                    if (!more && Volatile.Read(ref _overflow) is null)
                    {
                        yield break; // completed without overflow: disposed
                    }
                }

                if (pendingDelay.IsCompleted)
                {
                    pendingDelay = null;
                }
            }
        }
        finally
        {
            // Stop the outstanding wait and timer; observe them so a cancellation is never unobserved.
            await waits.CancelAsync().ConfigureAwait(false);
            if (pendingRead is not null)
            {
                await pendingRead.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
            }

            if (pendingDelay is not null)
            {
                await pendingDelay.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _bus.Unregister(this);
        _disposed.Cancel();
        _queue.Writer.TryComplete();

        // _disposed is intentionally not disposed: a reader racing this call may still read its token.
        return ValueTask.CompletedTask;
    }
}
