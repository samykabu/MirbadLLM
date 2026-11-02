using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Downloads;

/// <summary>
/// Exercises <see cref="ModelFolderInspector"/> against real <c>config.json</c> files pulled read-only from the
/// three checkpoints on the DGX Spark (<c>ssh ollama-box 'cat ~/mirbadllm/models/&lt;folder&gt;/config.json'</c>):
/// a Qwen3.5 BF16 checkpoint and its two pre-quantized NVFP4 variants (compressed-tensors and ModelOpt).
/// Each fixture's config.json is copied into a fresh temp "models root" per test, with a synthetic
/// <c>*.safetensors</c> file of a controlled size standing in for the real (multi-GB) weights.
/// </summary>
public sealed class ModelFolderInspectorTests : IDisposable
{
    private readonly string _root;

    public ModelFolderInspectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mirbadllm-inspector-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ModelFolderInspector CreateInspector() =>
        new(Options.Create(new ModelStorageOptions { ModelsLocalPath = _root }));

    /// <summary>Copies a fixture's config.json into <c>{_root}/{folder}</c> and adds a dummy weights file of the given size.</summary>
    private string SeedFolder(string fixtureName, string folder, double weightsGiB)
    {
        string dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.Copy(FixturePath(fixtureName), Path.Combine(dir, "config.json"));

        using var stream = File.Create(Path.Combine(dir, "model.safetensors"));
        stream.SetLength((long)(weightsGiB * 1_073_741_824));
        return folder;
    }

    private static string FixturePath(string fixtureName, [CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", fixtureName, "config.json");

    [Fact]
    public void Inspect_UncensoredBf16Checkpoint_DetectsArchitectureAndBf16()
    {
        SeedFolder("Ornith-1.5-9B-uncensored", "Ornith-1.5-9B-uncensored", weightsGiB: 18.0);
        var inspector = CreateInspector();

        var inspection = inspector.Inspect("Ornith-1.5-9B-uncensored");

        Assert.NotNull(inspection);
        Assert.Equal("Qwen3_5ForConditionalGeneration", inspection!.Architecture);
        Assert.Equal(262_144, inspection.MaxPositionEmbeddings);
        Assert.Equal("BF16", inspection.DetectedFormat);
        Assert.Equal(18.0, inspection.WeightsGiB, precision: 3);
        Assert.True(inspection.MaySupportMtp);
    }

    [Fact]
    public void Inspect_UncensoredBf16Checkpoint_SuggestsFp8OnlineQuantizationAndQwenParsers()
    {
        SeedFolder("Ornith-1.5-9B-uncensored", "Ornith-1.5-9B-uncensored", weightsGiB: 18.0);
        var inspector = CreateInspector();

        var inspection = inspector.Inspect("Ornith-1.5-9B-uncensored")!;

        Assert.Equal("fp8", inspection.Suggestion.Quantization);
        Assert.Equal("qwen3_coder", inspection.Suggestion.ToolCallParser);
        Assert.Equal("qwen3", inspection.Suggestion.ReasoningParser);
        Assert.Equal("Ornith-1.5-9B-uncensored", inspection.Suggestion.ServedName);
    }

    [Theory]
    [InlineData("Ornith-1.5-9B-uncensored-NVFP4", "compressed-tensors")]
    [InlineData("Ornith-1.5-9B-NVFP4", "modelopt")]
    public void Inspect_PreQuantizedNvfp4Checkpoints_AreDetectedAsPreQuantizedAndSuggestNoOnlineQuantization(string fixture, string expectedMethod)
    {
        SeedFolder(fixture, fixture, weightsGiB: 9.0);
        var inspector = CreateInspector();

        var inspection = inspector.Inspect(fixture)!;

        Assert.Contains("pre-quantized", inspection.DetectedFormat, StringComparison.Ordinal);
        Assert.Contains("NVFP4", inspection.DetectedFormat, StringComparison.Ordinal);
        Assert.Contains(expectedMethod, inspection.DetectedFormat, StringComparison.Ordinal);
        Assert.Null(inspection.Suggestion.Quantization);
    }

    /// <summary>
    /// The fixture's text_config has 32 layers with a full_attention layer every 4th (8 full-attention layers),
    /// num_key_value_heads=4, head_dim=256: KV bytes/token at fp8 = 2 x 8 x 4 x 256 = 16384. Capped at 131072
    /// tokens, that is exactly 2 GiB of KV cache (16384 x 131072 / 2^30 = 2^31 / 2^30 = 2).
    /// </summary>
    [Fact]
    public void Inspect_MemoryBudgetFormula_MatchesHandComputedKvCacheEstimate()
    {
        SeedFolder("Ornith-1.5-9B-uncensored", "Ornith-1.5-9B-uncensored", weightsGiB: 18.0);
        var inspector = CreateInspector();

        var inspection = inspector.Inspect("Ornith-1.5-9B-uncensored")!;

        // 18 (weights) + 7 (runtime overhead) + 2 (KV @ 131072 tokens) = 27.
        Assert.Equal(27.0, inspection.Suggestion.MemoryBudgetGiB);
        Assert.Contains("18.0 GiB weights", inspection.Suggestion.MemoryBudgetFormula, StringComparison.Ordinal);
        Assert.Contains("2.0 GiB KV cache", inspection.Suggestion.MemoryBudgetFormula, StringComparison.Ordinal);
        Assert.Contains("131,072 tokens", inspection.Suggestion.MemoryBudgetFormula, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ornith-1.5-9B-uncensored-NVFP4", 9.2, 19.0)]
    [InlineData("Ornith-1.5-9B-NVFP4", 8.3, 18.0)]
    public void Inspect_MemoryBudget_RoundsUpToWholeGiB(string fixture, double weightsGiB, double expectedBudget)
    {
        SeedFolder(fixture, fixture, weightsGiB);
        var inspector = CreateInspector();

        var inspection = inspector.Inspect(fixture)!;

        Assert.Equal(expectedBudget, inspection.Suggestion.MemoryBudgetGiB);
    }

    [Fact]
    public void MinimumBudget_FullContext_ExceedsTheBudgetThatCrashedOnTheDgx()
    {
        // Live failure: 262,144 tokens with a 16 GiB budget left 0.12 GiB of KV cache (4.11 GiB needed).
        SeedFolder("Ornith-1.5-9B-uncensored-NVFP4", "Ornith-1.5-9B-uncensored-NVFP4", weightsGiB: 9.3);
        var inspection = CreateInspector().Inspect("Ornith-1.5-9B-uncensored-NVFP4")!;

        double minimum = inspection.MinimumBudgetGiB(262_144);

        Assert.InRange(inspection.KvGiB(262_144), 3.9, 4.2);
        Assert.True(minimum > 16.0);
        Assert.InRange(minimum, 19.5, 21.0);
    }

    [Fact]
    public void Inspect_MlxCheckpoint_IsLabelledMlxWithItsBits_AndSizesItsKvCache()
    {
        // mlx-community/Qwen3-0.6B-4bit as staged on the Mac (macOS Phase 3): a top-level "quantization" block.
        var inspector = CreateInspector();
        SeedFolder("Qwen3-0.6B-4bit", "Qwen3-0.6B-4bit", weightsGiB: 0.31);

        var inspection = inspector.Inspect("Qwen3-0.6B-4bit")!;

        Assert.Equal("MLX 4-bit", inspection.DetectedFormat);
        Assert.Equal("Qwen3ForCausalLM", inspection.Architecture);
        Assert.Equal(40_960, inspection.MaxPositionEmbeddings);
        Assert.Null(inspection.Suggestion.Quantization); // pre-quantized: no online quantization suggested
        Assert.Equal(2.0 * 28 * 8 * 128, inspection.KvBytesPerToken); // per token at 8 bits; MLX's estimate doubles it
    }

    [Fact]
    public void Inspect_UnknownFolder_ReturnsNull()
    {
        var inspector = CreateInspector();

        Assert.Null(inspector.Inspect("does-not-exist"));
    }

    [Fact]
    public void ListFolders_OnlyListsFoldersWithConfigJson()
    {
        SeedFolder("Ornith-1.5-9B-uncensored", "Ornith-1.5-9B-uncensored", weightsGiB: 18.0);
        Directory.CreateDirectory(Path.Combine(_root, "not-a-checkpoint"));
        var inspector = CreateInspector();

        var folders = inspector.ListFolders();

        var summary = Assert.Single(folders);
        Assert.Equal("Ornith-1.5-9B-uncensored", summary.Folder);
        Assert.Equal(18.0, summary.WeightsGiB, precision: 3);
    }

    [Fact]
    public void ListFolders_NonexistentModelsRoot_ReturnsEmpty()
    {
        var inspector = new ModelFolderInspector(
            Options.Create(new ModelStorageOptions { ModelsLocalPath = Path.Combine(_root, "no-such-root") }));

        Assert.Empty(inspector.ListFolders());
    }

    [Fact]
    public void MaxModelLenPresetsFor_FiltersToCheckpointsMaxPositionEmbeddings()
    {
        var presets = VllmOptionsCatalog.MaxModelLenPresetsFor(32_768);

        Assert.Equal([8192, 16384, 32768], presets);
    }

    [Fact]
    public void MaxModelLenPresetsFor_NullCap_ReturnsAllPresets()
    {
        var presets = VllmOptionsCatalog.MaxModelLenPresetsFor(null);

        Assert.Equal(VllmOptionsCatalog.MaxModelLenPresets, presets);
    }
}
