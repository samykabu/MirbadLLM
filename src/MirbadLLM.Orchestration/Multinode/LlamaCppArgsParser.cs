using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// The inverse of <see cref="LlamaCppArgsBuilder"/> (design-p3p4 §A.1.6 "Read-back", MN-210): reads the argv a
/// llama.cpp instance was launched with (<c>/launch-config</c>, tokens kept) back into a <see cref="ModelDefinition"/>,
/// so the Apply dialog can show "old → new". The builder's argv is <c>typed flags</c>, then
/// <see cref="LlamaCppFlags.AlwaysArgs"/>, then the ExtraArgs; the parser reads the typed part, drops the always-args
/// and keeps everything after them verbatim as <see cref="LlamaCppSettings.ExtraArgs"/>. Unknown tokens in the typed part
/// (a hand-built spec) land in ExtraArgs too, in order.
/// <para>
/// Values the argv cannot tell apart are taken from the saved <c>template</c> when it builds the same argv: the
/// embedding micro-batch (an unset <see cref="LlamaCppSettings.MicroBatchSize"/> and its default emit the same
/// <c>-b</c>/<c>-ub</c>; <see cref="LlamaCppSettings.BatchSize"/> is ignored for embeddings), the derived split mode,
/// per-GPU budgets whose shares equal the launched <c>--tensor-split</c>, the pinned or default executable version, and
/// everything that is not an argument (<see cref="LlamaCppSettings.EmulateDimensions"/>, the image, tags, sources). So
/// <c>Build(Parse(Build(m)))</c> equals <c>Build(m)</c>, and an unchanged definition reads back as unchanged. Pure.
/// </para>
/// </summary>
public static class LlamaCppArgsParser
{
    /// <summary>
    /// Reads a <c>/launch-config</c> back: the argv (<see cref="Parse(IReadOnlyList{string}, ModelDefinition, NodeRecord?)"/>),
    /// the executable version (<see cref="LaunchConfig.ExecutableVersion"/>; the template's pin, or no pin when it is the
    /// node's default and the template pins none), the placed GPUs (<see cref="LaunchConfig.Gpus"/>, else the
    /// <see cref="ContainerLabels.GpuUuids"/> label) and the budget (<see cref="ContainerLabels.BudgetGiB"/> label).
    /// </summary>
    /// <param name="launch">The read-back of the instance.</param>
    /// <param name="template">The saved definition (or any definition giving the non-argument fields).</param>
    /// <param name="node">The model's node (default executable version and capacity-proportional split); null = unknown.</param>
    public static ModelDefinition Parse(LaunchConfig launch, ModelDefinition template, NodeRecord? node = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(template);
        var uuids = VllmArgsParser.LaunchedGpuUuids(launch);
        double budget = launch.Labels.TryGetValue(ContainerLabels.BudgetGiB, out string? text) &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedBudget) && parsedBudget > 0
                ? parsedBudget
                : template.MemoryBudgetGiB;

        var placed = template with
        {
            MemoryBudgetGiB = budget,
            Placement = LaunchedPlacement(template.Placement, launch, uuids),
        };
        var model = Parse(launch.Argv, placed, node);
        var settings = model.LlamaCpp!;
        string? saved = template.LlamaCpp?.ExecutableVersion;
        string? launched = launch.ExecutableVersion;
        string? version = launched is null || string.Equals(launched, saved, StringComparison.Ordinal)
            ? saved
            : saved is null && node is not null && string.Equals(launched, LaunchSpecBuilder.NodeDefaultExecutableVersion(node), StringComparison.Ordinal)
                ? null
                : launched;
        return model with { LlamaCpp = settings with { ExecutableVersion = version } };
    }

    /// <summary>
    /// Reads a builder argv back into <paramref name="template"/> (its placement gives the GPU count when the argv has no
    /// <c>--device</c>). The result is a llama.cpp process definition.
    /// </summary>
    public static ModelDefinition Parse(IReadOnlyList<string> argv, ModelDefinition template, NodeRecord? node = null)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(template);
        var saved = template.LlamaCpp ?? new LlamaCppSettings { ModelFile = string.Empty };
        int always = IndexOfAlwaysArgs(argv);
        var typed = always >= 0 ? argv.Take(always).ToList() : [.. argv];
        var trailing = always >= 0 ? argv.Skip(always + LlamaCppFlags.AlwaysArgs.Count).ToList() : [];

        var extra = new List<string>();
        string servedName = template.ServedName;
        string modelFile = saved.ModelFile;
        string? projector = null, draft = null, cacheK = null, cacheV = null, pooling = null, chatTemplate = null, tensorSplit = null;
        int? contextTotal = null, parallel = null, gpuLayers = null, batch = null, micro = null, threads = null, devices = null;
        bool? flashAttention = null, reasoning = null;
        bool embedding = false;
        LlamaCppSplitMode? splitMode = null;

        for (int i = 0; i < typed.Count; i++)
        {
            string arg = typed[i];
            string? value = i + 1 < typed.Count ? typed[i + 1] : null;
            bool consumed = true;
            switch (arg)
            {
                case "-m" when value is not null:
                    modelFile = StripModels(value);
                    break;
                case "--mmproj" when value is not null:
                    projector = StripModels(value);
                    break;
                case "-md" when value is not null:
                    draft = StripModels(value);
                    break;
                case "--alias" when value is not null:
                    servedName = value;
                    break;
                case "-c" when Int(value) is { } c:
                    contextTotal = c;
                    break;
                case "-np" when Int(value) is { } slots:
                    parallel = slots;
                    break;
                case "-ngl" when Int(value) is { } ngl:
                    gpuLayers = ngl;
                    break;
                case "-fa" when OnOffAuto(value, out bool? fa):
                    flashAttention = fa;
                    break;
                case "-ctk" when value is not null:
                    cacheK = value;
                    break;
                case "-ctv" when value is not null:
                    cacheV = value;
                    break;
                case "-b" when Int(value) is { } b:
                    batch = b;
                    break;
                case "-ub" when Int(value) is { } ub:
                    micro = ub;
                    break;
                case "--pooling" when value is not null:
                    pooling = value;
                    break;
                case "--split-mode" when value is "none":
                    splitMode = LlamaCppSplitMode.None;
                    break;
                case "--split-mode" when value is "layer":
                    splitMode = LlamaCppSplitMode.Layer;
                    break;
                case "--tensor-split" when value is not null:
                    tensorSplit = value;
                    break;
                case "--device" when value is not null:
                    devices = value.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
                    break;
                case "--chat-template" when value is not null:
                    chatTemplate = LlamaCppChatTemplate.BuiltinPrefix + value;
                    break;
                case "--chat-template-file" when value is not null:
                    chatTemplate = ChatTemplateFile(value, template.ModelPath);
                    break;
                case "--reasoning" when OnOffAuto(value, out bool? r):
                    reasoning = r;
                    break;
                case "-t" when Int(value) is { } t:
                    threads = t;
                    break;
                default:
                    consumed = false;
                    break;
            }

            if (consumed)
            {
                i++;
                continue;
            }

            if (arg == "--embedding")
            {
                embedding = true;
            }
            else if (always < 0 && SkipAlwaysArg(typed, ref i))
            {
                // A hand-built argv without the canonical block: its always-args are not settings.
            }
            else
            {
                extra.Add(arg);
            }
        }

        extra.AddRange(trailing);
        int np = parallel ?? saved.Parallel;
        int contextSize = contextTotal is { } total && np > 0 ? total / np : saved.ContextSize;
        int gpuCount = devices ?? template.Placement?.GpuUuids?.Count ?? 0;

        var settings = saved with
        {
            ModelFile = modelFile,
            ProjectorFile = projector,
            DraftModelFile = draft,
            ContextSize = contextSize,
            Parallel = np,
            GpuLayers = gpuLayers ?? saved.GpuLayers,
            FlashAttention = flashAttention,
            CacheTypeK = cacheK,
            CacheTypeV = cacheV,
            Delivery = LaunchDelivery.Process,
            ExtraArgs = extra,
            Embedding = embedding,
            Pooling = embedding ? pooling : saved.Pooling,
            ChatTemplate = chatTemplate,
            Reasoning = reasoning,
            Threads = threads,
        };

        settings = WithBatches(settings, saved, embedding, batch, micro);
        // No --split-mode means no GPU selection (the setting is not an argument then); the derived default and an
        // explicit value that equals it build the same argv, so the saved form is kept.
        settings = settings with
        {
            SplitMode = splitMode is null || saved.EffectiveSplitMode(gpuCount) == splitMode ? saved.SplitMode : splitMode,
        };
        settings = settings with { GpuBudgetsGiB = Budgets(template, saved, settings, tensorSplit, gpuCount, node) };
        return template with { ServedName = servedName, Runtime = RuntimeKind.LlamaCpp, LlamaCpp = settings };
    }

    /// <summary>
    /// The index where <see cref="LlamaCppFlags.AlwaysArgs"/> starts in <paramref name="argv"/> (the <c>{port}</c> token or a
    /// substituted port number both match); -1 when absent.
    /// </summary>
    public static int IndexOfAlwaysArgs(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var always = LlamaCppFlags.AlwaysArgs;

        // The earliest block is the builder's: ExtraArgs cannot repeat it (--host, --port and --fit are refused there).
        for (int start = 0; start + always.Count <= argv.Count; start++)
        {
            bool match = true;
            for (int k = 0; k < always.Count && match; k++)
            {
                string expected = always[k];
                string actual = argv[start + k];
                match = string.Equals(expected, actual, StringComparison.Ordinal) ||
                    (expected == ProcessArgvTokens.Port && int.TryParse(actual, NumberStyles.None, CultureInfo.InvariantCulture, out _));
            }

            if (match)
            {
                return start;
            }
        }

        return -1;
    }

    private static LlamaCppSettings WithBatches(LlamaCppSettings parsed, LlamaCppSettings saved, bool embedding, int? batch, int? micro)
    {
        if (!embedding)
        {
            return parsed with { BatchSize = batch, MicroBatchSize = micro };
        }

        // -b = -ub = EmbeddingMicroBatch(); BatchSize is not an argument of an embedding model.
        var keep = parsed with { BatchSize = saved.BatchSize, MicroBatchSize = saved.MicroBatchSize };
        int? launched = micro ?? batch;
        if (launched is null || keep.EmbeddingMicroBatch() == launched)
        {
            return launched is null ? keep with { MicroBatchSize = null } : keep;
        }

        var unset = keep with { MicroBatchSize = null };
        return unset.EmbeddingMicroBatch() == launched ? unset : keep with { MicroBatchSize = launched };
    }

    private static IReadOnlyList<double>? Budgets(
        ModelDefinition template, LlamaCppSettings saved, LlamaCppSettings parsed, string? tensorSplit, int gpuCount, NodeRecord? node)
    {
        if (tensorSplit is null)
        {
            // No split was emitted: budgets only matter for admission; keep a saved list that still fits the GPU count.
            return saved.GpuBudgetsGiB is { } list && list.Count == gpuCount && gpuCount <= 1 ? list : null;
        }

        var shares = tensorSplit.Split(',')
            .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN)
            .ToList();
        if (shares.Count < 2 || shares.Any(s => !double.IsFinite(s) || s <= 0))
        {
            return saved.GpuBudgetsGiB;
        }

        string launched = LlamaCppArgsBuilder.FormatTensorSplit(shares);
        if (saved.GpuBudgetsGiB is { Count: >= 2 } budgets && budgets.All(b => b > 0) &&
            LlamaCppArgsBuilder.FormatTensorSplit(LlamaCppArgsBuilder.TensorSplit(budgets)) == launched)
        {
            return budgets;
        }

        if (saved.GpuBudgetsGiB is null && node is not null && template.Placement?.GpuUuids is { Count: >= 2 })
        {
            try
            {
                var probe = template with { LlamaCpp = parsed with { GpuBudgetsGiB = null } };
                if (LaunchSpecBuilder.TensorSplitWeights(probe, node) is { } weights &&
                    LlamaCppArgsBuilder.FormatTensorSplit(LlamaCppArgsBuilder.TensorSplit(weights)) == launched)
                {
                    return null;
                }
            }
            catch (ArgumentException)
            {
                // The node no longer reports the GPUs: read the shares as budgets below.
            }
        }

        double sum = shares.Sum();
        return [.. shares.Select(s => Math.Round(s / sum * template.MemoryBudgetGiB, 3, MidpointRounding.AwayFromZero))];
    }

    private static ModelPlacement? LaunchedPlacement(ModelPlacement? saved, LaunchConfig launch, IReadOnlyList<string> uuids)
    {
        if (uuids.Count == 0)
        {
            return saved;
        }

        string? nodeId = saved?.NodeId ?? launch.Labels.GetValueOrDefault(ContainerLabels.Node);
        if (nodeId is null)
        {
            return saved;
        }

        IReadOnlyList<int>? indices = launch.Gpus.Count == uuids.Count && launch.Gpus.All(g => g.Index >= 0)
            ? [.. launch.Gpus.Select(g => g.Index)]
            : saved?.GpuUuids is { } savedUuids && savedUuids.SequenceEqual(uuids, StringComparer.Ordinal) ? saved.GpuIndices : null;
        return saved is null ? new ModelPlacement(nodeId, uuids, indices) : saved with { GpuUuids = uuids, GpuIndices = indices };
    }

    private static bool SkipAlwaysArg(List<string> typed, ref int i)
    {
        switch (typed[i])
        {
            case "--metrics" or "--no-ui":
                return true;
            case "--host" or "--port" or "--cache-ram" or "--fit" when i + 1 < typed.Count:
                i++;
                return true;
            default:
                return false;
        }
    }

    private static string StripModels(string value)
    {
        string prefix = ProcessArgvTokens.Models + "/";
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
    }

    private static string ChatTemplateFile(string value, string modelPath)
    {
        string relative = StripModels(value);
        string folder = modelPath + "/";
        return relative.StartsWith(folder, StringComparison.Ordinal) ? relative[folder.Length..] : relative[(relative.LastIndexOf('/') + 1)..];
    }

    private static int? Int(string? value) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;

    private static bool OnOffAuto(string? value, out bool? parsed)
    {
        parsed = value switch
        {
            "on" => true,
            "off" => false,
            _ => null,
        };
        return value is "on" or "off" or "auto";
    }
}
