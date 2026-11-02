using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using FakeApiKeyStore = MirbadLLM.UnitTests.Router.Fakes.FakeApiKeyStore;
using FakeMetricsHistory = MirbadLLM.UnitTests.Web.Fakes.FakeMetricsHistory;

namespace MirbadLLM.UnitTests.Auth;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

/// <summary>Covers the <c>/metrics</c> API-key gate: open until a Metrics key exists, then requiring either
/// a Metrics or an Inference key (see <see cref="ApiKeyAccess.CheckMetricsAsync"/>).</summary>
public sealed class MetricsApiKeyGateTests
{
    private static async Task<WebApplication> CreateAsync(FakeApiKeyStore apiKeyStore, Action<MirbadLLMOptions>? configureOptions = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry());
        builder.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        builder.Services.AddSingleton<IMetricsHistory>(new FakeMetricsHistory());
        builder.Services.AddSingleton<IApiKeyStore>(apiKeyStore);
        builder.Services.Configure<MirbadLLMOptions>(options => configureOptions?.Invoke(options));

        var app = builder.Build();
        app.MapMirbadLLMMetrics();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    [Fact]
    public async Task NoMetricsKeyExists_IsOpen()
    {
        using var app = await CreateAsync(new FakeApiKeyStore());
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OnceAMetricsKeyExists_MissingAuthorizationReturns401()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_metrics", ApiKeyScope.Metrics);
        using var app = await CreateAsync(store);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task OnceAMetricsKeyExists_MetricsScopedKeySucceeds()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_metrics", ApiKeyScope.Metrics);
        using var app = await CreateAsync(store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ssk_metrics");

        using var response = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OnceAMetricsKeyExists_InferenceScopedKeyAlsoSucceeds()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_metrics", ApiKeyScope.Metrics);
        store.AddActiveKey("ssk_inference", ApiKeyScope.Inference);
        using var app = await CreateAsync(store);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "ssk_inference");

        using var response = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task InferenceKeyAloneDoesNotCloseMetrics_UntilAMetricsKeyExists()
    {
        var store = new FakeApiKeyStore();
        store.AddActiveKey("ssk_inference", ApiKeyScope.Inference);
        using var app = await CreateAsync(store);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/metrics", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
