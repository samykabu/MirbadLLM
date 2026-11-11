using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Router.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Recovery;

/// <summary>
/// Backup format version 2 (design §5.5, MN-034): master, nodes, per-node Services lists, placements and the CA in the
/// encrypted section only with a passphrase; version-1 files still read, and a version-1 restore places every model on
/// the bootstrap node.
/// </summary>
public sealed class BackupV2Tests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private static readonly ModelDefinition OnDgx = new()
    {
        Id = "alpha",
        ServedName = "Alpha-9B",
        ModelPath = "Alpha-9B",
        MemoryBudgetGiB = 24,
        AutoStart = true,
        Placement = new ModelPlacement("dgx"),
        KeepServingDuringJobs = true,
        StartOnRequest = true,
        IdleStopMinutes = 20,
        StopToMakeRoom = true,
    };

    private static readonly ModelDefinition OnPc = new()
    {
        Id = "beta",
        ServedName = "Beta-4B",
        ModelPath = "Beta-4B",
        MemoryBudgetGiB = 12,
        Placement = new ModelPlacement("pc-office", ["GPU-1111"], [0]),
        HostMemoryBudgetGiB = 6,
    };

    private static readonly NodeRecord Dgx = new()
    {
        Id = "dgx",
        Uid = "0123456789abcdef0123456789abcdef",
        Address = "https://127.0.0.1:9443",
        Approved = true,
        CertSpkiSha256 = "dgx-pin",
        PendingCertSpkiSha256 = "dgx-pending-pin-never-in-backups",
        CertNotAfter = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
        MemoryReserveGiB = 8,
        Labels = ["arm64", "gb10"],
    };

    private static readonly NodeRecord RevokedPc = new()
    {
        Id = "pc-office",
        Uid = "fedcba9876543210fedcba9876543210",
        DisplayName = "Office PC",
        Address = "https://192.0.2.50:9443",
        AltAddresses = ["https://pc-office.lan:9443"],
        Approved = true,
        Draining = true,
        Revoked = true,
        GpuReserveGiB = new Dictionary<string, double> { ["GPU-1111"] = 1.5 },
        DefaultVllmImage = "vllm/vllm-openai:v0.11",
    };

    private sealed class FixedMasterIdentity : IMasterIdentity
    {
        public string InstanceId => "master-instance-1";

        public long Epoch => 7;

        public string BootId => "boot-1";

        public MasterTriple Current => new(InstanceId, Epoch, BootId);

        public bool TakeoverRequested => false;

        public IReadOnlyList<TakeoverRecord> Takeovers => [];

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private async Task<HostServicesStore> SeededServicesAsync()
    {
        var services = new HostServicesStore(Path.Combine(_dataDir, "host-services.json"));
        await services.GetContainersAsync([new ManagedContainerOptions { Name = "openviking", Protected = true }]);
        return services;
    }

    private static BackupService Create(
        HostServicesStore? services = null, CertificateAuthority? ca = null, bool includeCa = true, bool withMultinode = true) =>
        new(
            new FakeModelRegistry([OnPc, OnDgx]),
            new FakeApiKeyStore(),
            Options.Create(new BackupOptions { HostName = "dgx", IncludeCa = includeCa }),
            TimeProvider.System,
            services,
            master: withMultinode ? new FixedMasterIdentity() : null,
            nodes: withMultinode ? new ListOnlyNodeRegistry(RevokedPc, Dgx) : null,
            certificateAuthority: ca);

    [Fact]
    public async Task V2_RoundTrip_KeepsMasterNodesPlacementsAndPerNodeServices()
    {
        var service = Create(await SeededServicesAsync());

        byte[] bytes = BackupService.Serialize(await service.CreateAsync(passphrase: null));
        var parsed = BackupService.Parse(bytes);

        Assert.Equal(BackupDocument.MultiNodeSchemaVersion, parsed.SchemaVersion);
        Assert.Equal(new BackupMasterInfo("master-instance-1", 7), parsed.Master);

        Assert.Equal(["dgx", "pc-office"], parsed.Nodes!.Select(n => n.Id));
        var dgx = parsed.Nodes![0];
        Assert.Equal(Dgx.Uid, dgx.Uid);
        Assert.True(dgx.Approved);
        Assert.Equal("dgx-pin", dgx.CertSpkiSha256);
        Assert.Equal(Dgx.CertNotAfter, dgx.CertNotAfter);
        Assert.Equal(["arm64", "gb10"], dgx.Labels);
        var pc = parsed.Nodes[1];
        Assert.True(pc.Revoked);
        Assert.True(pc.Draining);
        Assert.Equal("Office PC", pc.DisplayName);
        Assert.Equal(["https://pc-office.lan:9443"], pc.AltAddresses);
        Assert.Equal(1.5, pc.GpuReserveGiB["GPU-1111"]);
        Assert.Equal("vllm/vllm-openai:v0.11", pc.DefaultVllmImage);

        var byId = parsed.Models.ToDictionary(m => m.Id);
        Assert.Equal(OnDgx.Placement, byId["alpha"].Placement);
        Assert.True(byId["alpha"].KeepServingDuringJobs);
        Assert.Equal((true, 20, true), (byId["alpha"].StartOnRequest, byId["alpha"].IdleStopMinutes, byId["alpha"].StopToMakeRoom));
        Assert.Equal(OnPc.Placement, byId["beta"].Placement);
        Assert.Equal(6, byId["beta"].HostMemoryBudgetGiB);

        // The Services lists sit under the bootstrap node (design §5.5 hostServices.nodes), not at the top level.
        var json = JsonNode.Parse(bytes)!;
        Assert.Null(json["hostServices"]!["containers"]);
        Assert.Equal("openviking", json["hostServices"]!["nodes"]!["dgx"]!["containers"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("openviking", Assert.Single(parsed.HostServicesFor("dgx")!.Containers!).Name);
        Assert.Null(parsed.HostServicesFor("pc-office"));

        // Never pending pins (nor anything else not listed in §5.5).
        string text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("dgx-pending-pin-never-in-backups", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pendingCertSpkiSha256", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V2_WithoutMultinodeSources_OmitsMasterAndNodes()
    {
        var parsed = BackupService.Parse(BackupService.Serialize(await Create(withMultinode: false).CreateAsync(passphrase: null)));

        Assert.Equal(BackupDocument.MultiNodeSchemaVersion, parsed.SchemaVersion);
        Assert.Null(parsed.Master);
        Assert.Null(parsed.Nodes);
        Assert.Null(parsed.HostServices);
        Assert.Equal(2, parsed.Models.Count);
    }

    [Fact]
    public async Task Ca_WithPassphrase_IsEncryptedAndDecryptsToTheIdenticalCa()
    {
        using var ca = CertificateAuthority.Create();
        string keyPem = ca.ExportPrivateKeyPem();

        byte[] bytes = BackupService.Serialize(await Create(ca: ca).CreateAsync(Passphrase));
        string text = Encoding.UTF8.GetString(bytes);
        var secrets = BackupService.DecryptSecrets(BackupService.Parse(bytes), Passphrase);

        // Neither the key nor the certificate is readable without the passphrase.
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN CERTIFICATE", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, text, StringComparison.Ordinal);

        var pki = Assert.IsType<BackupPkiSecrets>(secrets!.Pki);
        using var restored = CertificateAuthority.Load(pki.CaCertPem, pki.CaKeyPem);
        Assert.Equal(ca.Certificate.RawData, restored.Certificate.RawData);
        Assert.Equal(ca.SpkiSha256, restored.SpkiSha256);
        Assert.Equal(keyPem, restored.ExportPrivateKeyPem());
        Assert.Equal(ca.CertificatePem, pki.CaCertPem);
    }

    [Fact]
    public async Task Ca_WithoutPassphrase_IsNeverWritten()
    {
        using var ca = CertificateAuthority.Create();

        byte[] bytes = BackupService.Serialize(await Create(ca: ca).CreateAsync(passphrase: null));
        string text = Encoding.UTF8.GetString(bytes);

        Assert.Null(BackupService.Parse(bytes).Secrets);
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN CERTIFICATE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pki\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ca_IncludeCaFalse_LeavesItOutEvenWithAPassphrase()
    {
        using var ca = CertificateAuthority.Create();

        var parsed = BackupService.Parse(BackupService.Serialize(await Create(ca: ca, includeCa: false).CreateAsync(Passphrase)));
        var secrets = BackupService.DecryptSecrets(parsed, Passphrase)!;

        Assert.Null(secrets.Pki);
        Assert.True(new BackupOptions().IncludeCa, "the CA is included by default when a passphrase is set (decision D9)");
    }

    [Fact]
    public void Ca_ThatIsNotAMatchingPair_IsRefusedWithoutQuotingIt()
    {
        using var first = CertificateAuthority.Create();
        using var second = CertificateAuthority.Create();
        string secondKey = second.ExportPrivateKeyPem();
        var mismatched = new BackupSecrets([]) { Pki = new BackupPkiSecrets { CaCertPem = first.CertificatePem, CaKeyPem = secondKey } };
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(mismatched, BackupDocument.JsonOptions);
        var document = new BackupDocument { SchemaVersion = BackupDocument.MultiNodeSchemaVersion, Secrets = BackupCrypto.Encrypt(plaintext, Passphrase, BackupDocument.MultiNodeSchemaVersion) };

        var parsed = BackupService.Parse(BackupService.Serialize(document));
        var ex = Assert.Throws<BackupFormatException>(() => BackupService.DecryptSecrets(parsed, Passphrase));

        Assert.Contains("CA", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretsSection_IsBoundToItsDocumentsSchemaVersion()
    {
        // W3 review MN-094: the associated data names the document's version, so v1 files keep decrypting and a v2
        // section (which may carry the CA) does not decrypt inside a v1 document, nor the other way round.
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new BackupSecrets([]), BackupDocument.JsonOptions);
        var v1Section = BackupCrypto.Encrypt(plaintext, Passphrase);
        var v2Section = BackupCrypto.Encrypt(plaintext, Passphrase, BackupDocument.MultiNodeSchemaVersion);

        Assert.Equal(plaintext, BackupCrypto.Decrypt(v1Section, Passphrase, BackupDocument.CurrentSchemaVersion));
        Assert.Equal(plaintext, BackupCrypto.Decrypt(v2Section, Passphrase, BackupDocument.MultiNodeSchemaVersion));
        Assert.Throws<BackupPassphraseException>(() => BackupCrypto.Decrypt(v2Section, Passphrase, BackupDocument.CurrentSchemaVersion));
        Assert.Throws<BackupPassphraseException>(() => BackupCrypto.Decrypt(v1Section, Passphrase, BackupDocument.MultiNodeSchemaVersion));

        var downgraded = new BackupDocument { SchemaVersion = BackupDocument.CurrentSchemaVersion, Secrets = v2Section };
        Assert.Throws<BackupPassphraseException>(() => BackupService.DecryptSecrets(BackupService.Parse(BackupService.Serialize(downgraded)), Passphrase));
    }

    [Fact]
    public void V1_StillReads_DropsMultinodeSections_AndPlacesEveryModelOnTheBootstrapNode()
    {
        var parsed = BackupService.Parse(Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 1,
              "createdAt": "2026-09-01T03:00:00Z",
              "hostName": "dgx",
              "models": [
                { "id": "a", "servedName": "A", "modelPath": "A", "autoStart": true },
                { "id": "b", "servedName": "B", "modelPath": "B", "placement": { "nodeId": "pc-office" } }
              ],
              "hostServices": { "containers": [ { "name": "n8n" } ], "restartPolicies": { "n8n": "always" } },
              "master": { "masterInstanceId": "x", "epoch": 1 },
              "nodes": [ { "id": "dgx", "uid": "u", "address": "https://127.0.0.1:9443" } ]
            }
            """));

        Assert.Equal(BackupDocument.CurrentSchemaVersion, parsed.SchemaVersion);
        Assert.Null(parsed.Master);
        Assert.Null(parsed.Nodes);
        Assert.All(parsed.Models, m => Assert.Null(m.Placement));
        Assert.Equal("n8n", Assert.Single(parsed.HostServicesFor("dgx")!.Containers!).Name);

        var placed = parsed.WithDefaultPlacement("dgx");
        Assert.All(placed.Models, m => Assert.Equal(new ModelPlacement("dgx"), m.Placement));

        // A version-2 document keeps an unplaced model unplaced (design §5.1).
        var v2 = new BackupDocument { SchemaVersion = BackupDocument.MultiNodeSchemaVersion, Models = [OnDgx with { Placement = null }] };
        Assert.Null(Assert.Single(v2.WithDefaultPlacement("dgx").Models).Placement);
    }

    [Fact]
    public void V1_RoundTrip_OfAVersion1Document()
    {
        var v1 = new BackupDocument
        {
            CreatedAt = new DateTimeOffset(2026, 9, 1, 3, 0, 0, TimeSpan.Zero),
            HostName = "dgx",
            Models = [OnDgx with { Placement = null }],
            HostServices = new HostServicesBackup(new Dictionary<string, string> { ["n8n"] = "always" }, [new ManagedContainerOptions { Name = "n8n" }]),
        };

        var parsed = BackupService.Parse(BackupService.Serialize(v1));

        Assert.Equal(1, parsed.SchemaVersion);
        Assert.Equal("alpha", Assert.Single(parsed.Models).Id);
        Assert.Equal("always", parsed.HostServicesFor("dgx")!.RestartPolicies!["n8n"]);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 2, "models": [], "nodes": [ { "id": "dgx", "uid": "u1", "address": "a" }, { "id": "dgx", "uid": "u2", "address": "b" } ] }""", "node 'dgx' twice")]
    [InlineData("""{ "schemaVersion": 2, "models": [], "nodes": [ { "id": "Not A Node", "uid": "u1", "address": "a" } ] }""", "valid id")]
    [InlineData("""{ "schemaVersion": 2, "models": [], "nodes": [ { "id": "a", "uid": "same", "address": "a" }, { "id": "b", "uid": "same", "address": "b" } ] }""", "uid")]
    [InlineData("""{ "schemaVersion": 2, "models": [], "nodes": [ { "id": "a", "uid": "u", "address": " " } ] }""", "no address")]
    [InlineData("""{ "schemaVersion": 2, "models": [ { "id": "a", "servedName": "A", "modelPath": "A", "placement": { "nodeId": "Bad/Node" } } ] }""", "not a valid node id")]
    [InlineData("""{ "schemaVersion": 2, "models": [], "master": { "masterInstanceId": "a/b", "epoch": 1 } }""", "master section")]
    [InlineData("""{ "schemaVersion": 2, "models": [], "master": { "masterInstanceId": "a", "epoch": -1 } }""", "master section")]
    [InlineData("""{ "schemaVersion": 2, "models": [], "hostServices": { "nodes": { "Bad Node": { "containers": [] } } } }""", "Services lists")]
    public void V2_Parse_RefusesInvalidMultinodeSections(string content, string expected)
    {
        var ex = Assert.Throws<BackupFormatException>(() => BackupService.Parse(Encoding.UTF8.GetBytes(content)));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_V1_PlacesEveryModelOnTheBootstrapNode_V2_KeepsPlacements()
    {
        var registry = new FakeModelRegistry();
        var folders = new FakeCheckpointFolders("A", "B");
        var restores = new RestoreJobService(registry, new FakeApiKeyStore(), new ScriptedRuntime([]), new SequencedDownloader(folders), folders,
            new RestoreJobOptions { PollInterval = TimeSpan.FromMilliseconds(1) }, TimeProvider.System, NullLogger<RestoreJobService>.Instance);
        var a = new ModelDefinition { Id = "a", ServedName = "A", ModelPath = "A" };
        var b = new ModelDefinition { Id = "b", ServedName = "B", ModelPath = "B" };

        restores.Start(new RestoreRequest(new BackupDocument { Models = [a, b] }, null, ["a", "b"], true, false, false));
        await restores.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(await registry.ListAsync(), m => Assert.Equal(new ModelPlacement("dgx"), m.Placement));

        restores.Clear();
        var v2 = new BackupDocument
        {
            SchemaVersion = BackupDocument.MultiNodeSchemaVersion,
            Models = [a with { Placement = new ModelPlacement("pc-office") }, b],
        };
        restores.Start(new RestoreRequest(v2, null, ["a", "b"], true, false, false));
        await restores.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new ModelPlacement("pc-office"), (await registry.GetAsync("a"))!.Placement);
        Assert.Null((await registry.GetAsync("b"))!.Placement);
    }

    [Fact]
    public async Task Restore_V2_ImportsTheBootstrapNodesServicesList()
    {
        var source = await SeededServicesAsync();
        var document = await Create(source).CreateAsync(passphrase: null);
        var target = new HostServicesStore(Path.Combine(_dataDir, "target", "host-services.json"));
        var folders = new FakeCheckpointFolders("Alpha-9B", "Beta-4B");
        var restores = new RestoreJobService(new FakeModelRegistry(), new FakeApiKeyStore(), new ScriptedRuntime([]), new SequencedDownloader(folders), folders,
            new RestoreJobOptions { PollInterval = TimeSpan.FromMilliseconds(1) }, TimeProvider.System, NullLogger<RestoreJobService>.Instance, target);

        restores.Start(new RestoreRequest(document, null, [], true, false, ImportHostServices: true));
        await restores.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var list = await target.GetContainersAsync([new ManagedContainerOptions { Name = "seed-default" }]);
        Assert.Equal("openviking", Assert.Single(list).Name);
        Assert.Contains(restores.Current!.Notes, n => n.Contains("1 managed container restored", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_V1AgainstPlacedModelsHere_IsIdentical_AndCountsTheBootstrapNodesServices()
    {
        var here = new ModelDefinition { Id = "a", ServedName = "A", ModelPath = "A", Placement = new ModelPlacement("dgx") };
        var planner = new RestorePlanner(new FakeModelRegistry([here]), new FakeApiKeyStore(), new FakeCheckpointFolders("A"));
        var v1 = new BackupDocument { Models = [here with { Placement = null }] };

        var preview = await planner.PreviewAsync(v1, secrets: null);

        Assert.Equal(RestoreModelChange.Identical, Assert.Single(preview.Models).Change);
        Assert.Equal(new ModelPlacement("dgx"), preview.Models[0].Model.Placement);

        var v2 = new BackupDocument
        {
            SchemaVersion = BackupDocument.MultiNodeSchemaVersion,
            Models = [here with { Placement = new ModelPlacement("pc-office") }],
            HostServices = new HostServicesBackup(null, null)
            {
                Nodes = new Dictionary<string, HostServicesBackup>
                {
                    ["dgx"] = new(null, [new ManagedContainerOptions { Name = "n8n" }, new ManagedContainerOptions { Name = "openviking" }]),
                    ["pc-office"] = new(null, [new ManagedContainerOptions { Name = "other" }]),
                },
            },
        };

        var moved = await planner.PreviewAsync(v2, secrets: null);

        Assert.Equal(["node placement"], Assert.Single(moved.Models).ChangedFields);
        Assert.Equal(2, moved.HostContainers);
    }
}
