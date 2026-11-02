using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Web;

/// <summary>The tag filter on the Models page and the Dashboard: chips with counts, ALL semantics, the URL query, accessibility.</summary>
public sealed class TagFilterTests
{
    private static readonly ModelDefinition[] Fleet =
    [
        new() { Id = "chat-vl", ServedName = "Chat-VL", ModelPath = "a", Tags = ["chat", "vision"] },
        new() { Id = "chat-only", ServedName = "Chat-Only", ModelPath = "b", Tags = ["chat"] },
        new() { Id = "embedder", ServedName = "Embedder", ModelPath = "c", Tags = ["embedding", "long-context"] },
        new() { Id = "untagged", ServedName = "Untagged", ModelPath = "d" },
    ];

    private static Bunit.BunitContext Context()
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(Fleet));
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(128, 90)));
        ctx.Services.AddSingleton<IModelMetricsReader>(new FakeModelMetricsReader());
        ctx.Services.AddSingleton<IRouterStats>(new FakeRouterStats([]));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        return ctx;
    }

    private static string[] RowIds(IRenderedComponent<MirbadLLM.Web.Components.Pages.Models> cut) =>
        cut.FindAll("[data-testid^=models-row-]").Select(r => r.GetAttribute("data-testid")!["models-row-".Length..]).Order().ToArray();

    [Fact]
    public void Models_ShowsOnlyTagsInUseWithCounts_AndTagsOnEachRow()
    {
        using var ctx = Context();

        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Models>();

        var chips = cut.FindAll("[data-testid=models-tag-filter] button[aria-pressed]");
        Assert.Equal(4, chips.Count); // chat, embedding, long-context, vision — not the unused built-ins
        var chat = cut.Find("[data-testid=models-tag-filter-chat]");
        Assert.Equal("false", chat.GetAttribute("aria-pressed"));
        Assert.Contains("Chat", chat.TextContent, StringComparison.Ordinal);
        Assert.Equal("2", chat.QuerySelector(".tag-toggle-count")!.TextContent);
        Assert.Contains("models", chat.TextContent, StringComparison.Ordinal); // "Chat, 2 models" for screen readers
        Assert.Contains("4 of 4 models", cut.Find("[data-testid=models-tag-filter-count]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Vision", cut.Find("[data-testid=models-tags-chat-vl]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=models-tags-untagged]"));
    }

    [Fact]
    public void Models_SelectingTags_KeepsModelsWithAllOfThem_AndWritesTheUrl()
    {
        using var ctx = Context();
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Models>();

        cut.Find("[data-testid=models-tag-filter-chat]").Click();
        Assert.Equal(["chat-only", "chat-vl"], RowIds(cut));
        Assert.Equal("true", cut.Find("[data-testid=models-tag-filter-chat]").GetAttribute("aria-pressed"));
        Assert.Contains("2 of 4 models", cut.Find("[data-testid=models-tag-filter-count]").TextContent, StringComparison.Ordinal);
        Assert.EndsWith("?tags=chat", nav.Uri, StringComparison.Ordinal);

        cut.Find("[data-testid=models-tag-filter-vision]").Click();
        Assert.Equal(["chat-vl"], RowIds(cut));
        Assert.Contains("tags=chat%2Cvision", nav.Uri, StringComparison.Ordinal);

        cut.Find("[data-testid=models-tag-filter-embedding]").Click();
        Assert.Empty(RowIds(cut));
        Assert.NotNull(cut.Find("[data-testid=models-filter-empty]"));

        cut.Find("[data-testid=models-tag-filter-clear]").Click();
        Assert.Equal(4, RowIds(cut).Length);
        Assert.DoesNotContain("tags=", nav.Uri, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=models-tag-filter-clear]"));
    }

    [Fact]
    public void Models_ReadsTheSelectionFromTheUrl()
    {
        using var ctx = Context();
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("models?tags=Embedding,long-context");

        var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Models>();

        Assert.Equal(["embedder"], RowIds(cut));
        Assert.Equal("true", cut.Find("[data-testid=models-tag-filter-embedding]").GetAttribute("aria-pressed"));
        Assert.Equal("true", cut.Find("[data-testid=models-tag-filter-long-context]").GetAttribute("aria-pressed"));
        Assert.Contains("1 of 4 models", cut.Find("[data-testid=models-tag-filter-count]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Models_FilterRowIsAGroupWithALabel_AndHiddenWhenNoModelHasTags()
    {
        using (var ctx = Context())
        {
            var cut = ctx.Render<MirbadLLM.Web.Components.Pages.Models>();
            var group = cut.Find("[data-testid=models-tag-filter]");
            Assert.Equal("group", group.GetAttribute("role"));
            Assert.Equal("Filter by tag", cut.Find($"#{group.GetAttribute("aria-labelledby")}").TextContent.Trim());
            Assert.Equal("status", cut.Find("[data-testid=models-tag-filter-count]").GetAttribute("role"));
        }

        using var plain = new Bunit.BunitContext();
        plain.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry([new ModelDefinition { Id = "x", ServedName = "X", ModelPath = "x" }]));
        plain.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        Assert.Empty(plain.Render<MirbadLLM.Web.Components.Pages.Models>().FindAll("[data-testid=models-tag-filter]"));
    }

}
