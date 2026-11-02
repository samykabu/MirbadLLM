using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirbadLLM.HostAgent;

namespace MirbadLLM.UnitTests.HostAgent;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>mirbadllm-hostd: allowlist, systemctl argument vectors, status parsing, GPU mapping and action logging.</summary>
public sealed class HostAgentTests
{
    // ---- Config (fails closed) ----

    [Theory]
    [InlineData("""{"services":[{"name":"x","unit":"x.service; rm -rf /","scope":"system"}]}""")]
    [InlineData("""{"services":[{"name":"x","unit":"--now","scope":"system"}]}""")]
    [InlineData("""{"services":[{"name":"x","unit":"x.timer","scope":"system"}]}""")]
    [InlineData("""{"services":[{"name":"X Y","unit":"x.service","scope":"system"}]}""")]
    [InlineData("""{"services":[{"name":"x","unit":"x.service","scope":"user"}]}""")]
    [InlineData("""{"services":[{"name":"x","unit":"x.service","scope":"user","user":"root;id"}]}""")]
    [InlineData("""{"services":[{"name":"x","unit":"x.service","scope":"session"}]}""")]
    [InlineData("""{"services":[{"name":"x","unit":"x.service"},{"name":"x","unit":"y.service"}]}""")]
    [InlineData("""{"socket":"relative.sock","services":[]}""")]
    public void Config_RejectsAnythingMalformed(string json) =>
        Assert.Throws<FormatException>(() => HostdConfig.Parse(json));

    [Fact]
    public void Config_ParsesEntriesAndLooksUpByExactName()
    {
        var config = HostAgentFixtures.Config();

        Assert.Equal(3, config.Services.Count);
        var lm = config.Find("lmstudio-server")!;
        Assert.Equal(UnitScope.User, lm.Scope);
        Assert.Equal("dgxuser", lm.User);
        Assert.True(config.Find("rustdesk")!.Protected);
        Assert.Null(config.Find("ComfyUI"));
        Assert.Null(config.Find("comfyui.service"));
    }

    [Fact]
    public void Config_DefaultAllowlistInDeployFolderIsValid()
    {
        string path = FindRepoFile(Path.Combine("deploy", "hostd", "hostd.json"));
        var config = HostdConfig.Parse(File.ReadAllText(path));

        Assert.Equal(["comfyui", "ollama", "lmstudio-server", "dgx-dashboard", "dgx-dashboard-admin", "rustdesk", "nvidia-dgx-telemetry"],
            config.Services.Select(s => s.Name));
        Assert.All(config.Services.Where(s => s.Name is "comfyui" or "ollama" or "lmstudio-server"), s => Assert.False(s.Protected));
        Assert.All(config.Services.Where(s => s.Protected), s => Assert.False(string.IsNullOrEmpty(s.Risk)));
        Assert.Equal(4, config.Services.Count(s => s.Protected));
    }

    // ---- Argument construction (no shell, exact vectors) ----

    [Fact]
    public void Arguments_SystemScope_AreExact()
    {
        var comfy = HostAgentFixtures.Config().Find("comfyui")!;

        Assert.Equal(
            ["show", "--no-pager", "--timestamp=unix", "--property=Id,ActiveState,SubState,UnitFileState,MainPID,ActiveEnterTimestamp,MemoryCurrent,ControlGroup", "--", "comfyui.service"],
            SystemctlCommands.Show(comfy));
        Assert.Equal(["stop", "--no-block", "--no-ask-password", "--", "comfyui.service"], SystemctlCommands.Action(comfy, UnitAction.Stop));
        Assert.Equal(["disable", "--no-ask-password", "--", "comfyui.service"], SystemctlCommands.Autostart(comfy, enabled: false));
    }

    [Fact]
    public void Arguments_UserScope_GoThroughTheUsersManager()
    {
        var lm = HostAgentFixtures.Config().Find("lmstudio-server")!;

        Assert.Equal(["--user", "-M", "dgxuser@", "restart", "--no-block", "--no-ask-password", "--", "lmstudio-server.service"],
            SystemctlCommands.Action(lm, UnitAction.Restart));
        Assert.Equal(["--user", "-M", "dgxuser@", "enable", "--no-ask-password", "--", "lmstudio-server.service"],
            SystemctlCommands.Autostart(lm, enabled: true));
        Assert.Equal("show", SystemctlCommands.Show(lm)[3]);
    }

    [Fact]
    public async Task Restart_RunsSystemctlByAbsolutePathWithTheArgumentVector()
    {
        var (manager, runner, _) = CreateManager();
        var lm = manager.Config.Find("lmstudio-server")!;

        var result = await manager.RunAsync(lm, UnitAction.Restart, confirm: null, TestContext.Current.CancellationToken);

        Assert.Equal(ActionOutcome.Done, result.Outcome);
        var call = runner.Calls.First(c => c.Arguments.Contains("restart"));
        Assert.Equal("/usr/bin/systemctl", call.FileName);
        Assert.Equal(["--user", "-M", "dgxuser@", "restart", "--no-block", "--no-ask-password", "--", "lmstudio-server.service"], call.Arguments);
    }

    // ---- Allowlist over HTTP ----

    [Theory]
    [InlineData("POST", "/services/nginx/stop")]
    [InlineData("POST", "/services/nginx/restart")]
    [InlineData("POST", "/services/comfyui.service/stop")]
    [InlineData("POST", "/services/..%2Fetc/stop")]
    [InlineData("GET", "/services/nginx")]
    public async Task Api_UnknownService_Is404AndNeverRunsSystemctl(string method, string path)
    {
        await using var host = await StartAsync();

        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(host.Runner.Calls);
        Assert.Contains(host.Logger.Entries, e => e.Message.Contains("unknown service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Api_UnknownServiceAutostart_Is404AndNeverRunsSystemctl()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.PostAsJsonAsync("/services/nginx/autostart", new { enabled = false }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(host.Runner.Calls);
    }

    [Fact]
    public async Task Api_UnknownAction_Is400AndNeverRunsSystemctl()
    {
        await using var host = await StartAsync();

        using var response = await host.Client.PostAsync("/services/comfyui/kill", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Runner.Calls);
    }

    [Fact]
    public async Task Api_ProtectedStopWithoutConfirm_Is403AndNeverRunsSystemctl()
    {
        await using var host = await StartAsync();

        using var refused = await host.Client.PostAsync("/services/rustdesk/stop", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Empty(host.Runner.SystemctlCalls);

        using var wrong = await host.Client.PostAsync("/services/rustdesk/stop?confirm=RustDesk", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Empty(host.Runner.SystemctlCalls);

        using var ok = await host.Client.PostAsync("/services/rustdesk/stop?confirm=rustdesk", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains(host.Runner.SystemctlCalls, a => a.SequenceEqual(["stop", "--no-block", "--no-ask-password", "--", "rustdesk.service"]));
    }

    [Fact]
    public async Task Api_ListReturnsEveryAllowlistedUnit()
    {
        await using var host = await StartAsync();

        var body = await host.Client.GetFromJsonAsync<ListBody>("/services", TestContext.Current.CancellationToken);

        Assert.Equal(["comfyui", "lmstudio-server", "rustdesk"], body!.Services.Select(s => s.Name));
        Assert.Equal("user", body.Services[1].Scope);
        Assert.True(body.Services[2].Protected);
    }

    // ---- Status parsing ----

    [Fact]
    public void StatusParser_ReadsTheRealShowOutput()
    {
        var props = UnitStatusParser.Parse(HostAgentFixtures.ComfyShow);

        Assert.Equal("active", props.ActiveState);
        Assert.Equal("running", props.SubState);
        Assert.Equal("enabled", props.UnitFileState);
        Assert.Equal(276633, props.MainPid);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790687285), props.ActiveSince);
        Assert.Equal(1649680384L, props.MemoryBytes);
        Assert.Equal("/system.slice/comfyui.service", props.ControlGroup);
    }

    [Fact]
    public void StatusParser_UnsetValuesBecomeNull()
    {
        var props = UnitStatusParser.Parse("MemoryCurrent=[not set]\nActiveState=inactive\nUnitFileState=\nActiveEnterTimestamp=\nMainPID=0\nControlGroup=\n");

        Assert.Equal("inactive", props.ActiveState);
        Assert.Null(props.MemoryBytes);
        Assert.Null(props.ActiveSince);
        Assert.Null(props.MainPid);
        Assert.Null(props.ControlGroup);
        Assert.Null(UnitStatusParser.Parse("MemoryCurrent=18446744073709551615").MemoryBytes);
    }

    // ---- GPU → unit via cgroup ----

    [Fact]
    public void Gpu_ParsesComputeAppsAndCgroupFiles()
    {
        var apps = GpuCgroupMapper.ParseComputeApps(HostAgentFixtures.ComputeApps + "\n999, [N/A]\n");

        Assert.Equal(4, apps.Count);
        Assert.Equal(40144L * 1024 * 1024, apps[276633]);
        Assert.Equal("/system.slice/comfyui.service", GpuCgroupMapper.ParseCgroupFile("0::/system.slice/comfyui.service\n"));
        Assert.Equal("/a", GpuCgroupMapper.ParseCgroupFile("12:memory:/legacy\n0::/a\n"));
    }

    [Fact]
    public void Gpu_SumsOnlyProcessesInTheUnitsCgroupOrItsChildren()
    {
        var apps = GpuCgroupMapper.ParseComputeApps(HostAgentFixtures.ComputeApps);
        var reader = new FakeCgroupReader(new()
        {
            [213208] = "/system.slice/docker-e5ae.scope",
            [276633] = "/system.slice/comfyui.service",
            [277328] = "/system.slice/ollama.service",
            [280674] = "/system.slice/ollama.service/runner",
        });

        Assert.Equal(40144L * 1024 * 1024, GpuCgroupMapper.SumForCgroup("/system.slice/comfyui.service", apps, reader));
        Assert.Equal((7019L + 2668) * 1024 * 1024, GpuCgroupMapper.SumForCgroup("/system.slice/ollama.service", apps, reader));
        Assert.Equal(0L, GpuCgroupMapper.SumForCgroup("/system.slice/comfyui", apps, reader));
        Assert.Null(GpuCgroupMapper.SumForCgroup(null, apps, reader));
    }

    [Fact]
    public async Task List_MapsGpuMemoryToTheUnit()
    {
        var reader = new FakeCgroupReader(new() { [276633] = "/system.slice/comfyui.service", [213208] = "/system.slice/docker-x.scope" });
        var (manager, runner, _) = CreateManager(reader);
        runner.Respond = (file, args) => file == SystemctlCommands.NvidiaSmiPath
            ? new ProcessResult(0, HostAgentFixtures.ComputeApps, "")
            : args.Contains("comfyui.service")
                ? new ProcessResult(0, HostAgentFixtures.ComfyShow, "")
                : new ProcessResult(0, "ActiveState=inactive\nSubState=dead\nUnitFileState=disabled\nMemoryCurrent=[not set]\nControlGroup=\n", "");

        var list = await manager.ListAsync(TestContext.Current.CancellationToken);

        var comfy = list.Single(s => s.Name == "comfyui");
        Assert.Equal(40144L * 1024 * 1024, comfy.GpuMemoryBytes);
        Assert.Equal(1649680384L, comfy.MemoryBytes);
        Assert.Equal(1790687285L, comfy.ActiveSinceUnix);
        var rust = list.Single(s => s.Name == "rustdesk");
        Assert.Equal("inactive", rust.ActiveState);
        Assert.Null(rust.GpuMemoryBytes);
        Assert.Null(rust.MemoryBytes);
        Assert.Single(runner.Calls, c => c.FileName == SystemctlCommands.NvidiaSmiPath);
    }

    [Fact]
    public async Task List_NvidiaSmiMissingLeavesGpuUnknownNotZero()
    {
        var (manager, runner, _) = CreateManager();
        runner.Respond = (file, args) => file == SystemctlCommands.NvidiaSmiPath
            ? new ProcessResult(127, "", "not found")
            : new ProcessResult(0, HostAgentFixtures.ComfyShow, "");

        var list = await manager.ListAsync(TestContext.Current.CancellationToken);

        Assert.All(list, s => Assert.Null(s.GpuMemoryBytes));
    }

    // ---- Action logging ----

    [Fact]
    public async Task Actions_AreLoggedWithUnitActionAndResult()
    {
        var (manager, runner, logger) = CreateManager();
        var comfy = manager.Config.Find("comfyui")!;
        var rust = manager.Config.Find("rustdesk")!;

        await manager.RunAsync(comfy, UnitAction.Restart, null, TestContext.Current.CancellationToken);
        await manager.RunAsync(rust, UnitAction.Stop, null, TestContext.Current.CancellationToken);
        runner.Respond = (_, args) => args.Contains("show")
            ? new ProcessResult(0, HostAgentFixtures.ComfyShow, "")
            : new ProcessResult(1, "", "Failed to stop comfyui.service: Access denied\n");
        var failed = await manager.RunAsync(comfy, UnitAction.Stop, null, TestContext.Current.CancellationToken);
        await manager.SetAutostartAsync(rust, enabled: false, confirm: null, TestContext.Current.CancellationToken);

        Assert.Equal(ActionOutcome.Failed, failed.Outcome);
        Assert.Contains("Access denied", failed.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message == "action service=comfyui unit=comfyui.service action=restart result=ok");
        Assert.Contains(logger.Entries, e => e.Message == "action service=rustdesk unit=rustdesk.service action=stop result=refused reason=protected-needs-confirmation");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("action service=comfyui unit=comfyui.service action=stop result=failed reason=exit code 1: Failed to stop", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message == "action service=rustdesk unit=rustdesk.service action=disable result=refused reason=protected-needs-confirmation");
        Assert.DoesNotContain(runner.SystemctlCalls, a => a.Contains("rustdesk.service") && !a.Contains("show"));
    }

    [Fact]
    public async Task ProtectedStart_NeedsNoConfirmation()
    {
        var (manager, runner, _) = CreateManager();

        var result = await manager.RunAsync(manager.Config.Find("rustdesk")!, UnitAction.Start, null, TestContext.Current.CancellationToken);

        Assert.Equal(ActionOutcome.Done, result.Outcome);
        Assert.Contains(runner.SystemctlCalls, a => a.SequenceEqual(["start", "--no-block", "--no-ask-password", "--", "rustdesk.service"]));
    }

    // ---- helpers ----

    private static (UnitManager Manager, FakeProcessRunner Runner, CapturingLogger<UnitManager> Logger) CreateManager(ICgroupReader? reader = null)
    {
        var runner = new FakeProcessRunner();
        var logger = new CapturingLogger<UnitManager>();
        var config = HostAgentFixtures.Config();
        var manager = new UnitManager(config, HostAgentFixtures.Store(config), DenyList.BuiltIn, runner, reader ?? new FakeCgroupReader([]), logger);
        return (manager, runner, logger);
    }

    internal sealed class AgentHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public required HttpClient Client { get; init; }

        public required FakeProcessRunner Runner { get; init; }

        public required CapturingLogger<UnitManager> Logger { get; init; }

        public required WhitelistStore Store { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync(TestContext.Current.CancellationToken);
            await App.DisposeAsync();
        }
    }

    internal static async Task<AgentHost> StartAsync(Func<string, IReadOnlyList<string>, ProcessResult>? respond = null, WhitelistStore? store = null, ICgroupReader? cgroups = null)
    {
        var runner = new FakeProcessRunner();
        if (respond is not null)
        {
            runner.Respond = respond;
        }

        var logger = new CapturingLogger<UnitManager>();
        var config = HostAgentFixtures.Config();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(DenyList.BuiltIn);
        store ??= HostAgentFixtures.Store(config);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.Services.AddSingleton<ICgroupReader>(cgroups ?? new FakeCgroupReader([]));
        builder.Services.AddSingleton<GpuProcessInventory>();
        builder.Services.AddSingleton<ILogger<UnitManager>>(logger);
        builder.Services.AddSingleton<UnitManager>();
        builder.Services.AddSingleton<IUnitManager>(sp => sp.GetRequiredService<UnitManager>());
        var app = builder.Build();
        app.MapHostAgentApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new AgentHost { App = app, Client = app.GetTestClient(), Runner = runner, Logger = logger, Store = store };
    }

    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relative);
    }

    internal sealed class ListBody
    {
        public List<UnitStatusDto> Services { get; set; } = [];
    }
}
