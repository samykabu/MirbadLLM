using MirbadLLM.HostAgent;

namespace MirbadLLM.UnitTests.HostAgent;

/// <summary>
/// The macOS hostd (docs/macos/implementation-plan.md M10, Phase 4): launchd labels as units, the launchd deny list,
/// the parsers against output captured on the Mac (<c>Fixtures/macos-*</c>), and the exact launchctl argument vectors
/// <see cref="LaunchdManager"/> runs for each action, through a fake runner and fake plist folders.
/// </summary>
public sealed class LaunchdTests
{
    private const string Probe = "com.example.probe";
    private const string ProbeName = "com-example-probe";

    // ---- Config, names, deny list ----

    [Fact]
    public void ConfigUnderLaunchdTakesLabelsAndDefaultsTheMacSocketAndGroup()
    {
        var config = HostdConfig.Parse("""
            {"discoverUsers":["macuser"],"services":[
              {"name":"ollama","unit":"homebrew.mxcl.ollama","scope":"user","user":"macuser"},
              {"name":"rustdesk","unit":"com.carriez.RustDesk_service","scope":"system","protected":true,"risk":"Remote desktop."}]}
            """, ServiceFlavor.Launchd);

        Assert.Equal(ServiceFlavor.Launchd, config.Flavor);
        Assert.Equal(HostdConfig.MacDefaultSocket, config.Socket);
        Assert.Equal("_mirbadllm", config.SocketGroup);
        Assert.Equal("homebrew.mxcl.ollama", config.Find("ollama")!.Unit);
        Assert.True(config.IsValidUnitName("com.carriez.RustDesk_service"));
    }

    [Theory]
    [InlineData("""{"services":[{"name":"x","unit":"../evil","scope":"system"}]}""")]
    [InlineData("""{"services":[{"name":"x","unit":"system/com.x","scope":"system"}]}""")]
    [InlineData("""{"services":[{"name":"x","unit":"-k","scope":"system"}]}""")]
    [InlineData("""{"services":[{"name":"x","unit":"com.x y","scope":"system"}]}""")]
    [InlineData("""{"socketGroup":"wheel;id","services":[]}""")]
    public void ConfigUnderLaunchdRejectsLabelsThatCouldBeOptionsOrPaths(string json) =>
        Assert.Throws<FormatException>(() => HostdConfig.Parse(json, ServiceFlavor.Launchd));

    [Fact]
    public void TheMacSeedInTheDeployFolderParses()
    {
        var config = HostdConfig.Parse(File.ReadAllText(HostAgentFixtures.FindRepoFile(Path.Combine("deploy", "agent", "macos", "hostd.macos.json"))),
            ServiceFlavor.Launchd);

        Assert.Equal(HostdConfig.MacDefaultSocket, config.Socket);
        Assert.Equal("_mirbadllm", config.SocketGroup);
        Assert.Empty(config.Services);
        Assert.Empty(config.DiscoverUsers);
    }

    [Theory]
    [InlineData("com.apple.screensharing", "com.apple.*")]
    [InlineData("com.mirbadllm.agent", "com.mirbadllm.*")]
    [InlineData("com.mirbadllm.hostd", "com.mirbadllm.*")]
    [InlineData("actions.runner.example-org.mac-build-1", "actions.runner.*")]
    [InlineData("com.docker.vmnetd", "com.docker.*")]
    [InlineData("com.openssh.sshd", "com.openssh.*")]
    public void TheLaunchdDenyListCoversApplesJobsTheAgentAndTheSharedRunners(string label, string pattern) =>
        Assert.Equal(pattern, new DenyList([], ServiceFlavor.Launchd).Match(label));

    [Theory]
    [InlineData("homebrew.mxcl.ollama")]
    [InlineData("com.carriez.RustDesk_service")]
    [InlineData("com.github.facebook.watchman")]
    public void TheLaunchdDenyListLeavesOrdinaryJobsManageable(string label) =>
        Assert.Null(new DenyList([], ServiceFlavor.Launchd).Match(label));

    [Fact]
    public void TheSystemdDenyListStillAddsTheServiceSuffixAndTheLaunchdOneDoesNot()
    {
        Assert.Equal("foo.service", new DenyList(["foo.service"]).Match("foo"));
        Assert.Null(new DenyList(["foo.service"], ServiceFlavor.Launchd).Match("foo"));
    }

    [Fact]
    public void AWhitelistUnderLaunchdAcceptsLabelsAndRefusesServiceUnitsItCannotRun()
    {
        var config = new HostdConfig(HostdConfig.MacDefaultSocket, [], ["macuser"], ServiceFlavor.Launchd, "_mirbadllm");
        var store = HostAgentFixtures.Store(config, new DenyList([], ServiceFlavor.Launchd));

        Assert.True(store.Add("homebrew.mxcl.ollama", UnitScope.User, "macuser", "Ollama", "", isProtected: false).Ok);
        Assert.Equal("homebrew-mxcl-ollama", store.Units.Single().Name);
        Assert.Equal(WhitelistError.Denied, store.Add("actions.runner.x.y", UnitScope.User, "macuser", null, "", false).Error);
        Assert.Equal(WhitelistError.Invalid, store.Add("a/b", UnitScope.System, null, null, "", false).Error);
    }

    // ---- Parsers (output from the Mac) ----

    [Fact]
    public void PrintOfARunningDaemonGivesItsStateAndPidNotTheNestedOnes()
    {
        var state = LaunchctlParser.ParsePrint(HostAgentFixtures.Fixture("macos-print-running.txt"));

        Assert.True(state.Loaded);
        Assert.Equal("running", state.State);
        Assert.Equal(311, state.Pid);
        Assert.Equal(("active", "running"), LaunchctlParser.States(state));
    }

    [Fact]
    public void PrintOfALoadedJobThatIsNotRunningHasNoPid()
    {
        var state = LaunchctlParser.ParsePrint(HostAgentFixtures.Fixture("macos-print-not-running.txt"));

        Assert.Equal("not running", state.State);
        Assert.Null(state.Pid);
        Assert.Equal(("inactive", "not running"), LaunchctlParser.States(state));
        Assert.Equal(("inactive", "not loaded"), LaunchctlParser.States(JobState.NotLoaded));
        Assert.Equal(("activating", "spawn scheduled"), LaunchctlParser.States(new JobState(true, "spawn scheduled", null)));
    }

    [Fact]
    public void AMissingServiceIsNotLoadedButOtherFailuresAreNot()
    {
        Assert.True(LaunchctlParser.IsNotLoaded(new ProcessResult(113, "", "Bad request.\nCould not find service \"x\" in domain for user gui: 501\n")));
        Assert.False(LaunchctlParser.IsNotLoaded(new ProcessResult(1, "", "Operation not permitted")));
        Assert.False(LaunchctlParser.IsNotLoaded(new ProcessResult(-1, "", "", TimedOut: true)));
    }

    [Fact]
    public void PrintDisabledReadsBothSpellingsAndTheOverrideWinsOverThePlist()
    {
        var real = LaunchctlParser.ParseDisabled(HostAgentFixtures.Fixture("macos-print-disabled-system.txt"));
        Assert.False(real["com.carriez.RustDesk_service"]);
        Assert.True(real["com.apple.CSCSupportd"]);

        var old = LaunchctlParser.ParseDisabled("disabled services = {\n\t\"a.b\" => true\n\t\"c.d\" => false\n}\n");
        Assert.True(old["a.b"]);
        Assert.False(old["c.d"]);

        Assert.Equal("disabled", LaunchctlParser.FileState(old, "a.b", plistDisabled: false));
        Assert.Equal("enabled", LaunchctlParser.FileState(old, "c.d", plistDisabled: true));
        Assert.Equal("disabled", LaunchctlParser.FileState(old, "e.f", plistDisabled: true));
        Assert.Equal("enabled", LaunchctlParser.FileState(old, "e.f", plistDisabled: null));
    }

    [Fact]
    public void APlistGivesItsLabelProgramAndDisabledKeyAndNothingElseCounts()
    {
        var rustdesk = LaunchctlParser.ParsePlist(HostAgentFixtures.Fixture("macos-plist-rustdesk.xml"), "/Library/LaunchDaemons/r.plist")!;
        Assert.Equal("com.carriez.RustDesk_service", rustdesk.Label);
        Assert.Equal("/bin/sh", rustdesk.Program);
        Assert.False(rustdesk.Disabled);

        var program = LaunchctlParser.ParsePlist(Plist("<key>Label</key><string>a.b</string><key>Program</key><string>/opt/x</string><key>Disabled</key><true/>"), "p")!;
        Assert.Equal("/opt/x", program.Program);
        Assert.True(program.Disabled);

        Assert.Null(LaunchctlParser.ParsePlist(Plist("<key>Program</key><string>/opt/x</string>"), "p"));
        Assert.Null(LaunchctlParser.ParsePlist("not xml", "p"));
        Assert.Null(LaunchctlParser.ParsePlist("<html/>", "p"));
    }

    [Fact]
    public void APlistsDoctypeIsNeverFetched()
    {
        // An external entity would be read only if DTDs were processed; they are ignored, so the label stays literal.
        string xml = """
            <?xml version="1.0"?>
            <!DOCTYPE plist [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <plist version="1.0"><dict><key>Label</key><string>a.b</string></dict></plist>
            """;
        Assert.Equal("a.b", LaunchctlParser.ParsePlist(xml, "p")!.Label);
    }

    [Fact]
    public void PsGivesResidentBytesAndTheLocalStartTime()
    {
        var usage = LaunchctlParser.ParsePs("532848 Sat Oct 17 07:04:11 2026    \n", TimeZoneInfo.Utc)!;

        Assert.Equal(532848L * 1024, usage.RssBytes);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 7, 4, 11, TimeSpan.Zero), usage.Started);
        Assert.Null(LaunchctlParser.ParsePs("", TimeZoneInfo.Utc));
        Assert.Null(LaunchctlParser.ParsePs("12 yesterday", TimeZoneInfo.Utc));
    }

    // ---- The manager: exact launchctl vectors ----

    [Fact]
    public async Task StartingAJobThatIsNotLoadedBootstrapsItsPlistThenKicksIt()
    {
        var mac = new FakeMac { Loaded = false };
        var manager = mac.Manager();

        var result = await manager.RunAsync(manager.Whitelist.Find(ProbeName)!, UnitAction.Start, null, CancellationToken.None);

        Assert.Equal(ActionOutcome.Done, result.Outcome);
        Assert.Contains(["bootstrap", "gui/501", FakeMac.ProbePlist], mac.Launchctl);
        Assert.Contains(["kickstart", $"gui/501/{Probe}"], mac.Launchctl);
        Assert.Equal("active", result.Status!.ActiveState);
    }

    [Fact]
    public async Task StoppingUnloadsTheJobSoKeepAliveCannotBringItBack()
    {
        var mac = new FakeMac { Loaded = true };
        var manager = mac.Manager();

        var result = await manager.RunAsync(manager.Whitelist.Find(ProbeName)!, UnitAction.Stop, null, CancellationToken.None);

        Assert.Equal(ActionOutcome.Done, result.Outcome);
        Assert.Contains(["bootout", $"gui/501/{Probe}"], mac.Launchctl);
        Assert.DoesNotContain(mac.Launchctl, a => a[0] is "kill" or "stop");
    }

    [Fact]
    public async Task RestartingALoadedJobKillsAndRestartsItInPlace()
    {
        var mac = new FakeMac { Loaded = true };
        var manager = mac.Manager();

        await manager.RunAsync(manager.Whitelist.Find(ProbeName)!, UnitAction.Restart, null, CancellationToken.None);

        Assert.Contains(["kickstart", "-k", $"gui/501/{Probe}"], mac.Launchctl);
        Assert.DoesNotContain(mac.Launchctl, a => a[0] == "bootstrap");
    }

    [Fact]
    public async Task AutostartIsLaunchdsEnabledOverride()
    {
        var mac = new FakeMac { Loaded = true };
        var manager = mac.Manager();
        var unit = manager.Whitelist.Find(ProbeName)!;

        await manager.SetAutostartAsync(unit, enabled: false, null, CancellationToken.None);
        await manager.SetAutostartAsync(unit, enabled: true, null, CancellationToken.None);

        Assert.Contains(["disable", $"gui/501/{Probe}"], mac.Launchctl);
        Assert.Contains(["enable", $"gui/501/{Probe}"], mac.Launchctl);
    }

    [Fact]
    public async Task StartingADisabledJobSaysToTurnAutostartOnInsteadOfLaunchdsError()
    {
        var mac = new FakeMac { Loaded = false, Disabled = true, BootstrapFails = true };
        var manager = mac.Manager();

        var result = await manager.RunAsync(manager.Whitelist.Find(ProbeName)!, UnitAction.Start, null, CancellationToken.None);

        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Contains("turn autostart on first", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(mac.Launchctl, a => a[0] == "kickstart");
    }

    [Fact]
    public async Task AProtectedJobNeedsItsNameToStopAndNothingRunsWithoutIt()
    {
        var mac = new FakeMac { Loaded = true, Protected = true };
        var manager = mac.Manager();
        var unit = manager.Whitelist.Find(ProbeName)!;

        var refused = await manager.RunAsync(unit, UnitAction.Stop, confirm: null, CancellationToken.None);
        Assert.Equal(ActionOutcome.ConfirmationRequired, refused.Outcome);
        Assert.Empty(mac.Launchctl);

        var done = await manager.RunAsync(unit, UnitAction.Stop, confirm: ProbeName, CancellationToken.None);
        Assert.Equal(ActionOutcome.Done, done.Outcome);
    }

    [Fact]
    public async Task ADeniedLabelIsRefusedBelowTheApiEvenIfItReachedTheWhitelistFile()
    {
        var mac = new FakeMac { Loaded = true };
        var manager = mac.Manager();
        var runner = new AllowedUnit("actions-runner-x", "actions.runner.x.y", UnitScope.User, "macuser", "runner", "", false, null);

        var result = await manager.RunAsync(runner, UnitAction.Stop, null, CancellationToken.None);

        Assert.Equal(ActionOutcome.Denied, result.Outcome);
        Assert.Empty(mac.Launchctl);
    }

    [Fact]
    public async Task WithNobodyLoggedInAUsersJobsAreInTheirUserDomain()
    {
        var mac = new FakeMac { Loaded = true, GuiSession = false };
        var manager = mac.Manager();

        await manager.RunAsync(manager.Whitelist.Find(ProbeName)!, UnitAction.Restart, null, CancellationToken.None);

        Assert.Contains(["kickstart", "-k", $"user/501/{Probe}"], mac.Launchctl);
    }

    [Fact]
    public async Task ASystemJobIsInTheSystemDomain()
    {
        var mac = new FakeMac { Loaded = true };
        var manager = mac.Manager();
        manager.Whitelist.Add("com.example.daemon", UnitScope.System, null, "Daemon", "", false);

        await manager.RunAsync(manager.Whitelist.Find("com-example-daemon")!, UnitAction.Restart, null, CancellationToken.None);

        Assert.Contains(["kickstart", "-k", "system/com.example.daemon"], mac.Launchctl);
    }

    [Fact]
    public async Task StatusReadsPidMemoryAndStartFromPsAndHasNoGpuMemory()
    {
        var mac = new FakeMac { Loaded = true };
        var manager = mac.Manager();

        var status = await manager.GetAsync(manager.Whitelist.Find(ProbeName)!, CancellationToken.None);

        Assert.Equal("active", status.ActiveState);
        Assert.Equal("running", status.SubState);
        Assert.Equal("enabled", status.UnitFileState);
        Assert.Equal(4242, status.MainPid);
        Assert.Equal(1024L * 1024, status.MemoryBytes);
        Assert.NotNull(status.ActiveSinceUnix);
        Assert.Null(status.GpuMemoryBytes);
        Assert.Null(status.Error);
    }

    [Fact]
    public async Task AStoppedJobsStatusReadsOnlyThePlistNamedAfterItsLabel()
    {
        var mac = new FakeMac { Loaded = false };
        mac.Paths.AddPlist("/Users/macuser/Library/LaunchAgents", "/Users/macuser/Library/LaunchAgents/a.other.plist", "a.other");
        mac.Paths.AddPlist("/Users/macuser/Library/LaunchAgents", "/Users/macuser/Library/LaunchAgents/z.other.plist", "z.other");
        var manager = mac.Manager();

        var status = await manager.GetAsync(manager.Whitelist.Find(ProbeName)!, CancellationToken.None);

        Assert.Equal("not loaded", status.SubState);
        Assert.Equal([FakeMac.ProbePlist], mac.Runner.Calls.Where(c => c.FileName == LaunchctlCommands.PlutilPath).Select(c => c.Arguments[^1]));
    }

    [Fact]
    public async Task APlistNamedOtherwiseIsStillFoundByItsLabel()
    {
        var mac = new FakeMac { Loaded = false, ProbePath = "/Users/macuser/Library/LaunchAgents/renamed.plist" };
        var manager = mac.Manager();

        var result = await manager.RunAsync(manager.Whitelist.Find(ProbeName)!, UnitAction.Start, null, CancellationToken.None);

        Assert.Equal(ActionOutcome.Done, result.Outcome);
        Assert.Contains(["bootstrap", "gui/501", "/Users/macuser/Library/LaunchAgents/renamed.plist"], mac.Launchctl);
    }

    [Fact]
    public async Task DiscoveryListsThePlistsOfEachScopeFlaggedDeniedAndSkipsRefusedFiles()
    {
        var mac = new FakeMac { Loaded = true };
        mac.Paths.AddPlist(LaunchdPaths.SystemDaemons, "/Library/LaunchDaemons/com.mirbadllm.agent.plist", "com.mirbadllm.agent");
        mac.Paths.AddPlist(LaunchdPaths.SystemDaemons, "/Library/LaunchDaemons/evil.plist", "com.example.evil", refused: true);
        var manager = mac.Manager();

        var discovery = await manager.DiscoverAsync(CancellationToken.None);

        var agent = discovery.Discovered.Single(d => d.Unit == "com.mirbadllm.agent");
        Assert.True(agent.Denied);
        Assert.Equal("system", agent.Scope);
        var probe = discovery.Discovered.Single(d => d.Unit == Probe);
        Assert.Equal("user", probe.Scope);
        Assert.Equal("macuser", probe.User);
        Assert.True(probe.Managed);
        Assert.Equal("/bin/sleep", probe.Description);
        Assert.DoesNotContain(discovery.Discovered, d => d.Unit == "com.example.evil");
        Assert.Empty(discovery.DiscoveryErrors);
    }

    [Fact]
    public async Task AUserHostdDoesNotDiscoverIsNeverAskedFor()
    {
        var mac = new FakeMac { Loaded = true };
        var manager = mac.Manager();
        var stranger = new AllowedUnit("x", "com.example.x", UnitScope.User, "mallory", "x", "", false, null);

        var status = await manager.GetAsync(stranger, CancellationToken.None);

        Assert.Equal("unknown", status.ActiveState);
        Assert.DoesNotContain(mac.Runner.Calls, c => c.Arguments.Contains("mallory"));
    }

    private static string Plist(string body) =>
        $"""<?xml version="1.0"?><plist version="1.0"><dict>{body}</dict></plist>""";

    /// <summary>A Mac with one LaunchAgent (<see cref="Probe"/>, /bin/sleep) for user macuser (uid 501), scripted launchctl.</summary>
    private sealed class FakeMac
    {
        public const string ProbePlist = "/Users/macuser/Library/LaunchAgents/com.example.probe.plist";

        public bool Loaded { get; set; }

        public bool Disabled { get; set; }

        public bool BootstrapFails { get; set; }

        public bool GuiSession { get; set; } = true;

        public bool Protected { get; set; }

        public string ProbePath { get; set; } = ProbePlist;

        public FakeProcessRunner Runner { get; } = new();

        public FakePaths Paths { get; } = new();

        public List<string[]> Launchctl =>
            [.. Runner.Calls.Where(c => c.FileName == LaunchctlCommands.LaunchctlPath && c.Arguments[0] is not ("print" or "print-disabled"))
                .Select(c => c.Arguments.ToArray())];

        public LaunchdManager Manager()
        {
            Paths.AddPlist("/Users/macuser/Library/LaunchAgents", ProbePath, Probe);
            var config = new HostdConfig(HostdConfig.MacDefaultSocket, [], ["macuser"], ServiceFlavor.Launchd, "_mirbadllm");
            var deny = new DenyList([], ServiceFlavor.Launchd);
            var store = HostAgentFixtures.Store(config, deny);
            store.Add(Probe, UnitScope.User, "macuser", "Probe", "", Protected);
            Runner.Respond = Respond;
            return new LaunchdManager(config, store, deny, Runner, Paths, new CapturingLogger<UnitManager>());
        }

        private ProcessResult Respond(string file, IReadOnlyList<string> args)
        {
            if (file == LaunchctlCommands.IdPath)
            {
                return args[^1] == "macuser" ? new ProcessResult(0, "501\n", "") : new ProcessResult(1, "", "no such user");
            }

            if (file == LaunchctlCommands.PsPath)
            {
                return new ProcessResult(0, "1024 Sat Oct 17 07:04:11 2026\n", "");
            }

            if (file == LaunchctlCommands.PlutilPath)
            {
                return Paths.Xml.TryGetValue(args[^1], out string? xml) ? new ProcessResult(0, xml, "") : new ProcessResult(1, "", "no file");
            }

            switch (args[0])
            {
                case "print" when args[1] is "gui/501":
                    return GuiSession ? new ProcessResult(0, "gui/501 = {\n}\n", "") : new ProcessResult(113, "", "Bad request.");
                case "print" when args[1] is "user/501" or "system":
                    return new ProcessResult(0, "", "");
                case "print":
                    return Loaded
                        ? new ProcessResult(0, $"{args[1]} = {{\n\tstate = running\n\tpid = 4242\n}}\n", "")
                        : new ProcessResult(113, "", $"Could not find service \"{args[1]}\"");
                case "print-disabled":
                    return new ProcessResult(0, $"\tdisabled services = {{\n\t\t\"{Probe}\" => {(Disabled ? "disabled" : "enabled")}\n\t}}\n", "");
                case "bootstrap":
                    if (BootstrapFails)
                    {
                        return new ProcessResult(5, "", "Bootstrap failed: 5: Input/output error");
                    }

                    Loaded = true;
                    return new ProcessResult(0, "", "");
                case "bootout":
                    Loaded = false;
                    return new ProcessResult(0, "", "");
                case "disable":
                    Disabled = true;
                    return new ProcessResult(0, "", "");
                case "enable":
                    Disabled = false;
                    return new ProcessResult(0, "", "");
                default:
                    return new ProcessResult(0, "", "");
            }
        }
    }

    /// <summary>Plist folders in memory: each file's XML (what plutil would print), and which ones the checks refuse.</summary>
    private sealed class FakePaths : LaunchdPaths
    {
        private readonly Dictionary<string, List<string>> _folders = new(StringComparer.Ordinal);
        private readonly HashSet<string> _refused = new(StringComparer.Ordinal);

        public Dictionary<string, string> Xml { get; } = new(StringComparer.Ordinal);

        public void AddPlist(string folder, string path, string label, bool refused = false)
        {
            if (!_folders.TryGetValue(folder, out var files))
            {
                _folders[folder] = files = [];
            }

            if (!files.Contains(path))
            {
                files.Add(path);
            }

            Xml[path] = Plist($"<key>Label</key><string>{label}</string><key>ProgramArguments</key><array><string>/bin/sleep</string><string>60</string></array>");
            if (refused)
            {
                _refused.Add(path);
            }
        }

        public override IReadOnlyList<string> ListPlists(string folder) => _folders.TryGetValue(folder, out var files) ? files : [];

        public override Task<string?> RefuseAsync(string file, string? owner, CancellationToken ct) =>
            Task.FromResult(_refused.Contains(file) ? "not owned by the folder's owner" : null);
    }
}
