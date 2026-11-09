using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>Tunables of <see cref="RemoteModelRuntime"/> (defaults = design §4.5).</summary>
public sealed class RemoteModelRuntimeOptions
{
    /// <summary>How often an operation is re-read while the runtime waits for it.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a start waits for its instance to be created (design §4.5: 60 s). The bound restarts whenever the operation
    /// reaches a new stage and does not run out while the image is being pulled ("bounded by 60 s, or by the pull").
    /// </summary>
    public TimeSpan CreateTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a stop (or a cancel) waits for its operation to end.</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// The master's <see cref="IModelRuntime"/> and <see cref="IModelLauncher"/> over the node agents (design §4.3, §4.5,
/// §4.8, §4.9, §5.1): model instances live on the node the definition is placed on, and every mutation is an agent
/// operation submitted through <see cref="AgentOperationRunner"/> (operation reference written before sending).
/// <list type="bullet">
/// <item><see cref="StartAsync"/> = launch <c>ensureStarted</c>; <see cref="RestartAsync"/> and <see cref="LaunchAsync"/> =
/// launch <c>replace</c>. The spec comes from <see cref="LaunchSpecBuilder"/> (today's argv with the node's total memory,
/// legacy config hash and execution fingerprint labels). The call returns once the agent reports the instance created
/// (stage <c>created</c> or later), as today's contract says; the wait is bounded by
/// <see cref="RemoteModelRuntimeOptions.CreateTimeout"/> but never cuts an image pull short.</item>
/// <item>Agent refusals map to today's exceptions (<see cref="AgentErrorMapper"/>): <c>admission_refused</c> →
/// <see cref="ModelMemoryAdmissionException"/>, <c>start_conflict</c> → <see cref="ModelStartConflictException"/>, with
/// today's texts plus "on node {id}". A <c>start_conflict</c> about the model itself (its own earlier launch is still
/// admitted or pulling, e.g. after a master restart) is not an error: <c>ensureStarted</c> waits for that launch like a
/// normal start, and <c>replace</c> supersedes it (cancel, or stop once its instance exists) and launches again
/// (W2 review MN-092).</item>
/// <item>Status: <see cref="ListStatusAsync"/> reads the <see cref="INodeStatusCache"/> (no agent call per model);
/// <see cref="GetStatusAsync"/> asks the agent and falls back to the cache when the node cannot answer. Restart-pending
/// follows <see cref="RestartPendingRule"/>: the legacy hash with today's <c>Cmd</c> recheck (through
/// <c>/launch-config</c>) or a differing execution fingerprint.</item>
/// <item>Mutations of one model are serialized by a per-model lock on the master (design §4.7); commands are only sent to a
/// node the cache reports Online or Degraded, and new starts are refused on a draining node (design §3.5).</item>
/// </list>
/// Not registered in DI here (MN-040).
/// </summary>
public sealed class RemoteModelRuntime : IModelRuntime, IModelLauncher
{
    private readonly IModelRegistry _registry;
    private readonly INodeRegistry _nodes;
    private readonly INodeStatusCache _cache;
    private readonly IAgentClientFactory _clients;
    private readonly AgentOperationRunner _runner;
    private readonly MirbadLLMOptions _options;
    private readonly TimeProvider _time;
    private readonly RemoteModelRuntimeOptions _runtimeOptions;
    private readonly ILogger _logger;
    private readonly ModelRecoveryState? _recovery;

    /// <summary>
    /// The composed fit preview, for the GGUF estimate a llama.cpp launch's host budget is worked out from (MN-239-17);
    /// null = no estimate, and the host budget is the flat llama.cpp 1 GiB as before.
    /// </summary>
    private readonly PlacementFit? _fit;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelLocks = new(StringComparer.Ordinal);

    /// <summary>Node total memory by node uid: MemTotal does not change while the node runs (today's rule).</summary>
    private readonly ConcurrentDictionary<string, double> _totals = new(StringComparer.Ordinal);

    /// <summary>Canonical config hash per instance (<c>{node}/{instance}</c>), recomputed from its command line (today's cache).</summary>
    private readonly ConcurrentDictionary<string, string> _canonicalHashes = new(StringComparer.Ordinal);

    /// <summary>Creates the runtime.</summary>
    private readonly ITokenStore? _tokens;

    public RemoteModelRuntime(
        IModelRegistry registry,
        INodeRegistry nodes,
        INodeStatusCache cache,
        IAgentClientFactory clients,
        AgentOperationRunner runner,
        IOptions<MirbadLLMOptions> options,
        TimeProvider? timeProvider = null,
        ILogger<RemoteModelRuntime>? logger = null,
        RemoteModelRuntimeOptions? runtimeOptions = null,
        PlacementFit? placementFit = null,
        ModelRecoveryState? recovery = null,
        ITokenStore? tokens = null)
    {
        _tokens = tokens;
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        _registry = registry;
        _nodes = nodes;
        _cache = cache;
        _clients = clients;
        _runner = runner;
        _options = options.Value;
        _time = timeProvider ?? TimeProvider.System;
        _runtimeOptions = runtimeOptions ?? new RemoteModelRuntimeOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _fit = placementFit;
        _recovery = recovery;
    }

    // ---------------------------------------------------------------- status

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default)
    {
        var models = await _registry.ListAsync(ct).ConfigureAwait(false);
        var statuses = new List<ModelStatus>(models.Count);
        foreach (var model in models)
        {
            statuses.Add(await MapAsync(model.Id, model, _cache.GetRuntime(model.Id), ct).ConfigureAwait(false));
        }

        return statuses;
    }

    /// <inheritdoc />
    public async Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var saved = await _registry.GetAsync(id, ct).ConfigureAwait(false);
        string? nodeId = NodeOf(id, saved);
        if (nodeId is null)
        {
            return new ModelStatus(id, ModelState.Stopped);
        }

        return await MapAsync(id, saved, await ReadRuntimeAsync(nodeId, id, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- start, restart, launch, stop

    /// <inheritdoc />
    public async Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var model = await _registry.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown model '{id}'.");
        return await LaunchLockedAsync(model, LaunchMode.EnsureStarted, force, tuning: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ModelStatus> RestartAsync(string id, bool force = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var model = await _registry.GetAsync(id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown model '{id}'.");
        return await LaunchLockedAsync(model, LaunchMode.Replace, force, tuning: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<ModelStatus> LaunchAsync(ModelDefinition definition, LaunchOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);
        return LaunchLockedAsync(definition, LaunchMode.Replace, options.Force, options.Tuning, ct);
    }

    /// <inheritdoc />
    public async Task StopAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var saved = await _registry.GetAsync(id, ct).ConfigureAwait(false);
        string? nodeId = NodeOf(id, saved);
        if (nodeId is null)
        {
            return; // unplaced and not running anywhere: nothing to stop (today: no container → return)
        }

        var gate = ModelLock(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RequireServing(nodeId, id, forStart: false);
            var beforeStop = _cache.GetNode(nodeId);
            await StopOnNodeAsync(nodeId, id, ct).ConfigureAwait(false);
            if (_cache.GetNode(nodeId) is { } live && live.NodeUid == beforeStop?.NodeUid)
            {
                _recovery?.Observe(live, id, ModelState.Stopped);
            }
        }
        finally
        {
            gate.Release();
        }

        await RefreshCacheAsync(id).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- launcher reads

    /// <inheritdoc />
    public async Task<ModelDefinition?> GetLaunchedDefinitionAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var saved = await _registry.GetAsync(id, ct).ConfigureAwait(false);
        string? nodeId = NodeOf(id, saved);
        if (nodeId is null || await _nodes.GetAsync(nodeId, ct).ConfigureAwait(false) is not { } node)
        {
            return null;
        }

        var launch = await _clients.GetClient(nodeId).GetLaunchConfigAsync(id, ct).ConfigureAwait(false);
        if (launch is null)
        {
            return null;
        }

        var template = saved ?? new ModelDefinition { Id = id, ServedName = id, ModelPath = id, Placement = new ModelPlacement(nodeId) };
        if (IsLlamaCppProcess(launch))
        {
            // MN-219 (D125): the llama.cpp argv reads back through its own parser (design-p3p4 §A.1.6 "Read-back").
            return LlamaCppArgsParser.Parse(launch, template with { Runtime = RuntimeKind.LlamaCpp }, node);
        }

        if (IsMlxProcess(launch))
        {
            return MlxArgsParser.Parse(launch, template with { Runtime = RuntimeKind.Mlx }, node);
        }

        if (launch.Runtime is RuntimeKind.Sglang or RuntimeKind.Nim)
        {
            return ContainerRuntimeArgsParser.Parse(launch, template);
        }

        if (launch.Runtime == RuntimeKind.LlamaCpp)
        {
            // shortcut: llama.cpp in a container has no parser of its own, so the saved settings stand in for what runs and the Apply dialog lists no changes; add a parser if container delivery gets real use.
            return template with { Runtime = RuntimeKind.LlamaCpp };
        }

        double totalGiB = await GetTotalGiBAsync(node, ct).ConfigureAwait(false);
        return VllmArgsParser.Parse(launch, LaunchSpecBuilder.DefaultImage(node, _options), totalGiB, template);
    }

    /// <inheritdoc />
    public async Task<string?> GetLaunchedConfigHashAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var saved = await _registry.GetAsync(id, ct).ConfigureAwait(false);
        string? nodeId = NodeOf(id, saved);
        if (nodeId is null)
        {
            return null;
        }

        var runtime = await _clients.GetClient(nodeId).GetRuntimeAsync(id, ct).ConfigureAwait(false);
        if (runtime is { Runtime: RuntimeKind.LlamaCpp or RuntimeKind.Mlx, Delivery: LaunchDelivery.Process } or { Runtime: RuntimeKind.Sglang or RuntimeKind.Nim })
        {
            // No legacy hash for llama.cpp (design-p3p4 §A.1.6), MLX, SGLang or NIM: the launcher hash is the execution fingerprint (D125).
            return runtime.ExecutionFingerprint;
        }

        return runtime is null ? null : await LaunchedHashAsync(ToNodeRuntime(nodeId, runtime), expectedHash: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> ComputeConfigHashAsync(ModelDefinition definition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var node = await RequireNodeAsync(definition, ct).ConfigureAwait(false);
        if (definition.Runtime == RuntimeKind.LlamaCpp)
        {
            // The execution fingerprint of the spec it would launch (D125): it covers the argv and the executable version,
            // not the tuning label, so a tuning candidate and its applied definition compare equal.
            return LaunchSpecBuilder.BuildLlamaCpp(definition, node, tuning: false).ExecutionFingerprint;
        }

        if (definition.Runtime == RuntimeKind.Mlx)
        {
            return LaunchSpecBuilder.BuildMlx(definition, node, tuning: false).ExecutionFingerprint;
        }

        if (definition.Runtime is RuntimeKind.Sglang or RuntimeKind.Nim)
        {
            return LaunchSpecBuilder.BuildAdditionalContainer(definition, node, tuning: false).ExecutionFingerprint;
        }

        double totalGiB = await GetTotalGiBAsync(node, ct).ConfigureAwait(false);
        return LaunchSpecBuilder.ConfigHash(definition, node, LaunchSpecBuilder.DefaultImage(node, _options), totalGiB);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListTuningContainersAsync(CancellationToken ct = default)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var live in _cache.ListNodes())
        {
            IEnumerable<string> found;
            try
            {
                found = live.IsServing
                    ? (await _clients.GetClient(live.NodeId).ListRuntimesAsync(new RuntimesQuery { Label = RuntimesQuery.TuningLabel }, ct).ConfigureAwait(false))
                        .Where(r => r.Tuning).Select(r => r.ModelId)
                    : CachedTuning(live.NodeId);
            }
            catch (Exception ex) when (ex is AgentUnreachableException or AgentException or KeyNotFoundException)
            {
                found = CachedTuning(live.NodeId);
            }

            ids.UnionWith(found);
        }

        return [.. ids];

        IEnumerable<string> CachedTuning(string nodeId) => _cache.ListRuntimes(nodeId).Where(r => r.Tuning).Select(r => r.ModelId);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamLogsAsync(
        string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var saved = await _registry.GetAsync(id, ct).ConfigureAwait(false);
        string? nodeId = NodeOf(id, saved);
        IAgentClient? client = null;
        try
        {
            client = nodeId is null ? null : _clients.GetClient(nodeId);
        }
        catch (KeyNotFoundException)
        {
            client = null;
        }

        if (client is null)
        {
            yield break;
        }

        var lines = client.StreamLogsAsync(id, new LogsQuery { Tail = tail, Follow = follow }, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await lines.MoveNextAsync().ConfigureAwait(false);
                }
                catch (AgentException ex) when (ex.Code == ErrorCodes.NotFound)
                {
                    more = false; // no instance: today's "no container" → no lines
                }

                if (!more)
                {
                    break;
                }

                yield return lines.Current;
            }
        }
        finally
        {
            await lines.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- launch internals

    private async Task<ModelStatus> LaunchLockedAsync(ModelDefinition model, LaunchMode mode, bool force, bool tuning, CancellationToken ct)
    {
        var gate = ModelLock(model.Id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        ModelStatus status;
        try
        {
            var node = await RequireNodeAsync(model, ct).ConfigureAwait(false);
            RequireServing(node.Id, model.Id, forStart: true);
            var live = _cache.GetNode(node.Id);
            status = await LaunchOnNodeAsync(model, node, mode, force, tuning, handleSelfConflict: true, ct).ConfigureAwait(false);
            if (!tuning && live is not null)
            {
                _recovery?.Observe(live, model.Id, status.State);
            }

            if (!tuning && AuditScope.Current?.Actor != AuditActors.SystemAutoStart)
            {
                _recovery?.ResetCrashes(model.Id); // A start by hand (UI, API, restore) clears the crash counter and any pause.
            }
        }
        finally
        {
            gate.Release();
        }

        await RefreshCacheAsync(model.Id).ConfigureAwait(false);
        return status;
    }

    private async Task<ModelStatus> LaunchOnNodeAsync(
        ModelDefinition model, NodeRecord node, LaunchMode mode, bool force, bool tuning, bool handleSelfConflict, CancellationToken ct)
    {
        string id = model.Id;
        double totalGiB = await GetTotalGiBAsync(node, ct).ConfigureAwait(false);
        var credentials = await ContainerCredentials.ResolveAsync(_tokens, model, ct).ConfigureAwait(false);
        var built = LaunchSpecBuilder.Build(model, node, LaunchSpecBuilder.DefaultImage(node, _options), totalGiB, tuning, credentials.Environment);

        // The host budget a llama.cpp launch sends must be the figure the fit preview showed, which for a partial
        // offload is 1 GiB plus the layers left in host RAM (MN-239-17). Only then, and only when the model carries no
        // explicit HostMemoryBudgetGiB: a vLLM start on the DGX makes no extra call and its launch body is unchanged.
        // PlacementFit's own short-TTL cache usually means the preview's read serves this one.
        var estimate = _fit is not null && model.Runtime == RuntimeKind.LlamaCpp && model.HostMemoryBudgetGiB is null
            ? await _fit.EstimateAsync(node, model, ct).ConfigureAwait(false)
            : null;
        var envelope = _runner.CreateEnvelope();
        var request = new LaunchRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            Mode = mode,
            Spec = built.Spec,
            Admission = LaunchSpecBuilder.Admission(model, node, force, nodeFeatures: null, estimate),
            ExpectedConfigHash = built.LegacyConfigHash,
            ExpectedExecutionFingerprint = built.ExecutionFingerprint,
            Registry = credentials.Registry,
        };

        OperationStatus accepted;
        try
        {
            accepted = await _runner.SubmitAsync(node.Id, AgentRoutes.RuntimeLaunch, id, request,
                (client, r, token) => client.LaunchAsync(id, r, token), owner: null, ct).ConfigureAwait(false);
        }
        catch (AgentException ex) when (handleSelfConflict && IsSelfConflict(ex, id))
        {
            // Its own earlier launch still holds the start reservation (admitted or pulling): W2 review MN-092.
            return await ContinueOwnLaunchAsync(model, node, mode, force, tuning, ct).ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            throw AgentErrorMapper.ToMasterException(ex);
        }

        return await WaitForCreatedAsync(node.Id, id, accepted, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A <c>start_conflict</c> about the model itself: <c>ensureStarted</c> waits for the earlier launch like a normal
    /// start; <c>replace</c> supersedes it and launches again (once).
    /// </summary>
    private async Task<ModelStatus> ContinueOwnLaunchAsync(ModelDefinition model, NodeRecord node, LaunchMode mode, bool force, bool tuning, CancellationToken ct)
    {
        var ownLaunch = await FindActiveLaunchAsync(node.Id, model.Id, ct).ConfigureAwait(false);
        if (ownLaunch is null)
        {
            // It ended between the refusal and the lookup: launch again, once.
            return await LaunchOnNodeAsync(model, node, mode, force, tuning, handleSelfConflict: false, ct).ConfigureAwait(false);
        }

        RemoteModelRuntimeLog.SelfConflict(_logger, model.Id, node.Id, ownLaunch.Id, mode);
        if (mode == LaunchMode.EnsureStarted)
        {
            return await WaitForCreatedAsync(node.Id, model.Id, ownLaunch, ct).ConfigureAwait(false);
        }

        await SupersedeAsync(node.Id, model.Id, ownLaunch, ct).ConfigureAwait(false);
        return await LaunchOnNodeAsync(model, node, mode, force, tuning, handleSelfConflict: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the launch has created its instance (stage <c>created</c>, <c>started</c> or <c>ready</c>) or ended,
    /// then answers like today's <c>StartAsync</c>: the instance status (Starting), or the mapped refusal when the launch
    /// failed before anything existed.
    /// </summary>
    private async Task<ModelStatus> WaitForCreatedAsync(string nodeId, string modelId, OperationStatus operation, CancellationToken ct)
    {
        var current = operation;
        string? lastStage = current.Stage;
        DateTimeOffset deadline = _time.GetUtcNow() + _runtimeOptions.CreateTimeout;
        Exception? lastFailure = null;
        while (true)
        {
            if (current.State.IsTerminal())
            {
                await _runner.CloseFromStatusAsync(current, CancellationToken.None).ConfigureAwait(false);
                return await FinishedLaunchAsync(nodeId, modelId, current, ct).ConfigureAwait(false);
            }

            if (InstanceExists(current.Stage))
            {
                return await CreatedStatusAsync(nodeId, modelId, ct).ConfigureAwait(false);
            }

            DateTimeOffset now = _time.GetUtcNow();
            if (current.Stage == OperationStages.Pulling || !string.Equals(current.Stage, lastStage, StringComparison.Ordinal))
            {
                // A new stage restarts the bound; a running pull never runs out of it (design §4.5).
                lastStage = current.Stage;
                deadline = now + _runtimeOptions.CreateTimeout;
            }

            if (now >= deadline)
            {
                if (lastFailure is not null)
                {
                    throw lastFailure;
                }

                RemoteModelRuntimeLog.CreateTimedOut(_logger, modelId, nodeId, current.Id, current.Stage ?? "accepted", _runtimeOptions.CreateTimeout.TotalSeconds);
                return new ModelStatus(modelId, ModelState.Starting);
            }

            await Task.Delay(_runtimeOptions.PollInterval, _time, ct).ConfigureAwait(false);
            try
            {
                current = await _clients.GetClient(nodeId).GetOperationAsync(operation.Id, ct).ConfigureAwait(false) ?? current;
                lastFailure = null;
            }
            catch (AgentUnreachableException ex)
            {
                lastFailure = ex; // the launch keeps running on the node; keep asking until the bound
            }
            catch (AgentException ex)
            {
                throw AgentErrorMapper.ToMasterException(ex); // never a raw agent exception to callers (MN-047)
            }
        }
    }

    /// <summary>The answer for a launch that ended while the runtime waited for its instance.</summary>
    private async Task<ModelStatus> FinishedLaunchAsync(string nodeId, string modelId, OperationStatus operation, CancellationToken ct)
    {
        switch (operation.State)
        {
            case OperationState.Succeeded:
                return await CreatedStatusAsync(nodeId, modelId, ct).ConfigureAwait(false);

            case OperationState.Failed when await CreatedByAsync(nodeId, modelId, operation.Id, ct).ConfigureAwait(false) is { } instance:
                // The instance was created and then failed (e.g. vLLM died while loading): today StartAsync returned
                // Starting and the next status read reported Failed; report the instance as it is now.
                return new ModelStatus(modelId, instance.State, instance.InstanceId, instance.HostPort, instance.Since, instance.Error);

            case OperationState.Failed when operation.Error?.Code is ErrorCodes.Superseded or ErrorCodes.OperationExpired:
                // Ended Failed by another action (a newer master's command, D52) or by the reconciler after its id expired
                // (D56) before the container existed: like a cancelled start, nothing runs (409, D64 completed by MN-047).
                throw Interrupted(nodeId, modelId, operation.Error.Code, operation.Error.Message);

            case OperationState.Failed:
                throw ToMasterException(operation.Error ?? new AgentError
                {
                    Code = ErrorCodes.InternalError,
                    Message = $"The launch of model '{modelId}' failed.",
                }, nodeId);

            default:
                // Cancelled/superseded/expired before create: another action won and nothing runs (409 at the API, W4 review).
                throw Interrupted(nodeId, modelId, operation.State.ToString().ToLowerInvariant(), detail: null);
        }
    }

    private static ModelStartInterruptedException Interrupted(string nodeId, string modelId, string how, string? detail)
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"The start of model '{modelId}' ended {how} before its container was created.");
        if (!string.IsNullOrWhiteSpace(detail))
        {
            text += " " + detail.Trim();
        }

        return new ModelStartInterruptedException(AgentErrorMapper.WithNode(text, nodeId))
        {
            ModelId = modelId,
            NodeId = nodeId,
        };
    }

    /// <summary>
    /// The instance <paramref name="operationId"/> created, when it still exists (a terminal operation's stage is
    /// <c>completed</c>, so the instance's operation label tells whether the launch got that far). Null when the node
    /// cannot tell (unreachable, or an agent error such as <c>docker_unavailable</c>, MN-044 seed 4402): the caller then
    /// reports the launch's own failure, mapped.
    /// </summary>
    private async Task<RuntimeStatus?> CreatedByAsync(string nodeId, string modelId, string operationId, CancellationToken ct)
    {
        try
        {
            var runtime = await _clients.GetClient(nodeId).GetRuntimeAsync(modelId, ct).ConfigureAwait(false);
            return runtime is not null && string.Equals(runtime.OperationId, operationId, StringComparison.Ordinal) ? runtime : null;
        }
        catch (Exception ex) when (ex is AgentUnreachableException or AgentException)
        {
            return null;
        }
    }

    /// <summary>The instance a launch created, as a <see cref="ModelStatus"/> (restart-pending is false for a fresh start, as today).</summary>
    private async Task<ModelStatus> CreatedStatusAsync(string nodeId, string modelId, CancellationToken ct)
    {
        RuntimeStatus? runtime;
        try
        {
            runtime = await _clients.GetClient(nodeId).GetRuntimeAsync(modelId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentUnreachableException or AgentException)
        {
            // Created, but the node cannot show it right now (unreachable, Docker down): today's answer for a fresh start.
            return new ModelStatus(modelId, ModelState.Starting);
        }

        return runtime is null
            ? new ModelStatus(modelId, ModelState.Stopped)
            : new ModelStatus(modelId, runtime.State, runtime.InstanceId, runtime.HostPort, runtime.Since, runtime.Error);
    }

    /// <summary>
    /// <c>replace</c> while the model's own earlier launch still holds the start reservation: cancel that launch (no
    /// instance yet), or stop the model once its instance exists, and wait until the launch has ended.
    /// </summary>
    private async Task SupersedeAsync(string nodeId, string modelId, OperationStatus ownLaunch, CancellationToken ct)
    {
        if (!InstanceExists(ownLaunch.Stage))
        {
            var envelope = _runner.CreateEnvelope();
            var cancel = new OperationCancelRequest { OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt };
            try
            {
                await _runner.SubmitAsync(nodeId, AgentRoutes.OperationCancel, ownLaunch.Id, cancel,
                    (client, r, token) => client.CancelOperationAsync(ownLaunch.Id, r, token), owner: null, ct).ConfigureAwait(false);
            }
            catch (AgentException ex) when (ex.Code == ErrorCodes.InvalidState)
            {
                // The instance was created meanwhile: a stop ends the launch instead.
                await StopOnNodeAsync(nodeId, modelId, ct).ConfigureAwait(false);
            }
            catch (AgentException ex)
            {
                throw AgentErrorMapper.ToMasterException(ex);
            }
        }
        else
        {
            await StopOnNodeAsync(nodeId, modelId, ct).ConfigureAwait(false);
        }

        var ended = await WaitForEndAsync(nodeId, ownLaunch.Id, ct).ConfigureAwait(false);
        if (ended is { } status && status.State.IsTerminal())
        {
            await _runner.CloseFromStatusAsync(status, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Stops and removes the model's instance on a node and waits for the stop to finish (today's synchronous stop).</summary>
    private async Task StopOnNodeAsync(string nodeId, string modelId, CancellationToken ct)
    {
        var envelope = _runner.CreateEnvelope();
        var request = new StopRequest { OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt };
        OperationStatus accepted;
        try
        {
            accepted = await _runner.SubmitAsync(nodeId, AgentRoutes.RuntimeStop, modelId, request,
                (client, r, token) => client.StopAsync(modelId, r, token), owner: null, ct).ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            throw AgentErrorMapper.ToMasterException(ex);
        }

        var ended = accepted.State.IsTerminal() ? accepted : await WaitForEndAsync(nodeId, accepted.Id, ct).ConfigureAwait(false);
        if (ended is not { } final || !final.State.IsTerminal())
        {
            throw new TimeoutException(AgentErrorMapper.WithNode(
                string.Create(CultureInfo.InvariantCulture, $"Model '{modelId}' did not stop within {_runtimeOptions.StopTimeout.TotalSeconds:0} s; the stop continues."),
                nodeId));
        }

        await _runner.CloseFromStatusAsync(final, CancellationToken.None).ConfigureAwait(false);
        if (final.State != OperationState.Succeeded)
        {
            throw ToMasterException(final.Error ?? new AgentError
            {
                Code = ErrorCodes.InternalError,
                Message = $"The stop of model '{modelId}' ended {final.State.ToString().ToLowerInvariant()}.",
            }, nodeId);
        }
    }

    /// <summary>Polls an operation until it ends or <see cref="RemoteModelRuntimeOptions.StopTimeout"/> passes; the last status seen (null when never read).</summary>
    private async Task<OperationStatus?> WaitForEndAsync(string nodeId, string operationId, CancellationToken ct)
    {
        DateTimeOffset deadline = _time.GetUtcNow() + _runtimeOptions.StopTimeout;
        OperationStatus? last = null;
        while (true)
        {
            try
            {
                last = await _clients.GetClient(nodeId).GetOperationAsync(operationId, ct).ConfigureAwait(false) ?? last;
            }
            catch (AgentUnreachableException) when (_time.GetUtcNow() < deadline)
            {
                // keep asking until the bound
            }
            catch (AgentException ex)
            {
                throw AgentErrorMapper.ToMasterException(ex); // never a raw agent exception to callers (MN-047)
            }

            if (last is not null && last.State.IsTerminal())
            {
                return last;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return last;
            }

            await Task.Delay(_runtimeOptions.PollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The model's own non-terminal launch on the node (oldest first), or null.</summary>
    private async Task<OperationStatus?> FindActiveLaunchAsync(string nodeId, string modelId, CancellationToken ct)
    {
        IReadOnlyList<OperationStatus> active;
        try
        {
            active = await _clients.GetClient(nodeId).ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }, ct).ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            throw AgentErrorMapper.ToMasterException(ex);
        }

        return active
            .Where(o => o.Kind == OperationKinds.Launch && string.Equals(o.Target, modelId, StringComparison.Ordinal) && !o.State.IsTerminal())
            .OrderBy(o => o.StartedAt)
            .FirstOrDefault();
    }

    private static bool IsSelfConflict(AgentException exception, string modelId)
    {
        if (exception.Code != ErrorCodes.StartConflict)
        {
            return false;
        }

        try
        {
            return string.Equals(exception.Error.ReadDetails<StartConflictDetails>()?.StartingModelId, modelId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool InstanceExists(string? stage) =>
        stage is OperationStages.Created or OperationStages.Started or OperationStages.Ready;

    private static Exception ToMasterException(AgentError error, string nodeId) =>
        AgentErrorMapper.ToMasterException(new AgentException(ErrorMappings.AgentStatus(error.Code), error, nodeId));

    // ---------------------------------------------------------------- nodes

    /// <summary>The node a model's instance lives on: its placement, else where the cache last saw an instance.</summary>
    private string? NodeOf(string id, ModelDefinition? saved) => saved?.Placement?.NodeId ?? _cache.GetRuntime(id)?.NodeId;

    /// <summary>The registered, non-revoked node a definition is placed on.</summary>
    private async Task<NodeRecord> RequireNodeAsync(ModelDefinition model, CancellationToken ct)
    {
        string nodeId = model.Placement?.NodeId
            ?? throw new NodeUnavailableException($"Model '{model.Id}' is not placed on a node.") { Reason = NodeUnavailableReason.Unplaced };
        var node = await _nodes.GetAsync(nodeId, ct).ConfigureAwait(false)
            ?? throw new NodeUnavailableException($"Model '{model.Id}' is placed on node '{nodeId}', which is not registered.")
            {
                Reason = NodeUnavailableReason.NodeNotRegistered,
                NodeId = nodeId,
            };
        return node.Revoked
            ? throw new NodeUnavailableException($"Model '{model.Id}' is placed on node '{nodeId}', which is revoked.")
            {
                Reason = NodeUnavailableReason.NodeNotRegistered,
                NodeId = nodeId,
            }
            : node;
    }

    /// <summary>
    /// Commands go only to a node the cache reports Online or Degraded (design §3.5: Offline, Incompatible and Pending
    /// takeover block commands); a draining node takes no new starts.
    /// </summary>
    private void RequireServing(string nodeId, string modelId, bool forStart)
    {
        var live = _cache.GetNode(nodeId);
        if (live is null || !live.IsServing)
        {
            string state = live is null ? "not connected" : live.State.ToString().ToLowerInvariant();
            string reasons = live is { Reasons.Count: > 0 } ? " (" + string.Join("; ", live.Reasons) + ")" : string.Empty;
            throw new NodeUnavailableException($"Node '{nodeId}' is {state}{reasons}: the command for model '{modelId}' was not sent.")
            {
                Reason = NodeUnavailableReason.NotServing,
                NodeId = nodeId,
            };
        }

        if (forStart && live.Draining)
        {
            throw new NodeUnavailableException($"Node '{nodeId}' is draining: model '{modelId}' cannot be started there.")
            {
                Reason = NodeUnavailableReason.Draining,
                NodeId = nodeId,
            };
        }
    }

    /// <summary>
    /// The node's total unified memory (the <c>--gpu-memory-utilization</c> denominator, today's MemTotal): the cache's
    /// last memory snapshot, else the capability report, else a fresh <c>/status</c>. Cached per node uid once known.
    /// </summary>
    private async Task<double> GetTotalGiBAsync(NodeRecord node, CancellationToken ct)
    {
        if (_totals.TryGetValue(node.Uid, out double cached))
        {
            return cached;
        }

        var live = _cache.GetNode(node.Id);
        double total = live?.Memory?.HostTotalGiB ?? 0;
        if (total <= 0)
        {
            total = live?.Capabilities?.Memory.TotalGiB ?? node.LastCapabilities?.Memory.TotalGiB ?? 0;
        }

        if (total <= 0)
        {
            var snapshot = await _clients.GetClient(node.Id).GetStatusAsync(ct).ConfigureAwait(false);
            total = snapshot.Memory.HostTotalGiB;
        }

        if (total > 0)
        {
            _totals[node.Uid] = total;
        }

        return total;
    }

    /// <summary>The model's instance on a node: from the agent when it can answer, else the cache's last view.</summary>
    private async Task<NodeModelRuntime?> ReadRuntimeAsync(string nodeId, string modelId, CancellationToken ct)
    {
        if (_cache.GetNode(nodeId) is { IsServing: true })
        {
            try
            {
                var runtime = await _clients.GetClient(nodeId).GetRuntimeAsync(modelId, ct).ConfigureAwait(false);
                return runtime is null ? null : ToNodeRuntime(nodeId, runtime);
            }
            catch (Exception ex) when (ex is AgentUnreachableException or AgentException or KeyNotFoundException)
            {
                RemoteModelRuntimeLog.StatusFromCache(_logger, modelId, nodeId, ex.Message);
            }
        }

        return _cache.GetRuntime(modelId) is { } cached && string.Equals(cached.NodeId, nodeId, StringComparison.Ordinal) ? cached : null;
    }

    private async Task RefreshCacheAsync(string modelId)
    {
        try
        {
            await _cache.RefreshModelAsync(modelId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best effort: the node monitor's events bring the change anyway.
            RemoteModelRuntimeLog.CacheRefreshFailed(_logger, modelId, ex.Message);
        }
    }

    private SemaphoreSlim ModelLock(string id) => _modelLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    // ---------------------------------------------------------------- mapping and restart-pending

    private async Task<ModelStatus> MapAsync(string id, ModelDefinition? saved, NodeModelRuntime? runtime, CancellationToken ct)
    {
        if (runtime is null)
        {
            return new ModelStatus(id, ModelState.Stopped);
        }

        bool pending = await IsRestartPendingAsync(saved, runtime, ct).ConfigureAwait(false);
        return new ModelStatus(id, runtime.State, runtime.InstanceId, runtime.HostPort, runtime.Since, runtime.Error, pending);
    }

    /// <summary>
    /// Design §5.1: pending = (legacy hash differs, with today's <c>Cmd</c> recheck) or (both sides carry an execution
    /// fingerprint and it differs), only for a Starting/Ready instance that is not a tuning candidate
    /// (<see cref="RestartPendingRule"/>). Unknown (false) when the expected side cannot be computed.
    /// </summary>
    private async Task<bool> IsRestartPendingAsync(ModelDefinition? saved, NodeModelRuntime runtime, CancellationToken ct)
    {
        if (saved is null || runtime.State is not (ModelState.Starting or ModelState.Ready) || runtime.Tuning)
        {
            return false;
        }

        BuiltLaunchSpec expected;
        try
        {
            if (await _nodes.GetAsync(runtime.NodeId, ct).ConfigureAwait(false) is not { } node)
            {
                return false;
            }

            double totalGiB = await GetTotalGiBAsync(node, ct).ConfigureAwait(false);
            expected = LaunchSpecBuilder.Build(saved, node, LaunchSpecBuilder.DefaultImage(node, _options), totalGiB, tuning: false);
        }
        catch (Exception ex) when (ex is AgentUnreachableException or AgentException or KeyNotFoundException or NotSupportedException or ArgumentException)
        {
            return false;
        }

        if (expected.LegacyConfigHash is null)
        {
            // llama.cpp (MN-219, D125): no legacy hash on either side; the execution fingerprint alone decides.
            return RestartPendingRule.IsPending(new HashPair(null, null), new HashPair(expected.ExecutionFingerprint, runtime.ExecutionFingerprint));
        }

        string? actual = await LaunchedHashAsync(runtime, expected.ConfigHash, ct).ConfigureAwait(false);
        return RestartPendingRule.IsPending(
            runtime.State,
            runtime.Tuning,
            new HashPair(expected.ConfigHash, actual),
            new HashPair(expected.ExecutionFingerprint, runtime.ExecutionFingerprint));
    }

    /// <summary>
    /// Today's <c>LaunchedHashAsync</c> over <c>/launch-config</c>: null without a hash label (unknown); the label when it
    /// equals <paramref name="expectedHash"/>; otherwise the canonical hash recomputed from the instance's command line
    /// (cached per instance), falling back to the label when it cannot be read.
    /// </summary>
    private async Task<string?> LaunchedHashAsync(NodeModelRuntime runtime, string? expectedHash, CancellationToken ct)
    {
        if (runtime.ConfigHash is not { } label)
        {
            return null;
        }

        if (expectedHash is not null && string.Equals(label, expectedHash, StringComparison.Ordinal))
        {
            return label;
        }

        string? key = runtime.InstanceId is { } instance ? runtime.NodeId + "/" + instance : null;
        if (key is not null && _canonicalHashes.TryGetValue(key, out string? cachedHash))
        {
            return cachedHash;
        }

        try
        {
            var launch = await _clients.GetClient(runtime.NodeId).GetLaunchConfigAsync(runtime.ModelId, ct).ConfigureAwait(false);
            if (launch is null || launch.Argv.Count == 0)
            {
                return label;
            }

            string hash = ModelConfigHash.Compute(launch.Image ?? string.Empty, launch.Argv);
            bool sameInstance = launch.Labels.TryGetValue(ContainerLabels.ConfigHash, out string? launchLabel) &&
                string.Equals(launchLabel, label, StringComparison.Ordinal);
            if (key is not null && sameInstance)
            {
                _canonicalHashes[key] = hash;
            }

            return hash;
        }
        catch (Exception ex) when (ex is AgentUnreachableException or AgentException or KeyNotFoundException)
        {
            return label;
        }
    }

    private static bool IsLlamaCppProcess(LaunchConfig launch) =>
        launch.Kind == LaunchDelivery.Process && string.Equals(launch.Executable, ExecutableNames.LlamaServer, StringComparison.Ordinal);

    private static bool IsMlxProcess(LaunchConfig launch) =>
        launch.Kind == LaunchDelivery.Process && string.Equals(launch.Executable, ExecutableNames.MlxLmServer, StringComparison.Ordinal);

    private NodeModelRuntime ToNodeRuntime(string nodeId, RuntimeStatus runtime) => new()
    {
        ModelId = runtime.ModelId,
        NodeId = nodeId,
        State = runtime.State,
        ServedName = runtime.ServedName,
        InstanceId = runtime.InstanceId,
        HostPort = runtime.HostPort,
        Since = runtime.Since,
        Error = runtime.Error,
        RestartCount = runtime.RestartCount,
        ConfigHash = runtime.ConfigHash,
        ExecutionFingerprint = runtime.ExecutionFingerprint,
        Tuning = runtime.Tuning,
        OperationId = runtime.OperationId,
        MeasuredGpuGiB = runtime.MeasuredGpuGiB,
        GpuUuids = [.. runtime.Gpus.Select(g => g.Uuid)],
        Labels = runtime.Labels,
        UpdatedAt = _time.GetUtcNow(),
    };
}

/// <summary>Source-generated log messages of <see cref="RemoteModelRuntime"/>.</summary>
internal static partial class RemoteModelRuntimeLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Model {ModelId} on node {NodeId}: its own launch {OperationId} still holds the start reservation; {Mode} continues from it.")]
    public static partial void SelfConflict(ILogger logger, string modelId, string nodeId, string operationId, LaunchMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model {ModelId} on node {NodeId}: launch {OperationId} has not created its container after {Seconds} s (stage {Stage}); reporting Starting, the launch continues on the node.")]
    public static partial void CreateTimedOut(ILogger logger, string modelId, string nodeId, string operationId, string stage, double seconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Status of model {ModelId} read from the cache: node {NodeId} did not answer ({Reason}).")]
    public static partial void StatusFromCache(ILogger logger, string modelId, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not refresh the status cache for model {ModelId}: {Reason}")]
    public static partial void CacheRefreshFailed(ILogger logger, string modelId, string reason);
}
