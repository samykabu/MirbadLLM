using Microsoft.AspNetCore.TestHost;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Router.Fakes;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;

namespace MirbadLLM.UnitTests.Router;

public sealed class RouterAuthTests
{
    [Fact]
    public async Task NoApiKeyConfigured_RequestsSucceedWithoutAuthorization()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));
        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }),
        };

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_MissingAuthorization_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), o => o.ApiKey = "secret");
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_WrongBearerToken_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), o => o.ApiKey = "secret");
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong");

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_CorrectBearerToken_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));
        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }),
        };

        using var app = await RouterHostFactory.CreateAsync(
            resolver, new FakeModelRuntime(), o => o.ApiKey = "secret", services => RouterProxyTests.ConfigureUpstream(services, handler));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ApiKeyConfigured_GatesModelsEndpointToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), o => o.ApiKey = "secret");
        using var client = app.GetTestClient();

        using var unauthorized = await client.GetAsync("/v1/models", ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        using var authorized = await client.GetAsync("/v1/models", ct);
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
    }
}
