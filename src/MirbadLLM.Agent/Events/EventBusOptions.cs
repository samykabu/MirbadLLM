using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Events;

/// <summary>
/// Limits and timings of <see cref="EventBus"/> (design §4.7). The defaults are the protocol values in
/// <see cref="Protocol"/>; tests shrink them.
/// </summary>
public sealed record EventBusOptions
{
    /// <summary>Events kept in the replay ring (<see cref="Protocol.EventRingCapacity"/>).</summary>
    public int RingCapacity { get; init; } = Protocol.EventRingCapacity;

    /// <summary>Oldest event kept in the replay ring (<see cref="Protocol.EventRingMaxAge"/>).</summary>
    public TimeSpan RingMaxAge { get; init; } = Protocol.EventRingMaxAge;

    /// <summary>Bounded live queue per subscriber (<see cref="Protocol.EventSubscriberQueueCapacity"/>).</summary>
    public int SubscriberQueueCapacity { get; init; } = Protocol.EventSubscriberQueueCapacity;

    /// <summary>Heartbeat cadence per subscriber (<see cref="Protocol.HeartbeatInterval"/>).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = Protocol.HeartbeatInterval;

    /// <summary>Throws when a value is out of range.</summary>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(RingCapacity, 1, nameof(RingCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RingMaxAge, TimeSpan.Zero, nameof(RingMaxAge));
        ArgumentOutOfRangeException.ThrowIfLessThan(SubscriberQueueCapacity, 1, nameof(SubscriberQueueCapacity));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HeartbeatInterval, TimeSpan.Zero, nameof(HeartbeatInterval));
    }
}
