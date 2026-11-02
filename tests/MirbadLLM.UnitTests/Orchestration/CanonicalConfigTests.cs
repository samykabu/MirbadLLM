using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore the token.

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// Canonical effective configuration: the ExtraArgs normalizer (every spelling of every owned flag, lossless JSON
/// merge, vLLM's last-wins semantics), the builder emitting each owned setting once, the speculative/compilation JSON
/// shapes, the registry migration, and config-hash stability for the live definitions. Restart pending after the migration
/// (live containers) is in <c>RemoteModelRuntimeTests</c> (ported from the pre-P1 runtime; MN-047, D60).
/// </summary>
public sealed class CanonicalConfigTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-canonical-" + Guid.NewGuid().ToString("N"));

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static readonly ModelDefinition Base = new() { Id = "m", ServedName = "M", ModelPath = "ckpt", MemoryBudgetGiB = 40 };

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private static NormalizedDefinition Norm(params string[] extra) => VllmArgsNormalizer.Normalize(Base with { ExtraArgs = extra });

    /// <summary>Value comparison including the lists (record equality compares lists by reference).</summary>
    private static void AssertSame(ModelDefinition expected, ModelDefinition actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private static int Count(IReadOnlyList<string> args, string flag) => args.Count(a => a == flag);

    // ------------------------------------------------------------ normalizer: forms

    [Theory]
    [InlineData("--max-num-seqs", "32")]
    [InlineData("--max_num_seqs", "32")]
    public void ScalarFlag_SeparateValue_IsLifted(string flag, string value)
    {
        var n = Norm("--enforce-eager", flag, value, "--seed", "7");
        Assert.Equal(32, n.Definition.MaxNumSeqs);
        Assert.Equal(["--enforce-eager", "--seed", "7"], n.Definition.ExtraArgs);
        Assert.Equal(flag, Assert.Single(n.Lifted).Flag);
        Assert.Empty(n.Problems);
    }

    [Fact]
    public void ScalarFlags_EqualsForm_AndOneLineEntry_AreLifted()
    {
        var n = Norm("--max-num-batched-tokens=16384", "--kv-cache-dtype auto", "--mamba_cache_mode=none");
        Assert.Equal(16384, n.Definition.MaxNumBatchedTokens);
        Assert.Equal("auto", n.Definition.KvCacheDtype);
        Assert.Equal("none", n.Definition.MambaCacheMode);
        Assert.Empty(n.Definition.ExtraArgs);
    }

    [Theory]
    [InlineData("--enable-prefix-caching", true)]
    [InlineData("--no-enable-prefix-caching", false)]
    [InlineData("--enable_prefix_caching", true)]
    [InlineData("--enable-prefix-caching=false", false)]
    [InlineData("--no-enable-prefix-caching=false", true)]
    public void PrefixCaching_BooleanForms(string token, bool expected)
    {
        var n = Norm(token);
        Assert.Equal(expected, n.Definition.EnablePrefixCaching);
        Assert.Empty(n.Definition.ExtraArgs);
    }

    [Theory]
    [InlineData("--async-scheduling", true)]
    [InlineData("--no-async-scheduling", false)]
    [InlineData("--async_scheduling", true)]
    public void AsyncScheduling_BooleanForms(string token, bool expected)
    {
        Assert.Equal(expected, Norm(token).Definition.AsyncScheduling);
    }

    [Fact]
    public void SpeculativeConfig_FullJson_EqualsForm_AndUnderscoreSpelling()
    {
        Assert.Equal(new SpeculativeDecoding("mtp", 4), Norm("--speculative-config={\"method\":\"mtp\",\"num_speculative_tokens\":4}").Definition.Speculative);
        Assert.Equal(new SpeculativeDecoding("ngram", 5), Norm("--speculative_config", "{\"method\":\"ngram\",\"num_speculative_tokens\":5}").Definition.Speculative);
    }

    [Fact]
    public void SpeculativeConfig_DottedKeys_BuildTheObject()
    {
        // NVIDIA's Nemotron recipe style: --speculative_config.model X plus a dotted depth.
        var n = Norm("--speculative_config.model", "/models/draft", "--speculative-config.num_speculative_tokens=3", "--speculative-config.moe_backend=triton");
        var spec = n.Definition.Speculative!;
        Assert.Equal("", spec.Method);
        Assert.Equal("/models/draft", spec.DraftModel);
        Assert.Equal(3, spec.NumSpeculativeTokens);
        Assert.Equal("\"triton\"", spec.Extra["moe_backend"]);
        Assert.Empty(n.Definition.ExtraArgs);
        Assert.Equal(3, n.Lifted.Count);
    }

    [Theory]
    [InlineData("-cc", "{\"cudagraph_mode\":\"PIECEWISE\"}")]
    [InlineData("--compilation-config", "{\"cudagraph_mode\":\"PIECEWISE\"}")]
    [InlineData("--compilation_config", "{\"cudagraph_mode\":\"PIECEWISE\"}")]
    public void CompilationConfig_FullJson_Forms(string flag, string json)
    {
        var n = Norm(flag, json);
        Assert.Equal("PIECEWISE", n.Definition.CudaGraphMode);
        Assert.Null(n.Definition.CompilationConfig);
        Assert.Empty(n.Definition.ExtraArgs);
    }

    [Theory]
    [InlineData("-cc.cudagraph_mode=FULL_AND_PIECEWISE")]
    [InlineData("--compilation-config.cudagraph_mode=FULL_AND_PIECEWISE")]
    [InlineData("-cc.cudagraph_mode FULL_AND_PIECEWISE")]
    public void CompilationConfig_DottedForms(string token)
    {
        var n = Norm(token);
        Assert.Equal("FULL_AND_PIECEWISE", n.Definition.CudaGraphMode);
        Assert.Empty(n.Definition.ExtraArgs);
    }

    [Fact]
    public void CompilationConfig_NestedDottedKeysMergeAmongThemselves_AndOtherKeysAreKept()
    {
        var n = Norm("-cc.cudagraph_mode=PIECEWISE", "-cc.pass_config.enable_fusion=true", "--compilation-config.pass_config.enable_noop=false", "-cc.mode", "3");

        Assert.Equal("PIECEWISE", n.Definition.CudaGraphMode);
        Assert.Equal("""{"mode":3,"pass_config":{"enable_fusion":true,"enable_noop":false}}""", n.Definition.CompilationConfig!.ToJson());

        var args = VllmArgsBuilder.Build(n.Definition, 100);
        int i = args.ToList().IndexOf("--compilation-config");
        Assert.Equal("""{"cudagraph_mode":"PIECEWISE","mode":3,"pass_config":{"enable_fusion":true,"enable_noop":false}}""", args[i + 1]);
        Assert.Equal(1, Count(args, "--compilation-config"));
    }

    [Fact]
    public void CompilationConfig_AnyDottedKey_DiscardsTheTypedAndFullJsonObjects()
    {
        var typed = Base with { CudaGraphMode = "PIECEWISE", CompilationConfig = JsonFields.Parse("""{"mode":3}""") };
        var n = VllmArgsNormalizer.Normalize(typed with { ExtraArgs = ["-cc", """{"cudagraph_mode":"FULL","level":2}""", "-cc.pass_config.enable_fusion=true"] });

        Assert.Null(n.Definition.CudaGraphMode);
        Assert.Equal("""{"pass_config":{"enable_fusion":true}}""", n.Definition.CompilationConfig!.ToJson());
    }

    [Fact]
    public void UnrelatedFlags_StayInOrder_AndOptimizationLevelIsNotOwned()
    {
        var n = Norm("--trust-remote-code", "-O3", "--max-num-seqs", "4", "--attention-backend", "flashinfer", "--hf-overrides", "{\"a\":1}");
        Assert.Equal(["--trust-remote-code", "-O3", "--attention-backend", "flashinfer", "--hf-overrides", "{\"a\":1}"], n.Definition.ExtraArgs);
    }

    // ------------------------------------------------------------ normalizer: semantics

    [Fact]
    public void LastOccurrenceWins_ForScalars_AndFullJson()
    {
        var n = Norm("--max-num-seqs", "8", "--max-num-seqs=64",
            "--speculative-config", "{\"method\":\"mtp\",\"num_speculative_tokens\":1}",
            "--speculative-config", "{\"method\":\"mtp\",\"num_speculative_tokens\":3}");
        Assert.Equal(64, n.Definition.MaxNumSeqs);
        Assert.Equal(new SpeculativeDecoding("mtp", 3), n.Definition.Speculative);
    }

    [Fact]
    public void RawFullJson_ReplacesTheTypedObject_LikeVllm()
    {
        // The builder puts ExtraArgs after the typed argument; vLLM keeps the last --speculative-config whole.
        var typed = Base with { Speculative = new SpeculativeDecoding("mtp", 2) { Extra = JsonFields.Parse("""{"acceptance":"x"}""") } };
        var n = VllmArgsNormalizer.Normalize(typed with { ExtraArgs = ["--speculative-config", """{"method":"ngram","num_speculative_tokens":4}"""] });
        Assert.Equal(new SpeculativeDecoding("ngram", 4), n.Definition.Speculative);
    }

    [Fact]
    public void DottedKeys_DiscardTheTypedObject_AndKeepEveryDottedKey()
    {
        // Verified in the image: with any dotted key present vLLM builds the config from the dotted keys only.
        var typed = Base with { Speculative = new SpeculativeDecoding("mtp", 2) { Extra = JsonFields.Parse("""{"moe_backend":"triton"}""") } };
        var n = VllmArgsNormalizer.Normalize(typed with { ExtraArgs = ["--speculative-config.num_speculative_tokens=5", "--speculative-config.disable_padded_drafter_batch=true"] });

        Assert.Equal("""{"disable_padded_drafter_batch":true,"num_speculative_tokens":5}""", n.Definition.Speculative!.ToJson());
        Assert.Equal("", n.Definition.Speculative.Method);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DottedKeys_WinOverAFullJsonObject_InEitherOrder(bool jsonFirst)
    {
        // Observed: --speculative-config '{"method":"mtp","num_speculative_tokens":2}' --speculative-config.num_speculative_tokens 5
        // gives {'num_speculative_tokens': 5}, and so does the reverse order.
        string[] json = ["--speculative-config", """{"method":"mtp","num_speculative_tokens":2}"""];
        string[] dotted = ["--speculative-config.num_speculative_tokens", "5"];
        var n = Norm([.. jsonFirst ? json.Concat(dotted) : dotted.Concat(json)]);

        Assert.Equal("""{"num_speculative_tokens":5}""", n.Definition.Speculative!.ToJson());
        Assert.Empty(n.Definition.ExtraArgs);
    }

    [Fact]
    public void DottedKeys_MergeAmongThemselves_LastWinsPerKey()
    {
        var n = Norm("--speculative-config.num_speculative_tokens=3", "--speculative-config.method=mtp", "--speculative-config.num_speculative_tokens=4");
        Assert.Equal(new SpeculativeDecoding("mtp", 4), n.Definition.Speculative);
    }

    [Fact]
    public void ContainerCommand_WithTypedJsonAndDottedExtras_ReadsBackAsTheDottedConfig()
    {
        var model = Base with
        {
            Speculative = new SpeculativeDecoding("mtp", 2),
            ExtraArgs = ["--speculative_config.model", "/models/draft", "--speculative-config.num_speculative_tokens=3"],
        };
        var legacyCmd = LiveDefinitions.LegacyBuild(model, 100);

        var parsed = VllmArgsParser.Parse(legacyCmd, "img", "img", 100, Base);
        Assert.Equal(new SpeculativeDecoding("", 3) { DraftModel = "/models/draft" }, parsed.Speculative);
        Assert.Equal(ModelConfigHash.Compute("img", legacyCmd), ModelConfigHash.Compute(model, "img", 100));
    }

    [Fact]
    public void InvalidOrMissingValues_StayInExtraArgs_AsProblems()
    {
        var n = Norm("--max-num-seqs", "lots", "--speculative-config", "{not json", "--kv-cache-dtype");
        Assert.Equal(["--max-num-seqs", "lots", "--speculative-config", "{not json", "--kv-cache-dtype"], n.Definition.ExtraArgs);
        Assert.Equal(3, n.Problems.Count);
        Assert.Empty(n.Lifted);
    }

    [Fact]
    public void AlreadyCanonical_ReturnsTheSameInstance()
    {
        var model = Base with { ExtraArgs = ["--enforce-eager"], Speculative = new SpeculativeDecoding("mtp", 2) };
        Assert.Same(model, VllmArgsNormalizer.Apply(model));
        Assert.Same(model, VllmArgsNormalizer.Apply(VllmArgsNormalizer.Apply(model)));
    }

    // ------------------------------------------------------------ live definitions

    [Fact]
    public void Live35B_LiftsTheRawConfigVllmActuallyUsed()
    {
        var n = VllmArgsNormalizer.Normalize(LiveDefinitions.Qwen35B);
        var d = n.Definition;

        // MTP × 3 with triton MoE (the raw, last-wins config), not the stale typed MTP × 2.
        Assert.Equal("mtp", d.Speculative!.Method);
        Assert.Equal(3, d.Speculative.NumSpeculativeTokens);
        Assert.Equal("""{"method":"mtp","moe_backend":"triton","num_speculative_tokens":3}""", d.Speculative.ToJson());
        Assert.True(d.AsyncScheduling);
        Assert.Equal("PIECEWISE", d.CudaGraphMode);
        Assert.Equal(8, d.MaxNumSeqs);
        Assert.Equal(
            ["--trust-remote-code", "--attention-backend", "flashinfer", "--moe-backend", "marlin", "--enable-chunked-prefill", "--load-format", "fastsafetensors"],
            d.ExtraArgs);
        Assert.Equal(["--async-scheduling", "--speculative-config"], n.Lifted.Select(l => l.Flag));
    }

    [Fact]
    public void LiveNemotron_LiftsTheDraftModelConfig_AndMambaCacheMode()
    {
        var d = VllmArgsNormalizer.Apply(LiveDefinitions.Nemotron);
        Assert.Equal("", d.Speculative!.Method);
        Assert.Equal("/models/Nemotron-3.5-Lightning-30B-A3B-NVFP4-DSpark", d.Speculative.DraftModel);
        Assert.Equal(3, d.Speculative.NumSpeculativeTokens);
        Assert.Equal("align", d.MambaCacheMode);
        Assert.Equal(["--moe-backend", "marlin", "--mamba-backend", "flashinfer"], d.ExtraArgs);
    }

    [Fact]
    public void Builder_EmitsEveryOwnedSettingExactlyOnce()
    {
        var args = VllmArgsBuilder.Build(LiveDefinitions.Qwen35B, LiveDefinitions.TotalGiB);
        foreach (string flag in new[] { "--speculative-config", "--compilation-config", "--async-scheduling", "--max-num-seqs", "--max-num-batched-tokens", "--kv-cache-dtype", "--enable-prefix-caching" })
        {
            Assert.Equal(1, Count(args, flag));
        }

        int i = args.ToList().IndexOf("--speculative-config");
        Assert.Equal("""{"method":"mtp","moe_backend":"triton","num_speculative_tokens":3}""", args[i + 1]);

        var nemotron = VllmArgsBuilder.Build(LiveDefinitions.Nemotron, LiveDefinitions.TotalGiB);
        Assert.Equal(1, Count(nemotron, "--speculative-config"));
        Assert.Equal(1, Count(nemotron, "--mamba-cache-mode"));
    }

    [Fact]
    public void Builder_PrefixCachingTriState_AndAsyncOff()
    {
        Assert.Contains("--no-enable-prefix-caching", VllmArgsBuilder.Build(LiveDefinitions.Embedding, 100));
        var defaults = VllmArgsBuilder.Build(Base with { EnablePrefixCaching = null, AsyncScheduling = false }, 100);
        Assert.DoesNotContain(defaults, a => a.Contains("prefix-caching", StringComparison.Ordinal));
        Assert.Contains("--no-async-scheduling", defaults);
    }

    // ------------------------------------------------------------ JSON shapes

    [Fact]
    public void Speculative_WritesTheVllmObject_AndReadsTheLegacyShape()
    {
        var spec = new SpeculativeDecoding("", 3) { DraftModel = "/models/d", Extra = JsonFields.Parse("""{"draft_tensor_parallel_size":1}""") };
        string json = JsonSerializer.Serialize(spec);
        Assert.Equal("""{"draft_tensor_parallel_size":1,"model":"/models/d","num_speculative_tokens":3}""", json);
        Assert.Equal(spec, JsonSerializer.Deserialize<SpeculativeDecoding>(json));

        Assert.Equal(new SpeculativeDecoding("mtp", 2), JsonSerializer.Deserialize<SpeculativeDecoding>("""{"Method":"mtp","NumSpeculativeTokens":2}"""));
        Assert.Equal(new SpeculativeDecoding("mtp", 2), JsonSerializer.Deserialize<SpeculativeDecoding>("""{"method":"mtp","numSpeculativeTokens":2}""",
            WebJson));
    }

    [Fact]
    public void Definition_RoundTripsThroughJson_WithEveryNewField()
    {
        var model = VllmArgsNormalizer.Apply(LiveDefinitions.Qwen35B) with
        {
            CompilationConfig = JsonFields.Parse("""{"pass_config":{"enable_fusion":true}}"""),
            MambaCacheMode = "align",
            EnablePrefixCaching = null,
        };

        var back = JsonSerializer.Deserialize<ModelDefinition>(JsonSerializer.Serialize(model))!;
        Assert.Equal(model with { ExtraArgs = back.ExtraArgs, Tags = back.Tags, Sources = back.Sources }, back);
        Assert.Equal(model.ExtraArgs, back.ExtraArgs);
    }

    // ------------------------------------------------------------ registry migration

    [Fact]
    public async Task Registry_MigratesALegacyFileOnLoad_AndPersistsItOnTheNextSave()
    {
        Directory.CreateDirectory(_dataDir);
        string file = Path.Combine(_dataDir, "models.json");
        await File.WriteAllTextAsync(file, "[" + string.Join(",",
            LiveDefinitions.LegacyJson(LiveDefinitions.Qwen35B),
            LiveDefinitions.LegacyJson(LiveDefinitions.Nemotron),
            LiveDefinitions.LegacyJson(LiveDefinitions.Embedding)) + "]");
        using var registry = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }));

        var qwen = (await registry.GetAsync("qwen36-35b-a3b-nvfp4"))!;
        // MN-006: the bare array is also the version-1 registry file, so the one-time migration places it on "dgx".
        AssertSame(VllmArgsNormalizer.Apply(LiveDefinitions.Qwen35B) with { Placement = new ModelPlacement("dgx") }, qwen);
        Assert.False((await registry.GetAsync("qwen3-embedding"))!.EnablePrefixCaching);
        Assert.Contains("--speculative-config", await File.ReadAllTextAsync(file)); // a read only wraps the v1 array in the v2 envelope (MN-006); normalization is not persisted

        await registry.UpsertAsync(LiveDefinitions.Embedding with { Tags = ["embedding", "rag"] });

        string saved = await File.ReadAllTextAsync(file);
        Assert.DoesNotContain("--speculative-config", saved);
        Assert.DoesNotContain("--async-scheduling", saved);
        Assert.Contains("\"moe_backend\":\"triton\"", saved, StringComparison.Ordinal);
        using var reread = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }));
        AssertSame(qwen, (await reread.GetAsync(qwen.Id))!);
    }

    [Fact]
    public async Task Registry_Save_LiftsOwnedFlags_AndRefusesUnreadableOnes()
    {
        using var registry = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }));

        await registry.UpsertAsync(Base with { ExtraArgs = ["--async-scheduling", "-cc.cudagraph_mode=PIECEWISE", "--enforce-eager"] });
        var stored = (await registry.GetAsync("m"))!;
        Assert.True(stored.AsyncScheduling);
        Assert.Equal("PIECEWISE", stored.CudaGraphMode);
        Assert.Equal(["--enforce-eager"], stored.ExtraArgs);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(Base with { ExtraArgs = ["--max-num-seqs", "many"] }));
        Assert.Contains("--max-num-seqs", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(Base with { Speculative = new SpeculativeDecoding("", 3) }));
    }

    // ------------------------------------------------------------ hash stability

    [Fact]
    public void Hash_OfAnUnchangedDefinition_IsTheLegacyHash()
    {
        // Containers launched before the redesign keep matching on the fast path (label == expected).
        var plain = Base with { Speculative = new SpeculativeDecoding("mtp", 2), CudaGraphMode = "PIECEWISE", ExtraArgs = ["--enforce-eager"] };
        Assert.Equal(
            LiveDefinitions.LegacyHash("img", LiveDefinitions.LegacyBuild(plain, 120)),
            ModelConfigHash.Compute(plain, "img", 120));
    }

    [Theory]
    [InlineData("qwen")]
    [InlineData("nemotron")]
    public void Hash_OfTheRunningLegacyCommand_EqualsTheNormalizedDefinition(string which)
    {
        var legacy = which == "qwen" ? LiveDefinitions.Qwen35B : LiveDefinitions.Nemotron;
        var runningCmd = LiveDefinitions.LegacyBuild(legacy, LiveDefinitions.TotalGiB);

        string fromContainer = ModelConfigHash.Compute(LiveDefinitions.Image, runningCmd);
        string fromDefinition = ModelConfigHash.Compute(VllmArgsNormalizer.Apply(legacy), LiveDefinitions.Image, LiveDefinitions.TotalGiB);

        Assert.Equal(fromDefinition, fromContainer);
        Assert.Equal(fromDefinition, ModelConfigHash.Compute(legacy, LiveDefinitions.Image, LiveDefinitions.TotalGiB));

        // The label those containers carry is the raw legacy hash, which differs: the runtime recomputes from Cmd.
        Assert.NotEqual(fromDefinition, LiveDefinitions.LegacyHash(LiveDefinitions.Image, runningCmd));
    }

    [Fact]
    public void Hash_OfTheEmbeddingModel_Changes_BecauseItsEffectiveArgsChange()
    {
        var runningCmd = LiveDefinitions.LegacyBuild(LiveDefinitions.Embedding, LiveDefinitions.TotalGiB);
        Assert.NotEqual(
            ModelConfigHash.Compute(LiveDefinitions.Image, runningCmd),
            ModelConfigHash.Compute(LiveDefinitions.Embedding, LiveDefinitions.Image, LiveDefinitions.TotalGiB));
    }

    [Fact]
    public void Parser_ReadsTheEffectiveConfig_OfALegacyContainer()
    {
        var launched = VllmArgsParser.Parse(
            LiveDefinitions.LegacyBuild(LiveDefinitions.Qwen35B, LiveDefinitions.TotalGiB), LiveDefinitions.Image, LiveDefinitions.Image,
            LiveDefinitions.TotalGiB, LiveDefinitions.Qwen35B);

        Assert.Equal(VllmArgsNormalizer.Apply(LiveDefinitions.Qwen35B) with { ExtraArgs = launched.ExtraArgs }, launched);
        Assert.Equal(VllmArgsNormalizer.Apply(LiveDefinitions.Qwen35B).ExtraArgs, launched.ExtraArgs);
    }
}
