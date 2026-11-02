using System.Runtime.CompilerServices;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Recovery;

/// <summary>
/// Restore modes and recovery mode (design §9.4, MN-113): configuration import with the uid rule and target picking, and
/// fleet disaster recovery on an empty master in recovery mode — all over the real stores in temporary data directories,
/// with backups made, serialized, parsed and decrypted by the real <see cref="BackupService"/>.
/// </summary>
public sealed partial class RestoreModesTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const string DgxUid = "0123456789abcdef0123456789abcdef";
    private const string PcUid = "fedcba9876543210fedcba9876543210";

    private static readonly RestoreJobOptions FastOptions = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(1),
        StartRetryInterval = TimeSpan.FromMilliseconds(5),
        StartRetryTimeout = TimeSpan.FromSeconds(10),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-mn113-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private T Own<T>(T value)
        where T : IDisposable
    {
        _disposables.Add(value);
        return value;
    }

    private string Dir(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static IOptions<MirbadLLMOptions> Data(string path) => Options.Create(new MirbadLLMOptions { DataPath = path });

    private JsonModelRegistry Registry(string dataPath, ModelSeedPolicy seed = ModelSeedPolicy.None) =>
        Own(new JsonModelRegistry(Data(dataPath), Options.Create(new RegistryMigrationOptions()), validators: null,
            Options.Create(new ModelRegistryOptions { SeedPolicy = seed })));

    private JsonNodeRegistry Nodes(string dataPath) => Own(new JsonNodeRegistry(Data(dataPath), TimeProvider.System));

    private JsonApiKeyStore Keys(string dataPath) => Own(new JsonApiKeyStore(Data(dataPath), TimeProvider.System, NullLogger<JsonApiKeyStore>.Instance));

    private static ModelDefinition Model(string id, string nodeId, bool autoStart = false, IReadOnlyList<string>? gpus = null) => new()
    {
        Id = id,
        ServedName = id.ToUpperInvariant(),
        ModelPath = id + "-ckpt",
        MemoryBudgetGiB = 12,
        AutoStart = autoStart,
        Sources = [new ModelSource("org/" + id, id + "-ckpt")],
        Placement = new ModelPlacement(nodeId, gpus, gpus is null ? null : [0]),
    };

    private static NodeRecord Node(string id, string uid, bool approved = true, string? pin = "pin-x", DateTimeOffset? notAfter = null) => new()
    {
        Id = id,
        Uid = uid,
        Address = $"https://{id}.lan:9443",
        Approved = approved,
        CertSpkiSha256 = pin,
        CertNotAfter = notAfter ?? DateTimeOffset.UtcNow.AddDays(300),
    };

    private sealed class FixedIdentity(string instanceId, long epoch) : IMasterIdentity
    {
        public string InstanceId => instanceId;

        public long Epoch => epoch;

        public string BootId => "boot";

        public MasterTriple Current => new(InstanceId, Epoch, BootId);

        public bool TakeoverRequested => false;

        public IReadOnlyList<TakeoverRecord> Takeovers => [];

        public Task<MasterTriple> RaiseEpochToAsync(long e, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>The old fleet: dgx and pc-office (one expired certificate, one revoked laptop), three models, a key, the Services list, a CA.</summary>
    private sealed record SourceFleet(BackupService Service, CertificateAuthority Ca, ApiKeyCreated Key);

    private async Task<SourceFleet> SourceFleetAsync(bool includeCa = true, string name = "source")
    {
        string source = Dir(name);
        var models = Registry(source);
        await models.UpsertAsync(Model("alpha", "dgx", autoStart: true));
        await models.UpsertAsync(Model("beta", "pc-office", gpus: ["GPU-1111"]));
        await models.UpsertAsync(Model("gamma", "pc-office") with { Placement = null, Id = "gamma" });

        var nodes = Nodes(source);
        await nodes.UpsertAsync(Node("dgx", DgxUid) with { Labels = ["arm64"], DefaultVllmImageDigest = "sha256:" + new string('a', 64) });
        await nodes.UpsertAsync(Node("pc-office", PcUid, notAfter: DateTimeOffset.UtcNow.AddDays(-3)) with
        {
            Draining = true,
            GpuReserveGiB = new Dictionary<string, double> { ["GPU-1111"] = 1.5 },
        });
        await nodes.UpsertAsync(Node("laptop", "11112222333344445555666677778888") with { Revoked = true });

        var keys = Keys(source);
        var key = await keys.CreateAsync("laptop", ApiKeyScope.Inference);

        var services = Own(new HostServicesStore(Path.Combine(source, "host-services.json")));
        await services.GetContainersAsync([new ManagedContainerOptions { Name = "openviking", Protected = true }]);

        var ca = Own(CertificateAuthority.Create());
        var service = new BackupService(models, keys, Options.Create(new BackupOptions { HostName = "old-dgx", IncludeCa = includeCa }),
            TimeProvider.System, services, master: new FixedIdentity("old-master-instance", 41), nodes: nodes, certificateAuthority: ca);
        return new SourceFleet(service, ca, key);
    }

    /// <summary>A backup made by the real service, through the file format (serialize, parse, decrypt).</summary>
    private static async Task<(BackupDocument Backup, BackupSecrets? Secrets)> RoundTripAsync(BackupService service, string? passphrase = Passphrase)
    {
        var parsed = BackupService.Parse(BackupService.Serialize(await service.CreateAsync(passphrase)));
        return (parsed, BackupService.DecryptSecrets(parsed, passphrase));
    }

    // ---------------------------------------------------------------- configuration import

    private sealed record ImportHarness(
        RestoreJobService Service, RestorePlanner Planner, JsonModelRegistry Registry, JsonNodeRegistry Nodes,
        PerNodeResources Resources, NodeScriptedRuntime Runtime, RecordingAuditLog Audit, HostServicesStore Services, string DataPath);

    private ImportHarness Import(params NodeRecord[] localNodes)
    {
        string target = Dir("target-" + Guid.NewGuid().ToString("N")[..6]);
        var registry = Registry(target);
        var nodes = Nodes(target);
        foreach (var node in localNodes)
        {
            nodes.UpsertAsync(node).GetAwaiter().GetResult();
        }

        var resources = new PerNodeResources();
        var runtime = new NodeScriptedRuntime();
        var audit = new RecordingAuditLog();
        var services = Own(new HostServicesStore(Path.Combine(target, "host-services.json")));
        var fallback = new FakeCheckpointFolders();
        var service = new RestoreJobService(registry, Keys(target), runtime, new SequencedDownloader(fallback), fallback, FastOptions,
            TimeProvider.System, NullLogger<RestoreJobService>.Instance, services, bootstrapNodeId: "dgx", nodes, resources, audit);
        var planner = new RestorePlanner(registry, Keys(target), fallback, repoInspector: null, "dgx", nodes, resources);
        return new ImportHarness(service, planner, registry, nodes, resources, runtime, audit, services, target);
    }

    private static async Task<RestoreJobSnapshot> RunAsync(RestoreJobService service, RestoreRequest request)
    {
        service.Start(request);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return service.Current!;
    }

    private static RestoreModelProgress Row(RestoreJobSnapshot job, string id) => job.Models.Single(m => m.Id == id);

    [Fact]
    public async Task ConfigurationImport_V2_SameUidKeepsTheNode_SlugOnlyAndUnmatchedNeedATarget()
    {
        var fleet = await SourceFleetAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        // Here: the same DGX (same uid) registered as "spark", and a different machine that reuses the name pc-office.
        var h = Import(Node("spark", DgxUid), Node("pc-office", "99999999999999999999999999999999"));

        var preview = await h.Planner.PreviewAsync(backup, secrets);

        var alpha = preview.Models.Single(m => m.Model.Id == "alpha");
        Assert.Equal(RestorePlacementMatch.SameUid, alpha.Placement!.Match);
        Assert.Equal("spark", alpha.Placement.DefaultTargetNodeId);
        Assert.Equal("spark", alpha.Model.Placement!.NodeId);
        Assert.False(alpha.Placement.NeedsTarget);

        var beta = preview.Models.Single(m => m.Model.Id == "beta");
        Assert.Equal(RestorePlacementMatch.SlugOnly, beta.Placement!.Match); // same slug, other uid: not the same machine
        Assert.True(beta.Placement.NeedsTarget);
        Assert.Null(beta.Model.Placement);

        Assert.Equal(RestorePlacementMatch.Unplaced, preview.Models.Single(m => m.Model.Id == "gamma").Placement!.Match);
        Assert.Equal(["pc-office", "spark"], preview.TargetNodes.Select(n => n.Id));
        Assert.False(preview.AllPlacementsMatched);
        Assert.Equal(1, preview.ApiKeysNew);

        // No target picked for beta: it is restored unplaced; alpha keeps its machine (now "spark") and auto-starts there.
        var job = await RunAsync(h.Service, new RestoreRequest(backup, secrets, ["alpha", "beta", "gamma"], true, true, true));

        Assert.Equal(RestoreJobState.Completed, job.State);
        Assert.Equal("spark", (await h.Registry.GetAsync("alpha"))!.Placement!.NodeId);
        Assert.Null((await h.Registry.GetAsync("beta"))!.Placement);
        Assert.Equal(RestoreModelState.Unplaced, Row(job, "beta").State);
        Assert.Equal(RestoreModelState.Unplaced, Row(job, "gamma").State);
        Assert.Equal(RestoreModelState.Started, Row(job, "alpha").State);
        Assert.Equal(["spark:alpha-ckpt"], h.Resources.DownloadLog);
        Assert.Equal(["alpha"], h.Runtime.Started);

        // Never node trust: the local registry is exactly what it was (no laptop, no pins or uids from the backup).
        var local = await h.Nodes.ListAsync();
        Assert.Equal(["pc-office", "spark"], local.Select(n => n.Id).Order(StringComparer.Ordinal));
        Assert.Equal("99999999999999999999999999999999", local.Single(n => n.Id == "pc-office").Uid);
        Assert.False(local.Single(n => n.Id == "pc-office").Draining);
        Assert.False(Directory.Exists(MasterPkiOptions.DirectoryOf(h.DataPath)));
        Assert.False(File.Exists(Path.Combine(h.DataPath, MasterStateDocument.FileName)));

        // Services list of the uid-matched node, and one audit line without secrets.
        Assert.Contains(job.Notes, n => n.Contains("1 managed container restored", StringComparison.Ordinal));
        var line = Assert.Single(h.Audit.Records);
        Assert.Equal(AuditActions.Restore, line.Action);
        Assert.Equal(AuditResults.Succeeded, line.Result);
        Assert.DoesNotContain(fleet.Key.FullKey, JsonSerializer.Serialize(line), StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, JsonSerializer.Serialize(line), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationImport_TargetsPlaceModels_DownloadOnTheTargetNode_AndRefuseUnknownNodes()
    {
        var fleet = await SourceFleetAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service, passphrase: null);
        Assert.Null(secrets);
        var h = Import(Node("dgx", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Node("pc-new", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));

        var job = await RunAsync(h.Service, new RestoreRequest(backup, null, ["alpha", "beta", "gamma"], true, false, false,
            Targets: [new RestoreTarget("beta", "pc-new", ["GPU-2222"]), new RestoreTarget("alpha", "nowhere"), new RestoreTarget("gamma", "dgx")]));

        var beta = (await h.Registry.GetAsync("beta"))!.Placement!;
        Assert.Equal("pc-new", beta.NodeId);
        Assert.Equal(["GPU-2222"], beta.GpuUuids);
        Assert.Equal("dgx", (await h.Registry.GetAsync("gamma"))!.Placement!.NodeId);
        Assert.Equal(RestoreModelState.Failed, Row(job, "alpha").State);
        Assert.Contains("'nowhere' is not registered", Row(job, "alpha").Detail, StringComparison.Ordinal);
        Assert.Null(await h.Registry.GetAsync("alpha"));
        Assert.Equal(["dgx:gamma-ckpt", "pc-new:beta-ckpt"], h.Resources.DownloadLog.Order(StringComparer.Ordinal));
    }

    private static GpuCapability Card(int index, double totalGiB, string name = "NVIDIA GeForce RTX 5060 Ti") =>
        new() { Index = index, Uuid = $"GPU-00000000-0000-0000-0000-00000000000{index}", Name = name, MemoryTotalGiB = totalGiB };

    private static NodeRecord DiscreteNode(string id, string uid, params GpuCapability[] gpus) => Node(id, uid) with
    {
        LastCapabilities = new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "1.0.0",
            Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
            Arch = "amd64",
            Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
            Gpus = gpus,
        },
    };

    [Fact]
    public void DefaultGpus_TakeTheLargestEqualCards_LowestIndicesFirst_OrNoneWithoutEnoughEqualCards()
    {
        // MN-124 (D105): the Restore tab's target picker sends no GPUs; a discrete target gets a default choice.
        GpuCapability[] gpus = [Card(0, 16), Card(1, 24, "NVIDIA GeForce RTX 4090"), Card(2, 16)];

        Assert.Equal([1], RestorePlacements.DefaultGpus(gpus, 1)!.Select(g => g.Index));
        Assert.Equal([0, 2], RestorePlacements.DefaultGpus(gpus, 2)!.Select(g => g.Index));
        Assert.Null(RestorePlacements.DefaultGpus(gpus, 3));
        Assert.Null(RestorePlacements.DefaultGpus([], 1));
    }

    [Fact]
    public void Resolve_ATargetOnADiscreteNodeWithoutGpus_PicksTheBackupsGpuCount_OrRestoresUnplacedWithANote()
    {
        var backup = new BackupDocument { SchemaVersion = BackupDocument.MultiNodeSchemaVersion, Nodes = [] };
        var tp = Model("tp", "old-pc", gpus: ["GPU-a", "GPU-b"]);
        var single = Model("one", "dgx");
        var pc = DiscreteNode("pc", PcUid, Card(0, 16), Card(1, 16));
        var odd = DiscreteNode("odd", DgxUid, Card(0, 16), Card(1, 24));

        var both = RestorePlacements.Resolve(backup, tp, [pc], new RestoreTarget("tp", "pc"), "dgx");
        Assert.Null(both.Error);
        Assert.True(both.Notable);
        Assert.Equal(["GPU-00000000-0000-0000-0000-000000000000", "GPU-00000000-0000-0000-0000-000000000001"], both.Placement!.GpuUuids);
        Assert.Equal([0, 1], both.Placement.GpuIndices);

        var one = RestorePlacements.Resolve(backup, single, [odd], new RestoreTarget("one", "odd"), "dgx");
        Assert.Equal([1], one.Placement!.GpuIndices); // the 24 GiB card

        var none = RestorePlacements.Resolve(backup, tp, [odd], new RestoreTarget("tp", "odd"), "dgx");
        Assert.Null(none.Error);
        Assert.Null(none.Placement);
        Assert.True(none.Notable);
        Assert.Contains("no 2 GPUs of equal memory", none.Detail, StringComparison.Ordinal);

        // GPUs chosen with the target are kept as sent.
        var chosen = RestorePlacements.Resolve(backup, single, [pc], new RestoreTarget("one", "pc", ["GPU-00000000-0000-0000-0000-000000000001"]), "dgx");
        Assert.False(chosen.Notable);
        Assert.Equal(["GPU-00000000-0000-0000-0000-000000000001"], chosen.Placement!.GpuUuids);
        Assert.True(RestorePlacements.TargetChoices([pc, Node("dgx", DgxUid)]).Single(n => n.Id == "pc").Discrete);
        Assert.False(RestorePlacements.TargetChoices([pc, Node("dgx", DgxUid)]).Single(n => n.Id == "dgx").Discrete);
    }

    [Fact]
    public async Task ConfigurationImport_V1_PlacesEveryModelOnTheBootstrapNode()
    {
        // A version-1 file (single box): no placements, no nodes; the restore puts every model on the bootstrap node.
        var v1 = BackupService.Parse(JsonSerializer.SerializeToUtf8Bytes(new BackupDocument
        {
            SchemaVersion = BackupDocument.CurrentSchemaVersion,
            HostName = "old-box",
            Models = [Model("solo", "ignored") with { Placement = null }],
        }, BackupDocument.JsonOptions));
        var h = Import(Node("dgx", "cccccccccccccccccccccccccccccccc"));

        var preview = await h.Planner.PreviewAsync(v1, null);
        Assert.Equal("dgx", preview.Models[0].Placement!.DefaultTargetNodeId);
        Assert.False(preview.Models[0].Placement!.NeedsTarget);

        var job = await RunAsync(h.Service, new RestoreRequest(v1, null, ["solo"], true, false, false));

        Assert.Equal(RestoreModelState.Ready, Row(job, "solo").State);
        Assert.Equal("dgx", (await h.Registry.GetAsync("solo"))!.Placement!.NodeId);
        Assert.Equal(["dgx:solo-ckpt"], h.Resources.DownloadLog);
    }

    [Fact]
    public async Task ConfigurationImport_NodeNotServing_LeavesModelsStopped_ReportedOncePerNode()
    {
        var models = new[] { Model("one", "dgx", autoStart: true), Model("two", "dgx", autoStart: true) };
        var backup = new BackupDocument { SchemaVersion = BackupDocument.MultiNodeSchemaVersion, Models = models, Nodes = [] };
        var h = Import(Node("dgx", DgxUid));
        h.Runtime.Unavailable.Add("dgx");

        var job = await RunAsync(h.Service, new RestoreRequest(backup, null, ["one", "two"], true, false, false,
            Targets: [new RestoreTarget("one", "dgx"), new RestoreTarget("two", "dgx")]));

        Assert.Equal(RestoreJobState.Completed, job.State);
        Assert.All(job.Models, m => Assert.Equal(RestoreModelState.NotStarted, m.State));
        Assert.Single(job.Notes, n => n.Contains("Node 'dgx' is not available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigurationImport_DownloadsRunInParallelAcrossNodes_OneAtATimeWithinANode()
    {
        var models = new[] { Model("a1", "n-a"), Model("a2", "n-a"), Model("b1", "n-b"), Model("b2", "n-b") };
        var backup = new BackupDocument { SchemaVersion = BackupDocument.MultiNodeSchemaVersion, Models = models, Nodes = [] };
        var h = Import(Node("n-a", "a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0a0"), Node("n-b", "b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0"));
        // Each node's first download waits until the other node's first download has started: sequential lanes time out.
        h.Resources.Expect("n-a", "n-b");

        var job = await RunAsync(h.Service, new RestoreRequest(backup, null, ["a1", "a2", "b1", "b2"], true, false, false,
            Targets: [.. models.Select(m => new RestoreTarget(m.Id, m.Placement!.NodeId))]));

        Assert.All(job.Models, m => Assert.Equal(RestoreModelState.Ready, m.State));
        Assert.Equal(1, h.Resources.Downloader("n-a").MaxConcurrent);
        Assert.Equal(1, h.Resources.Downloader("n-b").MaxConcurrent);
        Assert.Equal(["a1-ckpt", "a2-ckpt"], h.Resources.Downloader("n-a").Started);
    }

    [Fact]
    public async Task WrongPassphrase_IsRefusedForAV2BackupWithTheCa()
    {
        var fleet = await SourceFleetAsync();
        var parsed = BackupService.Parse(BackupService.Serialize(await fleet.Service.CreateAsync(Passphrase)));

        var ex = Assert.Throws<BackupPassphraseException>(() => BackupService.DecryptSecrets(parsed, "not the passphrase"));
        Assert.Contains("Wrong passphrase", ex.Message, StringComparison.Ordinal);
        Assert.Throws<BackupPassphraseException>(() => BackupService.DecryptSecrets(parsed, ""));
    }

    // ---------------------------------------------------------------- fleet disaster recovery

    private sealed record FleetHarness(FleetRecovery Recovery, string DataPath, RecordingAuditLog Audit, Func<int> Restarts);

    private FleetHarness Recovery(string? dataPath = null, bool marker = true, MasterPkiOptions? pki = null, RecoveryOptions? options = null)
    {
        string data = dataPath ?? Dir("fleet-" + Guid.NewGuid().ToString("N")[..6]);
        if (marker)
        {
            File.WriteAllBytes(Path.Combine(data, RecoveryOptions.MarkerFileName), []);
        }

        int restarts = 0;
        var audit = new RecordingAuditLog();
        var recovery = Own(new FleetRecovery(Data(data), Options.Create(options ?? new RecoveryOptions()), Options.Create(pki ?? new MasterPkiOptions()),
            TimeProvider.System, NullLogger<FleetRecovery>.Instance, audit, "dgx", () => restarts++));
        return new FleetHarness(recovery, data, audit, () => restarts);
    }

    [Fact]
    public async Task FleetRecovery_V2WithCa_InstallsBeforeNormalInit_NoSeedNoNewCa_AndRequestsTheTakeover()
    {
        var fleet = await SourceFleetAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        var h = Recovery();
        Assert.True(h.Recovery.IsActive);
        Assert.Equal(FleetRecoveryStage.AwaitingBackup, h.Recovery.Status.Stage);

        var result = await h.Recovery.InstallAsync(backup, secrets, actor: AuditActors.Admin("abc123"));

        Assert.Equal(FleetRecoveryStage.Installed, h.Recovery.Status.Stage);
        Assert.Equal(1, h.Restarts());
        Assert.Equal((3, 3, 1, 1, true), (result.Nodes, result.Models, result.ApiKeys, result.ServiceContainers, result.CaWrittenToDisk));
        Assert.Contains(result.Notes, n => n.Contains("'pc-office'", StringComparison.Ordinal) && n.Contains("join --rekey", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(h.DataPath, RecoveryOptions.MarkerFileName)));
        Assert.False(Directory.Exists(Path.Combine(h.DataPath, FleetRecovery.StagingDirectoryName)));
        Assert.Equal(RecoveryMode.None, FleetRecovery.PrepareBoot(new RecoveryOptions(), h.DataPath));

        // The normal boot that follows: a seeding registry finds the restored models (no seed), the PKI loads the backup's
        // CA (no new one), the identity continues the backup's instance one epoch up, and a takeover is requested.
        var models = Registry(h.DataPath, ModelSeedPolicy.Seed);
        var restored = await models.ListAsync();
        Assert.Equal(["alpha", "beta", "gamma"], restored.Select(m => m.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["GPU-1111"], restored.Single(m => m.Id == "beta").Placement!.GpuUuids);
        Assert.Null(restored.Single(m => m.Id == "gamma").Placement);

        using (var ca = CaStore.LoadOrCreate(MasterPkiOptions.DirectoryOf(h.DataPath), null, null))
        {
            Assert.Equal(fleet.Ca.SpkiSha256, ca.SpkiSha256);
        }

        using (var identity = MasterIdentity.Open(h.DataPath))
        {
            Assert.Equal("old-master-instance", identity.InstanceId);
            Assert.Equal(42, identity.Epoch);
            Assert.True(identity.TakeoverRequested);
        }

        var nodes = await Nodes(h.DataPath).ListAsync();
        var pc = nodes.Single(n => n.Id == "pc-office");
        Assert.Equal((PcUid, true, true, false), (pc.Uid, pc.Approved, pc.Draining, pc.Revoked));
        Assert.Equal(1.5, pc.GpuReserveGiB["GPU-1111"]);
        Assert.Equal("pin-x", pc.CertSpkiSha256);
        Assert.True(nodes.Single(n => n.Id == "laptop").Revoked);
        Assert.Equal("sha256:" + new string('a', 64), nodes.Single(n => n.Id == "dgx").DefaultVllmImageDigest);
        Assert.Empty(await Nodes(h.DataPath).ListJoinTokensAsync());

        Assert.NotNull(await Keys(h.DataPath).FindByPresentedKeyAsync(fleet.Key.FullKey));
        var services = Own(new HostServicesStore(Path.Combine(h.DataPath, "host-services.json")));
        Assert.Equal(["openviking"], (await services.ExportAsync()).Containers!.Select(c => c.Name));

        var line = Assert.Single(h.Audit.Records);
        Assert.Equal((AuditActions.Restore, AuditResults.Succeeded, "admin:abc123"), (line.Action, line.Result, line.Actor));
        Assert.DoesNotContain(secrets!.Pki!.CaKeyPem, JsonSerializer.Serialize(line), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FleetRecovery_ReplacesProvisionalMaterialOfAFirstNormalBoot()
    {
        var fleet = await SourceFleetAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        string data = Dir("provisional");

        // What a normal first boot leaves: the seed model, a fresh CA with master certificates, the bootstrap node record
        // (not enrolled) and its unused bootstrap token, and an empty key store.
        Assert.Single(await Registry(data, ModelSeedPolicy.Seed).ListAsync());
        string provisionalCaPin;
        using (var provisionalCa = CaStore.LoadOrCreate(MasterPkiOptions.DirectoryOf(data), null, null))
        {
            provisionalCaPin = provisionalCa.SpkiSha256;
            using var certs = MasterCertificates.LoadOrIssue(provisionalCa, MasterPkiOptions.DirectoryOf(data));
        }

        var nodes = Nodes(data);
        await nodes.UpsertAsync(new NodeRecord { Id = "dgx", Uid = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", Address = "https://mirbadllm-agent:9443" });
        await nodes.CreateJoinTokenAsync("dgx", TimeSpan.FromHours(1), JoinTokenPurpose.Bootstrap);
        await Keys(data).ListAsync();
        Assert.True(File.Exists(Path.Combine(MasterPkiOptions.DirectoryOf(data), MasterCertificates.ClientCertificateFileName)));

        var h = Recovery(data);
        var emptiness = await h.Recovery.CheckEmptinessAsync();
        Assert.True(emptiness.IsEmpty, string.Join(" ", emptiness.Blockers));

        await h.Recovery.InstallAsync(backup, secrets);

        Assert.False(File.Exists(Path.Combine(MasterPkiOptions.DirectoryOf(data), MasterCertificates.ClientCertificateFileName)));
        using (var ca = CaStore.LoadOrCreate(MasterPkiOptions.DirectoryOf(data), null, null))
        {
            Assert.Equal(fleet.Ca.SpkiSha256, ca.SpkiSha256);
            Assert.NotEqual(provisionalCaPin, ca.SpkiSha256);
        }

        Assert.DoesNotContain(await Registry(data, ModelSeedPolicy.Seed).ListAsync(), m => m.Id == "ornith-9b-fp8");
        Assert.Equal(DgxUid, (await Nodes(data).GetAsync("dgx"))!.Uid);
        Assert.Empty(await Nodes(data).ListJoinTokensAsync());
    }

    [Fact]
    public async Task FleetRecovery_KeepsAServicesListEditedOnAFirstNormalBoot_NextToTheRestoredOne()
    {
        var fleet = await SourceFleetAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        string data = Dir("edited-services");
        using (var services = new HostServicesStore(Path.Combine(data, "host-services.json")))
        {
            await services.UpdateContainersAsync([], _ => [new ManagedContainerOptions { Name = "my-grafana" }]);
            await services.SetAsync("my-grafana", "always");
        }

        var h = Recovery(data);
        Assert.True((await h.Recovery.CheckEmptinessAsync()).IsEmpty); // not a blocker: a first boot seeds it anyway

        var result = await h.Recovery.InstallAsync(backup, secrets);

        using (var restored = new HostServicesStore(Path.Combine(data, "host-services.json")))
        {
            Assert.Equal(["openviking"], (await restored.ExportAsync()).Containers!.Select(c => c.Name));
        }

        using (var kept = new HostServicesStore(Path.Combine(data, FleetRecovery.ReplacedServicesFileName)))
        {
            var lists = await kept.ExportAsync();
            Assert.Equal(["my-grafana"], lists.Containers!.Select(c => c.Name));
            Assert.Equal("always", lists.RestartPolicies!["my-grafana"]);
        }

        Assert.Contains(result.Notes, n => n.Contains(FleetRecovery.ReplacedServicesFileName, StringComparison.Ordinal));
        Assert.Empty(NodeHostServicesFiles.ListNodeIds(data)); // never read as a node's file
    }

    [Fact]
    public async Task FleetRecovery_IsRefusedOnANonEmptyMaster_AndChangesNothing()
    {
        var fleet = await SourceFleetAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        string data = Dir("busy");
        await Registry(data).UpsertAsync(Model("mine", "dgx"));
        var nodes = Nodes(data);
        await nodes.UpsertAsync(Node("dgx", "dddddddddddddddddddddddddddddddd"));
        await nodes.UpsertAsync(Node("other", "abababababababababababababababab"));
        await Keys(data).CreateAsync("someone", ApiKeyScope.Inference);
        byte[] modelsBefore = await File.ReadAllBytesAsync(Path.Combine(data, ModelRegistryDocument.FileName));
        byte[] nodesBefore = await File.ReadAllBytesAsync(Path.Combine(data, NodeRegistryDocument.FileName));

        var h = Recovery(data);
        var ex = await Assert.ThrowsAsync<FleetRecoveryException>(() => h.Recovery.InstallAsync(backup, secrets));

        Assert.Equal(ErrorCodes.MasterNotEmpty, ex.Code);
        Assert.Equal(409, ex.Status);
        Assert.Contains(ex.Blockers, b => b.Contains("Node 'other'", StringComparison.Ordinal));
        Assert.Contains(ex.Blockers, b => b.Contains("bootstrap node 'dgx' enrolled", StringComparison.Ordinal));
        Assert.Contains(ex.Blockers, b => b.Contains("Model 'mine'", StringComparison.Ordinal));
        Assert.Contains(ex.Blockers, b => b.Contains("API key", StringComparison.Ordinal));
        Assert.Equal(modelsBefore, await File.ReadAllBytesAsync(Path.Combine(data, ModelRegistryDocument.FileName)));
        Assert.Equal(nodesBefore, await File.ReadAllBytesAsync(Path.Combine(data, NodeRegistryDocument.FileName)));
        Assert.False(File.Exists(Path.Combine(data, MasterStateDocument.FileName)));
        Assert.True(File.Exists(Path.Combine(data, RecoveryOptions.MarkerFileName)));
        Assert.Equal(FleetRecoveryStage.AwaitingBackup, h.Recovery.Status.Stage);
        Assert.Equal(0, h.Restarts());
        Assert.Equal((AuditResults.Refused, ErrorCodes.MasterNotEmpty), (h.Audit.Records.Single().Result, h.Audit.Records.Single().ErrorCode));
    }

    [Fact]
    public async Task FleetRecovery_UsedJoinTokenOrEditedSeed_MakeTheMasterNonEmpty()
    {
        string data = Dir("token");
        var models = Registry(data, ModelSeedPolicy.Seed);
        var seed = Assert.Single(await models.ListAsync());
        await models.UpsertAsync(seed with { MemoryBudgetGiB = seed.MemoryBudgetGiB + 1 });
        var nodes = Nodes(data);
        await nodes.UpsertAsync(new NodeRecord { Id = "dgx", Uid = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", Address = "https://a:9443" });
        var token = await nodes.CreateJoinTokenAsync("dgx", TimeSpan.FromHours(1), JoinTokenPurpose.Bootstrap);
        await nodes.TryConsumeJoinTokenAsync(token.Token, "dgx");

        var emptiness = await Recovery(data).Recovery.CheckEmptinessAsync();

        Assert.False(emptiness.IsEmpty);
        Assert.Contains(emptiness.Blockers, b => b.Contains("join token for 'dgx' was used", StringComparison.Ordinal));
        Assert.Contains(emptiness.Blockers, b => b.Contains("Model 'ornith-9b-fp8'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FleetRecovery_RefusesUnsuitableBackups_AndOutsideRecoveryMode()
    {
        var fleet = await SourceFleetAsync(includeCa: false);
        var (noCa, noCaSecrets) = await RoundTripAsync(fleet.Service);
        var (noPassphrase, _) = await RoundTripAsync(fleet.Service, passphrase: null);
        var v1 = BackupService.Parse(JsonSerializer.SerializeToUtf8Bytes(
            new BackupDocument { SchemaVersion = 1, Models = [] }, BackupDocument.JsonOptions));
        var h = Recovery();

        foreach (var (backup, secrets, text) in new[]
        {
            (v1, (BackupSecrets?)null, "version-2 backup"),
            (noPassphrase, null, "without a passphrase"),
            (noCa, noCaSecrets, "holds no CA"),
        })
        {
            var ex = await Assert.ThrowsAsync<FleetRecoveryException>(() => h.Recovery.InstallAsync(backup, secrets));
            Assert.Equal(ErrorCodes.BackupUnsuitable, ex.Code);
            Assert.Equal(400, ex.Status);
            Assert.Contains(text, ex.Message, StringComparison.Ordinal);
        }

        // An encrypted backup whose passphrase was not given (secrets not unlocked) is unsuitable too.
        var (withCa, _) = await RoundTripAsync((await SourceFleetAsync(name: "source-with-ca")).Service);
        Assert.Equal(ErrorCodes.BackupUnsuitable, (await Assert.ThrowsAsync<FleetRecoveryException>(() => h.Recovery.InstallAsync(withCa, null))).Code);

        var normal = Recovery(marker: false);
        Assert.False(normal.Recovery.IsActive);
        Assert.Equal(FleetRecoveryStage.NotInRecoveryMode, normal.Recovery.Status.Stage);
        var refused = await Assert.ThrowsAsync<FleetRecoveryException>(() => normal.Recovery.InstallAsync(withCa, null));
        Assert.Equal(ErrorCodes.InvalidState, refused.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(normal.DataPath));
    }

    [Fact]
    public async Task FleetRecovery_ConfiguredCa_MustEqualTheBackups_AndIsNeverWrittenToDisk()
    {
        var fleet = await SourceFleetAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        using var other = CertificateAuthority.Create();

        var mismatch = Recovery(pki: new MasterPkiOptions { CaCertPem = other.CertificatePem, CaKeyPem = other.ExportPrivateKeyPem() });
        var ex = await Assert.ThrowsAsync<FleetRecoveryException>(() => mismatch.Recovery.InstallAsync(backup, secrets));
        Assert.Equal(ErrorCodes.BackupUnsuitable, ex.Code);
        Assert.Contains(Spki.ToDisplayFingerprint(fleet.Ca.SpkiSha256), ex.Message, StringComparison.Ordinal);
        Assert.Contains(Spki.ToDisplayFingerprint(other.SpkiSha256), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", ex.Message, StringComparison.Ordinal);

        var configured = new MasterPkiOptions { CaCertPem = fleet.Ca.CertificatePem, CaKeyPem = fleet.Ca.ExportPrivateKeyPem() };
        var match = Recovery(pki: configured);
        var result = await match.Recovery.InstallAsync(backup, secrets);

        Assert.False(result.CaWrittenToDisk);
        Assert.False(File.Exists(Path.Combine(MasterPkiOptions.DirectoryOf(match.DataPath), CaStore.KeyFileName)));
        using var loaded = CaStore.LoadOrCreate(MasterPkiOptions.DirectoryOf(match.DataPath), configured.CaCertPem, configured.CaKeyPem);
        Assert.Equal(fleet.Ca.SpkiSha256, loaded.SpkiSha256);
        Assert.False(File.Exists(Path.Combine(MasterPkiOptions.DirectoryOf(match.DataPath), CaStore.KeyFileName)));
    }

    [Fact]
    public void PrepareBoot_FinishesACompleteStaging_DiscardsAnIncompleteOne_AndTheOptionIsOneShot()
    {
        // A crash after staging completed: the next boot moves it into place.
        string data = Dir("resume");
        string staging = Path.Combine(data, FleetRecovery.StagingDirectoryName);
        Directory.CreateDirectory(Path.Combine(staging, MasterPkiOptions.DirectoryName));
        File.WriteAllText(Path.Combine(staging, ModelRegistryDocument.FileName), """{"schemaVersion":2,"models":[]}""");
        File.WriteAllText(Path.Combine(staging, MasterPkiOptions.DirectoryName, CaStore.CertificateFileName), "staged");
        File.WriteAllBytes(Path.Combine(staging, FleetRecovery.StagingCompleteFileName), []);
        Directory.CreateDirectory(MasterPkiOptions.DirectoryOf(data));
        File.WriteAllText(Path.Combine(MasterPkiOptions.DirectoryOf(data), "master-client.crt"), "provisional");
        File.WriteAllBytes(Path.Combine(data, RecoveryOptions.MarkerFileName), []);

        Assert.Equal(RecoveryMode.FleetRestore, FleetRecovery.PrepareBoot(null, data));
        Assert.False(Directory.Exists(staging));
        Assert.Equal("staged", File.ReadAllText(Path.Combine(MasterPkiOptions.DirectoryOf(data), CaStore.CertificateFileName)));
        Assert.False(File.Exists(Path.Combine(MasterPkiOptions.DirectoryOf(data), "master-client.crt")));
        Assert.True(File.Exists(Path.Combine(data, ModelRegistryDocument.FileName)));

        // An incomplete staging is dropped, nothing moved.
        string partial = Dir("partial");
        Directory.CreateDirectory(Path.Combine(partial, FleetRecovery.StagingDirectoryName));
        File.WriteAllText(Path.Combine(partial, FleetRecovery.StagingDirectoryName, NodeRegistryDocument.FileName), "{}");
        Assert.Equal(RecoveryMode.None, FleetRecovery.PrepareBoot(null, partial));
        Assert.False(Directory.Exists(Path.Combine(partial, FleetRecovery.StagingDirectoryName)));
        Assert.False(File.Exists(Path.Combine(partial, NodeRegistryDocument.FileName)));

        // After an install the option no longer counts and a leftover marker is removed; a newer marker counts again.
        string done = Dir("done");
        var option = new RecoveryOptions { Mode = RecoveryMode.FleetRestore };
        Assert.Equal(RecoveryMode.FleetRestore, FleetRecovery.PrepareBoot(option, done));
        File.WriteAllBytes(Path.Combine(done, RecoveryOptions.MarkerFileName), []);
        File.SetLastWriteTimeUtc(Path.Combine(done, RecoveryOptions.MarkerFileName), DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(Path.Combine(done, FleetRecoveryRecord.FileName), JsonSerializer.Serialize(
            new FleetRecoveryRecord { InstalledAt = DateTimeOffset.UtcNow.AddMinutes(-1), MasterInstanceId = "m", Epoch = 3 }, MasterStateJson.Options));
        Assert.Equal(RecoveryMode.None, FleetRecovery.PrepareBoot(option, done));
        Assert.False(File.Exists(Path.Combine(done, RecoveryOptions.MarkerFileName)));
        File.WriteAllBytes(Path.Combine(done, RecoveryOptions.MarkerFileName), []);
        Assert.Equal(RecoveryMode.FleetRestore, FleetRecovery.PrepareBoot(option, done));
    }

    [Fact]
    public void RecoveryMaintenanceWindow_IsAGlobalRestoringWindow()
    {
        IMaintenanceMode window = new RecoveryMaintenanceWindow(TimeProvider.System);

        var state = window.Current!;
        Assert.True(state.IsGlobal);
        Assert.Equal(MaintenanceJobKinds.Restore, state.JobKind);
        Assert.Equal(state.JobKind, window.ForModel("anything")!.JobKind);
        Assert.NotNull(window.ForNode("dgx"));
        Assert.Contains("restoring a backup", state.Describe(DateTimeOffset.UtcNow), StringComparison.Ordinal);
        Assert.True(state.RetryAfterSeconds(DateTimeOffset.UtcNow) >= 30);
    }

    [Fact]
    public void Seed_IsProvisionalOnlyWhenUnchanged()
    {
        var seed = JsonModelRegistry.SeedModel with { Placement = new ModelPlacement("dgx") };
        Assert.True(FleetRecovery.IsUnchangedSeed(seed));
        Assert.False(FleetRecovery.IsUnchangedSeed(seed with { MaxNumSeqs = 4 }));
        Assert.False(FleetRecovery.IsUnchangedSeed(seed with { Id = "other" }));
    }

    // ---------------------------------------------------------------- Restore tab (mode picker, target picker)

    private Bunit.BunitContext Page(ImportHarness h, FleetRecovery? fleet = null)
    {
        var ctx = new Bunit.BunitContext();
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetClaims(TestAdmin.User.Claims.ToArray());
        ctx.JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
        var options = Options.Create(new BackupOptions { Directory = Path.Combine(_root, "no-backups"), HostName = "here" });
        var backups = new BackupService(h.Registry, Keys(h.DataPath), options, TimeProvider.System);
        ctx.Services.AddSingleton(backups);
        ctx.Services.AddSingleton(new BackupDownloadTokens(TimeProvider.System));
        ctx.Services.AddSingleton(new BackupScheduler(backups, new BackupDirectory(options), options, TimeProvider.System, NullLogger<BackupScheduler>.Instance));
        ctx.Services.AddSingleton(h.Planner);
        ctx.Services.AddSingleton(h.Service);
        ctx.Services.AddSingleton<IAdminReauthenticator>(new HostServices.FakeReauth());
        if (fleet is not null)
        {
            ctx.Services.AddSingleton(fleet);
        }

        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("backup?tab=restore");
        return ctx;
    }

    [Fact]
    public async Task RestoreTab_ConfigurationImport_ShowsTheTargetPicker_AndRestoresOnThePickedNode()
    {
        var fleet = await SourceFleetAsync();
        byte[] bytes = BackupService.Serialize(await fleet.Service.CreateAsync(passphrase: null));
        var h = Import(Node("spark", DgxUid), Node("pc-office", "99999999999999999999999999999999"));
        using var ctx = Page(h);
        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Backup>();

        // Without recovery mode the fleet option is offered but disabled.
        Assert.True(cut.Find("[data-testid=restore-mode-fleet]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid=restore-mode-import]").HasAttribute("checked"));

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(Bunit.InputFileContent.CreateFromBinary(bytes, "backup.json"));
        // Triggers that wait for their handler (BunitDispatch): the bare Click()/Change() return before the handler ran, and
        // the preview's late render can retire a handler id, so under full-suite load the plan was read before the picks.
        cut.ClickAt("[data-testid=preview-restore]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=placement-beta]")), TimeSpan.FromSeconds(10));

        Assert.Contains("Same node", cut.Find("[data-testid=placement-alpha]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Name only", cut.Find("[data-testid=placement-beta]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=group-target-pc-office]"));

        cut.ChangeAt("[data-testid=target-beta]", "spark");
        cut.ChangeAt("[data-testid=import-services]", false);
        cut.ClickAt("[data-testid=restore-button]");
        Assert.Contains("1 keep their node (same uid), 1 go to the node you picked, 1 are restored unplaced",
            cut.Find("[data-testid=restore-plan]").TextContent, StringComparison.Ordinal);
        cut.ClickAt("[data-testid=confirm-restore]");
        await h.Service.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("spark", (await h.Registry.GetAsync("beta"))!.Placement!.NodeId);
        Assert.Equal("spark", (await h.Registry.GetAsync("alpha"))!.Placement!.NodeId);
        Assert.Null((await h.Registry.GetAsync("gamma"))!.Placement);
    }

    [Fact]
    public async Task RestoreTab_ADiscreteTarget_SaysItsGpusAreChosenAutomatically_AndTheRestorePlacesThem()
    {
        // MN-124 (D105): the picker sends no GPUs; the page says what the restore does for a discrete target.
        var fleet = await SourceFleetAsync();
        byte[] bytes = BackupService.Serialize(await fleet.Service.CreateAsync(passphrase: null));
        var h = Import(Node("spark", DgxUid), DiscreteNode("pc-office", "99999999999999999999999999999999", Card(0, 16)));
        using var ctx = Page(h);
        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Backup>();
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(Bunit.InputFileContent.CreateFromBinary(bytes, "backup.json"));
        cut.ClickAt("[data-testid=preview-restore]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=placement-beta]")), TimeSpan.FromSeconds(10));
        Assert.Empty(cut.FindAll("[data-testid=target-gpus-beta]"));

        cut.ChangeAt("[data-testid=target-beta]", "pc-office");
        Assert.Contains("chosen automatically", cut.Find("[data-testid=target-gpus-beta]").TextContent, StringComparison.Ordinal);
        cut.ChangeAt("[data-testid=target-beta]", "spark");
        Assert.Empty(cut.FindAll("[data-testid=target-gpus-beta]"));
        cut.ChangeAt("[data-testid=target-beta]", "pc-office");

        cut.ChangeAt("[data-testid=import-services]", false);
        cut.ClickAt("[data-testid=restore-button]");
        cut.ClickAt("[data-testid=confirm-restore]");
        await h.Service.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        var placement = (await h.Registry.GetAsync("beta"))!.Placement!;
        Assert.Equal("pc-office", placement.NodeId);
        Assert.Equal([Card(0, 16).Uuid], placement.GpuUuids);
    }

    [Fact]
    public async Task RestoreTab_RecoveryMode_DefaultsToFleetRecovery_AndInstallsUsingTheSession()
    {
        var fleet = await SourceFleetAsync();
        byte[] bytes = BackupService.Serialize(await fleet.Service.CreateAsync(Passphrase));
        var h = Import();
        var recovery = Recovery();
        using var ctx = Page(h, recovery.Recovery);
        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Backup>();

        Assert.True(cut.Find("[data-testid=restore-mode-fleet]").HasAttribute("checked"));
        Assert.False(cut.Find("[data-testid=restore-mode-fleet]").HasAttribute("disabled"));

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(Bunit.InputFileContent.CreateFromBinary(bytes, "backup.json"));
        cut.Find("[data-testid=restore-passphrase]").Change(Passphrase);
        cut.Find("[data-testid=preview-restore]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=fleet-preview]")), TimeSpan.FromSeconds(10));

        Assert.Contains("re-pair", cut.Find("[data-testid=fleet-node-pc-office]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Revoked", cut.Find("[data-testid=fleet-node-laptop]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=fleet-not-empty]"));

        cut.ClickAt("[data-testid=fleet-install]");
        Assert.Empty(cut.FindAll("[data-testid=fleet-admin-password]"));
        // The takeover after the restart is the operator's confirmed action (D105): not without "the previous master is stopped".
        Assert.Contains("takeover runs by itself", cut.Find("[data-testid=fleet-plan]").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=confirm-fleet]").HasAttribute("disabled"));
        cut.Find("[data-testid=fleet-old-master-stopped]").Change(true);
        cut.Find("[data-testid=confirm-fleet]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=fleet-installed]")), TimeSpan.FromSeconds(30));

        Assert.Equal(FleetRecoveryStage.Installed, recovery.Recovery.Status.Stage);
        Assert.Contains("old-master-instance", cut.Find("[data-testid=fleet-installed-summary]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Nodes page", cut.Find("[data-testid=fleet-installed-summary]").TextContent, StringComparison.Ordinal);
        Assert.Contains("takeover runs by itself", cut.Find("[data-testid=fleet-installed-summary]").TextContent, StringComparison.Ordinal);
        await WaitUntilAsync(() => recovery.Restarts() == 1); // the restart is requested right after the Installed stage is shown
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 10 s.");
            await Task.Delay(5);
        }
    }

    // ---------------------------------------------------------------- fakes

    /// <summary>Per-node folders and downloaders; logs "node:folder" for every download started.</summary>
    // ---------------------------------------------------------------- per-node Services lists (W7 review MN-126 item 4)

    /// <summary>The source fleet plus pc-office's own Services file and a stale file of a node that is not registered.</summary>
    private async Task<SourceFleet> FleetWithPerNodeServicesAsync()
    {
        var fleet = await SourceFleetAsync();
        string source = Path.Combine(_root, "source");
        await NodeHostServicesFiles.ImportAsync(source, "pc-office",
            new HostServicesBackup(new Dictionary<string, string> { ["ollama"] = "always" }, [new ManagedContainerOptions { Name = "ollama" }]));
        await NodeHostServicesFiles.ImportAsync(source, "gone", new HostServicesBackup(null, [new ManagedContainerOptions { Name = "old" }]));
        return fleet;
    }

    [Fact]
    public async Task Backup_CarriesEveryRegisteredNodesServicesFile_UnderItsNodeId()
    {
        var fleet = await FleetWithPerNodeServicesAsync();

        var (backup, _) = await RoundTripAsync(fleet.Service, passphrase: null);

        var perNode = backup.HostServices!.Nodes!;
        Assert.Equal(["dgx", "pc-office"], perNode.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["openviking"], perNode["dgx"].Containers!.Select(c => c.Name));
        Assert.Equal(["ollama"], backup.HostServicesFor("pc-office")!.Containers!.Select(c => c.Name));
        Assert.Equal("always", backup.HostServicesFor("pc-office")!.RestartPolicies!["ollama"]);
    }

    [Fact]
    public async Task Backup_OfASingleDgx_HasOnlyTheBootstrapNodesLists_AsBefore()
    {
        var fleet = await SourceFleetAsync();

        var (backup, _) = await RoundTripAsync(fleet.Service, passphrase: null);

        Assert.Equal(["dgx"], backup.HostServices!.Nodes!.Keys);
    }

    [Fact]
    public async Task ConfigurationImport_RestoresAnotherNodesServices_ToTheNodeWithItsUid_AndNotesTheRest()
    {
        var fleet = await FleetWithPerNodeServicesAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        // Here: the DGX, and pc-office's machine registered under another id; no node with the laptop's uid.
        var h = Import(Node("dgx", DgxUid), Node("office", PcUid));

        var job = await RunAsync(h.Service, new RestoreRequest(backup, secrets, [], true, false, ImportHostServices: true));

        Assert.Equal(RestoreJobState.Completed, job.State);
        string file = Path.Combine(h.DataPath, NodeHostServicesFiles.FileName("office"));
        using (var store = new HostServicesStore(file))
        {
            var lists = await store.ExportAsync();
            Assert.Equal(["ollama"], lists.Containers!.Select(c => c.Name));
            Assert.Equal("always", lists.RestartPolicies!["ollama"]);
        }

        Assert.False(File.Exists(Path.Combine(h.DataPath, NodeHostServicesFiles.FileName("pc-office"))));
        Assert.Contains(job.Notes, n => n.Contains("node 'office': 1 managed container restored (from the backup's node 'pc-office')", StringComparison.Ordinal));
        Assert.Equal(["openviking"], (await h.Services.ExportAsync()).Containers!.Select(c => c.Name));
    }

    [Fact]
    public async Task ConfigurationImport_ANodeWithoutAUidMatch_KeepsNoFile_AndIsNoted()
    {
        var fleet = await FleetWithPerNodeServicesAsync();
        var (backup, _) = await RoundTripAsync(fleet.Service, passphrase: null);
        var h = Import(Node("dgx", DgxUid), Node("pc-office", "99999999999999999999999999999999")); // same slug, other machine

        var job = await RunAsync(h.Service, new RestoreRequest(backup, null, [], true, false, ImportHostServices: true));

        Assert.Empty(NodeHostServicesFiles.ListNodeIds(h.DataPath));
        Assert.Contains(job.Notes, n => n.Contains("'pc-office' were not restored: no node here has the same uid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FleetRecovery_RestoresEveryNodesServicesFile()
    {
        var fleet = await FleetWithPerNodeServicesAsync();
        var (backup, secrets) = await RoundTripAsync(fleet.Service);
        var h = Recovery();

        var result = await h.Recovery.InstallAsync(backup, secrets);

        Assert.Equal(["pc-office"], NodeHostServicesFiles.ListNodeIds(h.DataPath));
        using (var store = new HostServicesStore(Path.Combine(h.DataPath, NodeHostServicesFiles.FileName("pc-office"))))
        {
            Assert.Equal(["ollama"], (await store.ExportAsync()).Containers!.Select(c => c.Name));
        }

        Assert.Equal(1, result.ServiceContainers); // the bootstrap node's list, as before
        Assert.Contains(result.Notes, n => n == "Services of node 'pc-office': 1 managed container restored.");
    }

    [Fact]
    public async Task NodeHostServicesFiles_ImportOfAnEntryWithoutAList_StartsEmpty_NeverWithTheDgxDefaults()
    {
        string data = Dir("nhs-" + Guid.NewGuid().ToString("N")[..6]);

        int count = await NodeHostServicesFiles.ImportAsync(data, "pc", new HostServicesBackup(new Dictionary<string, string> { ["x"] = "always" }, null));

        Assert.Equal(0, count);
        using var store = new HostServicesStore(Path.Combine(data, "host-services.pc.json"));
        Assert.Empty(await store.GetContainersAsync([new ManagedContainerOptions { Name = "openviking" }]));
        Assert.Equal(["pc"], NodeHostServicesFiles.ListNodeIds(data));
    }

    private sealed class PerNodeResources : IRestoreNodeResources
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, (FakeCheckpointFolders Folders, RendezvousDownloader Downloader)> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, TaskCompletionSource> _firstStarted = new(StringComparer.Ordinal);

        public List<string> DownloadLog { get; } = [];

        private bool _rendezvous;

        /// <summary>Downloads on these nodes must overlap: each node's first one waits for the others' first one.</summary>
        public void Expect(params string[] nodeIds)
        {
            foreach (string nodeId in nodeIds)
            {
                Get(nodeId);
            }

            _rendezvous = true;
        }

        public ICheckpointFolders FoldersFor(string nodeId) => Get(nodeId).Folders;

        public IHuggingFaceDownloader DownloaderFor(string nodeId) => Get(nodeId).Downloader;

        public SequencedDownloader Downloader(string nodeId) => Get(nodeId).Downloader.Inner;

        private (FakeCheckpointFolders Folders, RendezvousDownloader Downloader) Get(string nodeId)
        {
            lock (_gate)
            {
                if (!_nodes.TryGetValue(nodeId, out var entry))
                {
                    var folders = new FakeCheckpointFolders();
                    entry = (folders, new RendezvousDownloader(this, nodeId, new SequencedDownloader(folders)));
                    _nodes[nodeId] = entry;
                    _firstStarted[nodeId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                return entry;
            }
        }

        /// <summary>Records the start; after <see cref="Expect"/>, waits until every other expected node started one download too.</summary>
        public async Task OnStartAsync(string nodeId, string folder)
        {
            Task[] others;
            lock (_gate)
            {
                DownloadLog.Add($"{nodeId}:{folder}");
                _firstStarted[nodeId].TrySetResult();
                others = [.. _firstStarted.Where(kv => kv.Key != nodeId).Select(kv => kv.Value.Task)];
            }

            if (_rendezvous)
            {
                await Task.WhenAll(others).WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private sealed class RendezvousDownloader(PerNodeResources owner, string nodeId, SequencedDownloader inner) : IHuggingFaceDownloader
    {
        public SequencedDownloader Inner => inner;

        public async Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
        {
            var status = await inner.StartAsync(request, ct);
            await owner.OnStartAsync(nodeId, request.Folder!);
            return status;
        }

        public Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

        public Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default) => inner.GetAsync(folder, ct);

        public Task<bool> CancelAsync(string folder, CancellationToken ct = default) => inner.CancelAsync(folder, ct);
    }

    /// <summary>Starts models, or refuses with <see cref="NodeUnavailableException"/> for the nodes in <see cref="Unavailable"/>.</summary>
    private sealed class NodeScriptedRuntime : IModelRuntime
    {
        private readonly Lock _gate = new();

        public HashSet<string> Unavailable { get; } = new(StringComparer.Ordinal);

        public List<string> Started { get; } = [];

        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelStatus>>([]);

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) => Task.FromResult(new ModelStatus(id, ModelState.Stopped));

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default)
        {
            lock (_gate)
            {
                foreach (string node in Unavailable)
                {
                    throw new NodeUnavailableException($"node '{node}' is offline") { Reason = NodeUnavailableReason.NotServing, NodeId = node };
                }

                Started.Add(id);
                return Task.FromResult(new ModelStatus(id, ModelState.Starting));
            }
        }

        public Task StopAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        private readonly Lock _gate = new();
        private readonly List<AuditRecord> _records = [];

        public IReadOnlyList<AuditRecord> Records
        {
            get
            {
                lock (_gate)
                {
                    return [.. _records];
                }
            }
        }

        public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _records.Add(record);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default) => Task.FromResult(Records);
    }
}
