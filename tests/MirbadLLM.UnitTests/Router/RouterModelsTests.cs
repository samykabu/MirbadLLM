using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Router.Fakes;
using System.Net;
using System.Text.Json;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

namespace MirbadLLM.UnitTests.Router;

public sealed class RouterModelsTests
{
    [Fact]
    public async Task GetModels_ListsOnlyReadyModels()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("ready-1", "ready-model", new Uri("http://upstream-ready/"), ModelState.Ready));
        resolver.Set(new ModelEndpoint("starting-1", "starting-model", new Uri("http://upstream-starting/"), ModelState.Starting));
        resolver.Set(new ModelEndpoint("stopped-1", "stopped-model", new Uri("http://upstream-stopped/"), ModelState.Stopped));

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime());
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/v1/models", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal("list", document.RootElement.GetProperty("object").GetString());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        var entry = data[0];
        Assert.Equal("ready-model", entry.GetProperty("id").GetString());
        Assert.Equal("model", entry.GetProperty("object").GetString());
        Assert.Equal("mirbadllm", entry.GetProperty("owned_by").GetString());
    }

    [Fact]
    public async Task GetModels_CarriesContextAndCapabilities_FromTheDefinition()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat", "qwen-coder", new Uri("http://upstream-chat/"), ModelState.Ready));
        resolver.Set(new ModelEndpoint("embed", "bge-m3", new Uri("http://upstream-embed/"), ModelState.Ready));
        resolver.Set(new ModelEndpoint("unknown", "no-definition", new Uri("http://upstream-unknown/"), ModelState.Ready));
        var registry = new FakeModelRegistry()
            .Add(new ModelDefinition
            {
                Id = "chat", ServedName = "qwen-coder", ModelPath = "q", MaxModelLen = 131_072, ToolCallParser = "qwen3_coder",
                Tags = [ModelTags.Chat, ModelTags.Vision],
            })
            .Add(new ModelDefinition
            {
                Id = "embed", ServedName = "bge-m3", ModelPath = "e", Runtime = RuntimeKind.LlamaCpp,
                LlamaCpp = new LlamaCppSettings { ModelFile = "e.gguf", ContextSize = 8192, Embedding = true },
            });

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(),
            configureServices: services => services.AddSingleton<IModelRegistry>(registry));
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/v1/models", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var byId = document.RootElement.GetProperty("data").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetString()!);

        var chat = byId["qwen-coder"];
        Assert.Equal("mirbadllm", chat.GetProperty("owned_by").GetString());
        Assert.Equal(0, chat.GetProperty("created").GetInt64());
        Assert.Equal(131_072, chat.GetProperty("max_model_len").GetInt32());
        var chatCaps = chat.GetProperty("capabilities");
        Assert.True(chatCaps.GetProperty("chat").GetBoolean());
        Assert.False(chatCaps.GetProperty("embeddings").GetBoolean());
        Assert.True(chatCaps.GetProperty("tool_calling").GetBoolean());
        Assert.True(chatCaps.GetProperty("vision").GetBoolean());

        var embed = byId["bge-m3"];
        Assert.Equal(8192, embed.GetProperty("max_model_len").GetInt32());
        var embedCaps = embed.GetProperty("capabilities");
        Assert.False(embedCaps.GetProperty("chat").GetBoolean());
        Assert.True(embedCaps.GetProperty("embeddings").GetBoolean());
        Assert.False(embedCaps.GetProperty("tool_calling").GetBoolean());
        Assert.False(embedCaps.GetProperty("vision").GetBoolean());

        Assert.False(byId["no-definition"].TryGetProperty("capabilities", out _));
    }

    [Fact]
    public async Task GetModels_NoReadyModels_ReturnsEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("stopped-1", "stopped-model", new Uri("http://upstream/"), ModelState.Stopped));

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime());
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/v1/models", ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(0, document.RootElement.GetProperty("data").GetArrayLength());
    }
}
