using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Web;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>Tag catalog, normalization, the ALL-tags filter rule and the suggestions (<see cref="ModelTags"/>).</summary>
public sealed class ModelTagsTests
{
    private static ModelDefinition Model(
        string servedName = "Ornith-9B", string path = "Ornith-1.5-9B", string[]? args = null,
        string? toolParser = "qwen3_coder", string? reasoningParser = "qwen3", SpeculativeDecoding? speculative = null) => new()
    {
        Id = "m",
        ServedName = servedName,
        ModelPath = path,
        ExtraArgs = args ?? [],
        ToolCallParser = toolParser,
        ReasoningParser = reasoningParser,
        Speculative = speculative,
    };

    [Fact]
    public void Catalog_HasTheTenBuiltInsWithLabelsAndDescriptions()
    {
        Assert.Equal(
            ["chat", "reasoning", "tool-calling", "vision", "multimodal", "embedding", "reranker", "code", "speculative", "planner"],
            ModelTags.BuiltIn.Select(t => t.Id));
        Assert.All(ModelTags.BuiltIn, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.Equal("Tool calling", ModelTags.Label("tool-calling"));
        Assert.Equal("long-context", ModelTags.Label("long-context"));
    }

    [Theory]
    [InlineData("Vision", "vision")]
    [InlineData("  Tool Calling ", "tool-calling")]
    [InlineData("tool_calling", "tool-calling")]
    [InlineData("--long--context--", "long-context")]
    [InlineData("Ünïcode 128k", "n-code-128k")]
    [InlineData("abcdefghijklmnopqrstuvwx", "abcdefghijklmnopqrstuvwx")]
    public void Normalize_MakesALowercaseSlug(string raw, string expected) =>
        Assert.Equal(expected, ModelTags.Normalize(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("abcdefghijklmnopqrstuvwxy")] // 25 characters
    public void Normalize_RejectsEmptyOrTooLong(string raw) => Assert.Null(ModelTags.Normalize(raw));

    [Fact]
    public void NormalizeAll_DeduplicatesKeepingOrder_AndEnforcesTheLimits()
    {
        Assert.Equal(["vision", "chat"], ModelTags.NormalizeAll(["Vision", "chat", "VISION"]));
        Assert.Throws<ArgumentException>(() => ModelTags.NormalizeAll(["ok", "!!!"]));
        Assert.Throws<ArgumentException>(() => ModelTags.NormalizeAll(Enumerable.Range(1, 13).Select(i => $"t{i}")));
        Assert.Equal(12, ModelTags.NormalizeAll(Enumerable.Range(1, 12).Select(i => $"t{i}")).Count);
    }

    [Fact]
    public void HasAll_RequiresEverySelectedTag()
    {
        var model = Model() with { Tags = ["chat", "vision"] };

        Assert.True(ModelTags.HasAll(model, []));
        Assert.True(ModelTags.HasAll(model, ["vision"]));
        Assert.True(ModelTags.HasAll(model, ["vision", "chat"]));
        Assert.False(ModelTags.HasAll(model, ["vision", "embedding"]));
    }

    [Fact]
    public void Counts_ListsTagsInUseMostUsedFirst()
    {
        var counts = ModelTags.Counts([
            Model() with { Tags = ["chat", "vision"] },
            Model() with { Tags = ["chat"] },
            Model() with { Tags = [] },
        ]);

        Assert.Equal([("chat", 2), ("vision", 1)], counts);
    }

    [Fact]
    public void Suggest_GenerativeModelWithParsers_IsChatToolCallingReasoning() =>
        Assert.Equal(["chat", "reasoning", "tool-calling"], ModelTags.Suggest(Model()));

    [Fact]
    public void Suggest_AndCapabilities_ReadSglangsFlags_FromItsExtraArgs()
    {
        var sglang = Model() with
        {
            Runtime = RuntimeKind.Sglang, ToolCallParser = null, ReasoningParser = null,
            Container = new ContainerRuntimeSettings { Image = "sglang:1", ExtraArgs = ["--tool-call-parser", "qwen3_coder", "--reasoning-parser", "qwen3", "--context-length", "131072"] },
        };
        Assert.Equal(["chat", "reasoning", "tool-calling"], ModelTags.Suggest(sglang));
        Assert.True(ModelCapabilities.ToolCalling(sglang));
        Assert.Equal(131072, ModelCapabilities.MaxModelLen(sglang));
        Assert.Equal(["chat"], ModelTags.Suggest(sglang with { Container = sglang.Container! with { ExtraArgs = [] } }));
        Assert.Equal(sglang.MaxModelLen, ModelCapabilities.MaxModelLen(sglang with { Container = sglang.Container! with { ExtraArgs = ["--context-length=nope"] } }));
    }

    [Theory]
    [InlineData("--runner pooling")]
    [InlineData("--runner|pooling")]
    [InlineData("--runner=pooling")]
    [InlineData("--convert embed")]
    public void Suggest_PoolingRunnerOrEmbedConvert_IsEmbeddingOnly(string args) =>
        Assert.Equal(["embedding"], ModelTags.Suggest(Model("Qwen3-Embedding-8B", "Qwen3-Embedding-8B", args.Split('|'))));

    [Fact]
    public void Suggest_Reranker_FromConvertClassifyOrName()
    {
        Assert.Equal(["reranker"], ModelTags.Suggest(Model("scorer", "scorer", ["--convert classify"])));
        Assert.Equal(["reranker"], ModelTags.Suggest(Model("Qwen3-Reranker-4B", "Qwen3-Reranker-4B", ["--runner pooling"])));
        Assert.Equal(["reranker"], ModelTags.Suggest(Model("x", "x", ["--runner pooling"]), architecture: "Qwen3RerankerForSequenceClassification"));
    }

    [Fact]
    public void Suggest_Speculative_FromSettingOrExtraArgs()
    {
        Assert.Contains("speculative", ModelTags.Suggest(Model(speculative: new SpeculativeDecoding("mtp", 2))));
        Assert.Contains("speculative", ModelTags.Suggest(Model(args: ["--speculative-config", "{\"model\":\"/models/draft\"}"])));
        Assert.DoesNotContain("speculative", ModelTags.Suggest(Model()));
    }

    [Fact]
    public void Suggest_VisionAndMultimodal_FromVisionConfigOrVlArchitecture()
    {
        Assert.Equal(["chat", "vision", "multimodal"], ModelTags.Suggest(Model(toolParser: null, reasoningParser: null), hasVisionConfig: true));
        Assert.Contains("vision", ModelTags.Suggest(Model(), architecture: "Qwen2_5_VLForConditionalGeneration"));
        Assert.DoesNotContain("vision", ModelTags.Suggest(Model(), architecture: "Qwen3ForCausalLM"));
    }

    [Fact]
    public void Suggest_CodeAndPlanner_FromNames()
    {
        Assert.Contains("code", ModelTags.Suggest(Model("Qwen3-Coder-30B", "Qwen3-Coder-30B-A3B")));
        Assert.Contains("planner", ModelTags.Suggest(Model("intent-router", "x")));
        Assert.Contains("planner", ModelTags.Suggest(Model("x", "Agent-Planner-4B")));
    }

    [Fact]
    public void Suggest_NoParsers_IsJustChat() =>
        Assert.Equal(["chat"], ModelTags.Suggest(Model(toolParser: null, reasoningParser: " ")));
}

/// <summary>Tags and sources are bookkeeping: they never change the launch fingerprint or need a restart, and old files still load.</summary>
public sealed class ModelTagsAndSourcesPersistenceTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private JsonModelRegistry CreateRegistry() => new(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }));

    private static readonly ModelDefinition Base = new() { Id = "alpha", ServedName = "Alpha", ModelPath = "Alpha-9B", MemoryBudgetGiB = 20 };

    [Fact]
    public void ConfigHash_IgnoresTagsAndSources()
    {
        var tagged = Base with
        {
            Tags = ["chat", "vision"],
            Sources = [new ModelSource("owner/Alpha-9B", "Alpha-9B", "main", ["*.safetensors"])],
        };

        Assert.Equal(ModelConfigHash.Compute(Base, "img", 128), ModelConfigHash.Compute(tagged, "img", 128));
        Assert.Empty(SettingChanges.RestartNeeded(Base, tagged));
    }

    [Fact]
    public async Task OldModelsJsonWithoutTagsOrSources_LoadsWithEmptyLists()
    {
        Directory.CreateDirectory(_dataDir);
        await File.WriteAllTextAsync(Path.Combine(_dataDir, "models.json"), """
            [
              { "Id": "legacy", "ServedName": "Legacy", "ModelPath": "Legacy-1B", "MemoryBudgetGiB": 10, "ExtraArgs": [], "AutoStart": false },
              { "Id": "nulls", "ServedName": "Nulls", "ModelPath": "Nulls-1B", "Tags": null, "Sources": null }
            ]
            """);
        using var registry = CreateRegistry();

        var models = await registry.ListAsync();

        Assert.Equal(2, models.Count);
        Assert.All(models, m =>
        {
            Assert.NotNull(m.Tags);
            Assert.Empty(m.Tags);
            Assert.NotNull(m.Sources);
            Assert.Empty(m.Sources);
        });
    }

    [Fact]
    public async Task Upsert_NormalizesTags_AndRoundTripsSources()
    {
        using var registry = CreateRegistry();
        var source = new ModelSource("owner/Alpha-9B", "Alpha-9B", "abc123", ["*.json", "*.safetensors"]);

        await registry.UpsertAsync(Base with { Tags = ["Vision", "tool calling", "vision"], Sources = [source] });
        var saved = await registry.GetAsync("alpha");

        Assert.Equal(["vision", "tool-calling"], saved!.Tags);
        var roundTripped = Assert.Single(saved.Sources);
        Assert.Equal(("owner/Alpha-9B", "Alpha-9B", "abc123"), (roundTripped.RepoId, roundTripped.Folder, roundTripped.Revision));
        Assert.Equal(["*.json", "*.safetensors"], roundTripped.AllowPatterns);
    }

    [Theory]
    [InlineData("not-a-repo", "Alpha-9B")]
    [InlineData("owner/name", "../etc")]
    [InlineData("owner/name", "a/b")]
    [InlineData("owner/name", "..")]
    [InlineData("", "Alpha-9B")]
    public async Task Upsert_RejectsInvalidSources(string repo, string folder)
    {
        using var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(Base with { Sources = [new ModelSource(repo, folder)] }));
    }

    [Fact]
    public async Task Upsert_RejectsInvalidTagsAndDuplicateSourceFolders()
    {
        using var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(Base with { Tags = ["***"] }));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(Base with { Tags = [.. Enumerable.Range(0, 13).Select(i => $"t{i}")] }));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(Base with
        {
            Sources = [new ModelSource("a/b", "Alpha-9B"), new ModelSource("c/d", "Alpha-9B")],
        }));
    }

    [Fact]
    public void FolderInspector_DetectsAVisionConfig()
    {
        string root = Path.Combine(_dataDir, "models");
        Directory.CreateDirectory(Path.Combine(root, "VL"));
        File.WriteAllText(Path.Combine(root, "VL", "config.json"),
            """{ "architectures": ["Qwen3_5ForConditionalGeneration"], "vision_config": { "depth": 24 }, "text_config": { "num_hidden_layers": 2 } }""");
        Directory.CreateDirectory(Path.Combine(root, "Text"));
        File.WriteAllText(Path.Combine(root, "Text", "config.json"), """{ "architectures": ["Qwen3ForCausalLM"] }""");
        var inspector = new ModelFolderInspector(Options.Create(new ModelStorageOptions { ModelsLocalPath = root }));

        Assert.True(inspector.Inspect("VL")!.HasVisionConfig);
        Assert.False(inspector.Inspect("Text")!.HasVisionConfig);
    }
}
