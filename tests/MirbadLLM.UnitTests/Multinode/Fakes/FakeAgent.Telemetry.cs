using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// Identity, capabilities, the status snapshot and telemetry (design §3.3, §3.6, §4.3, §4.7, §8.1): <c>/status</c> is built
/// under the state lock with the event watermark; telemetry is a ring of samples (one per interval and one at every boot)
/// with a boot-aware cursor and an honest <c>gap</c> flag.
/// </summary>
internal sealed partial class FakeAgent
{
    private readonly List<NodeSample> _samples = [];
    private long _telemetrySeq;

    private FakeReply Info()
    {
        lock (_gate)
        {
            return FakeReply.Json(new InfoResponse
            {
                NodeId = NodeId,
                NodeUid = NodeUid,
                AgentVersion = _options.AgentVersion,
                Protocol = _options.Protocol,
                BootId = _boot.Id,
                StartedAt = _boot.StartedAt,
                Time = Now,
                AcceptedMaster = _acceptedMaster,
            });
        }
    }

    private FakeReply Capabilities()
    {
        lock (_gate)
        {
            return FakeReply.Json(new NodeCapabilities
            {
                Protocol = _options.Protocol,
                AgentVersion = _options.AgentVersion,
                Features = _options.Features,
                Os = _options.Os ?? new NodeOsInfo("linux", "Fake agent (Ubuntu 24.04)", "6.8.0-fake"),
                Arch = _options.Arch ?? "arm64",
                Cpu = new NodeCpuInfo(20, "Fake Grace"),
                Memory = new NodeMemoryInfo(MemoryModelLocked(), _options.TotalMemoryGiB),
                Gpus = CapabilityGpusLocked(),
                // A P3/P4 node with Docker off reports no container runtime at all (MN-233); every other fake keeps the
                // vLLM container runtime first, so existing capability reports are unchanged.
                Runtimes = _options.NoContainerRuntime
                    ? [.. _options.ExtraRuntimes]
                    : [new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = true, Images = [.. _images.Keys] }, .. _options.ExtraRuntimes],
                // MN-234: a node with no container runtime has no image list and manages no containers either. The real
                // agent strips both (DockerAvailability.Apply) and the frozen NodeCapabilities.windows.json says
                // "images": [] and "containers": false — without this the fake offered the vLLM arm64 image on the
                // Windows PC, so RemoteVllmCatalog put it in the model editor's image picker.
                Images = _options.NoContainerRuntime ? [] : [.. _images.Values],
                ModelRoots = [new ModelRootInfo("models", 2L << 40, 4L << 40, _folders.Count)],
                HostServices = new HostServicesCapability(
                    _options.HostServicesManager ?? HostServiceManagers.Systemd,
                    _hostdUp,
                    // No hostd on an SCM node (ScmHostServicesContributor reports no version), and no containers
                    // without a container runtime.
                    ScmNode ? null : "fake",
                    !_options.NoContainerRuntime && _dockerUp),
                BackupMount = new BackupMountCapability(_options.Features.Contains(AgentFeatures.BackupMount)),
            });
        }
    }

    /// <summary>
    /// This fake answers as a Windows/SCM node (MN-234): no hostd, no containers, no image list — what the real agent's
    /// <c>DockerAvailability</c> filter and <c>ScmHostServicesContributor</c> leave behind.
    /// </summary>
    private bool ScmNode =>
        string.Equals(_options.HostServicesManager, HostServiceManagers.Scm, StringComparison.Ordinal);

    /// <summary>The fake's Degraded reasons (Docker, hostd). Caller holds the state lock.</summary>
    private NodeHealth HealthLocked()
    {
        var reasons = new List<HealthReason>();
        if (!_dockerUp)
        {
            reasons.Add(new HealthReason(HealthReasonCodes.DockerUnreachable, "Docker does not answer."));
        }

        // MN-234: an SCM node has no hostd to miss. The real Windows agent reports services.scm and never
        // hostd_missing, so without this the fake's Windows PC was permanently Degraded with a Linux-worded reason and
        // every MN-233 composition assertion ran against a node that is never Online.
        if (!_hostdUp && !ScmNode)
        {
            reasons.Add(new HealthReason(HealthReasonCodes.HostdMissing, "hostd does not answer."));
        }

        return new NodeHealth { Reasons = reasons };
    }

    /// <summary>Publishes a <c>health</c> event (D68) when a toggle changed the reasons. Caller holds the state lock.</summary>
    private void PublishHealthIfChangedLocked(bool dockerBefore, bool hostdBefore)
    {
        if (dockerBefore != _dockerUp || hostdBefore != _hostdUp)
        {
            PublishLocked(new HealthEvent { Health = HealthLocked() });
        }
    }

    private FakeReply Status()
    {
        lock (_gate)
        {
            return FakeReply.Json(new StatusSnapshot
            {
                BootId = _boot.Id,
                Seq = _seq,
                Time = Now,
                Health = HealthLocked(),
                Memory = MemoryLocked(),
                Reservations = [.. _reservations.Values.OrderBy(r => r.CreatedAt)],
                Runtimes = [.. _instances.Values.Select(i => i.ToStatus()).OrderBy(r => r.ModelId, StringComparer.Ordinal)],
                Downloads = [.. _downloads.Values.Select(d => d.Status)],
                Operations =
                [
                    .. _opOrder.Where(o => !o.State.IsTerminal()).Select(o => o.ToStatus()),
                    .. _opOrder.Where(o => o.State.IsTerminal()).OrderByDescending(o => o.EndedAt)
                        .Take(OperationRetention.StatusRecentCount).Select(o => o.ToStatus()),
                ],
            });
        }
    }

    private FakeReply Telemetry(TelemetryQuery query)
    {
        lock (_gate)
        {
            IEnumerable<NodeSample> samples = _samples;
            bool gap = false;
            if (query.BootId is not null && query.BootId != _boot.Id)
            {
                gap = true;
            }
            else if (query.AfterSeq is { } after)
            {
                gap = _samples.Count > 0 && after < _samples[0].Seq - 1;
                samples = samples.Where(s => s.Seq > after);
            }

            if (query.Max is { } max)
            {
                if (max <= 0)
                {
                    throw Fail(ErrorCodes.InvalidRequest, "'max' must be positive.");
                }

                samples = samples.Take(max);
            }

            return FakeReply.Json(new TelemetryPage { BootId = _boot.Id, Samples = [.. samples], Gap = gap });
        }
    }

    private async Task<FakeReply> GpuIdleAsync(FakeCall call)
    {
        var query = (GpuIdleQuery)call.Body!;
        if (query.Seconds is < 1 or > 60)
        {
            throw Fail(ErrorCodes.InvalidRequest, "'seconds' must be between 1 and 60.");
        }

        lock (_gate)
        {
            _ = IdleGpusLocked(query.Gpus, 0, 0); // validates the indices before waiting
        }

        await Task.Delay(TimeSpan.FromSeconds(query.Seconds), _time, call.Http.RequestAborted);
        IReadOnlyList<GpuIdleSample> perGpu;
        lock (_gate)
        {
            perGpu = IdleGpusLocked(query.Gpus, UtilizationLocked(), query.Seconds);
        }

        return FakeReply.Json(new GpuIdleResponse
        {
            AveragePercent = perGpu.Count == 0 ? null : perGpu.Average(g => g.AveragePercent ?? 0),
            MaxPercent = perGpu.Count == 0 ? null : perGpu.Max(g => g.MaxPercent ?? 0),
            Samples = query.Seconds,
            PerGpu = perGpu,
        });
    }

    private double UtilizationLocked() => _inFlight > 0 ? 90 : _instances.Values.Any(i => i.State == ModelState.Ready) ? 2 : 0;

    private async Task TelemetryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_options.TelemetryInterval, _time, ct);
            lock (_gate)
            {
                if (!_crashed)
                {
                    TakeSampleLocked();
                }
            }
        }
    }

    private void TakeSampleLocked()
    {
        var now = Now;
        _samples.RemoveAll(s => now - s.At > _options.TelemetryWindow);
        double utilization = UtilizationLocked();
        _samples.Add(new NodeSample
        {
            Seq = ++_telemetrySeq,
            At = now,
            Memory = MemoryLocked(),
            Gpus = SampleGpusLocked(utilization),
            GpuProcesses = _hostdUp ? [.. GpuProcessesLocked()] : null,
            Models =
            [
                .. _instances.Values.Where(i => i.State == ModelState.Ready).Select(i => new ModelMetricsScrape
                {
                    ModelId = i.ModelId,
                    At = now,
                    Text = ScrapeTextLocked(i),
                }),
            ],
        });
    }

    /// <summary>
    /// The instance's <c>/metrics</c> exposition: vLLM's two gauges as before, or — for a llama.cpp process instance
    /// (MN-233) — the <c>llamacpp:*</c> families of design-p3p4 §A.1.9 with growing counters, so the master's
    /// <c>ModelKpiCalculator</c> takes its llama.cpp branch exactly as it does for the real agent's scrape. Caller holds
    /// the state lock.
    /// </summary>
    private static string ScrapeTextLocked(FakeInstance instance)
    {
        if (instance.Spec.Kind != LaunchDelivery.Process || instance.Spec.Runtime != RuntimeKind.LlamaCpp)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"vllm:num_requests_running{{model_name=\"{instance.ModelId}\"}} 0\nvllm:num_requests_waiting{{model_name=\"{instance.ModelId}\"}} 0\n");
        }

        long tick = ++instance.ScrapeCount;
        return string.Create(CultureInfo.InvariantCulture, $"""
            # TYPE {LlamaCppMetrics.RequestsProcessing} gauge
            {LlamaCppMetrics.RequestsProcessing} 1
            # TYPE {LlamaCppMetrics.RequestsDeferred} gauge
            {LlamaCppMetrics.RequestsDeferred} 2
            # TYPE {LlamaCppMetrics.PromptTokensTotal} counter
            {LlamaCppMetrics.PromptTokensTotal} {tick * 1000}
            {LlamaCppMetrics.PromptSecondsTotal} {tick * 2}
            {LlamaCppMetrics.TokensPredictedTotal} {tick * 100}
            {LlamaCppMetrics.TokensPredictedSecondsTotal} {tick * 5}
            {LlamaCppMetrics.PromptTokensCachedTotal} {tick * 400}
            {LlamaCppMetrics.SpecDecodeDraftTotal} {tick * 50}
            {LlamaCppMetrics.SpecDecodeAcceptedTotal} {tick * 20}

            """);
    }
}
