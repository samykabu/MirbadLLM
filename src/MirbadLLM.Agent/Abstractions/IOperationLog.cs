using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Abstractions;

/// <summary>
/// A mutation the agent is asked to accept (design §4.4 item 1): identity, envelope, master triple and the raw body. The
/// log redacts <see cref="Protocol.SecretFieldNames"/> from <see cref="Payload"/> before hashing or storing it.
/// </summary>
public sealed record OperationSubmission
{
    /// <summary>Operation id from the envelope.</summary>
    public required string OperationId { get; init; }

    /// <summary>Operation kind (<see cref="OperationKinds"/>), from the route.</summary>
    public required string Kind { get; init; }

    /// <summary>Target (model id, folder, unit, rotation id…), from the path.</summary>
    public required string Target { get; init; }

    /// <summary>Envelope issue time.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Envelope expiry.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The submitting master's triple (already accepted by <see cref="IFencingState"/>).</summary>
    public required MasterTriple Master { get; init; }

    /// <summary>The request body as received (secrets included; the log removes them).</summary>
    public required JsonElement Payload { get; init; }
}

/// <summary>Outcome of <see cref="IOperationLog.AcceptAsync"/> (design §4.4 items 1, 2, 6).</summary>
public enum OperationAcceptOutcome
{
    /// <summary>New operation, durably recorded (fsync'd) as Accepted: run it.</summary>
    Accepted,

    /// <summary>Same id, kind, target and digest: return the stored status, do not run anything.</summary>
    Duplicate,

    /// <summary>Same id with another kind, target or digest: 409 <c>operation_id_conflict</c>.</summary>
    Conflict,

    /// <summary>Unknown id whose envelope expired: 410 <c>operation_expired</c>; never executed.</summary>
    Expired,

    /// <summary><c>issuedAt</c> more than 5 minutes in the future: 400 <c>clock_skew</c>.</summary>
    IssuedInFuture,
}

/// <summary>Result of <see cref="IOperationLog.AcceptAsync"/>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Status">The new (Accepted) or stored status; null for Conflict, Expired and IssuedInFuture.</param>
public sealed record OperationAcceptance(OperationAcceptOutcome Outcome, OperationStatus? Status);

/// <summary>A durable stage change of an operation (design §4.4 item 4).</summary>
public sealed record OperationStageUpdate
{
    /// <summary>New stage (<see cref="OperationStages"/>). Null keeps the current one (progress-only update).</summary>
    public string? Stage { get; init; }

    /// <summary>State after the update; null keeps the current one (Accepted becomes Running on the first stage).</summary>
    public OperationState? State { get; init; }

    /// <summary>Progress, when known.</summary>
    public OperationProgress? Progress { get; init; }

    /// <summary>The start reservation the operation now holds (launch), persisted so it can be rebuilt (§4.8).</summary>
    public ReservationInfo? Reservation { get; init; }
}

/// <summary>
/// An operation as recovered from the log for reconciliation (design §4.4 item 5): the status plus what is needed to
/// resume it.
/// </summary>
public sealed record RecoverableOperation
{
    /// <summary>Current status (non-terminal).</summary>
    public required OperationStatus Status { get; init; }

    /// <summary>The master triple that submitted it (superseded → remove and fail with <c>superseded</c>).</summary>
    public required MasterTriple Master { get; init; }

    /// <summary>Redacted request body (secrets are <c>***</c>).</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>The reservation recorded with it, if any.</summary>
    public ReservationInfo? Reservation { get; init; }
}

/// <summary>
/// The agent's durable operation log (design §4.4 items 1–8), implemented in <c>MirbadLLM.Agent.Operations</c> (MN-013).
/// <para>Invariants:</para>
/// <list type="bullet">
/// <item>Durable acceptance: <see cref="AcceptAsync"/> appends the Accepted line to <c>{state}/operations/log.jsonl</c> and
/// flushes it to disk (<c>FileStream.Flush(true)</c>) before returning <see cref="OperationAcceptOutcome.Accepted"/>; if
/// the write fails it throws and nothing runs (503 <c>state_unavailable</c>).</item>
/// <item>Payload-bound deduplication: the digest is SHA-256 over the canonical body with secrets replaced by
/// <c>***</c>; the envelope fields are part of it. Stored payloads never contain secrets.</item>
/// <item>Every stage and the terminal state are durable before the method returns.</item>
/// <item>Retention: a terminal record is kept until <c>expiresAt</c> + 10 minutes and at least the last 1,000 records
/// are kept; active operations are never pruned. An unknown id with an expired envelope is
/// <see cref="OperationAcceptOutcome.Expired"/>.</item>
/// <item>A torn last line (crash mid-write) is ignored on load and counted in <see cref="TornLinesIgnored"/>.</item>
/// </list>
/// </summary>
public interface IOperationLog
{
    /// <summary>Accepts, deduplicates or refuses a submission (see <see cref="OperationAcceptOutcome"/>).</summary>
    Task<OperationAcceptance> AcceptAsync(OperationSubmission submission, CancellationToken ct = default);

    /// <summary>Records a stage, state or progress change durably and returns the new status.</summary>
    Task<OperationStatus> RecordStageAsync(string operationId, OperationStageUpdate update, CancellationToken ct = default);

    /// <summary>
    /// Records the terminal state (Succeeded, Failed, Cancelled or Unknown) with its <paramref name="failure"/> (Failed,
    /// or the observed state for Unknown) or <paramref name="result"/> durably and returns the final status. Completing an
    /// already terminal operation returns it unchanged.
    /// </summary>
    Task<OperationStatus> CompleteAsync(string operationId, OperationState state, AgentError? failure = null, JsonElement? result = null, CancellationToken ct = default);

    /// <summary>The status of an operation, or null when the log does not know it.</summary>
    OperationStatus? Find(string operationId);

    /// <summary>Non-terminal operations, oldest first.</summary>
    IReadOnlyList<OperationStatus> ListActive();

    /// <summary>The most recent terminal operations, newest first, at most <paramref name="max"/>.</summary>
    IReadOnlyList<OperationStatus> ListRecent(int max);

    /// <summary>Every non-terminal operation with what reconciliation needs to resume or fail it.</summary>
    IReadOnlyList<RecoverableOperation> ListRecoverable();

    /// <summary>Compacts <c>log.jsonl</c> into <c>operations.json</c> (temp + move), applying retention.</summary>
    Task CompactAsync(CancellationToken ct = default);

    /// <summary>Torn (unparseable) lines ignored when the log was loaded.</summary>
    int TornLinesIgnored { get; }
}
