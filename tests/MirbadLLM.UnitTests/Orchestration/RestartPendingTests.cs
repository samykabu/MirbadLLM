using System.Text.Json;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore the token.

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// Launch-config hash and reading a container's settings back. Restart-pending detection and restart admission over the
/// agents are in <c>RemoteModelRuntimeTests</c> (the same scenarios, ported from the pre-P1 runtime; MN-047, D60).
/// </summary>
public sealed class RestartPendingTests
{
    private const double TotalGiB = 120;

    private static readonly ModelDefinition Saved = new()
    {
        Id = "m1",
        ServedName = "M1",
        ModelPath = "ckpt",
        MemoryBudgetGiB = 40,
        Speculative = new SpeculativeDecoding("mtp", 2),
        CudaGraphMode = "PIECEWISE",
    };

    // ------------------------------------------------------------ hash

    [Fact]
    public void Hash_IsStable_AndIgnoresAutoStart()
    {
        string a = ModelConfigHash.Compute(Saved, "img", TotalGiB);
        string b = ModelConfigHash.Compute(Saved with { AutoStart = true }, "img", TotalGiB);
        Assert.Equal(a, b);
        Assert.Equal(16, a.Length);
        Assert.Equal(a, ModelConfigHash.Compute(Saved, "img", TotalGiB));
    }

    [Fact]
    public void Hash_DoesNotDependOnHowTheDefinitionWasBuilt()
    {
        // Same settings, properties assigned in a different order and round-tripped through JSON.
        var other = new ModelDefinition
        {
            CudaGraphMode = "PIECEWISE",
            Speculative = new SpeculativeDecoding("mtp", 2),
            MemoryBudgetGiB = 40,
            ModelPath = "ckpt",
            ServedName = "M1",
            Id = "m1",
        };
        var roundTripped = JsonSerializer.Deserialize<ModelDefinition>(JsonSerializer.Serialize(Saved))!;

        Assert.Equal(ModelConfigHash.Compute(Saved, "img", TotalGiB), ModelConfigHash.Compute(other, "img", TotalGiB));
        Assert.Equal(ModelConfigHash.Compute(Saved, "img", TotalGiB), ModelConfigHash.Compute(roundTripped, "img", TotalGiB));
    }

    [Theory]
    [InlineData("seqs")]
    [InlineData("served")]
    [InlineData("budget")]
    [InlineData("image")]
    [InlineData("extra")]
    [InlineData("batched")]
    public void Hash_ChangesWithEveryLaunchSetting(string change)
    {
        var changed = change switch
        {
            "seqs" => Saved with { MaxNumSeqs = 32 },
            "served" => Saved with { ServedName = "Other" },
            "budget" => Saved with { MemoryBudgetGiB = 41 },
            "image" => Saved with { Image = "other-image" },
            "extra" => Saved with { ExtraArgs = ["--enforce-eager"] },
            _ => Saved with { MaxNumBatchedTokens = 8192 },
        };

        Assert.NotEqual(ModelConfigHash.Compute(Saved, "img", TotalGiB), ModelConfigHash.Compute(changed, "img", TotalGiB));
    }

    [Fact]
    public void Hash_DefaultImageAndExplicitDefaultImage_AreTheSameLaunch()
    {
        Assert.Equal(
            ModelConfigHash.Compute(Saved, "img", TotalGiB),
            ModelConfigHash.Compute(Saved with { Image = "img" }, "img", TotalGiB));
    }

    [Fact]
    public void BatchedTokens_BecomesAVllmArgument()
    {
        var args = VllmArgsBuilder.Build(Saved with { MaxNumBatchedTokens = 8192 }, TotalGiB);
        int i = args.ToList().IndexOf("--max-num-batched-tokens");
        Assert.True(i > 0);
        Assert.Equal("8192", args[i + 1]);
        Assert.DoesNotContain("--max-num-batched-tokens", VllmArgsBuilder.Build(Saved, TotalGiB));
    }

    // ------------------------------------------------------------ parser

    [Fact]
    public void Parser_ReadsBackEveryLaunchSetting()
    {
        var full = Saved with
        {
            Quantization = "fp8",
            KvCacheDtype = "auto",
            MaxModelLen = 131_072,
            MaxNumSeqs = 8,
            MaxNumBatchedTokens = 2048,
            ToolCallParser = "hermes",
            ReasoningParser = null,
            EnablePrefixCaching = false,
            Image = "custom:1",
            ExtraArgs = ["--enforce-eager", "--seed", "7"],
        };

        var parsed = VllmArgsParser.Parse(VllmArgsBuilder.Build(full, TotalGiB), "custom:1", "default:1", TotalGiB, Saved with { AutoStart = true });

        Assert.Equal(full with { AutoStart = true }, parsed with { ExtraArgs = full.ExtraArgs });
        Assert.Equal(full.ExtraArgs, parsed.ExtraArgs);
    }

    [Fact]
    public void Parser_ContainerWithoutSpeculativeOrGraphs_ReadsAsOff()
    {
        var plain = Saved with { Speculative = null, CudaGraphMode = null };
        var parsed = VllmArgsParser.Parse(VllmArgsBuilder.Build(plain, TotalGiB), "default:1", "default:1", TotalGiB, Saved);

        Assert.Null(parsed.Speculative);
        Assert.Null(parsed.CudaGraphMode);
        Assert.Null(parsed.Image);
        Assert.Equal(40, parsed.MemoryBudgetGiB);
    }
}
