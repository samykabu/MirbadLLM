using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.UnitTests.HostServices;
using MirbadLLM.UnitTests.Orchestration;
using MirbadLLM.UnitTests.Router.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Recovery;

/// <summary>
/// "Backup and restore everything": running models, the backup share (password only in the encrypted section), managed
/// node services, benchmark/tuning/start-time history and the agent-update setting travel in the file and come back, and
/// older files without them still read.
/// </summary>
public sealed class BackupEverythingTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";
    private const string SharePassword = "nas-secret-Pa55";

    private static readonly RestoreJobOptions FastOptions = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(1),
        StartRetryInterval = TimeSpan.FromMilliseconds(5),
        StartRetryTimeout = TimeSpan.FromSeconds(10),
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-everything-" + Guid.NewGuid().ToString("N"));
    private readonly EphemeralDataProtectionProvider _protection = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Dir(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static ModelDefinition Model(string id, bool autoStart = false) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = id + "-ckpt",
        MemoryBudgetGiB = 10,
        AutoStart = autoStart,
        Sources = [new ModelSource("o/" + id, id + "-ckpt")],
    };

    private static void WriteRun(string dataPath, string kind, string modelId, string id, string marker)
    {
        string folder = Path.Combine(dataPath, kind, modelId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, id + ".json"), $$"""{"id":"{{id}}","modelId":"{{modelId}}","marker":"{{marker}}"}""");
    }

    private (BackupDestinationManager Manager, FakeBackupMountClient Client, FakeReauth Reauth) Destination(string dataPath, FakeBackupMountClient? client = null)
    {
        client ??= new FakeBackupMountClient { Mount = FakeBackupMountClient.Configured(mounted: true) };
        var reauth = new FakeReauth();
        var directory = new BackupDirectory(Options.Create(new BackupOptions { Directory = "/backups" }),
            () => "650 600 0:61 / /backups rw - cifs //192.0.2.200/backups/mirbadllm rw");
        var manager = new BackupDestinationManager(client, directory, NullLogger<BackupDestinationManager>.Instance, reauth,
            new BackupDestinationSecretStore(dataPath, _protection));
        return (manager, client, reauth);
    }

    private static BackupDestinationRequest Smb() => new()
    {
        Kind = BackupDestinationKind.Smb,
        Server = "192.0.2.200",
        Share = "backups",
        Subfolder = "mirbadllm",
        Username = "dgx-backup",
        Password = SharePassword,
    };

    [Fact]
    public async Task Backup_RoundTripsEverything_AndTheSharePasswordOnlyInsideTheEncryptedSection()
    {
        string data = Dir("source");
        var registry = new FakeModelRegistry([Model("a", autoStart: true), Model("b")]);
        ModelRecoveryState.WriteSeed(data, [("pc", "pc-uid", ["b"])]);
        var recovery = new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = data }));
        var updates = new AgentUpdateSettingsStore(Path.Combine(data, AgentUpdateSettingsStore.FileName));
        updates.SetAutomatic(true);
        var times = new StartupTimeTracker(Options.Create(new MirbadLLMOptions { DataPath = data }));
        times.Record("a", TimeSpan.FromSeconds(95));
        WriteRun(data, "benchmarks", "a", "20261001T030000Z", "bench");
        WriteRun(data, "tuning", "a", "20261002T030000Z", "tune");
        var (destinations, _, _) = Destination(data);
        await destinations.ApplyAsync(Smb(), TestAdmin.User, "test");
        var units = new FakeNodeServices();
        units.Manager("dgx").Units.Add(Unit("ollama", "ollama.service", isProtected: true));

        var service = new BackupService(registry, new FakeApiKeyStore(), Options.Create(new BackupOptions { HostName = "dgx" }), TimeProvider.System,
            destinations: destinations, recovery: recovery, agentUpdates: updates, dataPath: data, startupTimes: times, hostServiceManagers: units);
        byte[] bytes = BackupService.Serialize(await service.CreateAsync(Passphrase));
        var parsed = BackupService.Parse(bytes);
        var secrets = BackupService.DecryptSecrets(parsed, Passphrase)!;

        Assert.DoesNotContain(SharePassword, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(["b"], parsed.RunningModels!["pc"]);
        Assert.True(parsed.WasRunning("b"));
        Assert.False(parsed.WasRunning("a"));
        Assert.True(parsed.AgentUpdatesAutomatic);
        Assert.Equal("bench", parsed.History!.Benchmarks.Single().Run.GetProperty("marker").GetString());
        Assert.Equal("20261002T030000Z", parsed.History.Tuning.Single().Id);
        Assert.Equal(95, parsed.History.StartupTimes["a"]);
        var unit = Assert.Single(parsed.HostUnits!["dgx"]);
        Assert.Equal(("ollama", "ollama.service", true), (unit.Name, unit.Unit, unit.Protected));
        Assert.Equal("//192.0.2.200/backups/mirbadllm", parsed.BackupDestination!.Source);
        Assert.Equal((BackupDestinationKind.Smb, "192.0.2.200", "backups", SharePassword),
            (secrets.BackupDestination!.Kind, secrets.BackupDestination.Server, secrets.BackupDestination.Share, secrets.BackupDestination.Password));
        Assert.DoesNotContain(SharePassword, secrets.BackupDestination.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SharePassword, secrets.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SharePassword, File.ReadAllText(Path.Combine(data, BackupDestinationSecretStore.FileName)), StringComparison.Ordinal);

        // Without a passphrase nothing secret is written, the share password included.
        var plain = BackupService.Parse(BackupService.Serialize(await service.CreateAsync(null)));
        Assert.Null(plain.Secrets);
        Assert.NotNull(plain.BackupDestination);
    }

    [Fact]
    public void Parse_OlderFilesWithoutTheNewSections_StillRead_AndBadNodeIdsAreRefused()
    {
        const string v2 = """{"schemaVersion":2,"createdAt":"2026-10-01T00:00:00Z","models":[{"id":"a","servedName":"a","modelPath":"A"}],"nodes":[]}""";
        var parsed = BackupService.Parse(Encoding.UTF8.GetBytes(v2));
        Assert.Null(parsed.RunningModels);
        Assert.Null(parsed.History);
        Assert.Null(parsed.HostUnits);
        Assert.Null(parsed.AgentUpdatesAutomatic);
        Assert.False(parsed.WasRunning("a"));

        const string v1 = """{"schemaVersion":1,"models":[{"id":"a","servedName":"a","modelPath":"A"}],"runningModels":{"dgx":["a"]}}""";
        Assert.Null(BackupService.Parse(Encoding.UTF8.GetBytes(v1)).RunningModels); // version 1 has no node sections

        const string bad = """{"schemaVersion":2,"models":[],"runningModels":{"../etc":["a"]}}""";
        Assert.Throws<BackupFormatException>(() => BackupService.Parse(Encoding.UTF8.GetBytes(bad)));
    }

    [Fact]
    public void WriteSeed_MakesTheFirstSnapshotQueueTheModelsThatAreNotRunning()
    {
        string data = Dir("seed");
        ModelRecoveryState.WriteSeed(data, [("pc", "pc-uid", ["a", "b"])]);
        var state = new ModelRecoveryState(Options.Create(new MirbadLLMOptions { DataPath = data }));

        var node = ModelRecoveryStateTests.Node("real-boot", "a");
        state.Observe(node);

        Assert.Equal(["b"], state.Pending(node)); // "a" runs already; "b" is recovered by the health monitor
        Assert.Equal(["a", "b"], state.RunningModels()["pc"]);
    }

    private sealed record Job(RestoreJobService Service, FakeModelRegistry Registry, ScriptedRuntime Runtime, List<string> Events);

    private static Job CreateJob(
        IBackupDestinationManager? destinations = null, string? dataPath = null, StartupTimeTracker? times = null,
        AgentUpdateSettingsStore? updates = null, INodeHostServiceManagers? units = null)
    {
        var events = new List<string>();
        var folders = new FakeCheckpointFolders("a-ckpt", "b-ckpt", "c-ckpt");
        var runtime = new ScriptedRuntime(events);
        var registry = new FakeModelRegistry([]);
        var service = new RestoreJobService(registry, new FakeApiKeyStore(), runtime, new SequencedDownloader(folders), folders, FastOptions,
            TimeProvider.System, NullLogger<RestoreJobService>.Instance,
            destinations: destinations, agentUpdates: updates, dataPath: dataPath, startupTimes: times, hostServiceManagers: units);
        return new Job(service, registry, runtime, events);
    }

    private static async Task<RestoreJobSnapshot> RunAsync(RestoreJobService service, RestoreRequest request)
    {
        service.Start(request);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return service.Current!;
    }

    private static BackupDocument RunningBackup() => new()
    {
        Models = [Model("a", autoStart: true), Model("b"), Model("c")],
        RunningModels = new Dictionary<string, IReadOnlyList<string>> { ["dgx"] = ["b"] },
    };

    [Fact]
    public async Task ConfigurationImport_StartsTheModelsThatWereRunning_LikeAutoStartOnes()
    {
        var h = CreateJob();
        var job = await RunAsync(h.Service, new RestoreRequest(RunningBackup(), null, ["a", "b", "c"], true, false, false));

        Assert.Equal(["start:a", "start:b"], h.Events);
        Assert.Equal(RestoreModelState.Started, job.Models.Single(m => m.Id == "b").State);
        Assert.Equal(RestoreModelState.Ready, job.Models.Single(m => m.Id == "c").State);
    }

    [Fact]
    public async Task ConfigurationImport_WithoutStartRunning_StartsOnlyAutoStartModels()
    {
        var h = CreateJob();
        await RunAsync(h.Service, new RestoreRequest(RunningBackup(), null, ["a", "b", "c"], true, false, false, StartRunning: false));

        Assert.Equal(["start:a"], h.Events);
    }

    [Fact]
    public async Task ConfigurationImport_AddsHistoryNotHereYet_AndKeepsLocalResults()
    {
        string data = Dir("history");
        WriteRun(data, "benchmarks", "a", "20261001T030000Z", "local");
        var times = new StartupTimeTracker(Options.Create(new MirbadLLMOptions { DataPath = data }));
        times.Record("a", TimeSpan.FromSeconds(60));
        var history = new BackupHistory(
            [
                new BackupRunFile("a", "20261001T030000Z", JsonDocument.Parse("""{"marker":"backup"}""").RootElement.Clone()),
                new BackupRunFile("a", "20260901T030000Z", JsonDocument.Parse("""{"marker":"older"}""").RootElement.Clone()),
                new BackupRunFile("..", "evil", JsonDocument.Parse("""{"marker":"x"}""").RootElement.Clone()),
            ],
            [new BackupRunFile("b", "20261003T030000Z", JsonDocument.Parse("""{"marker":"tune"}""").RootElement.Clone())],
            new Dictionary<string, double> { ["a"] = 300, ["b"] = 120 });
        var h = CreateJob(dataPath: data, times: times);

        var job = await RunAsync(h.Service, new RestoreRequest(new BackupDocument { Models = [], History = history }, null, [], true, false, false));

        Assert.Contains("local", File.ReadAllText(Path.Combine(data, "benchmarks", "a", "20261001T030000Z.json")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(data, "benchmarks", "a", "20260901T030000Z.json")));
        Assert.True(File.Exists(Path.Combine(data, "tuning", "b", "20261003T030000Z.json")));
        Assert.False(Directory.Exists(Path.Combine(data, "evil")));
        Assert.Equal(TimeSpan.FromSeconds(60), times.GetLast("a"));
        Assert.Equal(TimeSpan.FromSeconds(120), times.GetLast("b"));
        Assert.Contains(job.Notes, n => n.Contains("2 benchmark or tuning result(s) and 1 start time(s) added", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfigurationImport_AppliesTheDestinationThroughTheManager_OnlyWithTheSession()
    {
        string data = Dir("destination");
        var (manager, client, _) = Destination(data, new FakeBackupMountClient());
        var h = CreateJob(destinations: manager);
        var backup = new BackupDocument { Models = [] };

        var refused = await RunAsync(h.Service, new RestoreRequest(backup, null, [], true, false, false, ApplyDestination: Smb()));
        Assert.Empty(client.Sent);
        Assert.Contains(refused.Notes, n => n.StartsWith("Backup destination not applied", StringComparison.Ordinal));

        h.Service.Clear();
        var applied = await RunAsync(h.Service, new RestoreRequest(backup, null, [], true, false, false, ApplyDestination: Smb()) { User = TestAdmin.User });
        var sent = Assert.Single(client.Sent);
        Assert.Equal(("smb", "192.0.2.200", SharePassword), (sent.Type, sent.Server, sent.Password));
        Assert.Contains(applied.Notes, n => n.StartsWith("Backup destination:", StringComparison.Ordinal));
        Assert.DoesNotContain(SharePassword, string.Join(" ", applied.Notes), StringComparison.Ordinal);
        Assert.DoesNotContain(SharePassword, new RestoreRequest(backup, null, [], true, false, false, ApplyDestination: Smb()).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfigurationImport_AddsBackMissingManagedServices_WithTheSession_AndSetsAgentUpdates()
    {
        var units = new FakeNodeServices();
        units.Manager("dgx").Units.Add(Unit("ollama", "ollama.service", isProtected: false));
        var updates = new AgentUpdateSettingsStore(Path.Combine(Dir("updates"), AgentUpdateSettingsStore.FileName));
        var h = CreateJob(units: units, updates: updates);
        var backup = new BackupDocument
        {
            Models = [],
            AgentUpdatesAutomatic = true,
            HostUnits = new Dictionary<string, IReadOnlyList<BackupHostUnit>>
            {
                ["dgx"] = [new("ollama", "ollama.service", HostServiceKind.System, "Ollama", false), new("comfy", "comfyui.service", HostServiceKind.User, "ComfyUI", true)],
            },
        };

        var job = await RunAsync(h.Service, new RestoreRequest(backup, null, [], true, false, true) { User = TestAdmin.User });

        var added = Assert.Single(units.Manager("dgx").Added);
        Assert.Equal((HostServiceKind.User, "comfyui.service", "comfy", true), (added.Kind, added.Unit, added.Confirm, added.Protected));
        Assert.True(added.User!.Identity!.IsAuthenticated); // the restoring admin's session, checked by the manager as on the Services page
        Assert.Contains(job.Notes, n => n.Contains("Managed services of 'dgx': 1 added, 1 already managed", StringComparison.Ordinal));
        Assert.True(updates.Automatic);
    }

    private static HostServiceStatus Unit(string name, string unit, bool isProtected) =>
        new(name, name, "", HostServiceKind.System, unit, isProtected, null, HostServiceState.Running, "", true, "", null, null, null);

    /// <summary>Per-node Services managers that record whitelist additions.</summary>
    internal sealed class FakeNodeServices : INodeHostServiceManagers
    {
        private readonly Dictionary<string, FakeServices> _managers = new(StringComparer.Ordinal);

        public FakeServices Manager(string nodeId) =>
            _managers.TryGetValue(nodeId, out var manager) ? manager : _managers[nodeId] = new FakeServices();

        public Task<IHostServiceManager?> ForNodeAsync(string nodeId, CancellationToken ct = default) =>
            Task.FromResult<IHostServiceManager?>(Manager(nodeId));
    }

    internal sealed class FakeServices : IHostServiceManager
    {
        public List<HostServiceStatus> Units { get; } = [];

        public List<HostServiceWhitelistAdd> Added { get; } = [];

        public Task<HostServicesSnapshot> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(new HostServicesSnapshot([.. Units], true, null));

        public Task<HostServiceStatus> AddToWhitelistAsync(HostServiceWhitelistAdd request, CancellationToken ct = default)
        {
            if (request.User?.Identity?.IsAuthenticated != true)
            {
                throw new AdminReauthenticationException(AdminReauthOutcome.LoginRequired, "Sign in.");
            }

            Added.Add(request);
            var status = new HostServiceStatus(request.Confirm!, request.DisplayName ?? request.Unit, "", request.Kind, request.Unit, request.Protected,
                null, HostServiceState.Running, "", true, "", null, null, null);
            Units.Add(status);
            return Task.FromResult(status);
        }

        public Task<HostServicesDiscovery> DiscoverAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostServiceStatus> UpdateWhitelistAsync(string name, HostServiceWhitelistUpdate request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HostServiceStatus> RunAsync(string name, HostServiceAction action, string? confirm = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<HostServiceStatus> SetAutostartAsync(string name, bool enabled, string? confirm = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
