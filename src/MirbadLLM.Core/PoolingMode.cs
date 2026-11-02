namespace MirbadLLM.Core;

/// <summary>What kind of model vLLM runs: a generation (chat) model, or a pooling model that returns vectors or scores.</summary>
public enum PoolingMode
{
    /// <summary>A generation model (chat/completions).</summary>
    None,

    /// <summary>An embedding model (<c>/v1/embeddings</c>).</summary>
    Embedding,

    /// <summary>A reranker / cross-encoder (<c>/v1/score</c>, <c>/v1/rerank</c>).</summary>
    Reranker,
}

/// <summary>Detects pooling models from their launch arguments and tags. Pure function, no I/O.</summary>
public static class PoolingDetection
{
    /// <summary>
    /// Reranker: <c>--convert classify</c>, <c>--task score|classify</c> or the <c>reranker</c> tag. Embedding:
    /// <c>--runner pooling</c>, <c>--convert embed</c>, <c>--task embed|embedding</c> or the <c>embedding</c> tag.
    /// Both <c>--flag value</c>, <c>--flag=value</c> and one-line <c>--flag value</c> entries count.
    /// </summary>
    public static PoolingMode Detect(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);

        // llama.cpp (MN-202, design-p3p4 §A.1.7): embedding mode is a typed setting, not an argument. vLLM models never
        // reach this branch, so their detection is unchanged.
        if (model.Runtime == RuntimeKind.LlamaCpp && model.LlamaCpp is { Embedding: true })
        {
            return PoolingMode.Embedding;
        }

        var args = model.ExtraArgs ?? [];
        var tags = model.Tags ?? [];

        if (ModelTags.HasArg(args, "--convert", "classify") || ModelTags.HasArg(args, "--task", "score") ||
            ModelTags.HasArg(args, "--task", "classify") || tags.Contains(ModelTags.Reranker, StringComparer.Ordinal))
        {
            return PoolingMode.Reranker;
        }

        return ModelTags.HasArg(args, "--runner", "pooling") || ModelTags.HasArg(args, "--convert", "embed") ||
            ModelTags.HasArg(args, "--task", "embed") || ModelTags.HasArg(args, "--task", "embedding") ||
            tags.Contains(ModelTags.Embedding, StringComparer.Ordinal)
            ? PoolingMode.Embedding
            : PoolingMode.None;
    }
}
