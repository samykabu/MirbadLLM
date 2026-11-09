using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Router.Fakes;

namespace MirbadLLM.UnitTests.Router;

/// <summary>Unit tests for <see cref="RouterChatClient"/>: SSE chunk parsing, request shape, readiness
/// checks, cancellation and <see cref="RouterStats"/> counting.</summary>
public sealed class RouterChatClientTests
{
    private static (IChatClient ChatClient, FakeModelEndpointResolver Resolver, RouterStats Stats, FakeUpstreamHandler Handler) Build()
    {
        var resolver = new FakeModelEndpointResolver();
        var handler = new FakeUpstreamHandler();

        var services = new ServiceCollection();
        services.AddMirbadLLMRouter();
        services.AddSingleton<IModelEndpointResolver>(resolver);
        services.AddHttpClient(RouterExtensions.UpstreamHttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);

        var provider = services.BuildServiceProvider();
        var chatClient = provider.GetRequiredService<IChatClient>();
        var stats = provider.GetRequiredService<RouterStats>();
        return (chatClient, resolver, stats, handler);
    }

    private const string SseBody = """
        data: {"choices":[{"delta":{"content":"Hel"},"finish_reason":null}]}

        data: {"choices":[{"delta":{"content":"lo"},"finish_reason":null}]}

        data: {"choices":[{"delta":{"reasoning_content":"thinking..."},"finish_reason":null}]}

        data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":2}}

        data: [DONE]

        """;

    [Fact]
    public async Task StreamsChunks_ContentReasoningAndFinalUsage_InOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var (chatClient, resolver, _, handler) = Build();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));
        handler.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseBody, Encoding.UTF8, "text/event-stream"),
        });

        var request = new ChatRequest("chat-model", [new ChatMessage("user", "hi")]);
        var chunks = new List<ChatChunk>();
        await foreach (var chunk in chatClient.StreamAsync(request, ct))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(4, chunks.Count);
        Assert.Equal("Hel", chunks[0].Content);
        Assert.Equal("lo", chunks[1].Content);
        Assert.Equal("thinking...", chunks[2].Reasoning);
        Assert.Equal("stop", chunks[3].FinishReason);
        Assert.NotNull(chunks[3].Usage);
        Assert.Equal(5, chunks[3].Usage!.PromptTokens);
        Assert.Equal(2, chunks[3].Usage!.CompletionTokens);
    }

    [Fact]
    public async Task RequestBody_ContainsStreamOptionsThinkingAndMaxTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        var (chatClient, resolver, _, handler) = Build();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));
        string? sentBody = null;
        handler.Handler = async (request, handlerCt) =>
        {
            // Snapshot the body inside the handler: the router disposes the request (and its
            // JsonContent) once the response has been streamed and consumed.
            sentBody = await request.Content!.ReadAsStringAsync(handlerCt);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        };

        var request = new ChatRequest(
            "chat-model",
            [new ChatMessage("system", "be terse"), new ChatMessage("user", "hi")],
            Temperature: 0.3,
            MaxTokens: 256,
            EnableThinking: true);

        await foreach (var _ in chatClient.StreamAsync(request, ct))
        {
            // Drain; body assertion happens after the request has been sent.
        }

        using var document = JsonDocument.Parse(sentBody!);
        var root = document.RootElement;

        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.True(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.3, root.GetProperty("temperature").GetDouble());
        Assert.Equal("chat-model", root.GetProperty("model").GetString());
        Assert.Equal(2, root.GetProperty("messages").GetArrayLength());
        Assert.Equal("hi", root.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public void AMessageWithImagesIsSentAsTextAndImageParts()
    {
        string json = JsonSerializer.Serialize(RouterChatClient.Content(new ChatMessage("user", "what is this?", ["data:image/png;base64,AQID"])));

        Assert.Equal("""[{"type":"text","text":"what is this?"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AQID"}}]""", json);
        Assert.Equal("""[{"type":"image_url","image_url":{"url":"u"}}]""", JsonSerializer.Serialize(RouterChatClient.Content(new ChatMessage("user", "", ["u"]))));
    }

    [Fact]
    public async Task NotReadyModel_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (chatClient, resolver, _, _) = Build();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Starting));

        var request = new ChatRequest("chat-model", [new ChatMessage("user", "hi")]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in chatClient.StreamAsync(request, ct))
            {
            }
        });
    }

    [Fact]
    public async Task UnknownModel_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var (chatClient, _, _, _) = Build();
        var request = new ChatRequest("does-not-exist", [new ChatMessage("user", "hi")]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in chatClient.StreamAsync(request, ct))
            {
            }
        });
    }

    [Fact]
    public async Task Cancellation_StopsEnumeration()
    {
        var ct = TestContext.Current.CancellationToken;
        var (chatClient, resolver, _, handler) = Build();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        var upstreamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Handler = async (_, upstreamCt) =>
        {
            using var registration = upstreamCt.Register(() => upstreamCancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, upstreamCt);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        var request = new ChatRequest("chat-model", [new ChatMessage("user", "hi")]);
        using var cts = new CancellationTokenSource();

        var enumerationTask = Task.Run(async () =>
        {
            await foreach (var _ in chatClient.StreamAsync(request, cts.Token))
            {
            }
        }, ct);

        await Task.Delay(100, ct);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enumerationTask);
        await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task RouterStats_CountSuccessAndFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var (chatClient, resolver, stats, handler) = Build();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));
        handler.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SseBody, Encoding.UTF8, "text/event-stream"),
        });

        var request = new ChatRequest("chat-model", [new ChatMessage("user", "hi")]);
        await foreach (var _ in chatClient.StreamAsync(request, ct))
        {
        }

        var snapshot = stats.Snapshot();
        var chatStats = Assert.Single(snapshot, s => s.ServedName == "chat-model");
        Assert.Equal(1, chatStats.TotalRequests);
        Assert.Equal(0, chatStats.FailedRequests);
        Assert.Equal(0, chatStats.InFlight);

        handler.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in chatClient.StreamAsync(request, ct))
            {
            }
        });

        snapshot = stats.Snapshot();
        chatStats = Assert.Single(snapshot, s => s.ServedName == "chat-model");
        Assert.Equal(2, chatStats.TotalRequests);
        Assert.Equal(1, chatStats.FailedRequests);
        Assert.Equal(0, chatStats.InFlight);
    }
}
