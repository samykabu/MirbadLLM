using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.HostServices;

/// <summary>
/// Recovers non-terminal host-service operations at agent start (design §4.4 item 8; hostd unchanged), for the
/// startup reconciliation (MN-025) through <see cref="IOperationRecoveryHandler"/>:
/// <list type="bullet">
/// <item><b>Desired-state kinds</b> (<see cref="OperationKinds.DesiredState"/>: start, stop, autostart, whitelist
/// add/update/remove, container start/stop/restart policy, backup-mount DELETE): read the current state; when it already
/// is the desired state the operation Succeeded (nothing is sent), otherwise the action is applied again (doing it twice
/// has the same effect as once), after the envelope-expiry and fencing checks.</item>
/// <item><b>Unverifiable kinds</b> (<see cref="OperationKinds.Unverifiable"/>: service restart, container restart,
/// backup-mount PUT): found <see cref="OperationStages.Dispatched"/> without a terminal record → <see cref="OperationState.Unknown"/>
/// with the observed state; nothing is replayed, a new attempt is a new operation. Found before dispatch → Failed:
/// hostd/Docker was never called, so nothing changed.</item>
/// </list>
/// </summary>
public sealed class HostServiceRecoveryHandler(
    HostServiceOperations operations,
    IOperationLog log,
    IFencingState fencing,
    TimeProvider time,
    ILogger<HostServiceRecoveryHandler> logger) : IOperationRecoveryHandler
{
    /// <inheritdoc/>
    public IReadOnlyList<string> Kinds => HostMutation.Kinds;

    /// <inheritdoc/>
    public async Task RecoverAsync(RecoverableOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var status = operation.Status;
        HostServicesLog.Recovering(logger, status.Id, status.Kind, status.Target, status.Stage ?? "-");

        var mutation = HostMutation.FromPayload(status.Kind, status.Target, operation.Payload);
        if (mutation is null)
        {
            await log.CompleteAsync(status.Id, OperationState.Failed,
                HostServiceErrors.Error(ErrorCodes.InternalError, $"The stored request of this {status.Kind} operation cannot be read; nothing was done."),
                ct: ct).ConfigureAwait(false);
            return;
        }

        // The stored payload carries the envelope; the log's record is authoritative for identity.
        mutation = mutation with { Request = mutation.Request with { OperationId = status.Id } };
        bool dispatched = string.Equals(status.Stage, OperationStages.Dispatched, StringComparison.Ordinal);

        if (!mutation.IsDesiredState)
        {
            if (!dispatched)
            {
                await operations.CompleteAsync(mutation, OperationState.Failed, HostServiceErrors.Error(ErrorCodes.InternalError,
                    "The agent restarted before the action was sent; nothing was changed. Run it again if it is still needed."), null)
                    .ConfigureAwait(false);
                return;
            }

            var observed = await operations.TryObserveAsync(mutation).ConfigureAwait(false);
            string message = "The agent restarted after sending the action and cannot tell whether it ran. " + HostServiceErrors.OutcomeUnknownHint
                + (observed is null ? "" : $" Observed: {observed.Description}.");
            await operations.CompleteAsync(mutation, OperationState.Unknown, HostServiceErrors.Error(ErrorCodes.OutcomeUnknown, message), observed?.Current)
                .ConfigureAwait(false);
            return;
        }

        HostObservation current;
        try
        {
            current = await operations.ObserveAsync(mutation, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || HostdClient.IsTimeout(ex))
        {
            string code = ex is HostServiceDeniedException ? ErrorCodes.Denied
                : mutation.IsContainer ? ErrorCodes.DockerUnavailable
                : ErrorCodes.HostdUnavailable;
            await operations.CompleteAsync(mutation, dispatched ? OperationState.Unknown : OperationState.Failed, HostServiceErrors.Error(code,
                $"The agent restarted and could not read the current state ({ex.Message}). " + HostServiceErrors.OutcomeUnknownHint), null)
                .ConfigureAwait(false);
            return;
        }

        if (current.Matches)
        {
            await operations.CompleteAsync(mutation, OperationState.Succeeded, null, current.Current).ConfigureAwait(false);
            return;
        }

        if (status.ExpiresAt != default && status.ExpiresAt <= time.GetUtcNow())
        {
            await operations.CompleteAsync(mutation, OperationState.Failed, HostServiceErrors.Error(ErrorCodes.OperationExpired,
                $"The operation expired before the agent could finish it; nothing was applied. Observed: {current.Description}."), current.Current)
                .ConfigureAwait(false);
            return;
        }

        if (!fencing.IsCurrent(operation.Master))
        {
            await operations.CompleteAsync(mutation, OperationState.Failed, HostServiceErrors.Error(ErrorCodes.Superseded,
                $"A newer master took over this node; the action was not applied again. Observed: {current.Description}."), current.Current)
                .ConfigureAwait(false);
            return;
        }

        // Desired state not reached: apply it again (idempotent), recording dispatched first as for a live request.
        await operations.ExecuteAcceptedAsync(mutation, operation.Master, ct).ConfigureAwait(false);
    }
}
