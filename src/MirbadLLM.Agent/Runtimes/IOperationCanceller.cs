using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// Cancels running operations of some kinds for <c>POST /operations/{operationId}/cancel</c> (design §4.3, §4.4 item 7).
/// The runtime package registers one for <see cref="OperationKinds.Launch"/>; the storage package (MN-020) is expected to
/// register one for image pulls and downloads. A kind no canceller claims answers 409 <c>invalid_state</c>.
/// </summary>
public interface IOperationCanceller
{
    /// <summary>Operation kinds (<see cref="OperationKinds"/>) this canceller handles.</summary>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>
    /// Null when <paramref name="target"/> (non-terminal) can still be cancelled; otherwise the refusal, typically 409
    /// <c>invalid_state</c> (for a launch: its instance already exists, so it must be stopped instead). Called before the
    /// cancel operation is recorded, so a refusal leaves no record.
    /// </summary>
    AgentException? CheckCancellable(OperationStatus target);

    /// <summary>
    /// Cancels <paramref name="target"/> and returns its status afterwards (normally <see cref="Core.OperationState.Cancelled"/>;
    /// still active if it did not stop within the canceller's wait). Throws <see cref="AgentException"/> when it became
    /// impossible after <see cref="CheckCancellable"/> passed.
    /// </summary>
    Task<OperationStatus> CancelAsync(OperationStatus target, CancellationToken ct = default);
}
