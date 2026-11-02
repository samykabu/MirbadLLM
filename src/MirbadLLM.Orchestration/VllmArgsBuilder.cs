using System.Globalization;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>Builds the <c>vllm serve</c> command-line arguments for a model, in the canonical order from docs/architecture.md.</summary>
public static class VllmArgsBuilder
{
    public const string AccessLogFlag = "--disable-access-log-for-endpoints";
    public const string AccessLogValue = "/health,/metrics";

    /// <summary>
    /// Pure function: no I/O, no state. <paramref name="totalGiB"/> is the host's total unified memory. The definition
    /// is normalized first (<see cref="VllmArgsNormalizer"/>), so every owned setting appears exactly once.
    /// </summary>
    public static IReadOnlyList<string> Build(ModelDefinition model, double totalGiB)
    {
        ArgumentNullException.ThrowIfNull(model);
        double utilization = totalGiB > 0 ? model.MemoryBudgetGiB / totalGiB : 0;
        return BuildCore(VllmArgsNormalizer.Apply(model), Utilization(utilization));
    }

    /// <summary>
    /// The arguments of a placement on <paramref name="gpuCount"/> discrete GPUs of <paramref name="gpuTotalGiB"/> each
    /// (design §5.1): <see cref="ModelDefinition.MemoryBudgetGiB"/> is the total across the GPUs, so
    /// <c>--gpu-memory-utilization</c> = (budget ÷ n) ÷ gpuTotalGiB, formatted exactly as <see cref="Build(ModelDefinition, double)"/>
    /// formats it; with more than one GPU, <c>--tensor-parallel-size n</c> follows it. One GPU emits nothing new.
    /// </summary>
    public static IReadOnlyList<string> Build(ModelDefinition model, double gpuTotalGiB, int gpuCount)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfLessThan(gpuCount, 1);
        double utilization = gpuTotalGiB > 0 ? model.MemoryBudgetGiB / gpuCount / gpuTotalGiB : 0;
        return BuildCore(
            VllmArgsNormalizer.Apply(model),
            Utilization(utilization),
            gpuCount > 1 ? gpuCount.ToString(CultureInfo.InvariantCulture) : null);
    }

    /// <summary>Today's <c>--gpu-memory-utilization</c> text: four decimals, invariant culture.</summary>
    internal static string Utilization(double fraction) => fraction.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>
    /// The argument list of an already-normalized definition with a given <c>--gpu-memory-utilization</c> text (null =
    /// omitted) and <c>--tensor-parallel-size</c> text (null = omitted, every unified and single-GPU launch).
    /// </summary>
    internal static List<string> BuildCore(ModelDefinition model, string? utilization, string? tensorParallel = null)
    {
        var args = new List<string>
        {
            "--model",
            $"/models/{model.ModelPath}",
            "--served-model-name",
            model.ServedName,
        };

        if (!string.IsNullOrEmpty(model.Quantization))
        {
            args.Add("--quantization");
            args.Add(model.Quantization);
        }

        args.Add("--kv-cache-dtype");
        args.Add(model.KvCacheDtype);
        args.Add("--max-model-len");
        args.Add(model.MaxModelLen.ToString(CultureInfo.InvariantCulture));
        args.Add("--max-num-seqs");
        args.Add(model.MaxNumSeqs.ToString(CultureInfo.InvariantCulture));

        if (model.MaxNumBatchedTokens is { } batched)
        {
            args.Add("--max-num-batched-tokens");
            args.Add(batched.ToString(CultureInfo.InvariantCulture));
        }

        if (utilization is not null)
        {
            args.Add("--gpu-memory-utilization");
            args.Add(utilization);
        }

        if (tensorParallel is not null)
        {
            args.Add(VllmArgsNormalizer.TensorParallelFlag);
            args.Add(tensorParallel);
        }

        if (!string.IsNullOrEmpty(model.ToolCallParser))
        {
            args.Add("--enable-auto-tool-choice");
            args.Add("--tool-call-parser");
            args.Add(model.ToolCallParser);
        }

        if (!string.IsNullOrEmpty(model.ReasoningParser))
        {
            args.Add("--reasoning-parser");
            args.Add(model.ReasoningParser);
        }

        switch (model.EnablePrefixCaching)
        {
            case true:
                args.Add("--enable-prefix-caching");
                break;
            case false:
                args.Add("--no-enable-prefix-caching");
                break;
        }

        if (model.Speculative is { } speculative)
        {
            args.Add("--speculative-config");
            args.Add(speculative.ToJson());
        }

        var compilation = VllmArgsNormalizer.CompilationFields(model);
        if (!compilation.IsEmpty)
        {
            args.Add("--compilation-config");
            args.Add(compilation.ToJson());
        }

        switch (model.AsyncScheduling)
        {
            case true:
                args.Add("--async-scheduling");
                break;
            case false:
                args.Add("--no-async-scheduling");
                break;
        }

        if (!string.IsNullOrEmpty(model.MambaCacheMode))
        {
            args.Add("--mamba-cache-mode");
            args.Add(model.MambaCacheMode);
        }

        // The controller polls these every few seconds; keep them out of the logs shown in the UI.
        args.Add(AccessLogFlag);
        args.Add(AccessLogValue);

        args.AddRange(model.ExtraArgs ?? []);
        return args;
    }
}
