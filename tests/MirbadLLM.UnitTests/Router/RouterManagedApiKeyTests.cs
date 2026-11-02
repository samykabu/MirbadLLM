using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Router.Fakes;

namespace MirbadLLM.UnitTests.Router;

/// <summary>Covers the managed-API-key rules on <c>/v1/*</c>: open until the first Inference key exists,
/// required afterwards, revocation, and that a Metrics-scoped key is rejected.</summary>
public sealed class RouterManagedApiKeyTests
{
    private static (FakeModelEndpointResolver Resolver, FakeUpstreamHandler Handler) MakeReadyModel()
    {
        var resolver = new FakeModelEndpointResolver();
        resolver.Set(new ModelEndpoint("chat-1", "chat-model", new Uri("http://upstream/"), ModelState.Ready));
        var handler = new FakeUpstreamHandler
        {
            Handler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }),
        };
        return (resolver, handler);
    }

    [Fact]
    public async Task NoManagedKeysExist_RequestSucceedsWithoutAuthorization()
    {
        var ct = TestContext.Current.CancellationToken;
        var (resolver, handler) = MakeReadyModel();
        var store = new FakeApiKeyStore();

        using var app = await RouterHostFactory.CreateAsync(
            resolver, new FakeModelRuntime(), configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler), apiKeyStore: store);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OnceAnInferenceKeyExists_MissingAuthorizationReturns401()
    {
        var ct = TestContext.Current.CancellationToken;
        var (resolver, _) = MakeReadyModel();
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_valid", ApiKeyScope.Inference);

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), apiKeyStore: store);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OnceAnInferenceKeyExists_CorrectKeySucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (resolver, handler) = MakeReadyModel();
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_valid", ApiKeyScope.Inference);

        using var app = await RouterHostFactory.CreateAsync(
            resolver, new FakeModelRuntime(), configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler), apiKeyStore: store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ssk_valid");

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MetricsScopedKey_IsRejectedOnV1()
    {
        var ct = TestContext.Current.CancellationToken;
        var (resolver, _) = MakeReadyModel();
        var store = new FakeApiKeyStore();
        // An Inference key must exist too, otherwise /v1 stays open regardless of what's presented — this
        // test is specifically about a Metrics-scoped key not satisfying an *active* Inference gate.
        store.AddActiveKey("ssk_inference", ApiKeyScope.Inference);
        store.AddActiveKey("ssk_metrics-only", ApiKeyScope.Metrics);

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), apiKeyStore: store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ssk_metrics-only");

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RevokedKey_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (resolver, _) = MakeReadyModel();
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_revoked", ApiKeyScope.Inference);
        store.Revoke("ssk_revoked");

        using var app = await RouterHostFactory.CreateAsync(resolver, new FakeModelRuntime(), apiKeyStore: store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ssk_revoked");

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RequireApiKeyFlag_DeniesEvenWithNoKeysCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (resolver, _) = MakeReadyModel();
        var store = new FakeApiKeyStore();

        using var app = await RouterHostFactory.CreateAsync(
            resolver, new FakeModelRuntime(), apiKeyStore: store, configureAuthOptions: o => o.RequireApiKey = true);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LegacyApiKeyOption_StillAcceptedAsInferenceKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var (resolver, handler) = MakeReadyModel();
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_someone-elses-key", ApiKeyScope.Inference);

        using var app = await RouterHostFactory.CreateAsync(
            resolver, new FakeModelRuntime(),
            configureOptions: o => o.ApiKey = "legacy-secret",
            configureServices: services => RouterProxyTests.ConfigureUpstream(services, handler),
            apiKeyStore: store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "legacy-secret");

        using var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "chat-model" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
