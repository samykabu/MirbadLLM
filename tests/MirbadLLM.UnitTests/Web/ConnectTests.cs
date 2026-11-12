using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>bUnit tests for the Connect page: base URL derived from the request, and the Ready-models list.</summary>
public sealed class ConnectTests
{
    [Fact]
    public void ShowsBaseUrlFromRequestAndReadyModelNames()
    {
        using var ctx = new Bunit.BunitContext();
        var endpoints = new List<ModelEndpoint>
        {
            new("ready-model", "Ready-Model", new Uri("http://ss-ready-model:8000/"), ModelState.Ready),
            new("starting-model", "Starting-Model", new Uri("http://ss-starting-model:8000/"), ModelState.Starting),
        };
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(endpoints));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));

        var httpContext = new DefaultHttpContext
        {
            Request = { Scheme = "http", Host = new HostString("spark", 8080) },
        };

        var cut = ctx.Render<Connect>(parameters => parameters.AddCascadingValue(httpContext));

        Assert.Equal("http://spark:8080/v1", cut.Find("[data-testid=base-url]").TextContent);
        var readyList = cut.Find("[data-testid=ready-models]");
        Assert.Contains("Ready-Model", readyList.TextContent);
        Assert.DoesNotContain("Starting-Model", readyList.TextContent);
    }

    [Fact]
    public void VsCodeSnippetListsReadyModelsWithTokenLimitsFromContext()
    {
        using var ctx = new Bunit.BunitContext();
        var endpoints = new List<ModelEndpoint>
        {
            new("big", "Big-Model", new Uri("http://ss-big:8000/"), ModelState.Ready),
            new("small", "Small-Model", new Uri("http://ss-small:8000/"), ModelState.Ready),
        };
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(endpoints));
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(
        [
            new ModelDefinition { Id = "big", ServedName = "Big-Model", ModelPath = "b", MaxModelLen = 262_144, Tags = [ModelTags.Reasoning, ModelTags.Vision] },
            new ModelDefinition { Id = "small", ServedName = "Small-Model", ModelPath = "s", MaxModelLen = 32_768 },
        ]));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        var httpContext = new DefaultHttpContext { Request = { Scheme = "http", Host = new HostString("spark", 8080) } };

        var cut = ctx.Render<Connect>(parameters => parameters.AddCascadingValue(httpContext));

        string snippet = cut.Find("[data-testid=vscode-snippet]").TextContent;
        Assert.Contains("\"vendor\": \"customendpoint\"", snippet);
        Assert.Contains("\"apiType\": \"chat-completions\"", snippet);
        using var parsed = System.Text.Json.JsonDocument.Parse(snippet);
        var models = parsed.RootElement[0].GetProperty("models");
        Assert.Equal(["Big-Model", "Big-Model-nothink", "Small-Model"], models.EnumerateArray().Select(m => m.GetProperty("id").GetString()));
        Assert.True(models[0].GetProperty("vision").GetBoolean());
        Assert.True(models[0].GetProperty("thinking").GetBoolean());
        Assert.False(models[1].GetProperty("thinking").GetBoolean());
        Assert.False(models[2].GetProperty("vision").GetBoolean());
        Assert.Contains("Big-Model-nothink", cut.Find("[data-testid=nothink-hint]").TextContent, StringComparison.Ordinal);
        Assert.Contains("\"url\": \"http://spark:8080/v1/chat/completions\"", snippet);
        Assert.Contains("\"maxInputTokens\": 245760", snippet);   // 262,144 - 16,384
        Assert.Contains("\"maxInputTokens\": 24576", snippet);    // 32,768 - 8,192 (a quarter)
        Assert.Contains("\"maxOutputTokens\": 8192", snippet);
        Assert.Contains("\"toolCalling\": true", snippet);
    }

    [Fact]
    public void ShowsEmptyStateWhenNoModelsAreReady()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver([]));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));

        var cut = ctx.Render<Connect>();

        Assert.NotNull(cut.Find("[data-testid=ready-models-empty]"));
    }

    // ---- WordBreakParts (pure split, no rendering) ----

    [Theory]
    [InlineData("http://spark:8080/v1")]
    [InlineData("a,b-c.d/e")]
    [InlineData("no-separators-at-all-well-this-has-dashes")]
    [InlineData("")]
    [InlineData("plain text with spaces")]
    public void WordBreakParts_AlwaysConcatenatesBackToTheExactOriginalText(string text) =>
        Assert.Equal(text, string.Concat(Connect.WordBreakParts(text).Select(p => p.Text)));

    [Fact]
    public void WordBreakParts_MarksBreakAfterEverySeparator_AndOnlyThoseParts()
    {
        var parts = Connect.WordBreakParts("a/b.c-d,e");

        Assert.Equal(
            [("a/", true), ("b.", true), ("c-", true), ("d,", true), ("e", false)],
            parts.Select(p => (p.Text, p.BreakAfter)));
    }

    [Fact]
    public void WordBreakParts_TrailingSeparator_StillGetsABreakAfter_WithNoEmptyTrailingPart()
    {
        var parts = Connect.WordBreakParts("end/");

        Assert.Equal([("end/", true)], parts.Select(p => (p.Text, p.BreakAfter)));
    }

    /// <summary>Sets up a Connect page with one Ready model and a mocked "copyText" JS call that records
    /// its arguments, for the <c>&lt;wbr&gt;</c>-vs-exact-copy tests below.</summary>
    private static (Bunit.BunitContext Ctx, Bunit.BunitJSModuleInterop Module) MakeReadyContext()
    {
        var ctx = new Bunit.BunitContext();
        var endpoints = new List<ModelEndpoint> { new("big", "Big-Model", new Uri("http://ss-big:8000/"), ModelState.Ready) };
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(endpoints));
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(
        [
            new ModelDefinition { Id = "big", ServedName = "Big-Model", ModelPath = "b", MaxModelLen = 131_072 },
        ]));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        var module = ctx.JSInterop.SetupModule("./interop.js");
        module.Setup<bool>("copyText", _ => true).SetResult(true);
        return (ctx, module);
    }

    private static Bunit.IRenderedComponent<Connect> RenderConnect(Bunit.BunitContext ctx)
    {
        var httpContext = new DefaultHttpContext { Request = { Scheme = "http", Host = new HostString("spark", 8080) } };
        return ctx.Render<Connect>(parameters => parameters.AddCascadingValue(httpContext));
    }

    [Fact]
    public void ServedNameCopy_UsesTheExactRouterName_InTheEndpointColumn()
    {
        var (ctx, module) = MakeReadyContext();
        using var owned = ctx;
        var cut = RenderConnect(ctx);
        Assert.NotNull(cut.Find(".access-layout > div:first-child [data-testid=ready-models]"));
        cut.Find("[data-testid=copy-served-big]").Click();
        Assert.Equal("Big-Model", Assert.Single(module.Invocations["copyText"]).Arguments[0]);
    }

    [Theory]
    [InlineData("vscode", "vscode-snippet", "Copy VS Code models configuration")]
    [InlineData("cline", "cline-snippet", "Copy Cline and Roo Code settings")]
    [InlineData("ollama", "ollama-snippet", "Copy Ollama settings")]
    [InlineData("curl", "curl-snippet", "Copy curl command")]
    public void Snippet_RendersWbrAfterSeparators_ButCopySendsTheExactUnbrokenText(string tab, string snippetTestId, string copyButtonLabel)
    {
        var (ctx, module) = MakeReadyContext();
        using var _ = ctx;
        var cut = RenderConnect(ctx);
        cut.Find($"[data-testid=client-tabs-tab-{tab}]").Click();

        var pre = cut.Find($"[data-testid={snippetTestId}]");
        int separators = pre.TextContent.Count(c => c is '/' or '.' or '-' or ',');
        var wbrs = pre.QuerySelectorAll("wbr");
        Assert.Equal(separators, wbrs.Length);
        Assert.True(separators > 0, "The snippet should contain at least one break-opportunity separator to make this test meaningful.");

        // The rendered text (tags stripped) is exactly the same string the snippet started as: <wbr>
        // contributes no characters, so reconstructing it from the DOM must round-trip perfectly.
        string renderedText = pre.TextContent;

        cut.Find($"button[aria-label='{copyButtonLabel}']").Click();

        var invocation = Assert.Single(module.Invocations["copyText"]);
        string copied = Assert.IsType<string>(invocation.Arguments[0]);
        Assert.Equal(renderedText, copied);
        Assert.DoesNotContain("wbr", copied, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContinueSnippet_AlsoRendersWbr_WithoutABreakingTheCopyButtonWiring()
    {
        var (ctx, module) = MakeReadyContext();
        using var _ = ctx;
        var cut = RenderConnect(ctx);
        cut.Find("[data-testid=client-tabs-tab-continue]").Click();

        var pre = cut.Find("[data-testid=continue-snippet]");
        Assert.Contains("provider: openai", pre.TextContent, StringComparison.Ordinal);
        Assert.NotEmpty(pre.QuerySelectorAll("wbr"));
        string renderedText = pre.TextContent;

        cut.Find("button[aria-label='Copy Continue configuration']").Click();

        var invocation = Assert.Single(module.Invocations["copyText"]);
        Assert.Equal(renderedText, Assert.IsType<string>(invocation.Arguments[0]));
    }

    [Fact]
    public void ClientSettings_AreTabs_VsCodeFirst_WithTheEssentialsAboveThem()
    {
        var (ctx, _) = MakeReadyContext();
        using var __ = ctx;
        var cut = RenderConnect(ctx);

        var tabs = cut.FindAll("[data-testid=client-tabs-list] [role=tab]");
        Assert.Equal(["VS Code", "Cline / Roo Code", "Continue", "Ollama", "curl"], tabs.Select(t => t.QuerySelector(".tabs-tab-label")!.TextContent.Trim()));
        Assert.Equal("true", cut.Find("[data-testid=client-tabs-tab-vscode]").GetAttribute("aria-selected"));
        Assert.Equal("Client settings", cut.Find("[data-testid=client-tabs-list]").GetAttribute("aria-label"));
        Assert.Equal("tablist", cut.Find("[data-testid=client-tabs-list]").GetAttribute("role"));
        Assert.Equal("tabpanel", cut.Find("[data-testid=client-tabs-panel]").GetAttribute("role"));

        // Only the selected client's snippet is in the panel.
        Assert.Single(cut.FindAll("[data-testid=client-tabs-panel] .snippet"));
        Assert.NotNull(cut.Find("[data-testid=client-tabs-panel] [data-testid=vscode-snippet]"));

        // Base URL, metrics URL and the API-key line stay above the tabs, outside any tab panel.
        string html = cut.Markup;
        int tabsAt = html.IndexOf("data-testid=\"client-tabs\"", StringComparison.Ordinal);
        Assert.InRange(html.IndexOf("data-testid=\"base-url\"", StringComparison.Ordinal), 0, tabsAt);
        Assert.InRange(html.IndexOf("data-testid=\"metrics-url\"", StringComparison.Ordinal), 0, tabsAt);
        Assert.NotNull(cut.Find("button[aria-label='Copy base URL']"));
    }

    [Theory]
    [InlineData("vscode", "vscode-snippet", "Copy VS Code models configuration")]
    [InlineData("cline", "cline-snippet", "Copy Cline and Roo Code settings")]
    [InlineData("continue", "continue-snippet", "Copy Continue configuration")]
    [InlineData("curl", "curl-snippet", "Copy curl command")]
    public void EverySnippet_IsInItsOwnTab_WithItsCopyButton_AndTheTabIsKeptInTheUrl(string tab, string snippetTestId, string copyButtonLabel)
    {
        var (ctx, module) = MakeReadyContext();
        using var _ = ctx;
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var cut = RenderConnect(ctx);

        cut.Find($"[data-testid=client-tabs-tab-{tab}]").Click();

        Assert.Equal("true", cut.Find($"[data-testid=client-tabs-tab-{tab}]").GetAttribute("aria-selected"));
        var panel = cut.Find("[data-testid=client-tabs-panel]");
        Assert.NotNull(panel.QuerySelector($"[data-testid={snippetTestId}]"));
        Assert.Contains("http://spark:8080/v1", panel.QuerySelector($"[data-testid={snippetTestId}]")!.TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".snippet pre"));
        if (tab != "vscode")
        {
            Assert.EndsWith($"?client={tab}", nav.Uri, StringComparison.Ordinal);
        }

        panel.QuerySelector($"button[aria-label='{copyButtonLabel}']")!.Click();
        Assert.Single(module.Invocations["copyText"]);
    }

    [Fact]
    public void OllamaTab_GivesTheAddressWithoutV1_TheModel_AndTheOllamaHostVariable()
    {
        var (ctx, _) = MakeReadyContext();
        using var __ = ctx;
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("connect?client=ollama");

        var cut = RenderConnect(ctx);

        Assert.Equal(
            "Base URL: http://spark:8080\nAPI key: (not required)\nModel: Big-Model\nEnvironment: OLLAMA_HOST=http://spark:8080",
            cut.Find("[data-testid=client-tabs-panel] [data-testid=ollama-snippet]").TextContent);
        Assert.Contains("Choose the Ollama provider in your client.", cut.Find(".snippet-steps").TextContent, StringComparison.Ordinal);
        string hint = cut.Find("[data-testid=ollama-hint]").TextContent;
        Assert.Contains("Big-Model:latest", hint, StringComparison.Ordinal);
        // Open now, and the page says what changes once a key is required.
        Assert.NotNull(cut.Find("[data-testid=ollama-key-open]"));
        Assert.Empty(cut.FindAll("[data-testid=ollama-key-required]"));
    }

    [Fact]
    public void OllamaTab_WithAKeyRequired_ShowsTheHeader_AndSaysWhichClientsAreRefused()
    {
        var (ctx, _) = MakeReadyContext();
        using var __ = ctx;
        ctx.Services.AddSingleton(Options.Create(new AuthOptions { RequireApiKey = true }));
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("connect?client=ollama");

        var cut = RenderConnect(ctx);

        Assert.Contains("API key: Authorization: Bearer <your key>", cut.Find("[data-testid=ollama-snippet]").TextContent, StringComparison.Ordinal);
        Assert.Contains("ollama command line", cut.Find("[data-testid=ollama-key-required]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=ollama-key-open]"));
    }

    [Fact]
    public void ClientTab_IsReadFromTheUrl_SoALinkOrReloadKeepsIt()
    {
        var (ctx, _) = MakeReadyContext();
        using var __ = ctx;
        ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("connect?client=curl");

        var cut = RenderConnect(ctx);

        Assert.Equal("true", cut.Find("[data-testid=client-tabs-tab-curl]").GetAttribute("aria-selected"));
        Assert.Equal("false", cut.Find("[data-testid=client-tabs-tab-vscode]").GetAttribute("aria-selected"));
        Assert.NotNull(cut.Find("[data-testid=client-tabs-panel] [data-testid=curl-snippet]"));
    }

    private static Bunit.BunitContext TwoNodeContext(bool offStartsOnRequest = false)
    {
        var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(
        [
            new("big", "Big-Model", new Uri("http://ss-big:8000/"), ModelState.Ready),
            new("small", "Small-Model", new Uri("http://ss-small:8000/"), ModelState.Ready),
            new("off", "Off-Model", new Uri("http://ss-off:8000/"), ModelState.Stopped),
        ]));
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(
        [
            new ModelDefinition { Id = "big", ServedName = "Big-Model", ModelPath = "b", MaxModelLen = 131_072, Placement = new ModelPlacement("dgx") },
            new ModelDefinition
            {
                Id = "small", ServedName = "Small-Model", ModelPath = "s", Runtime = RuntimeKind.LlamaCpp,
                LlamaCpp = new LlamaCppSettings { ModelFile = "s.gguf", ContextSize = 16_384 }, Placement = new ModelPlacement("pc"),
            },
            new ModelDefinition { Id = "off", ServedName = "Off-Model", ModelPath = "o", Placement = new ModelPlacement("pc"), StartOnRequest = offStartsOnRequest },
        ]));
        ctx.Services.AddSingleton<INodeRegistry>(new ListOnlyNodeRegistry(
            new NodeRecord { Id = "dgx", Uid = "dgx-uid", Address = "https://127.0.0.1:1", Approved = true },
            new NodeRecord { Id = "pc", Uid = "pc-uid", DisplayName = "Office PC", Address = "https://127.0.0.1:2", Approved = true }));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        return ctx;
    }

    [Fact]
    public void ServedModels_ShowTheirRealNodeAndRuntime_AndTheVsCodeNamesSayWhereTheyRun()
    {
        using var ctx = TwoNodeContext();
        var cut = RenderConnect(ctx);

        Assert.Contains("dgx", cut.Find("[data-testid=ready-model-where-big]").TextContent, StringComparison.Ordinal);
        Assert.Contains("vLLM", cut.Find("[data-testid=ready-model-where-big]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Office PC", cut.Find("[data-testid=ready-model-where-small]").TextContent, StringComparison.Ordinal);
        Assert.Contains("llama.cpp", cut.Find("[data-testid=ready-model-where-small]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Off-Model", cut.Find("[data-testid=not-ready-models]").TextContent, StringComparison.Ordinal);
        var unavailable = cut.Find("li[data-testid=not-ready-model-off]");
        Assert.Equal("models/off/overview", unavailable.QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("Office PC · vLLM", unavailable.TextContent, StringComparison.Ordinal);
        Assert.Equal("stopped", unavailable.QuerySelector("[data-testid=not-ready-state-off]")!.GetAttribute("data-state"));
        Assert.DoesNotContain("Off-Model", cut.Find("[data-testid=ready-models]").TextContent, StringComparison.Ordinal);

        string snippet = cut.Find("[data-testid=vscode-snippet]").TextContent;
        Assert.DoesNotContain("DGX", snippet, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Big-Model (dgx · vLLM)\"", snippet, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Small-Model (Office PC · llama.cpp)\"", snippet, StringComparison.Ordinal);
        Assert.Contains("\"maxInputTokens\": 12288", snippet, StringComparison.Ordinal); // llama.cpp context 16,384 minus a quarter
    }

    [Fact]
    public void ClientSettings_OfferAStoppedModelThatStartsOnRequest_AfterTheReadyOnes()
    {
        using var ctx = TwoNodeContext(offStartsOnRequest: true);
        var cut = RenderConnect(ctx);

        Assert.Contains("Off-Model", cut.Find("[data-testid=on-request-models]").TextContent, StringComparison.Ordinal);
        var options = cut.Find("[data-testid=snippet-model-select]").QuerySelectorAll("option");
        Assert.Equal(["Big-Model", "Small-Model", "Off-Model"], options.Select(o => o.GetAttribute("value")));
        Assert.EndsWith("starts on request", options[2].TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("starts on request", options[0].TextContent, StringComparison.Ordinal);

        // The first Ready model stays the default, and the VS Code entry lists all three.
        Assert.Contains("Big-Model", cut.Find("#snippet-model-hint").TextContent, StringComparison.Ordinal);
        string snippet = cut.Find("[data-testid=vscode-snippet]").TextContent;
        Assert.Contains("\"id\": \"Big-Model\"", snippet, StringComparison.Ordinal);
        Assert.Contains("\"id\": \"Off-Model\"", snippet, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Off-Model (Office PC · vLLM)\"", snippet, StringComparison.Ordinal);

        cut.Find("[data-testid=snippet-model-select]").Change("Off-Model");
        cut.Find("[data-testid=client-tabs-tab-curl]").Click();
        Assert.Contains("\"model\":\"Off-Model\"", cut.Find("[data-testid=curl-snippet]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientSettings_WithOnlyAModelThatStartsOnRequest_UseItInsteadOfThePlaceholder()
    {
        var ctx = new Bunit.BunitContext();
        using var _ = ctx;
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(
            [new("off", "Off-Model", new Uri("http://ss-off:8000/"), ModelState.Stopped)]));
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(
            [new ModelDefinition { Id = "off", ServedName = "Off-Model", ModelPath = "o", StartOnRequest = true }]));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));

        var cut = RenderConnect(ctx);

        Assert.Empty(cut.FindAll("[data-testid=snippet-model-placeholder]"));
        Assert.Contains("\"id\": \"Off-Model\"", cut.Find("[data-testid=vscode-snippet]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("<served-name>", cut.Find("[data-testid=vscode-snippet]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void SnippetModel_IsChosenByTheUser_AndFillsTheSingleModelSnippets()
    {
        using var ctx = TwoNodeContext();
        var nav = ctx.Services.GetRequiredService<NavigationManager>();
        var cut = RenderConnect(ctx);

        var select = cut.Find("[data-testid=snippet-model-select]");
        Assert.Equal(["Big-Model", "Small-Model"], select.QuerySelectorAll("option").Select(o => o.GetAttribute("value")));
        Assert.Contains("Big-Model", cut.Find("#snippet-model-hint").TextContent, StringComparison.Ordinal);

        select.Change("Small-Model");
        cut.Find("[data-testid=client-tabs-tab-curl]").Click();

        Assert.Contains("\"model\":\"Small-Model\"", cut.Find("[data-testid=curl-snippet]").TextContent, StringComparison.Ordinal);
        Assert.Contains("served on Office PC · llama.cpp", cut.Find("#snippet-model-hint").TextContent, StringComparison.Ordinal);
        Assert.Contains("model=Small-Model", new Uri(nav.Uri).Query, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessHeader_MarksConnectAsCurrent_AndTheBaseUrlAndKeysTabCarryTourAnchors()
    {
        using var ctx = TwoNodeContext();
        var cut = RenderConnect(ctx);

        Assert.Equal("Access", cut.Find("[data-testid=page-title]").TextContent);
        Assert.Equal("page", cut.Find("[data-testid=access-tab-connect]").GetAttribute("aria-current"));
        Assert.Null(cut.Find("[data-testid=access-tab-keys]").GetAttribute("aria-current"));
        Assert.Equal("access/keys", cut.Find("[data-tour=access-keys-tab]").GetAttribute("href"));
        Assert.NotNull(cut.Find("[data-tour=access-base-url] [data-testid=base-url]"));
    }
}
