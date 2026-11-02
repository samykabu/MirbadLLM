using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Web;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Layout;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Ui;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// MN-119 (design §8.3, §12.3): the Dashboard groups model cards by node with each node's state and headroom (one bar
/// for a unified node, one per GPU for a discrete one), keeps a <c>?node=</c> filter, and with a single node looks
/// exactly as before (compared with markup captured from the pre-MN-119 page); the status bar adds "· N nodes" and node
/// chips with several nodes.
/// </summary>
public sealed partial class DashboardGroupingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // ---------- Fakes ----------

    private sealed class FakeNodeStatusCache : INodeStatusCache
    {
        private readonly Lock _gate = new();
        private List<NodeLiveStatus> _nodes;
        private long _version;

        public FakeNodeStatusCache(params NodeLiveStatus[] nodes) => _nodes = [.. nodes];

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

    /// <summary>Host series per node (the P2 <see cref="IMetricsHistory.GetHost(string, TimeSpan)"/>).</summary>
    private sealed class NodeMetricsHistory : IMetricsHistory
    {
        public Dictionary<string, IReadOnlyList<HostSample>> Hosts { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<HostSample> GetHost(TimeSpan window) => Hosts.GetValueOrDefault("dgx") ?? [];

        public IReadOnlyList<HostSample> GetHost(string nodeId, TimeSpan window) => Hosts.GetValueOrDefault(nodeId) ?? [];

        /// <summary>Latest KPIs per model; each also reads as one sample at <see cref="Now"/>.</summary>
        public Dictionary<string, ModelKpis> Kpis { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<ModelSample> GetModel(string modelId, TimeSpan window) =>
            Kpis.TryGetValue(modelId, out var kpis) ? [new ModelSample(Now, kpis)] : [];

        public IReadOnlyDictionary<string, ModelKpis> Latest() => new Dictionary<string, ModelKpis>(Kpis, StringComparer.Ordinal);
    }

    // ---------- Builders ----------

    private static ModelDefinition Model(string id, string? node, double budget = 36, IReadOnlyList<string>? gpus = null, IReadOnlyList<string>? tags = null) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = "checkpoint",
        MemoryBudgetGiB = budget,
        Placement = node is null ? null : new ModelPlacement(node, gpus),
        Tags = tags ?? [],
    };

    private static NodeRecord Record(string id, string? name = null, double reserve = 8, IReadOnlyDictionary<string, double>? gpuReserves = null) =>
        ListOnlyNodeRegistry.Node(id) with
        {
            DisplayName = name,
            Approved = true,
            MemoryReserveGiB = reserve,
            GpuReserveGiB = gpuReserves ?? new Dictionary<string, double>(),
        };

    private static NodeLiveStatus Dgx(NodeState state = NodeState.Online, bool draining = false, double available = 80.5) => new()
    {
        NodeId = "dgx",
        NodeUid = "dgx-uid",
        DisplayName = "DGX Spark",
        State = state,
        Draining = draining,
        LastContact = Now.AddSeconds(-3),
        Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 119.7, HostAvailableGiB = available },
        Capabilities = new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "1.0.0",
            Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
            Arch = "arm64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 119.7),
            Gpus = [new GpuCapability { Index = 0, Uuid = "GPU-gb10", Name = "NVIDIA GB10", SharesHostMemory = true }],
        },
    };

    private static NodeLiveStatus Pc(NodeState state = NodeState.Online, IReadOnlyList<string>? reasons = null, DateTimeOffset? lastContact = null) => new()
    {
        NodeId = "pc",
        NodeUid = "pc-uid",
        DisplayName = "Office PC",
        State = state,
        Reasons = reasons ?? [],
        LastContact = lastContact ?? Now.AddSeconds(-2),
        Memory = new NodeMemorySnapshot
        {
            Model = MemoryModel.Discrete,
            HostTotalGiB = 64,
            HostAvailableGiB = 40,
            Gpus =
            [
                new GpuMemorySnapshot(0, "GPU-a", TotalGiB: 16, UsedGiB: 10, FreeGiB: 6),
                new GpuMemorySnapshot(1, "GPU-b", TotalGiB: 16, UsedGiB: 1, FreeGiB: 15),
            ],
        },
        Capabilities = new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "1.1.0",
            Os = new NodeOsInfo("windows", "Windows 11 Pro"),
            Arch = "amd64",
            Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
            Gpus =
            [
                new GpuCapability { Index = 0, Uuid = "GPU-a", Name = "RTX 4060 Ti", MemoryTotalGiB = 16 },
                new GpuCapability { Index = 1, Uuid = "GPU-b", Name = "RTX 4060 Ti", MemoryTotalGiB = 16 },
            ],
        },
    };

    private static NodeLiveStatus Simple(string id, string name, NodeState state = NodeState.Online) => new()
    {
        NodeId = id,
        NodeUid = id + "-uid",
        DisplayName = name,
        State = state,
        LastContact = Now,
        Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 32, HostAvailableGiB = 20 },
    };

    private static BunitContext Context(
        IReadOnlyList<ModelDefinition> models,
        FakeModelRuntime? runtime = null,
        FakeNodeStatusCache? cache = null,
        INodeRegistry? registry = null,
        IMetricsHistory? history = null,
        MemorySnapshot? memory = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry([.. models]));
        ctx.Services.AddSingleton<IModelRuntime>(runtime ?? new FakeModelRuntime());
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(memory ?? new MemorySnapshot(119.7, 80.5)));
        ctx.Services.AddSingleton<IModelMetricsReader>(new FakeModelMetricsReader());
        ctx.Services.AddSingleton<IRouterStats>(new FakeRouterStats([]));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions { MemoryReserveGiB = 8 }));
        if (cache is not null)
        {
            ctx.Services.AddSingleton<INodeStatusCache>(cache);
        }

        if (registry is not null)
        {
            ctx.Services.AddSingleton(registry);
        }

        if (history is not null)
        {
            ctx.Services.AddSingleton(history);
        }

        return ctx;
    }

    /// <summary>The fleet of the grouping tests: a unified DGX and a discrete two-GPU PC, a model on each GPU, one unplaced.</summary>
    private static (BunitContext Ctx, FakeNodeStatusCache Cache, FakeModelRuntime Runtime) TwoNodes(
        NodeLiveStatus? pc = null, IReadOnlyList<ModelDefinition>? models = null, IMetricsHistory? history = null)
    {
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("qwen", ModelState.Ready, HostPort: 8101));
        runtime.SetStatus(new ModelStatus("coder", ModelState.Ready, HostPort: 8102));
        runtime.SetStatus(new ModelStatus("small", ModelState.Ready, HostPort: 8103));
        var cache = new FakeNodeStatusCache(Dgx(), pc ?? Pc());
        var registry = new ListOnlyNodeRegistry(
            Record("dgx", "DGX Spark"),
            Record("pc", "Office PC", reserve: 4, gpuReserves: new Dictionary<string, double> { ["GPU-a"] = 1, ["GPU-b"] = 2 }),
            Record("old", "Old box") with { Revoked = true },
            Record("new", "New box") with { Approved = false });
        models ??=
        [
            Model("qwen", "dgx", budget: 36),
            Model("small", "pc", budget: 8, gpus: ["GPU-a"]),
            Model("gemma", "dgx", budget: 20),
            Model("coder", "pc", budget: 12, gpus: ["GPU-a", "GPU-b"]),
            Model("orphan", null, budget: 4),
        ];
        return (Context(models, runtime, cache, registry, history), cache, runtime);
    }

    private static string[] GroupIds(IRenderedComponent<Dashboard> cut) =>
        cut.FindAll("[data-testid=node-groups] > section.node-group").Select(g => g.GetAttribute("data-testid")!).ToArray();

    private static string[] RowIds(IRenderedComponent<Models> cut) =>
        cut.FindAll("[data-testid=models-table] tbody tr.models-row").Select(r => r.GetAttribute("data-testid")!["models-row-".Length..]).ToArray();

    // ---------- Snapshot: a single node looks exactly as today ----------

    /// <summary>The page before MN-119, rendered from the same fakes (see <see cref="SingleNodeModels"/>).</summary>
    private static string Fixture(string name, [CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", name));

    private static ModelDefinition[] SingleNodeModels(string? node) => [Model("qwen", node, budget: 36), Model("gemma", node, budget: 20)];

    private static FakeModelRuntime SingleNodeRuntime()
    {
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus("qwen", ModelState.Ready, HostPort: 8101));
        return runtime;
    }

    /// <summary>
    /// Removes what differs between two renders of the same page and carries no look: the load time, per-instance
    /// dialog ids, element references, event handler ids and CSS-isolation scope attributes.
    /// </summary>
    private static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Normalize(string markup)
    {
        markup = ScopeAttribute().Replace(markup, "");
        markup = LiveAt().Replace(markup, "data-live-at=\"\"");
        markup = DialogId().Replace(markup, "dialog-N");
        markup = ElementReference().Replace(markup, "");
        markup = EventHandlerId().Replace(markup, "$1=\"0\"");
        return markup;
    }

    [GeneratedRegex(@"\sb-[a-z0-9]{10}(?:="""")?")]
    private static partial Regex ScopeAttribute();

    [GeneratedRegex(@"data-live-at=""[^""]*""")]
    private static partial Regex LiveAt();

    [GeneratedRegex(@"dialog-\d+")]
    private static partial Regex DialogId();

    [GeneratedRegex(@"\sblazor:elementReference=""[^""]*""")]
    private static partial Regex ElementReference();

    [GeneratedRegex(@"(blazor:on\w+)=""\d+""")]
    private static partial Regex EventHandlerId();

    [Fact]
    public void SingleNode_KeepsMemoryHeadroom_AndAddsTheSharedAttentionReadout()
    {
        using var ctx = Context([], cache: new FakeNodeStatusCache(Dgx()));

        var bar = ctx.Render<MemoryHeadroom>(p => p
            .Add(x => x.TotalGiB, 119.7).Add(x => x.AvailableGiB, 80.5).Add(x => x.ModelsGiB, 36).Add(x => x.ReserveGiB, 8));
        Normalize(bar.Markup).MarkupMatches(Normalize(Fixture("memory-headroom-unified.html")));

        using var status = Context([Model("a", "dgx"), Model("b", "dgx")], cache: new FakeNodeStatusCache(Dgx()));
        var readout = status.Render<StatusReadout>();
        Normalize(readout.Markup).MarkupMatches(Normalize(Fixture("status-readout-single-node.html")));
    }

    // ---------- Several nodes ----------

    [Fact]
    public void TwoNodes_UnifiedNodeHasOneBar_WithItsOwnNumbers()
    {
        var (ctx, _, _) = TwoNodes();
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            var bar = cut.Find("[data-testid=node-dgx-memory-bar]");
            Assert.Equal("node-dgx-headroom-title", bar.GetAttribute("aria-labelledby"));
            Assert.Equal("H4", cut.Find("#node-dgx-headroom-title").TagName);
            Assert.Contains("119.7 GiB total", cut.Find("#node-dgx-headroom-title").TextContent, StringComparison.Ordinal);
            // 80.5 free minus the node's 8 GiB reserve.
            Assert.Equal("72.5 GiB", cut.Find("[data-testid=node-dgx-memory-admittable]").TextContent);
            var legend = cut.Find("[data-testid=node-dgx-memory-legend]").TextContent;
            Assert.Contains("36.0 GiB", legend, StringComparison.Ordinal); // qwen is Ready, gemma Stopped
            Assert.DoesNotContain("56.0 GiB", legend, StringComparison.Ordinal);
            Assert.Empty(bar.QuerySelectorAll("[data-testid^=node-dgx-memory-gpu-]"));
        }
    }

    [Fact]
    public void TwoNodes_DiscreteNodeHasOneBarPerGpu_WithBudgetSharesAndGpuReserves()
    {
        var (ctx, _, _) = TwoNodes();
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            var bar = cut.Find("[data-testid=node-pc-memory-bar]");
            Assert.Contains("2 GPUs", cut.Find("#node-pc-headroom-title").TextContent, StringComparison.Ordinal);
            Assert.Contains("40.0 GiB", cut.Find("[data-testid=node-pc-memory-host]").TextContent, StringComparison.Ordinal);

            var gpu0 = cut.Find("[data-testid=node-pc-memory-gpu-0]");
            Assert.Equal("GPU-a", gpu0.GetAttribute("data-uuid"));
            Assert.Contains("RTX 4060 Ti", gpu0.TextContent, StringComparison.Ordinal);
            Assert.Contains("16.0 GiB total", gpu0.TextContent, StringComparison.Ordinal);
            Assert.Equal("5.0 GiB", cut.Find("[data-testid=node-pc-memory-gpu-0-admittable]").TextContent); // 6 free − 1 reserve
            // small (8 GiB on GPU-a) + half of coder (12 GiB over two GPUs) = 14 GiB on GPU 0; 6 on GPU 1.
            Assert.Contains("14.0 GiB", cut.Find("[data-testid=node-pc-memory-gpu-0-legend]").TextContent, StringComparison.Ordinal);
            Assert.Contains("6.0 GiB", cut.Find("[data-testid=node-pc-memory-gpu-1-legend]").TextContent, StringComparison.Ordinal);
            Assert.Equal("13.0 GiB", cut.Find("[data-testid=node-pc-memory-gpu-1-admittable]").TextContent); // 15 free − 2 reserve
            Assert.Contains("GPU 1 memory of 16.0 GiB", cut.Find("[data-testid=node-pc-memory-gpu-1-track]").GetAttribute("aria-label"), StringComparison.Ordinal);
            Assert.Equal(2, bar.QuerySelectorAll(".headroom-gpu").Length);
        }
    }

    [Fact]
    public void TwoNodes_DiscreteNode_UsesMeasuredPerGpuModelMemory_WhenTheCollectorHasIt()
    {
        var history = new NodeMetricsHistory();
        history.Hosts["pc"] =
        [
            new HostSample(Now, new MemorySnapshot(64, 40), null)
            {
                Gpus =
                [
                    new HostGpuSample(0, "GPU-a", 30, 10, 16, new Dictionary<string, double> { ["small"] = 7.25, ["coder"] = 2.5 }),
                    new HostGpuSample(1, "GPU-b", 5, 1, 16),
                ],
            },
        ];
        var (ctx, _, _) = TwoNodes(history: history);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            Assert.Contains("9.8 GiB", cut.Find("[data-testid=node-pc-memory-gpu-0-legend]").TextContent, StringComparison.Ordinal);
            // GPU 1 has no measured split: the budget share stays.
            Assert.Contains("6.0 GiB", cut.Find("[data-testid=node-pc-memory-gpu-1-legend]").TextContent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DegradedAndDrainingNodes_ShowTheirStateAndReasons_ButStayLive()
    {
        var (ctx, cache, _) = TwoNodes(Pc(NodeState.Degraded, ["Docker is unreachable."]));
        using (ctx)
        {
            cache.Set(Dgx(draining: true), Pc(NodeState.Degraded, ["Docker is unreachable."]));
            var cut = ctx.Render<Dashboard>();

            Assert.Equal("degraded", cut.Find("[data-testid=node-group-state-pc]").GetAttribute("data-state"));
            Assert.Contains("Docker is unreachable.", cut.Find("[data-testid=node-group-reasons-pc]").TextContent, StringComparison.Ordinal);
            Assert.Equal("draining", cut.Find("[data-testid=node-group-state-dgx]").GetAttribute("data-state"));
            Assert.DoesNotContain("node-group--stale", cut.Find("[data-testid=node-group-pc]").ClassList);
            Assert.DoesNotContain("node-group--stale", cut.Find("[data-testid=node-group-dgx]").ClassList);
        }
    }

    [Fact]
    public void FleetTiles_SumFreeMemoryOverNodes_AndNameTheBusiestGpu()
    {
        var history = new NodeMetricsHistory();
        var t0 = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        history.Hosts["dgx"] =
        [
            new HostSample(t0, new MemorySnapshot(119.7, 80), new GpuSnapshot(20, null, null, null)),
            new HostSample(t0.AddSeconds(5.2), new MemorySnapshot(119.7, 80.5), new GpuSnapshot(35, null, null, null)),
        ];
        history.Hosts["pc"] =
        [
            new HostSample(t0.AddSeconds(1), new MemorySnapshot(64, 40), null) { Gpus = [new HostGpuSample(0, "GPU-a", 60, 10, 16), new HostGpuSample(1, "GPU-b", 90, 1, 16)] },
        ];
        var (ctx, _, _) = TwoNodes(history: history);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // dgx 80.5 free of 119.7 (unified) + pc 6 + 15 free of 32 VRAM (discrete).
            Assert.Equal("101.5", cut.Find("[data-testid=kpi-memory-value]").TextContent);
            Assert.Contains("50.2 of 151.7 GiB used across 2 nodes", cut.Find("[data-testid=kpi-memory-detail]").TextContent, StringComparison.Ordinal);
            Assert.Equal("90", cut.Find("[data-testid=kpi-gpu-value]").TextContent);
            Assert.Contains("Busiest of 2 nodes: Office PC", cut.Find("[data-testid=kpi-gpu-detail]").TextContent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ForceStart_OnAnotherNode_QuotesThatNodesMemoryAndReserve()
    {
        var runtime = new FakeModelRuntime { RefuseUnlessForce = true };
        var cache = new FakeNodeStatusCache(Dgx(), Simple("box", "Box") with { Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 64, HostAvailableGiB = 30 } });
        var registry = new ListOnlyNodeRegistry(Record("dgx", "DGX Spark"), Record("box", "Box", reserve: 6));
        using var ctx = Context([Model("big", "box", budget: 36)], runtime, cache, registry);

        var cut = ctx.Render<Models>();
        cut.ClickAt("[data-testid=start-big]");

        var numbers = cut.Find("[data-testid=force-start-numbers]").TextContent;
        Assert.Contains("36.0 GiB", numbers, StringComparison.Ordinal);
        Assert.Contains("24.0 GiB", numbers, StringComparison.Ordinal); // 30 free on box − its 6 GiB reserve
        Assert.Contains("6.0 GiB", numbers, StringComparison.Ordinal);
        Assert.Contains("30.0 GiB", numbers, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, false)] // default 5 s buckets: dgx and pc never share one, every point is a gap
    [InlineData(10, true)]    // a configured 10 s interval: both nodes land in the same buckets
    public void FleetTrend_AlignsOnTheConfiguredSamplingInterval(int? intervalSeconds, bool drawn)
    {
        var history = new NodeMetricsHistory();
        var t0 = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        history.Hosts["dgx"] = [new HostSample(t0.AddSeconds(1), new MemorySnapshot(119.7, 80), null), new HostSample(t0.AddSeconds(11), new MemorySnapshot(119.7, 81), null)];
        history.Hosts["pc"] = [new HostSample(t0.AddSeconds(6), new MemorySnapshot(64, 40), null), new HostSample(t0.AddSeconds(16), new MemorySnapshot(64, 41), null)];
        var (ctx, _, _) = TwoNodes(history: history);
        using (ctx)
        {
            if (intervalSeconds is { } seconds)
            {
                ctx.Services.AddSingleton(Options.Create(new MirbadLLM.Orchestration.MetricsOptions { MetricsSampleIntervalSeconds = seconds }));
            }

            var cut = ctx.Render<Dashboard>();

            Assert.Equal(drawn, cut.FindAll("svg[data-testid=kpi-memory-trend]").Count == 1);
        }
    }

    [Fact]
    public void ForceStart_OnADiscreteNode_QuotesThatNodesHostMemory_NotTheBootstrapProbe()
    {
        // MN-126: until per-GPU admission (MN-105/MN-106) the agent admits against its host pool on discrete nodes too.
        var runtime = new FakeModelRuntime { RefuseUnlessForce = true };
        var cache = new FakeNodeStatusCache(Dgx(), Pc());
        var registry = new ListOnlyNodeRegistry(Record("dgx", "DGX Spark"), Record("pc", "Office PC", reserve: 4));
        using var ctx = Context([Model("big", "pc", budget: 48)], runtime, cache, registry);

        var cut = ctx.Render<Models>();
        cut.ClickAt("[data-testid=start-big]");

        var numbers = cut.Find("[data-testid=force-start-numbers]").TextContent;
        Assert.Contains("48.0 GiB", numbers, StringComparison.Ordinal);
        Assert.Contains("36.0 GiB", numbers, StringComparison.Ordinal); // 40 free on pc − its 4 GiB reserve
        Assert.Contains("4.0 GiB", numbers, StringComparison.Ordinal);
        Assert.Contains("40.0 GiB", numbers, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeStates_FollowTheCache_OnTheNextRefresh()
    {
        var (ctx, cache, _) = TwoNodes();
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.Equal("online", cut.Find("[data-testid=node-group-state-pc]").GetAttribute("data-state"));

            cache.Set(Dgx(), Pc(NodeState.Offline));
            cut.WaitForAssertion(
                () => Assert.Equal("offline", cut.Find("[data-testid=node-group-state-pc]").GetAttribute("data-state")),
                TimeSpan.FromSeconds(15));
        }
    }

    // ---------- Status bar ----------

    [Fact]
    public void RailGeneration_SumsOnlyReadyGenerationModels_AndIgnoresNonfiniteReadings()
    {
        var runtime = new FakeModelRuntime();
        foreach (string id in new[] { "chat", "embed", "unknown" })
        {
            runtime.SetStatus(new(id, ModelState.Ready));
        }
        static ModelKpis Kpis(double rate) => new(new ModelMetrics(0, 0, 0, rate, 0), 0, null, null, null, null, null, null, 0, new Dictionary<string, double>(), 0);
        var history = new FakeMetricsHistory
        {
            LatestKpis = new Dictionary<string, ModelKpis>
            {
                ["chat"] = Kpis(12.5), ["embed"] = Kpis(900), ["unknown"] = Kpis(double.NaN), ["stopped"] = Kpis(100),
            },
        };
        using var ctx = Context([Model("chat", "dgx"), Model("embed", "dgx", tags: ["embedding"]), Model("unknown", "dgx"), Model("stopped", "dgx")], runtime, history: history);
        // Overview's rail item shows the fleet's generation while nothing needs attention (Monitoring's old count).
        var cut = ctx.Render<RailCounts>(p => p.Add(x => x.Kind, "overview"));
        Assert.Equal("12.5", cut.Find("[data-testid=rail-generation]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=rail-attention]"));
        Assert.Single(cut.FindAll(".nav-meta--live"));
    }

    [Fact]
    public async Task Attention_RailAndStatusAgree_AndRefreshTogether()
    {
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new("failed", ModelState.Failed));
        runtime.SetStatus(new("starting", ModelState.Starting));
        runtime.SetStatus(new("stopping", ModelState.Stopping));
        runtime.SetStatus(new("stalled", ModelState.Ready));
        var history = new FakeMetricsHistory();
        var kpis = new ModelKpis(new ModelMetrics(2, 0, 0.1, 0, 0), 0, null, null, null, null, null, null, 0, new Dictionary<string, double>(), 0);
        history.ModelSamplesById["stalled"] = Enumerable.Range(0, 73)
            .Select(i => new ModelSample(DateTimeOffset.UtcNow.AddMinutes(-6).AddSeconds(i * 5), kpis)).ToList();
        var cache = new FakeNodeStatusCache(Dgx(draining: true), Pc(NodeState.Offline), Simple("lab", "Lab", NodeState.Degraded));
        await using var ctx = Context([Model("failed", "dgx"), Model("starting", "dgx"), Model("stopping", "dgx"), Model("stalled", "dgx")], runtime, cache, history: history);
        ctx.Services.AddScoped<LiveDataService>();
        var rail = ctx.Render<RailCounts>(p => p.Add(x => x.Kind, "overview"));
        var status = ctx.Render<StatusReadout>();
        Assert.Equal("7", rail.Find("[data-testid=rail-attention]").TextContent);
        Assert.Equal("7", status.Find("[data-testid=status-attention]").TextContent.Trim());

        foreach (string id in new[] { "failed", "starting", "stopping", "stalled" })
        {
            runtime.SetStatus(new(id, ModelState.Stopped));
        }
        cache.Set(Dgx(), Pc());
        await ctx.Services.GetRequiredService<LiveDataService>().RefreshAsync(Xunit.TestContext.Current.CancellationToken);
        rail.WaitForAssertion(() => Assert.Empty(rail.FindAll("[data-testid=rail-attention]")));
        status.WaitForAssertion(() => Assert.Equal("0", status.Find("[data-testid=status-attention]").TextContent.Trim()));
    }

    [Fact]
    public async Task Rail_CountsAndNodeTree_UseRealStatesAndMemory_AndKeepTheActiveLeaf()
    {
        var (ctx, _, _) = TwoNodes(Pc(NodeState.Offline));
        await using (ctx)
        {
            ctx.Services.AddScoped<LiveDataService>();
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("nodes/dgx");
            var models = ctx.Render<RailCounts>(p => p.Add(x => x.Kind, "models"));
            var nodes = ctx.Render<RailCounts>(p => p.Add(x => x.Kind, "nodes"));
            var tree = ctx.Render<RailNodes>();
            Assert.Equal("3/5", models.Find("[data-testid=rail-models]").TextContent);
            Assert.Equal("1/2", nodes.Find("[data-testid=rail-node-count]").TextContent);
            Assert.NotNull(tree.Find("[data-testid=rail-nodes][data-tour=rail-nodes]"));
            var dgx = tree.Find("[data-testid=rail-node-dgx]");
            Assert.Equal("page", dgx.GetAttribute("aria-current"));
            Assert.Contains("Online", dgx.GetAttribute("title"), StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(dgx.QuerySelector(".rail-node-memory > span[style]"));
            Assert.NotNull(tree.Find("[data-testid=rail-node-pc] .rail-node-memory--missing"));
        }
    }

    [Fact]
    public void StatusBar_SeveralNodes_ShowsTheNodeCount_AndChipsForDownAndDegradedNodes()
    {
        var cache = new FakeNodeStatusCache(
            Dgx(),
            Pc(NodeState.Offline),
            Simple("lab", "Lab", NodeState.Degraded),
            Simple("new", "New", NodeState.PendingApproval));
        using var ctx = Context([Model("a", "dgx"), Model("b", "pc")], cache: cache);

        var cut = ctx.Render<StatusReadout>();

        Assert.Contains("0 / 2", cut.Find("[data-testid=status-ready]").TextContent, StringComparison.Ordinal);
        Assert.Equal("2 / 3 online", Words(cut.Find("[data-testid=status-nodes]").TextContent));
        Assert.Equal("2", cut.Find("[data-testid=status-attention]").TextContent.Trim()); // one node offline + one degraded
        Assert.Empty(cut.FindAll("[data-testid=status-free]")); // one node's free memory would mislead
        Assert.Empty(cut.FindAll("[data-testid=status-gpu]"));

        var down = cut.Find("[data-testid=status-nodes-down]");
        Assert.Contains("chip--failed", down.ClassList);
        Assert.Equal("nodes", down.GetAttribute("href"));
        Assert.Contains("1 node offline", down.TextContent, StringComparison.Ordinal);
        Assert.NotNull(down.QuerySelector("svg"));

        var degraded = cut.Find("[data-testid=status-nodes-degraded]");
        Assert.Contains("chip--busy", degraded.ClassList);
        Assert.Contains("Node degraded", degraded.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusBar_IncompatibleNode_CountsAsNotServing_AndHealthyFleetHasNoChips()
    {
        var cache = new FakeNodeStatusCache(Dgx(), Pc(NodeState.Incompatible), Simple("lab", "Lab", NodeState.Offline));
        using var ctx = Context([Model("a", "dgx")], cache: cache);

        var cut = ctx.Render<StatusReadout>();
        Assert.Contains("2 nodes not serving", cut.Find("[data-testid=status-nodes-down]").TextContent, StringComparison.Ordinal);

        using var healthy = Context([Model("a", "dgx")], cache: new FakeNodeStatusCache(Dgx(), Pc()));
        var ok = healthy.Render<StatusReadout>();
        Assert.Empty(ok.FindAll("[data-testid=status-nodes-down]"));
        Assert.Empty(ok.FindAll("[data-testid=status-nodes-degraded]"));
        Assert.Equal("2 / 2 online", Words(ok.Find("[data-testid=status-nodes]").TextContent));
        Assert.Equal("0", ok.Find("[data-testid=status-attention]").TextContent.Trim());
    }

    // ---------- MemoryHeadroom per-GPU variant ----------

    [Fact]
    public void MemoryHeadroom_PerGpu_DrawsOneBarPerGpu_AndDashesWhatIsNotReported()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<MemoryHeadroom>(p => p
            .Add(x => x.Title, "GPU memory")
            .Add(x => x.HeadingLevel, 3)
            .Add(x => x.IdPrefix, "x")
            .Add(x => x.Gpus, [
                new GpuHeadroom(0, "GPU-a", "RTX 4060 Ti", 16, 6, 8, 1),
                new GpuHeadroom(1, "GPU-b", null, null, null, 0, 0),
            ]));

        Assert.Equal("H3", cut.Find("#x-headroom-title").TagName);
        Assert.Equal("x-headroom-title", cut.Find("[data-testid=x-memory-bar]").GetAttribute("aria-labelledby"));
        var segments = cut.FindAll("[data-testid=x-memory-gpu-0-track] .headroom-seg");
        Assert.Equal(["width:50%", "width:12.5%", "width:6.25%"], segments.Select(s => s.GetAttribute("style")).ToArray()); // 8, 10−8, 1 of 16

        Assert.Empty(cut.FindAll("[data-testid=x-memory-gpu-1-track] .headroom-seg"));
        Assert.Equal("—", cut.Find("[data-testid=x-memory-gpu-1-admittable]").TextContent);
        Assert.Contains("not reported", cut.Find("[data-testid=x-memory-gpu-1-track]").GetAttribute("aria-label"), StringComparison.Ordinal);
        Assert.Contains("—", cut.Find("[data-testid=x-memory-gpu-1-legend]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=x-memory-host]"));
    }
}
