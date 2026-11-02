namespace MirbadLLM.Core;

/// <summary>
/// <c>POST /api/fencing/takeover</c> (design §4.2 step 3, §5.6; P2, frozen by MN-101, D73; MN-115): the operator confirms
/// the previous master is stopped. The master reads every reachable agent's accepted epoch, moves above all of them,
/// persists a <see cref="TakeoverRecord"/> and marks unreachable nodes Pending takeover. <see cref="ToString"/> never
/// prints the password.
/// </summary>
public sealed record TakeoverRequest
{
    /// <summary>Legacy password field, accepted for compatibility and ignored; the portal session authorizes the action.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// The confirmation the dialog asks the operator to type, <see cref="TakeoverConfirmations.PreviousMasterStopped"/>
    /// (409 <c>confirmation_required</c> when it differs).
    /// </summary>
    public required string Confirm { get; init; }

    /// <summary>Redacts the password.</summary>
    public override string ToString() => $"TakeoverRequest {{ Confirm = {Confirm}, Password = *** }}";
}

/// <summary>
/// <c>POST /api/fencing/takeover/continue</c> (design §4.2 step 3): continues an authorized takeover for one returning
/// node that holds an epoch not below the master's. Raises the epoch to <c>agentEpoch + 1</c> and records the
/// continuation in <see cref="TakeoverId"/>'s record. Never automatic.
/// </summary>
public sealed record TakeoverContinueRequest
{
    /// <summary>The Pending-takeover node.</summary>
    public required string NodeId { get; init; }

    /// <summary>The takeover record the confirmation names (409 <c>invalid_state</c> when it is not the latest).</summary>
    public required string TakeoverId { get; init; }

    /// <summary>The epoch the dialog showed for the agent (409 <c>invalid_state</c> when the agent now reports another).</summary>
    public required long AgentEpoch { get; init; }

    /// <summary>Legacy password field, accepted for compatibility and ignored; the portal session authorizes the action.</summary>
    public string? Password { get; init; }

    /// <summary>The node id typed by the operator.</summary>
    public required string Confirm { get; init; }

    /// <summary>Redacts the password.</summary>
    public override string ToString() => $"TakeoverContinueRequest {{ NodeId = {NodeId}, TakeoverId = {TakeoverId}, Password = *** }}";
}

/// <summary>Typed confirmations of the takeover dialogs (design §12.1 "states the precondition in its title").</summary>
public static class TakeoverConfirmations
{
    /// <summary>What the operator types to confirm a takeover.</summary>
    public const string PreviousMasterStopped = "the previous master is stopped";
}

/// <summary>How one node stands towards this master's epoch (design §4.2, §3.5 Pending takeover).</summary>
public enum NodeFencingState
{
    /// <summary>The agent accepted this master (or has accepted none yet): normal.</summary>
    Current,

    /// <summary>The agent answered <c>stale_master</c>: it holds a higher epoch. A takeover is needed.</summary>
    Stale,

    /// <summary>The agent answered <c>competing_master</c>: another master drives it at the same epoch.</summary>
    Competing,

    /// <summary>Unreachable during the last takeover; will accept the master's epoch on its next mutation if it is higher.</summary>
    PendingTakeover,

    /// <summary>Returned holding an epoch ≥ the master's: blocked until the operator continues the takeover.</summary>
    NeedsContinuation,

    /// <summary>Not contacted yet.</summary>
    Unknown,
}

/// <summary>One node in <see cref="FencingStatus"/>.</summary>
/// <param name="NodeId">Node id.</param>
/// <param name="State">Fencing state.</param>
/// <param name="AgentEpoch">The epoch the agent reports accepted (<c>/info</c>); null when unknown or none.</param>
public sealed record NodeFencingStatus(string NodeId, NodeFencingState State, long? AgentEpoch);

/// <summary>
/// <c>GET /api/fencing</c> and the source of the takeover banner (design §12.1 fencing banner; MN-115 computes, MN-121
/// shows): the master's identity, whether a takeover is requested or needed, the last takeover and every node's state.
/// </summary>
public sealed record FencingStatus
{
    /// <summary>The master's instance id.</summary>
    public required string InstanceId { get; init; }

    /// <summary>The master's epoch now.</summary>
    public required long Epoch { get; init; }

    /// <summary>True while the takeover flag file or one-shot option asks for a takeover that has not completed.</summary>
    public bool TakeoverRequested { get; init; }

    /// <summary>True while <c>MirbadLLM:Fencing:Takeover</c> is set in configuration (shown as a red warning, W1 review MN-090).</summary>
    public bool TakeoverOptionSet { get; init; }

    /// <summary>True when any node is <see cref="NodeFencingState.Stale"/>, <see cref="NodeFencingState.Competing"/> or <see cref="NodeFencingState.NeedsContinuation"/>: the failed-state banner shows.</summary>
    public bool ActionNeeded { get; init; }

    /// <summary>The latest takeover record, when any.</summary>
    public TakeoverRecord? LastTakeover { get; init; }

    /// <summary>Every non-revoked node.</summary>
    public IReadOnlyList<NodeFencingStatus> Nodes { get; init; } = [];
}

/// <summary>
/// Takeover completion on the master (design §4.2 step 3; P2, frozen by MN-101, D73), implemented by
/// <c>MirbadLLM.Orchestration.Fencing.TakeoverService</c> (MN-115) on top of <see cref="IMasterIdentity"/>, consumed by
/// the fencing API (MN-115) and the takeover banner and dialog (MN-121).
/// <para>Invariants: never automatic — every takeover and continuation is an operator action with a typed confirmation;
/// the epoch is persisted (with the record) before any agent sees it; a continuation only raises the epoch.</para>
/// </summary>
public interface IFencingCoordinator
{
    /// <summary>Incremented on every change of <see cref="GetStatus"/>; the banner polls it.</summary>
    long Version { get; }

    /// <summary>The current status (from memory; no agent call).</summary>
    FencingStatus GetStatus();

    /// <summary>
    /// Performs a takeover: reads every reachable agent's accepted epoch, raises the epoch to at least
    /// <c>max(current, all reachable agents' epochs) + 1</c>, persists the record, deletes the flag, and marks unreachable
    /// nodes Pending takeover.
    /// </summary>
    Task<TakeoverRecord> TakeOverAsync(string actor, CancellationToken ct = default);

    /// <summary>
    /// Continues the takeover for one returning node holding <paramref name="agentEpoch"/>: raises the epoch to at least
    /// <c>agentEpoch + 1</c> and records the continuation. Refused (<see cref="InvalidOperationException"/>, 409
    /// <c>invalid_state</c>) when the node is not <see cref="NodeFencingState.NeedsContinuation"/> or reports another epoch.
    /// </summary>
    Task<TakeoverRecord> ContinueAsync(string nodeId, string takeoverId, long agentEpoch, string actor, CancellationToken ct = default);
}
