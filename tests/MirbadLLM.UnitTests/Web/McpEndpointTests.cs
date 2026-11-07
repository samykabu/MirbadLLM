using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Auth;

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
