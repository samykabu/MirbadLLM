namespace MirbadLLM.Core;

/// <summary>
/// One line of the master's audit log, <c>{DataPath}/audit/{yyyy-MM}.jsonl</c> (design §10.5; P2, frozen by MN-101, D73;
/// written by MN-116's <c>JsonlAuditLog</c> with <see cref="MasterStateJson.Options"/> on one line). One record per
/// mutating action; never holds a secret (no tokens, passwords, passphrases, HF tokens or key material).
/// </summary>
public sealed record AuditRecord
{
    /// <summary>When the action finished (or was refused).</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>Who: <see cref="AuditActors.Admin"/>(session hash), <see cref="AuditActors.ApiCaller"/>(ip), or a <c>system:*</c> actor.</summary>
    public required string Actor { get; init; }

    /// <summary>What: an <see cref="AuditActions"/> value.</summary>
    public required string Action { get; init; }

    /// <summary>Node concerned, when any.</summary>
    public string? NodeId { get; init; }

    /// <summary>Model concerned, when any.</summary>
    public string? ModelId { get; init; }

    /// <summary>Agent operation id, so master and agent lines join (design §10.5).</summary>
    public string? OperationId { get; init; }

    /// <summary>Other target (folder, unit, rotation id, takeover id), when any.</summary>
    public string? Target { get; init; }

    /// <summary>An <see cref="AuditResults"/> value.</summary>
    public required string Result { get; init; }

    /// <summary>Error code (<c>ErrorCodes</c>) of a failed or refused action.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Short non-secret detail ("unplaced 2 models", "epoch 7 → 9").</summary>
    public string? Detail { get; init; }
}

/// <summary>Actor values of <see cref="AuditRecord.Actor"/> (design §10.5).</summary>
public static class AuditActors
{
    /// <summary>System auto-start (boot or on-request).</summary>
    public const string SystemAutoStart = "system:autostart";

    /// <summary>A restore job.</summary>
    public const string SystemRestore = "system:restore";

    /// <summary>A benchmark or tuning job.</summary>
    public const string SystemJob = "system:job";

    /// <summary>A move record.</summary>
    public const string SystemMove = "system:move";

    /// <summary>Certificate renewal and CA rotation driven by the master.</summary>
    public const string SystemPki = "system:pki";

    /// <summary>Configuration at boot (the takeover option or flag file).</summary>
    public const string Config = "config";

    /// <summary>An admin session: <c>admin:{sha256 of the session id, 12 hex}</c>.</summary>
    public static string Admin(string sessionHash) => "admin:" + sessionHash;

    /// <summary>An API caller without a session: <c>api:{ip}</c>.</summary>
    public static string ApiCaller(string ip) => "api:" + ip;
}

/// <summary>Action values of <see cref="AuditRecord.Action"/> (design §10.5). Dotted <c>{object}.{verb}</c>.</summary>
public static class AuditActions
{
    /// <summary>Model created.</summary>
    public const string ModelCreate = "model.create";

    /// <summary>Model definition saved.</summary>
    public const string ModelUpdate = "model.update";

    /// <summary>Model deleted.</summary>
    public const string ModelDelete = "model.delete";

    /// <summary>Model started.</summary>
    public const string ModelStart = "model.start";

    /// <summary>Model stopped.</summary>
    public const string ModelStop = "model.stop";

    /// <summary>Model restarted.</summary>
    public const string ModelRestart = "model.restart";

    /// <summary>Move started.</summary>
    public const string ModelMove = "model.move";

    /// <summary>Move force-committed.</summary>
    public const string ModelMoveForceCommit = "model.move.force-commit";

    /// <summary>Model unplaced.</summary>
    public const string ModelUnplace = "model.unplace";

    /// <summary>Join or rekey token created.</summary>
    public const string NodeJoinToken = "node.join-token";

    /// <summary>Node enrolled (or re-paired with rekey).</summary>
    public const string NodeEnroll = "node.enroll";

    /// <summary>Node approved.</summary>
    public const string NodeApprove = "node.approve";

    /// <summary>Pending node rejected.</summary>
    public const string NodeReject = "node.reject";

    /// <summary>Drain set.</summary>
    public const string NodeDrain = "node.drain";

    /// <summary>Drain cleared.</summary>
    public const string NodeUndrain = "node.undrain";

    /// <summary>Node settings patched.</summary>
    public const string NodeUpdate = "node.update";

    /// <summary>Node revoked.</summary>
    public const string NodeRevoke = "node.revoke";

    /// <summary>A node's agent updated from the master (docs/multinode/agent-updates.md).</summary>
    public const string NodeAgentUpdate = "node.agent-update";

    /// <summary>The fleet's automatic agent updates turned on or off.</summary>
    public const string AgentUpdateSettings = "agent-updates.settings";

    /// <summary>Agent certificate rotated.</summary>
    public const string NodeRotateCert = "node.rotate-cert";

    /// <summary>Takeover performed.</summary>
    public const string FencingTakeover = "fencing.takeover";

    /// <summary>Takeover continued for a returning node.</summary>
    public const string FencingContinue = "fencing.continue";

    /// <summary>CA rotation started, advanced or closed.</summary>
    public const string PkiCaRotation = "pki.ca-rotation";

    /// <summary>Benchmark or tuning job started or cancelled.</summary>
    public const string Job = "job";

    /// <summary>Download started or cancelled.</summary>
    public const string Download = "download";

    /// <summary>Model folder deleted.</summary>
    public const string FolderDelete = "folder.delete";

    /// <summary>Host service or container action.</summary>
    public const string HostService = "host-service";

    /// <summary>Backup restored (either mode).</summary>
    public const string Restore = "restore";

    /// <summary>API key created, revoked or deleted.</summary>
    public const string ApiKey = "api-key";
}

/// <summary>Result values of <see cref="AuditRecord.Result"/>.</summary>
public static class AuditResults
{
    /// <summary>Done.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>Tried and failed.</summary>
    public const string Failed = "failed";

    /// <summary>Refused before anything ran (validation, busy, re-auth).</summary>
    public const string Refused = "refused";

    /// <summary>Accepted; the outcome follows asynchronously (an agent operation).</summary>
    public const string Accepted = "accepted";

    /// <summary>Outcome could not be verified (host-service <c>outcome_unknown</c>).</summary>
    public const string Unknown = "unknown";
}

/// <summary>A read of recent audit lines (node detail "Recent commands", design §12.1).</summary>
public sealed record AuditQuery
{
    /// <summary>Only lines about this node.</summary>
    public string? NodeId { get; init; }

    /// <summary>Only lines about this model.</summary>
    public string? ModelId { get; init; }

    /// <summary>Only lines at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Most lines returned, newest first.</summary>
    public int Max { get; init; } = 50;
}

/// <summary>
/// The master's audit log (design §10.5), implemented by MN-116 over <c>{DataPath}/audit/{yyyy-MM}.jsonl</c> (one file per
/// month, kept <see cref="AuditLogFiles.RetentionMonths"/> months). Writing must never fail the action it records: an implementation logs
/// and drops a line it cannot write. <see cref="NullAuditLog"/> is the default until the composition (MN-130) registers it.
/// </summary>
public interface IAuditLog
{
    /// <summary>Appends one line (flushed). Never throws for I/O errors.</summary>
    Task WriteAsync(AuditRecord record, CancellationToken ct = default);

    /// <summary>Recent lines matching <paramref name="query"/>, newest first.</summary>
    Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default);
}

/// <summary>Where the audit log lives (design §10.5).</summary>
public static class AuditLogFiles
{
    /// <summary>Directory under the data path.</summary>
    public const string DirectoryName = "audit";

    /// <summary>Months of audit files kept.</summary>
    public const int RetentionMonths = 12;

    /// <summary>The file of the month of <paramref name="time"/> (UTC): <c>{yyyy-MM}.jsonl</c>.</summary>
    public static string FileNameFor(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) + ".jsonl";
}

/// <summary>An <see cref="IAuditLog"/> that keeps nothing (the default before MN-116/MN-130).</summary>
public sealed class NullAuditLog : IAuditLog
{
    /// <summary>The shared instance.</summary>
    public static NullAuditLog Instance { get; } = new();

    /// <inheritdoc/>
    public Task WriteAsync(AuditRecord record, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditRecord>>([]);
}
