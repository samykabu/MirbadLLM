using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Fakes;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Web.Enrollment;

namespace MirbadLLM.UnitTests.Deploy;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// The release artifacts (CONTRIBUTING.md, "Cutting a release"): the workflow that publishes a tag, the script that builds
/// and pushes the images, the compose file that pulls them, the release version in the binaries, the demo image and the
/// Markdown notes of the GitHub Release. Read as text like the rest of this class; the two tests that run a script skip
/// themselves without bash or Python.
/// </summary>
public sealed partial class DeployArtifactsTests
{
    private const string ForkGuard = "github.event_name != 'pull_request' || github.event.pull_request.head.repo.full_name == github.repository";

    /// <summary>The image lines of deploy/compose.release.yaml: one variable selects the release of both.</summary>
    private static readonly string[] ReleaseImages =
        ["    image: ghcr.io/samykabu/mirbadllm:${MIRBADLLM_VERSION:-latest}", "    image: ghcr.io/samykabu/mirbadllm-agent:${MIRBADLLM_VERSION:-latest}"];

    [Fact]
    public void ReleaseWorkflow_PublishesOnlyForATag_WithTheForkGuard_AndNoMoreThanItsThreePermissions()
    {
        string workflow = File.ReadAllText(RepoFile(".github", "workflows", "release.yml")).ReplaceLineEndings("\n");

        Assert.Contains("on:\n  push:\n    tags: [\"v*.*.*\"]\n  workflow_dispatch:\n", workflow, StringComparison.Ordinal);
        // The workflow's default is read-only; the one job adds what a release needs and nothing else.
        Assert.Contains("\npermissions:\n  contents: read\n", workflow, StringComparison.Ordinal);
        Assert.Contains("    permissions:\n      contents: write\n      packages: write\n      pull-requests: read\n    env:", workflow, StringComparison.Ordinal);
        // Every job carries ci.yml's guard: a self-hosted runner never runs a fork's code.
        Assert.Equal(RunsOnRegex().Count(workflow), Regex.Count(workflow, @"^    if: " + Regex.Escape(ForkGuard) + "$", RegexOptions.Multiline));
        Assert.Contains("if: " + ForkGuard, File.ReadAllText(RepoFile(".github", "workflows", "ci.yml")), StringComparison.Ordinal);

        // A manual run is the dry run: the login, the push and the GitHub Release need a tag push.
        Assert.Contains("PUBLISH: ${{ github.event_name == 'push' && '1' || '0' }}", workflow, StringComparison.Ordinal);
        Assert.Contains("PUSH: ${{ env.PUBLISH }}", workflow, StringComparison.Ordinal);
        foreach (string step in new[] { "Log in to ghcr.io", "GitHub Release" })
        {
            Assert.Contains($"      - name: {step}\n        if: env.PUBLISH == '1'\n", workflow, StringComparison.Ordinal);
        }

        // The job's token does not stay in the runner's Docker configuration, whatever the build did.
        Assert.Contains("      - name: Log out of ghcr.io\n        if: always() && env.PUBLISH == '1'\n        run: docker logout ghcr.io\n", workflow, StringComparison.Ordinal);
        // The only action is the checkout, pinned to the commit ci.yml uses; everything else is the runner's own tools.
        string[] actions = [.. Regex.Matches(workflow, @"uses: (\S+)").Select(m => m.Groups[1].Value)];
        string checkout = Assert.Single(actions);
        Assert.Matches("^actions/checkout@[0-9a-f]{40}$", checkout);
        Assert.Contains("uses: " + checkout, File.ReadAllText(RepoFile(".github", "workflows", "ci.yml")), StringComparison.Ordinal);
        Assert.Contains("run: bash deploy/release.sh", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseScript_DryRun_TagsAReleaseThreeWays_APrereleaseOnce_AndPushesAfterTheDemoTest()
    {
        if (ReleaseBash() is not { } bash)
        {
            Assert.Skip("Runs deploy/release.sh: needs bash (Linux, macOS, or Git for Windows).");
            return;
        }

        const string sha = "0123456789abcdef0123456789abcdef01234567";
        Dictionary<string, string> Env(string version, string push) =>
            new() { ["DRY_RUN"] = "1", ["VERSION"] = version, ["PUSH"] = push, ["GITHUB_SHA"] = sha, ["IMAGE_PREFIX"] = "ghcr.io/SomeOwner" };

        var release = await RunAsync(bash, ["deploy/release.sh"], Env("1.2.0", "1"));
        Assert.True(release.Exit == 0, release.Error);
        string[] builds = [.. release.Output.Split('\n').Where(l => l.StartsWith("+ docker buildx build ", StringComparison.Ordinal))];
        // Three images without a push, then the same three with it; the demo test sits between them.
        Assert.Equal(6, builds.Length);
        Assert.All(builds[..3], b => Assert.DoesNotContain(" --push ", b, StringComparison.Ordinal));
        Assert.All(builds[3..], b => Assert.Contains(" --push ", b, StringComparison.Ordinal));
        Assert.True(release.Output.IndexOf("Testing the demo image", StringComparison.Ordinal) < release.Output.IndexOf(" --push ", StringComparison.Ordinal));
        foreach (string image in new[] { "mirbadllm", "mirbadllm-agent", "mirbadllm-demo" })
        {
            // The owner's name in lower case, as a registry path needs it.
            Assert.Contains(builds, b => b.Contains($"-t ghcr.io/someowner/{image}:1.2.0 -t ghcr.io/someowner/{image}:1.2 -t ghcr.io/someowner/{image}:latest ", StringComparison.Ordinal));
        }

        Assert.All(builds, b =>
        {
            // The version next to the commit: the commit stays what the master compares with each agent's.
            Assert.Contains("--build-arg SOURCE_REVISION=0123456789ab --build-arg Version=1.2.0 ", b, StringComparison.Ordinal);
            Assert.Contains(@"--platform linux/amd64\,linux/arm64 ", b, StringComparison.Ordinal);
        });
        Assert.Equal(2, builds.Count(b => b.Contains("-f deploy/Dockerfile --target demo", StringComparison.Ordinal)));
        Assert.Equal(2, builds.Count(b => b.Contains("-f deploy/agent.Dockerfile", StringComparison.Ordinal)));

        // A prerelease moves neither latest nor <major>.<minor>, and nothing is pushed without PUSH=1.
        var candidate = await RunAsync(bash, ["deploy/release.sh"], Env("1.2.0-rc.1", "0"));
        Assert.True(candidate.Exit == 0, candidate.Error);
        Assert.Contains("-t ghcr.io/someowner/mirbadllm:1.2.0-rc.1 --platform", candidate.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(":latest", candidate.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("--push", candidate.Output, StringComparison.Ordinal);

        foreach (string bad in new[] { "v1.2.0", "1.2", "1.2.0+abc", "1.2.0 x" })
        {
            var refused = await RunAsync(bash, ["deploy/release.sh"], Env(bad, "1"));
            Assert.NotEqual(0, refused.Exit);
            Assert.DoesNotContain("docker buildx build", refused.Output, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The pull-based compose file must not drift from the one CI deploys: the same services, ports, volumes and settings,
    /// with the published images in place of the build sections.
    /// </summary>
    [Fact]
    public void ReleaseCompose_IsTheMultinodeCompose_WithThePublishedImages()
    {
        string[] multinode = ComposeLines("compose.multinode.yaml");
        string[] release = ComposeLines("compose.release.yaml");
        static bool IsImage(string line) => line.StartsWith("    image: ", StringComparison.Ordinal);

        Assert.Equal(multinode.Where(l => !IsImage(l)), release.Where(l => !IsImage(l)));
        // One variable selects the release of both images, so the master and its agent are always the same build.
        Assert.Equal(ReleaseImages, release.Where(IsImage));
        Assert.DoesNotContain("build:", File.ReadAllText(RepoFile("deploy", "compose.release.yaml")), StringComparison.Ordinal);
        Assert.Contains("#MIRBADLLM_VERSION=", File.ReadAllText(RepoFile("deploy", ".env.example")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The release version reaches every publish through the <c>Version</c> build argument, which MSBuild reads from the
    /// environment: each stage that publishes declares it before its first publish, and after the restore.
    /// </summary>
    [Theory]
    [InlineData("Dockerfile", 2)]
    [InlineData("agent.Dockerfile", 1)]
    public void Dockerfiles_GiveEveryPublishTheReleaseVersion(string dockerfile, int publishingStages)
    {
        string[] stages = [.. Regex.Split(File.ReadAllText(RepoFile("deploy", dockerfile)).ReplaceLineEndings("\n"), @"^(?=FROM )", RegexOptions.Multiline)
            .Where(s => s.Contains("dotnet publish", StringComparison.Ordinal))];
        Assert.Equal(publishingStages, stages.Length);
        foreach (string stage in stages)
        {
            int version = stage.IndexOf("\nARG Version=\n", StringComparison.Ordinal);
            Assert.True(version > stage.LastIndexOf("dotnet restore", StringComparison.Ordinal), "ARG Version= comes after the restore layer");
            Assert.True(version < stage.IndexOf("dotnet publish", StringComparison.Ordinal), "ARG Version= comes before the first publish");
            // The commit keeps its own argument: it is the build's identity, the version is only its name.
            Assert.Contains("-p:SourceRevisionId=\"${SOURCE_REVISION}\"", stage, StringComparison.Ordinal);
        }
    }

    /// <summary>The runner builds the other architecture without QEMU: nothing runs in a stage of the target platform.</summary>
    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("agent.Dockerfile")]
    public void Dockerfiles_RunNothingOnTheTargetPlatform(string dockerfile)
    {
        foreach (string stage in Regex.Split(File.ReadAllText(RepoFile("deploy", dockerfile)).ReplaceLineEndings("\n"), @"^(?=FROM )", RegexOptions.Multiline)
            .Where(s => s.StartsWith("FROM ", StringComparison.Ordinal) && !s.StartsWith("FROM --platform=$BUILDPLATFORM ", StringComparison.Ordinal)))
        {
            Assert.DoesNotMatch(new Regex(@"^RUN ", RegexOptions.Multiline), stage);
        }
    }

    [Fact]
    public void DemoImage_RunsTheMasterAndTwoFakeNodes_OnLoopback_InDevelopmentOnly()
    {
        string dockerfile = File.ReadAllText(RepoFile("deploy", "Dockerfile")).ReplaceLineEndings("\n");
        string stage = Stage(dockerfile, "demo");
        string script = File.ReadAllText(RepoFile("deploy", "demo", "demo.sh")).ReplaceLineEndings("\n");
        string root = Regex.Match(script, @"^ROOT=(\S+)$", RegexOptions.Multiline).Groups[1].Value;
        Assert.StartsWith("/tmp/", root, StringComparison.Ordinal);

        // The master's files and its agent bundles (the script takes the agent from the Linux bundle), then the demo files.
        Assert.StartsWith("FROM mcr.microsoft.com/dotnet/aspnet:", stage, StringComparison.Ordinal);
        Assert.Contains("COPY --from=build /app .", stage, StringComparison.Ordinal);
        Assert.Contains("COPY --from=agent-bundles /bundles /app/agent-bundles", stage, StringComparison.Ordinal);
        Assert.Contains("COPY deploy/demo/ /opt/mirbadllm-demo/", stage, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"bash\", \"/opt/mirbadllm-demo/demo.sh\"]", stage, StringComparison.Ordinal);
        Assert.Contains("USER app", stage, StringComparison.Ordinal);
        Assert.Contains("tar -xzf \"/app/agent-bundles/$rid.tar.gz\"", script, StringComparison.Ordinal);
        // deploy/release.sh waits for this health check before it pushes; the script reports ready last.
        Assert.Contains($"CMD [\"test\", \"-f\", \"{root}/ready\"]", stage, StringComparison.Ordinal);
        Assert.Contains("touch \"$ROOT/ready\"", script, StringComparison.Ordinal);
        // The demo stage is not the last one: a build without --target is still the master image.
        Assert.Contains("\nFROM ", dockerfile[dockerfile.IndexOf(" AS demo\n", StringComparison.Ordinal)..], StringComparison.Ordinal);

        (AgentOptions dgx, AgentOptions pc) = (DemoAgent("demo-dgx", root), DemoAgent("demo-pc", root));
        Assert.NotEqual(dgx.Port, pc.Port);
        Assert.True(dgx.PortRangeEnd < pc.PortRangeStart, "the two nodes' fake models take ports from separate ranges");

        // The master's settings in the script, as configuration.
        IConfiguration master = new ConfigurationBuilder().AddInMemoryCollection(
            Regex.Matches(script, @"^ +(MirbadLLM__\w+)=""?([^""\s]+)""? \\$", RegexOptions.Multiline).Select(m => KeyValuePair.Create(
                m.Groups[1].Value.Replace("__", ":", StringComparison.Ordinal), (string?)m.Groups[2].Value.Replace("$ROOT", root, StringComparison.Ordinal)))).Build();
        var options = new MirbadLLMOptions();
        master.GetSection(MirbadLLMOptions.SectionName).Bind(options);
        Assert.Equal(root + "/master", options.DataPath);
        Assert.Equal(root + "/backups", master.GetSection(BackupOptions.SectionName).Get<BackupOptions>()!.Directory);
        Assert.Equal(root + "/no-hostd.sock", master.GetSection(MirbadLLMOptions.SectionName).Get<HostServicesOptions>()!.HostAgentSocket);

        // demo-dgx enrolls from the bootstrap bundle, demo-pc with the development join-token helper, approved on its own.
        EnrollmentOptions enrollment = master.GetSection(EnrollmentOptions.SectionName).Get<EnrollmentOptions>()!;
        Assert.Equal(dgx.NodeId, master[$"{MirbadLLMOptions.SectionName}:Nodes:BootstrapNodeId"]);
        Assert.Equal(dgx.BootstrapDirectory, enrollment.BootstrapDirectory);
        Assert.Null(pc.BootstrapDirectory);
        Assert.Equal(new Uri($"https://127.0.0.1:{enrollment.Port}"), enrollment.BootstrapEnrollEndpoint);
        Assert.Equal(new Uri($"https://127.0.0.1:{dgx.Port}"), new Uri(enrollment.BootstrapAgentAddress!));
        DevJoinTokenOptions join = master.GetSection(DevJoinTokenOptions.SectionName).Get<DevJoinTokenOptions>()!;
        Assert.Equal(new[] { pc.NodeId }, join.Nodes);
        Assert.True(join.AutoApprove);
        Assert.Equal(root + "/join", join.OutputDirectory);
        DevJoinTokens.Validate(join, new DemoEnvironment("Development"), dgx.NodeId);
        Assert.Contains($"--node-id {pc.NodeId} --state-dir \"$ROOT/{pc.NodeId}/state\" --address https://127.0.0.1:{pc.Port} ", script, StringComparison.Ordinal);

        // Why the demo is Development: both switches refuse to start anywhere else, and the script says what that means.
        Assert.Contains("export ASPNETCORE_ENVIRONMENT=Development DOTNET_ENVIRONMENT=Development", script, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => DevJoinTokens.Validate(join, new DemoEnvironment("Production"), dgx.NodeId));
        // Every model the script adds is placed on one of the two nodes and named as a fake.
        string[][] models = [.. Regex.Match(script, @"^MODELS=""([^""]+)""$", RegexOptions.Multiline).Groups[1].Value.Split(' ').Select(m => m.Split(':'))];
        Assert.NotEmpty(models);
        Assert.All(models, m =>
        {
            Assert.StartsWith("fake-", m[0], StringComparison.Ordinal);
            Assert.Contains(m[1], new[] { dgx.NodeId, pc.NodeId });
        });
    }

    /// <summary>A demo node's agent.json, checked as the agent checks it at start: the fake runtime, on loopback only.</summary>
    private static AgentOptions DemoAgent(string nodeId, string root)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(RepoFile("deploy", "demo", $"agent.{nodeId}.json"), optional: false, reloadOnChange: false).Build();
        AgentOptions agent = AgentOptions.FromConfiguration(configuration);
        Assert.Equal(nodeId, agent.NodeId);
        Assert.Equal("127.0.0.1", agent.ListenAddress);
        Assert.Equal($"{root}/{nodeId}/state", agent.StateDirectory);
        Assert.Equal($"{root}/{nodeId}/models", agent.ModelsRoot);
        Assert.NotNull(DevFakeRuntime.Configure(configuration, new DemoEnvironment("Development"), agent));
        Assert.Throws<AgentStartupException>(() => DevFakeRuntime.Configure(configuration, new DemoEnvironment("Production"), agent));
        return agent;
    }

    [Fact]
    public async Task ReleaseNotes_Markdown_ListsThePullRequestsOfTheRelease_NewestFirst_ByGroup()
    {
        if (await PythonAsync() is not { } python)
        {
            Assert.Skip("Runs deploy/release-notes.py: needs Python 3.");
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), "mirbadllm-release-notes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string entries = Path.Combine(dir, "entries.json");
            File.WriteAllText(entries, """
                {"entries": [
                 {"number": 1, "title": "Old", "mergedAt": "2026-01-01T00:00:00Z", "commit": "aaa1", "url": null, "added": ["In the previous release"], "fixed": [], "changed": []},
                 {"number": 2, "title": "Two", "mergedAt": "2026-01-02T00:00:00Z", "commit": "bbb2", "url": null, "added": ["Feature two"], "fixed": ["Fix two"], "changed": []},
                 {"number": 3, "title": "Three", "mergedAt": "2026-01-03T00:00:00Z", "commit": "ccc3", "url": null, "added": ["Feature three"], "fixed": [], "changed": ["Change three"]}
                ]}
                """);
            string commits = Path.Combine(dir, "commits");
            string markdown = Path.Combine(dir, "notes.md");
            string[] args = [RepoFile("deploy", "release-notes.py"), "--repo", "owner/name", "--entries", entries, "--output", Path.Combine(dir, "out.json"), "--commits", commits, "--markdown", markdown];

            // The commits since the previous release, as "git rev-list" prints them.
            File.WriteAllText(commits, "ccc3\nbbb2\nffff\n");
            var run = await RunAsync(python, args, []);
            Assert.True(run.Exit == 0, run.Error);
            Assert.Equal(
                "### Added\n\n- Feature three (#3)\n- Feature two (#2)\n\n### Fixed\n\n- Fix two (#2)\n\n### Changed\n\n- Change three (#3)\n",
                File.ReadAllText(markdown));
            // The image's notes stay the whole history, whatever the release lists.
            Assert.Equal(3, Regex.Count(File.ReadAllText(Path.Combine(dir, "out.json")), "\"number\""));

            File.WriteAllText(commits, "ffff\n");
            run = await RunAsync(python, args, []);
            Assert.True(run.Exit == 0, run.Error);
            Assert.Equal("No release notes were collected for this release.\n", File.ReadAllText(markdown));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- helpers ----

    /// <summary>A compose file's lines without comments, blank lines and <c>build:</c> sections.</summary>
    private static string[] ComposeLines(string file)
    {
        var lines = new List<string>();
        bool build = false;
        foreach (string line in File.ReadAllLines(RepoFile("deploy", file)))
        {
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            build = line == "    build:" || (build && line.StartsWith("      ", StringComparison.Ordinal));
            if (!build)
            {
                lines.Add(line);
            }
        }

        return [.. lines];
    }

    private static readonly string[] ReleaseBashLocations = OperatingSystem.IsWindows()
        ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")]
        : ["/bin/bash", "/usr/bin/bash"];

    private static string? ReleaseBash() => ReleaseBashLocations.FirstOrDefault(File.Exists);

    /// <summary>The Python 3 on the PATH, if any (on Windows, <c>python3</c> can be a stub that only opens the Store).</summary>
    private static async Task<string?> PythonAsync()
    {
        foreach (string name in new[] { "python3", "python" })
        {
            try
            {
                var version = await RunAsync(name, ["--version"], []);
                if (version.Exit == 0 && version.Output.StartsWith("Python 3", StringComparison.Ordinal))
                {
                    return name;
                }
            }
            catch (Win32Exception)
            {
                // Not installed under this name.
            }
        }

        return null;
    }

    /// <summary>Runs a program from the repository root and returns its exit code and output.</summary>
    private static async Task<(int Exit, string Output, string Error)> RunAsync(string file, string[] args, Dictionary<string, string> env)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, WorkingDirectory = RepoRoot(),
        };
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
        return (process.ExitCode, (await output).ReplaceLineEndings("\n"), await error);
    }

    private sealed class DemoEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
