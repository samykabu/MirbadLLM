using Bunit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Web;

/// <summary>bUnit tests for the Chat page: model list/preselection, streaming a fake reply into the
/// conversation, and the per-message stats line.</summary>
public sealed class ChatTests
{
    private static List<ModelEndpoint> ReadyEndpoints(params string[] servedNames) =>
        servedNames.Select(n => new ModelEndpoint(n, n, new Uri($"http://ss-{n}:8000/"), ModelState.Ready)).ToList();

    [Fact]
    public void ModelDropdownShowsReadyModelNames()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(ReadyEndpoints("model-a", "model-b")));
        ctx.Services.AddSingleton<IChatClient>(new FakeChatClient());

        var cut = ctx.Render<Chat>();

        var options = cut.Find("[data-testid=chat-model-select]").QuerySelectorAll("option");
        var values = options.Select(o => o.GetAttribute("value")).ToList();
        Assert.Contains("model-a", values);
        Assert.Contains("model-b", values);
        Assert.NotNull(cut.Find("[data-testid=chat-picker] [data-testid=chat-model-select]"));
        Assert.Empty(cut.FindAll(".chat-settings [data-testid=chat-model-select]"));
        Assert.Equal("— tok/s", cut.Find("[data-testid=chat-live-rate]").TextContent.Trim());
    }

    [Fact]
    public void PreselectsModelFromQueryString()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(ReadyEndpoints("model-a", "model-b")));
        var chatClient = new FakeChatClient();
        chatClient.ChunksToYield.Add(new ChatChunk(Content: "ok"));
        ctx.Services.AddSingleton<IChatClient>(chatClient);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?model=model-b");

        var cut = ctx.Render<Chat>(parameters => parameters.AddCascadingValue(httpContext));

        cut.Find("[data-testid=chat-input]").Input("hi");
        cut.Find("[data-testid=chat-send]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(chatClient.LastRequest));
        Assert.Equal("model-b", chatClient.LastRequest?.ServedName);
    }

    [Fact]
    public void SendingStreamsFakeReplyIntoBubbleWithStats()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(ReadyEndpoints("model-a")));
        var chatClient = new FakeChatClient();
        chatClient.ChunksToYield.AddRange(
        [
            new ChatChunk(Content: "Hel"),
            new ChatChunk(Content: "lo"),
            new ChatChunk(Reasoning: "thinking..."),
            new ChatChunk(FinishReason: "stop", Usage: new ChatUsage(3, 2)),
        ]);
        ctx.Services.AddSingleton<IChatClient>(chatClient);

        var cut = ctx.Render<Chat>();

        cut.Find("[data-testid=chat-input]").Input("hi there");
        cut.Find("[data-testid=chat-send]").Click();

        // The fake client yields chunks across real async continuations, so the stream keeps
        // running past the initial event dispatch; wait for the final render to land.
        cut.WaitForAssertion(() => Assert.Contains("stop", cut.Find("[data-testid=chat-stats-1]").TextContent));

        var assistantBubble = cut.Find("[data-testid=chat-message-assistant]");
        Assert.Contains("Hello", assistantBubble.TextContent);
        Assert.Contains("thinking...", assistantBubble.TextContent);

        var stats = cut.Find("[data-testid=chat-stats-1]");
        Assert.Contains("stop", stats.TextContent);
        Assert.Contains("completion 2", stats.TextContent);
        Assert.Contains("prompt 3", stats.TextContent);

        Assert.Equal("model-a", chatClient.LastRequest?.ServedName);
        Assert.Equal("hi there", chatClient.LastRequest?.Messages[0].Content);
    }

    [Fact]
    public void UpstreamErrorShownInlineNotAsException()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(ReadyEndpoints("model-a")));
        var chatClient = new FakeChatClient { ThrowBeforeYielding = new InvalidOperationException("model is not ready") };
        ctx.Services.AddSingleton<IChatClient>(chatClient);

        var cut = ctx.Render<Chat>();

        cut.Find("[data-testid=chat-input]").Input("hi");
        cut.Find("[data-testid=chat-send]").Click();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=chat-message-error]")));
        var error = cut.Find("[data-testid=chat-message-error]");
        Assert.Contains("model is not ready", error.TextContent);
    }

    [Fact]
    public void EveryControlIsLabelledAndTheConversationIsALiveLog()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(ReadyEndpoints("model-a")));
        ctx.Services.AddSingleton<IChatClient>(new FakeChatClient());

        var cut = ctx.Render<Chat>();

        foreach (var control in cut.FindAll("input, select, textarea"))
        {
            string id = control.GetAttribute("id") ?? "";
            Assert.True(id.Length > 0 && cut.FindAll($"label[for='{id}']").Count > 0, $"Unlabelled control: {control.OuterHtml}");
        }

        var conversation = cut.Find("[data-testid=chat-conversation]");
        Assert.Equal("log", conversation.GetAttribute("role"));
        Assert.Equal("polite", conversation.GetAttribute("aria-live"));
        Assert.Equal("false", cut.Find("[data-testid=chat-settings-toggle]").GetAttribute("aria-expanded"));
    }

    [Fact]
    public void SendButtonDisabledWhenNoModelSelectedOrInputEmpty()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver([]));
        ctx.Services.AddSingleton<IChatClient>(new FakeChatClient());

        var cut = ctx.Render<Chat>();

        Assert.True(cut.Find("[data-testid=chat-send]").HasAttribute("disabled"));
    }

    private sealed class GatedChatClient : IChatClient
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return new ChatChunk(Content: "Hel");
            await Release.Task.WaitAsync(ct);
            yield return new ChatChunk(Content: "lo there");
            yield return new ChatChunk(FinishReason: "stop", Usage: new ChatUsage(3, 2));
        }
    }

    [Fact]
    public void StreamingReplyIsSilentWhileInFlightAndTheFinishedReplyIsAnnouncedOnce()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(ReadyEndpoints("model-a")));
        var client = new GatedChatClient();
        ctx.Services.AddSingleton<IChatClient>(client);

        var cut = ctx.Render<Chat>();
        cut.Find("[data-testid=chat-input]").Input("hi");
        cut.Find("[data-testid=chat-send]").Click();

        // In flight: the log keeps its polite semantics, but the growing reply opts out and nothing is announced.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=chat-message-assistant]")));
        var log = cut.Find("[data-testid=chat-conversation]");
        Assert.Equal("log", log.GetAttribute("role"));
        Assert.Equal("off", cut.Find("[data-testid=chat-message-assistant]").GetAttribute("aria-live"));
        Assert.Equal("true", cut.Find("[data-testid=chat-message-assistant]").GetAttribute("aria-busy"));
        Assert.Equal("", cut.Find("[data-testid=chat-announcer]").TextContent);

        client.Release.SetResult();

        // Done: the whole reply goes to the status region once.
        cut.WaitForAssertion(() => Assert.Equal("model-a replied: Hello there", cut.Find("[data-testid=chat-announcer]").TextContent));
        Assert.Equal("status", cut.Find("[data-testid=chat-announcer]").GetAttribute("role"));
        Assert.Null(cut.Find("[data-testid=chat-message-assistant]").GetAttribute("aria-busy"));
    }

    [Fact]
    public void SettingsSummaryKeepsTheModelNameInItsOwnTruncatableSpan()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(ReadyEndpoints("a-very-long-served-model-name")));
        ctx.Services.AddSingleton<IChatClient>(new FakeChatClient());

        var cut = ctx.Render<Chat>();

        Assert.Equal("a-very-long-served-model-name", cut.Find(".chat-settings-model").TextContent);
        Assert.Equal("· temp 0.70 · 1024 tokens", cut.Find(".chat-settings-rest").TextContent);
    }

    [Fact]
    public async Task EnterKeySendsTheFieldsCurrentTextEvenBeforeTheServerSawIt()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(ReadyEndpoints("model-a")));
        var chatClient = new FakeChatClient();
        chatClient.ChunksToYield.Add(new ChatChunk(Content: "ok"));
        ctx.Services.AddSingleton<IChatClient>(chatClient);
        var cut = ctx.Render<Chat>(parameters => parameters.AddCascadingValue(new DefaultHttpContext()));

        // The JS Enter handler hands over the textarea's value; no input/change event reached the server.
        await cut.InvokeAsync(() => cut.Instance.SendFromJsAsync("typed then Enter"));

        cut.WaitForAssertion(() => Assert.NotNull(chatClient.LastRequest));
        Assert.Equal("typed then Enter", chatClient.LastRequest!.Messages[^1].Content);
    }

    [Fact]
    public void ModelPickerShowsNodeAndRuntimeAndListsNotReadyModelsAsDisabledWithTheirState()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(
        [
            new("a", "model-a", new Uri("http://a/"), ModelState.Ready),
            new("b", "model-b", new Uri("http://b/"), ModelState.Starting),
        ]));
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(
        [
            new ModelDefinition { Id = "a", ServedName = "model-a", ModelPath = "a", Runtime = RuntimeKind.LlamaCpp, Placement = new ModelPlacement("pc") },
            new ModelDefinition { Id = "b", ServedName = "model-b", ModelPath = "b", Placement = new ModelPlacement("dgx") },
        ]));
        ctx.Services.AddSingleton<IChatClient>(new FakeChatClient());

        var cut = ctx.Render<Chat>();

        var ready = cut.Find("[data-testid=chat-model-select] option[value=model-a]");
        Assert.False(ready.HasAttribute("disabled"));
        Assert.Contains("pc · llama.cpp", ready.TextContent, StringComparison.Ordinal);
        var starting = cut.Find("[data-testid=chat-model-select] option[value=model-b]");
        Assert.True(starting.HasAttribute("disabled"));
        Assert.Contains("dgx · vLLM · Starting", starting.TextContent, StringComparison.Ordinal);
        Assert.Contains("Served on pc by llama.cpp.", cut.Find("[data-testid=chat-model-where]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 other model is not Ready", cut.Find("[data-testid=chat-model-hint]").TextContent, StringComparison.Ordinal);
        Assert.Equal("playground-model", cut.Find("#chat-model").ParentElement!.GetAttribute("data-tour"));
    }

    [Fact]
    public void AVisionModelTakesAnImageAndSendsItWhileAPlainModelOffersNoAttachButton()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(
        [
            new("v", "vision-model", new Uri("http://v/"), ModelState.Ready),
            new("t", "text-model", new Uri("http://t/"), ModelState.Ready),
        ]));
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry(
        [
            new ModelDefinition { Id = "v", ServedName = "vision-model", ModelPath = "v", Tags = [ModelTags.Vision] },
            new ModelDefinition { Id = "t", ServedName = "text-model", ModelPath = "t" },
        ]));
        var chatClient = new FakeChatClient();
        ctx.Services.AddSingleton<IChatClient>(chatClient);
        var cut = ctx.Render<Chat>();

        cut.Find("[data-testid=chat-model-select]").Change("text-model");
        Assert.Empty(cut.FindAll("[data-testid=chat-attach]"));

        cut.Find("[data-testid=chat-model-select]").Change("vision-model");
        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(
            InputFileContent.CreateFromBinary([1, 2, 3], "cat.png", contentType: "image/png"),
            InputFileContent.CreateFromBinary([1], "notes.txt", contentType: "text/plain"));
        Assert.Single(cut.FindAll("[data-testid=chat-attachments] li"));
        Assert.Contains("notes.txt", cut.Find("[data-testid=chat-error]").TextContent, StringComparison.Ordinal);

        // An image alone can be sent.
        cut.Find("[data-testid=chat-send]").Click();
        var sent = chatClient.LastRequest!.Messages[^1];
        Assert.Equal(["data:image/png;base64,AQID"], sent.Images);
        Assert.Empty(cut.FindAll("[data-testid=chat-attachments]"));
        Assert.Single(cut.FindAll("[data-testid=chat-message-images] img"));
    }

    [Fact]
    public void DisabledSendSaysWhyAndTheSuggestedPromptFillsTheMessage()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver(ReadyEndpoints("model-a")));
        ctx.Services.AddSingleton<IChatClient>(new FakeChatClient());

        var cut = ctx.Render<Chat>();

        var send = cut.Find("[data-testid=chat-send]");
        Assert.True(send.HasAttribute("disabled"));
        Assert.Equal("chat-send-reason", send.GetAttribute("aria-describedby"));
        Assert.Equal("Type a message to send.", cut.Find("[data-testid=chat-send-reason]").TextContent);
        Assert.Contains("model-a", cut.Find("[data-testid=chat-ready-models]").TextContent, StringComparison.Ordinal);
        Assert.Equal("OPTGROUP", cut.Find("[data-testid=chat-ready-models]").TagName);
        Assert.Empty(cut.FindAll("[data-testid=chat-empty] button[data-testid^=chat-pick-]"));

        cut.Find("[data-testid=chat-suggestion]").Click();

        Assert.Equal(Chat.SuggestedPrompt, cut.Find("[data-testid=chat-input]").GetAttribute("value"));
        Assert.False(cut.Find("[data-testid=chat-send]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=chat-send-reason]"));
    }

    [Fact]
    public void WithoutAReadyModelSendSaysSoAndTheEmptyStatePointsToModels()
    {
        using var ctx = new Bunit.BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelEndpointResolver>(new FakeModelEndpointResolver([]));
        ctx.Services.AddSingleton<IChatClient>(new FakeChatClient());

        var cut = ctx.Render<Chat>();

        Assert.Equal("Send needs a Ready model; none is Ready.", cut.Find("[data-testid=chat-send-reason]").TextContent);
        Assert.Equal("models", cut.Find("[data-testid=chat-empty] a").GetAttribute("href"));
        Assert.Equal("Playground", cut.Find("[data-testid=page-title]").TextContent);
    }
}
