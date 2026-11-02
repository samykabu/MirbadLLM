using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Enrollment;
using MirbadLLM.Agent.Hosting;

namespace MirbadLLM.UnitTests.Deploy;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// The Linux PC packaging (MN-103, <c>deploy/agent/linux</c>) agrees with the agent it installs: the unit's directives,
/// state directory, enrollment condition, writable roots, user and binary line up with the agent's own constants, the
/// install script and <c>agent.linux.example.json</c>; and, where bash runs (Linux), the real script renders an
/// <c>agent.json</c> that binds and validates and refuses unsafe arguments. The full life cycle (systemd, Docker, join,
/// upgrade, rollback, uninstall) is <c>deploy/agent/linux/test/dry-run.sh</c>.
/// </summary>
public sealed partial class LinuxAgentPackagingTests
{
    private const string UnitFile = "mirbadllm-agent.service";

    private static readonly string[] UnitSections = ["Unit", "Service", "Install"];

    private static readonly string[] Verbs = ["install", "upgrade", "rekey", "rollback", "uninstall", "package", "render-config", "render-dropin"];

    /// <summary>bash on Linux; Git for Windows' bash on a Windows dev box (never System32\bash.exe, which is WSL).</summary>
    private static readonly string[] BashLocations = OperatingSystem.IsWindows()
        ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")]
        : ["/bin/bash", "/usr/bin/bash"];

    /// <summary>Directives this unit may use, per section (a typo here would be ignored by systemd with a warning only).</summary>
    private static readonly Dictionary<string, HashSet<string>> KnownDirectives = new(StringComparer.Ordinal)
    {
        ["Unit"] = ["Description", "Documentation", "After", "Wants", "Requires", "ConditionPathExists", "StartLimitIntervalSec", "StartLimitBurst"],
        ["Service"] =
        [
            "Type", "User", "Group", "SupplementaryGroups", "ExecStart", "StateDirectory", "StateDirectoryMode", "UMask",
            "Environment", "Restart", "RestartSec", "RestartPreventExitStatus", "KillSignal", "TimeoutStopSec", "SyslogIdentifier",
            "NoNewPrivileges", "ProtectSystem", "ReadWritePaths", "ProtectHome", "PrivateTmp", "ProtectProc", "ProtectKernelTunables",
            "ProtectKernelModules", "ProtectKernelLogs", "ProtectControlGroups", "ProtectClock", "ProtectHostname",
            "RestrictAddressFamilies", "RestrictNamespaces", "RestrictRealtime", "RestrictSUIDSGID", "RemoveIPC", "LockPersonality",
            "SystemCallArchitectures", "SystemCallFilter", "SystemCallErrorNumber", "CapabilityBoundingSet", "AmbientCapabilities",
        ],
        ["Install"] = ["WantedBy"],
    };

    [Fact]
    public void Unit_UsesOnlyKnownDirectives_InTheirSections()
    {
        List<(string Section, string Key, string Value)> unit = ParseUnit(File.ReadAllText(LinuxFile(UnitFile)));
        Assert.NotEmpty(unit);
        foreach ((string section, string key, _) in unit)
        {
            Assert.True(KnownDirectives.TryGetValue(section, out HashSet<string>? keys), $"unknown section [{section}]");
            Assert.True(keys!.Contains(key), $"[{section}] {key} is not a directive this unit is expected to use");
        }

        Assert.Equal(UnitSections, unit.Select(d => d.Section).Distinct());
    }

    [Fact]
    public void Unit_StateDirectory_AndEnrollmentCondition_MatchTheAgent()
    {
        List<(string Section, string Key, string Value)> unit = Unit();
        string stateDirectory = "/var/lib/" + Single(unit, "StateDirectory");
        AgentOptions example = ExampleOptions();

        Assert.Equal(JoinCommand.DefaultStateDirectory, stateDirectory);
        Assert.Equal(example.StateDirectory, stateDirectory);
        Assert.Equal(stateDirectory, ScriptVariable("STATE_DIR"));

        // Skipped (not crash-looping) until join has written the identity's commit point and the node uid.
        string[] conditions = [.. All(unit, "ConditionPathExists")];
        Assert.Equal(
            new[] { $"{stateDirectory}/{FileAgentCredentials.CertificateFileName}", $"{stateDirectory}/{FileAgentCredentials.NodeUidFileName}" },
            conditions);
        string script = Script();
        Assert.Contains($"[[ -f \"${{STATE_DIR}}/{FileAgentCredentials.CertificateFileName}\" && -s \"${{STATE_DIR}}/{FileAgentCredentials.NodeUidFileName}\" ]]", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Unit_Binary_Config_User_AndRoots_MatchTheScriptAndTheExample()
    {
        List<(string Section, string Key, string Value)> unit = Unit();
        AgentOptions example = ExampleOptions();

        string[] exec = Single(unit, "ExecStart").Split(' ');
        Assert.Equal(ScriptVariable("BINARY").Replace("${PREFIX}", ScriptVariable("PREFIX"), StringComparison.Ordinal), exec[0]);
        Assert.Equal(3, exec.Length);
        Assert.Equal("--config", exec[1]);
        Assert.Equal(ScriptVariable("CONFIG").Replace("${CONFIG_DIR}", ScriptVariable("CONFIG_DIR"), StringComparison.Ordinal), exec[2]);
        Assert.Equal(UnitFile, ScriptVariable("UNIT"));
        Assert.Equal(ScriptVariable("SERVICE_USER"), Single(unit, "User"));
        Assert.Equal(ScriptVariable("SERVICE_USER"), Single(unit, "Group"));
        Assert.Equal("docker", Single(unit, "SupplementaryGroups"));

        // The read-only file system leaves exactly the example's roots writable (the drop-in adds the configured ones).
        Assert.Equal("strict", Single(unit, "ProtectSystem"));
        string[] writable = [.. Single(unit, "ReadWritePaths").Split(' ').Select(p => p.TrimStart('-'))];
        Assert.Equal(new[] { example.ModelsRoot, example.CacheRoot }, writable);
        Assert.Equal(example.ModelsRoot, ScriptVariable("DEFAULT_MODELS_ROOT"));
        Assert.Equal(example.CacheRoot, ScriptVariable("DEFAULT_CACHE_ROOT"));
        Assert.Equal(example.NodeId, ScriptVariable("EXAMPLE_NODE_ID"));
    }

    [Fact]
    public void Unit_Hardening_KeepsWhatTheAgentNeeds()
    {
        List<(string Section, string Key, string Value)> unit = Unit();
        string[] families = Single(unit, "RestrictAddressFamilies").Split(' ');
        // Docker socket (AF_UNIX), the mTLS listener and HTTP to models and Hugging Face (AF_INET/AF_INET6).
        Assert.Contains("AF_UNIX", families);
        Assert.Contains("AF_INET", families);
        Assert.Contains("AF_INET6", families);
        Assert.Equal("yes", Single(unit, "NoNewPrivileges"));
        Assert.Equal("", Single(unit, "CapabilityBoundingSet"));
        Assert.Equal("@system-service", Single(unit, "SystemCallFilter"));

        string[] keys = [.. unit.Select(d => d.Key)];
        Assert.DoesNotContain("MemoryDenyWriteExecute", keys); // the .NET JIT needs W+X pages
        Assert.DoesNotContain("PrivateDevices", keys);         // nvidia-smi opens /dev/nvidia*
        Assert.DoesNotContain("DevicePolicy", keys);
        Assert.DoesNotContain("PrivateNetwork", keys);
        Assert.DoesNotContain("IPAddressDeny", keys);
        Assert.DoesNotContain("PrivateUsers", keys);           // would hide the docker group from the process

        // A configuration error does not restart forever; the drain fits the stop timeout (also DeployArtifactsTests).
        Assert.Equal(global::MirbadLLM.Agent.Program.ExitConfiguration.ToString(System.Globalization.CultureInfo.InvariantCulture), Single(unit, "RestartPreventExitStatus"));
        Assert.Equal("on-failure", Single(unit, "Restart"));
    }

    [Fact]
    public void Script_Constants_MatchTheAgentContracts()
    {
        string script = Script();
        AgentOptions example = ExampleOptions();

        Assert.Equal(Protocol.DefaultAgentPort.ToString(System.Globalization.CultureInfo.InvariantCulture), ScriptVariable("DEFAULT_PORT"));
        Assert.Equal(Protocol.DefaultAgentPort, example.Port);
        Assert.Equal(ContainerLabels.Node, ScriptVariable("NODE_LABEL"));
        Match nodeId = NodeIdCheckRegex().Match(script);
        Assert.True(nodeId.Success, "valid_node_id not found");
        Assert.Equal(Protocol.NodeIdPattern, nodeId.Groups[1].Value);

        // The token travels in the variable join reads, never on argv.
        Assert.Contains($"{JoinCommand.TokenEnvironmentVariable}=\"${{token}}\" runuser -u \"${{SERVICE_USER}}\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("--token \"", script, StringComparison.Ordinal);
        Assert.Contains("join --enroll-url", script, StringComparison.Ordinal);
        Assert.Contains("--state-dir \"${STATE_DIR}\"", script, StringComparison.Ordinal);
        // Lingering for the user manager that runs native processes later (design §6.6).
        Assert.Contains("loginctl enable-linger \"${SERVICE_USER}\"", script, StringComparison.Ordinal);
        Assert.Contains("loginctl disable-linger \"${SERVICE_USER}\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Rekey_StopsTheService_RunsAJoinRekeyTheAgentParses_ThenStartsIt()
    {
        // MN-126: the script's rekey is the master's Linux rekey line (NodeApiEndpoints.RekeyCommands) with checks around it:
        // stop, join --rekey as the service user with the token in the environment, start on the new identity.
        string script = Script();
        int body = script.IndexOf("do_rekey() {", StringComparison.Ordinal);
        Assert.True(body >= 0, "do_rekey not found");
        string rekey = script[body..script.IndexOf("\n}\n", body, StringComparison.Ordinal)];
        int stop = rekey.IndexOf("systemctl stop \"${UNIT}\"", StringComparison.Ordinal);
        int join = rekey.IndexOf("\"${BINARY}\" join --rekey", StringComparison.Ordinal);
        int start = rekey.IndexOf("start_and_check 0", StringComparison.Ordinal);
        Assert.True(stop >= 0 && join > stop && start > join, "stop, join --rekey, start in that order");
        Assert.Contains($"{JoinCommand.TokenEnvironmentVariable}=\"${{token}}\" runuser -u \"${{SERVICE_USER}}\"", rekey, StringComparison.Ordinal);

        Match call = RekeyCallRegex().Match(rekey);
        Assert.True(call.Success, "the join --rekey call not found");
        string line = call.Groups[1].Value.Replace("\\\n", " ", StringComparison.Ordinal)
            .Replace("${uid_args[@]+\"${uid_args[@]}\"}", "--node-uid abc-123", StringComparison.Ordinal)
            .Replace("${addresses[@]+\"${addresses[@]}\"}", "--address 10.0.0.5", StringComparison.Ordinal)
            .Replace("\"${enroll_url}\"", "https://master:8443", StringComparison.Ordinal)
            .Replace("\"${ca_hash}\"", "sha256:" + new string('a', 64), StringComparison.Ordinal)
            .Replace("\"${node_id}\"", "pc-1", StringComparison.Ordinal)
            .Replace("\"${STATE_DIR}\"", ScriptVariable("STATE_DIR"), StringComparison.Ordinal);
        Assert.DoesNotContain("$", line, StringComparison.Ordinal);
        string[] args = [.. line.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Skip(1)]; // the arguments after "join"
        Assert.True(JoinCommand.IsRekey(args));
        Assert.True(JoinCommand.TryParseRekey(args, "env-token", out RekeyParameters? parsed, out string? error), error);
        Assert.Equal(("pc-1", "abc-123", "env-token", new Uri("https://master:8443")), (parsed!.NodeId, parsed.NodeUid, parsed.Token, parsed.EnrollUrl));
        Assert.Equal(JoinCommand.DefaultStateDirectory, parsed.StateDirectory);
    }

    [Fact]
    public void FirewallRule_IsDocumented_ForTheAgentPort()
    {
        string port = Protocol.DefaultAgentPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string readme = File.ReadAllText(LinuxFile("README.md"));
        Assert.Contains($"## Firewall (TCP {port})", readme, StringComparison.Ordinal);
        Assert.Contains($"ufw allow proto tcp from 192.0.2.191 to any port {port}", readme, StringComparison.Ordinal);
        Assert.Contains($"port port={port} protocol=tcp accept", readme, StringComparison.Ordinal);
        Assert.Contains($"tcp dport {port} accept", readme, StringComparison.Ordinal);
        Assert.Contains($"ufw allow proto tcp from <master-ip> to any port {port}", Script(), StringComparison.Ordinal);
        Assert.Contains($"Listens on TCP {port}", File.ReadAllText(LinuxFile(UnitFile)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("install-agent.sh")]
    [InlineData(UnitFile)]
    [InlineData("test/dry-run.sh")]
    [InlineData("test/Dockerfile")]
    [InlineData("../agent.linux.example.json")]
    public void LinuxFiles_UseLfLineEndings(string file)
    {
        // CRLF breaks the shebang and systemd's parser on the PC (.gitattributes keeps these LF).
        Assert.DoesNotContain("\r", File.ReadAllText(LinuxFile(file)), StringComparison.Ordinal);
    }

    // ---- the real script (bash on Linux) ----

    [Fact]
    public async Task RenderConfig_BindsAndValidates_WithTheNodeIdAndRoots()
    {
        SkipUnlessBash();
        (int exit, string output, string error) = await RunScriptAsync("render-config", "--node-id", "pc-7", "--models-root", "/data/models", "--cache-root", "/data/hf-cache");
        Assert.True(exit == 0, error);

        string path = Path.Combine(Path.GetTempPath(), $"agent-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, output, TestContext.Current.CancellationToken);
        try
        {
            AgentOptions rendered = AgentOptions.FromConfiguration(new ConfigurationBuilder().AddJsonFile(path, optional: false, reloadOnChange: false).Build());
            AgentOptions example = ExampleOptions();
            Assert.Equal("pc-7", rendered.NodeId);
            Assert.Equal("/data/models", rendered.ModelsRoot);
            Assert.Equal("/data/models", rendered.ModelsHostPath);
            Assert.Equal("/data/hf-cache", rendered.CacheRoot);
            Assert.Equal("/data/hf-cache", rendered.CacheHostPath);
            // Everything else is the example's.
            Assert.Equal(example.StateDirectory, rendered.StateDirectory);
            Assert.Equal(example.Port, rendered.Port);
            Assert.Equal(example.AllowTagPulls, rendered.AllowTagPulls);
            Assert.Equal(example.UseContainerNetwork, rendered.UseContainerNetwork);
            Assert.Null(rendered.BootstrapDirectory);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RenderDropin_MakesExactlyTheRootsWritable()
    {
        SkipUnlessBash();
        (int exit, string output, string error) = await RunScriptAsync("render-dropin", "--models-root", "/data/models", "--cache-root", "/data/hf-cache");
        Assert.True(exit == 0, error);
        List<(string Section, string Key, string Value)> dropin = ParseUnit(output);
        (string section, string key, string value) = Assert.Single(dropin);
        Assert.Equal("Service", section);
        Assert.Equal("ReadWritePaths", key);
        Assert.Equal("-/data/models -/data/hf-cache", value);
    }

    [Theory]
    [InlineData("--token is refused", "install", "--node-id", "pc-1", "--token", "secret")]
    [InlineData("bad node id", "render-config", "--node-id", "PC_1")]
    [InlineData("node id too long", "render-config", "--node-id", "a12345678901234567890123456789012345678901")]
    [InlineData("relative root", "render-config", "--node-id", "pc-1", "--models-root", "models")]
    [InlineData("root with a quote", "render-dropin", "--models-root", "/data/m\"odels")]
    [InlineData("root with ..", "render-dropin", "--cache-root", "/data/../etc")]
    [InlineData("unknown verb", "reinstall")]
    [InlineData("unknown option", "install", "--node", "pc-1")]
    [InlineData("--node-uid outside rekey", "install", "--node-id", "pc-1", "--node-uid", "abc-123")]
    [InlineData("--rekey with another verb", "upgrade", "--rekey")]
    public async Task UnsafeOrWrongArguments_ExitWithUsage(string why, params string[] args)
    {
        SkipUnlessBash();
        (int exit, string output, string error) = await RunScriptAsync(args);
        Assert.True(exit == 2, $"{why}: exit {exit}, stdout '{output}', stderr '{error}'");
        Assert.Equal("", output);
        Assert.DoesNotContain("secret", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("install", "--node-id", "pc-1")]
    [InlineData("upgrade")]
    [InlineData("rollback")]
    [InlineData("uninstall", "--purge")]
    [InlineData("rekey", "--enroll-url", "https://m:8443", "--ca-hash", "sha256:h")]
    [InlineData("--rekey", "--node-uid", "abc-123", "--enroll-url", "https://m:8443", "--ca-hash", "sha256:h")]
    public async Task ChangingVerbs_NeedRoot(params string[] args)
    {
        SkipUnlessBash();
        (int euidExit, string euid, _) = await RunBashAsync(["-c", "echo $EUID"]);
        Assert.Equal(0, euidExit);
        if (euid.Trim() == "0")
        {
            Assert.Skip("The tests run as root here; the dry run checks the refusal (runuser -u nobody).");
        }

        (int exit, _, string error) = await RunScriptAsync(args);
        Assert.Equal(1, exit);
        Assert.Contains("must run as root", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_ListsEveryVerb()
    {
        SkipUnlessBash();
        (int exit, string output, _) = await RunScriptAsync("--help");
        Assert.Equal(0, exit);
        string script = Script();
        foreach (string verb in Verbs)
        {
            Assert.Contains($"install-agent.sh {verb}", output, StringComparison.Ordinal);
            Assert.Contains($"\n    {verb})", script, StringComparison.Ordinal); // and the dispatcher handles it
        }
    }

    // ---- helpers ----

    private static List<(string Section, string Key, string Value)> Unit() => ParseUnit(File.ReadAllText(LinuxFile(UnitFile)));

    /// <summary>systemd unit syntax: sections, <c>Key=Value</c> lines, <c>#</c>/<c>;</c> comments (no continuations used).</summary>
    private static List<(string Section, string Key, string Value)> ParseUnit(string text)
    {
        var result = new List<(string, string, string)>();
        string? section = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            Assert.False(line.EndsWith('\\'), $"line continuation in '{line}'");
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            Assert.True(eq > 0 && section is not null, $"not a directive: '{line}'");
            result.Add((section!, line[..eq], line[(eq + 1)..]));
        }

        return result;
    }

    private static string Single(List<(string Section, string Key, string Value)> unit, string key) =>
        Assert.Single(unit, d => d.Key == key).Value;

    private static IEnumerable<string> All(List<(string Section, string Key, string Value)> unit, string key) =>
        unit.Where(d => d.Key == key).Select(d => d.Value);

    private static AgentOptions ExampleOptions() => AgentOptions.FromConfiguration(
        new ConfigurationBuilder().AddJsonFile(LinuxFile("../agent.linux.example.json"), optional: false, reloadOnChange: false).Build());

    private static string Script() => File.ReadAllText(LinuxFile("install-agent.sh"));

    /// <summary>A top-level <c>NAME=value</c> (or <c>NAME="value"</c>) assignment of the script.</summary>
    private static string ScriptVariable(string name)
    {
        Match m = Regex.Match(Script(), $"^{Regex.Escape(name)}=\"?([^\"\\n]*)\"?$", RegexOptions.Multiline);
        Assert.True(m.Success, $"{name}= not found in install-agent.sh");
        return m.Groups[1].Value;
    }

    private static void SkipUnlessBash()
    {
        if (Bash() is null)
        {
            Assert.Skip("Runs the real install-agent.sh: needs bash (Linux, or Git for Windows on a Windows box).");
        }
    }

    private static string? Bash() => BashLocations.FirstOrDefault(File.Exists);

    private static Task<(int Exit, string Output, string Error)> RunScriptAsync(params string[] args) =>
        RunBashAsync([LinuxFile("install-agent.sh"), .. args]);

    private static async Task<(int Exit, string Output, string Error)> RunBashAsync(string[] args)
    {
        var start = new ProcessStartInfo(Bash()!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment.Remove(JoinCommand.TokenEnvironmentVariable);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }

    private static string LinuxFile(string relative) =>
        Path.GetFullPath(Path.Combine([RepoRoot(), "deploy", "agent", "linux", .. relative.Split('/')]));

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MirbadLLM.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("MirbadLLM.slnx not found above the test binaries.");
    }

    /// <summary>The arguments of the agent call in <c>do_rekey</c>: from <c>join</c> to the <c>; then</c> that ends the condition.</summary>
    [GeneratedRegex(@"""\$\{BINARY\}"" (join --rekey[^;]*); then")]
    private static partial Regex RekeyCallRegex();

    [GeneratedRegex(@"valid_node_id\(\) \{ \[\[ ""\$1"" =~ (\S+) \]\]; \}")]
    private static partial Regex NodeIdCheckRegex();
}
