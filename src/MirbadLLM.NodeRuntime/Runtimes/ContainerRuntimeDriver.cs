using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes;

/// <summary>
/// <see cref="IRuntimeDriver"/> over the Docker Engine API (design §2.4, §4.3, §4.9, §10.3), ported from the master's
/// pre-P1 <c>DockerModelRuntime</c> without its vLLM hash/parser logic: the master builds the full <see cref="LaunchSpec"/>
/// (argv, labels incl. config hash) and this driver creates exactly the container <c>DockerModelRuntime</c> created
/// (name <c>ss-{id}</c>, same image, <c>Cmd</c>, binds, port binding on 127.0.0.1, network alias, <c>ipc: host</c>, all
/// GPUs, restart policy), plus the agent-set labels <c>mirbadllm.node</c> and <c>mirbadllm.operation</c>. From P2
/// (MN-107) a spec naming an approved digest is created from <c>repository@digest</c>, and launch environment variables are
/// passed to Docker with only their names recorded in labels (secret values never leave the create call). From P2
/// (MN-106) a spec with <c>gpus.mode = uuids</c> gets exactly those GPUs (<c>DeviceIDs</c>), a UUID that is not in the
/// node's GPU inventory is refused with 422 <c>gpu_missing</c> before anything is reserved, and the instance carries the
/// read-back labels <see cref="ContainerLabels.GpuUuids"/> and <see cref="ContainerLabels.GpuTotalGiB"/>; its
/// <c>launch-config</c> and status name its GPUs with their current indices. A spec with <c>gpus.mode = all</c> creates
/// exactly the P1 container.
/// <para>
/// State is always derived from Docker (containers listed by label), never cached as truth, so the agent can restart at
/// any time and adopt what runs, including containers created before P1 (no node label).
/// </para>
/// </summary>
public sealed class ContainerRuntimeDriver : IRuntimeDriver
{
    private readonly DockerEngineClient _docker;
    private readonly HttpClient _upstreamHttp;
    private readonly IPortAllocator _ports;
    private readonly ContainerRuntimeOptions _options;
    private readonly ILogger<ContainerRuntimeDriver> _logger;
    private readonly TimeProvider _time;
    private readonly IGpuInventory? _gpus;

    /// <summary>Ids currently being stopped; consulted before the Docker state so a stop in progress reports Stopping.</summary>
    private readonly ConcurrentDictionary<string, byte> _stopping = new(StringComparer.Ordinal);

    /// <summary>Serializes launch/stop/start of one model inside this process.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelLocks = new(StringComparer.Ordinal);

    /// <summary>Creates the driver.</summary>
    /// <param name="docker">Docker Engine client.</param>
    /// <param name="upstreamHttp">Client for the <c>/health</c> probe.</param>
    /// <param name="ports">Port allocator (the reservation manager, MN-016).</param>
    /// <param name="options">Node-local settings.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="timeProvider">Clock (system clock when null).</param>
    /// <param name="gpuInventory">
    /// The node's GPUs (MN-104): checks that selected UUIDs are on the node and maps them to current indices. Null = GPU
    /// selection by UUID is refused (<c>spec_rejected</c>), as on a P1 agent.
    /// </param>
    public ContainerRuntimeDriver(
        DockerEngineClient docker,
        HttpClient upstreamHttp,
        IPortAllocator ports,
        IOptions<ContainerRuntimeOptions> options,
        ILogger<ContainerRuntimeDriver> logger,
        TimeProvider? timeProvider = null,
        IGpuInventory? gpuInventory = null)
    {
        ArgumentNullException.ThrowIfNull(docker);
        ArgumentNullException.ThrowIfNull(upstreamHttp);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _docker = docker;
        _upstreamHttp = upstreamHttp;
        _ports = ports;
        _options = options.Value;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _gpus = gpuInventory;
    }

    /// <inheritdoc />
    public LaunchDelivery Delivery => LaunchDelivery.Container;

    /// <summary>
    /// True: this driver accepts <c>spec.env</c>/<c>spec.secretEnvNames</c> and keeps secret values out of
    /// <c>launch-config</c> and its labels and logs (MN-107, feature <see cref="AgentFeatures.LaunchEnv"/>).
    /// </summary>
    public static bool AcceptsEnvironment => true;

    /// <summary>
    /// True when every launch must name an approved digest (<see cref="ContainerRuntimeOptions.RequireApprovedDigest"/>,
    /// i.e. <c>AllowTagPulls</c> off): the node then reports <see cref="AgentFeatures.ImagesDigestPinned"/>. False keeps
    /// P1's tag fallback (the DGX in P1).
    /// </summary>
    public bool RequiresApprovedDigest => _options.RequireApprovedDigest;

    /// <summary>
    /// True when this driver selects GPUs by UUID (it has the node's GPU inventory): the node then reports
    /// <see cref="AgentFeatures.GpuSelectionUuids"/> (MN-106, D73).
    /// </summary>
    public bool SelectsGpusByUuid => _gpus is not null;

    /// <summary>The node's GPU inventory this driver checks selections against; null when it selects all GPUs only.</summary>
    public IGpuInventory? GpuInventory => _gpus;

    /// <summary>
    /// Checks a spec against the node's guardrails without touching Docker (422 <c>spec_rejected</c>); the launch handler
    /// calls it before answering 202.
    /// </summary>
    public ContainerLaunchPlan Validate(string modelId, LaunchSpec spec) => ContainerLaunchValidator.Validate(modelId, spec, _options);

    /// <summary>
    /// <see cref="Validate"/>, then (design §5.1, MN-106) for <c>gpus.mode = uuids</c> the GPUs against the inventory's
    /// last read (<see cref="IGpuInventory.Missing"/>; the launch handler refreshes it first, so a renumbered or removed
    /// card is seen): 422 <c>gpu_missing</c> listing the missing UUIDs. Without an inventory, 422 <c>spec_rejected</c>.
    /// </summary>
    public void CheckSpec(string modelId, LaunchSpec spec)
    {
        var plan = Validate(modelId, spec);
        if (plan.GpuUuids is { } uuids)
        {
            CheckGpusPresent(uuids);
        }
    }

    /// <summary>
    /// Refuses with 422 <c>gpu_missing</c> when any of <paramref name="uuids"/> is not in the inventory's last read, or
    /// with <c>spec_rejected</c> when this driver has no inventory.
    /// </summary>
    private void CheckGpusPresent(IReadOnlyList<string> uuids)
    {
        if (_gpus is null)
        {
            const string Reason = "GPU selection by UUID is not available on this node (all GPUs only)";
            throw new AgentException(422, new AgentError
            {
                Code = ErrorCodes.SpecRejected,
                Message = $"Launch spec rejected: {Reason}.",
                Details = AgentJson.ToElement(new SpecRejectedDetails(Reason)),
            });
        }

        var missing = _gpus.Missing(uuids);
        if (missing.Count > 0)
        {
            throw new AgentException(422, new AgentError
            {
                Code = ErrorCodes.GpuMissing,
                Message = $"GPU(s) {string.Join(", ", missing)} are not on node '{_options.NodeId}'; select the model's GPUs again.",
                Details = AgentJson.ToElement(new GpuMissingDetails(missing)),
            });
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default)
    {
        var containers = await _docker.ListContainersAsync([ContainerLabels.Model], all: true, ct).ConfigureAwait(false);
        var statuses = new List<RuntimeStatus>();
        foreach (var group in containers.Where(IsOwned).GroupBy(c => c.Labels[ContainerLabels.Model], StringComparer.Ordinal))
        {
            statuses.Add(await MapAsync(group.Key, group.First(), ct).ConfigureAwait(false));
        }

        return statuses;
    }

    /// <inheritdoc />
    public async Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var container = await FindContainerAsync(modelId, ct).ConfigureAwait(false);
        if (container is null)
        {
            return _stopping.ContainsKey(modelId) ? BuildStatus(modelId, ModelState.Stopping, instanceId: null, hostPort: null, since: null) : null;
        }

        return await MapAsync(modelId, container, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentException.ThrowIfNullOrEmpty(launch.OperationId);
        var plan = Validate(launch.ModelId, launch.Spec);
        if (plan.GpuUuids is { } uuids && (_gpus is null || _gpus.Last is not null))
        {
            // The launch handler checked against a fresh read (CheckSpec); before any read (an operation resumed right
            // after an agent restart) Docker itself refuses a device that is not there.
            CheckGpusPresent(uuids);
        }

        EnsureNodeId();

        var gate = _modelLocks.GetOrAdd(launch.ModelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await FindContainerAsync(launch.ModelId, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                if (launch.Mode == LaunchMode.EnsureStarted)
                {
                    var current = await MapAsync(launch.ModelId, existing, ct).ConfigureAwait(false);
                    if (current.State is ModelState.Ready or ModelState.Starting or ModelState.Stopping)
                    {
                        // Today's StartAsync: a running instance is returned as it is, whatever its configuration.
                        return new RuntimeLaunchResult(RuntimeLaunchOutcome.AlreadyRunning, current);
                    }

                    // Failed (exited/dead/crash-looping) container left behind by a previous run: clear it before recreating.
                    await stages.BeforeStepAsync(OperationStages.Removed, ct).ConfigureAwait(false);
                    await RemoveAsync(existing.Id, ct).ConfigureAwait(false);
                    await stages.StageReachedAsync(OperationStages.Removed, null, ct).ConfigureAwait(false);
                }
                else
                {
                    // Today's LaunchAsync/RestartAsync: always stop, remove and recreate.
                    bool stop = existing.State is "running" or "restarting" or "paused";
                    await StopAndRemoveAsync(launch.ModelId, existing, stop, stages, ct).ConfigureAwait(false);
                }
            }

            return await CreateAndStartAsync(launch, plan, stages, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var gate = _modelLocks.GetOrAdd(modelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var container = await FindContainerAsync(modelId, ct).ConfigureAwait(false)
                ?? throw new AgentException(404, new AgentError
                {
                    Code = ErrorCodes.NotFound,
                    Message = $"Model '{modelId}' has no instance on this node.",
                });

            if (container.State == "created")
            {
                await _docker.StartContainerAsync(container.Id, ct).ConfigureAwait(false);
            }

            return await MapAsync(modelId, container, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(stages);
        var gate = _modelLocks.GetOrAdd(modelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var container = await FindContainerAsync(modelId, ct).ConfigureAwait(false);
            if (container is null)
            {
                return;
            }

            await StopAndRemoveAsync(modelId, container, stop: true, stages, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var container = await FindContainerAsync(modelId, ct).ConfigureAwait(false);
        if (container is null)
        {
            return null;
        }

        var launch = await _docker.InspectLaunchConfigAsync(container.Id, ct).ConfigureAwait(false);
        return new LaunchConfig
        {
            Kind = LaunchDelivery.Container,
            Runtime = ReadRuntime(container.Labels),
            // The spec's reference (a tag) when the container was created from repo@digest, so the master's
            // ModelConfigHash over image + Cmd recomputes unchanged; else what Docker recorded (today's containers).
            Image = container.Labels.GetValueOrDefault(ContainerLaunchValidator.ImageLabel) ?? launch.Image,
            ImageDigest = container.Labels.GetValueOrDefault(ContainerLaunchValidator.ImageDigestLabel),
            Argv = launch.Cmd,
            Env = ReadEnvironment(container.Labels, launch.Env),
            Mounts = ReadMounts(launch.Binds),
            Gpus = GpuRefs(container.Labels),
            Labels = container.Labels,
            ExecutionFingerprint = container.Labels.GetValueOrDefault(ContainerLabels.ExecutionFingerprint),
            CreatedAt = container.CreatedAt,
        };
    }

    /// <summary>
    /// The launch environment of an instance (design §4.3 <c>launch-config</c>: names, secret values never): the names the
    /// driver recorded in <see cref="ContainerLaunchValidator.EnvNamesLabel"/>, each with its value from Docker's
    /// <c>Config.Env</c>, or null for the names in <see cref="ContainerLaunchValidator.SecretEnvNamesLabel"/>. The image's
    /// own variables (<c>PATH</c>, …) are not part of the launch and are left out. Instances without the label (P1 and
    /// earlier) have no launch environment.
    /// </summary>
    internal static IReadOnlyDictionary<string, string?> ReadEnvironment(IReadOnlyDictionary<string, string> labels, IReadOnlyList<string> dockerEnv)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!labels.TryGetValue(ContainerLaunchValidator.EnvNamesLabel, out string? namesLabel) || string.IsNullOrEmpty(namesLabel))
        {
            return result;
        }

        var secrets = new HashSet<string>(
            labels.GetValueOrDefault(ContainerLaunchValidator.SecretEnvNamesLabel)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [],
            StringComparer.Ordinal);
        foreach (string name in namesLabel.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (secrets.Contains(name))
            {
                result[name] = null;
                continue;
            }

            // Docker keeps the last definition of a name; so does this lookup.
            string? value = null;
            string prefix = name + "=";
            foreach (string entry in dockerEnv)
            {
                if (entry.StartsWith(prefix, StringComparison.Ordinal))
                {
                    value = entry[prefix.Length..];
                }
            }

            result[name] = value;
        }

        return result;
    }

    /// <summary>
    /// The logical mounts of an instance, mapped back from Docker's <c>HostConfig.Binds</c> (the strings
    /// <see cref="ContainerLaunchValidator"/> builds: <c>{root}[/{subPath}]:{target}[:ro]</c>). A bind whose target is not a
    /// logical volume's, or whose source is outside that volume's host root, is not a launch mount and is left out.
    /// </summary>
    internal IReadOnlyList<MountSpec> ReadMounts(IReadOnlyList<string> binds)
    {
        var mounts = new List<MountSpec>(binds.Count);
        foreach (string bind in binds)
        {
            if (TryReadMount(bind, LogicalVolumes.Models, LogicalVolumes.ModelsTarget, _options.ModelsHostPath) is { } models)
            {
                mounts.Add(models);
            }
            else if (TryReadMount(bind, LogicalVolumes.Cache, LogicalVolumes.CacheTarget, _options.CacheHostPath) is { } cache)
            {
                mounts.Add(cache);
            }
        }

        return mounts;
    }

    private static MountSpec? TryReadMount(string bind, string volume, string target, string hostRoot)
    {
        // Parsed from the end: a Windows host root (C:\…) contains ':' itself.
        string marker = ":" + target;
        int index = bind.LastIndexOf(marker, StringComparison.Ordinal);
        if (index <= 0)
        {
            return null;
        }

        string rest = bind[(index + marker.Length)..];
        if (rest.Length > 0 && rest[0] != ':')
        {
            return null; // e.g. "/models-other"
        }

        string mode = rest.Length > 0 ? rest[1..] : "";
        bool readOnly = mode.Split(',').Contains("ro", StringComparer.Ordinal);
        string source = bind[..index];
        string root = hostRoot.TrimEnd('/');
        if (string.Equals(source, hostRoot, StringComparison.Ordinal) || string.Equals(source, root, StringComparison.Ordinal))
        {
            return new MountSpec(volume, null, target, readOnly);
        }

        return source.StartsWith(root + "/", StringComparison.Ordinal) && source.Length > root.Length + 1
            ? new MountSpec(volume, source[(root.Length + 1)..], target, readOnly)
            : null;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamLogsAsync(
        string modelId, int tail, bool follow, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var container = await FindContainerAsync(modelId, ct).ConfigureAwait(false);
        if (container is null)
        {
            yield break;
        }

        await foreach (var line in _docker.GetLogsAsync(container.Id, tail, follow, ct).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    /// <inheritdoc />
    public async Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var container = await FindContainerAsync(modelId, ct).ConfigureAwait(false);
        return container is { State: "running" } ? UpstreamUri(modelId, TryGetHostPort(container)) : null;
    }

    /// <summary>
    /// True when <paramref name="argument"/> is <paramref name="root"/> or a path under it, also in the
    /// <c>--option=/models/x</c> form (W2 review MN-092: custom args may use it).
    /// </summary>
    internal static bool ArgumentUsesPath(string argument, string root)
    {
        int equals = argument.IndexOf('=', StringComparison.Ordinal);
        string value = equals >= 0 && !argument.StartsWith('/') ? argument[(equals + 1)..] : argument;
        return string.Equals(value, root, StringComparison.Ordinal) || value.StartsWith(root + "/", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default)
    {
        if (PathConfinement.CheckPlainRelative(folder) is not null || folder.Contains('/', StringComparison.Ordinal))
        {
            throw new AgentException(400, new AgentError
            {
                Code = ErrorCodes.InvalidRequest,
                Message = $"Folder '{folder}' is not a plain folder name.",
            });
        }

        string root = $"{LogicalVolumes.ModelsTarget}/{folder}";
        var containers = await _docker.ListContainersAsync([ContainerLabels.Model], all: true, ct).ConfigureAwait(false);
        foreach (var container in containers.Where(IsOwned))
        {
            try
            {
                var launch = await _docker.InspectLaunchConfigAsync(container.Id, ct).ConfigureAwait(false);
                if (launch.Cmd.Any(arg => ArgumentUsesPath(arg, root)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or DockerApiException)
            {
                // Fail safe: a container whose command cannot be read may use the folder.
                ContainerRuntimeLog.InspectFailed(_logger, ex, container.Id);
                return true;
            }
        }

        return false;
    }

    private async Task<RuntimeLaunchResult> CreateAndStartAsync(
        RuntimeLaunch launch, ContainerLaunchPlan plan, IRuntimeStageSink stages, CancellationToken ct)
    {
        string id = launch.ModelId;
        await _docker.EnsureNetworkAsync(_options.DockerNetwork, ct).ConfigureAwait(false);

        // With a digest, Docker resolves (and pulls) repo@digest: the instance runs exactly the approved image even if
        // the tag was re-pointed since (W2 review MN-092). Without one, today's tag path.
        string image = plan.CreateImage;
        if (!await _docker.ImageExistsAsync(image, ct).ConfigureAwait(false))
        {
            await stages.StageReachedAsync(OperationStages.Pulling, new OperationProgress { Message = $"pulling {image}" }, ct)
                .ConfigureAwait(false);
            ContainerRuntimeLog.Pulling(_logger, image, id);
            await _docker.PullImageAsync(image, launch.Registry, ct).ConfigureAwait(false);
            await stages.StageReachedAsync(OperationStages.Pulled, new OperationProgress { Percent = 100 }, ct).ConfigureAwait(false);
        }

        int hostPort = launch.HostPort > 0
            ? launch.HostPort
            : await _ports.AllocatePortAsync(id, launch.OperationId, ct).ConfigureAwait(false);
        if (hostPort is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Host port {hostPort} for model '{id}' is out of range.");
        }

        var labels = BuildLabels(launch, plan, hostPort);
        var request = new CreateContainerRequest(
            Name: $"ss-{id}",
            Image: image,
            Cmd: plan.Cmd,
            Labels: labels,
            Binds: plan.Binds,
            ContainerPort: plan.ContainerPort,
            HostPort: hostPort,
            HostIp: "127.0.0.1",
            Network: _options.DockerNetwork,
            NetworkAlias: $"ss-{id}",
            IpcMode: plan.IpcMode,
            Env: plan.Environment.IsEmpty ? null : plan.Environment.ToDockerEnv(),
            GpuDeviceIds: plan.GpuUuids);

        await stages.BeforeStepAsync(OperationStages.Created, ct).ConfigureAwait(false);
        string containerId;
        try
        {
            containerId = await _docker.CreateContainerAsync(request, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (plan.Environment.SecretNames.Count > 0)
        {
            // The failure ends up in the operation's error and the agent log: never with a secret value in it (§10.4).
            throw new DockerApiException(ex.StatusCode, plan.Environment.Redact(ex.ResponseBody));
        }

        await stages.StageReachedAsync(OperationStages.Created, null, ct).ConfigureAwait(false);
        ContainerRuntimeLog.Created(_logger, id, containerId, hostPort, launch.OperationId);

        await stages.BeforeStepAsync(OperationStages.Started, ct).ConfigureAwait(false);
        await _docker.StartContainerAsync(containerId, ct).ConfigureAwait(false);
        await stages.StageReachedAsync(OperationStages.Started, null, ct).ConfigureAwait(false);

        var status = BuildStatus(id, ModelState.Starting, containerId, hostPort, _time.GetUtcNow(), labels: labels);
        return new RuntimeLaunchResult(RuntimeLaunchOutcome.Created, status);
    }

    /// <summary>
    /// The container labels: today's (<c>mirbadllm.model</c>, the spec's served name / config hash / budget / tuning,
    /// <c>mirbadllm.port</c>) plus the agent-set node and operation; with a digest also the digest and the spec's image
    /// reference, with an environment its variable names (never values). A P1-shaped spec gets exactly today's labels.
    /// </summary>
    private Dictionary<string, string> BuildLabels(RuntimeLaunch launch, ContainerLaunchPlan plan, int hostPort)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal) { [ContainerLabels.Model] = launch.ModelId };
        foreach (var (key, value) in plan.SpecLabels)
        {
            labels[key] = value;
        }

        labels[ContainerLabels.Port] = hostPort.ToString(CultureInfo.InvariantCulture);
        labels[ContainerLabels.Node] = _options.NodeId;
        labels[ContainerLabels.Operation] = launch.OperationId;
        if (plan.Runtime != RuntimeKind.Vllm)
        {
            labels[ContainerLabels.Runtime] = RuntimeName(plan.Runtime);
            labels[ContainerLabels.HealthPath] = plan.HealthPath;
        }

        if (plan.ImageDigest is not null)
        {
            labels[ContainerLaunchValidator.ImageDigestLabel] = plan.ImageDigest;
            labels[ContainerLaunchValidator.ImageLabel] = plan.Image;
        }

        if (plan.GpuUuids is { } uuids)
        {
            // Read-back metadata (design §5.1): the GPUs Docker was given, and the per-GPU denominator when the master did
            // not send the one it used.
            labels[ContainerLabels.GpuUuids] = string.Join(',', uuids);
            if (!labels.ContainsKey(ContainerLabels.GpuTotalGiB) && GpuTotalGiB(uuids) is { } total)
            {
                labels[ContainerLabels.GpuTotalGiB] = total.ToString("R", CultureInfo.InvariantCulture);
            }
        }

        if (!plan.Environment.IsEmpty)
        {
            labels[ContainerLaunchValidator.EnvNamesLabel] = string.Join(',', plan.Environment.Names);
            if (plan.Environment.SecretNames.Count > 0)
            {
                labels[ContainerLaunchValidator.SecretEnvNamesLabel] = string.Join(',', plan.Environment.SecretNames);
            }
        }

        return labels;
    }

    private async Task StopAndRemoveAsync(string modelId, ContainerSummary container, bool stop, IRuntimeStageSink stages, CancellationToken ct)
    {
        _stopping[modelId] = 0;
        try
        {
            await stages.BeforeStepAsync(OperationStages.Stopping, ct).ConfigureAwait(false);
            await stages.StageReachedAsync(OperationStages.Stopping, null, ct).ConfigureAwait(false);
            if (stop)
            {
                try
                {
                    await _docker.StopContainerAsync(container.Id, timeoutSeconds: 30, ct).ConfigureAwait(false);
                }
                catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    // Already gone.
                }
            }

            await stages.BeforeStepAsync(OperationStages.Removed, ct).ConfigureAwait(false);
            await RemoveAsync(container.Id, ct).ConfigureAwait(false);
            await stages.StageReachedAsync(OperationStages.Removed, null, ct).ConfigureAwait(false);
        }
        finally
        {
            _stopping.TryRemove(modelId, out _);
        }
    }

    private async Task RemoveAsync(string containerId, CancellationToken ct)
    {
        try
        {
            await _docker.RemoveContainerAsync(containerId, ct).ConfigureAwait(false);
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone: the goal (no container) holds.
        }
    }

    private void EnsureNodeId()
    {
        if (string.IsNullOrWhiteSpace(_options.NodeId))
        {
            throw new InvalidOperationException("ContainerRuntimeOptions.NodeId is not configured.");
        }
    }

    /// <summary>
    /// Instances this driver manages: carrying <c>mirbadllm.model</c> and either this node's id or no node label
    /// (created before P1, adopted by the bootstrap agent, design §5.4).
    /// </summary>
    private bool IsOwned(ContainerSummary container) =>
        container.Labels.ContainsKey(ContainerLabels.Model) &&
        (!container.Labels.TryGetValue(ContainerLabels.Node, out string? node) ||
         string.Equals(node, _options.NodeId, StringComparison.Ordinal));

    private async Task<ContainerSummary?> FindContainerAsync(string modelId, CancellationToken ct)
    {
        var containers = await _docker.ListContainersAsync([$"{ContainerLabels.Model}={modelId}"], all: true, ct).ConfigureAwait(false);
        return containers.FirstOrDefault(IsOwned);
    }

    /// <summary>The pre-P1 state mapping (<c>DockerModelRuntime.MapAsync</c>), without restart-pending (computed on the master).</summary>
    private async Task<RuntimeStatus> MapAsync(string modelId, ContainerSummary container, CancellationToken ct)
    {
        int? hostPort = TryGetHostPort(container);
        if (_stopping.ContainsKey(modelId))
        {
            return Status(modelId, ModelState.Stopping, container, hostPort, container.CreatedAt);
        }

        switch (container.State)
        {
            case "running":
                var running = await ProbeHealthAsync(modelId, hostPort, container.Labels, ct).ConfigureAwait(false);
                if (running == ModelState.Starting)
                {
                    int restarts = await RestartCountAsync(container.Id, ct).ConfigureAwait(false);
                    if (restarts > 0)
                    {
                        return await FailCrashLoopAsync(modelId, container, hostPort, restarts, ct).ConfigureAwait(false);
                    }
                }

                // Model containers are created and started in one step, so creation time is the start time.
                return Status(modelId, running, container, hostPort, container.CreatedAt);

            case "restarting":
                return await FailCrashLoopAsync(modelId, container, hostPort, restarts: 0, ct).ConfigureAwait(false);

            case "exited":
            case "dead":
                string? error = await ReadFailureLogAsync(container.Id, ct).ConfigureAwait(false);
                return Status(modelId, ModelState.Failed, container, hostPort, since: null, error);

            case "removing":
                return Status(modelId, ModelState.Stopping, container, hostPort, container.CreatedAt);

            default: // created, paused
                return Status(modelId, ModelState.Starting, container, hostPort, container.CreatedAt);
        }
    }

    private RuntimeStatus Status(
        string modelId, ModelState state, ContainerSummary? container, int? hostPort, DateTimeOffset? since,
        string? error = null, int restartCount = 0) =>
        BuildStatus(modelId, state, container?.Id, hostPort, since, error, restartCount, container?.Labels);

    /// <summary>
    /// The GPUs of an instance from its <see cref="ContainerLabels.GpuUuids"/> label, in launch order, each with its index
    /// in the inventory's last read (-1 when the card is not in it now). Empty for instances on every GPU (P1).
    /// </summary>
    internal IReadOnlyList<GpuRef> GpuRefs(IReadOnlyDictionary<string, string> labels)
    {
        if (!labels.TryGetValue(ContainerLabels.GpuUuids, out string? text) || string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var inventory = _gpus?.Last ?? [];
        return [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(uuid => new GpuRef(inventory.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal))?.Index ?? -1, uuid))];
    }

    /// <summary>The selected GPUs' common <c>memory.total</c> from the inventory's last read; null when unknown or unequal.</summary>
    private double? GpuTotalGiB(IReadOnlyList<string> uuids)
    {
        var inventory = _gpus?.Last ?? [];
        var totals = uuids
            .Select(u => inventory.FirstOrDefault(g => string.Equals(g.Uuid, u, StringComparison.Ordinal))?.MemoryTotalGiB)
            .ToList();
        return totals.Count > 0 && totals.All(t => t is > 0) && totals.Distinct().Count() == 1 ? totals[0] : null;
    }

    private RuntimeStatus BuildStatus(
        string modelId, ModelState state, string? instanceId, int? hostPort, DateTimeOffset? since,
        string? error = null, int restartCount = 0, IReadOnlyDictionary<string, string>? labels = null)
    {
        labels ??= new Dictionary<string, string>(StringComparer.Ordinal);
        return new RuntimeStatus
        {
            ModelId = modelId,
            State = state,
            InstanceId = instanceId,
            HostPort = hostPort,
            Since = since,
            Error = error,
            RestartCount = restartCount,
            Labels = labels,
            Gpus = GpuRefs(labels),
            ServedName = labels.GetValueOrDefault(ContainerLabels.ServedName),
            ConfigHash = labels.GetValueOrDefault(ContainerLabels.ConfigHash),
            ExecutionFingerprint = labels.GetValueOrDefault(ContainerLabels.ExecutionFingerprint),
            Tuning = labels.TryGetValue(ContainerLabels.Tuning, out string? tuning) && tuning == "1",
            OperationId = labels.GetValueOrDefault(ContainerLabels.Operation),
            Runtime = ReadRuntime(labels),
            Delivery = LaunchDelivery.Container,
        };
    }

    private static string RuntimeName(RuntimeKind runtime) => runtime switch
    {
        RuntimeKind.LlamaCpp => "llamacpp",
        RuntimeKind.Sglang => "sglang",
        RuntimeKind.Nim => "nim",
        _ => "vllm",
    };

    private static RuntimeKind ReadRuntime(IReadOnlyDictionary<string, string> labels) =>
        labels.GetValueOrDefault(ContainerLabels.Runtime) switch
        {
            "llamacpp" => RuntimeKind.LlamaCpp,
            "sglang" => RuntimeKind.Sglang,
            "nim" => RuntimeKind.Nim,
            _ => RuntimeKind.Vllm,
        };

    private Uri? UpstreamUri(string modelId, int? hostPort)
    {
        if (_options.UseContainerNetwork)
        {
            return new Uri($"http://ss-{modelId}:{_options.ContainerPort.ToString(CultureInfo.InvariantCulture)}/");
        }

        return hostPort is { } port ? new Uri($"http://{_options.UpstreamHost}:{port.ToString(CultureInfo.InvariantCulture)}/") : null;
    }

    private async Task<ModelState> ProbeHealthAsync(
        string modelId, int? hostPort, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var uri = UpstreamUri(modelId, hostPort);
        if (uri is null)
        {
            return ModelState.Starting;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            string healthPath = labels.GetValueOrDefault(ContainerLabels.HealthPath) ?? "/health";
            using var response = await _upstreamHttp.GetAsync(new Uri(uri, healthPath.TrimStart('/')), cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? ModelState.Ready : ModelState.Starting;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return ModelState.Starting;
        }
    }

    /// <summary>
    /// Docker's restart count. A container that Docker has already restarted and that is still not healthy is
    /// crash-looping (the restart policy hides each crash behind a fresh start); a normal first start has 0.
    /// </summary>
    private async Task<int> RestartCountAsync(string containerId, CancellationToken ct)
    {
        try
        {
            var state = await _docker.InspectStateAsync(containerId, ct).ConfigureAwait(false);
            return state.RestartCount;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Stops (but keeps) a crash-looping container so it stops restarting and holding memory, and reports Failed with
    /// the root cause from its logs. The next ensure-started launch replaces it.
    /// </summary>
    private async Task<RuntimeStatus> FailCrashLoopAsync(
        string modelId, ContainerSummary container, int? hostPort, int restarts, CancellationToken ct)
    {
        string? error = await ReadFailureLogAsync(container.Id, ct).ConfigureAwait(false);
        try
        {
            await _docker.StopContainerAsync(container.Id, timeoutSeconds: 5, ct).ConfigureAwait(false);
            ContainerRuntimeLog.CrashLoopStopped(_logger, modelId);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            ContainerRuntimeLog.CrashLoopStopFailed(_logger, ex, modelId);
        }

        return Status(modelId, ModelState.Failed, container, hostPort, since: null, error, restarts);
    }

    private async Task<string?> ReadFailureLogAsync(string containerId, CancellationToken ct)
    {
        try
        {
            var lines = new List<string>();
            await foreach (var line in _docker.GetLogsAsync(containerId, tail: 400, follow: false, ct).ConfigureAwait(false))
            {
                lines.Add(line);
            }

            return FailureReport.Build(lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            ContainerRuntimeLog.ReadLogsFailed(_logger, ex, containerId);
            return null;
        }
    }

    private static int? TryGetHostPort(ContainerSummary? container) =>
        container is not null &&
        container.Labels.TryGetValue(ContainerLabels.Port, out string? portText) &&
        int.TryParse(portText, CultureInfo.InvariantCulture, out int port)
            ? port
            : null;
}
