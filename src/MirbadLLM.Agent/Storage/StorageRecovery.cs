using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// Recovers the storage package's non-terminal operations at agent start (design §4.4 item 5, §6.4), called by
/// <c>AgentReconciler</c> (MN-025) before the listener opens:
/// <list type="bullet">
/// <item><c>download</c>: resume monitoring the helper, or finish the operation from what the helper left
/// (<see cref="AgentDownloadService.ResumeAsync"/>);</item>
/// <item><c>downloadCancel</c>: a desired-state action, applied again (stop and remove the helper) and completed;</item>
/// <item><c>imagePull</c>: run the pull again from its stored, secret-free payload;</item>
/// <item><c>folderDelete</c>: finish the delete unless the master was superseded or the folder is now in use.</item>
/// </list>
/// </summary>
public sealed class StorageRecoveryHandler(
    AgentDownloadService downloads,
    AgentImageService images,
    ModelFolderService folders,
    StorageOperations operations) : IOperationRecoveryHandler
{
    /// <inheritdoc />
    public IReadOnlyList<string> Kinds { get; } =
        [OperationKinds.Download, OperationKinds.DownloadCancel, OperationKinds.ImagePull, OperationKinds.FolderDelete];

    /// <inheritdoc />
    public async Task RecoverAsync(RecoverableOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        switch (operation.Status.Kind)
        {
            case OperationKinds.Download:
                await downloads.ResumeAsync(operation, ct).ConfigureAwait(false);
                break;
            case OperationKinds.DownloadCancel:
                await RecoverCancelAsync(operation, ct).ConfigureAwait(false);
                break;
            case OperationKinds.ImagePull:
                await images.ResumeAsync(operation, ct).ConfigureAwait(false);
                break;
            case OperationKinds.FolderDelete:
                await folders.ResumeDeleteAsync(operation, ct).ConfigureAwait(false);
                break;
            default:
                await operations.CompleteAsync(operation.Status.Id, OperationState.Failed,
                    StorageErrors.Error(ErrorCodes.InternalError, $"The storage package cannot recover '{operation.Status.Kind}' operations."), ct: ct)
                    .ConfigureAwait(false);
                break;
        }
    }

    private async Task RecoverCancelAsync(RecoverableOperation operation, CancellationToken ct)
    {
        try
        {
            var final = await downloads.CancelFolderAsync(operation.Status.Target, ct).ConfigureAwait(false);
            await operations.CompleteAsync(operation.Status.Id, OperationState.Succeeded, result: AgentJson.ToElement(final), ct: ct)
                .ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            await operations.CompleteAsync(operation.Status.Id, OperationState.Failed, ex.Error, ct: ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The storage part of <c>POST /operations/{id}/cancel</c> (design §4.3, §4.4 item 7; the route itself is MN-031's): a
/// running image pull or download started by <paramref name="operationId"/> is cancelled and ends Cancelled. Registered
/// as an <see cref="Runtimes.IOperationCanceller"/> so the shared cancel route reaches pulls and downloads (W2 review
/// MN-092: before, only launches could be cancelled there).
/// </summary>
public sealed class StorageOperationCanceller(AgentDownloadService downloads, AgentImageService images, StorageOperations operations)
    : Runtimes.IOperationCanceller
{
    /// <inheritdoc />
    public IReadOnlyList<string> Kinds { get; } = [OperationKinds.ImagePull, OperationKinds.Download];

    /// <inheritdoc />
    public AgentException? CheckCancellable(OperationStatus target) => null;

    /// <inheritdoc />
    public async Task<OperationStatus> CancelAsync(OperationStatus target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!await TryCancelAsync(target.Id, ct).ConfigureAwait(false))
        {
            // Not running in this process: it just finished, or it is resumed by recovery after a restart.
            var current = operations.Log.Find(target.Id) ?? target;
            if (current.State.IsTerminal())
            {
                return current;
            }

            throw new AgentException(409, new AgentError
            {
                Code = ErrorCodes.InvalidState,
                Message = "Operation " + target.Id + " (" + target.Kind + ") is not running in this agent process; try again shortly.",
            });
        }

        return operations.Log.Find(target.Id) ?? target;
    }

    /// <summary>
    /// Cancels the pull or download started by <paramref name="operationId"/>; false when it is neither (or not running
    /// in this process), so the caller can try other packages.
    /// </summary>
    public async Task<bool> TryCancelAsync(string operationId, CancellationToken ct = default)
    {
        if (await images.CancelAsync(operationId, ct).ConfigureAwait(false))
        {
            return true;
        }

        return await downloads.CancelOperationAsync(operationId, ct).ConfigureAwait(false) is not null;
    }
}
