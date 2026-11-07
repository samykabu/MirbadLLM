using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>A launch spec with the two hashes it carries (design §5.1 "two fingerprints").</summary>
/// <param name="Spec">The spec sent in <see cref="LaunchRequest.Spec"/>; its labels already hold both hashes.</param>
/// <param name="Image">The image the instance runs (the definition's, else the node default, else the global default); empty for a process spec, which runs <see cref="LaunchSpec.Executable"/>.</param>
/// <param name="ConfigHash">
/// The legacy <see cref="ModelConfigHash"/> (image + canonical vLLM argv), exactly as today. Empty for runtimes without a
/// legacy hash (llama.cpp, design-p3p4 §A.1.6: restart-pending uses the execution fingerprint, which covers their argv);
/// <see cref="LegacyConfigHash"/> is the same value as null.
/// </param>
/// <param name="ExecutionFingerprint">The execution fingerprint of the spec on its node (<see cref="Multinode.ExecutionFingerprint"/>).</param>
public sealed record BuiltLaunchSpec(LaunchSpec Spec, string Image, string ConfigHash, string ExecutionFingerprint)
{
    /// <summary>
    /// <see cref="ConfigHash"/>, or null when the runtime has none (llama.cpp): the value for
    /// <see cref="LaunchRequest.ExpectedConfigHash"/> (the P3 process fixture sends null).
    /// </summary>
    public string? LegacyConfigHash => ConfigHash.Length == 0 ? null : ConfigHash;
}

/// <summary>
/// Environment variables of a launch (design §4.3, §10.4; MN-107): sent only to nodes reporting
/// <see cref="AgentFeatures.LaunchEnv"/>. <paramref name="SecretNames"/> name the variables whose values are secrets: the
/// agent keeps their values out of <c>launch-config</c>, its logs and its operation log, and the payload digest and
/// execution fingerprint leave them out (D38).
/// </summary>
/// <param name="Variables">Name → value.</param>
/// <param name="SecretNames">Names in <paramref name="Variables"/> whose values are secrets.</param>
public sealed record LaunchEnvironment(IReadOnlyDictionary<string, string> Variables, IReadOnlyList<string> SecretNames)
{
    /// <summary>No variables (every P1 launch).</summary>
    public static LaunchEnvironment None { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal), []);

    /// <summary>True when nothing would be sent.</summary>
    public bool IsEmpty => Variables.Count == 0 && SecretNames.Count == 0;

    /// <summary>Names and non-secret values only; secret values are never printed.</summary>
    public override string ToString() =>
        "{" + string.Join(", ", Variables.Select(v => v.Key + "=" + (SecretNames.Contains(v.Key, StringComparer.Ordinal) ? Protocol.RedactedValue : v.Value))) + "}";
}

/// <summary>
/// Builds the agent <see cref="LaunchSpec"/> of a vLLM <see cref="ModelDefinition"/> on a node (design §4.3, §5.1, §10.3):
/// the pre-P1 <c>DockerModelRuntime</c> container (image, <see cref="VllmArgsBuilder"/> argv with the node's total memory,
/// <c>models</c> read-only at <c>/models</c>, <c>cache</c> at <c>/root/.cache</c>, port 8000, <c>ipc: host</c>, every GPU)
/// expressed in logical terms, with today's labels (served name, legacy config hash, budget, tuning) plus the execution
/// fingerprint. Pure: no I/O.
/// <para>P1 (W2 review MN-092, D47): the spec always carries an empty <c>env</c>, no secret names and
/// <c>gpus.mode = all</c>, which is all the P1 container driver accepts; GPU selection by UUID (MN-106) extends this
/// builder in P2. The agent sets the reserved labels (model, node, operation) and the port itself, so the spec never
/// carries them.</para>
/// <para>P2 (MN-107, D73): a definition without an image of its own runs the node default image with its approved digest
/// (<see cref="NodeRecord.DefaultVllmImageDigest"/>, sent as <c>spec.imageDigest</c>); <c>spec.image</c> stays the tag, so
/// the legacy config hash is unchanged. Environment variables are sent only to a node reporting
/// <see cref="AgentFeatures.LaunchEnv"/>. A node with neither (the DGX in P1) gets exactly the P1 spec.</para>
/// <para>P2 discrete placements (MN-106, design §5.1, D25, D73): a placement that names GPUs
/// (<see cref="ModelPlacement.GpuUuids"/>) is sent only to a node reporting <see cref="AgentFeatures.GpuSelectionUuids"/>,
/// as <c>gpus.mode = uuids</c> with the saved UUIDs in order (never indices: a renumbered card keeps its model). The
/// budget is the total across the GPUs; the <c>--gpu-memory-utilization</c> denominator is the placed GPUs'
/// <c>memory.total</c> from the node's last capability report, and more than one GPU adds
/// <c>--tensor-parallel-size n</c>. The denominator goes into the <see cref="ContainerLabels.GpuTotalGiB"/> label for the
/// read-back (the agent adds <see cref="ContainerLabels.GpuUuids"/>), and the UUIDs into the execution fingerprint. A
/// placement without GPUs (unified, every P1 definition) is built exactly as before.</para>
/// </summary>
public static class LaunchSpecBuilder
{
    /// <summary>vLLM's port inside the container (today's <c>ContainerPort</c>).</summary>
    public const int VllmContainerPort = 8000;

    /// <summary>
    /// The image a definition with no image of its own runs on <paramref name="node"/> (design §5.1 "Default image per
    /// node"): <c>node.DefaultVllmImage ?? MirbadLLMOptions.VllmImage</c>. The DGX keeps no node default in P1, so this is
    /// today's fallback and the legacy hash input is unchanged.
    /// </summary>
    public static string DefaultImage(NodeRecord node, MirbadLLMOptions options)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrEmpty(node.DefaultVllmImage) ? options.VllmImage : node.DefaultVllmImage;
    }

    /// <summary>The image <paramref name="model"/> runs: its own, else <paramref name="defaultImage"/> (as <see cref="ModelConfigHash"/> resolves it).</summary>
    public static string ResolveImage(ModelDefinition model, string defaultImage)
    {
        ArgumentNullException.ThrowIfNull(model);
        return string.IsNullOrEmpty(model.Image) ? defaultImage : model.Image;
    }

    /// <summary>
    /// The approved digest of the image <paramref name="model"/> runs on <paramref name="node"/>: the definition's own
    /// <see cref="ModelDefinition.ImageDigest"/> when it has an image of its own (W7 review MN-126); else the node default's
    /// <see cref="NodeRecord.DefaultVllmImageDigest"/> when <paramref name="defaultImage"/> is the node default image; else
    /// null (the global <c>MirbadLLM:VllmImage</c> fallback has none).
    /// </summary>
    public static string? ResolveImageDigest(ModelDefinition model, NodeRecord node, string defaultImage)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        if (!string.IsNullOrEmpty(model.Image))
        {
            return string.IsNullOrEmpty(model.ImageDigest) ? null : model.ImageDigest;
        }

        return !string.IsNullOrEmpty(node.DefaultVllmImage) &&
            !string.IsNullOrEmpty(node.DefaultVllmImage) &&
            string.Equals(defaultImage, node.DefaultVllmImage, StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(node.DefaultVllmImageDigest)
                ? node.DefaultVllmImageDigest
                : null;
    }

    /// <summary>True when <paramref name="features"/> (null = <paramref name="node"/>'s last capability report) holds <paramref name="feature"/>.</summary>
    public static bool NodeReports(NodeRecord node, string feature, IReadOnlyList<string>? features = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrEmpty(feature);
        return (features ?? node.LastCapabilities?.Features ?? []).Contains(feature, StringComparer.Ordinal);
    }

    /// <summary>
    /// The GPUs <paramref name="model"/>'s placement names (<see cref="ModelPlacement.GpuUuids"/>, in order); empty on
    /// unified and GPU-less placements, which run on every GPU of the node (<c>gpus.mode = all</c>).
    /// </summary>
    public static IReadOnlyList<string> PlacedGpuUuids(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Placement?.GpuUuids ?? [];
    }

    /// <summary>
    /// The per-GPU <c>--gpu-memory-utilization</c> denominator of a placement on discrete GPUs (design §5.1): the placed
    /// GPUs' <c>memory.total</c> (GiB) from <paramref name="node"/>'s last capability report. A placed GPU the report does
    /// not list is left to the agent, which refuses it with 422 <c>gpu_missing</c>; tensor parallelism needs one capacity
    /// on every GPU (D25).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// None of the placed GPUs is in the node's last report (nothing to compute the utilization from), or the placed GPUs
    /// report different capacities.
    /// </exception>
    public static double PerGpuTotalGiB(ModelDefinition model, NodeRecord node)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        var uuids = PlacedGpuUuids(model);
        var reported = node.LastCapabilities?.Gpus ?? [];
        var totals = uuids
            .Select(u => reported.FirstOrDefault(g => string.Equals(g.Uuid, u, StringComparison.Ordinal))?.MemoryTotalGiB)
            .OfType<double>()
            .Where(t => t > 0)
            .Distinct()
            .ToList();
        if (totals.Count == 0)
        {
            throw Refusal(node, ErrorCodes.GpuMissing,
                $"Model '{model.Id}' is placed on GPU(s) {string.Join(", ", uuids)}, which node '{node.Id}' does not report " +
                "(with their memory) in its last capability report; select its GPUs again.",
                new GpuMissingDetails([.. uuids]));
        }

        if (totals.Count > 1)
        {
            throw Refusal(node, ErrorCodes.SpecRejected,
                $"Model '{model.Id}' is placed on GPUs of different memory on node '{node.Id}'; tensor parallelism needs equal GPUs.",
                new SpecRejectedDetails("tensor parallelism needs equal GPUs"));
        }

        return totals[0];
    }

    /// <summary>
    /// A refusal the master makes from its own view of the node, shaped like the agent's (W8 review MN-128): an
    /// <see cref="ArgumentException"/> over a 422 <see cref="AgentException"/> with <paramref name="code"/>, exactly what
    /// <see cref="Agents.AgentErrorMapper"/> makes of the agent's own <c>gpu_missing</c>/<c>spec_rejected</c>, so the admin
    /// API answers 422 for both instead of 500.
    /// </summary>
    private static ArgumentException Refusal<TDetails>(NodeRecord node, string code, string message, TDetails details)
    {
        var agent = new AgentException(ErrorMappings.AgentStatus(code), new AgentError
        {
            Code = code,
            Message = message,
            Details = AgentJson.ToElement(details),
        }, node.Id);
        return new ArgumentException(message, agent);
    }

    /// <summary>
    /// The legacy config hash <paramref name="model"/> launches with on <paramref name="node"/> (empty for llama.cpp, which
    /// has none: <see cref="BuiltLaunchSpec.ConfigHash"/>): on a placement without GPUs
    /// exactly today's <see cref="ModelConfigHash.Compute(ModelDefinition, string, double)"/> over
    /// <paramref name="totalGiB"/>; on discrete GPUs the hash of the per-GPU argv (<see cref="PerGpuTotalGiB"/>, TP).
    /// </summary>
    public static string ConfigHash(ModelDefinition model, NodeRecord node, string defaultImage, double totalGiB)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        if (model.Runtime != RuntimeKind.Vllm)
        {
            // llama.cpp has no legacy config hash (design-p3p4 §A.1.6); the execution fingerprint covers its argv.
            return string.Empty;
        }

        var uuids = PlacedGpuUuids(model);
        return uuids.Count == 0
            ? ModelConfigHash.Compute(model, defaultImage, totalGiB)
            : ModelConfigHash.Compute(ResolveImage(model, defaultImage), VllmArgsBuilder.Build(model, PerGpuTotalGiB(model, node), uuids.Count));
    }

    /// <summary>
    /// The spec of <paramref name="model"/> on <paramref name="node"/>. <paramref name="totalGiB"/> is the node's total
    /// unified memory (the <c>--gpu-memory-utilization</c> denominator, today's MemTotal on the DGX); a placement on discrete
    /// GPUs uses the GPUs' own capacity instead (<see cref="PerGpuTotalGiB"/>) and selects them by UUID.
    /// <paramref name="environment"/> (MN-107) is sent only when the node reports <see cref="AgentFeatures.LaunchEnv"/>
    /// in <paramref name="nodeFeatures"/> (null = the node's last capability report).
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The definition is a llama.cpp or MLX model the node cannot run (<see cref="BuildLlamaCpp"/>, <see cref="BuildMlx"/>,
    /// which ignore <paramref name="defaultImage"/> and <paramref name="totalGiB"/>), it needs environment variables on a node
    /// that does not report <see cref="AgentFeatures.LaunchEnv"/>, or it is placed on GPUs of a node that does not report
    /// <see cref="AgentFeatures.GpuSelectionUuids"/> (a P1 agent would refuse the spec, D47).
    /// </exception>
    /// <exception cref="ArgumentException">The placed GPUs give no usable denominator (<see cref="PerGpuTotalGiB"/>).</exception>
    public static BuiltLaunchSpec Build(
        ModelDefinition model,
        NodeRecord node,
        string defaultImage,
        double totalGiB,
        bool tuning,
        LaunchEnvironment? environment = null,
        IReadOnlyList<string>? nodeFeatures = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrEmpty(defaultImage);
        if (model.Runtime == RuntimeKind.LlamaCpp)
        {
            return BuildLlamaCpp(model, node, tuning, environment, nodeFeatures);
        }

        if (model.Runtime == RuntimeKind.Mlx)
        {
            return BuildMlx(model, node, tuning, environment, nodeFeatures);
        }

        if (model.Runtime is RuntimeKind.Sglang or RuntimeKind.Nim)
        {
            return BuildAdditionalContainer(model, node, tuning, environment, nodeFeatures);
        }

        if (model.Runtime != RuntimeKind.Vllm)
        {
            throw new NotSupportedException($"Model '{model.Id}' uses runtime {model.Runtime}, which this master cannot launch yet.");
        }

        environment ??= LaunchEnvironment.None;
        if (!environment.IsEmpty && !NodeReports(node, AgentFeatures.LaunchEnv, nodeFeatures))
        {
            throw new NotSupportedException(
                $"Model '{model.Id}' sets environment variables, but node '{node.Id}' does not support them (feature '{AgentFeatures.LaunchEnv}'); upgrade its agent.");
        }

        var gpuUuids = PlacedGpuUuids(model);
        double? gpuTotalGiB = null;
        if (gpuUuids.Count > 0)
        {
            if (!NodeReports(node, AgentFeatures.GpuSelectionUuids, nodeFeatures))
            {
                throw new NotSupportedException(
                    $"Model '{model.Id}' is placed on selected GPUs, but node '{node.Id}' cannot select GPUs (feature '{AgentFeatures.GpuSelectionUuids}'); upgrade its agent.");
            }

            gpuTotalGiB = PerGpuTotalGiB(model, node);
        }

        string image = ResolveImage(model, defaultImage);
        IReadOnlyList<string> argv = gpuTotalGiB is { } perGpu
            ? VllmArgsBuilder.Build(model, perGpu, gpuUuids.Count)
            : VllmArgsBuilder.Build(model, totalGiB);
        string configHash = ModelConfigHash.Compute(image, argv);

        var spec = new LaunchSpec
        {
            Kind = LaunchDelivery.Container,
            Runtime = RuntimeKind.Vllm,
            Image = image,
            ImageDigest = ResolveImageDigest(model, node, defaultImage),
            Argv = argv,
            Env = new Dictionary<string, string>(environment.Variables, StringComparer.Ordinal),
            SecretEnvNames = [.. environment.SecretNames],
            ContainerPort = VllmContainerPort,
            Mounts =
            [
                new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true),
                new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false),
            ],
            Gpus = gpuUuids.Count > 0
                ? new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [.. gpuUuids] }
                : new GpuSelection { Mode = GpuSelectionMode.All },
            IpcHost = true,
            HealthPath = "/health",
        };

        string fingerprint = Multinode.ExecutionFingerprint.Compute(spec, node.Uid, [.. gpuUuids], configHash);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContainerLabels.ServedName] = model.ServedName,
            [ContainerLabels.ConfigHash] = configHash,
            [ContainerLabels.ExecutionFingerprint] = fingerprint,
            [ContainerLabels.BudgetGiB] = model.MemoryBudgetGiB.ToString("0.###", CultureInfo.InvariantCulture),
        };
        if (gpuTotalGiB is { } denominator)
        {
            // The read-back denominator (design §5.1): exactly the number the utilization was computed with.
            labels[ContainerLabels.GpuTotalGiB] = denominator.ToString("R", CultureInfo.InvariantCulture);
        }

        if (tuning)
        {
            labels[ContainerLabels.Tuning] = "1";
        }

        return new BuiltLaunchSpec(spec with { Labels = labels }, image, configHash, fingerprint);
    }

    /// <summary>
    /// The admission inputs of a launch (design §4.3, §6.1): the model's budget and the node's reserve. A placement on
    /// discrete GPUs also carries its host-memory budget (<see cref="ModelDefinition.HostMemoryBudgetGiB"/>; null = the
    /// agent's estimate) and, only for a node reporting <see cref="AgentFeatures.AdmissionDiscrete"/> in
    /// <paramref name="nodeFeatures"/> (null = the node's last capability report), the node's per-GPU reserves of the
    /// placed GPUs (<see cref="AdmissionRequest.GpuReservesGiB"/>, MN-105, D73). A llama.cpp model with per-GPU budgets
    /// (<see cref="LlamaCppSettings.GpuBudgetsGiB"/>) sends them by UUID (<see cref="AdmissionRequest.GpuBudgetsGiB"/>) only
    /// to a node reporting <see cref="AgentFeatures.AdmissionGpuBudgets"/> (design-p3p4 §A.1.5, MN-210, D116); a llama.cpp
    /// model split over two or more GPUs without its own sends budgets derived in the proportion of its tensor split
    /// (<see cref="PlacementFit.LlamaCppGpuBudgets"/>; MN-224, D129 (4)), so the smaller card is not admitted the even
    /// share. Without the flag nothing is sent and the agent splits evenly (P2). A placement without GPUs gets exactly the
    /// P1 request.
    /// </summary>
    /// <param name="model">The model being launched.</param>
    /// <param name="node">The node it is launched on.</param>
    /// <param name="force">The operator's override.</param>
    /// <param name="nodeFeatures">The node's features; null = its last capability report.</param>
    /// <param name="estimate">
    /// The GGUF estimate of a llama.cpp model on this placement (<see cref="PlacementFit.EstimateAsync"/>), so the host
    /// budget the launch sends is the one the preview showed (MN-239-17); null = the flat llama.cpp estimate of
    /// <see cref="PlacementFit.EstimateHostGiB"/>, which is right for a full offload and the old behaviour otherwise.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The per-GPU budgets do not match the placement (<see cref="LlamaCppArgsBuilder.GpuBudgetProblems"/>), or a split GPU is
    /// missing from the node's report (<see cref="TensorSplitWeights"/>, as <see cref="BuildLlamaCpp"/> refuses it).
    /// </exception>
    public static AdmissionRequest Admission(
        ModelDefinition model, NodeRecord node, bool force, IReadOnlyList<string>? nodeFeatures = null, GgufMemoryEstimate? estimate = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        var gpuUuids = PlacedGpuUuids(model);
        if (gpuUuids.Count == 0)
        {
            return new AdmissionRequest
            {
                BudgetGiB = model.MemoryBudgetGiB,
                HostBudgetGiB = null,
                ReserveGiB = node.MemoryReserveGiB,
                GpuReserveGiB = null,
                Force = force,
            };
        }

        Dictionary<string, double>? reserves = null;
        if (NodeReports(node, AgentFeatures.AdmissionDiscrete, nodeFeatures))
        {
            foreach (string uuid in gpuUuids)
            {
                if (node.GpuReserveGiB.TryGetValue(uuid, out double reserve))
                {
                    (reserves ??= new Dictionary<string, double>(StringComparer.Ordinal))[uuid] = reserve;
                }
            }
        }

        return new AdmissionRequest
        {
            BudgetGiB = model.MemoryBudgetGiB,
            GpuBudgetsGiB = GpuBudgets(model, node, gpuUuids, nodeFeatures),
            // The agent sees no runtime arguments: an explicit --swap-space reaches it as the host budget the fit preview
            // estimated (PlacementFit.EstimateHostGiB), so preview and admission agree (MN-124). Without one a vLLM start
            // leaves it unset and both use the same default estimate (launch bodies unchanged).
            // A llama.cpp start always sends its own figure (MN-230, D130): the agent's default is vLLM's
            // 4 GiB × GPUs + 2, while the fit preview estimates 1 GiB plus any layers left in host RAM, so the agent
            // reserved several GiB nobody needs. An explicit HostMemoryBudgetGiB (what the editor writes for a partial
            // offload) still wins.
            // The figure is PlacementFit.LlamaCppHostGiB of the same estimate the preview showed (MN-239-17): the flat
            // EstimateHostGiB(1 GiB) admitted a partial offload on far less host RAM than the UI implied — measured at
            // 1 GiB sent against 2.99 GiB previewed for the embedder with GpuLayers 18 of 36. Without an estimate (no
            // caller, or a node that could not answer) it is that flat 1 GiB again, as before.
            HostBudgetGiB = model.HostMemoryBudgetGiB
                ?? (model.Runtime == RuntimeKind.LlamaCpp
                    ? PlacementFit.LlamaCppHostGiB(estimate)
                    : PlacementFit.ExplicitSwapSpaceGiB(model) is not null
                        ? PlacementFit.EstimateHostGiB(model, gpuUuids.Count)
                        : null),
            ReserveGiB = node.MemoryReserveGiB,
            GpuReserveGiB = null,
            GpuReservesGiB = reserves,
            Force = force,
        };
    }

    // ---------------------------------------------------------------- llama.cpp process specs (design-p3p4 §A.1, MN-210)

    /// <summary>
    /// The <c>llama-server</c> version <paramref name="model"/> launches with on <paramref name="node"/> (design-p3p4
    /// §A.1.2, D117): always concrete, so the launch fingerprint and the read-back's agree and a changed node default
    /// shows as "restart pending". The model's pin (<see cref="LlamaCppSettings.ExecutableVersion"/>) as it is (the agent
    /// refuses an unknown or unverified one with 422, and MN-224's placement check names it first); otherwise the node's
    /// default from its last report (<see cref="RuntimeCapability.Versions"/> of the llama.cpp process runtime), which
    /// must be verified.
    /// </summary>
    /// <exception cref="NotSupportedException">No pin, and the node reports no verified default version.</exception>
    public static string ResolveExecutableVersion(ModelDefinition model, NodeRecord node)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        if (PinnedExecutableVersion(model) is { Length: > 0 } pinned)
        {
            return pinned;
        }

        var kind = model.Runtime == RuntimeKind.Mlx ? RuntimeKind.Mlx : RuntimeKind.LlamaCpp;
        string executable = kind == RuntimeKind.Mlx ? ExecutableNames.MlxLmServer : ExecutableNames.LlamaServer;
        string runtime = kind == RuntimeKind.Mlx ? "MLX" : "llama.cpp";
        return NodeDefaultExecutableVersion(node, kind)
            ?? throw new NotSupportedException(
                $"Model '{model.Id}' pins no {executable} version and node '{node.Id}' reports no verified default version " +
                $"({DescribeVersions(node, kind)}); install or repair {runtime} on the node, or pin a verified version.");
    }

    /// <summary>The executable version a model pins (<see cref="LlamaCppSettings.ExecutableVersion"/> or <see cref="MlxSettings.ExecutableVersion"/>); null for none.</summary>
    public static string? PinnedExecutableVersion(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Runtime switch
        {
            RuntimeKind.LlamaCpp => model.LlamaCpp?.ExecutableVersion,
            RuntimeKind.Mlx => model.Mlx?.ExecutableVersion,
            _ => null,
        };
    }

    /// <summary>
    /// The node's verified default version of the <paramref name="kind"/> process runtime (llama.cpp unless given) from its
    /// last report (<see cref="RuntimeCapability.Versions"/>); null when it reports none.
    /// </summary>
    public static string? NodeDefaultExecutableVersion(NodeRecord node, RuntimeKind kind = RuntimeKind.LlamaCpp)
    {
        ArgumentNullException.ThrowIfNull(node);
        return ProcessVersions(node, kind).FirstOrDefault(v => v.Default && v.Verified)?.Version;
    }

    /// <summary>
    /// The tensor-split weights of a llama.cpp model on several placed GPUs (design-p3p4 §A.1.5): its per-GPU budgets when
    /// set, else each card's <c>memory.total</c> (the node's last report) minus the node's reserve for it. Null for fewer
    /// than two GPUs or a split mode other than <see cref="LlamaCppSplitMode.Layer"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The budgets do not match the placement, or a placed GPU is missing from the node's last report or has no memory left
    /// after its reserve (422 <c>gpu_missing</c> / <c>spec_rejected</c> shape, like <see cref="PerGpuTotalGiB"/>).
    /// </exception>
    public static IReadOnlyList<double>? TensorSplitWeights(ModelDefinition model, NodeRecord node)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        var uuids = PlacedGpuUuids(model);
        if (model.LlamaCpp is not { } settings || uuids.Count < 2 || settings.EffectiveSplitMode(uuids.Count) != LlamaCppSplitMode.Layer)
        {
            return null;
        }

        ThrowOnBudgetProblems(model, uuids.Count);
        if (settings.GpuBudgetsGiB is { } budgets)
        {
            return budgets;
        }

        var reported = node.LastCapabilities?.Gpus ?? [];
        var weights = new List<double>(uuids.Count);
        foreach (string uuid in uuids)
        {
            var gpu = reported.FirstOrDefault(g => string.Equals(g.Uuid, uuid, StringComparison.Ordinal));
            if (gpu?.MemoryTotalGiB is not { } total || total <= 0)
            {
                throw Refusal(node, ErrorCodes.GpuMissing,
                    $"Model '{model.Id}' is placed on GPU {uuid}, which node '{node.Id}' does not report (with its memory) in its " +
                    "last capability report; select its GPUs again.",
                    new GpuMissingDetails([uuid]));
            }

            double usable = total - node.GpuReserveGiB.GetValueOrDefault(uuid);
            if (usable <= 0)
            {
                throw Refusal(node, ErrorCodes.SpecRejected,
                    $"GPU {uuid} of node '{node.Id}' has no memory left after its reserve, so model '{model.Id}' cannot be split onto it; " +
                    "set per-GPU budgets or lower the reserve.",
                    new SpecRejectedDetails("no memory left after the GPU reserve"));
            }

            weights.Add(usable);
        }

        return weights;
    }

    /// <summary>
    /// The process spec of a llama.cpp model (design-p3p4 §A.1.2–§A.1.6, MN-210): <c>kind: process</c>,
    /// <c>runtime: llamacpp</c>, the logical executable <see cref="ExecutableNames.LlamaServer"/> at a concrete version
    /// (<see cref="ResolveExecutableVersion"/>), the <see cref="LlamaCppArgsBuilder"/> argv (paths as <c>{models}/…</c>
    /// tokens, <c>{port}</c> kept for the agent), no mounts, no image, no <c>ipc: host</c>, GPUs by UUID in placement
    /// order, and the labels served name, execution fingerprint, budget (and tuning). There is no legacy config hash
    /// (<see cref="BuiltLaunchSpec.ConfigHash"/> is empty); the fingerprint covers the executable version and the argv.
    /// <paramref name="environment"/> goes only to a node reporting <see cref="AgentFeatures.LaunchEnv"/> and may not name
    /// <see cref="ProcessEnvironment.Reserved"/> (the agent builds the child's environment itself, §A.1.3).
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The node does not report <see cref="AgentFeatures.RuntimeLlamaCppProcess"/> in <paramref name="nodeFeatures"/> (null =
    /// its last capability report), the model asks for container delivery (llama.cpp containers are deferred, design-p3p4
    /// §A.0), no executable version can be resolved, or environment variables go to a node without
    /// <see cref="AgentFeatures.LaunchEnv"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Invalid settings (<see cref="LlamaCppArgsBuilder.Problems"/>), mismatched per-GPU budgets, a reserved environment
    /// name, or GPUs the tensor split cannot use (<see cref="TensorSplitWeights"/>).
    /// </exception>
    public static BuiltLaunchSpec BuildLlamaCpp(
        ModelDefinition model,
        NodeRecord node,
        bool tuning,
        LaunchEnvironment? environment = null,
        IReadOnlyList<string>? nodeFeatures = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        if (model.Runtime != RuntimeKind.LlamaCpp)
        {
            throw new ArgumentException($"Model '{model.Id}' is not a llama.cpp model.", nameof(model));
        }

        var settings = model.LlamaCpp ?? throw new ArgumentException($"Model '{model.Id}' has no llama.cpp settings.", nameof(model));
        string feature = settings.Delivery == LaunchDelivery.Container
            ? AgentFeatures.RuntimeLlamaCppContainer
            : AgentFeatures.RuntimeLlamaCppProcess;
        if (!NodeReports(node, feature, nodeFeatures))
        {
            throw new NotSupportedException(
                $"Model '{model.Id}' is a llama.cpp model, but node '{node.Id}' cannot run llama.cpp as a {settings.Delivery} " +
                $"(feature '{feature}'); place it on a node that reports it or upgrade its agent.");
        }

        environment ??= LaunchEnvironment.None;
        if (!environment.IsEmpty && !NodeReports(node, AgentFeatures.LaunchEnv, nodeFeatures))
        {
            throw new NotSupportedException(
                $"Model '{model.Id}' sets environment variables, but node '{node.Id}' does not support them (feature '{AgentFeatures.LaunchEnv}'); upgrade its agent.");
        }

        var reserved = settings.Delivery == LaunchDelivery.Process
            ? environment.Variables.Keys.Concat(environment.SecretNames)
                .Where(ProcessEnvironment.IsReserved)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
        if (reserved.Count > 0)
        {
            throw Refusal(node, ErrorCodes.SpecRejected,
                $"Model '{model.Id}' sets {string.Join(", ", reserved)}, which the agent sets itself for a llama.cpp process.",
                new SpecRejectedDetails("reserved environment name: " + string.Join(", ", reserved)));
        }

        var gpuUuids = PlacedGpuUuids(model);
        ThrowOnBudgetProblems(model, gpuUuids.Count);
        IReadOnlyList<string> argv = LlamaCppArgsBuilder.Build(model, gpuUuids.Count, TensorSplitWeights(model, node));
        string? version = settings.Delivery == LaunchDelivery.Process ? ResolveExecutableVersion(model, node) : null;
        string? image = null;
        string? imageDigest = null;
        IReadOnlyList<MountSpec> mounts = [];
        if (settings.Delivery == LaunchDelivery.Container)
        {
            image = string.IsNullOrWhiteSpace(settings.Image) ? node.DefaultLlamaCppImage : settings.Image;
            if (string.IsNullOrWhiteSpace(image))
            {
                throw new NotSupportedException($"Model '{model.Id}' needs a llama.cpp container image; configure it on the model or node '{node.Id}'.");
            }

            imageDigest = string.IsNullOrWhiteSpace(settings.Image) ? node.DefaultLlamaCppImageDigest : settings.ImageDigest;
            argv = ContainerLlamaCppArgs(argv);
            mounts = [new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true)];
        }

        var spec = new LaunchSpec
        {
            Kind = settings.Delivery,
            Runtime = RuntimeKind.LlamaCpp,
            Image = image,
            ImageDigest = imageDigest,
            Executable = settings.Delivery == LaunchDelivery.Process ? ExecutableNames.LlamaServer : null,
            ExecutableVersion = version,
            Argv = argv,
            Env = new Dictionary<string, string>(environment.Variables, StringComparer.Ordinal),
            SecretEnvNames = [.. environment.SecretNames],
            ContainerPort = VllmContainerPort,
            Mounts = mounts,
            Gpus = gpuUuids.Count > 0
                ? new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [.. gpuUuids] }
                : new GpuSelection { Mode = GpuSelectionMode.All },
            IpcHost = settings.Delivery == LaunchDelivery.Container,
            HealthPath = "/health",
        };

        string fingerprint = Multinode.ExecutionFingerprint.Compute(spec, node.Uid, [.. gpuUuids], legacyConfigHash: null, version);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContainerLabels.ServedName] = model.ServedName,
            [ContainerLabels.ExecutionFingerprint] = fingerprint,
            [ContainerLabels.BudgetGiB] = model.MemoryBudgetGiB.ToString("0.###", CultureInfo.InvariantCulture),
        };
        if (tuning)
        {
            labels[ContainerLabels.Tuning] = "1";
        }

        return new BuiltLaunchSpec(spec with { Labels = labels }, image ?? string.Empty, string.Empty, fingerprint);
    }

    private static List<string> ContainerLlamaCppArgs(IReadOnlyList<string> argv)
    {
        var translated = argv.Select(arg => arg.StartsWith(ProcessArgvTokens.Models + "/", StringComparison.Ordinal)
                ? LogicalVolumes.ModelsTarget + arg[ProcessArgvTokens.Models.Length..]
                : arg == ProcessArgvTokens.Port ? VllmContainerPort.ToString(CultureInfo.InvariantCulture) : arg)
            .ToList();
        int host = translated.FindIndex(arg => string.Equals(arg, "--host", StringComparison.Ordinal));
        if (host >= 0 && host + 1 < translated.Count)
        {
            translated[host + 1] = "0.0.0.0";
        }

        return translated;
    }

    /// <summary>Builds an SGLang or NIM container launch using the shared container settings.</summary>
    public static BuiltLaunchSpec BuildAdditionalContainer(
        ModelDefinition model,
        NodeRecord node,
        bool tuning,
        LaunchEnvironment? environment = null,
        IReadOnlyList<string>? nodeFeatures = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        if (model.Runtime is not (RuntimeKind.Sglang or RuntimeKind.Nim))
        {
            throw new ArgumentException($"Model '{model.Id}' is not an SGLang or NIM model.", nameof(model));
        }

        string feature = model.Runtime == RuntimeKind.Sglang
            ? AgentFeatures.RuntimeSglangContainer
            : AgentFeatures.RuntimeNimContainer;
        if (!NodeReports(node, feature, nodeFeatures))
        {
            throw new NotSupportedException($"Node '{node.Id}' does not report {feature} for model '{model.Id}'.");
        }

        var settings = model.Container ?? throw new ArgumentException($"Model '{model.Id}' has no container runtime settings.", nameof(model));
        if (string.IsNullOrWhiteSpace(settings.Image))
        {
            throw new ArgumentException($"Model '{model.Id}' needs an explicit {model.Runtime} image.", nameof(model));
        }

        if (model.Runtime == RuntimeKind.Nim && string.IsNullOrWhiteSpace(settings.ImageDigest))
        {
            throw new ArgumentException($"NIM model '{model.Id}' needs an approved image digest.", nameof(model));
        }

        if (!LlamaCppArgsBuilder.IsRelativePath(model.ModelPath))
        {
            throw new ArgumentException($"Model path '{model.ModelPath}' must be relative to the models root.", nameof(model));
        }

        ValidateContainerExtraArgs(model.Runtime, settings.ExtraArgs);
        environment ??= LaunchEnvironment.None;
        if (!environment.IsEmpty && !NodeReports(node, AgentFeatures.LaunchEnv, nodeFeatures))
        {
            throw new NotSupportedException(
                $"Model '{model.Id}' sets environment variables, but node '{node.Id}' does not support them (feature '{AgentFeatures.LaunchEnv}').");
        }

        var gpuUuids = PlacedGpuUuids(model);
        if (gpuUuids.Count > 0 && !NodeReports(node, AgentFeatures.GpuSelectionUuids, nodeFeatures))
        {
            throw new NotSupportedException($"Node '{node.Id}' cannot select the GPUs placed for model '{model.Id}'.");
        }

        IReadOnlyList<string> argv = model.Runtime == RuntimeKind.Sglang
            ? ["python3", "-m", "sglang.launch_server", "--model-path", $"{LogicalVolumes.ModelsTarget}/{model.ModelPath}",
                "--served-model-name", model.ServedName, "--host", "0.0.0.0", "--port", VllmContainerPort.ToString(CultureInfo.InvariantCulture),
                .. settings.ExtraArgs]
            : [.. settings.ExtraArgs];
        var spec = new LaunchSpec
        {
            Kind = LaunchDelivery.Container,
            Runtime = model.Runtime,
            Image = settings.Image,
            ImageDigest = settings.ImageDigest,
            Argv = argv,
            Env = new Dictionary<string, string>(environment.Variables, StringComparer.Ordinal),
            SecretEnvNames = [.. environment.SecretNames],
            ContainerPort = VllmContainerPort,
            Mounts = model.Runtime == RuntimeKind.Sglang
                ? [new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true),
                    new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false)]
                : [new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false)],
            Gpus = gpuUuids.Count > 0
                ? new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [.. gpuUuids] }
                : new GpuSelection { Mode = GpuSelectionMode.All },
            IpcHost = true,
            HealthPath = model.Runtime == RuntimeKind.Nim ? "/v1/health/ready" : "/health",
        };
        string fingerprint = Multinode.ExecutionFingerprint.Compute(spec, node.Uid, [.. gpuUuids], legacyConfigHash: null);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContainerLabels.ServedName] = model.ServedName,
            [ContainerLabels.ExecutionFingerprint] = fingerprint,
            [ContainerLabels.BudgetGiB] = model.MemoryBudgetGiB.ToString("0.###", CultureInfo.InvariantCulture),
        };
        if (tuning)
        {
            labels[ContainerLabels.Tuning] = "1";
        }

        return new BuiltLaunchSpec(spec with { Labels = labels }, settings.Image, string.Empty, fingerprint);
    }

    private static void ValidateContainerExtraArgs(RuntimeKind runtime, IReadOnlyList<string> args)
    {
        string[] owned = runtime == RuntimeKind.Sglang
            ? ["--model", "--model-path", "--served-model-name", "--served-model", "--host", "--port", "-p"]
            : ["--host", "--port", "-p"];
        foreach (string arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || arg.Any(char.IsControl) || arg.Contains('\\') ||
                arg.StartsWith(ProcessArgvTokens.Models, StringComparison.Ordinal) ||
                owned.Any(flag => arg == flag || arg.StartsWith(flag + "=", StringComparison.Ordinal)))
            {
                throw new ArgumentException($"Extra argument '{arg}' is not allowed for {runtime}; mirbadllm owns paths, served name, host and port.", nameof(args));
            }
        }
    }

    // ---------------------------------------------------------------- MLX process specs (docs/macos/implementation-plan.md, Phase 3)

    /// <summary>
    /// The process spec of an MLX model: <c>kind: process</c>, <c>runtime: mlx</c>, the logical executable
    /// <see cref="ExecutableNames.MlxLmServer"/> at a concrete version (<see cref="ResolveExecutableVersion"/>), the
    /// <see cref="MlxArgsBuilder"/> argv, no mounts, no image, no GPUs (Metal uses the node's unified memory) and the labels
    /// served name, execution fingerprint, budget (and tuning). Like llama.cpp there is no legacy config hash.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The node does not report <see cref="AgentFeatures.RuntimeMlxProcess"/> (an Apple Silicon node with mlx-lm installed),
    /// no executable version can be resolved, or environment variables go to a node without <see cref="AgentFeatures.LaunchEnv"/>.
    /// </exception>
    /// <exception cref="ArgumentException">Invalid settings (<see cref="MlxArgsBuilder.Problems"/>), GPUs in the placement, or an environment name the agent sets itself.</exception>
    public static BuiltLaunchSpec BuildMlx(
        ModelDefinition model,
        NodeRecord node,
        bool tuning,
        LaunchEnvironment? environment = null,
        IReadOnlyList<string>? nodeFeatures = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(node);
        if (model.Runtime != RuntimeKind.Mlx)
        {
            throw new ArgumentException($"Model '{model.Id}' is not an MLX model.", nameof(model));
        }

        if (!NodeReports(node, AgentFeatures.RuntimeMlxProcess, nodeFeatures))
        {
            throw new NotSupportedException(
                $"Model '{model.Id}' is an MLX model, but node '{node.Id}' cannot run MLX (feature '{AgentFeatures.RuntimeMlxProcess}': " +
                "an Apple Silicon Mac with mlx-lm installed); place it on such a node.");
        }

        environment ??= LaunchEnvironment.None;
        if (!environment.IsEmpty && !NodeReports(node, AgentFeatures.LaunchEnv, nodeFeatures))
        {
            throw new NotSupportedException(
                $"Model '{model.Id}' sets environment variables, but node '{node.Id}' does not support them (feature '{AgentFeatures.LaunchEnv}'); upgrade its agent.");
        }

        var reserved = environment.Variables.Keys.Concat(environment.SecretNames)
            .Where(ProcessEnvironment.IsReservedForMlx)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (reserved.Count > 0)
        {
            throw Refusal(node, ErrorCodes.SpecRejected,
                $"Model '{model.Id}' sets {string.Join(", ", reserved)}, which the agent sets or fixes itself for an MLX process.",
                new SpecRejectedDetails("reserved environment name: " + string.Join(", ", reserved)));
        }

        if (PlacedGpuUuids(model).Count > 0)
        {
            throw new ArgumentException($"Model '{model.Id}' is an MLX model placed on GPUs; MLX uses the node's unified memory, clear the GPU selection.");
        }

        IReadOnlyList<string> argv = MlxArgsBuilder.Build(model);
        string version = ResolveExecutableVersion(model, node);
        var spec = new LaunchSpec
        {
            Kind = LaunchDelivery.Process,
            Runtime = RuntimeKind.Mlx,
            Image = null,
            ImageDigest = null,
            Executable = ExecutableNames.MlxLmServer,
            ExecutableVersion = version,
            Argv = argv,
            Env = new Dictionary<string, string>(environment.Variables, StringComparer.Ordinal),
            SecretEnvNames = [.. environment.SecretNames],
            Mounts = [],
            Gpus = new GpuSelection { Mode = GpuSelectionMode.All },
            IpcHost = false,
            HealthPath = "/health",
        };

        string fingerprint = Multinode.ExecutionFingerprint.Compute(spec, node.Uid, [], legacyConfigHash: null, version);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContainerLabels.ServedName] = model.ServedName,
            [ContainerLabels.ExecutionFingerprint] = fingerprint,
            [ContainerLabels.BudgetGiB] = model.MemoryBudgetGiB.ToString("0.###", CultureInfo.InvariantCulture),
        };
        if (tuning)
        {
            labels[ContainerLabels.Tuning] = "1";
        }

        return new BuiltLaunchSpec(spec with { Labels = labels }, string.Empty, string.Empty, fingerprint);
    }

    /// <summary>
    /// The per-GPU budgets by UUID for <see cref="AdmissionRequest.GpuBudgetsGiB"/>, or null when not sent: the model's own,
    /// else on two or more GPUs the ones derived in the tensor-split proportion (MN-224, D129 (4):
    /// <see cref="PlacementFit.LlamaCppGpuBudgets"/>, the numbers the fit preview shows).
    /// </summary>
    private static Dictionary<string, double>? GpuBudgets(
        ModelDefinition model, NodeRecord node, IReadOnlyList<string> gpuUuids, IReadOnlyList<string>? nodeFeatures)
    {
        if (model.Runtime != RuntimeKind.LlamaCpp || !NodeReports(node, AgentFeatures.AdmissionGpuBudgets, nodeFeatures) ||
            PlacementFit.LlamaCppGpuBudgets(model, node) is not { } budgets)
        {
            return null;
        }

        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < gpuUuids.Count; i++)
        {
            map[gpuUuids[i]] = budgets[i];
        }

        return map;
    }

    private static void ThrowOnBudgetProblems(ModelDefinition model, int gpuCount)
    {
        var problems = LlamaCppArgsBuilder.GpuBudgetProblems(model, gpuCount);
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems.Select(p => p.Message)));
        }
    }

    private static List<RuntimeVersion> ProcessVersions(NodeRecord node, RuntimeKind kind) =>
        [.. (node.LastCapabilities?.Runtimes ?? [])
            .Where(r => r.Kind == kind && r.Delivery == LaunchDelivery.Process)
            .SelectMany(r => r.Versions ?? [])];

    private static string DescribeVersions(NodeRecord node, RuntimeKind kind)
    {
        var versions = ProcessVersions(node, kind);
        return versions.Count == 0
            ? "none installed"
            : string.Join(", ", versions.Select(v => v.Version + (v.Verified ? string.Empty : " unverified") + (v.Default ? " default" : string.Empty)));
    }
}
