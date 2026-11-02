using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Ui;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA2000 // the context owns what it renders

/// <summary>The Releases page and a node's agent-update panel (docs/multinode/agent-updates.md §3).</summary>
public sealed class ReleaseNotesUiTests : IAsyncDisposable
{
    private readonly BunitContext _ctx = new();

    public ValueTask DisposeAsync() => _ctx.DisposeAsync();

    private static ReleaseNotes Notes(string currentCommit) => new(
    [
        new ReleaseEntry { Number = 150, Title = "Old", Commit = "2d71c7d97d84ffff", MergedAt = new(2026, 10, 14, 0, 0, 0, TimeSpan.Zero), Changed = ["older"] },
        new ReleaseEntry { Number = 153, Title = "macOS Phase 3", Commit = "8bb626717368aaaa", MergedAt = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), Added = ["MLX runtime"], Url = "https://github.com/x/y/pull/153" },
        new ReleaseEntry { Number = 156, Title = "install-hostd fix", Commit = currentCommit, MergedAt = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero), Fixed = ["install-hostd reads keys back"] },
    ]);

    [Fact]
    public void TheReleasesPageListsEveryPrNewestFirstWithItsGroups()
    {
        _ctx.Services.AddSingleton(Notes("f7dce9640000dddd"));

        var cut = _ctx.Render<Releases>();

        var cards = cut.FindAll("[data-testid^=release-][data-testid$=-notes]");
        Assert.Equal(3, cards.Count);
        Assert.Equal("release-156-notes", cards[0].GetAttribute("data-testid"));
        Assert.Contains("MLX runtime", cut.Find("[data-testid=release-153-notes-added]").TextContent, StringComparison.Ordinal);
        Assert.Contains("install-hostd reads keys back", cut.Find("[data-testid=release-156-notes-fixed]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void KindFilterCountsMatchingPrsAndFiltersTheirGroupsWhileAllRestoresTheTimeline()
    {
        _ctx.Services.AddSingleton(Notes("f7dce9640000dddd"));
        var cut = _ctx.Render<Releases>();
        Assert.Contains("All 3", cut.Find("[data-testid=releases-kind-all]").ParentElement!.TextContent, StringComparison.Ordinal);
        Assert.Equal(3, cut.FindAll(".release-day").Count);
        cut.Find("[data-testid=releases-kind-added]").Change(true);
        Assert.Single(cut.FindAll(".release-card"));
        Assert.NotNull(cut.Find("[data-testid=release-153-notes-added]"));
        Assert.Empty(cut.FindAll(".release-group--fixed"));
        cut.Find("[data-testid=releases-kind-all]").Change(true);
        Assert.Equal(3, cut.FindAll(".release-card").Count);
    }

    [Fact]
    public void AnEmptyKindExplainsWhyNoCardsAreShown()
    {
        _ctx.Services.AddSingleton(new ReleaseNotes([Notes("f7dce9640000dddd").Entries[0]]));
        var cut = _ctx.Render<Releases>();
        cut.Find("[data-testid=releases-kind-added]").Change(true);
        Assert.Empty(cut.FindAll(".release-card"));
        Assert.NotNull(cut.Find("[data-testid=releases-filter-empty]"));
    }

    [Fact]
    public void TheReleasesPageSaysWhenTheBuildHasNoNotes()
    {
        _ctx.Services.AddSingleton(new ReleaseNotes([], available: false));

        var cut = _ctx.Render<Releases>();

        Assert.NotNull(cut.Find("[data-testid=releases-unavailable]"));
    }

    [Fact]
    public void AnAgentFromAnotherBuildShowsWhatTheUpdateBringsAndNoPanelForTheSameBuild()
    {
        // The controller of the test host is this test assembly's build; give the notes its commit as the newest entry.
        string? controller = ProductVersion.RevisionOf(ProductVersion.Controller);
        if (controller is null)
        {
            return; // a build without a commit claims nothing (ProductVersion.SameBuild)
        }

        _ctx.Services.AddSingleton(Notes(controller + "0000"));
        var row = new Nodes.NodeRow
        {
            Id = "devpc",
            Name = "devpc",
            State = NodeState.Online,
            AgentVersion = "1.0.0+2d71c7d97d84",
            AgentProtocol = MirbadLLM.Agent.Contracts.Protocol.Range,
            Capabilities = new NodeCapabilities
            {
                Protocol = MirbadLLM.Agent.Contracts.Protocol.Range,
                AgentVersion = "1.0.0+2d71c7d97d84",
                Os = new NodeOsInfo("windows", "Windows 11 Pro"),
                Arch = "amd64",
                Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
            },
        };

        var cut = _ctx.Render<AgentUpdatePanel>(p => p.Add(x => x.Row, row));

        Assert.Contains("1.0.0 · 2d71c7d", cut.Find("[data-testid=agent-update-versions]").TextContent, StringComparison.Ordinal);
        Assert.Contains("MLX runtime", cut.Find("[data-testid=agent-update-notes-added]").TextContent, StringComparison.Ordinal);
        Assert.Contains("install-hostd reads keys back", cut.Find("[data-testid=agent-update-notes-fixed]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=agent-update-notes-changed]")); // #150 is the agent's own build: not new to it
        Assert.NotNull(cut.Find("[data-testid=agent-update-no-bundle]")); // no bundle catalog in this host

        var same = _ctx.Render<AgentUpdatePanel>(p => p.Add(x => x.Row, row with { AgentVersion = "1.0.0+" + controller }));
        Assert.Empty(same.FindAll("[data-testid=agent-update]"));
    }
}
