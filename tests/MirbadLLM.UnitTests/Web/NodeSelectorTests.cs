using System.Text.RegularExpressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Ui;
using FakeModelRegistry = MirbadLLM.UnitTests.Web.Fakes.FakeModelRegistry;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707, xUnit1051 // underscore test names read better; hand-written fakes ignore cancellation.

/// <summary>
/// MN-120: the node selectors of the Overview (which took over Monitoring's node rail) and of Services (design §8.3, §9.1,
/// §12.3). With one node there is no selector, the node's host history and the registered services manager; with
/// several, the Overview's scope rail (a card per node, any number of nodes) or Services' Segmented control (≤ 4 nodes)
/// or select picks the node, the choice is kept in <c>?node=</c>, the Overview shows that node's history (one memory
/// chart per GPU plus host RAM on a discrete node) and only its models, and Services drives that node's manager and memory.
/// </summary>
public sealed class NodeSelectorTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    // ================================================================ selector component

    [Fact]
    public void Selector_IsHidden_WithOneNode()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<NodeSelector>(p => p
            .Add(x => x.Nodes, [new NodeSelector.Option("dgx", "DGX", NodeState.Online)])
            .Add(x => x.SelectedId, "dgx"));

        Assert.Empty(cut.FindAll("[data-testid=node-selector]"));
        Assert.Equal("", cut.Markup.Trim());
    }

    [Fact]
    public void Selector_IsSegmented_UpToFourNodes_AndASelectAbove()
    {
        using var ctx = new BunitContext();
        var four = Enumerable.Range(1, 4).Select(i => new NodeSelector.Option($"n{i}", $"Node {i}", NodeState.Online)).ToList();
        var segmented = ctx.Render<NodeSelector>(p => p.Add(x => x.Nodes, four).Add(x => x.SelectedId, "n2"));

        Assert.Equal(4, segmented.FindAll("[data-testid=node-selector-segmented] input[type=radio]").Count);
        Assert.Empty(segmented.FindAll("[data-testid=node-selector-select]"));
        Assert.True(segmented.Find("[data-testid=node-option-n2]").HasAttribute("checked"));
        Assert.False(segmented.Find("[data-testid=node-option-n1]").HasAttribute("checked"));
        Assert.Equal("radiogroup", segmented.Find("[data-testid=node-selector-segmented]").GetAttribute("role"));

        var five = four.Append(new NodeSelector.Option("n5", "Node 5", NodeState.Offline)).ToList();
        var select = ctx.Render<NodeSelector>(p => p.Add(x => x.Nodes, five).Add(x => x.SelectedId, "n5"));

        Assert.Empty(select.FindAll("[data-testid=node-selector-segmented]"));
        var options = select.FindAll("[data-testid=node-selector-select] option");
        Assert.Equal(["n1", "n2", "n3", "n4", "n5"], options.Select(o => o.GetAttribute("value")));
        Assert.Equal("Node 5 · Offline", options[4].TextContent);
        Assert.True(options[4].HasAttribute("selected"));
        // The select has a label for assistive tech even though it is visually hidden.
        string id = select.Find("[data-testid=node-selector-select]").Id!;
        Assert.Equal("Node", select.Find($"label[for={id}]").TextContent);
    }

    [Fact]
    public void Selector_NamesEveryStateButOnline()
    {
        Assert.Null(NodeSelector.StateSuffix(new("a", "A", NodeState.Online)));
        Assert.Equal("Offline", NodeSelector.StateSuffix(new("a", "A", NodeState.Offline)));
        Assert.Equal("Degraded", NodeSelector.StateSuffix(new("a", "A", NodeState.Degraded)));
        Assert.Equal("Draining", NodeSelector.StateSuffix(new("a", "A", NodeState.Online, Draining: true)));
        Assert.Equal("Offline, draining", NodeSelector.StateSuffix(new("a", "A", NodeState.Offline, Draining: true)));
    }

    [Fact]
    public void Selector_WritesTheChoiceIntoTheUrl_KeepingOtherParameters()
    {
        using var ctx = new BunitContext();
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/?model=m1");
        string? changed = null;
        var cut = ctx.Render<NodeSelector>(p => p
            .Add(x => x.Nodes, [new NodeSelector.Option("dgx", "DGX", NodeState.Online), new NodeSelector.Option("pc", "PC", NodeState.Online)])
            .Add(x => x.SelectedId, "dgx")
            .Add(x => x.SelectedIdChanged, id => changed = id));

        cut.ChangeAt("[data-testid=node-option-pc]", true);

        Assert.Equal("pc", changed);
        var query = new Uri(nav.Uri).Query;
        Assert.Contains("node=pc", query, StringComparison.Ordinal);
        Assert.Contains("model=m1", query, StringComparison.Ordinal);
        Assert.Equal("pc", NodeSelector.FromQuery(nav));
    }

    [Fact]
    public void Resolve_PrefersTheRequestedNode_ThenTheDefault_ThenTheFirst()
    {
        List<NodeSelector.Option> nodes = [new("a", "A", NodeState.Online), new("dgx", "DGX", NodeState.Online)];
        Assert.Equal("a", NodeSelector.Resolve(nodes, "a", "dgx"));
        Assert.Equal("dgx", NodeSelector.Resolve(nodes, "gone", "dgx"));
        Assert.Equal("dgx", NodeSelector.Resolve(nodes, null, "dgx"));
        Assert.Equal("a", NodeSelector.Resolve([new("a", "A", NodeState.Online)], null, "dgx"));
        Assert.Equal("dgx", NodeSelector.Resolve([], "a", "dgx"));
    }

    [Fact]
    public void From_LeavesOutNodesPendingApproval()
    {
        var rows = new List<Nodes.NodeRow>
        {
            new() { Id = "dgx", Name = "DGX", State = NodeState.Online },
            new() { Id = "new", Name = "New", State = NodeState.PendingApproval },
            new() { Id = "pc", Name = "PC", State = NodeState.Offline, Draining = true },
        };

        var options = NodeSelector.From(rows);

        Assert.Equal(["dgx", "pc"], options.Select(o => o.Id));
        Assert.True(options[1].Draining);
    }

    // ================================================================ Monitoring

    [Fact]
    public void Monitoring_OneNode_HasNoSelector_AndReadsTheDefaultSeries_AsBefore()
    {
        var history = new NodeHistory();
        history.Hosts["dgx"] = [Unified(0, 128, 90)];
        history.Kpis["m1"] = Kpis(10);
        using var ctx = MonitoringContext(history, [Model("m1", "dgx")], Node("dgx"));

        var cut = ctx.Render<Dashboard>();

        Assert.Empty(cut.FindAll("[data-testid=monitoring-node-selector]"));
        Assert.Contains("dgx", history.NodeReads);
        Assert.Equal("Last 15 min", cut.Find("[data-testid=host-section-note]").TextContent);
        Assert.NotNull(cut.Find("[data-testid=line-chart-memory-by-model]"));
        Assert.NotNull(cut.Find("[data-testid=model-row-m1]"));
    }

    [Fact]
    public void Monitoring_OneNode_ShowsTheSameHistory_WithOrWithoutANodeRegistry()
    {
        string History(params NodeRecord[] nodes)
        {
            var history = new NodeHistory();
            history.Hosts["dgx"] = [Unified(0, 128, 90), Unified(5, 128, 80)];
            history.Kpis["m1"] = Kpis(10);
            using var ctx = MonitoringContext(history, [Model("m1", "dgx")], nodes);
            var cut = ctx.Render<Dashboard>();
            Assert.NotNull(cut.Find("[data-testid=model-row-m1]"));
            return Normalize(cut.Find("[data-testid=host-history] .history-body").InnerHtml);
        }

        // The registry adds a node panel (and an attention item: a node the cache never saw is offline), never another history.
        Assert.Equal(History(), History(Node("dgx")));
    }

    [Fact]
    public void Monitoring_TwoNodes_ShowsTheRail_AndTheNodeFromTheUrl()
    {
        var history = TwoNodeHistory();
        using var ctx = MonitoringContext(history, [Model("on-dgx", "dgx"), Model("on-pc", "pc"), Model("legacy", null)],
            Node("dgx", "DGX Spark"), Node("pc", "Office PC"));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/?node=pc");

        var cut = ctx.Render<Dashboard>();

        Assert.Equal("true", cut.Find("[data-testid=node-option-pc]").GetAttribute("aria-pressed"));
        Assert.Equal("false", cut.Find("[data-testid=node-option-dgx]").GetAttribute("aria-pressed"));
        Assert.Equal("false", cut.Find("[data-testid=node-option-all]").GetAttribute("aria-pressed"));
        Assert.Contains("pc", history.NodeReads);
        Assert.Contains("12", cut.Find("[data-testid=host-memory]").TextContent, StringComparison.Ordinal); // 32 − 20 on the PC
        Assert.EndsWith("Office PC", cut.Find("[data-testid=host-section-note]").TextContent, StringComparison.Ordinal);
        // Only the PC's model has a row; the DGX's and the unplaced (DGX) model do not.
        Assert.NotNull(cut.Find("[data-testid=model-row-on-pc]"));
        Assert.Empty(cut.FindAll("[data-testid=model-row-on-dgx]"));
        Assert.Empty(cut.FindAll("[data-testid=model-row-legacy]"));
        Assert.Equal("Generation on Office PC", cut.Find("#pulse-title").TextContent);
        Assert.Equal("20", cut.Find("[data-testid=pulse-total]").TextContent.Trim());
    }

    [Fact]
    public void Monitoring_PickingNodes_FiltersTheSeriesAndModels_AndKeepsItInTheUrl()
    {
        var history = TwoNodeHistory();
        using var ctx = MonitoringContext(history, [Model("on-dgx", "dgx"), Model("on-pc", "pc"), Model("legacy", null)],
            Node("dgx", "DGX Spark"), Node("pc", "Office PC"));
        var nav = ctx.Services.GetRequiredService<NavigationManager>();

        var cut = ctx.Render<Dashboard>();

        // Default: All nodes. Picking the bootstrap node shows its own and the unplaced models.
        Assert.Equal("true", cut.Find("[data-testid=node-option-all]").GetAttribute("aria-pressed"));
        cut.Find("[data-testid=node-option-dgx]").Click();
        cut.WaitForAssertion(() => Assert.Contains("38", cut.Find("[data-testid=host-memory]").TextContent, StringComparison.Ordinal)); // 128 − 90
        Assert.Contains("node=dgx", new Uri(nav.Uri).Query, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=model-row-on-dgx]"));
        Assert.NotNull(cut.Find("[data-testid=model-row-legacy]"));
        Assert.Empty(cut.FindAll("[data-testid=model-row-on-pc]"));

        cut.Find("[data-testid=node-option-pc]").Click();

        Assert.Contains("node=pc", new Uri(nav.Uri).Query, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.Contains("12", cut.Find("[data-testid=host-memory]").TextContent, StringComparison.Ordinal));
        Assert.NotNull(cut.Find("[data-testid=model-row-on-pc]"));
        Assert.Empty(cut.FindAll("[data-testid=model-row-on-dgx]"));

        // Pressing the filtered node again, or "Show all nodes", goes back to every node.
        cut.Find("[data-testid=node-option-pc]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=host-memory]")));
        Assert.DoesNotContain("node=", new Uri(nav.Uri).Query, StringComparison.Ordinal);
        cut.Find("[data-testid=node-option-pc]").Click();
        cut.Find("[data-testid=model-table-clear]").Click();
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=node-option-all]").GetAttribute("aria-pressed")));
    }

    [Fact]
    public void Monitoring_TwoNodes_DefaultsToAllNodes_WithACardPerNode_AndEveryReadyModel()
    {
        var history = TwoNodeHistory();
        using var ctx = MonitoringContext(history, [Model("on-dgx", "dgx"), Model("on-pc", "pc"), Model("legacy", null)],
            Node("dgx", "DGX Spark"), Node("pc", "Office PC"));
        var nav = ctx.Services.GetRequiredService<NavigationManager>();

        var cut = ctx.Render<Dashboard>();

        Assert.Equal("true", cut.Find("[data-testid=node-option-all]").GetAttribute("aria-pressed"));
        Assert.Empty(cut.FindAll("[data-testid=host-section-note]"));
        Assert.Contains("dgx", history.NodeReads);
        Assert.Contains("pc", history.NodeReads);
        // Each node's own memory, measured: 128 − 90 on the DGX, 32 − 20 on the PC; the ring says it in words too.
        Assert.Equal("Memory: 30%", cut.Find("[data-testid=node-option-dgx-memory]").GetAttribute("aria-label"));
        Assert.Contains("38.0 of 128.0 GiB", cut.Find("[data-testid=node-option-dgx]").GetAttribute("title"), StringComparison.Ordinal);
        Assert.Contains("12.0 of 32.0 GiB", cut.Find("[data-testid=node-option-pc]").GetAttribute("title"), StringComparison.Ordinal);
        Assert.Equal("Offline", cut.Find("[data-testid=node-option-dgx-state]").TextContent.Trim()); // no status cache: the registry alone
        // A bead per placed model: the DGX runs its own and the unplaced one.
        Assert.Equal("2 running", cut.Find("[data-testid=node-option-dgx-count]").TextContent);
        Assert.Equal("1 running", cut.Find("[data-testid=node-option-pc-count]").TextContent);
        // Models from both nodes, each saying where it runs; the pulse sums all of them.
        Assert.Contains("Office PC · vLLM", cut.Find("[data-testid=model-row-on-pc]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=model-row-on-dgx]"));
        Assert.Equal("41", cut.Find("[data-testid=pulse-total]").TextContent.Trim()); // 10 + 11 + 20
        Assert.Equal("3/3", cut.Find("[data-testid=pulse-ready]").TextContent);

        // A node card narrows the scope and keeps it in the URL; All nodes clears it again.
        cut.Find("[data-testid=node-option-pc]").Click();
        cut.WaitForAssertion(() => Assert.Contains("12", cut.Find("[data-testid=host-memory]").TextContent, StringComparison.Ordinal));
        Assert.Contains("node=pc", new Uri(nav.Uri).Query, StringComparison.Ordinal);
        cut.Find("[data-testid=node-option-all]").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=host-memory]")));
        Assert.DoesNotContain("node=", new Uri(nav.Uri).Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Monitoring_UnifiedNodeWithoutGpuReadings_ExplainsTheDashes()
    {
        var history = new NodeHistory();
        history.Hosts["dgx"] = [new HostSample(DateTimeOffset.UtcNow, new MemorySnapshot(64, 24), null)];
        history.Hosts["pc"] = [];
        using var ctx = MonitoringContext(history, [], Node("dgx"), Node("pc", "Office PC"));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/?node=dgx");

        var cut = ctx.Render<Dashboard>();

        Assert.Equal("Not reported by this node", cut.Find("[data-testid=host-gpu-util-detail]").TextContent.Trim());
        Assert.Equal("Not reported by this node", cut.Find("[data-testid=host-power-detail]").TextContent.Trim());
        Assert.Equal("dgx reports no GPU utilization (no GPU readout on this node)",
            cut.Find("[data-testid=line-chart-gpu-utilization-empty]").TextContent);
        Assert.Equal("No model was Ready on dgx in the last 15 min, and no node sent a GPU reading.", cut.Find("[data-testid=signal-empty]").TextContent);

        cut.Find("[data-testid=node-option-all]").Click();
        cut.WaitForAssertion(() => Assert.Contains("GPU — · — · —", cut.Find("[data-testid=node-option-dgx]").TextContent, StringComparison.Ordinal));
        Assert.Contains("No samples in the last 15 min", cut.Find("[data-testid=node-option-pc]").TextContent, StringComparison.Ordinal);
        Assert.Equal("Memory: —", cut.Find("[data-testid=node-option-pc-memory]").GetAttribute("aria-label"));
        Assert.Equal("No models placed", cut.Find("[data-testid=node-option-pc-count]").TextContent);
    }

    [Fact]
    public void Monitoring_NodeWithoutReadyModels_SaysSoForThatNode()
    {
        var history = TwoNodeHistory();
        history.Kpis.Remove("on-pc");
        using var ctx = MonitoringContext(history, [Model("on-dgx", "dgx"), Model("on-pc", "pc")], Node("dgx"), Node("pc", "Office PC"));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/?node=pc");

        var cut = ctx.Render<Dashboard>();

        Assert.Contains("No model is Ready on Office PC", cut.Find("[data-testid=monitoring-no-models]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 not running", cut.Find("[data-testid=model-table-fold]").TextContent, StringComparison.Ordinal);
        Assert.Equal("0 running · 1 off", cut.Find("[data-testid=node-option-pc-count]").TextContent);
        Assert.Equal("—", cut.Find("[data-testid=pulse-total]").TextContent.Trim());
        Assert.Equal("0/1", cut.Find("[data-testid=pulse-ready]").TextContent);
    }

    [Fact]
    public void Monitoring_DiscreteNode_DrawsOneMemoryChartPerGpu_PlusHostRam()
    {
        var history = new NodeHistory();
        history.Hosts["dgx"] = [Unified(0, 128, 90)];
        history.Hosts["pc"] =
        [
            Discrete(0, 64, 40, [Gpu(0, "GPU-a", 50, 10, 16, ("on-pc", 8)), Gpu(1, "GPU-b", 30, 4, 16)]),
            Discrete(5, 64, 38, [Gpu(0, "GPU-a", 70, 12, 16, ("on-pc", 8)), Gpu(1, "GPU-b", 10, 5, 16)]),
        ];
        history.Kpis["on-pc"] = Kpis(20);
        using var ctx = MonitoringContext(history, [Model("on-pc", "pc")], Node("dgx"), Node("pc", "Office PC"));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/?node=pc");

        var cut = ctx.Render<Dashboard>();

        Assert.NotNull(cut.Find("[data-testid=host-tiles-discrete]"));
        Assert.NotNull(cut.Find("[data-testid=line-chart-gpu-0-memory-by-model]"));
        Assert.NotNull(cut.Find("[data-testid=line-chart-gpu-1-memory-by-model]"));
        Assert.NotNull(cut.Find("[data-testid=line-chart-host-ram]"));
        Assert.Empty(cut.FindAll("[data-testid=line-chart-memory-by-model]"));
        Assert.Contains("Host RAM used", cut.Find("[data-testid=host-memory]").TextContent, StringComparison.Ordinal);
        Assert.Equal("40", cut.Find("[data-testid=host-gpu-util-value]").TextContent.Trim()); // (70 + 10) / 2
        Assert.Contains("average of 2 GPUs", cut.Find("[data-testid=host-gpu-util-detail]").TextContent, StringComparison.Ordinal);
        // The rail card reads VRAM on a discrete node: 12 + 5 of 16 + 16 GiB.
        Assert.Contains("VRAM, 2 GPUs: 17.0 of 32.0 GiB", cut.Find("[data-testid=node-option-pc]").GetAttribute("title"), StringComparison.Ordinal);

        // The unified DGX keeps the single-pool charts.
        cut.Find("[data-testid=node-option-dgx]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=line-chart-memory-by-model]")));
        Assert.Empty(cut.FindAll("[data-testid=line-chart-gpu-0-memory-by-model]"));
    }

    [Fact]
    public void Overview_TenOrMoreNodes_StayCardsOnOneScrollingRail_WithPageDots()
    {
        var history = TwoNodeHistory();
        NodeRecord[] twelve = [Node("dgx"), Node("pc"), .. Enumerable.Range(3, 10).Select(i => Node($"n{i:00}"))];
        using var ctx = MonitoringContext(history, [], twelve);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/?node=n12");

        var cut = ctx.Render<Dashboard>();

        // No select past eight nodes any more: every node is a card in the one rail, All nodes first.
        Assert.Empty(cut.FindAll("select"));
        var track = cut.Find("[data-testid=scope-track]");
        Assert.Equal(13, track.QuerySelectorAll("button[aria-pressed]").Length);
        Assert.Equal("node-option-all", track.QuerySelector("button")!.GetAttribute("data-testid"));
        // The dots are the browser's (js/overview.js builds one per screenful); the page gives them a labelled group.
        Assert.Equal("Node pages", cut.Find("[data-testid=scope-dots]").GetAttribute("aria-label"));
        // The deep-linked card is pressed, and the page reads that node.
        Assert.Equal("true", cut.Find("[data-testid=node-option-n12]").GetAttribute("aria-pressed"));
        Assert.Contains("n12", history.NodeReads);

        cut.Find("[data-testid=node-option-pc]").Click();
        cut.WaitForAssertion(() => Assert.Contains("12", cut.Find("[data-testid=host-memory]").TextContent, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- series (real code)

    [Fact]
    public void GpuMemory_StacksModelsOtherAndFree_UpToTheGpuTotal_WithGapsWhereTheGpuIsMissing()
    {
        IReadOnlyList<HostSample> host =
        [
            Discrete(0, 64, 40, [Gpu(0, "GPU-a", 50, 12, 16, ("big", 8), ("small", 2))]),
            Discrete(5, 64, 40, [Gpu(1, "GPU-b", 50, 1, 16)]), // GPU-a absent: a gap, not a zero
            Discrete(10, 64, 40, [Gpu(0, "GPU-a", 50, 9, 16, ("big", 8))]),
        ];
        var colors = ModelColors.Assign(["big", "small"]);

        var series = MonitoringSeries.GpuMemory(host, "GPU-a", colors, id => id.ToUpperInvariant());

        Assert.Equal(["BIG", "SMALL", MonitoringSeries.OtherProcesses, MonitoringSeries.Free], series.Select(s => s.Name));
        Assert.Equal([8, null, 8], series[0].Points.Select(p => p.Value));
        Assert.Equal([2, null, null], series[1].Points.Select(p => p.Value));
        Assert.Equal([2, null, 1], series[2].Points.Select(p => p.Value)); // used − models
        Assert.Equal([4, null, 7], series[3].Points.Select(p => p.Value)); // total − used
        Assert.Equal(16, series.Sum(s => s.Points[0].Value ?? 0), 6);
        Assert.Equal(16, series.Sum(s => s.Points[2].Value ?? 0), 6);
        Assert.Equal("var(--ss-cat-1)", series[0].Color);
    }

    [Fact]
    public void Gpus_AreNullOnUnifiedSamples_AndFollowTheNewestIndexOnDiscreteOnes()
    {
        Assert.Null(MonitoringSeries.Gpus([Unified(0, 128, 90)]));

        var gpus = MonitoringSeries.Gpus(
        [
            Discrete(0, 64, 40, [Gpu(0, "GPU-a", 1, 1, 16), Gpu(1, "GPU-b", 1, 1, 16)]),
            Discrete(5, 64, 40, [Gpu(1, "GPU-a", 1, 1, 16), Gpu(0, "GPU-b", 1, 1, 16)]), // renumbered after a driver update
        ]);

        Assert.NotNull(gpus);
        Assert.Equal(["GPU-b", "GPU-a"], gpus.Select(g => g.Uuid));
        Assert.Equal(["GPU 0", "GPU 1"], gpus.Select(g => g.Label));
    }

    [Fact]
    public void GpuUtilization_HasOneLinePerGpu_AndHostRamStacksUsedAndFree()
    {
        IReadOnlyList<HostSample> host =
        [
            Discrete(0, 64, 40, [Gpu(0, "GPU-a", 50, 1, 16), Gpu(1, "GPU-b", 20, 1, 16)]),
            Discrete(5, 64, 30, [Gpu(0, "GPU-a", 60, 1, 16)]),
        ];
        var gpus = MonitoringSeries.Gpus(host)!;

        var util = MonitoringSeries.GpuUtilization(host, gpus);
        Assert.Equal(["GPU 0", "GPU 1"], util.Select(s => s.Name));
        Assert.Equal([50, 60], util[0].Points.Select(p => p.Value));
        Assert.Equal([20, null], util[1].Points.Select(p => p.Value));

        var ram = MonitoringSeries.HostRam(host);
        Assert.Equal([MonitoringSeries.Used, MonitoringSeries.Free], ram.Select(s => s.Name));
        Assert.Equal([24, 34], ram[0].Points.Select(p => p.Value));
        Assert.Equal([40, 30], ram[1].Points.Select(p => p.Value));
    }

    [Fact]
    public void ModelsOn_CountsUnplacedModelsOnlyOnTheDefaultNode()
    {
        ModelDefinition[] models = [Model("a", "dgx"), Model("b", "pc"), Model("c", null)];
        Assert.Equal(["a", "c"], MonitoringSeries.ModelsOn(models, "dgx", "dgx").Order());
        Assert.Equal(["b"], MonitoringSeries.ModelsOn(models, "pc", "dgx"));
    }

    // ================================================================ Services

    [Fact]
    public void Services_OneNode_HasNoSelector_AndUsesTheRegisteredManager()
    {
        var dgx = new ListManager("comfyui");
        var perNode = new FakeNodeHostServices();
        using var ctx = ServicesContext(dgx, perNode, Node("dgx"));

        var cut = ctx.Render<Services>();

        Assert.Empty(cut.FindAll("[data-testid=services-node-selector]"));
        Assert.NotNull(cut.Find("[data-testid=service-row-comfyui]"));
        Assert.Empty(perNode.Requests);
    }

    [Fact]
    public void Services_TwoNodes_ManagesTheSelectedNode_AndSwitchesBack()
    {
        var dgx = new ListManager("comfyui");
        var pc = new ListManager("ollama");
        var perNode = new FakeNodeHostServices { ["pc"] = pc };
        using var ctx = ServicesContext(dgx, perNode, Node("dgx", "DGX Spark", hostServices: true), Node("pc", "Office PC", hostServices: true));
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/services?node=pc");

        var cut = ctx.Render<Services>();

        Assert.True(cut.Find("[data-testid=node-option-pc]").HasAttribute("checked"));
        Assert.NotNull(cut.Find("[data-testid=service-row-ollama]"));
        Assert.Empty(cut.FindAll("[data-testid=service-row-comfyui]"));

        // An action goes to the PC's manager, never the DGX's.
        cut.ClickAt("[data-testid=restart-ollama]");
        cut.ClickAt("[data-testid=service-dialog-confirm]");
        Assert.Equal(["ollama:Restart"], pc.Runs);
        Assert.Empty(dgx.Runs);

        cut.ChangeAt("[data-testid=node-option-dgx]", true);

        Assert.Contains("node=dgx", new Uri(nav.Uri).Query, StringComparison.Ordinal);
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=service-row-comfyui]")));
        Assert.Empty(cut.FindAll("[data-testid=service-row-ollama]"));
    }

    [Fact]
    public void Services_NodeWithoutHostServiceSupport_SaysSo_InsteadOfTheTabs()
    {
        var perNode = new FakeNodeHostServices { ["mac"] = new ListManager("never") };
        using var ctx = ServicesContext(new ListManager("comfyui"), perNode, Node("dgx", hostServices: true), Node("mac", "Mac mini", hostServices: false));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/services?node=mac");

        var cut = ctx.Render<Services>();

        Assert.Contains("Mac mini can't manage services", cut.Find("[data-testid=services-unsupported]").TextContent, StringComparison.Ordinal);
        Assert.Contains("reports no host-service support", cut.Find("[data-testid=services-unsupported-why]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=services-tabs]"));
        Assert.Empty(cut.FindAll("[data-testid=service-row-never]"));
        Assert.Empty(cut.FindAll("[data-testid=service-row-comfyui]"));

        // The explanation names the nodes that can, and switches to one.
        Assert.Contains("Services can be managed on dgx", Regex.Replace(cut.Find("[data-testid=services-unsupported-others]").TextContent, @"\s+", " "), StringComparison.Ordinal);
        cut.ClickAt("[data-testid=services-switch-dgx]");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=service-row-comfyui]")));
        Assert.True(cut.Find("[data-testid=node-option-dgx]").HasAttribute("checked"));
        Assert.Contains("node=dgx", new Uri(ctx.Services.GetRequiredService<NavigationManager>().Uri).Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Services_WithoutANodeInTheLink_OpensOnANodeThatManagesServices_NotTheBootstrapNodeThatCant()
    {
        var dgx = new ListManager("comfyui");
        var pc = new ListManager("ollama");
        var perNode = new FakeNodeHostServices { ["pc"] = pc };
        using var ctx = ServicesContext(dgx, perNode, Node("dgx", "DGX Spark", hostServices: false), Node("pc", "Office PC", hostServices: true));

        var cut = ctx.Render<Services>();

        Assert.True(cut.Find("[data-testid=node-option-pc]").HasAttribute("checked"));
        Assert.NotNull(cut.Find("[data-testid=service-row-ollama]"));
        Assert.Empty(cut.FindAll("[data-testid=services-unsupported]"));
        Assert.Equal(0, dgx.Lists);
        Assert.Equal("nodes/pc?tab=services", cut.Find("[data-testid=services-node-link]").GetAttribute("href"));
    }

    [Fact]
    public void ChooseNode_PrefersTheRequestedNode_ThenTheBootstrapNodeIfItManagesServices_ThenTheFirstThatDoes()
    {
        var rows = new List<Nodes.NodeRow>
        {
            Nodes.Merge(Node("a-mac", hostServices: false), null, []),
            Nodes.Merge(Node("b-pc", hostServices: true), null, []),
            Nodes.Merge(Node("dgx", hostServices: true), null, []),
            Nodes.Merge(Node("unknown"), null, []),
        };
        var options = NodeSelector.From(rows);

        Assert.Equal("a-mac", NodeServices.ChooseNode(rows, options, "a-mac", "dgx")); // asked for: even one that can't
        Assert.Equal("dgx", NodeServices.ChooseNode(rows, options, null, "dgx"));
        Assert.Equal("dgx", NodeServices.ChooseNode(rows, options, "gone", "dgx"));

        var noDgx = rows.Take(2).Append(Nodes.Merge(Node("dgx", hostServices: false), null, [])).ToList();
        Assert.Equal("b-pc", NodeServices.ChooseNode(noDgx, NodeSelector.From(noDgx), null, "dgx"));

        var none = new List<Nodes.NodeRow> { Nodes.Merge(Node("a-mac", hostServices: false), null, []), Nodes.Merge(Node("unknown"), null, []) };
        Assert.Equal("a-mac", NodeServices.ChooseNode(none, NodeSelector.From(none), null, "dgx")); // the selector's own fallback
    }

    [Fact]
    public void NodeDetail_ServicesTab_ManagesThatNodesServices_WithSegmentedTabsUnderTheirOwnQueryKey()
    {
        var dgx = new ListManager("comfyui");
        var pc = new ListManager("ollama");
        var perNode = new FakeNodeHostServices { ["pc"] = pc };
        using var ctx = ServicesContext(dgx, perNode, Node("dgx", "DGX Spark", hostServices: true), Node("pc", "Office PC", hostServices: true));
        // Node detail reads its node by id, which the list-only registry does not answer.
        var registry = new MirbadLLM.UnitTests.Multinode.FakeNodeRegistry();
        registry.Records["dgx"] = Node("dgx", "DGX Spark", hostServices: true);
        registry.Records["pc"] = Node("pc", "Office PC", hostServices: true);
        ctx.Services.AddSingleton<INodeRegistry>(registry);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/nodes/pc?tab=services");

        var cut = ctx.Render<NodeDetail>(p => p.Add(x => x.Id, "pc"));

        Assert.Equal("true", cut.Find("[data-testid=node-tabs-tab-services]").GetAttribute("aria-selected"));
        Assert.NotNull(cut.Find("[data-testid=service-row-ollama]"));
        Assert.Empty(cut.FindAll("[data-testid=service-row-comfyui]"));
        Assert.Empty(cut.FindAll("[data-testid=services-node-selector]")); // the node is the page's
        Assert.Empty(cut.FindAll("[data-testid=memory-bar]")); // headroom is on Overview
        Assert.Contains("tabs--segmented", cut.Find("[data-testid=services-tabs]").ClassName, StringComparison.Ordinal);

        cut.ClickAt("[data-testid=restart-ollama]");
        cut.ClickAt("[data-testid=service-dialog-confirm]");
        Assert.Equal(["ollama:Restart"], pc.Runs);
        Assert.Empty(dgx.Runs);

        cut.ClickAt("[data-testid=services-tabs-tab-discovered]");
        var query = new Uri(nav.Uri).Query;
        Assert.Contains("tab=services", query, StringComparison.Ordinal); // the node's tab stays
        Assert.Contains("services=discovered", query, StringComparison.Ordinal);
    }

    [Fact]
    public void Services_NodeWhoseManagerCannotBeBuilt_ShowsAnError_AndNeverTheDefaultNodesServices()
    {
        var dgx = new ListManager("comfyui");
        using var ctx = ServicesContext(dgx, new FakeNodeHostServices(), Node("dgx"), Node("pc", "Office PC"));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/services?node=pc");

        var cut = ctx.Render<Services>();

        Assert.Contains("Office PC", cut.Find("[data-testid=services-error]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=service-row-comfyui]"));
        Assert.Equal(0, dgx.Lists);
    }

    [Fact]
    public void Services_HeadroomFollowsTheSelectedNode()
    {
        var perNode = new FakeNodeHostServices { ["pc"] = new ListManager("ollama") };
        var pcLive = new NodeLiveStatus
        {
            NodeId = "pc",
            NodeUid = "pc-uid",
            State = NodeState.Online,
            Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 32, HostAvailableGiB = 20 },
        };
        using var ctx = ServicesContext(new ListManager("comfyui"), perNode, [pcLive], Node("dgx"), Node("pc", "Office PC"));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/services?node=pc");

        var cut = ctx.Render<Services>();

        Assert.Contains("32", cut.Find("[data-testid=memory-bar]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("121", cut.Find("[data-testid=memory-bar]").TextContent, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- default per-node managers (real code)

    [Fact]
    public async Task NodeHostServices_TheBootstrapNodeGetsTheRegisteredManager_OthersNeedAgentClients()
    {
        var registered = new ListManager("comfyui");
        var services = new ServiceCollection().AddSingleton<IHostServiceManager>(registered).BuildServiceProvider();

        using var managers = new NodeHostServices(services);

        Assert.Same(registered, await managers.ForNodeAsync("dgx"));
        Assert.Null(await managers.ForNodeAsync("pc"));
    }

    [Fact]
    public void NodeHostServices_StoreFileIsPerNode_AndRefusesAnythingButANodeId()
    {
        Assert.Equal("host-services.pc-office.json", NodeHostServices.StoreFileName("pc-office"));
        Assert.Throws<ArgumentException>(() => NodeHostServices.StoreFileName("../dgx"));
        Assert.Throws<ArgumentException>(() => NodeHostServices.StoreFileName("PC"));
    }

    [Fact]
    public async Task NodeHostServices_DrivesTheNodesAgent_WithItsOwnEmptyContainerWhitelist()
    {
        const string NodeId = "pc";
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions { NodeId = NodeId }, ct);
        string dataDir = Path.Combine(Path.GetTempPath(), "mn120-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        using var master = agent.Ca.IssueMasterClientCertificate();
        using var trust = TrustAnchorSet.Create([agent.Ca.Certificate]);
        var options = Options.Create(new MirbadLLMOptions { DataPath = dataDir });
        using var registry = new JsonNodeRegistry(options, TimeProvider.System);
        await using var factory = new AgentClientFactory(registry, new TestMasterIdentity(), master, trust, TimeProvider.System);
        using var references = new JsonAgentOperationReferenceStore(options, TimeProvider.System);
        try
        {
            await registry.UpsertAsync(new NodeRecord
            {
                Id = NodeId,
                Uid = agent.NodeUid,
                Address = agent.BaseAddress.AbsoluteUri,
                Approved = true,
                CertSpkiSha256 = agent.CurrentSpki,
            }, ct);
            await factory.RefreshAsync(ct);
            var services = new ServiceCollection()
                .AddSingleton<IHostServiceManager>(new ListManager("dgx-only"))
                .AddSingleton<MirbadLLM.Agent.Contracts.IAgentClientFactory>(factory)
                .AddSingleton(new AgentOperationRunner(factory, references, registry, TimeProvider.System))
                .AddSingleton<IOptions<MirbadLLMOptions>>(options)
                .BuildServiceProvider();

            using var managers = new NodeHostServices(services);
            var manager = await managers.ForNodeAsync(NodeId, ct);
            var snapshot = await manager!.ListAsync(ct);

            // The node's own units through its agent; no DGX default containers seeded into its whitelist.
            Assert.Contains(snapshot.Services, s => s.Name == AgentContractSeed.Service);
            Assert.DoesNotContain(snapshot.Services, s => s.Kind == HostServiceKind.Container);
            Assert.DoesNotContain(snapshot.Services, s => s.Name == "dgx-only");
            Assert.True(File.Exists(Path.Combine(dataDir, "host-services.pc.json")));
            Assert.False(File.Exists(Path.Combine(dataDir, "host-services.json")));
            // One manager per node for the singleton's life, shared by every page.
            Assert.Same(manager, await managers.ForNodeAsync(NodeId, ct));
        }
        finally
        {
            try
            {
                Directory.Delete(dataDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    // ================================================================ helpers

    private static DateTimeOffset At(int seconds) => T0.AddSeconds(seconds);

    private static HostSample Unified(int seconds, double total, double available) =>
        new(At(seconds), new MemorySnapshot(total, available), new GpuSnapshot(40, 100, 50, 2000));

    private static HostSample Discrete(int seconds, double total, double available, IReadOnlyList<HostGpuSample> gpus) =>
        new(At(seconds), new MemorySnapshot(total, available), new GpuSnapshot(gpus.Count > 0 ? gpus[0].UtilizationPercent : null, 120, 60, 2400)) { Gpus = gpus };

    private static HostGpuSample Gpu(int index, string uuid, double util, double used, double total, params (string Model, double GiB)[] models) =>
        new(index, uuid, util, used, total, models.ToDictionary(m => m.Model, m => m.GiB, StringComparer.Ordinal));

    private static ModelKpis Kpis(double generated) => new(
        new ModelMetrics(0, 0, 0, generated, 0), 0, null, null, null, null, null, null, 0, new Dictionary<string, double>(), 0);

    private static ModelDefinition Model(string id, string? node) =>
        new() { Id = id, ServedName = id.ToUpperInvariant(), ModelPath = "p", Placement = node is null ? null : new ModelPlacement(node) };

    private static NodeRecord Node(string id, string? name = null, bool? hostServices = null) => new()
    {
        Id = id,
        Uid = id + "-uid",
        DisplayName = name,
        Address = "https://127.0.0.1:9443",
        Approved = true,
        LastCapabilities = hostServices is null ? null : new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "1.0.0",
            Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
            Arch = "amd64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 32),
            HostServices = hostServices.Value ? new HostServicesCapability("systemd", true, "1.0", true) : null,
        },
    };

    private static NodeHistory TwoNodeHistory()
    {
        var history = new NodeHistory();
        history.Hosts["dgx"] = [Unified(0, 128, 90)];
        history.Hosts["pc"] = [Unified(0, 32, 20)];
        history.Kpis["on-dgx"] = Kpis(10);
        history.Kpis["legacy"] = Kpis(11);
        history.Kpis["on-pc"] = Kpis(20);
        return history;
    }

    /// <summary>Chart and selector ids come from static counters; LiveStamp renders the snapshot time; element references are per render.</summary>
    private static string Normalize(string markup) =>
        Regex.Replace(Regex.Replace(markup, @"(chart-help-|node-selector-)\d+", "$1N"), @"(datetime|data-live-at|blazor:elementReference)=""[^""]*""", "");

    private static BunitContext MonitoringContext(NodeHistory history, IReadOnlyList<ModelDefinition> models, params NodeRecord[] nodes)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(models));
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        ctx.Services.AddSingleton<IRouterStats>(new FakeRouterStats([]));
        ctx.Services.AddSingleton<IMetricsHistory>(history);
        if (nodes.Length > 0)
        {
            ctx.Services.AddSingleton<INodeRegistry>(new ListOnlyNodeRegistry(nodes));
        }

        return ctx;
    }

    private static BunitContext ServicesContext(IHostServiceManager defaultManager, INodeHostServices perNode, params NodeRecord[] nodes) =>
        ServicesContext(defaultManager, perNode, [], nodes);

    private static BunitContext ServicesContext(
        IHostServiceManager defaultManager, INodeHostServices perNode, IReadOnlyList<NodeLiveStatus> live, params NodeRecord[] nodes)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton(defaultManager);
        ctx.Services.AddSingleton(perNode);
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(121, 50)));
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry());
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        ctx.Services.AddSingleton<INodeRegistry>(new ListOnlyNodeRegistry(nodes));
        if (live.Count > 0)
        {
            ctx.Services.AddSingleton<INodeStatusCache>(new LiveOnlyCache(live));
        }

        return ctx;
    }

    /// <summary>Host series per node; records which API the page read.</summary>
    private sealed class NodeHistory : IMetricsHistory
    {
        public Dictionary<string, IReadOnlyList<HostSample>> Hosts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ModelKpis> Kpis { get; } = new(StringComparer.Ordinal);

        public List<string> NodeReads { get; } = [];

        public int DefaultReads { get; private set; }

        public IReadOnlyList<HostSample> GetHost(TimeSpan window)
        {
            DefaultReads++;
            return Hosts.GetValueOrDefault("dgx") ?? [];
        }

        public IReadOnlyList<HostSample> GetHost(string nodeId, TimeSpan window)
        {
            NodeReads.Add(nodeId);
            return Hosts.GetValueOrDefault(nodeId) ?? [];
        }

        public IReadOnlyList<ModelSample> GetModel(string modelId, TimeSpan window) =>
            Kpis.TryGetValue(modelId, out var kpis) ? [new ModelSample(T0, kpis)] : [];

        public IReadOnlyDictionary<string, ModelKpis> Latest() => new Dictionary<string, ModelKpis>(Kpis, StringComparer.Ordinal);
    }

    /// <summary>A manager that lists fixed running services and records actions.</summary>
    private sealed class ListManager(params string[] names) : IHostServiceManager
    {
        public List<string> Runs { get; } = [];

        public int Lists { get; private set; }

        public Task<HostServicesSnapshot> ListAsync(CancellationToken ct = default)
        {
            Lists++;
            return Task.FromResult(new HostServicesSnapshot([.. names.Select(Status)], true, null));
        }

        public Task<HostServicesDiscovery> DiscoverAsync(CancellationToken ct = default) =>
            Task.FromResult(new HostServicesDiscovery([], true, null, []));

        public Task<HostServiceStatus> AddToWhitelistAsync(HostServiceWhitelistAdd request, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostServiceStatus> UpdateWhitelistAsync(string name, HostServiceWhitelistUpdate request, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<HostServiceStatus> RunAsync(string name, HostServiceAction action, string? confirm = null, CancellationToken ct = default)
        {
            Runs.Add($"{name}:{action}");
            return Task.FromResult(Status(name));
        }

        public Task<HostServiceStatus> SetAutostartAsync(string name, bool enabled, string? confirm = null, CancellationToken ct = default) => throw new NotSupportedException();

        private static HostServiceStatus Status(string name) =>
            new(name, name, "", HostServiceKind.System, name + ".service", false, null, HostServiceState.Running, "running", true, "enabled", T0, GiB, null);
    }

    /// <summary>Per-node managers by id; records which nodes were asked for.</summary>
    private sealed class FakeNodeHostServices : INodeHostServices
    {
        private readonly Dictionary<string, IHostServiceManager> _managers = new(StringComparer.Ordinal);

        public List<string> Requests { get; } = [];

        public IHostServiceManager this[string nodeId]
        {
            set => _managers[nodeId] = value;
        }

        public Task<IHostServiceManager?> ForNodeAsync(string nodeId, CancellationToken ct = default)
        {
            Requests.Add(nodeId);
            return Task.FromResult(_managers.GetValueOrDefault(nodeId));
        }
    }

    private sealed class TestMasterIdentity : IMasterIdentity
    {
        public string InstanceId => "mn120-master";

        public long Epoch => 1;

        public string BootId => "mn120-boot";

        public MasterTriple Current => new(InstanceId, Epoch, BootId);

        public bool TakeoverRequested => false;

        public IReadOnlyList<TakeoverRecord> Takeovers => [];

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>A node status cache that only lists nodes.</summary>
    private sealed class LiveOnlyCache(IReadOnlyList<NodeLiveStatus> nodes) : INodeStatusCache
    {
        public long Version => 1;

        public IReadOnlyList<NodeLiveStatus> ListNodes() => nodes;

        public NodeLiveStatus? GetNode(string nodeId) => nodes.FirstOrDefault(n => n.NodeId == nodeId);

        public NodeModelRuntime? GetRuntime(string modelId) => null;

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) => [];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
