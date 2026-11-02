using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// One selectable value with a short human label, for a dropdown that needs more than a bare token
/// (e.g. "PIECEWISE"). <see cref="Hint"/> is the fuller explanation, shown as a field hint under the select
/// for whichever option is selected, so the option text itself stays short enough not to clip.
/// </summary>
public sealed record CatalogOption(string Value, string Label, string? Hint = null);

/// <summary>Everything the "add model" dropdowns need: fixed vLLM option lists plus the vLLM images found on the node.</summary>
public sealed record VllmCatalog(
    IReadOnlyList<string> ToolCallParsers,
    IReadOnlyList<string> ReasoningParsers,
    IReadOnlyList<CatalogOption> Quantizations,
    IReadOnlyList<CatalogOption> KvCacheDtypes,
    IReadOnlyList<CatalogOption> CudaGraphModes,
    IReadOnlyList<CatalogOption> SpeculativeMethods,
    IReadOnlyList<int> MaxModelLenPresets,
    IReadOnlyList<int> MaxNumSeqsPresets,
    IReadOnlyList<string> Images);

/// <summary>
/// Fixed option lists sourced from the vLLM build actually running on the DGX Spark
/// (<c>local/qwen38-vllm-dflash2:66e5414c6d75</c>, verified live per <c>docs/phase0-results.md</c>). The images come
/// from the node's capability report (<see cref="Multinode.RemoteVllmCatalogService"/>).
/// </summary>
public static class VllmOptionsCatalog
{
    public static readonly IReadOnlyList<string> ToolCallParsers =
    [
        "apertus", "cohere_command3", "cohere_command4", "deepseek_v3", "deepseek_v31", "deepseek_v32", "deepseek_v4",
        "dots", "ernie45", "functiongemma", "gemma4", "gigachat3", "glm45", "glm47", "granite", "granite-20b-fc",
        "granite4", "hermes", "hunyuan_a13b", "hy_v3", "inkling", "internlm", "jamba", "kimi_k2", "kimi_k3", "lfm2",
        "ling3", "llama3_json", "llama4_json", "llama4_pythonic", "longcat", "mimo", "minicpm5", "minimax_m2",
        "minimax_m3", "mistral", "muse_glimmer", "olmo3", "openai", "phi4_mini_json", "poolside_v1", "pythonic",
        "qwen3_coder", "qwen3_xml", "seed_oss", "step3", "step3p5", "xlam",
    ];

    public static readonly IReadOnlyList<string> ReasoningParsers =
    [
        "cohere_command3", "cohere_command4", "deepseek_r1", "deepseek_v3", "deepseek_v4", "ernie45", "gemma4",
        "glm45", "glm47", "granite", "holo2", "hunyuan_a13b", "hy_v3", "inkling", "kimi_k2", "kimi_k3", "ling3",
        "mimo", "minimax_m2", "minimax_m2_append_think", "minimax_m3", "mistral", "nemotron_v3", "olmo3",
        "openai_gptoss", "poolside_v1", "qwen3", "seed_oss", "step3", "step3p5",
    ];

    public static readonly IReadOnlyList<CatalogOption> Quantizations =
    [
        new("none", "None", "Checkpoint already quantized, or run at full precision."),
        new("fp8", "FP8 (online)", "Online quantization at load time."),
    ];

    public static readonly IReadOnlyList<CatalogOption> KvCacheDtypes =
    [
        new("auto", "Auto", "Same dtype as the model weights."),
        new("fp8", "FP8", "Halves KV cache memory vs. auto."),
    ];

    public static readonly IReadOnlyList<CatalogOption> CudaGraphModes =
    [
        new("", "vLLM default"),
        new("PIECEWISE", "PIECEWISE", "Required for MTP on GB10 — FULL crashes."),
        new("FULL", "FULL"),
        new("FULL_AND_PIECEWISE", "FULL_AND_PIECEWISE"),
        new("NONE", "NONE", "Disables CUDA graphs."),
    ];

    public static readonly IReadOnlyList<CatalogOption> SpeculativeMethods =
    [
        new("none", "None"),
        new("mtp", "MTP", "Multi-token prediction; needs a checkpoint with MTP weights."),
        new("ngram", "n-gram", "Prompt lookup; no extra weights needed."),
    ];

    public static readonly IReadOnlyList<int> MaxModelLenPresets = [8192, 16384, 32768, 65536, 131072, 262144];

    public static readonly IReadOnlyList<int> MaxNumSeqsPresets = [1, 4, 8, 16, 32, 64];

    /// <summary><see cref="MaxModelLenPresets"/> filtered to what the checkpoint actually supports.</summary>
    public static IReadOnlyList<int> MaxModelLenPresetsFor(int? maxPositionEmbeddings) =>
        maxPositionEmbeddings is { } cap
            ? MaxModelLenPresets.Where(p => p <= cap).ToList()
            : MaxModelLenPresets;
}
