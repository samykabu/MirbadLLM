using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Web.Api;

namespace MirbadLLM.UnitTests.HostServices;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>Controller side of the Services page: host agent client, container status/memory/autostart, confirmations, API.</summary>
public sealed class HostServiceManagerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    // ---- Host agent client ----

    [Fact]
    public async Task AgentClient_ListsUnitsAndForwardsActionsWithConfirm()
    {
        var agent = new FakeAgent();
        var client = agent.Client();
        var ct = TestContext.Current.CancellationToken;

        var units = await client.ListAsync(ct);
        await client.RunAsync("rustdesk", HostServiceAction.Stop, "rustdesk", ct);
        await client.SetAutostartAsync("comfyui", enabled: false, confirm: null, ct);

        Assert.Equal(["comfyui", "lmstudio-server", "rustdesk"], units.Select(u => u.Name));
        Assert.Equal(40144L * 1024 * 1024, units[0].GpuMemoryBytes);
        Assert.Equal("http://hostd/services/rustdesk/stop?confirm=rustdesk", agent.Handler.Requests[1].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, agent.Handler.Requests[1].Method);
        Assert.Equal("http://hostd/services/comfyui/autostart", agent.Handler.Requests[2].RequestUri!.ToString());
        Assert.Equal("""{"enabled":false}""", agent.Handler.RequestBodies[2]);
    }

    [Fact]
    public async Task AgentClient_MapsErrorsToTypedExceptions()
    {
        var agent = new FakeAgent();
        var client = agent.Client();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<HostServiceNotFoundException>(() => client.RunAsync("sshd", HostServiceAction.Stop, null, ct));

        agent.Handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":"protected"}""") };
        var forbidden = await Assert.ThrowsAsync<HostServiceConfirmationRequiredException>(() => client.RunAsync("rustdesk", HostServiceAction.Stop, null, ct));
        Assert.Equal("protected", forbidden.Message);

        agent.Handler.Handler = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("""{"error":"systemctl stop failed"}""") };
        var failed = await Assert.ThrowsAsync<HostServiceException>(() => client.RunAsync("comfyui", HostServiceAction.Stop, null, ct));
        Assert.Equal("systemctl stop failed", failed.Message);

        agent.Handler.Handler = _ => throw new HttpRequestException("Connection refused");
        await Assert.ThrowsAsync<HostAgentUnavailableException>(() => client.ListAsync(ct));
    }

    [Fact]
    public async Task AgentClient_MissingSocketIsUnavailableWithoutAnyRequest()
    {
        var agent = new FakeAgent { Installed = false };

        await Assert.ThrowsAsync<HostAgentUnavailableException>(() => agent.Client().ListAsync(TestContext.Current.CancellationToken));
        Assert.Empty(agent.Handler.Requests);
    }

    // ---- Listing ----

    [Fact]
    public async Task List_CombinesUnitsAndContainersWithMemoryAndAutostart()
    {
        var (manager, docker, _, _) = ServicesTestSetup.Create();
        docker.Add("openviking");
        docker.Add("n8n-compose-n8n-1", c => c.RestartPolicy = "unless-stopped");
        docker.Add("sharp_wing");
        docker.Add("ss-vllm", c =>
        {
            c.Status = "exited";
            c.ExitCode = 137;
            c.RestartPolicy = "no";
        });

        var snapshot = await manager.ListAsync(TestContext.Current.CancellationToken);

        Assert.True(snapshot.HostAgentAvailable);
        Assert.Equal(["comfyui", "lmstudio-server", "rustdesk", "openviking", "n8n-compose-n8n-1", "sharp_wing", "ss-vllm"],
            snapshot.Services.Select(s => s.Name));

        var comfy = snapshot.Services[0];
        Assert.Equal(HostServiceKind.System, comfy.Kind);
        Assert.Equal(HostServiceState.Running, comfy.State);
        Assert.True(comfy.Autostart);
        Assert.Equal(1_649_680_384L + (40144L * 1024 * 1024), comfy.TotalMemoryBytes);
        Assert.Equal(HostServiceKind.User, snapshot.Services[1].Kind);

        var openviking = snapshot.Services[3];
        Assert.Equal(HostServiceKind.Container, openviking.Kind);
        Assert.True(openviking.Protected);
        Assert.Equal(500L * 1024 * 1024, openviking.MemoryBytes); // usage minus inactive_file
        Assert.Null(openviking.GpuMemoryBytes);
        Assert.Equal("always", openviking.AutostartDetail);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero), openviking.Since!.Value.AddTicks(-(openviking.Since.Value.Ticks % TimeSpan.TicksPerSecond)));

        var vllm = snapshot.Services[6];
        Assert.Equal(HostServiceState.Stopped, vllm.State);
        Assert.Equal("exited (137)", vllm.StateDetail);
        Assert.False(vllm.Autostart);
        Assert.Null(vllm.MemoryBytes);
        Assert.Null(vllm.Since);
    }

    [Fact]
    public async Task List_NeverIncludesMirbadLLMOrModelContainers()
    {
        var (manager, docker, _, _) = ServicesTestSetup.Create(
        [
            new() { Name = "mirbadllm" },
            new() { Name = "ss-ornith-9b-nvfp4" },
            new() { Name = "n8n-compose-n8n-1" },
        ]);
        docker.Add("mirbadllm");
        docker.Add("ss-ornith-9b-nvfp4", c => c.Labels["mirbadllm.model"] = "ornith-9b-nvfp4");
        docker.Add("n8n-compose-n8n-1");
        var ct = TestContext.Current.CancellationToken;

        var snapshot = await manager.ListAsync(ct);

        Assert.Equal(["n8n-compose-n8n-1"], snapshot.Services.Where(s => s.Kind == HostServiceKind.Container).Select(s => s.Name));
        await Assert.ThrowsAsync<HostServiceNotFoundException>(() => manager.RunAsync("ss-ornith-9b-nvfp4", HostServiceAction.Stop, null, ct));
        await Assert.ThrowsAsync<HostServiceNotFoundException>(() => manager.RunAsync("mirbadllm", HostServiceAction.Stop, null, ct));
        Assert.Empty(docker.Posts);
    }

    [Fact]
    public async Task List_MissingAgentStillShowsContainers()
    {
        var (manager, docker, agent, _) = ServicesTestSetup.Create();
        agent.Installed = false;
        docker.Add("n8n-compose-n8n-1");

        var snapshot = await manager.ListAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.HostAgentAvailable);
        Assert.Contains("not installed", snapshot.HostAgentError, StringComparison.Ordinal);
        Assert.Equal(["n8n-compose-n8n-1"], snapshot.Services.Select(s => s.Name));
    }

    [Theory]
    [InlineData("running", null, false, HostServiceState.Running)]
    [InlineData("running", "starting", false, HostServiceState.Starting)]
    [InlineData("running", "unhealthy", false, HostServiceState.Failed)]
    [InlineData("restarting", null, false, HostServiceState.Starting)]
    [InlineData("exited", null, false, HostServiceState.Stopped)]
    [InlineData("exited", null, true, HostServiceState.Failed)]
    [InlineData("created", null, false, HostServiceState.Stopped)]
    [InlineData("dead", null, false, HostServiceState.Failed)]
    public void ContainerState_Maps(string status, string? health, bool oom, HostServiceState expected) =>
        Assert.Equal(expected, HostServiceManager.MapContainerState(status, health, oom));

    [Theory]
    [InlineData("active", HostServiceState.Running)]
    [InlineData("activating", HostServiceState.Starting)]
    [InlineData("deactivating", HostServiceState.Stopping)]
    [InlineData("inactive", HostServiceState.Stopped)]
    [InlineData("failed", HostServiceState.Failed)]
    public void UnitState_Maps(string active, HostServiceState expected) =>
        Assert.Equal(expected, HostServiceManager.MapUnitState(active));

    [Fact]
    public void UnitAutostart_StaticUnitsCannotBeToggled()
    {
        Assert.True(HostServiceManager.MapUnitAutostart("enabled"));
        Assert.False(HostServiceManager.MapUnitAutostart("disabled"));
        Assert.Null(HostServiceManager.MapUnitAutostart("static"));
        Assert.Null(HostServiceManager.MapUnitAutostart("masked"));
    }

    [Fact]
    public void ContainerMemory_SubtractsReclaimableCache()
    {
        Assert.Equal(400L, DockerServiceClient.MemoryFromStats(new() { Usage = 500, Stats = new() { ["inactive_file"] = 100 } }));
        Assert.Equal(450L, DockerServiceClient.MemoryFromStats(new() { Usage = 500, Stats = new() { ["total_inactive_file"] = 50 } }));
        Assert.Null(DockerServiceClient.MemoryFromStats(new()));
    }

    // ---- Autostart = restart policy ----

    [Fact]
    public async Task Autostart_OffThenOn_RestoresAlways()
    {
        var (manager, docker, _, file) = ServicesTestSetup.Create();
        docker.Add("n8n-compose-n8n-1", c => c.RestartPolicy = "always");
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var off = await manager.SetAutostartAsync("n8n-compose-n8n-1", enabled: false, confirm: null, ct);
            Assert.False(off.Autostart);
            Assert.Equal("no", docker.Containers["n8n-compose-n8n-1"].RestartPolicy);

            var on = await manager.SetAutostartAsync("n8n-compose-n8n-1", enabled: true, confirm: null, ct);
            Assert.True(on.Autostart);
            Assert.Equal("always", docker.Containers["n8n-compose-n8n-1"].RestartPolicy);
            Assert.Contains(docker.Handler.RequestBodies, b => b == """{"RestartPolicy":{"Name":"always"}}""");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Autostart_OnForANeverEnabledContainer_UsesUnlessStopped_AndOffEndsAtNo()
    {
        var (manager, docker, _, file) = ServicesTestSetup.Create();
        docker.Add("ss-vllm", c =>
        {
            c.Status = "exited";
            c.RestartPolicy = "no";
        });
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await manager.SetAutostartAsync("ss-vllm", enabled: true, confirm: null, ct);
            Assert.Equal("unless-stopped", docker.Containers["ss-vllm"].RestartPolicy);

            await manager.SetAutostartAsync("ss-vllm", enabled: false, confirm: null, ct);
            Assert.Equal("no", docker.Containers["ss-vllm"].RestartPolicy);

            // Already in the requested state: no Docker update.
            int updates = docker.Posts.Count;
            await manager.SetAutostartAsync("ss-vllm", enabled: false, confirm: null, ct);
            Assert.Equal(updates, docker.Posts.Count);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- Protected: typed confirmation enforced below the UI ----

    [Fact]
    public async Task Protected_StopRestartAndAutostartOff_NeedTheName()
    {
        var (manager, docker, agent, _) = ServicesTestSetup.Create();
        docker.Add("sharp_wing");
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<HostServiceConfirmationRequiredException>(() => manager.RunAsync("sharp_wing", HostServiceAction.Stop, null, ct));
        await Assert.ThrowsAsync<HostServiceConfirmationRequiredException>(() => manager.RunAsync("sharp_wing", HostServiceAction.Restart, "Sharp_Wing", ct));
        await Assert.ThrowsAsync<HostServiceConfirmationRequiredException>(() => manager.SetAutostartAsync("sharp_wing", false, null, ct));
        await Assert.ThrowsAsync<HostServiceConfirmationRequiredException>(() => manager.RunAsync("rustdesk", HostServiceAction.Stop, null, ct));
        Assert.Empty(docker.Posts);
        Assert.DoesNotContain(agent.Handler.Requests, r => r.Method == HttpMethod.Post);

        await manager.RunAsync("sharp_wing", HostServiceAction.Restart, "sharp_wing", ct);
        await manager.RunAsync("rustdesk", HostServiceAction.Stop, "rustdesk", ct);
        Assert.Equal(["restart sharp_wing"], docker.Posts);
        Assert.Contains(agent.Handler.Requests, r => r.RequestUri!.PathAndQuery == "/services/rustdesk/stop?confirm=rustdesk");
    }

    // ---- Admin API ----

    [Fact]
    public async Task Api_RejectsProtectedStopWithoutConfirm()
    {
        var (manager, docker, _, _) = ServicesTestSetup.Create();
        docker.Add("sharp_wing");
        await using var host = await StartApiAsync(manager);
        var ct = TestContext.Current.CancellationToken;

        using var refused = await host.Client.PostAsync("/api/services/sharp_wing/stop", null, ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("protected", await refused.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.Empty(docker.Posts);

        using var autostartRefused = await host.Client.PostAsJsonAsync("/api/services/sharp_wing/autostart", new { enabled = false }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, autostartRefused.StatusCode);

        using var ok = await host.Client.PostAsync("/api/services/sharp_wing/stop?confirm=sharp_wing", null, ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var status = await ok.Content.ReadFromJsonAsync<HostServiceStatus>(JsonOptions, ct);
        Assert.Equal(HostServiceState.Stopped, status!.State);
        Assert.Equal(["stop sharp_wing"], docker.Posts);
    }

    [Fact]
    public async Task Api_ListsAndMapsErrors()
    {
        var (manager, docker, agent, _) = ServicesTestSetup.Create();
        docker.Add("n8n-compose-n8n-1");
        await using var host = await StartApiAsync(manager);
        var ct = TestContext.Current.CancellationToken;

        var snapshot = await host.Client.GetFromJsonAsync<HostServicesSnapshot>("/api/services", JsonOptions, ct);
        Assert.Contains(snapshot!.Services, s => s.Name == "comfyui" && s.Kind == HostServiceKind.System);

        using var unknown = await host.Client.PostAsync("/api/services/sshd/stop", null, ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using var badAction = await host.Client.PostAsync("/api/services/comfyui/kill", null, ct);
        Assert.Equal(HttpStatusCode.BadRequest, badAction.StatusCode);

        agent.Installed = false;
        using var missing = await host.Client.PostAsync("/api/services/comfyui/restart", null, ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, missing.StatusCode);
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
        app.MapHostServiceApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new ApiHost { App = app, Client = app.GetTestClient() };
    }
}
