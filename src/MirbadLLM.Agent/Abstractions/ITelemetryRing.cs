using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Abstractions;

/// <summary>
/// The agent's telemetry ring (design §8.1), implemented in <c>MirbadLLM.Agent.Telemetry</c> (MN-018) and filled by its
/// 5-second sampler.
/// <para>Invariants: samples are numbered <c>(bootId, seq)</c> with a per-boot monotonic seq; the ring keeps 15 minutes
/// (<see cref="Protocol.TelemetryRingWindow"/>); a read with another boot returns the oldest samples of the current boot
/// with <see cref="TelemetryPage.Gap"/> = true; a read whose cursor was evicted returns the oldest kept samples with
/// <c>Gap</c> = true; samples are returned in time order and never invented.</para>
/// </summary>
public interface ITelemetryRing
{
    /// <summary>This agent boot's id.</summary>
    string BootId { get; }

    /// <summary>Assigns the next seq to <paramref name="sample"/>, appends it and evicts samples older than the window. Returns the stored sample.</summary>
    NodeSample Append(NodeSample sample);

    /// <summary>Samples after the cursor, at most <paramref name="max"/>.</summary>
    TelemetryPage Read(string? bootId, long? afterSeq, int max);

    /// <summary>The newest sample, or null before the first.</summary>
    NodeSample? Latest { get; }
}
