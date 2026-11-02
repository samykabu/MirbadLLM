using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// The inverse of <see cref="VllmArgsBuilder"/>: reads a running container's command line back into a
/// <see cref="ModelDefinition"/>, so the UI can say exactly which settings a restart would change, even for a
/// container launched before launch configurations were recorded. Unknown flags land in
/// <see cref="ModelDefinition.ExtraArgs"/>; owned flags found there (containers launched before the canonical
/// configuration, whose ExtraArgs could repeat them) are then lifted with vLLM's last-wins semantics, so the result is
/// what vLLM actually ran with.
/// <para>Discrete placements (design §5.1, MN-106): <see cref="Parse(LaunchConfig, string, double, ModelDefinition)"/>
/// reads the budget back as utilization × gpuTotalGiB × n, with the per-GPU denominator and the GPUs from the launch
/// metadata the agent returns (<see cref="LaunchConfig.Gpus"/>, labels <see cref="ContainerLabels.GpuUuids"/> and
/// <see cref="ContainerLabels.GpuTotalGiB"/>), never from the host's MemTotal. <c>--tensor-parallel-size</c> is read
/// where the builder emits it and is no definition field: the placement's GPU count is.</para>
/// </summary>
public static class VllmArgsParser
{
    public static ModelDefinition Parse(
        IReadOnlyList<string> args, string image, string defaultImage, double totalGiB, ModelDefinition template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var launch = ParseLaunch(args, template);
        return Finish(launch.Model, launch.Utilization, totalGiB, gpuCount: 1, image, defaultImage);
    }

    /// <summary>
    /// Reads a <c>/launch-config</c> back into a definition (design §4.3, §5.1). An instance with GPU metadata (a discrete
    /// placement) reads its budget as utilization × <see cref="ContainerLabels.GpuTotalGiB"/> × n and its placement's GPUs
    /// from <see cref="LaunchConfig.Gpus"/> (else the <see cref="ContainerLabels.GpuUuids"/> label); every other instance
    /// (unified, from before P2) is read exactly as
    /// <see cref="Parse(IReadOnlyList{string}, string, string, double, ModelDefinition)"/> reads it, against
    /// <paramref name="hostTotalGiB"/>.
    /// </summary>
    public static ModelDefinition Parse(LaunchConfig launch, string defaultImage, double hostTotalGiB, ModelDefinition template)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(template);
        var uuids = LaunchedGpuUuids(launch);
        if (uuids.Count == 0)
        {
            return Parse(launch.Argv, launch.Image ?? string.Empty, defaultImage, hostTotalGiB, template);
        }

        var parsed = ParseLaunch(launch.Argv, template);
        var model = Finish(parsed.Model, parsed.Utilization, GpuTotalGiB(launch) ?? 0, uuids.Count, launch.Image ?? string.Empty, defaultImage);
        return model with { Placement = LaunchedPlacement(template.Placement, launch, uuids) };
    }

    /// <summary>
    /// The ordered GPU UUIDs of an instance: <see cref="LaunchConfig.Gpus"/>, else the <see cref="ContainerLabels.GpuUuids"/>
    /// label; empty = all GPUs (unified, P1).
    /// </summary>
    public static IReadOnlyList<string> LaunchedGpuUuids(LaunchConfig launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (launch.Gpus.Count > 0)
        {
            return [.. launch.Gpus.Select(g => g.Uuid)];
        }

        return launch.Labels.TryGetValue(ContainerLabels.GpuUuids, out string? label) && !string.IsNullOrWhiteSpace(label)
            ? [.. label.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];
    }

    /// <summary>The per-GPU denominator an instance was launched with (<see cref="ContainerLabels.GpuTotalGiB"/>); null when absent or unreadable.</summary>
    public static double? GpuTotalGiB(LaunchConfig launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        return launch.Labels.TryGetValue(ContainerLabels.GpuTotalGiB, out string? text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double total) && total > 0
                ? total
                : null;
    }

    private static ModelDefinition Finish(
        ModelDefinition model, string? utilization, double denominatorGiB, int gpuCount, string image, string defaultImage)
    {
        if (double.TryParse(utilization, NumberStyles.Float, CultureInfo.InvariantCulture, out double fraction) && denominatorGiB > 0)
        {
            model = model with { MemoryBudgetGiB = Math.Round(fraction * denominatorGiB * gpuCount, 1) };
        }

        return model with
        {
            Image = string.IsNullOrEmpty(image) || string.Equals(image, defaultImage, StringComparison.Ordinal) ? null : image,
        };
    }

    /// <summary>
    /// The placement an instance runs on: the saved node with the launched GPUs. The indices are the instance's current
    /// ones when the agent reports them all, else the saved ones when the GPUs are the saved ones, else none.
    /// </summary>
    private static ModelPlacement? LaunchedPlacement(ModelPlacement? saved, LaunchConfig launch, IReadOnlyList<string> uuids)
    {
        string? nodeId = saved?.NodeId ?? launch.Labels.GetValueOrDefault(ContainerLabels.Node);
        if (nodeId is null)
        {
            return saved;
        }

        IReadOnlyList<int>? indices = launch.Gpus.Count == uuids.Count && launch.Gpus.All(g => g.Index >= 0)
            ? [.. launch.Gpus.Select(g => g.Index)]
            : saved?.GpuUuids is { } savedUuids && savedUuids.SequenceEqual(uuids, StringComparer.Ordinal) ? saved.GpuIndices : null;
        return saved is null
            ? new ModelPlacement(nodeId, uuids, indices)
            : saved with { GpuUuids = uuids, GpuIndices = indices };
    }

    /// <summary>
    /// The canonical form of a launch command line: parsed, normalized and rebuilt in the builder's order, keeping the
    /// <c>--gpu-memory-utilization</c> text verbatim. Two command lines vLLM reads the same way (an owned flag repeated in
    /// the trailing raw arguments, JSON keys in another order) canonicalize to the same list; a builder output is its
    /// own canonical form.
    /// </summary>
    public static IReadOnlyList<string> Canonicalize(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var template = new ModelDefinition { Id = "canonical", ServedName = "", ModelPath = "" };
        var (model, utilization, tensorParallel) = ParseLaunch(args, template);
        return VllmArgsBuilder.BuildCore(model, utilization, tensorParallel);
    }

    /// <summary>
    /// Parses everything except the image and the utilization→budget conversion; returns the raw utilization text and the
    /// raw <c>--tensor-parallel-size</c> text where the builder emits it (before the trailing raw arguments).
    /// </summary>
    private static (ModelDefinition Model, string? Utilization, string? TensorParallel) ParseLaunch(IReadOnlyList<string> args, ModelDefinition template)
    {
        var model = template with
        {
            Quantization = null,
            ToolCallParser = null,
            ReasoningParser = null,
            EnablePrefixCaching = null,
            Speculative = null,
            CudaGraphMode = null,
            CompilationConfig = null,
            AsyncScheduling = null,
            MambaCacheMode = null,
            MaxNumBatchedTokens = null,
            ExtraArgs = [],
        };

        string? utilization = null;
        string? tensorParallel = null;
        var extra = new List<string>();
        bool inExtra = false;
        int i = 0;
        string? Next() => i + 1 < args.Count ? args[++i] : null;

        for (; i < args.Count; i++)
        {
            string arg = args[i];
            if (inExtra)
            {
                extra.Add(arg);
                continue;
            }

            switch (arg)
            {
                case "--model":
                    string? path = Next();
                    model = model with
                    {
                        ModelPath = path is not null && path.StartsWith("/models/", StringComparison.Ordinal)
                            ? path["/models/".Length..]
                            : path ?? model.ModelPath,
                    };
                    break;
                case "--served-model-name":
                    model = model with { ServedName = Next() ?? model.ServedName };
                    break;
                case "--quantization":
                    model = model with { Quantization = Next() };
                    break;
                case "--kv-cache-dtype":
                    model = model with { KvCacheDtype = Next() ?? model.KvCacheDtype };
                    break;
                case "--max-model-len":
                    model = model with { MaxModelLen = ParseInt(Next()) ?? model.MaxModelLen };
                    break;
                case "--max-num-seqs":
                    model = model with { MaxNumSeqs = ParseInt(Next()) ?? model.MaxNumSeqs };
                    break;
                case "--max-num-batched-tokens":
                    model = model with { MaxNumBatchedTokens = ParseInt(Next()) };
                    break;
                case VllmArgsNormalizer.TensorParallelFlag:
                    tensorParallel = Next();
                    break;
                case "--gpu-memory-utilization":
                    utilization = Next();
                    break;
                case "--enable-auto-tool-choice":
                    break;
                case "--tool-call-parser":
                    model = model with { ToolCallParser = Next() };
                    break;
                case "--reasoning-parser":
                    model = model with { ReasoningParser = Next() };
                    break;
                case "--enable-prefix-caching":
                    model = model with { EnablePrefixCaching = true };
                    break;
                case "--no-enable-prefix-caching":
                    model = model with { EnablePrefixCaching = false };
                    break;
                case "--async-scheduling":
                    model = model with { AsyncScheduling = true };
                    break;
                case "--no-async-scheduling":
                    model = model with { AsyncScheduling = false };
                    break;
                case "--mamba-cache-mode":
                    model = model with { MambaCacheMode = Next() };
                    break;
                case "--speculative-config":
                    model = model with { Speculative = JsonFields.TryParse(Next(), out var spec) && !spec.IsEmpty ? SpeculativeDecoding.FromFields(spec) : null };
                    break;
                case "--compilation-config":
                    var compilation = JsonFields.TryParse(Next(), out var cc) ? cc : JsonFields.Empty;
                    string? mode = compilation.GetString(VllmArgsNormalizer.CudaGraphModeKey);
                    var rest = mode is null ? compilation : compilation.Without(VllmArgsNormalizer.CudaGraphModeKey);
                    model = model with { CudaGraphMode = mode, CompilationConfig = rest.IsEmpty ? null : rest };
                    break;
                case VllmArgsBuilder.AccessLogFlag:
                    Next();

                    // The builder appends ExtraArgs right after this flag's value.
                    inExtra = true;
                    break;
                default:
                    extra.Add(arg);
                    break;
            }
        }

        // Raw arguments come last on the command line, so an owned flag repeated there is the one vLLM used.
        return (VllmArgsNormalizer.Apply(model with { ExtraArgs = extra }), utilization, tensorParallel);
    }

    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}
