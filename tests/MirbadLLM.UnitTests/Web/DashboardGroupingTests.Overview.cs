using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// UI redesign (2026-10-15): the Overview shows one headroom panel per node and no model
/// cards; the Models list carries each model's node (a link), GPUs and runtime, with the same node scope control the
/// Dashboard had (<c>?node=</c>).
/// </summary>
public sealed partial class DashboardGroupingTests
{
    // ---------- Overview ----------

    [Fact]
    public void Overview_VitalsComeFirst_AndNodeCardsKeepReadyModelLinksAndUnknownRates()
    {
        var (ctx, _, _) = TwoNodes();
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.True(cut.Markup.IndexOf("data-testid=\"kpi-strip\"", StringComparison.Ordinal)
                < cut.Markup.IndexOf("data-testid=\"node-groups\"", StringComparison.Ordinal));
            var card = cut.Find("[data-testid=node-group-dgx]");
            Assert.Equal("--ring-size: 56px", card.QuerySelector(".ring")!.GetAttribute("style"));
            Assert.Equal("models/qwen/overview", card.QuerySelector(".overview-run")!.GetAttribute("href"));
            Assert.Equal("—", card.QuerySelector(".overview-run .num")!.TextContent);
            Assert.NotNull(card.QuerySelector("details.node-group-breakdown"));
            Assert.Equal(["chat", "access", "models"], cut.FindAll(".overview-next a").Select(a => a.GetAttribute("href")));
        }
    }

    [Fact]
    public void Overview_OneHeadroomPanelPerNode_InNameOrder_WithoutModelCards()
    {
        var (ctx, _, _) = TwoNodes();
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            // "DGX Spark" < "Office PC"; revoked, pending and "not placed" are no panels.
            Assert.Equal(["node-group-dgx", "node-group-pc"], GroupIds(cut));
            Assert.Equal("overview-headroom", cut.Find("[data-testid=node-group-dgx]").GetAttribute("data-tour"));
            Assert.Null(cut.Find("[data-testid=node-group-pc]").GetAttribute("data-tour"));
            Assert.Equal("nodes/dgx", cut.Find("[data-testid=node-group-link-dgx]").GetAttribute("href"));
            Assert.Equal("online", cut.Find("[data-testid=node-group-state-pc]").GetAttribute("data-state"));
            Assert.NotNull(cut.Find("[data-testid=node-group-state-pc] svg")); // colour + icon + text
            Assert.Contains("1 Ready of 2", cut.Find("[data-testid=node-group-summary-dgx]").TextContent, StringComparison.Ordinal);
            Assert.Contains("3 Ready of 5", cut.Find("[data-testid=models-summary]").TextContent, StringComparison.Ordinal);

            // Headroom only: no model cards, no page-level bar, a link to the full list instead.
            Assert.Empty(cut.FindAll("article.model-card"));
            Assert.Empty(cut.FindAll("[data-testid=memory-bar]"));
            Assert.Equal("models", cut.Find("[data-testid=all-models]").GetAttribute("href"));
            Assert.Equal("Overview", cut.Find("[data-testid=page-title]").TextContent);
            Assert.Contains("2 nodes", cut.Find("[data-testid=page-title]").ParentElement!.TextContent, StringComparison.Ordinal);

            // Inside a node panel the bar has no frame of its own (no nested panels).
            Assert.DoesNotContain("panel", cut.Find("[data-testid=node-dgx-memory-bar]").ClassList);
        }
    }

    [Fact]
    public void Overview_OfflineNode_PanelSaysLastSeen_WithoutABar()
    {
        var (ctx, _, _) = TwoNodes(Pc(NodeState.Offline, ["No contact for 15 s (3 missed heartbeats)."], Now.AddSeconds(-75)) with { Memory = null });
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            var pc = cut.Find("[data-testid=node-group-pc]");
            Assert.Contains("node-group--stale", pc.ClassList);
            Assert.Contains("Last seen 1m ", cut.Find("[data-testid=node-group-seen-pc]").TextContent, StringComparison.Ordinal);
            // No reading: an honest dash, not a bar of zeros.
            Assert.Contains("—", cut.Find("[data-testid=node-group-no-memory-pc]").TextContent, StringComparison.Ordinal);
            Assert.Empty(cut.FindAll("[data-testid=node-pc-memory-bar]"));
            Assert.DoesNotContain("node-group--stale", cut.Find("[data-testid=node-group-dgx]").ClassList);
        }
    }

    [Fact]
    public void Overview_DegradedAndDrainingNodes_KeepTheirPanelsLive()
    {
        var (ctx, cache, _) = TwoNodes();
        using (ctx)
        {
            cache.Set(Dgx(draining: true), Pc(NodeState.Degraded, ["Docker is unreachable."]));
            var cut = ctx.Render<Dashboard>();

            Assert.NotNull(cut.Find("[data-testid=node-pc-memory-bar]"));
            Assert.DoesNotContain("node-group--stale", cut.Find("[data-testid=node-group-pc]").ClassList);
        }
    }

    [Fact]
    public void Overview_SingleNode_HasOneHeadroomPanel_AndNoNodeFilter()
    {
        using var ctx = Context([Model("qwen", "dgx", budget: 36), Model("gemma", "dgx", budget: 20)], cache: new FakeNodeStatusCache(Dgx()),
            registry: new ListOnlyNodeRegistry(Record("dgx", "DGX Spark"), Record("gone") with { Revoked = true }));

        var cut = ctx.Render<Dashboard>();

        Assert.Equal(["node-group-dgx"], GroupIds(cut));
        Assert.Empty(cut.FindAll("[data-testid=dashboard-node-filter]"));
        Assert.DoesNotContain("DGX", cut.Find("[data-testid=page-title]").ParentElement!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_NodeWithoutModels_KeepsItsPanel_WithHeadroom()
    {
        var (ctx, _, _) = TwoNodes(models: [Model("qwen", "dgx")]);
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();

            Assert.Equal(["node-group-dgx", "node-group-pc"], GroupIds(cut));
            Assert.NotNull(cut.Find("[data-testid=node-pc-memory-bar]"));
            Assert.Empty(cut.FindAll("[data-testid=node-group-summary-pc]"));
        }
    }

    [Fact]
    public void Overview_NodeStates_FollowTheCache_OnTheNextRefresh()
    {
        var (ctx, cache, _) = TwoNodes();
        using (ctx)
        {
            var cut = ctx.Render<Dashboard>();
            Assert.DoesNotContain("node-group--stale", cut.Find("[data-testid=node-group-pc]").ClassList);

            cache.Set(Dgx(), Pc(NodeState.Offline));
            cut.WaitForAssertion(
                () => Assert.Contains("node-group--stale", cut.Find("[data-testid=node-group-pc]").ClassList),
                TimeSpan.FromSeconds(15));
        }
    }

    // ---------- Models: node, GPUs, runtime and the node scope ----------

    [Fact]
    public void Models_ShowEachModelsNode_GpusAndRuntime()
    {
        var models = new[]
        {
            Model("qwen", "dgx", budget: 36),
            Model("coder", "pc", budget: 12, gpus: ["GPU-a", "GPU-b"]) with { Runtime = RuntimeKind.LlamaCpp },
            Model("small", "pc", budget: 8, gpus: ["GPU-b"]),
            Model("orphan", null, budget: 4),
            Model("lost", "ghost"),
        };
        var (ctx, _, _) = TwoNodes(models: models);
        using (ctx)
        {
            var cut = ctx.Render<Models>();

            var qwen = cut.Find("[data-testid=models-node-qwen]");
            Assert.Equal("nodes/dgx", qwen.QuerySelector("a")!.GetAttribute("href"));
            Assert.Equal("DGX Spark", qwen.QuerySelector("a")!.TextContent);
            Assert.Contains("GPUs 0, 1", cut.Find("[data-testid=models-node-coder]").TextContent, StringComparison.Ordinal);
            Assert.Contains("GPU 1", cut.Find("[data-testid=models-node-small]").TextContent, StringComparison.Ordinal);
            Assert.Contains("llama.cpp", cut.Find("[data-testid=models-row-coder]").TextContent, StringComparison.Ordinal);
            Assert.Contains("vLLM", cut.Find("[data-testid=models-row-qwen]").TextContent, StringComparison.Ordinal);
            Assert.Contains("Not placed", cut.Find("[data-testid=models-node-orphan]").TextContent, StringComparison.Ordinal);
            // A placement on a node the master does not know: its id, no link.
            Assert.Null(cut.Find("[data-testid=models-node-lost]").QuerySelector("a"));
            Assert.Contains("ghost", cut.Find("[data-testid=models-node-lost]").TextContent, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Models_NodeScope_FromTheUrl_ShowsOnlyThatNode_AndAllNodesClearsIt()
    {
        var (ctx, _, _) = TwoNodes();
        using (ctx)
        {
            var nav = ctx.Services.GetRequiredService<NavigationManager>();
            nav.NavigateTo("?node=pc");

            var cut = ctx.Render<Models>();

            Assert.Equal(["coder", "small"], RowIds(cut));
            Assert.Equal("radiogroup", cut.Find("[data-testid=models-node-filter] [role=radiogroup]").GetAttribute("role"));
            Assert.True(cut.Find("[data-testid=models-node-filter-pc]").HasAttribute("checked"));

            cut.ChangeAt("[data-testid=models-node-filter-all]", true);

            Assert.Equal(5, RowIds(cut).Length);
            Assert.DoesNotContain("node=", nav.Uri, StringComparison.Ordinal);

            cut.ChangeAt("[data-testid=models-node-filter-_unplaced]", true);

            Assert.Equal(["orphan"], RowIds(cut));
            Assert.EndsWith("?node=_unplaced", nav.Uri, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Models_NodeScope_UnknownNodeInTheUrl_IsIgnored()
    {
        var (ctx, _, _) = TwoNodes();
        using (ctx)
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("?node=nope");

            var cut = ctx.Render<Models>();

            Assert.Equal(5, RowIds(cut).Length);
            Assert.True(cut.Find("[data-testid=models-node-filter-all]").HasAttribute("checked"));
        }
    }

    [Fact]
    public void Models_NodeScope_IsASelect_WithMoreThanFourChoices()
    {
        var nodes = new[] { Simple("a", "Alpha"), Simple("b", "Bravo"), Simple("c", "Charlie"), Simple("d", "Delta"), Simple("e", "Echo") };
        using var ctx = Context([Model("m1", "a"), Model("m2", "e")], cache: new FakeNodeStatusCache(nodes));

        var cut = ctx.Render<Models>();

        Assert.Empty(cut.FindAll("[data-testid=models-node-filter] [role=radiogroup]"));
        Assert.Equal(6, cut.FindAll("[data-testid=models-node-filter-select] option").Count); // All nodes + five
        cut.ChangeAt("[data-testid=models-node-filter-select]", "e");

        Assert.Equal(["m2"], RowIds(cut));
    }

    [Fact]
    public void Models_NodeScope_IsHidden_WithOneNode()
    {
        using var ctx = Context([Model("qwen", "dgx")], cache: new FakeNodeStatusCache(Dgx()));

        var cut = ctx.Render<Models>();

        Assert.Empty(cut.FindAll("[data-testid=models-node-filter]"));
    }

    [Fact]
    public void Models_FindBox_MatchesServedNameIdOrNodeName_AndIsTheSlashShortcutTarget()
    {
        var (ctx, _, _) = TwoNodes();
        using (ctx)
        {
            var cut = ctx.Render<Models>();
            var find = cut.Find("[data-testid=models-filter]");
            Assert.True(find.HasAttribute("data-shortcut-filter"));

            find.Input("office");
            Assert.Equal(["coder", "small"], RowIds(cut));

            cut.Find("[data-testid=models-filter]").Input("GEM");
            Assert.Equal(["gemma"], RowIds(cut));

            cut.Find("[data-testid=models-filter]").Input("zzz");
            Assert.Contains("matches “zzz”", cut.Find("[data-testid=models-filter-empty]").TextContent, StringComparison.Ordinal);
        }
    }
}
