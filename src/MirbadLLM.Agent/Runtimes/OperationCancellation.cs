using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// <c>POST /operations/{operationId}/cancel</c> (design §4.3, §4.4 item 7). The cancel is an operation of its own (its
/// id in the body), deduplicated like every mutation. Order: fencing → a known cancel id is answered from the log →
/// envelope → the target must exist (404 <c>not_found</c>) → a terminal target is answered Succeeded with its final
/// status → the target's <see cref="IOperationCanceller"/> must accept (409 <c>invalid_state</c> otherwise, e.g. a launch
/// whose instance exists must be stopped) → durable acceptance → cancel → Succeeded with the target's status as result.
/// Refusals before acceptance leave no record.
/// </summary>
public sealed class OperationCancellation : IDisposable
{
    private readonly MutationGate _gate;
    private readonly IReadOnlyList<IOperationCanceller> _cancellers;
    private readonly SemaphoreSlim _acceptLock = new(1, 1);

    /// <summary>Creates the handler.</summary>
    public OperationCancellation(MutationGate gate, IEnumerable<IOperationCanceller> cancellers)
    {
        ArgumentNullException.ThrowIfNull(cancellers);
        _gate = gate;
        _cancellers = [.. cancellers];
    }

    /// <inheritdoc/>
    public void Dispose() => _acceptLock.Dispose();

    /// <summary>Cancels <paramref name="targetId"/>; 200 with the cancel operation's status, or the refusal.</summary>
    public async Task<MutationResult> CancelAsync(
        string targetId, OperationCancelRequest request, JsonElement payload, MasterTriple master, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);
        ArgumentNullException.ThrowIfNull(request);

        if (await _gate.CheckFencingAsync(master, ct).ConfigureAwait(false) is { } fenced)
        {
            return MutationResult.Refused(fenced);
        }

        await _acceptLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gate.IsKnown(request.OperationId))
            {
                return FromGate(await Accept(request, targetId, master, payload, ct).ConfigureAwait(false));
            }

            if (_gate.CheckEnvelope(request) is { } envelope)
            {
                return MutationResult.Refused(envelope);
            }

            if (_gate.Log.Find(targetId) is not { } target)
            {
                return MutationResult.Refused(AgentHttp.Failure(StatusCodes.Status404NotFound, ErrorCodes.NotFound,
                    "Operation " + targetId + " is not known on this node."));
            }

            IOperationCanceller? canceller = null;
            if (!RuntimeOperations.IsTerminal(target.State))
            {
                canceller = _cancellers.FirstOrDefault(c => c.Kinds.Contains(target.Kind, StringComparer.Ordinal));
                if (canceller is null)
                {
                    return MutationResult.Refused(AgentHttp.Failure(StatusCodes.Status409Conflict, ErrorCodes.InvalidState,
                        "Operations of kind '" + target.Kind + "' cannot be cancelled."));
                }

                if (canceller.CheckCancellable(target) is { } refused)
                {
                    return MutationResult.Refused(refused);
                }
            }

            var accepted = await Accept(request, targetId, master, payload, ct).ConfigureAwait(false);
            if (accepted.Error is not null || accepted.Outcome != OperationAcceptOutcome.Accepted)
            {
                return FromGate(accepted);
            }

            try
            {
                var after = canceller is null ? target : await canceller.CancelAsync(target, CancellationToken.None).ConfigureAwait(false);
                var done = await _gate.CompleteAsync(request.OperationId, OperationState.Succeeded, null, AgentJson.ToElement(after), CancellationToken.None)
                    .ConfigureAwait(false);
                return MutationResult.Ok(StatusCodes.Status200OK, done);
            }
            catch (AgentException ex)
            {
                await TryFailAsync(request.OperationId, ex.Error).ConfigureAwait(false);
                return MutationResult.Refused(ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never leave an accepted cancel active (active operations are never pruned, W2 review MN-092).
                var error = new AgentError { Code = ErrorCodes.InternalError, Message = "The cancel failed: " + ex.Message };
                await TryFailAsync(request.OperationId, error).ConfigureAwait(false);
                return MutationResult.Refused(new AgentException(StatusCodes.Status500InternalServerError, error));
            }
        }
        finally
        {
            _acceptLock.Release();
        }
    }

    private async Task TryFailAsync(string operationId, AgentError error)
    {
        try
        {
            await _gate.CompleteAsync(operationId, OperationState.Failed, error, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The log is unavailable: the cancel stays active and agent-start reconciliation (MN-025) settles it.
        }
    }

    private Task<GateResult> Accept(OperationCancelRequest request, string targetId, MasterTriple master, JsonElement payload, CancellationToken ct) =>
        _gate.AcceptAsync(request, OperationKinds.OperationCancel, targetId, master, payload, ct);

    private static MutationResult FromGate(GateResult result) =>
        result.Error is not null
            ? MutationResult.Refused(result.Error)
            : MutationResult.Ok(StatusCodes.Status200OK, result.Status!);
}
