using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MirbadLLM.HostAgent;

namespace MirbadLLM.UnitTests.HostAgent;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>mirbadllm-hostd: deny list, discovery from real DGX output, the persisted whitelist and its API.</summary>
public sealed class DiscoveryAndWhitelistTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ---- Deny list ----

    [Theory]
    [InlineData("ssh.service", "ssh*")]
    [InlineData("sshd", "ssh*")]
    [InlineData("SSH.Service", "ssh*")]
    [InlineData("systemd-journald.service", "systemd-*")]
    [InlineData("systemd-networkd.service", "systemd-*")]
    [InlineData("dbus.service", "dbus*")]
    [InlineData("dbus-org.freedesktop.hostname1.service", "dbus*")]
    [InlineData("docker.service", "docker*")]
    [InlineData("containerd.service", "containerd*")]
    [InlineData("NetworkManager-wait-online.service", "NetworkManager*")]
    [InlineData("networkmanager.service", "NetworkManager*")]
    [InlineData("wpa_supplicant.service", "wpa_supplicant*")]
    [InlineData("polkit.service", "polkit*")]
    [InlineData("getty@tty1.service", "getty*")]
    [InlineData("serial-getty@ttyS0.service", "serial-getty*")]
    [InlineData("nvidia-persistenced.service", "nvidia-persistenced*")]
    [InlineData("nvidia-fabricmanager.service", "nvidia-fabricmanager*")]
    [InlineData("nvidia-open-driver-helper.service", "nvidia-*driver*")]
    [InlineData("udev.service", "udev*")]
    [InlineData("cron.service", "cron*")]
    [InlineData("rsyslog.service", "rsyslog*")]
    [InlineData("snapd.seeded.service", "snapd*")]
    [InlineData("apparmor.service", "apparmor*")]
    [InlineData("ufw.service", "ufw*")]
    [InlineData("chrony.service", "chrony*")]
    [InlineData("auditd.service", "auditd*")]
    [InlineData("user@1000.service", "user@*")]
    [InlineData("user@126.service", "user@*")]
    [InlineData("mirbadllm-hostd.service", "mirbadllm-hostd*")]
    [InlineData("gdm.service", "gdm*")]
    [InlineData("display-manager.service", "display-manager*")]
    [InlineData("accounts-daemon.service", "accounts-daemon*")]
    [InlineData("unattended-upgrades.service", "unattended-upgrades*")]
    [InlineData("multipathd.service", "multipathd*")]
    [InlineData("lvm2-monitor.service", "lvm2*")]
    public void Deny_MatchesSystemServices(string unit, string pattern) =>
        Assert.Equal(pattern, DenyList.BuiltIn.Match(unit));

    [Theory]
    [InlineData("comfyui.service")]
    [InlineData("ollama.service")]
    [InlineData("smartmontools.service")]
    [InlineData("nvidia-dgx-telemetry.service")]
    [InlineData("rustdesk.service")]
    [InlineData("user-runtime-dir@1000.service")]
    [InlineData("lmstudio-server")]
    [InlineData("")]
    public void Deny_LeavesOrdinaryServicesAlone(string unit) =>
        Assert.Null(DenyList.BuiltIn.Match(unit));

    [Fact]
    public void Deny_ExtraFileCanOnlyAddPatterns()
    {
        var extra = DenyList.ParseExtra("""{ "patterns": ["rustdesk*", "Foo*Bar"] } """);
        var deny = new DenyList(extra);

        Assert.Equal("rustdesk*", deny.Match("rustdesk.service"));
        Assert.Equal("Foo*Bar", deny.Match("foo-x-bar"));
        Assert.Equal("ssh*", deny.Match("ssh.service")); // built-ins stay
        Assert.Equal(DenyList.BuiltInPatterns.Count + 2, deny.Patterns.Count);
        Assert.Throws<FormatException>(() => DenyList.ParseExtra("""{ "patterns": ["a b"] }"""));
        Assert.Throws<FormatException>(() => DenyList.ParseExtra("""{ "patterns": ["x;rm"] }"""));
        Assert.Throws<JsonException>(() => DenyList.ParseExtra("not json"));
    }

    // ---- Discovery parsing (real DGX output) ----

    [Fact]
    public void Candidates_FromRealListings_SkipTemplatesAliasesMaskedAndNotFound()
    {
        var names = UnitDiscovery.Candidates(HostAgentFixtures.Fixture("list-units-system.json"), HostAgentFixtures.Fixture("list-unit-files-system.json"));

        Assert.Contains("comfyui.service", names);
        Assert.Contains("smartmontools.service", names);
        Assert.Contains("getty@tty1.service", names);                     // an instance is a real unit
        Assert.DoesNotContain("getty@.service", names);                    // template without instance
        Assert.DoesNotContain("modprobe@.service", names);
        Assert.DoesNotContain("dbus-org.freedesktop.hostname1.service", names); // alias
        Assert.DoesNotContain("alsa-utils.service", names);                // masked
        Assert.DoesNotContain("auditd.service", names);                    // not-found
        Assert.DoesNotContain("sshd.service", names);                      // not-found
        Assert.All(names, n => Assert.EndsWith(".service", n, StringComparison.Ordinal));
        Assert.Equal(names.Distinct().Count(), names.Count);
    }

    [Fact]
    public async Task Discover_FromRealOutput_FlagsManagedAndDenied_WithMemoryAndGpu()
    {
        var reader = new FakeCgroupReader(new() { [276633] = "/system.slice/comfyui.service", [277328] = "/system.slice/ollama.service" });
        var (manager, _) = CreateManager(HostAgentFixtures.DgxResponder(), reader);

        var result = await manager.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.DiscoveryErrors);
        var comfy = result.Discovered.Single(d => d.Unit == "comfyui.service");
        Assert.True(comfy.Managed);
        Assert.Equal("comfyui", comfy.Name);
        Assert.False(comfy.Denied);
        Assert.Equal(40144L * 1024 * 1024, comfy.GpuMemoryBytes);
        Assert.True(comfy.MemoryBytes > 1_000_000_000);

        var smart = result.Discovered.Single(d => d.Unit == "smartmontools.service");
        Assert.False(smart.Managed);
        Assert.False(smart.Denied);
        Assert.Equal("smartmontools", smart.Name);
        Assert.Equal("Self Monitoring and Reporting Technology (SMART) Daemon", smart.Description);
        Assert.Equal("active", smart.ActiveState);
        Assert.Equal(2269184L, smart.MemoryBytes);

        var ssh = result.Discovered.Single(d => d.Unit == "ssh.service" && d.Scope == "system");
        Assert.True(ssh.Denied);
        Assert.Equal("ssh*", ssh.DeniedBy);
        Assert.Contains(result.Discovered, d => d.Unit == "user@1000.service" && d.Denied && d.DeniedBy == "user@*");

        var lm = result.Discovered.Single(d => d.Unit == "lmstudio-server.service");
        Assert.Equal("user", lm.Scope);
        Assert.Equal("dgxuser", lm.User);
        Assert.True(lm.Managed);
        Assert.Equal("lmstudio-server", lm.Name);

        // Loaded without a unit file (transient/not-found) never appears; neither do templates.
        Assert.DoesNotContain(result.Discovered, d => d.Unit.Contains("@.", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Discovered, d => d.Unit == "auditd.service");
        Assert.Contains("ssh*", result.DenyPatterns);
        Assert.Equal(3, result.Services.Count); // the whitelist's own status list rides along
    }

    [Fact]
    public async Task Discover_UserScopeFailure_IsReportedButSystemStillListed()
    {
        var dgx = HostAgentFixtures.DgxResponder();
        var (manager, _) = CreateManager((file, args) =>
            args.Count > 0 && args[0] == "--user" ? new ProcessResult(1, "", "Failed to connect to bus: No medium found") : dgx(file, args));

        var result = await manager.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Discovered, d => d.Unit == "comfyui.service");
        Assert.DoesNotContain(result.Discovered, d => d.Scope == "user");
        Assert.Single(result.DiscoveryErrors);
        Assert.Contains("user dgxuser", result.DiscoveryErrors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ShowBlocks_ParseMultiUnitOutput()
    {
        var blocks = UnitStatusParser.ParseBlocks(HostAgentFixtures.Fixture("show-all-system.txt"));

        Assert.True(blocks.Count > 300);
        Assert.Equal("/system.slice/comfyui.service", blocks["comfyui.service"]["ControlGroup"]);
        Assert.Equal("disabled", blocks["ssh.service"]["UnitFileState"]);
        Assert.Contains(@"systemd-fsck@dev-disk-by\x2duuid-CAAB\x2dF3C5.service", blocks.Keys);
    }

    // ---- Denied units are refused by the agent itself ----

    [Theory]
    [InlineData("/services/ssh/stop")]
    [InlineData("/services/sshd/restart")]
    [InlineData("/services/systemd-journald/start")]
    [InlineData("/services/docker.service/stop")]
    [InlineData("/services/user@1000/stop")]
    public async Task Api_ActionOnDeniedUnit_Is403EvenWithConfirm(string path)
    {
        await using var host = await HostAgentTests.StartAsync();

        using var response = await host.Client.PostAsync(path + "?confirm=x", null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("denied", (await ErrorAsync(response)).Code);
        Assert.Empty(host.Runner.SystemctlCalls);
        Assert.Contains(host.Logger.Entries, e => e.Message.Contains("reason=denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Api_WhitelistAddOfDeniedUnit_Is403_NothingPersisted()
    {
        await using var host = await HostAgentTests.StartAsync(HostAgentFixtures.DgxResponder());
        var ct = TestContext.Current.CancellationToken;

        using var response = await host.Client.PostAsJsonAsync("/whitelist", new { unit = "ssh.service", scope = "system", confirm = "ssh" }, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("denied", (await ErrorAsync(response)).Code);
        Assert.DoesNotContain(host.Store.Units, u => u.Unit == "ssh.service");
        Assert.Empty(host.Runner.SystemctlCalls);
    }

    [Fact]
    public async Task Manager_RefusesADeniedUnitEvenIfItReachedTheWhitelist()
    {
        var (manager, runner) = CreateManager();
        var smuggled = new AllowedUnit("ssh", "ssh.service", UnitScope.System, null, "SSH", "", false, null);

        var stop = await manager.RunAsync(smuggled, UnitAction.Stop, null, TestContext.Current.CancellationToken);
        var disable = await manager.SetAutostartAsync(smuggled, false, null, TestContext.Current.CancellationToken);

        Assert.Equal(ActionOutcome.Denied, stop.Outcome);
        Assert.Equal(ActionOutcome.Denied, disable.Outcome);
        Assert.Empty(runner.SystemctlCalls);
    }

    [Fact]
    public void Store_Add_RefusesDeniedUnits()
    {
        var store = HostAgentFixtures.Store();

        var result = store.Add("NetworkManager.service", UnitScope.System, null, null, "", false);

        Assert.Equal(WhitelistError.Denied, result.Error);
        Assert.DoesNotContain(store.Units, u => u.Unit == "NetworkManager.service");
    }

    // ---- Whitelist persistence, seeding and migration ----

    [Fact]
    public void Store_FirstRun_SeedsFromHostdJson_KeepingNamesDisplayNamesAndProtection()
    {
        string path = HostAgentFixtures.TempWhitelistPath();
        var logger = new CapturingLogger<WhitelistStore>();

        var store = HostAgentFixtures.Store(path: path, logger: logger);

        Assert.True(File.Exists(path));
        Assert.Equal(["comfyui", "lmstudio-server", "rustdesk"], store.Units.Select(u => u.Name));
        var rust = store.Find("rustdesk")!;
        Assert.True(rust.Protected);
        Assert.Equal("Remote desktop.", rust.Risk);
        Assert.Equal("RustDesk", rust.DisplayName);
        Assert.Equal("dgxuser", store.Find("lmstudio-server")!.User);
        Assert.Contains(logger.Entries, e => e.Message.Contains("3 units (3 merged from hostd.json)", StringComparison.Ordinal));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(3, doc.RootElement.GetProperty("services").GetArrayLength());
        Assert.Equal(3, doc.RootElement.GetProperty("seeded").GetArrayLength());
    }

    [Fact]
    public void Store_DeployedHostdJson_MigratesAsIs()
    {
        var config = HostdConfig.Parse(File.ReadAllText(HostAgentFixtures.FindRepoFile(Path.Combine("deploy", "hostd", "hostd.json"))));

        var store = HostAgentFixtures.Store(config);

        Assert.Equal(config.Services.Select(s => s.Name), store.Units.Select(u => u.Name));
        Assert.Equal(config.Services.Select(s => (s.DisplayName, s.Protected, s.Risk)), store.Units.Select(u => (u.DisplayName, u.Protected, u.Risk)));
        Assert.Equal(["dgxuser"], config.DiscoverUsers);
    }

    [Fact]
    public void Store_AddRemoveUpdate_PersistAndSurviveARestart_WithBeforeAfterLogged()
    {
        string path = HostAgentFixtures.TempWhitelistPath();
        var logger = new CapturingLogger<WhitelistStore>();
        var store = HostAgentFixtures.Store(path: path, logger: logger);

        var added = store.Add("smartmontools.service", UnitScope.System, null, "SMART", "SMART daemon", isProtected: true);
        Assert.True(added.Ok);
        Assert.Equal("smartmontools", added.Unit!.Name);
        Assert.Equal(WhitelistError.Exists, store.Add("smartmontools.service", UnitScope.System, null, null, "", false).Error);
        Assert.Equal(WhitelistError.ConfirmationRequired, store.Update("smartmontools", null, false, confirm: null).Error);
        Assert.True(store.Update("smartmontools", "SMART disks", false, confirm: "smartmontools").Ok);
        Assert.True(store.Remove("comfyui").Ok);
        Assert.Equal(WhitelistError.NotFound, store.Remove("comfyui").Error);

        var reopened = HostAgentFixtures.Store(path: path);
        Assert.Equal(["lmstudio-server", "rustdesk", "smartmontools"], reopened.Units.Select(u => u.Name));
        Assert.Equal("SMART disks", reopened.Find("smartmontools")!.DisplayName);
        Assert.False(reopened.Find("smartmontools")!.Protected);
        Assert.Null(reopened.Find("comfyui")); // removed seed entries are not re-seeded

        Assert.Contains(logger.Entries, e => e.Message.StartsWith("whitelist add service=smartmontools before=null after={", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.StartsWith("whitelist update service=smartmontools before={", StringComparison.Ordinal)
            && e.Message.Contains("\"protected\":true", StringComparison.Ordinal) && e.Message.Contains("after={", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, e => e.Message.StartsWith("whitelist remove service=comfyui before={", StringComparison.Ordinal) && e.Message.EndsWith("after=null", StringComparison.Ordinal));
    }

    [Fact]
    public void Store_NewHostdJsonEntries_AreMergedOnce()
    {
        string path = HostAgentFixtures.TempWhitelistPath();
        HostAgentFixtures.Store(path: path);
        var more = HostdConfig.Parse("""
            { "services": [
                { "name": "comfyui", "unit": "comfyui.service" },
                { "name": "smart", "unit": "smartmontools.service", "protected": true }
            ] }
            """);

        var merged = HostAgentFixtures.Store(more, path: path);
        Assert.Contains(merged.Units, u => u.Name == "smart" && u.Protected);
        Assert.True(merged.Remove("smart").Ok);

        var again = HostAgentFixtures.Store(more, path: path);
        Assert.Null(again.Find("smart"));
    }

    [Fact]
    public void Store_FailsClosedOnAMalformedFile_AndDropsDeniedEntries()
    {
        string path = HostAgentFixtures.TempWhitelistPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "services": [ { "name": "x", "unit": "x.service; rm -rf /" } ] }""");
        Assert.Throws<FormatException>(() => HostAgentFixtures.Store(path: path));

        File.WriteAllText(path, "{ not json");
        Assert.ThrowsAny<JsonException>(() => HostAgentFixtures.Store(path: path));

        File.WriteAllText(path, """
            { "seeded": ["comfyui", "lmstudio-server", "rustdesk"],
              "services": [ { "name": "ssh", "unit": "ssh.service" }, { "name": "comfyui", "unit": "comfyui.service" } ] }
            """);
        var logger = new CapturingLogger<WhitelistStore>();
        var store = HostAgentFixtures.Store(path: path, logger: logger);
        Assert.Equal(["comfyui"], store.Units.Select(u => u.Name));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("unit=ssh.service reason=denied", StringComparison.Ordinal));
        Assert.DoesNotContain("ssh.service", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("smartmontools.service", "smartmontools")]
    [InlineData("NetworkManager_x.service", "networkmanager-x")]
    [InlineData("foo@bar.service", "foo-bar")]
    [InlineData(@"systemd-fsck@dev-disk-by\x2duuid.service", "systemd-fsck-dev-disk-by-x2duuid")]
    public void DeriveName_IsAValidWhitelistName(string unit, string expected)
    {
        Assert.Equal(expected, HostdConfig.DeriveName(unit));
        Assert.True(HostdConfig.IsValidName(HostdConfig.DeriveName(unit)));
    }

    // ---- Whitelist API ----

    [Fact]
    public async Task Api_WhitelistAdd_NeedsTheTypedName_ThenManagesTheUnit_AndDeleteRemovesIt()
    {
        await using var host = await HostAgentTests.StartAsync(HostAgentFixtures.DgxResponder());
        var ct = TestContext.Current.CancellationToken;

        using var wrong = await host.Client.PostAsJsonAsync("/whitelist", new { unit = "smartmontools.service", scope = "system", confirm = "smartmontools.service" }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("confirm", (await ErrorAsync(wrong)).Code);

        using var missing = await host.Client.PostAsJsonAsync("/whitelist", new { unit = "nope.service", scope = "system", confirm = "nope" }, ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var ok = await host.Client.PostAsJsonAsync("/whitelist",
            new { unit = "smartmontools.service", scope = "system", displayName = "SMART", confirm = "smartmontools" }, ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var status = await ok.Content.ReadFromJsonAsync<UnitStatusDto>(Web, ct);
        Assert.Equal("smartmontools", status!.Name);
        Assert.Equal("SMART", status.DisplayName);
        Assert.Equal("Self Monitoring and Reporting Technology (SMART) Daemon", status.Description);

        var list = await host.Client.GetFromJsonAsync<HostAgentTests.ListBody>("/services", ct);
        Assert.Contains(list!.Services, s => s.Name == "smartmontools");

        using var again = await host.Client.PostAsJsonAsync("/whitelist", new { unit = "smartmontools.service", scope = "system", confirm = "smartmontools" }, ct);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        using var removed = await host.Client.DeleteAsync("/whitelist/smartmontools", ct);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        using var gone = await host.Client.PostAsync("/services/smartmontools/restart", null, ct);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);

        // Only read-only systemctl calls happened: nothing was started, stopped or enabled.
        Assert.All(host.Runner.SystemctlCalls, a => Assert.Contains(a, x => x is "show" or "list-units" or "list-unit-files"));
    }

    [Fact]
    public async Task Api_Patch_UnprotectNeedsConfirm_RenameDoesNot()
    {
        await using var host = await HostAgentTests.StartAsync();
        var ct = TestContext.Current.CancellationToken;

        using var refused = await SendPatchAsync(host.Client, "/whitelist/rustdesk", new { @protected = false }, ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True(host.Store.Find("rustdesk")!.Protected);

        using var renamed = await SendPatchAsync(host.Client, "/whitelist/rustdesk", new { displayName = "Remote desktop" }, ct);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        using var ok = await SendPatchAsync(host.Client, "/whitelist/rustdesk", new { @protected = false, confirm = "rustdesk" }, ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.False(host.Store.Find("rustdesk")!.Protected);
        Assert.Equal("Remote desktop", host.Store.Find("rustdesk")!.DisplayName);
    }

    [Fact]
    public async Task Api_UserScopeAdd_OnlyForDiscoverUsers()
    {
        await using var host = await HostAgentTests.StartAsync(HostAgentFixtures.DgxResponder());
        var ct = TestContext.Current.CancellationToken;

        using var other = await host.Client.PostAsJsonAsync("/whitelist", new { unit = "pipewire.service", scope = "user", user = "root", confirm = "pipewire" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);

        using var ok = await host.Client.PostAsJsonAsync("/whitelist", new { unit = "pipewire.service", scope = "user", confirm = "pipewire" }, ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var entry = host.Store.Find("pipewire")!;
        Assert.Equal(UnitScope.User, entry.Scope);
        Assert.Equal("dgxuser", entry.User);
    }

    [Fact]
    public async Task Api_ListAll_ReturnsDiscoveredWithFlags()
    {
        await using var host = await HostAgentTests.StartAsync(HostAgentFixtures.DgxResponder());

        var body = await host.Client.GetFromJsonAsync<DiscoveryDto>("/services?include=all", Web, TestContext.Current.CancellationToken);

        Assert.Contains(body!.Discovered, d => d.Unit == "ssh.service" && d.Denied);
        Assert.Contains(body.Discovered, d => d.Unit == "comfyui.service" && d.Managed);
        Assert.Equal(3, body.Services.Count);
    }

    // ---- helpers ----

    private static (UnitManager Manager, FakeProcessRunner Runner) CreateManager(
        Func<string, IReadOnlyList<string>, ProcessResult>? respond = null, ICgroupReader? reader = null)
    {
        var runner = new FakeProcessRunner();
        if (respond is not null)
        {
            runner.Respond = respond;
        }

        var config = HostAgentFixtures.Config();
        var manager = new UnitManager(config, HostAgentFixtures.Store(config), DenyList.BuiltIn, runner,
            reader ?? new FakeCgroupReader([]), new CapturingLogger<UnitManager>());
        return (manager, runner);
    }

    private static async Task<HttpResponseMessage> SendPatchAsync(HttpClient client, string url, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) };
        return await client.SendAsync(request, ct);
    }

    private static async Task<ErrorBody> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ErrorBody>(Web, TestContext.Current.CancellationToken))!;

    private sealed record ErrorBody(string Error, string Code);
}
