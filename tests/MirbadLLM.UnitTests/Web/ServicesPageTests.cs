using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;

namespace MirbadLLM.UnitTests.Web;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>bUnit tests for the Services page.</summary>
public sealed class ServicesPageTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    private sealed class FakeHostServiceManager : IHostServiceManager
    {
        public List<HostServiceStatus> Services { get; } = [];

        public bool AgentAvailable { get; set; } = true;

        public List<(string Name, HostServiceAction Action, string? Confirm)> Runs { get; } = [];

        public List<(string Name, bool Enabled, string? Confirm)> Autostarts { get; } = [];

        public List<DiscoveredService> Discovered { get; } = [];

        public List<HostServiceWhitelistAdd> Adds { get; } = [];

        public List<string> Removed { get; } = [];

        public List<(string Name, HostServiceWhitelistUpdate Update)> Updates { get; } = [];

        public string Password { get; set; } = "s3cret";

        public int DiscoverCalls { get; private set; }

        public Task<HostServicesDiscovery> DiscoverAsync(CancellationToken ct = default)
        {
            DiscoverCalls++;
            return Task.FromResult(new HostServicesDiscovery(Discovered.ToList(), AgentAvailable, null, []));
        }

        public Task<HostServiceStatus> AddToWhitelistAsync(HostServiceWhitelistAdd request, CancellationToken ct = default)
        {
            if (request.User?.Identity?.IsAuthenticated != true)
            {
                throw new AdminReauthenticationException(AdminReauthOutcome.LoginRequired, "Sign in to the portal to perform this action.");
            }

            Adds.Add(request);
            var d = Discovered.Single(x => x.Unit == request.Unit);
            int i = Discovered.IndexOf(d);
            Discovered[i] = d with { Managed = true };
            var status = Svc(d.Name, request.DisplayName ?? d.Name, d.Kind, d.State, d.MemoryBytes, d.GpuMemoryBytes, isProtected: request.Protected);
            Services.Add(status);
            return Task.FromResult(status);
        }

        public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default)
        {
            Removed.Add(name);
            Services.RemoveAll(x => x.Name == name);
            return Task.CompletedTask;
        }

        public Task<HostServiceStatus> UpdateWhitelistAsync(string name, HostServiceWhitelistUpdate request, CancellationToken ct = default)
        {
            var current = Services.Single(x => x.Name == name);
            if (current.Protected && request.Protected == false && request.User?.Identity?.IsAuthenticated != true)
            {
                throw new AdminReauthenticationException(AdminReauthOutcome.LoginRequired, "Sign in to the portal to perform this action.");
            }

            Updates.Add((name, request));
            int i = Services.IndexOf(current);
            Services[i] = current with { Protected = request.Protected ?? current.Protected };
            return Task.FromResult(Services[i]);
        }

        public Task<HostServicesSnapshot> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(new HostServicesSnapshot(Services.ToList(), AgentAvailable, AgentAvailable ? null : "The host agent is not installed (no socket)."));

        public Task<HostServiceStatus> RunAsync(string name, HostServiceAction action, string? confirm = null, CancellationToken ct = default)
        {
            var s = Services.Single(x => x.Name == name);
            if (s.Protected && action != HostServiceAction.Start && confirm != name)
            {
                throw new HostServiceConfirmationRequiredException("needs name");
            }

            Runs.Add((name, action, confirm));
            return Task.FromResult(s);
        }

        public Task<HostServiceStatus> SetAutostartAsync(string name, bool enabled, string? confirm = null, CancellationToken ct = default)
        {
            Autostarts.Add((name, enabled, confirm));
            int i = Services.FindIndex(x => x.Name == name);
            Services[i] = Services[i] with { Autostart = enabled, AutostartDetail = enabled ? "unless-stopped" : "no" };
            return Task.FromResult(Services[i]);
        }
    }

    private static HostServiceStatus Svc(string name, string display, HostServiceKind kind, HostServiceState state,
        long? ram, long? gpu, bool? autostart = true, bool isProtected = false, string? risk = null) =>
        new(name, display, "", kind, kind == HostServiceKind.Container ? name : $"{name}.service", isProtected, risk, state,
            state == HostServiceState.Running ? "running" : "dead", autostart, autostart switch { true => "enabled", false => "disabled", _ => "static" },
            state == HostServiceState.Running ? DateTimeOffset.UtcNow.AddDays(-2).AddHours(-3) : null, ram, gpu);

    private static DiscoveredService Found(string name, string unit, HostServiceKind kind, long? ram, long? gpu = null,
        string description = "", bool managed = false, string? deniedBy = null) =>
        new(name, unit, kind, description, HostServiceState.Running, "running", "enabled", ram, gpu, managed, deniedBy is not null, deniedBy);

    private sealed class FakeReauth(bool enabled) : IAdminReauthenticator
    {
        public bool LoginEnabled => enabled;

        public AdminReauthOutcome Verify(System.Security.Claims.ClaimsPrincipal? user) => AdminReauthOutcome.Verified;
    }

    private static (BunitContext Ctx, FakeHostServiceManager Manager) Setup(bool loginEnabled = true, bool signedIn = true)
    {
        var ctx = new BunitContext();
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetClaims(TestAdmin.User.Claims.ToArray());
        if (!signedIn) auth.SetNotAuthorized();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var manager = new FakeHostServiceManager();
        manager.Services.AddRange(
        [
            Svc("comfyui", "ComfyUI", HostServiceKind.System, HostServiceState.Running, (long)(1.6 * GiB), (long)(38.4 * GiB)),
            Svc("lmstudio-server", "LM Studio server", HostServiceKind.User, HostServiceState.Running, 136 * MiB, 0),
            Svc("dgx-dashboard", "DGX Dashboard", HostServiceKind.System, HostServiceState.Failed, null, null, isProtected: true),
            Svc("sharp_wing", "Pangolin tunnel (newt)", HostServiceKind.Container, HostServiceState.Running, 40 * MiB, null, isProtected: true,
                risk: "sharp_wing is the Pangolin tunnel; stopping it can cut remote access to this box."),
            Svc("ss-vllm", "Legacy vLLM (ss-vllm)", HostServiceKind.Container, HostServiceState.Stopped, null, null, autostart: false),
        ]);
        manager.Discovered.AddRange(
        [
            Found("comfyui", "comfyui.service", HostServiceKind.System, (long)(1.6 * GiB), (long)(38.4 * GiB), "ComfyUI", managed: true),
            Found("ollama-embed", "ollama-embed.service", HostServiceKind.System, 4 * GiB, 0, "Ollama embeddings"),
            Found("ssh", "ssh.service", HostServiceKind.System, 17 * MiB, 0, "OpenBSD Secure Shell server", deniedBy: "ssh*"),
            Found("smartmontools", "smartmontools.service", HostServiceKind.System, 2 * MiB, 0, "Self Monitoring and Reporting Technology (SMART) Daemon"),
            Found("pipewire", "pipewire.service", HostServiceKind.User, 12 * MiB, 0, "PipeWire Multimedia Service"),
            Found("grafana", "grafana", HostServiceKind.Container, 300 * MiB, null, "grafana/grafana:11"),
        ]);
        ctx.Services.AddSingleton<IHostServiceManager>(manager);
        ctx.Services.AddSingleton<IAdminReauthenticator>(new FakeReauth(loginEnabled));
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(121, 50)));
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry());
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        ctx.Services.AddSingleton<MirbadLLM.Web.INodeHostServices>(sp => new MirbadLLM.Web.NodeHostServices(sp)); // the composition's singleton (MN-126)
        return (ctx, manager);
    }

    [Fact]
    public void Renders_KindsStatesMemoryAndUptime()
    {
        var (ctx, _) = Setup();
        using var _ = ctx;

        var cut = ctx.Render<Services>();

        Assert.Equal("System service", cut.Find("[data-testid=kind-comfyui]").TextContent);
        Assert.Equal("User service", cut.Find("[data-testid=kind-lmstudio-server]").TextContent);
        Assert.Equal("Container", cut.Find("[data-testid=kind-sharp_wing]").TextContent);

        var comfyState = cut.Find("[data-testid=state-comfyui]");
        Assert.Contains("Running", comfyState.TextContent, StringComparison.Ordinal);
        Assert.NotNull(comfyState.QuerySelector("svg")); // icon + text, never colour alone
        Assert.Contains("Failed", cut.Find("[data-testid=state-dgx-dashboard]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Stopped", cut.Find("[data-testid=state-ss-vllm]").TextContent, StringComparison.Ordinal);

        string comfyMem = cut.Find("[data-testid=memory-comfyui]").TextContent;
        Assert.Contains("1.6 GiB", comfyMem, StringComparison.Ordinal);
        Assert.Contains("38.4 GiB", comfyMem, StringComparison.Ordinal);
        Assert.Contains("136 MiB", cut.Find("[data-testid=memory-lmstudio-server]").TextContent, StringComparison.Ordinal);
        Assert.Contains("—", cut.Find("[data-testid=memory-ss-vllm]").TextContent, StringComparison.Ordinal);
        Assert.Equal("2d 03h", cut.Find("[data-testid=uptime-comfyui]").TextContent);
        Assert.Equal("—", cut.Find("[data-testid=uptime-ss-vllm]").TextContent);

        Assert.NotNull(cut.Find("[data-testid=protected-sharp_wing]"));
        Assert.NotNull(cut.Find("[data-testid=start-ss-vllm]"));
        Assert.NotNull(cut.Find("[data-testid=memory-bar]"));
        Assert.Equal("true", cut.Find("[data-testid=autostart-comfyui]").GetAttribute("aria-checked"));
        Assert.Equal("false", cut.Find("[data-testid=autostart-ss-vllm]").GetAttribute("aria-checked"));
        Assert.Equal("Start ComfyUI at boot", cut.Find("[data-testid=autostart-comfyui]").GetAttribute("aria-label"));
        Assert.Empty(cut.FindAll("[data-testid=host-agent-missing]"));
    }

    [Fact]
    public void StopDialog_StatesTheMemoryEffect()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        cut.Find("[data-testid=stop-comfyui]").Click();

        Assert.Equal("true", cut.Find("[data-testid=service-dialog]").GetAttribute("data-open"));
        Assert.Equal("Stop ComfyUI?", cut.Find("[data-testid=service-dialog] h2").TextContent);
        string effect = cut.Find("[data-testid=service-dialog-effect]").TextContent;
        Assert.Contains("Stopping ComfyUI frees about 40.0 GiB (1.6 GiB RAM + 38.4 GiB GPU).", effect, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=service-dialog-input]"));

        cut.Find("[data-testid=service-dialog-confirm]").Click();

        Assert.Equal([("comfyui", HostServiceAction.Stop, (string?)null)], manager.Runs);
    }

    [Fact]
    public void ProtectedStop_NeedsTheTypedName()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        cut.Find("[data-testid=stop-sharp_wing]").Click();

        Assert.Contains("stopping it can cut remote access", cut.Find("[data-testid=service-dialog-risk]").TextContent, StringComparison.Ordinal);
        var confirm = cut.Find("[data-testid=service-dialog-confirm]");
        Assert.True(confirm.HasAttribute("disabled"));

        cut.Find("[data-testid=service-dialog-input]").Input("sharp");
        Assert.True(cut.Find("[data-testid=service-dialog-confirm]").HasAttribute("disabled"));

        cut.Find("[data-testid=service-dialog-input]").Input("sharp_wing");
        Assert.False(cut.Find("[data-testid=service-dialog-confirm]").HasAttribute("disabled"));
        cut.Find("[data-testid=service-dialog-confirm]").Click();

        Assert.Equal([("sharp_wing", HostServiceAction.Stop, (string?)"sharp_wing")], manager.Runs);
    }

    [Fact]
    public void ProtectedStop_CancelRunsNothing()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        cut.Find("[data-testid=stop-sharp_wing]").Click();
        cut.Find("[data-testid=service-dialog-cancel]").Click();

        Assert.Empty(manager.Runs);
        Assert.Equal("false", cut.Find("[data-testid=service-dialog]").GetAttribute("data-open"));
    }

    [Fact]
    public void AutostartSwitch_CallsTheManager()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        cut.Find("[data-testid=autostart-ss-vllm]").Click();

        Assert.Equal([("ss-vllm", true, (string?)null)], manager.Autostarts);
        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=autostart-ss-vllm]").GetAttribute("aria-checked")));
        Assert.Contains("Autostart for Legacy vLLM (ss-vllm) is now on.", cut.Find("[data-testid=services-notice]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedAutostartOff_AsksForTheTypedName()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        cut.Find("[data-testid=autostart-sharp_wing]").Click();

        Assert.Empty(manager.Autostarts);
        Assert.Equal("Turn off autostart for Pangolin tunnel (newt)?", cut.Find("[data-testid=service-dialog] h2").TextContent);
        cut.Find("[data-testid=service-dialog-input]").Input("sharp_wing");
        cut.Find("[data-testid=service-dialog-confirm]").Click();

        Assert.Equal([("sharp_wing", false, (string?)"sharp_wing")], manager.Autostarts);
    }

    [Fact]
    public void StaticUnit_AutostartCannotBeToggled()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        manager.Services.Add(Svc("telemetry", "Telemetry", HostServiceKind.System, HostServiceState.Running, MiB, 0, autostart: null));
        var cut = ctx.Render<Services>();

        var sw = cut.Find("[data-testid=autostart-telemetry]");
        Assert.True(sw.HasAttribute("disabled"));
        Assert.Contains("static", sw.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingAgent_ShowsInstallInstructionsAndStillListsContainers()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        manager.AgentAvailable = false;
        manager.Services.RemoveAll(s => s.Kind != HostServiceKind.Container);

        var cut = ctx.Render<Services>();

        var callout = cut.Find("[data-testid=host-agent-missing]");
        Assert.Contains("Host agent not installed", callout.TextContent, StringComparison.Ordinal);
        Assert.Contains("sudo bash deploy/hostd/install-hostd.sh", cut.Find("[data-testid=host-agent-install]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=service-row-sharp_wing]"));
    }

    [Fact]
    public void Empty_ExplainsWhereServicesComeFrom()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        manager.Services.Clear();

        var cut = ctx.Render<Services>();

        Assert.Contains("hostd.json", cut.Find("[data-testid=services-empty]").TextContent, StringComparison.Ordinal);
    }

    // ---- Managed / Discovered tabs, one level; the kind is a filter inside Discovered ----

    [Fact]
    public void Renders_ManagedTabByDefault_DiscoveredLoadsLazilyOnTheDiscoveredTab()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;

        var cut = ctx.Render<Services>();

        Assert.Equal("Managed", cut.Find("#services-title").TextContent);
        Assert.Equal("true", cut.Find("[data-testid=services-tabs-tab-managed]").GetAttribute("aria-selected"));
        Assert.Empty(cut.FindAll("[data-testid^=discovered-row-]"));
        Assert.Equal(0, manager.DiscoverCalls); // nothing is listed until the Discovered tab is selected

        cut.Find("[data-testid=services-tabs-tab-discovered]").Click();

        Assert.Equal(1, manager.DiscoverCalls);
        // Every kind is listed by default (one table with a Kind column); the kind filter narrows it.
        Assert.True(cut.Find("[data-testid=discovered-kind-all]").HasAttribute("checked"));
        Assert.NotNull(cut.Find("[data-testid=discovered-group-all]"));
        Assert.Contains("Kind", cut.Find("[data-testid=discovered-group-all] thead").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=managed-system-comfyui]"));
        Assert.Contains("4.0 GiB", cut.Find("[data-testid=discovered-memory-system-ollama-embed]").TextContent, StringComparison.Ordinal);
        Assert.Equal("Container", cut.Find("[data-testid=discovered-kind-of-containers-grafana]").TextContent);
        Assert.NotNull(cut.Find("[data-testid=discovered-row-user-pipewire]"));

        cut.Find("[data-testid=discovered-kind-user]").Change(true);
        Assert.NotNull(cut.Find("[data-testid=discovered-row-user-pipewire]"));
        Assert.Empty(cut.FindAll("[data-testid=discovered-row-containers-grafana]"));
        Assert.DoesNotContain("Kind", cut.Find("[data-testid=discovered-group-user] thead").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=discovered-kind-containers]").Change(true);
        Assert.NotNull(cut.Find("[data-testid=discovered-row-containers-grafana]"));
        Assert.Contains("kind=containers", ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri, StringComparison.Ordinal);

        // No tabs nested inside Discovered: the kind is a radio group.
        Assert.Single(cut.FindAll("[role=tablist]"));
        Assert.Equal("radiogroup", cut.Find("[data-testid=discovered-kind]").GetAttribute("role"));
    }

    [Fact]
    public void TabsAndKindFilter_ShowCounts()
    {
        var (ctx, _) = Setup();
        using var _ = ctx;

        var cut = ctx.Render<Services>();

        Assert.Contains("5", cut.Find("[data-testid=services-tabs-tab-managed]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=services-tabs-tab-discovered]").Click();
        Assert.Contains("6", cut.Find("[data-testid=services-tabs-tab-discovered]").TextContent, StringComparison.Ordinal);
        Assert.Equal("6", cut.Find("[data-testid=discovered-kind-all-count]").TextContent);
        Assert.Equal("4", cut.Find("[data-testid=discovered-kind-system-count]").TextContent);
        Assert.Equal("1", cut.Find("[data-testid=discovered-kind-containers-count]").TextContent);
        Assert.Equal("1", cut.Find("[data-testid=discovered-kind-user-count]").TextContent);
    }

    [Fact]
    public void Discovered_KeepsTheManagersMemoryOrder_AndFiltersWithinTheChosenKind()
    {
        var (ctx, _) = Setup();
        using var _ = ctx;

        var cut = ctx.Render<Services>();
        cut.Find("[data-testid=services-tabs-tab-discovered]").Click();
        cut.Find("[data-testid=discovered-kind-system]").Change(true);

        var systemRows = cut.FindAll("[data-testid=discovered-group-system] tbody tr").Select(r => r.GetAttribute("data-testid")).ToList();
        Assert.Equal(["discovered-row-system-comfyui", "discovered-row-system-ollama-embed", "discovered-row-system-ssh", "discovered-row-system-smartmontools"], systemRows);

        cut.Find("[data-testid=discovered-filter]").Input("smart");
        Assert.Single(cut.FindAll("[data-testid=discovered-group-system] tbody tr"));
        Assert.NotNull(cut.Find("[data-testid=discovered-row-system-smartmontools]"));

        // The filter also narrows the other kinds.
        cut.Find("[data-testid=discovered-kind-containers]").Change(true);
        Assert.Contains("Nothing matches", cut.Find("[data-testid=discovered-empty-containers]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=discovered-kind-system]").Change(true);
        cut.Find("[data-testid=discovered-filter]").Input("SHELL");
        Assert.NotNull(cut.Find("[data-testid=discovered-row-system-ssh]")); // matches the description, case-insensitive
    }

    [Fact]
    public void DeniedRows_AreReadOnlyWithTheReason_AndStayVisibleInSystem()
    {
        var (ctx, _) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();
        cut.Find("[data-testid=services-tabs-tab-discovered]").Click();

        var row = cut.Find("[data-testid=discovered-row-system-ssh]");
        Assert.Equal("true", row.GetAttribute("data-denied"));
        Assert.Contains("is-denied", row.ClassName, StringComparison.Ordinal);
        Assert.Empty(row.QuerySelectorAll("button"));
        string reason = cut.Find("[data-testid=denied-system-ssh]").TextContent;
        Assert.Contains("System — not manageable", reason, StringComparison.Ordinal);
        Assert.Contains("ssh*", reason, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=denied-system-ssh] svg")); // icon + text
        Assert.NotNull(cut.Find("[data-testid=add-system-smartmontools]"));
    }

    [Fact]
    public void AddDialog_RequiresTheTypedNameWithoutAPasswordPrompt()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();
        cut.Find("[data-testid=services-tabs-tab-discovered]").Click();

        cut.Find("[data-testid=add-system-smartmontools]").Click();

        Assert.Equal("true", cut.Find("[data-testid=whitelist-dialog]").GetAttribute("data-open"));
        Assert.Equal("Self Monitoring and Reporting Technology (SMART) Daemon", cut.Find("[data-testid=whitelist-display-name]").GetAttribute("value"));
        Assert.True(cut.Find("[data-testid=whitelist-dialog-confirm]").HasAttribute("disabled"));

        cut.Find("[data-testid=whitelist-confirm]").Input("smartmontools");
        Assert.False(cut.Find("[data-testid=whitelist-dialog-confirm]").HasAttribute("disabled"));

        cut.Find("[data-testid=whitelist-confirm]").Input("smartmontools.service");
        Assert.True(cut.Find("[data-testid=whitelist-dialog-confirm]").HasAttribute("disabled")); // wrong name

        cut.Find("[data-testid=whitelist-confirm]").Input("smartmontools");
        cut.Find("[data-testid=whitelist-display-name]").Input("SMART");
        cut.Find("[data-testid=whitelist-protected]").Change(true);
        Assert.False(cut.Find("[data-testid=whitelist-dialog-confirm]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=whitelist-password]"));

        cut.Find("[data-testid=whitelist-dialog-confirm]").Click();

        var add = Assert.Single(manager.Adds);
        Assert.Equal(("smartmontools.service", HostServiceKind.System, "SMART", true, "smartmontools", (string?)null),
            (add.Unit, add.Kind, add.DisplayName, add.Protected, add.Confirm, add.AdminPassword));
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid=whitelist-dialog]").GetAttribute("data-open")));
        Assert.Contains("SMART is now managed.", cut.Find("[data-testid=services-notice]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=services-tabs-tab-managed]").Click();
        Assert.NotNull(cut.Find("[data-testid=service-row-smartmontools]"));
    }

    [Fact]
    public void AddDialog_AnonymousSession_StaysOpenAndChangesNothing()
    {
        var (ctx, manager) = Setup(signedIn: false);
        using var _ = ctx;
        var cut = ctx.Render<Services>();
        cut.Find("[data-testid=services-tabs-tab-discovered]").Click();
        cut.Find("[data-testid=discovered-kind-containers]").Change(true);
        cut.Find("[data-testid=add-containers-grafana]").Click();

        cut.Find("[data-testid=whitelist-confirm]").Input("grafana");
        cut.Find("[data-testid=whitelist-dialog-confirm]").Click();

        Assert.Empty(manager.Adds);
        Assert.Equal("true", cut.Find("[data-testid=whitelist-dialog]").GetAttribute("data-open"));
        Assert.Contains("Sign in to the portal", cut.Find("[data-testid=whitelist-dialog-error]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=whitelist-password]"));
        Assert.False(cut.Find("[data-testid=whitelist-dialog-confirm]").HasAttribute("disabled"));
    }

    [Fact]
    public void Unprotect_AsksForNameAndSession_RemoveOnlyForAConfirm()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        cut.Find("[data-testid=unprotect-dgx-dashboard]").Click();
        Assert.Equal("Unprotect DGX Dashboard?", cut.Find("[data-testid=whitelist-dialog] h2").TextContent);
        cut.Find("[data-testid=whitelist-confirm]").Input("dgx-dashboard");
        cut.Find("[data-testid=whitelist-dialog-confirm]").Click();
        var update = Assert.Single(manager.Updates);
        Assert.Equal(("dgx-dashboard", false, "dgx-dashboard", (string?)null), (update.Name, update.Update.Protected, update.Update.Confirm, update.Update.AdminPassword));

        cut.Find("[data-testid=remove-ss-vllm]").Click();
        Assert.Empty(cut.FindAll("[data-testid=whitelist-password]"));
        Assert.False(cut.Find("[data-testid=whitelist-dialog-confirm]").HasAttribute("disabled"));
        cut.Find("[data-testid=whitelist-dialog-confirm]").Click();
        Assert.Equal(["ss-vllm"], manager.Removed);
    }

    [Fact]
    public void LoginDisabled_ExplainsAndKeepsTheDialogFromConfirming()
    {
        var (ctx, manager) = Setup(loginEnabled: false);
        using var _ = ctx;
        var cut = ctx.Render<Services>();
        cut.Find("[data-testid=services-tabs-tab-discovered]").Click();

        Assert.Contains("MirbadLLM__AdminPassword", cut.Find("[data-testid=whitelist-login-disabled]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=add-system-smartmontools]").Click();

        Assert.NotNull(cut.Find("[data-testid=whitelist-dialog-login-disabled]"));
        Assert.Empty(cut.FindAll("[data-testid=whitelist-password]"));
        Assert.True(cut.Find("[data-testid=whitelist-dialog-confirm]").HasAttribute("disabled"));
        Assert.Empty(manager.Adds);
    }

    [Fact]
    public void InitialTabAndKind_AreReadFromTheQueryString()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("http://localhost/services?tab=discovered&kind=containers");

        var cut = ctx.Render<Services>();

        Assert.Equal(1, manager.DiscoverCalls);
        Assert.Equal("true", cut.Find("[data-testid=services-tabs-tab-discovered]").GetAttribute("aria-selected"));
        Assert.True(cut.Find("[data-testid=discovered-kind-containers]").HasAttribute("checked"));
        Assert.NotNull(cut.Find("[data-testid=discovered-row-containers-grafana]"));
        Assert.Empty(cut.FindAll("[data-testid=discovered-row-system-comfyui]"));
    }

    [Fact]
    public void Bulk_ProtectedRowsCannotBeSelected_AndTheReasonIsShown()
    {
        var (ctx, _) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        Assert.True(cut.Find("[data-testid=select-sharp_wing]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid=select-dgx-dashboard]").HasAttribute("disabled"));
        Assert.Equal("bulk-protected-note", cut.Find("[data-testid=select-sharp_wing]").GetAttribute("aria-describedby"));
        Assert.Contains("Protected services are left out of bulk actions", cut.Find("[data-testid=bulk-protected-note]").TextContent, StringComparison.Ordinal);
        Assert.Equal("Select ComfyUI", cut.Find("[data-testid=select-comfyui]").GetAttribute("aria-label"));

        // "Select all" takes every unprotected row and nothing else.
        cut.Find("[data-testid=select-all]").Change(true);
        Assert.Contains("3", cut.Find("[data-testid=bulk-count]").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=select-comfyui]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=select-ss-vllm]").HasAttribute("checked"));
        Assert.False(cut.Find("[data-testid=select-sharp_wing]").HasAttribute("checked"));
    }

    [Fact]
    public void BulkStop_OneDialog_ListsTheServices_AndTheCombinedMemory_CancelRunsNothing()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        Assert.True(cut.Find("[data-testid=bulk-stop]").HasAttribute("disabled"));
        cut.Find("[data-testid=select-comfyui]").Change(true);
        cut.Find("[data-testid=select-lmstudio-server]").Change(true);
        cut.Find("[data-testid=select-ss-vllm]").Change(true);
        cut.Find("[data-testid=bulk-stop]").Click();

        Assert.Equal("true", cut.Find("[data-testid=bulk-dialog]").GetAttribute("data-open"));
        Assert.Equal("Stop 2 services?", cut.Find("[data-testid=bulk-dialog] h2").TextContent);
        string list = cut.Find("[data-testid=bulk-dialog-list]").TextContent;
        Assert.Contains("ComfyUI", list, StringComparison.Ordinal);
        Assert.Contains("LM Studio server", list, StringComparison.Ordinal);
        Assert.DoesNotContain("Legacy vLLM", list, StringComparison.Ordinal);
        Assert.Contains("Legacy vLLM", cut.Find("[data-testid=bulk-dialog-skipped]").TextContent, StringComparison.Ordinal);

        // 1.6 GiB + 38.4 GiB + 136 MiB: about 40.1 GiB in total, 1.7 GiB of it RAM and 38.4 GiB GPU.
        string effect = cut.Find("[data-testid=bulk-dialog-effect]").TextContent;
        Assert.Contains("40.1 GiB", effect, StringComparison.Ordinal);
        Assert.Contains("1.7 GiB RAM", effect, StringComparison.Ordinal);
        Assert.Contains("38.4 GiB GPU", effect, StringComparison.Ordinal);

        cut.Find("[data-testid=bulk-dialog-cancel]").Click();
        Assert.Equal("false", cut.Find("[data-testid=bulk-dialog]").GetAttribute("data-open"));
        Assert.Empty(manager.Runs);
    }

    [Fact]
    public void BulkRestart_Confirm_RunsEachSelectedService_WithoutTypedConfirmation()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        cut.Find("[data-testid=select-comfyui]").Change(true);
        cut.Find("[data-testid=select-lmstudio-server]").Change(true);
        cut.Find("[data-testid=bulk-restart]").Click();
        Assert.Equal("Restart 2 services", cut.Find("[data-testid=bulk-dialog-confirm]").TextContent.Trim());
        cut.Find("[data-testid=bulk-dialog-confirm]").Click();

        cut.WaitForAssertion(() => Assert.Equal(2, manager.Runs.Count));
        Assert.All(manager.Runs, r => Assert.Equal(HostServiceAction.Restart, r.Action));
        Assert.Equal(["comfyui", "lmstudio-server"], manager.Runs.Select(r => r.Name).ToArray());
        Assert.Contains("2 services", cut.Find("[data-testid=services-notice]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void BulkStart_OnlyTakesStoppedServices()
    {
        var (ctx, _) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        cut.Find("[data-testid=select-comfyui]").Change(true);
        Assert.True(cut.Find("[data-testid=bulk-start]").HasAttribute("disabled"));
        cut.Find("[data-testid=select-ss-vllm]").Change(true);
        cut.Find("[data-testid=bulk-start]").Click();

        Assert.Equal("Start 1 service?", cut.Find("[data-testid=bulk-dialog] h2").TextContent);
        Assert.Contains("50.0 GiB", cut.Find("[data-testid=bulk-dialog-effect]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedFilter_ShowsALiveMatchCount_AndFiltersRows()
    {
        var (ctx, _) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        Assert.Equal("", cut.Find("[data-testid=managed-filter-count]").TextContent);
        Assert.Equal("status", cut.Find("[data-testid=managed-filter-count]").GetAttribute("role"));
        Assert.True(cut.Find("[data-testid=managed-filter]").HasAttribute("data-shortcut-filter"));

        cut.Find("[data-testid=managed-filter]").Input("comfy");

        Assert.Equal("1 of 5 match", cut.Find("[data-testid=managed-filter-count]").TextContent);
        Assert.NotNull(cut.Find("[data-testid=service-row-comfyui]"));
        Assert.Empty(cut.FindAll("[data-testid=service-row-sharp_wing]"));
    }

    [Fact]
    public void DiscoveredFilter_ShowsNofMMatch_AndTheKindFilterCountsMatches()
    {
        var (ctx, _) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();
        cut.Find("[data-testid=services-tabs-tab-discovered]").Click();

        cut.Find("[data-testid=discovered-filter]").Input("ollama");

        Assert.Equal("1 of 6 match", cut.Find("[data-testid=discovered-filter-count]").TextContent);
        Assert.Equal("1", cut.Find("[data-testid=discovered-kind-all-count]").TextContent);
        Assert.Equal("1", cut.Find("[data-testid=discovered-kind-system-count]").TextContent);
        Assert.Equal("0", cut.Find("[data-testid=discovered-kind-containers-count]").TextContent);
    }

    [Fact]
    public void Tabs_ReadAsLabelAndCount_TheKindIsASegmentedFilter_TablesScrollAndStackByContainer()
    {
        var (ctx, _) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        Assert.Equal("Managed, 5 services", System.Text.RegularExpressions.Regex.Replace(cut.Find("[data-testid=services-tabs-tab-managed]").TextContent, @"\s+", " ").Trim());
        Assert.Contains("tabs--line", cut.Find("[data-testid=services-tabs]").ClassName, StringComparison.Ordinal);

        var table = cut.Find("[data-testid=services-table]");
        Assert.Contains("data-table--stack-wide", table.ClassName, StringComparison.Ordinal);
        Assert.Contains("panel--scroll-x", table.ParentElement!.ClassName, StringComparison.Ordinal);

        cut.Find("[data-testid=services-tabs-tab-discovered]").Click();
        Assert.Contains("segmented", cut.Find("[data-testid=discovered-kind]").ClassName, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=discovered-tabs]"));
        var discovered = cut.Find(".discovered-table");
        Assert.Contains("panel--scroll-x", discovered.ParentElement!.ClassName, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_CarriesAnUpdatedStamp_FromTheSharedRefresh()
    {
        var (ctx, _) = Setup();
        using var _ = ctx;
        var cut = ctx.Render<Services>();

        var stamp = cut.Find("[data-testid=services-updated]");
        Assert.False(string.IsNullOrEmpty(stamp.GetAttribute("data-live-at")));
        Assert.NotNull(stamp.QuerySelector("[data-live-text]"));
    }

    [Fact]
    public void Managed_IsPaged_AndTheFilterGoesBackToPageOne_WithTheMatchCountStillOverEverything()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        for (int i = 0; i < 30; i++)
        {
            manager.Services.Add(Svc($"extra-{i:00}", $"Extra {i:00}", HostServiceKind.System, HostServiceState.Running, MiB, 0));
        }

        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = ctx.Render<Services>();

        Assert.Equal(25, cut.FindAll("[data-testid=services-table] tbody tr").Count);
        Assert.Equal("1–25 of 35 services", cut.Find("[data-testid=managed-pager-range]").TextContent);

        cut.Find("[data-testid=managed-pager-next]").Click();
        Assert.Equal(10, cut.FindAll("[data-testid=services-table] tbody tr").Count);
        Assert.Contains("managed-page=2", nav.Uri, StringComparison.Ordinal);
        Assert.Equal("page", cut.Find("[data-testid=managed-pager-page-2]").GetAttribute("aria-current"));

        // "Select all" covers the rows on this page only.
        cut.Find("[data-testid=select-all]").Change(true);
        Assert.Contains("10", cut.Find("[data-testid=bulk-count]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=managed-filter]").Input("extra-0");
        Assert.Equal(10, cut.FindAll("[data-testid=services-table] tbody tr").Count); // extra-00..09, back on page 1
        Assert.Equal("10 of 35 match", cut.Find("[data-testid=managed-filter-count]").TextContent);
        Assert.DoesNotContain("managed-page=", nav.Uri, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=managed-pager]")); // one page: no pager
    }

    [Fact]
    public void Discovered_IsPaged_AndSwitchingKindGoesBackToPageOne()
    {
        var (ctx, manager) = Setup();
        using var _ = ctx;
        for (int i = 0; i < 40; i++)
        {
            manager.Discovered.Add(Found($"unit-{i:00}", $"unit-{i:00}.service", HostServiceKind.System, MiB, 0, $"Unit {i:00}"));
        }

        var cut = ctx.Render<Services>();
        cut.Find("[data-testid=services-tabs-tab-discovered]").Click();
        cut.Find("[data-testid=discovered-kind-system]").Change(true);

        Assert.Equal(25, cut.FindAll("[data-testid=discovered-group-system] tbody tr").Count);
        Assert.Empty(cut.FindAll("[data-testid^=discovered-more-]"));
        Assert.Equal("1–25 of 44 services", cut.Find("[data-testid=discovered-pager-range]").TextContent);

        cut.Find("[data-testid=discovered-pager-last]").Click();
        Assert.Equal(19, cut.FindAll("[data-testid=discovered-group-system] tbody tr").Count);

        cut.Find("[data-testid=discovered-kind-user]").Change(true);
        cut.Find("[data-testid=discovered-kind-system]").Change(true);
        Assert.Equal(25, cut.FindAll("[data-testid=discovered-group-system] tbody tr").Count);
        Assert.Equal("1–25 of 44 services", cut.Find("[data-testid=discovered-pager-range]").TextContent);

        cut.Find("[data-testid=discovered-filter]").Input("unit-1");
        Assert.Equal(10, cut.FindAll("[data-testid=discovered-group-system] tbody tr").Count);
        Assert.Equal("10 of 46 match", cut.Find("[data-testid=discovered-filter-count]").TextContent);
    }
}
