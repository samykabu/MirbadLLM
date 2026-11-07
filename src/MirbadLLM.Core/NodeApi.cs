using System.Text.Json.Serialization;

namespace MirbadLLM.Core;

// Master admin API for nodes (design §5.6, §12.1; P2, frozen by MN-101, D73). Admin-cookie protected like the rest of
// /api, JSON web defaults (camelCase) with the master's enum converter. Implemented by MN-102 (NodeApiEndpoints), used by
// the Nodes page (MN-117). Refusals are ProblemDetails with an ErrorCodes value in the "code" extension.

/// <summary>
/// <c>POST /api/nodes/join-tokens</c> (design §3.2, §5.6): creates a join token for a new node (or, with
/// <see cref="Rekey"/>, a rekey token for an enrolled one). Requires a signed-in admin session
/// (403 <c>reauthentication_failed</c>). <see cref="ToString"/> never prints the password.
/// </summary>
public sealed record JoinTokenCreateRequest
{
    /// <summary>Node id slug <c>^[a-z0-9][a-z0-9-]{0,39}$</c>; for a new node it must not exist yet (or exist only as a pending record).</summary>
    public required string NodeId { get; init; }

    /// <summary>Display name stored on the pending record. Null = the id.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Token lifetime in minutes, 10 – 1,440 (registry bounds <c>JsonNodeRegistry.MinJoinTokenLifetime</c>/<c>MaxJoinTokenLifetime</c>). Null = 60.</summary>
    public int? TtlMinutes { get; init; }

    /// <summary>Legacy password field, accepted for compatibility and ignored; the portal session authorizes the action.</summary>
    public string? Password { get; init; }

    /// <summary>True: a <see cref="JoinTokenPurpose.Rekey"/> token for an enrolled, non-revoked node (<c>join --rekey</c>, MN-114).</summary>
    public bool Rekey { get; init; }

    /// <summary>Redacts the password.</summary>
    public override string ToString() => $"JoinTokenCreateRequest {{ NodeId = {NodeId}, Rekey = {Rekey}, Password = *** }}";
}

/// <summary>Answer of <c>POST /api/nodes/join-tokens</c> (design §3.2, §12.1 Add node). The token is shown once and never stored.</summary>
public sealed record JoinTokenCreateResponse
{
    /// <summary>The node id the token is bound to.</summary>
    public required string NodeId { get; init; }

    /// <summary>The token (32 random bytes, base64url). Secret: shown once, never logged.</summary>
    public required string Token { get; init; }

    /// <summary>The CA SPKI hash the agent pins before sending the token (<c>sha256:…</c> form used on the command line).</summary>
    public required string CaHash { get; init; }

    /// <summary>The enroll listener URL, e.g. <c>https://mirbad.example.com:8443</c>.</summary>
    public required string EnrollUrl { get; init; }

    /// <summary>When the token stops working.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Purpose: join or rekey.</summary>
    public JoinTokenPurpose Purpose { get; init; } = JoinTokenPurpose.Join;

    /// <summary>Ready-to-copy command lines per OS.</summary>
    public required JoinCommands Commands { get; init; }

    /// <summary>
    /// The Linux agent bundles the master serves (<c>GET /agent/bundles</c>) that <see cref="JoinCommands.Linux"/> downloads
    /// and checks; null when this master has none (a dev run without the image): the command then expects
    /// <c>install-agent.sh</c> in the current folder.
    /// </summary>
    public AgentDownload? AgentDownload { get; init; }

    /// <summary>Redacts the token.</summary>
    public override string ToString() => $"JoinTokenCreateResponse {{ NodeId = {NodeId}, ExpiresAt = {ExpiresAt:O}, Token = *** }}";
}

/// <summary>The agent bundles a join or rekey command downloads (no secrets: the bundles are public, anonymous downloads).</summary>
public sealed record AgentDownload
{
    /// <summary>
    /// One entry per architecture the master has a bundle for, in a fixed order (linux-x64, linux-arm64, then win-x64 from
    /// MN-218: a <c>.zip</c>, filled into <see cref="JoinCommands.Windows"/>; MN-202, D116).
    /// </summary>
    public required IReadOnlyList<AgentBundleLink> Bundles { get; init; }
}

/// <summary>An agent bundle: where to download it and the SHA-256 the command checks before running anything from it.</summary>
/// <param name="Rid">.NET runtime identifier: <c>linux-x64</c>, <c>linux-arm64</c> or (P4) <c>win-x64</c>.</param>
/// <param name="Url">Absolute download URL (<c>http(s)://{master}/agent/bundles/{rid}.tar.gz</c>; <c>{rid}.zip</c> for <c>win-x64</c>).</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the tarball.</param>
/// <param name="Version">The bundle's version (the source revision it was built from).</param>
public sealed record AgentBundleLink(string Rid, string Url, string Sha256, string Version);

/// <summary>The join command per OS (design §12.1: Line tabs Linux systemd, Windows, macOS). Each contains the token.</summary>
/// <param name="Linux">Linux (systemd service, MN-103 installer).</param>
/// <param name="Windows">Windows (P4); null until supported.</param>
/// <param name="Macos">macOS (later); null until supported.</param>
public sealed record JoinCommands(string Linux, string? Windows = null, string? Macos = null)
{
    /// <summary>Redacts the commands (they embed the token).</summary>
    public override string ToString() => "JoinCommands { *** }";
}

/// <summary>
/// <c>POST /api/nodes/{id}/approve</c> (design §3.2): approves a PendingApproval node. The operator has seen the source
/// address, OS/arch, GPUs and SPKI fingerprint. <see cref="ExpectedSpkiSha256"/> guards against approving a different
/// enrollment than the one shown (409 <c>invalid_state</c> when the pending pin changed).
/// </summary>
public sealed record NodeApproveRequest
{
    /// <summary>The pending pin the dialog showed. Null = do not check (API callers).</summary>
    public string? ExpectedSpkiSha256 { get; init; }

    /// <summary>Agent address to store (<c>https://host:9443</c>). Null = the address the agent reported first.</summary>
    public string? Address { get; init; }

    /// <summary>Other addresses (tried in order). Null = keep.</summary>
    public IReadOnlyList<string>? AltAddresses { get; init; }
}

/// <summary>
/// <c>PATCH /api/nodes/{id}</c> (design §5.6, §12.1 Settings): every property is optional, absent (null) = keep the stored
/// value. Ids, uid, pins, approval, draining and revocation are not patchable (they have their own endpoints).
/// </summary>
public sealed record NodePatchRequest
{
    /// <summary>Display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Agent base address.</summary>
    public string? Address { get; init; }

    /// <summary>Other addresses.</summary>
    public IReadOnlyList<string>? AltAddresses { get; init; }

    /// <summary>Host memory reserve (unified: the pool; discrete: host RAM).</summary>
    public double? MemoryReserveGiB { get; init; }

    /// <summary>Per-GPU reserves by UUID (replaces the whole map when present).</summary>
    public IReadOnlyDictionary<string, double>? GpuReserveGiB { get; init; }

    /// <summary>Concurrent starts; 0 = back to the default (null in the record).</summary>
    public int? MaxConcurrentStarts { get; init; }

    /// <summary>Default vLLM image; empty string = clear.</summary>
    public string? DefaultVllmImage { get; init; }

    /// <summary>Approved digest of the default vLLM image; empty string = clear.</summary>
    public string? DefaultVllmImageDigest { get; init; }

    /// <summary>Default llama.cpp image; empty string = clear.</summary>
    public string? DefaultLlamaCppImage { get; init; }

    /// <summary>Approved digest of the default llama.cpp image; empty string = clear.</summary>
    public string? DefaultLlamaCppImageDigest { get; init; }

    /// <summary>Labels (replaces the list when present).</summary>
    public IReadOnlyList<string>? Labels { get; init; }
}

/// <summary>
/// <c>DELETE /api/nodes/{id}</c> body (design §5.6, §12.1 revoke confirm): revokes the node. Refused with 409
/// <c>models_placed</c> while models are placed there unless <see cref="UnplaceModels"/>; with it those models are stopped
/// (best effort) and unplaced. <see cref="ToString"/> never prints the password.
/// </summary>
public sealed record NodeRevokeRequest
{
    /// <summary>Legacy password field, accepted for compatibility and ignored; the portal session authorizes the action.</summary>
    public string? Password { get; init; }

    /// <summary>The node id typed by the operator (409 <c>confirmation_required</c> when it differs).</summary>
    public required string Confirm { get; init; }

    /// <summary>Unplace the models placed on the node.</summary>
    public bool UnplaceModels { get; init; }

    /// <summary>Redacts the password.</summary>
    public override string ToString() => $"NodeRevokeRequest {{ Confirm = {Confirm}, UnplaceModels = {UnplaceModels}, Password = *** }}";
}

/// <summary>Answer of <c>DELETE /api/nodes/{id}</c>.</summary>
/// <param name="NodeId">The revoked node.</param>
/// <param name="UnplacedModelIds">Models that became unplaced.</param>
/// <param name="ExpiredJoinTokens">Unused join tokens of the node that were expired.</param>
public sealed record NodeRevokeResult(string NodeId, IReadOnlyList<string> UnplacedModelIds, int ExpiredJoinTokens);

/// <summary>
/// One element of <c>GET /api/nodes</c> and the body of <c>GET /api/nodes/{id}</c> (design §5.6, §12.1): the stored
/// record, the live view and counts. Holds no secret (pins are public-key hashes).
/// </summary>
public sealed record NodeView
{
    /// <summary>The <c>nodes.json</c> record (revoked nodes included in <c>GET /api/nodes?includeRevoked=true</c> only).</summary>
    public required NodeRecord Node { get; init; }

    /// <summary>The live view; null for a node the monitor does not watch (revoked, or pending without a pin).</summary>
    public NodeLiveStatus? Live { get; init; }

    /// <summary>Models placed on the node.</summary>
    public int PlacedModels { get; init; }

    /// <summary>Placed models that are Ready.</summary>
    public int ReadyModels { get; init; }

    /// <summary>The node's benchmark or tuning job, when one runs (§6.5).</summary>
    public NodeJobSummary? Job { get; init; }
}

/// <summary>
/// <c>GET /api/nodes/{id}/fit?modelId=</c> or <c>POST /api/nodes/{id}/fit</c> with a definition (design §5.6, §6.2):
/// admission preview from the last telemetry (≤ 5 s old). The master never refuses on its own pre-check when the node
/// is reachable; the agent decides at launch. Computed by MN-108 (<c>PlacementFit</c>), used by the editor (MN-118).
/// </summary>
public sealed record NodeFitRequest
{
    /// <summary>An existing model to check (its stored budget and placement).</summary>
    public string? ModelId { get; init; }

    /// <summary>A posted definition (editor, unsaved); wins over <see cref="ModelId"/>.</summary>
    public ModelDefinition? Definition { get; init; }

    /// <summary>GPU UUIDs to check on a discrete node; null = the definition's placement.</summary>
    public IReadOnlyList<string>? GpuUuids { get; init; }
}

/// <summary>Answer of the fit preview (design §6.2: "fits on dgx: needs 36 GiB, 41 GiB free after the 8 GiB reserve").</summary>
public sealed record NodeFitResult
{
    /// <summary>Node id.</summary>
    public required string NodeId { get; init; }

    /// <summary>Memory model of the node.</summary>
    public required MemoryModel Model { get; init; }

    /// <summary>True when the budget fits now; false when it does not; null when unknown (no fresh telemetry, node not serving).</summary>
    public bool? Fits { get; init; }

    /// <summary>Budget asked for (unified: whole model; discrete: total over the GPUs).</summary>
    public required double NeedGiB { get; init; }

    /// <summary>Unified: available − reserve − reservations. Discrete: the smallest per-GPU free after reserve. Null when unknown.</summary>
    public double? FreeGiB { get; init; }

    /// <summary>Reserve applied (unified pool or host).</summary>
    public double ReserveGiB { get; init; }

    /// <summary>Per-GPU fit on discrete nodes; empty on unified nodes.</summary>
    public IReadOnlyList<GpuFit> Gpus { get; init; } = [];

    /// <summary>Host-RAM fit on discrete nodes; null on unified nodes.</summary>
    public HostFit? Host { get; init; }

    /// <summary>Plain-words hint for the editor ("41.2 GiB free after reserve — fits").</summary>
    public required string Message { get; init; }

    /// <summary>When the telemetry used was sampled; null when none.</summary>
    public DateTimeOffset? MeasuredAt { get; init; }

    /// <summary>
    /// The GGUF estimate behind <see cref="NeedGiB"/> for a llama.cpp model whose GGUF summary was readable (design-p3p4
    /// §A.1.10, MN-224; MN-202, D116); null otherwise and omitted, so vLLM answers are unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GgufMemoryEstimate? Estimate { get; init; }
}

/// <summary>Fit of one GPU (discrete).</summary>
/// <param name="Uuid">GPU UUID.</param>
/// <param name="Index">Current index (display).</param>
/// <param name="NeedGiB">Per-GPU budget (budget ÷ n; for llama.cpp the GPU's <see cref="LlamaCppSettings.GpuBudgetsGiB"/> entry when set).</param>
/// <param name="FreeGiB">Free VRAM after reserve and other reservations; null when unknown.</param>
/// <param name="ReserveGiB">Reserve applied to this GPU.</param>
/// <param name="Fits">Null when unknown.</param>
public sealed record GpuFit(string Uuid, int? Index, double NeedGiB, double? FreeGiB, double ReserveGiB, bool? Fits);

/// <summary>Fit of the host-RAM part (discrete).</summary>
/// <param name="NeedGiB">Host budget (<see cref="ModelDefinition.HostMemoryBudgetGiB"/> or the estimate of §6.1).</param>
/// <param name="FreeGiB">Host available after reserve and other reservations; null when unknown.</param>
/// <param name="Estimated">True when <paramref name="NeedGiB"/> is the §6.1 estimate.</param>
/// <param name="Fits">Null when unknown.</param>
public sealed record HostFit(double NeedGiB, double? FreeGiB, bool Estimated, bool? Fits);

/// <summary>
/// Problem codes of <c>placement_invalid</c> (design §5.1 validation; MN-108 gives each its own message). Used in
/// <see cref="PlacementProblem.Code"/>.
/// </summary>
public static class PlacementProblemCodes
{
    /// <summary>The placement names a node that is not in <c>nodes.json</c>.</summary>
    public const string NodeUnknown = "node_unknown";

    /// <summary>The node is revoked.</summary>
    public const string NodeRevoked = "node_revoked";

    /// <summary>A GPU UUID is not in the node's last capabilities.</summary>
    public const string GpuUnknown = "gpu_unknown";

    /// <summary>A GPU UUID is listed twice.</summary>
    public const string GpuDuplicate = "gpu_duplicate";

    /// <summary>A tensor-parallel placement on GPUs of different model or <c>memory.total</c> (D25).</summary>
    public const string GpuCapacityMismatch = "gpu_capacity_mismatch";

    /// <summary>GPUs (UUIDs or indices) on a unified node.</summary>
    public const string GpusOnUnifiedNode = "gpus_on_unified_node";

    /// <summary>A discrete node needs at least one GPU (a CPU-only process is P3).</summary>
    public const string GpusRequired = "gpus_required";

    /// <summary>The runtime kind + delivery is not in the node's capabilities.</summary>
    public const string RuntimeUnavailable = "runtime_unavailable";

    /// <summary><c>LlamaCpp</c> present while the kind is not llama.cpp, or missing while it is.</summary>
    public const string LlamaCppSettingsMismatch = "llamacpp_settings_mismatch";

    /// <summary>The node is draining (no new placements, §3.5).</summary>
    public const string NodeDraining = "node_draining";

    // ---- P3 (MN-202, D116) ----

    /// <summary>
    /// <see cref="LlamaCppSettings.GpuBudgetsGiB"/> has a different count than the placed GPUs, a non-positive entry, or a
    /// sum other than <see cref="ModelDefinition.MemoryBudgetGiB"/> (± 0.01 GiB).
    /// </summary>
    public const string GpuBudgetsMismatch = "gpu_budgets_mismatch";

    /// <summary>
    /// <see cref="LlamaCppSettings.ExecutableVersion"/> names a version the node does not report as installed and verified
    /// (<see cref="RuntimeCapability.Versions"/>).
    /// </summary>
    public const string ExecutableVersionUnavailable = "executable_version_unavailable";

    /// <summary>
    /// A llama.cpp setting is invalid for the launch: unknown <see cref="LlamaCppSettings.Pooling"/>, an unparsable
    /// <see cref="LlamaCppSettings.ChatTemplate"/>, or ExtraArgs that <see cref="LlamaCppFlags.RefusedExtraArgs"/> refuses.
    /// </summary>
    public const string LlamaCppSettingsInvalid = "llamacpp_settings_invalid";

    // ---- macOS Phase 3 (docs/macos/implementation-plan.md) ----

    /// <summary><c>Mlx</c> present while the kind is not MLX, or missing while it is.</summary>
    public const string MlxSettingsMismatch = "mlx_settings_mismatch";

    /// <summary>
    /// An MLX setting is invalid for the launch: a folder that is not a plain relative path, a size out of range, chat
    /// template arguments that are not a JSON object, or ExtraArgs that <see cref="MlxFlags.RefusedExtraArgs"/> refuses.
    /// </summary>
    public const string MlxSettingsInvalid = "mlx_settings_invalid";

    /// <summary>Shared container settings are missing or attached to the wrong runtime.</summary>
    public const string ContainerSettingsMismatch = "container_settings_mismatch";

    /// <summary>Shared container image, digest, model path or extra arguments are invalid.</summary>
    public const string ContainerSettingsInvalid = "container_settings_invalid";
}

/// <summary>One validation problem of a placement (design §5.1).</summary>
/// <param name="Code">A <see cref="PlacementProblemCodes"/> constant.</param>
/// <param name="Message">Plain-words message for the editor.</param>
/// <param name="GpuUuid">The GPU concerned, when any.</param>
public sealed record PlacementProblem(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? GpuUuid = null);

/// <summary>
/// A placement failed validation (design §5.1; thrown by the registry's validators, MN-108). An
/// <see cref="ArgumentException"/>, so today's callers keep answering 400; the admin API adds code
/// <c>placement_invalid</c> and the problems.
/// </summary>
public sealed class PlacementValidationException : ArgumentException
{
    /// <summary>Creates the exception (prefer the problems constructor).</summary>
    public PlacementValidationException()
        : this([])
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public PlacementValidationException(string message)
        : base(message)
    {
        Problems = [];
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public PlacementValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [];
    }

    /// <summary>Creates the exception for the problems found (the message joins theirs).</summary>
    public PlacementValidationException(IReadOnlyList<PlacementProblem> problems)
        : base(problems is { Count: > 0 } ? string.Join(" ", problems.Select(p => p.Message)) : "The placement is invalid.")
    {
        Problems = problems ?? [];
    }

    /// <summary>Every problem found, each with its own message.</summary>
    public IReadOnlyList<PlacementProblem> Problems { get; }
}
