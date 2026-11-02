using System.Text.Json;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// Every error code of the multi-node design, one constant each (unique values): agent API errors (§4.3–4.4, §7.2),
/// client-visible router codes (§7.4) and master admin-API codes (§5.4, §5.6). Codes are lowercase snake case. A code
/// used by more than one surface (<see cref="ServerBusy"/>, <see cref="NotFound"/>) is one constant.
/// <para><see cref="AgentCodes"/>, <see cref="RouterCodes"/> and <see cref="ApiCodes"/> say which surface uses which;
/// <see cref="ErrorMappings"/> holds the translation tables.</para>
/// </summary>
public static class ErrorCodes
{
    // ---- Agent API (design §4.3, §4.4, §4.2, §7.2) ----

    /// <summary>400: <c>MirbadLLM-Protocol</c> names a version the agent does not speak; the master renegotiates (§3.6).</summary>
    public const string ProtocolMismatch = "protocol_mismatch";

    /// <summary>400: malformed body, query or path parameter.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>400: <c>MirbadLLM-Request-Time</c> or envelope <c>issuedAt</c> differs from the agent clock by more than 5 minutes (§4.2, §4.3).</summary>
    public const string ClockSkew = "clock_skew";

    /// <summary>403: the TLS peer is not the master (wrong CA, SAN or EKU) — normally refused during the handshake (§4.2).</summary>
    public const string Forbidden = "forbidden";

    /// <summary>404: no such instance, folder, download, operation, unit, container or rotation (a runtime 404 means Stopped).</summary>
    public const string NotFound = "not_found";

    /// <summary>409: the master triple's epoch is lower than the one the agent accepted (§4.2).</summary>
    public const string StaleMaster = "stale_master";

    /// <summary>409: equal epoch but another instance or boot already drives this node (§4.2).</summary>
    public const string CompetingMaster = "competing_master";

    /// <summary>409: the operation id was used with another action, target or payload digest (§4.4 item 2).</summary>
    public const string OperationIdConflict = "operation_id_conflict";

    /// <summary>410: the envelope's <c>expiresAt</c> passed and the id is not in the log; nothing is executed (§4.4 item 6).</summary>
    public const string OperationExpired = "operation_expired";

    /// <summary>409: memory admission refused; details <see cref="AdmissionRefusedDetails"/> (§4.3, §4.8).</summary>
    public const string AdmissionRefused = "admission_refused";

    /// <summary>409: another start holds the node (or the GPU); details <see cref="StartConflictDetails"/> (§4.8).</summary>
    public const string StartConflict = "start_conflict";

    /// <summary>422: the launch spec breaks a guardrail (undeclared digest, mount, reserved label, …); details <see cref="SpecRejectedDetails"/> (§10.3).</summary>
    public const string SpecRejected = "spec_rejected";

    /// <summary>422: placed GPU UUIDs are not on the node; details <see cref="GpuMissingDetails"/> (§5.1).</summary>
    public const string GpuMissing = "gpu_missing";

    /// <summary>409: a folder is used by an instance or a download (§4.3).</summary>
    public const string InUse = "in_use";

    /// <summary>409: a download into that folder is already running (§4.3).</summary>
    public const string AlreadyRunning = "already_running";

    /// <summary>409: the destination folder exists and <c>existing</c> is <c>refuse</c> (§4.3, §6.4).</summary>
    public const string FolderExists = "folder_exists";

    /// <summary>422: no repo file matches the allow patterns (§4.3).</summary>
    public const string NoFilesMatch = "no_files_match";

    /// <summary>507: not enough disk for the download (§4.3, §6.4).</summary>
    public const string InsufficientDisk = "insufficient_disk";

    /// <summary>409: abort after the rotation was committed (§10.2).</summary>
    public const string AlreadyCommitted = "already_committed";

    /// <summary>409: the action is not valid in the current state (e.g. commit before a certificate was installed).</summary>
    public const string InvalidState = "invalid_state";

    /// <summary>503: the agent could not durably record the operation; nothing ran (§4.4 item 1).</summary>
    public const string StateUnavailable = "state_unavailable";

    /// <summary>Operation error: a stage found that its master triple was superseded; the operation stopped (§4.2, §4.4 item 5).</summary>
    public const string Superseded = "superseded";

    /// <summary>503 from the agent proxy before any response: the instance is not running or refused the connection (§7.1, §7.2).</summary>
    public const string ModelNotRunning = "model_not_running";

    /// <summary>503: in-flight limit (agent or master) or upload budget (master) exhausted; <c>Retry-After: 5</c> (§7.2, §7.4).</summary>
    public const string ServerBusy = "server_busy";

    /// <summary>503: Docker is unreachable on the node.</summary>
    public const string DockerUnavailable = "docker_unavailable";

    /// <summary>503: hostd is not installed or does not answer (host services, backup mount, GPU processes).</summary>
    public const string HostdUnavailable = "hostd_unavailable";

    /// <summary>403: hostd's deny list (or the agent's container deny rules) refused the unit or container (§10.3).</summary>
    public const string Denied = "denied";

    /// <summary>409: a protected service action needs the typed name in <c>confirm</c>.</summary>
    public const string ConfirmationRequired = "confirmation_required";

    /// <summary>409: the unit or container is already managed, or the name is taken.</summary>
    public const string Conflict = "conflict";

    /// <summary>501: the node lacks the feature (<see cref="AgentFeatures"/>) the route needs (§3.6).</summary>
    public const string NotSupported = "not_supported";

    /// <summary>504: hostd or Docker did not answer in time (read-only host calls).</summary>
    public const string Timeout = "timeout";

    /// <summary>
    /// 504: a host-service action was dispatched but its outcome cannot be verified (hostd/Docker timed out or the
    /// connection was lost after sending, or the agent restarted after dispatching a restart or backup-mount PUT). The
    /// operation is <see cref="OperationState.Unknown"/>, with the observed state in its message and result; nothing is
    /// replayed (§4.4 item 8). Added by the W2 review (MN-092, D42) so the master can tell it from a plain failure.
    /// </summary>
    public const string OutcomeUnknown = "outcome_unknown";

    /// <summary>500: unexpected agent failure.</summary>
    public const string InternalError = "internal_error";

    // ---- Agent API, P3/P4 (MN-202, design D116; design-p3p4 §A.1.2, §A.1.10, §A.2.2) ----

    /// <summary>
    /// 422: a process launch names a logical executable or version the node's catalogue (<c>Agent:Executables</c>) does not
    /// have. The message names what is installed.
    /// </summary>
    public const string ExecutableMissing = "executable_missing";

    /// <summary>
    /// 422: the named executable version is installed but its files no longer match the install manifest's SHA-256 values
    /// (health reason <c>executable_unverified</c>); reinstall it with <c>install-agent.ps1 -LlamaCpp</c>.
    /// </summary>
    public const string ExecutableUnverified = "executable_unverified";

    /// <summary>422: the file is not a readable GGUF v2/v3 or breaks a reader bound (length, count, allocation).</summary>
    public const string GgufInvalid = "gguf_invalid";

    /// <summary>
    /// 422: an Ollama manifest the puller does not accept (not exactly one model layer, an adapter or projector layer, an
    /// unknown media type, or a model layer whose digest differs from the pinned one).
    /// </summary>
    public const string UnsupportedManifest = "unsupported_manifest";

    // ---- Enrollment (design §3.2) ----

    /// <summary>403: token unknown, expired, used or bound to another node, or the CSR failed proof of possession. One code on purpose.</summary>
    public const string EnrollmentRefused = "enrollment_refused";

    /// <summary>429: the per-IP rate limit (5/minute, like <c>/login/submit</c>) was hit.</summary>
    public const string RateLimited = "rate_limited";

    // ---- Router, client-visible OpenAI codes (design §7.4) ----

    /// <summary>404: unknown served name (unchanged).</summary>
    public const string ModelNotFound = "model_not_found";

    /// <summary>503: not Ready (unchanged), or the agent said <see cref="ModelNotRunning"/>; <c>Retry-After: 30</c>.</summary>
    public const string ModelNotReady = "model_not_ready";

    /// <summary>503: a benchmark or tuning job holds the box (P1) / the model's node (P2); <c>Retry-After</c> = job estimate.</summary>
    public const string Maintenance = "maintenance";

    /// <summary>503: the model's node has not been reachable for &gt; 15 s; <c>Retry-After: 30</c>.</summary>
    public const string NodeOffline = "node_offline";

    /// <summary>503: no common protocol version with the model's node; <c>Retry-After: 300</c>.</summary>
    public const string NodeIncompatible = "node_incompatible";

    /// <summary>503: the connection to the agent failed for this request; <c>Retry-After: 10</c>.</summary>
    public const string NodeUnreachable = "node_unreachable";

    /// <summary>503: the model has no placement.</summary>
    public const string ModelUnplaced = "model_unplaced";

    /// <summary>502 (type <c>api_error</c>): the agent was reached but answered an unexpected error (unchanged code).</summary>
    public const string UpstreamUnavailable = "upstream_unavailable";

    // ---- Master admin API (design §5.4, §5.6) ----

    /// <summary>400: <c>PUT /api/models/{id}</c> with an explicit <c>"placement": null</c>; use <c>/unplace</c> (§5.4).</summary>
    public const string UseUnplace = "use_unplace";

    /// <summary>
    /// 400: <c>PUT /api/models/{id}</c> whose <c>placement</c> names a different node than the stored one (or places an
    /// unplaced model); changing the node is a move, <c>POST /api/models/{id}/move</c> (§5.4, §5.6). Added by MN-090.
    /// </summary>
    public const string UseMove = "use_move";

    /// <summary>400: <c>POST /api/models</c> without placement while more than one node exists (§5.6).</summary>
    public const string PlacementRequired = "placement_required";

    /// <summary>400: a node-scoped endpoint without <c>?node=</c> while more than one node exists (§5.6).</summary>
    public const string NodeRequired = "node_required";

    /// <summary>409: the model's node is draining (§5.6).</summary>
    public const string NodeDraining = "node_draining";

    /// <summary>409: an ownership record (move, job, restore, edit) holds the model (§6.7).</summary>
    public const string ModelBusy = "model_busy";

    // ---- Master admin API, P2 (MN-101, design D73). Bodies are ProblemDetails with the code in the "code" extension. ----

    /// <summary>
    /// 400: the model's placement fails validation (§5.1: node unknown or revoked, GPU UUID unknown or listed twice, unequal
    /// GPUs for tensor parallelism, GPUs on a unified node, runtime not available, llama.cpp settings mismatch). The
    /// <c>problems</c> extension lists each <see cref="PlacementProblem"/> with its own message (MN-108).
    /// </summary>
    public const string PlacementInvalid = "placement_invalid";

    /// <summary>
    /// 409: a <c>PUT /api/models/{id}</c> carried a placement generation older than the stored one: a move, unplace or
    /// placement edit landed between the read and the write (W1 review MN-090 note on MN-108). Re-read and retry.
    /// </summary>
    public const string PlacementChanged = "placement_changed";

    /// <summary>409: the model has no Hugging Face sources, so it cannot be moved by re-download (§6.4).</summary>
    public const string ModelNotMovable = "model_not_movable";

    /// <summary>409: revoke refused because models are placed on the node and <c>unplaceModels</c> is false (§5.6).</summary>
    public const string ModelsPlaced = "models_placed";

    /// <summary>409: the node is revoked (join tokens, approval, placement and commands are refused; MN-090 rules).</summary>
    public const string NodeRevoked = "node_revoked";

    /// <summary>403: the admin password re-check failed (join tokens, revoke, takeover, force-commit; §5.6).</summary>
    public const string ReauthenticationFailed = "reauthentication_failed";

    /// <summary>409: the node already runs a benchmark or tuning job (one per node, §6.5 item 1); the message names the node.</summary>
    public const string JobConflict = "job_conflict";

    /// <summary>409: <c>MirbadLLM:Jobs:MaxConcurrent</c> jobs already run across nodes (default 2, §6.5 item 1, D15).</summary>
    public const string JobLimitReached = "job_limit_reached";

    /// <summary>409: fleet disaster recovery on a master that is not empty (a node, a non-seed model, an issued agent certificate, a used join token; §9.4).</summary>
    public const string MasterNotEmpty = "master_not_empty";

    /// <summary>400: the backup cannot be used for fleet recovery (not version 2, no <c>pki</c> secret section, or no passphrase; §9.4 item 1).</summary>
    public const string BackupUnsuitable = "backup_unsuitable";

    /// <summary>503: the master runs in recovery mode and serves only login, the Restore tab and <c>/healthz</c> (§9.4).</summary>
    public const string RecoveryMode = "recovery_mode";

    /// <summary>Codes the agent API returns.</summary>
    public static IReadOnlyList<string> AgentCodes { get; } =
    [
        ProtocolMismatch, InvalidRequest, ClockSkew, Forbidden, NotFound, StaleMaster, CompetingMaster,
        OperationIdConflict, OperationExpired, AdmissionRefused, StartConflict, SpecRejected, GpuMissing, InUse,
        AlreadyRunning, FolderExists, NoFilesMatch, InsufficientDisk, AlreadyCommitted, InvalidState, StateUnavailable,
        Superseded, ModelNotRunning, ServerBusy, DockerUnavailable, HostdUnavailable, Denied, ConfirmationRequired,
        Conflict, NotSupported, Timeout, InternalError, OutcomeUnknown,
        ExecutableMissing, ExecutableUnverified, GgufInvalid, UnsupportedManifest,
    ];

    /// <summary>Codes the master's enroll listener returns.</summary>
    public static IReadOnlyList<string> EnrollCodes { get; } = [EnrollmentRefused, RateLimited, InvalidRequest];

    /// <summary>OpenAI-shaped codes <c>/v1</c> clients can see (design §7.4).</summary>
    public static IReadOnlyList<string> RouterCodes { get; } =
    [
        ModelNotFound, ModelNotReady, Maintenance, NodeOffline, NodeIncompatible, NodeUnreachable, ModelUnplaced,
        ServerBusy, UpstreamUnavailable,
    ];

    /// <summary>Codes the master's <c>/api</c> adds (design §5.4, §5.6).</summary>
    public static IReadOnlyList<string> ApiCodes { get; } =
    [
        UseUnplace, UseMove, PlacementRequired, NodeRequired, NodeDraining, ModelBusy, NodeOffline, NodeIncompatible,
        PlacementInvalid, PlacementChanged, ModelNotMovable, ModelsPlaced, NodeRevoked, ReauthenticationFailed, JobConflict,
        JobLimitReached, MasterNotEmpty, BackupUnsuitable, RecoveryMode, ConfirmationRequired, InvalidState, GpuMissing,
        ExecutableMissing, ExecutableUnverified, GgufInvalid, UnsupportedManifest,
    ];

    /// <summary>
    /// The HTTP status the master's <c>/api</c> answers for one of its own codes (P2 codes; design §5.6, D73). Unknown
    /// codes → 400. Agent codes passed through keep <see cref="ErrorMappings.AgentStatus"/>.
    /// </summary>
    public static int ApiStatus(string code) => code switch
    {
        UseUnplace or UseMove or PlacementRequired or NodeRequired or PlacementInvalid or BackupUnsuitable => 400,
        ReauthenticationFailed => 403,
        NodeDraining or ModelBusy or PlacementChanged or ModelNotMovable or ModelsPlaced or NodeRevoked or JobConflict
            or JobLimitReached or MasterNotEmpty or ConfirmationRequired or InvalidState => 409,
        GpuMissing or ExecutableMissing or ExecutableUnverified or GgufInvalid or UnsupportedManifest => 422,
        NodeOffline or NodeIncompatible or RecoveryMode => 503,
        _ => 400,
    };
}

/// <summary>
/// An agent error (design §4.3): the body is always <c>{"error": {"code": "…", "message": "…", "details": {…}}}</c>
/// (<see cref="AgentErrorResponse"/>). The master never forwards it to an OpenAI client; it translates it
/// (<see cref="ErrorMappings.ClientTranslations"/>).
/// </summary>
public sealed record AgentError
{
    /// <summary>An <see cref="ErrorCodes"/> constant.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable message. For admission refusals it is today's <c>ModelMemoryAdmissionException</c> text plus "on node {id}".</summary>
    public required string Message { get; init; }

    /// <summary>Code-specific details (<see cref="AdmissionRefusedDetails"/>, <see cref="StartConflictDetails"/>, …). Null when none.</summary>
    public JsonElement? Details { get; init; }

    /// <summary>Reads <see cref="Details"/> as <typeparamref name="T"/>; null when absent.</summary>
    public T? ReadDetails<T>()
        where T : class =>
        Details is { } element && element.ValueKind != JsonValueKind.Null ? element.Deserialize<T>(AgentJson.Options) : null;
}

/// <summary>The error body of every non-2xx agent response: <c>{"error": {...}}</c> (design §4.3).</summary>
/// <param name="Error">The error.</param>
public sealed record AgentErrorResponse(AgentError Error);

/// <summary>Details of 409 <c>admission_refused</c> (design §4.3, §4.8). All values in GiB.</summary>
public sealed record AdmissionRefusedDetails
{
    /// <summary>Budget the launch needs.</summary>
    public required double NeedGiB { get; init; }

    /// <summary>Free memory the decision used (available − reserve − other reservations + released).</summary>
    public required double FreeGiB { get; init; }

    /// <summary>Memory available from the probe.</summary>
    public required double AvailableGiB { get; init; }

    /// <summary>Node (or GPU) reserve.</summary>
    public required double ReserveGiB { get; init; }

    /// <summary>Memory counted as released by a replaced instance (measured, §4.8).</summary>
    public double ReleasedGiB { get; init; }

    /// <summary>Memory held by other start reservations.</summary>
    public double ReservedByOthersGiB { get; init; }

    /// <summary>GPU UUID the refusal is about (discrete, P2). Null on unified nodes.</summary>
    public string? Gpu { get; init; }

    /// <summary>True when the host-memory part refused (discrete, P2).</summary>
    public bool? Host { get; init; }
}

/// <summary>Details of 409 <c>start_conflict</c> (design §4.3, §4.8).</summary>
public sealed record StartConflictDetails
{
    /// <summary>The model whose start holds the reservation.</summary>
    public required string StartingModelId { get; init; }

    /// <summary>GPU UUIDs in conflict (discrete, P2). Null on unified nodes.</summary>
    public IReadOnlyList<string>? Gpus { get; init; }
}

/// <summary>Details of 422 <c>spec_rejected</c> (design §4.3, §10.3).</summary>
/// <param name="Reason">Which guardrail refused the spec, in plain words.</param>
public sealed record SpecRejectedDetails(string Reason);

/// <summary>
/// Details of a 400 <c>invalid_request</c> that an upstream service decided rather than the agent (W3 review MN-094): today
/// a Hugging Face refusal (unknown or gated repo, bad token) is a separate exception that the admin API answers with 422,
/// so the master maps <see cref="Source"/> = <see cref="HuggingFace"/> back onto it. Additive; other refusals carry none.
/// </summary>
/// <param name="Source">Who refused the request.</param>
public sealed record UpstreamRefusalDetails(string Source)
{
    /// <summary>The Hugging Face Hub refused the repository, revision or token.</summary>
    public const string HuggingFace = "huggingface";

    /// <summary>The Ollama registry refused the name or tag (unknown model or tag; MN-202, D116, for MN-221).</summary>
    public const string Ollama = "ollama";
}

/// <summary>
/// Details of the 400 <c>invalid_request</c> a GGUF read answers when a folder holds several model GGUFs (design-p3p4
/// §A.2.1): the file names to choose from, so the editor offers them without parsing the message (MN-230, D130).
/// Additive: the message keeps the same wording and an older agent sends no details.
/// </summary>
/// <param name="Candidates">The model GGUF file names in the folder, in the order the agent lists them.</param>
public sealed record GgufChoiceDetails(IReadOnlyList<string> Candidates);

/// <summary>Details of 422 <c>gpu_missing</c> (design §5.1).</summary>
/// <param name="Uuids">Placed GPU UUIDs not present on the node.</param>
public sealed record GpuMissingDetails(IReadOnlyList<string> Uuids);

/// <summary>
/// An agent answered with an error (design §4.3). Thrown by <see cref="IAgentClient"/> for every non-2xx response, and
/// usable by agent code to produce one. Map it with <see cref="ErrorMappings"/>.
/// </summary>
public class AgentException : Exception
{
    /// <summary>Creates an exception without an agent error (prefer the other constructors).</summary>
    public AgentException()
        : this(500, new AgentError { Code = ErrorCodes.InternalError, Message = "Agent error." })
    {
    }

    /// <summary>Creates an exception with a message and code <c>internal_error</c>.</summary>
    public AgentException(string message)
        : this(500, new AgentError { Code = ErrorCodes.InternalError, Message = message })
    {
    }

    /// <summary>Creates an exception with a message, inner exception and code <c>internal_error</c>.</summary>
    public AgentException(string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = 500;
        Error = new AgentError { Code = ErrorCodes.InternalError, Message = message };
    }

    /// <summary>Creates an exception for an agent error response.</summary>
    public AgentException(int statusCode, AgentError error, string? nodeId = null, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        ArgumentNullException.ThrowIfNull(error);
        StatusCode = statusCode;
        Error = error;
        NodeId = nodeId;
    }

    /// <summary>HTTP status of the response.</summary>
    public int StatusCode { get; }

    /// <summary>The agent's error.</summary>
    public AgentError Error { get; }

    /// <summary>The <see cref="ErrorCodes"/> code.</summary>
    public string Code => Error.Code;

    /// <summary>The node that answered, when known.</summary>
    public string? NodeId { get; }
}

/// <summary>
/// The agent could not be reached or the transport failed before any response (design §4.5, §7.4 <c>node_unreachable</c>).
/// Mutations returning an operation may be retried with the same id and body only after this exception.
/// </summary>
public class AgentUnreachableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AgentUnreachableException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public AgentUnreachableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the transport error.</summary>
    public AgentUnreachableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a node.</summary>
    public AgentUnreachableException(string nodeId, string message, Exception? innerException)
        : base(message, innerException)
    {
        NodeId = nodeId;
    }

    /// <summary>The node, when known.</summary>
    public string? NodeId { get; }

    /// <summary>True when HTTP/2 could not be negotiated (no downgrade, design §7.2): the node is Degraded with that reason.</summary>
    public bool Http2NotNegotiated { get; init; }
}

/// <summary>How the master translates an agent outcome into a client-visible <c>/v1</c> error before headers (design §7.4).</summary>
/// <param name="ClientCode">OpenAI-shaped code.</param>
/// <param name="StatusCode">HTTP status.</param>
/// <param name="ErrorType">OpenAI error <c>type</c> (<c>api_error</c> for 502, otherwise the router's usual type for the status).</param>
/// <param name="RetryAfterSeconds"><c>Retry-After</c>; null = none (or computed, for maintenance).</param>
public sealed record ClientErrorTranslation(string ClientCode, int StatusCode, string? ErrorType, int? RetryAfterSeconds);

/// <summary>
/// Mapping tables (plan §2 "exception mapping table", design §4.8, §7.4).
/// </summary>
public static class ErrorMappings
{
    /// <summary>
    /// The one HTTP status of each agent error code (design §4.3; W2 review MN-092, D44). Every agent package and FakeAgent
    /// answer with it, so a code means the same status on every route. Unknown codes → 500.
    /// </summary>
    public static int AgentStatus(string code) => code switch
    {
        ErrorCodes.ProtocolMismatch or ErrorCodes.InvalidRequest or ErrorCodes.ClockSkew => 400,
        ErrorCodes.Forbidden or ErrorCodes.Denied => 403,
        ErrorCodes.NotFound => 404,
        ErrorCodes.StaleMaster or ErrorCodes.CompetingMaster or ErrorCodes.OperationIdConflict or ErrorCodes.AdmissionRefused
            or ErrorCodes.StartConflict or ErrorCodes.InUse or ErrorCodes.AlreadyRunning or ErrorCodes.FolderExists
            or ErrorCodes.AlreadyCommitted or ErrorCodes.InvalidState or ErrorCodes.Superseded or ErrorCodes.ConfirmationRequired
            or ErrorCodes.Conflict => 409,
        ErrorCodes.OperationExpired => 410,
        ErrorCodes.SpecRejected or ErrorCodes.GpuMissing or ErrorCodes.NoFilesMatch or ErrorCodes.ExecutableMissing
            or ErrorCodes.ExecutableUnverified or ErrorCodes.GgufInvalid or ErrorCodes.UnsupportedManifest => 422,
        ErrorCodes.NotSupported => 501,
        ErrorCodes.StateUnavailable or ErrorCodes.ModelNotRunning or ErrorCodes.ServerBusy or ErrorCodes.DockerUnavailable
            or ErrorCodes.HostdUnavailable => 503,
        ErrorCodes.Timeout or ErrorCodes.OutcomeUnknown => 504,
        ErrorCodes.InsufficientDisk => 507,
        _ => 500,
    };

    /// <summary>
    /// Agent error code → the exception the master's existing callers already handle (design §4.8: today's message texts
    /// plus "on node {id}"). Codes not listed map to <see cref="InvalidOperationException"/> carrying the agent message;
    /// transport failures are <see cref="AgentUnreachableException"/>.
    /// <list type="table">
    /// <item><term><c>admission_refused</c></term><description><see cref="ModelMemoryAdmissionException"/></description></item>
    /// <item><term><c>start_conflict</c></term><description><see cref="ModelStartConflictException"/> (<c>StartingModelId</c> from details)</description></item>
    /// <item><term><c>not_found</c></term><description><see cref="KeyNotFoundException"/> (runtime GETs treat it as Stopped instead)</description></item>
    /// <item><term><c>stale_master</c>, <c>competing_master</c></term><description><see cref="NodeUnavailableException"/> (not serving: Pending takeover, MN-047)</description></item>
    /// <item><term><c>confirmation_required</c></term><description><see cref="HostServiceConfirmationRequiredException"/></description></item>
    /// <item><term><c>denied</c></term><description><see cref="HostServiceDeniedException"/></description></item>
    /// <item><term><c>conflict</c></term><description><see cref="HostServiceConflictException"/></description></item>
    /// <item><term><c>hostd_unavailable</c>, <c>docker_unavailable</c>, <c>timeout</c></term><description><see cref="HostServiceException"/> (host-service routes)</description></item>
    /// <item><term><c>invalid_request</c>, <c>spec_rejected</c>, <c>gpu_missing</c>, <c>no_files_match</c></term><description><see cref="ArgumentException"/></description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyDictionary<string, Type> MasterExceptions { get; } = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [ErrorCodes.AdmissionRefused] = typeof(ModelMemoryAdmissionException),
        [ErrorCodes.StartConflict] = typeof(ModelStartConflictException),
        [ErrorCodes.NotFound] = typeof(KeyNotFoundException),
        [ErrorCodes.StaleMaster] = typeof(NodeUnavailableException),
        [ErrorCodes.CompetingMaster] = typeof(NodeUnavailableException),
        [ErrorCodes.ConfirmationRequired] = typeof(HostServiceConfirmationRequiredException),
        [ErrorCodes.Denied] = typeof(HostServiceDeniedException),
        [ErrorCodes.Conflict] = typeof(HostServiceConflictException),
        [ErrorCodes.HostdUnavailable] = typeof(HostServiceException),
        [ErrorCodes.DockerUnavailable] = typeof(HostServiceException),
        [ErrorCodes.Timeout] = typeof(HostServiceException),
        [ErrorCodes.OutcomeUnknown] = typeof(HostServiceException),
        [ErrorCodes.InvalidRequest] = typeof(ArgumentException),
        [ErrorCodes.SpecRejected] = typeof(ArgumentException),
        [ErrorCodes.GpuMissing] = typeof(ArgumentException),
        [ErrorCodes.NoFilesMatch] = typeof(ArgumentException),
        [ErrorCodes.ExecutableMissing] = typeof(ArgumentException),
        [ErrorCodes.ExecutableUnverified] = typeof(ArgumentException),
        [ErrorCodes.GgufInvalid] = typeof(ArgumentException),
        [ErrorCodes.UnsupportedManifest] = typeof(ArgumentException),
    };

    /// <summary>
    /// Client-visible errors by code (design §7.4 table). Keys are client codes; <c>maintenance</c> has a computed
    /// <c>Retry-After</c> (the job estimate, unchanged rule) and so a null value here.
    /// </summary>
    public static IReadOnlyDictionary<string, ClientErrorTranslation> ClientErrors { get; } = new Dictionary<string, ClientErrorTranslation>(StringComparer.Ordinal)
    {
        [ErrorCodes.ModelNotFound] = new(ErrorCodes.ModelNotFound, 404, null, null),
        [ErrorCodes.ModelNotReady] = new(ErrorCodes.ModelNotReady, 503, null, 30),
        [ErrorCodes.Maintenance] = new(ErrorCodes.Maintenance, 503, null, null),
        [ErrorCodes.NodeOffline] = new(ErrorCodes.NodeOffline, 503, null, 30),
        [ErrorCodes.NodeIncompatible] = new(ErrorCodes.NodeIncompatible, 503, null, 300),
        [ErrorCodes.NodeUnreachable] = new(ErrorCodes.NodeUnreachable, 503, null, 10),
        [ErrorCodes.ModelUnplaced] = new(ErrorCodes.ModelUnplaced, 503, null, null),
        [ErrorCodes.ServerBusy] = new(ErrorCodes.ServerBusy, 503, null, 5),
        [ErrorCodes.UpstreamUnavailable] = new(ErrorCodes.UpstreamUnavailable, 502, "api_error", null),
    };

    /// <summary>
    /// Agent error code on a proxied request (before response headers) → client error (design §7.1, §7.4). Codes not
    /// listed become <c>upstream_unavailable</c> (502). After headers were sent, nothing is translated: the master aborts
    /// the client connection.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ProxyAgentToClient { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ErrorCodes.ModelNotRunning] = ErrorCodes.ModelNotReady,
        [ErrorCodes.ServerBusy] = ErrorCodes.ServerBusy,
        [ErrorCodes.NotFound] = ErrorCodes.ModelNotReady,
        [ErrorCodes.ProtocolMismatch] = ErrorCodes.NodeIncompatible,
    };

    /// <summary>The client translation for an agent code on a proxied request (default <c>upstream_unavailable</c>).</summary>
    public static ClientErrorTranslation TranslateProxyError(string agentCode) =>
        ClientErrors[agentCode is not null && ProxyAgentToClient.TryGetValue(agentCode, out string? client) ? client : ErrorCodes.UpstreamUnavailable];
}
