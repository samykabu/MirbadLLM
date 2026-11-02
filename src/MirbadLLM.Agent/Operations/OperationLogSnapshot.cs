using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Operations;

/// <summary>
/// <c>{state}/operations/operations.json</c>: the compacted operation log (design §4.4 items 1 and 6), written with
/// temp file + <c>Flush(true)</c> + rename. Holds every retained operation folded to its current state, plus the length and
/// SHA-256 of the <c>log.jsonl</c> prefix it already contains: if the agent dies after the rename but before the log is
/// truncated, the next load skips exactly that prefix instead of replaying it twice.
/// </summary>
internal sealed record OperationLogSnapshot
{
    /// <summary>Schema version written by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of the file.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>When the snapshot was written (agent clock).</summary>
    public DateTimeOffset CompactedAt { get; init; }

    /// <summary>Bytes of <c>log.jsonl</c> folded into this snapshot.</summary>
    public long CoveredLogBytes { get; init; }

    /// <summary>Base64 SHA-256 of those bytes.</summary>
    public string? CoveredLogSha256 { get; init; }

    /// <summary>Retained operations, in acceptance order.</summary>
    public IReadOnlyList<StoredOperation> Operations { get; init; } = [];
}

/// <summary>One operation folded from its log lines (in memory and in <see cref="OperationLogSnapshot"/>).</summary>
internal sealed record StoredOperation
{
    /// <summary>Current status.</summary>
    public required OperationStatus Status { get; init; }

    /// <summary>The submitting master triple.</summary>
    public required MasterTriple Master { get; init; }

    /// <summary>The redacted request body.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>The start reservation recorded by a stage (cleared at completion).</summary>
    public ReservationInfo? Reservation { get; init; }

    /// <summary>Boot id of the agent that accepted it.</summary>
    public string? BootId { get; init; }

    /// <summary>Node uid at acceptance.</summary>
    public string? NodeUid { get; init; }
}
