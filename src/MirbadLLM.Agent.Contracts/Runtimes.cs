using System.Text.Json.Serialization;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>A GPU by index and UUID (design §4.3 <c>launch-config.gpus</c>, <c>RuntimeStatus.gpus</c>). The UUID is the identity.</summary>
/// <param name="Index"><c>nvidia-smi</c> index (display only).</param>
/// <param name="Uuid">GPU UUID.</param>
public sealed record GpuRef(int Index, string Uuid);

/// <summary>
/// One runtime instance on a node (design §4.3 <c>RuntimeStatus</c>), derived from Docker (or the process table) and
/// its labels. <c>RestartPending</c> is not here: the master computes it from the saved definition (§5.1).
/// </summary>
public sealed record RuntimeStatus
{
    /// <summary>Model id (<c>mirbadllm.model</c>).</summary>
    public required string ModelId { get; init; }

    /// <summary>Lifecycle state (today's mapping from container state + <c>/health</c>).</summary>
    public required ModelState State { get; init; }

    /// <summary>Container id or process id.</summary>
    public string? InstanceId { get; init; }

    /// <summary>Loopback port on the node (never reachable from the LAN, §7.2).</summary>
    public int? HostPort { get; init; }

    /// <summary>Since when the state holds.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Failure text (with the last log lines from <c>FailureReport</c>) when Failed.</summary>
    public string? Error { get; init; }

    /// <summary>Restarts seen by the runtime (crash-loop handling stops it at 3 in 10 minutes).</summary>
    public int RestartCount { get; init; }

    /// <summary>All labels of the instance.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Served name (<c>mirbadllm.served-name</c>).</summary>
    public string? ServedName { get; init; }

    /// <summary>Legacy config hash (<c>mirbadllm.config-hash</c>).</summary>
    public string? ConfigHash { get; init; }

    /// <summary>Execution fingerprint (<c>mirbadllm.exec-fingerprint</c>); null on instances from before P1.</summary>
    public string? ExecutionFingerprint { get; init; }

    /// <summary>True for tuning candidates (<c>mirbadllm.tuning=1</c>).</summary>
    public bool Tuning { get; init; }

    /// <summary>GPUs the instance uses (empty = all/unified).</summary>
    public IReadOnlyList<GpuRef> Gpus { get; init; } = [];

    /// <summary>Operation that created the instance (<c>mirbadllm.operation</c>); null on adopted instances.</summary>
    public string? OperationId { get; init; }

    /// <summary>Measured GPU memory of the instance's processes, when hostd reports them.</summary>
    public double? MeasuredGpuGiB { get; init; }

    /// <summary>Runtime kind.</summary>
    public RuntimeKind Runtime { get; init; } = RuntimeKind.Vllm;

    /// <summary>Container or process.</summary>
    public LaunchDelivery Delivery { get; init; } = LaunchDelivery.Container;
}

/// <summary>Query of <c>GET /agent/v1/runtimes</c> (design §4.3).</summary>
public sealed record RuntimesQuery
{
    /// <summary><c>tuning</c> (<see cref="TuningLabel"/>) lists only tuning candidates; null lists all.</summary>
    public string? Label { get; init; }

    /// <summary>The <see cref="Label"/> value selecting tuning candidates.</summary>
    public const string TuningLabel = "tuning";
}

/// <summary>
/// How a launch treats an existing instance (design §4.3 "mode", finding 4). Deduplication is by operation id only: a
/// repeated id never repeats work; a new <see cref="Replace"/> operation always recreates.
/// </summary>
public enum LaunchMode
{
    /// <summary>
    /// Today's <c>StartAsync</c>: an existing Starting/Ready/Stopping instance is returned as it is, whatever its
    /// configuration; a Failed instance is removed and recreated; no instance → create.
    /// </summary>
    EnsureStarted,

    /// <summary>
    /// Today's <c>LaunchAsync</c>/<c>RestartAsync</c>: always stop, remove and recreate, even with an identical
    /// configuration. Admission counts the old instance as released only after its removal is confirmed and memory is
    /// re-probed (§4.8).
    /// </summary>
    Replace,
}

/// <summary>
/// <c>POST /agent/v1/runtimes/{modelId}/launch</c> (design §4.3, §4.8, §4.9). Answered 202 with the operation after the
/// fencing check, start rule, admission and reservation; pull, create and start run inside the operation.
/// </summary>
public sealed record LaunchRequest : AgentMutation
{
    /// <summary>Ensure-started or replace.</summary>
    public required LaunchMode Mode { get; init; }

    /// <summary>The fully built instance description; the agent never interprets vLLM flags.</summary>
    public required LaunchSpec Spec { get; init; }

    /// <summary>Admission inputs.</summary>
    public required AdmissionRequest Admission { get; init; }

    /// <summary>Legacy config hash the master expects the instance to carry (also in <see cref="LaunchSpec.Labels"/>).</summary>
    public string? ExpectedConfigHash { get; init; }

    /// <summary>Execution fingerprint the master expects (also in <see cref="LaunchSpec.Labels"/>).</summary>
    public string? ExpectedExecutionFingerprint { get; init; }
}

/// <summary>
/// The runtime-ready description of an instance (design §1 glossary "Launch spec", §4.3, §10.3). The master never sends
/// a host path: mounts name logical volumes, and process argv may use <c>{models}</c>/<c>{cache}</c> tokens. Guardrails
/// (§10.3): approved image digests only, logical volumes with fixed targets, no privileged/cap-add/host network/PID,
/// container port fixed and published on 127.0.0.1 with an agent-allocated host port, reserved labels refused.
/// </summary>
public sealed record LaunchSpec
{
    /// <summary>Container or native process.</summary>
    public required LaunchDelivery Kind { get; init; }

    /// <summary>Runtime kind (vLLM today).</summary>
    public RuntimeKind Runtime { get; init; } = RuntimeKind.Vllm;

    /// <summary>Container image reference (container delivery).</summary>
    public string? Image { get; init; }

    /// <summary>Approved repository digest of <see cref="Image"/> (required for new launches from P2, §10.3).</summary>
    public string? ImageDigest { get; init; }

    /// <summary>Logical executable name configured on the node (process delivery), e.g. <c>llama-server</c>.</summary>
    public string? Executable { get; init; }

    /// <summary>
    /// Version of <see cref="Executable"/> to run (process delivery, design-p3p4 §A.1.2; MN-202, D116), e.g. <c>b11476</c>.
    /// The master's builder always sends a concrete version (the model's pin, else the node's default from
    /// <c>RuntimeCapability.Versions</c>), so the launch fingerprint and the read-back's agree and a changed node default
    /// shows as "restart pending"; null (hand-built specs) = the node's default, which the agent resolves and reports in
    /// <see cref="LaunchConfig.ExecutableVersion"/>. The agent refuses an unknown name or version with 422 <c>executable_missing</c>
    /// and a version whose files fail verification with 422 <c>executable_unverified</c>. Omitted when null; sent only in
    /// process specs, i.e. only to agents reporting <see cref="AgentFeatures.RuntimeLlamaCppProcess"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutableVersion { get; init; }

    /// <summary>
    /// Arguments (vLLM: the container <c>Cmd</c>; process: argv with the <c>{models}</c>/<c>{cache}</c>/<c>{port}</c>
    /// tokens of <see cref="ProcessArgvTokens"/>, which the agent substitutes and confines, design-p3p4 §A.1.3).
    /// </summary>
    public IReadOnlyList<string> Argv { get; init; } = [];

    /// <summary>
    /// Environment variables. A process spec may not name any of <see cref="ProcessEnvironment.Reserved"/> (422
    /// <c>spec_rejected</c>): the agent builds the child's whole environment block itself (design-p3p4 §A.1.3).
    /// </summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>Names in <see cref="Env"/> whose values are secrets: never stored, digested or returned by <c>launch-config</c>.</summary>
    public IReadOnlyList<string> SecretEnvNames { get; init; } = [];

    /// <summary>Port the runtime listens on inside the container (vLLM: 8000).</summary>
    public int ContainerPort { get; init; } = 8000;

    /// <summary>Logical mounts.</summary>
    public IReadOnlyList<MountSpec> Mounts { get; init; } = [];

    /// <summary>GPU selection.</summary>
    public GpuSelection Gpus { get; init; } = new();

    /// <summary><c>ipc: host</c> (vLLM needs it).</summary>
    public bool IpcHost { get; init; }

    /// <summary>Health probe path (<c>/health</c>).</summary>
    public string HealthPath { get; init; } = "/health";

    /// <summary>
    /// Labels to set (served name, config hash, execution fingerprint, budget, tuning, GPU metadata). Must not contain
    /// <see cref="ContainerLabels.Reserved"/>; the agent sets those itself.
    /// </summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();
}

/// <summary>A logical mount (design §4.3, §10.3).</summary>
/// <param name="Volume"><see cref="LogicalVolumes.Models"/> or <see cref="LogicalVolumes.Cache"/>.</param>
/// <param name="SubPath">Optional plain relative path inside the volume, confined after resolving symlinks. Null = the root.</param>
/// <param name="Target">Container target: <see cref="LogicalVolumes.ModelsTarget"/> or <see cref="LogicalVolumes.CacheTarget"/>.</param>
/// <param name="ReadOnly">Read-only mount (runtimes always mount <c>models</c> read-only).</param>
public sealed record MountSpec(string Volume, string? SubPath, string Target, bool ReadOnly);

/// <summary>How an instance selects GPUs (design §4.3, §5.1).</summary>
public enum GpuSelectionMode
{
    /// <summary>Every GPU (<c>Count=-1</c>), today's DGX behaviour.</summary>
    All,

    /// <summary>No GPU.</summary>
    None,

    /// <summary>The GPUs in <see cref="GpuSelection.Uuids"/> (<c>DeviceIDs</c> / <c>CUDA_VISIBLE_DEVICES</c>), P2.</summary>
    Uuids,
}

/// <summary>GPU selection of a launch spec: <c>{"mode": "all"}</c> or <c>{"mode": "uuids", "uuids": [...]}</c>.</summary>
public sealed record GpuSelection
{
    /// <summary>Selection mode.</summary>
    public GpuSelectionMode Mode { get; init; } = GpuSelectionMode.All;

    /// <summary>Ordered GPU UUIDs when <see cref="Mode"/> is <see cref="GpuSelectionMode.Uuids"/>; missing ones → 422 <c>gpu_missing</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Uuids { get; init; }
}

/// <summary>Admission inputs of a launch (design §4.3, §4.8, §6.1). The agent decides with a fresh probe under its lock.</summary>
public sealed record AdmissionRequest
{
    /// <summary>Memory budget (unified: whole model; discrete: total across the placed GPUs).</summary>
    public required double BudgetGiB { get; init; }

    /// <summary>Host-memory budget on discrete nodes (P2). Null on unified nodes or = runtime estimate.</summary>
    public double? HostBudgetGiB { get; init; }

    /// <summary>Node reserve (from the node record).</summary>
    public required double ReserveGiB { get; init; }

    /// <summary>
    /// Per-GPU reserve on discrete nodes (P2): the reserve of every placed GPU that has no entry in
    /// <see cref="GpuReservesGiB"/>. Null = 0. On discrete nodes <see cref="ReserveGiB"/> is the host-RAM reserve.
    /// </summary>
    public double? GpuReserveGiB { get; init; }

    /// <summary>
    /// Per-GPU reserves by GPU UUID on discrete nodes (design §6.1, from <c>NodeRecord.GpuReserveGiB</c>; MN-101, D73).
    /// Overrides <see cref="GpuReserveGiB"/> for the GPUs it names. Sent only to agents reporting
    /// <see cref="AgentFeatures.AdmissionDiscrete"/> and omitted when null, so P1 requests are unchanged.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, double>? GpuReservesGiB { get; init; }

    /// <summary>The UI's "Start anyway": skip memory admission (the start rule still applies).</summary>
    public bool Force { get; init; }

    /// <summary>
    /// Per-GPU budgets by GPU UUID (design-p3p4 §A.1.5, from <c>LlamaCppSettings.GpuBudgetsGiB</c> in placement order;
    /// MN-202, D116): each placed GPU is admitted against its own entry instead of <c>BudgetGiB ÷ n</c>, so a llama.cpp
    /// model can span unequal GPUs (12 + 16 GB). Sent only to agents reporting <see cref="AgentFeatures.AdmissionGpuBudgets"/>
    /// and omitted when null; without it the agent keeps P2's even split. The values sum to <see cref="BudgetGiB"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, double>? GpuBudgetsGiB { get; init; }

    /// <summary>
    /// The reserve that applies to <paramref name="gpuUuid"/>: its <see cref="GpuReservesGiB"/> entry, else
    /// <see cref="GpuReserveGiB"/>, else 0.
    /// </summary>
    public double ReserveForGpu(string gpuUuid) =>
        GpuReservesGiB is { } map && gpuUuid is not null && map.TryGetValue(gpuUuid, out double reserve) ? reserve : GpuReserveGiB ?? 0;

    /// <summary>
    /// The budget <paramref name="gpuUuid"/> is admitted against: its <see cref="GpuBudgetsGiB"/> entry, else the even
    /// split <c>BudgetGiB ÷ <paramref name="placedGpuCount"/></c> (P2).
    /// </summary>
    public double BudgetForGpu(string gpuUuid, int placedGpuCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(placedGpuCount, 1);
        return GpuBudgetsGiB is { } map && gpuUuid is not null && map.TryGetValue(gpuUuid, out double budget)
            ? budget
            : BudgetGiB / placedGpuCount;
    }
}

/// <summary><c>POST /agent/v1/runtimes/{modelId}/stop</c> (design §4.3): stop and remove; no instance → Succeeded.</summary>
public sealed record StopRequest : AgentMutation;

/// <summary>
/// <c>GET /agent/v1/runtimes/{modelId}/launch-config</c> (design §4.3, §5.1): what the current instance was launched with.
/// Feeds <c>VllmArgsParser</c>, <c>ModelConfigHash</c> and the execution fingerprint on the master. Secret environment
/// values are never returned (null values in <see cref="Env"/>).
/// </summary>
public sealed record LaunchConfig
{
    /// <summary>Container or process.</summary>
    public LaunchDelivery Kind { get; init; } = LaunchDelivery.Container;

    /// <summary>Image reference (container).</summary>
    public string? Image { get; init; }

    /// <summary>Image digest (container).</summary>
    public string? ImageDigest { get; init; }

    /// <summary>Logical executable (process).</summary>
    public string? Executable { get; init; }

    /// <summary>
    /// The executable version the instance runs (process; the resolved version when the spec named none; MN-202, D116).
    /// Feeds the execution fingerprint's <c>exe-version</c>. Omitted when null (every container read-back).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutableVersion { get; init; }

    /// <summary>The container <c>Cmd</c> or process argv as launched (process: tokens kept, <c>{port}</c> included).</summary>
    public IReadOnlyList<string> Argv { get; init; } = [];

    /// <summary>Environment: name → value, value null for secrets.</summary>
    public IReadOnlyDictionary<string, string?> Env { get; init; } = new Dictionary<string, string?>();

    /// <summary>Logical mounts.</summary>
    public IReadOnlyList<MountSpec> Mounts { get; init; } = [];

    /// <summary>GPUs the instance uses.</summary>
    public IReadOnlyList<GpuRef> Gpus { get; init; } = [];

    /// <summary>All labels.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Execution fingerprint label; null on instances from before P1.</summary>
    public string? ExecutionFingerprint { get; init; }

    /// <summary>When the instance was created.</summary>
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>Query of <c>GET /agent/v1/runtimes/{modelId}/logs</c> (design §4.3): SSE, one <c>data:</c> line per log line.</summary>
public sealed record LogsQuery
{
    /// <summary>Lines of history first.</summary>
    public int Tail { get; init; } = 200;

    /// <summary>Keep streaming new lines.</summary>
    public bool Follow { get; init; } = true;
}
