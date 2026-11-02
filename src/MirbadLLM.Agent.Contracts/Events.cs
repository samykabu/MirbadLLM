using System.Text.Json.Serialization;

namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// Query of <c>GET /agent/v1/events</c> (design §4.7). With the agent's current <see cref="BootId"/> and an
/// <see cref="AfterSeq"/> still in the ring, the agent replays from <c>AfterSeq + 1</c>; otherwise it sends one
/// <see cref="ResetEvent"/> and closes. Both null = live events only.
/// </summary>
public sealed record EventsQuery
{
    /// <summary>Boot id of the snapshot the master applied.</summary>
    public string? BootId { get; init; }

    /// <summary>Last applied sequence number (the snapshot's <see cref="StatusSnapshot.Seq"/> after a snapshot).</summary>
    public long? AfterSeq { get; init; }
}

/// <summary>SSE <c>event:</c> names and JSON <c>type</c> discriminators of <see cref="AgentEvent"/> (design §3.4, §4.7).</summary>
public static class AgentEventTypes
{
    /// <summary>Every 5 s; carries the current seq.</summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>A runtime instance changed.</summary>
    public const string Runtime = "runtime";

    /// <summary>An operation changed state, stage or progress.</summary>
    public const string Operation = "operation";

    /// <summary>A download changed.</summary>
    public const string Download = "download";

    /// <summary>A start reservation was taken or released.</summary>
    public const string Reservation = "reservation";

    /// <summary>The node's health (its Degraded reasons) changed (additive, MN-047 / D68).</summary>
    public const string Health = "health";

    /// <summary>The requested cursor cannot be replayed; the stream closes and the master re-snapshots.</summary>
    public const string Reset = "reset";

    /// <summary>The subscriber queue overflowed; the stream closes and the master re-snapshots.</summary>
    public const string Overflow = "overflow";

    /// <summary>Every type, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } = [Heartbeat, Runtime, Operation, Download, Reservation, Health, Reset, Overflow];
}

/// <summary>
/// One agent event (design §3.4, §4.7), sent over SSE as <c>event: {type}</c> + <c>data: {json}</c>, the JSON carrying the
/// same <c>type</c> discriminator. Always serialize as <see cref="AgentEvent"/> (the base type) so the discriminator is
/// written. Every state change increments the per-boot <see cref="Seq"/>; the master ignores events with
/// <c>seq ≤</c> the last applied for the same boot. Heartbeat, reset and overflow are not state changes: they carry the
/// current seq and are not stored in the replay ring.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HeartbeatEvent), AgentEventTypes.Heartbeat)]
[JsonDerivedType(typeof(RuntimeEvent), AgentEventTypes.Runtime)]
[JsonDerivedType(typeof(OperationEvent), AgentEventTypes.Operation)]
[JsonDerivedType(typeof(DownloadEvent), AgentEventTypes.Download)]
[JsonDerivedType(typeof(ReservationEvent), AgentEventTypes.Reservation)]
[JsonDerivedType(typeof(HealthEvent), AgentEventTypes.Health)]
[JsonDerivedType(typeof(ResetEvent), AgentEventTypes.Reset)]
[JsonDerivedType(typeof(OverflowEvent), AgentEventTypes.Overflow)]
public abstract record AgentEvent
{
    /// <summary>Agent boot id.</summary>
    public string BootId { get; init; } = "";

    /// <summary>Per-boot monotonic sequence number, assigned by the event bus.</summary>
    public long Seq { get; init; }

    /// <summary>Agent clock when the event was produced.</summary>
    public DateTimeOffset At { get; init; }

    /// <summary>The SSE <c>event:</c> name (<see cref="AgentEventTypes"/>).</summary>
    [JsonIgnore]
    public abstract string EventType { get; }
}

/// <summary>Liveness every 5 s (design §3.4).</summary>
public sealed record HeartbeatEvent : AgentEvent
{
    /// <inheritdoc/>
    [JsonIgnore] // MN-090: the override needs its own attribute, else "eventType" is written next to "type"
    public override string EventType => AgentEventTypes.Heartbeat;
}

/// <summary>A runtime instance changed (design §4.7). <see cref="Runtime"/> null = the instance is gone (Stopped).</summary>
public sealed record RuntimeEvent : AgentEvent
{
    /// <summary>Model id.</summary>
    public required string ModelId { get; init; }

    /// <summary>The instance now; null when it was removed.</summary>
    public RuntimeStatus? Runtime { get; init; }

    /// <inheritdoc/>
    [JsonIgnore] // MN-090: the override needs its own attribute, else "eventType" is written next to "type"
    public override string EventType => AgentEventTypes.Runtime;
}

/// <summary>An operation changed (design §4.7).</summary>
public sealed record OperationEvent : AgentEvent
{
    /// <summary>The operation now.</summary>
    public required OperationStatus Operation { get; init; }

    /// <inheritdoc/>
    [JsonIgnore] // MN-090: the override needs its own attribute, else "eventType" is written next to "type"
    public override string EventType => AgentEventTypes.Operation;
}

/// <summary>A download changed (design §4.7).</summary>
public sealed record DownloadEvent : AgentEvent
{
    /// <summary>The download now.</summary>
    public required NodeDownloadStatus Download { get; init; }

    /// <inheritdoc/>
    [JsonIgnore] // MN-090: the override needs its own attribute, else "eventType" is written next to "type"
    public override string EventType => AgentEventTypes.Download;
}

/// <summary>A start reservation was taken or released (design §4.8).</summary>
public sealed record ReservationEvent : AgentEvent
{
    /// <summary>The reservation.</summary>
    public required ReservationInfo Reservation { get; init; }

    /// <summary>True when it was released (instance Ready, operation terminal or cancelled).</summary>
    public bool Released { get; init; }

    /// <inheritdoc/>
    [JsonIgnore] // MN-090: the override needs its own attribute, else "eventType" is written next to "type"
    public override string EventType => AgentEventTypes.Reservation;
}

/// <summary>
/// The node's agent-computed health changed (design §3.4/§3.5 Online ↔ Degraded; additive in protocol 1, MN-047 / D68).
/// The agent checks its health contributors every heartbeat interval and publishes this event when the set of reason codes
/// differs from the last one it published (a fresh boot starts from "healthy"), so a master whose stream stays up sees
/// Docker, hostd, GPU-probe, model-root and op-log problems come and go without a re-snapshot. It is a state change: it
/// takes the next seq and is replayed like the others. <c>/status</c> still probes health itself.
/// </summary>
public sealed record HealthEvent : AgentEvent
{
    /// <summary>The node's health now (empty reasons = healthy).</summary>
    public required NodeHealth Health { get; init; }

    /// <inheritdoc/>
    [JsonIgnore] // MN-090: the override needs its own attribute, else "eventType" is written next to "type"
    public override string EventType => AgentEventTypes.Health;
}

/// <summary>The cursor cannot be replayed (other boot, or evicted from the ring); the stream closes (design §4.7).</summary>
public sealed record ResetEvent : AgentEvent
{
    /// <summary>Why: <see cref="BootChanged"/> or <see cref="Evicted"/>.</summary>
    public required string Reason { get; init; }

    /// <summary>The requested boot is not the agent's current boot.</summary>
    public const string BootChanged = "bootChanged";

    /// <summary>The requested seq is no longer in the ring.</summary>
    public const string Evicted = "evicted";

    /// <inheritdoc/>
    [JsonIgnore] // MN-090: the override needs its own attribute, else "eventType" is written next to "type"
    public override string EventType => AgentEventTypes.Reset;
}

/// <summary>The subscriber's bounded queue (1,000 events) overflowed; the stream closes (design §4.7).</summary>
public sealed record OverflowEvent : AgentEvent
{
    /// <inheritdoc/>
    [JsonIgnore] // MN-090: the override needs its own attribute, else "eventType" is written next to "type"
    public override string EventType => AgentEventTypes.Overflow;
}
