using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.UnitTests.Auth;
using MirbadLLM.UnitTests.Benchmarking;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707, xUnit1051

public sealed class McpEndpointTests : IDisposable
{
    private readonly AuthWebFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Missing_Wrong_AndNonMcpCredentials_Return401()
    {
        using var client = Client();
        using var missing = await Post(client, Initialize(), key: null);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var wrong = await Post(client, Initialize(), "ssk_not-a-key");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var inference = await Keys().CreateAsync("inference", ApiKeyScope.Inference);
        using var isolated = await Post(client, Initialize(), inference.FullKey);
        Assert.Equal(HttpStatusCode.Unauthorized, isolated.StatusCode);

        var metrics = await Keys().CreateAsync("metrics", ApiKeyScope.Metrics);
        using var metricsIsolated = await Post(client, Initialize(), metrics.FullKey);
        Assert.Equal(HttpStatusCode.Unauthorized, metricsIsolated.StatusCode);
    }

    [Fact]
    public async Task ReadAndOperatorKeys_InitializeAndListTools_OverStatelessHttp()
    {
        using var client = Client();
        var read = await Keys().CreateAsync("reader", ApiKeyScope.McpRead);
        var op = await Keys().CreateAsync("operator", ApiKeyScope.McpOperator);

        using var initialized = await Post(client, Initialize(), read.FullKey);
        Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        Assert.False(initialized.Headers.Contains("Mcp-Session-Id"));

        using var listed = await Post(client, Request(2, "tools/list", new { }), op.FullKey);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        string json = await listed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("mirbad_get_fleet_health", json, StringComparison.Ordinal);
        Assert.Contains("mirbad_deploy_model", json, StringComparison.Ordinal);
        // Access > MCP documents the same tools the endpoint publishes.
        Assert.All(MirbadLLM.Web.Mcp.McpToolCatalog.All, tool => Assert.Contains(tool.Name, json, StringComparison.Ordinal));
        // A boolean subschema here makes Claude Code reject the whole tool list.
        Assert.DoesNotContain("\"output\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"outputSchema\":{\"type\":\"object\"}", json, StringComparison.Ordinal);

        using var result = JsonDocument.Parse(json[(json.IndexOf("data: ", StringComparison.Ordinal) + 6)..]);
        foreach (JsonElement tool in result.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            foreach (JsonProperty argument in tool.GetProperty("inputSchema").GetProperty("properties").EnumerateObject())
            {
                Assert.Equal(JsonValueKind.Object, argument.Value.ValueKind);
            }
        }
    }

    [Fact]
    public async Task ReadKey_MutationReturnsToolError_WithoutMutation_AndOperatorCanCallReadTool()
    {
        using var client = Client();
        var read = await Keys().CreateAsync("reader", ApiKeyScope.McpRead);
        var op = await Keys().CreateAsync("operator", ApiKeyScope.McpOperator);
        var registry = _factory.Services.GetRequiredService<IModelRegistry>();
        int before = (await registry.ListAsync()).Count;

        using var denied = await Post(client, Request(3, "tools/call", new
        {
            name = "mirbad_delete_model",
            arguments = new { modelId = "does-not-exist" },
        }), read.FullKey);
        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        string deniedJson = await denied.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("isError", deniedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Operator", deniedJson, StringComparison.Ordinal);
        Assert.Equal(before, (await registry.ListAsync()).Count);

        using var allowed = await Post(client, Request(4, "tools/call", new
        {
            name = "mirbad_list_models",
            arguments = new { offset = 0, limit = 10 },
        }), op.FullKey);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        string allowedJson = await allowed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"isError\":true", allowedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("structuredContent", allowedJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NodeThatStopsAnswering_IsReported_NotAToolError()
    {
        var cache = new RemoteJobTests.FakeNodeCache(new NodeLiveStatus { NodeId = "dgx", NodeUid = "uid", State = NodeState.Online });
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<INodeStatusCache>(cache)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var read = await factory.Services.GetRequiredService<IApiKeyStore>().CreateAsync("reader", ApiKeyScope.McpRead);

        // No client yet: the background refresh is off here, as it is for a node waiting for approval.
        string noClient = await CallTool(client, read.FullKey, "mirbad_list_downloads", new { });
        Assert.DoesNotContain("\"isError\":true", noClient, StringComparison.Ordinal);
        Assert.Contains("\"available\":false", noClient, StringComparison.Ordinal);
        await ((AgentClientFactory)factory.Services.GetRequiredService<IAgentClientFactory>()).RefreshAsync(TestContext.Current.CancellationToken);

        string healthy = await CallTool(client, read.FullKey, "mirbad_get_fleet_health", new { });
        Assert.Contains("\"status\":\"healthy\"", healthy, StringComparison.Ordinal);
        string listed = await CallTool(client, read.FullKey, "mirbad_list_downloads", new { });
        Assert.Contains("\"available\":true", listed, StringComparison.Ordinal);

        await _factory.StopAgentAsync();

        string degraded = await CallTool(client, read.FullKey, "mirbad_get_fleet_health", new { });
        Assert.DoesNotContain("\"isError\":true", degraded, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"degraded\"", degraded, StringComparison.Ordinal);
        Assert.Contains("\"unavailableNodes\":[\"dgx\"]", degraded, StringComparison.Ordinal);

        string unavailable = await CallTool(client, read.FullKey, "mirbad_list_downloads", new { });
        Assert.DoesNotContain("\"isError\":true", unavailable, StringComparison.Ordinal);
        Assert.Contains("\"available\":false", unavailable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FleetHealth_IsNotHealthy_WithNoKnownNodes()
    {
        using var client = Client();
        var read = await Keys().CreateAsync("reader", ApiKeyScope.McpRead);
        string health = await CallTool(client, read.FullKey, "mirbad_get_fleet_health", new { });
        Assert.Contains("\"status\":\"degraded\"", health, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FleetHealth_ReportsAPendingNode_WithoutFailing()
    {
        var cache = new RemoteJobTests.FakeNodeCache(new NodeLiveStatus { NodeId = "dgx", NodeUid = "uid", State = NodeState.PendingApproval });
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<INodeStatusCache>(cache)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var read = await factory.Services.GetRequiredService<IApiKeyStore>().CreateAsync("reader", ApiKeyScope.McpRead);
        string health = await CallTool(client, read.FullKey, "mirbad_get_fleet_health", new { });
        Assert.Contains("\"status\":\"degraded\"", health, StringComparison.Ordinal);
        Assert.Contains("\"unavailableNodes\":[\"dgx\"]", health, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_OnLlamaCpp_UsesTheNodesDelivery_AndDownloadsOnlyTheGguf()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(LlamaCppProcess));

        string result = await CallTool(client, key, "mirbad_deploy_model", new
        {
            modelId = "small-gguf", servedName = "small", repoId = "org/small-GGUF", folder = "small-gguf", nodeId = "dgx",
            runtime = "llamacpp", modelFile = "small-q8.gguf", kvCacheDtype = "q8_0", memoryBudgetGiB = 4.5,
        });

        Assert.DoesNotContain("\"isError\":true", result, StringComparison.Ordinal);
        var model = await Models().GetAsync("small-gguf");
        Assert.Equal(RuntimeKind.LlamaCpp, model!.Runtime);
        Assert.Equal("small-gguf/small-q8.gguf", model.LlamaCpp!.ModelFile);
        Assert.Equal(LaunchDelivery.Process, model.LlamaCpp.Delivery);
        Assert.Equal("q8_0", model.LlamaCpp.CacheTypeK);
        Assert.Equal("q8_0", model.LlamaCpp.CacheTypeV);
        Assert.Equal(4.5, model.MemoryBudgetGiB);
        Assert.Equal(["small-q8.gguf"], Assert.Single(model.Sources).AllowPatterns);
    }

    [Fact]
    public async Task Deploy_OnVllm_SetsKvCacheAndADraftModel_AndDownloadsTheDrafter()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(Vllm));

        string result = await CallTool(client, key, "mirbad_deploy_model", new
        {
            modelId = "fast", servedName = "fast", repoId = "org/fast", folder = "fast", nodeId = "dgx",
            kvCacheDtype = "auto", enablePrefixCaching = false,
            speculativeMethod = "dflash", speculativeTokens = 7, draftFolder = "fast-drafter", draftRepoId = "org/fast-drafter",
        });

        Assert.DoesNotContain("\"isError\":true", result, StringComparison.Ordinal);
        Assert.Contains("\"draftDownload\":{", result, StringComparison.Ordinal);
        var model = await Models().GetAsync("fast");
        Assert.Equal(RuntimeKind.Vllm, model!.Runtime);
        Assert.Equal("auto", model.KvCacheDtype);
        Assert.False(model.EnablePrefixCaching);
        Assert.Equal("dflash", model.Speculative!.Method);
        Assert.Equal(7, model.Speculative.NumSpeculativeTokens);
        Assert.Equal("/models/fast-drafter", model.Speculative.DraftModel);
        Assert.Equal(["org/fast", "org/fast-drafter"], model.Sources.Select(s => s.RepoId));
    }

    [Fact]
    public async Task Deploy_OnADiscreteNode_MapsGpuIndicesToUuids()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(Vllm) with
        {
            Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
            Features = [AgentFeatures.GpuSelectionUuids],
            Gpus = [new GpuCapability { Index = 0, Uuid = "GPU-A", Name = "RTX" }],
        });

        string refused = await CallTool(client, key, "mirbad_deploy_model", new { modelId = "no-gpu", servedName = "x", repoId = "org/x", folder = "x", nodeId = "dgx" });
        Assert.Contains("\"isError\":true", refused, StringComparison.Ordinal);
        Assert.Contains("select at least one GPU", refused, StringComparison.Ordinal);

        await CallTool(client, key, "mirbad_deploy_model", new { modelId = "on-gpu", servedName = "x", repoId = "org/x", folder = "x", nodeId = "dgx", gpuIndices = FirstGpu });
        Assert.Equal(["GPU-A"], (await Models().GetAsync("on-gpu"))!.Placement!.GpuUuids);
    }

    [Fact]
    public async Task Deploy_AcceptsAnImageNotOnTheNode_AndFillsTheDigestOfOneThatIs()
    {
        using var client = Client();
        const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        string key = await NodeReporting(Capabilities(new RuntimeCapability { Kind = RuntimeKind.Nim, Delivery = LaunchDelivery.Container, Available = true }) with
        {
            Images = [new ImageInfo { Ref = "nim:on-node", Digest = Digest }],
        });

        string elsewhere = await CallTool(client, key, "mirbad_deploy_model", new
        {
            modelId = "from-registry", servedName = "x", repoId = "org/x", folder = "x", nodeId = "dgx", runtime = "nim", image = "registry.example/nim:1", imageDigest = Digest,
        });
        Assert.DoesNotContain("\"isError\":true", elsewhere, StringComparison.Ordinal);
        Assert.Contains("planningNotes", elsewhere, StringComparison.Ordinal);
        Assert.Equal("registry.example/nim:1", (await Models().GetAsync("from-registry"))!.Container!.Image);

        await CallTool(client, key, "mirbad_deploy_model", new { modelId = "on-node", servedName = "y", repoId = "org/y", folder = "y", nodeId = "dgx", runtime = "nim", image = "nim:on-node" });
        Assert.Equal(Digest, (await Models().GetAsync("on-node"))!.Container!.ImageDigest);

        string badDigest = await CallTool(client, key, "mirbad_deploy_model", new { modelId = "bad", servedName = "z", repoId = "org/z", folder = "z", nodeId = "dgx", runtime = "nim", image = "nim:1", imageDigest = "latest" });
        Assert.Contains("imageDigest must be", badDigest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanningLimits_ReachTheCallingModel_OnConnectAndInTheDeployAnswer()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(Vllm));

        using var connected = await Post(client, Initialize(), key);
        string instructions = await connected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("speculative decoding only on vllm and mlx", instructions, StringComparison.Ordinal);
        Assert.Contains("depends on the vLLM image and the drafter", instructions, StringComparison.Ordinal);

        string plain = await CallTool(client, key, "mirbad_deploy_model", new { modelId = "plain", servedName = "p", repoId = "org/p", folder = "p", nodeId = "dgx" });
        Assert.Contains("\"planningNotes\":[]", plain, StringComparison.Ordinal);

        string speculative = await CallTool(client, key, "mirbad_deploy_model", new { modelId = "spec", servedName = "s", repoId = "org/s", folder = "s", nodeId = "dgx", speculativeMethod = "ngram", speculativeTokens = 2 });
        Assert.Contains("saved but not proven", speculative, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sglang", null, null, "cannot run SGLang")]
    [InlineData("sglang", "bad image", null, "container image reference")]
    [InlineData("sglang", null, "fp8", "does not use kvCacheDtype")]
    [InlineData("llamacpp", null, null, "needs modelFile")]
    [InlineData("mlx", null, "fp8", "4 or 8")]
    public async Task Deploy_RefusesWhatTheNodeOrRuntimeCannotDo_AndSavesNothing(string runtime, string? image, string? kv, string message)
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(Vllm) with { Images = [new ImageInfo { Ref = "sglang:1" }] });

        string result = await CallTool(client, key, "mirbad_deploy_model", new
        {
            modelId = "refused", servedName = "x", repoId = "org/x", folder = "x", nodeId = "dgx", runtime, kvCacheDtype = kv,
            image = image ?? (runtime == "sglang" && kv is null ? "sglang:1" : null),
        });

        Assert.Contains("\"isError\":true", result, StringComparison.Ordinal);
        Assert.Contains(message, result, StringComparison.Ordinal);
        Assert.Null(await Models().GetAsync("refused"));
    }

    [Fact]
    public async Task ListNodeRuntimes_ReturnsWhatEachNodeReports()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(LlamaCppProcess) with { Images = [new ImageInfo { Ref = "sglang:1" }] });

        string listed = await CallTool(client, key, "mirbad_list_node_runtimes", new { nodeId = "dgx" });
        Assert.Contains("\"kind\":\"llamacpp\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"delivery\":\"process\"", listed, StringComparison.Ordinal);
        Assert.Contains("\"ref\":\"sglang:1\"", listed, StringComparison.Ordinal);

        string unknown = await CallTool(client, key, "mirbad_list_node_runtimes", new { nodeId = "nope" });
        Assert.Contains("\"isError\":true", unknown, StringComparison.Ordinal);
    }

    private static readonly int[] FirstGpu = [0];
    private static readonly RuntimeCapability Vllm = new() { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = true };
    private static readonly RuntimeCapability LlamaCppProcess = new() { Kind = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Process, Available = true };

    private static NodeCapabilities Capabilities(RuntimeCapability runtime) => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "1.0.0",
        Os = new NodeOsInfo("linux", "Linux"),
        Arch = "arm64",
        Memory = new NodeMemoryInfo(MemoryModel.Unified, 128),
        Runtimes = [runtime],
    };

    /// <summary>Gives the node the report and a client (the background refresh is off here); returns an Operator key.</summary>
    private async Task<string> NodeReporting(NodeCapabilities capabilities)
    {
        var key = await Keys().CreateAsync("operator", ApiKeyScope.McpOperator);
        var registry = _factory.Services.GetRequiredService<INodeRegistry>();
        await registry.UpsertAsync((await registry.GetAsync("dgx"))! with { LastCapabilities = capabilities });
        await ((AgentClientFactory)_factory.Services.GetRequiredService<IAgentClientFactory>()).RefreshAsync(TestContext.Current.CancellationToken);
        return key.FullKey;
    }

    private IModelRegistry Models() => _factory.Services.GetRequiredService<IModelRegistry>();

    private static async Task<string> CallTool(HttpClient client, string key, string name, object arguments)
    {
        using var response = await Post(client, Request(5, "tools/call", new { name, arguments }), key);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private IApiKeyStore Keys()
    {
        _ = _factory.CreateClient();
        return _factory.Services.GetRequiredService<IApiKeyStore>();
    }

    private HttpClient Client() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpResponseMessage> Post(HttpClient client, string json, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static string Initialize() => Request(1, "initialize", new
    {
        protocolVersion = "2025-11-25",
        capabilities = new { },
        clientInfo = new { name = "MirbadLLM.Tests", version = "1.0" },
    });

    private static string Request(int id, string method, object parameters) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0", id, method, @params = parameters,
    });
}
