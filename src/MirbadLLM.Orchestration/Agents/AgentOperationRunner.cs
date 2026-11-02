using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>What observing an open operation reference found.</summary>
/// <param name="Reference">The reference after observation (closed when the outcome became known).</param>
/// <param name="Status">The agent's status, when it answered with one.</param>
/// <param name="Failure">Why the agent could not be asked (node unreachable, no client); the reference stays open.</param>
public sealed record AgentOperationObservation(AgentOperationReference Reference, OperationStatus? Status, Exception? Failure);

/// <summary>
/// Master-side operation lifecycle (design §4.3 envelope, §4.4 "Master side", §4.6): creates envelopes, writes the
/// operation reference <b>before</b> sending, closes it on a terminal status or an HTTP refusal, and resumes observing open
/// references after a master restart (<c>GET /operations/{id}</c>, never a replacement).
/// <para>Callers (MN-028, MN-032, MN-033, MN-036 …):</para>
/// <code>
/// var envelope = runner.CreateEnvelope();
/// var request = new StopRequest { OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt };
/// OperationStatus status = await runner.SubmitAsync(nodeId, AgentRoutes.RuntimeStop, modelId, request,
///     (client, r, ct) =&gt; client.StopAsync(modelId, r, ct), owner: null, ct);
/// </code>
/// <para>A transport failure (<see cref="AgentUnreachableException"/>) leaves the reference open: the agent may have
/// accepted the operation. The client already retried it with the same body; later observation (events, or
/// <see cref="ObserveAsync"/>) settles it. Terminal operation events can close it through <see cref="CloseFromStatusAsync"/>.</para>
/// </summary>
public sealed class AgentOperationRunner
{
    /// <summary>
    /// An unknown id with a valid envelope is closed as <see cref="AgentOperationOutcome.NotAccepted"/> only when the
    /// reference is older than this (a submission may still be in flight).
    /// </summary>
    public static readonly TimeSpan NotAcceptedGrace = TimeSpan.FromMinutes(2);

    private readonly IAgentClientFactory _clients;
    private readonly IAgentOperationReferenceStore _store;
    private readonly INodeRegistry _nodes;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public AgentOperationRunner(
        IAgentClientFactory clients,
        IAgentOperationReferenceStore store,
        INodeRegistry nodes,
        TimeProvider timeProvider,
        ILogger<AgentOperationRunner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _clients = clients;
        _store = store;
        _nodes = nodes;
        _time = timeProvider;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Called with every agent error answered to a submitted mutation (before it is rethrown). The node monitor sets it
    /// to <c>NodeStatusCache.ReportAgentError</c>, so a <c>stale_master</c>/<c>competing_master</c>/<c>protocol_mismatch</c>
    /// answer to any caller (MN-028, MN-032, …) reaches the node state (W3 review MN-094). Must not throw.
    /// </summary>
    public Action<string, AgentException>? AgentErrorObserver { get; set; }

    /// <summary>A new envelope (id, <c>issuedAt</c> now, <c>expiresAt</c> = +24 h).</summary>
    public AgentEnvelope CreateEnvelope() => AgentEnvelope.Create(_time);

    /// <summary>Submits a mutation that answers an <see cref="OperationStatus"/> (see the class remarks).</summary>
    public Task<OperationStatus> SubmitAsync<TRequest>(
        string nodeId,
        AgentRoute route,
        string target,
        TRequest request,
        Func<IAgentClient, TRequest, CancellationToken, Task<OperationStatus>> send,
        string? owner = null,
        CancellationToken ct = default)
        where TRequest : AgentMutation =>
        SubmitAsync(nodeId, route, target, request, send, status => status.State, status => status.Error?.Code, owner, ct);

    /// <summary>
    /// Submits a mutation with any response type (e.g. <c>POST /downloads</c> answers a download status):
    /// <paramref name="stateOf"/> extracts the operation state when the response carries one.
    /// </summary>
    public async Task<TResult> SubmitAsync<TRequest, TResult>(
        string nodeId,
        AgentRoute route,
        string target,
        TRequest request,
        Func<IAgentClient, TRequest, CancellationToken, Task<TResult>> send,
        Func<TResult, OperationState?> stateOf,
        Func<TResult, string?>? errorOf = null,
        string? owner = null,
        CancellationToken ct = default)
        where TRequest : AgentMutation
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(stateOf);
        if (!route.IsMutation)
        {
            throw new ArgumentException($"Route {route.Name} is not a mutation.", nameof(route));
        }

        if (request.ExpiresAt <= request.IssuedAt || string.IsNullOrEmpty(request.OperationId))
        {
            throw new ArgumentException("The request carries no valid envelope (operation id, issuedAt < expiresAt).", nameof(request));
        }

        NodeRecord node = await _nodes.GetAsync(nodeId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Node '{nodeId}' is not in the registry.");
        IAgentClient client = _clients.GetClient(nodeId);

        var reference = new AgentOperationReference
        {
            OperationId = request.OperationId,
            NodeId = nodeId,
            NodeUid = node.Uid,
            Action = route.OperationKind!,
            Target = target,
            PayloadDigest = AgentPayloadDigest.Compute(request, route.RequestType),
            IssuedAt = request.IssuedAt,
            ExpiresAt = request.ExpiresAt,
            Owner = owner,
            CreatedAt = _time.GetUtcNow(),
        };
        await _store.AddAsync(reference, ct).ConfigureAwait(false);

        TResult result;
        try
        {
            result = await send(client, request, ct).ConfigureAwait(false);
        }
        catch (AgentException ex) when (Observe(nodeId, request.OperationId, ex))
        {
            throw; // Not reached: Observe returns false.
        }
        catch (AgentException ex) when (ex.StatusCode is >= 400 and < 500 || ex.Code == ErrorCodes.StateUnavailable)
        {
            // A refusal (4xx), or 503 state_unavailable ("nothing ran"): the operation will not run; never retried
            // automatically (§4.4).
            await CloseAsync(reference, AgentOperationOutcome.Refused, ex.Code).ConfigureAwait(false);
            throw;
        }

        // Any other agent error (a 5xx after the durable accept, 504 outcome_unknown, an unreadable 2xx body) may belong to
        // an operation that was accepted and runs or ran: the reference stays open and ObserveAsync settles it from the
        // agent's stored status (W2 review MN-092). Still never retried automatically.


        if (stateOf(result) is { } state && state.IsTerminal())
        {
            await CloseAsync(reference, ToOutcome(state), errorOf?.Invoke(result)).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Exception-filter helper: hands the error to <see cref="AgentErrorObserver"/> and never catches.</summary>
    private bool Observe(string nodeId, string operationId, AgentException error)
    {
        try
        {
            AgentErrorObserver?.Invoke(nodeId, error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AgentClientLog.ObserveFailed(_logger, operationId, nodeId, "the agent-error observer failed: " + ex.Message);
        }

        return false;
    }

    /// <summary>Closes the reference of a terminal status (e.g. from an <see cref="OperationEvent"/>). False when there is none or it is not terminal.</summary>
    public async Task<bool> CloseFromStatusAsync(OperationStatus status, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!status.State.IsTerminal())
        {
            return false;
        }

        AgentOperationReference? reference = await _store.GetAsync(status.Id, ct).ConfigureAwait(false);
        if (reference is not { Status: AgentOperationReferenceStatus.Open })
        {
            return false;
        }

        await CloseAsync(reference, ToOutcome(status.State), status.Error?.Code).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Asks the agent about one reference and closes it when the outcome is known: a terminal status; an unknown id with
    /// an expired envelope (<see cref="AgentOperationOutcome.Expired"/>); an unknown id with a valid envelope older than
    /// <see cref="NotAcceptedGrace"/> (<see cref="AgentOperationOutcome.NotAccepted"/>); a node whose uid changed or that is
    /// gone (<see cref="AgentOperationOutcome.Lost"/>). An unreachable node leaves it open.
    /// </summary>
    public async Task<AgentOperationObservation> ObserveAsync(AgentOperationReference reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Status == AgentOperationReferenceStatus.Closed)
        {
            return new AgentOperationObservation(reference, null, null);
        }

        NodeRecord? node = await _nodes.GetAsync(reference.NodeId, ct).ConfigureAwait(false);
        if (node is null || node.Revoked || !string.Equals(node.Uid, reference.NodeUid, StringComparison.Ordinal))
        {
            string reason = node is null ? "node deleted" : node.Revoked ? "node revoked" : "node re-enrolled (uid changed)";
            return new AgentOperationObservation(await CloseAsync(reference, AgentOperationOutcome.Lost, reason).ConfigureAwait(false), null, null);
        }

        OperationStatus? status;
        try
        {
            status = await _clients.GetClient(reference.NodeId).GetOperationAsync(reference.OperationId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentUnreachableException or AgentException or KeyNotFoundException)
        {
            AgentClientLog.ObserveFailed(_logger, reference.OperationId, reference.NodeId, ex.Message);
            return new AgentOperationObservation(reference, null, ex);
        }

        DateTimeOffset now = _time.GetUtcNow();
        if (status is null)
        {
            if (now > reference.ExpiresAt)
            {
                return new AgentOperationObservation(
                    await CloseAsync(reference, AgentOperationOutcome.Expired, ErrorCodes.OperationExpired).ConfigureAwait(false), null, null);
            }

            if (now - reference.CreatedAt > NotAcceptedGrace)
            {
                return new AgentOperationObservation(
                    await CloseAsync(reference, AgentOperationOutcome.NotAccepted, "unknown to the agent").ConfigureAwait(false), null, null);
            }

            return new AgentOperationObservation(reference, null, null);
        }

        if (status.State.IsTerminal())
        {
            return new AgentOperationObservation(
                await CloseAsync(reference, ToOutcome(status.State), status.Error?.Code).ConfigureAwait(false), status, null);
        }

        return new AgentOperationObservation(reference, status, null);
    }

    /// <summary>
    /// Master boot (design §4.6): observes every open reference once, closing those whose outcome is known. Returns one
    /// observation per open reference; still-open ones are followed by the node monitor's operation events.
    /// </summary>
    public async Task<IReadOnlyList<AgentOperationObservation>> ResumeOpenAsync(CancellationToken ct = default)
    {
        IReadOnlyList<AgentOperationReference> open = await _store.ListOpenAsync(ct).ConfigureAwait(false);
        List<AgentOperationObservation> results = [];
        foreach (AgentOperationReference reference in open)
        {
            results.Add(await ObserveAsync(reference, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>The reference outcome of a terminal agent state.</summary>
    public static AgentOperationOutcome ToOutcome(OperationState state) => state switch
    {
        OperationState.Succeeded => AgentOperationOutcome.Succeeded,
        OperationState.Failed => AgentOperationOutcome.Failed,
        OperationState.Cancelled => AgentOperationOutcome.Cancelled,
        OperationState.Unknown => AgentOperationOutcome.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Not a terminal state."),
    };

    private async Task<AgentOperationReference> CloseAsync(AgentOperationReference reference, AgentOperationOutcome outcome, string? error)
    {
        // Closing is bookkeeping after the agent acted: not cancelled with the caller's token.
        AgentOperationReference closed = await _store.CloseAsync(reference.OperationId, outcome, error, CancellationToken.None).ConfigureAwait(false) ?? reference;
        AgentClientLog.ReferenceClosed(_logger, reference.OperationId, reference.NodeId, outcome);
        return closed;
    }
}
