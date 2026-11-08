using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Benchmarking;

/// <summary>
/// Which runtimes the tuner can really search. It knows vLLM's and llama.cpp's settings; any other runtime would fall
/// through to the vLLM search, restart the same server for every candidate and could name a winner that changes nothing.
/// </summary>
public static class TuningSupport
{
    /// <summary>Why <paramref name="model"/> can't be tuned; null when it can.</summary>
    public static string? RefusalFor(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Runtime == RuntimeKind.Vllm || LlamaCppTuning.Applies(model))
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
        return $"Tuning isn't available for {runtime} yet. The search only changes vLLM and llama.cpp settings, so every candidate would run the same server. Benchmarks still work.";
    }
}
