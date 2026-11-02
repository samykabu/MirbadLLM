using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Gguf;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Agent;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// MN-209: the §A.1.10 GGUF memory estimate against the design's worked examples and spike S1's calibration table
/// (design-p3p4 §A.1.10; MN-207, D118): Qwen3-Embedding-4B Q8_0, <c>ContextSize</c> 8192, <c>Parallel</c> 2, f16 KV →
/// 8.9 GiB at the default <c>ub</c> 2048 and 13.5 GiB at <c>ub</c> 8192, each within 5 %, with the formula's term values
/// of the calibration table (weights 4 075.7 MiB of tensor data, KV 2 304.0 MiB exact, compute 1 445.9 / 5 783.4 MiB).
/// </summary>
public sealed class LlamaCppMemoryEstimateTests
{
    private const double MiB = 1024d * 1024;

    private static readonly LlamaCppSettings OpenVikingEmbedding = new()
    {
        ModelFile = "Qwen3-Embedding-4B-GGUF/Qwen3-Embedding-4B-Q8_0.gguf",
        Embedding = true,
        Pooling = LlamaCppPooling.Last,
        ContextSize = 8192,
        Parallel = 2,
        CacheTypeK = "f16",
        CacheTypeV = "f16",
    };

    private static void Within(double expected, double actual, double fraction = 0.05) =>
        Assert.InRange(actual, expected * (1 - fraction), expected * (1 + fraction));

    [Fact]
    public void Qwen3Embedding_DefaultMicroBatch_Is8Point9GiB()
    {
        var estimate = LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding);

        Assert.Equal(2048, LlamaCppMemoryEstimate.MicroBatch(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding));
        Within(8.9, estimate.BudgetGiB);
        Assert.Equal(8.86, estimate.BudgetGiB); // 1.10 × 7.642 GiB + 0.45, rounded up
        Assert.Equal(Math.Round(4075.7 / 1024, 2), estimate.WeightsGiB); // S1: CUDA0 model buffer 4075.7 MiB
        Assert.Equal(Math.Round(2304.0 / 1024, 2), estimate.KvGiB);     // S1: KV buffer 2304.0 MiB, exact
        Assert.Equal(Math.Round(1445.9 / 1024, 2), estimate.ComputeGiB); // the formula's column of the table
        Assert.Equal(0.45, estimate.CudaContextGiB);
        Assert.Equal(0, estimate.SsmStateGiB);
        Assert.Equal(0, estimate.HostGiB);
        Assert.Empty(estimate.PerGpuGiB);
    }

    [Fact]
    public void Qwen3Embedding_ModelCardMicroBatch_Is13Point5GiB()
    {
        var estimate = LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding with { MicroBatchSize = 8192 });

        Within(13.5, estimate.BudgetGiB);
        Assert.Equal(Math.Round(5783.4 / 1024, 2), estimate.ComputeGiB);
        Assert.Equal(2.25, estimate.KvGiB);
    }

    [Fact]
    public void Qwen3Embedding_FromTheRealHeader_Is8Point9GiB()
    {
        var summary = GgufReader.Read(GgufReaderTests.FixturePath("qwen3-embedding-4b-q8_0.header.bin"), "f", "x.gguf", TestContext.Current.CancellationToken);
        Within(8.9, LlamaCppMemoryEstimate.Estimate(summary, OpenVikingEmbedding).BudgetGiB);
    }

    [Fact]
    public void ParallelOne_HalvesTheKvTerm()
    {
        var estimate = LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding with { Parallel = 1 });
        Assert.Equal(1.13, estimate.KvGiB); // 1.125
        Within(7.6, estimate.BudgetGiB);
    }

    [Fact]
    public void NonCausalEmbedding_UsesTheWholeContextAsMicroBatch()
    {
        var bert = GgufReaderTests.Qwen3Embedding with { CausalAttention = false };
        Assert.Equal(8192, LlamaCppMemoryEstimate.MicroBatch(bert, OpenVikingEmbedding));
        Assert.Equal(Math.Round(5783.4 / 1024, 2), LlamaCppMemoryEstimate.Estimate(bert, OpenVikingEmbedding).ComputeGiB);
    }

    [Fact]
    public void ChatModel_UsesMicroBatchSizeOrTheDefault512()
    {
        var chat = OpenVikingEmbedding with { Embedding = false, Pooling = null };
        Assert.Equal(512, LlamaCppMemoryEstimate.MicroBatch(GgufReaderTests.Qwen3Embedding, chat));
        Assert.Equal(1024, LlamaCppMemoryEstimate.MicroBatch(GgufReaderTests.Qwen3Embedding, chat with { MicroBatchSize = 1024 }));
        double compute = 512 * (2560d + 151_665) * 4 * 1.2 / (1024 * MiB);
        Assert.Equal(Math.Round(compute, 2), LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, chat).ComputeGiB);
    }

    [Fact]
    public void Qwen35Hybrid_CountsOnlyAttentionLayersForKv_AndAddsTheRecurrentState()
    {
        var planner = new LlamaCppSettings
        {
            ModelFile = "ov-intent-analysis/model.gguf",
            ContextSize = 4096,
            Parallel = 4,
            ChatTemplate = LlamaCppChatTemplate.ChatMl,
            Reasoning = false,
        };

        var estimate = LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen35Planner, planner);

        // kv: 6 attention layers (24 / 4) × 16 384 × 2 KV heads × (256 + 256) × 2 B = 0.1875 GiB (design: ≈ 0.19).
        Assert.Equal(0.19, estimate.KvGiB);
        // ssm: 18 recurrent layers × 4 slots × ((4 − 1) × (2048 + 2 × 16 × 128) × 4 B + 128 × 2048 × 4 B) = 77.1 MiB.
        double ssm = 18 * 4 * ((3 * (2048 + (2 * 16 * 128)) * 4) + (128 * 2048 * 4d));
        Assert.Equal(Math.Round(ssm / (1024 * MiB), 2), estimate.SsmStateGiB);
        Assert.Equal(0.75, estimate.WeightsGiB); // 800 881 920 B of tensor data (design: 0.76 from the file size)
        // compute: 512 × (1024 + 248 320) × 4 B × 1.2 = 0.57 GiB.
        Assert.Equal(0.57, estimate.ComputeGiB);
        Assert.Equal(2.19, estimate.BudgetGiB);
    }

    [Fact]
    public void ComputeTerm_HasA256MiBFloor()
    {
        var tiny = GgufReaderTests.Qwen3Embedding with { EmbeddingLength = 64, VocabSize = 8, HeadCount = 4, HeadCountKv = 2, KeyLength = 16, ValueLength = 16 };
        Assert.Equal(0.25, LlamaCppMemoryEstimate.Estimate(tiny, OpenVikingEmbedding).ComputeGiB);
    }

    [Fact]
    public void PartialOffload_MovesWeightsToTheHost()
    {
        var estimate = LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding with { GpuLayers = 18 });
        Assert.Equal(1.99, estimate.WeightsGiB);
        Assert.Equal(1.99, estimate.HostGiB);
        var none = LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding with { GpuLayers = 0 });
        Assert.Equal(0, none.WeightsGiB);
        Assert.Equal(3.98, none.HostGiB);
    }

    [Fact]
    public void QuantisedKvCache_ShrinksTheKvTerm()
    {
        var estimate = LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding with { CacheTypeK = "q8_0", CacheTypeV = "q8_0" });
        Assert.Equal(Math.Round(2.25 * 34 / 64, 2), estimate.KvGiB);
    }

    [Fact]
    public void MissingHeadSizes_FallBackToEmbeddingOverHeads()
    {
        var noLengths = GgufReaderTests.Qwen3Embedding with { KeyLength = null, ValueLength = null, HeadCountKv = null };
        // headCountKv = 32 heads, key = value = 2560 / 32 = 80: 36 × 16 384 × 32 × 160 × 2 B = 5.625 GiB.
        Assert.Equal(5.63, LlamaCppMemoryEstimate.Estimate(noLengths, OpenVikingEmbedding).KvGiB);
    }

    [Fact]
    public void TwoGpus_AddAContextEach_AndSplitTheBudgetByTheGpuBudgets()
    {
        var even = LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding, gpuCount: 2);
        Assert.Equal(0.9, even.CudaContextGiB);
        Assert.Equal(9.31, even.BudgetGiB);
        Assert.Equal([4.66, 4.66], even.PerGpuGiB);

        var weighted = LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding with { GpuBudgetsGiB = [4, 6] }, gpuCount: 2);
        Assert.Equal([Math.Round(9.31 * 0.4, 2), Math.Round(9.31 * 0.6, 2)], weighted.PerGpuGiB);
    }

    [Fact]
    public void UnusableInput_IsAnArgumentException()
    {
        Assert.Throws<ArgumentException>(() => LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding with { CacheTypeK = "q3_k" }));
        Assert.Throws<ArgumentException>(() => LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding with { EmbeddingLength = null }, OpenVikingEmbedding));
        Assert.Throws<ArgumentException>(() => LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding with { VocabSize = null }, OpenVikingEmbedding));
        Assert.Throws<ArgumentException>(() => LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding with { Parallel = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding, gpuCount: 0));
    }

    [Fact]
    public void Estimate_MatchesTheFrozenFixtureShape()
    {
        // The P3 fit-result fixture (MN-202) carries the design's rounded terms; the computed estimate agrees with them.
        var estimate = LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, OpenVikingEmbedding);
        Assert.Equal(2.25, estimate.KvGiB);
        Assert.Equal(1.41, estimate.ComputeGiB);
        Assert.InRange(estimate.WeightsGiB, 3.98, 3.99);
        Assert.Equal(8.9, Math.Round(estimate.BudgetGiB, 1));
    }
}
