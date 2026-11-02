using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.NodeRuntime.Abstractions;

/// <summary>
/// One supervised process in <c>{state}/processes.json</c> (design-p3p4 §A.1.3 step 6; frozen by MN-202, D116). Written
/// by the process driver (MN-211) right after the process is created (atomic temp + move + fsync, like the operation
/// log), read back at agent start for adoption (MN-211/MN-232), by the GPU-process probe and the external-use check
/// (MN-214: PID → model, measured delta) and by the proxy and metrics scrape for the instance key (MN-222). The record is
/// the instance's identity on a node without Docker, so it also answers <c>/runtimes</c> and <c>/launch-config</c> after
/// a restart. No secret is stored in clear: the API key is DPAPI-protected (<see cref="IInstanceKeyProtector"/>) and
/// secret environment values are never stored at all. camelCase JSON (<c>AgentJson</c>).
/// </summary>
public sealed record ProcessRecord
{
    /// <summary>Model id.</summary>
    public required string ModelId { get; init; }

    /// <summary>The launch operation that created the instance.</summary>
    public required string OperationId { get; init; }

    /// <summary>The launch spec as accepted (tokens kept; secret env values removed, their names kept in <c>secretEnvNames</c>).</summary>
    public required LaunchSpec Spec { get; init; }

    /// <summary>Process identity: PID, creation time and image path (the PID-reuse check).</summary>
    public required ProcessIdentity Process { get; init; }

    /// <summary>Logical executable name.</summary>
    public required string ExecutableName { get; init; }

    /// <summary>Resolved executable version (what <c>launch-config</c> reports).</summary>
    public required string ExecutableVersion { get; init; }

    /// <summary>Loopback port the process listens on (substituted for <c>{port}</c>).</summary>
    public required int Port { get; init; }

    /// <summary>
    /// 16 hex of SHA-256 over the substituted argv (what actually ran, <c>argv[0]</c> excluded; one function in the driver,
    /// MN-211). Detects a hand-edited record, and is the orphan sweep's marker: a candidate is the agent's only when the
    /// digest of its arguments (<see cref="IProcessHost.ReadArgumentsAsync"/>) equals a record's or a pending start's.
    /// </summary>
    public required string ArgvDigest { get; init; }

    /// <summary>The per-instance API key, protected by <see cref="IInstanceKeyProtector"/> (never the clear key).</summary>
    public required string ProtectedApiKey { get; init; }

    /// <summary>
    /// Job label, <c>mirbadllm-ss-{modelId}</c>. Only a label: the job cannot be re-opened by name after an agent restart
    /// (spike S4 / F2), so adoption creates a new job with the same label (<see cref="IProcessHost.OpenAsync"/>).
    /// </summary>
    public required string JobName { get; init; }

    /// <summary>Log file of stdout/stderr (<c>{state}/logs/ss-{modelId}.log</c>).</summary>
    public required string LogPath { get; init; }

    /// <summary>GPUs the instance uses, in placement order (read-back).</summary>
    public IReadOnlyList<GpuRef> Gpus { get; init; } = [];

    /// <summary>When the first process of this instance was started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the instance was first Ready; null while Starting.</summary>
    public DateTimeOffset? ReadyAt { get; init; }

    /// <summary>Restarts after Ready so far (<c>RuntimeStatus.RestartCount</c>).</summary>
    public int RestartCount { get; init; }

    /// <summary>When each restart happened (the restart budget's window, <c>Agent:Process:RestartWindow</c>).</summary>
    public IReadOnlyList<DateTimeOffset> RestartTimes { get; init; } = [];

    /// <summary>GPU memory measured at start (Σ per-GPU <c>memory.used</c> after Ready − before create), GiB; null when not measured.</summary>
    public double? MeasuredGpuGiB { get; init; }
}

/// <summary><c>{state}/processes.json</c>: the process records of the node (design-p3p4 §A.1.3).</summary>
public sealed record ProcessRecordsDocument
{
    /// <summary>Current schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>File name in the state directory.</summary>
    public const string FileName = "processes.json";

    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>One record per instance (model id unique).</summary>
    public IReadOnlyList<ProcessRecord> Processes { get; init; } = [];

    /// <summary>
    /// Starts in flight (MN-207, D121): the driver writes one durably <b>before</b> <c>CreateProcess</c>, replaces it with the
    /// <see cref="ProcessRecord"/> right after, and removes it when the create fails. After a crash in between, the orphan
    /// sweep can tell the agent's own process (same argv digest, started after <see cref="PendingProcessStart.At"/>) from
    /// any other process, and kills only the former. The sweep removes the entries it has resolved.
    /// </summary>
    public IReadOnlyList<PendingProcessStart> Pending { get; init; } = [];
}

/// <summary>A start the driver is about to make (<see cref="ProcessRecordsDocument.Pending"/>; MN-207, D121).</summary>
public sealed record PendingProcessStart
{
    /// <summary>Model id.</summary>
    public required string ModelId { get; init; }

    /// <summary>The launch operation (or the restart's operation) that makes the start.</summary>
    public required string OperationId { get; init; }

    /// <summary>The verified executable about to be started.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>The port substituted for <c>{port}</c>.</summary>
    public required int Port { get; init; }

    /// <summary>Digest of the substituted argv (<see cref="ProcessRecord.ArgvDigest"/>'s function): the sweep's marker.</summary>
    public required string ArgvDigest { get; init; }

    /// <summary>When the entry was written (just before the create).</summary>
    public required DateTimeOffset At { get; init; }
}

/// <summary>
/// Durable store of <see cref="ProcessRecord"/>s (frozen by MN-202, D116; implemented by MN-211's
/// <c>ProcessRecordStore</c>). Writes are atomic and durable before they return; a torn or unreadable file is reported,
/// never silently emptied (the operation log's rule).
/// </summary>
public interface IProcessRecordStore
{
    /// <summary>Every record.</summary>
    Task<IReadOnlyList<ProcessRecord>> ListAsync(CancellationToken ct = default);

    /// <summary>The record of a model, or null.</summary>
    Task<ProcessRecord?> GetAsync(string modelId, CancellationToken ct = default);

    /// <summary>Inserts or replaces the model's record.</summary>
    Task UpsertAsync(ProcessRecord record, CancellationToken ct = default);

    /// <summary>Removes the model's record; false when there was none.</summary>
    Task<bool> RemoveAsync(string modelId, CancellationToken ct = default);

    /// <summary>Every pending start (<see cref="ProcessRecordsDocument.Pending"/>; MN-207, D121).</summary>
    Task<IReadOnlyList<PendingProcessStart>> ListPendingAsync(CancellationToken ct = default);

    /// <summary>Writes a pending start durably, replacing the model's previous one; returns once it is on disk.</summary>
    Task AddPendingAsync(PendingProcessStart pending, CancellationToken ct = default);

    /// <summary>
    /// Replaces the model's pending start with its record in one atomic write (the create succeeded). The record's
    /// <see cref="ProcessRecord.ModelId"/> names the pending entry.
    /// </summary>
    Task CommitPendingAsync(ProcessRecord record, CancellationToken ct = default);

    /// <summary>Removes the model's pending start (the create failed, or the sweep resolved it); false when there was none.</summary>
    Task<bool> RemovePendingAsync(string modelId, CancellationToken ct = default);
}

/// <summary>
/// Protects the per-instance API key at rest (design-p3p4 §A.8; frozen by MN-202, D116). Windows: DPAPI, machine scope
/// with the service account's SID as entropy, through a P/Invoke of <c>CryptProtectData</c> (MN-222; the
/// <c>System.Security.Cryptography.ProtectedData</c> package is not used: no new NuGet package). Tests use a reversible fake.
/// </summary>
public interface IInstanceKeyProtector
{
    /// <summary>The protected form of <paramref name="key"/> (base64).</summary>
    string Protect(string key);

    /// <summary>The clear key; throws <see cref="System.Security.Cryptography.CryptographicException"/> when it cannot be unprotected.</summary>
    string Unprotect(string protectedKey);
}

/// <summary>
/// GPU <c>memory.used</c> per placed GPU right before create and right after Ready: the "measured at start" figure on
/// WDDM, where <c>nvidia-smi</c> reports no per-process memory (design-p3p4 §A.1.9; frozen by MN-202, D116; implemented by
/// MN-214, called by the process driver when registered).
/// </summary>
public interface IGpuStartMeter
{
    /// <summary>Samples <c>memory.used</c> of <paramref name="gpuUuids"/> now; null when the GPUs cannot be read.</summary>
    Task<GpuStartMark?> MarkAsync(IReadOnlyList<string> gpuUuids, CancellationToken ct = default);

    /// <summary>Σ (used now − used at <paramref name="mark"/>) over the marked GPUs in GiB, never negative; null when unreadable.</summary>
    Task<double?> MeasureAsync(GpuStartMark mark, CancellationToken ct = default);
}

/// <summary>A <see cref="IGpuStartMeter.MarkAsync"/> sample.</summary>
/// <param name="At">When it was taken.</param>
/// <param name="UsedGiB">GPU UUID → <c>memory.used</c> in GiB.</param>
public sealed record GpuStartMark(DateTimeOffset At, IReadOnlyDictionary<string, double> UsedGiB);
