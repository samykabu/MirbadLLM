using System.Text.Json;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// The stored status of an agent operation (design §4.3 <c>GET /operations/{id}</c>, §4.4). Short mutations answer 200
/// with it after completing; long ones (launch, stop, image pull, download) answer 202 and finish asynchronously.
/// A repeated submission with the same id, action, target and digest returns this stored status (§4.4 item 2).
/// </summary>
public sealed record OperationStatus
{
    /// <summary>Operation id.</summary>
    public required string Id { get; init; }

    /// <summary>Operation kind (<see cref="OperationKinds"/>).</summary>
    public required string Kind { get; init; }

    /// <summary>Target: model id, folder, unit or container name, rotation id, image, operation id…</summary>
    public required string Target { get; init; }

    /// <summary>Base64 SHA-256 of the canonical request body with secrets removed (§4.4 item 1).</summary>
    public required string PayloadDigest { get; init; }

    /// <summary>Lifecycle state; <see cref="OperationState.Unknown"/> for unverifiable host-service outcomes (§4.4 item 8).</summary>
    public required OperationState State { get; init; }

    /// <summary>Last durable stage (<see cref="OperationStages"/>). Null before the first stage.</summary>
    public string? Stage { get; init; }

    /// <summary>Error when <see cref="State"/> is Failed (or the observed state when Unknown). Null otherwise.</summary>
    public AgentError? Error { get; init; }

    /// <summary>Progress of long operations (pulls, downloads). Null when not reported.</summary>
    public OperationProgress? Progress { get; init; }

    /// <summary>The envelope's issue time, as submitted.</summary>
    public DateTimeOffset IssuedAt { get; init; }

    /// <summary>The envelope's expiry, as submitted; the record is retained until this + 10 minutes (§4.4 item 6).</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>When the agent durably accepted it.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When it reached a terminal state. Null while active.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>
    /// Kind-specific result, typed by the route's <see cref="AgentRoute.ResultType"/> (read it with
    /// <see cref="AgentJson.ReadResult{T}"/>). Null when the kind has none or it has not finished.
    /// </summary>
    public JsonElement? Result { get; init; }
}

/// <summary>Progress of a long operation (design §4.3).</summary>
public sealed record OperationProgress
{
    /// <summary>0–100 when known.</summary>
    public double? Percent { get; init; }

    /// <summary>Bytes done (pull, download).</summary>
    public long? BytesDone { get; init; }

    /// <summary>Bytes expected; null or 0 when unknown.</summary>
    public long? BytesTotal { get; init; }

    /// <summary>Short human-readable status, e.g. <c>pulling layer 3/12</c>.</summary>
    public string? Message { get; init; }
}

/// <summary>Query of <c>GET /operations</c> (design §4.3).</summary>
public sealed record OperationsQuery
{
    /// <summary><c>active</c> (<see cref="ActiveState"/>) lists only non-terminal operations; null lists recent ones too.</summary>
    public string? State { get; init; }

    /// <summary>The only <see cref="State"/> filter value: non-terminal operations (used by the master after restarts).</summary>
    public const string ActiveState = "active";
}

/// <summary>
/// <c>POST /operations/{operationId}/cancel</c> (design §4.3, §4.4 item 7): a new operation (its own id) that cancels a
/// running launch (before its instance exists; releases the reservation), an image pull or a download. A launch whose
/// instance already exists must be followed by a stop.
/// </summary>
public sealed record OperationCancelRequest : AgentMutation;

/// <summary>
/// Operation kinds (design §4.3, §4.4): the action recorded in the operation log and used for deduplication (same id
/// with a different kind → <c>operation_id_conflict</c>). One per mutating route; see <see cref="AgentRoute.OperationKind"/>.
/// </summary>
public static class OperationKinds
{
    /// <summary>Launch a runtime instance.</summary>
    public const string Launch = "launch";

    /// <summary>Stop and remove a runtime instance.</summary>
    public const string Stop = "stop";

    /// <summary>Pull a container image.</summary>
    public const string ImagePull = "imagePull";

    /// <summary>Start a Hugging Face download.</summary>
    public const string Download = "download";

    /// <summary>Cancel a download.</summary>
    public const string DownloadCancel = "downloadCancel";

    /// <summary>Delete a model folder.</summary>
    public const string FolderDelete = "folderDelete";

    /// <summary>Cancel another operation.</summary>
    public const string OperationCancel = "operationCancel";

    /// <summary>Start a host unit (desired state).</summary>
    public const string ServiceStart = "serviceStart";

    /// <summary>Stop a host unit (desired state).</summary>
    public const string ServiceStop = "serviceStop";

    /// <summary>Restart a host unit (not verifiable after a crash → Unknown).</summary>
    public const string ServiceRestart = "serviceRestart";

    /// <summary>Turn a host unit's autostart on or off (desired state).</summary>
    public const string ServiceAutostart = "serviceAutostart";

    /// <summary>Add a unit to hostd's whitelist (desired state).</summary>
    public const string WhitelistAdd = "whitelistAdd";

    /// <summary>Update a whitelisted unit (desired state).</summary>
    public const string WhitelistUpdate = "whitelistUpdate";

    /// <summary>Remove a unit from the whitelist (desired state).</summary>
    public const string WhitelistRemove = "whitelistRemove";

    /// <summary>Start a non-model container (desired state).</summary>
    public const string ContainerStart = "containerStart";

    /// <summary>Stop a non-model container (desired state).</summary>
    public const string ContainerStop = "containerStop";

    /// <summary>Restart a non-model container (not verifiable → Unknown).</summary>
    public const string ContainerRestart = "containerRestart";

    /// <summary>Change a container's restart policy (desired state).</summary>
    public const string ContainerAutostart = "containerAutostart";

    /// <summary>Configure and mount the backup share (not verifiable → Unknown; the password is never stored).</summary>
    public const string BackupMountSet = "backupMountSet";

    /// <summary>Unmount and forget the backup share (desired state).</summary>
    public const string BackupMountRemove = "backupMountRemove";

    /// <summary>Start a certificate rotation: pending key + CSR.</summary>
    public const string PkiRotationStart = "pkiRotationStart";

    /// <summary>Install the new certificate as pending.</summary>
    public const string PkiRotationCert = "pkiRotationCert";

    /// <summary>Commit a rotation (irreversible, idempotent).</summary>
    public const string PkiRotationCommit = "pkiRotationCommit";

    /// <summary>Abort a rotation before commit.</summary>
    public const string PkiRotationAbort = "pkiRotationAbort";

    /// <summary>Replace the trust anchors (CA rotation).</summary>
    public const string PkiTrustSet = "pkiTrustSet";

    /// <summary><c>POST /agent/update</c>: install the master's bundle through the node's update helper (agent-updates.md U3).</summary>
    public const string AgentUpdate = "agentUpdate";

    /// <summary>
    /// Host-service kinds whose outcome is reconciled by reading the current state after a crash: if it matches the desired
    /// state the operation Succeeded, otherwise the action is applied again (design §4.4 item 8).
    /// </summary>
    public static IReadOnlyList<string> DesiredState { get; } =
    [
        ServiceStart, ServiceStop, ServiceAutostart, WhitelistAdd, WhitelistUpdate, WhitelistRemove,
        ContainerStart, ContainerStop, ContainerAutostart, BackupMountRemove,
    ];

    /// <summary>
    /// Host-service kinds that are never replayed: dispatched without a terminal record (or a hostd timeout) ends
    /// <see cref="OperationState.Unknown"/> with the observed state (design §4.4 item 8).
    /// </summary>
    public static IReadOnlyList<string> Unverifiable { get; } = [ServiceRestart, ContainerRestart, BackupMountSet];
}

/// <summary>
/// Durable stage names recorded by the agent (design §4.4 item 4). The agent re-checks the accepted master triple before
/// every destructive stage (stopping, removing, creating, deleting) and fails a superseded operation with
/// <c>superseded</c> (§4.2).
/// </summary>
public static class OperationStages
{
    /// <summary>Durably accepted (the first record of every operation).</summary>
    public const string Accepted = "accepted";

    /// <summary>Launch: reservation recorded (start rule + admission passed, port allocated).</summary>
    public const string Admitted = "admitted";

    /// <summary>Launch / image pull: pulling the image.</summary>
    public const string Pulling = "pulling";

    /// <summary>Launch: pull finished (admission is re-probed before create when it took &gt; 30 s, §4.8).</summary>
    public const string Pulled = "pulled";

    /// <summary>Launch (replace) / stop: stopping the current instance.</summary>
    public const string Stopping = "stopping";

    /// <summary>Launch (replace) / stop: the old instance is removed (memory is re-probed after this, §4.8).</summary>
    public const string Removed = "removed";

    /// <summary>Launch: the instance exists but is not started yet (reconciliation starts it, §4.4 item 5).</summary>
    public const string Created = "created";

    /// <summary>Launch: the instance was started (state Starting).</summary>
    public const string Started = "started";

    /// <summary>Launch: the instance reported Ready; the reservation is released.</summary>
    public const string Ready = "ready";

    /// <summary>Download: files are being fetched.</summary>
    public const string Downloading = "downloading";

    /// <summary>Folder delete: removing files.</summary>
    public const string Deleting = "deleting";

    /// <summary>
    /// Host service: persisted (fsync'd) immediately before calling hostd or Docker; recovery decides between reconcile
    /// and Unknown from here (§4.4 item 8).
    /// </summary>
    public const string Dispatched = "dispatched";

    /// <summary>PKI: pending key and CSR persisted.</summary>
    public const string KeyPending = "keyPending";

    /// <summary>PKI: new certificate installed as pending.</summary>
    public const string CertInstalled = "certInstalled";

    /// <summary>PKI: committed (old key deleted).</summary>
    public const string Committed = "committed";

    /// <summary>PKI: aborted (pending material dropped).</summary>
    public const string Aborted = "aborted";

    /// <summary>The terminal record of any operation.</summary>
    public const string Completed = "completed";
}

/// <summary>Retention and expiry of operations (design §4.3 envelope, §4.4 item 6).</summary>
public static class OperationRetention
{
    /// <summary><c>expiresAt = issuedAt + 24 h</c>.</summary>
    public static readonly TimeSpan EnvelopeLifetime = TimeSpan.FromHours(24);

    /// <summary>A terminal record is kept until its <c>expiresAt</c> plus this clock-skew margin.</summary>
    public static readonly TimeSpan RetentionMargin = TimeSpan.FromMinutes(10);

    /// <summary>At least this many most recent records are always kept (active ones are never pruned).</summary>
    public const int MinRetainedRecords = 1_000;

    /// <summary>An envelope whose <c>issuedAt</c> is further in the future than this is refused.</summary>
    public static readonly TimeSpan MaxIssuedAtFutureSkew = TimeSpan.FromMinutes(5);

    /// <summary>How often <c>log.jsonl</c> is compacted into <c>operations.json</c>.</summary>
    public static readonly TimeSpan CompactionInterval = TimeSpan.FromHours(24);

    /// <summary>How many recent terminal operations <c>GET /status</c> includes besides the active ones.</summary>
    public const int StatusRecentCount = 100;

    /// <summary>Operation log file, relative to the agent state directory.</summary>
    public const string LogRelativePath = "operations/log.jsonl";

    /// <summary>Compacted operations file, relative to the agent state directory.</summary>
    public const string CompactedRelativePath = "operations/operations.json";
}

/// <summary>What one line of the operation log records (design §4.4).</summary>
public enum OperationLogEntryType
{
    /// <summary>Durable acceptance: identity, digest, redacted payload, envelope and master triple.</summary>
    Accepted,

    /// <summary>A stage change (and/or progress, reservation).</summary>
    Stage,

    /// <summary>The terminal state with error/result.</summary>
    Completed,
}

/// <summary>
/// One JSON line of the agent's operation log <c>{state}/operations/log.jsonl</c> (design §4.4), appended and fsync'd
/// before the agent answers. The current status of an operation is its <c>accepted</c> line folded with every later line
/// of the same id. A torn last line (crash mid-write) is ignored and reported. Never contains secrets: the payload is
/// stored with <see cref="Protocol.SecretFieldNames"/> values replaced by <c>***</c> and the digest is computed after
/// that replacement.
/// </summary>
public sealed record OperationLogEntry
{
    /// <summary>Line type.</summary>
    public required OperationLogEntryType Type { get; init; }

    /// <summary>Operation id.</summary>
    public required string OperationId { get; init; }

    /// <summary>When the line was written (agent clock).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>Agent boot id that wrote the line.</summary>
    public string? BootId { get; init; }

    /// <summary>Node uid (accepted line).</summary>
    public string? NodeUid { get; init; }

    /// <summary>Operation kind (accepted line).</summary>
    public string? Kind { get; init; }

    /// <summary>Target (accepted line).</summary>
    public string? Target { get; init; }

    /// <summary>Payload digest (accepted line).</summary>
    public string? PayloadDigest { get; init; }

    /// <summary>Redacted request body (accepted line); used by reconciliation to resume the operation.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>Envelope issue time (accepted line).</summary>
    public DateTimeOffset? IssuedAt { get; init; }

    /// <summary>Envelope expiry (accepted line); drives retention.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Master triple that submitted the operation (accepted line); re-checked before destructive stages.</summary>
    public MasterTriple? Master { get; init; }

    /// <summary>State after this line.</summary>
    public OperationState? State { get; init; }

    /// <summary>Stage after this line.</summary>
    public string? Stage { get; init; }

    /// <summary>Progress (stage lines).</summary>
    public OperationProgress? Progress { get; init; }

    /// <summary>Start reservation held by a launch (stage lines); rebuilt after an agent restart (§4.8).</summary>
    public ReservationInfo? Reservation { get; init; }

    /// <summary>Error (completed line).</summary>
    public AgentError? Error { get; init; }

    /// <summary>Result (completed line).</summary>
    public JsonElement? Result { get; init; }
}
