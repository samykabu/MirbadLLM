using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Abstractions;

/// <summary>
/// Adds one subsystem's part to the capability report (design §3.3), so each agent package owns its piece:
/// runtimes (MN-015), GPUs and the telemetry feature (MN-018), images, model roots and the download feature (MN-020),
/// host services, backup mount and GPU processes (MN-023). <c>GET /capabilities</c> (MN-014) starts from the base report
/// (protocol, version, OS, arch, CPU, memory) and folds every registered contributor in registration order.
/// </summary>
public interface ICapabilityContributor
{
    /// <summary>
    /// Returns <paramref name="current"/> with this subsystem's fields and <see cref="NodeCapabilities.Features"/> added
    /// (<c>current with { … }</c>). Must not remove what others added, and must not throw for a failing subsystem: it then
    /// reports the subsystem as unavailable and lets <see cref="IHealthContributor"/> say why.
    /// </summary>
    Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default);
}

/// <summary>
/// Reports one subsystem's Degraded reasons (design §3.5): Docker unreachable (MN-015), <c>nvidia-smi</c> failing
/// (MN-018), model root below 5 % free (MN-020), hostd missing on a node that had it (MN-023), operation log not writable
/// (MN-013). <c>GET /status</c> (MN-017) puts the union of all contributors into <see cref="StatusSnapshot.Health"/>.
/// Reasons computed by the master (clock skew, certificate expiry, outdated agent, HTTP/2) are not reported here.
/// </summary>
public interface IHealthContributor
{
    /// <summary>Current reasons (empty when healthy). Fast and never throws; reuse the last probe result where possible.</summary>
    Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default);
}

/// <summary>
/// The agent's downloads as other packages need them (design §4.3, §6.4), implemented by the storage package (MN-020):
/// <c>GET /status</c> (MN-017) lists them, and folder deletion and reconciliation (MN-025) consult them.
/// </summary>
public interface IAgentDownloads
{
    /// <summary>Running and recently finished downloads.</summary>
    Task<IReadOnlyList<NodeDownloadStatus>> ListAsync(CancellationToken ct = default);

    /// <summary>True when a running download writes into <paramref name="folder"/>.</summary>
    Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default);
}

/// <summary>
/// Finishes or fails one family of non-terminal operations at agent start, before the listener opens (design §4.4 items 5
/// and 8). <c>AgentReconciler</c> (MN-025) handles <c>launch</c> and <c>stop</c> itself with the runtime driver and the
/// reservation manager, and hands every other kind to the handler whose <see cref="Kinds"/> contains it: host services
/// (MN-023: desired-state reconcile or <see cref="OperationState.Unknown"/>), downloads and image pulls (MN-020: resume),
/// PKI rotations (MN-036: keep pending). An operation whose kind no handler claims is failed with <c>internal_error</c>.
/// </summary>
public interface IOperationRecoveryHandler
{
    /// <summary>Operation kinds (<see cref="OperationKinds"/>) this handler recovers.</summary>
    IReadOnlyList<string> Kinds { get; }

    /// <summary>
    /// Brings <paramref name="operation"/> to a terminal state or resumes it, recording every change through
    /// <see cref="IOperationLog"/>. Must not replay an unverifiable action (§4.4 item 8) and must re-check fencing with
    /// <see cref="IFencingState.IsCurrent"/> before any destructive step.
    /// </summary>
    Task RecoverAsync(RecoverableOperation operation, CancellationToken ct = default);
}
