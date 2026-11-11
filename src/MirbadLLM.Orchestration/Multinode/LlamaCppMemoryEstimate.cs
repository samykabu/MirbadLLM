using System.Globalization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// The GGUF admission estimate of a llama.cpp model (design-p3p4 §A.1.10; MN-209), shown by the editor and the fit preview
/// (MN-224, MN-226) and proposed as the model's budget. The formula is kept exactly as the design writes it (spike S1
/// found it 17–18 % above the measurement, the intended safe side; MN-207, D118):
/// <code>
/// weights     = tensorBytes (all split parts) × min(gpuLayers, blockCount) / blockCount
/// kv          = attnLayers × ContextSize × Parallel × headCountKv × (keyLength × bytes(ctk) + valueLength × bytes(ctv))
///               attnLayers = blockCount, or blockCount / fullAttentionInterval for hybrid models
/// ssmState    = hybrid only: (blockCount − attnLayers) × Parallel × (convState + ssmState)   (f32, llama.cpp's r/s cache)
///               convState = (convKernel − 1) × (ssmInner + 2 × ssmGroups × ssmStateSize) × 4 B
///               ssmState  = ssmStateSize × ssmInner × 4 B
/// compute     = max(256 MiB, ub × (embeddingLength + vocab) × 4 B × 1.2)
///               ub = LlamaCppSettings.EmbeddingMicroBatch(summary.CausalAttention) for embedding models
///                    (the -b = -ub the builder emits), MicroBatchSize ?? 512 for chat models
/// cudaContext = 0.45 GiB × GPUs
/// budget      = 1.10 × (weights + kv + ssmState + compute) + cudaContext
/// host        = tensorBytes − weights (layers left on the CPU)
/// </code>
/// Missing head sizes fall back as in llama.cpp (<c>headCountKv = headCount</c>, <c>keyLength = valueLength =
/// embeddingLength / headCount</c>); a null cache type is f16 (llama.cpp's default). Terms are rounded to 0.01 GiB, the
/// budget is rounded <b>up</b> to 0.01 GiB from the unrounded sum, and on two or more GPUs <see cref="GgufMemoryEstimate.PerGpuGiB"/>
/// splits the budget by the tensor-split weights (<see cref="LlamaCppSettings.GpuBudgetsGiB"/> when it has one entry per
/// GPU, else even).
/// </summary>
public static class LlamaCppMemoryEstimate
{
    /// <summary>Bytes in a GiB.</summary>
    public const double GiB = 1024d * 1024 * 1024;

    /// <summary>Safety factor over the buffers.</summary>
    public const double Overhead = 1.10;

    /// <summary>Factor on the compute-buffer term (S1 measured 1.076; kept, D118).</summary>
    public const double ComputeFactor = 1.2;

    /// <summary>Smallest compute-buffer term.</summary>
    public const double MinComputeBytes = 256d * 1024 * 1024;

    /// <summary>CUDA context per GPU (S1 measured ≈ 0.1 GiB under WDDM; kept as the safe side, D118).</summary>
    public const double CudaContextGiBPerGpu = 0.45;

    /// <summary>llama.cpp's default micro-batch of a chat model (<c>-ub</c>).</summary>
    public const int DefaultChatMicroBatch = 512;

    /// <summary>
    /// The micro-batch the estimate's compute term uses: what the args builder emits for an embedding model
    /// (<see cref="LlamaCppSettings.EmbeddingMicroBatch"/> with the GGUF's <c>attention.causal</c>), else
    /// <see cref="LlamaCppSettings.MicroBatchSize"/> or <see cref="DefaultChatMicroBatch"/>.
    /// </summary>
    public static int MicroBatch(GgufSummary gguf, LlamaCppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(gguf);
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Embedding ? settings.EmbeddingMicroBatch(gguf.CausalAttention) : settings.MicroBatchSize ?? DefaultChatMicroBatch;
    }

    /// <summary>Estimates the memory of <paramref name="settings"/> over the GGUF <paramref name="gguf"/> on <paramref name="gpuCount"/> GPUs.</summary>
    /// <param name="gguf">The GGUF header summary (from the agent's <c>gguf.inspect</c> route).</param>
    /// <param name="settings">The model's llama.cpp settings.</param>
    /// <param name="gpuCount">Placed GPUs (at least 1).</param>
    /// <param name="cpuOnly">
    /// True on a CPU-only node (<see cref="NodeAccelerators.Cpu"/>): no layer is offloaded whatever <c>-ngl</c> says and
    /// there is no CUDA context, so every weight counts in the budget, which such a node admits against host memory, and
    /// <see cref="GgufMemoryEstimate.HostGiB"/> is 0.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The summary lacks the sizes the KV or compute term needs (embedding length, head count, vocabulary), a cache type
    /// is unknown, or a setting is out of range (context, parallel, micro-batch below 1).
    /// </exception>
    public static GgufMemoryEstimate Estimate(GgufSummary gguf, LlamaCppSettings settings, int gpuCount = 1, bool cpuOnly = false)
    {
        ArgumentNullException.ThrowIfNull(gguf);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(gpuCount, 1);
        if (settings.ContextSize < 1 || settings.Parallel < 1)
        {
            throw new ArgumentException("ContextSize and Parallel must be at least 1.", nameof(settings));
        }

        if (gguf.BlockCount < 1)
        {
            throw new ArgumentException("The GGUF summary has no layers.", nameof(gguf));
        }

        int blocks = gguf.BlockCount;
        int embedding = gguf.EmbeddingLength ?? throw Missing("embedding length");
        int vocab = gguf.VocabSize ?? throw Missing("vocabulary size");
        int heads = gguf.HeadCount ?? throw Missing("attention head count");
        if (heads < 1)
        {
            throw Missing("attention head count");
        }

        int headsKv = gguf.HeadCountKv ?? heads;
        int keyLength = gguf.KeyLength ?? embedding / heads;
        int valueLength = gguf.ValueLength ?? embedding / heads;

        double offloaded = cpuOnly ? 1 : Math.Clamp(settings.GpuLayers, 0, blocks) / (double)blocks;
        double weightsBytes = gguf.TensorBytes * offloaded;
        double hostBytes = gguf.TensorBytes - weightsBytes;

        bool hybrid = gguf.FullAttentionInterval is > 0;
        int attnLayers = hybrid ? blocks / gguf.FullAttentionInterval!.Value : blocks;
        double ctxTotal = (double)settings.ContextSize * settings.Parallel;
        double kvBytes = attnLayers * ctxTotal * headsKv
            * (keyLength * CacheTypeBytes(settings.CacheTypeK) + valueLength * CacheTypeBytes(settings.CacheTypeV));

        double ssmBytes = 0;
        if (hybrid && gguf.SsmStateSize is { } state && gguf.SsmInnerSize is { } inner)
        {
            int kernel = gguf.SsmConvKernel ?? 1;
            int groups = gguf.SsmGroupCount ?? 0;
            double conv = Math.Max(kernel - 1, 0) * ((double)inner + 2d * groups * state) * 4;
            double recurrent = (double)state * inner * 4;
            ssmBytes = (blocks - attnLayers) * (double)settings.Parallel * (conv + recurrent);
        }

        int ub = MicroBatch(gguf, settings);
        if (ub < 1)
        {
            throw new ArgumentException("The micro-batch must be at least 1.", nameof(settings));
        }

        double computeBytes = Math.Max(MinComputeBytes, ub * ((double)embedding + vocab) * 4 * ComputeFactor);
        double cuda = cpuOnly ? 0 : CudaContextGiBPerGpu * gpuCount;
        double budget = (Overhead * (weightsBytes + kvBytes + ssmBytes + computeBytes) / GiB) + cuda;
        double roundedBudget = Math.Ceiling(Math.Round(budget * 100, 6)) / 100;

        return new GgufMemoryEstimate
        {
            WeightsGiB = Round(weightsBytes / GiB),
            KvGiB = Round(kvBytes / GiB),
            SsmStateGiB = Round(ssmBytes / GiB),
            ComputeGiB = Round(computeBytes / GiB),
            CudaContextGiB = Round(cuda),
            BudgetGiB = roundedBudget,
            HostGiB = Round(hostBytes / GiB),
            PerGpuGiB = gpuCount > 1 ? Split(roundedBudget, gpuCount, settings.GpuBudgetsGiB) : [],
        };
    }

    /// <summary>Bytes per element of a KV cache type (<c>-ctk</c>/<c>-ctv</c>); null is f16.</summary>
    /// <exception cref="ArgumentException">The type is not one llama-server accepts for the KV cache.</exception>
    public static double CacheTypeBytes(string? cacheType) => cacheType?.ToLowerInvariant() switch
    {
        null or "f16" or "bf16" => 2,
        "f32" => 4,
        "q8_0" => 34d / 32,
        "q4_0" => 18d / 32,
        "q4_1" => 20d / 32,
        "iq4_nl" => 18d / 32,
        "q5_0" => 22d / 32,
        "q5_1" => 24d / 32,
        _ => throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"Unknown KV cache type '{cacheType}'."), nameof(cacheType)),
    };

    private static double[] Split(double budget, int gpuCount, IReadOnlyList<double>? weights)
    {
        double[] w = weights is { } given && given.Count == gpuCount && given.All(x => x > 0)
            ? [.. given]
            : [.. Enumerable.Repeat(1d, gpuCount)];
        double total = w.Sum();
        return [.. w.Select(x => Round(budget * x / total))];
    }

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static ArgumentException Missing(string what) =>
        new($"The GGUF summary has no {what}; the memory cannot be estimated.");
}
