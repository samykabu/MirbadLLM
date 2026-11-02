using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>The latest telemetry sample the master received from a node (design §8.1).</summary>
/// <param name="NodeId">Node id.</param>
/// <param name="BootId">Agent boot the sample belongs to.</param>
/// <param name="Sample">The newest sample of that boot the master has.</param>
/// <param name="ReceivedAt">Master clock when the pull that returned it completed (freshness never uses the agent clock).</param>
public sealed record NodeTelemetryReading(string NodeId, string BootId, NodeSample Sample, DateTimeOffset ReceivedAt);

/// <summary>Latest telemetry per node, read by the remote probes (<see cref="RemoteMemoryProbe"/> and friends).</summary>
public interface INodeTelemetry
{
    /// <summary>A reading older than this (by <see cref="NodeTelemetryReading.ReceivedAt"/>) is stale: the node is not reporting.</summary>
    TimeSpan MaxSampleAge { get; }

    /// <summary>The latest reading of a node, or null when the master has none.</summary>
    NodeTelemetryReading? GetLatest(string nodeId);
}

/// <summary>
/// The master half of the telemetry split (design §8.1), which replaced the pre-P1 single-host sampler (MN-040; deleted by
/// MN-047, D60): every <see cref="MetricsOptions.SampleInterval"/> it pulls <c>GET /telemetry?bootId=&amp;afterSeq=</c>
/// from every approved node, turns each <see cref="NodeSample"/> into a host sample keyed by node and, per Ready model,
/// parses the raw scrape with <see cref="PrometheusText"/> and computes KPIs with <see cref="ModelKpiCalculator"/>
/// against that model's previous scrape, exactly as the pre-P1 sampler did, into <see cref="InMemoryMetricsHistory"/>.
/// <para>Cursor rules: the cursor is <c>(bootId, seq)</c> per node. A failed pull (partition, timeout, agent error)
/// leaves it unchanged, so the next successful pull back-fills the real samples from the agent's 15-minute ring, in time
/// order, in pages of <see cref="PageSize"/>. A different boot id, or <c>gap: true</c> in the same boot, records a
/// <see cref="MetricsGap"/> from the last sample before to the first sample after, and starts fresh rate baselines: a
/// counter difference across a gap is not a rate (the first scrape after it has <see cref="ModelSample.HasRates"/>
/// false). Nothing is interpolated.</para>
/// <para>Model memory per host sample follows <see cref="ModelMemoryAttribution.WithModelMemory"/>: the models scraped in the
/// sample (Ready at that time), plus, on the newest sample of a pass, the Starting/Stopping instances the node status
/// cache reports. GPU memory is attributed per model only when every running container model's container id is known
/// (from the status cache); otherwise each model shows its budget. A process instance (llama.cpp, <c>delivery: process</c>)
/// never takes GPU process rows: it shows its memory measured at start (the status cache's <c>MeasuredGpuGiB</c>), else
/// its budget (MN-223, D129 (1)).</para>
/// </summary>
public sealed partial class RemoteTelemetryCollector(
    INodeRegistry nodes,
    IAgentClientFactory agents,
    IModelRegistry registry,
    InMemoryMetricsHistory history,
    IOptions<MetricsOptions> metricsOptions,
    TimeProvider timeProvider,
    ILogger<RemoteTelemetryCollector> logger,
    INodeStatusCache? statusCache = null) : BackgroundService, INodeTelemetry
{
    /// <summary>Samples requested per pull; 15 minutes at 5 s is 180, so one page normally covers a whole ring.</summary>
    public const int PageSize = 200;

    /// <summary>Pages pulled per node per pass at most (a confused agent cannot keep a pass busy forever).</summary>
    public const int MaxPagesPerPass = 10;

    private readonly MetricsOptions _options = metricsOptions.Value;
    private readonly ConcurrentDictionary<string, NodeCursor> _cursors = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NodeTelemetryReading> _latest = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _pass = new(1, 1);

    /// <inheritdoc />
    public TimeSpan MaxSampleAge
    {
        get
        {
            var threeIntervals = _options.SampleInterval * 3;
            return threeIntervals > Protocol.OfflineAfter ? threeIntervals : Protocol.OfflineAfter;
        }
    }

    /// <inheritdoc />
    public NodeTelemetryReading? GetLatest(string nodeId) => _latest.TryGetValue(nodeId, out var reading) ? reading : null;

    /// <inheritdoc />
    public override void Dispose()
    {
        _pass.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SampleInterval, timeProvider);
        do
        {
            try
            {
                await CollectOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPassFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Runs one pass over every approved, non-revoked node. Exposed for tests, which drive the collector without the
    /// background loop. Passes do not overlap.
    /// </summary>
    public async Task CollectOnceAsync(CancellationToken ct = default)
    {
        await _pass.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IReadOnlyList<ModelDefinition> models;
            IReadOnlyList<NodeRecord> records;
            try
            {
                models = await registry.ListAsync(ct).ConfigureAwait(false);
                records = await nodes.ListAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cursors stay where they are: the next pass back-fills these samples from the agents' rings.
                LogRegistryFailed(logger, ex);
                return;
            }

            var active = records.Where(n => n.Approved && !n.Revoked).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            foreach (string gone in _cursors.Keys.Where(id => !active.Contains(id)).ToList())
            {
                _cursors.TryRemove(gone, out _);
                _latest.TryRemove(gone, out _);
            }

            var definitions = models.ToDictionary(m => m.Id, StringComparer.Ordinal);
            foreach (var cursor in _cursors.Values)
            {
                foreach (string staleId in cursor.Baselines.Keys.Where(id => !definitions.ContainsKey(id)).ToList())
                {
                    cursor.Baselines.Remove(staleId);
                    history.RemoveModel(staleId);
                }
            }

            await Task.WhenAll(active.Select(id => CollectNodeAsync(id, definitions, ct))).ConfigureAwait(false);
        }
        finally
        {
            _pass.Release();
        }
    }

    private async Task CollectNodeAsync(string nodeId, IReadOnlyDictionary<string, ModelDefinition> definitions, CancellationToken ct)
    {
        var cursor = _cursors.GetOrAdd(nodeId, static _ => new NodeCursor());
        try
        {
            var client = agents.GetClient(nodeId);
            for (int page = 0; page < MaxPagesPerPass; page++)
            {
                var result = await client.GetTelemetryAsync(
                    new TelemetryQuery { BootId = cursor.BootId, AfterSeq = cursor.Seq, Max = PageSize }, ct).ConfigureAwait(false);
                bool more = result.Samples.Count >= PageSize;
                Apply(nodeId, cursor, result, definitions, newestOfPass: !more);
                if (!more)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is AgentUnreachableException or AgentException or HttpRequestException or OperationCanceledException or KeyNotFoundException)
        {
            // Partition, timeout, unknown or unusable node: the cursor is unchanged and the next pull back-fills.
            LogPullFailed(logger, nodeId, ex.Message);
        }
    }

    private void Apply(string nodeId, NodeCursor cursor, TelemetryPage page, IReadOnlyDictionary<string, ModelDefinition> definitions, bool newestOfPass)
    {
        bool bootChanged = cursor.BootId is not null && !string.Equals(cursor.BootId, page.BootId, StringComparison.Ordinal);
        if (bootChanged || (page.Gap && cursor.BootId is not null))
        {
            cursor.PendingGap ??= bootChanged ? MetricsGap.AgentRestarted : MetricsGap.SamplesDropped;
            cursor.Baselines.Clear();
            if (bootChanged)
            {
                cursor.Seq = null;
                LogBootChanged(logger, nodeId, cursor.BootId!, page.BootId);
            }
        }

        cursor.BootId = page.BootId;
        var samples = page.Samples.Where(s => cursor.Seq is not { } seq || s.Seq > seq).OrderBy(s => s.Seq).ToList();
        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            if (cursor.PendingGap is { } reason)
            {
                if (cursor.LastSampleAt is { } from)
                {
                    history.AddGap(nodeId, new MetricsGap(from, sample.At, reason));
                }

                cursor.PendingGap = null;
            }

            bool newest = newestOfPass && i == samples.Count - 1;
            RecordSample(nodeId, cursor, sample, definitions, newest);
            cursor.Seq = sample.Seq;
            cursor.LastSampleAt = cursor.LastSampleAt is { } last && last > sample.At ? last : sample.At;
        }

        if (samples.Count > 0)
        {
            _latest[nodeId] = new NodeTelemetryReading(nodeId, page.BootId, samples[^1], timeProvider.GetUtcNow());
        }
    }

    private void RecordSample(string nodeId, NodeCursor cursor, NodeSample sample, IReadOnlyDictionary<string, ModelDefinition> definitions, bool newest)
    {
        var instances = statusCache?.ListRuntimes(nodeId).ToDictionary(r => r.ModelId, StringComparer.Ordinal)
            ?? new Dictionary<string, NodeModelRuntime>(StringComparer.Ordinal);
        var running = new Dictionary<string, ModelMemoryAttribution.RunningModel>(StringComparer.Ordinal);
        var scraped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scrape in sample.Models)
        {
            if (!definitions.TryGetValue(scrape.ModelId, out var model) || IsPlacedElsewhere(model, nodeId))
            {
                continue;
            }

            scraped.Add(model.Id);
            running[model.Id] = ModelMemoryAttribution.Running(model, instances.GetValueOrDefault(model.Id));
            RecordModel(cursor, model.Id, scrape, sample.At);
        }

        // A model that was not scraped was not Ready at this sample: its next Ready starts a fresh rate baseline.
        foreach (string id in cursor.Baselines.Keys.Where(id => !scraped.Contains(id)).ToList())
        {
            cursor.Baselines.Remove(id);
        }

        if (newest)
        {
            foreach (var instance in instances.Values)
            {
                if (instance.State is ModelState.Starting or ModelState.Stopping &&
                    definitions.TryGetValue(instance.ModelId, out var model) && !IsPlacedElsewhere(model, nodeId))
                {
                    running.TryAdd(model.Id, ModelMemoryAttribution.Running(model, instance));
                }
            }
        }

        var processes = ModelMemoryAttribution.ContainersKnown(running) ? sample.GpuProcesses : null;
        var host = ModelMemoryAttribution.WithModelMemory(
            new HostSample(sample.At, sample.Memory.ToHostSnapshot(), ToGpuSnapshot(sample.Gpus)), running, processes);
        history.AddHost(nodeId, host with
        {
            Gpus = ToHostGpus(sample, host.ModelMemoryGiB, definitions, host.MemorySource == ModelMemorySource.Measured ? processes : null, running),
        });
    }

    /// <summary>
    /// The per-GPU readings of a discrete node (design §8.3 "one chart per GPU plus host RAM"; MN-120): one entry per GPU in
    /// index order, merging the GPU telemetry with the memory snapshot (either may lack a GPU or a field). Null on a unified
    /// node, so its samples stay exactly as in P1. Model memory per GPU comes from <paramref name="modelMemoryGiB"/> (each
    /// model's measured memory, or its budget): a model's total is split evenly over the GPUs its placement names (vLLM
    /// puts equal bytes on every tensor-parallel GPU, and the budget of a discrete placement is the total across them,
    /// design §5.1), matched by UUID, else by index, else (no GPUs in the placement) the node's only GPU. A model whose
    /// GPUs cannot be told is left out of every GPU's figures (it shows under "other processes").
    /// <para>A llama.cpp model that carries its own per-GPU budgets is split in their proportion instead
    /// (<see cref="BudgetShares"/>, MN-239 item 10): the launch sends exactly those budgets, so a 15/5 GiB split must not
    /// read 10/10 on the headroom bars.</para>
    /// <para>Measured per GPU (MN-106, D91): when the sample's memory is measured (<paramref name="processes"/>) and every
    /// process row of a model's container names its GPU (<see cref="GpuProcess.GpuUuid"/>, hostd with
    /// <c>--query-compute-apps=gpu_uuid,…</c>), the model's figure on each GPU is the sum of its rows on that GPU (its
    /// placed GPUs without a row show 0). A model with a row that names no GPU (an older hostd) keeps the even split.</para>
    /// </summary>
    internal static IReadOnlyList<HostGpuSample>? ToHostGpus(
        NodeSample sample,
        IReadOnlyDictionary<string, double>? modelMemoryGiB,
        IReadOnlyDictionary<string, ModelDefinition> definitions,
        IReadOnlyList<GpuProcess>? processes = null,
        IReadOnlyDictionary<string, ModelMemoryAttribution.RunningModel>? running = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(definitions);
        if (sample.Memory.Model != MemoryModel.Discrete)
        {
            return null;
        }

        var telemetry = sample.Gpus.GroupBy(g => g.Index).ToDictionary(g => g.Key, g => g.First());
        var memory = sample.Memory.Gpus.GroupBy(g => g.Index).ToDictionary(g => g.Key, g => g.First());
        var gpus = telemetry.Keys.Union(memory.Keys).Order().Select(index =>
        {
            var t = telemetry.GetValueOrDefault(index);
            var m = memory.GetValueOrDefault(index);
            string uuid = FirstText(t?.Uuid, m?.Uuid) ?? $"GPU-index-{index}";
            return (Index: index, Uuid: uuid, Telemetry: t, Memory: m);
        }).ToList();

        var measured = MeasuredPerGpu(processes, running);
        Dictionary<string, Dictionary<string, double>>? perGpu = null;
        if (modelMemoryGiB is not null)
        {
            perGpu = gpus.ToDictionary(g => g.Uuid, _ => new Dictionary<string, double>(StringComparer.Ordinal), StringComparer.Ordinal);
            foreach (var (modelId, gib) in modelMemoryGiB)
            {
                var definition = definitions.GetValueOrDefault(modelId);
                var placement = definition?.Placement;
                IReadOnlyList<string> targets;
                int share;
                if (placement?.GpuUuids is { Count: > 0 } uuids)
                {
                    targets = [.. gpus.Where(g => uuids.Contains(g.Uuid, StringComparer.Ordinal)).Select(g => g.Uuid)];
                    share = uuids.Count;
                }
                else if (placement?.GpuIndices is { Count: > 0 } indices)
                {
                    targets = [.. gpus.Where(g => indices.Contains(g.Index)).Select(g => g.Uuid)];
                    share = indices.Count;
                }
                else
                {
                    targets = gpus.Count == 1 ? [gpus[0].Uuid] : [];
                    share = 1;
                }

                if (measured.TryGetValue(modelId, out var onGpus))
                {
                    foreach (string uuid in targets)
                    {
                        perGpu[uuid][modelId] = 0;
                    }

                    foreach (var (uuid, used) in onGpus)
                    {
                        if (perGpu.TryGetValue(uuid, out var models))
                        {
                            models[modelId] = used;
                        }
                    }

                    continue;
                }

                var weights = BudgetShares(definition, placement);
                foreach (string uuid in targets)
                {
                    perGpu[uuid][modelId] = weights is null ? gib / share : gib * weights[uuid];
                }
            }
        }

        return [.. gpus.Select(g => new HostGpuSample(
            g.Index,
            g.Uuid,
            g.Telemetry?.UtilizationPercent,
            g.Telemetry?.MemoryUsedGiB ?? g.Memory?.UsedGiB,
            g.Telemetry?.MemoryTotalGiB ?? g.Memory?.TotalGiB,
            perGpu?.GetValueOrDefault(g.Uuid)))];
    }

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>
    /// The share of a model's memory each placed GPU holds, by UUID, when the model carries its own per-GPU budgets
    /// (<see cref="LlamaCppSettings.GpuBudgetsGiB"/>, design-p3p4 §A.1.5): the launch sends exactly those, so a 15/5 GiB
    /// split must read 15/5 on the headroom bars and not the even 10/10 (MN-239 item 10, D131). A multi-GPU llama.cpp
    /// model is a native process, so its rows carry no container id and the measured per-GPU path (D91) never applies to
    /// it. Null when the model has no own budgets (the even split is what the agent then admits), when they do not match
    /// the placement, or when they are not usable.
    /// </summary>
    private static Dictionary<string, double>? BudgetShares(ModelDefinition? model, ModelPlacement? placement)
    {
        if (model is null || model.Runtime != RuntimeKind.LlamaCpp
            || model.LlamaCpp?.GpuBudgetsGiB is not { Count: > 1 } budgets
            || placement?.GpuUuids is not { } uuids || uuids.Count != budgets.Count)
        {
            return null;
        }

        double total = budgets.Sum();
        if (!(total > 0) || budgets.Any(b => b < 0))
        {
            return null;
        }

        var shares = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < uuids.Count; i++)
        {
            shares[uuids[i]] = budgets[i] / total;
        }

        return shares;
    }

    /// <summary>
    /// GiB per (model, GPU UUID) from the GPU process rows of each running model's container, for the models whose rows
    /// all name their GPU (D91). A model with a row that names no GPU is left out (even split); so is one with no row.
    /// </summary>
    private static Dictionary<string, Dictionary<string, double>> MeasuredPerGpu(
        IReadOnlyList<GpuProcess>? processes, IReadOnlyDictionary<string, ModelMemoryAttribution.RunningModel>? running)
    {
        var result = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        if (processes is null || running is null)
        {
            return result;
        }

        var untagged = new HashSet<string>(StringComparer.Ordinal);
        foreach (var process in processes)
        {
            if (process.ContainerId is not { Length: > 0 } container)
            {
                continue;
            }

            string? modelId = running.FirstOrDefault(kv => SameContainer(kv.Value.ContainerId, container)).Key;
            if (modelId is null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(process.GpuUuid))
            {
                untagged.Add(modelId);
                continue;
            }

            var onGpus = result.TryGetValue(modelId, out var existing)
                ? existing
                : result[modelId] = new Dictionary<string, double>(StringComparer.Ordinal);
            onGpus[process.GpuUuid] = onGpus.GetValueOrDefault(process.GpuUuid) + process.UsedMiB / 1024.0;
        }

        foreach (string modelId in untagged)
        {
            result.Remove(modelId);
        }

        return result;
    }

    /// <summary>As <see cref="ModelMemoryAttribution"/> matches them: the full 64-hex id from the cgroup against a full or 12+ character id.</summary>
    private static bool SameContainer(string? modelContainer, string processContainer) =>
        modelContainer is { Length: >= 12 } id &&
        (processContainer.StartsWith(id, StringComparison.OrdinalIgnoreCase) || id.StartsWith(processContainer, StringComparison.OrdinalIgnoreCase));

    private void RecordModel(NodeCursor cursor, string modelId, ModelMetricsScrape scrape, DateTimeOffset sampleAt)
    {
        if (scrape.Text is not { } text)
        {
            // A failed scrape keeps the previous baseline, like the pre-P1 sampler.
            LogScrapeFailed(logger, modelId, scrape.Error ?? "no text");
            return;
        }

        try
        {
            var samples = PrometheusText.Parse(text);
            var previous = cursor.Baselines.GetValueOrDefault(modelId);
            double elapsedSeconds = previous is null ? 0 : (scrape.At - previous.At).TotalSeconds;
            var kpis = ModelKpiCalculator.Compute(samples, previous?.Samples, elapsedSeconds);
            cursor.Baselines[modelId] = new Baseline(samples, scrape.At);
            history.AddModel(modelId, new ModelSample(sampleAt, kpis) { HasRates = previous is not null && elapsedSeconds > 0 });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogScrapeFailed(logger, modelId, ex.Message);
        }
    }

    /// <summary>Today's single-GPU view: the lowest-index GPU, as <c>nvidia-smi</c>'s first line was.</summary>
    internal static GpuSnapshot? ToGpuSnapshot(IReadOnlyList<GpuSample> gpus) =>
        gpus.Count == 0 ? null : gpus.MinBy(g => g.Index)!.ToSnapshot();

    private static bool IsPlacedElsewhere(ModelDefinition model, string nodeId) =>
        model.Placement is { } placement && !string.Equals(placement.NodeId, nodeId, StringComparison.Ordinal);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry collection pass failed.")]
    private static partial void LogPassFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telemetry pass skipped: the model or node registry could not be read.")]
    private static partial void LogRegistryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Telemetry pull from node {NodeId} failed; the next pull back-fills: {Reason}")]
    private static partial void LogPullFailed(ILogger logger, string nodeId, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Node {NodeId} agent restarted (boot {OldBootId} -> {NewBootId}); its downtime is a telemetry gap.")]
    private static partial void LogBootChanged(ILogger logger, string nodeId, string oldBootId, string newBootId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Metrics scrape of model {ModelId} unusable: {Reason}")]
    private static partial void LogScrapeFailed(ILogger logger, string modelId, string reason);

    /// <summary>A model's previous scrape: the rate baseline for the next one.</summary>
    private sealed record Baseline(IReadOnlyList<PrometheusSample> Samples, DateTimeOffset At);

    /// <summary>Per-node pull state. Touched only inside a pass (passes do not overlap).</summary>
    private sealed class NodeCursor
    {
        public string? BootId;
        public long? Seq;
        public DateTimeOffset? LastSampleAt;
        public string? PendingGap;
        public readonly Dictionary<string, Baseline> Baselines = new(StringComparer.Ordinal);
    }
}
