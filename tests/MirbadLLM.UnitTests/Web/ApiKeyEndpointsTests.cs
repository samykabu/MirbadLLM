using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Web.Api;
using FakeApiKeyStore = MirbadLLM.UnitTests.Router.Fakes.FakeApiKeyStore;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

/// <summary>Covers <c>/api/keys/{id}</c> deletion semantics: it is a permanent removal, distinct from
/// revocation (<c>POST /api/keys/{id}/revoke</c>), and refuses to delete an active key.</summary>
public sealed class ApiKeyEndpointsTests
{
    private sealed class TestHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required FakeApiKeyStore Store { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(TestContext.Current.CancellationToken);
            await App.DisposeAsync();
        }
    }

    private static async Task<TestHost> StartAsync()
    {
        var store = new FakeApiKeyStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IApiKeyStore>(store);

        var app = builder.Build();
        app.MapApiKeyEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken);

        return new TestHost { App = app, Client = app.GetTestClient(), Store = store };
    }

    [Fact]
    public async Task DeletingARevokedKey_RemovesItAndReturns204()
    {
        await using var host = await StartAsync();
        var key = host.Store.AddRevokedKey("ssk_revoked1", ApiKeyScope.Inference, "old-key");

        using var response = await host.Client.DeleteAsync($"/api/keys/{key.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await host.Store.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletingAnActiveKey_Returns409_AndDoesNotRemoveIt()
    {
        await using var host = await StartAsync();
        var key = host.Store.AddActiveKey("ssk_active1", ApiKeyScope.Inference, "live-key");

        using var response = await host.Client.DeleteAsync($"/api/keys/{key.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Single(await host.Store.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletingAnUnknownId_Returns404()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.DeleteAsync("/api/keys/no-such-id", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RevokeEndpoint_MarksAnActiveKeyRevoked_WithoutRemovingIt()
    {
        await using var host = await StartAsync();
        var key = host.Store.AddActiveKey("ssk_torevoke", ApiKeyScope.Metrics, "dashboard");

        using var response = await host.Client.PostAsync($"/api/keys/{key.Id}/revoke", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var stored = Assert.Single(await host.Store.ListAsync(TestContext.Current.CancellationToken));
        Assert.False(stored.IsActive);
    }
}
