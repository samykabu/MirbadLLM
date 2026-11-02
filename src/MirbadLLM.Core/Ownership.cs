using System.Text.Json.Serialization;

namespace MirbadLLM.Core;

/// <summary>What holds a model (design §6.7). Serialized camelCase (<c>move</c>, <c>job</c>, <c>restore</c>, <c>edit</c>).</summary>
public enum OwnershipKind
{
    /// <summary>A move to another node (re-download on the target, §6.4).</summary>
    Move,

    /// <summary>A benchmark or tuning job on the model.</summary>
    Job,

    /// <summary>A restore of the model (configuration import or fleet recovery, §9.4).</summary>
    Restore,

    /// <summary>A placement edit that needs a restart (GPU change on the same node, §6.3).</summary>
    Edit,
}

/// <summary>Stage names of a move record (design §6.7). The placement is never committed before the source stop is confirmed.</summary>
public static class MoveStages
{
    /// <summary>Downloading the model's sources on the target node.</summary>
    public const string Downloading = "downloading";

    /// <summary>Stopping the source instance (pauses here while the source node is not serving).</summary>
    public const string StoppingSource = "stopping-source";

    /// <summary>Confirmed by status: no instance on the source node.</summary>
    public const string SourceStopped = "source-stopped";

    /// <summary>Registry written with the target placement (generation incremented).</summary>
    public const string PlacementCommitted = "placement-committed";

    /// <summary>Starting on the target (when the model was running before the move).</summary>
    public const string StartingTarget = "starting-target";

    /// <summary>Finished; the record is released.</summary>
    public const string Done = "done";

    /// <summary>Failed or cancelled before the placement was committed; nothing changed in the registry.</summary>
    public const string Failed = "failed";

    /// <summary>Every stage in order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Downloading, StoppingSource, SourceStopped, PlacementCommitted, StartingTarget, Done, Failed];
}

/// <summary>
/// An ownership record in <c>{DataPath}/ownership.json</c> (design §6.7; P2, frozen by MN-101, stored by MN-108's
/// <c>OwnershipStore</c>): multi-step work that spans several agent operations holds the model, so other owners get 409
/// <c>model_busy</c> naming the holder, and plain start/stop requests are refused except the holder's own.
/// </summary>
public sealed record OwnershipRecord
{
    /// <summary>Random record id; operation references name it as their <c>owner</c> (design §4.4 master side).</summary>
    public required string Id { get; init; }

    /// <summary>What holds the model.</summary>
    public required OwnershipKind Kind { get; init; }

    /// <summary>The held model.</summary>
    public required string ModelId { get; init; }

    /// <summary>Node uids involved (move: source then target), so a re-enrolled node does not continue the record.</summary>
    public IReadOnlyList<string> NodeUids { get; init; } = [];

    /// <summary>The model's <see cref="ModelPlacement.Generation"/> when the record was acquired; a stage of an older generation is stale.</summary>
    public long PlacementGeneration { get; init; }

    /// <summary>Current stage (<see cref="MoveStages"/> for moves; free-form for the other kinds).</summary>
    public required string Stage { get; init; }

    /// <summary>Agent operations this record still waits for (reconciled before the record resumes after a master restart).</summary>
    public IReadOnlyList<string> OutstandingOperationIds { get; init; } = [];

    /// <summary>When the record was acquired.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the stage last changed.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Who started it: an <see cref="AuditActors"/> value.</summary>
    public string? Actor { get; init; }

    /// <summary>Plain-words holder for the 409 message, e.g. "a move to pc-office", "a tuning run".</summary>
    public string? Description { get; init; }

    /// <summary>The job id (kind job) or restore id (kind restore).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReferenceId { get; init; }

    /// <summary>Move details (kind move only).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MoveDetails? Move { get; init; }

    /// <summary>Last error, for the UI. Null when none.</summary>
    public string? Error { get; init; }
}

/// <summary>The move part of an ownership record (design §6.4, §6.7).</summary>
public sealed record MoveDetails
{
    /// <summary>Node the model leaves.</summary>
    public required string SourceNodeId { get; init; }

    /// <summary>Uid of the source node at the start.</summary>
    public required string SourceNodeUid { get; init; }

    /// <summary>Node the model goes to.</summary>
    public required string TargetNodeId { get; init; }

    /// <summary>Uid of the target node at the start.</summary>
    public required string TargetNodeUid { get; init; }

    /// <summary>Target GPUs (UUIDs) on a discrete node; empty on unified nodes.</summary>
    public IReadOnlyList<string> TargetGpuUuids { get; init; } = [];

    /// <summary>True when the model was running when the move started (it is started on the target at the end).</summary>
    public bool WasRunning { get; init; }

    /// <summary>
    /// Set when the operator force-committed while the source was not serving, after confirming the consequence ("If dgx
    /// comes back, the old instance may still be running there until it is stopped; no traffic is sent to it").
    /// Reconciliation then stops the model on the source when it returns.
    /// </summary>
    public DateTimeOffset? ForceCommittedAt { get; init; }

    /// <summary>True while the stale source instance still has to be stopped after a force-commit.</summary>
    public bool SourceCleanupPending { get; init; }
}

/// <summary><c>{DataPath}/ownership.json</c> (design §6.7), written atomically with <see cref="MasterStateJson.Options"/>.</summary>
public sealed record OwnershipDocument
{
    /// <summary>Schema version of this file format.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>File name under the data path.</summary>
    public const string FileName = "ownership.json";

    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Open records (released records are removed).</summary>
    public IReadOnlyList<OwnershipRecord> Records { get; init; } = [];
}

/// <summary>Result of <see cref="IOwnershipStore.TryAcquireAsync"/>.</summary>
/// <param name="Acquired">True when the record is now stored.</param>
/// <param name="Holder">The record that holds the model when not acquired.</param>
public sealed record OwnershipAcquireResult(bool Acquired, OwnershipRecord? Holder);

/// <summary>
/// The ownership record store (design §6.7), implemented by <c>MirbadLLM.Orchestration.Multinode.OwnershipStore</c>
/// (MN-108) over <c>ownership.json</c>.
/// <para>Invariants: at most one record per model; every write is atomic (temp + move, directory flush, D69) under one
/// lock; a record is kept until released, across master restarts; holders resume from their stage after reconciling
/// their outstanding operations.</para>
/// </summary>
public interface IOwnershipStore
{
    /// <summary>Every open record.</summary>
    Task<IReadOnlyList<OwnershipRecord>> ListAsync(CancellationToken ct = default);

    /// <summary>The record holding <paramref name="modelId"/>, or null.</summary>
    Task<OwnershipRecord?> GetForModelAsync(string modelId, CancellationToken ct = default);

    /// <summary>Stores <paramref name="record"/> unless another record holds its model (then returns that holder).</summary>
    Task<OwnershipAcquireResult> TryAcquireAsync(OwnershipRecord record, CancellationToken ct = default);

    /// <summary>Atomic read-modify-write of one record; null when it no longer exists.</summary>
    Task<OwnershipRecord?> UpdateAsync(string recordId, Func<OwnershipRecord, OwnershipRecord> update, CancellationToken ct = default);

    /// <summary>Removes the record. False when it did not exist.</summary>
    Task<bool> ReleaseAsync(string recordId, CancellationToken ct = default);
}

/// <summary>
/// An ownership record holds the model (design §6.7): the admin API answers 409 <c>model_busy</c> with the holder's
/// description. An <see cref="InvalidOperationException"/>, like <see cref="NodeUnavailableException"/>.
/// </summary>
public sealed class ModelBusyException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public ModelBusyException()
        : this("The model is busy.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public ModelBusyException(string message)
        : base(message)
    {
        ModelId = "";
    }

    /// <summary>Creates the exception with a message and inner exception.</summary>
    public ModelBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
        ModelId = "";
    }

    /// <summary>Creates the exception for a model held by <paramref name="holder"/>.</summary>
    public ModelBusyException(string modelId, OwnershipRecord holder)
        : base("Model '" + modelId + "' is busy: " + DescribeHolder(holder) + " holds it.")
    {
        ModelId = modelId;
        Holder = holder;
    }

    /// <summary>The held model.</summary>
    public string ModelId { get; }

    /// <summary>The holder, when known.</summary>
    public OwnershipRecord? Holder { get; }

    /// <summary>The holder's description, else a phrase for its kind.</summary>
    public static string DescribeHolder(OwnershipRecord? holder) =>
        holder?.Description ?? holder?.Kind switch
        {
            OwnershipKind.Move => "a move",
            OwnershipKind.Job => "a benchmark or tuning job",
            OwnershipKind.Restore => "a restore",
            OwnershipKind.Edit => "a placement change",
            _ => "another action",
        };
}

// ---- Move and unplace API (design §5.6, §6.7; MN-109) ----

/// <summary>
/// <c>POST /api/models/{id}/move</c> (design §5.6): starts a move record. 409 <c>model_not_movable</c> without sources,
/// <c>model_busy</c> when another record holds the model, <c>node_draining</c>/<c>node_revoked</c> for the target, 400
/// <c>placement_invalid</c> for the target placement. <see cref="ToString"/> never prints the password.
/// </summary>
public sealed record ModelMoveRequest
{
    /// <summary>The target node.</summary>
    public required string TargetNodeId { get; init; }

    /// <summary>Target GPUs by UUID (discrete); wins over <see cref="GpuIndices"/>.</summary>
    public IReadOnlyList<string>? GpuUuids { get; init; }

    /// <summary>Target GPUs by current index (mapped to UUIDs from the node's capabilities at once).</summary>
    public IReadOnlyList<int>? GpuIndices { get; init; }

    /// <summary>Legacy password field; ignored. A signed-in session authorizes stopping a running model.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// Hugging Face token for the target's re-download of a gated or private repo (W8 review MN-128, additive, omitted when
    /// null). Secret: the master keeps it in memory for the move's <c>downloading</c> stage only and passes it to the target
    /// agent's download (never written to <c>ownership.json</c>, logs, audit lines or operation digests); a move resumed
    /// after a master restart has no token.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HfToken { get; init; }

    /// <summary>Redacts the password and the token.</summary>
    public override string ToString() =>
        $"ModelMoveRequest {{ TargetNodeId = {TargetNodeId}, Password = ***, HfToken = {(HfToken is null ? "null" : "***")} }}";
}

/// <summary>
/// <c>POST /api/models/{id}/move/force-commit</c> (design §6.7): commits a move paused at <c>stopping-source</c> because the
/// source node is not serving. The operator confirmed the consequence and types the source node id.
/// </summary>
public sealed record ModelMoveForceCommitRequest
{
    /// <summary>Legacy password field; ignored. A signed-in session authorizes the action.</summary>
    public string? Password { get; init; }

    /// <summary>The source node id typed by the operator.</summary>
    public required string Confirm { get; init; }

    /// <summary>Redacts the password.</summary>
    public override string ToString() => $"ModelMoveForceCommitRequest {{ Confirm = {Confirm}, Password = *** }}";
}

/// <summary><c>POST /api/models/{id}/unplace</c> (design §5.6): stops the model (when running), then clears its placement.</summary>
public sealed record ModelUnplaceRequest
{
    /// <summary>Legacy password field; ignored. A signed-in session authorizes the action.</summary>
    public string? Password { get; init; }

    /// <summary>Redacts the password.</summary>
    public override string ToString() => "ModelUnplaceRequest { Password = *** }";
}
