using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime;

/// <summary>One folder under the models root that has a <c>config.json</c> (a checkpoint the "add model" UI can offer).</summary>
public sealed record ModelFolderSummary(string Folder, double WeightsGiB, string? Architecture, string DetectedFormat);

/// <summary>
/// Reads <c>config.json</c> files under <see cref="ModelStorageOptions.ModelsLocalPath"/> to list checkpoints
/// already on the box and suggest <see cref="ModelDefinition"/> values for them. Pure filesystem/JSON reads —
/// no Docker calls — so it stays cheap to call from the editor page on every folder pick.
/// </summary>
public sealed class ModelFolderInspector(IOptions<ModelStorageOptions> storageOptions)
{
    /// <summary>Cap used by the memory-budget formula: KV cache is sized for at most this many tokens even when the model supports a longer context.</summary>
    private const int KvBudgetTokenCap = 131_072;

    /// <summary>
    /// Memory vLLM uses beyond the weights and KV cache; see <see cref="ModelFolderInspection.RuntimeOverheadGiB"/>
    /// (the record moved to <c>MirbadLLM.Core</c> so the agent protocol can carry it, design §4.3).
    /// </summary>
    public const double RuntimeOverheadGiB = ModelFolderInspection.RuntimeOverheadGiB;

    /// <summary>Weights-on-disk proxy for "≥ 7B params at BF16" (7e9 params * 2 bytes ≈ 13.04 GiB).</summary>
    private const double SevenBillionBf16GiB = 13.0;

    private readonly string _root = storageOptions.Value.ModelsLocalPath;

    public IReadOnlyList<ModelFolderSummary> ListFolders()
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var result = new List<ModelFolderSummary>();
        foreach (string dir in Directory.EnumerateDirectories(_root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string folder = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, "config.json")))
            {
                continue;
            }

            try
            {
                var inspection = Inspect(folder);
                if (inspection is not null)
                {
                    result.Add(new ModelFolderSummary(folder, inspection.WeightsGiB, inspection.Architecture, inspection.DetectedFormat));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A folder with a malformed or unreadable config.json is skipped rather than failing the whole list.
            }
        }

        return result;
    }

    public ModelFolderInspection? Inspect(string folder)
    {
        string dir = Path.Combine(_root, folder);
        string configPath = Path.Combine(dir, "config.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var root = document.RootElement;
        var effective = root.TryGetProperty("text_config", out var textConfig) && textConfig.ValueKind == JsonValueKind.Object
            ? textConfig
            : root;

        string? architecture = TryGetArchitecture(root) ?? TryGetString(effective, "model_type");
        int? maxPositionEmbeddings = TryGetInt(effective, "max_position_embeddings") ?? TryGetInt(root, "max_position_embeddings");

        var (quantLabel, isPreQuantized) = DetectQuantization(root);
        string detectedFormat = quantLabel ?? DetectDtypeLabel(root);

        double weightsGiB = SumSafetensorsGiB(dir);
        bool maySupportMtp = (TryGetInt(effective, "mtp_num_hidden_layers") ?? 0) > 0;

        var suggestion = BuildSuggestion(folder, architecture, maxPositionEmbeddings, isPreQuantized, weightsGiB, effective);

        // A vision tower (Qwen-VL, Qwen3.5, Gemma 3, …) shows up as a vision_config block next to text_config.
        bool hasVisionConfig = root.TryGetProperty("vision_config", out var visionConfig) && visionConfig.ValueKind == JsonValueKind.Object;

        return new ModelFolderInspection(folder, architecture, maxPositionEmbeddings, detectedFormat, weightsGiB, maySupportMtp, suggestion,
            EstimateKvBytesPerToken(effective), hasVisionConfig, IsMoe(effective), IsMambaHybrid(root, effective, architecture),
            SpeculativeBlockSize(root));
    }

    private static readonly string[] MoeKeys = ["num_experts", "num_local_experts", "n_routed_experts", "moe_num_experts"];

    /// <summary>Mixture of experts: any expert-count key above zero.</summary>
    private static bool IsMoe(JsonElement effective) => MoeKeys.Any(k => (TryGetInt(effective, k) ?? 0) > 0);

    private static readonly string[] MambaArchitectures = ["Mamba", "NemotronH", "Jamba", "Zamba", "FalconH", "Plamo2", "GraniteMoeHybrid"];

    /// <summary>
    /// A Mamba-hybrid model (state-space layers next to attention): a <c>hybrid_override_pattern</c> with mamba (M)
    /// layers, a <c>layers_block_type</c> listing mamba, mamba/ssm keys, or a known Mamba-family architecture.
    /// Linear-attention hybrids (Qwen3.5 <c>layer_types</c>) are not counted.
    /// </summary>
    private static bool IsMambaHybrid(JsonElement root, JsonElement effective, string? architecture)
    {
        foreach (var element in new[] { effective, root })
        {
            if (TryGetString(element, "hybrid_override_pattern") is { } pattern && pattern.Contains('M', StringComparison.Ordinal))
            {
                return true;
            }

            if (element.TryGetProperty("layers_block_type", out var types) && types.ValueKind == JsonValueKind.Array &&
                types.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString()!.Contains("mamba", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (element.ValueKind == JsonValueKind.Object &&
                element.EnumerateObject().Any(p => p.Name.StartsWith("mamba_", StringComparison.Ordinal) || p.Name == "ssm_state_size"))
            {
                return true;
            }
        }

        return architecture is not null && MambaArchitectures.Any(a => architecture.Contains(a, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] BlockSizeKeys = ["speculative_block_size", "spec_block_size", "draft_block_size", "dflash_block_size", "block_size"];

    /// <summary>A draft checkpoint's trained block size (how many tokens it drafts per step), when its config declares one.</summary>
    private static int? SpeculativeBlockSize(JsonElement root)
    {
        foreach (string key in BlockSizeKeys)
        {
            if (TryGetInt(root, key) is > 0 and var size)
            {
                return size;
            }
        }

        return root.TryGetProperty("speculative_config", out var nested) && nested.ValueKind == JsonValueKind.Object
            ? BlockSizeKeys.Select(k => TryGetInt(nested, k)).FirstOrDefault(v => v is > 0)
            : null;
    }

    private static ModelFolderSuggestion BuildSuggestion(
        string folder, string? architecture, int? maxPositionEmbeddings, bool isPreQuantized, double weightsGiB, JsonElement effective)
    {
        bool looksLikeQwen3 = architecture is not null && architecture.Contains("qwen3", StringComparison.OrdinalIgnoreCase);
        string? toolCallParser = looksLikeQwen3 ? "qwen3_coder" : null;
        string? reasoningParser = looksLikeQwen3 ? "qwen3" : null;

        string? quantization = !isPreQuantized && weightsGiB >= SevenBillionBf16GiB ? "fp8" : null;

        int kvTokens = Math.Min(maxPositionEmbeddings ?? KvBudgetTokenCap, KvBudgetTokenCap);
        double kvBytesPerToken = EstimateKvBytesPerToken(effective);
        double kvGiB = kvBytesPerToken * kvTokens / 1_073_741_824.0;
        double rawBudget = weightsGiB + RuntimeOverheadGiB + kvGiB;
        double budget = Math.Ceiling(rawBudget);

        string formula = string.Create(CultureInfo.InvariantCulture, $"{weightsGiB:F1} GiB weights + {RuntimeOverheadGiB:F1} GiB runtime overhead + " +
            $"{kvGiB:F1} GiB KV cache ({kvTokens:N0} tokens @ fp8, {kvBytesPerToken:F0} bytes/token) = {rawBudget:F1} GiB, rounded up to {budget:F0} GiB.");

        return new ModelFolderSuggestion(folder, toolCallParser, reasoningParser, quantization, budget, formula);
    }

    /// <summary>KV bytes/token at fp8 (1 byte/element) = 2 (K and V) x full-attention layers x kv heads x head dim.</summary>
    private static double EstimateKvBytesPerToken(JsonElement effective)
    {
        int numLayers = TryGetInt(effective, "num_hidden_layers") ?? 0;
        int fullAttentionLayers = numLayers;
        if (effective.TryGetProperty("layer_types", out var layerTypes) && layerTypes.ValueKind == JsonValueKind.Array)
        {
            fullAttentionLayers = layerTypes.EnumerateArray().Count(e => e.ValueKind == JsonValueKind.String && e.GetString() == "full_attention");
        }

        int kvHeads = TryGetInt(effective, "num_key_value_heads") ?? TryGetInt(effective, "num_attention_heads") ?? 0;

        int headDim = TryGetInt(effective, "head_dim") ?? 0;
        if (headDim <= 0)
        {
            int hiddenSize = TryGetInt(effective, "hidden_size") ?? 0;
            int attentionHeads = TryGetInt(effective, "num_attention_heads") ?? 0;
            headDim = attentionHeads > 0 ? hiddenSize / attentionHeads : 0;
        }

        return 2.0 * fullAttentionLayers * kvHeads * headDim;
    }

    private static (string? Label, bool IsPreQuantized) DetectQuantization(JsonElement root)
    {
        // An MLX checkpoint (mlx-lm's convert, e.g. mlx-community/*; macOS Phase 3) has a top-level "quantization" block
        // with its bits and group size; its "quantization_config" copy says nothing about the method.
        if (root.TryGetProperty("quantization", out var mlx) && mlx.ValueKind == JsonValueKind.Object && TryGetInt(mlx, "bits") is > 0 and var bits)
        {
            return (string.Create(CultureInfo.InvariantCulture, $"MLX {bits}-bit"), true);
        }

        if (!root.TryGetProperty("quantization_config", out var qc) || qc.ValueKind != JsonValueKind.Object)
        {
            return (null, false);
        }

        string raw = qc.GetRawText();
        string method = TryGetString(qc, "quant_method") ?? "";
        string family = raw.Contains("nvfp4", StringComparison.OrdinalIgnoreCase) || raw.Contains("fp4", StringComparison.OrdinalIgnoreCase)
            ? "NVFP4"
            : raw.Contains("fp8", StringComparison.OrdinalIgnoreCase) || raw.Contains("float-quantized", StringComparison.OrdinalIgnoreCase)
                ? "FP8"
                : "quantized";

        string label = string.IsNullOrEmpty(method)
            ? $"pre-quantized ({family})"
            : $"pre-quantized ({method}, {family})";
        return (label, true);
    }

    private static string DetectDtypeLabel(JsonElement root)
    {
        string dtype = TryGetString(root, "torch_dtype") ?? TryGetString(root, "dtype") ?? "float16";
        return dtype.Contains("bfloat16", StringComparison.OrdinalIgnoreCase) ? "BF16" : "FP16";
    }

    private static double SumSafetensorsGiB(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        long totalBytes = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*.safetensors", SearchOption.AllDirectories))
        {
            try
            {
                totalBytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
                // Skip a file that vanished/changed mid-scan.
            }
        }

        return totalBytes / 1_073_741_824.0;
    }

    private static string? TryGetArchitecture(JsonElement root) =>
        root.TryGetProperty("architectures", out var architectures) &&
        architectures.ValueKind == JsonValueKind.Array &&
        architectures.GetArrayLength() > 0
            ? architectures[0].GetString()
            : null;

    private static string? TryGetString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? TryGetInt(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int result)
            ? result
            : null;
}
