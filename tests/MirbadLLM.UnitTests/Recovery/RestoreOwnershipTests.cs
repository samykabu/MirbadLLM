using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Recovery;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Recovery;

/// <summary>
/// Configuration import against a registry composed WITH the MN-108 validators (W7 review MN-126 item 1): the
/// <see cref="OwnershipValidator"/> and <see cref="PlacementValidator"/> over a real <see cref="OwnershipStore"/> and node
/// registry, as <c>AddPlacementValidation</c> composes them. The restore holds a restore record per model and writes as its
/// holder, so placing an unplaced model and keeping or bumping the stored generation work; a model another record holds is
/// skipped with the holder named; every record is released, and one left by a crash is released at boot.
/// </summary>
public sealed class RestoreOwnershipTests : IDisposable
{
    private const string DgxUid = "0123456789abcdef0123456789abcdef";
    private const string PcUid = "fedcba9876543210fedcba9876543210";

    private static readonly RestoreJobOptions FastOptions = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(1),
        StartRetryInterval = TimeSpan.FromMilliseconds(5),
        StartRetryTimeout = TimeSpan.FromSeconds(10),
    };

    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-mn126-own-" + Guid.NewGuid().ToString("N"));
    private readonly JsonNodeRegistry _nodes;
    private readonly OwnershipStore _ownership;
    private readonly JsonModelRegistry _registry;
    private readonly JsonApiKeyStore _keys;
    private readonly FakeCheckpointFolders _folders = new();
    private readonly List<string> _events = [];

    public RestoreOwnershipTests()
    {
        Directory.CreateDirectory(_dataPath);
        var data = Options.Create(new MirbadLLMOptions { DataPath = _dataPath });
        _nodes = new JsonNodeRegistry(data, TimeProvider.System);
        _ownership = new OwnershipStore(data);
        _registry = new JsonModelRegistry(
            data,
            Options.Create(new RegistryMigrationOptions()),
            [new OwnershipValidator(_ownership), new PlacementValidator(_nodes, _ownership)],
            Options.Create(new ModelRegistryOptions { SeedPolicy = ModelSeedPolicy.None }));
        _keys = new JsonApiKeyStore(data, TimeProvider.System, NullLogger<JsonApiKeyStore>.Instance);
        _nodes.UpsertAsync(Node("dgx", DgxUid)).GetAwaiter().GetResult();
        _nodes.UpsertAsync(Node("pc", PcUid)).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _registry.Dispose();
        _ownership.Dispose();
        _nodes.Dispose();
        _keys.Dispose();
        if (Directory.Exists(_dataPath))
        {
            Directory.Delete(_dataPath, recursive: true);
        }
    }

    private static NodeRecord Node(string id, string uid) => new()
    {
        Id = id,
        Uid = uid,
        Address = $"https://{id}.lan:9443",
        Approved = true,
        CertSpkiSha256 = "pin-" + id,
        CertNotAfter = DateTimeOffset.UtcNow.AddDays(300),
    };

    private static ModelDefinition Model(string id, string? nodeId, long generation = 0, bool autoStart = false) => new()
    {
        Id = id,
        ServedName = id.ToUpperInvariant(),
        ModelPath = id + "-ckpt",
        MemoryBudgetGiB = 12,
        AutoStart = autoStart,
        Sources = [new ModelSource("org/" + id, id + "-ckpt")],
        Placement = nodeId is null ? null : new ModelPlacement(nodeId) { Generation = generation },
    };

    private static BackupDocument Backup(params ModelDefinition[] models) => new()
    {
        SchemaVersion = BackupDocument.MultiNodeSchemaVersion,
        CreatedAt = DateTimeOffset.UtcNow,
        HostName = "old-master",
        Models = models,
        Nodes =
        [
            new BackupNode { Id = "dgx", Uid = DgxUid, Address = "https://dgx.lan:9443", Approved = true },
            new BackupNode { Id = "pc", Uid = PcUid, Address = "https://pc.lan:9443", Approved = true },
        ],
    };

    private RestoreJobService Service(IOwnershipStore? ownership, IHuggingFaceDownloader? downloader = null, IModelRuntime? runtime = null) =>
        new(_registry, _keys, runtime ?? new ScriptedRuntime(_events), downloader ?? new SequencedDownloader(_folders), _folders, FastOptions,
            TimeProvider.System, NullLogger<RestoreJobService>.Instance, hostServices: null, bootstrapNodeId: "dgx", _nodes,
            new SingleNodeRestoreResources(_folders, downloader ?? new SequencedDownloader(_folders)), audit: null, ownership);

    private static async Task<RestoreJobSnapshot> RunAsync(RestoreJobService service, BackupDocument backup, params string[] ids)
    {
        service.Start(new RestoreRequest(backup, null, ids, ReplaceExisting: true, ImportApiKeys: false, ImportHostServices: false));
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return service.Current!;
    }

    private static RestoreModelProgress Row(RestoreJobSnapshot job, string id) => job.Models.Single(m => m.Id == id);

    /// <summary>A model to be unplaced here: placed by the restore (its holder), at generation 0 + 1.</summary>
    private async Task SeedUnplacedAsync(string id)
    {
        await _registry.UpsertAsync(Model(id, "dgx"));
        await _registry.UpsertAsync(Model(id, null)); // unplace: allowed for a non-holder while no record holds the model
        Assert.Null((await _registry.GetAsync(id))!.Placement);
    }

    [Fact]
    public async Task Import_PlacesAModelUnplacedHere_AsTheHolderOfItsRestoreRecord_AndReleasesIt()
    {
        await SeedUnplacedAsync("alpha");
        _folders.Add("alpha-ckpt");

        var job = await RunAsync(Service(_ownership), Backup(Model("alpha", "dgx")), "alpha");

        Assert.Equal(RestoreJobState.Completed, job.State);
        Assert.Equal(RestoreModelState.Ready, Row(job, "alpha").State);
        var stored = await _registry.GetAsync("alpha");
        Assert.Equal("dgx", stored!.Placement!.NodeId);
        Assert.Equal(1, stored.Placement.Generation); // placing an unplaced model is a placement change (D75)
        Assert.Empty(await _ownership.ListAsync());
    }

    [Fact]
    public async Task Import_WithoutAnOwnershipStore_CannotPlaceAModelUnplacedHere_AndSaysWhy()
    {
        // The composition before this review: validators composed, restore without the store. The write is refused (409
        // placement_changed) and reported per model; the other model is still restored.
        await SeedUnplacedAsync("alpha");
        _folders.Add("alpha-ckpt");
        _folders.Add("beta-ckpt");

        var job = await RunAsync(Service(ownership: null), Backup(Model("alpha", "dgx"), Model("beta", "dgx")), "alpha", "beta");

        Assert.Equal(RestoreModelState.Failed, Row(job, "alpha").State);
        Assert.Contains("unplaced", Row(job, "alpha").Detail, StringComparison.Ordinal);
        Assert.Equal(RestoreModelState.Ready, Row(job, "beta").State);
        Assert.Null((await _registry.GetAsync("alpha"))!.Placement);
    }

    [Fact]
    public async Task Import_KeepsTheStoredGeneration_OnTheSameNode_AndBumpsItOnAnotherNode_WhateverTheBackupCarries()
    {
        // Stored generation 2 here (moved to pc and back); the backup was made at generation 0 and 7.
        await _registry.UpsertAsync(Model("same", "dgx"));
        await _registry.UpsertAsync(Model("moved", "dgx"));
        await MoveThereAndBackAsync("same");
        await MoveThereAndBackAsync("moved");
        _folders.Add("same-ckpt");
        _folders.Add("moved-ckpt");

        var job = await RunAsync(Service(_ownership), Backup(Model("same", "dgx", generation: 0), Model("moved", "pc", generation: 7)), "same", "moved");

        Assert.Equal(RestoreJobState.Completed, job.State);
        var same = (await _registry.GetAsync("same"))!.Placement!;
        var moved = (await _registry.GetAsync("moved"))!.Placement!;
        Assert.Equal(("dgx", 2L), (same.NodeId, same.Generation));
        Assert.Equal(("pc", 3L), (moved.NodeId, moved.Generation));
        Assert.Empty(await _ownership.ListAsync());
    }

    [Fact]
    public async Task Import_SkipsAModelAJobHolds_NamingTheHolder_AndLeavesTheJobsRecord()
    {
        await _registry.UpsertAsync(Model("tuned", "dgx") with { MaxNumSeqs = 4 });
        var jobRecord = new OwnershipRecord
        {
            Id = "job-1",
            Kind = OwnershipKind.Job,
            ModelId = "tuned",
            Stage = "tuning",
            StartedAt = DateTimeOffset.UtcNow,
            Description = "a tuning run on node dgx",
        };
        Assert.True((await _ownership.TryAcquireAsync(jobRecord)).Acquired);
        _folders.Add("tuned-ckpt");
        _folders.Add("other-ckpt");

        var job = await RunAsync(Service(_ownership), Backup(Model("tuned", "pc"), Model("other", "dgx")), "tuned", "other");

        var row = Row(job, "tuned");
        Assert.Equal(RestoreModelState.Skipped, row.State);
        Assert.Contains("a tuning run on node dgx holds the model", row.Detail, StringComparison.Ordinal);
        Assert.Equal(RestoreModelState.Ready, Row(job, "other").State);
        var stored = (await _registry.GetAsync("tuned"))!;
        Assert.Equal(("dgx", 4), (stored.Placement!.NodeId, stored.MaxNumSeqs)); // untouched
        Assert.Equal(["job-1"], (await _ownership.ListAsync()).Select(r => r.Id));
    }

    [Fact]
    public async Task WhileItsCheckpointDownloads_TheModelIsHeld_SoAnotherWriterCannotChangeItsPlacement()
    {
        await _registry.UpsertAsync(Model("alpha", "dgx"));
        var downloader = new GatedDownloader(_folders);
        var service = Service(_ownership, downloader);
        service.Start(new RestoreRequest(Backup(Model("alpha", "dgx")), null, ["alpha"], true, false, false));
        await downloader.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var record = Assert.Single(await _ownership.ListAsync());
        Assert.Equal((OwnershipKind.Restore, "alpha"), (record.Kind, record.ModelId));
        Assert.StartsWith(RestoreJobService.RecordIdPrefix, record.Id, StringComparison.Ordinal);
        var busy = await Assert.ThrowsAsync<ModelBusyException>(() => _registry.UpsertAsync(Model("alpha", null)));
        Assert.Contains("a restore", busy.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ModelBusyException>(() => _registry.DeleteAsync("alpha"));
        await _registry.UpsertAsync((await _registry.GetAsync("alpha"))! with { MaxNumSeqs = 8 }); // settings stay editable

        downloader.Release.TrySetResult();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(RestoreModelState.Ready, Row(service.Current!, "alpha").State);
        Assert.Empty(await _ownership.ListAsync());
    }

    [Fact]
    public async Task AnAutoStartModel_IsHeldUntilItsStartFinished_ThenReleased()
    {
        _folders.Add("alpha-ckpt");
        var runtime = new ProbingRuntime(_ownership);
        var job = await RunAsync(Service(_ownership, runtime: runtime), Backup(Model("alpha", "dgx", autoStart: true)), "alpha");

        Assert.Equal(RestoreModelState.Started, Row(job, "alpha").State);
        Assert.Equal(OwnershipKind.Restore, runtime.HolderAtStart?.Kind);
        Assert.Empty(await _ownership.ListAsync());
    }

    [Fact]
    public async Task ACancelledRestore_ReleasesEveryRecordItHeld()
    {
        var downloader = new GatedDownloader(_folders);
        var service = Service(_ownership, downloader);
        service.Start(new RestoreRequest(Backup(Model("alpha", "dgx"), Model("beta", "dgx")), null, ["alpha", "beta"], true, false, false));
        await downloader.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, (await _ownership.ListAsync()).Count); // both definitions written and held; alpha downloading

        await service.CancelAsync();

        Assert.Equal(RestoreJobState.Cancelled, service.Current!.State);
        Assert.Empty(await _ownership.ListAsync());
    }

    [Fact]
    public async Task ARestoreRecordLeftByACrash_IsReleasedAtBoot_OtherKindsAreKept()
    {
        var now = DateTimeOffset.UtcNow;
        await _registry.UpsertAsync(Model("alpha", "dgx"));
        await _registry.UpsertAsync(Model("beta", "dgx"));
        await _ownership.TryAcquireAsync(new OwnershipRecord
        {
            Id = RestoreJobService.RecordIdPrefix + "deadbeef-alpha", Kind = OwnershipKind.Restore, ModelId = "alpha", Stage = "restoring", StartedAt = now,
        });
        await _ownership.TryAcquireAsync(new OwnershipRecord
        {
            Id = "job-7", Kind = OwnershipKind.Job, ModelId = "beta", Stage = "benchmark", StartedAt = now,
        });

        // A new process: the store reads ownership.json again, the hosted sweep runs at start.
        using var reopened = new OwnershipStore(Options.Create(new MirbadLLMOptions { DataPath = _dataPath }));
        var recovery = new RestoreOwnershipRecovery(Service(reopened), NullLogger<RestoreOwnershipRecovery>.Instance);
        await recovery.StartAsync(CancellationToken.None);

        Assert.Equal(["job-7"], (await reopened.ListAsync()).Select(r => r.Id));
    }

    /// <summary>Two placement commits by a holder (a move to pc and one back): generation 2, on dgx.</summary>
    private async Task MoveThereAndBackAsync(string id)
    {
        foreach (string target in new[] { "pc", "dgx" })
        {
            var current = (await _registry.GetAsync(id))!;
            string recordId = "move-" + id + "-" + target;
            Assert.True((await _ownership.TryAcquireAsync(new OwnershipRecord
            {
                Id = recordId, Kind = OwnershipKind.Move, ModelId = id, Stage = MoveStages.SourceStopped, StartedAt = DateTimeOffset.UtcNow,
            })).Acquired);
            using (ModelWriteScope.AsOwner(recordId))
            {
                await _registry.UpsertAsync(current with { Placement = current.Placement! with { NodeId = target } });
            }

            await _ownership.ReleaseAsync(recordId);
        }
    }

    /// <summary>A downloader whose first download waits for <see cref="Release"/>.</summary>
    private sealed class GatedDownloader(FakeCheckpointFolders folders) : IHuggingFaceDownloader
    {
        private readonly SequencedDownloader _inner = new(folders);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
        {
            Started.TrySetResult();
            return _inner.StartAsync(request, ct);
        }

        public Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);

        public async Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default)
        {
            await Release.Task.WaitAsync(ct);
            return await _inner.GetAsync(folder, ct);
        }

        public Task<bool> CancelAsync(string folder, CancellationToken ct = default) => _inner.CancelAsync(folder, ct);
    }

    /// <summary>A runtime that records who held the model when its start was called.</summary>
    private sealed class ProbingRuntime(IOwnershipStore ownership) : IModelRuntime
    {
        public OwnershipRecord? HolderAtStart { get; private set; }

        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelStatus>>([]);

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) => Task.FromResult(new ModelStatus(id, ModelState.Stopped));

        public async Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
        {
            HolderAtStart = await ownership.GetForModelAsync(id, ct);
            return new ModelStatus(id, ModelState.Starting);
        }

        public Task StopAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
