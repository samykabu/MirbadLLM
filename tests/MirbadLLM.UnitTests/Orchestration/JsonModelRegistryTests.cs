using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Orchestration;

public sealed class JsonModelRegistryTests : IDisposable
{
    private readonly string _dataDir;

    public JsonModelRegistryTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private JsonModelRegistry CreateRegistry() =>
        new(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }));

    [Fact]
    public async Task ListAsync_OnFirstRun_SeedsOrnithModel()
    {
        using var registry = CreateRegistry();

        var models = await registry.ListAsync();

        var seed = Assert.Single(models);
        Assert.Equal("ornith-9b-fp8", seed.Id);
        Assert.Equal("Ornith-Uncensored-FP8", seed.ServedName);
        Assert.Equal("Ornith-1.5-9B-uncensored", seed.ModelPath);
        Assert.Equal("fp8", seed.Quantization);
        Assert.Equal(36, seed.MemoryBudgetGiB);
        Assert.True(seed.AutoStart);
        Assert.True(File.Exists(Path.Combine(_dataDir, "models.json")));
    }

    [Fact]
    public async Task UpsertThenGet_RoundTripsAllFields()
    {
        using var registry = CreateRegistry();
        var model = new ModelDefinition
        {
            Id = "my-model",
            ServedName = "My-Model",
            ModelPath = "path/to/model",
            Quantization = "fp8",
            KvCacheDtype = "fp8",
            MaxModelLen = 8192,
            MaxNumSeqs = 4,
            MemoryBudgetGiB = 12.5,
            ToolCallParser = "qwen3_coder",
            ReasoningParser = "qwen3",
            EnablePrefixCaching = false,
            Speculative = new SpeculativeDecoding("mtp", 2),
            CudaGraphMode = "PIECEWISE",
            Image = "custom/image:tag",
            ExtraArgs = ["--foo", "bar"],
            AutoStart = true,
        };

        await registry.UpsertAsync(model);
        var loaded = await registry.GetAsync("my-model");

        // Assert.Equivalent does a structural comparison (unlike record Equals, which would compare
        // ExtraArgs's underlying collection instances by reference since IReadOnlyList<T> has no value equality).
        Assert.Equivalent(model, loaded, strict: true);
    }

    [Fact]
    public async Task UpsertAsync_PersistsAcrossNewRegistryInstance()
    {
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataDir });
        using (var first = new JsonModelRegistry(options))
        {
            await first.UpsertAsync(new ModelDefinition { Id = "persisted", ServedName = "Persisted", ModelPath = "p" });
        }

        using var second = new JsonModelRegistry(options);
        var models = await second.ListAsync();

        Assert.Contains(models, m => m.Id == "persisted");
    }

    [Fact]
    public async Task DeleteAsync_RemovesModel_ReturnsTrue()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(new ModelDefinition { Id = "to-delete", ServedName = "ToDelete", ModelPath = "p" });

        bool deleted = await registry.DeleteAsync("to-delete");
        bool deletedAgain = await registry.DeleteAsync("to-delete");

        Assert.True(deleted);
        Assert.False(deletedAgain);
        Assert.Null(await registry.GetAsync("to-delete"));
    }

    [Theory]
    [InlineData("Bad-Id")]
    [InlineData("bad_id")]
    [InlineData("")]
    [InlineData("this-id-is-far-too-long-to-be-accepted-by-the-registry")]
    public async Task UpsertAsync_RejectsInvalidId(string id)
    {
        using var registry = CreateRegistry();
        var model = new ModelDefinition { Id = id, ServedName = "x", ModelPath = "p" };

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(model));
    }

    [Fact]
    public async Task UpsertAsync_RejectsDuplicateServedName()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(new ModelDefinition { Id = "first", ServedName = "Shared-Name", ModelPath = "p" });

        var duplicate = new ModelDefinition { Id = "second", ServedName = "Shared-Name", ModelPath = "p" };

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(duplicate));
    }

    [Fact]
    public async Task UpsertAsync_AllowsUpdatingTheSameModelWithItsOwnServedName()
    {
        using var registry = CreateRegistry();
        var model = new ModelDefinition { Id = "self", ServedName = "Self", ModelPath = "p", MaxNumSeqs = 8 };
        await registry.UpsertAsync(model);

        var updated = model with { MaxNumSeqs = 32 };
        await registry.UpsertAsync(updated);

        var loaded = await registry.GetAsync("self");
        Assert.Equal(32, loaded!.MaxNumSeqs);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public async Task UpsertAsync_RejectsNonPositiveMemoryBudget(double budget)
    {
        using var registry = CreateRegistry();
        var model = new ModelDefinition { Id = "bad-budget", ServedName = "x", ModelPath = "p", MemoryBudgetGiB = budget };

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(model));
    }

    [Fact]
    public async Task UpsertAsync_DoesNotLeaveATempFileBehind()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(new ModelDefinition { Id = "atomic", ServedName = "Atomic", ModelPath = "p" });

        Assert.False(File.Exists(Path.Combine(_dataDir, "models.json.tmp")));
        Assert.True(File.Exists(Path.Combine(_dataDir, "models.json")));
    }

    // ---- MN-006: version-2 envelope (design §5.4)

    [Fact]
    public async Task ListAsync_OnFirstRun_WritesTheV2Envelope_WithTheSeedOnTheBootstrapNode()
    {
        using var registry = CreateRegistry();

        var seed = Assert.Single(await registry.ListAsync());

        Assert.Equal(new ModelPlacement(RegistryMigrationOptions.DefaultBootstrapNodeId), seed.Placement);
        Assert.Equal(RuntimeKind.Vllm, seed.Runtime);
        using var file = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_dataDir, "models.json")));
        Assert.Equal(ModelRegistryDocument.CurrentSchemaVersion, file.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ornith-9b-fp8", file.RootElement.GetProperty("models")[0].GetProperty("Id").GetString());
        Assert.False(File.Exists(Path.Combine(_dataDir, ModelRegistryDocument.LegacyCopyFileName)));
    }

    [Fact]
    public async Task UpsertThenGet_RoundTripsTheMultinodeFields()
    {
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataDir });
        var model = new ModelDefinition
        {
            Id = "gguf",
            ServedName = "Gguf",
            ModelPath = "gguf-folder",
            Placement = new ModelPlacement("pc-office", ["GPU-1"], [0]),
            Runtime = RuntimeKind.LlamaCpp,
            LlamaCpp = new LlamaCppSettings { ModelFile = "gguf-folder/m.gguf", Delivery = LaunchDelivery.Process },
            HostMemoryBudgetGiB = 12,
            KeepServingDuringJobs = true,
        };
        using (var first = new JsonModelRegistry(options))
        {
            await first.UpsertAsync(model);
        }

        using var second = new JsonModelRegistry(options);
        var loaded = (await second.GetAsync("gguf"))!;

        Assert.Equal(model.Placement, loaded.Placement);
        Assert.Equal(RuntimeKind.LlamaCpp, loaded.Runtime);
        Assert.Equal(LaunchDelivery.Process, loaded.LlamaCpp!.Delivery);
        Assert.Equal(12, loaded.HostMemoryBudgetGiB);
        Assert.True(loaded.KeepServingDuringJobs);
    }

    [Fact]
    public async Task UpsertAsync_WithoutPlacement_StoresTheModelUnplaced()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(new ModelDefinition { Id = "loose", ServedName = "Loose", ModelPath = "p" });

        using var reread = CreateRegistry();
        Assert.Null((await reread.GetAsync("loose"))!.Placement);
        Assert.NotNull((await reread.GetAsync("ornith-9b-fp8"))!.Placement);
    }
}
