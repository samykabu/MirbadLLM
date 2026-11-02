using MirbadLLM.Core;

namespace MirbadLLM.Agent.Abstractions;

/// <summary>Decision of <see cref="IFencingState"/> for a presented master triple (design §4.2 layer 2).</summary>
public enum FencingDecision
{
    /// <summary>Equal to the accepted triple, or a higher epoch that is now persisted and accepted.</summary>
    Accepted,

    /// <summary>Lower epoch than the accepted one: 409 <c>stale_master</c>.</summary>
    StaleMaster,

    /// <summary>Equal epoch but another instance id or boot id: 409 <c>competing_master</c>.</summary>
    CompetingMaster,
}

/// <summary>Result of a fencing check.</summary>
/// <param name="Decision">The decision.</param>
/// <param name="Accepted">The accepted triple after the check (the presented one when accepted).</param>
public sealed record FencingCheck(FencingDecision Decision, MasterTriple? Accepted);

/// <summary>
/// The agent's fencing state (design §4.2 layer 2), implemented in <c>MirbadLLM.Agent.Fencing</c> (MN-014).
/// <para>Invariants:</para>
/// <list type="bullet">
/// <item>The highest accepted triple is persisted atomically and flushed in the state directory before a higher epoch is
/// reported as accepted, and survives restarts.</item>
/// <item>Lower epoch → <see cref="FencingDecision.StaleMaster"/>; equal epoch with a different instance or boot →
/// <see cref="FencingDecision.CompetingMaster"/> (the first master to reach the node at an epoch wins); higher epoch →
/// persisted, then <see cref="FencingDecision.Accepted"/>.</item>
/// <item>Before every destructive stage (stop, remove, create, folder delete) the operation's own triple is re-checked
/// with <see cref="IsCurrent"/>; a superseded operation stops there and fails with <c>superseded</c>.</item>
/// <item>Checks are serialized: two concurrent higher epochs cannot both be accepted out of order.</item>
/// </list>
/// </summary>
public interface IFencingState
{
    /// <summary>The highest accepted triple; null before any master was accepted.</summary>
    MasterTriple? Accepted { get; }

    /// <summary>Checks a mutating request's triple and persists a higher epoch before accepting it.</summary>
    Task<FencingCheck> CheckAsync(MasterTriple presented, CancellationToken ct = default);

    /// <summary>True when <paramref name="operationMaster"/> is still the accepted master (same instance, epoch and boot).</summary>
    bool IsCurrent(MasterTriple operationMaster);
}
