using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Runtimes;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>The instance a benchmark is about to measure: its provenance and the resources it shares (MN-112).</summary>
/// <param name="Provenance">Node, GPUs, runtime, delivery, digest, hashes and data path; <see cref="BenchmarkProvenance.Noise"/>
/// is left <see cref="BenchmarkNoise.Unknown"/> for the caller to set from the result.</param>
/// <param name="Scope">What the suite's contention check looks at.</param>
public sealed record MeasuredInstance(BenchmarkProvenance Provenance, NoiseScope Scope);

/// <summary>
/// Describes where a benchmark or tuning candidate is measured (design §6.5 items 4–5, MN-112): the provenance recorded on
/// <see cref="BenchmarkRun.Provenance"/>/<see cref="TuningRun.Provenance"/> and the noise scope handed to the suite on
/// <see cref="BenchmarkTarget.Scope"/>. Reads only the master's caches (never calls an agent).
/// </summary>
public interface IBenchmarkProvenanceSource
{
    /// <summary>The instance of <paramref name="modelId"/> measured through <paramref name="endpoint"/>.</summary>
    Task<MeasuredInstance> DescribeAsync(string modelId, BenchmarkEndpoint endpoint, CancellationToken ct = default);

    /// <summary>
    /// Where a run of <paramref name="model"/> would be measured now (node, uid, GPUs, runtime, delivery), to pick the
    /// stored runs an ETA or a comparison may use (<see cref="BenchmarkComparisons.Comparable"/>).
    /// </summary>
    BenchmarkProvenance Expected(ModelDefinition model);
}

/// <summary>What a job needs from an optional <see cref="IBenchmarkProvenanceSource"/> (MN-112), null-safe.</summary>
public static class BenchmarkProvenanceSourceExtensions
{
    /// <summary>
    /// <see cref="IBenchmarkProvenanceSource.DescribeAsync"/>, or null without a source or when it fails: provenance is a
    /// record of the run, not a precondition, so the run is then measured unscoped and stored without it.
    /// </summary>
    public static async Task<MeasuredInstance?> TryDescribeAsync(
        this IBenchmarkProvenanceSource? source, string modelId, BenchmarkEndpoint endpoint, CancellationToken ct = default)
    {
        if (source is null)
        {
            return null;
        }

        try
        {
            return await source.DescribeAsync(modelId, endpoint, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Where a run of <paramref name="modelId"/> would be measured now; null without a source or for an unknown model.</summary>
    public static async Task<BenchmarkProvenance?> ExpectedAsync(
        this IBenchmarkProvenanceSource? source, IModelRegistry registry, string modelId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return source is not null && await registry.GetAsync(modelId, ct).ConfigureAwait(false) is { } model ? source.Expected(model) : null;
    }
}

/// <summary>
/// The master's <see cref="IBenchmarkProvenanceSource"/> over the node status cache and the registry (MN-112). The node is
/// the endpoint's (the instance's), else the placement's, else the bootstrap node. GPUs are recorded on discrete nodes only
/// (the contract: empty on unified nodes, so a DGX run stays comparable with the runs stored before P2), from the instance's
/// GPU UUIDs, else the placement's, with name and current index from the node's capability report.
/// <para>Noise scope: every other model the registry places on the node, plus unknown instances the node reports; on a
/// discrete node only those sharing one of the measured GPUs, plus the node's models with no GPU (host-heavy). A model whose
/// GPUs are not known on a discrete node is kept (conservative).</para>
/// <para>A llama.cpp process instance also records <see cref="BenchmarkProvenance.ExecutableVersion"/> (design-p3p4 §A.6,
/// MN-225): the version its launch resolved, i.e. the model's pin, else the node's verified default from its last capability
/// report (<see cref="ExecutableVersionOf"/>). Container instances and vLLM record none.</para>
/// </summary>
public sealed class NodeBenchmarkProvenanceSource(INodeStatusCache nodes, IModelRegistry registry, string? bootstrapNodeId = null)
    : IBenchmarkProvenanceSource
{
    private readonly string _bootstrapNodeId =
        string.IsNullOrEmpty(bootstrapNodeId) ? RegistryMigrationOptions.DefaultBootstrapNodeId : bootstrapNodeId;

    /// <inheritdoc/>
    public async Task<MeasuredInstance> DescribeAsync(string modelId, BenchmarkEndpoint endpoint, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(endpoint);
        var models = await registry.ListAsync(ct).ConfigureAwait(false);
        var model = models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.Ordinal));
        var instance = nodes.GetRuntime(modelId);
        string nodeId = endpoint.NodeId ?? instance?.NodeId ?? NodeOf(model);
        var node = nodes.GetNode(nodeId);
        bool discrete = node?.Capabilities?.Memory.Model == MemoryModel.Discrete;
        var uuids = discrete ? GpusOf(instance, model) : [];

        var provenance = new BenchmarkProvenance
        {
            NodeId = nodeId,
            NodeUid = node?.NodeUid,
            Gpus = Describe(node, uuids),
            Runtime = model?.Runtime ?? RuntimeKind.Vllm,
            Delivery = BenchmarkComparisons.DeliveryOf(model),
            ImageDigest = instance?.Labels.GetValueOrDefault(ContainerLaunchValidator.ImageDigestLabel) is { Length: > 0 } digest ? digest : null,
            ConfigHash = instance?.ConfigHash,
            ExecutionFingerprint = instance?.ExecutionFingerprint,
            ExecutableVersion = ExecutableVersionOf(model, node?.Capabilities),
            Proxy = endpoint.DataPath == BenchmarkDataPaths.Direct ? null : endpoint.DataPath,
            Noise = BenchmarkNoise.Unknown,
        };
        return new MeasuredInstance(provenance, Scope(modelId, nodeId, uuids, models));
    }

    /// <inheritdoc/>
    public BenchmarkProvenance Expected(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var instance = nodes.GetRuntime(model.Id);
        string nodeId = instance?.NodeId ?? NodeOf(model);
        var node = nodes.GetNode(nodeId);
        bool discrete = node?.Capabilities?.Memory.Model == MemoryModel.Discrete;
        return new BenchmarkProvenance
        {
            NodeId = nodeId,
            NodeUid = node?.NodeUid,
            Gpus = Describe(node, discrete ? GpusOf(instance, model) : []),
            Runtime = model.Runtime,
            Delivery = BenchmarkComparisons.DeliveryOf(model),
            ExecutableVersion = ExecutableVersionOf(model, node?.Capabilities),
        };
    }

    /// <summary>
    /// The <c>llama-server</c> version a llama.cpp process instance of <paramref name="model"/> runs on a node reporting
    /// <paramref name="capabilities"/> (what <c>LaunchSpecBuilder.ResolveExecutableVersion</c> launches with): the model's pin,
    /// else the node's verified default; null for vLLM, container delivery, or a node reporting none.
    /// </summary>
    public static string? ExecutableVersionOf(ModelDefinition? model, NodeCapabilities? capabilities)
    {
        if (model is not { Runtime: RuntimeKind.LlamaCpp, LlamaCpp: { Delivery: LaunchDelivery.Process } settings })
        {
            return null;
        }

        return settings.ExecutableVersion is { Length: > 0 } pinned
            ? pinned
            : (capabilities?.Runtimes ?? [])
                .Where(r => r.Kind == RuntimeKind.LlamaCpp && r.Delivery == LaunchDelivery.Process)
                .SelectMany(r => r.Versions ?? [])
                .FirstOrDefault(v => v.Default && v.Verified)?.Version;
    }

    private NoiseScope Scope(string modelId, string nodeId, IReadOnlyList<string> uuids, IReadOnlyList<ModelDefinition> models)
    {
        var runtimes = nodes.ListRuntimes(nodeId);
        var known = models.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var shared = new List<string>();
        foreach (var other in models.Where(m => !string.Equals(m.Id, modelId, StringComparison.Ordinal)))
        {
            var instance = runtimes.FirstOrDefault(r => string.Equals(r.ModelId, other.Id, StringComparison.Ordinal));
            if (!string.Equals(instance?.NodeId ?? NodeOf(other), nodeId, StringComparison.Ordinal))
            {
                continue;
            }

            var gpus = GpusOf(instance, other);
            if (uuids.Count == 0 || gpus.Count == 0 || gpus.Intersect(uuids, StringComparer.Ordinal).Any())
            {
                shared.Add(other.Id);
            }
        }

        // An instance the registry does not know (left over, or a model deleted meanwhile) still loads the node.
        shared.AddRange(runtimes
            .Where(r => !known.Contains(r.ModelId) && !string.Equals(r.ModelId, modelId, StringComparison.Ordinal))
            .Where(r => uuids.Count == 0 || r.GpuUuids.Count == 0 || r.GpuUuids.Intersect(uuids, StringComparer.Ordinal).Any())
            .Select(r => r.ModelId));

        var sharedSet = shared.ToHashSet(StringComparer.Ordinal);
        var running = runtimes
            .Where(r => r.State == ModelState.Ready && sharedSet.Contains(r.ModelId))
            .Select(r => r.ModelId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new NoiseScope(nodeId, [.. sharedSet.Order(StringComparer.Ordinal)], running, uuids);
    }

    private string NodeOf(ModelDefinition? model) =>
        model?.Placement?.NodeId is { Length: > 0 } nodeId ? nodeId : _bootstrapNodeId;

    private static IReadOnlyList<string> GpusOf(NodeModelRuntime? instance, ModelDefinition? model) =>
        instance?.GpuUuids is { Count: > 0 } running ? running : model?.Placement?.GpuUuids ?? [];

    private static IReadOnlyList<ProvenanceGpu> Describe(NodeLiveStatus? node, IReadOnlyList<string> uuids) =>
        [.. uuids.Select(uuid =>
            node?.Capabilities?.Gpus.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.OrdinalIgnoreCase)) is { } gpu
                ? new ProvenanceGpu(uuid, gpu.Name, gpu.Index)
                : new ProvenanceGpu(uuid))];
}

/// <summary>
/// Which stored runs may be compared (design §6.5 item 5, MN-112): comparisons, ETAs and "best" lists use only runs measured
/// on the same hardware and runtime; the others are shown labelled <see cref="DifferentHardware"/>.
/// </summary>
public static class BenchmarkComparisons
{
    /// <summary>The label of a run that is not comparable with the reference.</summary>
    public const string DifferentHardware = "different hardware";

    /// <summary>
    /// The provenance of <paramref name="run"/>; for a run stored before MN-112, what it was: the bootstrap node, direct,
    /// no GPU list, its own runtime and config hash, the noise verdict of its result.
    /// </summary>
    public static BenchmarkProvenance Of(BenchmarkRun run, string bootstrapNodeId)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Provenance ?? Legacy(bootstrapNodeId, run.Settings, run.ConfigHash, run.Proxy, run.Result?.Noise.Verdict);
    }

    /// <summary>As <see cref="Of(BenchmarkRun, string)"/> for a tuning run (its baseline's runtime).</summary>
    public static BenchmarkProvenance Of(TuningRun run, string bootstrapNodeId)
    {
        ArgumentNullException.ThrowIfNull(run);
        return run.Provenance ?? Legacy(bootstrapNodeId, run.Baseline, run.Candidates.Count > 0 ? run.Candidates[0].ConfigHash : null, run.Proxy, null);
    }

    /// <summary>
    /// True when <paramref name="a"/> and <paramref name="b"/> measured the same hardware and runtime
    /// (<see cref="BenchmarkProvenance.IsComparableWith"/>). A run stored before MN-112 has no node uid: it then matches by
    /// node id, so the DGX's old runs stay comparable with its new ones. Null = a run stored before MN-112 on the bootstrap
    /// node.
    /// </summary>
    public static bool Comparable(BenchmarkProvenance? a, BenchmarkProvenance? b, string bootstrapNodeId)
    {
        var left = a ?? Legacy(bootstrapNodeId, null, null, null, null);
        var right = b ?? Legacy(bootstrapNodeId, null, null, null, null);
        return left.NodeUid is null || right.NodeUid is null
            ? (left with { NodeUid = null }).IsComparableWith(right with { NodeUid = null })
            : left.IsComparableWith(right);
    }

    /// <summary><see cref="DifferentHardware"/> when the runs are not <see cref="Comparable"/>; else null.</summary>
    public static string? Label(BenchmarkProvenance? a, BenchmarkProvenance? b, string bootstrapNodeId) =>
        Comparable(a, b, bootstrapNodeId) ? null : DifferentHardware;

    /// <summary>
    /// The label of a version change between two comparable runs (design-p3p4 §A.6, MN-225: results across llama.cpp
    /// versions stay comparable when the hardware and runtime match, and the change is labelled), e.g.
    /// <c>llama.cpp b11476 → b11502</c>. Null when the runs are not <see cref="Comparable"/> (that label is
    /// <see cref="DifferentHardware"/>), or neither or only one of them recorded a version, or both recorded the same one.
    /// </summary>
    public static string? VersionLabel(BenchmarkProvenance? reference, BenchmarkProvenance? other, string bootstrapNodeId)
    {
        if (reference?.ExecutableVersion is not { Length: > 0 } from || other?.ExecutableVersion is not { Length: > 0 } to ||
            string.Equals(from, to, StringComparison.Ordinal) || !Comparable(reference, other, bootstrapNodeId))
        {
            return null;
        }

        return $"llama.cpp {from} → {to}";
    }

    /// <summary>
    /// <paramref name="runs"/> (newest first, as the store lists them) that are comparable with <paramref name="reference"/>;
    /// all of them when there is no reference (no provenance source composed: P1 behaviour).
    /// </summary>
    public static IReadOnlyList<BenchmarkRun> ComparableWith(IReadOnlyList<BenchmarkRun> runs, BenchmarkProvenance? reference, string bootstrapNodeId)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return reference is null ? runs : [.. runs.Where(r => Comparable(Of(r, bootstrapNodeId), reference, bootstrapNodeId))];
    }

    /// <summary>As <see cref="ComparableWith(IReadOnlyList{BenchmarkRun}, BenchmarkProvenance?, string)"/> for tuning runs.</summary>
    public static IReadOnlyList<TuningRun> ComparableWith(IReadOnlyList<TuningRun> runs, BenchmarkProvenance? reference, string bootstrapNodeId)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return reference is null ? runs : [.. runs.Where(r => Comparable(Of(r, bootstrapNodeId), reference, bootstrapNodeId))];
    }

    /// <summary>
    /// The noise verdict of several measurements (a tuning run's candidates): noisy if one was, else unknown if one was or
    /// there is none, else quiet.
    /// </summary>
    public static BenchmarkNoise Combine(IEnumerable<BenchmarkNoise> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        var all = verdicts.ToList();
        return all.Contains(BenchmarkNoise.Noisy) ? BenchmarkNoise.Noisy
            : all.Count == 0 || all.Contains(BenchmarkNoise.Unknown) ? BenchmarkNoise.Unknown
            : BenchmarkNoise.Quiet;
    }

    /// <summary>
    /// A run's stored provenance (MN-112): <paramref name="recorded"/> (the first measured instance's) with the config hash
    /// the job read when it has one, the data path, and the verdict over <paramref name="verdicts"/>
    /// (<see cref="Combine"/>). Null when nothing was recorded (no source: the run is stored as before).
    /// </summary>
    public static BenchmarkProvenance? Complete(BenchmarkProvenance? recorded, string? configHash, string? proxy, IEnumerable<BenchmarkNoise> verdicts) =>
        recorded is null ? null : recorded with
        {
            ConfigHash = configHash ?? recorded.ConfigHash,
            Proxy = proxy,
            Noise = Combine(verdicts),
        };

    /// <summary>A model's delivery: llama.cpp's own setting, else container (vLLM always runs in a container).</summary>
    public static LaunchDelivery DeliveryOf(ModelDefinition? model) =>
        model?.Runtime == RuntimeKind.LlamaCpp ? model.LlamaCpp?.Delivery ?? LaunchDelivery.Container : LaunchDelivery.Container;

    private static BenchmarkProvenance Legacy(string bootstrapNodeId, ModelDefinition? settings, string? configHash, string? proxy, BenchmarkNoise? noise) =>
        new()
        {
            NodeId = string.IsNullOrEmpty(bootstrapNodeId) ? RegistryMigrationOptions.DefaultBootstrapNodeId : bootstrapNodeId,
            Runtime = settings?.Runtime ?? RuntimeKind.Vllm,
            Delivery = DeliveryOf(settings),
            ConfigHash = configHash,
            Proxy = proxy,
            Noise = noise ?? BenchmarkNoise.Unknown,
        };
}
