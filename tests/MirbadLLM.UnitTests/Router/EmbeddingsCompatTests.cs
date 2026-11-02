using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Router.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Router;

/// <summary>
/// MN-217: the router's <c>dimensions</c> emulation for llama.cpp embedding models (design-p3p4 §A.3, D117, D119). The
/// real <c>ProxyHandler</c> runs on the agent path against an in-memory data client that records the forwarded bytes and
/// answers with canned bytes, so "byte for byte" is checked on the wire in both directions.
/// </summary>
public sealed class EmbeddingsCompatTests
{
    private const string NodeId = "win-1";
    private const string LlamaId = "qwen3-embedding-4b";
    private const string LlamaName = "qwen3-embedding-4b-win";
    private const string VllmId = "ss-qwen3-embedding-4b";
    private const string VllmName = "qwen3-embedding-4b";

    private static readonly float[] Native = [0.5f, -0.5f, 0.25f, 0.25f, 0.3f, -0.2f, 0.1f, 0.4f];

    // ---- Emulation ----

    [Fact]
    public async Task FloatVectors_AreTruncatedAndRenormalised_AndTheFieldIsStripped()
    {
        await using var env = await Env.StartAsync();
        env.Data.Answer(HttpStatusCode.OK, FloatAnswer("client-said-this", Native, Native.Reverse().ToArray()));

        const string request = """{"model":"qwen3-embedding-4b-win","input":["a","b"], "dimensions": 4,"encoding_format":"float"}""";
        using var response = await env.PostAsync("/v1/embeddings", request);

        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal("""{"model":"qwen3-embedding-4b-win","input":["a","b"], "encoding_format":"float"}""", env.Data.SingleBody());

        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        AssertUnitPrefix(Native, Floats(data[0].GetProperty("embedding")), 4);
        AssertUnitPrefix([.. Native.Reverse()], Floats(data[1].GetProperty("embedding")), 4);
        Assert.Equal(1, data[1].GetProperty("index").GetInt32());

        // llama-server echoes the request model unvalidated (S1 / C8): the router does not rewrite it; usage is kept.
        Assert.Equal("client-said-this", document.RootElement.GetProperty("model").GetString());
        Assert.Equal(7, document.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(body.Length, (int)response.Content.Headers.ContentLength!.Value);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Base64Vectors_AreTruncatedAndRenormalised()
    {
        await using var env = await Env.StartAsync();
        env.Data.Answer(HttpStatusCode.OK, Base64Answer(Native));

        using var response = await env.PostAsync(
            "/v1/embeddings", """{"model":"qwen3-embedding-4b-win","input":"a","encoding_format":"base64","dimensions":3}""");

        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        Assert.Equal("""{"model":"qwen3-embedding-4b-win","input":"a","encoding_format":"base64"}""", env.Data.SingleBody());

        using var document = JsonDocument.Parse(body);
        var bytes = Convert.FromBase64String(document.RootElement.GetProperty("data")[0].GetProperty("embedding").GetString()!);
        var values = new float[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        }

        AssertUnitPrefix(Native, values, 3);
    }

    [Fact]
    public async Task DimensionsEqualToNative_KeepsAllValues_Normalised()
    {
        await using var env = await Env.StartAsync();
        env.Data.Answer(HttpStatusCode.OK, FloatAnswer(LlamaName, Native));

        using var response = await env.PostAsync("/v1/embeddings", """{"dimensions":8,"model":"qwen3-embedding-4b-win","input":"a"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"model":"qwen3-embedding-4b-win","input":"a"}""", env.Data.SingleBody());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        AssertUnitPrefix(Native, Floats(document.RootElement.GetProperty("data")[0].GetProperty("embedding")), 8);
    }

    [Fact]
    public async Task DimensionsAboveNative_Is400InTheOpenAiShape()
    {
        await using var env = await Env.StartAsync();
        env.Data.Answer(HttpStatusCode.OK, FloatAnswer(LlamaName, Native));

        using var response = await env.PostAsync("/v1/embeddings", """{"model":"qwen3-embedding-4b-win","input":"a","dimensions":9}""");

        await AssertRefusalAsync(response, "(9)", "(8)");
        Assert.Single(env.Data.Requests); // the native size is only known from the answer
        Assert.Equal(1, env.Data.DisposedResponses);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("4.0")]
    [InlineData("1e3")]
    [InlineData("\"1024\"")]
    [InlineData("true")]
    [InlineData("[4]")]
    [InlineData("99999999999")]
    public async Task InvalidDimensions_Is400_BeforeForwarding(string value)
    {
        await using var env = await Env.StartAsync();

        using var response = await env.PostAsync("/v1/embeddings", $$"""{"model":"qwen3-embedding-4b-win","input":"a","dimensions":{{value}}}""");

        await AssertRefusalAsync(response);
        Assert.Empty(env.Data.Requests);
    }

    [Fact]
    public async Task RepeatedDimensions_Is400_BeforeForwarding()
    {
        await using var env = await Env.StartAsync();

        using var response = await env.PostAsync("/v1/embeddings", """{"model":"qwen3-embedding-4b-win","dimensions":4,"input":"a","dimensions":4}""");

        await AssertRefusalAsync(response);
        Assert.Empty(env.Data.Requests);
    }

    [Fact]
    public async Task NonOkAnswer_ToAnEmulatedRequest_PassesThroughUnchanged()
    {
        await using var env = await Env.StartAsync();
        byte[] answer = Encoding.UTF8.GetBytes("""{"error":{"code":400,"message":"input is too large to process","type":"invalid_request_error"}}""");
        env.Data.Answer(HttpStatusCode.BadRequest, answer);

        using var response = await env.PostAsync("/v1/embeddings", """{"model":"qwen3-embedding-4b-win","input":"a","dimensions":4}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(answer, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory]
    [InlineData("""{"object":"list"}""")]
    [InlineData("""{"data":[{"embedding":{"x":1}}]}""")]
    [InlineData("""{"data":[{"embedding":"not base64!"}]}""")]
    [InlineData("""{"data":[{"embedding":[1,"x",3,4,5]}]}""")]
    [InlineData("not json")]
    [InlineData("""{"data":[{"embedding":[1e400,0,0,0,0]}]}""")] // MN-219: a non-finite number threw FormatException (500)
    [InlineData("""{"data":[{"embedding":[1,2,3,4,5],"embedding":[1,2,3,4,5]}]}""")] // MN-219: a duplicate key threw ArgumentException
    public async Task MalformedOkAnswer_Is502UpstreamUnavailable(string answer)
    {
        await using var env = await Env.StartAsync();
        env.Data.Answer(HttpStatusCode.OK, Encoding.UTF8.GetBytes(answer));

        using var response = await env.PostAsync("/v1/embeddings", """{"model":"qwen3-embedding-4b-win","input":"a","dimensions":4}""");

        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadGateway, body);
        using var document = JsonDocument.Parse(body);
        Assert.Equal("upstream_unavailable", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // ---- Byte for byte (D119: only when sent) ----

    /// <summary>OpenViking's path (S2 / C7): no <c>dimensions</c>; request and answer untouched, odd formatting included.</summary>
    [Fact]
    public async Task LlamaCppEmbeddings_WithoutDimensions_AreByteIdenticalBothWays()
    {
        await using var env = await Env.StartAsync();
        byte[] answer = Encoding.UTF8.GetBytes(
            "{ \"model\" : \"whatever\",\n  \"object\":\"list\",\"data\":[{\"embedding\":[0.50000000001, -0.5,1e-3],\"index\":0,\"object\":\"embedding\"}],\"usage\":{\"prompt_tokens\":2,\"total_tokens\":2}}\n");
        env.Data.Answer(HttpStatusCode.OK, answer);

        const string request = "{ \"model\":\"qwen3-embedding-4b-win\" ,\n \"input\": [\"caf\\u00e9\", \"x\"], \"user\":\"ov\" }";
        using var response = await env.PostAsync("/v1/embeddings", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(request), env.Data.SingleBodyBytes());
        Assert.Equal(answer, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task NullDimensions_IsNotCarried_AndPassesThrough()
    {
        await using var env = await Env.StartAsync();
        byte[] answer = FloatAnswer(LlamaName, Native);
        env.Data.Answer(HttpStatusCode.OK, answer);

        const string request = """{"model":"qwen3-embedding-4b-win","input":"a","dimensions":null}""";
        using var response = await env.PostAsync("/v1/embeddings", request);

        Assert.Equal(request, env.Data.SingleBody());
        Assert.Equal(answer, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    /// <summary>The DGX vLLM refuses <c>dimensions</c> for this model (S2 / C7); that 400 reaches the client unchanged.</summary>
    [Fact]
    public async Task VllmModel_WithDimensions_IsForwardedUnchanged_AndItsRefusalRelayedUnchanged()
    {
        await using var env = await Env.StartAsync();
        byte[] refusal = Encoding.UTF8.GetBytes(
            """{"error":{"message":"Model \"qwen3-embedding-4b\" does not support Matryoshka embeddings; dimensions must be unset","type":"BadRequestError","param":null,"code":400}}""");
        env.Data.Answer(HttpStatusCode.BadRequest, refusal);

        const string request = """{"model":"qwen3-embedding-4b","input":"a","dimensions":1024}""";
        using var response = await env.PostAsync("/v1/embeddings", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(request, env.Data.SingleBody());
        Assert.Contains($"/proxy/{VllmId}/v1/embeddings", env.Data.Requests.Single().Uri.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(refusal, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task VllmModel_WithInvalidDimensions_IsStillForwardedUnchanged()
    {
        await using var env = await Env.StartAsync();
        byte[] answer = FloatAnswer(VllmName, Native);
        env.Data.Answer(HttpStatusCode.OK, answer);

        const string request = """{"model":"qwen3-embedding-4b","input":"a","dimensions":"abc"}""";
        using var response = await env.PostAsync("/v1/embeddings", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(request, env.Data.SingleBody());
        Assert.Equal(answer, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory]
    [InlineData("emulate-off")]
    [InlineData("llama-chat")]
    public async Task LlamaCppModelsTheRuleDoesNotApplyTo_PassThrough(string modelId)
    {
        await using var env = await Env.StartAsync();
        byte[] answer = FloatAnswer(modelId, Native);
        env.Data.Answer(HttpStatusCode.OK, answer);

        string request = $$"""{"model":"{{modelId}}","input":"a","dimensions":4}""";
        using var response = await env.PostAsync("/v1/embeddings", request);

        Assert.Equal(request, env.Data.SingleBody());
        Assert.Equal(answer, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/completions")]
    [InlineData("/v1/embeddings/")]
    [InlineData("/v1/embeddings?x=1")] // the query is not part of the path: still the embeddings path
    public async Task OtherPaths_AreUntouched_EvenWithDimensions(string url)
    {
        await using var env = await Env.StartAsync();
        byte[] answer = FloatAnswer(LlamaName, Native);
        env.Data.Answer(HttpStatusCode.OK, answer);

        const string request = """{"model":"qwen3-embedding-4b-win","input":"a","dimensions":4}""";
        using var response = await env.PostAsync(url, request);

        bool emulated = url.StartsWith("/v1/embeddings?", StringComparison.Ordinal);
        byte[] received = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(emulated ? """{"model":"qwen3-embedding-4b-win","input":"a"}""" : request, env.Data.SingleBody());
        Assert.Equal(!emulated, answer.SequenceEqual(received));
    }

    [Fact]
    public async Task StreamedChat_ToTheLlamaCppModel_IsUntouched()
    {
        await using var env = await Env.StartAsync();
        byte[] sse = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n");
        env.Data.Answer(HttpStatusCode.OK, sse, "text/event-stream");

        const string request = """{"model":"qwen3-embedding-4b-win","stream":true,"dimensions":4}""";
        using var response = await env.PostAsync("/v1/chat/completions", request);

        Assert.Equal(request, env.Data.SingleBody());
        Assert.Equal(sse, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task NonJsonOkAnswer_ToAnEmulatedRequest_PassesThrough()
    {
        await using var env = await Env.StartAsync();
        byte[] answer = Encoding.UTF8.GetBytes("plain text");
        env.Data.Answer(HttpStatusCode.OK, answer, "text/plain");

        using var response = await env.PostAsync("/v1/embeddings", """{"model":"qwen3-embedding-4b-win","input":"a","dimensions":4}""");

        Assert.Equal(answer, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    // ---- The request splice and the "carries" rule, seen on the wire ----

    [Theory]
    [InlineData("""{"dimensions":4,"model":"qwen3-embedding-4b-win"}""", """{"model":"qwen3-embedding-4b-win"}""")]
    [InlineData("""{"model":"qwen3-embedding-4b-win","dimensions":4}""", """{"model":"qwen3-embedding-4b-win"}""")]
    [InlineData("""{"model":"qwen3-embedding-4b-win", "dimensions" : 4 , "input":"x"}""", """{"model":"qwen3-embedding-4b-win",  "input":"x"}""")]
    [InlineData("{\n  \"model\": \"qwen3-embedding-4b-win\",\n  \"dimensions\": 4\n}", "{\n  \"model\": \"qwen3-embedding-4b-win\"\n}")]
    [InlineData("""{"input":{"dimensions":7},"model":"qwen3-embedding-4b-win","dimensions":4}""", """{"input":{"dimensions":7},"model":"qwen3-embedding-4b-win"}""")]
    public async Task OnlyTheTopLevelField_IsRemoved_TheRestKeptAsSent(string request, string forwarded)
    {
        await using var env = await Env.StartAsync();
        env.Data.Answer(HttpStatusCode.OK, FloatAnswer(LlamaName, Native));

        using var response = await env.PostAsync("/v1/embeddings", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(forwarded, env.Data.SingleBody());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(4, document.RootElement.GetProperty("data")[0].GetProperty("embedding").GetArrayLength());
    }

    [Theory]
    [InlineData("""{"model":"qwen3-embedding-4b-win","input":{"dimensions":4}}""")] // nested: not the OpenAI field
    [InlineData("""{"model":"qwen3-embedding-4b-win","dimensions":4} trailing""")] // not one JSON object
    public async Task BodiesThatDoNotCarryTheField_PassThrough(string request)
    {
        await using var env = await Env.StartAsync();
        byte[] answer = FloatAnswer(LlamaName, Native);
        env.Data.Answer(HttpStatusCode.OK, answer);

        using var response = await env.PostAsync("/v1/embeddings", request);

        Assert.Equal(request, env.Data.SingleBody());
        Assert.Equal(answer, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    [Fact]
    public async Task OtherMethods_PassThrough()
    {
        await using var env = await Env.StartAsync();
        byte[] answer = FloatAnswer(LlamaName, Native);
        env.Data.Answer(HttpStatusCode.OK, answer);

        const string request = """{"model":"qwen3-embedding-4b-win","input":"a","dimensions":4}""";
        using var message = new HttpRequestMessage(HttpMethod.Put, "/v1/embeddings") { Content = new StringContent(request, Encoding.UTF8, "application/json") };
        using var response = await env.Client.SendAsync(message, Ct);

        Assert.Equal(request, env.Data.SingleBody());
        Assert.Equal(answer, await response.Content.ReadAsByteArrayAsync(Ct));
    }

    // ---- Helpers ----

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertRefusalAsync(HttpResponseMessage response, params string[] messageParts)
    {
        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("dimensions", error.GetProperty("param").GetString());
        Assert.Equal(JsonValueKind.Null, error.GetProperty("code").ValueKind);
        string message = error.GetProperty("message").GetString()!;
        Assert.Contains("dimensions", message, StringComparison.Ordinal);
        foreach (string part in messageParts)
        {
            Assert.Contains(part, message, StringComparison.Ordinal);
        }
    }

    /// <summary>The first <paramref name="n"/> values of <paramref name="native"/>, L2-normalised, equal <paramref name="actual"/>.</summary>
    private static void AssertUnitPrefix(float[] native, float[] actual, int n)
    {
        Assert.Equal(n, actual.Length);
        double norm = Math.Sqrt(native.Take(n).Sum(v => (double)v * v));
        for (int i = 0; i < n; i++)
        {
            Assert.Equal(native[i] / norm, actual[i], 5);
        }

        Assert.Equal(1.0, Math.Sqrt(actual.Sum(v => (double)v * v)), 5);
    }

    private static float[] Floats(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetSingle())];

    private static byte[] FloatAnswer(string model, params float[][] vectors) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        model,
        @object = "list",
        data = vectors.Select((v, i) => new { embedding = v, index = i, @object = "embedding" }),
        usage = new { prompt_tokens = 7, total_tokens = 7 },
    });

    private static byte[] Base64Answer(float[] vector)
    {
        var bytes = new byte[vector.Length * 4];
        for (int i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), vector[i]);
        }

        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = LlamaName,
            @object = "list",
            data = new[] { new { embedding = Convert.ToBase64String(bytes), index = 0, @object = "embedding" } },
        });
    }

    private static ModelDefinition Llama(string id, string servedName, bool embedding, bool? emulate = null) => new()
    {
        Id = id,
        ServedName = servedName,
        ModelPath = "/models/" + id,
        Runtime = RuntimeKind.LlamaCpp,
        LlamaCpp = new LlamaCppSettings { ModelFile = id + "/" + id + ".gguf", Embedding = embedding, EmulateDimensions = emulate },
        Placement = new ModelPlacement(NodeId),
    };

    private sealed class Env : IAsyncDisposable
    {
        public FakeModelRegistry Registry { get; } = new();

        public StubCache Cache { get; } = new();

        public StubDataClient Data { get; } = new();

        public WebApplication App { get; private set; } = null!;

        public HttpClient Client { get; private set; } = null!;

        public static async Task<Env> StartAsync()
        {
            var env = new Env();
            env.Registry
                .Add(Llama(LlamaId, LlamaName, embedding: true))
                .Add(Llama("emulate-off", "emulate-off", embedding: true, emulate: false))
                .Add(Llama("llama-chat", "llama-chat", embedding: false))
                .Add(new ModelDefinition { Id = VllmId, ServedName = VllmName, ModelPath = "/models/q", Placement = new ModelPlacement(NodeId) });
            foreach (var model in await env.Registry.ListAsync(Ct))
            {
                env.Cache.Runtimes[model.Id] = new NodeModelRuntime { ModelId = model.Id, NodeId = NodeId, State = ModelState.Ready };
            }

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddRouting();
            builder.Services.AddMirbadLLMRouter();
            builder.Services.AddMirbadLLMAgentRouting();
            builder.Services.AddSingleton<IModelRegistry>(env.Registry);
            builder.Services.AddSingleton<INodeStatusCache>(env.Cache);
            builder.Services.AddSingleton<IAgentClientFactory>(new StubClientFactory(env.Data));
            builder.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
            builder.Services.AddSingleton<IApiKeyStore>(new FakeApiKeyStore());
            env.App = builder.Build();
            env.App.MapMirbadLLMRouter();
            await env.App.StartAsync(Ct);
            env.Client = env.App.GetTestClient();
            return env;
        }

        public async Task<HttpResponseMessage> PostAsync(string url, string json)
        {
            using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(json));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return await Client.PostAsync(url, content, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
        }
    }

    private sealed class StubCache : INodeStatusCache
    {
        public ConcurrentDictionary<string, NodeModelRuntime> Runtimes { get; } = new(StringComparer.Ordinal);

        public long Version => 1;

        public IReadOnlyList<NodeLiveStatus> ListNodes() => [Node];

        public NodeLiveStatus? GetNode(string nodeId) => nodeId == NodeId ? Node : null;

        public NodeModelRuntime? GetRuntime(string modelId) => Runtimes.GetValueOrDefault(modelId);

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) => [.. Runtimes.Values];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;

        private static NodeLiveStatus Node => new() { NodeId = NodeId, NodeUid = "uid-" + NodeId, State = NodeState.Online };
    }

    private sealed record SentRequest(Uri Uri, byte[] Body);

    /// <summary>In-memory <see cref="IAgentDataClient"/>: records the forwarded bytes, answers with canned bytes.</summary>
    private sealed class StubDataClient : IAgentDataClient
    {
        private int _disposed;
        private Func<HttpResponseMessage> _answer = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };

        public ConcurrentQueue<SentRequest> Requests { get; } = new();

        public int DisposedResponses => Volatile.Read(ref _disposed);

        public string NodeId => EmbeddingsCompatTests.NodeId;

        public HttpMessageInvoker Invoker => throw new NotSupportedException();

        public void Answer(HttpStatusCode status, byte[] body, string mediaType = "application/json") => _answer = () =>
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(status) { Content = new TrackingContent(content, () => Interlocked.Increment(ref _disposed)) };
        };

        public byte[] SingleBodyBytes() => Assert.Single(Requests).Body;

        public string SingleBody() => Encoding.UTF8.GetString(SingleBodyBytes());

        public Uri BuildProxyUri(string modelId, string pathAndQuery) => new($"https://agent.test/agent/v1/proxy/{modelId}/{pathAndQuery}");

        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Enqueue(new SentRequest(request.RequestUri!, body));
            return _answer();
        }
    }

    private sealed class TrackingContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly Action _onDispose;

        public TrackingContent(HttpContent inner, Action onDispose)
        {
            _inner = inner;
            _onDispose = onDispose;
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => _inner.CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _onDispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class StubClientFactory(StubDataClient data) : IAgentClientFactory
    {
        public IAgentClient GetClient(string nodeId)
        {
            if (nodeId != NodeId)
            {
                throw new KeyNotFoundException(nodeId);
            }

            var client = DispatchProxy.Create<IAgentClient, DataOnlyClient>();
            ((DataOnlyClient)(object)client).Data = data;
            return client;
        }

        public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

        public void RebuildHandlers(string nodeId)
        {
        }

        public void SetNegotiatedProtocol(string nodeId, int version)
        {
        }
    }

#pragma warning disable CA1852 // DispatchProxy derives from it at run time
    internal class DataOnlyClient : DispatchProxy
#pragma warning restore CA1852
    {
        public IAgentDataClient? Data { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Data" => Data,
            "get_NodeId" => NodeId,
            _ => throw new NotSupportedException($"{targetMethod?.Name} is not used by the router."),
        };
    }
}
