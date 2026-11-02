using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Recovery;

/// <summary>What <see cref="AgentReconciler"/> did with one non-terminal operation found at agent start.</summary>
public enum ReconciliationAction
{
    /// <summary>
    /// Work continues in this process: a launch re-runs its driver phase from the pull, or waits for its instance to report
    /// Ready (the runtime watcher settles it).
    /// </summary>
    Resumed,

    /// <summary>Brought to a terminal state by the reconciler (Succeeded, Failed or Cancelled; the log says which).</summary>
    Completed,

    /// <summary>Handed to the <see cref="Abstractions.IOperationRecoveryHandler"/> of its kind (host services, storage, PKI).</summary>
    Delegated,

    /// <summary>Left as it is: a PKI rotation stays pending until the master commits or aborts it (design §10.2).</summary>
    Kept,
}

/// <summary>One reconciled operation.</summary>
/// <param name="OperationId">Operation id.</param>
/// <param name="Kind">Operation kind (<see cref="OperationKinds"/>).</param>
/// <param name="Stage">The stage it was found at.</param>
/// <param name="Action">What the reconciler did.</param>
/// <param name="State">The operation's state afterwards (from the log).</param>
public sealed record ReconciledOperation(string OperationId, string Kind, string? Stage, ReconciliationAction Action, OperationState State);

/// <summary>Result of <see cref="AgentReconciler.RunAsync"/> (design §4.4 item 5, §4.6), for the startup log and tests.</summary>
public sealed record ReconciliationReport
{
    /// <summary>Every operation that was non-terminal at start, oldest first.</summary>
    public required IReadOnlyList<ReconciledOperation> Operations { get; init; }

    /// <summary>Start reservations rebuilt from non-terminal launches (design §4.8).</summary>
    public required int ReservationsRebuilt { get; init; }

    /// <summary>
    /// Instances with mirbadllm labels that no active operation drives (created before P1, or their operation ended or
    /// was lost), adopted untouched as today.
    /// </summary>
    public required IReadOnlyList<RuntimeStatus> Adopted { get; init; }

    /// <summary>Why the runtime instances could not be listed at start (Docker unreachable); null when they could.</summary>
    public string? InstanceListingError { get; init; }
}
