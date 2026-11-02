using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using MirbadLLM.Agent;
using MirbadLLM.HostAgent;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Deploy;

/// <summary>
/// macOS Phase 4 (docs/macos/implementation-plan.md M10): the macOS hostd's LaunchDaemon, its seed, the installer verbs
/// that install it and point the agent at it, and the image build that puts it in the macOS bundle.
/// </summary>
public sealed partial class DeployArtifactsTests
{
    private static readonly string[] MacPlists = ["com.mirbadllm.agent.plist", "com.mirbadllm.hostd.plist"];

    /// <summary>The top-level dictionary of a plist (DTDs ignored, never fetched).</summary>
    private static Dictionary<string, XElement> PlistDict(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        var dict = XDocument.Load(reader).Root!.Element("dict")!.Elements().ToList();
        var result = new Dictionary<string, XElement>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < dict.Count; i += 2)
        {
            Assert.Equal("key", dict[i].Name.LocalName);
            result.Add(dict[i].Value, dict[i + 1]);
        }

        return result;
    }

    [Fact]
    public void HostdPlist_RunsTheBinaryAsRoot_WithTheMacConfig_AndKeepsItAlive()
    {
        var plist = PlistDict(RepoFile("deploy", "agent", "macos", "com.mirbadllm.hostd.plist"));

        Assert.Equal("com.mirbadllm.hostd", plist["Label"].Value);
        Assert.Equal(["/usr/local/lib/mirbadllm-hostd/mirbadllm-hostd"], plist["ProgramArguments"].Elements("string").Select(e => e.Value));
        // Root: launchctl bootstrap/bootout into the system domain and the users' gui domains needs it.
        Assert.False(plist.ContainsKey("UserName"));
        Assert.False(plist.ContainsKey("GroupName"));
        var env = plist["EnvironmentVariables"].Elements().ToList();
        Assert.Equal(HostdConfig.MacDefaultPath, env[env.FindIndex(e => e.Value == "MIRBADLLM_HOSTD_CONFIG") + 1].Value);
        Assert.Equal(WhitelistStore.MacDirectory, plist["WorkingDirectory"].Value);
        Assert.Equal("63", plist["Umask"].Value); // 077: the whitelist it writes is root 0600
        Assert.Equal("true", plist["RunAtLoad"].Name.LocalName);
        Assert.Equal("true", plist["KeepAlive"].Name.LocalName);
        Assert.Equal("/Library/Logs/mirbadllm-hostd/hostd.log", plist["StandardOutPath"].Value);
        Assert.Equal("/Library/Logs/mirbadllm-hostd/hostd.log", plist["StandardErrorPath"].Value);
    }

    [Fact]
    public void MacPlists_AreValidPropertyLists()
    {
        foreach (string name in MacPlists)
        {
            string path = RepoFile("deploy", "agent", "macos", name);
            Assert.NotEmpty(PlistDict(path));
            if (OperatingSystem.IsMacOS())
            {
                using var lint = Process.Start(new ProcessStartInfo("/usr/bin/plutil", ["-lint", "-s", path]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
                lint.WaitForExit();
                Assert.True(lint.ExitCode == 0, $"plutil -lint {name}: {lint.StandardOutput.ReadToEnd()}{lint.StandardError.ReadToEnd()}");
            }
        }
    }

    [Fact]
    public void MacInstaller_InstallsHostd_AndPointsTheAgentAtItsSocket()
    {
        string script = File.ReadAllText(RepoFile("deploy", "agent", "macos", "install-agent.sh"));

        foreach (string expected in new[]
        {
            "install-hostd) do_install_hostd ;;",
            "uninstall-hostd) do_uninstall_hostd ;;",
            "--with-hostd) with_hostd=1;",
            "HOSTD_LABEL=com.mirbadllm.hostd",
            "HOSTD_BINARY=\"${HOSTD_PREFIX}/mirbadllm-hostd\"",
            $"HOSTD_SOCKET={HostdConfig.MacDefaultSocket}",
            $"HOSTD_DIR=\"{HostdConfig.MacDirectory}\"",
            "HOSTD_SEED_NAME=hostd.macos.json",
            // An upgrade keeps an installed hostd at the agent's version.
            "if ((with_hostd == 0)) && [[ \"${upgrade}\" == 1 ]] && hostd_installed; then with_hostd=1; fi",
            // The agent's keys: the names AgentOptions binds.
            $"config_set {nameof(AgentOptions.HostdSocket)} \"\\\"${{HOSTD_SOCKET}}\\\"\"",
            $"config_set {nameof(AgentOptions.ManageLaunchdJobs)} true",
            $"config_set {nameof(AgentOptions.HostdExpected)} true",
            // hostd's files stay root-owned, the socket's directory is hostd's own work.
            "install -d -m 0755 -o root -g wheel \"${HOSTD_DIR}\"",
            "install -d -m 0700 -o root -g wheel \"${HOSTD_STATE}\"",
            "install -m 0644 -o root -g wheel \"${WORK}/hostd.json\" \"${HOSTD_CONFIG}\"",
            // The bundle carries the plist and the seed, and the binary when it was published next to the agent.
            "install -m 0644 \"$(hostd_file_source com.mirbadllm.hostd.plist)\" \"${stage}/com.mirbadllm.hostd.plist\"",
            "install -m 0644 \"$(hostd_file_source \"${HOSTD_SEED_NAME}\")\" \"${stage}/${HOSTD_SEED_NAME}\"",
            "install -m 0755 \"${hostd_src}\" \"${stage}/mirbadllm-hostd\"",
        })
        {
            Assert.Contains(expected, script, StringComparison.Ordinal);
        }

        // --help shows the hostd verbs (usage prints the header up to the options line).
        string[] lines = File.ReadAllLines(RepoFile("deploy", "agent", "macos", "install-agent.sh"));
        var usage = System.Text.RegularExpressions.Regex.Match(script, @"usage\(\) \{ sed -n '2,(\d+)p'");
        int last = int.Parse(usage.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        string help = string.Join('\n', lines[1..last]);
        Assert.Contains("install-hostd", help, StringComparison.Ordinal);
        Assert.Contains("--with-hostd", help, StringComparison.Ordinal);
        Assert.StartsWith("#", lines[last], StringComparison.Ordinal);
    }

    [Fact]
    public void MasterImage_PutsTheMacHostdInTheMacBundle()
    {
        string dockerfile = File.ReadAllText(RepoFile("deploy", "Dockerfile")).ReplaceLineEndings("\n");
        int mac = dockerfile.IndexOf("--output /bundles/osx-arm64.tar.gz", StringComparison.Ordinal);
        string macStep = dockerfile[dockerfile.LastIndexOf("RUN set -eu;", mac, StringComparison.Ordinal)..mac];

        Assert.Contains("dotnet publish src/MirbadLLM.HostAgent/MirbadLLM.HostAgent.csproj -c Release -r osx-arm64", macStep, StringComparison.Ordinal);
        Assert.Contains("cp /publish/osx-arm64-hostd/mirbadllm-hostd /publish/osx-arm64/;", macStep, StringComparison.Ordinal);
        Assert.Contains("COPY src/MirbadLLM.HostAgent/ src/MirbadLLM.HostAgent/", dockerfile, StringComparison.Ordinal);
        Assert.Contains("dotnet restore src/MirbadLLM.HostAgent/MirbadLLM.HostAgent.csproj -r osx-arm64", dockerfile, StringComparison.Ordinal);

        string[] ignore = [.. File.ReadAllLines(RepoFile("deploy", "Dockerfile.dockerignore")).Select(l => l.Trim())];
        Assert.DoesNotContain("src/MirbadLLM.HostAgent/**", ignore);
    }
}
