using System.Collections.Concurrent;
using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// The fit preview of <c>GET/POST /api/nodes/{id}/fit</c> (design §5.6, §6.2; MN-108): an immediate answer for the UI from
/// the node's last telemetry ("fits on dgx: needs 36 GiB, 41 GiB free after the 8 GiB reserve"). It never refuses
/// anything: the agent re-checks with a fresh probe under its start lock and decides at launch, so an unknown answer
/// (<see cref="NodeFitResult.Fits"/> null) is normal for a node that is not reporting.
/// <para>Rules (design §6.1): telemetry older than <see cref="MaxTelemetryAge"/> by the master's clock is not used
/// (the status cache's memory stands in while the node is serving, as for <see cref="RemoteMemoryProbe"/>). Unified:
/// <c>free = available − node reserve − Σ budgets of the node's other starting models</c>. Discrete, per selected GPU:
/// <c>free = gpuFree − gpu reserve − Σ per-GPU budgets of other starting models on it</c> against <c>budget ÷ n</c>, plus
/// the host part (<see cref="ModelDefinition.HostMemoryBudgetGiB"/> or the §6.1 estimate) against <c>hostAvailable −
/// node reserve − Σ host budgets of other starting models</c>. Without a GPU selection every GPU is shown as a
/// single-GPU candidate. When the model already runs on the node, its own budget counts as free (a restart reuses it).</para>
/// <para>llama.cpp (MN-224, design-p3p4 §A.1.5, §A.1.10): on a node reporting <c>admission.gpu-budgets</c> each placed GPU
/// needs its own budget (<see cref="LlamaCppGpuBudgets"/>: the model's, else derived in the tensor-split proportion), as
/// the launch sends them; without the flag the even split, as the agent then admits. With agent clients, a node reporting
/// <c>gguf.inspect</c> answers the model file's GGUF summary and <see cref="NodeFitResult.Estimate"/> carries
/// <see cref="LlamaCppMemoryEstimate"/>'s figures (shown next to the budget, which stays what admission uses); the host
/// part of a partial offload is 1 GiB plus the layers left on the CPU (design §6.1, <see cref="LlamaCppHostGiB"/>).</para>
/// </summary>
public sealed class PlacementFit
{
    /// <summary>The newest telemetry the preview uses (design §6.2: ≤ 5 s old).</summary>
    public static readonly TimeSpan MaxTelemetryAge = TimeSpan.FromSeconds(5);

    private readonly INodeRegistry _nodes;
    private readonly IModelRegistry _models;
    private readonly INodeTelemetry? _telemetry;
    private readonly INodeStatusCache? _status;
    private readonly TimeProvider _timeProvider;
    private readonly IAgentClientFactory? _agents;

    /// <summary>How long the preview waits for a node's GGUF summary before answering without the estimate.</summary>
    public static readonly TimeSpan GgufTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long one node's answer about one GGUF file — <b>including "it could not say"</b> — is reused (MN-239 item 12).
    /// <para>Since MN-233 composed this class with agent clients, the preview is a remote read on the editor's keystroke
    /// path: <c>ModelEditor.RefreshFitAsync</c> is bound to ten handlers, loops over every node and then re-checks the
    /// selected one, so on a two-node fleet with the Windows PC unreachable typing a digit cost up to two
    /// <see cref="GgufTimeout"/>s per pass. The failed answer is cached too — that is what removes the stall — and it is
    /// safe to cache because <see cref="NodeRecord.LastContact"/> is part of the key: a node that is not answering does
    /// not move it, and a node that starts answering again does, which retires the entry at once.</para>
    /// </summary>
    public static readonly TimeSpan GgufCacheDuration = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<GgufKey, (GgufSummary? Summary, DateTimeOffset Expires)> _gguf = new();

    private readonly record struct GgufKey(string NodeId, string Folder, string File, long Contact);

    /// <summary>Creates the preview.</summary>
    /// <param name="nodes">Reserves, capabilities.</param>
    /// <param name="models">Stored definitions (the checked model, the budgets of other starting models).</param>
    /// <param name="telemetry">Latest node telemetry; null = none (every answer unknown unless the cache has memory).</param>
    /// <param name="status">Node states and instances; null = not composed (no serving check, no reservations).</param>
    /// <param name="timeProvider">Master clock.</param>
    /// <param name="agents">
    /// Agent clients for the GGUF summary of a llama.cpp model (<see cref="AgentFeatures.GgufInspect"/>, design-p3p4
    /// §A.1.10, MN-224); null = no estimate (<see cref="NodeFitResult.Estimate"/> stays null).
    /// </param>
    public PlacementFit(
        INodeRegistry nodes, IModelRegistry models, INodeTelemetry? telemetry, INodeStatusCache? status, TimeProvider? timeProvider = null,
        IAgentClientFactory? agents = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(models);
        _nodes = nodes;
        _models = models;
        _telemetry = telemetry;
        _status = status;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _agents = agents;
    }

    /// <summary>
    /// The preview of <paramref name="request"/> on node <paramref name="nodeId"/>; null when the node does not exist.
    /// Throws <see cref="KeyNotFoundException"/> for an unknown <see cref="NodeFitRequest.ModelId"/> and
    /// <see cref="ArgumentException"/> when the request names neither a model nor a definition, or a budget that is not
    /// positive.
    /// </summary>
    public async Task<NodeFitResult?> CheckAsync(string nodeId, NodeFitRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(request);

        var node = await _nodes.GetAsync(nodeId, ct).ConfigureAwait(false);
        if (node is null)
        {
            return null;
        }

        var definition = request.Definition;
        if (definition is null)
        {
            if (string.IsNullOrWhiteSpace(request.ModelId))
            {
                throw new ArgumentException("Give a model id (?modelId=) or a definition.", nameof(request));
            }

            definition = await _models.GetAsync(request.ModelId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Model '{request.ModelId}' does not exist.");
        }

        if (!(definition.MemoryBudgetGiB > 0) || double.IsInfinity(definition.MemoryBudgetGiB))
        {
            throw new ArgumentException("The memory budget must be a positive number.", nameof(request));
        }

        var live = _status?.GetNode(nodeId);
        var (memory, measuredAt) = Memory(nodeId, live);
        var model = node.LastCapabilities?.Memory.Model ?? memory?.Model ?? live?.Memory?.Model ?? MemoryModel.Unified;
        var context = new FitContext(node, definition, await OthersStartingAsync(nodeId, definition.Id, ct).ConfigureAwait(false),
            RunsHere(nodeId, definition.Id));

        string? unknownReason = node.Revoked
            ? $"Node '{node.Id}' is revoked."
            : live is not null && !live.IsServing
                ? $"Node '{node.Id}' is {StateName(live.State)}; the fit is unknown (the agent checks when the model starts)."
                : memory is null
                    ? $"Node '{node.Id}' has not reported memory in the last {MaxTelemetryAge.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s; the fit is unknown (the agent checks when the model starts)."
                    : null;

        var gguf = node.Revoked || (live is not null && !live.IsServing) ? null : await GgufAsync(node, definition, ct).ConfigureAwait(false);
        return model == MemoryModel.Unified
            ? WithEstimate(Unified(context, unknownReason is null ? memory : null, unknownReason, measuredAt), Estimate(gguf, definition, 1, null, CpuOnly(node)))
            : Discrete(context, unknownReason is null ? memory : null, unknownReason, measuredAt, request.GpuUuids, gguf);
    }

    /// <summary>
    /// The per-GPU budgets of a llama.cpp model on its placed GPUs (design-p3p4 §A.1.5; MN-224, D129 (4)), in placement
    /// order: its own <see cref="LlamaCppSettings.GpuBudgetsGiB"/> when set; else, split over two or more GPUs by layer,
    /// <see cref="ModelDefinition.MemoryBudgetGiB"/> divided in the proportion of
    /// <see cref="LaunchSpecBuilder.TensorSplitWeights"/> (each card's <c>memory.total</c> minus its reserve), so admission
    /// matches the emitted <c>--tensor-split</c> and the smaller card is not admitted the even share. Null for vLLM, one
    /// GPU or another split mode (the even split applies). The launch sends these as
    /// <see cref="AdmissionRequest.GpuBudgetsGiB"/> to a node reporting <see cref="AgentFeatures.AdmissionGpuBudgets"/>, and
    /// the preview shows the same numbers there.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The model's own budgets do not match the placement (<see cref="LlamaCppArgsBuilder.GpuBudgetProblems"/>), or a placed
    /// GPU is missing from the node's report or has nothing left after its reserve (<see cref="LaunchSpecBuilder.TensorSplitWeights"/>).
    /// </exception>
    public static IReadOnlyList<double>? LlamaCppGpuBudgets(ModelDefinition model, NodeRecord node)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        if (model.Runtime != RuntimeKind.LlamaCpp || model.LlamaCpp is not { } settings)
        {
            return null;
        }

        var uuids = LaunchSpecBuilder.PlacedGpuUuids(model);
        if (settings.GpuBudgetsGiB is { } own)
        {
            var problems = LlamaCppArgsBuilder.GpuBudgetProblems(model, uuids.Count);
            if (problems.Count > 0)
            {
                throw new ArgumentException(string.Join(" ", problems.Select(p => p.Message)));
            }

            return own;
        }

        if (LaunchSpecBuilder.TensorSplitWeights(model, node) is not { } weights)
        {
            return null;
        }

        double total = weights.Sum();
        return [.. weights.Select(w => model.MemoryBudgetGiB * w / total)];
    }

    /// <summary>
    /// The host-RAM estimate of a llama.cpp model (design §6.1) when <see cref="ModelDefinition.HostMemoryBudgetGiB"/> is
    /// null: 1 GiB plus, with partial offload (<c>GpuLayers</c> below the GGUF's layer count), the share of the weights left
    /// on the CPU (<see cref="GgufMemoryEstimate.HostGiB"/>). Without an estimate: 1 GiB (<see cref="EstimateHostGiB"/>).
    /// </summary>
    public static double LlamaCppHostGiB(GgufMemoryEstimate? estimate) => 1 + (estimate?.HostGiB ?? 0);

    private sealed record FitContext(NodeRecord Node, ModelDefinition Definition, IReadOnlyList<ModelDefinition> OthersStarting, bool RunsHere);

    private static NodeFitResult WithEstimate(NodeFitResult result, GgufMemoryEstimate? estimate) =>
        estimate is null ? result : result with { Estimate = estimate, Message = result.Message + EstimateText(estimate) };

    private static string EstimateText(GgufMemoryEstimate estimate) =>
        estimate.HostGiB > 0
            ? $" GGUF estimate: {GiB(estimate.BudgetGiB)} on the GPUs and {GiB(estimate.HostGiB)} of layers in host RAM."
            : $" GGUF estimate: {GiB(estimate.BudgetGiB)}.";

    /// <summary>
    /// The GGUF summary of a llama.cpp model's file from its node (feature <see cref="AgentFeatures.GgufInspect"/>), or
    /// null: no agent clients, not llama.cpp, a node that is not approved, a node without the feature, a model file that
    /// is not <c>folder/file.gguf</c>, or any failure or a slow answer (the preview never fails for it).
    /// <para>The approval check is the master's trust gate (D130, <c>ApiNodeScope</c>: "before it, nothing is sent to the
    /// node"). <see cref="CheckAsync"/>'s <c>IsServing</c> check happens to cover a pending node that the status cache
    /// already knows, but not one it has not polled yet, and not a <see cref="PlacementFit"/> built without a cache —
    /// and since MN-233 gave this class an <see cref="IAgentClientFactory"/>, <c>POST /api/nodes/{id}/fit</c> and the
    /// editor could then read a GGUF off an unapproved node. Checked here, so every caller is covered (MN-234). The fit
    /// *verdict* is still answered for an unapproved node — that is MN-108's design, and it is what the Nodes page shows
    /// before approval; only the remote read is withheld.</para>
    /// </summary>
    private async Task<GgufSummary?> GgufAsync(NodeRecord node, ModelDefinition definition, CancellationToken ct)
    {
        if (_agents is null || !node.Approved || definition.Runtime != RuntimeKind.LlamaCpp ||
            definition.LlamaCpp is not { } settings || !LaunchSpecBuilder.NodeReports(node, AgentFeatures.GgufInspect))
        {
            return null;
        }

        string modelFile = settings.ModelFile ?? "";
        int slash = modelFile.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == modelFile.Length - 1 || modelFile.IndexOf('/', slash + 1) >= 0)
        {
            return null;
        }

        var key = new GgufKey(node.Id, modelFile[..slash], modelFile[(slash + 1)..], node.LastContact?.UtcTicks ?? 0);
        var now = _timeProvider.GetUtcNow();
        if (_gguf.TryGetValue(key, out var cached) && cached.Expires > now)
        {
            return cached.Summary;
        }

        GgufSummary? summary;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GgufTimeout);
        try
        {
            summary = await _agents.GetClient(node.Id).InspectGgufAsync(key.Folder, key.File, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            summary = null; // an old agent, a missing or unreadable file, an offline node, a timeout: no estimate
        }

        // Cached either way, see GgufCacheDuration: the failure is the expensive one, and the key retires it.
        _gguf[key] = (summary, now + GgufCacheDuration);
        if (_gguf.Count > MaxCachedGgufSummaries)
        {
            Prune(now);
        }

        return summary;
    }

    /// <summary>A bound on the cache, which is keyed by node, file and contact time and so cannot grow forever.</summary>
    private const int MaxCachedGgufSummaries = 256;

    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in _gguf)
        {
            if (entry.Value.Expires <= now)
            {
                _gguf.TryRemove(entry);
            }
        }
    }

    /// <summary>
    /// The GGUF memory estimate of <paramref name="model"/> as it would run on <paramref name="node"/>'s placed GPUs —
    /// the same figures the preview shows, from the same cached read. Null for a model that is not llama.cpp, a node
    /// that cannot answer, or a summary that lacks what the estimate needs.
    /// <para>This is what makes the launch's host budget and the preview's host figure one number (MN-239-17):
    /// <see cref="LaunchSpecBuilder.Admission"/> takes the estimate and applies <see cref="LlamaCppHostGiB"/>, so a
    /// partial offload is admitted on the host RAM the operator was shown rather than the flat 1 GiB of
    /// <see cref="EstimateHostGiB"/>. When the node cannot answer at launch time the launch degrades to that flat
    /// figure, exactly as before this method existed — and the agent re-checks under its own start lock regardless.</para>
    /// </summary>
    public async Task<GgufMemoryEstimate?> EstimateAsync(NodeRecord node, ModelDefinition model, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(model);
        var uuids = LaunchSpecBuilder.PlacedGpuUuids(model);
        var perGpuBudgets = uuids.Count == 0 ? null : GpuBudgetsOn(model, node);
        return Estimate(await GgufAsync(node, model, ct).ConfigureAwait(false), model, Math.Max(uuids.Count, 1), perGpuBudgets, CpuOnly(node));
    }

    /// <summary>
    /// <see cref="LlamaCppMemoryEstimate.Estimate"/> of <paramref name="definition"/> on <paramref name="gpuCount"/> GPUs,
    /// its per-GPU part split like <paramref name="perGpuBudgets"/> (null = the model's own budgets, else even); null without
    /// a summary or when the summary lacks what the estimate needs.
    /// </summary>
    private static GgufMemoryEstimate? Estimate(
        GgufSummary? gguf, ModelDefinition definition, int gpuCount, IReadOnlyList<double>? perGpuBudgets, bool cpuOnly = false)
    {
        if (gguf is null || definition.LlamaCpp is not { } settings)
        {
            return null;
        }

        try
        {
            return LlamaCppMemoryEstimate.Estimate(
                gguf, perGpuBudgets is null ? settings : settings with { GpuBudgetsGiB = perGpuBudgets }, Math.Max(gpuCount, 1), cpuOnly);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>True when the node's last report says it runs models on the CPU (<see cref="NodeAccelerators.Cpu"/>).</summary>
    private static bool CpuOnly(NodeRecord node) => NodeAccelerators.IsCpu(node.LastCapabilities);

    private static NodeFitResult Unified(FitContext context, NodeMemorySnapshot? memory, string? unknownReason, DateTimeOffset? measuredAt)
    {
        var node = context.Node;
        double need = context.Definition.MemoryBudgetGiB;
        double reserve = node.MemoryReserveGiB;
        double others = context.OthersStarting.Sum(m => m.MemoryBudgetGiB);
        double? free = memory is null
            ? null
            : Capped(memory.HostAvailableGiB - reserve - others + (context.RunsHere ? need : 0), memory.HostTotalGiB - reserve - others);

        // Apple Silicon (docs/macos/implementation-plan.md M7): the GPU may keep only part of the unified memory resident
        // (Metal's working set, 2/3 of RAM on an 8 GiB M2), and a model larger than that fails at load whatever RAM says.
        double? gpuWorkingSet = node.LastCapabilities?.Memory.GpuWorkingSetGiB;
        if (gpuWorkingSet is { } ws && free is { } before)
        {
            free = Math.Min(before, ws - others);
        }

        bool? fits = free is { } f ? need <= f : null;
        if (gpuWorkingSet is { } limit && need > limit && unknownReason is null)
        {
            return new NodeFitResult
            {
                NodeId = node.Id,
                Model = MemoryModel.Unified,
                Fits = false,
                NeedGiB = need,
                FreeGiB = free,
                ReserveGiB = reserve,
                Message = $"Does not fit on {node.Id}: needs {GiB(need)}, but its GPU can keep at most {GiB(limit)} resident (Metal's working set).",
                MeasuredAt = memory is null ? null : measuredAt,
            };
        }

        string message = unknownReason ?? (fits == true
            ? $"Fits on {node.Id}: needs {GiB(need)}, {GiB(free!.Value)} free after the {GiB(reserve)} reserve{Reserved(context)}."
            : $"Does not fit on {node.Id} now: needs {GiB(need)}, {GiB(free!.Value)} free after the {GiB(reserve)} reserve{Reserved(context)}.");

        return new NodeFitResult
        {
            NodeId = node.Id,
            Model = MemoryModel.Unified,
            Fits = fits,
            NeedGiB = need,
            FreeGiB = free,
            ReserveGiB = reserve,
            Message = message,
            MeasuredAt = memory is null ? null : measuredAt,
        };
    }

    private static NodeFitResult Discrete(
        FitContext context, NodeMemorySnapshot? memory, string? unknownReason, DateTimeOffset? measuredAt, IReadOnlyList<string>? requested,
        GgufSummary? gguf)
    {
        var node = context.Node;
        var definition = context.Definition;
        var capabilities = node.LastCapabilities;
        var known = capabilities?.Gpus ?? [];

        IReadOnlyList<string> selected = requested is { Count: > 0 }
            ? requested
            : definition.Placement is { } placement && string.Equals(placement.NodeId, node.Id, StringComparison.Ordinal)
                ? placement.GpuUuids ?? []
                : [];
        bool candidates = selected.Count == 0;
        var uuids = candidates ? known.Select(g => g.Uuid).ToList() : selected.Distinct(StringComparer.Ordinal).ToList();
        int n = candidates ? 1 : uuids.Count;
        double perGpuNeed = n == 0 ? definition.MemoryBudgetGiB : definition.MemoryBudgetGiB / n;

        // A llama.cpp model on a node that admits per-GPU budgets: each GPU needs its own (MN-224), as the launch sends them.
        var perGpuBudgets = candidates ? null : GpuBudgetsOn(definition with { Placement = new ModelPlacement(node.Id, uuids) }, node);
        double NeedOn(int i) => perGpuBudgets is { } budgets && i < budgets.Count ? budgets[i] : perGpuNeed;

        var gpus = new List<GpuFit>(uuids.Count);
        var unknownGpus = new List<string>();
        for (int i = 0; i < uuids.Count; i++)
        {
            string uuid = uuids[i];
            double need = NeedOn(i);
            var capability = known.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal));
            double gpuReserve = node.GpuReserveGiB.TryGetValue(uuid, out double r) ? r : 0;
            if (capability is null)
            {
                unknownGpus.Add(uuid);
                gpus.Add(new GpuFit(uuid, null, need, null, gpuReserve, false));
                continue;
            }

            double? gpuFree = memory?.Gpus.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal))?.FreeGiB;
            double othersOnGpu = context.OthersStarting.Sum(m => PerGpuBudgetOn(m, uuid, node));
            double? free = gpuFree is { } raw
                ? Capped(raw - gpuReserve - othersOnGpu + (context.RunsHere ? PerGpuBudgetOn(definition, uuid, node) : 0),
                    capability.MemoryTotalGiB is { } total ? total - gpuReserve - othersOnGpu : null)
                : null;
            gpus.Add(new GpuFit(uuid, capability.Index, need, free, gpuReserve, free is { } f ? need <= f : null));
        }

        var estimate = Estimate(gguf, definition, n, perGpuBudgets);
        bool estimated = definition.HostMemoryBudgetGiB is null;
        double hostNeed = definition.HostMemoryBudgetGiB
            ?? (definition.Runtime == RuntimeKind.LlamaCpp ? LlamaCppHostGiB(estimate) : EstimateHostGiB(definition, n));
        double hostReserve = node.MemoryReserveGiB;
        double? hostFree = memory is null
            ? null
            : memory.HostAvailableGiB - hostReserve - context.OthersStarting.Sum(m => m.HostMemoryBudgetGiB ?? EstimateHostGiB(m, GpuCount(m)))
                + (context.RunsHere ? hostNeed : 0);
        var host = new HostFit(hostNeed, hostFree, estimated, hostFree is { } hf ? hostNeed <= hf : null);

        bool? fits;
        double? freeGiB;
        if (unknownReason is not null || gpus.Count == 0)
        {
            fits = unknownReason is null && gpus.Count == 0 ? false : null;
            freeGiB = null;
        }
        else if (candidates)
        {
            var best = gpus.Where(g => g.FreeGiB is not null).OrderByDescending(g => g.FreeGiB).FirstOrDefault();
            fits = best is null || host.Fits is null ? null : best.Fits == true && host.Fits == true;
            freeGiB = best?.FreeGiB;
        }
        else
        {
            bool anyUnknown = gpus.Exists(g => g.Fits is null) || host.Fits is null;
            bool anyFails = gpus.Exists(g => g.Fits == false) || host.Fits == false;
            fits = anyFails ? false : anyUnknown ? null : true;
            freeGiB = gpus.Exists(g => g.FreeGiB is null) ? null : gpus.Min(g => g.FreeGiB);
        }

        string message = unknownReason ?? DiscreteMessage(node, gpus, host, candidates, fits, unknownGpus, perGpuNeed);
        return WithEstimate(new NodeFitResult
        {
            NodeId = node.Id,
            Model = MemoryModel.Discrete,
            Fits = fits,
            NeedGiB = definition.MemoryBudgetGiB,
            FreeGiB = freeGiB,
            ReserveGiB = hostReserve,
            Gpus = gpus,
            Host = host,
            Message = message,
            MeasuredAt = memory is null ? null : measuredAt,
        }, estimate);
    }

    private static string DiscreteMessage(
        NodeRecord node, List<GpuFit> gpus, HostFit host, bool candidates, bool? fits, List<string> unknownGpus, double perGpuNeed)
    {
        if (gpus.Count == 0)
        {
            return $"Node '{node.Id}' reports no GPUs; a model needs at least one GPU there.";
        }

        if (unknownGpus.Count > 0)
        {
            return $"Node '{node.Id}' has no GPU {string.Join(", ", unknownGpus)} (as of its last report); select one of its GPUs.";
        }

        string hostPart = host.FreeGiB is { } hostFree
            ? $"host RAM needs {GiB(host.NeedGiB)}{(host.Estimated ? " (estimate)" : "")}, {GiB(hostFree)} free after the reserve"
            : "host RAM unknown";

        if (candidates)
        {
            var fitting = gpus.Where(g => g.Fits == true).Select(g => "GPU " + IndexOf(g)).ToList();
            return fitting.Count > 0 && host.Fits == true
                ? $"Fits on {node.Id} on {string.Join(", ", fitting)}: needs {GiB(perGpuNeed)} on one GPU; {hostPart}."
                : fits is null
                    ? $"Fit on {node.Id} unknown: no fresh memory for its GPUs; {hostPart}."
                    : $"Does not fit on {node.Id} now: no single GPU has {GiB(perGpuNeed)} free after its reserve, or the host part does not fit; {hostPart}.";
        }

        string verdict = fits switch
        {
            true => $"Fits on {node.Id}",
            false => $"Does not fit on {node.Id} now",
            null => $"Fit on {node.Id} unknown",
        };

        // Per-GPU budgets (a llama.cpp split, MN-224): each GPU names its own need.
        if (gpus.Exists(g => g.NeedGiB != perGpuNeed))
        {
            string each = string.Join("; ", gpus.Select(g => g.FreeGiB is { } free
                ? $"GPU {IndexOf(g)} needs {GiB(g.NeedGiB)}, {GiB(free)} free after the {GiB(g.ReserveGiB)} reserve"
                : $"GPU {IndexOf(g)} needs {GiB(g.NeedGiB)}, free unknown"));
            return $"{verdict}: {each}; {hostPart}.";
        }

        string perGpu = string.Join("; ", gpus.Select(g => g.FreeGiB is { } free
            ? $"GPU {IndexOf(g)}: {GiB(free)} free after the {GiB(g.ReserveGiB)} reserve"
            : $"GPU {IndexOf(g)}: unknown"));
        return $"{verdict}: needs {GiB(perGpuNeed)} per GPU ({perGpu}); {hostPart}.";
    }

    /// <summary>
    /// Host-RAM estimate of design §6.1 when <see cref="ModelDefinition.HostMemoryBudgetGiB"/> is null: vLLM on discrete GPUs
    /// <c>swap × n + 2 GiB</c> (swap = an explicit <c>--swap-space</c> in ExtraArgs, else vLLM's default 4 GiB); llama.cpp
    /// 1 GiB (all layers on the GPU; partial offload needs the GGUF metadata the editor reads); MLX 2 GiB.
    /// </summary>
    public static double EstimateHostGiB(ModelDefinition model, int gpuCount)
    {
        ArgumentNullException.ThrowIfNull(model);
        int n = Math.Max(gpuCount, 1);
        return model.Runtime switch
        {
            RuntimeKind.LlamaCpp => 1,
            RuntimeKind.Mlx => 2,
            _ => (SwapSpaceGiB(model.ExtraArgs ?? []) ?? 4) * n + 2,
        };
    }

    /// <summary>The model's explicit <c>--swap-space</c> (GiB) from its extra arguments, or null (vLLM's default applies).</summary>
    public static double? ExplicitSwapSpaceGiB(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Runtime is RuntimeKind.Vllm ? SwapSpaceGiB(model.ExtraArgs ?? []) : null;
    }

    private static double? SwapSpaceGiB(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            string? value = arg.StartsWith("--swap-space=", StringComparison.Ordinal)
                ? arg["--swap-space=".Length..]
                : arg == "--swap-space" && i + 1 < args.Count ? args[i + 1] : null;
            if (value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double gib) && gib >= 0)
            {
                return gib;
            }
        }

        return null;
    }

    /// <summary>
    /// <paramref name="free"/>, never above <paramref name="ceiling"/> (the memory's capacity after the reserve and the other
    /// starts). A model that runs here gets its own budget added back (a restart frees it), but a reading taken before its
    /// instance took the memory (telemetry up to 15 s old, or the status cache's last snapshot) already counts that memory
    /// as free: added back once more, a 16 GiB GPU showed 19.5 GiB free (W8 review MN-128, found by MN-122).
    /// </summary>
    private static double Capped(double free, double? ceiling) => ceiling is { } max ? Math.Min(free, max) : free;

    private static int GpuCount(ModelDefinition model) => model.Placement?.GpuUuids?.Count ?? 1;

    /// <summary>
    /// What <paramref name="model"/> (as placed) holds on <paramref name="uuid"/>: its per-GPU budget there on a node that
    /// admits per-GPU budgets (<see cref="GpuBudgetsOn"/>), else its budget split evenly (P2).
    /// </summary>
    private static double PerGpuBudgetOn(ModelDefinition model, string uuid, NodeRecord node)
    {
        if (model.Placement?.GpuUuids is not { Count: > 0 } uuids)
        {
            return 0;
        }

        int at = uuids.ToList().IndexOf(uuid);
        if (at < 0)
        {
            return 0;
        }

        return GpuBudgetsOn(model, node) is { } budgets && at < budgets.Count ? budgets[at] : model.MemoryBudgetGiB / uuids.Count;
    }

    /// <summary>
    /// <see cref="LlamaCppGpuBudgets"/> when <paramref name="node"/> admits per-GPU budgets (its last report lists
    /// <see cref="AgentFeatures.AdmissionGpuBudgets"/>; without it the agent splits evenly, P2), and null when they cannot
    /// be worked out (a mismatch the save and the launch report themselves).
    /// </summary>
    private static IReadOnlyList<double>? GpuBudgetsOn(ModelDefinition model, NodeRecord node)
    {
        if (model.Runtime != RuntimeKind.LlamaCpp || !LaunchSpecBuilder.NodeReports(node, AgentFeatures.AdmissionGpuBudgets))
        {
            return null;
        }

        try
        {
            return LlamaCppGpuBudgets(model, node);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private (NodeMemorySnapshot? Memory, DateTimeOffset? MeasuredAt) Memory(string nodeId, NodeLiveStatus? live)
    {
        var now = _timeProvider.GetUtcNow();
        if (_telemetry?.GetLatest(nodeId) is { } reading && now - reading.ReceivedAt <= MaxTelemetryAge)
        {
            return (reading.Sample.Memory, reading.ReceivedAt);
        }

        // The cache is fed by snapshots and events while the node is serving (contact within 15 s), as RemoteMemoryProbe uses it.
        if (live is { IsServing: true, Memory: { } memory } && live.LastContact is { } contact && now - contact <= MaxTelemetryAge)
        {
            return (memory, contact);
        }

        return (null, null);
    }

    private async Task<IReadOnlyList<ModelDefinition>> OthersStartingAsync(string nodeId, string modelId, CancellationToken ct)
    {
        if (_status is null)
        {
            return [];
        }

        var starting = _status.ListRuntimes(nodeId)
            .Where(r => r.State == ModelState.Starting && !string.Equals(r.ModelId, modelId, StringComparison.Ordinal))
            .Select(r => r.ModelId)
            .ToHashSet(StringComparer.Ordinal);
        if (starting.Count == 0)
        {
            return [];
        }

        return [.. (await _models.ListAsync(ct).ConfigureAwait(false)).Where(m => starting.Contains(m.Id))];
    }

    private bool RunsHere(string nodeId, string modelId) =>
        _status?.ListRuntimes(nodeId).Any(r =>
            string.Equals(r.ModelId, modelId, StringComparison.Ordinal) &&
            r.State is ModelState.Starting or ModelState.Ready) == true;

    private static string Reserved(FitContext context) =>
        context.OthersStarting.Count == 0
            ? ""
            : $" and {GiB(context.OthersStarting.Sum(m => m.MemoryBudgetGiB))} held by {context.OthersStarting.Count.ToString(CultureInfo.InvariantCulture)} starting model(s)";

    private static string IndexOf(GpuFit gpu) => gpu.Index?.ToString(CultureInfo.InvariantCulture) ?? gpu.Uuid;

    private static string StateName(NodeState state) => state switch
    {
        NodeState.PendingApproval => "waiting for approval",
        NodeState.PendingTakeover => "pending takeover",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static string GiB(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + " GiB";
}
