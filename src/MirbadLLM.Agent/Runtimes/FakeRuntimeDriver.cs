using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Fakes;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// Development-only runtime driver (MN-042, design §13 item 5): runs a <see cref="DevModelServer"/> on the reserved
/// loopback port instead of a vLLM container, so a master and two agents run on a laptop without GPUs or Docker. It is
/// registered only by <see cref="DevFakeRuntime"/>, which refuses to enable it outside the Development environment.
/// <para>It behaves like <see cref="ContainerRuntimeDriver"/> where the master can tell: the same spec checks
/// (<see cref="ContainerLaunchValidator"/>, so a spec the DGX would refuse is refused here), the same labels (the spec's,
/// plus <c>mirbadllm.model/node/operation/port</c>), the same stages through <see cref="IRuntimeStageSink"/>
/// (<c>removed</c>/<c>stopping</c> before a replace, <c>created</c>, <c>started</c>), Starting until the fake model
/// "loaded", then Ready, and a <c>launch-config</c> that reads back exactly what was launched. Instances live in this
/// process: an agent restart loses them, which reconciliation treats like containers removed while it was down.</para>
/// <para><b>Discrete fake node</b> (<c>discrete:2x16</c>, MN-122): a <c>gpus.mode = uuids</c> launch is checked against the
/// configured GPUs (422 <c>gpu_missing</c> before reserving), recorded with its GPUs and the §5.1 read-back labels, and its
/// budget shows as VRAM used on those GPUs in the fake <c>nvidia-smi</c> (<see cref="GpuUsedGiB"/>).</para>
/// </summary>
internal sealed partial class FakeRuntimeDriver : IRuntimeDriver, IAsyncDisposable
{
    private readonly ContainerRuntimeOptions _options;
    private readonly Func<IPortAllocator> _ports;
    private readonly DevFakeRuntimeOptions _fake;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, FakeInstance> _instances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public FakeRuntimeDriver(
        ContainerRuntimeOptions options,
        Func<IPortAllocator> ports,
        DevFakeRuntimeOptions fake,
        TimeProvider time,
        ILogger<FakeRuntimeDriver> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(fake);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _ports = ports;
        _fake = fake;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public LaunchDelivery Delivery => LaunchDelivery.Container;

    /// <summary>Memory the running fake instances use (their <c>mirbadllm.budget-gib</c> labels), for the fake memory probe.</summary>
    public double UsedGiB => _instances.Values.Where(i => i.Server is not null).Sum(i => i.BudgetGiB);

    /// <summary>
    /// VRAM the running fake instances use on GPU <paramref name="uuid"/> of a discrete fake node (MN-122): each takes its
    /// budget ÷ n on each of its n GPUs (design §5.1), for the fake <c>nvidia-smi</c>. 0 on a unified node.
    /// </summary>
    public double GpuUsedGiB(string uuid) =>
        _instances.Values
            .Where(i => i.Server is not null && i.VramGpus.Contains(uuid, StringComparer.Ordinal))
            .Sum(i => i.BudgetGiB / i.VramGpus.Count);

    /// <inheritdoc />
    public void CheckSpec(string modelId, LaunchSpec spec) => Plan(modelId, spec);

    /// <inheritdoc />
    public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RuntimeStatus>>([.. _instances.Values.Select(Status)]);

    /// <inheritdoc />
    public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default) =>
        Task.FromResult(_instances.TryGetValue(modelId, out FakeInstance? instance) ? Status(instance) : null);

    /// <inheritdoc />
    public async Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentException.ThrowIfNullOrEmpty(launch.OperationId);
        (ContainerLaunchPlan plan, IReadOnlyList<GpuRef> gpus, IReadOnlyList<string> vramGpus) = Plan(launch.ModelId, launch.Spec);
        SemaphoreSlim gate = _locks.GetOrAdd(launch.ModelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_instances.TryGetValue(launch.ModelId, out FakeInstance? existing))
            {
                RuntimeStatus current = Status(existing);
                if (launch.Mode == LaunchMode.EnsureStarted && current.State is ModelState.Starting or ModelState.Ready or ModelState.Stopping)
                {
                    return new RuntimeLaunchResult(RuntimeLaunchOutcome.AlreadyRunning, current);
                }

                if (launch.Mode == LaunchMode.Replace && existing.Server is not null)
                {
                    await stages.BeforeStepAsync(OperationStages.Stopping, ct).ConfigureAwait(false);
                    await stages.StageReachedAsync(OperationStages.Stopping, null, ct).ConfigureAwait(false);
                }

                await stages.BeforeStepAsync(OperationStages.Removed, ct).ConfigureAwait(false);
                await RemoveAsync(existing).ConfigureAwait(false);
                await stages.StageReachedAsync(OperationStages.Removed, null, ct).ConfigureAwait(false);
            }

            int hostPort = launch.HostPort > 0
                ? launch.HostPort
                : await _ports().AllocatePortAsync(launch.ModelId, launch.OperationId, ct).ConfigureAwait(false);
            var labels = new Dictionary<string, string>(plan.SpecLabels, StringComparer.Ordinal)
            {
                [ContainerLabels.Model] = launch.ModelId,
                [ContainerLabels.Port] = hostPort.ToString(CultureInfo.InvariantCulture),
                [ContainerLabels.Node] = _options.NodeId,
                [ContainerLabels.Operation] = launch.OperationId,
            };
            if (plan.ImageDigest is not null)
            {
                labels[ContainerLaunchValidator.ImageDigestLabel] = plan.ImageDigest;
            }

            if (gpus.Count > 0)
            {
                // The read-back labels of a launch on selected GPUs (design §5.1, as MN-106's container driver writes them).
                labels[ContainerLabels.GpuUuids] = string.Join(',', gpus.Select(g => g.Uuid));
                labels[ContainerLabels.GpuTotalGiB] = _fake.Gpus.First(g => g.Uuid == gpus[0].Uuid).TotalGiB.ToString("0.###", CultureInfo.InvariantCulture);
            }

            await stages.BeforeStepAsync(OperationStages.Created, ct).ConfigureAwait(false);
            var instance = new FakeInstance(launch.ModelId, "fake-" + Guid.NewGuid().ToString("N")[..12], hostPort, launch.Spec, plan, labels, _time.GetUtcNow())
            {
                Gpus = gpus,
                VramGpus = vramGpus,
            };
            _instances[launch.ModelId] = instance;
            instance.Log($"created instance {instance.Id} for model '{launch.ModelId}' (image {plan.Image}, fake runtime)");
            await stages.StageReachedAsync(OperationStages.Created, null, ct).ConfigureAwait(false);
            LogCreated(_logger, launch.ModelId, instance.Id, hostPort, launch.OperationId);

            await stages.BeforeStepAsync(OperationStages.Started, ct).ConfigureAwait(false);
            await StartInstanceAsync(instance, ct).ConfigureAwait(false);
            await stages.StageReachedAsync(OperationStages.Started, null, ct).ConfigureAwait(false);
            return new RuntimeLaunchResult(RuntimeLaunchOutcome.Created, Status(instance));
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
        SemaphoreSlim gate = _locks.GetOrAdd(modelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_instances.TryGetValue(modelId, out FakeInstance? instance))
            {
                throw new AgentException(404, new AgentError { Code = ErrorCodes.NotFound, Message = $"Model '{modelId}' has no instance on this node." });
            }

            if (instance.Server is null && instance.Error is null)
            {
                await StartInstanceAsync(instance, ct).ConfigureAwait(false);
            }

            return Status(instance);
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
        SemaphoreSlim gate = _locks.GetOrAdd(modelId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_instances.TryGetValue(modelId, out FakeInstance? instance))
            {
                return;
            }

            await stages.BeforeStepAsync(OperationStages.Stopping, ct).ConfigureAwait(false);
            instance.Stopping = true;
            await stages.StageReachedAsync(OperationStages.Stopping, null, ct).ConfigureAwait(false);
            await stages.BeforeStepAsync(OperationStages.Removed, ct).ConfigureAwait(false);
            await RemoveAsync(instance).ConfigureAwait(false);
            await stages.StageReachedAsync(OperationStages.Removed, null, ct).ConfigureAwait(false);
            LogRemoved(_logger, modelId, instance.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(modelId, out FakeInstance? instance))
        {
            return Task.FromResult<LaunchConfig?>(null);
        }

        return Task.FromResult<LaunchConfig?>(new LaunchConfig
        {
            Kind = LaunchDelivery.Container,
            Runtime = instance.Plan.Runtime,
            Image = instance.Plan.Image,
            ImageDigest = instance.Labels.GetValueOrDefault(ContainerLaunchValidator.ImageDigestLabel),
            Argv = instance.Plan.Cmd,
            Mounts = instance.Spec.Mounts,
            Gpus = instance.Gpus,
            Labels = instance.Labels,
            ExecutionFingerprint = instance.Labels.GetValueOrDefault(ContainerLabels.ExecutionFingerprint),
            CreatedAt = instance.CreatedAt,
        });
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_instances.TryGetValue(modelId, out FakeInstance? instance))
        {
            yield break;
        }

        string[] history = instance.Lines();
        int next = tail > 0 ? Math.Max(0, history.Length - tail) : 0;
        foreach (string line in history.Skip(next))
        {
            yield return line;
        }

        next = history.Length;
        while (follow && !ct.IsCancellationRequested && _instances.TryGetValue(modelId, out FakeInstance? current) && ReferenceEquals(current, instance))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            string[] lines = instance.Lines();
            for (; next < lines.Length; next++)
            {
                yield return lines[next];
            }
        }
    }

    /// <inheritdoc />
    public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default) =>
        Task.FromResult(_instances.TryGetValue(modelId, out FakeInstance? instance) && instance.Server is { } server && !instance.Stopping
            ? server.BaseUri
            : null);

    /// <inheritdoc />
    public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default)
    {
        string root = $"{LogicalVolumes.ModelsTarget}/{folder}";
        return Task.FromResult(_instances.Values.Any(i => i.Plan.Cmd.Any(arg => UsesPath(arg, root))));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (FakeInstance instance in _instances.Values)
        {
            await RemoveAsync(instance).ConfigureAwait(false);
        }

        _instances.Clear();
        foreach (SemaphoreSlim gate in _locks.Values)
        {
            gate.Dispose();
        }
    }

    /// <summary>
    /// The driver's checks of a spec, before anything is reserved (<see cref="CheckSpec"/>) and again at launch: the container
    /// driver's (<see cref="ContainerLaunchValidator"/>) and, on a discrete fake node, the GPU selection (MN-122).
    /// <list type="bullet">
    /// <item>Unified node: exactly the container driver's checks, so <c>gpus.mode = uuids</c> is refused as on the DGX.</item>
    /// <item>Discrete node, <c>uuids</c>: every UUID must be one of the node's GPUs, else 422 <c>gpu_missing</c> naming
    /// the missing ones (before reserving, design §5.1); the instance uses those GPUs at their current index and its
    /// budget is split over them.</item>
    /// <item>Discrete node, <c>all</c> (what a master sends before MN-106): the model sits where vLLM would put it, on the
    /// first <c>--tensor-parallel-size</c> GPUs (default 1); its status lists no GPUs, as for any <c>all</c> launch.</item>
    /// </list>
    /// </summary>
    private (ContainerLaunchPlan Plan, IReadOnlyList<GpuRef> Gpus, IReadOnlyList<string> VramGpus) Plan(string modelId, LaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        // A unified fake has no GPU list to select from (the DGX's GB10 is shared memory): refuse a 'uuids' selection
        // here instead of relying on the shared validator, which accepts 'uuids' since MN-106.
        if (!_fake.IsDiscrete && spec.Gpus.Mode == GpuSelectionMode.Uuids)
        {
            throw new AgentException(ErrorMappings.AgentStatus(ErrorCodes.SpecRejected), new AgentError
            {
                Code = ErrorCodes.SpecRejected,
                Message = $"Model '{modelId}': this node has unified memory and no GPU list; gpus.mode 'uuids' is not supported here.",
            });
        }

        if (!_fake.IsDiscrete || spec.Gpus.Mode != GpuSelectionMode.Uuids)
        {
            ContainerLaunchPlan plan = ContainerLaunchValidator.Validate(modelId, spec, _options);
            return (plan, [], _fake.IsDiscrete ? [.. _fake.Gpus.Take(TensorParallelSize(plan.Cmd)).Select(g => g.Uuid)] : []);
        }

        IReadOnlyList<string> requested = spec.Gpus.Uuids ?? [];
        if (requested.Count == 0 || requested.Distinct(StringComparer.Ordinal).Count() != requested.Count)
        {
            throw new AgentException(ErrorMappings.AgentStatus(ErrorCodes.SpecRejected), new AgentError
            {
                Code = ErrorCodes.SpecRejected,
                Message = $"Model '{modelId}': gpus.mode 'uuids' needs each GPU UUID once (got {requested.Count}).",
            });
        }

        List<string> missing = [.. requested.Where(u => _fake.Gpus.All(g => !string.Equals(g.Uuid, u, StringComparison.Ordinal)))];
        if (missing.Count > 0)
        {
            throw new AgentException(ErrorMappings.AgentStatus(ErrorCodes.GpuMissing), new AgentError
            {
                Code = ErrorCodes.GpuMissing,
                Message = $"GPU(s) {string.Join(", ", missing)} are not on node {_options.NodeId}.",
                Details = AgentJson.ToElement(new GpuMissingDetails(missing)),
            });
        }

        // The container checks without the selection (the P1 validator refuses 'uuids' until MN-106 lifts it there).
        ContainerLaunchPlan checkedPlan = ContainerLaunchValidator.Validate(modelId, spec with { Gpus = new GpuSelection() }, _options);
        IReadOnlyList<GpuRef> gpus =
        [
            .. requested.Select(u => _fake.Gpus.First(g => string.Equals(g.Uuid, u, StringComparison.Ordinal))).Select(g => new GpuRef(g.Index, g.Uuid)),
        ];
        return (checkedPlan, gpus, requested);
    }

    /// <summary><c>--tensor-parallel-size N</c> / <c>=N</c> / <c>-tp N</c> in the argv; 1 when absent or invalid.</summary>
    internal static int TensorParallelSize(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        for (int i = 0; i < argv.Count; i++)
        {
            string arg = argv[i];
            string? value = arg.StartsWith("--tensor-parallel-size=", StringComparison.Ordinal) ? arg["--tensor-parallel-size=".Length..]
                : (arg is "--tensor-parallel-size" or "-tp") && i + 1 < argv.Count ? argv[i + 1]
                : null;
            if (value is not null)
            {
                return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int size) && size > 0 ? size : 1;
            }
        }

        return 1;
    }

    private static bool UsesPath(string argument, string root)
    {
        int equals = argument.IndexOf('=', StringComparison.Ordinal);
        string value = equals >= 0 && !argument.StartsWith('/') ? argument[(equals + 1)..] : argument;
        return string.Equals(value, root, StringComparison.Ordinal) || value.StartsWith(root + "/", StringComparison.Ordinal);
    }

    private async Task StartInstanceAsync(FakeInstance instance, CancellationToken ct)
    {
        string served = instance.Labels.GetValueOrDefault(ContainerLabels.ServedName) ?? instance.ModelId;
        try
        {
            instance.Server = await DevModelServer.StartAsync(instance.HostPort, served, _fake.StartDelay, _fake.TokenDelay, _time, ct).ConfigureAwait(false);
            instance.StartedAt = _time.GetUtcNow();
            instance.Log($"fake model '{served}' listening on {instance.Server.BaseUri} (ready in {_fake.StartDelay.TotalSeconds:0.#} s)");
        }
        catch (IOException ex)
        {
            // Like a container whose published port is taken: the start fails and the instance stays Failed.
            instance.Error = $"The fake model server could not listen on 127.0.0.1:{instance.HostPort}: {ex.Message}";
            instance.Log(instance.Error);
            LogStartFailed(_logger, ex, instance.ModelId, instance.HostPort);
            throw new InvalidOperationException(instance.Error, ex);
        }
    }

    private async Task RemoveAsync(FakeInstance instance)
    {
        _instances.TryRemove(new KeyValuePair<string, FakeInstance>(instance.ModelId, instance));
        if (instance.Server is { } server)
        {
            instance.Server = null;
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private RuntimeStatus Status(FakeInstance instance)
    {
        ModelState state;
        DateTimeOffset? since = instance.CreatedAt;
        if (instance.Error is not null)
        {
            state = ModelState.Failed;
            since = null;
        }
        else if (instance.Stopping)
        {
            state = ModelState.Stopping;
        }
        else if (instance.Server is { } server && _time.GetUtcNow() >= server.ReadyAt)
        {
            state = ModelState.Ready;
        }
        else
        {
            state = ModelState.Starting;
        }

        return new RuntimeStatus
        {
            ModelId = instance.ModelId,
            State = state,
            InstanceId = instance.Id,
            HostPort = instance.HostPort,
            Since = since,
            Error = instance.Error,
            Labels = instance.Labels,
            ServedName = instance.Labels.GetValueOrDefault(ContainerLabels.ServedName),
            ConfigHash = instance.Labels.GetValueOrDefault(ContainerLabels.ConfigHash),
            ExecutionFingerprint = instance.Labels.GetValueOrDefault(ContainerLabels.ExecutionFingerprint),
            Tuning = instance.Labels.TryGetValue(ContainerLabels.Tuning, out string? tuning) && tuning == "1",
            Gpus = instance.Gpus,
            OperationId = instance.Labels.GetValueOrDefault(ContainerLabels.Operation),
            Runtime = RuntimeKind.Vllm,
            Delivery = LaunchDelivery.Container,
        };
    }

    [LoggerMessage(EventId = 2690, Level = LogLevel.Information,
        Message = "Fake runtime: created {ModelId} as {InstanceId} on 127.0.0.1:{HostPort} (operation {OperationId})")]
    private static partial void LogCreated(ILogger logger, string modelId, string instanceId, int hostPort, string operationId);

    [LoggerMessage(EventId = 2691, Level = LogLevel.Information, Message = "Fake runtime: removed {ModelId} ({InstanceId})")]
    private static partial void LogRemoved(ILogger logger, string modelId, string instanceId);

    [LoggerMessage(EventId = 2692, Level = LogLevel.Warning, Message = "Fake runtime: {ModelId} could not listen on 127.0.0.1:{HostPort}")]
    private static partial void LogStartFailed(ILogger logger, Exception exception, string modelId, int hostPort);

    private sealed class FakeInstance(
        string modelId, string id, int hostPort, LaunchSpec spec, ContainerLaunchPlan plan, IReadOnlyDictionary<string, string> labels, DateTimeOffset createdAt)
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = [];

        public string ModelId { get; } = modelId;

        public string Id { get; } = id;

        public int HostPort { get; } = hostPort;

        public LaunchSpec Spec { get; } = spec;

        public ContainerLaunchPlan Plan { get; } = plan;

        public IReadOnlyDictionary<string, string> Labels { get; } = labels;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public double BudgetGiB { get; } =
            labels.TryGetValue(ContainerLabels.BudgetGiB, out string? text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double gib)
                ? gib
                : 0;

        public DevModelServer? Server { get; set; }

        /// <summary>GPUs of a <c>uuids</c> launch (status, launch-config); empty for <c>all</c>.</summary>
        public IReadOnlyList<GpuRef> Gpus { get; init; } = [];

        /// <summary>UUIDs whose VRAM the instance takes on a discrete fake node; empty on a unified one.</summary>
        public IReadOnlyList<string> VramGpus { get; init; } = [];

        public DateTimeOffset? StartedAt { get; set; }

        public volatile bool Stopping;

        public string? Error { get; set; }

        public void Log(string line)
        {
            lock (_gate)
            {
                _lines.Add($"{DateTimeOffset.UtcNow:O} {line}");
            }
        }

        public string[] Lines()
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }
    }
}
