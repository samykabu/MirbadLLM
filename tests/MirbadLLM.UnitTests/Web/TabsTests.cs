using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Web.Components.Ui;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>bUnit tests for the shared <see cref="Tabs"/> component: ARIA roles/attributes, the roving-tabindex
/// markup that js/shell.js drives in the browser, label + count accessible names, the Line/Segmented variants,
/// and selection persisted to/from the URL query string.</summary>
public sealed class TabsTests
{
    private static readonly IReadOnlyList<TabItem> ThreeTabs =
    [
        new("a", "Alpha"),
        new("b", "Bravo", Count: 3),
        new("c", "Charlie"),
    ];

    private static Bunit.BunitContext MakeContext(string? uri = null)
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        if (uri is not null)
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        }

        return ctx;
    }

    private static Bunit.IRenderedComponent<Tabs> Render(Bunit.BunitContext ctx, IReadOnlyList<TabItem> items, string selectedId,
        Action<string>? onSelected = null, string? queryKey = null) =>
        ctx.Render<Tabs>(p => p
            .Add(x => x.Items, items)
            .Add(x => x.SelectedId, selectedId)
            .Add(x => x.AriaLabel, "Test tabs")
            .Add(x => x.TestId, "t")
            .Add(x => x.QueryKey, queryKey)
            .Add(x => x.SelectedIdChanged, onSelected is null ? default : EventCallback.Factory.Create<string>(new object(), onSelected))
            .Add(x => x.ChildContent, (string id) => (builder) => builder.AddContent(0, $"panel:{id}")));

    [Fact]
    public void RendersTablistWithAriaSelectedAndRovingTabindex()
    {
        using var ctx = MakeContext();
        var cut = Render(ctx, ThreeTabs, "b");

        Assert.Equal("tablist", cut.Find("[data-testid=t-list]").GetAttribute("role"));

        var a = cut.Find("[data-testid=t-tab-a]");
        var b = cut.Find("[data-testid=t-tab-b]");
        Assert.Equal("tab", a.GetAttribute("role"));
        Assert.Equal("false", a.GetAttribute("aria-selected"));
        Assert.Equal("-1", a.GetAttribute("tabindex"));
        Assert.Equal("true", b.GetAttribute("aria-selected"));
        Assert.Equal("0", b.GetAttribute("tabindex"));

        var panel = cut.Find("[data-testid=t-panel]");
        Assert.Equal("tabpanel", panel.GetAttribute("role"));
        Assert.Equal(b.GetAttribute("id"), panel.GetAttribute("aria-labelledby"));
        Assert.Contains("panel:b", panel.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Click_SelectsTheTab_AndRaisesSelectedIdChanged()
    {
        using var ctx = MakeContext();
        string? selected = null;
        var cut = Render(ctx, ThreeTabs, "a", id => selected = id);

        cut.Find("[data-testid=t-tab-c]").Click();

        Assert.Equal("c", selected);
        Assert.Equal("true", cut.Find("[data-testid=t-tab-c]").GetAttribute("aria-selected"));
        Assert.Equal("false", cut.Find("[data-testid=t-tab-a]").GetAttribute("aria-selected"));
    }

    [Fact]
    public void ArrowKeys_AreHandledInTheBrowser_NotByAServerRoundTrip()
    {
        using var ctx = MakeContext();
        var cut = Render(ctx, ThreeTabs, "a");

        // js/shell.js owns arrow/Home/End on any [data-roving-tabs] list: it moves focus at once and clicks
        // the new tab, so the server only ever sees the click. No keydown handler is wired server-side.
        Assert.True(cut.Find("[data-testid=t-list]").HasAttribute("data-roving-tabs"));
        Assert.Throws<MissingEventHandlerException>(() =>
            cut.Find("[data-testid=t-tab-a]").KeyDown(new KeyboardEventArgs { Key = "ArrowRight" }));
    }

    [Fact]
    public void DisabledTabs_AreDisabledButtons_SoTheBrowserSkipsThem()
    {
        using var ctx = MakeContext();
        IReadOnlyList<TabItem> items = [new("a", "Alpha"), new("b", "Bravo", Disabled: true), new("c", "Charlie")];
        var cut = Render(ctx, items, "a");

        Assert.True(cut.Find("[data-testid=t-tab-b]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=t-tab-c]").HasAttribute("disabled"));
    }

    // Whitespace between inline spans collapses in the accessible name, as it does here.
    private static string Normalize(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    [Fact]
    public void LabelAndCount_ReadAsOnePhrase_WithAHiddenSeparatorAndNoun()
    {
        using var ctx = MakeContext();
        IReadOnlyList<TabItem> items = [new("m", "Managed", Count: 11, CountNoun: "services"), new("d", "Discovered", Count: 4)];
        var cut = Render(ctx, items, "m");

        // Visible: "Managed" and a pill "11"; accessible name (text content): "Managed, 11 services".
        Assert.Equal("Managed, 11 services", Normalize(cut.Find("[data-testid=t-tab-m]").TextContent));
        Assert.Equal("Discovered, 4", Normalize(cut.Find("[data-testid=t-tab-d]").TextContent));
        Assert.Equal("11", cut.Find("[data-testid=t-tab-m-count]").TextContent);
        Assert.Contains("visually-hidden", cut.Find("[data-testid=t-tab-m] .visually-hidden").ClassName, StringComparison.Ordinal);
        Assert.Equal("Managed", cut.Find("[data-testid=t-tab-m] .tabs-tab-label").GetAttribute("data-text"));
    }

    [Fact]
    public void Variant_LineByDefault_SegmentedForASecondLevel()
    {
        using var ctx = MakeContext();
        var line = Render(ctx, ThreeTabs, "a");
        Assert.Contains("tabs--line", line.Find("[data-testid=t]").ClassName, StringComparison.Ordinal);

        var segmented = ctx.Render<Tabs>(p => p
            .Add(x => x.Items, ThreeTabs)
            .Add(x => x.SelectedId, "a")
            .Add(x => x.AriaLabel, "Kind")
            .Add(x => x.TestId, "s")
            .Add(x => x.Variant, TabsVariant.Segmented));
        Assert.Contains("tabs--segmented", segmented.Find("[data-testid=s]").ClassName, StringComparison.Ordinal);
        Assert.Equal("tablist", segmented.Find("[data-testid=s-list]").GetAttribute("role"));
    }

    [Fact]
    public void CountAndStatusBadge_Render_WhenSet()
    {
        using var ctx = MakeContext();
        IReadOnlyList<TabItem> items =
        [
            new("a", "Alpha", BadgeText: "Busy", BadgeIcon: "gauge", BadgeClass: "busy"),
            new("b", "Bravo", Count: 3),
        ];
        var cut = Render(ctx, items, "a");

        Assert.Contains("3", cut.Find("[data-testid=t-tab-b]").TextContent, StringComparison.Ordinal);
        var badge = cut.Find("[data-testid=t-tab-a-badge]");
        Assert.Contains("Busy", badge.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void InitialSelection_IsReadFromTheQueryString_WhenPresentAndValid()
    {
        using var ctx = MakeContext("http://localhost/monitoring?model=c");
        string? selected = null;

        var cut = Render(ctx, ThreeTabs, "a", id => selected = id, queryKey: "model");

        Assert.Equal("c", selected);
        Assert.Equal("true", cut.Find("[data-testid=t-tab-c]").GetAttribute("aria-selected"));
    }

    [Fact]
    public void InitialSelection_IgnoresAQueryValueThatIsNotAKnownTab()
    {
        using var ctx = MakeContext("http://localhost/monitoring?model=nope");

        var cut = Render(ctx, ThreeTabs, "a", queryKey: "model");

        Assert.Equal("true", cut.Find("[data-testid=t-tab-a]").GetAttribute("aria-selected"));
    }

    [Fact]
    public void SelectingATab_UpdatesTheQueryString_WithoutLosingOtherParameters()
    {
        using var ctx = MakeContext("http://localhost/monitoring?keep=1");
        var cut = Render(ctx, ThreeTabs, "a", queryKey: "model");

        cut.Find("[data-testid=t-tab-b]").Click();

        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        Assert.Contains("model=b", nav.Uri, StringComparison.Ordinal);
        Assert.Contains("keep=1", nav.Uri, StringComparison.Ordinal);
    }
}
