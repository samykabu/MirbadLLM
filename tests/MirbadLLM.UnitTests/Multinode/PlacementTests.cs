using System.Net;
using System.Net.Http.Json;
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
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Api;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-108 (design §5.1 validation, §6.2 fit, §6.7 ownership, D75 placement generation): the registry validators run
/// through the real <see cref="JsonModelRegistry"/> extension point with the real <see cref="JsonNodeRegistry"/> and
/// <see cref="OwnershipStore"/>; the API cases run the real admin endpoints on a test server.
/// </summary>
public sealed class PlacementTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly string[] MultinodeFields = ["placement", "runtime", "llamaCpp", "hostMemoryBudgetGiB", "keepServingDuringJobs"];

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-mn108-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    // ------------------------------------------------------------------ fixtures

    private static readonly IReadOnlyList<RuntimeCapability> VllmOnly =
        [new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = true }];

    private static NodeCapabilities UnifiedCaps() => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "1.0.0",
        Features = [.. AgentFeatures.P1],
        Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
        Arch = "arm64",
        Memory = new NodeMemoryInfo(MemoryModel.Unified, 121.7),
        Gpus = [new GpuCapability { Index = 0, Uuid = "GPU-gb10", Name = "NVIDIA GB10", SharesHostMemory = true }],
        Runtimes = VllmOnly,
    };

    private static NodeCapabilities DiscreteCaps(bool uuidFeature = true) => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "1.1.0",
        Features = uuidFeature ? [.. AgentFeatures.P1, .. AgentFeatures.P2] : [.. AgentFeatures.P1],
        Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
        Gpus =
        [
            new GpuCapability { Index = 0, Uuid = "GPU-a", Name = "NVIDIA GeForce RTX 4060 Ti", MemoryTotalGiB = 16 },
            new GpuCapability { Index = 1, Uuid = "GPU-b", Name = "NVIDIA GeForce RTX 4060 Ti", MemoryTotalGiB = 16 },
            new GpuCapability { Index = 2, Uuid = "GPU-c", Name = "NVIDIA GeForce RTX 3090", MemoryTotalGiB = 24 },
        ],
        Runtimes = VllmOnly,
    };

    private static NodeRecord Node(string id, NodeCapabilities? caps, bool approved = true, bool draining = false, bool revoked = false) => new()
    {
        Id = id,
        Uid = "uid-" + id,
        Address = $"https://{id}:9443",
        Approved = approved,
        Draining = draining,
        Revoked = revoked,
        MemoryReserveGiB = 8,
        LastCapabilities = caps,
    };

    private static ModelDefinition Model(string id, ModelPlacement? placement) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = id,
        MemoryBudgetGiB = 12,
        Placement = placement,
    };

    private sealed class Fixture
    {
        public required JsonModelRegistry Registry { get; init; }
        public required JsonNodeRegistry Nodes { get; init; }
        public required OwnershipStore Ownership { get; init; }
    }

    /// <summary>The composition MN-130 will do: <see cref="PlacementServiceCollectionExtensions.AddPlacementValidation"/> + the registry's validator constructor.</summary>
    private async Task<Fixture> CreateAsync(params NodeRecord[] nodes)
    {
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataDir });
        var nodeRegistry = new JsonNodeRegistry(options, TimeProvider.System);
        foreach (var node in nodes)
        {
            await nodeRegistry.UpsertAsync(node, Ct);
        }

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<MirbadLLMOptions>>(options);
        services.AddSingleton<INodeRegistry>(nodeRegistry);
        services.AddPlacementValidation();
        var provider = services.BuildServiceProvider();
        var registry = new JsonModelRegistry(
            options,
            Options.Create(new RegistryMigrationOptions()),
            provider.GetServices<IModelDefinitionValidator>(),
            Options.Create(new ModelRegistryOptions { SeedPolicy = ModelSeedPolicy.None }));

        _disposables.Add(registry);
        _disposables.Add(nodeRegistry);
        _disposables.Add(provider);
        return new Fixture { Registry = registry, Nodes = nodeRegistry, Ownership = provider.GetRequiredService<OwnershipStore>() };
    }

    private Task<Fixture> CreateFleetAsync() => CreateAsync(
        Node("dgx", UnifiedCaps()),
        Node("pc", DiscreteCaps()),
        Node("old-pc", DiscreteCaps(uuidFeature: false)),
        Node("busy", UnifiedCaps(), draining: true),
        Node("gone", UnifiedCaps(), revoked: true),
        Node("new-pc", DiscreteCaps(), approved: false),
        Node("silent", null));

    private static OwnershipRecord Hold(string modelId, OwnershipKind kind = OwnershipKind.Move, string id = "rec-1", long generation = 0) => new()
    {
        Id = id,
        Kind = kind,
        ModelId = modelId,
        Stage = kind == OwnershipKind.Move ? MoveStages.StoppingSource : "running",
        StartedAt = DateTimeOffset.UnixEpoch,
        PlacementGeneration = generation,
        Description = kind == OwnershipKind.Move ? "a move to pc" : "a tuning run",
    };

    private static async Task<PlacementValidationException> InvalidAsync(Fixture f, ModelDefinition model) =>
        await Assert.ThrowsAsync<PlacementValidationException>(() => f.Registry.UpsertAsync(model, Ct));

    // ------------------------------------------------------------------ today's single DGX

    [Fact]
    public async Task SingleDgx_SavesExactlyAsBefore_GenerationStaysZeroAndOutOfTheFile()
    {
        var f = await CreateAsync(Node("dgx", UnifiedCaps()));

        await f.Registry.UpsertAsync(Model("chat", new ModelPlacement("dgx")), Ct);
        var stored = (await f.Registry.GetAsync("chat", Ct))!;
        await f.Registry.UpsertAsync(stored with { MaxNumSeqs = 4, Tags = ["chat"] }, Ct);         // the editor: on top of the stored definition
        await f.Registry.UpsertAsync(Model("chat", new ModelPlacement("dgx")) with { MaxNumSeqs = 8 }, Ct); // a client sending no generation

        var saved = (await f.Registry.GetAsync("chat", Ct))!;
        Assert.Equal(new ModelPlacement("dgx"), saved.Placement);
        Assert.Equal(0, saved.Placement!.Generation);
        Assert.Equal(8, saved.MaxNumSeqs);
        string file = await File.ReadAllTextAsync(Path.Combine(_dataDir, ModelRegistryDocument.FileName), Ct);
        Assert.DoesNotContain("Generation", file, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await f.Ownership.ListAsync(Ct));
        Assert.False(File.Exists(f.Ownership.FilePath)); // nothing held, nothing written
    }

    [Fact]
    public async Task FirstBoot_BeforeTheBootstrapNodeRecordExists_APlacementOnItIsAccepted()
    {
        var f = await CreateAsync();

        await f.Registry.UpsertAsync(Model("seed", new ModelPlacement("dgx")), Ct);
        var other = await InvalidAsync(f, Model("other", new ModelPlacement("pc")));

        Assert.Equal("seed", (await f.Registry.GetAsync("seed", Ct))!.Id);
        Assert.Equal(PlacementProblemCodes.NodeUnknown, Assert.Single(other.Problems).Code);
    }

    // ------------------------------------------------------------------ validation (§5.1)

    [Fact]
    public async Task EveryValidationError_HasItsOwnCodeAndMessage()
    {
        var f = await CreateFleetAsync();
        var cases = new (ModelDefinition Model, string Code)[]
        {
            (Model("m1", new ModelPlacement("nowhere")), PlacementProblemCodes.NodeUnknown),
            (Model("m2", new ModelPlacement("gone")), PlacementProblemCodes.NodeRevoked),
            (Model("m3", new ModelPlacement("busy")), PlacementProblemCodes.NodeDraining),
            (Model("m4", new ModelPlacement("pc", ["GPU-a", "GPU-zz"])), PlacementProblemCodes.GpuUnknown),
            (Model("m5", new ModelPlacement("pc", ["GPU-a", "GPU-a"])), PlacementProblemCodes.GpuDuplicate),
            (Model("m6", new ModelPlacement("pc", ["GPU-a", "GPU-c"])), PlacementProblemCodes.GpuCapacityMismatch),
            (Model("m7", new ModelPlacement("dgx", ["GPU-gb10"])), PlacementProblemCodes.GpusOnUnifiedNode),
            (Model("m8", new ModelPlacement("pc")), PlacementProblemCodes.GpusRequired),
            (Model("m9", new ModelPlacement("old-pc", ["GPU-a"])), PlacementProblemCodes.RuntimeUnavailable),
            (Model("m10", new ModelPlacement("dgx")) with { Runtime = RuntimeKind.LlamaCpp, LlamaCpp = new LlamaCppSettings { ModelFile = "m.gguf" } },
                PlacementProblemCodes.RuntimeUnavailable),
            (Model("m11", new ModelPlacement("dgx")) with { LlamaCpp = new LlamaCppSettings { ModelFile = "m.gguf" } },
                PlacementProblemCodes.LlamaCppSettingsMismatch),
            (Model("m12", new ModelPlacement("new-pc", ["GPU-a"])), PlacementProblemCodes.NodeUnknown),
        };

        var messages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (model, code) in cases)
        {
            var refusal = await InvalidAsync(f, model);
            var problem = Assert.Single(refusal.Problems);
            Assert.Equal(code, problem.Code);
            Assert.False(string.IsNullOrWhiteSpace(problem.Message));
            Assert.True(messages.Add(problem.Message), $"message reused: {problem.Message}");
            Assert.Null(await f.Registry.GetAsync(model.Id, Ct)); // nothing written
        }

        // The GPU-specific problems name the GPU.
        var unknown = await InvalidAsync(f, Model("m4", new ModelPlacement("pc", ["GPU-zz"])));
        Assert.Equal("GPU-zz", Assert.Single(unknown.Problems).GpuUuid);
        Assert.Contains("GPU-zz", unknown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralProblems_AreAllReported()
    {
        var f = await CreateFleetAsync();

        var refusal = await InvalidAsync(f, Model("m", new ModelPlacement("pc", ["GPU-zz", "GPU-zz"])) with
        {
            LlamaCpp = new LlamaCppSettings { ModelFile = "m.gguf" },
        });

        Assert.Equal(
            [PlacementProblemCodes.LlamaCppSettingsMismatch, PlacementProblemCodes.GpuDuplicate, PlacementProblemCodes.GpuUnknown, PlacementProblemCodes.GpuUnknown],
            refusal.Problems.Select(p => p.Code));
    }

    [Fact]
    public async Task ValidPlacements_AreStored_WithUuidsAsIdentityAndIndicesRefreshed()
    {
        var f = await CreateFleetAsync();

        await f.Registry.UpsertAsync(Model("tp", new ModelPlacement("pc", ["GPU-b", "GPU-a"], [7, 7])), Ct);  // stale indices
        await f.Registry.UpsertAsync(Model("by-index", new ModelPlacement("pc", GpuIndices: [2])), Ct);      // picked by index
        await f.Registry.UpsertAsync(Model("unified", new ModelPlacement("dgx")), Ct);
        await f.Registry.UpsertAsync(Model("offline", new ModelPlacement("silent", ["GPU-anything"])), Ct);   // never reported: hardware unchecked

        Assert.Equal(new ModelPlacement("pc", ["GPU-b", "GPU-a"], [1, 0]), (await f.Registry.GetAsync("tp", Ct))!.Placement);
        Assert.Equal(new ModelPlacement("pc", ["GPU-c"], [2]), (await f.Registry.GetAsync("by-index", Ct))!.Placement);
        Assert.Equal(new ModelPlacement("dgx"), (await f.Registry.GetAsync("unified", Ct))!.Placement);
        Assert.Equal(["GPU-anything"], (await f.Registry.GetAsync("offline", Ct))!.Placement!.GpuUuids);
    }

    [Fact]
    public async Task Draining_RefusesOnlyNewPlacements_ModelsAlreadyThereCanBeEdited()
    {
        var f = await CreateFleetAsync();
        await f.Registry.UpsertAsync(Model("there", new ModelPlacement("dgx")), Ct);
        await f.Nodes.UpdateAsync("dgx", n => n with { Draining = true }, Ct);

        var stored = (await f.Registry.GetAsync("there", Ct))!;
        await f.Registry.UpsertAsync(stored with { MaxNumSeqs = 2 }, Ct);
        var refusal = await InvalidAsync(f, Model("new", new ModelPlacement("dgx")));

        Assert.Equal(2, (await f.Registry.GetAsync("there", Ct))!.MaxNumSeqs);
        Assert.Equal(PlacementProblemCodes.NodeDraining, Assert.Single(refusal.Problems).Code);
    }

    [Fact]
    public async Task AnUnchangedPlacement_IsNotRevalidatedAgainstDriftedCapabilities()
    {
        var f = await CreateFleetAsync();
        await f.Registry.UpsertAsync(Model("m", new ModelPlacement("pc", ["GPU-c"])), Ct);
        await f.Nodes.UpdateAsync("pc", n => n with { LastCapabilities = DiscreteCaps() with { Gpus = [] } }, Ct); // the card was removed

        var stored = (await f.Registry.GetAsync("m", Ct))!;
        await f.Registry.UpsertAsync(stored with { MaxNumSeqs = 3 }, Ct); // a settings edit still saves (launch reports gpu_missing)
        var regpu = await InvalidAsync(f, (await f.Registry.GetAsync("m", Ct))! with
        {
            Placement = stored.Placement! with { GpuUuids = ["GPU-a"] },
        });

        Assert.Equal(3, (await f.Registry.GetAsync("m", Ct))!.MaxNumSeqs);
        Assert.Equal(PlacementProblemCodes.GpuUnknown, Assert.Single(regpu.Problems).Code);
    }

    // ------------------------------------------------------------------ generation (D75)

    [Fact]
    public async Task Generation_RisesOnEveryPlacementChange_AndStaleSavesAreRefused()
    {
        var f = await CreateFleetAsync();
        await f.Registry.UpsertAsync(Model("m", new ModelPlacement("pc", ["GPU-a"])), Ct);
        var g0 = (await f.Registry.GetAsync("m", Ct))!;
        Assert.Equal(0, g0.Placement!.Generation);

        await f.Registry.UpsertAsync(g0 with { Placement = g0.Placement with { GpuUuids = ["GPU-b"] } }, Ct);
        var g1 = (await f.Registry.GetAsync("m", Ct))!;
        await f.Registry.UpsertAsync(g1 with { MaxNumSeqs = 9 }, Ct); // same location: generation kept
        var kept = (await f.Registry.GetAsync("m", Ct))!;

        Assert.Equal(1, g1.Placement!.Generation);
        Assert.Equal(1, kept.Placement!.Generation);

        // A save based on the old read (generation 0), and one from a client that sends no generation at all.
        var stale = await Assert.ThrowsAsync<PlacementConflictException>(() => f.Registry.UpsertAsync(g0 with { MaxNumSeqs = 1 }, Ct));
        await Assert.ThrowsAsync<PlacementConflictException>(() =>
            f.Registry.UpsertAsync(Model("m", new ModelPlacement("pc", ["GPU-b"])), Ct));
        // A generation this master never issued.
        await Assert.ThrowsAsync<PlacementConflictException>(() =>
            f.Registry.UpsertAsync(kept with { Placement = kept.Placement with { Generation = 5 } }, Ct));

        Assert.Contains("changed meanwhile", stale.Message, StringComparison.Ordinal);
        var final = (await f.Registry.GetAsync("m", Ct))!;
        Assert.Equal(new ModelPlacement("pc", ["GPU-b"], [1]), final.Placement);
        Assert.Equal(1, final.Placement!.Generation);
        Assert.Equal(9, final.MaxNumSeqs);
    }

    [Fact]
    public async Task Unplacing_IsAllowed_ButPlacingAnUnplacedModelNeedsAnOwnershipHolder()
    {
        var f = await CreateFleetAsync();
        await f.Registry.UpsertAsync(Model("m", new ModelPlacement("pc", ["GPU-a"])), Ct);
        var placed = (await f.Registry.GetAsync("m", Ct))!;
        await f.Registry.UpsertAsync(placed with { Placement = placed.Placement! with { GpuUuids = ["GPU-b"] } }, Ct); // generation 1
        var g1 = (await f.Registry.GetAsync("m", Ct))!;

        await f.Registry.UpsertAsync(g1 with { Placement = null }, Ct); // revoke with unplaceModels (MN-102): no holder needed
        Assert.Null((await f.Registry.GetAsync("m", Ct))!.Placement);

        // A stale read-merge-write from before the unplace would re-place it: refused.
        await Assert.ThrowsAsync<PlacementConflictException>(() => f.Registry.UpsertAsync(g1, Ct));

        // A move holds the model and places it; the generation keeps rising across the unplace.
        Assert.True((await f.Ownership.TryAcquireAsync(Hold("m", generation: 1), Ct)).Acquired);
        using (ModelWriteScope.AsOwner("rec-1"))
        {
            await f.Registry.UpsertAsync(g1 with { Placement = new ModelPlacement("dgx", Generation: 1) }, Ct);
        }

        var moved = (await f.Registry.GetAsync("m", Ct))!;
        Assert.Equal(new ModelPlacement("dgx"), moved.Placement);
        Assert.Equal(2, moved.Placement!.Generation);
    }

    // ------------------------------------------------------------------ ownership (§6.7)

    [Fact]
    public async Task AHeldModel_RefusesDeleteAndPlacementEditsByOthers_ButNotItsHolder()
    {
        var f = await CreateFleetAsync();
        await f.Registry.UpsertAsync(Model("m", new ModelPlacement("pc", ["GPU-a"])), Ct);
        Assert.True((await f.Ownership.TryAcquireAsync(Hold("m", OwnershipKind.Job, "job-1"), Ct)).Acquired);
        var stored = (await f.Registry.GetAsync("m", Ct))!;

        var delete = await Assert.ThrowsAsync<ModelBusyException>(() => f.Registry.DeleteAsync("m", Ct));
        var regpu = await Assert.ThrowsAsync<ModelBusyException>(() =>
            f.Registry.UpsertAsync(stored with { Placement = stored.Placement! with { GpuUuids = ["GPU-b"] } }, Ct));
        var unplace = await Assert.ThrowsAsync<ModelBusyException>(() => f.Registry.UpsertAsync(stored with { Placement = null }, Ct));
        await f.Registry.UpsertAsync(stored with { MaxNumSeqs = 5 }, Ct); // settings edits are not placement edits

        using (ModelWriteScope.AsOwner("someone-else"))
        {
            await Assert.ThrowsAsync<ModelBusyException>(() => f.Registry.DeleteAsync("m", Ct));
        }

        Assert.Equal("job-1", delete.Holder!.Id);
        Assert.Contains("a tuning run", delete.Message, StringComparison.Ordinal);
        Assert.Equal("m", regpu.ModelId);
        Assert.NotNull(unplace.Holder);
        Assert.Equal(5, (await f.Registry.GetAsync("m", Ct))!.MaxNumSeqs);

        using (ModelWriteScope.AsOwner("job-1"))
        {
            var current = (await f.Registry.GetAsync("m", Ct))!;
            await f.Registry.UpsertAsync(current with { Placement = current.Placement! with { GpuUuids = ["GPU-b"] } }, Ct);
            Assert.True(await f.Registry.DeleteAsync("m", Ct));
        }
    }

    // ------------------------------------------------------------------ ownership store

    [Fact]
    public async Task OwnershipStore_KeepsRecordsAcrossRestarts_OnePerModel()
    {
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataDir });
        using (var store = new OwnershipStore(options))
        {
            Assert.True((await store.TryAcquireAsync(Hold("a"), Ct)).Acquired);
            Assert.True((await store.TryAcquireAsync(Hold("b", OwnershipKind.Job, "rec-2"), Ct)).Acquired);

            var refused = await store.TryAcquireAsync(Hold("a", OwnershipKind.Job, "rec-3"), Ct);
            var retried = await store.TryAcquireAsync(Hold("a"), Ct); // same id: idempotent
            Assert.False(refused.Acquired);
            Assert.Equal("rec-1", refused.Holder!.Id);
            Assert.True(retried.Acquired);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryAcquireAsync(Hold("c", id: "rec-1"), Ct));

            var updated = await store.UpdateAsync("rec-1", r => r with { Stage = MoveStages.SourceStopped, OutstandingOperationIds = ["op-1"] }, Ct);
            Assert.Equal(MoveStages.SourceStopped, updated!.Stage);
            Assert.Null(await store.UpdateAsync("missing", r => r, Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync("rec-1", r => r with { ModelId = "b" }, Ct));
            Assert.True(await store.ReleaseAsync("rec-2", Ct));
            Assert.False(await store.ReleaseAsync("rec-2", Ct));
        }

        using var reopened = new OwnershipStore(options);
        var record = Assert.Single(await reopened.ListAsync(Ct));
        Assert.Equal(("rec-1", "a", MoveStages.SourceStopped), (record.Id, record.ModelId, record.Stage));
        Assert.Equal(["op-1"], record.OutstandingOperationIds);
        Assert.Null(await reopened.GetForModelAsync("b", Ct));

        var json = JsonNode.Parse(await File.ReadAllTextAsync(reopened.FilePath, Ct))!;
        Assert.Equal(1, (int)json["schemaVersion"]!);
        Assert.Equal("move", (string)json["records"]![0]!["kind"]!);
        Assert.False(File.Exists(reopened.FilePath + ".tmp"));
    }

    [Fact]
    public async Task OwnershipStore_ACrashBetweenTheTempWriteAndTheRename_LosesNothing()
    {
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataDir });
        string file;
        byte[] before;
        using (var store = new OwnershipStore(options))
        {
            Assert.True((await store.TryAcquireAsync(Hold("a"), Ct)).Acquired);
            file = store.FilePath;
            before = await File.ReadAllBytesAsync(file, Ct);

            store.BeforeReplaceForTests = _ => throw new IOException("power loss");
            await Assert.ThrowsAsync<IOException>(() => store.TryAcquireAsync(Hold("b", id: "rec-2"), Ct));
            await Assert.ThrowsAsync<IOException>(() => store.ReleaseAsync("rec-1", Ct));

            // The failed writes changed nothing, in memory or on disk; the temp file is what a crash leaves behind.
            Assert.Equal(["a"], (await store.ListAsync(Ct)).Select(r => r.ModelId));
            Assert.Equal(before, await File.ReadAllBytesAsync(file, Ct));
            Assert.True(File.Exists(file + ".tmp"));
        }

        // A truncated temp file from the crash does not matter after a restart.
        await File.WriteAllTextAsync(file + ".tmp", "{\"schemaVersion\":1,\"rec", Ct);
        using var restarted = new OwnershipStore(options);
        Assert.Equal("rec-1", (await restarted.GetForModelAsync("a", Ct))!.Id);
        Assert.True((await restarted.TryAcquireAsync(Hold("b", id: "rec-2"), Ct)).Acquired);

        using var again = new OwnershipStore(options);
        Assert.Equal(["a", "b"], (await again.ListAsync(Ct)).Select(r => r.ModelId).Order());
    }

    [Fact]
    public async Task OwnershipStore_RefusesAFileItCannotTrust_AndLeavesItUntouched()
    {
        var options = Options.Create(new MirbadLLMOptions { DataPath = _dataDir });
        Directory.CreateDirectory(_dataDir);
        string file = Path.Combine(_dataDir, OwnershipDocument.FileName);
        var cases = new[]
        {
            "{ not json",
            "{\"schemaVersion\": 9, \"records\": []}",
            "{\"schemaVersion\": 1, \"records\": [" +
                "{\"id\":\"r1\",\"kind\":\"job\",\"modelId\":\"m\",\"stage\":\"s\",\"startedAt\":\"2026-10-06T00:00:00Z\"}," +
                "{\"id\":\"r2\",\"kind\":\"move\",\"modelId\":\"m\",\"stage\":\"s\",\"startedAt\":\"2026-10-06T00:00:00Z\"}]}",
        };

        foreach (string content in cases)
        {
            await File.WriteAllTextAsync(file, content, Ct);
            using var store = new OwnershipStore(options);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ListAsync(Ct));
            await Assert.ThrowsAsync<InvalidDataException>(() => store.TryAcquireAsync(Hold("x"), Ct));
            Assert.Equal(content, await File.ReadAllTextAsync(file, Ct));
        }
    }

    [Fact]
    public async Task OwnershipStore_ConcurrentAcquires_ExactlyOneWins()
    {
        using var store = new OwnershipStore(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }));

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            Task.Run(() => store.TryAcquireAsync(Hold("m", OwnershipKind.Job, $"rec-{i}"), Ct), Ct)));

        var winner = Assert.Single(results, r => r.Acquired);
        string winnerId = Assert.Single(await store.ListAsync(Ct)).Id;
        Assert.All(results.Where(r => !r.Acquired), r => Assert.Equal(winnerId, r.Holder!.Id));
        Assert.Null(winner.Holder);
    }

    // ------------------------------------------------------------------ admin API

    private sealed class ApiHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required Fixture Fixture { get; init; }
        public required FakeTelemetry Telemetry { get; init; }
        public required FakeStatusCache Status { get; init; }
        public required ManualTime Time { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(Ct);
            await App.DisposeAsync();
        }
    }

    private async Task<ApiHost> StartApiAsync(Func<IModelRegistry, IModelRegistry>? wrap = null, IAuditLog? audit = null)
    {
        var fixture = await CreateFleetAsync();
        var time = new ManualTime(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var telemetry = new FakeTelemetry();
        var status = new FakeStatusCache();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<IModelRegistry>(wrap is null ? fixture.Registry : wrap(fixture.Registry));
        builder.Services.AddSingleton<INodeRegistry>(fixture.Nodes);
        builder.Services.AddSingleton<INodeTelemetry>(telemetry);
        builder.Services.AddSingleton<INodeStatusCache>(status);
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        builder.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(128, 64)));
        builder.Services.AddSingleton<IModelMetricsReader>(new FakeModelMetricsReader());
        builder.Services.AddSingleton<IRouterStats>(new FakeRouterStats([]));
        builder.Services.AddSingleton<IHuggingFaceDownloader>(new FakeHuggingFaceDownloader());
        builder.Services.AddSingleton<IHuggingFaceRepoInspector>(new FakeHuggingFaceRepoInspector());
        builder.Services.AddNodeSourcing(Path.Combine(_dataDir, "no-such-models-root"));
        if (audit is not null)
        {
            builder.Services.AddSingleton(audit);
        }

        var app = builder.Build();
        if (audit is null)
        {
            app.MapAdminApi();
        }
        else
        {
            // As Program.cs maps it: the pre-P2 model routes inside the audited group (MN-116).
            Microsoft.AspNetCore.Http.EndpointFilterExtensions.AddEndpointFilter(app.MapGroup(""), new MirbadLLM.Orchestration.Audit.LegacyApiAuditFilter()).MapAdminApi();
        }

        await app.StartAsync(Ct);
        return new ApiHost { App = app, Client = app.GetTestClient(), Fixture = fixture, Telemetry = telemetry, Status = status, Time = time };
    }

    private static Task<HttpResponseMessage> PutJsonAsync(HttpClient client, string id, string json) =>
        client.PutAsync($"/api/models/{id}", new StringContent(json, Encoding.UTF8, "application/json"), Ct);

    private static string Body(ModelDefinition model, Action<JsonObject>? edit = null)
    {
        var node = JsonSerializer.SerializeToNode(model, WebJson)!.AsObject();
        edit?.Invoke(node);
        return node.ToJsonString();
    }

    private static async Task<JsonObject> ProblemAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();

    [Fact]
    public async Task Api_RefusalsByTheValidators_AreAuditedOnce_AsRefused_WithTheirCode()
    {
        // W7 review MN-126 item 2b: PUT/DELETE /api/models are audited by the legacy filter only (the handlers write no
        // line of their own), and a 409 placement_changed / model_busy is a refusal with its code.
        var audit = new ListAuditLog();
        await using var host = await StartApiAsync(audit: audit);
        var registry = host.Fixture.Registry;
        await registry.UpsertAsync(Model("m", new ModelPlacement("pc", ["GPU-a"])), Ct);
        var g0 = (await registry.GetAsync("m", Ct))!;
        await registry.UpsertAsync(g0 with { Placement = g0.Placement! with { GpuUuids = ["GPU-b"] } }, Ct); // generation 1

        using var stale = await PutJsonAsync(host.Client, "m", Body(g0 with { MaxNumSeqs = 2 }));
        Assert.True((await host.Fixture.Ownership.TryAcquireAsync(Hold("m", OwnershipKind.Job, "job-1"), Ct)).Acquired);
        using var delete = await host.Client.DeleteAsync("/api/models/m", Ct);
        using var ok = await PutJsonAsync(host.Client, "m", Body((await registry.GetAsync("m", Ct))! with { MaxNumSeqs = 3 }));

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Conflict, HttpStatusCode.OK), (stale.StatusCode, delete.StatusCode, ok.StatusCode));
        Assert.Contains("send placement.generation", (string)(await ProblemAsync(stale))["detail"]!, StringComparison.Ordinal);
        var lines = audit.Records;
        Assert.Equal(3, lines.Count);
        Assert.Equal((AuditActions.ModelUpdate, "m", AuditResults.Refused, ErrorCodes.PlacementChanged), (lines[0].Action, lines[0].ModelId, lines[0].Result, lines[0].ErrorCode));
        Assert.Equal((AuditActions.ModelDelete, "m", AuditResults.Refused, ErrorCodes.ModelBusy), (lines[1].Action, lines[1].ModelId, lines[1].Result, lines[1].ErrorCode));
        Assert.Equal((AuditActions.ModelUpdate, AuditResults.Succeeded, null), (lines[2].Action, lines[2].Result, lines[2].ErrorCode));
    }

    private sealed class ListAuditLog : IAuditLog
    {
        private readonly List<AuditRecord> _records = [];

        public IReadOnlyList<AuditRecord> Records
        {
            get
            {
                lock (_records)
                {
                    return [.. _records];
                }
            }
        }

        public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            lock (_records)
            {
                _records.Add(record);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(Records);
    }

    [Fact]
    public async Task Api_MapsEachRefusal_ToItsCodeAndStatus()
    {
        await using var host = await StartApiAsync();
        var registry = host.Fixture.Registry;
        await registry.UpsertAsync(Model("m", new ModelPlacement("pc", ["GPU-a"])), Ct);
        var g0 = (await registry.GetAsync("m", Ct))!;
        await registry.UpsertAsync(g0 with { Placement = g0.Placement! with { GpuUuids = ["GPU-b"] } }, Ct); // generation 1
        var g1 = (await registry.GetAsync("m", Ct))!;

        using var invalid = await PutJsonAsync(host.Client, "m", Body(g0 with { Placement = new ModelPlacement("pc", ["GPU-a", "GPU-c"], Generation: 1) }));
        using var stale = await PutJsonAsync(host.Client, "m", Body(g0 with { MaxNumSeqs = 2 }));
        // The current placement, but sent without its generation (a client that does not know the field).
        using var noGeneration = await PutJsonAsync(host.Client, "m", Body(g1, o => Assert.True(o["placement"]!.AsObject().Remove("generation"))));
        using var create = await host.Client.PostAsJsonAsync("/api/models", Model("new", new ModelPlacement("gone")), WebJson, Ct);
        Assert.True((await host.Fixture.Ownership.TryAcquireAsync(Hold("m", OwnershipKind.Job, "job-1"), Ct)).Acquired);
        using var delete = await host.Client.DeleteAsync("/api/models/m", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var invalidBody = await ProblemAsync(invalid);
        Assert.Equal(ErrorCodes.PlacementInvalid, (string)invalidBody["code"]!);
        var problem = Assert.Single(invalidBody["problems"]!.AsArray())!;
        Assert.Equal(PlacementProblemCodes.GpuCapacityMismatch, (string)problem["code"]!);
        Assert.Equal("GPU-c", (string)problem["gpuUuid"]!);

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(ErrorCodes.PlacementChanged, (string)(await ProblemAsync(stale))["code"]!);
        Assert.Equal(HttpStatusCode.Conflict, noGeneration.StatusCode);
        Assert.Equal(ErrorCodes.PlacementChanged, (string)(await ProblemAsync(noGeneration))["code"]!);

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(PlacementProblemCodes.NodeRevoked, (string)(await ProblemAsync(create))["problems"]![0]!["code"]!);

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        var busy = await ProblemAsync(delete);
        Assert.Equal(ErrorCodes.ModelBusy, (string)busy["code"]!);
        Assert.Equal("job-1", (string)busy["holder"]!["id"]!);
        Assert.Equal("a tuning run", (string)busy["holder"]!["description"]!);

        var saved = (await registry.GetAsync("m", Ct))!;
        Assert.Equal(["GPU-b"], saved.Placement!.GpuUuids);
        Assert.Equal(1, saved.Placement.Generation);
    }

    [Fact]
    public async Task Api_OldClientsAndTheEditor_SaveAsToday_OnTheDgx()
    {
        await using var host = await StartApiAsync();
        using var created = await host.Client.PostAsJsonAsync("/api/models", Model("chat", null), WebJson, Ct); // goes to... several nodes exist
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.Equal(ErrorCodes.PlacementRequired, (string)(await ProblemAsync(created))["code"]!);

        using var placed = await host.Client.PostAsJsonAsync("/api/models", Model("chat", new ModelPlacement("dgx")), WebJson, Ct);
        Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        var stored = (await host.Fixture.Registry.GetAsync("chat", Ct))!;

        // A pre-multi-node client: no placement, no generation.
        string oldClient = Body(stored with { MaxNumSeqs = 6 }, o =>
        {
            foreach (string field in MultinodeFields)
            {
                o.Remove(field);
            }
        });
        using var put = await PutJsonAsync(host.Client, "chat", oldClient);
        // The editor: the whole stored definition, placement {nodeId: dgx} without a generation (it is 0).
        using var editor = await PutJsonAsync(host.Client, "chat", Body(stored with { MaxNumSeqs = 7 }));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, editor.StatusCode);
        var saved = (await host.Fixture.Registry.GetAsync("chat", Ct))!;
        Assert.Equal(7, saved.MaxNumSeqs);
        Assert.Equal(new ModelPlacement("dgx"), saved.Placement);
        Assert.Equal(0, saved.Placement!.Generation);
        Assert.DoesNotContain("generation", await editor.Content.ReadAsStringAsync(Ct), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Api_TwoStalePutsRacing_ExactlyOneWins_TheOtherGetsPlacementChanged()
    {
        await using var host = await StartApiAsync();
        var registry = host.Fixture.Registry;
        await registry.UpsertAsync(Model("m", new ModelPlacement("pc", ["GPU-a"])), Ct);

        for (int round = 0; round < 10; round++)
        {
            var read = (await registry.GetAsync("m", Ct))!; // both clients read the same generation
            string toA = round % 2 == 0 ? "GPU-b" : "GPU-a";
            string toAB = round % 2 == 0 ? "GPU-a" : "GPU-b";
            var bodies = new[]
            {
                Body(read with { Placement = read.Placement! with { GpuUuids = [toA] }, MaxNumSeqs = 100 + round }),
                Body(read with { Placement = read.Placement! with { GpuUuids = [toAB, toA] }, MaxNumSeqs = 200 + round }),
            };

            using var gate = new ManualResetEventSlim(false);
            var tasks = bodies.Select(body => Task.Run(async () =>
            {
                gate.Wait(Ct);
                return await PutJsonAsync(host.Client, "m", body);
            }, Ct)).ToArray();
            gate.Set();
            var responses = await Task.WhenAll(tasks);

            try
            {
                var statuses = responses.Select(r => r.StatusCode).ToList();
                Assert.Single(statuses, s => s == HttpStatusCode.OK);
                var loser = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
                Assert.Equal(ErrorCodes.PlacementChanged, (string)(await ProblemAsync(loser))["code"]!);

                var winner = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
                var echoed = (await winner.Content.ReadFromJsonAsync<ModelDefinition>(WebJson, Ct))!;
                var saved = (await registry.GetAsync("m", Ct))!;
                Assert.Equal(read.Placement!.Generation + 1, saved.Placement!.Generation);
                Assert.Equal(echoed.Placement, saved.Placement);
                Assert.Equal(echoed.MaxNumSeqs, saved.MaxNumSeqs);
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
    }

    /// <summary>Lets a test run an action right after the PUT handler's read, i.e. between its read and its write.</summary>
    private sealed class RaceAfterRead(IModelRegistry inner) : IModelRegistry
    {
        private Func<Task>? _race;

        public void RunOnceAfterNextRead(Func<Task> race) => _race = race;

        public Task<IReadOnlyList<ModelDefinition>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public async Task<ModelDefinition?> GetAsync(string id, CancellationToken ct = default)
        {
            var model = await inner.GetAsync(id, ct);
            if (Interlocked.Exchange(ref _race, null) is { } race)
            {
                await race();
            }

            return model;
        }

        public Task UpsertAsync(ModelDefinition model, CancellationToken ct = default) => inner.UpsertAsync(model, ct);

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => inner.DeleteAsync(id, ct);
    }

    [Fact]
    public async Task Api_AnOldClientPut_RacingAMove_IsRefused_AndTheMoveStays()
    {
        RaceAfterRead? race = null;
        await using var host = await StartApiAsync(inner => race = new RaceAfterRead(inner));
        var registry = host.Fixture.Registry;
        var ownership = host.Fixture.Ownership;
        await registry.UpsertAsync(Model("m", new ModelPlacement("dgx")), Ct);
        var before = (await registry.GetAsync("m", Ct))!;

        async Task MoveAsync(ModelPlacement? target, string recordId)
        {
            Assert.True((await ownership.TryAcquireAsync(Hold("m", id: recordId, generation: 0), Ct)).Acquired);
            var current = (await registry.GetAsync("m", Ct))!;
            using (ModelWriteScope.AsOwner(recordId))
            {
                await registry.UpsertAsync(current with { Placement = target }, Ct);
            }

            Assert.True(await ownership.ReleaseAsync(recordId, Ct));
        }

        string oldClient = Body(before with { MaxNumSeqs = 3 }, o => o.Remove("placement"));

        // 1) Moved between the handler's read and its write: the merged (old) placement would revert the move.
        race!.RunOnceAfterNextRead(() => MoveAsync(new ModelPlacement("pc", ["GPU-a"]), "move-1"));
        using var afterMove = await PutJsonAsync(host.Client, "m", oldClient);

        Assert.Equal(HttpStatusCode.Conflict, afterMove.StatusCode);
        Assert.Equal(ErrorCodes.PlacementChanged, (string)(await ProblemAsync(afterMove))["code"]!);
        var moved = (await registry.GetAsync("m", Ct))!;
        Assert.Equal("pc", moved.Placement!.NodeId);
        Assert.Equal(1, moved.Placement.Generation);
        Assert.NotEqual(3, moved.MaxNumSeqs);

        // 2) Unplaced meanwhile (revoke): the merged placement would re-place it.
        race.RunOnceAfterNextRead(async () => await registry.UpsertAsync((await registry.GetAsync("m", Ct))! with { Placement = null }, Ct));
        using var afterUnplace = await PutJsonAsync(host.Client, "m", Body(moved with { MaxNumSeqs = 3 }, o => o.Remove("placement")));
        Assert.Equal(HttpStatusCode.Conflict, afterUnplace.StatusCode);
        Assert.Null((await registry.GetAsync("m", Ct))!.Placement);

        // 3) Read while unplaced, placed meanwhile by a move: the merged null would unplace it again. The generation alone
        //    cannot see this one (the body has no placement); the handler's expectation does.
        var unplaced = (await registry.GetAsync("m", Ct))!;
        race.RunOnceAfterNextRead(() => MoveAsync(new ModelPlacement("dgx", Generation: 1), "move-2"));
        using var afterPlace = await PutJsonAsync(host.Client, "m", Body(unplaced with { MaxNumSeqs = 3 }, o => o.Remove("placement")));
        Assert.Equal(HttpStatusCode.Conflict, afterPlace.StatusCode);
        var replaced = (await registry.GetAsync("m", Ct))!;
        Assert.Equal(new ModelPlacement("dgx"), replaced.Placement);
        Assert.Equal(2, replaced.Placement!.Generation);

        // 4) Deleted meanwhile: the PUT must not re-create it.
        race.RunOnceAfterNextRead(async () => Assert.True(await registry.DeleteAsync("m", Ct)));
        using var afterDelete = await PutJsonAsync(host.Client, "m", Body(replaced with { MaxNumSeqs = 3 }, o => o.Remove("placement")));
        Assert.Equal(HttpStatusCode.Conflict, afterDelete.StatusCode);
        Assert.Null(await registry.GetAsync("m", Ct));

        // Without a race the same body saves.
        await registry.UpsertAsync(Model("m", new ModelPlacement("dgx")), Ct);
        using var quiet = await PutJsonAsync(host.Client, "m", Body(Model("m", new ModelPlacement("dgx")) with { MaxNumSeqs = 3 }, o => o.Remove("placement")));
        Assert.Equal(HttpStatusCode.OK, quiet.StatusCode);
    }

    [Fact]
    public async Task Api_APutRacingAHeldMove_GetsModelBusy()
    {
        RaceAfterRead? race = null;
        await using var host = await StartApiAsync(inner => race = new RaceAfterRead(inner));
        var registry = host.Fixture.Registry;
        await registry.UpsertAsync(Model("m", new ModelPlacement("pc", ["GPU-a"])), Ct);
        var before = (await registry.GetAsync("m", Ct))!;
        Assert.True((await host.Fixture.Ownership.TryAcquireAsync(Hold("m", id: "move-1"), Ct)).Acquired);

        // The move commits but still holds the model (starting-target): the stale placement is a placement edit by another owner.
        race!.RunOnceAfterNextRead(async () =>
        {
            using (ModelWriteScope.AsOwner("move-1"))
            {
                await registry.UpsertAsync(before with { Placement = new ModelPlacement("dgx") }, Ct);
            }
        });
        using var response = await PutJsonAsync(host.Client, "m", Body(before with { MaxNumSeqs = 3 }, o => o.Remove("placement")));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ErrorCodes.ModelBusy, (string)(await ProblemAsync(response))["code"]!);
        Assert.Equal("dgx", (await registry.GetAsync("m", Ct))!.Placement!.NodeId);
    }

    // ------------------------------------------------------------------ fit preview (§6.2)

    private static NodeTelemetryReading Reading(string nodeId, NodeMemorySnapshot memory, DateTimeOffset at) =>
        new(nodeId, "boot-1", new NodeSample { Seq = 1, At = at, Memory = memory }, at);

    private static NodeLiveStatus Live(string nodeId, NodeState state, DateTimeOffset contact, NodeMemorySnapshot? memory = null) => new()
    {
        NodeId = nodeId,
        NodeUid = "uid-" + nodeId,
        State = state,
        LastContact = contact,
        Memory = memory,
    };

    private static async Task<NodeFitResult> FitAsync(ApiHost host, string url)
    {
        using var response = await host.Client.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NodeFitResult>(WebJson, Ct))!;
    }

    [Fact]
    public async Task Fit_Unified_FromFreshTelemetry_WithTheReserveAndStartingModels()
    {
        await using var host = await StartApiAsync();
        var registry = host.Fixture.Registry;
        await registry.UpsertAsync(Model("big", new ModelPlacement("dgx")) with { MemoryBudgetGiB = 36 }, Ct);
        await registry.UpsertAsync(Model("other", new ModelPlacement("dgx")) with { MemoryBudgetGiB = 10 }, Ct);
        var now = host.Time.GetUtcNow();
        host.Telemetry.Set(Reading("dgx", new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 121.7, HostAvailableGiB = 49.2 }, now.AddSeconds(-2)));
        host.Status.SetNode(Live("dgx", NodeState.Online, now));

        var fits = await FitAsync(host, "/api/nodes/dgx/fit?modelId=big");
        host.Status.SetRuntime(new NodeModelRuntime { ModelId = "other", NodeId = "dgx", State = ModelState.Starting });
        var withStarting = await FitAsync(host, "/api/nodes/dgx/fit?modelId=big");

        Assert.True(fits.Fits);
        Assert.Equal(MemoryModel.Unified, fits.Model);
        Assert.Equal(36, fits.NeedGiB);
        Assert.Equal(41.2, fits.FreeGiB!.Value, 6);
        Assert.Equal(8, fits.ReserveGiB);
        Assert.Equal("Fits on dgx: needs 36 GiB, 41.2 GiB free after the 8 GiB reserve.", fits.Message);
        Assert.Equal(now.AddSeconds(-2), fits.MeasuredAt);

        Assert.False(withStarting.Fits);
        Assert.Equal(31.2, withStarting.FreeGiB!.Value, 6);
        Assert.Contains("10 GiB held by 1 starting model(s)", withStarting.Message, StringComparison.Ordinal);

        // Running there already: a restart reuses its own memory.
        host.Status.SetRuntime(new NodeModelRuntime { ModelId = "big", NodeId = "dgx", State = ModelState.Ready });
        var running = await FitAsync(host, "/api/nodes/dgx/fit?modelId=big");
        Assert.True(running.Fits);
        Assert.Equal(67.2, running.FreeGiB!.Value, 6);
    }

    [Fact]
    public async Task Fit_IsUnknown_WithoutFreshTelemetry_OrWhenTheNodeIsNotServing()
    {
        await using var host = await StartApiAsync();
        await host.Fixture.Registry.UpsertAsync(Model("m", new ModelPlacement("dgx")), Ct);
        var now = host.Time.GetUtcNow();
        var memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 121.7, HostAvailableGiB = 100 };

        host.Telemetry.Set(Reading("dgx", memory, now.AddSeconds(-6)));
        var stale = await FitAsync(host, "/api/nodes/dgx/fit?modelId=m");
        host.Status.SetNode(Live("dgx", NodeState.Online, now.AddSeconds(-1), memory)); // the cache stands in while serving
        var fromCache = await FitAsync(host, "/api/nodes/dgx/fit?modelId=m");
        host.Telemetry.Set(Reading("dgx", memory, now));
        host.Status.SetNode(Live("dgx", NodeState.Offline, now.AddSeconds(-20)));
        var offline = await FitAsync(host, "/api/nodes/dgx/fit?modelId=m");

        Assert.Null(stale.Fits);
        Assert.Null(stale.FreeGiB);
        Assert.Contains("has not reported memory in the last 5 s", stale.Message, StringComparison.Ordinal);
        Assert.True(fromCache.Fits);
        Assert.Null(offline.Fits);
        Assert.Contains("offline", offline.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fit_Discrete_PerGpuWithReserves_HostPart_AndCandidates()
    {
        await using var host = await StartApiAsync();
        await host.Fixture.Nodes.UpdateAsync("pc", n => n with { GpuReserveGiB = new Dictionary<string, double> { ["GPU-b"] = 1 } }, Ct);
        await host.Fixture.Registry.UpsertAsync(Model("tp", new ModelPlacement("pc", ["GPU-a", "GPU-b"])) with { MemoryBudgetGiB = 24 }, Ct);
        var now = host.Time.GetUtcNow();
        host.Telemetry.Set(Reading("pc", new NodeMemorySnapshot
        {
            Model = MemoryModel.Discrete,
            HostTotalGiB = 64,
            HostAvailableGiB = 30,
            Gpus =
            [
                new GpuMemorySnapshot(0, "GPU-a", 16, 1, 15),
                new GpuMemorySnapshot(1, "GPU-b", 16, 4.5, 11.5),
                new GpuMemorySnapshot(2, "GPU-c", 24, 0.5, 23.5),
            ],
        }, now));
        host.Status.SetNode(Live("pc", NodeState.Online, now));

        var placed = await FitAsync(host, "/api/nodes/pc/fit?modelId=tp");                                 // its own GPUs
        var chosen = await FitAsync(host, "/api/nodes/pc/fit?modelId=tp&gpu=GPU-a&gpu=GPU-c");          // another selection
        using var post = await host.Client.PostAsJsonAsync("/api/nodes/pc/fit",
            new NodeFitRequest { Definition = Model("draft", null) with { MemoryBudgetGiB = 20, HostMemoryBudgetGiB = 25 } }, WebJson, Ct);
        var candidates = (await post.Content.ReadFromJsonAsync<NodeFitResult>(WebJson, Ct))!;

        Assert.Equal(MemoryModel.Discrete, placed.Model);
        Assert.Equal(["GPU-a", "GPU-b"], placed.Gpus.Select(g => g.Uuid));
        Assert.Equal([12.0, 12.0], placed.Gpus.Select(g => g.NeedGiB));
        Assert.Equal([15.0, 10.5], placed.Gpus.Select(g => g.FreeGiB!.Value));
        Assert.Equal([0.0, 1.0], placed.Gpus.Select(g => g.ReserveGiB));
        Assert.Equal([true, false], placed.Gpus.Select(g => g.Fits!.Value));
        Assert.False(placed.Fits);
        Assert.Equal(10.5, placed.FreeGiB);
        Assert.Equal(new HostFit(10, 22, true, true), placed.Host); // estimate 4 GiB × 2 + 2, after the 8 GiB reserve
        Assert.Contains("GPU 1: 10.5 GiB free after the 1 GiB reserve", placed.Message, StringComparison.Ordinal);

        Assert.True(chosen.Fits);
        Assert.Equal([0, 2], chosen.Gpus.Select(g => g.Index!.Value));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Equal(3, candidates.Gpus.Count);                     // no selection: every GPU as a single-GPU candidate
        Assert.Equal([20.0, 20.0, 20.0], candidates.Gpus.Select(g => g.NeedGiB));
        Assert.Equal([false, false, true], candidates.Gpus.Select(g => g.Fits!.Value));
        Assert.Equal(23.5, candidates.FreeGiB);
        Assert.False(candidates.Host!.Estimated);
        Assert.False(candidates.Fits);                              // GPU 2 fits, the declared host part (25 > 22) does not
    }

    /// <summary>
    /// W8 review (MN-128, found by MN-122): a running model gets its own budget added back, but a reading from before its
    /// instance took the memory already counts it as free; the free figure never exceeds the GPU's (or host's) capacity
    /// after the reserve.
    /// </summary>
    [Fact]
    public async Task Fit_NeverShowsMoreFreeThanTheMemoryHas_ForAModelRunningThere()
    {
        await using var host = await StartApiAsync();
        await host.Fixture.Registry.UpsertAsync(Model("one", new ModelPlacement("pc", ["GPU-b"])) with { MemoryBudgetGiB = 4 }, Ct);
        await host.Fixture.Registry.UpsertAsync(Model("big", new ModelPlacement("dgx")) with { MemoryBudgetGiB = 36 }, Ct);
        var now = host.Time.GetUtcNow();
        // Read before "one" took its 4 GiB on GPU-b (15.5 of 16 GiB free), and before "big" took its 36 GiB on dgx.
        host.Telemetry.Set(Reading("pc", new NodeMemorySnapshot
        {
            Model = MemoryModel.Discrete,
            HostTotalGiB = 64,
            HostAvailableGiB = 30,
            Gpus = [new GpuMemorySnapshot(0, "GPU-a", 16, 1, 15), new GpuMemorySnapshot(1, "GPU-b", 16, 0.5, 15.5)],
        }, now));
        host.Telemetry.Set(Reading("dgx", new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 121.7, HostAvailableGiB = 110 }, now));
        host.Status.SetNode(Live("pc", NodeState.Online, now));
        host.Status.SetNode(Live("dgx", NodeState.Online, now));
        host.Status.SetRuntime(new NodeModelRuntime { ModelId = "one", NodeId = "pc", State = ModelState.Ready, GpuUuids = ["GPU-b"] });
        host.Status.SetRuntime(new NodeModelRuntime { ModelId = "big", NodeId = "dgx", State = ModelState.Ready });

        var discrete = await FitAsync(host, "/api/nodes/pc/fit?modelId=one");
        var unified = await FitAsync(host, "/api/nodes/dgx/fit?modelId=big");

        var gpu = Assert.Single(discrete.Gpus);
        double gpuTotal = (await host.Fixture.Nodes.GetAsync("pc", Ct))!.LastCapabilities!.Gpus.Single(g => g.Uuid == "GPU-b").MemoryTotalGiB!.Value;
        Assert.Equal(gpuTotal - gpu.ReserveGiB, gpu.FreeGiB!.Value, 6); // not 15.5 + 4
        Assert.True(gpu.FreeGiB <= gpuTotal);
        Assert.Equal(121.7 - 8, unified.FreeGiB!.Value, 6);             // not 110 − 8 + 36
    }

    [Fact]
    public async Task Fit_RefusesBadRequests()
    {
        await using var host = await StartApiAsync();

        using var noNode = await host.Client.GetAsync("/api/nodes/nowhere/fit?modelId=m", Ct);
        using var noModel = await host.Client.GetAsync("/api/nodes/dgx/fit?modelId=missing", Ct);
        using var nothing = await host.Client.GetAsync("/api/nodes/dgx/fit", Ct);

        Assert.Equal(HttpStatusCode.NotFound, noNode.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noModel.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
    }

    // ------------------------------------------------------------------ fakes

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeTelemetry : INodeTelemetry
    {
        private readonly Dictionary<string, NodeTelemetryReading> _readings = new(StringComparer.Ordinal);

        public TimeSpan MaxSampleAge => TimeSpan.FromSeconds(15);

        public void Set(NodeTelemetryReading reading)
        {
            lock (_readings)
            {
                _readings[reading.NodeId] = reading;
            }
        }

        public NodeTelemetryReading? GetLatest(string nodeId)
        {
            lock (_readings)
            {
                return _readings.GetValueOrDefault(nodeId);
            }
        }
    }

    private sealed class FakeStatusCache : INodeStatusCache
    {
        private readonly Dictionary<string, NodeLiveStatus> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, NodeModelRuntime> _runtimes = new(StringComparer.Ordinal);

        public long Version { get; private set; }

        public void SetNode(NodeLiveStatus node)
        {
            lock (_nodes)
            {
                _nodes[node.NodeId] = node;
                Version++;
            }
        }

        public void SetRuntime(NodeModelRuntime runtime)
        {
            lock (_nodes)
            {
                _runtimes[runtime.ModelId] = runtime;
                Version++;
            }
        }

        public IReadOnlyList<NodeLiveStatus> ListNodes()
        {
            lock (_nodes)
            {
                return [.. _nodes.Values];
            }
        }

        public NodeLiveStatus? GetNode(string nodeId)
        {
            lock (_nodes)
            {
                return _nodes.GetValueOrDefault(nodeId);
            }
        }

        public NodeModelRuntime? GetRuntime(string modelId)
        {
            lock (_nodes)
            {
                return _runtimes.GetValueOrDefault(modelId);
            }
        }

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null)
        {
            lock (_nodes)
            {
                return [.. _runtimes.Values.Where(r => nodeId is null || r.NodeId == nodeId)];
            }
        }

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
