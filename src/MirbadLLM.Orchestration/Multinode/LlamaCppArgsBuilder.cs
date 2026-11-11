using System.Globalization;
using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// Builds the <c>llama-server</c> argv of a llama.cpp <see cref="ModelDefinition"/> (design-p3p4 §A.1.5–§A.1.7, MN-210):
/// the only place on the master that knows <c>llama-server</c> flags. The order is fixed, so the argv is canonical and the
/// execution fingerprint (which covers the argv of every non-vLLM runtime) is stable:
/// <c>-m</c>, <c>--mmproj</c>, <c>--alias</c>, <c>-c</c>, <c>-np</c>, <c>-ngl</c>, <c>-fa</c>, <c>-ctk</c>, <c>-ctv</c>,
/// <c>-b</c>, <c>-ub</c>, <c>--embedding</c>, <c>--pooling</c>, <c>--split-mode</c>, <c>--tensor-split</c>,
/// <c>--device</c>, <c>--chat-template</c>/<c>--chat-template-file</c>, <c>--reasoning</c>, <c>-t</c>, then
/// <see cref="LlamaCppFlags.AlwaysArgs"/> (with the <c>{port}</c> token) and the ExtraArgs last.
/// <para>
/// Paths are <c>{models}/…</c> tokens (the agent substitutes and confines them, §A.1.3); GPUs are never physical indices:
/// the agent sets <c>CUDA_VISIBLE_DEVICES</c> to the placed UUIDs in order and CUDA renumbers them from 0, so the argv
/// names <c>CUDA0..n-1</c> (§A.1.5). On a Vulkan node (<see cref="NodeAccelerators.Vulkan"/>) the agent does the same with
/// <c>GGML_VK_VISIBLE_DEVICES</c> and the argv names <c>Vulkan0..n-1</c>. Pure: no I/O.
/// </para>
/// </summary>
public static partial class LlamaCppArgsBuilder
{
    /// <summary>Decimals of a <c>--tensor-split</c> share (§A.1.5).</summary>
    public const int TensorSplitDecimals = 3;

    /// <summary>The CUDA device name of the <paramref name="index"/>-th placed GPU inside the process.</summary>
    public static string CudaDevice(int index) => "CUDA" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The Vulkan device name of the <paramref name="index"/>-th placed GPU inside the process.</summary>
    public static string VulkanDevice(int index) => "Vulkan" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The <c>--device</c> value for <paramref name="gpuCount"/> placed GPUs: <c>CUDA0[,CUDA1…]</c>, or
    /// <c>Vulkan0[,Vulkan1…]</c> on a Vulkan node (<paramref name="vulkan"/>).
    /// </summary>
    public static string Devices(int gpuCount, bool vulkan = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(gpuCount, 1);
        return string.Join(',', Enumerable.Range(0, gpuCount).Select(i => vulkan ? VulkanDevice(i) : CudaDevice(i)));
    }

    /// <summary>
    /// The tensor split of <paramref name="weights"/> (per-GPU budgets or capacities, in placement order):
    /// <c>weight_i / Σ weight</c>, rounded to <see cref="TensorSplitDecimals"/> decimals (§A.1.5).
    /// </summary>
    /// <exception cref="ArgumentException">Fewer than two weights, or a weight that is not a positive finite number.</exception>
    public static IReadOnlyList<double> TensorSplit(IReadOnlyList<double> weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Count < 2)
        {
            throw new ArgumentException("A tensor split needs at least two GPUs.", nameof(weights));
        }

        if (weights.Any(w => !double.IsFinite(w) || w <= 0))
        {
            throw new ArgumentException("Every tensor-split weight must be a positive number.", nameof(weights));
        }

        double sum = weights.Sum();
        return [.. weights.Select(w => Math.Round(w / sum, TensorSplitDecimals, MidpointRounding.AwayFromZero))];
    }

    /// <summary>The <c>--tensor-split</c> text of <paramref name="shares"/>: comma-separated, invariant, at most 3 decimals.</summary>
    public static string FormatTensorSplit(IReadOnlyList<double> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        return string.Join(',', shares.Select(s => s.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The settings problems that make <paramref name="model"/> unlaunchable (placement problem
    /// <see cref="PlacementProblemCodes.LlamaCppSettingsInvalid"/>, design-p3p4 §A.1.6): missing settings, a model file,
    /// projector or folder that is not a plain relative path, non-positive sizes, an unknown pooling value, an unparsable
    /// chat template, a cache type that is not a plain type name, and ExtraArgs that <see cref="LlamaCppFlags.RefusedExtraArgs"/>
    /// refuses (owned or path-valued flags). Empty when the model can be built. The registry's save path (MN-224's
    /// <c>PlacementValidator</c>) answers these with 400; <see cref="Build"/> refuses them too.
    /// </summary>
    public static IReadOnlyList<PlacementProblem> Problems(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var problems = new List<PlacementProblem>();
        void Add(string message) => problems.Add(new PlacementProblem(PlacementProblemCodes.LlamaCppSettingsInvalid, message));

        if (model.LlamaCpp is not { } s)
        {
            problems.Add(new PlacementProblem(
                PlacementProblemCodes.LlamaCppSettingsMismatch, $"Model '{model.Id}' is a llama.cpp model without llama.cpp settings."));
            return problems;
        }

        if (!IsRelativePath(s.ModelFile))
        {
            Add($"The model file '{s.ModelFile}' must be a relative path inside the models folder ('folder/file.gguf', '/' separators, no '..').");
        }

        if (s.ProjectorFile is not null && !IsRelativePath(s.ProjectorFile))
        {
            Add($"The projector file '{s.ProjectorFile}' must be a relative path inside the models folder.");
        }

        if (s.ContextSize < 1)
        {
            Add($"The context size must be at least 1 token per slot (is {s.ContextSize}).");
        }

        if (s.Parallel < 1)
        {
            Add($"Parallel slots must be at least 1 (is {s.Parallel}).");
        }

        if (s.ContextSize >= 1 && s.Parallel >= 1 && (long)s.ContextSize * s.Parallel > int.MaxValue)
        {
            Add($"Context size × parallel slots ({s.ContextSize} × {s.Parallel}) is too large.");
        }

        if (s.GpuLayers < 0)
        {
            Add($"GPU layers must be 0 or more (is {s.GpuLayers}).");
        }

        if (s.BatchSize is < 1)
        {
            Add($"The batch size must be at least 1 (is {s.BatchSize}).");
        }

        if (s.MicroBatchSize is < 1)
        {
            Add($"The micro-batch size must be at least 1 (is {s.MicroBatchSize}).");
        }

        if (s.Threads is < 1)
        {
            Add($"Threads must be at least 1 (is {s.Threads}).");
        }

        foreach (var (name, value) in new[] { ("K", s.CacheTypeK), ("V", s.CacheTypeV) })
        {
            if (value is not null && !CacheTypeRegex().IsMatch(value))
            {
                Add($"The {name} cache type '{value}' is not a cache type name (e.g. f16, q8_0).");
            }
        }

        if (s.Pooling is not null && !LlamaCppPooling.IsValid(s.Pooling))
        {
            Add($"Pooling '{s.Pooling}' is not one of {string.Join(", ", LlamaCppPooling.All)}.");
        }

        if (s.ChatTemplate is not null)
        {
            if (!LlamaCppChatTemplate.TryParse(s.ChatTemplate, out var template))
            {
                Add($"The chat template '{s.ChatTemplate}' is neither 'builtin:{{name}}' nor a file name in the model folder.");
            }
            else if (template.File is not null && !IsRelativePath(model.ModelPath))
            {
                Add($"A chat template file needs the model folder; '{model.ModelPath}' is not a relative folder path.");
            }
        }

        var refused = LlamaCppFlags.RefusedExtraArgs(s.ExtraArgs);
        if (refused.Count > 0)
        {
            Add($"Extra arguments {string.Join(", ", refused)} are not allowed: mirbadllm sets them itself or they take a path.");
        }

        return problems;
    }

    /// <summary>
    /// The per-GPU budget problems of <paramref name="model"/> on <paramref name="gpuCount"/> placed GPUs (placement
    /// problem <see cref="PlacementProblemCodes.GpuBudgetsMismatch"/>, §A.1.5): when
    /// <see cref="LlamaCppSettings.GpuBudgetsGiB"/> is set, its count must equal the placed GPUs, every entry must be
    /// positive and the sum must equal <see cref="ModelDefinition.MemoryBudgetGiB"/> (± 0.01 GiB). Empty when unset.
    /// </summary>
    public static IReadOnlyList<PlacementProblem> GpuBudgetProblems(ModelDefinition model, int gpuCount)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.LlamaCpp?.GpuBudgetsGiB is not { } budgets)
        {
            return [];
        }

        var problems = new List<PlacementProblem>();
        void Add(string message) => problems.Add(new PlacementProblem(PlacementProblemCodes.GpuBudgetsMismatch, message));
        if (budgets.Count != gpuCount)
        {
            Add($"Model '{model.Id}' has {budgets.Count} per-GPU budget(s) but is placed on {gpuCount} GPU(s).");
        }

        if (budgets.Any(b => !double.IsFinite(b) || b <= 0))
        {
            Add($"Every per-GPU budget of model '{model.Id}' must be a positive number of GiB.");
        }
        else if (Math.Abs(budgets.Sum() - model.MemoryBudgetGiB) > 0.01)
        {
            Add($"The per-GPU budgets of model '{model.Id}' sum to {budgets.Sum().ToString("0.###", CultureInfo.InvariantCulture)} GiB, " +
                $"not its budget of {model.MemoryBudgetGiB.ToString("0.###", CultureInfo.InvariantCulture)} GiB.");
        }

        return problems;
    }

    /// <summary>
    /// The argv of <paramref name="model"/> on <paramref name="gpuCount"/> placed GPUs (0 = no GPU selection: no
    /// <c>--split-mode</c>/<c>--device</c>). <paramref name="splitWeights"/> are the per-GPU weights of the tensor split
    /// in placement order (the budgets, else each card's capacity minus its reserve; <see cref="LaunchSpecBuilder"/>
    /// chooses them); they are used only for <see cref="LlamaCppSplitMode.Layer"/> on two or more GPUs.
    /// <para>
    /// <c>-c</c> is <c>ContextSize × Parallel</c> (<c>ContextSize</c> is per slot); <c>-np</c> and <c>-fa</c>
    /// (<c>auto</c> when unset) are always explicit. An embedding model gets <c>-b = -ub = </c>
    /// <see cref="LlamaCppSettings.EmbeddingMicroBatch"/> with the causal default (the builder has no GGUF; llama-server
    /// forces <c>n_batch = n_ubatch</c> with embeddings on, so <see cref="LlamaCppSettings.BatchSize"/> is ignored); a chat
    /// model gets <c>-b</c>/<c>-ub</c> only when set (llama.cpp's defaults otherwise).
    /// </para>
    /// <paramref name="vulkan"/>: the model's node runs llama.cpp's Vulkan build, so <c>--device</c> names Vulkan devices.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <see cref="Problems"/> is not empty, split mode <see cref="LlamaCppSplitMode.None"/> on several GPUs, or a layer split
    /// on several GPUs without one weight per GPU.
    /// </exception>
    public static IReadOnlyList<string> Build(ModelDefinition model, int gpuCount, IReadOnlyList<double>? splitWeights = null, bool vulkan = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfNegative(gpuCount);
        var problems = Problems(model);
        if (problems.Count > 0)
        {
            throw new ArgumentException($"Model '{model.Id}' cannot be launched: {string.Join(" ", problems.Select(p => p.Message))}");
        }

        var s = model.LlamaCpp!;
        var args = new List<string> { "-m", Models(s.ModelFile) };
        if (s.ProjectorFile is not null)
        {
            args.AddRange(["--mmproj", Models(s.ProjectorFile)]);
        }

        args.AddRange(["--alias", model.ServedName]);
        args.AddRange(["-c", Int((long)s.ContextSize * s.Parallel)]);
        args.AddRange(["-np", Int(s.Parallel)]);
        args.AddRange(["-ngl", Int(s.GpuLayers)]);
        args.AddRange(["-fa", OnOffAuto(s.FlashAttention)]);
        if (s.CacheTypeK is not null)
        {
            args.AddRange(["-ctk", s.CacheTypeK]);
        }

        if (s.CacheTypeV is not null)
        {
            args.AddRange(["-ctv", s.CacheTypeV]);
        }

        if (s.Embedding)
        {
            string microBatch = Int(s.EmbeddingMicroBatch());
            args.AddRange(["-b", microBatch, "-ub", microBatch]);
        }
        else
        {
            if (s.BatchSize is { } batch)
            {
                args.AddRange(["-b", Int(batch)]);
            }

            if (s.MicroBatchSize is { } micro)
            {
                args.AddRange(["-ub", Int(micro)]);
            }
        }

        if (s.Embedding)
        {
            args.Add("--embedding");
            if (s.Pooling is not null)
            {
                args.AddRange(["--pooling", s.Pooling]);
            }
        }

        if (gpuCount > 0)
        {
            var mode = s.EffectiveSplitMode(gpuCount);
            if (mode == LlamaCppSplitMode.None && gpuCount > 1)
            {
                throw new ArgumentException(
                    $"Model '{model.Id}' is placed on {gpuCount} GPUs with split mode 'none', which would use only the first; " +
                    "choose split mode 'layer' or place it on one GPU.");
            }

            args.AddRange(["--split-mode", SplitModeName(mode)]);
            if (mode == LlamaCppSplitMode.Layer && gpuCount > 1)
            {
                if (splitWeights is null || splitWeights.Count != gpuCount)
                {
                    throw new ArgumentException(
                        $"Model '{model.Id}' is split over {gpuCount} GPUs but {splitWeights?.Count ?? 0} split weight(s) were given.",
                        nameof(splitWeights));
                }

                args.AddRange(["--tensor-split", FormatTensorSplit(TensorSplit(splitWeights))]);
            }

            args.AddRange(["--device", Devices(gpuCount, vulkan)]);
        }

        if (s.ChatTemplate is not null && LlamaCppChatTemplate.TryParse(s.ChatTemplate, out var template))
        {
            if (template.BuiltinName is { } builtin)
            {
                args.AddRange(["--chat-template", builtin]);
            }
            else
            {
                args.AddRange(["--chat-template-file", Models(model.ModelPath + "/" + template.File)]);
            }
        }

        if (s.Reasoning is { } reasoning)
        {
            args.AddRange(["--reasoning", reasoning ? "on" : "off"]);
        }

        if (s.Threads is { } threads)
        {
            args.AddRange(["-t", Int(threads)]);
        }

        args.AddRange(LlamaCppFlags.AlwaysArgs);
        args.AddRange(s.ExtraArgs);
        return args;
    }

    /// <summary><c>{models}/</c> + a relative path (the agent substitutes and confines it).</summary>
    public static string Models(string relativePath) => ProcessArgvTokens.Models + "/" + relativePath;

    /// <summary><c>none</c> / <c>layer</c>.</summary>
    public static string SplitModeName(LlamaCppSplitMode mode) => mode switch
    {
        LlamaCppSplitMode.None => "none",
        LlamaCppSplitMode.Layer => "layer",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown split mode."),
    };

    /// <summary>
    /// True for a plain relative path with <c>/</c> separators: not empty, not rooted, no drive or stream colon, no
    /// backslash, no empty, <c>.</c> or <c>..</c> segment, no control characters. The agent still confines it after
    /// resolving links (§A.1.3); this keeps obviously escaping values out of the definition.
    /// </summary>
    internal static bool IsRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal) ||
            path.Any(char.IsControl))
        {
            return false;
        }

        return path.Split('/').All(segment => segment.Length > 0 && segment is not "." and not ".." && segment.Trim().Length == segment.Length);
    }

    private static string OnOffAuto(bool? value) => value switch
    {
        true => "on",
        false => "off",
        null => "auto",
    };

    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^[a-z0-9_]{1,16}$", RegexOptions.CultureInvariant)]
    private static partial Regex CacheTypeRegex();
}
