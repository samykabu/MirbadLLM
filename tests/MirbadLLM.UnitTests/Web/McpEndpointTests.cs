using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
    public async Task ChangingAKeysScopes_AppliesToMcpOnItsNextRequest()
    {
        using var client = Client();
        var key = await Keys().CreateAsync("one-key", ApiKeyScope.Inference);
        using (var noMcp = await Post(client, Initialize(), key.FullKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noMcp.StatusCode);
        }

        await Keys().UpdateScopesAsync(key.Record.Id, [ApiKeyScope.Inference, ApiKeyScope.McpRead]);
        Assert.Contains("Operator scope", await CallTool(client, key.FullKey, "mirbad_delete_model", new { modelId = "nope" }), StringComparison.Ordinal);

        // MCP Operator wins over MCP Read when a key holds both, whichever is stored first.
        await Keys().UpdateScopesAsync(key.Record.Id, [ApiKeyScope.McpRead, ApiKeyScope.McpOperator]);
        Assert.DoesNotContain("Operator scope", await CallTool(client, key.FullKey, "mirbad_delete_model", new { modelId = "nope" }), StringComparison.Ordinal);

        await Keys().UpdateScopesAsync(key.Record.Id, [ApiKeyScope.McpRead]);
        Assert.Contains("Operator scope", await CallTool(client, key.FullKey, "mirbad_delete_model", new { modelId = "nope" }), StringComparison.Ordinal);

        await Keys().UpdateScopesAsync(key.Record.Id, [ApiKeyScope.Metrics]);
        using var removed = await Post(client, Initialize(), key.FullKey);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
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
            modelId = "from-registry", servedName = "x", nodeId = "dgx", runtime = "nim", image = "registry.example/nim:1", imageDigest = Digest,
        });
        Assert.DoesNotContain("\"isError\":true", elsewhere, StringComparison.Ordinal);
        Assert.Contains("planningNotes", elsewhere, StringComparison.Ordinal);
        Assert.Equal("registry.example/nim:1", (await Models().GetAsync("from-registry"))!.Container!.Image);

        await CallTool(client, key, "mirbad_deploy_model", new { modelId = "on-node", servedName = "y", nodeId = "dgx", runtime = "nim", image = "nim:on-node" });
        Assert.Equal(Digest, (await Models().GetAsync("on-node"))!.Container!.ImageDigest);

        string badDigest = await CallTool(client, key, "mirbad_deploy_model", new { modelId = "bad", servedName = "z", nodeId = "dgx", runtime = "nim", image = "nim:1", imageDigest = "latest" });
        Assert.Contains("imageDigest must be", badDigest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deploy_OnSglang_SavesSpeculativeDecoding_AndNeedsAnAlgorithm()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(new RuntimeCapability { Kind = RuntimeKind.Sglang, Delivery = LaunchDelivery.Container, Available = true }));

        string saved = await CallTool(client, key, "mirbad_deploy_model", new
        {
            modelId = "sg", servedName = "sg", repoId = "org/sg", folder = "sg", nodeId = "dgx", runtime = "sglang", image = "sglang:1",
            speculativeMethod = "DFLASH", speculativeTokens = 16, draftFolder = "sg-dflash", draftRepoId = "org/sg-dflash",
        });
        Assert.DoesNotContain("\"isError\":true", saved, StringComparison.Ordinal);
        Assert.Contains("saved but not proven", saved, StringComparison.Ordinal);
        var model = await Models().GetAsync("sg");
        Assert.Equal("DFLASH", model!.Speculative!.Method);
        Assert.Equal("/models/sg-dflash", model.Speculative.DraftModel);

        string refused = await CallTool(client, key, "mirbad_deploy_model", new
        {
            modelId = "no-algo", servedName = "x", repoId = "org/x", folder = "x", nodeId = "dgx", runtime = "sglang", image = "sglang:1", speculativeTokens = 16, draftFolder = "d",
        });
        Assert.Contains("needs an algorithm", refused, StringComparison.Ordinal);
        Assert.Null(await Models().GetAsync("no-algo"));
    }

    [Fact]
    public async Task Deploy_StoresExtraArgs_WhereEachRuntimeReadsThem_AndRefusesReservedFlags()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(Vllm) with
        {
            Runtimes =
            [
                Vllm,
                LlamaCppProcess,
                new RuntimeCapability { Kind = RuntimeKind.Mlx, Delivery = LaunchDelivery.Process, Available = true },
                new RuntimeCapability { Kind = RuntimeKind.Sglang, Delivery = LaunchDelivery.Container, Available = true },
                new RuntimeCapability { Kind = RuntimeKind.Nim, Delivery = LaunchDelivery.Container, Available = true },
            ],
        });
        string[] args = ["--some-flag", "2"];
        const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

        await Deploy("a-vllm", "vllm");
        await Deploy("a-sglang", "sglang", image: "sglang:1");
        await Deploy("a-nim", "nim", image: "nim:1", imageDigest: Digest);
        await Deploy("a-llama", "llamacpp", modelFile: "m.gguf");
        Assert.Equal(args, (await Models().GetAsync("a-vllm"))!.ExtraArgs);
        Assert.Equal(args, (await Models().GetAsync("a-sglang"))!.Container!.ExtraArgs);
        Assert.Equal(args, (await Models().GetAsync("a-nim"))!.Container!.ExtraArgs);
        Assert.Equal(args, (await Models().GetAsync("a-llama"))!.LlamaCpp!.ExtraArgs);

        // MLX accepts only a fixed list of flags (MlxFlags.AllowedInExtraArgs).
        args = ["--temp", "0.5"];
        await Deploy("a-mlx", "mlx");
        Assert.Equal(args, (await Models().GetAsync("a-mlx"))!.Mlx!.ExtraArgs);

        string reserved = await CallTool(client, key, "mirbad_deploy_model", new
        {
            modelId = "reserved", servedName = "r", repoId = "org/r", folder = "r", nodeId = "dgx", runtime = "sglang", image = "sglang:1", extraArgs = ReservedPort,
        });
        Assert.Contains("\"isError\":true", reserved, StringComparison.Ordinal);
        Assert.Contains("--port", reserved, StringComparison.Ordinal);
        Assert.Null(await Models().GetAsync("reserved"));

        async Task Deploy(string id, string runtime, string? image = null, string? imageDigest = null, string? modelFile = null)
        {
            string result = await CallTool(client, key, "mirbad_deploy_model", new
            {
                modelId = id, servedName = id, repoId = runtime == "nim" ? null : "org/" + id, folder = runtime == "nim" ? null : id, nodeId = "dgx", runtime, image, imageDigest, modelFile, extraArgs = args,
            });
            Assert.DoesNotContain("\"isError\":true", result, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PlanningLimits_ReachTheCallingModel_OnConnectAndInTheDeployAnswer()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(Vllm));

        using var connected = await Post(client, Initialize(), key);
        string instructions = await connected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("speculative decoding on vllm, sglang and mlx", instructions, StringComparison.Ordinal);
        Assert.Contains("depends on the runtime's image and the drafter", instructions.Replace("\\u0027", "'", StringComparison.Ordinal), StringComparison.Ordinal);

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

    [Fact]
    public async Task Update_SavesOnlyWhatIsNamed_WithoutRestarting_AndListsTheChange()
    {
        using var client = Client();
        const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        string key = await NodeReporting(Capabilities(new RuntimeCapability { Kind = RuntimeKind.Sglang, Delivery = LaunchDelivery.Container, Available = true }));
        await CallTool(client, key, "mirbad_deploy_model", new
        {
            modelId = "sg", servedName = "sg", repoId = "org/sg", folder = "sg", nodeId = "dgx", runtime = "sglang", image = "sglang:1", imageDigest = Digest, memoryBudgetGiB = 48,
            speculativeMethod = "DFLASH", speculativeTokens = 16, draftFolder = "sg-dflash",
        });

        string updated = await CallTool(client, key, "mirbad_update_model", new { modelId = "sg", extraArgs = Parsers, speculativeTokens = 8 });

        Assert.DoesNotContain("\"isError\":true", updated, StringComparison.Ordinal);
        Assert.Contains("Container extra args: none \\u2192 --tool-call-parser qwen3_coder", updated, StringComparison.Ordinal);
        Assert.Contains("mirbad_stop_model", updated, StringComparison.Ordinal);
        var model = await Models().GetAsync("sg");
        Assert.Equal(Parsers, model!.Container!.ExtraArgs);
        Assert.Equal(Digest, model.Container.ImageDigest);
        Assert.Equal(48, model.MemoryBudgetGiB);
        Assert.Equal("DFLASH", model.Speculative!.Method);
        Assert.Equal(8, model.Speculative.NumSpeculativeTokens);
        Assert.Equal("/models/sg-dflash", model.Speculative.DraftModel);
        Assert.Equal(ModelState.Stopped, (await _factory.Services.GetRequiredService<IModelRuntime>().GetStatusAsync("sg")).State);

        string same = await CallTool(client, key, "mirbad_update_model", new { modelId = "sg" });
        Assert.Contains("Nothing changed.", same, StringComparison.Ordinal);

        // A digest belongs to one image, so a new image doesn't keep the old one's.
        await CallTool(client, key, "mirbad_update_model", new { modelId = "sg", image = "sglang:2", extraArgs = Array.Empty<string>(), speculativeOff = true });
        model = await Models().GetAsync("sg");
        Assert.Equal("sglang:2", model!.Container!.Image);
        Assert.Null(model.Container.ImageDigest);
        Assert.Empty(model.Container.ExtraArgs);
        Assert.Null(model.Speculative);
    }

    [Fact]
    public async Task DeployAndUpdate_TakeTheOnDemandSettings_WhichNeedNoRestart()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(Vllm));
        await CallTool(client, key, "mirbad_deploy_model", new
        {
            modelId = "swap", servedName = "swap", repoId = "org/swap", folder = "swap", nodeId = "dgx", startOnRequest = true, idleStopMinutes = 15,
        });
        var model = await Models().GetAsync("swap");
        Assert.Equal((true, 15, false), (model!.StartOnRequest, model.IdleStopMinutes, model.StopToMakeRoom));

        string updated = await CallTool(client, key, "mirbad_update_model", new { modelId = "swap", idleStopMinutes = 0, stopToMakeRoom = true });

        Assert.DoesNotContain("\"isError\":true", updated, StringComparison.Ordinal);
        Assert.Contains("no restart is needed", updated, StringComparison.Ordinal);
        model = await Models().GetAsync("swap");
        Assert.Equal((true, (int?)null, true), (model!.StartOnRequest, model.IdleStopMinutes, model.StopToMakeRoom)); // unnamed ones keep their value

        string refused = await CallTool(client, key, "mirbad_update_model", new { modelId = "swap", idleStopMinutes = -5 });
        Assert.Contains("IdleStopMinutes", refused, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--port", null, "--port")]
    [InlineData(null, "fp8", "does not use kvCacheDtype")]
    public async Task Update_RefusesWhatTheRuntimeCannotDo_AndChangesNothing(string? flag, string? kv, string message)
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(new RuntimeCapability { Kind = RuntimeKind.Sglang, Delivery = LaunchDelivery.Container, Available = true }));
        await CallTool(client, key, "mirbad_deploy_model", new { modelId = "sg", servedName = "sg", repoId = "org/sg", folder = "sg", nodeId = "dgx", runtime = "sglang", image = "sglang:1" });

        string refused = await CallTool(client, key, "mirbad_update_model", new
        {
            modelId = "sg", extraArgs = flag is null ? null : new[] { flag, "9000" }, kvCacheDtype = kv,
        });

        Assert.Contains("\"isError\":true", refused, StringComparison.Ordinal);
        Assert.Contains(message, refused, StringComparison.Ordinal);
        Assert.Empty((await Models().GetAsync("sg"))!.Container!.ExtraArgs);
    }

    [Fact]
    public async Task Update_OnVllm_ChangesGpus_AndSpeculativeOffIsRefusedWhereItMeansNothing()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(Vllm) with
        {
            Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
            Features = [AgentFeatures.GpuSelectionUuids],
            Gpus =
            [
                new GpuCapability { Index = 0, Uuid = "GPU-A", Name = "RTX" },
                new GpuCapability { Index = 1, Uuid = "GPU-B", Name = "RTX" },
            ],
        });
        await CallTool(client, key, "mirbad_deploy_model", new { modelId = "on-gpu", servedName = "x", repoId = "org/x", folder = "x", nodeId = "dgx", gpuIndices = FirstGpu });

        string updated = await CallTool(client, key, "mirbad_update_model", new { modelId = "on-gpu", gpuIndices = SecondGpu, kvCacheDtype = "fp8" });

        Assert.DoesNotContain("\"isError\":true", updated, StringComparison.Ordinal);
        var model = await Models().GetAsync("on-gpu");
        Assert.Equal(["GPU-B"], model!.Placement!.GpuUuids);
        Assert.Equal("fp8", model.KvCacheDtype);

        string mixed = await CallTool(client, key, "mirbad_update_model", new { modelId = "on-gpu", speculativeOff = true, speculativeTokens = 2 });
        Assert.Contains("cannot be combined", mixed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_NeedsAnOperatorKey_AndAnExistingModel()
    {
        using var client = Client();
        var read = await Keys().CreateAsync("reader", ApiKeyScope.McpRead);
        var op = await Keys().CreateAsync("operator", ApiKeyScope.McpOperator);

        string denied = await CallTool(client, read.FullKey, "mirbad_update_model", new { modelId = "nope" });
        Assert.Contains("Operator scope", denied, StringComparison.Ordinal);

        string unknown = await CallTool(client, op.FullKey, "mirbad_update_model", new { modelId = "nope" });
        Assert.Contains("was not found", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logs_NeedAnOperatorKey_AreLimited_AndAreEmptyWithNoInstance()
    {
        using var client = Client();
        string key = await NodeReporting(Capabilities(Vllm));
        var read = await Keys().CreateAsync("reader", ApiKeyScope.McpRead);
        await CallTool(client, key, "mirbad_deploy_model", new { modelId = "plain", servedName = "p", repoId = "org/p", folder = "p", nodeId = "dgx" });

        string denied = await CallTool(client, read.FullKey, "mirbad_get_model_logs", new { modelId = "plain" });
        Assert.Contains("Operator scope", denied, StringComparison.Ordinal);

        string tooMany = await CallTool(client, key, "mirbad_get_model_logs", new { modelId = "plain", tail = 2001 });
        Assert.Contains("tail must be between", tooMany, StringComparison.Ordinal);

        string empty = await CallTool(client, key, "mirbad_get_model_logs", new { modelId = "plain" });
        Assert.DoesNotContain("\"isError\":true", empty, StringComparison.Ordinal);
        Assert.Contains("\"lines\":[]", empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logs_ReturnWithoutFollowing_AndKeepTheNewestLinesWhenLong()
    {
        var logs = new LogRuntime();
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IModelRuntime>(logs)));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var op = await factory.Services.GetRequiredService<IApiKeyStore>().CreateAsync("operator", ApiKeyScope.McpOperator);
        await factory.Services.GetRequiredService<IModelRegistry>().UpsertAsync(new ModelDefinition { Id = "m", ServedName = "m", ModelPath = "m" });

        string result = await CallTool(client, op.FullKey, "mirbad_get_model_logs", new { modelId = "m", tail = 3 });

        Assert.Equal((3, false), logs.LastRead);
        Assert.Contains("\"truncated\":true", result, StringComparison.Ordinal);
        Assert.Contains("newest", result, StringComparison.Ordinal);
        Assert.DoesNotContain("oldest", result, StringComparison.Ordinal);
    }

    /// <summary>Serves a log whose oldest line alone is over the limit.</summary>
    private sealed class LogRuntime : IModelRuntime
    {
        public (int Tail, bool Follow) LastRead { get; private set; }

        public Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelStatus>>([]);

        public Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) => Task.FromResult(new ModelStatus(id, ModelState.Stopped));

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            LastRead = (tail, follow);
            yield return "oldest" + new string('x', 100_000);
            await Task.Yield();
            yield return "newest";
        }
    }

    [Fact]
    public async Task Deploy_OnNim_NeedsNoFolder_AndRefusesOne_WhileOthersStillNeedIt()
    {
        using var client = Client();
        const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        string key = await NodeReporting(Capabilities(new RuntimeCapability { Kind = RuntimeKind.Nim, Delivery = LaunchDelivery.Container, Available = true }));

        string saved = await CallTool(client, key, "mirbad_deploy_model", new { modelId = "nim-a", servedName = "meta/llama", nodeId = "dgx", runtime = "nim", image = "nvcr.io/nim/a:1", imageDigest = Digest });
        Assert.DoesNotContain("\"isError\":true", saved, StringComparison.Ordinal);
        Assert.Contains("NGC_API_KEY", saved, StringComparison.Ordinal);
        var model = await Models().GetAsync("nim-a");
        Assert.Empty(model!.Sources);
        Assert.False(model.NeedsCheckpoint);

        string tuning = await CallTool(client, key, "mirbad_start_tuning", new { modelId = "nim-a" });
        Assert.Contains("Tuning isn", tuning, StringComparison.Ordinal);

        string withFolder = await CallTool(client, key, "mirbad_deploy_model", new { modelId = "nim-b", servedName = "b", nodeId = "dgx", runtime = "nim", image = "nvcr.io/nim/a:1", imageDigest = Digest, folder = "b" });
        Assert.Contains("does not use repoId", withFolder, StringComparison.Ordinal);

        string withoutFolder = await CallTool(client, key, "mirbad_deploy_model", new { modelId = "vllm-c", servedName = "c", nodeId = "dgx" });
        Assert.Contains("repoId and folder are required", withoutFolder, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_CannotChangeTheImageOrCommand_OfAModelWithSecrets()
    {
        using var client = Client();
        const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        string key = await NodeReporting(Capabilities(new RuntimeCapability { Kind = RuntimeKind.Nim, Delivery = LaunchDelivery.Container, Available = true }));
        await CallTool(client, key, "mirbad_deploy_model", new { modelId = "nim-a", servedName = "a", nodeId = "dgx", runtime = "nim", image = "nvcr.io/nim/a:1", imageDigest = Digest });
        var stored = (await Models().GetAsync("nim-a"))!;
        await Models().UpsertAsync(stored with
        {
            Container = stored.Container! with { SecretEnv = new Dictionary<string, string> { ["NGC_API_KEY"] = "ngc" } },
        });

        foreach (object arguments in new object[] { new { modelId = "nim-a", image = "evil.example/nim:1" }, new { modelId = "nim-a", extraArgs = Parsers } })
        {
            string refused = await CallTool(client, key, "mirbad_update_model", arguments);
            Assert.Contains("only be changed in the portal", refused, StringComparison.Ordinal);
        }

        Assert.Equal("nvcr.io/nim/a:1", (await Models().GetAsync("nim-a"))!.Container!.Image);
        Assert.DoesNotContain("\"isError\":true", await CallTool(client, key, "mirbad_update_model", new { modelId = "nim-a", memoryBudgetGiB = 40 }), StringComparison.Ordinal);
    }

    private static readonly string[] Parsers = ["--tool-call-parser", "qwen3_coder", "--reasoning-parser", "qwen3"];
    private static readonly int[] SecondGpu = [1];
    private static readonly int[] FirstGpu = [0];
    private static readonly string[] ReservedPort = ["--port", "9000"];
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
