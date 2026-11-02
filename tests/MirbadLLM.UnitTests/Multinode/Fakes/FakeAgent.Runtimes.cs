using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// Runtimes (design §4.3 "Runtimes", §4.8, §4.9, §10.3): simulated container instances with agent-set labels, launch
/// modes, start reservations and unified-memory admission under one node-wide lock, stages with the fencing re-check
/// before destructive ones, a readiness watcher that releases the reservation, logs, and the inference proxy.
/// </summary>
internal sealed partial class FakeAgent
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Host", "Authorization",
    };

    private readonly Dictionary<string, FakeInstance> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReservationInfo> _reservations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _modelLocks = new(StringComparer.Ordinal);
    private readonly HttpClient _upstream = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(10) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly SemaphoreSlim _modelServerLock = new(1, 1);
    private FakeModelServer? _modelServer;
    private double _baseAvailableGiB = double.NaN;
    private int _nextPort = 30000;
    private int _inFlight;
    private readonly List<LaunchRequest> _launchRequests = [];

    /// <summary>Every launch request the fake accepted for validation, in order (MN-219: what the master sent).</summary>
    public IReadOnlyList<LaunchRequest> LaunchRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _launchRequests];
            }
        }
    }

    /// <summary>The upstream every Ready instance is served by (created on first use).</summary>
    public async Task<FakeModelServer> GetModelServerAsync()
    {
        await _modelServerLock.WaitAsync();
        try
        {
            return _modelServer ??= await FakeModelServer.CreateAsync(_time);
        }
        finally
        {
            _modelServerLock.Release();
        }
    }

    /// <summary>Makes a running instance fail (crash of the runtime).</summary>
    public void FailInstance(string modelId, string error)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(modelId, out var instance))
            {
                SetInstanceStateLocked(instance, ModelState.Failed, error);
                FailLaunchOfLocked(instance, error);
            }
        }
    }

    /// <summary>Appends a log line to an instance.</summary>
    public void AppendLog(string modelId, string line)
    {
        lock (_gate)
        {
            if (_instances.TryGetValue(modelId, out var instance))
            {
                AppendLogLineLocked(instance, line);
            }
        }
    }

    private double AvailableGiBLocked()
    {
        double baseline = double.IsNaN(_baseAvailableGiB) ? _options.AvailableMemoryGiB : _baseAvailableGiB;
        return baseline - _instances.Values.Where(i => i.State == ModelState.Ready).Sum(i => i.BudgetGiB);
    }

    private NodeMemorySnapshot MemoryLocked() => new()
    {
        Model = MemoryModelLocked(),
        HostTotalGiB = _options.TotalMemoryGiB,
        HostAvailableGiB = Math.Max(0, HostAvailableGiBLocked()),
        Gpus = MemoryGpusLocked(),
    };

    // ---- Reads ----

    private FakeReply RuntimesList(RuntimesQuery query)
    {
        lock (_gate)
        {
            var list = _instances.Values.Select(i => i.ToStatus());
            if (query.Label == RuntimesQuery.TuningLabel)
            {
                list = list.Where(r => r.Tuning);
            }
            else if (query.Label is not null)
            {
                throw Fail(ErrorCodes.InvalidRequest, $"Unknown label filter '{query.Label}'.");
            }

            return FakeReply.Json<IReadOnlyList<RuntimeStatus>>([.. list.OrderBy(r => r.ModelId, StringComparer.Ordinal)]);
        }
    }

    private FakeReply RuntimeGet(string modelId)
    {
        lock (_gate)
        {
            return _instances.TryGetValue(modelId, out var instance)
                ? FakeReply.Json(instance.ToStatus())
                : throw Fail(ErrorCodes.NotFound, $"No instance of '{modelId}' on node {NodeId} (Stopped).");
        }
    }

    private FakeReply LaunchConfigOf(string modelId)
    {
        lock (_gate)
        {
            if (!_instances.TryGetValue(modelId, out var instance))
            {
                throw Fail(ErrorCodes.NotFound, $"No instance of '{modelId}' on node {NodeId}.");
            }

            var spec = instance.Spec;
            return FakeReply.Json(new LaunchConfig
            {
                Kind = spec.Kind,
                Image = spec.Image,
                ImageDigest = spec.ImageDigest,
                Executable = spec.Executable,
                ExecutableVersion = spec.ExecutableVersion,
                Argv = spec.Argv,
                Env = spec.Env.ToDictionary(e => e.Key, e => spec.SecretEnvNames.Contains(e.Key, StringComparer.Ordinal) ? null : (string?)e.Value, StringComparer.Ordinal),
                Mounts = spec.Mounts,
                Gpus = instance.Gpus,
                Labels = instance.Labels,
                ExecutionFingerprint = instance.Labels.GetValueOrDefault(ContainerLabels.ExecutionFingerprint),
                CreatedAt = instance.CreatedAt,
            });
        }
    }

    // ---- Launch ----

    private FakeReply Launch(FakeCall call)
    {
        var request = call.Request<LaunchRequest>();
        string modelId = call.Target;
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            _launchRequests.Add(request);
            ValidateSpecLocked(request.Spec);
            var gpus = ResolveGpusLocked(request.Spec.Gpus);

            _instances.TryGetValue(modelId, out var existing);
            if (request.Mode == LaunchMode.EnsureStarted && existing is { State: ModelState.Starting or ModelState.Ready or ModelState.Stopping })
            {
                // Today's StartAsync: the running instance is the answer, whatever its configuration (design §4.3 "mode").
                var adopted = AcceptLocked(call);
                CompleteLocked(adopted, OperationState.Succeeded, result: existing.ToStatus());
                return FakeReply.Json(adopted.ToStatus(), call.Route.SuccessStatus);
            }

            double released = request.Mode == LaunchMode.Replace && existing is { State: ModelState.Ready } ? existing.BudgetGiB : 0;
            var reservation = ReserveLocked(call.Mutation.OperationId, modelId, request.Admission, released, gpus);
            var op = AcceptLocked(call);
            op.LaunchGpus = gpus;
            StageLocked(op, OperationStages.Admitted, reservation: reservation);
            _reservations[op.Id] = reservation;
            PublishLocked(new ReservationEvent { Reservation = reservation });
            StartLaunchWork(op, request, reservation);
            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    /// <summary>
    /// The guardrails of design §10.3 that a P1 agent enforces on a spec: reserved labels, approved digests, logical volumes
    /// with fixed targets and confined sub-paths. 422 <c>spec_rejected</c> with the reason.
    /// </summary>
    private void ValidateSpecLocked(LaunchSpec spec)
    {
        static AgentException Rejected(string reason) =>
            Fail(ErrorCodes.SpecRejected, "The launch spec was rejected: " + reason, new SpecRejectedDetails(reason));

        if (spec.Kind == LaunchDelivery.Process && _options.Features.Contains(AgentFeatures.RuntimeLlamaCppProcess, StringComparer.Ordinal))
        {
            // A P3 node (MN-219, minimal: MN-233 extends it): a llama.cpp process by logical executable, no image or mounts.
            if (spec.Runtime != RuntimeKind.LlamaCpp || string.IsNullOrWhiteSpace(spec.Executable) || spec.Image is not null || spec.Mounts.Count > 0)
            {
                throw Rejected("a process spec runs llama.cpp by logical executable, without image or mounts");
            }

            if (spec.Labels.Keys.FirstOrDefault(k => ContainerLabels.Reserved.Contains(k, StringComparer.Ordinal)) is { } reservedLabel)
            {
                throw Rejected($"label '{reservedLabel}' is set by the agent");
            }

            // As the real agent's executable catalogue does (MN-222, design-p3p4 §A.1.2, MN-233): the launch must name a
            // version this node reports as verified, so the master always resolves a concrete, verified one.
            if (_options.ExtraRuntimes.FirstOrDefault(r => r.Kind == RuntimeKind.LlamaCpp && r.Delivery == LaunchDelivery.Process) is { Versions.Count: > 0 } runtime &&
                !runtime.Versions.Any(v => v.Verified && string.Equals(v.Version, spec.ExecutableVersion, StringComparison.Ordinal)))
            {
                throw Rejected($"llama-server version '{spec.ExecutableVersion}' is not verified on this node");
            }

            return;
        }

        if (spec.Kind != LaunchDelivery.Container)
        {
            throw Rejected("only container delivery is available on this node");
        }

        if (string.IsNullOrWhiteSpace(spec.Image))
        {
            throw Rejected("the spec names no image");
        }

        if (spec.Labels.Keys.FirstOrDefault(k => ContainerLabels.Reserved.Contains(k, StringComparer.Ordinal)) is { } reserved)
        {
            throw Rejected($"label '{reserved}' is set by the agent");
        }

        if (spec.ImageDigest is { } digest ? !_approvedDigests.Contains(digest) : !_options.AllowTagPulls)
        {
            throw Rejected(spec.ImageDigest is null ? "an approved image digest is required" : $"digest {spec.ImageDigest} is not approved on this node");
        }

        foreach (var mount in spec.Mounts)
        {
            string? target = mount.Volume switch
            {
                LogicalVolumes.Models => LogicalVolumes.ModelsTarget,
                LogicalVolumes.Cache => LogicalVolumes.CacheTarget,
                _ => null,
            };
            if (target is null)
            {
                throw Rejected($"volume '{mount.Volume}' is not a logical volume");
            }

            if (mount.Target != target)
            {
                throw Rejected($"volume '{mount.Volume}' must be mounted at {target}");
            }

            if (mount.Volume == LogicalVolumes.Models && !mount.ReadOnly)
            {
                throw Rejected("runtimes mount the models volume read-only");
            }

            if (mount.SubPath is { } subPath && !IsPlainRelativePath(subPath))
            {
                throw Rejected($"sub-path '{subPath}' leaves the volume");
            }
        }

        if (spec.ContainerPort is <= 0 or > 65535)
        {
            throw Rejected("the container port is invalid");
        }
    }

    private static bool IsPlainRelativePath(string path) =>
        path.Length > 0 && !Path.IsPathRooted(path) && !path.StartsWith('/') && !path.StartsWith('\\') &&
        path.Split('/', '\\').All(part => part.Length > 0 && part != "." && part != "..");

    /// <summary>
    /// The start rule and admission under the node-wide lock (design §4.8, §6.1 unified): no other start reservation on
    /// the node, then <c>budget ≤ available − reserve − Σ other reservations + released</c> unless forced. Messages keep
    /// today's <c>DockerModelRuntime</c> texts plus "on node {id}".
    /// </summary>
    private ReservationInfo ReserveLocked(string operationId, string modelId, AdmissionRequest admission, double releasedGiB, IReadOnlyList<GpuRef> gpus)
    {
        if (PerGpuAdmissionLocked)
        {
            return ReserveDiscreteLocked(operationId, modelId, admission, releasedGiB, gpus);
        }

        if (_reservations.Values.FirstOrDefault(r => r.OperationId != operationId) is { } other)
        {
            // The real agent's texts (AdmissionTexts), so MN-028's exception mapping sees identical messages on both.
            throw Fail(ErrorCodes.StartConflict,
                MirbadLLM.Agent.Admission.AdmissionTexts.OnNode(MirbadLLM.Agent.Admission.AdmissionTexts.StartConflict(other.ModelId, modelId), NodeId),
                new StartConflictDetails { StartingModelId = other.ModelId });
        }

        CheckAdmissionLocked(modelId, admission, releasedGiB, operationId);
        return new ReservationInfo
        {
            OperationId = operationId,
            ModelId = modelId,
            BudgetGiB = admission.BudgetGiB,
            HostBudgetGiB = admission.HostBudgetGiB,
            HostPort = AllocatePortLocked(),
            CreatedAt = Now,
        };
    }

    private void CheckAdmissionLocked(string modelId, AdmissionRequest admission, double releasedGiB, string operationId)
    {
        if (admission.Force)
        {
            return;
        }

        double available = AvailableGiBLocked();
        double others = _reservations.Values.Where(r => r.OperationId != operationId).Sum(r => r.BudgetGiB);
        double free = available + releasedGiB - admission.ReserveGiB - others;
        if (admission.BudgetGiB > free)
        {
            string text = MirbadLLM.Agent.Admission.AdmissionTexts.OnNode(
                MirbadLLM.Agent.Admission.AdmissionTexts.MemoryRefused(modelId, admission.BudgetGiB, free, available, admission.ReserveGiB, releasedGiB, others),
                NodeId);
            throw Fail(ErrorCodes.AdmissionRefused, text, new AdmissionRefusedDetails
            {
                NeedGiB = admission.BudgetGiB,
                FreeGiB = free,
                AvailableGiB = available,
                ReserveGiB = admission.ReserveGiB,
                ReleasedGiB = releasedGiB,
                ReservedByOthersGiB = others,
            });
        }
    }

    private int AllocatePortLocked()
    {
        var used = _instances.Values.Select(i => i.HostPort).Concat(_reservations.Values.Select(r => r.HostPort ?? 0)).ToHashSet();
        while (used.Contains(_nextPort))
        {
            _nextPort++;
        }

        return _nextPort++;
    }

    private void ReleaseReservationLocked(FakeOp op)
    {
        if (_reservations.Remove(op.Id, out var reservation))
        {
            op.Reservation = null;
            PublishLocked(new ReservationEvent { Reservation = reservation, Released = true });
        }
    }

    private void StartLaunchWork(FakeOp op, LaunchRequest request, ReservationInfo reservation, string? resumeFrom = null) =>
        _ = Task.Run(() => RunLaunchAsync(op, request, reservation, resumeFrom), CancellationToken.None);

    /// <summary>
    /// The launch after the 202 (design §4.9): pull if missing (re-check admission after a long pull), remove the old
    /// instance (replace or failed), re-check the master triple before each destructive stage, create, start; the watcher
    /// takes it to Ready (or Failed) and releases the reservation.
    /// </summary>
    private async Task RunLaunchAsync(FakeOp op, LaunchRequest request, ReservationInfo reservation, string? resumeFrom)
    {
        string modelId = op.Target;
        var ct = op.Cts.Token;
        var modelLock = ModelLock(modelId);
        try
        {
            await modelLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            string image = request.Spec.Image ?? string.Empty;
            bool pullNeeded;
            lock (_gate)
            {
                if (!Alive(op))
                {
                    return;
                }

                pullNeeded = request.Spec.Kind == LaunchDelivery.Container &&
                    (resumeFrom is null or OperationStages.Accepted or OperationStages.Admitted or OperationStages.Pulling) &&
                    !_images.ContainsKey(image);
            }

            if (pullNeeded)
            {
                var started = _time.GetTimestamp();
                lock (_gate)
                {
                    if (!Alive(op))
                    {
                        return;
                    }

                    StageLocked(op, OperationStages.Pulling, new OperationProgress { Percent = 0, Message = "pulling " + image });
                }

                await Task.Delay(_options.PullDuration, _time, ct);
                await _pulls.WaitAsync(ct);
                lock (_gate)
                {
                    if (!Alive(op))
                    {
                        return;
                    }

                    AddImageLocked(image, request.Spec.ImageDigest);
                    StageLocked(op, OperationStages.Pulled, new OperationProgress { Percent = 100, Message = "pulled " + image });
                    if (_time.GetElapsedTime(started) > _options.PullRecheckAfter && !RecheckLocked(op, request, 0))
                    {
                        return;
                    }
                }
            }

            FakeInstance? old;
            lock (_gate)
            {
                if (!Alive(op))
                {
                    return;
                }

                _instances.TryGetValue(modelId, out old);
                if (old is not null && !CheckCurrentLocked(op))
                {
                    return;
                }

                if (old is not null)
                {
                    StageLocked(op, OperationStages.Stopping);
                    SetInstanceStateLocked(old, ModelState.Stopping, null);
                }
            }

            if (old is not null)
            {
                await Task.Delay(_options.StopDuration, _time, ct);
                lock (_gate)
                {
                    if (!Alive(op))
                    {
                        return;
                    }

                    RemoveInstanceLocked(old);
                    StageLocked(op, OperationStages.Removed);
                    if (!RecheckLocked(op, request, 0))
                    {
                        return;
                    }
                }
            }

            lock (_gate)
            {
                if (!Alive(op) || !CheckCurrentLocked(op))
                {
                    return;
                }

                var instance = CreateInstanceLocked(op, request, reservation);
                op.InstanceCreated = true;
                StageLocked(op, OperationStages.Created);
                if (!Alive(op))
                {
                    return;
                }

                instance.Started = true;
                AppendLogLineLocked(instance, "INFO Started; loading weights");
                StageLocked(op, OperationStages.Started);
                StartReadinessWatch(instance, op);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled (operation cancel) or crashed.
        }
        finally
        {
            modelLock.Release();
        }
    }

    /// <summary>Re-probes admission for a held reservation (after a slow pull or a removal); a refusal fails the operation with the 409 details.</summary>
    private bool RecheckLocked(FakeOp op, LaunchRequest request, double released)
    {
        try
        {
            if (PerGpuAdmissionLocked && op.Reservation is { } held)
            {
                CheckDiscreteAdmissionLocked(op.Target, request.Admission, released, op.Id, held.Gpus, held.HostBudgetGiB ?? 0);
            }
            else
            {
                CheckAdmissionLocked(op.Target, request.Admission, released, op.Id);
            }

            return true;
        }
        catch (AgentException ex)
        {
            CompleteLocked(op, OperationState.Failed, ex.Error);
            return false;
        }
    }

    /// <summary>The fencing re-check before a destructive stage (design §4.2): a superseded operation fails with <c>superseded</c>.</summary>
    private bool CheckCurrentLocked(FakeOp op)
    {
        if (IsCurrentLocked(op))
        {
            return true;
        }

        CompleteLocked(op, OperationState.Failed, Error(ErrorCodes.Superseded,
            $"Master {op.Master.ToHeaderValue()} was superseded by {_acceptedMaster?.ToHeaderValue()}; operation {op.Id} stopped before stage changes."));
        return false;
    }

    private bool Alive(FakeOp op) => !op.State.IsTerminal() && !op.Cts.IsCancellationRequested && !_crashed;

    private FakeInstance CreateInstanceLocked(FakeOp op, LaunchRequest request, ReservationInfo reservation)
    {
        var labels = new Dictionary<string, string>(request.Spec.Labels, StringComparer.Ordinal)
        {
            [ContainerLabels.Model] = op.Target,
            [ContainerLabels.Node] = NodeId,
            [ContainerLabels.Operation] = op.Id,
            [ContainerLabels.Port] = (reservation.HostPort ?? 0).ToString(CultureInfo.InvariantCulture),
        };
        var instance = new FakeInstance
        {
            ModelId = op.Target,
            InstanceId = Guid.NewGuid().ToString("N"),
            HostPort = reservation.HostPort ?? AllocatePortLocked(),
            State = ModelState.Starting,
            Since = Now,
            CreatedAt = Now,
            Labels = labels,
            Spec = request.Spec,
            BudgetGiB = request.Admission.BudgetGiB,
            OperationId = op.Id,
            Gpus = op.LaunchGpus,
        };
        _instances[op.Target] = instance;
        AppendLogLineLocked(instance, request.Spec.Kind == LaunchDelivery.Process
            ? $"INFO Created process {request.Spec.Executable} {request.Spec.ExecutableVersion} for {op.Target}"
            : $"INFO Created container ss-{op.Target} from {request.Spec.Image}");
        PublishLocked(new RuntimeEvent { ModelId = instance.ModelId, Runtime = instance.ToStatus() });
        return instance;
    }

    private void StartReadinessWatch(FakeInstance instance, FakeOp? op)
    {
        var token = _boot.Cts.Token;
        _ = Task.Run(() => WatchReadinessAsync(instance, op, token), CancellationToken.None);
    }

    /// <summary>The runtime watcher: Ready after the start duration (unless held or failing); releases the reservation.</summary>
    private async Task WatchReadinessAsync(FakeInstance instance, FakeOp? op, CancellationToken boot)
    {
        try
        {
            await Task.Delay(_options.StartDuration, _time, boot);
            await _starts.WaitAsync(boot);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (boot.IsCancellationRequested || _crashed || !_instances.TryGetValue(instance.ModelId, out var current) ||
                !ReferenceEquals(current, instance) || instance.State != ModelState.Starting)
            {
                return;
            }

            if (_failStarts)
            {
                const string error = "vLLM exited during startup: CUDA out of memory (simulated)";
                AppendLogLineLocked(instance, "ERROR " + error);
                SetInstanceStateLocked(instance, ModelState.Failed, error);
                if (op is not null)
                {
                    CompleteLocked(op, OperationState.Failed, Error(ErrorCodes.InternalError, $"Model '{instance.ModelId}' failed while starting: {error}"));
                }

                return;
            }

            AppendLogLineLocked(instance, "INFO Application startup complete.");
            // A process instance's "measured at start" figure (design-p3p4 §A.1.9, MN-233): the real agent samples the
            // placed GPUs' memory.used before create and after Ready and stores the delta on the record.
            if (instance.Spec.Kind == LaunchDelivery.Process)
            {
                instance.MeasuredGpuGiB = _options.ProcessMeasuredGpuGiB;
            }

            SetInstanceStateLocked(instance, ModelState.Ready, null);
            if (op is not null && !op.State.IsTerminal())
            {
                ReleaseReservationLocked(op);
                StageLocked(op, OperationStages.Ready);
                CompleteLocked(op, OperationState.Succeeded, result: instance.ToStatus());
            }
        }
    }

    private void SetInstanceStateLocked(FakeInstance instance, ModelState state, string? error)
    {
        instance.State = state;
        instance.Error = error;
        instance.Since = Now;
        PublishLocked(new RuntimeEvent { ModelId = instance.ModelId, Runtime = instance.ToStatus() });
    }

    private void RemoveInstanceLocked(FakeInstance instance)
    {
        if (_instances.TryGetValue(instance.ModelId, out var current) && ReferenceEquals(current, instance))
        {
            _instances.Remove(instance.ModelId);
            instance.Removed = true;
            foreach (var follower in instance.Followers)
            {
                follower.Writer.TryComplete();
            }

            PublishLocked(new RuntimeEvent { ModelId = instance.ModelId, Runtime = null });
            FailLaunchOfLocked(instance, $"The instance of '{instance.ModelId}' was removed before it became Ready.");
        }
    }

    /// <summary>A launch still waiting for Ready when its instance fails or disappears ends Failed (reservation released).</summary>
    private void FailLaunchOfLocked(FakeInstance instance, string message)
    {
        if (_ops.TryGetValue(instance.OperationId, out var launch) && !launch.State.IsTerminal() && launch.InstanceCreated)
        {
            CompleteLocked(launch, OperationState.Failed, Error(ErrorCodes.InternalError, message));
        }
    }

    private SemaphoreSlim ModelLock(string modelId)
    {
        lock (_gate)
        {
            if (!_modelLocks.TryGetValue(modelId, out var semaphore))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _modelLocks[modelId] = semaphore;
                _retired.Add(semaphore);
            }

            return semaphore;
        }
    }

    /// <summary>Reconciliation of a launch after a restart (design §4.4 item 5).</summary>
    private void ReconcileLaunchLocked(FakeOp op)
    {
        var request = op.Payload.Deserialize<LaunchRequest>(AgentJson.Options)!;
        op.LaunchGpus = ReconciledGpusLocked(request.Spec.Gpus);
        _instances.TryGetValue(op.Target, out var instance);
        bool ours = instance is not null && instance.OperationId == op.Id;
        if (ours && op.Stage is OperationStages.Created && !instance!.Started)
        {
            // Created but not started: start it if the master is still the accepted one, else remove it (superseded).
            op.InstanceCreated = true;
            if (!IsCurrentLocked(op))
            {
                RemoveInstanceLocked(instance);
                CompleteLocked(op, OperationState.Failed, Error(ErrorCodes.Superseded, $"Master {op.Master.ToHeaderValue()} was superseded; the created instance was removed."));
                return;
            }

            RebuildReservationLocked(op, request);
            instance.Started = true;
            StageLocked(op, OperationStages.Started);
            StartReadinessWatch(instance, op);
            return;
        }

        if (ours)
        {
            op.InstanceCreated = true;
            switch (instance!.State)
            {
                case ModelState.Starting:
                    RebuildReservationLocked(op, request);
                    StartReadinessWatch(instance, op);
                    return;
                case ModelState.Ready:
                    StageLocked(op, OperationStages.Ready);
                    CompleteLocked(op, OperationState.Succeeded, result: instance.ToStatus());
                    return;
                default:
                    CompleteLocked(op, OperationState.Failed, Error(ErrorCodes.InternalError, instance.Error ?? "The instance is not running."));
                    return;
            }
        }

        // No instance of this operation yet: re-run from the pull stage with the reservation re-acquired (§4.8).
        var reservation = RebuildReservationLocked(op, request);
        StartLaunchWork(op, request, reservation, op.Stage);
    }

    private ReservationInfo RebuildReservationLocked(FakeOp op, LaunchRequest request)
    {
        var reservation = op.Reservation ?? new ReservationInfo
        {
            OperationId = op.Id,
            ModelId = op.Target,
            BudgetGiB = request.Admission.BudgetGiB,
            HostBudgetGiB = request.Admission.HostBudgetGiB,
            HostPort = AllocatePortLocked(),
            CreatedAt = Now,
        };
        op.Reservation = reservation;
        _reservations[op.Id] = reservation;
        PublishLocked(new ReservationEvent { Reservation = reservation });
        return reservation;
    }

    // ---- Stop ----

    private FakeReply StopRuntime(FakeCall call)
    {
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            var op = AcceptLocked(call);
            StartStopWork(op, call.Target);
            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    private void StartStopWork(FakeOp op, string modelId) => _ = Task.Run(() => RunStopAsync(op, modelId), CancellationToken.None);

    private async Task RunStopAsync(FakeOp op, string modelId)
    {
        var ct = op.Cts.Token;
        var modelLock = ModelLock(modelId);
        try
        {
            await modelLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            FakeInstance? instance;
            lock (_gate)
            {
                if (!Alive(op))
                {
                    return;
                }

                if (!_instances.TryGetValue(modelId, out instance))
                {
                    CompleteLocked(op, OperationState.Succeeded);
                    return;
                }

                if (!CheckCurrentLocked(op))
                {
                    return;
                }

                StageLocked(op, OperationStages.Stopping);
                SetInstanceStateLocked(instance, ModelState.Stopping, null);
            }

            await Task.Delay(_options.StopDuration, _time, ct);
            lock (_gate)
            {
                if (!Alive(op))
                {
                    return;
                }

                RemoveInstanceLocked(instance);
                StageLocked(op, OperationStages.Removed);
                CompleteLocked(op, OperationState.Succeeded);
            }
        }
        catch (OperationCanceledException)
        {
            // Crashed.
        }
        finally
        {
            modelLock.Release();
        }
    }

    // ---- Logs ----

    private void AppendLogLineLocked(FakeInstance instance, string line)
    {
        string stamped = string.Create(CultureInfo.InvariantCulture, $"{Now:O} {line}");
        instance.Logs.Add(stamped);
        foreach (var follower in instance.Followers)
        {
            follower.Writer.TryWrite(stamped);
        }
    }

    private async Task<FakeReply> LogsAsync(FakeCall call)
    {
        var query = (LogsQuery)call.Body!;
        var http = call.Http;
        string[] history;
        System.Threading.Channels.Channel<string>? follower = null;
        FakeInstance? instance;
        lock (_gate)
        {
            if (!_instances.TryGetValue(call.Path["modelId"], out instance))
            {
                throw Fail(ErrorCodes.NotFound, $"No instance of '{call.Path["modelId"]}' on node {NodeId}.");
            }

            history = [.. instance.Logs.TakeLast(Math.Max(0, query.Tail))];
            if (query.Follow)
            {
                follower = System.Threading.Channels.Channel.CreateUnbounded<string>();
                instance.Followers.Add(follower);
            }
        }

        http.Response.StatusCode = 200;
        http.Response.ContentType = AgentJson.EventStreamContentType;
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        var ct = http.RequestAborted;
        try
        {
            foreach (string line in history)
            {
                await WriteLogLineAsync(http, line, ct);
            }

            await http.Response.Body.FlushAsync(ct);
            if (follower is not null)
            {
                await foreach (string line in follower.Reader.ReadAllAsync(ct))
                {
                    await WriteLogLineAsync(http, line, ct);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // Client went away.
        }
        finally
        {
            if (follower is not null)
            {
                lock (_gate)
                {
                    instance.Followers.Remove(follower);
                }
            }
        }

        return FakeReply.StreamDone;
    }

    private static async Task WriteLogLineAsync(HttpContext http, string line, CancellationToken ct)
    {
        await http.Response.Body.WriteAsync(Encoding.UTF8.GetBytes("data: " + line.Replace('\n', ' ') + "\n\n"), ct);
        await http.Response.Body.FlushAsync(ct);
    }

    // ---- Proxy (design §7.2) ----

    private static bool IsAllowedProxyPath(string path)
    {
        foreach (string allowed in MirbadLLM.Agent.Contracts.Protocol.DefaultProxyAllowedPaths)
        {
            if (allowed.EndsWith("/*", StringComparison.Ordinal)
                ? path.StartsWith(allowed[..^1], StringComparison.Ordinal) && path.Length > allowed.Length - 1
                : string.Equals(path, allowed, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<FakeReply> ProxyAsync(FakeCall call)
    {
        var http = call.Http;
        string modelId = call.Path["modelId"];
        string path = call.Path["path"];
        if (!IsAllowedProxyPath(path))
        {
            throw Fail(ErrorCodes.NotFound, $"'{path}' is not a proxied path.");
        }

        lock (_gate)
        {
            if (!_instances.TryGetValue(modelId, out var instance) || instance.State != ModelState.Ready)
            {
                throw Fail(ErrorCodes.ModelNotRunning, $"Model '{modelId}' is not running on node {NodeId}.");
            }
        }

        if (Interlocked.Increment(ref _inFlight) > _options.MaxInFlightProxy)
        {
            Interlocked.Decrement(ref _inFlight);
            http.Response.Headers.RetryAfter = "5";
            throw Fail(ErrorCodes.ServerBusy, $"Node {NodeId} has {_options.MaxInFlightProxy} proxied requests in flight.");
        }

        try
        {
            var upstream = await GetModelServerAsync();
            using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), new Uri(upstream.BaseUri, path + http.Request.QueryString));
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
            {
                request.Content = new StreamContent(http.Request.Body, MirbadLLM.Agent.Contracts.Protocol.ProxyCopyBufferBytes);
            }

            foreach (var header in http.Request.Headers)
            {
                if (HopByHopHeaders.Contains(header.Key) || header.Key.StartsWith(':') || header.Key.StartsWith("MirbadLLM-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!request.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value))
                {
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value);
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
            }
            catch (HttpRequestException ex)
            {
                throw Fail(ErrorCodes.ModelNotRunning, $"Model '{modelId}' refused the connection: {ex.Message}");
            }

            using (response)
            {
                http.Response.StatusCode = (int)response.StatusCode;
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                {
                    if (!HopByHopHeaders.Contains(header.Key) &&
                        !string.Equals(header.Key, MirbadLLM.Agent.Contracts.Protocol.AgentErrorHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        http.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }

                http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await using var body = await response.Content.ReadAsStreamAsync(http.RequestAborted);
                byte[] buffer = new byte[MirbadLLM.Agent.Contracts.Protocol.ProxyCopyBufferBytes];
                try
                {
                    int read;
                    while ((read = await body.ReadAsync(buffer, http.RequestAborted)) > 0)
                    {
                        await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), http.RequestAborted);
                        await http.Response.Body.FlushAsync(http.RequestAborted);
                    }
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    // Upstream died mid-stream: cut the client (no JSON after headers, design §7.3).
                    http.Abort();
                }
            }

            return FakeReply.StreamDone;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>A simulated container instance.</summary>
    private sealed class FakeInstance
    {
        public required string ModelId { get; init; }

        public required string InstanceId { get; init; }

        public required int HostPort { get; init; }

        public required ModelState State { get; set; }

        public required DateTimeOffset Since { get; set; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required Dictionary<string, string> Labels { get; init; }

        public required LaunchSpec Spec { get; init; }

        public required double BudgetGiB { get; init; }

        public required string OperationId { get; init; }

        public IReadOnlyList<GpuRef> Gpus { get; init; } = [];

        public string? Error { get; set; }

        public bool Started { get; set; }

        public bool Removed { get; set; }

        /// <summary>GPU memory measured at start (<c>NvidiaSmiGpuStartMeter</c>, design-p3p4 §A.1.9); set once Ready.</summary>
        public double? MeasuredGpuGiB { get; set; }

        /// <summary>How many telemetry samples this instance has been scraped for (the fake's growing counters, MN-233).</summary>
        public long ScrapeCount { get; set; }

        public List<string> Logs { get; } = [];

        public List<System.Threading.Channels.Channel<string>> Followers { get; } = [];

        public RuntimeStatus ToStatus() => new()
        {
            ModelId = ModelId,
            State = State,
            InstanceId = InstanceId,
            HostPort = HostPort,
            Since = Since,
            Error = Error,
            Labels = new Dictionary<string, string>(Labels, StringComparer.Ordinal),
            ServedName = Labels.GetValueOrDefault(ContainerLabels.ServedName),
            ConfigHash = Labels.GetValueOrDefault(ContainerLabels.ConfigHash),
            ExecutionFingerprint = Labels.GetValueOrDefault(ContainerLabels.ExecutionFingerprint),
            Tuning = Labels.GetValueOrDefault(ContainerLabels.Tuning) == "1",
            Gpus = Gpus,
            OperationId = Labels.GetValueOrDefault(ContainerLabels.Operation),
            Runtime = Spec.Runtime,
            Delivery = Spec.Kind,
            MeasuredGpuGiB = MeasuredGpuGiB,
        };
    }
}
