using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Api;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Ui;
using TestContext = Xunit.TestContext;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA2000 // The fixture disposes its context, operation store and password guard.

public sealed class AgentUpdatePanelTests : IAsyncDisposable
{
    private const string Password = "confirm upgrade";
    private const string NodeId = "compute-1";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly CertificateAuthority Ca = CertificateAuthority.Create();
    private static readonly NodeApiCaller Caller = new("admin:test", "agent-update-tests") { User = TestAdmin.User };
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ss-update-ui-" + Guid.NewGuid().ToString("N"));
    private readonly BunitContext _ctx = new();
    private readonly FakeStatusCache _cache = new();
    private readonly FakeNodeRegistry _nodes = new();
    private readonly AdminPasswordGuard _guard = new();
    private readonly JsonAgentOperationReferenceStore _store;
    private readonly NodeAdminService _admin;
    private readonly AgentOperationRunner _runner;
    private readonly UpdateClient _client;

    public AgentUpdatePanelTests()
    {
        string version = ProductVersion.RevisionOf(ProductVersion.Controller)!;
        AgentBundleFixtures.Write(_root, "linux-x64", version);
        var bundles = new AgentBundleCatalog(_root, NullLogger<AgentBundleCatalog>.Instance);
        _nodes.Records[NodeId] = new NodeRecord { Id = NodeId, Uid = "node-uid", Address = "https://127.0.0.1:9443", Approved = true };
        _cache.Nodes[NodeId] = new NodeLiveStatus
        {
            NodeId = NodeId, NodeUid = "node-uid", State = NodeState.Degraded, AgentVersion = "1.0.0+111111111111",
            Reasons = ["The models volume has 4.5% free."],
            Capabilities = new NodeCapabilities
            {
                Protocol = Protocol.Range, AgentVersion = "1.0.0+111111111111", Os = new NodeOsInfo("linux", "Linux"),
                Arch = "x64", Memory = new NodeMemoryInfo(MemoryModel.Unified, 64), Features = [AgentFeatures.AgentUpdate],
            },
        };
        _store = new JsonAgentOperationReferenceStore(Options.Create(new MirbadLLMOptions { DataPath = _root }), TimeProvider.System);
        var client = DispatchProxy.Create<IAgentClient, UpdateClient>();
        _client = (UpdateClient)(object)client;
        _runner = new AgentOperationRunner(new LocalFoldersAgentClients(client), _store, _nodes, TimeProvider.System);
        _admin = new NodeAdminService(_nodes, new FakeModelRegistry(), Ca, Options.Create(new NodeApiOptions()),
            Options.Create(new MirbadLLM.Web.Enrollment.EnrollmentOptions()), TimeProvider.System, NullLogger<NodeAdminService>.Instance,
            reauth: new AdminReauthenticator(Options.Create(new AuthOptions { AdminPassword = Password })),
            cache: _cache, agentBundles: bundles)
        { UpdateSettings = new AgentUpdateSettingsStore(Path.Combine(_root, "updates.json")) };
        _ctx.Services.AddSingleton<INodeStatusCache>(_cache);
        _ctx.Services.AddSingleton<IAgentOperationReferenceStore>(_store);
        _ctx.Services.AddSingleton(_runner);
        _ctx.Services.AddSingleton(bundles);
    }

    [Theory]
    [InlineData(NodeState.Online, true)]
    [InlineData(NodeState.Degraded, true)]
    [InlineData(NodeState.Offline, false)]
    [InlineData(NodeState.PendingApproval, false)]
    [InlineData(NodeState.Incompatible, false)]
    [InlineData(NodeState.PendingTakeover, false)]
    [InlineData(NodeState.Revoked, false)]
    public void OnlyReachableNodesCanBeUpdated(NodeState state, bool allowed)
    {
        var node = _cache.Nodes[NodeId] with { State = state };
        Assert.Equal(allowed, _admin.AgentUpdateBlocker(node) is null);
    }

    [Fact]
    public void HelperCurrentBuildAndOpenUpdateGuardsRemainInEffect()
    {
        var node = _cache.Nodes[NodeId];
        Assert.Contains("update helper", _admin.AgentUpdateBlocker(node with { Capabilities = node.Capabilities! with { Features = [] } })!, StringComparison.Ordinal);
        Assert.Contains("already runs", _admin.AgentUpdateBlocker(node with { AgentVersion = ProductVersion.Controller })!, StringComparison.Ordinal);
        Assert.Contains("already running", _admin.AgentUpdateBlocker(node with { OpenOperations = [Operation(OperationStages.Downloading)] })!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DegradedNodeCanSubmitASignedUpdateWithSessionAndTrackIt()
    {
        var cut = Render();
        Assert.False(cut.Find("[data-testid=agent-update-start]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("input[type=password]"));
        Assert.Contains("low disk space", cut.Find("[data-testid=agent-update-health-note]").TextContent, StringComparison.Ordinal);
        Assert.False(cut.Find("[data-testid=agent-update-manual]").HasAttribute("open"));
        Assert.False(cut.Find("[data-testid=agent-update-changes]").HasAttribute("open"));

        cut.Find("[data-testid=agent-update-start]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=agent-update-running]")));
        Assert.NotNull(_client.Request);
        Assert.NotEmpty(_client.Request.Signature);
        Assert.Equal("node-uid", AgentUpdateOrder.Parse(_client.Request.Order)!.NodeUid);
        Assert.Equal(_client.Request.OperationId, Assert.Single(await _store.ListOpenAsync(Ct)).OperationId);
        Assert.DoesNotContain(Password, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticUpdatesUseTheSameDegradedNodeEligibility()
    {
        _admin.UpdateSettings!.SetAutomatic(true);
        var updater = new AgentAutoUpdater(_admin, _runner, TimeProvider.System, NullLogger<AgentAutoUpdater>.Instance);
        Assert.Equal(NodeId, await updater.TickAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(_client.Request);
    }

    [Fact]
    public async Task ProgressSurvivesPageReloadAndOnlyVerifiesTheReportedBuild()
    {
        await _store.AddAsync(Reference(), Ct);
        _cache.Nodes[NodeId] = _cache.Nodes[NodeId] with { OpenOperations = [Operation(OperationStages.Downloading)] };
        var cut = Render();
        Assert.Contains("Downloading", cut.Find("[data-testid=agent-update-running]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Download", cut.Find("[aria-current=step]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=agent-update-start]"));

        _cache.Nodes[NodeId] = _cache.Nodes[NodeId] with { OpenOperations = [Operation(OperationStages.Dispatched)] };
        cut.Render(p => p.Add(x => x.Row, Row()));
        Assert.Contains("Installing", cut.Find("[data-testid=agent-update-running]").TextContent, StringComparison.Ordinal);

        _cache.Nodes[NodeId] = _cache.Nodes[NodeId] with { State = NodeState.Offline, OpenOperations = [] };
        cut.Dispose();
        cut = Render();
        Assert.Contains("reconnect", cut.Find("[data-testid=agent-update-running]").TextContent, StringComparison.Ordinal);
        Assert.Contains("op-update", cut.Find("[data-testid=agent-update-operations]").ParentElement!.TextContent, StringComparison.Ordinal);

        await _store.CloseAsync("op-update", AgentOperationOutcome.Succeeded, null, Ct);
        _cache.Nodes[NodeId] = _cache.Nodes[NodeId] with { State = NodeState.Degraded };
        cut.Render(p => p.Add(x => x.Row, Row()));
        Assert.Empty(cut.FindAll("[data-testid=agent-update-complete]"));
        _cache.Nodes[NodeId] = _cache.Nodes[NodeId] with { AgentVersion = ProductVersion.Controller };
        cut.Render(p => p.Add(x => x.Row, Row()));
        cut.WaitForAssertion(() => Assert.Contains("Agent build verified", cut.Find("[data-testid=agent-update-complete]").TextContent, StringComparison.Ordinal));
        Assert.Contains("Build verified", cut.Find("[aria-current=step]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Node health is still Degraded", cut.Find("[data-testid=agent-update-health-remaining]").TextContent, StringComparison.Ordinal);
        Assert.Equal("nodes/compute-1#node-attention-title", cut.Find("[data-testid=agent-update-health-remaining] a").GetAttribute("href"));
        var module = _ctx.JSInterop.SetupModule("./interop.js");
        module.SetupVoid("showNodeHealth").SetVoidResult();
        cut.Find("[data-testid=agent-update-health-remaining] a").Click();
        Assert.Single(module.Invocations["showNodeHealth"]);
        Assert.Equal(NodeState.Degraded, _cache.Nodes[NodeId].State);

        _cache.Nodes[NodeId] = _cache.Nodes[NodeId] with { State = NodeState.Online, Reasons = [] };
        cut.Render(p => p.Add(x => x.Row, Row()));
        Assert.NotEmpty(cut.FindAll("[data-testid=agent-update-complete]"));
        Assert.Empty(cut.FindAll("[data-testid=agent-update-health-remaining]"));
    }

    [Fact]
    public async Task FailureShowsItsReasonAndAllowsRetryButOldNodeIdentityDoesNotLeak()
    {
        await _store.AddAsync(Reference(), Ct);
        await _store.CloseAsync("op-update", AgentOperationOutcome.Failed, "installer rolled back", Ct);
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Contains("installer rolled back", cut.Find("[data-testid=agent-update-result]").TextContent, StringComparison.Ordinal));
        Assert.Contains("Retry update", cut.Find("[data-testid=agent-update-start]").TextContent, StringComparison.Ordinal);
        _nodes.Records[NodeId] = _nodes.Records[NodeId] with { Uid = "new-identity" };
        cut.Render(p => p.Add(x => x.Row, Row()));
        Assert.Empty(cut.FindAll("[data-testid=agent-update-result]"));
    }

    [Fact]
    public async Task PreviousBuildsSuccessfulUpdateDoesNotBlockTheNextUpdate()
    {
        await _store.AddAsync(Reference() with { Target = "222222222222" }, Ct);
        await _store.CloseAsync("op-update", AgentOperationOutcome.Succeeded, null, Ct);
        var cut = Render();
        Assert.Empty(cut.FindAll("[data-testid=agent-update-running]"));
        Assert.Contains("Update now", cut.Find("[data-testid=agent-update-start]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableProgressIsVisibleInsteadOfCrashingThePanel()
    {
        File.WriteAllText(Path.Combine(_root, AgentOperationReferenceDocument.FileName), "invalid json");
        var cut = Render();
        cut.WaitForAssertion(() => Assert.Contains("Could not read", cut.Find("[data-testid=agent-update-progress-error]").TextContent, StringComparison.Ordinal));
    }

    private Nodes.NodeRow Row() => Nodes.Merge(_nodes.Records[NodeId], _cache.Nodes[NodeId], []);
    private IRenderedComponent<AgentUpdatePanel> Render() => _ctx.Render<AgentUpdatePanel>(p => p
        .Add(x => x.Row, Row()).Add(x => x.Admin, _admin).Add(x => x.Caller, Caller));
    private static NodeOperationSummary Operation(string stage) => new("op-update", OperationKinds.AgentUpdate,
        ProductVersion.RevisionOf(ProductVersion.Controller)!, OperationState.Running, stage, Now);
    private static AgentOperationReference Reference() => new()
    {
        OperationId = "op-update", NodeId = NodeId, NodeUid = "node-uid", Action = OperationKinds.AgentUpdate,
        Target = ProductVersion.RevisionOf(ProductVersion.Controller)!, PayloadDigest = "digest",
        IssuedAt = Now, ExpiresAt = Now.AddHours(1),
    };

    public class UpdateClient : DispatchProxy
    {
        internal AgentUpdateRequest? Request { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IAgentClient.UpdateAgentAsync)) throw new NotSupportedException(targetMethod?.Name);
            Request = (AgentUpdateRequest)args![0]!;
            return Task.FromResult(new OperationStatus
            {
                Id = Request.OperationId, Kind = OperationKinds.AgentUpdate, Target = Request.Version,
                PayloadDigest = AgentPayloadDigest.Compute(Request, typeof(AgentUpdateRequest)), State = OperationState.Running,
                StartedAt = Now,
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.DisposeAsync();
        _guard.Dispose();
        _store.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
