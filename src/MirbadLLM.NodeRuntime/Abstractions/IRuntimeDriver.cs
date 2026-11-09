using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime.Abstractions;

/// <summary>What the driver is asked to launch (design §4.3 launch, §4.9).</summary>
public sealed record RuntimeLaunch
{
    /// <summary>Model id (instance name <c>ss-{ModelId}</c>).</summary>
    public required string ModelId { get; init; }

    /// <summary>The launch operation (set as <c>mirbadllm.operation</c>).</summary>
    public required string OperationId { get; init; }

    /// <summary>Ensure-started or replace.</summary>
    public required LaunchMode Mode { get; init; }

    /// <summary>The validated launch spec.</summary>
    public required LaunchSpec Spec { get; init; }

    /// <summary>Host port from the reservation (<see cref="IPortAllocator"/>); the driver may also ask the allocator itself.</summary>
    public required int HostPort { get; init; }

    /// <summary>The login for pulling a private image; null for a public one.</summary>
    public RegistryCredentials? Registry { get; init; }
}

/// <summary>What a launch ended with.</summary>
public enum RuntimeLaunchOutcome
{
    /// <summary>A new instance was created and started.</summary>
    Created,

    /// <summary><see cref="LaunchMode.EnsureStarted"/> found a Starting/Ready/Stopping instance and returned it as it is.</summary>
    AlreadyRunning,
}

/// <summary>Result of <see cref="IRuntimeDriver.LaunchAsync"/>.</summary>
/// <param name="Outcome">Created or already running.</param>
/// <param name="Instance">The instance now (Starting for a new one).</param>
public sealed record RuntimeLaunchResult(RuntimeLaunchOutcome Outcome, RuntimeStatus Instance);

/// <summary>
/// Callbacks a driver invokes while it executes a launch or stop, so the operation log and fencing stay in charge
/// (design §4.2, §4.4 item 4, §4.8). Implemented by the agent's runtime operation handler.
/// </summary>
public interface IRuntimeStageSink
{
    /// <summary>
    /// Called before each destructive step (<see cref="OperationStages.Stopping"/>, <see cref="OperationStages.Removed"/>,
    /// <see cref="OperationStages.Created"/>, <see cref="OperationStages.Started"/>): re-checks fencing and, before create,
    /// re-checks admission when needed. Throws <see cref="AgentException"/> (<c>superseded</c>, <c>admission_refused</c>)
    /// to stop the operation at that point.
    /// </summary>
    Task BeforeStepAsync(string stage, CancellationToken ct);

    /// <summary>Records that a stage was reached (durably, through the operation log), with optional progress (image pull).</summary>
    Task StageReachedAsync(string stage, OperationProgress? progress, CancellationToken ct);
}

/// <summary>
/// Hands out loopback host ports for runtime instances (design §4.8, §10.3: the port is allocated by the agent, never
/// chosen by the master). Implemented by the agent's reservation manager (MN-016) under its node-wide admission lock and
/// injected into the container driver (MN-015). Lives in NodeRuntime because the driver does.
/// </summary>
public interface IPortAllocator
{
    /// <summary>
    /// The port reserved for <paramref name="operationId"/>, allocating one for <paramref name="modelId"/> if the
    /// reservation has none yet. Never returns a port another instance or reservation uses.
    /// </summary>
    Task<int> AllocatePortAsync(string modelId, string operationId, CancellationToken ct = default);
}

/// <summary>
/// Executes runtime instances on the node (design §2.4, §4.3, §6.6): the container driver ported from
/// the pre-P1 <c>DockerModelRuntime</c> (MN-015, <c>ContainerRuntimeDriver</c> in NodeRuntime), a process driver later (P3/P4), and
/// a fake driver for the dev setup (MN-042). Lives in NodeRuntime (not <c>MirbadLLM.Agent/Abstractions</c>) because the
/// container driver is implemented there and NodeRuntime cannot reference the agent host.
/// <para>Invariants:</para>
/// <list type="bullet">
/// <item>The driver sets <c>mirbadllm.model</c>, <c>mirbadllm.node</c> and <c>mirbadllm.operation</c> itself and
/// refuses specs that set them (422 <c>spec_rejected</c>); it only stops or removes instances carrying its own labels.</item>
/// <item>Container names stay <c>ss-{id}</c> and today's labels are kept, so instances from before P1 are adopted.</item>
/// <item>State mapping, crash-loop stop and the <c>/health</c> probe behave as in the pre-P1 <c>DockerModelRuntime</c>: a
/// container that Docker restarted at least once (or that is <c>restarting</c>) and is still not healthy is a crash loop;
/// the driver stops it and reports Failed with the exit code and the last log lines as the root cause, so an instance is
/// never stuck at Starting (W2 review MN-092, D43).</item>
/// <item>Mounts resolve only the logical volumes <c>models</c>/<c>cache</c> to their fixed targets; ports are published on
/// 127.0.0.1 only.</item>
/// <item>The image pull is its own stage reported through <see cref="IRuntimeStageSink"/>.</item>
/// </list>
/// </summary>
public interface IRuntimeDriver
{
    /// <summary>
    /// Checks a spec against the node's guardrails without side effects (no Docker call, no reservation): throws an
    /// <see cref="AgentException"/> (422 <c>spec_rejected</c>, 400 <c>invalid_request</c>) for a spec the driver would
    /// refuse. The launch handler calls it before reserving, so such a launch is refused synchronously and records nothing
    /// (design §4.3). Default: accepts every spec. Added by the W2 review (MN-092).
    /// </summary>
    void CheckSpec(string modelId, LaunchSpec spec)
    {
    }

    /// <summary>The delivery this driver executes.</summary>
    LaunchDelivery Delivery { get; }

    /// <summary>Every instance with mirbadllm labels on the node.</summary>
    Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default);

    /// <summary>The instance of a model, or null (Stopped).</summary>
    Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default);

    /// <summary>
    /// Launches per <see cref="RuntimeLaunch.Mode"/>: ensure-started returns a Starting/Ready/Stopping instance as it is
    /// and recreates a Failed one; replace always stops, removes and recreates. Returns once the instance is created and
    /// started (state Starting); readiness is observed separately.
    /// </summary>
    Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default);

    /// <summary>Starts an instance that was created but not started (reconciliation after a crash, design §4.4 item 5).</summary>
    Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default);

    /// <summary>Stops and removes a model's instance. No instance → no-op.</summary>
    Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default);

    /// <summary>What the instance was launched with; null when there is none.</summary>
    Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default);

    /// <summary>Log lines (history, then new lines when <paramref name="follow"/>).</summary>
    IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, CancellationToken ct = default);

    /// <summary>
    /// The upstream base address of a running instance for the agent proxy: <c>http://ss-{id}:8000/</c> on the
    /// <c>mirbadllm</c> network, else <c>http://127.0.0.1:{hostPort}/</c>. Null when it is not running.
    /// </summary>
    Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default);

    /// <summary>
    /// The bearer key the agent proxy and the metrics scrape must send upstream for this instance (process delivery: the
    /// per-instance <c>LLAMA_API_KEY</c>, design-p3p4 §A.8; MN-202, D116). The proxy strips the client's
    /// <c>Authorization</c> header and adds <c>Bearer {key}</c> only when this returns a key. Default: null (containers are
    /// reached on a private network or loopback without a key, as today), so the container driver is unchanged.
    /// </summary>
    Task<string?> GetUpstreamApiKeyAsync(string modelId, CancellationToken ct = default) => Task.FromResult<string?>(null);

    /// <summary>
    /// How the agent proxy must rewrite a JSON request body before it reaches this instance (docs/macos/implementation-plan.md,
    /// Phase 3: an MLX server loads whatever the request's <c>model</c>, <c>draft_model</c> or <c>adapters</c> names, spike S4).
    /// Default: null (bodies are streamed unchanged), so the container driver and llama.cpp instances are unchanged.
    /// </summary>
    Task<UpstreamBodyRewrite?> GetUpstreamBodyRewriteAsync(string modelId, CancellationToken ct = default) =>
        Task.FromResult<UpstreamBodyRewrite?>(null);

    /// <summary>True when any instance mounts <c>{models}/{folder}</c> (folder delete answers 409 <c>in_use</c>).</summary>
    Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default);
}

/// <summary>
/// A request-body rewrite for the agent proxy (<see cref="IRuntimeDriver.GetUpstreamBodyRewriteAsync"/>): a JSON object
/// body gets <c>model</c> = <paramref name="Model"/> and loses <paramref name="RemovedFields"/>; any other body is forwarded as
/// it is (the server refuses it itself).
/// </summary>
/// <param name="Model">The value <c>model</c> is set to.</param>
/// <param name="RemovedFields">Top-level fields removed from the body.</param>
/// <param name="ResponseModel">
/// The name responses report instead of <paramref name="Model"/> (the served name), so a client never sees the server's
/// internal name; null leaves responses as they are.
/// </param>
public sealed record UpstreamBodyRewrite(string Model, IReadOnlyList<string> RemovedFields, string? ResponseModel = null);
