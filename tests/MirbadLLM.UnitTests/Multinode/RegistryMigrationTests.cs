using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Api;
using FakeHttpMessageHandler = MirbadLLM.UnitTests.Orchestration.Fakes.FakeHttpMessageHandler;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-006 (design §5.4): the one-time, versioned <c>models.json</c> v1 → v2 migration on a file shaped like the
/// owner's live one, v2 "unplaced" semantics, and the <c>PUT /api/models/{id}</c> merge-on-absent rules.
/// </summary>
public sealed class RegistryMigrationTests : IDisposable
{
    private const string Image = "local/qwen38-vllm-dflash2:66e5414c6d75";
    private const double TotalGiB = 121.7;

    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] LiveIds =
    [
        "qwen36-35b-a3b-nvfp4", "nemotron-3-5", "qwen3-embedding-4b", "qwen3-vl-embedding-2b", "ov-intent-analysis",
        "qwen38-27b-nvfp4", "qwen3-embedding", "ornith-9b-fp8",
    ];

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-mn006-" + Guid.NewGuid().ToString("N"));

    private string ModelsFile => Path.Combine(_dataDir, ModelRegistryDocument.FileName);

    private string LegacyCopy => Path.Combine(_dataDir, ModelRegistryDocument.LegacyCopyFileName);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private JsonModelRegistry Registry(RegistryMigrationOptions? migration = null) =>
        new(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }), Options.Create(migration ?? new RegistryMigrationOptions()));

    private static string LiveFixture([CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "models.live-v1.json"));

    private async Task<byte[]> WriteLiveV1Async()
    {
        Directory.CreateDirectory(_dataDir);
        byte[] bytes = Encoding.UTF8.GetBytes(LiveFixture());
        await File.WriteAllBytesAsync(ModelsFile, bytes, Ct);
        return bytes;
    }

    /// <summary>The definitions exactly as today's registry returns them from the v1 file (deserialize + normalize).</summary>
    private static List<ModelDefinition> TodaysView() =>
        JsonSerializer.Deserialize<List<ModelDefinition>>(LiveFixture(), FileJson)!
            .Select(VllmArgsNormalizer.Apply)
            .ToList();

    private static void AssertSameJson(ModelDefinition expected, ModelDefinition actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private JsonObject ReadFileJson() => JsonNode.Parse(File.ReadAllText(ModelsFile))!.AsObject();

    // ------------------------------------------------------------ v1 → v2 on the live-shaped file

    [Fact]
    public async Task LiveFile_IsMigratedOnce_ToTheV2Envelope_WithTheV1CopyKept()
    {
        byte[] original = await WriteLiveV1Async();
        using var registry = Registry();

        var models = await registry.ListAsync(Ct);

        Assert.Equal(LiveIds, models.Select(m => m.Id));
        Assert.All(models, m =>
        {
            Assert.Equal(new ModelPlacement("dgx"), m.Placement);
            Assert.Equal(RuntimeKind.Vllm, m.Runtime);
            Assert.Null(m.LlamaCpp);
            Assert.Null(m.HostMemoryBudgetGiB);
        });
        Assert.Equal(
            ["qwen3-embedding-4b", "qwen3-vl-embedding-2b", "ov-intent-analysis"],
            models.Where(m => m.KeepServingDuringJobs).Select(m => m.Id));

        var file = ReadFileJson();
        Assert.Equal(ModelRegistryDocument.CurrentSchemaVersion, (int)file["schemaVersion"]!);
        Assert.Equal(8, file["models"]!.AsArray().Count);
        Assert.Equal(original, await File.ReadAllBytesAsync(LegacyCopy, Ct));
        Assert.False(File.Exists(ModelsFile + ".tmp"));
        Assert.Single(Directory.GetFiles(_dataDir, "models.v1*"));

        // The rewritten file reads back as the frozen envelope with the registry's file options.
        var document = JsonSerializer.Deserialize<ModelRegistryDocument>(File.ReadAllText(ModelsFile), FileJson)!;
        Assert.All(document.Models, m => Assert.Equal("dgx", m.Placement!.NodeId));
    }

    [Fact]
    public async Task SecondLoad_AndANewProcess_DoNothing()
    {
        await WriteLiveV1Async();
        using (var first = Registry())
        {
            _ = await first.ListAsync(Ct);
        }

        byte[] migrated = await File.ReadAllBytesAsync(ModelsFile, Ct);
        byte[] copy = await File.ReadAllBytesAsync(LegacyCopy, Ct);
        var writeTime = File.GetLastWriteTimeUtc(ModelsFile);

        // A different keep-serving list or bootstrap id must not matter any more: the file is version 2.
        using var second = Registry(new RegistryMigrationOptions
        {
            Nodes = { BootstrapNodeId = "other" },
            Migration = { KeepServingDuringJobs = ["qwen36-35b-a3b-nvfp4"] },
        });
        var models = await second.ListAsync(Ct);
        _ = await second.GetAsync("nemotron-3-5", Ct);

        Assert.Equal(migrated, await File.ReadAllBytesAsync(ModelsFile, Ct));
        Assert.Equal(copy, await File.ReadAllBytesAsync(LegacyCopy, Ct));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(ModelsFile));
        Assert.All(models, m => Assert.Equal("dgx", m.Placement!.NodeId));
        Assert.False(models.Single(m => m.Id == "qwen36-35b-a3b-nvfp4").KeepServingDuringJobs);
        Assert.Single(Directory.GetFiles(_dataDir, "models.v1*"));
    }

    [Fact]
    public async Task Migration_KeepsEveryConfigHash_AndNeedsNoRestart()
    {
        await WriteLiveV1Async();
        var before = TodaysView();
        using var registry = Registry();

        var after = await registry.ListAsync(Ct);

        foreach (var old in before)
        {
            var migrated = after.Single(m => m.Id == old.Id);
            Assert.Equal(ModelConfigHash.Compute(old, Image, TotalGiB), ModelConfigHash.Compute(migrated, Image, TotalGiB));
            Assert.Empty(SettingChanges.RestartNeeded(old, migrated));

            // Nothing but the new fields differs from today's in-memory view.
            AssertSameJson(old with { Placement = migrated.Placement, KeepServingDuringJobs = migrated.KeepServingDuringJobs }, migrated);
        }

        // And again after a reload from the rewritten file.
        using var reread = Registry();
        foreach (var old in before)
        {
            Assert.Equal(ModelConfigHash.Compute(old, Image, TotalGiB), ModelConfigHash.Compute((await reread.GetAsync(old.Id, Ct))!, Image, TotalGiB));
        }
    }

    [Fact]
    public async Task Migration_KeepsTheTypedFields_OfTheLiveModels()
    {
        await WriteLiveV1Async();
        using var registry = Registry();

        var qwen = (await registry.GetAsync("qwen36-35b-a3b-nvfp4", Ct))!;
        Assert.Equal("mtp", qwen.Speculative!.Method);
        Assert.Equal(3, qwen.Speculative.NumSpeculativeTokens);
        Assert.Equal("triton", qwen.Speculative.Extra.GetString("moe_backend"));
        Assert.True(qwen.AsyncScheduling);
        Assert.Equal("PIECEWISE", qwen.CudaGraphMode);
        Assert.True(qwen.AutoStart);

        var nemotron = (await registry.GetAsync("nemotron-3-5", Ct))!;
        Assert.Equal("/models/Nemotron-3.5-Lightning-30B-A3B-NVFP4-DSpark", nemotron.Speculative!.DraftModel);
        Assert.Equal(3, nemotron.Speculative.NumSpeculativeTokens);
        Assert.Equal("align", nemotron.MambaCacheMode);
        Assert.Equal(2, nemotron.Sources.Count);

        var embedding = (await registry.GetAsync("qwen3-embedding-4b", Ct))!;
        Assert.Equal(["--runner", "pooling"], embedding.ExtraArgs);
        Assert.False(embedding.EnablePrefixCaching);
        Assert.Contains("--runner", VllmArgsBuilder.Build(embedding, TotalGiB));
    }

    [Fact]
    public async Task Migration_StoresDefinitionsAsRead_SoNormalizationIsStillPersistedOnTheNextSave()
    {
        await WriteLiveV1Async();
        using var registry = Registry();

        // In memory the raw owned flags of the 27B are lifted (raw MTP × 5 wins, async scheduling on) ...
        var qwen27 = (await registry.GetAsync("qwen38-27b-nvfp4", Ct))!;
        Assert.Equal(5, qwen27.Speculative!.NumSpeculativeTokens);
        Assert.True(qwen27.AsyncScheduling);
        Assert.Equal(["--trust-remote-code"], qwen27.ExtraArgs);

        // ... but the migration rewrite did not persist that (exactly as before MN-006, a read never normalizes the file).
        string afterMigration = await File.ReadAllTextAsync(ModelsFile, Ct);
        Assert.Contains("--async-scheduling", afterMigration, StringComparison.Ordinal);
        Assert.Contains("--speculative-config", afterMigration, StringComparison.Ordinal);

        // The next save persists the canonical shape, still as version 2 with the placements.
        await registry.UpsertAsync((await registry.GetAsync("ornith-9b-fp8", Ct))! with { AutoStart = true }, Ct);
        string saved = await File.ReadAllTextAsync(ModelsFile, Ct);
        Assert.DoesNotContain("--async-scheduling", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("--speculative-config", saved, StringComparison.Ordinal);
        using var reread = Registry();
        AssertSameJson(qwen27, (await reread.GetAsync("qwen38-27b-nvfp4", Ct))!);
        Assert.All(await reread.ListAsync(Ct), m => Assert.Equal("dgx", m.Placement!.NodeId));
    }

    [Fact]
    public async Task Migration_UsesTheConfiguredBootstrapNode_AndKeepServingList()
    {
        await WriteLiveV1Async();
        using var registry = Registry(new RegistryMigrationOptions
        {
            Nodes = { BootstrapNodeId = "spark-1" },
            Migration = { KeepServingDuringJobs = ["qwen3-embedding", " ov-intent-analysis ", "no-such-model"] },
        });

        var models = await registry.ListAsync(Ct);

        Assert.All(models, m => Assert.Equal(new ModelPlacement("spark-1"), m.Placement));
        Assert.Equal(["ov-intent-analysis", "qwen3-embedding"], models.Where(m => m.KeepServingDuringJobs).Select(m => m.Id).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Migration_WithABlankKeepServingEntry_ExemptsNothing()
    {
        await WriteLiveV1Async();
        using var registry = Registry(new RegistryMigrationOptions { Migration = { KeepServingDuringJobs = [""] } });

        Assert.DoesNotContain(await registry.ListAsync(Ct), m => m.KeepServingDuringJobs);
    }

    [Fact]
    public async Task Migration_RefusesAnInvalidBootstrapNodeId_AndLeavesTheFileAlone()
    {
        byte[] original = await WriteLiveV1Async();
        using var registry = Registry(new RegistryMigrationOptions { Nodes = { BootstrapNodeId = "DGX Spark" } });

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ListAsync(Ct));

        Assert.Equal(original, await File.ReadAllBytesAsync(ModelsFile, Ct));
        Assert.False(File.Exists(LegacyCopy));
    }

    [Fact]
    public async Task InterruptedMigration_CopyAlreadyWritten_IsCompletedWithoutASecondCopy()
    {
        byte[] original = await WriteLiveV1Async();
        await File.WriteAllBytesAsync(LegacyCopy, original, Ct); // crash after the copy, before the rewrite
        using var registry = Registry();

        var models = await registry.ListAsync(Ct);

        Assert.Equal(8, models.Count);
        Assert.Equal(2, (int)ReadFileJson()["schemaVersion"]!);
        Assert.Equal(original, await File.ReadAllBytesAsync(LegacyCopy, Ct));
        Assert.Single(Directory.GetFiles(_dataDir, "models.v1*"));
    }

    [Fact]
    public async Task ADifferentV1File_PutInPlaceLater_NeverOverwritesTheFirstCopy()
    {
        Directory.CreateDirectory(_dataDir);
        byte[] earlier = Encoding.UTF8.GetBytes("""[{"Id":"old","ServedName":"old","ModelPath":"Old"}]""");
        await File.WriteAllBytesAsync(LegacyCopy, earlier, Ct);
        byte[] original = await WriteLiveV1Async();
        using var registry = Registry();

        _ = await registry.ListAsync(Ct);

        Assert.Equal(earlier, await File.ReadAllBytesAsync(LegacyCopy, Ct));
        string second = Assert.Single(Directory.GetFiles(_dataDir, "models.v1.*.json"));
        Assert.Equal(original, await File.ReadAllBytesAsync(second, Ct));
    }

    [Fact]
    public async Task ConcurrentFirstReads_MigrateExactlyOnce()
    {
        await WriteLiveV1Async();
        using var registry = Registry();

        var lists = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => registry.ListAsync(Ct), Ct)));

        Assert.All(lists, l => Assert.Equal(8, l.Count));
        Assert.Single(Directory.GetFiles(_dataDir, "models.v1*"));
        Assert.Equal(2, (int)ReadFileJson()["schemaVersion"]!);
    }

    // ------------------------------------------------------------ version 2: unplaced stays unplaced

    private async Task WriteV2Async(params ModelDefinition[] models)
    {
        Directory.CreateDirectory(_dataDir);
        await File.WriteAllTextAsync(ModelsFile, JsonSerializer.Serialize(new ModelRegistryDocument { Models = models }, FileJson), Ct);
    }

    private static readonly ModelDefinition Placed = new()
    {
        Id = "placed", ServedName = "placed", ModelPath = "Placed", Placement = new ModelPlacement("dgx"), KeepServingDuringJobs = true,
    };

    private static readonly ModelDefinition Unplaced = new() { Id = "restored", ServedName = "restored", ModelPath = "Restored" };

    [Fact]
    public async Task V2_NullPlacement_StaysUnplaced_AfterSaveAndReload()
    {
        await WriteV2Async(Placed, Unplaced);
        Assert.DoesNotContain("Placement", JsonNode.Parse(File.ReadAllText(ModelsFile))!["models"]![1]!.ToJsonString(), StringComparison.Ordinal);
        using (var registry = Registry())
        {
            Assert.Null((await registry.GetAsync("restored", Ct))!.Placement);
            await registry.UpsertAsync(Placed with { MaxNumSeqs = 4 }, Ct);
        }

        using var reread = Registry();
        Assert.Null((await reread.GetAsync("restored", Ct))!.Placement);
        Assert.Equal(new ModelPlacement("dgx"), (await reread.GetAsync("placed", Ct))!.Placement);
        Assert.False(File.Exists(LegacyCopy));
    }

    [Fact]
    public async Task V2_ExplicitNullPlacementInTheFile_IsPreserved_AndNotReplaced()
    {
        Directory.CreateDirectory(_dataDir);
        await File.WriteAllTextAsync(ModelsFile, """
            { "schemaVersion": 2, "models": [ { "Id": "x", "ServedName": "x", "ModelPath": "X", "Placement": null } ] }
            """, Ct);
        string before = await File.ReadAllTextAsync(ModelsFile, Ct);
        using var registry = Registry();

        Assert.Null((await registry.GetAsync("x", Ct))!.Placement);
        Assert.Equal(before, await File.ReadAllTextAsync(ModelsFile, Ct)); // a load never rewrites a v2 file
    }

    [Fact]
    public async Task NewerSchemaVersion_IsRefused_AndNotRewritten()
    {
        Directory.CreateDirectory(_dataDir);
        const string Newer = """{ "schemaVersion": 3, "models": [] }""";
        await File.WriteAllTextAsync(ModelsFile, Newer, Ct);
        using var registry = Registry();

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.ListAsync(Ct));
        Assert.Equal(Newer, await File.ReadAllTextAsync(ModelsFile, Ct));
    }

    [Theory]
    [InlineData("""{ "models": [ { "id": "a", "servedName": "a", "modelPath": "a" } ] }""")] // no version
    [InlineData("""{ "schemaVersion": 2 }""")] // no models
    [InlineData("""{ "SchemaVersion": 2, "Models": [ { "id": "a", "servedName": "a", "modelPath": "a" } ] }""")] // wrong case
    [InlineData("""{ "schemaVersion": "2", "models": [] }""")] // version not a number
    [InlineData("""{ "something": "else" }""")]
    [InlineData("42")]
    public async Task AnEnvelopeWithoutBothKeys_IsRefused_AndNotRewritten(string json)
    {
        // MN-090: a defaulted version/model list would read as an empty registry, and the next save would erase the file.
        Directory.CreateDirectory(_dataDir);
        await File.WriteAllTextAsync(ModelsFile, json, Ct);
        using var registry = Registry();

        await Assert.ThrowsAsync<InvalidDataException>(() => registry.ListAsync(Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => registry.UpsertAsync(Placed, Ct));
        Assert.Equal(json, await File.ReadAllTextAsync(ModelsFile, Ct));
    }

    [Fact]
    public async Task AV1FileWithAUtf8Bom_IsMigrated_AndTheCopyKeepsTheOriginalBytes()
    {
        // MN-090: files saved by a Windows editor start with a BOM; today's stream-based load accepted them.
        Directory.CreateDirectory(_dataDir);
        byte[] original = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(LiveFixture())];
        await File.WriteAllBytesAsync(ModelsFile, original, Ct);
        using var registry = Registry();

        var models = await registry.ListAsync(Ct);

        Assert.Equal(LiveIds, models.Select(m => m.Id));
        Assert.Equal(original, await File.ReadAllBytesAsync(LegacyCopy, Ct));
        Assert.Equal(ModelRegistryDocument.CurrentSchemaVersion, (int)ReadFileJson()["schemaVersion"]!);
    }

    [Fact]
    public async Task AV2FileWithAUtf8Bom_Loads()
    {
        Directory.CreateDirectory(_dataDir);
        string json = JsonSerializer.Serialize(new ModelRegistryDocument { Models = [Placed] }, FileJson);
        await File.WriteAllBytesAsync(ModelsFile, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(json)], Ct);
        using var registry = Registry();

        Assert.Equal(new ModelPlacement("dgx"), (await registry.GetAsync(Placed.Id, Ct))!.Placement);
    }

    [Fact]
    public async Task MissingFile_IsSeeded_AsAV2EnvelopePlacedOnTheBootstrapNode()
    {
        using var registry = Registry(new RegistryMigrationOptions { Nodes = { BootstrapNodeId = "spark-1" } });

        var seed = Assert.Single(await registry.ListAsync(Ct));

        Assert.Equal("ornith-9b-fp8", seed.Id);
        Assert.Equal(new ModelPlacement("spark-1"), seed.Placement);
        Assert.False(seed.KeepServingDuringJobs);
        Assert.Equal(2, (int)ReadFileJson()["schemaVersion"]!);
        Assert.False(File.Exists(LegacyCopy));
    }

    // ------------------------------------------------------------ PUT merge rules (design §5.4)

    private sealed class ApiHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required JsonModelRegistry Registry { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(Ct);
            await App.DisposeAsync();
            Registry.Dispose();
        }
    }

    private async Task<ApiHost> StartApiAsync(INodeRegistry? nodes = null)
    {
        var registry = Registry();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<IModelRegistry>(registry);
        if (nodes is not null)
        {
            builder.Services.AddSingleton(nodes);
        }

        builder.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        builder.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(128, 64)));
        builder.Services.AddSingleton<IModelMetricsReader>(new FakeModelMetricsReader());
        builder.Services.AddSingleton<IRouterStats>(new FakeRouterStats([]));
        builder.Services.AddSingleton<IHuggingFaceDownloader>(new FakeHuggingFaceDownloader());
        builder.Services.AddSingleton<IHuggingFaceRepoInspector>(new FakeHuggingFaceRepoInspector());
        builder.Services.AddNodeSourcing(Path.Combine(_dataDir, "no-such-models-root"));

        var app = builder.Build();
        app.MapAdminApi();
        await app.StartAsync(Ct);
        return new ApiHost { App = app, Client = app.GetTestClient(), Registry = registry };
    }

    /// <summary>A body as a pre-multi-node client sends it: today's fields only, camelCase.</summary>
    private static string OldClientBody(ModelDefinition model, int maxNumSeqs)
    {
        var node = JsonSerializer.SerializeToNode(model, WebJson)!.AsObject();
        foreach (string field in new[] { "placement", "runtime", "llamaCpp", "hostMemoryBudgetGiB", "keepServingDuringJobs" })
        {
            node.Remove(field);
        }

        node["maxNumSeqs"] = maxNumSeqs;
        return node.ToJsonString();
    }

    private static Task<HttpResponseMessage> PutAsync(ApiHost host, string id, string json) =>
        host.Client.PutAsync($"/api/models/{id}", new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    [Fact]
    public async Task Put_FromAnOldClient_KeepsEveryStoredMultinodeField()
    {
        var stored = Placed with
        {
            Placement = new ModelPlacement("dgx", ["GPU-aaaa"], [0]),
            HostMemoryBudgetGiB = 6,
            Runtime = RuntimeKind.Vllm,
        };
        await WriteV2Async(stored);
        await using var host = await StartApiAsync();

        var response = await PutAsync(host, "placed", OldClientBody(stored, 32));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = (await response.Content.ReadFromJsonAsync<ModelDefinition>(WebJson, Ct))!;
        Assert.Equal(stored.Placement, echoed.Placement);
        var saved = (await host.Registry.GetAsync("placed", Ct))!;
        Assert.Equal(32, saved.MaxNumSeqs);
        Assert.Equal(stored.Placement, saved.Placement);
        Assert.True(saved.KeepServingDuringJobs);
        Assert.Equal(6, saved.HostMemoryBudgetGiB);
        Assert.Equal(RuntimeKind.Vllm, saved.Runtime);
    }

    [Fact]
    public async Task Put_FromAnOldClient_DoesNotPlaceAnUnplacedModel()
    {
        await WriteV2Async(Placed, Unplaced);
        await using var host = await StartApiAsync();

        var response = await PutAsync(host, "restored", OldClientBody(Unplaced, 4));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await host.Registry.GetAsync("restored", Ct))!;
        Assert.Equal(4, saved.MaxNumSeqs);
        Assert.Null(saved.Placement);
    }

    [Theory]
    [InlineData("placement")]
    [InlineData("Placement")]
    public async Task Put_WithAnExplicitNullPlacement_IsRefusedWithUseUnplace(string propertyName)
    {
        await WriteV2Async(Placed);
        await using var host = await StartApiAsync();
        var body = JsonNode.Parse(OldClientBody(Placed, 8))!.AsObject();
        body[propertyName] = null;

        var response = await PutAsync(host, "placed", body.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        Assert.Equal(ErrorCodes.UseUnplace, (string?)problem["code"]);
        var saved = (await host.Registry.GetAsync("placed", Ct))!;
        Assert.Equal(new ModelPlacement("dgx"), saved.Placement);
        Assert.Equal(16, saved.MaxNumSeqs);
    }

    [Fact]
    public async Task Put_WithAPlacementOnAnotherNode_IsRefused_BecauseThatIsAMove()
    {
        await WriteV2Async(Placed, Unplaced);
        await using var host = await StartApiAsync();

        var moved = await PutAsync(host, "placed", JsonSerializer.Serialize(Placed with { Placement = new ModelPlacement("pc-office") }, WebJson));
        var placed = await PutAsync(host, "restored", JsonSerializer.Serialize(Unplaced with { Placement = new ModelPlacement("dgx") }, WebJson));

        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, placed.StatusCode);
        Assert.Equal(ErrorCodes.UseMove, (string?)JsonNode.Parse(await moved.Content.ReadAsStringAsync(Ct))!["code"]);
        Assert.Equal(ErrorCodes.UseMove, (string?)JsonNode.Parse(await placed.Content.ReadAsStringAsync(Ct))!["code"]);
        Assert.Equal(new ModelPlacement("dgx"), (await host.Registry.GetAsync("placed", Ct))!.Placement);
        Assert.Null((await host.Registry.GetAsync("restored", Ct))!.Placement);
    }

    [Fact]
    public async Task Put_WithTheNewFields_StoresThem_OnTheSameNode()
    {
        await WriteV2Async(Placed with { HostMemoryBudgetGiB = 6 });
        await using var host = await StartApiAsync();
        var update = Placed with
        {
            Placement = new ModelPlacement("dgx", ["GPU-bbbb"], [1]),
            KeepServingDuringJobs = false,
            HostMemoryBudgetGiB = null,
        };
        var body = JsonSerializer.SerializeToNode(update, WebJson)!.AsObject();
        body["keepServingDuringJobs"] = false; // explicit false (the record omits defaults)
        body["hostMemoryBudgetGiB"] = null;    // explicit null clears it

        var response = await PutAsync(host, "placed", body.ToJsonString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = (await host.Registry.GetAsync("placed", Ct))!;
        Assert.Equal(new ModelPlacement("dgx", ["GPU-bbbb"], [1]), saved.Placement);
        Assert.False(saved.KeepServingDuringJobs);
        Assert.Null(saved.HostMemoryBudgetGiB);
    }

    [Fact]
    public async Task Put_KeepsTodaysErrors_NotFound_BadJson_AndValidation()
    {
        await WriteV2Async(Placed);
        await using var host = await StartApiAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(host, "missing", OldClientBody(Placed, 8))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(host, "placed", "{not json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(host, "placed", """{"id":"placed"}""")).StatusCode); // required members missing
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(host, "placed", "[]")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(host, "placed", OldClientBody(Placed, 0))).StatusCode); // registry validation
        Assert.Equal(
            HttpStatusCode.UnsupportedMediaType,
            (await host.Client.PutAsync("/api/models/placed", new StringContent("{}", Encoding.UTF8, "text/plain"), Ct)).StatusCode);
    }

    [Fact]
    public void MergeAbsentFields_OnlyOverlaysTheNamedFields()
    {
        var stored = Placed with { Runtime = RuntimeKind.LlamaCpp, LlamaCpp = new LlamaCppSettings { ModelFile = "m.gguf" }, HostMemoryBudgetGiB = 4 };
        var incoming = new ModelDefinition { Id = "placed", ServedName = "placed", ModelPath = "Placed", MaxNumSeqs = 2 };

        var none = AdminApiEndpoints.MergeAbsentFields(incoming, stored, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var all = AdminApiEndpoints.MergeAbsentFields(
            incoming, stored, new HashSet<string>(["placement", "Runtime", "LLAMACPP", "hostMemoryBudgetGiB", "keepServingDuringJobs"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal((stored.Placement, stored.Runtime, stored.LlamaCpp, stored.HostMemoryBudgetGiB, true), (none.Placement, none.Runtime, none.LlamaCpp, none.HostMemoryBudgetGiB, none.KeepServingDuringJobs));
        Assert.Equal(2, none.MaxNumSeqs);
        Assert.Equal(((ModelPlacement?)null, RuntimeKind.Vllm, (LlamaCppSettings?)null, (double?)null, false), (all.Placement, all.Runtime, all.LlamaCpp, all.HostMemoryBudgetGiB, all.KeepServingDuringJobs));
    }

    [Fact]
    public void MergeAbsentFields_KeepsTheOnDemandSettings_UnlessTheBodyNamesThem()
    {
        var stored = Placed with { StartOnRequest = true, IdleStopMinutes = 20, StopToMakeRoom = true };
        var incoming = new ModelDefinition { Id = "placed", ServedName = "placed", ModelPath = "Placed" };

        var none = AdminApiEndpoints.MergeAbsentFields(incoming, stored, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var all = AdminApiEndpoints.MergeAbsentFields(
            incoming, stored, new HashSet<string>(["startOnRequest", "idleStopMinutes", "stopToMakeRoom"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal((true, 20, true), (none.StartOnRequest, none.IdleStopMinutes, none.StopToMakeRoom));
        Assert.Equal((false, (int?)null, false), (all.StartOnRequest, all.IdleStopMinutes, all.StopToMakeRoom));
    }

    [Fact]
    public void MergeAbsentFields_KeepsRuntimeSettingsAndSpeculativeDecoding_UnlessTheBodyNamesThem()
    {
        var stored = Placed with
        {
            Mlx = new MlxSettings { ModelFolder = "m" },
            Container = new ContainerRuntimeSettings { Image = "sg:1" },
            Speculative = new SpeculativeDecoding("mtp", 2),
        };
        var incoming = new ModelDefinition { Id = "placed", ServedName = "placed", ModelPath = "Placed" };

        var none = AdminApiEndpoints.MergeAbsentFields(incoming, stored, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var all = AdminApiEndpoints.MergeAbsentFields(incoming, stored, new HashSet<string>(["MLX", "container", "Speculative"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal((stored.Mlx, stored.Container, stored.Speculative), (none.Mlx, none.Container, none.Speculative));
        Assert.Equal(((MlxSettings?)null, (ContainerRuntimeSettings?)null, (SpeculativeDecoding?)null), (all.Mlx, all.Container, all.Speculative));
    }

    // ------------------------------------------------------------ POST default placement (design §5.6, MN-090)

    private static Task<HttpResponseMessage> PostAsync(ApiHost host, ModelDefinition model) =>
        host.Client.PostAsync("/api/models", new StringContent(JsonSerializer.Serialize(model, WebJson), Encoding.UTF8, "application/json"), Ct);

    [Fact]
    public async Task Post_WithoutPlacement_GoesToTheBootstrapNode_UntilANodeRegistryIsComposed()
    {
        await WriteV2Async(Placed);
        await using var host = await StartApiAsync();

        var response = await PostAsync(host, Unplaced with { Id = "fresh" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(new ModelPlacement("dgx"), (await host.Registry.GetAsync("fresh", Ct))!.Placement);
        Assert.Equal(new ModelPlacement("dgx"), (await response.Content.ReadFromJsonAsync<ModelDefinition>(WebJson, Ct))!.Placement);
    }

    [Fact]
    public async Task Post_WithoutPlacement_GoesToTheOnlyNode_AndKeepsAnExplicitPlacement()
    {
        await WriteV2Async(Placed);
        await using var host = await StartApiAsync(new ListOnlyNodeRegistry(ListOnlyNodeRegistry.Node("node-a"), ListOnlyNodeRegistry.Node("gone", revoked: true)));

        var defaulted = await PostAsync(host, Unplaced with { Id = "fresh" });
        var explicitly = await PostAsync(host, Unplaced with { Id = "chosen", ServedName = "chosen", Placement = new ModelPlacement("node-a", ["GPU-cccc"], [0]) });

        Assert.Equal(HttpStatusCode.Created, defaulted.StatusCode);
        Assert.Equal(HttpStatusCode.Created, explicitly.StatusCode);
        Assert.Equal(new ModelPlacement("node-a"), (await host.Registry.GetAsync("fresh", Ct))!.Placement);
        Assert.Equal(new ModelPlacement("node-a", ["GPU-cccc"], [0]), (await host.Registry.GetAsync("chosen", Ct))!.Placement);
    }

    [Fact]
    public async Task Post_WithoutPlacement_WithSeveralNodes_IsRefusedWithPlacementRequired()
    {
        await WriteV2Async(Placed);
        await using var host = await StartApiAsync(new ListOnlyNodeRegistry(ListOnlyNodeRegistry.Node("node-a"), ListOnlyNodeRegistry.Node("node-b")));

        var response = await PostAsync(host, Unplaced with { Id = "fresh" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.PlacementRequired, (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["code"]);
        Assert.Null(await host.Registry.GetAsync("fresh", Ct));
    }
}
