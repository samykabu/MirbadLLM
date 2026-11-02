using System.Text.Json.Serialization;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// <c>GET /agent/v1/info</c> (design §3.6, §4.3): identity, protocol range and the accepted master triple. Needs no
/// <c>MirbadLLM-Protocol</c> header; allowed even when the node is Incompatible or Pending takeover.
/// </summary>
public sealed record InfoResponse
{
    /// <summary>Node id.</summary>
    public required string NodeId { get; init; }

    /// <summary>Node uid (must match the registry; a mismatch is "identity mismatch", design §9.4).</summary>
    public required string NodeUid { get; init; }

    /// <summary>Agent informational version.</summary>
    public required string AgentVersion { get; init; }

    /// <summary>Protocol versions the agent speaks.</summary>
    public required ProtocolRange Protocol { get; init; }

    /// <summary>Random id of this agent boot (event and telemetry cursors are per boot).</summary>
    public required string BootId { get; init; }

    /// <summary>When this agent boot started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The agent's clock now (the master derives clock skew from it).</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>The highest master triple the agent has accepted; null when it never accepted one.</summary>
    public MasterTriple? AcceptedMaster { get; init; }
}

/// <summary>
/// <c>GET /agent/v1/status</c> (design §4.3, §4.7): a consistent snapshot built under the agent's state lock.
/// <see cref="Seq"/> is the event sequence number it is consistent with: the master applies the snapshot, then
/// subscribes to <c>/events?bootId={BootId}&amp;afterSeq={Seq}</c>, so nothing is lost or applied twice.
/// </summary>
public sealed record StatusSnapshot
{
    /// <summary>Agent boot id.</summary>
    public required string BootId { get; init; }

    /// <summary>Event watermark of this snapshot.</summary>
    public required long Seq { get; init; }

    /// <summary>Agent clock when the snapshot was built.</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>Subsystem health; Degraded reasons are listed.</summary>
    public required NodeHealth Health { get; init; }

    /// <summary>Memory now.</summary>
    public required NodeMemorySnapshot Memory { get; init; }

    /// <summary>Start reservations held (§4.8).</summary>
    public IReadOnlyList<ReservationInfo> Reservations { get; init; } = [];

    /// <summary>Every runtime instance with mirbadllm labels on the node.</summary>
    public IReadOnlyList<RuntimeStatus> Runtimes { get; init; } = [];

    /// <summary>Downloads (running and recently finished).</summary>
    public IReadOnlyList<NodeDownloadStatus> Downloads { get; init; } = [];

    /// <summary>Non-terminal operations plus the last 100 terminal ones (<see cref="OperationRetention.StatusRecentCount"/>).</summary>
    public IReadOnlyList<OperationStatus> Operations { get; init; } = [];
}

/// <summary>Agent-side health (design §3.5 Degraded reasons). Empty <see cref="Reasons"/> = healthy.</summary>
public sealed record NodeHealth
{
    /// <summary>Why the node should be shown Degraded. Empty when healthy.</summary>
    public IReadOnlyList<HealthReason> Reasons { get; init; } = [];

    /// <summary>True when any reason is present. Not serialized.</summary>
    [JsonIgnore]
    public bool Degraded => Reasons.Count > 0;
}

/// <summary>One Degraded reason (design §3.5).</summary>
/// <param name="Code">A <see cref="HealthReasonCodes"/> constant.</param>
/// <param name="Message">Plain-words reason for the UI.</param>
public sealed record HealthReason(string Code, string Message);

/// <summary>Degraded reason codes (design §3.5). The last three are computed by the master, the rest by the agent.</summary>
public static class HealthReasonCodes
{
    /// <summary>Docker is unreachable.</summary>
    public const string DockerUnreachable = "docker_unreachable";

    /// <summary><c>nvidia-smi</c> fails.</summary>
    public const string GpuProbeFailing = "gpu_probe_failing";

    /// <summary>hostd is missing on a node that had it.</summary>
    public const string HostdMissing = "hostd_missing";

    /// <summary>A model root has less than 5 % free.</summary>
    public const string ModelRootLow = "model_root_low";

    /// <summary>The operation log cannot be written.</summary>
    public const string StateUnavailable = "state_unavailable";

    /// <summary>Clock skew &gt; 30 s (master-computed).</summary>
    public const string ClockSkew = "clock_skew";

    /// <summary>Agent certificate expires in &lt; 14 days (master-computed).</summary>
    public const string CertificateExpiring = "certificate_expiring";

    /// <summary>Agent older than the recommended version (master-computed).</summary>
    public const string AgentOutdated = "agent_outdated";

    /// <summary>HTTP/2 could not be negotiated (master-computed, design §7.2).</summary>
    public const string Http2NotNegotiated = "http2_not_negotiated";

    /// <summary>
    /// A placement on this node names a GPU UUID the node no longer reports (card removed or replaced; master-computed from
    /// the last capabilities, design §5.1, MN-106/MN-108). The model and node pages flag it; its launch is refused with 422
    /// <c>gpu_missing</c>. Added by MN-101 (D73).
    /// </summary>
    public const string PlacedGpuMissing = "placed_gpu_missing";

    /// <summary>
    /// A CA rotation is open and this node has not committed a certificate under the new CA yet (master-computed,
    /// design §10.2 CA rotation, MN-116). Added by MN-101 (D73).
    /// </summary>
    public const string CaRotationPending = "ca_rotation_pending";

    /// <summary>
    /// An installed executable version no longer matches its install manifest (agent-computed, design-p3p4 §A.1.2,
    /// MN-222): that version is unusable (<c>RuntimeCapability.Versions[].verified = false</c>, launches get 422
    /// <c>executable_unverified</c>); other versions keep working. Added by MN-202 (D116).
    /// </summary>
    public const string ExecutableUnverified = "executable_unverified";

    /// <summary>
    /// A GPU's VRAM used by programs other than mirbadllm's models (<c>used − Σ measured model deltas</c>) exceeds the
    /// GPU's reserve for 2 consecutive samples (agent-computed, design-p3p4 §A.4.3, MN-214; cleared after 2 samples
    /// below). Models keep running; new starts may be refused with exact numbers. Added by MN-202 (D116).
    /// </summary>
    public const string GpuExternalUseHigh = "gpu_external_use_high";
}

/// <summary>
/// A start reservation (design §4.8): recorded under the node-wide admission lock before the launch is answered 202,
/// held until the instance is Ready or the operation is terminal, persisted with the operation and rebuilt after an agent
/// restart.
/// </summary>
public sealed record ReservationInfo
{
    /// <summary>The launch operation holding it.</summary>
    public required string OperationId { get; init; }

    /// <summary>Model being started.</summary>
    public required string ModelId { get; init; }

    /// <summary>GPU UUIDs (discrete) or the pseudo-GPU <see cref="HostPseudoGpu"/> for a CPU-only process; empty on unified nodes.</summary>
    public IReadOnlyList<string> Gpus { get; init; } = [];

    /// <summary>Memory budget held (GiB).</summary>
    public required double BudgetGiB { get; init; }

    /// <summary>Host-memory budget held on discrete nodes (GiB). Null on unified nodes.</summary>
    public double? HostBudgetGiB { get; init; }

    /// <summary>Loopback port allocated under the same lock.</summary>
    public int? HostPort { get; init; }

    /// <summary>When it was recorded.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The pseudo-GPU name used by CPU-only processes in the start rule (design §4.8).</summary>
    public const string HostPseudoGpu = "host";
}
