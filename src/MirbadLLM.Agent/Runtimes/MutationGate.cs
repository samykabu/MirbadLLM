using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// The front half of every runtime and operation mutation (design §4.2–§4.4): the fencing check before anything is
/// acknowledged, the envelope pre-check, and durable acceptance in <see cref="IOperationLog"/> with payload-bound
/// deduplication. Each successful log write is published as an <see cref="OperationEvent"/>.
/// </summary>
public sealed class MutationGate
{
    private readonly IOperationLog _log;
    private readonly IFencingState _fencing;
    private readonly IEventBus _events;
    private readonly TimeProvider _time;

    /// <summary>Creates the gate.</summary>
    public MutationGate(IOperationLog log, IFencingState fencing, IEventBus events, TimeProvider time)
    {
        _log = log;
        _fencing = fencing;
        _events = events;
        _time = time;
    }

    /// <summary>The operation log behind the gate.</summary>
    public IOperationLog Log => _log;

    /// <summary>
    /// Checks the presented master triple (design §4.2 layer 2): lower epoch → 409 <c>stale_master</c>, equal epoch with
    /// another instance or boot → 409 <c>competing_master</c>; a higher epoch is persisted by <see cref="IFencingState"/>.
    /// Returns null when accepted.
    /// </summary>
    public async Task<AgentException?> CheckFencingAsync(MasterTriple presented, CancellationToken ct)
    {
        FencingCheck check;
        try
        {
            check = await _fencing.CheckAsync(presented, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AgentHttp.Failure(StatusCodes.Status503ServiceUnavailable, ErrorCodes.StateUnavailable,
                "The master triple could not be persisted; nothing ran. " + ex.Message);
        }

        return check.Decision switch
        {
            FencingDecision.Accepted => null,
            FencingDecision.StaleMaster => AgentHttp.Failure(StatusCodes.Status409Conflict, ErrorCodes.StaleMaster,
                string.Create(CultureInfo.InvariantCulture,
                    $"This node accepted master epoch {check.Accepted?.Epoch}; epoch {presented.Epoch} is stale.")),
            FencingDecision.CompetingMaster => AgentHttp.Failure(StatusCodes.Status409Conflict, ErrorCodes.CompetingMaster,
                string.Create(CultureInfo.InvariantCulture,
                    $"Another master already drives this node at epoch {presented.Epoch}.")),
            _ => AgentHttp.Failure(StatusCodes.Status500InternalServerError, ErrorCodes.InternalError, "Unknown fencing decision."),
        };
    }

    /// <summary>True when the operation's own triple is still the accepted master (checked before destructive stages).</summary>
    public bool IsCurrent(MasterTriple operationMaster) => _fencing.IsCurrent(operationMaster);

    /// <summary>
    /// Pre-checks the envelope of an operation id the log does not know yet, so nothing is reserved for a submission the
    /// log would refuse anyway: expired → 410 <c>operation_expired</c>; issued more than 5 minutes ahead → 400
    /// <c>clock_skew</c>. Returns null when the envelope is acceptable.
    /// </summary>
    public AgentException? CheckEnvelope(AgentMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var now = _time.GetUtcNow();
        if (mutation.ExpiresAt <= now)
        {
            return AgentHttp.Failure(StatusCodes.Status410Gone, ErrorCodes.OperationExpired,
                "Operation " + mutation.OperationId + " expired; nothing was executed. Read the current state.");
        }

        if (mutation.IssuedAt - now > OperationRetention.MaxIssuedAtFutureSkew)
        {
            return AgentHttp.Failure(StatusCodes.Status400BadRequest, ErrorCodes.ClockSkew,
                "The envelope's issuedAt is more than 5 minutes ahead of the node clock.");
        }

        return null;
    }

    /// <summary>True when the log already knows <paramref name="operationId"/> (a retry or a reused id).</summary>
    public bool IsKnown(string operationId) => _log.Find(operationId) is not null;

    /// <summary>
    /// Durably accepts, deduplicates or refuses a submission (design §4.4 items 1, 2, 6) and maps the outcome to the
    /// agent's answers. A failed write is 503 <c>state_unavailable</c>: nothing runs.
    /// </summary>
    public async Task<GateResult> AcceptAsync(
        AgentMutation mutation, string kind, string target, MasterTriple master, JsonElement payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        var submission = new OperationSubmission
        {
            OperationId = mutation.OperationId,
            Kind = kind,
            Target = target,
            IssuedAt = mutation.IssuedAt,
            ExpiresAt = mutation.ExpiresAt,
            Master = master,
            Payload = payload,
        };

        OperationAcceptance acceptance;
        try
        {
            acceptance = await _log.AcceptAsync(submission, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return GateResult.Refused(AgentHttp.Failure(StatusCodes.Status503ServiceUnavailable, ErrorCodes.StateUnavailable,
                "The operation could not be recorded durably; nothing ran. " + ex.Message));
        }

        switch (acceptance.Outcome)
        {
            case OperationAcceptOutcome.Accepted:
                PublishOperation(acceptance.Status!);
                return new GateResult(OperationAcceptOutcome.Accepted, acceptance.Status, null);
            case OperationAcceptOutcome.Duplicate:
                return new GateResult(OperationAcceptOutcome.Duplicate, acceptance.Status, null);
            case OperationAcceptOutcome.Conflict:
                return GateResult.Refused(AgentHttp.Failure(StatusCodes.Status409Conflict, ErrorCodes.OperationIdConflict,
                    "Operation id " + mutation.OperationId + " was already used with another action, target or payload."));
            case OperationAcceptOutcome.Expired:
                return GateResult.Refused(AgentHttp.Failure(StatusCodes.Status410Gone, ErrorCodes.OperationExpired,
                    "Operation " + mutation.OperationId + " expired; nothing was executed. Read the current state."));
            case OperationAcceptOutcome.IssuedInFuture:
                return GateResult.Refused(AgentHttp.Failure(StatusCodes.Status400BadRequest, ErrorCodes.ClockSkew,
                    "The envelope's issuedAt is more than 5 minutes ahead of the node clock."));
            default:
                return GateResult.Refused(AgentHttp.Failure(StatusCodes.Status500InternalServerError, ErrorCodes.InternalError,
                    "Unknown acceptance outcome."));
        }
    }

    /// <summary>
    /// Records a stage durably and publishes the operation's <b>latest</b> status (the log's, not the returned one): two
    /// writers on one operation (the watcher finishing it, a late driver stage) then never publish a Running status after
    /// the terminal one (W2 review MN-092).
    /// </summary>
    public async Task<OperationStatus> RecordStageAsync(string operationId, OperationStageUpdate update, CancellationToken ct)
    {
        var status = await _log.RecordStageAsync(operationId, update, ct).ConfigureAwait(false);
        PublishOperation(_log.Find(operationId) ?? status);
        return status;
    }

    /// <summary>Records the terminal state durably and publishes the final status.</summary>
    public async Task<OperationStatus> CompleteAsync(
        string operationId, OperationState state, AgentError? failure, JsonElement? result, CancellationToken ct)
    {
        var status = await _log.CompleteAsync(operationId, state, failure, result, ct).ConfigureAwait(false);
        PublishOperation(_log.Find(operationId) ?? status);
        return status;
    }

    /// <summary>Publishes an operation's current status.</summary>
    public void PublishOperation(OperationStatus status) => _events.Publish(new OperationEvent { Operation = status });
}

/// <summary>Result of <see cref="MutationGate.AcceptAsync"/>.</summary>
/// <param name="Outcome">Accepted or Duplicate when <paramref name="Error"/> is null.</param>
/// <param name="Status">The new or stored status.</param>
/// <param name="Error">The refusal to answer (conflict, expired, skew, state unavailable).</param>
public sealed record GateResult(OperationAcceptOutcome Outcome, OperationStatus? Status, AgentException? Error)
{
    /// <summary>A refusal.</summary>
    public static GateResult Refused(AgentException error) => new(OperationAcceptOutcome.Conflict, null, error);
}
