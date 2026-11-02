using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Storage;

/// <summary>What <see cref="StorageOperations.AdmitAsync"/> decided for a mutation.</summary>
public enum MutationAdmissionOutcome
{
    /// <summary>New operation, durably accepted: execute it.</summary>
    Run,

    /// <summary>Same id and payload as a stored operation: answer from <see cref="MutationAdmission.Status"/>, run nothing.</summary>
    Replay,

    /// <summary>Refused before anything ran (fencing, conflict, expiry, skew, state unavailable): answer <see cref="MutationAdmission.Error"/>.</summary>
    Refused,
}

/// <summary>Result of <see cref="StorageOperations.AdmitAsync"/>.</summary>
/// <param name="Outcome">Run, replay or refused.</param>
/// <param name="Status">The accepted (Run) or stored (Replay) status; null when refused.</param>
/// <param name="Master">The submitting master's triple; null when the header was missing or malformed.</param>
/// <param name="Error">The refusal (Refused only).</param>
public sealed record MutationAdmission(MutationAdmissionOutcome Outcome, OperationStatus? Status, MasterTriple? Master, AgentException? Error);

/// <summary>
/// The storage package's side of design §4.4 for its mutations (image pull, folder delete, download start/cancel):
/// fencing check of the presented master triple, durable acceptance with a redacted payload, payload-bound
/// deduplication, and stage/terminal records that are also published as <see cref="OperationEvent"/>s.
/// <para>Refusal rule (used for replies and replays alike): an operation that fails before its first stage was refused
/// synchronously and answers with its error; one that failed while executing answers with its stored status.</para>
/// </summary>
public sealed class StorageOperations(IOperationLog log, IFencingState fencing, IEventBus events)
{
    /// <summary>The operation log (for lookups by the storage services).</summary>
    public IOperationLog Log { get; } = log;

    /// <summary>The fencing state (destructive stages re-check <see cref="IFencingState.IsCurrent"/>).</summary>
    public IFencingState Fencing { get; } = fencing;

    /// <summary>
    /// Checks the <c>MirbadLLM-Master</c> triple, then durably accepts the operation (the payload is redacted first, so
    /// the digest never covers the HF token) or finds it already stored.
    /// </summary>
    public async Task<MutationAdmission> AdmitAsync(
        HttpRequest request, AgentMutation envelope, string kind, string target, JsonElement body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrWhiteSpace(envelope.OperationId))
        {
            return Refused(StorageErrors.InvalidRequest("operationId is required."), null);
        }

        if (!MasterTriple.TryParse(request.Headers[Protocol.MasterHeader], out var master))
        {
            return Refused(StorageErrors.InvalidRequest($"Mutations need a valid {Protocol.MasterHeader} header."), null);
        }

        var fence = await Fencing.CheckAsync(master, ct).ConfigureAwait(false);
        switch (fence.Decision)
        {
            case FencingDecision.StaleMaster:
                return Refused(StorageErrors.Create(ErrorCodes.StaleMaster, "A master with a higher epoch drives this node."), master);
            case FencingDecision.CompetingMaster:
                return Refused(StorageErrors.Create(ErrorCodes.CompetingMaster, "Another master boot already drives this node at this epoch."), master);
        }

        OperationAcceptance acceptance;
        try
        {
            acceptance = await Log.AcceptAsync(new OperationSubmission
            {
                OperationId = envelope.OperationId,
                Kind = kind,
                Target = target,
                IssuedAt = envelope.IssuedAt,
                ExpiresAt = envelope.ExpiresAt,
                Master = master,
                Payload = PayloadRedaction.Redact(body),
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Any failure to record durably means nothing runs (as MutationGate does for the runtime routes).
            return Refused(StorageErrors.Create(ErrorCodes.StateUnavailable, "The operation log cannot be written; nothing was run."), master);
        }

        switch (acceptance.Outcome)
        {
            case OperationAcceptOutcome.Accepted:
                Publish(acceptance.Status!);
                return new MutationAdmission(MutationAdmissionOutcome.Run, acceptance.Status, master, null);
            case OperationAcceptOutcome.Duplicate:
                return new MutationAdmission(MutationAdmissionOutcome.Replay, acceptance.Status, master, null);
            case OperationAcceptOutcome.Conflict:
                return Refused(StorageErrors.Create(ErrorCodes.OperationIdConflict,
                    $"Operation '{envelope.OperationId}' was already used with another action, target or payload."), master);
            case OperationAcceptOutcome.Expired:
                return Refused(StorageErrors.Create(ErrorCodes.OperationExpired,
                    $"Operation '{envelope.OperationId}' expired at {envelope.ExpiresAt:O}; nothing was run."), master);
            default:
                return Refused(StorageErrors.Create(ErrorCodes.ClockSkew,
                    "The operation's issuedAt is more than 5 minutes ahead of this node's clock."), master);
        }
    }

    /// <summary>True when a stored status was refused synchronously (failed before its first stage).</summary>
    public static bool IsRefusal(OperationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State == OperationState.Failed && status.Error is not null &&
            (status.Stage is null || status.Stage == OperationStages.Accepted);
    }

    /// <summary>Records a stage/progress change durably and publishes it.</summary>
    public async Task<OperationStatus> StageAsync(string operationId, OperationStageUpdate update, CancellationToken ct = default)
    {
        var status = await Log.RecordStageAsync(operationId, update, ct).ConfigureAwait(false);
        Publish(status);
        return status;
    }

    /// <summary>Records the terminal state durably and publishes it.</summary>
    public async Task<OperationStatus> CompleteAsync(
        string operationId, OperationState state, AgentError? failure = null, JsonElement? result = null, CancellationToken ct = default)
    {
        var status = await Log.CompleteAsync(operationId, state, failure, result, ct).ConfigureAwait(false);
        Publish(status);
        return status;
    }

    /// <summary>Publishes an operation status without recording it (progress the log is not written for).</summary>
    public void Publish(OperationStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Events.Publish(new OperationEvent { Operation = status });
    }

    /// <summary>The event bus (download events).</summary>
    public IEventBus Events { get; } = events;

    private static MutationAdmission Refused(AgentException error, MasterTriple? master) =>
        new(MutationAdmissionOutcome.Refused, null, master, error);
}
