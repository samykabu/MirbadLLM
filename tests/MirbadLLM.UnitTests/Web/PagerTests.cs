using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;
using MirbadLLM.Web.Components.Ui;
using FakeApiKeyStore = MirbadLLM.UnitTests.Router.Fakes.FakeApiKeyStore;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Web;

/// <summary>The shared Pager (page math, URL persistence, reset on filter change, a11y), its wiring on the Models page,
/// the Dashboard and the API-key list, and the Models table's row-actions menu.</summary>
public sealed class PagerTests
{
    // ---- Page math ----

    [Theory]
    [InlineData(0, 25, 1)]
    [InlineData(25, 25, 1)]
    [InlineData(26, 25, 2)]
    [InlineData(137, 25, 6)]
    [InlineData(137, 0, 1)] // All
    public void PageCount(int total, int size, int expected) => Assert.Equal(expected, Pager.PageCountFor(total, size));

    [Theory]
    [InlineData(137, 1, 25, 1, 25)]
    [InlineData(137, 6, 25, 126, 137)]
    [InlineData(137, 99, 25, 126, 137)] // past the end clamps to the last page
    [InlineData(137, 0, 25, 1, 25)]     // before the start clamps to the first
    [InlineData(137, 3, 0, 1, 137)]     // All
    [InlineData(0, 1, 25, 0, 0)]
    public void Range_IsOneBasedAndClamped(int total, int page, int size, int from, int to) =>
        Assert.Equal((from, to), Pager.Range(total, page, size));

    [Fact]
    public void Slice_TakesThePage_ClampsIt_AndAllReturnsEverything()
    {
        var items = Enumerable.Range(1, 60).ToList();

        Assert.Equal(Enumerable.Range(26, 25), Pager.Slice(items, 2, null));
        Assert.Equal(Enumerable.Range(51, 10), Pager.Slice(items, 9, null));
        Assert.Equal(Enumerable.Range(11, 10), Pager.Slice(items, 2, 10));
        Assert.Equal(items, Pager.Slice(items, 4, 0));
        Assert.Equal(Enumerable.Range(13, 12), Pager.Slice(items, 2, null, defaultSize: 12));
    }

    [Theory]
    [InlineData(1, 1, "1")]
    [InlineData(1, 5, "1 2 3 4 5")]
    [InlineData(4, 7, "1 2 3 4 5 6 7")]
    [InlineData(1, 10, "1 2 … 10")]
    [InlineData(5, 10, "1 … 4 5 6 … 10")]
    [InlineData(3, 10, "1 2 3 4 … 10")]
    [InlineData(10, 10, "1 … 9 10")]
    public void Window_KeepsFirstLastAndNeighbours_WithGaps(int current, int count, string expected) =>
        Assert.Equal(expected, string.Join(' ', Pager.Window(current, count).Select(n => n?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "…")));

    // ---- The rendered component ----

    private static BunitContext PagerContext(string? url = null)
    {
        var ctx = new BunitContext();
        if (url is not null)
        {
            ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        }

        return ctx;
    }

    [Fact]
    public void Hidden_WhenEverythingFitsOnOnePage()
    {
        using var ctx = PagerContext();

        var cut = ctx.Render<Pager>(p => p.Add(x => x.Total, 25));

        Assert.Empty(cut.FindAll("nav"));
        cut.Render(p => p.Add(x => x.Total, 0));
        Assert.Empty(cut.FindAll("nav"));
    }

    [Fact]
    public void Renders_AnAccessiblePaginationLandmark()
    {
        using var ctx = PagerContext();

        var cut = ctx.Render<Pager>(p => p.Add(x => x.Total, 137).Add(x => x.Noun, "models"));

        var nav = cut.Find("nav[data-testid=pager]");
        Assert.Equal("Pagination", nav.GetAttribute("aria-label"));
        Assert.Equal("1–25 of 137 models", cut.Find("[data-testid=pager-range]").TextContent);
        Assert.Equal("polite", cut.Find("[data-testid=pager-range]").GetAttribute("aria-live"));
        Assert.Equal("page", cut.Find("[data-testid=pager-page-1]").GetAttribute("aria-current"));
        Assert.Null(cut.Find("[data-testid=pager-page-2]").GetAttribute("aria-current"));
        Assert.Equal("Page 2", cut.Find("[data-testid=pager-page-2]").GetAttribute("aria-label"));
        Assert.True(cut.Find("[data-testid=pager-first]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid=pager-prev]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=pager-next]").HasAttribute("disabled"));
        Assert.Equal("Next page", cut.Find("[data-testid=pager-next]").GetAttribute("aria-label"));
        Assert.Equal("Last page", cut.Find("[data-testid=pager-last]").GetAttribute("aria-label"));
        Assert.All(cut.FindAll("nav button"), b => Assert.Equal("button", b.GetAttribute("type")));

        // The size select is labelled and offers 10 / 25 / 50 / All with 25 chosen.
        var select = cut.Find("[data-testid=pager-size]");
        Assert.Equal("Rows per page", cut.Find($"label[for={select.Id}]").TextContent);
        Assert.Equal(["10", "25", "50", "All"], select.QuerySelectorAll("option").Select(o => o.TextContent));
        Assert.True(select.QuerySelector("option[value='25']")!.HasAttribute("selected"));
    }

    [Fact]
    public void Navigating_WritesPageAndSizeToTheUrl_AndChangingSizeGoesBackToPageOne()
    {
        using var ctx = PagerContext("tables?tab=x");
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        int page = 1;
        int? size = null;
        var cut = ctx.Render<Pager>(p => p.Add(x => x.Total, 137)
            .Add(x => x.PageChanged, v => page = v).Add(x => x.SizeChanged, v => size = v));

        cut.Find("[data-testid=pager-page-2]").Click();
        Assert.Equal(2, page);
        Assert.EndsWith("tables?tab=x&page=2", nav.Uri, StringComparison.Ordinal);
        Assert.Equal("26–50 of 137", cut.Find("[data-testid=pager-range]").TextContent);
        Assert.Equal("page", cut.Find("[data-testid=pager-page-2]").GetAttribute("aria-current"));

        cut.Find("[data-testid=pager-last]").Click();
        Assert.Equal(6, page);
        Assert.Equal("126–137 of 137", cut.Find("[data-testid=pager-range]").TextContent);
        Assert.True(cut.Find("[data-testid=pager-next]").HasAttribute("disabled"));

        cut.Find("[data-testid=pager-size]").Change("50");
        Assert.Equal((1, 50), (page, size));
        Assert.EndsWith("tables?tab=x&size=50", nav.Uri, StringComparison.Ordinal);
        Assert.Equal("1–50 of 137", cut.Find("[data-testid=pager-range]").TextContent);

        cut.Find("[data-testid=pager-size]").Change("all");
        Assert.Equal(0, size);
        Assert.Contains("size=all", nav.Uri, StringComparison.Ordinal);
        Assert.Equal("1–137 of 137", cut.Find("[data-testid=pager-range]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=pager-next]")); // one page: only the range and the size select stay
    }

    [Fact]
    public void StartsFromTheUrl_SoReloadAndBackKeepThePosition()
    {
        using var ctx = PagerContext("tables?page=3&size=10");
        int page = 1;
        int? size = null;

        var cut = ctx.Render<Pager>(p => p.Add(x => x.Total, 137)
            .Add(x => x.PageChanged, v => page = v).Add(x => x.SizeChanged, v => size = v));

        Assert.Equal((3, 10), (page, size));
        Assert.Equal("21–30 of 137", cut.Find("[data-testid=pager-range]").TextContent);
    }

    [Fact]
    public void APrefix_KeepsSeveralListsApartInTheUrl_AndIgnoresUnknownSizes()
    {
        using var ctx = PagerContext("tables?page=4&a-page=2&a-size=7");
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        int page = 1;

        var cut = ctx.Render<Pager>(p => p.Add(x => x.Total, 137).Add(x => x.QueryPrefix, "a").Add(x => x.PageChanged, v => page = v));

        Assert.Equal(2, page);
        Assert.Equal("26–50 of 137", cut.Find("[data-testid=pager-range]").TextContent); // size 7 is not an option: default 25
        cut.Find("[data-testid=pager-next]").Click();
        Assert.Contains("a-page=3", nav.Uri, StringComparison.Ordinal);
        Assert.Contains("page=4", nav.Uri, StringComparison.Ordinal); // the other list's key is untouched
    }

    [Fact]
    public void ChangingTheResetKey_GoesBackToPageOne()
    {
        using var ctx = PagerContext();
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        int page = 1;
        var cut = ctx.Render<Pager>(p => p.Add(x => x.Total, 137).Add(x => x.ResetKey, "").Add(x => x.PageChanged, v => page = v));
        cut.Find("[data-testid=pager-page-3]").Click();
        Assert.Equal(3, page);

        cut.Render(p => p.Add(x => x.Total, 60).Add(x => x.Page, page).Add(x => x.ResetKey, "chat"));

        Assert.Equal(1, page);
        Assert.DoesNotContain("page=", nav.Uri, StringComparison.Ordinal);
        Assert.Equal("page", cut.Find("[data-testid=pager-page-1]").GetAttribute("aria-current"));
    }

    // ---- Wiring on the pages ----

    private static BunitContext ModelsContext(int count, Func<int, IReadOnlyList<string>>? tags = null)
    {
        var models = Enumerable.Range(0, count)
            .Select(i => new ModelDefinition { Id = $"m{i:00}", ServedName = $"Model-{i:00}", ModelPath = $"M{i:00}", Tags = tags?.Invoke(i) ?? [] })
            .ToArray();
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(models));
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(128, 90)));
        ctx.Services.AddSingleton<IModelMetricsReader>(new FakeModelMetricsReader());
        ctx.Services.AddSingleton<IRouterStats>(new FakeRouterStats([]));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        return ctx;
    }

    [Fact]
    public void Models_ArePaged_AndATagFilterGoesBackToPageOne_WithTheCountStillOverEveryModel()
    {
        using var ctx = ModelsContext(40, i => i % 2 == 0 ? ["chat"] : []);
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Models>();

        Assert.Equal(25, cut.FindAll("[data-testid^=models-row-]").Count);
        Assert.Equal("1–25 of 40 models", cut.Find("[data-testid=models-pager-range]").TextContent);

        cut.Find("[data-testid=models-pager-next]").Click();
        Assert.Equal(15, cut.FindAll("[data-testid^=models-row-]").Count);
        Assert.Contains("page=2", nav.Uri, StringComparison.Ordinal);

        cut.Find("[data-testid=models-tag-filter-chat]").Click();
        Assert.Equal(20, cut.FindAll("[data-testid^=models-row-]").Count);
        Assert.Contains("20 of 40 models", cut.Find("[data-testid=models-tag-filter-count]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("page=", nav.Uri, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=models-pager]"));
    }

    [Fact]
    public void Models_OpenOnThePageInTheUrl()
    {
        using var ctx = ModelsContext(40);
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("models?page=2&size=10");

        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Models>();

        Assert.Equal(["m10", "m11", "m12", "m13", "m14", "m15", "m16", "m17", "m18", "m19"],
            cut.FindAll("[data-testid^=models-row-]").Select(r => r.GetAttribute("data-testid")!["models-row-".Length..]));
    }

    [Fact]
    public void Models_RowActions_AreEditPlusAnAccessibleMenu()
    {
        using var ctx = ModelsContext(1);
        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Models>();

        var row = cut.Find("[data-testid=models-row-m00]");
        Assert.Equal("Edit Model-00", row.QuerySelector("[data-testid=models-edit-m00]")!.GetAttribute("aria-label"));

        var trigger = cut.Find("[data-testid=models-more-m00]");
        var menu = cut.Find("[data-testid=models-more-m00-menu]");
        Assert.Equal("BUTTON", trigger.TagName);
        Assert.Equal("button", trigger.GetAttribute("type"));
        Assert.Equal("menu", trigger.GetAttribute("aria-haspopup"));
        Assert.Equal("false", trigger.GetAttribute("aria-expanded"));
        Assert.Equal("More actions for Model-00", trigger.GetAttribute("aria-label"));
        Assert.Equal(menu.Id, trigger.GetAttribute("aria-controls"));
        Assert.Equal(menu.Id, trigger.GetAttribute("popovertarget")); // native popover: top layer, Esc and outside click close it
        Assert.True(trigger.HasAttribute("data-row-menu-trigger"));

        Assert.Equal("menu", menu.GetAttribute("role"));
        Assert.True(menu.HasAttribute("popover"));
        Assert.True(menu.HasAttribute("data-row-menu"));
        Assert.Equal("More actions for Model-00", menu.GetAttribute("aria-label"));

        // Stopped: Chat and Benchmark stay in the menu, disabled, saying why; the rest are links out of the tab order
        // (arrows move between them; js/shell.js skips aria-disabled items).
        var items = menu.QuerySelectorAll("[role=menuitem]");
        Assert.Equal(["Chat · start it first", "Logs", "Benchmark · needs Ready", "Tune", "Edit"],
            items.Select(i => string.Join(' ', i.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))));
        var benchmark = cut.Find("[data-testid=models-benchmark-m00]");
        Assert.Equal("true", benchmark.GetAttribute("aria-disabled"));
        Assert.Null(benchmark.GetAttribute("href"));
        Assert.All(items.Where(i => i.GetAttribute("aria-disabled") != "true"), i => Assert.Equal("-1", i.GetAttribute("tabindex")));
        Assert.Equal("models/m00/tune", cut.Find("[data-testid=models-tune-m00]").GetAttribute("href"));
        Assert.Equal("models/m00/logs", cut.Find("[data-testid=models-logs-m00]").GetAttribute("href"));
        Assert.Equal("models/m00", cut.Find("[data-testid=models-edit-m00]").GetAttribute("href"));
    }

    [Fact]
    public void Models_ReadyRow_MenuOffersChatAndBenchmark()
    {
        using var ctx = ModelsContext(1);
        var runtime = (FakeModelRuntime)ctx.Services.GetRequiredService<IModelRuntime>();
        runtime.SetStatus(new ModelStatus("m00", ModelState.Ready));

        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Models>();

        var items = cut.Find("[data-testid=models-more-m00-menu]").QuerySelectorAll("[role=menuitem]");
        Assert.Equal(["Chat", "Logs", "Benchmark", "Tune", "Edit"], items.Select(i => i.TextContent.Trim()));
        Assert.All(items, i => Assert.Null(i.GetAttribute("aria-disabled")));
        Assert.Equal("models/m00/benchmark", cut.Find("[data-testid=models-benchmark-m00]").GetAttribute("href"));
        Assert.Equal("chat?model=Model-00", cut.Find("[data-testid=models-chat-m00]").GetAttribute("href"));
    }

    [Fact]
    public void Models_LongTagLists_ShowTwoChipsAndAPlusN()
    {
        using var ctx = ModelsContext(2, i => i == 0 ? ["chat", "vision", "code", "embedding", "reasoning"] : ["chat", "vision", "code"]);

        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Models>();

        var many = cut.Find("[data-testid=models-tags-m00]");
        Assert.Equal(3, many.QuerySelectorAll("li").Length);
        var more = cut.Find("[data-testid=models-tags-m00-more]");
        Assert.Equal("+3", more.QuerySelector("[aria-hidden=true]")!.TextContent);
        Assert.Contains("and 3 more:", more.TextContent, StringComparison.Ordinal);
        Assert.Contains("Embedding", more.GetAttribute("title"), StringComparison.Ordinal);

        // Exactly three tags all fit.
        Assert.Equal(3, cut.Find("[data-testid=models-tags-m01]").QuerySelectorAll("li").Length);
        Assert.Empty(cut.FindAll("[data-testid=models-tags-m01-more]"));
    }

    [Fact]
    public void Models_Columns_AreTheGroupedOperatingSet_WithPlacementFactsInTheDrawer()
    {
        using var ctx = ModelsContext(1);

        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Models>();

        var headers = cut.FindAll("[data-testid=models-table] thead th");
        Assert.Equal(["Model", "Where", "State", "Tok/s", "Requests", "Actions"], headers.Select(h => h.TextContent.Trim()));
        Assert.Contains("models-table", cut.Find("[data-testid=models-table]").ClassName, StringComparison.Ordinal);
        // Every fact cell carries its label for the stacked (narrow) layout.
        Assert.Equal(["Where", "State", "tok/s", "Requests"],
            cut.FindAll("[data-testid=models-row-m00] td[data-label]").Select(td => td.GetAttribute("data-label")));
    }

    [Fact]
    public void ApiKeys_ArePaged()
    {
        var store = new FakeApiKeyStore();
        for (int i = 0; i < 30; i++)
        {
            store.AddActiveKey($"ssk_key{i:00}abcdefgh1234", ApiKeyScope.Inference, $"key-{i:00}");
        }

        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IApiKeyStore>(store);

        var cut = ctx.Render<ApiKeys>();

        Assert.Equal(25, cut.FindAll("[data-testid=keys-table] tbody tr").Count);
        Assert.Equal("1–25 of 30 keys", cut.Find("[data-testid=keys-pager-range]").TextContent);
        cut.Find("[data-testid=keys-pager-next]").Click();
        Assert.Equal(5, cut.FindAll("[data-testid=keys-table] tbody tr").Count);
    }
}
