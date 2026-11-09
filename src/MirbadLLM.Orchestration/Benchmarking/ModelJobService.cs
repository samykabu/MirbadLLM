using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>Timings of <see cref="ModelJobService"/>; tests shrink them.</summary>
public sealed class ModelJobOptions
{
    /// <summary>How often status and metrics are polled while draining or waiting for Ready.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for in-flight requests to finish after the API pauses.</summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public TimeSpan GpuIdleSampleDuration { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan GpuIdleSampleInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a tuning candidate may take to become Ready. Null = 1.5 × <see cref="MirbadLLMOptions.StartupTimeout"/>.</summary>
    public TimeSpan? CandidateStartTimeout { get; set; }

    /// <summary>Used for estimates until a benchmark of the model has been measured.</summary>
    public TimeSpan DefaultBenchmarkDuration { get; set; } = TimeSpan.FromMinutes(4);

    /// <summary>Used for estimates until a start of the model has been measured.</summary>
    public TimeSpan DefaultStartupDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long removing a failed candidate's container may take before the run stops (infrastructure failure).</summary>
    public TimeSpan CleanupTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How long a candidate waits for another model that is still starting. Null = <see cref="MirbadLLMOptions.StartupTimeout"/>.</summary>
    public TimeSpan? OtherModelStartWait { get; set; }

    /// <summary>Workload presets: repetitions of each timed case while searching.</summary>
    public int SearchRepetitions { get; set; } = 3;

    /// <summary>Workload presets: repetitions in the confirmation stage (top-2 + baseline re-measured).</summary>
    public int ConfirmationRepetitions { get; set; } = 5;

    /// <summary>Shuffles the confirmation order; null = <see cref="Random.Shared"/>. Tests pass a seeded one.</summary>
    public Random? ConfirmationRandom { get; set; }

    /// <summary>Per-preset SLO guards replacing the defaults of <see cref="WorkloadProfiles.For"/>.</summary>
    public Dictionary<TuningGoal, SloGuards> Guards { get; } = [];
}

/// <summary>
/// The tuning run can't continue safely: a failed candidate's container could not be removed (Docker unreachable,
/// container stuck). The run stops and the saved settings are restored with their own bounded Ready check.
/// </summary>
public sealed class TuningInfrastructureException : Exception
{
    public TuningInfrastructureException()
    {
    }

    public TuningInfrastructureException(string message)
        : base(message)
    {
    }

    public TuningInfrastructureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The failed candidate whose cleanup failed, so the run still records it.</summary>
    public TuningCandidateResult? Candidate { get; set; }
}

/// <summary>What a tuning run would do, shown before it starts.</summary>
/// <param name="ReusesRunningBaseline">The running container already has the saved settings, so the baseline is measured without a restart.</param>
/// <param name="StartupEstimate">One start of this model: measured (median of the last run's starts) or the default.</param>
/// <param name="BenchmarkEstimate">One candidate's benchmark: measured for this preset when possible, else the default.</param>
/// <param name="EstimateBasis">Where the per-candidate numbers come from, for the UI.</param>
public sealed record TuningPreview(
    ModelDefinition Baseline, TuningPlan Plan, TimeSpan Estimate, TimeSpan PerCandidate, bool ReusesRunningBaseline,
    TimeSpan StartupEstimate = default, TimeSpan BenchmarkEstimate = default, string? EstimateBasis = null);

/// <summary>
/// Runs benchmark and tuning jobs in the background: one job per node, at most <see cref="JobOptions.MaxConcurrent"/>
/// (default 2) across nodes (design §6.5 item 1, D15; MN-111). A job holds its model's node (the placement, else the
/// bootstrap node). While a job runs, the router is in maintenance mode for that node (the window carries
/// <see cref="MaintenanceState.NodeId"/>), in-flight requests on the node are drained before measuring, and the node's
/// GPU idle utilization is recorded. Results are persisted by <see cref="BenchmarkStore"/>. A tuning run never
/// changes the saved definition: candidates run as labelled override containers, and the saved settings are
/// restored at the end (or on cancel/failure).
/// <para>Where the load goes is <paramref name="targets"/>' choice (the master's <see cref="AgentJobTargets"/>: the node's
/// data client to the agent proxy) and is recorded as the run's <c>proxy</c> provenance. With <paramref name="gpuIdle"/> the GPU idle check is the node's
/// <c>/gpu/idle</c> instead of sampling <paramref name="gpu"/> here. Logs come from <paramref name="runtime"/>.</para>
/// <para>With <paramref name="ownership"/> (MN-108's store, optional until MN-130 composes it) every job holds an
/// ownership record of kind <see cref="OwnershipKind.Job"/> on its model for its lifetime (design §6.7); a model held by
/// another record is refused with <see cref="ModelBusyException"/>.</para>
/// <para>With <paramref name="nodeScopedMaintenance"/> off (the default, P1; <see cref="NodeScopedMaintenanceKey"/>) a job's
/// maintenance window is global (<see cref="MaintenanceState.NodeId"/> null), exactly as before P2: the router answers in
/// the P1 order and the 503 text does not name the node. On (P2), the window carries the job's node.</para>
/// <para>With <paramref name="nodeScopedPolicy"/> (the P2 composition, MN-130, D101) the choice is made per job when it
/// starts (<see cref="NodeScopedMaintenancePolicy.IsOnAsync"/>) and <paramref name="nodeScopedMaintenance"/> is ignored.
/// The agent operations a job submits are attributed to <see cref="AuditActors.SystemJob"/> (MN-116).</para>
/// <para>The tuning gates inspect model folders on the job's node: <paramref name="inspectOnNode"/> (node id, folder) when
/// given, else <paramref name="inspect"/> (one node only, kept for the P1 composition and tests).</para>
/// <para>With <paramref name="provenance"/> (MN-112) runs record their provenance, the suite's noise check is scoped to what
/// the model shares, and ETAs use only runs measured on the same hardware; without it runs are stored as before.</para>
/// <para>A llama.cpp model's tuning (MN-225, <see cref="LlamaCppTuning"/>) reads its GGUF header through
/// <paramref name="inspectGguf"/> (node id, model) for the candidates' memory checks; null or failing = not checked.</para>
/// </summary>
public sealed class ModelJobService(
    IModelRegistry registry,
    IModelRuntime runtime,
    IModelLauncher launcher,
    IBenchmarkSuite suite,
    BenchmarkStore store,
    MaintenanceModeService maintenance,
    IMetricsHistory history,
    IGpuProbe gpu,
    StartupTimeTracker startupTimes,
    IOptions<MirbadLLMOptions> options,
    ModelJobOptions jobOptions,
    ILogger<ModelJobService> logger,
    IBenchmarkTargetResolver targets,
    Func<string, ModelFolderInspection?>? inspect = null,
    TimeProvider? timeProvider = null,
    IGpuIdleSampler? gpuIdle = null,
    JobOptions? limits = null,
    string? bootstrapNodeId = null,
    IOwnershipStore? ownership = null,
    bool nodeScopedMaintenance = false,
    Func<string, string, ModelFolderInspection?>? inspectOnNode = null,
    IBenchmarkProvenanceSource? provenance = null,
    NodeScopedMaintenancePolicy? nodeScopedPolicy = null,
    Func<string, ModelDefinition, GgufSummary?>? inspectGguf = null)
{
    /// <summary>
    /// The configuration key of the node-scoped maintenance switch (default off), the same key the router binds
    /// (<c>RouterMaintenanceOptions.NodeScopedKey</c>); the composition passes its value as <c>nodeScopedMaintenance</c>.
    /// </summary>
    public const string NodeScopedMaintenanceKey = "MirbadLLM:Router:Maintenance:NodeScoped";

    private readonly MirbadLLMOptions _options = options.Value;
    private readonly IBenchmarkTargetResolver _targets = targets ?? throw new ArgumentNullException(nameof(targets));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly int _maxConcurrent = Math.Max(1, (limits ?? new JobOptions()).MaxConcurrent);
    private readonly string _bootstrapNodeId = string.IsNullOrEmpty(bootstrapNodeId) ? RegistryMigrationOptions.DefaultBootstrapNodeId : bootstrapNodeId;
    private readonly Lock _gate = new();

    /// <summary>Running jobs by node id (one per node).</summary>
    private readonly Dictionary<string, JobState> _jobs = new(StringComparer.Ordinal);

    /// <summary>How the last job on each node ended.</summary>
    private readonly Dictionary<string, JobOutcome> _lastByNode = new(StringComparer.Ordinal);

    private readonly List<Task> _runners = [];
    private JobOutcome? _last;

    /// <summary>
    /// The oldest running job, or null. With one node this is "the" job, as before P2; multi-node callers use
    /// <see cref="Running"/> or <see cref="JobOnNode"/>.
    /// </summary>
    public JobSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return Oldest()?.Snapshot();
            }
        }
    }

    /// <summary>Every running job, oldest first (at most one per node).</summary>
    public IReadOnlyList<JobSnapshot> Running
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs.Values.OrderBy(j => j.StartedAt).ThenBy(j => j.NodeId, StringComparer.Ordinal).Select(j => j.Snapshot())];
            }
        }
    }

    /// <summary>How the most recent job (on any node) ended.</summary>
    public JobOutcome? LastOutcome
    {
        get
        {
            lock (_gate)
            {
                return _last;
            }
        }
    }

    /// <summary>The node a job holds when its model has no placement, and the node <c>/api/benchmark</c> and <c>/api/tuning</c> report.</summary>
    public string BootstrapNodeId => _bootstrapNodeId;

    /// <summary>Jobs at once across all nodes (<see cref="JobOptions.MaxConcurrent"/>, at least 1).</summary>
    public int MaxConcurrent => _maxConcurrent;

    /// <summary>The job running on <paramref name="nodeId"/>, or null.</summary>
    public JobSnapshot? JobOnNode(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        lock (_gate)
        {
            return _jobs.TryGetValue(nodeId, out var job) ? job.Snapshot() : null;
        }
    }

    /// <summary>
    /// The job a page about <paramref name="model"/> shows (MN-126): the one running on the model's node (it blocks a new
    /// job on the model; it is the model's own when its <see cref="JobSnapshot.ModelId"/> matches), or null. Jobs on other
    /// nodes don't concern the model. Null model = <see cref="Current"/>.
    /// </summary>
    public JobSnapshot? JobAffecting(ModelDefinition? model) => model is null ? Current : JobOnNode(NodeOf(model));

    /// <summary>How the last job on <paramref name="nodeId"/> ended, or null.</summary>
    public JobOutcome? LastOutcomeOnNode(string nodeId)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        lock (_gate)
        {
            return _lastByNode.GetValueOrDefault(nodeId);
        }
    }

    /// <summary>The node a job on <paramref name="model"/> holds: its placement's node, else the bootstrap node.</summary>
    public string NodeOf(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Placement?.NodeId is { Length: > 0 } nodeId ? nodeId : _bootstrapNodeId;
    }

    /// <summary>
    /// <c>GET /api/jobs</c> (design §5.6, D34): every running job with its node, oldest first, always a list. The exempt
    /// models are the node's other models with <see cref="ModelDefinition.KeepServingDuringJobs"/> (never the model under test).
    /// </summary>
    public async Task<IReadOnlyList<NodeJobSummary>> ListJobSummariesAsync(CancellationToken ct = default)
    {
        var running = Running;
        if (running.Count == 0)
        {
            return [];
        }

        var models = await registry.ListAsync(ct).ConfigureAwait(false);
        return [.. running.Select(job => new NodeJobSummary
        {
            JobId = job.JobId,
            Kind = KindName(job.Kind),
            NodeId = job.NodeId ?? _bootstrapNodeId,
            ModelId = job.ModelId,
            StartedAt = job.StartedAt,
            EstimatedEnd = job.EstimatedEnd,
            Phase = job.Phase,
            ExemptModelIds = MaintenanceScope.ExemptModelIds(WindowOf(job), models),
        })];
    }

    /// <summary>The maintenance window a job holds: scoped to its node when node-scoped maintenance is on, else global (P1).</summary>
    private MaintenanceState WindowOf(JobSnapshot job) =>
        maintenance.Active.FirstOrDefault(w => string.Equals(w.JobId, job.JobId, StringComparison.Ordinal))
        ?? new(job.JobId, KindName(job.Kind), job.ModelId, job.StartedAt, job.EstimatedEnd, nodeScopedMaintenance && nodeScopedPolicy is null ? job.NodeId : null);

    /// <summary>Completes when every running job has finished. For tests and shutdown.</summary>
    public Task WhenIdleAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll([.. _runners]);
        }
    }

    /// <summary>Samples GPU utilization for a few seconds; above 10 % means other GPU work (ComfyUI, Ollama…) is active.</summary>
    public Task<GpuIdleReading> SampleGpuIdleAsync(CancellationToken ct = default) => SampleGpuIdleAsync(modelId: null, ct);

    /// <summary>
    /// As <see cref="SampleGpuIdleAsync(CancellationToken)"/>, on the node that runs <paramref name="modelId"/> when the
    /// check goes through an agent (<see cref="IGpuIdleSampler"/>); null = the bootstrap node.
    /// </summary>
    public async Task<GpuIdleReading> SampleGpuIdleAsync(string? modelId, CancellationToken ct)
    {
        if (gpuIdle is not null)
        {
            return await gpuIdle.SampleAsync(modelId, jobOptions.GpuIdleSampleDuration, ct).ConfigureAwait(false);
        }

        var values = new List<double>();
        int samples = Math.Max(1, (int)Math.Round(jobOptions.GpuIdleSampleDuration / jobOptions.GpuIdleSampleInterval));
        for (int i = 0; i < samples; i++)
        {
            if (i > 0)
            {
                await Task.Delay(jobOptions.GpuIdleSampleInterval, _time, ct).ConfigureAwait(false);
            }

            try
            {
                if (await gpu.GetAsync(ct).ConfigureAwait(false) is { UtilizationPercent: { } utilization })
                {
                    values.Add(utilization);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No reading for this sample.
            }
        }

        return values.Count == 0
            ? new GpuIdleReading(null, null, 0)
            : new GpuIdleReading(values.Average(), values.Max(), values.Count);
    }

    /// <summary>The last on-demand benchmark's duration for this model, else the default.</summary>
    public async Task<TimeSpan> EstimateBenchmarkAsync(string modelId, CancellationToken ct = default)
    {
        var expected = await provenance.ExpectedAsync(registry, modelId, ct).ConfigureAwait(false);
        var runs = await store.ListComparableBenchmarksAsync(modelId, expected, _bootstrapNodeId, ct).ConfigureAwait(false);
        var last = runs.FirstOrDefault(r => r.Result is not null && !r.Cancelled);
        return last is not null && last.Duration > TimeSpan.Zero ? last.Duration : jobOptions.DefaultBenchmarkDuration;
    }

    public TimeSpan EstimateStartup(string modelId) => startupTimes.GetLast(modelId) ?? jobOptions.DefaultStartupDuration;

    public Task<TuningPreview> PreviewTuningAsync(string modelId, IReadOnlyCollection<TuningDimension>? dimensions, CancellationToken ct = default) =>
        PreviewTuningAsync(modelId, dimensions, goal: null, ct);

    /// <summary>
    /// What a tuning run would do. With a workload preset <paramref name="goal"/> the estimate includes the confirmation
    /// stage (top-2 + baseline re-measured with more repetitions).
    /// </summary>
    public async Task<TuningPreview> PreviewTuningAsync(
        string modelId, IReadOnlyCollection<TuningDimension>? dimensions, TuningGoal? goal, CancellationToken ct = default)
    {
        var stored = await registry.GetAsync(modelId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown model '{modelId}'.");

        // The effective configuration: a raw owned flag in ExtraArgs can't override what a candidate sets.
        var saved = VllmArgsNormalizer.Apply(stored);
        ThrowIfUnsupported(saved);
        goal = goal is { } asked ? EffectiveGoal(saved, asked) : null;
        if (goal is { } preset && WorkloadProfiles.IsPreset(preset))
        {
            return await PreviewSearchAsync(saved, dimensions, preset, ct).ConfigureAwait(false);
        }

        var plan = TuningPlanner.Plan(saved, Inspect(saved), dimensions, InspectGguf(saved));
        bool reuse = await BaselineIsRunningAsync(saved, ct).ConfigureAwait(false);
        var bench = await EstimateBenchmarkAsync(modelId, ct).ConfigureAwait(false);
        var perCandidate = EstimateStartup(modelId) + bench;
        var estimate = (perCandidate * plan.PreviewCandidates.Count) - (reuse ? EstimateStartup(modelId) : TimeSpan.Zero);
        if (goal is { } g && WorkloadProfiles.IsPreset(g) && plan.PreviewCandidates.Count > 1)
        {
            // Baseline + the top two, each with ConfirmationRepetitions instead of SearchRepetitions.
            int confirmed = Math.Min(3, plan.PreviewCandidates.Count);
            double factor = (double)Math.Max(1, jobOptions.ConfirmationRepetitions) / Math.Max(1, jobOptions.SearchRepetitions);
            estimate += (EstimateStartup(modelId) + (bench * factor)) * confirmed;
        }

        return new TuningPreview(saved, plan, estimate, perCandidate, reuse);
    }

    /// <summary>
    /// Workload-preset preview: the interaction-aware search plan (<see cref="SearchPlanner"/>) and an ETA from the
    /// expected candidate count (follow-ups and refinement included) and this model's measured durations.
    /// </summary>
    private async Task<TuningPreview> PreviewSearchAsync(ModelDefinition saved, IReadOnlyCollection<TuningDimension>? dimensions, TuningGoal goal, CancellationToken ct)
    {
        string node = NodeOf(saved);
        var plan = SearchPlanner.Plan(saved, Inspect(saved), dimensions, folder => InspectFolder(node, folder), _options.MtpArchitectures.ToList(), InspectGguf(saved));
        bool reuse = await BaselineIsRunningAsync(saved, ct).ConfigureAwait(false);
        var (startup, bench, basis) = await EstimateDurationsAsync(saved.Id, goal, ct).ConfigureAwait(false);
        int confirmations = plan.ExpectedCandidates > 1 ? Math.Min(3, plan.ExpectedCandidates) : 0;
        double factor = (double)Math.Max(1, jobOptions.ConfirmationRepetitions) / Math.Max(1, jobOptions.SearchRepetitions);
        var estimate = TuningEstimator.Estimate(plan.ExpectedCandidates, reuse, confirmations, startup, bench, factor);
        return new TuningPreview(saved, plan, estimate, startup + bench, reuse, startup, bench, basis);
    }

    /// <summary>
    /// Per-candidate durations for an ETA: starts = the median of the last tuning run's measured starts, else the last
    /// start, else the default; benchmark = the median of the last run with this preset, else the last on-demand
    /// benchmark with this preset, else the default.
    /// </summary>
    public async Task<(TimeSpan Startup, TimeSpan Benchmark, string Basis)> EstimateDurationsAsync(string modelId, TuningGoal goal, CancellationToken ct = default)
    {
        var expected = await provenance.ExpectedAsync(registry, modelId, ct).ConfigureAwait(false);
        var runs = await store.ListComparableTuningAsync(modelId, expected, _bootstrapNodeId, ct).ConfigureAwait(false);
        var startups = runs.Take(3).SelectMany(r => r.Candidates).Select(c => c.StartupSeconds ?? 0).Where(s => s > 0).ToList();
        var startup = TuningEstimator.Median(startups, EstimateStartup(modelId));

        var sameGoal = runs.FirstOrDefault(r => r.Goal == goal && r.Candidates.Any(c => c.BenchmarkSeconds is > 0));
        var benchSeconds = sameGoal?.Candidates.Where(c => c.Step != ConfirmationStep).Select(c => c.BenchmarkSeconds ?? 0).Where(s => s > 0).ToList() ?? [];
        TimeSpan bench;
        string basis;
        if (benchSeconds.Count > 0)
        {
            bench = TuningEstimator.Median(benchSeconds, jobOptions.DefaultBenchmarkDuration);
            basis = "measured in the last tuning run with this workload";
        }
        else if ((await store.ListComparableBenchmarksAsync(modelId, expected, _bootstrapNodeId, ct).ConfigureAwait(false)).FirstOrDefault(b => b.Preset == goal && b.Result is not null && !b.Cancelled) is { } last)
        {
            bench = last.Duration;
            basis = "the last benchmark with this workload";
        }
        else
        {
            bench = jobOptions.DefaultBenchmarkDuration;
            basis = "default estimate (nothing measured with this workload yet)";
        }

        return (startup, bench, startups.Count > 0 ? $"{basis}; starts measured in earlier tuning" : basis);
    }

    /// <summary>Starts an on-demand benchmark of a Ready model with its current settings (no restart).</summary>
    /// <exception cref="JobConflictException">Another job holds the model's node, or the job limit is reached.</exception>
    /// <exception cref="KeyNotFoundException">Unknown model.</exception>
    /// <exception cref="InvalidOperationException">The model is not Ready.</exception>
    public Task<JobSnapshot> StartBenchmarkAsync(string modelId, CancellationToken ct = default) =>
        StartBenchmarkAsync(modelId, preset: null, ct);

    /// <summary>
    /// Starts an on-demand benchmark: the classic suite when <paramref name="preset"/> is null (or a classic goal),
    /// else the workload preset's workloads (an embedding model always gets the embeddings workload).
    /// </summary>
    /// <exception cref="JobConflictException">Another job holds the model's node, or the job limit is reached.</exception>
    /// <exception cref="ModelBusyException">Another ownership record (a move, a restore) holds the model.</exception>
    public async Task<JobSnapshot> StartBenchmarkAsync(string modelId, TuningGoal? preset, CancellationToken ct = default)
    {
        var saved = await registry.GetAsync(modelId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown model '{modelId}'.");
        string nodeId = NodeOf(saved);
        ThrowIfBusy(nodeId, JobKind.Benchmark, modelId);
        ThrowIfUnsupported(saved);
        var status = await runtime.GetStatusAsync(modelId, ct).ConfigureAwait(false);
        if (status.State != ModelState.Ready)
        {
            throw new InvalidOperationException(
                $"{saved.ServedName} is not Ready (state: {status.State}). Start it first; a benchmark measures a running model.");
        }

        var estimate = await EstimateBenchmarkAsync(modelId, ct).ConfigureAwait(false);
        var job = await BeginAsync(JobKind.Benchmark, saved, nodeId, estimate, goal: null, ct).ConfigureAwait(false);
        try
        {
            if (preset is { } p && WorkloadProfiles.IsPreset(p))
            {
                job.Preset = WorkloadProfiles.PresetFor(p, PoolingDetection.Detect(saved) != PoolingMode.None);
            }

            StartRunner(job, () => RunBenchmarkAsync(job, saved));
        }
        catch (Exception ex)
        {
            // Never leave the window or the node held when the job can't be handed to its runner.
            await FinishAsync(job, FailedOutcome(job, ex)).ConfigureAwait(false);
            throw;
        }

        return job.Snapshot();
    }

    /// <summary>Starts a tuning run. The winner is never applied automatically.</summary>
    /// <exception cref="JobConflictException">Another job holds the model's node, or the job limit is reached.</exception>
    /// <exception cref="ModelBusyException">Another ownership record (a move, a restore) holds the model.</exception>
    /// <exception cref="KeyNotFoundException">Unknown model.</exception>
    public async Task<JobSnapshot> StartTuningAsync(
        string modelId, TuningGoal goal, IReadOnlyCollection<TuningDimension>? dimensions = null, CancellationToken ct = default)
    {
        var model = await registry.GetAsync(modelId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown model '{modelId}'.");
        if (TuningSupport.RefusalFor(model) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        string nodeId = NodeOf(model);
        ThrowIfBusy(nodeId, JobKind.Tuning, modelId);
        goal = EffectiveGoal(model, goal);
        var preview = await PreviewTuningAsync(modelId, dimensions, goal, ct).ConfigureAwait(false);
        var job = await BeginAsync(JobKind.Tuning, model, nodeId, preview.Estimate, goal, ct).ConfigureAwait(false);
        try
        {
            if (WorkloadProfiles.IsPreset(goal))
            {
                job.Preset = goal;
                job.Repetitions = Math.Max(1, jobOptions.SearchRepetitions);
            }

            lock (_gate)
            {
                job.CandidateCount = preview.Plan.PreviewCandidates.Count;
            }

            StartRunner(job, () => RunTuningAsync(job, preview, goal, dimensions));
        }
        catch (Exception ex)
        {
            await FinishAsync(job, FailedOutcome(job, ex)).ConfigureAwait(false);
            throw;
        }

        return job.Snapshot();
    }

    /// <summary>
    /// Cancels every running job (of <paramref name="kind"/>, when given) on every node. False when there is none.
    /// </summary>
    public bool Cancel(JobKind? kind = null)
    {
        lock (_gate)
        {
            bool any = false;
            foreach (var job in _jobs.Values.Where(j => kind is null || j.Kind == kind))
            {
                CancelLocked(job);
                any = true;
            }

            return any;
        }
    }

    /// <summary>Cancels the job on <paramref name="nodeId"/> (of <paramref name="kind"/>, when given). False when there is none.</summary>
    public bool CancelOnNode(string nodeId, JobKind? kind = null)
    {
        ArgumentNullException.ThrowIfNull(nodeId);
        lock (_gate)
        {
            if (!_jobs.TryGetValue(nodeId, out var job) || (kind is { } k && job.Kind != k))
            {
                return false;
            }

            CancelLocked(job);
            return true;
        }
    }

    /// <summary>
    /// Cancels the running job <paramref name="jobId"/> (its maintenance window's <see cref="MaintenanceState.JobId"/>), on
    /// whichever node it runs. False when no running job has that id. The maintenance banner's Stop uses it (MN-126).
    /// </summary>
    public bool CancelJob(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        lock (_gate)
        {
            var job = _jobs.Values.FirstOrDefault(j => string.Equals(j.JobId, jobId, StringComparison.Ordinal));
            if (job is null)
            {
                return false;
            }

            CancelLocked(job);
            return true;
        }
    }

    private static void CancelLocked(JobState job)
    {
        job.Cancelling = true;
        job.Phase = job.Kind == JobKind.Tuning ? "Cancelling; restoring the saved settings" : "Cancelling";
        job.Cts.Cancel();
    }

    /// <summary>
    /// Boot recovery: a tuning run interrupted by a controller restart can leave a model running a candidate
    /// (labelled <c>mirbadllm.tuning=1</c>); relaunch those with their saved definitions. Job ownership records left by
    /// the previous process are released (no job survives a restart).
    /// </summary>
    public Task RecoverAsync(CancellationToken ct = default) => RecoverCoreAsync(nodeId: null, ct);

    /// <summary>
    /// As <see cref="RecoverAsync"/> for the models of <paramref name="nodeId"/> only (their placement's node, else the
    /// bootstrap node), so each node is recovered once it is reconciled, whatever the other nodes' state (MN-111).
    /// </summary>
    public Task RecoverNodeAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        return RecoverCoreAsync(nodeId, ct);
    }

    /// <summary>
    /// True once boot recovery (<see cref="RecoverAsync"/> or <see cref="RecoverNodeAsync"/>) finished for
    /// <paramref name="nodeId"/>: its interrupted tuning candidates were relaunched and the job records no running job holds
    /// released. Tests that place job records or tuning instances wait for it, so recovery cannot treat them as left over
    /// (W9 review MN-132, D104).
    /// </summary>
    public bool HasRecovered(string nodeId) => _recovered.ContainsKey(nodeId) || _recovered.ContainsKey(AllNodesRecovered);

    private const string AllNodesRecovered = "*";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _recovered = new(StringComparer.Ordinal);

    private async Task RecoverCoreAsync(string? nodeId, CancellationToken ct)
    {
        try
        {
            await RecoverOnceAsync(nodeId, ct).ConfigureAwait(false);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _recovered[nodeId ?? AllNodesRecovered] = true;
            }
        }
    }

    private async Task RecoverOnceAsync(string? nodeId, CancellationToken ct)
    {
        IReadOnlyList<string> ids;
        try
        {
            ids = await launcher.ListTuningContainersAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            JobLog.RecoveryFailed(logger, ex);
            return;
        }

        foreach (string id in ids)
        {
            try
            {
                if (await registry.GetAsync(id, ct).ConfigureAwait(false) is { } saved &&
                    (nodeId is null || string.Equals(NodeOf(saved), nodeId, StringComparison.Ordinal)) &&
                    !IsUnderTest(id))
                {
                    await launcher.LaunchAsync(saved, new LaunchOptions(Force: true), ct).ConfigureAwait(false);
                    JobLog.RecoveredTuningContainer(logger, id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                JobLog.RecoveryFailed(logger, ex);
            }
        }

        await ReleaseStaleOwnershipAsync(nodeId, ct).ConfigureAwait(false);
    }

    /// <summary>True when a job of this process runs on <paramref name="modelId"/> (its candidate is not stale).</summary>
    private bool IsUnderTest(string modelId)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => string.Equals(j.ModelId, modelId, StringComparison.Ordinal));
        }
    }

    /// <summary>Releases job records no running job of this process holds (on <paramref name="nodeId"/>'s models; null = all).</summary>
    private async Task ReleaseStaleOwnershipAsync(string? nodeId, CancellationToken ct)
    {
        if (ownership is null)
        {
            return;
        }

        try
        {
            foreach (var record in await ownership.ListAsync(ct).ConfigureAwait(false))
            {
                if (record.Kind != OwnershipKind.Job || HoldsRecord(record.Id))
                {
                    continue;
                }

                if (nodeId is not null && await registry.GetAsync(record.ModelId, ct).ConfigureAwait(false) is { } model &&
                    !string.Equals(NodeOf(model), nodeId, StringComparison.Ordinal))
                {
                    continue;
                }

                await ownership.ReleaseAsync(record.Id, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            JobLog.RecoveryFailed(logger, ex);
        }
    }

    private bool HoldsRecord(string recordId)
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => string.Equals(j.OwnershipId, recordId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Applies one tuning candidate's effective tuned settings (the whole speculative and compilation config, max num
    /// seqs, max num batched tokens, KV cache dtype, prefix caching, async scheduling, mamba cache mode — never max
    /// model len or the memory budget) onto the model's saved definition. Both sides are normalized first
    /// (<see cref="VllmArgsNormalizer"/>), so the candidate's settings are what vLLM actually measured (a run recorded
    /// before the canonical configuration could carry a raw override in ExtraArgs) and no raw owned flag survives in
    /// the saved ExtraArgs to override them again. Used by both the tuning page's per-candidate "Apply" action (any
    /// accepted row, not only the run's winner) and its "Apply best" button.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No run with this id exists, it has no candidate at this index,
    /// or the model itself is unknown.</exception>
    /// <exception cref="JobConflictException">A benchmark or tuning job is running.</exception>
    /// <exception cref="InvalidOperationException">The run belongs to a different model, or the candidate was
    /// not accepted.</exception>
    public async Task<ModelDefinition> ApplyTuningCandidateAsync(string modelId, string runId, int candidateIndex, CancellationToken ct = default)
    {
        // A job on the model's node blocks the apply (per node from P2; an unknown model fails below with 404).
        if (await registry.GetAsync(modelId, ct).ConfigureAwait(false) is { } current)
        {
            string nodeId = NodeOf(current);
            lock (_gate)
            {
                if (_jobs.TryGetValue(nodeId, out var job))
                {
                    throw new JobConflictException(
                        $"{(job.Kind == JobKind.Tuning ? "A tuning run" : "A benchmark")} on {job.ModelId}{NodeSuffix(nodeId)} is in progress; wait for it to finish or cancel it first.")
                    {
                        NodeId = nodeId,
                    };
                }
            }
        }

        var run = await store.FindTuningRunAsync(runId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown tuning run '{runId}'.");

        if (!string.Equals(run.ModelId, modelId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Tuning run '{runId}' belongs to model '{run.ModelId}', not '{modelId}'.");
        }

        var candidate = run.Candidates.FirstOrDefault(c => c.Index == candidateIndex)
            ?? throw new KeyNotFoundException($"Tuning run '{runId}' has no candidate at index {candidateIndex}.");

        if (candidate.Outcome != CandidateOutcome.Accepted)
        {
            throw new InvalidOperationException(
                $"Candidate {candidateIndex} ({candidate.Label}) was not accepted ({candidate.Outcome}); only an accepted candidate can be applied.");
        }

        var saved = await registry.GetAsync(modelId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown model '{modelId}'.");

        // A run from before tuning was refused for this runtime only changed settings its server never reads.
        if (TuningSupport.RefusalFor(saved) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
        }

        var merged = MergeTuned(saved, candidate.Settings);

        await registry.UpsertAsync(merged, ct).ConfigureAwait(false);
        return merged;
    }

    /// <summary>
    /// The saved definition with a candidate's tuned fields laid over it, both normalized first. A legacy run
    /// (recorded before the canonical config) can carry an owned flag in the candidate's ExtraArgs as well as a
    /// typed value — e.g. MTP × 2 typed plus a raw <c>--speculative-config</c> with 3 tokens. The old builder
    /// emitted ExtraArgs last, so vLLM ran the raw one; the normalizer applies the same rule, so this returns
    /// what the candidate actually measured. Pure function: equal to the normalized <paramref name="saved"/>
    /// when applying the candidate would change nothing.
    /// </summary>
    public static ModelDefinition MergeTuned(ModelDefinition saved, ModelDefinition candidateSettings)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(candidateSettings);
        if (LlamaCppTuning.Applies(saved))
        {
            // llama.cpp (MN-225): the tuned settings go into LlamaCppSettings; nothing vLLM-specific applies.
            return LlamaCppTuning.MergeTuned(saved, candidateSettings);
        }

        if (SmallRuntimeTuning.Applies(saved))
        {
            return SmallRuntimeTuning.MergeTuned(saved, candidateSettings);
        }

        var w = VllmArgsNormalizer.Apply(candidateSettings);
        return VllmArgsNormalizer.Apply(VllmArgsNormalizer.Apply(saved) with
        {
            Speculative = w.Speculative,
            CudaGraphMode = w.CudaGraphMode,
            CompilationConfig = w.CompilationConfig,
            MaxNumSeqs = w.MaxNumSeqs,
            MaxNumBatchedTokens = w.MaxNumBatchedTokens,
            KvCacheDtype = w.KvCacheDtype,
            EnablePrefixCaching = w.EnablePrefixCaching,
            AsyncScheduling = w.AsyncScheduling,
            MambaCacheMode = w.MambaCacheMode,
        });
    }

    public static string ConflictMessage(JobSnapshot running, JobKind requested) => ConflictMessage(running, requested, nameNode: false);

    /// <summary>
    /// "A tuning run on qwen is in progress, so a benchmark can't start. …"; with <paramref name="nameNode"/> the node
    /// follows the model ("… on qwen on node pc-office is in progress …").
    /// </summary>
    public static string ConflictMessage(JobSnapshot running, JobKind requested, bool nameNode)
    {
        ArgumentNullException.ThrowIfNull(running);
        string what = running.Kind == JobKind.Tuning ? "A tuning run" : "A benchmark";
        string wanted = requested == JobKind.Tuning ? "a tuning run" : "a benchmark";
        string where = nameNode && running.NodeId is { } node ? " on node " + node : "";
        return $"{what} on {running.ModelId}{where} is in progress, so {wanted} can't start. Wait for it to finish or cancel it first.";
    }

    /// <summary>
    /// The 409 <c>job_limit_reached</c> text: the limit and the jobs holding it ("2 jobs already run (the limit,
    /// MirbadLLM:Jobs:MaxConcurrent, is 2): a benchmark on qwen on node dgx; a tuning run on gemma on node pc-office. …").
    /// </summary>
    public static string LimitMessage(IReadOnlyCollection<JobSnapshot> running, int limit, JobKind requested, string modelId, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(running);
        string jobs = string.Join("; ", running.Select(j =>
            $"{(j.Kind == JobKind.Tuning ? "a tuning run" : "a benchmark")} on {j.ModelId}{(j.NodeId is { } n ? " on node " + n : "")}"));
        string wanted = requested == JobKind.Tuning ? "A tuning run" : "A benchmark";
        string count = running.Count == 1 ? "1 job already runs" : running.Count.ToString(CultureInfo.InvariantCulture) + " jobs already run";
        return string.Create(CultureInfo.InvariantCulture,
            $"{count} (the limit, {JobOptions.SectionName}:{nameof(JobOptions.MaxConcurrent)}, is {limit}): {jobs}. {wanted} on {modelId} on node {nodeId} can't start until one finishes or is cancelled.");
    }

    /// <summary>Rerankers have no benchmark yet (embedding models get the pooling benchmark).</summary>
    /// <summary>
    /// The goal a run is actually scored on. An embedding model is always tuned on the embeddings preset and a
    /// generation model never (<see cref="WorkloadProfiles.PresetFor"/>). A llama.cpp model is always tuned on a
    /// workload preset too (MN-230, D130): its classic chat levels stop at its slots (at most 8,
    /// <see cref="BenchmarkSuite.ChatLevels"/>), so the classic goals' N=16 comparison has no measurement and
    /// <see cref="TuningScorer"/> would reject every candidate, baseline included, leaving a run without a winner.
    /// A classic goal asked for a vLLM model is kept exactly as before.
    /// </summary>
    private static TuningGoal EffectiveGoal(ModelDefinition model, TuningGoal goal) =>
        WorkloadProfiles.IsPreset(goal) || LlamaCppTuning.Applies(model) || SmallRuntimeTuning.Applies(model)
            ? WorkloadProfiles.PresetFor(goal, PoolingDetection.Detect(model) != PoolingMode.None)
            : goal;

    private static void ThrowIfUnsupported(ModelDefinition model)
    {
        if (PoolingDetection.Detect(model) == PoolingMode.Reranker)
        {
            throw new InvalidOperationException(BenchmarkSuite.RerankerUnsupported);
        }
    }

    private static string KindName(JobKind kind) => kind == JobKind.Tuning ? MaintenanceJobKinds.Tuning : MaintenanceJobKinds.Benchmark;

    /// <summary>" on node X" for a node other than the bootstrap node (single-node messages stay as before P2).</summary>
    private string NodeSuffix(string nodeId) =>
        string.Equals(nodeId, _bootstrapNodeId, StringComparison.Ordinal) ? "" : " on node " + nodeId;

    private void ThrowIfBusy(string nodeId, JobKind requested, string modelId)
    {
        lock (_gate)
        {
            ThrowIfBusyLocked(nodeId, requested, modelId);
        }
    }

    /// <summary>One job per node (<c>job_conflict</c> naming the node), then the global limit (<c>job_limit_reached</c>).</summary>
    private void ThrowIfBusyLocked(string nodeId, JobKind requested, string modelId)
    {
        if (_jobs.TryGetValue(nodeId, out var running))
        {
            throw new JobConflictException(ConflictMessage(running.Snapshot(), requested, nameNode: NodeSuffix(nodeId).Length > 0))
            {
                Code = ErrorCodes.JobConflict,
                NodeId = nodeId,
            };
        }

        if (_jobs.Count >= _maxConcurrent)
        {
            var all = _jobs.Values.OrderBy(j => j.StartedAt).Select(j => j.Snapshot()).ToList();
            throw new JobConflictException(LimitMessage(all, _maxConcurrent, requested, modelId, nodeId))
            {
                Code = ErrorCodes.JobLimitReached,
                Limit = _maxConcurrent,
            };
        }
    }

    /// <summary>
    /// Reserves the node (one job per node, the global limit), takes the model's ownership record when a store is
    /// composed, then opens the node's maintenance window. A refusal leaves nothing behind.
    /// </summary>
    private async Task<JobState> BeginAsync(JobKind kind, ModelDefinition saved, string nodeId, TimeSpan estimate, TuningGoal? goal, CancellationToken ct)
    {
        // Decided before anything is reserved, so a refusal or a cancellation here leaves nothing behind (D101).
        bool scoped = nodeScopedMaintenance;
        if (nodeScopedPolicy is not null)
        {
            try
            {
                scoped = await nodeScopedPolicy.IsOnAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                scoped = false; // the node registry cannot be read: a global window (P1), never a wrong node scope
                JobLog.ScopeUndecided(logger, ex, saved.Id);
            }
        }

        var now = _time.GetUtcNow();
        JobState job;
        lock (_gate)
        {
            ThrowIfBusyLocked(nodeId, kind, saved.Id);
            job = new JobState(Guid.NewGuid().ToString("N"), kind, saved.Id, nodeId, now, now + estimate, goal);
            _jobs[nodeId] = job;
        }

        try
        {
            await AcquireOwnershipAsync(job, saved, now, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (_jobs.TryGetValue(nodeId, out var held) && ReferenceEquals(held, job))
                {
                    _jobs.Remove(nodeId);
                }
            }

            job.Cts.Dispose();
            throw;
        }

        maintenance.Enter(new MaintenanceState(job.JobId, KindName(kind), saved.Id, now, now + estimate, scoped ? nodeId : null));
        return job;
    }

    private async Task AcquireOwnershipAsync(JobState job, ModelDefinition saved, DateTimeOffset now, CancellationToken ct)
    {
        if (ownership is null)
        {
            return;
        }

        var record = new OwnershipRecord
        {
            Id = "job-" + job.JobId,
            Kind = OwnershipKind.Job,
            ModelId = saved.Id,
            PlacementGeneration = saved.Placement?.Generation ?? 0,
            Stage = KindName(job.Kind),
            StartedAt = now,
            UpdatedAt = now,
            Actor = AuditActors.SystemJob,
            Description = (job.Kind == JobKind.Tuning ? "a tuning run" : "a benchmark") + " on node " + job.NodeId,
            ReferenceId = job.JobId,
        };
        // Claim the id before the store has it, so a concurrent boot/node recovery (ReleaseStaleOwnershipAsync) never sees
        // this job's fresh record as stale and releases it.
        lock (_gate)
        {
            job.OwnershipId = record.Id;
        }

        OwnershipAcquireResult result;
        try
        {
            result = await ownership.TryAcquireAsync(record, ct).ConfigureAwait(false);
        }
        catch
        {
            ClearOwnershipId(job);
            throw;
        }

        if (!result.Acquired)
        {
            ClearOwnershipId(job);
            throw result.Holder is { } holder ? new ModelBusyException(saved.Id, holder) : new ModelBusyException($"Model '{saved.Id}' is busy.");
        }
    }

    private void ClearOwnershipId(JobState job)
    {
        lock (_gate)
        {
            job.OwnershipId = null;
        }
    }

    /// <summary>
    /// Runs <paramref name="run"/> in the background. Whatever happens in it, the job ends: an exception escaping the
    /// runner (its own handlers catch the expected ones) still exits the window, releases the record and frees the node.
    /// </summary>
    private void StartRunner(JobState job, Func<Task> run)
    {
        lock (_gate)
        {
            _runners.RemoveAll(t => t.IsCompleted);
            _runners.Add(Task.Run(async () =>
            {
                // The job's agent operations (stops, launches, restores) are system:job's in the audit log (MN-116).
                using var audit = AuditScope.Begin(AuditActors.SystemJob);
                try
                {
                    await run().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    JobLog.JobFailed(logger, ex, job.ModelId);
                    await FinishAsync(job, FailedOutcome(job, ex)).ConfigureAwait(false);
                }
            }));
        }
    }

    private JobOutcome FailedOutcome(JobState job, Exception ex) =>
        new(job.JobId, job.Kind, job.ModelId, "", "Failed", _time.GetUtcNow(), ex.Message);

    /// <summary>Ends a job once (later calls are no-ops): exits its window, releases its record, frees its node.</summary>
    private async Task FinishAsync(JobState job, JobOutcome outcome)
    {
        lock (_gate)
        {
            if (job.Finished)
            {
                return;
            }

            job.Finished = true;
        }

        maintenance.Exit(job.JobId);
        if (ownership is not null && job.OwnershipId is { } recordId)
        {
            try
            {
                await ownership.ReleaseAsync(recordId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Boot recovery releases a record this process could not.
                JobLog.ReleaseFailed(logger, ex, job.ModelId);
            }
        }

        lock (_gate)
        {
            _last = outcome;
            _lastByNode[job.NodeId] = outcome;
            if (_jobs.TryGetValue(job.NodeId, out var held) && ReferenceEquals(held, job))
            {
                _jobs.Remove(job.NodeId);
            }
        }

        job.Cts.Dispose();
    }

    private JobState? Oldest() =>
        _jobs.Values.OrderBy(j => j.StartedAt).ThenBy(j => j.NodeId, StringComparer.Ordinal).FirstOrDefault();

    private void Update(JobState job, Action<JobState> change)
    {
        lock (_gate)
        {
            change(job);
        }
    }

    private void SetEstimatedEnd(JobState job, DateTimeOffset end)
    {
        Update(job, j => j.EstimatedEnd = end);
        maintenance.UpdateEstimate(job.JobId, end);
    }

    // ---------------------------------------------------------------- benchmark

    private async Task RunBenchmarkAsync(JobState job, ModelDefinition saved)
    {
        var ct = job.Cts.Token;
        var startedAt = job.StartedAt;
        ModelDefinition settings = saved;
        string? hash = null;
        BenchmarkResult? result = null;
        string? error = null;
        bool cancelled = false;
        try
        {
            var (quietNotes, idle) = await QuietTheBoxAsync(job, ct).ConfigureAwait(false);

            var status = await runtime.GetStatusAsync(job.ModelId, ct).ConfigureAwait(false);
            if (status.State != ModelState.Ready)
            {
                throw new InvalidOperationException($"{saved.ServedName} stopped being Ready (state: {status.State}).");
            }

            settings = await launcher.GetLaunchedDefinitionAsync(job.ModelId, ct).ConfigureAwait(false) ?? saved;
            hash = await launcher.GetLaunchedConfigHashAsync(job.ModelId, ct).ConfigureAwait(false)
                ?? await launcher.ComputeConfigHashAsync(settings, ct).ConfigureAwait(false);

            var measureStart = _time.GetUtcNow();
            var target = await TargetAsync(job, settings, status, ct).ConfigureAwait(false);
            var raw = await suite.RunAsync(target, SuiteProgress(job, measureStart, remainingAfter: TimeSpan.Zero), ct)
                .ConfigureAwait(false);
            result = WithQuietNotes(raw, quietNotes, idle);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            JobLog.JobFailed(logger, ex, job.ModelId);
        }

        var finishedAt = _time.GetUtcNow();
        var run = new BenchmarkRun(BenchmarkStore.NewId(startedAt), job.ModelId, BenchmarkOrigin.OnDemand, startedAt, finishedAt,
            settings, hash, result, error, cancelled, job.Preset)
        {
            Proxy = DataPathOf(job),
            Provenance = BenchmarkComparisons.Complete(ProvenanceOf(job), hash, DataPathOf(job), result is null ? [] : [result.Noise.Verdict]),
        };
        if (!cancelled)
        {
            await SaveQuietlyAsync(() => store.SaveBenchmarkAsync(run)).ConfigureAwait(false);
        }

        await FinishAsync(job, new JobOutcome(job.JobId, JobKind.Benchmark, job.ModelId, run.Id,
            cancelled ? "Cancelled" : error is null ? "Completed" : "Failed", finishedAt, error));
    }

    // ---------------------------------------------------------------- tuning

    private async Task RunTuningAsync(JobState job, TuningPreview preview, TuningGoal goal, IReadOnlyCollection<TuningDimension>? dimensions)
    {
        _ = dimensions;
        var ct = job.Cts.Token;
        var saved = preview.Baseline;
        var startedAt = job.StartedAt;
        var notes = new List<string>();
        var results = new List<TuningCandidateResult>();
        var status = TuningRunStatus.Completed;
        string? error = null;
        bool wasRunning = false;
        TuningCandidateResult? baseline = null;
        TuningCandidateResult? best = null;
        var profile = WorkloadProfiles.IsPreset(goal) ? Profile(goal) : null;
        int? confirmedWinner = null;

        try
        {
            var initial = await runtime.GetStatusAsync(job.ModelId, ct).ConfigureAwait(false);
            wasRunning = initial.State is ModelState.Starting or ModelState.Ready;

            var (quietNotes, idle) = await QuietTheBoxAsync(job, ct).ConfigureAwait(false);
            notes.AddRange(quietNotes);
            foreach (var step in preview.Plan.Steps)
            {
                if (step.SkipReason is not null && step.SkipReason != "Not selected.")
                {
                    notes.Add($"{step.Title}: {step.SkipReason}");
                }
                else if (step.Note is not null)
                {
                    notes.Add($"{step.Title}: {step.Note}");
                }
            }

            var measured = new HashSet<string>(StringComparer.Ordinal);
            int planned = preview.Plan.PreviewCandidates.Count;

            // Baseline: the saved settings, measured on the running container when it already has them.
            if (profile is not null && planned > 1)
            {
                Update(job, j => j.PendingConfirmations = Math.Min(3, preview.Plan.ExpectedCandidates));
                planned = preview.Plan.ExpectedCandidates;
            }

            baseline = await MeasureCandidateAsync(job, results.Count, "Baseline", "Baseline (saved settings)", saved,
                reuseRunning: preview.ReusesRunningBaseline, planned, preview.PerCandidate, quietNotes, idle, ct,
                preview.Plan.PreviewCandidates[0].Description).ConfigureAwait(false);
            measured.Add(TuningPlanner.Fingerprint(saved));
            if (baseline.Result is null)
            {
                results.Add(baseline);
                throw new InvalidOperationException($"The baseline (saved settings) could not be measured: {baseline.Reason}");
            }

            var baselineResult = baseline.Result;
            if (profile is not null)
            {
                notes.AddRange(WorkloadScorer.RelaxForBaseline(profile.Guards, baselineResult).Notes);
            }

            baseline = Evaluate(goal, profile, baselineResult, baseline);
            results.Add(baseline);
            PublishCandidates(job, results);
            best = TuningScorer.PickBest(results) ?? baseline;

            // Measures the candidates not measured yet (fingerprint of the effective configuration), in order.
            async Task MeasureAllAsync(IEnumerable<TuningCandidatePlan> candidates)
            {
                foreach (var candidate in candidates)
                {
                    if (!measured.Add(TuningPlanner.Fingerprint(candidate.Settings)))
                    {
                        continue;
                    }

                    planned = Math.Max(planned, results.Count + 1);
                    var measuredCandidate = await MeasureCandidateAsync(job, results.Count, candidate.Step, candidate.Label, candidate.Settings,
                        reuseRunning: false, planned, preview.PerCandidate, quietNotes, idle, ct, candidate.Description).ConfigureAwait(false);
                    if (measuredCandidate.Result is not null)
                    {
                        measuredCandidate = Evaluate(goal, profile, baselineResult, measuredCandidate);
                    }

                    results.Add(measuredCandidate);
                    PublishCandidates(job, results);
                }
            }

            foreach (var step in preview.Plan.Steps.Where(s => s.SkipReason is null))
            {
                int stepStart = results.Count;
                if (preview.Plan.Search is { } search)
                {
                    Update(job, j => j.StepDescription = step.Phase is null ? step.Description : $"{step.Phase} — {step.Title}: {step.Description}");
                    await MeasureAllAsync(SearchPlanner.CandidatesFor(step, best.Settings, search, notes)).ConfigureAwait(false);
                    await MeasureAllAsync(SearchPlanner.FollowUps(step, results.Skip(stepStart).ToList(), search)).ConfigureAwait(false);
                }
                else
                {
                    await MeasureAllAsync(TuningPlanner.CandidatesFor(step, best.Settings)).ConfigureAwait(false);
                }

                // Coordinate descent: the next step builds on the best accepted result so far.
                best = TuningScorer.PickBest(results) ?? baseline;
            }

            if (profile is not null)
            {
                confirmedWinner = await ConfirmAsync(job, profile, saved, results, notes, preview.PerCandidate, quietNotes, idle, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = TuningRunStatus.Cancelled;
        }
        catch (TuningInfrastructureException ex)
        {
            // Bounded recovery failed (the failed candidate's container would not go): stop, restore, report.
            if (ex.Candidate is { } failedCandidate)
            {
                results.Add(failedCandidate);
                PublishCandidates(job, results);
            }

            status = TuningRunStatus.Failed;
            error = ex.Message;
            JobLog.JobFailed(logger, ex, job.ModelId);
        }
        catch (Exception ex)
        {
            status = TuningRunStatus.Failed;
            error = ex.Message;
            JobLog.JobFailed(logger, ex, job.ModelId);
        }

        // Restore the saved settings; never leave a candidate running. Not cancellable.
        Update(job, j =>
        {
            j.Phase = "Restoring the saved settings";
            j.CandidateLabel = null;
        });
        string? restoreNote = await RestoreAsync(job.ModelId, saved, wasRunning).ConfigureAwait(false);
        if (restoreNote is not null)
        {
            notes.Add(restoreNote);
        }

        TuningCandidateResult? winner;
        double? gain;
        if (profile is null)
        {
            winner = status == TuningRunStatus.Completed ? TuningScorer.PickBest(results) : null;
            gain = winner is null || baseline is null ? null : TuningScorer.GainPercent(winner.Score, baseline.Score);
        }
        else
        {
            // Workload presets: the confirmation stage decided (scores there are relative to the re-measured baseline).
            winner = status == TuningRunStatus.Completed && confirmedWinner is { } w ? results.FirstOrDefault(c => c.Index == w) : null;
            gain = winner?.Score is { } s ? (s - 1) * 100 : null;
        }

        var finishedAt = _time.GetUtcNow();
        var run = new TuningRun(BenchmarkStore.NewId(startedAt), job.ModelId, goal, startedAt, finishedAt, status, saved,
            results, winner?.Index, gain, error, notes, profile is null ? 1 : TuningRun.CurrentSchemaVersion)
        {
            Proxy = DataPathOf(job),
            Provenance = BenchmarkComparisons.Complete(ProvenanceOf(job), null, DataPathOf(job),
                results.Where(c => c.Result is not null).Select(c => c.Result!.Noise.Verdict)),
        };
        await SaveQuietlyAsync(() => store.SaveTuningAsync(run)).ConfigureAwait(false);

        await FinishAsync(job, new JobOutcome(job.JobId, JobKind.Tuning, job.ModelId, run.Id, status.ToString(), finishedAt, error));
    }

    private WorkloadProfile Profile(TuningGoal goal) =>
        WorkloadProfiles.For(goal, jobOptions.Guards.TryGetValue(goal, out var guards) ? guards : null);

    private static TuningCandidateResult Evaluate(TuningGoal goal, WorkloadProfile? profile, BenchmarkResult baseline, TuningCandidateResult candidate)
    {
        if (profile is not null)
        {
            var evaluation = WorkloadScorer.Evaluate(profile, baseline, candidate.Result!);
            var range = WorkloadScorer.Range(profile, baseline, candidate.Result!);
            return candidate with
            {
                Outcome = evaluation.Accepted ? CandidateOutcome.Accepted : CandidateOutcome.Rejected,
                Reason = evaluation.Reason,
                Score = evaluation.Score,
                ScoreMin = range?.Min,
                ScoreMax = range?.Max,
            };
        }

        var verdict = TuningScorer.Evaluate(goal, baseline, candidate.Result!);
        return candidate with
        {
            Outcome = verdict.Accepted ? CandidateOutcome.Accepted : CandidateOutcome.Rejected,
            Reason = verdict.Reason,
            Score = verdict.Score,
        };
    }

    public const string ConfirmationStep = "Confirmation";

    /// <summary>
    /// The confirmation stage (C4): the top two accepted candidates and the baseline are re-measured with
    /// <see cref="ModelJobOptions.ConfirmationRepetitions"/> repetitions, each from a fresh start, in randomized order.
    /// The best re-measured candidate wins only when it beats the re-measured baseline by at least 5 % with
    /// non-overlapping per-repetition ranges; otherwise the baseline is kept. Returns the winning candidate's index
    /// (the baseline's re-measurement when it is kept), or null when nothing could be confirmed.
    /// </summary>
    private async Task<int?> ConfirmAsync(
        JobState job, WorkloadProfile profile, ModelDefinition saved, List<TuningCandidateResult> results, List<string> notes,
        TimeSpan perCandidate, IReadOnlyList<string> quietNotes, GpuIdleReading idle, CancellationToken ct)
    {
        var top = results
            .Where(c => c.Index != 0 && c.Outcome == CandidateOutcome.Accepted && c.Score is not null)
            .OrderByDescending(c => c.Score)
            .Take(2)
            .ToList();
        if (top.Count == 0)
        {
            notes.Add("No candidate was accepted, so the saved settings stay (nothing to confirm).");
            return results.FirstOrDefault(c => c.Index == 0 && c.Outcome == CandidateOutcome.Accepted)?.Index;
        }

        var lineup = new List<(string Label, ModelDefinition Settings, bool IsBaseline)> { ("Baseline (saved settings)", saved, true) };
        lineup.AddRange(top.Select(c => (c.Label, c.Settings, false)));
        var random = jobOptions.ConfirmationRandom ?? Random.Shared;
        var order = lineup.OrderBy(_ => random.Next()).ToList();

        Update(job, j =>
        {
            j.Repetitions = Math.Max(1, jobOptions.ConfirmationRepetitions);
            j.PendingConfirmations = 0;
            j.StepDescription = "Confirmation — the top two and the baseline re-measured with more repetitions, in random order.";
        });
        int planned = results.Count + order.Count;
        var confirmed = new List<(TuningCandidateResult Result, bool IsBaseline)>(order.Count);
        foreach (var (label, settings, isBaseline) in order)
        {
            string? description = results.FirstOrDefault(c => c.Label == label)?.Description;
            var measured = await MeasureCandidateAsync(job, results.Count, ConfirmationStep, $"{label} (confirm)", settings,
                reuseRunning: false, planned, perCandidate, quietNotes, idle, ct,
                "Re-measure with more repetitions to separate a real gain from noise." + (description is null ? "" : " " + description)).ConfigureAwait(false);
            results.Add(measured);
            confirmed.Add((measured, isBaseline));
            PublishCandidates(job, results);
        }

        var baseline = confirmed.First(c => c.IsBaseline).Result;
        if (baseline.Result is null)
        {
            notes.Add($"The baseline could not be re-measured ({baseline.Reason}), so no candidate is confirmed and the saved settings stay.");
            return null;
        }

        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Step == ConfirmationStep && results[i].Result is not null)
            {
                results[i] = Evaluate(profile.Goal, profile, baseline.Result, results[i]);
            }
        }

        PublishCandidates(job, results);
        var baselineConfirmed = results.First(c => c.Index == baseline.Index);
        var best = results
            .Where(c => c.Step == ConfirmationStep && c.Index != baseline.Index && c.Outcome == CandidateOutcome.Accepted && c.Score is not null)
            .OrderByDescending(c => c.Score)
            .FirstOrDefault();
        if (best is not null && WorkloadScorer.BeatsBaseline(best.Score, Range(best), baselineConfirmed.Score, Range(baselineConfirmed)))
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"Confirmed: {best.Label} beats the re-measured baseline by {(best.Score!.Value - 1) * 100:0.#} % with non-overlapping ranges."));
            return best.Index;
        }

        notes.Add(best is null
            ? "Kept the saved settings: no candidate was accepted when re-measured."
            : string.Create(CultureInfo.InvariantCulture,
                $"Kept the saved settings: the best re-measured candidate ({best.Label}, ×{best.Score:0.000}, range {best.ScoreMin:0.000}–{best.ScoreMax:0.000}) did not beat the baseline (range {baselineConfirmed.ScoreMin:0.000}–{baselineConfirmed.ScoreMax:0.000}) by ≥ 5 % with non-overlapping ranges."));
        return baselineConfirmed.Outcome == CandidateOutcome.Accepted ? baselineConfirmed.Index : null;
    }

    private static (double Min, double Max)? Range(TuningCandidateResult candidate) =>
        candidate.ScoreMin is { } min && candidate.ScoreMax is { } max ? (min, max) : null;

    private void PublishCandidates(JobState job, List<TuningCandidateResult> results)
    {
        var copy = results.ToList();
        Update(job, j => j.Candidates = copy);
    }

    private async Task<TuningCandidateResult> MeasureCandidateAsync(
        JobState job, int index, string step, string label, ModelDefinition settings, bool reuseRunning, int planned,
        TimeSpan perCandidate, IReadOnlyList<string> quietNotes, GpuIdleReading idle, CancellationToken ct, string? description = null)
    {
        string hash = await launcher.ComputeConfigHashAsync(settings, ct).ConfigureAwait(false);
        int remaining;
        lock (_gate)
        {
            // Live ETA: the candidates left (plus pending confirmations) at this run's measured pace when there is one.
            remaining = Math.Max(0, planned - index - 1) + job.PendingConfirmations;
            perCandidate = job.MeasuredPerCandidate ?? perCandidate;
        }

        Update(job, j =>
        {
            j.CandidateLabel = label;
            j.CandidateDescription = description;
            j.CandidateNumber = index + 1;
            j.CandidateCount = Math.Max(j.CandidateCount, planned);
            j.Step = 0;
            j.TotalSteps = suite.StepCount;
        });

        double? startupSeconds = null;

        // A failed candidate this run launched is removed (verified gone) before the next one starts; a reused
        // baseline (the user's own running container) is left alone. When the removal itself fails, the run stops
        // and the exception carries this candidate so it is still recorded.
        async Task<TuningCandidateResult> FailAsync(string? reason, bool cleanup)
        {
            var failed = new TuningCandidateResult(index, step, label, settings, hash, CandidateOutcome.Failed, reason, null, null, startupSeconds,
                Description: description, EffectiveConfig: TuningDescriptions.Summary(settings));
            if (cleanup)
            {
                try
                {
                    await CleanupCandidateAsync(job, ct).ConfigureAwait(false);
                }
                catch (TuningInfrastructureException ex)
                {
                    ex.Candidate = failed;
                    throw;
                }
            }

            return failed;
        }

        ModelStatus status;
        if (reuseRunning)
        {
            status = await runtime.GetStatusAsync(job.ModelId, ct).ConfigureAwait(false);
        }
        else
        {
            SetEstimatedEnd(job, _time.GetUtcNow() + perCandidate + (perCandidate * remaining));
            Update(job, j => j.Phase = $"Starting {label}");
            var launchedAt = _time.GetUtcNow();
            var (launchFailure, needsCleanup) = await LaunchCandidateAsync(job, settings, ct).ConfigureAwait(false);
            if (launchFailure is not null)
            {
                return await FailAsync(launchFailure, needsCleanup).ConfigureAwait(false);
            }

            var (ready, failure) = await WaitForReadyAsync(job.ModelId, launchedAt, ct).ConfigureAwait(false);
            if (ready is null)
            {
                return await FailAsync(failure, cleanup: true).ConfigureAwait(false);
            }

            status = ready;
            var startup = _time.GetUtcNow() - launchedAt;
            startupSeconds = Math.Round(startup.TotalSeconds);
            startupTimes.Record(job.ModelId, startup);
        }

        if (status.State != ModelState.Ready)
        {
            return await FailAsync($"The model is not Ready (state: {status.State}).", cleanup: !reuseRunning).ConfigureAwait(false);
        }

        var (runtimeNotes, kvPool) = await ReadRuntimeNotesAsync(job.ModelId, settings, ct).ConfigureAwait(false);
        BenchmarkResult result;
        var benchmarkStart = _time.GetUtcNow();
        try
        {
            // A tuning candidate skips the thinking questions: they add minutes per candidate and tune nothing.
            var target = await TargetAsync(job, settings, status, ct).ConfigureAwait(false) with { Thinks = false };
            result = await suite.RunAsync(target,
                SuiteProgress(job, benchmarkStart, remainingAfter: perCandidate * remaining), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return await FailAsync($"Benchmark failed: {ex.Message}", cleanup: !reuseRunning).ConfigureAwait(false);
        }

        double benchmarkSeconds = Math.Round((_time.GetUtcNow() - benchmarkStart).TotalSeconds, 1);
        Update(job, j => j.RecordDuration(TimeSpan.FromSeconds((startupSeconds ?? 0) + benchmarkSeconds), counted: startupSeconds is not null));
        return new TuningCandidateResult(index, step, label, settings, hash, CandidateOutcome.Accepted, null,
            WithQuietNotes(result, quietNotes, idle), null, startupSeconds,
            Description: description, EffectiveConfig: TuningDescriptions.Summary(settings), RuntimeNotes: runtimeNotes, BenchmarkSeconds: benchmarkSeconds,
            KvPool: kvPool);
    }

    /// <summary>
    /// What vLLM reported resolving for this container (graph mode, speculative config, async scheduling, capped
    /// token budget) compared with what was requested; "as requested" when it reported nothing different or the log
    /// can't be read. Plus the KV pool line ("KV pool 507k tokens = 7.7 concurrent requests at 65,536 max model len"), null when not logged.
    /// </summary>
    private async Task<(IReadOnlyList<string> Notes, string? KvPool)> ReadRuntimeNotesAsync(string modelId, ModelDefinition requested, CancellationToken ct)
    {
        if (requested.Runtime != RuntimeKind.Vllm)
        {
            return ([], null); // The parser only knows vLLM's log lines, so "as requested" would be a claim nothing checked.
        }

        var lines = new List<string>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            await foreach (string line in runtime.StreamLogsAsync(modelId, tail: 4000, follow: false, cts.Token).ConfigureAwait(false))
            {
                lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ([RuntimeLogParser.AsRequested], null);
        }

        var report = RuntimeLogParser.Parse(lines);
        return (RuntimeLogParser.Compare(VllmArgsNormalizer.Apply(requested), report), report.KvPoolLine);
    }

    /// <summary>
    /// Launches a candidate. Returns the failure (null on success) and whether a container may have been touched (so
    /// it must be cleaned up). Memory admission refuses before anything changes; a start conflict with another model
    /// waits for that model (bounded by <see cref="ModelJobOptions.OtherModelStartWait"/>) and retries once; job
    /// cancellation propagates.
    /// </summary>
    private async Task<(string? Failure, bool NeedsCleanup)> LaunchCandidateAsync(JobState job, ModelDefinition settings, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await launcher.LaunchAsync(settings, new LaunchOptions(Force: false, Tuning: true), ct).ConfigureAwait(false);
                return (null, false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ModelMemoryAdmissionException ex)
            {
                return (ex.Message, false);
            }
            catch (ModelStartConflictException ex)
            {
                string other = ex.StartingModelId ?? "another model";
                if (attempt > 1)
                {
                    return ($"Not started: {other} was still starting. {ex.Message}", false);
                }

                var wait = jobOptions.OtherModelStartWait ?? _options.StartupTimeout;
                Update(job, j => j.Phase = $"Waiting for {other} to finish starting");
                if (!await WaitForOtherModelsAsync(job.ModelId, job.NodeId, wait, ct).ConfigureAwait(false))
                {
                    return (string.Create(CultureInfo.InvariantCulture,
                        $"Not started: {other} was still starting after {wait.TotalMinutes:0.#} min, so this candidate was skipped."), false);
                }
            }
            catch (Exception ex)
            {
                return ($"Launch failed: {ex.Message}", true);
            }
        }
    }

    /// <summary>
    /// True once no model other than <paramref name="modelId"/> is Starting on <paramref name="nodeId"/> (starts on other
    /// nodes don't conflict); false at the timeout.
    /// </summary>
    private async Task<bool> WaitForOtherModelsAsync(string modelId, string nodeId, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + timeout;
        while (true)
        {
            try
            {
                var onNode = await OnNodeAsync(nodeId, ct).ConfigureAwait(false);
                var statuses = await runtime.ListStatusAsync(ct).ConfigureAwait(false);
                if (!statuses.Any(s => s.State == ModelState.Starting && !string.Equals(s.Id, modelId, StringComparison.Ordinal) && onNode(s.Id)))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Status unavailable for now; keep waiting until the deadline.
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(jobOptions.PollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Removes the model's (failed) candidate container and verifies it is gone within
    /// <see cref="ModelJobOptions.CleanupTimeout"/>, so the next candidate starts on a clean box. When that fails the
    /// run stops: <see cref="TuningInfrastructureException"/>. Job cancellation propagates.
    /// </summary>
    private async Task CleanupCandidateAsync(JobState job, CancellationToken ct)
    {
        Update(job, j => j.Phase = "Removing the failed candidate's container");
        var timeout = jobOptions.CleanupTimeout;
        var deadline = _time.GetUtcNow() + timeout;
        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await runtime.StopAsync(job.ModelId, linked.Token).ConfigureAwait(false);
            while (true)
            {
                var status = await runtime.GetStatusAsync(job.ModelId, linked.Token).ConfigureAwait(false);
                if (status.State == ModelState.Stopped)
                {
                    return;
                }

                if (_time.GetUtcNow() >= deadline)
                {
                    throw new TuningInfrastructureException(string.Create(CultureInfo.InvariantCulture,
                        $"Tuning stopped: the failed candidate's container was still there (state: {status.State}) {timeout.TotalSeconds:0} s after removing it."));
                }

                await Task.Delay(jobOptions.PollInterval, _time, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TuningInfrastructureException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TuningInfrastructureException(string.Create(CultureInfo.InvariantCulture,
                $"Tuning stopped: removing the failed candidate's container did not finish within {timeout.TotalSeconds:0} s."));
        }
        catch (Exception ex)
        {
            throw new TuningInfrastructureException($"Tuning stopped: could not remove the failed candidate's container: {ex.Message}", ex);
        }
    }

    private async Task<(ModelStatus? Ready, string? Failure)> WaitForReadyAsync(string modelId, DateTimeOffset launchedAt, CancellationToken ct)
    {
        var timeout = jobOptions.CandidateStartTimeout ?? (_options.StartupTimeout * 1.5);
        while (true)
        {
            ModelStatus status;
            try
            {
                status = await runtime.GetStatusAsync(modelId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                return (null, $"Status check failed while starting: {ex.Message}");
            }

            switch (status.State)
            {
                case ModelState.Ready:
                    return (status, null);
                case ModelState.Failed:
                    return (null, "Failed to start: " + FailureCause(status.Error));
                case ModelState.Stopped:
                    return (null, "The container disappeared while starting.");
            }

            if (_time.GetUtcNow() - launchedAt > timeout)
            {
                return (null, string.Create(CultureInfo.InvariantCulture, $"Did not become Ready within {timeout.TotalMinutes:0} min."));
            }

            await Task.Delay(jobOptions.PollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The root cause line of a failure report (<c>Cause: …</c>), else its last line.</summary>
    public static string FailureCause(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "no details were reported.";
        }

        var lines = error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string line = lines.Length > 0 && lines[0].StartsWith(FailureReport.CausePrefix, StringComparison.Ordinal)
            ? lines[0][FailureReport.CausePrefix.Length..]
            : lines.LastOrDefault() ?? error.Trim();
        return line.Length <= 240 ? line : string.Concat(line.AsSpan(0, 239), "…");
    }

    /// <summary>
    /// Puts the saved settings back: relaunch unless the container already runs them unlabelled, then wait (bounded by
    /// the candidate start timeout) until it is Ready and report when it isn't; stop it if it was not running before.
    /// Not cancellable.
    /// </summary>
    private async Task<string?> RestoreAsync(string modelId, ModelDefinition saved, bool wasRunning)
    {
        try
        {
            if (!wasRunning)
            {
                await runtime.StopAsync(modelId).ConfigureAwait(false);
                return "The model was not running before tuning, so it was stopped again.";
            }

            bool tuningContainer = (await launcher.ListTuningContainersAsync().ConfigureAwait(false)).Contains(modelId, StringComparer.Ordinal);
            string? running = await launcher.GetLaunchedConfigHashAsync(modelId).ConfigureAwait(false);
            string wanted = await launcher.ComputeConfigHashAsync(saved).ConfigureAwait(false);
            if (!tuningContainer && string.Equals(running, wanted, StringComparison.Ordinal))
            {
                return null;
            }

            var launchedAt = _time.GetUtcNow();
            try
            {
                await launcher.LaunchAsync(saved, new LaunchOptions(Force: true)).ConfigureAwait(false);
            }
            catch (ModelStartConflictException)
            {
                // Another model is starting: wait for it (bounded), then try once more.
                await WaitForOtherModelsAsync(modelId, NodeOf(saved), jobOptions.OtherModelStartWait ?? _options.StartupTimeout, CancellationToken.None).ConfigureAwait(false);
                launchedAt = _time.GetUtcNow();
                await launcher.LaunchAsync(saved, new LaunchOptions(Force: true)).ConfigureAwait(false);
            }

            var (ready, failure) = await WaitForReadyAsync(modelId, launchedAt, CancellationToken.None).ConfigureAwait(false);
            return ready is not null
                ? "Restored the saved settings; the model is Ready again."
                : $"Restored the saved settings, but the model did not become Ready: {failure} Check it on the Dashboard.";
        }
        catch (Exception ex)
        {
            JobLog.RestoreFailed(logger, ex, modelId);
            return $"Could not restore the saved settings: {ex.Message}. Restart the model from the Dashboard.";
        }
    }

    // ---------------------------------------------------------------- quiet box

    /// <summary>
    /// With the API paused, waits until no Ready model has running or waiting requests (fresh samples after the
    /// pause), then samples the GPU's idle utilization. Returns the notes that make a result noisy.
    /// </summary>
    private async Task<(IReadOnlyList<string> Notes, GpuIdleReading Idle)> QuietTheBoxAsync(JobState job, CancellationToken ct)
    {
        var notes = new List<string>();
        Update(job, j => j.Phase = "Pausing the API and waiting for in-flight requests to finish");
        if (!await DrainAsync(job.StartedAt, job.NodeId, await GpuScopeAsync(job, ct).ConfigureAwait(false), ct).ConfigureAwait(false))
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"noisy: in-flight requests at start (still running after {jobOptions.DrainTimeout.TotalSeconds:0} s)"));
        }

        Update(job, j => j.Phase = "Checking that the GPU is idle");
        var idle = await SampleGpuIdleAsync(job.ModelId, ct).ConfigureAwait(false);
        if (idle.Busy)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture,
                $"noisy: the GPU was {idle.AveragePercent:0}% busy with work outside mirbadllm before measuring"));
        }

        return (notes, idle);
    }

    /// <summary>True when drained; false when requests were still in flight at the timeout. Every Ready model, on every node.</summary>
    public Task<bool> DrainAsync(DateTimeOffset since, CancellationToken ct = default) => DrainAsync(since, nodeId: null, ct);

    /// <summary>
    /// As <see cref="DrainAsync(DateTimeOffset, CancellationToken)"/>, waiting only for the Ready models on
    /// <paramref name="nodeId"/> (design §6.5 item 4: the other nodes keep serving and are not this job's noise); null =
    /// every node. A model the registry does not know is waited for (conservative).
    /// </summary>
    public Task<bool> DrainAsync(DateTimeOffset since, string? nodeId, CancellationToken ct) => DrainAsync(since, nodeId, gpuScope: null, ct);

    /// <summary>
    /// As <see cref="DrainAsync(DateTimeOffset, string?, CancellationToken)"/>; with <paramref name="gpuScope"/> (a model on
    /// discrete GPUs: the measured model and the models sharing its GPUs or the host, <see cref="NoiseScope.SharedWith"/>)
    /// only those are waited for, so a keep-serving model on another GPU of the node neither delays the run nor makes it
    /// noisy (W8 review MN-128: the drain was node-scoped while the noise check is GPU-scoped). Null = the node rule.
    /// </summary>
    private async Task<bool> DrainAsync(DateTimeOffset since, string? nodeId, IReadOnlySet<string>? gpuScope, CancellationToken ct)
    {
        var deadline = _time.GetUtcNow() + jobOptions.DrainTimeout;
        Func<string, bool> onNode = nodeId is null ? _ => true : await OnNodeAsync(nodeId, ct).ConfigureAwait(false);
        if (gpuScope is not null)
        {
            var nodeRule = onNode;
            onNode = id => nodeRule(id) && gpuScope.Contains(id);
        }

        while (true)
        {
            var statuses = await runtime.ListStatusAsync(ct).ConfigureAwait(false);
            bool busy = false;
            bool allFresh = true;
            var now = _time.GetUtcNow();
            foreach (var status in statuses.Where(s => s.State == ModelState.Ready && onNode(s.Id)))
            {
                var fresh = history.GetModel(status.Id, now - since + TimeSpan.FromSeconds(1)).Where(s => s.At >= since).ToList();
                if (fresh.Count == 0)
                {
                    allFresh = false;
                    continue;
                }

                var load = fresh[^1].Kpis.Load;
                if (load.RunningRequests > 0 || load.WaitingRequests > 0)
                {
                    busy = true;
                }
            }

            if (!busy && allFresh)
            {
                return true;
            }

            if (now >= deadline)
            {
                return !busy;
            }

            await Task.Delay(jobOptions.PollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The models a drain before measuring <paramref name="job"/>'s model waits for on a discrete node: the model itself
    /// and its noise scope (<see cref="NoiseScope.SharedWith"/>); null on a unified node, without a provenance source, or
    /// when the scope cannot be read (the node rule then applies, as before).
    /// </summary>
    private async Task<IReadOnlySet<string>?> GpuScopeAsync(JobState job, CancellationToken ct)
    {
        string? nodeId;
        lock (_gate)
        {
            nodeId = job.NodeId;
        }

        var measured = await provenance.TryDescribeAsync(job.ModelId, new BenchmarkEndpoint(ScopeOnlyUri, BenchmarkDataPaths.Direct, nodeId), ct)
            .ConfigureAwait(false);
        return measured?.Scope is { PerGpu: true } scope
            ? new HashSet<string>([job.ModelId, .. scope.SharedWith], StringComparer.Ordinal)
            : null;
    }

    /// <summary>A placeholder endpoint address: <see cref="GpuScopeAsync"/> reads only the scope, never the address.</summary>
    private static readonly Uri ScopeOnlyUri = new("http://scope.invalid/");

    /// <summary>
    /// A filter for model ids on <paramref name="nodeId"/>: false for a model the registry places on another node, true
    /// otherwise (unknown ids included, so a single node behaves exactly as before P2).
    /// </summary>
    private async Task<Func<string, bool>> OnNodeAsync(string nodeId, CancellationToken ct)
    {
        var elsewhere = (await registry.ListAsync(ct).ConfigureAwait(false))
            .Where(m => !string.Equals(NodeOf(m), nodeId, StringComparison.Ordinal))
            .Select(m => m.Id)
            .ToHashSet(StringComparer.Ordinal);
        return id => !elsewhere.Contains(id);
    }

    private static BenchmarkResult WithQuietNotes(BenchmarkResult result, IReadOnlyList<string> notes, GpuIdleReading idle)
    {
        var reasons = notes.Concat(result.Noise.Reasons).ToList();
        return result with
        {
            Noise = result.Noise with
            {
                Noisy = reasons.Count > 0,
                Reasons = reasons,
                IdleGpuPercent = idle.AveragePercent,
            },
        };
    }

    private SyncProgress<BenchmarkProgress> SuiteProgress(JobState job, DateTimeOffset measureStart, TimeSpan remainingAfter) =>
        new SyncProgress<BenchmarkProgress>(p =>
        {
            var now = _time.GetUtcNow();
            DateTimeOffset? end = null;
            Update(job, j =>
            {
                j.Step = p.Step;
                j.TotalSteps = p.TotalSteps;
                j.Phase = j.Kind == JobKind.Tuning && j.CandidateLabel is not null
                    ? $"Benchmarking {j.CandidateLabel}: {p.Phase}"
                    : $"Benchmarking: {p.Phase}";

                // Re-project the end once enough of the suite has run to extrapolate honestly.
                double done = (double)(p.Step - 1) / Math.Max(1, p.TotalSteps);
                var elapsed = now - measureStart;
                if (done >= 0.2 && elapsed > TimeSpan.FromSeconds(30))
                {
                    end = measureStart + (elapsed / done) + remainingAfter;
                }
            });

            if (end is { } e)
            {
                SetEstimatedEnd(job, e);
            }
        });

    /// <summary>The suite's target: the address <see cref="IBenchmarkTargetResolver"/> chose, whose data path the job records.</summary>
    private async Task<BenchmarkTarget> TargetAsync(JobState job, ModelDefinition settings, ModelStatus status, CancellationToken ct)
    {
        string modelId = job.ModelId;
        var endpoint = await _targets.ResolveAsync(modelId, status, ct).ConfigureAwait(false);
        var measured = await provenance.TryDescribeAsync(modelId, endpoint, ct).ConfigureAwait(false);
        int? repetitions;
        TuningGoal? preset;
        lock (_gate)
        {
            repetitions = job.Repetitions;
            preset = job.Preset;
            job.DataPath = endpoint.DataPath;
            job.Provenance ??= measured?.Provenance;
        }

        // vLLM exactly as before; a llama.cpp model with its per-slot context, slots and runtime (MN-225).
        return BenchmarkSuite.TargetFor(modelId, settings, endpoint.BaseUri, preset, repetitions) with
        {
            Scope = measured?.Scope,
        };
    }

    /// <summary>Provenance of the job's first measured instance (MN-112); null without a provenance source.</summary>
    private BenchmarkProvenance? ProvenanceOf(JobState job)
    {
        lock (_gate)
        {
            return job.Provenance;
        }
    }

    /// <summary>
    /// The run's <c>proxy</c> provenance. A direct run stores nothing (null reads as direct), so today's single-host
    /// wiring writes the same benchmark and tuning JSON as before (W3 review MN-094).
    /// </summary>
    private string? DataPathOf(JobState job)
    {
        lock (_gate)
        {
            return job.DataPath == BenchmarkDataPaths.Direct ? null : job.DataPath;
        }
    }

    private async Task<bool> BaselineIsRunningAsync(ModelDefinition saved, CancellationToken ct)
    {
        var status = await runtime.GetStatusAsync(saved.Id, ct).ConfigureAwait(false);
        if (status.State != ModelState.Ready)
        {
            return false;
        }

        string? running = await launcher.GetLaunchedConfigHashAsync(saved.Id, ct).ConfigureAwait(false);
        return running is not null &&
            string.Equals(running, await launcher.ComputeConfigHashAsync(saved, ct).ConfigureAwait(false), StringComparison.Ordinal) &&
            !(await launcher.ListTuningContainersAsync(ct).ConfigureAwait(false)).Contains(saved.Id, StringComparer.Ordinal);
    }

    /// <summary>Inspects <paramref name="folder"/> on <paramref name="nodeId"/> (null when no inspector is composed or it fails).</summary>
    private ModelFolderInspection? InspectFolder(string nodeId, string folder)
    {
        if (inspectOnNode is null && inspect is null)
        {
            return null;
        }

        try
        {
            return inspectOnNode is not null ? inspectOnNode(nodeId, folder) : inspect!(folder);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Inspects <paramref name="model"/>'s folder on the node a job on it holds (<see cref="NodeOf"/>).</summary>
    private ModelFolderInspection? Inspect(ModelDefinition model) => InspectFolder(NodeOf(model), model.ModelPath);

    /// <summary>A llama.cpp model's GGUF summary on its node (MN-225); null for vLLM, without an inspector, or when it fails.</summary>
    private GgufSummary? InspectGguf(ModelDefinition model)
    {
        if (inspectGguf is null || !LlamaCppTuning.Applies(model))
        {
            return null;
        }

        try
        {
            return inspectGguf(NodeOf(model), model);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task SaveQuietlyAsync(Func<Task> save)
    {
        try
        {
            await save().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            JobLog.SaveFailed(logger, ex);
        }
    }

    /// <summary>Reports synchronously on the caller's thread (unlike <see cref="Progress{T}"/>, which posts).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class JobState(string jobId, JobKind kind, string modelId, string nodeId, DateTimeOffset startedAt, DateTimeOffset estimatedEnd, TuningGoal? goal)
    {
        public string JobId { get; } = jobId;

        public JobKind Kind { get; } = kind;

        public string ModelId { get; } = modelId;

        /// <summary>The node the job holds.</summary>
        public string NodeId { get; } = nodeId;

        /// <summary>The ownership record this job holds (when a store is composed).</summary>
        public string? OwnershipId { get; set; }

        public DateTimeOffset StartedAt { get; } = startedAt;

        public DateTimeOffset EstimatedEnd { get; set; } = estimatedEnd;

        public TuningGoal? Goal { get; } = goal;

        public string Phase { get; set; } = "Starting";

        public int Step { get; set; }

        public int TotalSteps { get; set; }

        public string? CandidateLabel { get; set; }

        /// <summary>What the current candidate tries to achieve (D1).</summary>
        public string? CandidateDescription { get; set; }

        /// <summary>The phase and step being searched, with its description.</summary>
        public string? StepDescription { get; set; }

        /// <summary>Confirmation measurements still to come after the search (for the live ETA).</summary>
        public int PendingConfirmations { get; set; }

        private TimeSpan _measuredTotal;
        private int _measuredCount;

        /// <summary>This run's average start + benchmark of launched candidates; null until one finished.</summary>
        public TimeSpan? MeasuredPerCandidate => _measuredCount == 0 ? null : _measuredTotal / _measuredCount;

        public void RecordDuration(TimeSpan duration, bool counted)
        {
            if (counted)
            {
                _measuredTotal += duration;
                _measuredCount++;
            }
        }

        /// <summary>Workload preset the suite runs (null = classic suite).</summary>
        public TuningGoal? Preset { get; set; }

        /// <summary>Repetitions per timed case (null = the suite's default).</summary>
        public int? Repetitions { get; set; }

        /// <summary>Data path of the load (<see cref="BenchmarkDataPaths"/>), once a target was resolved.</summary>
        public string? DataPath { get; set; }

        /// <summary>Provenance of the first measured instance (MN-112), once a target was resolved with a source.</summary>
        public BenchmarkProvenance? Provenance { get; set; }

        public int CandidateNumber { get; set; }

        public int CandidateCount { get; set; }

        public IReadOnlyList<TuningCandidateResult> Candidates { get; set; } = [];

        public bool Cancelling { get; set; }

        /// <summary>Set once by <c>FinishAsync</c>.</summary>
        public bool Finished { get; set; }

        public CancellationTokenSource Cts { get; } = new();

        public JobSnapshot Snapshot() => new(JobId, Kind, ModelId, StartedAt, EstimatedEnd, Phase, Step, TotalSteps,
            CandidateLabel, CandidateNumber, CandidateCount, Candidates, Goal, Cancelling,
            CandidateDescription ?? StepDescription, NodeId);
    }
}

internal static partial class JobLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Benchmark/tuning job on {ModelId} failed.")]
    public static partial void JobFailed(ILogger logger, Exception exception, string modelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not restore the saved settings of {ModelId} after tuning.")]
    public static partial void RestoreFailed(ILogger logger, Exception exception, string modelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save a benchmark/tuning result.")]
    public static partial void SaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Relaunched {ModelId} with its saved settings: an interrupted tuning run had left a candidate running.")]
    public static partial void RecoveredTuningContainer(ILogger logger, string modelId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Boot recovery of interrupted tuning runs failed.")]
    public static partial void RecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the node registry to scope the job window of {ModelId}; it is global.")]
    public static partial void ScopeUndecided(ILogger logger, Exception exception, string modelId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release the job's ownership record of {ModelId}; boot recovery releases it.")]
    public static partial void ReleaseFailed(ILogger logger, Exception exception, string modelId);
}
