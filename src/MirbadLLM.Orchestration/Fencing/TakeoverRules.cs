using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Fencing;

/// <summary>How a returning (or any) agent relates to the master's current triple (design §4.2 step 3).</summary>
public enum ReturningAgentStatus
{
    /// <summary>The agent already accepted exactly this master boot.</summary>
    Current,

    /// <summary>
    /// The agent holds no triple or a lower epoch: it accepts the master on the next mutation and leaves Pending
    /// takeover.
    /// </summary>
    AcceptsOnNextMutation,

    /// <summary>
    /// The agent holds an equal epoch from another instance or boot, or a higher epoch: it stays blocked (no routing,
    /// no commands except <c>info</c>) until the operator continues the authorized takeover. Never automatic.
    /// </summary>
    NeedsContinuation,
}

/// <summary>Result of <see cref="TakeoverRules.AssessAgent"/>.</summary>
/// <param name="Status">What the agent will do with the master's triple.</param>
/// <param name="AgentEpoch">The epoch the agent holds; null when it never accepted a master.</param>
public sealed record ReturningAgentAssessment(ReturningAgentStatus Status, long? AgentEpoch);

/// <summary>
/// Pure rules of the explicit takeover (design §4.2 layer 3, Appendix B round 2 #2): the takeover epoch is
/// <c>max(restored epoch, every reachable agent's accepted epoch) + 1</c>; unreachable agents become Pending takeover;
/// a returning agent whose epoch is not below the master's needs an operator continuation that raises the master to
/// <c>agentEpoch + 1</c>. The side effects (reading <c>/info</c>, node states, the API) belong to the takeover service;
/// persistence goes through <see cref="IMasterIdentity.CompleteTakeoverAsync"/>.
/// </summary>
public static class TakeoverRules
{
    /// <summary>
    /// The epoch to take over with: one above the highest of <paramref name="currentEpoch"/> (the restored epoch as
    /// incremented by this boot) and every answering agent's accepted epoch.
    /// </summary>
    public static long ComputeEpoch(long currentEpoch, IEnumerable<TakeoverNodeEpoch> answered)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(currentEpoch);
        ArgumentNullException.ThrowIfNull(answered);
        long highest = currentEpoch;
        foreach (TakeoverNodeEpoch node in answered)
        {
            if (node.AcceptedEpoch is long epoch && epoch > highest)
            {
                highest = epoch;
            }
        }

        return checked(highest + 1);
    }

    /// <summary>
    /// Builds a new takeover record from the agents that answered <c>/info</c> and the node ids that did not. Pass it to
    /// <see cref="IMasterIdentity.CompleteTakeoverAsync"/>. <paramref name="takeoverId"/> is the one-shot
    /// <see cref="FencingOptions.TakeoverId"/> when the option asked for this takeover (MN-115); null = a new random id.
    /// A node listed in both lists counts as answered.
    /// </summary>
    public static TakeoverRecord CreateRecord(
        long currentEpoch,
        IReadOnlyList<TakeoverNodeEpoch> answered,
        IReadOnlyList<string> unreachable,
        string? @operator,
        DateTimeOffset at,
        string? takeoverId = null)
    {
        ArgumentNullException.ThrowIfNull(answered);
        ArgumentNullException.ThrowIfNull(unreachable);
        if (takeoverId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(takeoverId);
        }

        var answeredIds = new HashSet<string>(answered.Select(a => a.NodeId), StringComparer.Ordinal);
        return new TakeoverRecord
        {
            TakeoverId = takeoverId ?? Guid.NewGuid().ToString("N"),
            Epoch = ComputeEpoch(currentEpoch, answered),
            PreviousEpoch = currentEpoch,
            At = at,
            Operator = @operator,
            Answered = [.. answered],
            Unreachable = [.. unreachable.Distinct(StringComparer.Ordinal).Where(id => !answeredIds.Contains(id))],
        };
    }

    /// <summary>
    /// Predicts the agent's decision for the master's triple, mirroring the agent rules: none or lower → accepted;
    /// the same boot → current; equal epoch with another instance or boot (<c>competing_master</c>) or a higher epoch
    /// (<c>stale_master</c>) → needs an operator continuation.
    /// </summary>
    public static ReturningAgentAssessment AssessAgent(MasterTriple master, MasterTriple? agentAccepted)
    {
        ArgumentNullException.ThrowIfNull(master);
        if (agentAccepted is null)
        {
            return new ReturningAgentAssessment(ReturningAgentStatus.AcceptsOnNextMutation, null);
        }

        if (master.SameBoot(agentAccepted))
        {
            return new ReturningAgentAssessment(ReturningAgentStatus.Current, agentAccepted.Epoch);
        }

        return agentAccepted.Epoch < master.Epoch
            ? new ReturningAgentAssessment(ReturningAgentStatus.AcceptsOnNextMutation, agentAccepted.Epoch)
            : new ReturningAgentAssessment(ReturningAgentStatus.NeedsContinuation, agentAccepted.Epoch);
    }

    /// <summary>
    /// Adds an operator-confirmed continuation for one returning agent: the new epoch is <c>agentEpoch + 1</c>.
    /// Throws <see cref="InvalidOperationException"/> when the agent's epoch is below the master's (no continuation is
    /// needed: it accepts on the next mutation). Pass the result to <see cref="IMasterIdentity.CompleteTakeoverAsync"/>,
    /// which replaces the stored record and raises the epoch.
    /// </summary>
    public static TakeoverRecord AddContinuation(
        TakeoverRecord record,
        string nodeId,
        long agentEpoch,
        long currentEpoch,
        string? @operator,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (agentEpoch < currentEpoch)
        {
            throw new InvalidOperationException(
                $"Node '{nodeId}' holds epoch {agentEpoch}, below the master's {currentEpoch}; no continuation is " +
                "needed, it accepts the master on the next request.");
        }

        var continuation = new TakeoverContinuation(nodeId, agentEpoch, checked(agentEpoch + 1), at, @operator);
        return record with { Continuations = [.. record.Continuations ?? [], continuation] };
    }
}
