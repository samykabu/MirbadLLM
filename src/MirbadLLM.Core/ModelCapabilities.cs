namespace MirbadLLM.Core;

/// <summary>
/// What a definition says a model can do, for the places that describe models to clients (<c>/v1/models</c>, the
/// VS Code config export). Pure functions over the definition and its tags, no I/O.
/// </summary>
public static class ModelCapabilities
{
    /// <summary>A generation model: not a pooling (embedding or reranker) model and not tagged <c>planner</c>.</summary>
    public static bool IsChat(ModelDefinition model) =>
        PoolingDetection.Detect(model) == PoolingMode.None && !HasTag(model, ModelTags.Planner);

    /// <summary>vLLM: a tool-call parser is set; llama.cpp and MLX: the <c>tool-calling</c> tag.</summary>
    public static bool ToolCalling(ModelDefinition model) =>
        model.Runtime == RuntimeKind.Vllm ? !string.IsNullOrWhiteSpace(model.ToolCallParser) : HasTag(model, ModelTags.ToolCalling);

    /// <summary>The <c>vision</c> or <c>multimodal</c> tag.</summary>
    public static bool Vision(ModelDefinition model) => HasTag(model, ModelTags.Vision) || HasTag(model, ModelTags.Multimodal);

    /// <summary>Context length in tokens per request: llama.cpp's context per slot, MLX's context size, else vLLM's max model len.</summary>
    public static int MaxModelLen(ModelDefinition model) =>
        model.Runtime == RuntimeKind.LlamaCpp && model.LlamaCpp is { } llama ? llama.ContextSize
        : model.Runtime == RuntimeKind.Mlx && model.Mlx is { } mlx ? mlx.ContextSize
        : model.MaxModelLen;

    private static bool HasTag(ModelDefinition model, string tag) => (model.Tags ?? []).Contains(tag, StringComparer.Ordinal);
}
