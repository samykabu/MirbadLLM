using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Web;
using MirbadLLM.Web.Api;

namespace MirbadLLM.UnitTests.HostServices;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>Controller side of discovery and the whitelist: container discovery, container whitelist, re-auth.</summary>
public sealed class WhitelistTests
{
    private const string Password = "s3cret";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // ---- Discovery ----

    [Fact]
    public async Task Discover_Containers_ExcludeMirbadLLMModelAndDownloadContainers()
    {
        var (manager, docker, _, file) = ServicesTestSetup.Create();
        docker.Add("mirbadllm");
        docker.Add("ss-ornith-9b-nvfp4", c => c.Labels["mirbadllm.model"] = "ornith-9b-nvfp4");
        docker.Add("ss-dl-qwen", c => c.Labels["mirbadllm.download"] = "true");
        docker.Add("openviking");
        docker.Add("grafana", c => c.Usage = 2L * 1024 * 1024 * 1024);
        docker.Add("old", c => c.Status = "exited");
        try
        {
            var discovery = await manager.DiscoverAsync(TestContext.Current.CancellationToken);

            var containers = discovery.Services.Where(s => s.Kind == HostServiceKind.Container).ToList();
            Assert.Equal(["grafana", "openviking", "old"], containers.Select(c => c.Name));
            Assert.True(containers.Single(c => c.Name == "openviking").Managed);   // seeded default
            Assert.False(containers.Single(c => c.Name == "grafana").Managed);
            Assert.Equal(HostServiceState.Stopped, containers.Single(c => c.Name == "old").State);
            Assert.Null(containers.Single(c => c.Name == "old").MemoryBytes);
            Assert.True(discovery.HostAgentAvailable);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Discover_MergesUnitsAndSortsByMemoryDescending()
    {
        var (manager, docker, _, file) = ServicesTestSetup.Create();
        docker.Add("grafana", c => c.Usage = 3L * 1024 * 1024 * 1024);
        try
        {
            var discovery = await manager.DiscoverAsync(TestContext.Current.CancellationToken);

            Assert.Equal("comfyui", discovery.Services[0].Name); // 1.6 GiB RAM + 39 GiB GPU
            Assert.Equal("grafana", discovery.Services[1].Name);
            var totals = discovery.Services.Select(s => s.TotalMemoryBytes ?? -1).ToList();
            Assert.Equal(totals.OrderByDescending(t => t), totals);
            var ssh = discovery.Services.Single(s => s.Unit == "ssh.service");
            Assert.True(ssh.Denied);
            Assert.Equal("ssh*", ssh.DeniedBy);
            Assert.Contains(discovery.Services, s => s.Name == "smartmontools" && !s.Managed && s.Kind == HostServiceKind.System);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- Container whitelist ----

    [Fact]
    public async Task ContainerWhitelist_IsSeededFromConfig_IntoHostServicesJson()
    {
        var (manager, docker, _, file) = ServicesTestSetup.Create([new() { Name = "n8n-compose-n8n-1", DisplayName = "n8n", Protected = true }]);
        docker.Add("n8n-compose-n8n-1");
        try
        {
            var snapshot = await manager.ListAsync(TestContext.Current.CancellationToken);

            Assert.Contains(snapshot.Services, s => s.Name == "n8n-compose-n8n-1" && s.Protected && s.DisplayName == "n8n");
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            var containers = doc.RootElement.GetProperty("containers");
            Assert.Equal(1, containers.GetArrayLength());
            Assert.Equal("n8n-compose-n8n-1", containers[0].GetProperty("name").GetString());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task ContainerWhitelist_ExistingRestartPoliciesSurviveTheMigration()
    {
        var (manager, docker, _, file) = ServicesTestSetup.Create();
        await File.WriteAllTextAsync(file, """{ "restartPolicies": { "n8n-compose-n8n-1": "always" } }""", TestContext.Current.CancellationToken);
        docker.Add("n8n-compose-n8n-1", c => c.RestartPolicy = "no");
        try
        {
            await manager.SetAutostartAsync("n8n-compose-n8n-1", true, null, TestContext.Current.CancellationToken);

            Assert.Equal("always", docker.Containers["n8n-compose-n8n-1"].RestartPolicy);
            string text = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.Contains("\"restartPolicies\"", text, StringComparison.Ordinal);
            Assert.Contains("\"containers\"", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Actions_OnANonWhitelistedContainer_AreRefused()
    {
        var (manager, docker, _, file) = ServicesTestSetup.Create();
        docker.Add("grafana");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await Assert.ThrowsAsync<HostServiceNotFoundException>(() => manager.RunAsync("grafana", HostServiceAction.Stop, null, ct));
            await Assert.ThrowsAsync<HostServiceNotFoundException>(() => manager.SetAutostartAsync("grafana", false, null, ct));
            Assert.Empty(docker.Posts);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AddContainer_WithSessionAndName_ThenItCanBeControlled_AndRemoveStopsThat()
    {
        var reauth = new FakeReauth();
        var (manager, docker, _, file) = ServicesTestSetup.Create(reauth: reauth);
        docker.Add("grafana");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var added = await manager.AddToWhitelistAsync(new(HostServiceKind.Container, "grafana", "Grafana", true, "grafana", Password, "10.0.0.5") { User = TestAdmin.User }, ct);

            Assert.Equal("Grafana", added.DisplayName);
            Assert.True(added.Protected);
            await manager.RunAsync("grafana", HostServiceAction.Restart, "grafana", ct);
            Assert.Equal(["restart grafana"], docker.Posts);
            await Assert.ThrowsAsync<HostServiceConflictException>(() =>
                manager.AddToWhitelistAsync(new(HostServiceKind.Container, "grafana", null, false, "grafana", Password, "10.0.0.5") { User = TestAdmin.User }, ct));

            await manager.RemoveFromWhitelistAsync("grafana", ct);
            await Assert.ThrowsAsync<HostServiceNotFoundException>(() => manager.RunAsync("grafana", HostServiceAction.Stop, null, ct));
            Assert.Equal(["restart grafana"], docker.Posts);
            Assert.DoesNotContain("grafana", await File.ReadAllTextAsync(file, ct), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AddContainer_NeverForExcludedContainers()
    {
        var (manager, docker, _, file) = ServicesTestSetup.Create(reauth: new FakeReauth());
        docker.Add("ss-model", c => c.Labels["mirbadllm.model"] = "m");
        docker.Add("mirbadllm");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await Assert.ThrowsAsync<HostServiceNotFoundException>(() =>
                manager.AddToWhitelistAsync(new(HostServiceKind.Container, "ss-model", null, false, "ss-model", Password, "ip") { User = TestAdmin.User }, ct));
            await Assert.ThrowsAsync<HostServiceNotFoundException>(() =>
                manager.AddToWhitelistAsync(new(HostServiceKind.Container, "mirbadllm", null, false, "mirbadllm", Password, "ip") { User = TestAdmin.User }, ct));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- Re-authentication ----

    [Fact]
    public async Task Add_AnonymousSession_IsRejected_BeforeAnythingIsForwarded()
    {
        var reauth = new FakeReauth();
        var (manager, docker, agent, file) = ServicesTestSetup.Create(reauth: reauth);
        docker.Add("grafana");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var unit = await Assert.ThrowsAsync<AdminReauthenticationException>(() =>
                manager.AddToWhitelistAsync(new(HostServiceKind.System, "smartmontools.service", null, false, "smartmontools", "guess", "ip"), ct));
            var container = await Assert.ThrowsAsync<AdminReauthenticationException>(() =>
                manager.AddToWhitelistAsync(new(HostServiceKind.Container, "grafana", null, false, "grafana", null, "ip"), ct));

            Assert.Equal(AdminReauthOutcome.LoginRequired, unit.Outcome);
            Assert.Equal(AdminReauthOutcome.LoginRequired, container.Outcome);
            Assert.DoesNotContain(agent.Handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("whitelist", StringComparison.Ordinal));
            Assert.Equal(2, reauth.Calls);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Add_Unit_ForwardsNameAndProtectedToTheAgent_WhichRefusesDeniedUnits()
    {
        var (manager, _, agent, file) = ServicesTestSetup.Create(reauth: new FakeReauth());
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var added = await manager.AddToWhitelistAsync(new(HostServiceKind.System, "smartmontools.service", "SMART", true, "smartmontools", Password, "ip") { User = TestAdmin.User }, ct);
            Assert.Equal("smartmontools", added.Name);
            Assert.True(added.Protected);
            string body = agent.Handler.RequestBodies[agent.Handler.Requests.FindIndex(r => r.RequestUri!.AbsolutePath == "/whitelist")];
            Assert.Contains("\"unit\":\"smartmontools.service\"", body, StringComparison.Ordinal);
            Assert.Contains("\"confirm\":\"smartmontools\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, body, StringComparison.Ordinal); // the password never leaves the controller

            var denied = await Assert.ThrowsAsync<HostServiceDeniedException>(() =>
                manager.AddToWhitelistAsync(new(HostServiceKind.System, "ssh.service", null, false, "ssh", Password, "ip") { User = TestAdmin.User }, ct));
            Assert.Contains("ssh*", denied.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task WhitelistChanges_AreRefusedWhenAdminLoginIsDisabled()
    {
        var reauth = new FakeReauth() { LoginEnabled = false };
        var (manager, docker, _, file) = ServicesTestSetup.Create(reauth: reauth);
        docker.Add("grafana");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var add = await Assert.ThrowsAsync<AdminReauthenticationException>(() =>
                manager.AddToWhitelistAsync(new(HostServiceKind.Container, "grafana", null, false, "grafana", Password, "ip") { User = TestAdmin.User }, ct));
            var remove = await Assert.ThrowsAsync<AdminReauthenticationException>(() => manager.RemoveFromWhitelistAsync("comfyui", ct));

            Assert.Contains("MirbadLLM__AdminPassword", add.Message, StringComparison.Ordinal);
            Assert.Equal(AdminReauthOutcome.LoginDisabled, remove.Outcome);

            // No reauthenticator registered at all behaves the same.
            var (bare, _, _, bareFile) = ServicesTestSetup.Create();
            await Assert.ThrowsAsync<AdminReauthenticationException>(() => bare.RemoveFromWhitelistAsync("comfyui", ct));
            File.Delete(bareFile);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Unprotect_NeedsNameAndSession_ProtectAndRemoveDoNot()
    {
        var reauth = new FakeReauth();
        var (manager, _, agent, file) = ServicesTestSetup.Create(reauth: reauth);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await Assert.ThrowsAsync<HostServiceConfirmationRequiredException>(() =>
                manager.UpdateWhitelistAsync("rustdesk", new(null, false, null, Password, "ip") { User = TestAdmin.User }, ct));
            await Assert.ThrowsAsync<AdminReauthenticationException>(() =>
                manager.UpdateWhitelistAsync("rustdesk", new(null, false, "rustdesk", Password, "ip"), ct));
            Assert.True(agent.Units.Single(u => u.Name == "rustdesk").Protected);

            var ok = await manager.UpdateWhitelistAsync("rustdesk", new(null, false, "rustdesk", Password, "ip") { User = TestAdmin.User }, ct);
            Assert.False(ok.Protected);

            int calls = reauth.Calls;
            var again = await manager.UpdateWhitelistAsync("comfyui", new(null, true, null, null, "ip") { User = TestAdmin.User }, ct);
            Assert.True(again.Protected);
            await manager.RemoveFromWhitelistAsync("lmstudio-server", ct);
            Assert.Equal(calls, reauth.Calls);
            Assert.DoesNotContain(agent.Units, u => u.Name == "lmstudio-server");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void SessionAuthorization_RequiresAnAuthenticatedSession_WithoutSpendingLoginAttempts()
    {
        using var guard = new AdminPasswordGuard();
        var reauth = new AdminReauthenticator(Options.Create(new AuthOptions { AdminPassword = Password }));
        Assert.True(reauth.LoginEnabled);
        Assert.Equal(AdminReauthOutcome.LoginRequired, reauth.Verify(null));
        Assert.Equal(AdminReauthOutcome.LoginRequired, reauth.Verify(new System.Security.Claims.ClaimsPrincipal()));
        var legacy = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity("Cookies"));
        Assert.Equal(AdminReauthOutcome.LoginRequired, reauth.Verify(legacy));
        for (int i = 0; i < 10; i++) Assert.Equal(AdminReauthOutcome.Verified, reauth.Verify(TestAdmin.User));
        for (int i = 0; i < AdminPasswordGuard.PermitLimit; i++) Assert.True(guard.TryAcquire("login-client"));
        Assert.False(guard.TryAcquire("login-client"));
        Assert.Equal(AdminReauthOutcome.Verified, reauth.Verify(TestAdmin.User));
        var disabled = new AdminReauthenticator(Options.Create(new AuthOptions()));
        Assert.Equal(AdminReauthOutcome.LoginDisabled, disabled.Verify(TestAdmin.User));
    }

    // ---- Admin API ----

    [Fact]
    public async Task Api_Whitelist_MapsReauthDenialAndConflict()
    {
        var reauth = new FakeReauth();
        var (manager, docker, _, file) = ServicesTestSetup.Create(reauth: reauth);
        docker.Add("grafana");
        await using var host = await StartApiAsync(manager);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            host.Client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
            using var wrong = await host.Client.PostAsJsonAsync("/api/services/whitelist",
                new { kind = "Container", unit = "grafana", confirm = "grafana", password = "nope" }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
            host.Client.DefaultRequestHeaders.Remove("X-Test-Anonymous");

            reauth.Exhausted = true;
            using var limited = await host.Client.PostAsJsonAsync("/api/services/whitelist",
                new { kind = "Container", unit = "grafana", confirm = "grafana", password = Password }, ct);
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            reauth.Exhausted = false;

            using var denied = await host.Client.PostAsJsonAsync("/api/services/whitelist",
                new { kind = "System", unit = "ssh.service", confirm = "ssh", password = Password }, ct);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Contains("ssh*", await denied.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

            using var ok = await host.Client.PostAsJsonAsync("/api/services/whitelist",
                new { kind = "Container", unit = "grafana", displayName = "Grafana", confirm = "grafana", password = Password }, ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            var status = await ok.Content.ReadFromJsonAsync<HostServiceStatus>(JsonOptions, ct);
            Assert.Equal("Grafana", status!.DisplayName);

            using var dupe = await host.Client.PostAsJsonAsync("/api/services/whitelist",
                new { kind = "Container", unit = "grafana", confirm = "grafana", password = Password }, ct);
            Assert.Equal(HttpStatusCode.Conflict, dupe.StatusCode);

            var discovered = await host.Client.GetFromJsonAsync<HostServicesDiscovery>("/api/services/discovered", JsonOptions, ct);
            Assert.Contains(discovered!.Services, s => s.Name == "grafana" && s.Managed);

            using var removed = await host.Client.DeleteAsync("/api/services/whitelist/grafana", ct);
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private sealed class ApiHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public required HttpClient Client { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(TestContext.Current.CancellationToken);
            await App.DisposeAsync();
        }
    }

    private static async Task<ApiHost> StartApiAsync(IHostServiceManager manager)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(manager);
        builder.Services.Configure<JsonOptions>(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        var app = builder.Build();
        app.Use(async (http, next) => { if (!http.Request.Headers.ContainsKey("X-Test-Anonymous")) http.User = TestAdmin.User; await next(http); });
        app.MapHostServiceApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new ApiHost { App = app, Client = app.GetTestClient() };
    }
}
