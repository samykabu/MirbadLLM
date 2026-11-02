using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Ui;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// bUnit tests for the minimal Nodes pages (MN-037, design §12.1): the <c>/nodes</c> list and the <c>/nodes/{id}</c>
/// Overview tab, rendered from a fake <see cref="INodeStatusCache"/> and <see cref="INodeRegistry"/>.
/// </summary>
public sealed class NodesPageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private const string Build = "1.0.0+78b39d2"; // the test rows' agent build: the same as the master's

    // ---------- Fakes ----------

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeNodeStatusCache : INodeStatusCache
    {
        private readonly Lock _gate = new();
        private List<NodeLiveStatus> _nodes = [];
        private long _version;

        public long Version => Interlocked.Read(ref _version);

        public void Set(params NodeLiveStatus[] nodes)
        {
            lock (_gate)
            {
                _nodes = [.. nodes];
            }

            Interlocked.Increment(ref _version);
        }

        public IReadOnlyList<NodeLiveStatus> ListNodes()
        {
            lock (_gate)
            {
                return [.. _nodes];
            }
        }

        public NodeLiveStatus? GetNode(string nodeId) => ListNodes().FirstOrDefault(n => n.NodeId == nodeId);

        public NodeModelRuntime? GetRuntime(string modelId) => ListRuntimes().FirstOrDefault(r => r.ModelId == modelId);

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) =>
            ListNodes().Where(n => nodeId is null || n.NodeId == nodeId).SelectMany(n => n.Runtimes).ToList();

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeNodeRegistry(params NodeRecord[] nodes) : INodeRegistry
    {
        public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodeRecord>>(nodes);

        public Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult(nodes.FirstOrDefault(n => n.Id == id));

        public Task UpsertAsync(NodeRecord node, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JoinTokenRecord>>([]);
    }

    // ---------- Builders ----------

    private static NodeCapabilities Gb10Caps() => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "1.0.0+78b39d2",
        Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS", "6.11.0-nvidia"),
        Arch = "arm64",
        Cpu = new NodeCpuInfo(20, "Cortex-X925"),
        Memory = new NodeMemoryInfo(MemoryModel.Unified, 119.7),
        Gpus = [new GpuCapability { Index = 0, Uuid = "GPU-gb10", Name = "NVIDIA GB10", SharesHostMemory = true, ComputeCapability = "12.1" }],
        Runtimes = [new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = true }],
    };

    private static NodeCapabilities PcCaps() => new()
    {
        Protocol = new ProtocolRange(1, 2),
        AgentVersion = "1.1.0",
        Os = new NodeOsInfo("windows", "Windows 11 Pro"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
        Gpus =
        [
            new GpuCapability { Index = 0, Uuid = "GPU-a", Name = "NVIDIA GeForce RTX 4060 Ti", MemoryTotalGiB = 16 },
            new GpuCapability { Index = 1, Uuid = "GPU-b", Name = "NVIDIA GeForce RTX 4060 Ti", MemoryTotalGiB = 16 },
        ],
    };

    private static NodeLiveStatus Dgx(NodeState state = NodeState.Online, bool draining = false) => new()
    {
        NodeId = "dgx",
        NodeUid = "uid-dgx",
        DisplayName = "DGX Spark",
        Address = "https://127.0.0.1:9443",
        State = state,
        Draining = draining,
        LastContact = Now.AddSeconds(-12),
        AgentVersion = "1.0.0+78b39d2",
        AgentProtocol = new ProtocolRange(1, 1),
        SelectedProtocol = 1,
        Capabilities = Gb10Caps(),
        Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 119.7, HostAvailableGiB = 80.5 },
        Runtimes =
        [
            new NodeModelRuntime { ModelId = "qwen", NodeId = "dgx", State = ModelState.Ready },
            new NodeModelRuntime { ModelId = "gemma", NodeId = "dgx", State = ModelState.Stopped },
        ],
        OpenOperations =
        [
            new NodeOperationSummary("op-1", "model.start", "qwen", OperationState.Running, "pulling", Now.AddSeconds(-65)),
        ],
        CertNotAfter = Now.AddDays(92).AddHours(1),
        RotationStage = CertificateRotationStage.CsrReceived,
    };

    private static NodeLiveStatus Pc(NodeState state) => new()
    {
        NodeId = "pc-office",
        NodeUid = "uid-pc",
        DisplayName = "Office PC",
        Address = "https://192.0.2.40:9443",
        State = state,
        Reasons = state == NodeState.Incompatible ? ["No protocol version in common (agent 2–2, master 1–1)."] : ["GPU 1 is not answering."],
        LastContact = Now.AddMinutes(-4).AddSeconds(-5),
        AgentVersion = "2.0.0",
        AgentProtocol = new ProtocolRange(2, 2),
        Capabilities = PcCaps(),
        Memory = new NodeMemorySnapshot
        {
            Model = MemoryModel.Discrete,
            HostTotalGiB = 64,
            HostAvailableGiB = 40,
            Gpus = [new GpuMemorySnapshot(0, "GPU-a", 16, 4, 12), new GpuMemorySnapshot(1, "GPU-b", 16, null, null)],
        },
    };

    private static NodeRecord Record(string id, bool approved = true, bool revoked = false, string? displayName = null) => new()
    {
        Id = id,
        Uid = id + "-uid",
        DisplayName = displayName,
        Address = $"https://{id}.lan:9443",
        Approved = approved,
        Revoked = revoked,
    };

    private static (FakeNodeStatusCache Cache, FakeNodeRegistry Registry) Register(
        BunitContext ctx, IEnumerable<NodeLiveStatus> live, NodeRecord[]? records = null, IEnumerable<ModelDefinition>? models = null)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cache = new FakeNodeStatusCache();
        cache.Set([.. live]);
        var registry = new FakeNodeRegistry(records ?? []);
        ctx.Services.AddSingleton<INodeStatusCache>(cache);
        ctx.Services.AddSingleton<INodeRegistry>(registry);
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry([.. models ?? []]));
        ctx.Services.AddSingleton<TimeProvider>(new FixedTime(Now));
        return (cache, registry);
    }

    private static ModelDefinition Placed(string id, string node, double budget = 20) =>
        new() { Id = id, ServedName = id, ModelPath = id, MemoryBudgetGiB = budget, Placement = new ModelPlacement(node) };

    private static string Text(IRenderedComponent<Nodes> cut, string testId) => cut.Find($"[data-testid={testId}]").TextContent.Trim();

    private static string Squash(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    // ---------- List ----------

    [Fact]
    public async Task List_KeepsStateHeadroomModelsAndAlerts_TheNodeFactsLiveOnItsPage()
    {
        await using var ctx = new BunitContext();
        Register(ctx, [Dgx()], [Record("dgx")], [Placed("qwen", "dgx"), Placed("gemma", "dgx"), Placed("other", "pc-office")]);

        var cut = ctx.Render<Nodes>();

        var row = cut.Find("[data-testid=nodes-row-dgx]");
        Assert.Contains("DGX Spark", row.QuerySelector(".col-primary")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("dgx", row.QuerySelector(".col-primary")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("nodes/dgx", cut.Find("[data-testid=nodes-open-dgx]").GetAttribute("href"));
        Assert.Equal("dgx", Text(cut, "nodes-open-dgx"));
        Assert.Equal("online", cut.Find("[data-testid=nodes-state-dgx]").GetAttribute("data-state"));

        // Headroom, unified: free of total, in the tabular numeral face.
        Assert.Contains("80.5 GiB free of 119.7 GiB Unified memory", Squash(Text(cut, "nodes-memory-dgx")), StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=nodes-ring-dgx]"));
        Assert.NotNull(cut.Find("[data-testid=nodes-memory-dgx] .num"));
        Assert.Equal("1 Ready / 2 placed", Squash(Text(cut, "nodes-models-dgx")));

        // The only alert: the open operation (the certificate is 92 days out, the agent current).
        Assert.Equal("1 operation running", Text(cut, "nodes-alert-ops-dgx"));
        Assert.Empty(cut.FindAll("[data-testid=nodes-alert-cert-dgx]"));
        Assert.Empty(cut.FindAll("[data-testid=nodes-alert-agent-dgx]"));

        // Folded into Node detail: address, OS · arch, GPUs, agent, certificate; a serving node has no "last seen".
        Assert.Equal("list", cut.Find("[data-testid=nodes-table]").GetAttribute("role"));
        Assert.Equal("ARTICLE", row.TagName);
        Assert.NotNull(cut.Find("[data-testid=nodes-summary]"));
        foreach (string gone in new[] { "nodes-address-dgx", "nodes-os-dgx", "nodes-gpus-dgx", "nodes-agent-dgx", "nodes-cert-dgx", "nodes-seen-dgx" })
        {
            Assert.Empty(cut.FindAll($"[data-testid={gone}]"));
        }
    }

    [Fact]
    public async Task List_DiscreteNode_ShowsFreeMemoryPerGpu_WithDashesForAGpuWithoutAReading()
    {
        await using var ctx = new BunitContext();
        Register(ctx, [Pc(NodeState.Online)]);

        var cut = ctx.Render<Nodes>();

        Assert.Equal("GPU 0 12.0 GiB free of 16.0 GiB", Squash(Text(cut, "nodes-gpu-free-pc-office-0")));
        Assert.Equal("GPU 1 — free of 16.0 GiB", Squash(Text(cut, "nodes-gpu-free-pc-office-1")));
        Assert.Contains("Host RAM", Text(cut, "nodes-memory-pc-office"), StringComparison.Ordinal);
    }

    [Fact]
    public void Alerts_FlagACertificateNearExpiry_AFailedRotation_AnOutdatedAgent_AndFailedModels()
    {
        var healthy = Nodes.Merge(Record("dgx"), Dgx() with { OpenOperations = [] }, []);
        Assert.Empty(Nodes.Alerts(healthy, Now, Build));

        var soon = healthy with { CertNotAfter = Now.AddDays(12).AddHours(1) };
        Assert.Equal("Certificate expires in 12 days", Assert.Single(Nodes.Alerts(soon, Now, Build)).Text);
        Assert.Equal("busy", Nodes.Alerts(soon, Now, Build)[0].Tone);

        var expired = healthy with { CertNotAfter = Now.AddMinutes(-1) };
        Assert.Equal(("cert", "failed"), (Nodes.Alerts(expired, Now, Build)[0].Key, Nodes.Alerts(expired, Now, Build)[0].Tone));

        // A pending node's join-time certificate is not flagged: it gets a fresh one when approved.
        Assert.Empty(Nodes.Alerts(soon with { State = NodeState.PendingApproval }, Now, Build));

        var rotation = healthy with { RotationError = "agent busy" };
        Assert.Equal("rotation", Assert.Single(Nodes.Alerts(rotation, Now, Build)).Key);

        var older = healthy with { AgentProtocol = new ProtocolRange(0, 0) };
        Assert.Equal("Agent outdated: update it", Assert.Single(Nodes.Alerts(older, Now, Build)).Text);
        var incompatible = healthy with { State = NodeState.Incompatible, AgentProtocol = new ProtocolRange(2, 2) };
        Assert.Equal(("Agent incompatible: update it", "failed"), (Assert.Single(Nodes.Alerts(incompatible, Now, Build)).Text, Nodes.Alerts(incompatible, Now, Build)[0].Tone));

        var failed = healthy with
        {
            Runtimes =
            [
                new NodeModelRuntime { ModelId = "a", NodeId = "dgx", State = ModelState.Failed },
                new NodeModelRuntime { ModelId = "b", NodeId = "dgx", State = ModelState.Failed },
                new NodeModelRuntime { ModelId = "c", NodeId = "dgx", State = ModelState.Ready },
            ],
        };
        Assert.Equal("2 models failed", Assert.Single(Nodes.Alerts(failed, Now, Build)).Text);

        // An agent of another commit than the master's: the build alert, and only when the protocol alert is not there.
        Assert.Equal(("build", "busy"), (Assert.Single(Nodes.Alerts(healthy, Now, "1.0.0+826bc75fc222")).Key, Nodes.Alerts(healthy, Now, "1.0.0+826bc75fc222")[0].Tone));
        Assert.Equal("agent", Assert.Single(Nodes.Alerts(older, Now, "1.0.0+826bc75fc222")).Key);
    }

    [Fact]
    public async Task List_ShowsEveryStateWithReasons_MergesPendingFromTheRegistry_AndHidesRevoked()
    {
        await using var ctx = new BunitContext();
        Register(
            ctx,
            [Dgx(draining: true), Pc(NodeState.Incompatible)],
            [Record("dgx"), Record("pc-office"), Record("mac-mini", approved: false, displayName: "Mac mini"), Record("old", revoked: true), Record("lost")]);

        var cut = ctx.Render<Nodes>();

        Assert.Equal("draining", cut.Find("[data-testid=nodes-state-dgx]").GetAttribute("data-state"));
        Assert.Contains("Draining", cut.Find("[data-testid=nodes-state-dgx]").TextContent, StringComparison.Ordinal);
        Assert.Equal("incompatible", cut.Find("[data-testid=nodes-state-pc-office]").GetAttribute("data-state"));
        Assert.Contains("No protocol version in common", Text(cut, "nodes-reasons-pc-office"), StringComparison.Ordinal);
        Assert.Equal("Agent incompatible: update it", Text(cut, "nodes-alert-agent-pc-office"));
        Assert.Contains("12.0 GiB free of 16.0 GiB", Squash(Text(cut, "nodes-memory-pc-office")), StringComparison.Ordinal);
        Assert.Equal("4m 05s ago", Text(cut, "nodes-seen-pc-office"));

        // A record without live status: pending when not approved, else Offline with a reason.
        Assert.Equal("pending-approval", cut.Find("[data-testid=nodes-state-mac-mini]").GetAttribute("data-state"));
        Assert.Contains("Pending approval", cut.Find("[data-testid=nodes-state-mac-mini]").TextContent, StringComparison.Ordinal);
        Assert.Equal("offline", cut.Find("[data-testid=nodes-state-lost]").GetAttribute("data-state"));
        Assert.Contains("no live status", Text(cut, "nodes-reasons-lost"), StringComparison.Ordinal);

        Assert.Empty(cut.FindAll("[data-testid=nodes-row-old]"));
        // Ordered by display name: DGX Spark, lost, Mac mini, Office PC.
        Assert.Equal(
            ["nodes-row-dgx", "nodes-row-lost", "nodes-row-mac-mini", "nodes-row-pc-office"],
            cut.FindAll("[data-testid=nodes-table] > article").Select(r => r.GetAttribute("data-testid")).ToArray());
    }

    [Fact]
    public async Task List_RendersDashes_ForEverythingTheNodeHasNotReported()
    {
        await using var ctx = new BunitContext();
        Register(ctx, [new NodeLiveStatus { NodeId = "bare", NodeUid = "u", State = NodeState.Offline }]);

        var cut = ctx.Render<Nodes>();

        Assert.Contains("—", Text(cut, "nodes-memory-bare"), StringComparison.Ordinal);
        Assert.Contains("No memory reading yet", Text(cut, "nodes-memory-bare"), StringComparison.Ordinal);
        Assert.Equal("None", Text(cut, "nodes-alerts-bare"));
        Assert.Equal("—", Text(cut, "nodes-seen-bare"));
        Assert.DoesNotContain("NaN", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(">0 GiB", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_IsPaged()
    {
        await using var ctx = new BunitContext();
        var many = Enumerable.Range(1, 30)
            .Select(i => new NodeLiveStatus { NodeId = $"n{i:00}", NodeUid = $"u{i}", State = NodeState.Online })
            .ToArray();
        Register(ctx, many);

        var cut = ctx.Render<Nodes>();

        Assert.Equal(25, cut.FindAll("[data-testid=nodes-table] > article").Count);
        Assert.Contains("1–25 of 30 nodes", cut.Find("[data-testid=nodes-pager-range]").TextContent, StringComparison.Ordinal);

        cut.ClickAt("[data-testid=nodes-pager-next]");

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid=nodes-table] > article").Count));
    }

    [Fact]
    public async Task List_FollowsTheSharedRefresh_ReadingTheCacheAgain()
    {
        await using var ctx = new BunitContext();
        var (cache, _) = Register(ctx, [Dgx()]);
        ctx.Services.AddSingleton(sp => new LiveDataService(sp));

        var cut = ctx.Render<Nodes>();
        Assert.Equal("online", cut.Find("[data-testid=nodes-state-dgx]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=nodes-updated]").GetAttribute("data-live-at"));

        cache.Set(Dgx(NodeState.Degraded) with { Reasons = ["Docker is not answering."] });
        await ctx.Services.GetRequiredService<LiveDataService>().RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        cut.WaitForAssertion(() => Assert.Equal("degraded", cut.Find("[data-testid=nodes-state-dgx]").GetAttribute("data-state")));
        Assert.Contains("Docker is not answering.", Text(cut, "nodes-reasons-dgx"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_ExplainsEmptyAndUnavailable()
    {
        await using (var ctx = new BunitContext())
        {
            Register(ctx, []);
            var cut = ctx.Render<Nodes>();
            Assert.NotNull(cut.Find("[data-testid=nodes-empty]"));
            Assert.Empty(cut.FindAll("[data-testid=nodes-pager]"));
        }

        await using (var ctx = new BunitContext())
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            var cut = ctx.Render<Nodes>();
            Assert.NotNull(cut.Find("[data-testid=nodes-unavailable]"));
        }
    }

    [Fact]
    public async Task List_WorksWithTheRegistryAlone()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<INodeRegistry>(new FakeNodeRegistry(Record("dgx") with { LastCapabilities = Gb10Caps() }));

        var cut = ctx.Render<Nodes>();

        Assert.Equal("offline", cut.Find("[data-testid=nodes-state-dgx]").GetAttribute("data-state"));
        Assert.Contains("Unified memory, no reading yet", Text(cut, "nodes-memory-dgx"), StringComparison.Ordinal);
        Assert.Contains("— placed", System.Text.RegularExpressions.Regex.Replace(Text(cut, "nodes-models-dgx"), @"\s+", " "), StringComparison.Ordinal);
    }

    // ---------- Detail ----------

    [Fact]
    public async Task Detail_HeaderCarriesIdStateOsAndRuntimes_OverviewShowsAttentionHeadroomAndModels()
    {
        await using var ctx = new BunitContext();
        Register(
            ctx,
            [Dgx(NodeState.Degraded) with
            {
                Reasons = ["Docker is not answering.", "hostd unavailable."],
                Runtimes =
                [
                    new NodeModelRuntime { ModelId = "qwen", NodeId = "dgx", State = ModelState.Ready },
                    new NodeModelRuntime { ModelId = "gemma", NodeId = "dgx", State = ModelState.Failed, Error = "CUDA out of memory" },
                ],
            }],
            [Record("dgx") with { MemoryReserveGiB = 10, Rotation = new CertificateRotationRecord { RotationId = "r1", NodeUid = "uid-dgx", Stage = CertificateRotationStage.CsrReceived, Error = "agent busy" } }],
            [Placed("qwen", "dgx", budget: 30), Placed("gemma", "dgx", budget: 12) with { ServedName = "gemma-3", Runtime = RuntimeKind.LlamaCpp }]);

        var cut = ctx.Render<NodeDetail>(p => p.Add(x => x.Id, "dgx"));

        // Header: the name, the id (it differs), the state with its first reason, OS and runtime chips, last contact.
        Assert.Equal("dgx", cut.Find("[data-testid=page-title]").TextContent);
        Assert.Equal("dgx", cut.Find("[data-testid=node-id]").TextContent);
        Assert.Equal("degraded", cut.Find("[data-testid=node-state-badge]").GetAttribute("data-state"));
        Assert.Equal("Docker is not answering. (+1 more on Overview)", cut.Find("[data-testid=node-state-reason]").TextContent);
        Assert.Equal("Linux", cut.Find("[data-testid=node-os-badge]").TextContent);
        Assert.Equal("vLLM", cut.Find("[data-testid=node-runtime-chip-vllm]").TextContent);
        Assert.Equal("12s ago", cut.Find("[data-testid=node-last-seen]").TextContent);

        // Five Line tabs, Overview selected, under the tour's anchor.
        Assert.Contains("tabs--line", cut.Find("[data-testid=node-tabs]").ClassName, StringComparison.Ordinal);
        Assert.Equal(
            ["overview", "gpus", "services", "operations", "settings"],
            cut.FindAll("[data-testid=node-tabs-list] [role=tab]").Select(t => t.GetAttribute("data-testid")!["node-tabs-tab-".Length..]).ToArray());
        Assert.Equal("true", cut.Find("[data-testid=node-tabs-tab-overview]").GetAttribute("aria-selected"));
        Assert.NotNull(cut.Find("[data-tour=node-tabs] [data-testid=node-tabs-list]"));

        // Attention: every reason, and the failed certificate rotation with its cause.
        Assert.Equal(["Docker is not answering.", "hostd unavailable."], cut.FindAll("[data-testid=node-reasons] li").Select(li => li.TextContent).ToArray());
        Assert.Contains("agent busy", cut.Find("[data-testid=node-alert-rotation]").TextContent, StringComparison.Ordinal);

        // Headroom: unified bar, only the Ready model's budget counts, the node's own reserve.
        Assert.Contains("119.7 GiB total", cut.Find("[data-testid=memory-bar]").TextContent, StringComparison.Ordinal);
        Assert.Contains("30.0 GiB", cut.Find("[data-testid=memory-legend]").TextContent, StringComparison.Ordinal);
        Assert.Equal("70.5 GiB", cut.Find("[data-testid=memory-admittable]").TextContent);

        // Models placed here: served name (a link), state with a failure's cause in place, runtime, budget.
        Assert.Equal(["node-model-gemma", "node-model-qwen"], cut.FindAll("[data-testid=node-models-table] tbody tr").Select(r => r.GetAttribute("data-testid")).ToArray());
        Assert.Equal("models/gemma", cut.Find("[data-testid=node-model-open-gemma]").GetAttribute("href"));
        Assert.Equal("gemma-3", cut.Find("[data-testid=node-model-open-gemma]").TextContent);
        Assert.Equal("failed", cut.Find("[data-testid=node-model-state-gemma]").GetAttribute("data-state"));
        Assert.Contains("CUDA out of memory", cut.Find("[data-testid=node-model-error-gemma]").TextContent, StringComparison.Ordinal);
        Assert.Equal("llama.cpp", cut.Find("[data-testid=node-model-runtime-gemma]").TextContent);
        Assert.Equal("vLLM", cut.Find("[data-testid=node-model-runtime-qwen]").TextContent);

        // Overview now includes At a glance; certificate controls stay in Settings and operations in Operations.
        Assert.NotNull(cut.Find("[data-testid=node-capabilities]"));
        Assert.Empty(cut.FindAll("[data-testid=node-ops-table]"));
    }

    [Fact]
    public async Task Detail_SettingsSubtabs_SeparateTheGeneralHardwareAndCertificateFacts()
    {
        await using var ctx = new BunitContext();
        Register(
            ctx,
            [Dgx()],
            [Record("dgx") with { Rotation = new CertificateRotationRecord { RotationId = "r1", NodeUid = "uid-dgx", Stage = CertificateRotationStage.CsrReceived, Error = "agent busy" } }]);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("nodes/dgx?tab=settings");

        var cut = ctx.Render<NodeDetail>(p => p.Add(x => x.Id, "dgx"));

        Assert.Equal("true", cut.Find("[data-testid=node-settings-tabs-tab-general]").GetAttribute("aria-selected"));
        Assert.Empty(cut.FindAll("[data-testid=node-certs]"));
        Assert.Empty(cut.FindAll("[data-testid=node-capabilities]"));
        cut.Find("[data-testid=node-settings-tabs-tab-hardware]").Click();
        Assert.Equal("https://127.0.0.1:9443", cut.Find("[data-testid=node-address]").TextContent);
        Assert.Contains("Ubuntu 24.04 LTS · arm64 · 6.11.0-nvidia", cut.Find("[data-testid=node-os]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Cortex-X925 · 20 logical cores", cut.Find("[data-testid=node-cpu]").TextContent, StringComparison.Ordinal);
        Assert.Contains("NVIDIA GB10", cut.Find("[data-testid=node-gpus]").TextContent, StringComparison.Ordinal);
        Assert.Contains("compute 12.1", cut.Find("[data-testid=node-gpus]").TextContent, StringComparison.Ordinal);
        Assert.Contains("vLLM (container)", cut.Find("[data-testid=node-runtimes]").TextContent, StringComparison.Ordinal);
        Assert.Equal("1.0.0+78b39d2", cut.Find("[data-testid=node-agent-version]").TextContent);
        Assert.Equal("protocol 1 (agent 1)", cut.Find("[data-testid=node-protocol]").TextContent);

        cut.Find("[data-testid=node-settings-tabs-tab-certificate]").Click();
        Assert.Empty(cut.FindAll("[data-testid=node-capabilities]"));
        Assert.Equal("2027-01-04 (in 92 days)", cut.Find("[data-testid=node-certs-expiry]").TextContent);
        Assert.Equal("Rotating: CSR received", cut.Find("[data-testid=node-certs-rotation]").TextContent);
        Assert.Equal("agent busy", cut.Find("[data-testid=node-certs-error]").TextContent);
    }

    [Theory]
    [InlineData("general", "node-settings")]
    [InlineData("certificate", "node-certs")]
    [InlineData("ca-rotation", "node-ca")]
    [InlineData("hardware", "node-capabilities")]
    public async Task Detail_SettingsSectionLinks_SelectOnlyTheirPanel_AndKeepTheMainTab(string section, string panel)
    {
        await using var ctx = new BunitContext();
        Register(ctx, [Dgx()], [Record("dgx")]);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo($"nodes/dgx?tab=settings&section={section}&keep=1");
        var cut = ctx.Render<NodeDetail>(p => p.Add(x => x.Id, "dgx"));

        Assert.Equal("true", cut.Find("[data-testid=node-tabs-tab-settings]").GetAttribute("aria-selected"));
        Assert.Equal("true", cut.Find($"[data-testid=node-settings-tabs-tab-{section}]").GetAttribute("aria-selected"));
        Assert.NotNull(cut.Find($"[data-testid={panel}]"));
        foreach (string other in new[] { "node-settings", "node-certs", "node-ca", "node-capabilities" })
        {
            if (other != panel) Assert.Empty(cut.FindAll($"[data-testid={other}]"));
        }

        var ids = cut.FindAll("[id]").Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        cut.Find("[data-testid=node-settings-tabs-tab-hardware]").Click();
        Assert.Contains("tab=settings", new Uri(nav.Uri).Query, StringComparison.Ordinal);
        Assert.Contains("section=hardware", new Uri(nav.Uri).Query, StringComparison.Ordinal);
        Assert.Contains("keep=1", new Uri(nav.Uri).Query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("models", "overview")]
    [InlineData("commands", "operations")]
    [InlineData("certificates", "settings")]
    [InlineData("gpus", "gpus")]
    [InlineData("services", "services")]
    [InlineData("nonsense", "overview")]
    public void Detail_OldTabLinks_OpenWhereTheirContentLivesNow(string requested, string expected) =>
        Assert.Equal(expected, NodeDetail.TabFor(requested));

    [Fact]
    public async Task Detail_AnOldCertificatesLink_SelectsSettings_AndRewritesTheQuery()
    {
        await using var ctx = new BunitContext();
        Register(ctx, [Dgx()], [Record("dgx")]);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("nodes/dgx?tab=certificates");

        var cut = ctx.Render<NodeDetail>(p => p.Add(x => x.Id, "dgx"));

        Assert.Equal("true", cut.Find("[data-testid=node-tabs-tab-settings]").GetAttribute("aria-selected"));
        Assert.NotNull(cut.Find("[data-testid=node-certs]"));
        Assert.Contains("tab=settings", new Uri(nav.Uri).Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_DiscreteNode_DrawsOneHeadroomBarPerGpu_OnOverview()
    {
        await using var ctx = new BunitContext();
        Register(ctx, [Pc(NodeState.Degraded)]);

        var cut = ctx.Render<NodeDetail>(p => p.Add(x => x.Id, "pc-office"));

        Assert.Contains("64.0 GiB", cut.Find("[data-testid=node-memory-host]").TextContent, StringComparison.Ordinal);
        Assert.Contains("NVIDIA GeForce RTX 4060 Ti", cut.Find("[data-testid=node-memory-gpu-0]").TextContent, StringComparison.Ordinal);
        Assert.Equal("12.0 GiB", cut.Find("[data-testid=node-memory-gpu-0-admittable]").TextContent);
        Assert.Equal("—", cut.Find("[data-testid=node-memory-gpu-1-admittable]").TextContent);
    }

    [Fact]
    public async Task Detail_PagesOpenOperations()
    {
        await using var ctx = new BunitContext();
        var ops = Enumerable.Range(1, 12)
            .Select(i => new NodeOperationSummary($"op-{i:00}", "files.download", $"folder-{i}", OperationState.Accepted, null, Now.AddMinutes(-i)))
            .ToArray();
        Register(ctx, [Dgx() with { OpenOperations = ops }]);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("nodes/dgx?tab=operations&ops-size=10");

        var cut = ctx.Render<NodeDetail>(p => p.Add(x => x.Id, "dgx"));

        Assert.Equal(10, cut.FindAll("[data-testid=node-ops-table] tbody tr").Count);
        Assert.Contains("of 12 operations", cut.Find("[data-testid=node-ops-pager-range]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_UnknownOrRevokedNode_ShowsNotFound()
    {
        await using var ctx = new BunitContext();
        Register(ctx, [Dgx()], [Record("old", revoked: true)]);

        var unknown = ctx.Render<NodeDetail>(p => p.Add(x => x.Id, "nope"));
        var revoked = ctx.Render<NodeDetail>(p => p.Add(x => x.Id, "old"));

        Assert.NotNull(unknown.Find("[data-testid=node-not-found]"));
        Assert.NotNull(revoked.Find("[data-testid=node-not-found]"));
    }

    [Fact]
    public async Task Detail_PendingNode_FromTheRegistryAlone()
    {
        await using var ctx = new BunitContext();
        Register(ctx, [], [Record("mac-mini", approved: false)]);

        var cut = ctx.Render<NodeDetail>(p => p.Add(x => x.Id, "mac-mini"));

        Assert.Equal("pending-approval", cut.Find("[data-testid=node-state-badge]").GetAttribute("data-state"));
        Assert.NotNull(cut.Find("[data-testid=node-memory-missing]"));
        Assert.Contains("Nothing is sent to this node until it is approved", cut.Find("[data-testid=node-pending-note]").TextContent, StringComparison.Ordinal);
    }

    // ---------- StateBadge (node states) ----------

    [Theory]
    [InlineData(NodeState.Online, false, "state--ready", "online", "Online")]
    [InlineData(NodeState.Degraded, false, "state--starting", "degraded", "Degraded")]
    [InlineData(NodeState.Incompatible, false, "state--failed", "incompatible", "Incompatible")]
    [InlineData(NodeState.Offline, false, "state--stopped", "offline", "Offline")]
    [InlineData(NodeState.PendingApproval, false, "state--stopped", "pending-approval", "Pending approval")]
    [InlineData(NodeState.PendingTakeover, false, "state--starting", "pending-takeover", "Pending takeover")]
    [InlineData(NodeState.Revoked, false, "state--failed", "revoked", "Revoked")]
    [InlineData(NodeState.Online, true, "state--starting", "draining", "Draining")]
    [InlineData(NodeState.Offline, true, "state--stopped", "offline", "Offline")]
    public void StateBadge_NodeState_PairsColourWithIconAndLabel(NodeState state, bool draining, string cssClass, string slug, string label)
    {
        using var ctx = new BunitContext();

        var cut = ctx.Render<StateBadge>(p => p.Add(x => x.Node, state).Add(x => x.Draining, draining).Add(x => x.TestId, "badge"));

        var badge = cut.Find("[data-testid=badge]");
        Assert.Contains(cssClass, badge.GetAttribute("class"), StringComparison.Ordinal);
        Assert.Equal(slug, badge.GetAttribute("data-state"));
        Assert.Equal(label, badge.TextContent.Trim());
        var svg = badge.QuerySelector("svg")!;
        Assert.Equal("true", svg.GetAttribute("aria-hidden"));
        Assert.NotEmpty(svg.InnerHtml);
    }

    [Fact]
    public void StateBadge_EveryNodeState_HasItsOwnIconAndLabel()
    {
        var looks = Enum.GetValues<NodeState>().Select(s => StateBadge.NodeLook(s)).Append(StateBadge.NodeLook(NodeState.Online, draining: true)).ToList();

        Assert.Equal(looks.Count, looks.Select(l => l.Icon).Distinct().Count());
        Assert.Equal(looks.Count, looks.Select(l => l.Label).Distinct().Count());
        Assert.All(looks, l => Assert.NotEmpty(IconPaths.For(l.Icon)));
    }

    // ---------- Shell: nav entry and shortcut ----------

    [Fact]
    public void Shell_HasANodesLinkUnderFleet_AndTheGnShortcut()
    {
        string layout = RepoFile("src/MirbadLLM.Web/Components/Layout/MainLayout.razor");
        int fleet = layout.IndexOf("id=\"nav-fleet\"", StringComparison.Ordinal);
        int admin = layout.IndexOf("id=\"nav-admin\"", StringComparison.Ordinal);
        int nodes = layout.IndexOf("href=\"nodes\"", StringComparison.Ordinal);
        Assert.True(fleet > 0 && nodes > fleet && nodes < admin, "the Nodes link belongs to the Fleet group");
        Assert.Contains("<kbd>n</kbd></dt><dd>Nodes</dd>", layout, StringComparison.Ordinal);

        string shell = RepoFile("src/MirbadLLM.Web/wwwroot/js/shell.js");
        Assert.Contains("n: 'nodes'", shell, StringComparison.Ordinal);
    }

    private static string RepoFile(string relative, [CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MirbadLLM.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }
}
