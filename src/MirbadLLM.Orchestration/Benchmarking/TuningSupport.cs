using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// Which runtimes the tuner can really search: vLLM, llama.cpp, MLX and SGLang. NIM has no setting worth a search (only its
/// image's own variables), so it would restart the same server for every candidate and could name a winner that changes nothing.
/// </summary>
public static class TuningSupport
{
    /// <summary>Why <paramref name="model"/> can't be tuned; null when it can.</summary>
    public static string? RefusalFor(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Runtime == RuntimeKind.Vllm || LlamaCppTuning.Applies(model) || SmallRuntimeTuning.Applies(model))
        {
            return null;
        }

        string runtime = model.Runtime switch
        {
            RuntimeKind.LlamaCpp => "llama.cpp",
            RuntimeKind.Mlx => "MLX",
            RuntimeKind.Sglang => "SGLang",
            RuntimeKind.Nim => "NIM",
            _ => model.Runtime.ToString(),
        };
        return $"Tuning isn't available for {runtime}. It has no settings a search could change, so every candidate would run the same server. Benchmarks still work.";
    }
}
