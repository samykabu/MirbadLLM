using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using FakeReauth = MirbadLLM.UnitTests.HostServices.FakeReauth;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-032: the master-side remote implementations over <see cref="FakeAgent"/> through the real client factory, operation
/// runner and reference store: host units and containers behind <see cref="HostServiceManager"/> (admin re-auth, typed
/// confirmations and the container whitelist stay on the master), the backup mount, Hugging Face downloads (token only in
/// memory), folders (placed-model check before the agent), checkpoint presence and the per-node vLLM catalog.
/// </summary>
public sealed class RemoteServicesTests
{
    private const string NodeId = "dgx";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Host units through HostServiceManager ----

    [Fact]
    public async Task List_ShowsTheNodesUnitsAndWhitelistedContainers()
    {
        await using var env = await Env.StartAsync();

        var snapshot = await env.Manager.ListAsync(Ct);

        Assert.True(snapshot.HostAgentAvailable);
        var unit = Assert.Single(snapshot.Services, s => s.Name == AgentContractSeed.Service);
        Assert.Equal(HostServiceKind.System, unit.Kind);
        Assert.Equal(HostServiceState.Running, unit.State);
        Assert.True(unit.Autostart);
        Assert.True(Assert.Single(snapshot.Services, s => s.Name == AgentContractSeed.ProtectedService).Protected);
        var container = Assert.Single(snapshot.Services, s => s.Kind == HostServiceKind.Container);
        Assert.Equal(AgentContractSeed.Container, container.Name);
        Assert.Equal(HostServiceState.Running, container.State);
        Assert.Equal("unless-stopped", container.AutostartDetail);
    }

    [Fact]
    public async Task Discover_ListsUnitsAndContainers_WithoutTheAgentsOrMastersContainers()
    {
        await using var env = await Env.StartAsync();

        var discovery = await env.Manager.DiscoverAsync(Ct);

        Assert.True(discovery.HostAgentAvailable);
        Assert.Contains(discovery.Services, s => s.Name == "contract-extra" && !s.Managed);
        Assert.Contains(discovery.Services, s => s.Name == "systemd-journald" && s.Denied);
        Assert.Contains(discovery.Services, s => s.Name == AgentContractSeed.Container && s.Managed);
        Assert.DoesNotContain(discovery.Services, s => s.Name is "mirbadllm" or "mirbadllm-agent");
    }

    [Fact]
    public async Task ProtectedUnit_NeedsTheTypedNameOnTheMaster_BeforeAnythingIsSent()
    {
        await using var env = await Env.StartAsync();

        await Assert.ThrowsAsync<HostServiceConfirmationRequiredException>(
            () => env.Manager.RunAsync(AgentContractSeed.ProtectedService, HostServiceAction.Stop, confirm: null, Ct));
        Assert.Empty(await env.Store.ListAsync(Ct));

        var stopped = await env.Manager.RunAsync(AgentContractSeed.ProtectedService, HostServiceAction.Stop, AgentContractSeed.ProtectedService, Ct);

        Assert.Equal(HostServiceState.Stopped, stopped.State);
        var reference = Assert.Single(await env.Store.ListAsync(Ct));
        Assert.Equal(OperationKinds.ServiceStop, reference.Action);
        Assert.Equal(AgentOperationOutcome.Succeeded, reference.Outcome);
    }

    [Fact]
    public async Task UnitAutostart_RoundTripsThroughTheAgent()
    {
        await using var env = await Env.StartAsync();

        var off = await env.Manager.SetAutostartAsync(AgentContractSeed.Service, enabled: false, ct: Ct);
        var on = await env.Manager.SetAutostartAsync(AgentContractSeed.Service, enabled: true, ct: Ct);

        Assert.False(off.Autostart);
        Assert.Equal("disabled", off.AutostartDetail);
        Assert.True(on.Autostart);
    }

    [Fact]
    public async Task UnknownUnit_IsNotFound()
    {
        await using var env = await Env.StartAsync();

        await Assert.ThrowsAsync<HostServiceNotFoundException>(() => env.Manager.RunAsync("nope", HostServiceAction.Start, ct: Ct));
    }

    [Fact]
    public async Task WhitelistAdd_RequiresASessionOnTheMaster_ThenAddsOnTheNode()
    {
        await using var env = await Env.StartAsync();
        var add = new HostServiceWhitelistAdd(HostServiceKind.System, "contract-extra.service", "Extra", false, "contract-extra", "wrong", "client");

        await Assert.ThrowsAsync<AdminReauthenticationException>(() => env.Manager.AddToWhitelistAsync(add, Ct));
        Assert.Empty(await env.Store.ListAsync(Ct));

        var added = await env.Manager.AddToWhitelistAsync(add with { AdminPassword = null, User = TestAdmin.User }, Ct);

        Assert.Equal("contract-extra", added.Name);
        Assert.Contains((await env.Manager.ListAsync(Ct)).Services, s => s.Name == "contract-extra");
    }

    [Fact]
    public async Task WhitelistAdd_OfADeniedUnit_IsRefusedByTheNode()
    {
        await using var env = await Env.StartAsync();
        var add = new HostServiceWhitelistAdd(HostServiceKind.System, "systemd-journald.service", null, false, "systemd-journald", "s3cret", "client") { User = TestAdmin.User };

        await Assert.ThrowsAsync<HostServiceDeniedException>(() => env.Manager.AddToWhitelistAsync(add, Ct));

        var reference = Assert.Single(await env.Store.ListAsync(Ct));
        Assert.Equal(AgentOperationOutcome.Refused, reference.Outcome); // a 4xx: nothing ran (D41)
    }

    [Fact]
    public async Task WhitelistUpdateAndRemove_GoToTheNode()
    {
        await using var env = await Env.StartAsync();

        var renamed = await env.Manager.UpdateWhitelistAsync(
            AgentContractSeed.Service, new HostServiceWhitelistUpdate("Renamed", null, null, null, "client"), Ct);
        await env.Manager.RemoveFromWhitelistAsync(AgentContractSeed.Service, Ct);

        Assert.Equal("Renamed", renamed.DisplayName);
        Assert.DoesNotContain((await env.Manager.ListAsync(Ct)).Services, s => s.Name == AgentContractSeed.Service);
    }

    [Fact]
    public async Task UnknownOutcome_SurfacesTheD42Message_AndKeepsTheReferenceOpen()
    {
        await using var env = await Env.StartAsync();
        env.Agent.SetHostdTimingOut(true);

        var ex = await Assert.ThrowsAsync<HostServiceOutcomeUnknownException>(
            () => env.Manager.RunAsync(AgentContractSeed.Service, HostServiceAction.Restart, ct: Ct));

        Assert.StartsWith(HostServiceOutcomeUnknownException.Hint, ex.Message, StringComparison.Ordinal);
        Assert.Contains("did not answer", ex.Message, StringComparison.Ordinal);
        Assert.Equal(NodeId, ex.NodeId);
        var open = Assert.Single(await env.Store.ListOpenAsync(Ct));
        Assert.Equal(ex.OperationId, open.OperationId);
        Assert.Equal(OperationKinds.ServiceRestart, open.Action);

        // Observing it later closes it as Unknown; nothing is replayed.
        var observed = await env.Runner.ObserveAsync(open, Ct);
        Assert.Equal(OperationState.Unknown, observed.Status?.State);
        Assert.Equal(AgentOperationOutcome.Unknown, observed.Reference.Outcome);
    }

    [Fact]
    public async Task HostdDown_IsShownOnThePage_ContainersStillListed()
    {
        await using var env = await Env.StartAsync();
        env.Agent.SetHostdAvailable(false);

        var snapshot = await env.Manager.ListAsync(Ct);

        Assert.False(snapshot.HostAgentAvailable);
        Assert.False(string.IsNullOrEmpty(snapshot.HostAgentError));
        Assert.Contains(snapshot.Services, s => s.Name == AgentContractSeed.Container && s.State == HostServiceState.Running);
        await Assert.ThrowsAsync<HostAgentUnavailableException>(() => env.Manager.RunAsync(AgentContractSeed.Service, HostServiceAction.Start, ct: Ct));
    }

    // ---- Containers through HostServiceManager ----

    [Fact]
    public async Task ContainerActions_UseTheMastersWhitelist()
    {
        await using var env = await Env.StartAsync();

        var stopped = await env.Manager.RunAsync(AgentContractSeed.Container, HostServiceAction.Stop, ct: Ct);
        var started = await env.Manager.RunAsync(AgentContractSeed.Container, HostServiceAction.Start, ct: Ct);

        Assert.Equal(HostServiceState.Stopped, stopped.State);
        Assert.Equal(HostServiceState.Running, started.State);
        Assert.Contains(await env.Store.ListAsync(Ct), r => r.Action == OperationKinds.ContainerStop && r.Target == AgentContractSeed.Container);

        // mirbadllm-agent exists on the node but is not on the master's whitelist: the master never sends anything.
        int before = (await env.Store.ListAsync(Ct)).Count;
        await Assert.ThrowsAnyAsync<HostServiceException>(() => env.Manager.RunAsync("mirbadllm-agent", HostServiceAction.Stop, ct: Ct));
        Assert.Equal(before, (await env.Store.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task ContainerAutostart_RemembersThePolicyOnTheMaster()
    {
        await using var env = await Env.StartAsync();

        var off = await env.Manager.SetAutostartAsync(AgentContractSeed.Container, enabled: false, ct: Ct);
        string? remembered = await env.HostStore.GetAsync(AgentContractSeed.Container, Ct);
        var on = await env.Manager.SetAutostartAsync(AgentContractSeed.Container, enabled: true, ct: Ct);

        Assert.False(off.Autostart);
        Assert.Equal("no", off.AutostartDetail);
        Assert.Equal("unless-stopped", remembered);
        Assert.True(on.Autostart);
        Assert.Equal("unless-stopped", on.AutostartDetail);
    }

    [Fact]
    public async Task ContainerWhitelistAdd_ChecksTheNodesContainer()
    {
        await using var env = await Env.StartAsync(managedContainers: []);
        var add = new HostServiceWhitelistAdd(HostServiceKind.Container, AgentContractSeed.Container, "Tool", true, AgentContractSeed.Container, "s3cret", "client") { User = TestAdmin.User };

        var added = await env.Manager.AddToWhitelistAsync(add, Ct);
        await Assert.ThrowsAsync<HostServiceNotFoundException>(() => env.Manager.AddToWhitelistAsync(
            add with { Unit = "mirbadllm-agent", Confirm = "mirbadllm-agent" }, Ct));

        Assert.Equal(AgentContractSeed.Container, added.Name);
        Assert.True(added.Protected);
        Assert.Equal("local/contract-tool:1", added.Description);
    }

    // ---- Backup mount ----

    [Fact]
    public async Task BackupMount_SetTestRemove_PasswordNeverInOperationsJson()
    {
        await using var env = await Env.StartAsync();
        var client = new RemoteBackupMountClient(NodeId, env.Factory, env.Runner);
        var request = new HostAgentBackupMountRequest { Type = "smb", Server = "nas", Share = "backups", Username = "u", Password = "hunter2-secret" };

        var test = await client.TestBackupMountAsync(request, Ct);
        var mounted = await client.SetBackupMountAsync(request, Ct);
        var read = await client.GetBackupMountAsync(Ct);
        var removed = await client.RemoveBackupMountAsync(Ct);

        Assert.True(test.Ok);
        Assert.True(mounted.Mounted);
        Assert.Equal("//nas/backups", read.Source);
        Assert.False(removed.Configured);
        string references = await File.ReadAllTextAsync(Path.Combine(env.DataDir, "operations.json"), Ct);
        Assert.DoesNotContain("hunter2-secret", references, StringComparison.Ordinal);
        Assert.Contains(OperationKinds.BackupMountSet, references, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackupMount_HostdDown_IsHostAgentUnavailable()
    {
        await using var env = await Env.StartAsync();
        env.Agent.SetHostdAvailable(false);
        var client = new RemoteBackupMountClient(NodeId, env.Factory, env.Runner);

        await Assert.ThrowsAsync<HostAgentUnavailableException>(() => client.GetBackupMountAsync(Ct));
    }

    [Fact]
    public async Task BackupMount_UnknownOutcome_IsSurfaced()
    {
        await using var env = await Env.StartAsync();
        env.Agent.SetHostdTimingOut(true);
        var client = new RemoteBackupMountClient(NodeId, env.Factory, env.Runner);

        var ex = await Assert.ThrowsAsync<HostServiceOutcomeUnknownException>(() => client.SetBackupMountAsync(
            new HostAgentBackupMountRequest { Type = "nfs", Server = "nas", Share = "/volume1/backups" }, Ct));

        Assert.StartsWith(HostServiceOutcomeUnknownException.Hint, ex.Message, StringComparison.Ordinal);
    }

    // ---- Downloads ----

    [Fact]
    public void HuggingFaceRefusals_KeepTodaysExceptionType_OtherInvalidRequestsStayArgumentErrors()
    {
        // W3 review MN-094: today a gated/unknown repo is HuggingFaceApiException (download API 422); the agent answers
        // invalid_request marked with UpstreamRefusalDetails so the master maps it back.
        var marked = MirbadLLM.Agent.Storage.StorageErrors.HuggingFaceRefused("Repository org/gated is gated.");
        Assert.Equal(ErrorCodes.InvalidRequest, marked.Code);
        Assert.Equal(UpstreamRefusalDetails.HuggingFace, marked.Error.ReadDetails<UpstreamRefusalDetails>()?.Source);

        var hf = Assert.IsType<MirbadLLM.NodeRuntime.HuggingFaceApiException>(RemoteAgentErrors.ToStorageException(marked, NodeId));
        Assert.Contains("org/gated is gated", hf.Message, StringComparison.Ordinal);

        var plain = new AgentException(400, new AgentError { Code = ErrorCodes.InvalidRequest, Message = "bad folder" });
        Assert.IsType<ArgumentException>(RemoteAgentErrors.ToStorageException(plain, NodeId));
    }

    [Fact]
    public async Task Download_RunsOnTheNode_TokenOnlyInMemory()
    {
        await using var env = await Env.StartAsync();
        var downloader = new RemoteHuggingFaceDownloader(NodeId, env.Factory, env.Runner);
        const string Token = "hf_supersecret_token_value";

        var started = await downloader.StartAsync(new DownloadRequest("org/Tiny Model", Token: Token), Ct);
        var done = await WaitDownloadAsync(downloader, started.Folder, d => d.State == DownloadState.Completed);

        Assert.Equal("Tiny-Model", started.Folder); // today's folder rule, decided on the master
        Assert.Equal(DownloadState.Completed, done.State);
        Assert.Contains(await downloader.ListAsync(Ct), d => d.Folder == "Tiny-Model");
        string references = await File.ReadAllTextAsync(Path.Combine(env.DataDir, "operations.json"), Ct);
        Assert.DoesNotContain(Token, references, StringComparison.Ordinal);
        var reference = Assert.Single(await env.Store.ListAsync(Ct));
        Assert.Equal(OperationKinds.Download, reference.Action);
        Assert.Equal("Tiny-Model", reference.Target);
    }

    [Fact]
    public async Task Download_Cancel_RunningFinishedAndUnknown()
    {
        await using var env = await Env.StartAsync();
        var downloader = new RemoteHuggingFaceDownloader(NodeId, env.Factory, env.Runner);
        await env.Agent.SetDownloadsHeldAsync(true);

        var started = await downloader.StartAsync(new DownloadRequest(AgentContractSeed.DownloadRepo, Folder: "held"), Ct);
        Assert.True(await downloader.CancelAsync(started.Folder, Ct));
        var cancelled = await WaitDownloadAsync(downloader, "held", d => d.State == DownloadState.Cancelled);

        Assert.Equal(DownloadState.Cancelled, cancelled.State);
        Assert.True(await downloader.CancelAsync("held", Ct)); // already finished: not an error (D39)
        Assert.False(await downloader.CancelAsync("never-started", Ct));
        Assert.Null(await downloader.GetAsync("never-started", Ct));
    }

    [Fact]
    public async Task Download_Conflicts_KeepTodaysExceptions()
    {
        await using var env = await Env.StartAsync();
        var refusing = new RemoteHuggingFaceDownloader(NodeId, env.Factory, env.Runner, DownloadExistingMode.Refuse);
        await env.Agent.SetDownloadsHeldAsync(true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => refusing.StartAsync(new DownloadRequest(AgentContractSeed.DownloadRepo, Folder: AgentContractSeed.ModelFolder), Ct));
        await refusing.StartAsync(new DownloadRequest(AgentContractSeed.DownloadRepo, Folder: "busy"), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => refusing.StartAsync(new DownloadRequest(AgentContractSeed.DownloadRepo, Folder: "busy"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => refusing.StartAsync(new DownloadRequest(" "), Ct));

        // Today's default completes into an existing folder.
        var completing = new RemoteHuggingFaceDownloader(NodeId, env.Factory, env.Runner);
        var status = await completing.StartAsync(new DownloadRequest(AgentContractSeed.DownloadRepo, Folder: AgentContractSeed.ScratchFolder), Ct);
        Assert.Equal(AgentContractSeed.ScratchFolder, status.Folder);
    }

    // ---- Folders and catalog ----

    [Fact]
    public async Task Folders_ListAndInspect()
    {
        await using var env = await Env.StartAsync();
        var folders = new RemoteModelFolders(env.Factory, env.Runner, env.Models);

        var list = await folders.ListFoldersAsync(NodeId, Ct);
        var inspection = await folders.InspectAsync(NodeId, AgentContractSeed.ModelFolder, Ct);

        Assert.Contains(list, f => f.Folder == AgentContractSeed.ModelFolder && f.WeightsGiB == 8 && f.DetectedFormat == "BF16");
        Assert.NotNull(inspection);
        Assert.Equal("Qwen3ForCausalLM", inspection.Architecture);
        Assert.Null(await folders.InspectAsync(NodeId, "missing", Ct));
    }

    [Fact]
    public async Task FolderDelete_RefusesFoldersOfModelsPlacedOnTheNode_BeforeCallingTheAgent()
    {
        await using var env = await Env.StartAsync();
        env.Models.Add(Model("placed", AgentContractSeed.ModelFolder, NodeId));
        env.Models.Add(Model("elsewhere", AgentContractSeed.ScratchFolder, "other-node"));
        var folders = new RemoteModelFolders(env.Factory, env.Runner, env.Models);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => folders.DeleteAsync(NodeId, AgentContractSeed.ModelFolder, AgentContractSeed.ModelFolder, Ct));
        Assert.Contains("placed", ex.Message, StringComparison.Ordinal);
        Assert.Empty(await env.Store.ListAsync(Ct));

        var deleted = await folders.DeleteAsync(NodeId, AgentContractSeed.ScratchFolder, AgentContractSeed.ScratchFolder, Ct);

        Assert.Equal(OperationState.Succeeded, deleted.State);
        Assert.DoesNotContain(await folders.ListEntriesAsync(NodeId, Ct), e => e.Folder == AgentContractSeed.ScratchFolder);
        Assert.Contains(await folders.ListEntriesAsync(NodeId, Ct), e => e.Folder == AgentContractSeed.ModelFolder);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => folders.DeleteAsync(NodeId, "missing", "missing", Ct));
    }

    [Fact]
    public void References_CoverSourcesDraftModelsAndExtraArgs()
    {
        var model = Model("m", "Main/sub", NodeId) with
        {
            Sources = [new ModelSource("org/extra", "Extra")],
            Speculative = new SpeculativeDecoding("eagle", 2) { DraftModel = "/models/Draft" },
            ExtraArgs = ["--tokenizer", "/models/Tok/tokenizer.json", "--x=/models/Toks"],
        };

        Assert.True(RemoteModelFolders.References(model, NodeId, "Main"));
        Assert.True(RemoteModelFolders.References(model, NodeId, "Extra"));
        Assert.True(RemoteModelFolders.References(model, NodeId, "Draft"));
        Assert.True(RemoteModelFolders.References(model, NodeId, "Tok"));
        Assert.True(RemoteModelFolders.References(model, NodeId, "Toks"));
        Assert.False(RemoteModelFolders.References(model, NodeId, "To"));
        Assert.False(RemoteModelFolders.References(model, "other", "Main"));
        Assert.False(RemoteModelFolders.References(model with { Placement = null }, NodeId, "Main"));
    }

    [Fact]
    public async Task CheckpointFolders_ReadPresenceFromTheNode()
    {
        await using var env = await Env.StartAsync();
        var folders = new RemoteCheckpointFolders(NodeId, env.Factory, TimeProvider.System);

        Assert.True(folders.IsPresent(AgentContractSeed.ModelFolder));
        Assert.True(folders.IsPresent(AgentContractSeed.ModelFolder + "/sub"));
        Assert.False(folders.IsPresent("missing"));
        Assert.False(folders.IsPresent("../escape"));

        env.Agent.AddFolder("late");
        Assert.False(folders.IsPresent("late")); // cached list
        await folders.RefreshAsync(Ct);
        Assert.True(folders.IsPresent("late"));
    }

    [Fact]
    public async Task Catalog_TakesImagesFromTheNodesCapabilities()
    {
        await using var env = await Env.StartAsync();
        var record = await env.Registry.GetAsync(NodeId, Ct);
        await env.Registry.UpsertAsync(record! with
        {
            DefaultVllmImage = "local/node-default-vllm:1",
            LastCapabilities = await env.Factory.GetClient(NodeId).GetCapabilitiesAsync(Ct) with
            {
                Images =
                [
                    new ImageInfo { Ref = "local/contract-vllm:1" },
                    new ImageInfo { Ref = "busybox:latest" },
                ],
                Runtimes = [new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = true, Images = ["harbor/custom:2"] }],
            },
        }, Ct);
        var catalog = new RemoteVllmCatalogService(env.Registry, Options.Create(new MirbadLLMOptions()));

        var result = await catalog.GetAsync(NodeId, "local/current-vllm:9", Ct);
        var unknown = await catalog.GetAsync("nope", null, Ct);

        Assert.Equal(["local/current-vllm:9", "local/node-default-vllm:1", "local/contract-vllm:1", "harbor/custom:2"], result.Images);
        Assert.Equal(VllmOptionsCatalog.ToolCallParsers, result.ToolCallParsers);
        Assert.Equal([new MirbadLLMOptions().VllmImage], unknown.Images);
    }

    // ---- Helpers ----

    private static ModelDefinition Model(string id, string path, string nodeId) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = path,
        Placement = new ModelPlacement(nodeId),
    };

    private static async Task<DownloadStatus> WaitDownloadAsync(RemoteHuggingFaceDownloader downloader, string folder, Func<DownloadStatus, bool> done)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (true)
        {
            var status = await downloader.GetAsync(folder, Ct);
            if (status is not null && done(status))
            {
                return status;
            }

            Assert.True(DateTimeOffset.UtcNow < deadline, $"Download '{folder}' did not reach the expected state: {status}");
            await Task.Delay(20, Ct);
        }
    }

    private sealed class MasterIdentity : IMasterIdentity
    {
        public string InstanceId => "mn032-master";

        public long Epoch => 1;

        public string BootId => "mn032-boot";

        public MasterTriple Current => new(InstanceId, Epoch, BootId);

        public bool TakeoverRequested => false;

        public IReadOnlyList<TakeoverRecord> Takeovers => [];

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>A FakeAgent for node <c>dgx</c>, the registry, the real client factory, reference store and runner, and a manager over the remote backends.</summary>
    private sealed class Env : IAsyncDisposable
    {
        private readonly IssuedCredential _master;
        private readonly TrustAnchorSet _trust;

        private Env(FakeAgent agent, string dataDir, IReadOnlyList<ManagedContainerOptions>? managedContainers)
        {
            Agent = agent;
            DataDir = dataDir;
            _master = agent.Ca.IssueMasterClientCertificate();
            _trust = TrustAnchorSet.Create([agent.Ca.Certificate]);
            var options = Options.Create(new MirbadLLMOptions { DataPath = dataDir });
            Registry = new JsonNodeRegistry(options, TimeProvider.System);
            Factory = new AgentClientFactory(Registry, new MasterIdentity(), _master, _trust, TimeProvider.System);
            Store = new JsonAgentOperationReferenceStore(options, TimeProvider.System);
            Runner = new AgentOperationRunner(Factory, Store, Registry, TimeProvider.System);
            HostStore = new HostServicesStore(Path.Combine(dataDir, "host-services.json"));
            var hostOptions = Options.Create(new HostServicesOptions
            {
                ManagedContainers = managedContainers is null
                    ? [new ManagedContainerOptions { Name = AgentContractSeed.Container, DisplayName = "Contract tool" }]
                    : [.. managedContainers],
            });
            if (managedContainers is { Count: 0 })
            {
                // An empty seed would fall back to the built-in defaults; start from an explicitly empty whitelist.
                HostStore.UpdateContainersAsync([], _ => []).GetAwaiter().GetResult();
            }

            Manager = new HostServiceManager(
                new RemoteHostUnitBackend(NodeId, Factory, Runner),
                new RemoteHostContainerBackend(NodeId, Factory, Runner),
                HostStore,
                hostOptions,
                NullLogger<HostServiceManager>.Instance,
                new FakeReauth());
        }

        public FakeAgent Agent { get; }

        public string DataDir { get; }

        public JsonNodeRegistry Registry { get; }

        public AgentClientFactory Factory { get; }

        public JsonAgentOperationReferenceStore Store { get; }

        public AgentOperationRunner Runner { get; }

        public HostServicesStore HostStore { get; }

        public HostServiceManager Manager { get; }

        public FakeModelRegistry Models { get; } = new();

        public static async Task<Env> StartAsync(IReadOnlyList<ManagedContainerOptions>? managedContainers = null)
        {
            var agent = await FakeAgent.StartAsync(ct: Ct);
            string dataDir = Path.Combine(Path.GetTempPath(), "mn032-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);
            var env = new Env(agent, dataDir, managedContainers);
            try
            {
                await env.Registry.UpsertAsync(new NodeRecord
                {
                    Id = NodeId,
                    Uid = agent.NodeUid,
                    Address = agent.BaseAddress.AbsoluteUri,
                    Approved = true,
                    CertSpkiSha256 = agent.CurrentSpki,
                }, Ct);
                await env.Factory.RefreshAsync(Ct);
                return env;
            }
            catch
            {
                await env.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Agent.DisposeAsync();
            Store.Dispose();
            Registry.Dispose();
            HostStore.Dispose();
            _master.Dispose();
            _trust.Dispose();
            try
            {
                Directory.Delete(DataDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}
