using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Router.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Router;

/// <summary>
/// MN-110: the <c>/v1</c> precedence matrix of design §6.5 item 2, through the real router (agent path, real
/// <see cref="MaintenanceModeService"/>), for the P1 order (node-scoped maintenance off, or a global window) and the P2
/// order (only node-scoped windows): every combination of 401 / 413 / 400 / 404 / maintenance / node offline / not ready
/// must answer the highest-precedence error. Plus exemptions, <c>/v1/models</c> semantics and the browser chat.
/// </summary>
public sealed class PrecedenceMatrixTests
{
    private const string JobNode = "dgx";
    private const string OtherNode = "pc";
    private const string ApiKey = "secret";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private enum Condition
    {
        Maintenance,
        NoKey,
        TooLarge,
        NoModel,
        Unknown,
        NodeOffline,
        NotReady,
    }

    private static readonly Condition[] P1Order =
        [Condition.Maintenance, Condition.NoKey, Condition.TooLarge, Condition.NoModel, Condition.Unknown, Condition.NodeOffline, Condition.NotReady];

    // Design §6.5 item 2 lists 413 first; the router keeps P1's 401-before-413 in both orders (MN-110 deviation): an
    // unauthenticated caller never gets a body buffered, and the 401/413 order does not depend on maintenance state.
    private static readonly Condition[] P2Order =
        [Condition.NoKey, Condition.TooLarge, Condition.NoModel, Condition.Unknown, Condition.Maintenance, Condition.NodeOffline, Condition.NotReady];

    private static readonly Dictionary<Condition, (HttpStatusCode Status, string Code)> Answers = new()
    {
        [Condition.Maintenance] = (HttpStatusCode.ServiceUnavailable, "maintenance"),
        [Condition.NoKey] = (HttpStatusCode.Unauthorized, "invalid_api_key"),
        [Condition.TooLarge] = (HttpStatusCode.RequestEntityTooLarge, "request_too_large"),
        [Condition.NoModel] = (HttpStatusCode.BadRequest, "model_required"),
        [Condition.Unknown] = (HttpStatusCode.NotFound, "model_not_found"),
        [Condition.NodeOffline] = (HttpStatusCode.ServiceUnavailable, "node_offline"),
        [Condition.NotReady] = (HttpStatusCode.ServiceUnavailable, "model_not_ready"),
    };

    /// <summary>How the window and the switch are set for a matrix run.</summary>
    public enum Mode
    {
        /// <summary>Node-scoped window, <see cref="RouterMaintenanceOptions.NodeScoped"/> off: P1 order.</summary>
        P1SwitchOff,

        /// <summary>Global window, switch on: P1 order (a global window always keeps it).</summary>
        P1GlobalWindow,

        /// <summary>Node-scoped window, switch on: P2 order.</summary>
        P2NodeWindow,
    }

    [Theory]
    [InlineData(Mode.P1SwitchOff)]
    [InlineData(Mode.P1GlobalWindow)]
    [InlineData(Mode.P2NodeWindow)]
    public async Task EveryCombination_AnswersTheHighestPrecedenceError(Mode mode)
    {
        await using var env = await Env.StartAsync(nodeScoped: mode != Mode.P1SwitchOff);
        var order = mode == Mode.P2NodeWindow ? P2Order : P1Order;
        var failures = new List<string>();
        int combinations = 0;

        foreach (var set in Combinations())
        {
            combinations++;
            env.Maintenance.Exit("job-1");
            if (set.Contains(Condition.Maintenance))
            {
                env.Maintenance.Enter(Window("job-1", "qwen", mode == Mode.P1GlobalWindow ? null : JobNode));
            }

            env.Cache.SetNode(JobNode, set.Contains(Condition.NodeOffline) ? NodeState.Offline : NodeState.Online);
            env.Cache.SetRuntime("other", JobNode, set.Contains(Condition.NotReady) ? ModelState.Starting : ModelState.Ready);

            using var request = BuildRequest(set);
            using var response = await env.Client.SendAsync(request, Ct);
            var first = order.FirstOrDefault(set.Contains, (Condition)(-1));
            var (status, code) = Enum.IsDefined(first) ? Answers[first] : (HttpStatusCode.OK, "");
            string actual = await CodeOfAsync(response);
            if (response.StatusCode != status || actual != code)
            {
                failures.Add($"[{string.Join(",", set)}] expected {(int)status} {code}, got {(int)response.StatusCode} {actual}");
            }
        }

        Assert.Equal(64, combinations);
        Assert.True(failures.Count == 0, $"{mode}:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task P2_OnlyTheJobNodesNonExemptModelsAndTheModelUnderTestArePaused()
    {
        await using var env = await Env.StartAsync();
        env.Registry.Add(Model("qwen", "qwen-chat", JobNode, keepServing: true)); // exempt flag never covers the model under test
        env.Maintenance.Enter(Window("job-1", "qwen", JobNode, TimeSpan.FromMinutes(7)));

        using var underTest = await env.PostChatAsync("qwen-chat");
        using var other = await env.PostChatAsync("other-chat");
        using var exempt = await env.PostChatAsync("embed");
        using var remote = await env.PostChatAsync("remote-chat");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, underTest.StatusCode);
        Assert.Equal("maintenance", await CodeOfAsync(underTest));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, other.StatusCode);
        Assert.Equal("maintenance", await CodeOfAsync(other));
        int retryAfter = int.Parse(other.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 400, 421);
        Assert.Contains("on qwen on node dgx; API paused until ~", await other.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, exempt.StatusCode);
        Assert.Equal(HttpStatusCode.OK, remote.StatusCode);
        Assert.Equal(["embed", "remote"], env.Data.Requests.Select(r => r.ModelId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task P2_ExemptionAndPlacementEditsTakeEffectAtOnce_AndExitReopensTheNode()
    {
        await using var env = await Env.StartAsync();
        env.Maintenance.Enter(Window("job-1", "qwen", JobNode));

        using (var paused = await env.PostChatAsync("other-chat"))
        {
            Assert.Equal("maintenance", await CodeOfAsync(paused));
        }

        env.Registry.Add(Model("other", "other-chat", JobNode, keepServing: true));
        using (var exempted = await env.PostChatAsync("other-chat"))
        {
            Assert.Equal(HttpStatusCode.OK, exempted.StatusCode);
        }

        env.Registry.Add(Model("embed", "embed", JobNode, keepServing: false));
        using (var unexempted = await env.PostChatAsync("embed"))
        {
            Assert.Equal("maintenance", await CodeOfAsync(unexempted));
        }

        env.Maintenance.Exit("job-1");
        using var after = await env.PostChatAsync("embed");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task P2_PausedModelIsNotAutoStarted()
    {
        await using var env = await Env.StartAsync(configure: o => o.AutoStartOnRequest = true);
        env.Cache.SetRuntime("other", JobNode, ModelState.Stopped);
        env.Maintenance.Enter(Window("job-1", "qwen", JobNode));

        using var response = await env.PostChatAsync("other-chat");

        Assert.Equal("maintenance", await CodeOfAsync(response));
        Assert.Equal(0, env.Runtime.StartCallCount);
    }

    [Fact]
    public async Task P2_TwoNodeWindows_EachPausesOnlyItsNode()
    {
        await using var env = await Env.StartAsync();
        env.Maintenance.Enter(Window("job-1", "qwen", JobNode));
        env.Maintenance.Enter(Window("job-2", "bench-remote", OtherNode));

        using var remote = await env.PostChatAsync("remote-chat");
        using var exempt = await env.PostChatAsync("embed");

        Assert.Equal("maintenance", await CodeOfAsync(remote));
        Assert.Contains("on bench-remote on node pc", await remote.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, exempt.StatusCode);

        env.Maintenance.Exit("job-2");
        using var reopened = await env.PostChatAsync("remote-chat");
        Assert.Equal(HttpStatusCode.OK, reopened.StatusCode);
    }

    [Fact]
    public async Task GlobalWindow_StillBlocksEverything_BeforeTheApiKey_EvenNextToANodeWindow()
    {
        await using var env = await Env.StartAsync();
        env.Maintenance.Enter(Window("job-1", "qwen", JobNode));
        env.Maintenance.Enter(new MaintenanceState("restore-1", MaintenanceJobKinds.Restore, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5)));

        using var exempt = await env.PostChatAsync("embed");
        using var remoteNoKey = await env.PostChatAsync("remote-chat", key: null);
        using var models = await env.GetModelsAsync(key: null);

        foreach (var response in new[] { exempt, remoteNoKey, models })
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("maintenance", await CodeOfAsync(response));
            Assert.Contains("restoring a backup", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        Assert.Empty(env.Data.Requests);
    }

    [Fact]
    public async Task P2_V1Models_NeverAnswers503_AndLeavesPausedModelsOut()
    {
        await using var env = await Env.StartAsync();
        env.Maintenance.Enter(Window("job-1", "qwen", JobNode));

        using var noKey = await env.GetModelsAsync(key: null);
        using var models = await env.GetModelsAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        Assert.Equal(["embed", "remote-chat"], await ModelIdsAsync(models));

        env.Maintenance.Exit("job-1");
        using var after = await env.GetModelsAsync();
        Assert.Equal(["embed", "other-chat", "qwen-chat", "remote-chat"], await ModelIdsAsync(after));
    }

    [Fact]
    public async Task SwitchOff_NodeWindowIsGlobal_V1ModelsIs503BeforeTheKey()
    {
        await using var env = await Env.StartAsync(nodeScoped: false);
        env.Maintenance.Enter(Window("job-1", "qwen", JobNode));

        using var models = await env.GetModelsAsync(key: null);
        using var remote = await env.PostChatAsync("remote-chat");

        Assert.Equal("maintenance", await CodeOfAsync(models));
        Assert.Equal("maintenance", await CodeOfAsync(remote));
    }

    [Fact]
    public async Task SwitchIsBoundFromConfiguration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MirbadLLM:Router:Maintenance:NodeScoped"] = "true" }).Build());
        services.AddMirbadLLMRouter();
        await using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<IOptions<RouterMaintenanceOptions>>().Value.NodeScoped);

        // Off when unset (P1 keeps today's behaviour, MN-126), and one key for the router and the job service.
        Assert.False(new RouterMaintenanceOptions().NodeScoped);
        var unset = new ServiceCollection();
        unset.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        unset.AddMirbadLLMRouter();
        await using var unsetProvider = unset.BuildServiceProvider();
        Assert.False(unsetProvider.GetRequiredService<IOptions<RouterMaintenanceOptions>>().Value.NodeScoped);
        Assert.Equal("MirbadLLM:Router:Maintenance:NodeScoped", RouterMaintenanceOptions.NodeScopedKey);
        Assert.Equal(RouterMaintenanceOptions.NodeScopedKey, ModelJobService.NodeScopedMaintenanceKey);
    }

    [Fact]
    public async Task ChatClient_FollowsTheSamePrecedence()
    {
        await using var env = await Env.StartAsync();
        var chat = env.App.Services.GetRequiredService<IChatClient>();
        env.Maintenance.Enter(Window("job-1", "qwen", JobNode));

        var chunks = await CollectAsync(chat, "remote-chat");
        var paused = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(chat, "other-chat"));

        Assert.Equal("hi", string.Concat(chunks.Select(c => c.Content)));
        Assert.StartsWith("mirbadllm is running a benchmark on qwen on node dgx; API paused until ~", paused.Message, StringComparison.Ordinal);

        env.Maintenance.Enter(new MaintenanceState("job-g", "benchmark", "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CollectAsync(chat, "remote-chat"));
    }

    // ---- MaintenanceModeService (multi-window, keyed by job id) ----

    [Fact]
    public async Task Service_KeepsOneWindowPerJob_OldestFirst()
    {
        var registry = new FakeModelRegistry()
            .Add(Model("qwen", "qwen-chat", JobNode))
            .Add(Model("embed", "embed", JobNode, keepServing: true))
            .Add(Model("remote", "remote-chat", OtherNode));
        // A throwaway data path: the default (/data) would leave maintenance.json on the test machine (W7 review MN-126).
        using var dir = new MatrixTempDir("mn110-");
        var service = new MaintenanceModeService(Options.Create(new MirbadLLMOptions { DataPath = dir.Path }), registry);
        var first = Window("job-1", "qwen", JobNode);
        var second = Window("job-2", "remote", OtherNode);

        service.Enter(first);
        service.Enter(second);
        service.UpdateEstimate("job-2", second.EstimatedEnd.AddMinutes(5));
        await service.RefreshModelsAsync(Ct);

        Assert.Equal(first, service.Current);
        Assert.Equal(["job-1", "job-2"], service.Active.Select(w => w.JobId));
        Assert.Equal(second.EstimatedEnd.AddMinutes(5), service.ForNode(OtherNode)!.EstimatedEnd);
        Assert.Null(service.ForNode("elsewhere"));
        Assert.Equal("job-1", service.ForModel("qwen")?.JobId);
        Assert.Null(service.ForModel("embed"));
        Assert.Equal("job-2", service.ForModel("remote")?.JobId);
        Assert.Null(service.ForModel("unknown"));
        Assert.Equal(["embed"], MaintenanceModeService.ExemptModelIds(first, await registry.ListAsync(Ct)));

        service.Enter(first with { EstimatedEnd = first.EstimatedEnd.AddHours(1) }); // same job: replaced, not added
        Assert.Equal(2, service.Active.Count);
        service.Exit("job-1");
        Assert.Equal(second.JobId, service.Current?.JobId);
        Assert.Null(service.ForModel("qwen"));

        service.Enter(new MaintenanceState("g", "benchmark", "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Assert.Equal("g", service.ForModel("embed")?.JobId);
        Assert.Equal("g", service.ForNode(JobNode)?.JobId);
        Assert.Empty(MaintenanceModeService.ExemptModelIds(service.ForNode(JobNode)!, await registry.ListAsync(Ct)));
    }

    [Fact]
    public void Service_PersistsOneWindowAsTodaysObject_AndSeveralAsAnArray()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mn110-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new MaintenanceModeService(Options.Create(new MirbadLLMOptions { DataPath = dir }));
            string file = Path.Combine(dir, "maintenance.json");

            service.Enter(Window("job-1", "qwen", null));
            using (var one = JsonDocument.Parse(File.ReadAllText(file)))
            {
                Assert.Equal("job-1", one.RootElement.GetProperty("JobId").GetString());
            }

            service.Enter(Window("job-2", "remote", OtherNode));
            using (var two = JsonDocument.Parse(File.ReadAllText(file)))
            {
                Assert.Equal(2, two.RootElement.GetArrayLength());
            }

            service.Exit("job-1");
            service.Exit("job-2");
            Assert.False(File.Exists(file));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- helpers ----

    private static IEnumerable<HashSet<Condition>> Combinations()
    {
        Condition?[] bodies = [null, Condition.TooLarge, Condition.NoModel, Condition.Unknown];
        foreach (bool noKey in new[] { false, true })
        {
            foreach (var body in bodies)
            {
                foreach (bool job in new[] { false, true })
                {
                    foreach (bool offline in new[] { false, true })
                    {
                        foreach (bool notReady in new[] { false, true })
                        {
                            var set = new HashSet<Condition>();
                            if (noKey) set.Add(Condition.NoKey);
                            if (body is { } b) set.Add(b);
                            if (job) set.Add(Condition.Maintenance);
                            if (offline) set.Add(Condition.NodeOffline);
                            if (notReady) set.Add(Condition.NotReady);
                            yield return set;
                        }
                    }
                }
            }
        }
    }

    private static HttpRequestMessage BuildRequest(HashSet<Condition> set)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        if (!set.Contains(Condition.NoKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        }

        if (set.Contains(Condition.TooLarge))
        {
            // Declared over the 64 MiB limit: the router answers from Content-Length without reading the body.
            var content = new ByteArrayContent("{}"u8.ToArray());
            content.Headers.ContentLength = Protocol.MaxProxyRequestBodyBytes + 1;
            request.Content = content;
        }
        else
        {
            string json = set.Contains(Condition.NoModel) ? """{"prompt":"x"}"""
                : set.Contains(Condition.Unknown) ? """{"model":"nobody"}"""
                : """{"model":"other-chat"}""";
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static MaintenanceState Window(string jobId, string modelId, string? nodeId, TimeSpan? left = null) =>
        new(jobId, "benchmark", modelId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + (left ?? TimeSpan.FromMinutes(1)), nodeId);

    private static ModelDefinition Model(string id, string servedName, string nodeId, bool keepServing = false) => new()
    {
        Id = id,
        ServedName = servedName,
        ModelPath = "/models/" + id,
        Placement = new ModelPlacement(nodeId),
        KeepServingDuringJobs = keepServing,
    };

    private static async Task<string> CodeOfAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return "";
        }

        string text = await response.Content.ReadAsStringAsync(Ct);
        try
        {
            using var body = JsonDocument.Parse(text);
            return body.RootElement.GetProperty("error").GetProperty("code").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "<" + text + ">";
        }
    }

    private static async Task<string[]> ModelIdsAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return [.. body.RootElement.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("id").GetString()!).Order(StringComparer.Ordinal)];
    }

    private static async Task<List<ChatChunk>> CollectAsync(IChatClient chat, string servedName)
    {
        var chunks = new List<ChatChunk>();
        await foreach (var chunk in chat.StreamAsync(new ChatRequest(servedName, [new ChatMessage("user", "hi")]), Ct))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    /// <summary>The master's router on the agent path over in-memory agents: dgx (job node) and pc, four models.</summary>
    private sealed class MatrixTempDir(string prefix) : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private sealed class Env : IAsyncDisposable
    {
        private readonly MatrixTempDir _dataDir = new("mn110-env-");

        public FakeModelRegistry Registry { get; } = new();

        public StubCache Cache { get; } = new();

        public StubData Data { get; } = new();

        public FakeModelRuntime Runtime { get; } = new();

        public MaintenanceModeService Maintenance { get; private set; } = null!;

        public WebApplication App { get; private set; } = null!;

        public HttpClient Client { get; private set; } = null!;

        public static async Task<Env> StartAsync(bool nodeScoped = true, Action<MirbadLLMOptions>? configure = null)
        {
            var env = new Env();
            env.Registry
                .Add(Model("qwen", "qwen-chat", JobNode))
                .Add(Model("other", "other-chat", JobNode))
                .Add(Model("embed", "embed", JobNode, keepServing: true))
                .Add(Model("remote", "remote-chat", OtherNode));
            env.Cache.SetNode(JobNode, NodeState.Online);
            env.Cache.SetNode(OtherNode, NodeState.Online);
            foreach (var (model, node) in new[] { ("qwen", JobNode), ("other", JobNode), ("embed", JobNode), ("remote", OtherNode) })
            {
                env.Cache.SetRuntime(model, node, ModelState.Ready);
            }

            env.Maintenance = new MaintenanceModeService(Options.Create(new MirbadLLMOptions { DataPath = env._dataDir.Path }), env.Registry);

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.AddRouting();
            builder.Services.AddMirbadLLMRouter();
            builder.Services.AddMirbadLLMAgentRouting();
            builder.Services.AddSingleton<IModelRegistry>(env.Registry);
            builder.Services.AddSingleton<INodeStatusCache>(env.Cache);
            builder.Services.AddSingleton<IAgentClientFactory>(new StubFactory(env.Data));
            builder.Services.AddSingleton<IModelRuntime>(env.Runtime);
            builder.Services.AddSingleton<IMaintenanceMode>(env.Maintenance);
            builder.Services.AddSingleton<IApiKeyStore>(new FakeApiKeyStore());
            builder.Services.Configure<MirbadLLMOptions>(o =>
            {
                o.ApiKey = ApiKey;
                configure?.Invoke(o);
            });
            builder.Services.Configure<RouterMaintenanceOptions>(o => o.NodeScoped = nodeScoped);
            env.App = builder.Build();
            env.App.MapMirbadLLMRouter();
            await env.App.StartAsync(Ct);
            env.Client = env.App.GetTestClient();
            return env;
        }

        public Task<HttpResponseMessage> PostChatAsync(string servedName, string? key = ApiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { model = servedName }), Encoding.UTF8, "application/json"),
            };
            if (key is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            return SendAndDisposeRequestAsync(request);
        }

        public Task<HttpResponseMessage> GetModelsAsync(string? key = ApiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
            if (key is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }

            return SendAndDisposeRequestAsync(request);
        }

        private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(HttpRequestMessage request)
        {
            using (request)
            {
                return await Client.SendAsync(request, Ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.DisposeAsync();
            _dataDir.Dispose();
        }
    }

    private sealed class StubCache : INodeStatusCache
    {
        private readonly ConcurrentDictionary<string, NodeLiveStatus> _nodes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, NodeModelRuntime> _runtimes = new(StringComparer.Ordinal);
        private long _version;

        public long Version => Interlocked.Read(ref _version);

        public void SetNode(string nodeId, NodeState state)
        {
            _nodes[nodeId] = new NodeLiveStatus { NodeId = nodeId, NodeUid = "uid-" + nodeId, State = state };
            Interlocked.Increment(ref _version);
        }

        public void SetRuntime(string modelId, string nodeId, ModelState state)
        {
            _runtimes[modelId] = new NodeModelRuntime { ModelId = modelId, NodeId = nodeId, State = state };
            Interlocked.Increment(ref _version);
        }

        public IReadOnlyList<NodeLiveStatus> ListNodes() => [.. _nodes.Values];

        public NodeLiveStatus? GetNode(string nodeId) => _nodes.GetValueOrDefault(nodeId);

        public NodeModelRuntime? GetRuntime(string modelId) =>
            _runtimes.TryGetValue(modelId, out var runtime) && runtime.State != ModelState.Stopped ? runtime : null;

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) =>
            [.. _runtimes.Values.Where(r => nodeId is null || r.NodeId == nodeId)];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>One proxied request: which node and model it reached.</summary>
    internal sealed record Sent(string NodeId, string ModelId);

    /// <summary>In-memory agent data path shared by both nodes: records what reaches an agent and answers an SSE chat.</summary>
    internal sealed class StubData
    {
        public ConcurrentQueue<Sent> Requests { get; } = new();

        public IAgentDataClient For(string nodeId) => new NodeData(this, nodeId);

        private sealed class NodeData(StubData owner, string nodeId) : IAgentDataClient
        {
            public string NodeId => nodeId;

            public HttpMessageInvoker Invoker => throw new NotSupportedException();

            public Uri BuildProxyUri(string modelId, string pathAndQuery) =>
                new($"https://{nodeId}.test/agent/v1/proxy/{modelId}/{pathAndQuery}");

            public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct = default)
            {
                string modelId = request.RequestUri!.Segments[4].TrimEnd('/');
                owner.Requests.Enqueue(new Sent(nodeId, modelId));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
                        Encoding.UTF8, "text/event-stream"),
                });
            }
        }
    }

    private sealed class StubFactory(StubData data) : IAgentClientFactory
    {
        public IAgentClient GetClient(string nodeId)
        {
            if (nodeId is not (JobNode or OtherNode))
            {
                throw new KeyNotFoundException(nodeId);
            }

            var client = DispatchProxy.Create<IAgentClient, DataClientProxy>();
            ((DataClientProxy)(object)client).Data = data.For(nodeId);
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

    /// <summary>An <see cref="IAgentClient"/> whose only working members are <c>Data</c> and <c>NodeId</c> (all the router uses).</summary>
#pragma warning disable CA1852 // DispatchProxy derives from it at run time
    internal class DataClientProxy : DispatchProxy
#pragma warning restore CA1852
    {
        public IAgentDataClient? Data { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Data" => Data,
            "get_NodeId" => Data?.NodeId,
            _ => throw new NotSupportedException($"{targetMethod?.Name} is not used by the router."),
        };
    }
}
