using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Events;

/// <summary>
/// The agent's event bus (design §4.7), implementing every invariant documented on <see cref="IEventBus"/>:
/// <list type="bullet">
/// <item>One state lock covers a state change, its seq, its append to the ring, its fan-out to subscriber queues and the
/// <see cref="AgentStatusView"/> update; <see cref="Snapshot{T}"/> and <see cref="Subscribe"/> take the same lock, so a
/// snapshot's watermark is exact and a subscription starting from it loses and doubles nothing.</item>
/// <item>The ring keeps the last <see cref="EventBusOptions.RingCapacity"/> events, none older than
/// <see cref="EventBusOptions.RingMaxAge"/>.</item>
/// <item>Each subscriber has a bounded live queue; a full queue ends that subscription with one <see cref="OverflowEvent"/>.
/// Publishing never blocks on a subscriber.</item>
/// </list>
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly Lock _stateLock = new();
    private readonly EventBusOptions _options;
    private readonly TimeProvider _time;
    private readonly AgentStatusView? _view;
    private readonly AgentEvent?[] _ring;
    private readonly List<EventSubscription> _subscribers = [];
    private int _ringHead;
    private int _ringCount;
    private long _seq;

    /// <summary>Creates a bus for one agent boot.</summary>
    /// <param name="bootId">This boot's id; a new GUID when null. MN-026 passes the id <c>/info</c> reports.</param>
    /// <param name="options">Limits; protocol defaults when null.</param>
    /// <param name="time">Clock for event times, ring age and heartbeats; <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="view">Status view kept up to date under the state lock (what <c>GET /status</c> reads); none when null.</param>
    public EventBus(string? bootId = null, EventBusOptions? options = null, TimeProvider? time = null, AgentStatusView? view = null)
    {
        _options = options ?? new EventBusOptions();
        _options.Validate();
        _time = time ?? TimeProvider.System;
        _view = view;
        _ring = new AgentEvent?[_options.RingCapacity];
        BootId = string.IsNullOrEmpty(bootId) ? Guid.NewGuid().ToString("D") : bootId;
    }

    /// <inheritdoc/>
    public string BootId { get; }

    /// <inheritdoc/>
    public long CurrentSeq => Interlocked.Read(ref _seq);

    /// <summary>Live subscribers (diagnostics and tests).</summary>
    public int SubscriberCount
    {
        get
        {
            lock (_stateLock)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>Events currently in the replay ring (diagnostics and tests).</summary>
    public int RingCount
    {
        get
        {
            lock (_stateLock)
            {
                EvictExpired(_time.GetUtcNow());
                return _ringCount;
            }
        }
    }

    internal EventBusOptions Options => _options;

    internal TimeProvider Time => _time;

    /// <inheritdoc/>
    public AgentEvent Publish(AgentEvent evt) => Publish(static () => { }, evt);

    /// <inheritdoc/>
    public AgentEvent Publish(Action stateChange, AgentEvent evt)
    {
        ArgumentNullException.ThrowIfNull(stateChange);
        ArgumentNullException.ThrowIfNull(evt);
        if (evt is HeartbeatEvent or ResetEvent or OverflowEvent)
        {
            throw new ArgumentException(
                $"'{evt.EventType}' events are generated per subscriber and are not state changes; they cannot be published.", nameof(evt));
        }

        lock (_stateLock)
        {
            // A throwing state change publishes nothing and consumes no seq.
            stateChange();

            var now = _time.GetUtcNow();
            long seq = _seq + 1;
            var stamped = evt with { BootId = BootId, Seq = seq, At = now };
            Interlocked.Exchange(ref _seq, seq);

            _view?.Apply(stamped);
            Append(stamped);
            EvictExpired(now);

            for (int i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].Offer(stamped, seq))
                {
                    _subscribers.RemoveAt(i);
                }
            }

            return stamped;
        }
    }

    /// <inheritdoc/>
    public T Snapshot<T>(Func<long, T> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        lock (_stateLock)
        {
            return build(_seq);
        }
    }

    /// <inheritdoc/>
    public IEventSubscription Subscribe(string? bootId, long? afterSeq)
    {
        lock (_stateLock)
        {
            var now = _time.GetUtcNow();
            EvictExpired(now);

            if (bootId is null && afterSeq is null)
            {
                return Register([]);
            }

            if (!string.Equals(bootId, BootId, StringComparison.Ordinal))
            {
                return EventSubscription.Terminal(this, Reset(ResetEvent.BootChanged, now));
            }

            if (afterSeq is not { } after)
            {
                return Register([]);
            }

            if (after == _seq)
            {
                return Register([]);
            }

            if (after > _seq || after < 0 || _ringCount == 0 || RingAt(0).Seq > after + 1)
            {
                return EventSubscription.Terminal(this, Reset(ResetEvent.Evicted, now));
            }

            long first = RingAt(0).Seq;
            int start = checked((int)(after + 1 - first));
            var replay = new AgentEvent[_ringCount - start];
            for (int i = 0; i < replay.Length; i++)
            {
                replay[i] = RingAt(start + i);
            }

            return Register(replay);
        }
    }

    /// <summary>
    /// A heartbeat for <paramref name="subscription"/> carrying the current seq, or null when its queue still holds events
    /// (they are delivered first, so a heartbeat never claims a seq the subscriber has not yet received).
    /// </summary>
    internal HeartbeatEvent? TryCreateHeartbeat(EventSubscription subscription)
    {
        lock (_stateLock)
        {
            return subscription.HasQueuedEvents
                ? null
                : new HeartbeatEvent { BootId = BootId, Seq = _seq, At = _time.GetUtcNow() };
        }
    }

    internal OverflowEvent CreateOverflow(long seq) => new() { BootId = BootId, Seq = seq, At = _time.GetUtcNow() };

    internal void Unregister(EventSubscription subscription)
    {
        lock (_stateLock)
        {
            _subscribers.Remove(subscription);
        }
    }

    private EventSubscription Register(AgentEvent[] replay)
    {
        var subscription = new EventSubscription(this, replay);
        _subscribers.Add(subscription);
        return subscription;
    }

    private ResetEvent Reset(string reason, DateTimeOffset now) => new() { BootId = BootId, Seq = _seq, At = now, Reason = reason };

    private AgentEvent RingAt(int index) => _ring[(_ringHead + index) % _ring.Length]!;

    private void Append(AgentEvent evt)
    {
        if (_ringCount == _ring.Length)
        {
            _ring[_ringHead] = evt;
            _ringHead = (_ringHead + 1) % _ring.Length;
            return;
        }

        _ring[(_ringHead + _ringCount) % _ring.Length] = evt;
        _ringCount++;
    }

    private void EvictExpired(DateTimeOffset now)
    {
        var cutoff = now - _options.RingMaxAge;
        while (_ringCount > 0 && _ring[_ringHead]!.At < cutoff)
        {
            _ring[_ringHead] = null;
            _ringHead = (_ringHead + 1) % _ring.Length;
            _ringCount--;
        }
    }
}
