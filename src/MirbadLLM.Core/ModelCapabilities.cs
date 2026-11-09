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

    /// <summary>vLLM: a tool-call parser is set; SGLang: that flag in its extra arguments or the tag; the others: the <c>tool-calling</c> tag.</summary>
    public static bool ToolCalling(ModelDefinition model) =>
        model.Runtime == RuntimeKind.Vllm
            ? !string.IsNullOrWhiteSpace(model.ToolCallParser)
            : HasTag(model, ModelTags.ToolCalling) || (model.Runtime == RuntimeKind.Sglang && ContainerFlag(model, "--tool-call-parser") is not null);

    /// <summary>The <c>vision</c> or <c>multimodal</c> tag.</summary>
    public static bool Vision(ModelDefinition model) => HasTag(model, ModelTags.Vision) || HasTag(model, ModelTags.Multimodal);

    /// <summary>
    /// Context length in tokens per request: llama.cpp's context per slot, MLX's context size, SGLang's
    /// <c>--context-length</c> when its extra arguments set it, else the stored max model len.
    /// shortcut: without that flag SGLang reads the checkpoint's own limit, which only an inspected folder's stored value reflects.
    /// </summary>
    public static int MaxModelLen(ModelDefinition model) =>
        model.Runtime == RuntimeKind.LlamaCpp && model.LlamaCpp is { } llama ? llama.ContextSize
        : model.Runtime == RuntimeKind.Mlx && model.Mlx is { } mlx ? mlx.ContextSize
        : model.Runtime == RuntimeKind.Sglang && int.TryParse(ContainerFlag(model, "--context-length"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int context) && context > 0 ? context
        : model.MaxModelLen;

    /// <summary>The value of <paramref name="flag"/> in the container's extra arguments (<c>--flag value</c> or <c>--flag=value</c>); null when it isn't set.</summary>
    public static string? ContainerFlag(ModelDefinition model, string flag)
    {
        var args = model.Container?.ExtraArgs ?? [];
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == flag)
            {
                return i + 1 < args.Count ? args[i + 1] : "";
            }

            if (args[i].StartsWith(flag + "=", StringComparison.Ordinal))
            {
                return args[i][(flag.Length + 1)..];
            }
        }

        return null;
    }

    private static bool HasTag(ModelDefinition model, string tag) => (model.Tags ?? []).Contains(tag, StringComparer.Ordinal);
}
