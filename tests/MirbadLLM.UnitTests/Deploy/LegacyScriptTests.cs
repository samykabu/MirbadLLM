using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.HostAgent;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Deploy;

/// <summary>
/// The shell side of the rename: installers keep a pre-rename install under its old names, the update helper installed
/// on a pre-rename node installs a new bundle, and the DGX deploy cuts the running pre-rename containers over (with
/// rollback). Runs the real scripts with bash (Linux, macOS, or Git for Windows' bash).
/// </summary>
public sealed class LegacyScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-legacy-sh-" + Guid.NewGuid().ToString("N"));

    public LegacyScriptTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ---- installers ----

    [Fact]
    public async Task LinuxInstaller_UpgradesAPreRenameInstallUnderItsOldUnitUserAndFolders()
    {
        SkipUnlessBash();
        string systemd = Dir("systemd");
        var fresh = await BashAsync([Repo("deploy/agent/linux/install-agent.sh"), "render-unit"], new() { ["MIRBADLLM_SYSTEMD_DIR"] = Unix(systemd) });
        Assert.Contains("ExecStart=/usr/local/lib/mirbadllm-agent/mirbadllm-agent --config /etc/mirbadllm-agent/agent.json", fresh.Output, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(systemd, "spark-serve-agent.service"), "");
        var legacy = await BashAsync([Repo("deploy/agent/linux/install-agent.sh"), "render-unit"], new() { ["MIRBADLLM_SYSTEMD_DIR"] = Unix(systemd) });
        Assert.Equal(0, legacy.Exit);
        Assert.Contains("ExecStart=/usr/local/lib/spark-serve-agent/spark-serve-agent --config /etc/spark-serve-agent/agent.json", legacy.Output, StringComparison.Ordinal);
        Assert.Contains("User=spark-serve-agent", legacy.Output, StringComparison.Ordinal);
        Assert.Contains("StateDirectory=spark-serve-agent", legacy.Output, StringComparison.Ordinal);
        Assert.Contains("ConditionPathExists=/var/lib/spark-serve-agent/agent.crt", legacy.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("mirbadllm-agent", legacy.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostdInstaller_UpgradesAPreRenameHostAgentUnderItsOldUnitAndSocket()
    {
        SkipUnlessBash();
        string systemd = Dir("systemd-h");
        File.WriteAllText(Path.Combine(systemd, "spark-serve-hostd.service"), "");
        var legacy = await BashAsync([Repo("deploy/hostd/install-hostd.sh"), "render-unit"], new() { ["MIRBADLLM_SYSTEMD_DIR"] = Unix(systemd) });
        Assert.Equal(0, legacy.Exit);
        Assert.Contains("ExecStart=/usr/local/lib/spark-serve-hostd/spark-serve-hostd", legacy.Output, StringComparison.Ordinal);
        Assert.Contains("RuntimeDirectory=spark-serve-hostd", legacy.Output, StringComparison.Ordinal);
        Assert.Contains("StateDirectory=spark-serve-hostd", legacy.Output, StringComparison.Ordinal);
        Assert.Contains("Environment=MIRBADLLM_HOSTD_CONFIG=/etc/spark-serve/hostd.json", legacy.Output, StringComparison.Ordinal);
        Assert.Contains("-/etc/spark-serve-hostd", legacy.Output, StringComparison.Ordinal);
    }

    // ---- the update helper installed on a pre-rename node ----

    [Theory]
    // The helper installed on a pre-rename node, with a bundle of this version (its folder plus the pre-rename link).
    [InlineData("tests/MirbadLLM.UnitTests/Agent/Fixtures/pre-rename-agent-update-helper.sh", true)]
    // This version's helper, with a bundle built before the rename (only the pre-rename folder).
    [InlineData("deploy/agent/agent-update-helper.sh", false)]
    public async Task TheUpdateHelper_InstallsAcrossTheRename(string helperPath, bool newBundle)
    {
        SkipUnlessBash();
        if (!(await BashAsync(["-c", "command -v openssl && command -v tar"], [])).Output.Contains("tar", StringComparison.Ordinal))
        {
            Assert.Skip("needs openssl and tar in bash");
        }

        using var ca = CertificateAuthority.Create();
        string trust = Dir("trust");
        string spool = Dir("spool");
        File.WriteAllText(Path.Combine(trust, "ca.pem"), ca.CertificatePem.ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(trust, "node-uid"), "uid-1\n");
        File.WriteAllText(Path.Combine(trust, "rid"), "linux-x64\n");

        string marker = Path.Combine(_root, "installed");
        string stage = Dir("stage");
        string folderName = newBundle ? "mirbadllm-agent-v9-linux-x64" : "spark-serve-agent-v9-linux-x64";
        string folder = Path.Combine(stage, folderName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "install-agent.sh"), $"#!/bin/bash\necho \"$1 helper=${{MIRBADLLM_UPDATE_HELPER:-$SPARK_SERVE_UPDATE_HELPER}}\" > '{Unix(marker)}'\n");
        string tarball = Path.Combine(_root, "bundle.tar.gz");
        // install-agent.sh package's layout: the bundle folder, and its pre-rename name as a link.
        string contents = newBundle ? $"ln -s {folderName} spark-serve-agent-v9-linux-x64 && tar -czf '{Unix(tarball)}' {folderName} spark-serve-agent-v9-linux-x64" : $"tar -czf '{Unix(tarball)}' {folderName}";
        var tar = await BashAsync(["-c", $"cd '{Unix(stage)}' && {contents}"], []);
        Assert.True(tar.Exit == 0, tar.Error);
        byte[] bundle = File.ReadAllBytes(tarball);

        var now = DateTimeOffset.UtcNow;
        string order = new AgentUpdateOrder("uid-1", "linux-x64", "v9", Convert.ToHexStringLower(SHA256.HashData(bundle)), bundle.Length, now, now.AddHours(1)).Format();
        File.WriteAllBytes(Path.Combine(spool, "bundle.tar.gz"), bundle);
        File.WriteAllText(Path.Combine(spool, "operation"), "op-1\n");
        File.WriteAllText(Path.Combine(spool, "order"), order);
        File.WriteAllText(Path.Combine(spool, "order.sig"), Convert.ToBase64String(ca.SignData(Encoding.UTF8.GetBytes(order))) + "\n");

        string helper = HostAgentFixtures.FindRepoFile(Path.Combine(helperPath.Split('/')));
        var run = await BashAsync([Unix(helper), Unix(spool), Unix(trust), Environment.UserName], []);

        string result = File.ReadAllText(Path.Combine(spool, "result"));
        Assert.True(result.Contains("exit=0", StringComparison.Ordinal), result + run.Error);
        Assert.Equal("upgrade helper=1", File.ReadAllText(marker).Trim());
    }

    // ---- the DGX deploy's rename cut-over ----

    [Fact]
    public async Task DeployCutover_ReplacesThePreRenameContainers_AfterTheNewOnesAreHealthy()
    {
        SkipUnlessBash();
        var run = await DeployAsync(agentHealth: "running/healthy");

        Assert.True(run.Exit == 0, run.Output + run.Error);
        string calls = File.ReadAllText(Path.Combine(_root, "docker.log"));
        Assert.True(Index(calls, "stop -t 45 spark-serve-agent") < Index(calls, "stop -t 45 spark-serve\n"));
        Assert.True(Index(calls, "stop -t 45 spark-serve\n") < Index(calls, "volume create mirbadllm-agent-state"));
        Assert.Contains("-v spark-serve-agent-state:/from:ro -v mirbadllm-agent-state:/to", calls, StringComparison.Ordinal);
        Assert.True(Index(calls, "up -d --no-build --no-deps master") < Index(calls, "network connect mirbadllm ss-m1"));
        Assert.True(Index(calls, "network connect mirbadllm ss-m1") < Index(calls, "up -d --no-build --no-deps agent"));
        Assert.DoesNotContain("network connect mirbadllm spark-serve", calls, StringComparison.Ordinal);
        Assert.True(Index(calls, "up -d --no-build --no-deps agent") < Index(calls, "rm spark-serve spark-serve-agent"));
        // The env file's pre-rename keys reach compose under their new names; no value reaches the log.
        Assert.Contains("compose MIRBADLLM_DATA=/legacy/data MIRBADLLM_ADMIN_PASSWORD=set", calls, StringComparison.Ordinal);
        Assert.Contains("SPARK_SERVE_ADMIN_PASSWORD", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", run.Output + run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployCutover_RollsBackToThePreRenameContainers_WhenTheNewAgentFails()
    {
        SkipUnlessBash();
        var run = await DeployAsync(agentHealth: "exited/");

        Assert.Equal(1, run.Exit);
        string calls = File.ReadAllText(Path.Combine(_root, "docker.log"));
        Assert.Contains("rm -sf master agent", calls, StringComparison.Ordinal);
        Assert.Contains("volume rm -f mirbadllm-agent-state", calls, StringComparison.Ordinal);
        Assert.True(Index(calls, "rm -sf master agent") < Index(calls, "start spark-serve\n"));
        Assert.Contains("start spark-serve-agent", calls, StringComparison.Ordinal);
        Assert.DoesNotContain("rm spark-serve spark-serve-agent", calls, StringComparison.Ordinal);
        Assert.DoesNotContain("10.1.2.3", run.Output + run.Error, StringComparison.Ordinal); // container logs are masked
    }

    /// <summary>The deploy script with fake docker and curl: spark-serve and spark-serve-agent run, mirbadllm* do not exist yet.</summary>
    private async Task<(int Exit, string Output, string Error)> DeployAsync(string agentHealth)
    {
        string bin = Dir("bin");
        string log = Unix(Path.Combine(_root, "docker.log"));
        File.WriteAllText(Path.Combine(bin, "docker"), $$$"""
            #!/usr/bin/env bash
            echo "$*" >> '{{{log}}}'
            case "$*" in
              compose*) echo "compose MIRBADLLM_DATA=$MIRBADLLM_DATA MIRBADLLM_ADMIN_PASSWORD=${MIRBADLLM_ADMIN_PASSWORD:+set}" >> '{{{log}}}' ;;
              "inspect --type container spark-serve --format {{.Config.Image}}") echo spark-serve/controller:old ;;
              "inspect --type container spark-serve-agent --format {{.Config.Image}}") echo spark-serve/agent:old ;;
              "inspect --type container spark-serve --format"*) echo master ;;
              "inspect --type container"*) exit 1 ;;
              "inspect mirbadllm-agent"*) echo '{{{agentHealth}}}' ;;
              "inspect spark-serve-agent"*) echo running/healthy ;;
              "volume inspect spark-serve"*) exit 0 ;;
              "volume inspect"*) exit 1 ;;
              "network inspect"*) echo "ss-m1 spark-serve spark-serve-agent " ;;
              logs*) echo "connected to 10.1.2.3" ;;
            esac
            exit 0
            """.ReplaceLineEndings("\n"));
        File.WriteAllText(Path.Combine(bin, "curl"), "#!/usr/bin/env bash\necho 200\n");
        string env = Path.Combine(_root, ".env");
        File.WriteAllText(env, "DOCKER_GID=1\nSPARK_SERVE_DATA=/legacy/data\nSPARK_SERVE_ADMIN_PASSWORD=\"s3cret\"\n");
        string repo = Unix(RepoRoot());
        return await BashAsync(["-c", $"chmod +x '{Unix(bin)}'/* && cd '{repo}' && PATH='{Unix(bin)}':\"$PATH\" bash deploy/ci-deploy-multinode.sh"],
            new() { ["MIRBADLLM_ENV_FILE"] = Unix(env), ["GITHUB_SHA"] = "0123456789abcdef", ["HOME"] = Unix(_root) });
    }

    // ---- helpers ----

    private static int Index(string text, string value)
    {
        int i = text.IndexOf(value, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{value.Trim()}' not in:\n{text}");
        return i;
    }

    private string Dir(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Repo(string relative) => Unix(Path.Combine([RepoRoot(), .. relative.Split('/')]));

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

    /// <summary>A path as bash sees it (Git for Windows: /c/Users/...).</summary>
    private static string Unix(string path) =>
        OperatingSystem.IsWindows() && path.Length > 2 && path[1] == ':'
            ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
            : path;

    private static readonly string[] BashLocations = OperatingSystem.IsWindows()
        ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")]
        : ["/bin/bash", "/usr/bin/bash"];

    private static string? Bash() => BashLocations.FirstOrDefault(File.Exists);

    private static void SkipUnlessBash()
    {
        if (Bash() is null)
        {
            Assert.Skip("Runs the real scripts: needs bash (Linux, macOS, or Git for Windows).");
        }
    }

    private static async Task<(int Exit, string Output, string Error)> BashAsync(string[] args, Dictionary<string, string> env)
    {
        var start = new ProcessStartInfo(Bash()!) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in env)
        {
            start.Environment[key] = value;
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await output, await error);
    }
}
