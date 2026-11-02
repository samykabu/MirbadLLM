using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Abstractions;

/// <summary>
/// The agent's event bus (design §3.4, §4.7), implemented in <c>MirbadLLM.Agent.Events</c> (MN-017).
/// <para>Invariants:</para>
/// <list type="bullet">
/// <item>Every state change gets the next per-boot <see cref="AgentEvent.Seq"/> and is appended to a ring of the last
/// 10,000 events or 10 minutes (<see cref="Protocol.EventRingCapacity"/>, <see cref="Protocol.EventRingMaxAge"/>).</item>
/// <item>One lock (the "state lock") covers applying a state change, assigning its seq and appending it, and building a
/// snapshot (<see cref="Snapshot{T}"/>): a snapshot's watermark is exactly the seq of the last change it contains, so a
/// subscriber starting after it loses and doubles nothing.</item>
/// <item>A subscription with the current boot and a seq still in the ring replays from <c>seq + 1</c>; otherwise it yields
/// one <see cref="ResetEvent"/> and completes.</item>
/// <item>Each subscriber has a bounded queue (1,000 events); on overflow it yields one <see cref="OverflowEvent"/> and
/// completes, so a slow master never makes the agent buffer without bound.</item>
/// <item>Heartbeat, reset and overflow events carry the current seq and are not stored in the ring.</item>
/// </list>
/// </summary>
public interface IEventBus
{
    /// <summary>This agent boot's id.</summary>
    string BootId { get; }

    /// <summary>Seq of the last published state change (0 before any).</summary>
    long CurrentSeq { get; }

    /// <summary>Assigns the next seq, boot id and time to <paramref name="evt"/>, appends it and fans it out. Returns the stamped event.</summary>
    AgentEvent Publish(AgentEvent evt);

    /// <summary>Applies <paramref name="stateChange"/> and publishes <paramref name="evt"/> atomically under the state lock.</summary>
    AgentEvent Publish(Action stateChange, AgentEvent evt);

    /// <summary>Builds a snapshot under the state lock; <paramref name="build"/> receives the watermark seq.</summary>
    T Snapshot<T>(Func<long, T> build);

    /// <summary>Subscribes from a cursor (both null = live only). Dispose the subscription to unsubscribe.</summary>
    IEventSubscription Subscribe(string? bootId, long? afterSeq);
}

/// <summary>One subscriber of <see cref="IEventBus"/>.</summary>
public interface IEventSubscription : IAsyncDisposable
{
    /// <summary>Replayed events, then live ones; completes after a reset or overflow event or on dispose.</summary>
    IAsyncEnumerable<AgentEvent> ReadAllAsync(CancellationToken ct = default);
}
