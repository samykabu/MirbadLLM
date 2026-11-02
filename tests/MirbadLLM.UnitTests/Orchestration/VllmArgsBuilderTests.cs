using System.Globalization;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;

// xUnit convention: MethodUnderTest_Scenario_ExpectedBehavior test names read better with underscores.
#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Orchestration;

public sealed class VllmArgsBuilderTests
{
    // Phase 0 golden command line: uncensored FP8 (docs/phase0-results.md).
    // 36 GiB budget / 120 GiB total = 0.30 utilization (asserted at architecture.md's 4-decimal precision).
    [Fact]
    public void Build_UncensoredFp8_MatchesPhase0FlagsInCanonicalOrder()
    {
        var model = new ModelDefinition
        {
            Id = "ornith-9b-fp8",
            ServedName = "Ornith-Uncensored-FP8",
            ModelPath = "Ornith-1.5-9B-uncensored",
            Quantization = "fp8",
            KvCacheDtype = "fp8",
            MaxModelLen = 262_144,
            MaxNumSeqs = 16,
            MemoryBudgetGiB = 36,
            ToolCallParser = "qwen3_coder",
            ReasoningParser = "qwen3",
            EnablePrefixCaching = true,
        };

        var args = VllmArgsBuilder.Build(model, totalGiB: 120);

        string[] expected =
        [
            "--model", "/models/Ornith-1.5-9B-uncensored",
            "--served-model-name", "Ornith-Uncensored-FP8",
            "--quantization", "fp8",
            "--kv-cache-dtype", "fp8",
            "--max-model-len", "262144",
            "--max-num-seqs", "16",
            "--gpu-memory-utilization", "0.3000",
            "--enable-auto-tool-choice",
            "--tool-call-parser", "qwen3_coder",
            "--reasoning-parser", "qwen3",
            "--enable-prefix-caching",
            "--disable-access-log-for-endpoints", "/health,/metrics",
        ];

        Assert.Equal(expected, args);
    }

    // Phase 0 golden command line: official NVFP4 + MTP, PIECEWISE graphs.
    [Fact]
    public void Build_Nvfp4WithMtp_MatchesPhase0FlagsInCanonicalOrder()
    {
        var model = new ModelDefinition
        {
            Id = "ornith-nvfp4-mtp",
            ServedName = "ornith-nvfp4-mtp",
            ModelPath = "Ornith-1.5-9B-NVFP4",
            Quantization = null,
            KvCacheDtype = "fp8",
            MaxModelLen = 262_144,
            MaxNumSeqs = 16,
            MemoryBudgetGiB = 36,
            ToolCallParser = "qwen3_coder",
            ReasoningParser = "qwen3",
            EnablePrefixCaching = true,
            Speculative = new SpeculativeDecoding("mtp", 2),
            CudaGraphMode = "PIECEWISE",
        };

        var args = VllmArgsBuilder.Build(model, totalGiB: 120);

        string[] expected =
        [
            "--model", "/models/Ornith-1.5-9B-NVFP4",
            "--served-model-name", "ornith-nvfp4-mtp",
            "--kv-cache-dtype", "fp8",
            "--max-model-len", "262144",
            "--max-num-seqs", "16",
            "--gpu-memory-utilization", "0.3000",
            "--enable-auto-tool-choice",
            "--tool-call-parser", "qwen3_coder",
            "--reasoning-parser", "qwen3",
            "--enable-prefix-caching",
            "--speculative-config", """{"method":"mtp","num_speculative_tokens":2}""",
            "--compilation-config", """{"cudagraph_mode":"PIECEWISE"}""",
            "--disable-access-log-for-endpoints", "/health,/metrics",
        ];

        Assert.Equal(expected, args);

        // Golden-test note (docs/phase0-results.md): the builder's canonical order differs from the raw
        // Phase 0 lines; what must match is the *set* of flags+values, which this equality check covers.
        var expectedFlags = new HashSet<string>(expected);
        var actualFlags = new HashSet<string>(args);
        Assert.Equal(expectedFlags, actualFlags);
    }

    [Fact]
    public void Build_NoParsers_OmitsToolChoiceAndReasoningFlags()
    {
        var model = new ModelDefinition
        {
            Id = "bare",
            ServedName = "bare",
            ModelPath = "Bare-Model",
            ToolCallParser = null,
            ReasoningParser = null,
            EnablePrefixCaching = false,
        };

        var args = VllmArgsBuilder.Build(model, totalGiB: 100);

        Assert.DoesNotContain("--enable-auto-tool-choice", args);
        Assert.DoesNotContain("--tool-call-parser", args);
        Assert.DoesNotContain("--reasoning-parser", args);
        Assert.DoesNotContain("--enable-prefix-caching", args);
        Assert.DoesNotContain("--quantization", args);
        Assert.DoesNotContain("--speculative-config", args);
        Assert.DoesNotContain("--compilation-config", args);
    }

    [Fact]
    public void Build_ExtraArgs_AreAppendedLast()
    {
        var model = new ModelDefinition
        {
            Id = "extra",
            ServedName = "extra",
            ModelPath = "Extra-Model",
            ExtraArgs = ["--foo", "bar", "--baz"],
        };

        var args = VllmArgsBuilder.Build(model, totalGiB: 100);

        Assert.Equal(["--foo", "bar", "--baz"], args.TakeLast(3));
    }

    [Fact]
    public void Build_UnderDeCultureCurrentCulture_StillUsesInvariantDecimalPoint()
    {
        var commaDecimalCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        commaDecimalCulture.NumberFormat.NumberDecimalSeparator = ",";

        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = commaDecimalCulture;
        try
        {
            var model = new ModelDefinition { Id = "culture", ServedName = "culture", ModelPath = "M", MemoryBudgetGiB = 30 };
            var args = VllmArgsBuilder.Build(model, totalGiB: 100);

            int index = args.ToList().IndexOf("--gpu-memory-utilization");
            string utilization = args[index + 1];

            Assert.DoesNotContain(',', utilization);
            Assert.Equal("0.3000", utilization);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
