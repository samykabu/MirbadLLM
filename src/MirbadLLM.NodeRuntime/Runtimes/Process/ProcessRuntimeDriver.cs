using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes.Process;

/// <summary>
/// <see cref="IRuntimeDriver"/> for native processes (<c>kind: process</c>, llama.cpp's <c>llama-server</c>;
/// design-p3p4 §A.1.1, §A.1.3, §A.1.4; MN-207 D121). OS-neutral: every OS call goes through <see cref="IProcessHost"/>
/// (Windows: <c>WindowsProcessHost</c>, MN-212). It owns the state machine, <c>processes.json</c>
/// (<see cref="IProcessRecordStore"/>), the port check, the Ready probe, the log files, crash handling, adoption and the
/// orphan sweep.
/// <list type="bullet">
/// <item><b>Start:</b> <see cref="ProcessLaunchValidator"/> (catalogue, argv, env, confinement) → a free loopback port →
/// fencing (<see cref="IRuntimeStageSink"/>) → a durable <see cref="PendingProcessStart"/> →
/// <see cref="IProcessHost.StartAsync"/> with a complete environment block (per-instance <c>LLAMA_API_KEY</c>) →
/// <see cref="IProcessRecordStore.CommitPendingAsync"/>; a failed create removes the pending start. Returns Starting.</item>
/// <item><b>Ready</b> only when <c>/health</c> is 200 and <c>/v1/models</c> asked with the per-instance key lists the
/// alias (<see cref="ProcessHealthProbe"/>); polled every <c>HealthPollInterval</c> until <c>StartTimeout</c>.</item>
/// <item><b>Exit before Ready</b> → Failed at once with the exit code and the log tail. <b>Exit after Ready</b> → restarted
/// on the same port while the <see cref="RestartBudget"/> allows (2 in 10 minutes), then Failed.</item>
/// <item><b>Stop:</b> fencing re-check (<c>stopping</c>) before the kill, <see cref="IHostedProcess.KillTreeAsync"/>, then
/// the record is removed (<c>removed</c>).</item>
/// <item><b>Agent restart</b> (<see cref="RecoverAsync"/>, also run lazily by every other member): each record is
/// re-opened by PID + start time + image path (<see cref="IProcessHost.OpenAsync"/>, which assigns it to a new job);
/// gone or reused → the record is removed (Stopped); adopted → Starting while the probe runs in the background (never
/// inside reconciliation, §A.4.1). Then the orphan sweep kills only a candidate whose arguments hash to a record's or a
/// pending start's digest and that started after it; everything else is logged as foreign and left alone.</item>
/// </list>
/// Disposing the driver stops supervising but never kills a process: the processes outlive the agent by design.
/// </summary>
public sealed partial class ProcessRuntimeDriver : IRuntimeDriver, IAsyncDisposable
{
    /// <summary>Prefix of the per-instance job label (<c>mirbadllm-ss-{id}</c>).</summary>
    public const string JobNamePrefix = "mirbadllm-ss-";

    private static readonly TimeSpan LogFollowInterval = TimeSpan.FromMilliseconds(500);

    private readonly IProcessHost _host;
    private readonly IProcessRecordStore _store;
    private readonly IExecutableCatalog _catalog;
    private readonly IInstanceKeyProtector _keys;
    private readonly IPortAllocator _ports;
    private readonly IGpuStartMeter? _meter;
    private readonly IGpuInventory? _gpus;
    private readonly ProcessRuntimeOptions _options;
    private readonly ProcessLogFiles _logs;
    private readonly ProcessHealthProbe _probe;
    private readonly RestartBudget _budget;
    private readonly ILogger<ProcessRuntimeDriver> _logger;
    private readonly TimeProvider _time;
    private readonly UpstreamChanges? _upstreams;
    private readonly ConcurrentDictionary<string, Instance> _instances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _recoverGate = new(1, 1);
    private volatile bool _recovered;
    private int _disposed;

    /// <summary>Creates the driver.</summary>
    /// <param name="host">The OS process host.</param>
    /// <param name="store"><c>processes.json</c>.</param>
    /// <param name="catalog">Logical executable + version → verified path.</param>
    /// <param name="keys">Protects the per-instance API key at rest.</param>
    /// <param name="ports">Port allocator (the reservation manager).</param>
    /// <param name="upstreamHttp">Client for the loopback Ready probe.</param>
    /// <param name="options">Node-local settings (<c>Agent:Process</c> mapped by the agent).</param>
    /// <param name="logger">Logger.</param>
    /// <param name="gpuStartMeter">"Measured at start" GPU memory (MN-214); null = not measured.</param>
    /// <param name="gpuInventory">The node's GPUs: UUIDs are checked against it (422 <c>gpu_missing</c>); null = not checked.</param>
    /// <param name="timeProvider">Clock (system clock when null).</param>
    /// <param name="upstreamChanges">
    /// Bumped whenever an instance is created, adopted or removed, so the agent proxy's upstream cache cannot proxy to a
    /// replaced instance's old port (MN-239 bug B); null = nothing listens (the behaviour before that fix).
    /// </param>
    public ProcessRuntimeDriver(
        IProcessHost host,
        IProcessRecordStore store,
        IExecutableCatalog catalog,
        IInstanceKeyProtector keys,
        IPortAllocator ports,
        HttpClient upstreamHttp,
        IOptions<ProcessRuntimeOptions> options,
        ILogger<ProcessRuntimeDriver> logger,
        IGpuStartMeter? gpuStartMeter = null,
        IGpuInventory? gpuInventory = null,
        TimeProvider? timeProvider = null,
        UpstreamChanges? upstreamChanges = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(upstreamHttp);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _options.Validate();
        _host = host;
        _store = store;
        _catalog = catalog;
        _keys = keys;
        _ports = ports;
        _meter = gpuStartMeter;
        _gpus = gpuInventory;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _upstreams = upstreamChanges;
        _logs = new ProcessLogFiles(_options);
        _probe = new ProcessHealthProbe(upstreamHttp, _options.ProbeTimeout);
        _budget = RestartBudget.From(_options);
    }

    /// <inheritdoc />
    public LaunchDelivery Delivery => LaunchDelivery.Process;

    /// <summary>
    /// True: this driver accepts <c>spec.env</c>/<c>spec.secretEnvNames</c> and keeps secret values out of
    /// <c>launch-config</c>, the record and the logs (MN-211, feature <c>launch.env</c>). Mirrors
    /// <c>ContainerRuntimeDriver.AcceptsEnvironment</c>, which the capability contributor reads.
    /// </summary>
    public static bool AcceptsEnvironment => true;

    /// <summary>
    /// True when this driver selects GPUs by UUID (it has the node's GPU inventory, so <see cref="CheckSpec"/> refuses a
    /// UUID that is not on the node with 422 <c>gpu_missing</c>): the node then reports <c>gpus.uuids</c>, without which
    /// the master's placement validator refuses every GPU placement on it.
    /// </summary>
    public bool SelectsGpusByUuid => _gpus is not null;

    /// <summary>What the last <see cref="RecoverAsync"/> did (null before it ran).</summary>
    public ProcessRecoveryReport? LastRecovery { get; private set; }

    /// <summary>The job label of <paramref name="modelId"/>'s instance.</summary>
    public static string JobNameFor(string modelId) => JobNamePrefix + modelId;

    /// <inheritdoc />
    public void CheckSpec(string modelId, LaunchSpec spec)
    {
        var plan = ProcessLaunchValidator.Validate(modelId, spec, _options, _catalog);
        if (plan.GpuUuids is { } uuids && _gpus is not null)
        {
            CheckGpusPresent(uuids);
        }
    }

    // ------------------------------------------------------------------ recovery (adoption + orphan sweep)

    /// <summary>
    /// Adopts the processes of <c>processes.json</c> and sweeps orphans, once per driver (later calls return at once).
    /// Fast enough for the pre-<c>RunAsync</c> phase (file reads, <see cref="IProcessHost.OpenAsync"/>, the candidate list
    /// and its command lines; §A.4.1): the Ready probes of adopted processes run in the background. A
    /// <c>processes.json</c> that cannot be read throws <see cref="InvalidDataException"/> and is left as it is.
    /// </summary>
    public async Task<ProcessRecoveryReport> RecoverAsync(CancellationToken ct = default)
    {
        await _recoverGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_recovered && LastRecovery is { } done)
            {
                return done;
            }

            var records = await _store.ListAsync(ct).ConfigureAwait(false);
            var pending = await _store.ListPendingAsync(ct).ConfigureAwait(false);
            var adopted = new List<string>();
            var gone = new List<string>();
            foreach (var record in records)
            {
                if (await AdoptAsync(record, ct).ConfigureAwait(false))
                {
                    adopted.Add(record.ModelId);
                }
                else
                {
                    gone.Add(record.ModelId);
                }
            }

            var (killed, foreign, unlisted) = await SweepAsync(records, pending, ct).ConfigureAwait(false);

            // Only a pending start the sweep could actually look for is resolved (MN-239, D129 (7f)). This used to
            // remove every entry unconditionally while the sweep skipped a folder whose listing had failed, so the
            // orphan that entry existed to find was forgotten for good — holding its port and its VRAM until someone
            // noticed. An entry whose executable folder could not be listed stays, and the next recovery looks again.
            var resolved = new List<string>(pending.Count);
            foreach (var entry in pending)
            {
                if (Path.GetDirectoryName(entry.ExecutablePath) is { Length: > 0 } folder && unlisted.Contains(folder))
                {
                    ProcessRuntimeLog.PendingKept(_logger, entry.ModelId, folder);
                    continue;
                }

                await _store.RemovePendingAsync(entry.ModelId, ct).ConfigureAwait(false);
                resolved.Add(entry.ModelId);
            }

            var report = new ProcessRecoveryReport(adopted, gone, killed, foreign, resolved);
            LastRecovery = report;
            _recovered = true;
            return report;
        }
        finally
        {
            _recoverGate.Release();
        }
    }

    private Task EnsureRecoveredAsync(CancellationToken ct) =>
        _recovered ? Task.CompletedTask : RecoverAsync(ct);

    private async Task<bool> AdoptAsync(ProcessRecord record, CancellationToken ct)
    {
        if (_instances.ContainsKey(record.ModelId))
        {
            return true; // adopted by an earlier recovery attempt that failed later: never a second job and supervisor
        }

        IHostedProcess? process;
        try
        {
            process = await _host.OpenAsync(record.Process, record.JobName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // MN-219: the identity checks passed but the host could not supervise it (the Windows host throws
            // Win32Exception when the new job cannot be made or assigned). Never abort recovery (it runs before
            // RunAsync, §A.4.1): drop the record; the sweep that follows still matches its digest and ends the process,
            // so the master starts the model again under supervision.
            ProcessRuntimeLog.AdoptFailed(_logger, ex, record.ModelId, record.Process.Pid);
            await _store.RemoveAsync(record.ModelId, ct).ConfigureAwait(false);
            return false;
        }

        if (process is null)
        {
            ProcessRuntimeLog.RecordGone(_logger, record.ModelId, record.Process.Pid);
            await _store.RemoveAsync(record.ModelId, ct).ConfigureAwait(false);
            return false;
        }

        string apiKey;
        try
        {
            apiKey = _keys.Unprotect(record.ProtectedApiKey);
        }
        catch (CryptographicException)
        {
            // Without its key the proxy and the probe cannot reach the instance: it is ours (identity checked), so end it.
            ProcessRuntimeLog.Failed(_logger, record.ModelId, "its API key cannot be unprotected; the process was stopped");
            await process.KillTreeAsync(ct).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            await _store.RemoveAsync(record.ModelId, ct).ConfigureAwait(false);
            return false;
        }

        var now = _time.GetUtcNow();
        var instance = new Instance(record.ModelId, record, process, apiKey, AdoptedAlias(record), secretValues: null)
        {
            State = ModelState.Starting,
            Since = now,
            AttemptStartedAt = now,
        };
        _instances[record.ModelId] = instance;
        _upstreams?.Changed(record.ModelId);
        ProcessRuntimeLog.Adopted(_logger, record.ModelId, record.Process.Pid, record.Process.StartTime, process.JobName);
        StartSupervision(instance);
        return true;
    }

    /// <summary>
    /// The orphan sweep (D121): candidates under each version folder that run as the agent's account, killed only when
    /// their arguments' digest equals a record's or a pending start's and they started after that entry was written.
    /// </summary>
    private async Task<(IReadOnlyList<int> Killed, IReadOnlyList<int> Foreign, IReadOnlySet<string> UnlistedFolders)> SweepAsync(
        IReadOnlyList<ProcessRecord> records, IReadOnlyList<PendingProcessStart> pending, CancellationToken ct)
    {
        var markers = records.Select(r => (r.ArgvDigest, At: r.StartedAt, Kind: "the record", r.ModelId))
            .Concat(pending.Select(p => (p.ArgvDigest, p.At, Kind: "a pending start", p.ModelId)))
            .ToList();
        var folders = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var record in records)
        {
            AddFolder(folders, record.Process.ImagePath);
        }

        foreach (var entry in pending)
        {
            AddFolder(folders, entry.ExecutablePath);
        }

        foreach (var state in _catalog.List())
        {
            if (!state.Verified)
            {
                continue;
            }

            try
            {
                folders.Add(_catalog.Resolve(state.Name, state.Version).Folder);
            }
            catch (AgentException)
            {
                // Became unverified in between: its folder is still covered by the records and pending starts, if any.
            }
        }

        var adopted = _instances.Values.Select(i => i.Record.Process).ToHashSet();
        var killed = new List<int>();
        var foreign = new List<int>();
        var unlisted = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var seen = new HashSet<ProcessIdentity>();
        foreach (string folder in folders)
        {
            IReadOnlyList<ProcessIdentity> candidates;
            try
            {
                candidates = await _host.ListCandidatesAsync(folder, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The caller needs to know: a pending start whose executable lives here was not looked for, so it must
                // not be marked resolved (MN-239, D129 (7f)).
                ProcessRuntimeLog.SweepFailed(_logger, ex, folder);
                unlisted.Add(folder);
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (!seen.Add(candidate) || adopted.Contains(candidate))
                {
                    continue;
                }

                IReadOnlyList<string>? arguments;
                try
                {
                    arguments = await _host.ReadArgumentsAsync(candidate, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unreadable candidate never aborts recovery (MN-219); it is not provably ours, so it is left alone.
                    ProcessRuntimeLog.Foreign(_logger, candidate.Pid, candidate.ImagePath, "its command line cannot be read: " + ex.Message);
                    foreign.Add(candidate.Pid);
                    continue;
                }

                if (arguments is null)
                {
                    ProcessRuntimeLog.Foreign(_logger, candidate.Pid, candidate.ImagePath, "its command line cannot be read");
                    foreign.Add(candidate.Pid);
                    continue;
                }

                string digest = ProcessArgvDigest.Compute(arguments);
                var match = markers.FirstOrDefault(m =>
                    string.Equals(m.ArgvDigest, digest, StringComparison.Ordinal) && candidate.StartTime >= m.At);
                if (match.ModelId is null)
                {
                    ProcessRuntimeLog.Foreign(_logger, candidate.Pid, candidate.ImagePath, "its command line is not one this agent started");
                    foreign.Add(candidate.Pid);
                    continue;
                }

                // Supervision of adopted instances already runs: a crash restarted meanwhile has the record's digest and
                // a later start, but it is the instance's own process now (MN-219).
                if (_instances.Values.Any(i => i.Process is { } current && current.Identity == candidate))
                {
                    continue;
                }

                // That check alone has a window, which is what made the MN-219 test flaky on a loaded machine
                // (MN-239 bug F). RestartAsync commits the new record *before* it swaps the instance's in-memory
                // process, so for an instant the record on disk names the restarted PID while _instances still names
                // the one that exited — and the sweep would then end a process it had just started. The records this
                // sweep matches against were read when recovery began, so re-read this model's own.
                if (await _store.GetAsync(match.ModelId, ct).ConfigureAwait(false) is { } fresh && fresh.Process == candidate)
                {
                    continue;
                }

                bool ended;
                try
                {
                    ended = await _host.KillAsync(candidate, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A kill that fails (Win32Exception, TimeoutException) is logged; the sweep goes on (MN-219).
                    ProcessRuntimeLog.OrphanKillFailed(_logger, ex, candidate.Pid, candidate.ImagePath, match.ModelId);
                    continue;
                }

                if (ended)
                {
                    ProcessRuntimeLog.OrphanKilled(_logger, candidate.Pid, candidate.ImagePath, match.Kind, match.ModelId);
                    killed.Add(candidate.Pid);
                }
            }
        }

        return (killed, foreign, unlisted);
    }

    private static void AddFolder(HashSet<string> folders, string? executablePath)
    {
        if (!string.IsNullOrEmpty(executablePath) && Path.GetDirectoryName(executablePath) is { Length: > 0 } folder)
        {
            folders.Add(folder);
        }
    }

    // ------------------------------------------------------------------ IRuntimeDriver

    /// <inheritdoc />
    public async Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default)
    {
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        return [.. _instances.Values.OrderBy(i => i.ModelId, StringComparer.Ordinal).Select(Status)];
    }

    /// <inheritdoc />
    public async Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        return _instances.TryGetValue(modelId, out var instance) ? Status(instance) : null;
    }

    /// <inheritdoc />
    public async Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentException.ThrowIfNullOrEmpty(launch.OperationId);
        var plan = ProcessLaunchValidator.Validate(launch.ModelId, launch.Spec, _options, _catalog);
        if (plan.GpuUuids is { } uuids && _gpus is { Last: not null })
        {
            CheckGpusPresent(uuids);
        }

        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        var gate = ModelLock(launch.ModelId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_instances.TryGetValue(launch.ModelId, out var existing))
            {
                var current = Status(existing);
                if (launch.Mode == LaunchMode.EnsureStarted)
                {
                    // Stopping is set only by a stop that holds this lock: seen here, that stop was interrupted (MN-219).
                    if (current.State is ModelState.Ready or ModelState.Starting)
                    {
                        return new RuntimeLaunchResult(RuntimeLaunchOutcome.AlreadyRunning, current);
                    }

                    // A Failed instance left behind: clear it (and its record) before starting again.
                    await stages.BeforeStepAsync(OperationStages.Removed, ct).ConfigureAwait(false);
                    await RemoveInstanceAsync(existing, ct).ConfigureAwait(false);
                    await stages.StageReachedAsync(OperationStages.Removed, null, ct).ConfigureAwait(false);
                }
                else
                {
                    await StopAndRemoveAsync(existing, stages, ct).ConfigureAwait(false);
                }
            }

            return await CreateAsync(launch, plan, stages, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>A process is created and started in one host call, so there is no "created, not started" state: this
    /// returns the instance as it is, or 404 when there is none.</remarks>
    public async Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        return _instances.TryGetValue(modelId, out var instance)
            ? Status(instance)
            : throw new AgentException(404, new AgentError
            {
                Code = ErrorCodes.NotFound,
                Message = $"Model '{modelId}' has no instance on this node.",
            });
    }

    /// <inheritdoc />
    public async Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(stages);
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        var gate = ModelLock(modelId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_instances.TryGetValue(modelId, out var instance))
            {
                await StopAndRemoveAsync(instance, stages, ct).ConfigureAwait(false);
            }
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
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        if (!_instances.TryGetValue(modelId, out var instance))
        {
            return null;
        }

        var record = instance.Record;
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in record.Spec.Env)
        {
            env[name] = value;
        }

        foreach (string secret in record.Spec.SecretEnvNames)
        {
            env[secret] = null;
        }

        var labels = Labels(record);
        return new LaunchConfig
        {
            Kind = LaunchDelivery.Process,
            Executable = record.ExecutableName,
            ExecutableVersion = record.ExecutableVersion,
            Argv = [.. record.Spec.Argv],
            Env = env,
            Mounts = [],
            Gpus = GpuRefs(record),
            Labels = labels,
            ExecutionFingerprint = labels.GetValueOrDefault(ContainerLabels.ExecutionFingerprint),
            CreatedAt = record.StartedAt,
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamLogsAsync(
        string modelId, int tail, bool follow, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        if (!_instances.TryGetValue(modelId, out var instance))
        {
            yield break;
        }

        await foreach (string line in ProcessLogFiles.StreamAsync(
            instance.Record.LogPath, tail, follow, LogFollowInterval,
            () => _instances.TryGetValue(modelId, out var now) && ReferenceEquals(now, instance), ct).ConfigureAwait(false))
        {
            yield return line;
        }
    }

    /// <inheritdoc />
    public async Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        return _instances.TryGetValue(modelId, out var instance) &&
               instance.State is ModelState.Starting or ModelState.Ready &&
               instance.Process is { HasExited: false }
            ? ProcessHealthProbe.BaseAddress(_options.UpstreamHost, instance.Record.Port)
            : null;
    }

    /// <inheritdoc />
    public async Task<string?> GetUpstreamApiKeyAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        return _instances.TryGetValue(modelId, out var instance) ? instance.ApiKey : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// An MLX instance: <c>model</c> becomes <see cref="MlxFlags.DefaultModel"/> (the folder it was started with) and
    /// <see cref="MlxFlags.RemovedRequestFields"/> are dropped, so a client can never make the node load another folder.
    /// </remarks>
    public async Task<UpstreamBodyRewrite?> GetUpstreamBodyRewriteAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        return _instances.TryGetValue(modelId, out var instance) && instance.Record.Spec.Runtime == RuntimeKind.Mlx
            ? new UpstreamBodyRewrite(MlxFlags.DefaultModel, MlxFlags.RemovedRequestFields,
                instance.Record.Spec.Labels.GetValueOrDefault(ContainerLabels.ServedName))
            : null;
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

        await EnsureRecoveredAsync(ct).ConfigureAwait(false);
        string root = $"{LogicalVolumes.ModelsToken}/{folder}";
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return _instances.Values.Any(i => i.Record.Spec.Argv.Any(a =>
            string.Equals(a, root, comparison) || a.StartsWith(root + "/", comparison)));
    }

    /// <summary>Stops supervising (the processes keep running: they outlive the agent) and releases the host handles.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var instance in _instances.Values)
        {
            await instance.Cancellation.CancelAsync().ConfigureAwait(false);
            if (instance.Loop is { } loop)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (instance.Process is { } process)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }

            instance.Cancellation.Dispose();
        }

        _recoverGate.Dispose();
    }

    // ------------------------------------------------------------------ create / stop

    private async Task<RuntimeLaunchResult> CreateAsync(RuntimeLaunch launch, ProcessLaunchPlan plan, IRuntimeStageSink stages, CancellationToken ct)
    {
        string id = launch.ModelId;
        int preferred = launch.HostPort > 0
            ? launch.HostPort
            : await _ports.AllocatePortAsync(id, launch.OperationId, ct).ConfigureAwait(false);
        if (preferred is < 1 or > 65535)
        {
            throw new InvalidOperationException($"Host port {preferred} for model '{id}' is out of range.");
        }

        var avoid = _instances.Values.Where(i => !string.Equals(i.ModelId, id, StringComparison.Ordinal))
            .Select(i => i.Record.Port).ToHashSet();
        int port = ProcessPorts.Choose(preferred, avoid);
        if (port != preferred)
        {
            ProcessRuntimeLog.PortSkipped(_logger, id, preferred, port);
        }

        string apiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var secretValues = plan.SecretEnvNames.ToDictionary(n => n, n => launch.Spec.Env[n], StringComparer.Ordinal);
        GpuStartMark? mark = null;
        if (_meter is not null && plan.GpuUuids is { } uuids)
        {
            try
            {
                mark = await _meter.MarkAsync(uuids, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ProcessRuntimeLog.MeasureFailed(_logger, ex, id);
            }
        }

        // Fencing (and, before create, admission) is re-checked right before the destructive step. Create and start are
        // one host call, so both checks come first.
        await stages.BeforeStepAsync(OperationStages.Created, ct).ConfigureAwait(false);
        await stages.BeforeStepAsync(OperationStages.Started, ct).ConfigureAwait(false);

        // Protected before the create (MN-219): a failure here must not leave a running process without a record.
        string protectedKey = _keys.Protect(apiKey);
        var now = _time.GetUtcNow();
        var started = await StartProcessAsync(id, launch.OperationId, plan, port, apiKey, secretValues, ct).ConfigureAwait(false);
        var record = new ProcessRecord
        {
            ModelId = id,
            OperationId = launch.OperationId,
            Spec = StoredSpec(launch.Spec),
            Process = started.Process.Identity,
            ExecutableName = plan.Executable.Name,
            ExecutableVersion = plan.Executable.Version,
            Port = port,
            ArgvDigest = started.Digest,
            ProtectedApiKey = protectedKey,
            JobName = started.Process.JobName,
            LogPath = started.LogPath,
            Gpus = GpuRefsFor(plan.GpuUuids),
            StartedAt = now,
        };
        await CommitAsync(record, started.Process, ct).ConfigureAwait(false);

        var instance = new Instance(id, record, started.Process, apiKey, plan.Alias, secretValues)
        {
            State = ModelState.Starting,
            Since = now,
            AttemptStartedAt = now,
            LogStart = started.LogStart,
            Mark = mark,
        };
        _instances[id] = instance;
        _upstreams?.Changed(id);
        ProcessRuntimeLog.Started(_logger, id, plan.Executable.Name, plan.Executable.Version, record.Process.Pid, port, launch.OperationId);
        StartSupervision(instance);

        await stages.StageReachedAsync(OperationStages.Created, null, ct).ConfigureAwait(false);
        await stages.StageReachedAsync(OperationStages.Started, null, ct).ConfigureAwait(false);
        return new RuntimeLaunchResult(RuntimeLaunchOutcome.Created, Status(instance));
    }

    /// <summary>
    /// The pending start, the create, and nothing else (the caller commits the record): a failed create removes the
    /// pending start again, so only a crash between create and commit leaves one for the orphan sweep.
    /// </summary>
    private async Task<(IHostedProcess Process, string Digest, string LogPath, long LogStart)> StartProcessAsync(
        string modelId, string operationId, ProcessLaunchPlan plan, int port, string apiKey,
        IReadOnlyDictionary<string, string>? secretValues, CancellationToken ct)
    {
        var arguments = plan.ArgumentsFor(port);
        string digest = ProcessArgvDigest.Compute(arguments);
        string logPath = _logs.PrepareForStart(modelId);
        long logStart = ProcessLogFiles.LengthOf(logPath);
        var environment = BuildEnvironment(plan, apiKey, secretValues);
        await _store.AddPendingAsync(new PendingProcessStart
        {
            ModelId = modelId,
            OperationId = operationId,
            ExecutablePath = plan.Executable.Path,
            Port = port,
            ArgvDigest = digest,
            At = _time.GetUtcNow(),
        }, ct).ConfigureAwait(false);

        try
        {
            var process = await _host.StartAsync(new ProcessHostStart
            {
                ExecutablePath = plan.Executable.Path,
                Arguments = arguments,
                Environment = environment,
                WorkingDirectory = plan.Executable.Folder,
                LogPath = logPath,
                JobName = JobNameFor(modelId),
            }, ct).ConfigureAwait(false);
            return (process, digest, logPath, logStart);
        }
        catch
        {
            await _store.RemovePendingAsync(modelId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Replaces the pending start with the record; when that write fails, the process is ended (it could not be found again).</summary>
    private async Task CommitAsync(ProcessRecord record, IHostedProcess process, CancellationToken ct)
    {
        try
        {
            await _store.CommitPendingAsync(record, ct).ConfigureAwait(false);
        }
        catch
        {
            await process.KillTreeAsync(CancellationToken.None).ConfigureAwait(false);
            await process.DisposeAsync().ConfigureAwait(false);
            try
            {
                await _store.RemovePendingAsync(record.ModelId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // The pending start stays; the next sweep finds no process for it and removes it.
            }

            throw;
        }
    }

    /// <summary>
    /// The child's complete environment block (design-p3p4 §A.1.3): the spec's variables, then the agent's own names,
    /// which the validator keeps out of the spec. Nothing is inherited from the agent.
    /// </summary>
    private Dictionary<string, string> BuildEnvironment(ProcessLaunchPlan plan, string apiKey, IReadOnlyDictionary<string, string>? secretValues)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in plan.Environment)
        {
            env[name] = secretValues is not null && secretValues.TryGetValue(name, out string? secret) ? secret : value;
        }

        if ((_options.SystemRoot ?? Environment.GetEnvironmentVariable(ProcessEnvironment.SystemRoot)) is { Length: > 0 } systemRoot)
        {
            env[ProcessEnvironment.SystemRoot] = systemRoot;
        }

        string temp = Path.Combine(_options.CacheRoot, "tmp");
        string cudaCache = Path.Combine(_options.CacheRoot, "cuda");
        Directory.CreateDirectory(temp);
        if (!OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(cudaCache);
        }
        env[ProcessEnvironment.Temp] = temp;
        env[ProcessEnvironment.Tmp] = temp;
        env[ProcessEnvironment.Path] = plan.Executable.Folder;
        if (OperatingSystem.IsMacOS())
        {
            // Apple Silicon (docs/macos/implementation-plan.md, Phase 2): Metal, no CUDA. TMPDIR is the temp folder there, and
            // the system tools a runtime may call (llama.cpp none, Python's MLX a few) live in /usr/bin and /bin.
            env[ProcessEnvironment.TmpDir] = temp;
            env[ProcessEnvironment.Path] = plan.Executable.Folder + ":/usr/bin:/bin";
            if (plan.Runtime == RuntimeKind.Mlx)
            {
                // mlx_lm.server has no API key (its port is loopback-only, plan M4). The Hugging Face libraries must never
                // download (a request's model field would otherwise make the node fetch a repo, spike S4) and keep their
                // cache and any home-folder writes in the agent's cache root.
                string hfHome = Path.Combine(_options.CacheRoot, "hf");
                string home = Path.Combine(_options.CacheRoot, "home");
                // hub/ must exist: /v1/models scans it, and a missing one makes that request fail (CacheNotFound), which
                // the Ready check reads as "not up".
                Directory.CreateDirectory(Path.Combine(hfHome, "hub"));
                Directory.CreateDirectory(home);
                env[ProcessEnvironment.HfHubOffline] = "1";
                env[ProcessEnvironment.TransformersOffline] = "1";
                env[ProcessEnvironment.HfHubDisableTelemetry] = "1";
                env[ProcessEnvironment.HfHome] = hfHome;
                env[ProcessEnvironment.Home] = home;
                return env;
            }

            env[ProcessEnvironment.LlamaApiKey] = apiKey;
            return env;
        }

        env[ProcessEnvironment.CudaCachePath] = cudaCache;
        env[ProcessEnvironment.CudaCacheMaxSize] = ProcessEnvironment.CudaCacheMaxBytes.ToString(CultureInfo.InvariantCulture);
        switch (plan.GpuMode)
        {
            case GpuSelectionMode.Uuids:
                env[ProcessEnvironment.CudaVisibleDevices] = string.Join(',', plan.GpuUuids!);
                break;
            case GpuSelectionMode.None:
                env[ProcessEnvironment.CudaVisibleDevices] = "-1";
                break;
        }

        env[ProcessEnvironment.LlamaApiKey] = apiKey;
        return env;
    }

    /// <summary>The spec as stored in the record: secret values removed (names kept in <c>secretEnvNames</c>).</summary>
    private static LaunchSpec StoredSpec(LaunchSpec spec)
    {
        if (spec.SecretEnvNames.Count == 0)
        {
            return spec;
        }

        var secrets = new HashSet<string>(spec.SecretEnvNames, StringComparer.Ordinal);
        return spec with { Env = spec.Env.Where(e => !secrets.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal) };
    }

    private async Task StopAndRemoveAsync(Instance instance, IRuntimeStageSink stages, CancellationToken ct)
    {
        // The fencing re-check comes before anything is ended.
        await stages.BeforeStepAsync(OperationStages.Stopping, ct).ConfigureAwait(false);
        lock (instance.Sync)
        {
            instance.Stopping = true;
            instance.State = ModelState.Stopping;
            instance.Since = _time.GetUtcNow();
        }

        await stages.StageReachedAsync(OperationStages.Stopping, null, ct).ConfigureAwait(false);
        await instance.Cancellation.CancelAsync().ConfigureAwait(false);
        if (instance.Process is { HasExited: false } process)
        {
            try
            {
                await process.KillTreeAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // MN-219: a kill that threw or was cancelled leaves nothing supervising the instance; Failed (not
                // Stopping) lets the next stop or EnsureStarted remove it (the remove kills it again).
                SetFailed(instance, "The stop was interrupted before the process ended; stop or start it again.");
                throw;
            }
        }

        try
        {
            await stages.BeforeStepAsync(OperationStages.Removed, ct).ConfigureAwait(false);
        }
        catch
        {
            // Superseded after the kill: the process is gone, the record stays until the next stop or launch.
            SetFailed(instance, "The instance was stopped, but its removal was interrupted.");
            throw;
        }

        await RemoveInstanceAsync(instance, ct).ConfigureAwait(false);
        await stages.StageReachedAsync(OperationStages.Removed, null, ct).ConfigureAwait(false);
    }

    /// <summary>Ends the instance's process if it still runs, removes its record and forgets it.</summary>
    private async Task RemoveInstanceAsync(Instance instance, CancellationToken ct)
    {
        lock (instance.Sync)
        {
            instance.Stopping = true;
        }

        await instance.Cancellation.CancelAsync().ConfigureAwait(false);
        if (instance.Process is { HasExited: false } running)
        {
            await running.KillTreeAsync(ct).ConfigureAwait(false);
        }

        await _store.RemoveAsync(instance.ModelId, ct).ConfigureAwait(false);
        _instances.TryRemove(new KeyValuePair<string, Instance>(instance.ModelId, instance));
        // Before the kill is not enough: the proxy may cache a positive answer between the signal and the removal.
        _upstreams?.Changed(instance.ModelId);
        if (instance.Process is { } process)
        {
            await process.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ supervision

    private void StartSupervision(Instance instance)
    {
        var ct = instance.Cancellation.Token;
        instance.Loop = Task.Run(() => SuperviseAsync(instance, ct), CancellationToken.None);
    }

    private async Task SuperviseAsync(Instance instance, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var process = instance.Process!;
                var exit = process.WaitForExitAsync(ct);
                if (instance.State == ModelState.Starting)
                {
                    var outcome = await WaitReadyAsync(instance, process, exit, ct).ConfigureAwait(false);
                    if (outcome == ReadyOutcome.TimedOut)
                    {
                        await FailAfterTimeoutAsync(instance, process, ct).ConfigureAwait(false);
                        return;
                    }

                    if (outcome == ReadyOutcome.Ready)
                    {
                        if (await BrokenBackendAsync(instance, ct).ConfigureAwait(false) is { } broken)
                        {
                            await process.KillTreeAsync(ct).ConfigureAwait(false);
                            SetFailed(instance, broken);
                            return;
                        }

                        await MarkReadyAsync(instance, ct).ConfigureAwait(false);
                    }
                }

                // While it runs, its log is kept under the size limit by copy and truncate (MN-219, D128).
                while (!exit.IsCompleted)
                {
                    await Task.WhenAny(exit, Task.Delay(_options.HealthPollInterval, _time, ct)).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (!exit.IsCompleted)
                    {
                        _logs.RotateWhileRunning(instance.Record.LogPath);
                    }
                }

                int? exitCode = await exit.ConfigureAwait(false);
                if (ct.IsCancellationRequested || instance.Stopping)
                {
                    return;
                }

                if (!await HandleExitAsync(instance, exitCode, ct).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ProcessRuntimeLog.SupervisionFailed(_logger, ex, instance.ModelId);
            SetFailed(instance, "Supervision of the process failed: " + ex.Message);
        }
    }

    private enum ReadyOutcome
    {
        Ready,
        Exited,
        TimedOut,
    }

    private async Task<ReadyOutcome> WaitReadyAsync(Instance instance, IHostedProcess process, Task<int?> exit, CancellationToken ct)
    {
        var baseAddress = ProcessHealthProbe.BaseAddress(_options.UpstreamHost, instance.Record.Port);
        var deadline = instance.AttemptStartedAt + _options.StartTimeout;
        bool mismatchLogged = false;
        while (true)
        {
            if (exit.IsCompleted || process.HasExited)
            {
                return ReadyOutcome.Exited;
            }

            var result = await _probe.ProbeAsync(baseAddress, instance.Alias, instance.ApiKey, ct).ConfigureAwait(false);
            if (result == ProcessProbeResult.Ready)
            {
                return ReadyOutcome.Ready;
            }

            if (result is ProcessProbeResult.WrongKey or ProcessProbeResult.AliasMissing && !mismatchLogged)
            {
                ProcessRuntimeLog.ProbeMismatch(_logger, instance.ModelId, instance.Record.Port, result);
                mismatchLogged = true;
            }

            var left = deadline - _time.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                return exit.IsCompleted ? ReadyOutcome.Exited : ReadyOutcome.TimedOut;
            }

            var delay = Task.Delay(left < _options.HealthPollInterval ? left : _options.HealthPollInterval, _time, ct);
            await Task.WhenAny(exit, delay).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// llama-server answers <c>/health</c> 200 even when its GPU backend failed during the warm-up (Metal: "Insufficient
    /// Memory ... kIOGPUCommandBufferCallbackErrorOutOfMemory", then "backend is in error state"), and every request then
    /// fails. The Failed text for such a start, from the lines this start wrote; null when they show no such failure.
    /// Adopted processes (no start offset) are not checked: they were Ready before.
    /// </summary>
    private async Task<string?> BrokenBackendAsync(Instance instance, CancellationToken ct)
    {
        if (instance.LogStart is not { } from)
        {
            return null;
        }

        var lines = await ProcessLogFiles.ReadTailAsync(instance.Record.LogPath, from, _options.FailureLogLines, ct).ConfigureAwait(false);
        return BackendFailure(lines);
    }

    /// <summary>The Failed text when <paramref name="lines"/> show a failed GPU backend, else null.</summary>
    internal static string? BackendFailure(IReadOnlyList<string> lines)
    {
        string? cause = null;
        bool broken = false;
        foreach (string line in lines)
        {
            if (GpuOutOfMemoryLine().IsMatch(line))
            {
                cause ??= LogLevelPrefix().Replace(line, "").Trim();
                broken = true;
            }
            else if (BrokenBackendLine().IsMatch(line))
            {
                broken = true;
            }
        }

        if (!broken)
        {
            return null;
        }

        string headline = cause is null
            ? "llama-server's GPU backend failed while it loaded the model, so it cannot answer requests; it was stopped."
            : "The GPU ran out of memory while llama-server loaded the model, so it cannot answer requests; it was stopped. " +
              "Lower the context size or the parallel slots, or free memory on the node.";
        var tail = lines.Skip(Math.Max(0, lines.Count - 40)).ToList();
        string text = $"{FailureReport.CausePrefix}{cause ?? headline}\n\n{headline}";
        return tail.Count == 0 ? text : $"{text}\n\n{string.Join('\n', tail)}";
    }

    [GeneratedRegex(@"kIOGPUCommandBufferCallbackErrorOutOfMemory|cudaMalloc failed: out of memory", RegexOptions.CultureInvariant)]
    private static partial Regex GpuOutOfMemoryLine();

    [GeneratedRegex(@"backend is in error state|ggml_backend_sched_graph_compute_async failed", RegexOptions.CultureInvariant)]
    private static partial Regex BrokenBackendLine();

    // llama-server's "0.24.312.385 E " timestamp and level before the message.
    [GeneratedRegex(@"^\s*(\d+\.\d+\.\d+\.\d+\s+)?[EWID]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex LogLevelPrefix();

    private async Task MarkReadyAsync(Instance instance, CancellationToken ct)
    {
        var gate = ModelLock(instance.ModelId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (instance.Stopping || !IsCurrent(instance))
            {
                return;
            }

            var record = instance.Record;
            double? measured = record.MeasuredGpuGiB;
            if (instance.Mark is { } mark && _meter is not null)
            {
                try
                {
                    measured = await _meter.MeasureAsync(mark, ct).ConfigureAwait(false) ?? measured;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ProcessRuntimeLog.MeasureFailed(_logger, ex, instance.ModelId);
                }

                instance.Mark = null;
            }

            var now = _time.GetUtcNow();
            if (record.ReadyAt is null || measured != record.MeasuredGpuGiB)
            {
                var ready = record with { ReadyAt = record.ReadyAt ?? now, MeasuredGpuGiB = measured };
                try
                {
                    await _store.UpsertAsync(ready, ct).ConfigureAwait(false);
                    record = ready;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // MN-239, D129 (7e). This write is a processes.json rewrite. It used to throw out of MarkReadyAsync
                    // into SuperviseAsync's catch-all, which called SetFailed: llama-server was alive, probed Ready and
                    // serving, and the master was told it had failed — and supervision had exited. A record that could
                    // not be written costs an adoption after a restart, not the instance. Say so, and keep serving.
                    ProcessRuntimeLog.ReadyNotRecorded(_logger, instance.ModelId, record.Port, ex.Message);
                }
            }

            lock (instance.Sync)
            {
                instance.Record = record;
                instance.State = ModelState.Ready;
                instance.Since = now;
                instance.Error = null;
            }

            ProcessRuntimeLog.Ready(_logger, instance.ModelId, record.Process.Pid, record.Port);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task FailAfterTimeoutAsync(Instance instance, IHostedProcess process, CancellationToken ct)
    {
        await process.KillTreeAsync(ct).ConfigureAwait(false);
        var lines = await ProcessLogFiles.ReadTailAsync(instance.Record.LogPath, _options.FailureLogLines, ct).ConfigureAwait(false);
        string minutes = _options.StartTimeout.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture);
        SetFailed(instance, BuildFailure($"llama-server did not become Ready within {minutes} min and was stopped.", lines));
    }

    /// <summary>An unexpected exit: restart on the same port while the budget allows (true), else Failed (false).</summary>
    private async Task<bool> HandleExitAsync(Instance instance, int? exitCode, CancellationToken ct)
    {
        var gate = ModelLock(instance.ModelId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (instance.Stopping || !IsCurrent(instance))
            {
                return false;
            }

            var record = instance.Record;
            var lines = await ProcessLogFiles.ReadTailAsync(record.LogPath, _options.FailureLogLines, ct).ConfigureAwait(false);
            string code = exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            bool wasReady = record.ReadyAt is not null;
            var restartTimes = _budget.TryRestart(wasReady, record.RestartTimes, _time.GetUtcNow());
            if (restartTimes is null)
            {
                string headline = wasReady
                    ? $"llama-server exited with code {code} after it was Ready; the restart budget ({_budget.Budget} in " +
                      $"{_budget.Window.TotalMinutes.ToString("0.##", CultureInfo.InvariantCulture)} min) is used up."
                    : $"llama-server exited with code {code} before it was Ready.";
                SetFailed(instance, BuildFailure(headline, lines));
                return false;
            }

            if (instance.SecretValues is null && record.Spec.SecretEnvNames.Count > 0)
            {
                SetFailed(instance, BuildFailure(
                    $"llama-server exited with code {code}; it cannot be restarted because its secret environment values are not " +
                    "kept across an agent restart. Start the model again.", lines));
                return false;
            }

            ProcessRuntimeLog.Restarting(_logger, instance.ModelId, record.Process.Pid, exitCode, restartTimes.Count, _budget.Budget, record.Port);
            try
            {
                await RestartAsync(instance, restartTimes, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SetFailed(instance, BuildFailure($"llama-server exited with code {code} and could not be restarted: {ex.Message}", lines));
                return false;
            }

            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>A new process for the same instance on the same port, with the same key (not a new operation).</summary>
    private async Task RestartAsync(Instance instance, IReadOnlyList<DateTimeOffset> restartTimes, CancellationToken ct)
    {
        var record = instance.Record;
        var spec = record.Spec with
        {
            ExecutableVersion = record.ExecutableVersion,
            Env = MergeSecrets(record.Spec.Env, instance.SecretValues),
        };
        var plan = ProcessLaunchValidator.Validate(record.ModelId, spec, _options, _catalog);
        var started = await StartProcessAsync(record.ModelId, record.OperationId, plan, record.Port, instance.ApiKey, instance.SecretValues, ct)
            .ConfigureAwait(false);
        var next = record with
        {
            Process = started.Process.Identity,
            ArgvDigest = started.Digest,
            JobName = started.Process.JobName,
            LogPath = started.LogPath,
            ExecutableVersion = plan.Executable.Version,
            RestartCount = record.RestartCount + 1,
            RestartTimes = restartTimes,
        };
        await CommitAsync(next, started.Process, ct).ConfigureAwait(false);

        var old = instance.Process;
        var now = _time.GetUtcNow();
        lock (instance.Sync)
        {
            instance.Record = next;
            instance.Process = started.Process;
            instance.State = ModelState.Starting;
            instance.Since = now;
            instance.AttemptStartedAt = now;
            instance.LogStart = started.LogStart;
            instance.Error = null;
        }

        if (old is not null)
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Dictionary<string, string> MergeSecrets(IReadOnlyDictionary<string, string> env, IReadOnlyDictionary<string, string>? secrets)
    {
        var merged = new Dictionary<string, string>(env, StringComparer.Ordinal);
        foreach (var (name, value) in secrets ?? new Dictionary<string, string>())
        {
            merged[name] = value;
        }

        return merged;
    }

    private void SetFailed(Instance instance, string error)
    {
        lock (instance.Sync)
        {
            instance.State = ModelState.Failed;
            instance.Since = null;
            instance.Error = error;
        }

        ProcessRuntimeLog.Failed(_logger, instance.ModelId, FirstLine(error));
    }

    private static string FirstLine(string text)
    {
        int newline = text.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? text : text[..newline];
    }

    /// <summary>
    /// The Failed text (as the container driver's: <see cref="FailureReport"/>'s <c>Cause:</c> first line, then context):
    /// the root cause from the log (an exception line, else llama-server's last error line, else the headline), the
    /// headline with the exit code, then the last 40 log lines.
    /// </summary>
    internal static string BuildFailure(string headline, IReadOnlyList<string> lines)
    {
        string cause = FailureReport.FindRootCause(lines) ?? LastErrorLine(lines) ?? headline;
        var tail = lines.Skip(Math.Max(0, lines.Count - 40)).ToList();
        string text = $"{FailureReport.CausePrefix}{cause}\n\n{headline}";
        return tail.Count == 0 ? text : $"{text}\n\n{string.Join('\n', tail)}";
    }

    private static string? LastErrorLine(IReadOnlyList<string> lines)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (ErrorLine().IsMatch(lines[i]))
            {
                return lines[i].Trim();
            }
        }

        return null;
    }

    [GeneratedRegex(@"^E |\berror\b|\bfailed\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    // ------------------------------------------------------------------ status

    private RuntimeStatus Status(Instance instance)
    {
        ModelState state;
        DateTimeOffset? since;
        string? error;
        ProcessRecord record;
        lock (instance.Sync)
        {
            state = instance.State;
            since = instance.Since;
            error = instance.Error;
            record = instance.Record;
        }

        var labels = Labels(record);
        return new RuntimeStatus
        {
            ModelId = instance.ModelId,
            State = state,
            InstanceId = record.Process.Pid.ToString(CultureInfo.InvariantCulture),
            HostPort = record.Port,
            Since = since,
            Error = error,
            RestartCount = record.RestartCount,
            Labels = labels,
            ServedName = labels.GetValueOrDefault(ContainerLabels.ServedName) ?? instance.Alias,
            ConfigHash = labels.GetValueOrDefault(ContainerLabels.ConfigHash),
            ExecutionFingerprint = labels.GetValueOrDefault(ContainerLabels.ExecutionFingerprint),
            Tuning = labels.TryGetValue(ContainerLabels.Tuning, out string? tuning) && tuning == "1",
            Gpus = GpuRefs(record),
            OperationId = record.OperationId,
            MeasuredGpuGiB = record.MeasuredGpuGiB,
            Runtime = record.Spec.Runtime,
            Delivery = LaunchDelivery.Process,
        };
    }

    /// <summary>The spec's labels plus the agent-set model, node, operation, port and GPU read-back labels.</summary>
    private Dictionary<string, string> Labels(ProcessRecord record)
    {
        var labels = new Dictionary<string, string>(record.Spec.Labels, StringComparer.Ordinal)
        {
            [ContainerLabels.Model] = record.ModelId,
            [ContainerLabels.Node] = _options.NodeId,
            [ContainerLabels.Operation] = record.OperationId,
            [ContainerLabels.Port] = record.Port.ToString(CultureInfo.InvariantCulture),
        };
        if (record.Spec.Gpus is { Mode: GpuSelectionMode.Uuids, Uuids: { Count: > 0 } uuids })
        {
            labels[ContainerLabels.GpuUuids] = string.Join(',', uuids);
        }

        return labels;
    }

    private IReadOnlyList<GpuRef> GpuRefs(ProcessRecord record) =>
        record.Spec.Gpus is { Mode: GpuSelectionMode.Uuids, Uuids: { } uuids } ? GpuRefsFor(uuids) : record.Gpus;

    private IReadOnlyList<GpuRef> GpuRefsFor(IReadOnlyList<string>? uuids)
    {
        if (uuids is null)
        {
            return [];
        }

        var inventory = _gpus?.Last ?? [];
        return [.. uuids.Select(u => new GpuRef(inventory.FirstOrDefault(g => string.Equals(g.Uuid, u, StringComparison.Ordinal))?.Index ?? -1, u))];
    }

    private void CheckGpusPresent(IReadOnlyList<string> uuids)
    {
        var missing = _gpus!.Missing(uuids);
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

    /// <summary>
    /// The readiness alias of an adopted record: llama.cpp's <c>--alias</c>; for MLX, the real path of the model folder the
    /// validator derives from <c>--model</c>, as at launch. MLX has no alias flag, so reading argv alone gave "" and every
    /// MLX model failed supervision after an agent restart or a pushed update (seen on mac-1, 2026-10-17).
    /// </summary>
    private string AdoptedAlias(ProcessRecord record)
    {
        try
        {
            return AdoptedAlias(record, _options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ProcessRuntimeLog.Failed(_logger, record.ModelId, $"its readiness alias cannot be derived again: {ex.Message}");
            return "";
        }
    }

    internal static string AdoptedAlias(ProcessRecord record, ProcessRuntimeOptions options)
    {
        string alias = AliasOf(record.Spec.Argv);
        // The argv rules only: the executable check would refuse while the catalogue still hashes the MLX folder, which
        // it does right after every agent start, the very moment adoption runs (seen on mac-1 after an automatic update).
        return alias.Length > 0 || record.Spec.Runtime != RuntimeKind.Mlx
            ? alias
            : ProcessLaunchValidator.MlxAlias(record.Spec.Argv, options);
    }

    private static string AliasOf(IReadOnlyList<string> argv)
    {
        for (int i = 0; i + 1 < argv.Count; i++)
        {
            if (argv[i] is "--alias" or "-a")
            {
                return argv[i + 1];
            }
        }

        return "";
    }

    private bool IsCurrent(Instance instance) =>
        _instances.TryGetValue(instance.ModelId, out var current) && ReferenceEquals(current, instance);

    private SemaphoreSlim ModelLock(string modelId) => _modelLocks.GetOrAdd(modelId, _ => new SemaphoreSlim(1, 1));

    /// <summary>One supervised instance. Fields that change are read and written under <see cref="Sync"/>.</summary>
    private sealed class Instance
    {
        public Instance(
            string modelId, ProcessRecord record, IHostedProcess process, string apiKey, string alias,
            IReadOnlyDictionary<string, string>? secretValues)
        {
            ModelId = modelId;
            Record = record;
            Process = process;
            ApiKey = apiKey;
            Alias = alias;
            SecretValues = secretValues;
        }

        public Lock Sync { get; } = new();

        public string ModelId { get; }

        public ProcessRecord Record { get; set; }

        public IHostedProcess? Process { get; set; }

        /// <summary>The clear per-instance key (memory only; the record holds the protected form).</summary>
        public string ApiKey { get; }

        public string Alias { get; }

        /// <summary>Secret environment values of the launch; null after adoption (they are never stored).</summary>
        public IReadOnlyDictionary<string, string>? SecretValues { get; }

        public ModelState State { get; set; }

        public DateTimeOffset? Since { get; set; }

        public string? Error { get; set; }

        public bool Stopping { get; set; }

        public DateTimeOffset AttemptStartedAt { get; set; }

        /// <summary>Where this attempt's output starts in the log; null after adoption.</summary>
        public long? LogStart { get; set; }

        public GpuStartMark? Mark { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Loop { get; set; }
    }
}

/// <summary>What <see cref="ProcessRuntimeDriver.RecoverAsync"/> did at agent start.</summary>
/// <param name="Adopted">Models whose recorded process was re-opened (reported Starting until the probe passes).</param>
/// <param name="Gone">Models whose recorded process was gone (exited, rebooted, PID reused): records removed, Stopped.</param>
/// <param name="OrphansKilled">PIDs of orphans killed (their arguments matched a record or a pending start).</param>
/// <param name="Foreign">PIDs of candidates left alone (unknown or unreadable command line).</param>
/// <param name="PendingResolved">Models whose pending starts were removed.</param>
public sealed record ProcessRecoveryReport(
    IReadOnlyList<string> Adopted,
    IReadOnlyList<string> Gone,
    IReadOnlyList<int> OrphansKilled,
    IReadOnlyList<int> Foreign,
    IReadOnlyList<string> PendingResolved);
