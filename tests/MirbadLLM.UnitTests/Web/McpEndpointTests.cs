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
