namespace MirbadLLM.Core;

/// <summary>Suggested <see cref="ModelDefinition"/> field values for a folder, with the reasoning spelled out for the UI's info panel.</summary>
public sealed record ModelFolderSuggestion(
    string ServedName,
    string? ToolCallParser,
    string? ReasoningParser,
    string? Quantization,
    double MemoryBudgetGiB,
    string MemoryBudgetFormula);

/// <summary>
/// Full inspection of one checkpoint folder, driving the "add model" dropdowns and auto-fill. Moved here from
/// <c>MirbadLLM.Orchestration</c> unchanged so the agent can return it from <c>GET /agent/v1/model-folders/{folder}</c>
/// (design §4.3 "today's record, moved to Core").
/// </summary>
public sealed record ModelFolderInspection(
    string Folder,
    string? Architecture,
    int? MaxPositionEmbeddings,
    string DetectedFormat,
    double WeightsGiB,
    bool MaySupportMtp,
    ModelFolderSuggestion Suggestion,
    double KvBytesPerToken = 0,
    bool HasVisionConfig = false,
    bool IsMoe = false,
    bool IsMambaHybrid = false,
    int? SpeculativeBlockSize = null)
{
    /// <summary>
    /// Memory vLLM uses beyond the weights and KV cache (CUDA context, activations, graphs, vision encoder, linear-attention
    /// state). Measured on the DGX Spark: a 16 GiB budget for 9.3 GiB of weights left only 0.12 GiB for KV cache.
    /// </summary>
    public const double RuntimeOverheadGiB = 7.0;

    /// <summary>
    /// Parameters in billions estimated from the weights on disk and their format (NVFP4 ≈ 0.56 bytes/param with
    /// scales, FP8 1, otherwise 2).
    /// </summary>
    public double ParamsBillions
    {
        get
        {
            double bytesPerParam = DetectedFormat.Contains("FP4", StringComparison.OrdinalIgnoreCase) ? 0.5625
                : DetectedFormat.Contains("FP8", StringComparison.OrdinalIgnoreCase) ? 1.0
                : 2.0;
            return WeightsGiB * 1_073_741_824.0 / bytesPerParam / 1e9;
        }
    }

    /// <summary>
    /// Smallest budget vLLM accepts for this context length: weights + runtime overhead + KV cache for one request of
    /// <paramref name="maxModelLen"/> tokens (vLLM refuses to start when a single full-length request does not fit).
    /// </summary>
    public double MinimumBudgetGiB(int maxModelLen) =>
        WeightsGiB + RuntimeOverheadGiB + KvGiB(maxModelLen);

    public double KvGiB(int tokens) => KvBytesPerToken * tokens / 1_073_741_824.0;
}
