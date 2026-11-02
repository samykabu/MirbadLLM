using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Web;
using MirbadLLM.Web.Enrollment;

namespace MirbadLLM.UnitTests.Deploy;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// The packaging artifacts (MN-041) agree with the code they configure: every <c>agent.json</c> binds and validates through
/// <see cref="AgentOptions"/>, the P1 compose file's settings name real options and line up between master and agent
/// (bootstrap directory, enroll endpoint, agent address, download helper image), the exposure and stop rules of the W4
/// review hold, the Dockerfiles copy every project they build and pull the official .NET base images, and CI stays on
/// self-hosted runners. No Docker needed: the files are read as text (no YAML package, rule 6).
/// </summary>
public sealed partial class DeployArtifactsTests
{
    private const string AgentEnvPrefix = AgentOptions.EnvironmentPrefix;

    /// <summary>The master publishes the application port only (W4 review "Exposure").</summary>
    // 8443 is published for the LAN PCs' enrollment and rekey (P2, MN-124, D105); the agent publishes nothing.
    private static readonly string[] ExpectedMasterPorts = ["${MIRBADLLM_PORT:-8080}:8080", "${MIRBADLLM_ENROLL_PORT:-8443}:8443"];

    [Theory]
    [InlineData("agent.dgx.json", "dgx")]
    [InlineData("agent.linux.example.json", "pc-1")]
    [InlineData("agent.windows.example.json", "win-1")]
    public void AgentJsonFiles_BindAndValidate(string file, string nodeId)
    {
        AgentOptions options = AgentOptions.FromConfiguration(
            new ConfigurationBuilder().AddJsonFile(RepoFile("deploy", "agent", file), optional: false, reloadOnChange: false).Build());

        Assert.Equal(nodeId, options.NodeId);
        Assert.Equal(Protocol.DefaultAgentPort, options.Port);
        Assert.Equal(TimeSpan.FromSeconds(30), options.ShutdownTimeout);
        if (nodeId != "dgx")
        {
            // Remote nodes enroll with "join" and run on the host: no bootstrap bundle, loopback upstreams, no hostd.
            Assert.Null(options.BootstrapDirectory);
            Assert.False(options.UseContainerNetwork);
            Assert.False(options.AllowTagPulls);
            Assert.False(options.ManageBackupMount);
            Assert.False(options.ManageSystemdUnits);
            Assert.Equal(options.ModelsRoot, options.ModelsHostPath);
        }
    }

    [Fact]
    public void DgxAgent_WithComposeOverrides_IsTheP1ConfigurationSurface()
    {
        AgentOptions agent = DgxAgentOptions();

        // Plan §3.6 W4 notes: today's node settings 1:1 plus the agent-only ones.
        Assert.Equal("dgx", agent.NodeId);
        Assert.Equal(RegistryMigrationOptions.DefaultBootstrapNodeId, agent.NodeId);
        Assert.True(agent.AllowTagPulls);
        Assert.Equal(BootstrapBundle.DefaultDirectory, agent.BootstrapDirectory);
        Assert.Equal("mirbadllm", agent.DockerNetwork);
        Assert.True(agent.UseContainerNetwork);
        Assert.Equal(8101, agent.PortRangeStart);
        Assert.Equal(8, agent.MemoryReserveGiB);
        // The GB10 is unified by configuration, not only by the GPU probe (W9 review MN-132, D102).
        Assert.Equal(MemoryModel.Unified, agent.MemoryModel);
        Assert.True(agent.HostdExpected);
        Assert.Equal(HostServicesOptions.DefaultHostAgentSocket, agent.HostdSocket);
        Assert.Equal("unix:///var/run/docker.sock", agent.DockerEndpoint);

        var defaults = new MirbadLLMOptions();
        Assert.Equal(defaults.ModelsHostPath, agent.ModelsHostPath);
        Assert.Equal(defaults.CacheHostPath, agent.CacheHostPath);
        Assert.Equal(defaults.VllmImage, agent.DownloadHelperImage);

        // Every container path in agent.json is a mount target in compose.
        string[] volumes = [.. ListItems(Section(Service("agent"), "volumes"))];
        Assert.Contains(volumes, v => v.EndsWith(":" + agent.StateDirectory, StringComparison.Ordinal) && !v.StartsWith('/'));
        Assert.Contains(volumes, v => v.EndsWith(":" + agent.ModelsRoot, StringComparison.Ordinal));
        Assert.Contains(volumes, v => v.EndsWith(":" + agent.CacheRoot, StringComparison.Ordinal));
        Assert.Contains(volumes, v => v.EndsWith(":" + agent.BootstrapDirectory, StringComparison.Ordinal));
        Assert.Contains(volumes, v => v == "/var/run/docker.sock:/var/run/docker.sock");
        // The host folder falls back to the pre-rename host agent's (ci-deploy-multinode.sh sets MIRBADLLM_HOSTD_RUN).
        string hostdRun = Path.GetDirectoryName(agent.HostdSocket)!.Replace('\\', '/');
        Assert.Contains(volumes, v => v == $"${{MIRBADLLM_HOSTD_RUN:-{hostdRun}}}:{hostdRun}:ro");
    }

    [Fact]
    public void MasterAndAgent_AgreeOnEnrollment()
    {
        AgentOptions agent = DgxAgentOptions();
        IConfiguration master = ToConfiguration(ServiceEnvironment(Service("master")), prefix: "");
        EnrollmentOptions enrollment = master.GetSection(EnrollmentOptions.SectionName).Get<EnrollmentOptions>()!;
        string masterName = Scalar(Service("master"), "container_name");
        string agentName = Scalar(Service("agent"), "container_name");

        // Container names keep existing tooling and the agent's host-service deny rules working (MN-041 brief).
        Assert.Equal("mirbadllm", masterName);
        Assert.Equal("mirbadllm-agent", agentName);
        Assert.Equal(masterName, agent.MasterContainerName);
        Assert.Equal(agentName, agent.AgentContainerName);

        Assert.True(enrollment.BootstrapEnabled);
        Assert.Equal(Protocol.DefaultEnrollPort, enrollment.Port);
        Assert.Equal(new Uri($"https://{masterName}:{enrollment.Port}"), enrollment.BootstrapEnrollEndpoint);
        Assert.Equal(agent.BootstrapDirectory, enrollment.BootstrapDirectory);
        // D59: the agent container on the host's Docker network reads agent-mtls-loopback.
        Assert.Equal(MasterHosting.DefaultBootstrapAgentAddress, enrollment.BootstrapAgentAddress);
        Assert.Equal(new Uri($"https://{agentName}:{agent.Port}"), new Uri(enrollment.BootstrapAgentAddress!));
        Assert.Contains(agentName, agent.Addresses);

        // The node record, migration and auto-start all use the same bootstrap node id.
        Assert.Equal(agent.NodeId, master[$"{MirbadLLMOptions.SectionName}:Nodes:BootstrapNodeId"]);
        // D65: the image's application port stays where clients expect it.
        Assert.Equal("8080", master["ASPNETCORE_HTTP_PORTS"]);
    }

    [Fact]
    public void DownloadHelperImage_FollowsTheMastersVllmImage()
    {
        // W4 review (MN-096): both come from the one variable, with the code default when it is unset.
        string masterRaw = RawEnvironment(Service("master"))["MirbadLLM__VllmImage"];
        string agentRaw = RawEnvironment(Service("agent"))[AgentEnvPrefix + "Agent__DownloadHelperImage"];
        Assert.Equal(masterRaw, agentRaw);
        Assert.StartsWith("${MIRBADLLM_VLLM_IMAGE:-", masterRaw, StringComparison.Ordinal);
        Assert.Equal(new MirbadLLMOptions().VllmImage, Substitute(masterRaw));
    }

    [Fact]
    public void ComposeEnvironment_NamesOnlyExistingSettings()
    {
        foreach (string key in RawEnvironment(Service("agent")).Keys)
        {
            Assert.StartsWith(AgentEnvPrefix + AgentOptions.SectionName + "__", key, StringComparison.Ordinal);
            AssertSetting(typeof(AgentOptions), key[(AgentEnvPrefix.Length + AgentOptions.SectionName.Length + 2)..], key);
        }

        AssertMasterSettings(RawEnvironment(Service("master")).Keys);
    }

    [Fact]
    public void KubernetesDeployment_NamesOnlyExistingSettings_AndRunsOneReplica()
    {
        string deployment = File.ReadAllText(RepoFile("deploy", "k8s", "master", "deployment.yaml"));
        string secret = File.ReadAllText(RepoFile("deploy", "k8s", "master", "secret.example.yaml"));
        List<string> keys = [.. K8sEnvNameRegex().Matches(deployment).Select(m => m.Groups[1].Value)];
        keys.AddRange(SecretKeyRegex().Matches(secret).Select(m => m.Groups[1].Value));
        Assert.NotEmpty(keys);
        AssertMasterSettings(keys);

        Assert.Contains("replicas: 1", deployment, StringComparison.Ordinal);
        Assert.Matches(@"strategy:\s+type: Recreate", deployment);
        // Topology (b): no co-located agent, so no bootstrap bundle.
        Assert.Matches(@"MirbadLLM__Enrollment__BootstrapEnabled\s+value: ""false""", deployment);
        Assert.DoesNotContain("docker.sock", deployment, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_Exposure_StopGrace_AndNoPkiKeys()
    {
        string master = Service("master");
        string agent = Service("agent");

        // 8080 and the enroll listener's 8443 (P2: Linux PCs join and rekey over the LAN, D105); the agent publishes nothing
        // (the master dials https://mirbadllm-agent:9443 over the mirbadllm network).
        string[] masterPorts = [.. ListItems(Section(master, "ports"))];
        Assert.Equal(ExpectedMasterPorts, masterPorts);
        Assert.Contains("8443", ListItems(Section(master, "expose")).ToList());
        Assert.Empty(Section(agent, "ports"));
        Assert.Contains(Section(master, "networks"), l => l.Trim() == "mirbadllm:");
        Assert.Contains(Section(agent, "networks"), l => l.Trim() == "mirbadllm:");
        // A master deployed before the rename recorded the DGX agent as https://spark-serve-agent:9443.
        Assert.Contains(Section(agent, "networks"), l => l.Trim() == "aliases: [spark-serve-agent]");

        // The master has none of the node's resources (design §2.2).
        string masterVolumes = string.Join('\n', Section(master, "volumes"));
        Assert.DoesNotContain("docker.sock", masterVolumes, StringComparison.Ordinal);
        Assert.DoesNotContain(":/models", masterVolumes, StringComparison.Ordinal);
        Assert.DoesNotContain("mirbadllm-hostd", masterVolumes, StringComparison.Ordinal);
        Assert.Contains("propagation: rslave", masterVolumes, StringComparison.Ordinal);
        Assert.Empty(Section(master, "deploy"));
        Assert.Empty(Section(master, "group_add"));

        // D63: the DGX keeps its CA in /data/pki.
        Assert.DoesNotContain(RawEnvironment(master).Keys, k => k.StartsWith("MirbadLLM__Pki__", StringComparison.Ordinal));

        // W4 review: Docker's stop grace must exceed the agent's 30 s drain (≥ 40 s), for the master too.
        AgentOptions options = DgxAgentOptions();
        foreach (string service in new[] { master, agent })
        {
            TimeSpan grace = ParseDuration(Scalar(service, "stop_grace_period"));
            Assert.True(grace >= TimeSpan.FromSeconds(40), $"stop_grace_period {grace}");
            Assert.True(grace >= options.ShutdownTimeout + TimeSpan.FromSeconds(10), $"stop_grace_period {grace}");
        }
    }

    /// <summary>
    /// Every deploy/ path a Dockerfile copies is let through its allow-list .dockerignore (BuildKit's
    /// &lt;Dockerfile&gt;.dockerignore starts from "**"): the macOS bundle stage once failed only in the real image build,
    /// with "/deploy/agent/macos: not found", because nothing else reads the ignore file.
    /// </summary>
    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("agent.Dockerfile")]
    public void Dockerfiles_EveryCopiedDeployPath_IsInTheBuildContext(string dockerfile)
    {
        string[] allowed = [.. File.ReadAllLines(RepoFile("deploy", dockerfile + ".dockerignore"))
            .Select(l => l.Trim()).Where(l => l.StartsWith('!')).Select(l => l[1..])];
        foreach (Match copy in Regex.Matches(File.ReadAllText(RepoFile("deploy", dockerfile)), @"^COPY (?!--from)(.+) \S+$", RegexOptions.Multiline))
        {
            foreach (string source in copy.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(p => p.StartsWith("deploy/", StringComparison.Ordinal)))
            {
                string path = source.TrimEnd('/');
                // A file is listed itself or under an allowed folder/**; a copied folder needs at least one allowed path in it.
                bool covered = allowed.Any(a => a == path
                    || (a.EndsWith("/**", StringComparison.Ordinal) && (path + "/").StartsWith(a[..^2], StringComparison.Ordinal))
                    || (source.EndsWith('/') && a.StartsWith(path + "/", StringComparison.Ordinal)));
                Assert.True(covered, $"{dockerfile} copies {source}, which deploy/{dockerfile}.dockerignore leaves out of the build context.");
            }
        }
    }

    [Theory]
    [InlineData("Dockerfile", "src/MirbadLLM.Web/MirbadLLM.Web.csproj")]
    [InlineData("agent.Dockerfile", "src/MirbadLLM.Agent/MirbadLLM.Agent.csproj")]
    public void Dockerfiles_CopyEveryProjectTheyBuild_AndUseTheOfficialDotnetImages(string dockerfile, string entryProject)
    {
        string text = File.ReadAllText(RepoFile("deploy", dockerfile));
        foreach (Match from in FromRegex().Matches(text))
        {
            // The official .NET images; a registry mirror is a local change to the FROM lines.
            Assert.StartsWith("mcr.microsoft.com/dotnet/", from.Groups[1].Value, StringComparison.Ordinal);
        }

        // The csproj-only restore layer needs every referenced project file (MN-041 found the master's missing three).
        foreach (string project in ProjectClosure(entryProject))
        {
            Assert.Contains($"COPY {project} {Path.GetDirectoryName(project)!.Replace('\\', '/')}/", text, StringComparison.Ordinal);
        }

        Assert.Contains("--chown=1654:1654 --chmod=0700 /layout/bootstrap " + BootstrapBundle.DefaultDirectory, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentImage_ShipsTheDgxConfiguration_WhereTheAgentLooksForIt()
    {
        string text = File.ReadAllText(RepoFile("deploy", "agent.Dockerfile"));
        Assert.Contains("COPY deploy/agent/agent.dgx.json /etc/mirbadllm-agent/agent.json", text, StringComparison.Ordinal);
        Assert.Contains($"{AgentOptions.ConfigPathEnvironmentVariable}=/etc/mirbadllm-agent/agent.json", text, StringComparison.Ordinal);
        Assert.Contains($"/layout/state {DgxAgentOptions().StateDirectory}", text, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"mirbadllm-agent.dll\"]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LinuxUnit_RunsTheDedicatedUser_AndOutlastsTheDrain()
    {
        string unit = File.ReadAllText(RepoFile("deploy", "agent", "linux", "mirbadllm-agent.service"));
        Assert.Contains("User=mirbadllm-agent", unit, StringComparison.Ordinal);
        Assert.Contains("SupplementaryGroups=docker", unit, StringComparison.Ordinal);
        Assert.Contains("ExecStart=/usr/local/lib/mirbadllm-agent/mirbadllm-agent --config /etc/mirbadllm-agent/agent.json", unit, StringComparison.Ordinal);
        int configurationExit = global::MirbadLLM.Agent.Program.ExitConfiguration;
        Assert.Contains($"RestartPreventExitStatus={configurationExit}", unit, StringComparison.Ordinal);
        int stop = int.Parse(Regex.Match(unit, @"^TimeoutStopSec=(\d+)\r?$", RegexOptions.Multiline).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(TimeSpan.FromSeconds(stop) >= new AgentOptions().ShutdownTimeout + TimeSpan.FromSeconds(10));
        // .NET's JIT needs writable+executable pages.
        Assert.DoesNotContain("MemoryDenyWriteExecute=yes", unit, StringComparison.Ordinal);

        string install = File.ReadAllText(RepoFile("deploy", "agent", "linux", "install-agent.sh"));
        Assert.Contains("deploy/agent/agent.linux.example.json", install, StringComparison.Ordinal);
        Assert.Contains("\"NodeId\": \"pc-1\"", File.ReadAllText(RepoFile("deploy", "agent", "agent.linux.example.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void Workflows_UseSelfHostedRunnersOnly()
    {
        foreach (string workflow in Directory.GetFiles(RepoFile(".github", "workflows"), "*.yml"))
        {
            string text = File.ReadAllText(workflow);
            MatchCollection runsOn = RunsOnRegex().Matches(text);
            Assert.NotEmpty(runsOn);
            foreach (Match m in runsOn)
            {
                Assert.Contains("self-hosted", m.Groups[1].Value, StringComparison.Ordinal);
                Assert.DoesNotContain("-latest", m.Groups[1].Value, StringComparison.Ordinal);
                Assert.DoesNotContain("macos", m.Groups[1].Value, StringComparison.Ordinal);
            }
        }

        // The build container's memory cap (W3 review MN-094 incident) stays.
        Assert.Contains("--memory=", File.ReadAllText(RepoFile(".github", "workflows", "ci.yml")), StringComparison.Ordinal);
    }

    // ---- helpers ----

    private static AgentOptions DgxAgentOptions()
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonFile(RepoFile("deploy", "agent", "agent.dgx.json"), optional: false, reloadOnChange: false)
            .AddConfiguration(ToConfiguration(ServiceEnvironment(Service("agent")), AgentEnvPrefix))
            .Build();
        return AgentOptions.FromConfiguration(configuration);
    }

    private static void AssertMasterSettings(IEnumerable<string> keys)
    {
        var sections = new (string Prefix, Type[] Types)[]
        {
            ("MirbadLLM__Backup__", [typeof(BackupOptions)]),
            ("MirbadLLM__Enrollment__", [typeof(EnrollmentOptions)]),
            ("MirbadLLM__Nodes__", [typeof(RegistryMigrationOptions.NodesSettings)]),
            ("MirbadLLM__Pki__", [typeof(MasterPkiOptions)]),
            ("MirbadLLM__", [typeof(MirbadLLMOptions), typeof(AuthOptions)]),
        };
        foreach (string key in keys)
        {
            if (key == "ASPNETCORE_HTTP_PORTS")
            {
                continue;
            }

            (string prefix, Type[] types) = sections.First(s => key.StartsWith(s.Prefix, StringComparison.Ordinal));
            string name = key[prefix.Length..];
            // ListenerAddresses__0 and the like: a list element.
            name = Regex.Replace(name, "__\\d+$", "");
            Assert.True(
                types.Any(t => t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null),
                $"{key}: no setting '{name}' on {string.Join(" or ", types.Select(t => t.Name))}");
        }
    }

    private static void AssertSetting(Type type, string name, string key) =>
        Assert.True(type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is { } p && (p.CanWrite || p.PropertyType != typeof(string)),
            $"{key}: no setting '{name}' on {type.Name}");

    private static IConfiguration ToConfiguration(IReadOnlyDictionary<string, string> environment, string prefix) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(environment
                .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => KeyValuePair.Create(kv.Key[prefix.Length..].Replace("__", ":", StringComparison.Ordinal), (string?)kv.Value)))
            .Build();

    private static Dictionary<string, string> ServiceEnvironment(string service) =>
        RawEnvironment(service).ToDictionary(kv => kv.Key, kv => Substitute(kv.Value), StringComparer.Ordinal);

    private static Dictionary<string, string> RawEnvironment(string service)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in Section(service, "environment"))
        {
            Match m = EnvLineRegex().Match(line);
            Assert.True(m.Success, $"unexpected environment line '{line}'");
            result.Add(m.Groups[1].Value, m.Groups[2].Value.Trim().Trim('"'));
        }

        return result;
    }

    /// <summary>Compose interpolation with nothing set: <c>${VAR:-default}</c> → default, <c>${VAR}</c> → empty.</summary>
    private static string Substitute(string value) =>
        InterpolationRegex().Replace(value, m => m.Groups[2].Success ? m.Groups[2].Value : "");

    private static string ComposeText() => File.ReadAllText(RepoFile("deploy", "compose.multinode.yaml")).ReplaceLineEndings("\n");

    /// <summary>The lines of a top-level service (indent 2) up to the next service or top-level key.</summary>
    private static string Service(string name)
    {
        string[] lines = ComposeText().Split('\n');
        int start = Array.IndexOf(lines, $"  {name}:");
        Assert.True(start >= 0, $"service {name} not found");
        var block = new List<string>();
        for (int i = start + 1; i < lines.Length && (lines[i].Length == 0 || lines[i].StartsWith("   ", StringComparison.Ordinal) || lines[i].TrimStart().StartsWith('#')); i++)
        {
            block.Add(lines[i]);
        }

        return string.Join('\n', block);
    }

    /// <summary>The non-comment lines nested under a service key (indent 4), without their indentation.</summary>
    private static IEnumerable<string> Section(string service, string key)
    {
        string[] lines = service.Split('\n');
        int start = Array.IndexOf(lines, $"    {key}:");
        if (start < 0)
        {
            yield break;
        }

        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (!line.StartsWith("     ", StringComparison.Ordinal))
            {
                yield break;
            }

            yield return line.Trim();
        }
    }

    private static IEnumerable<string> ListItems(IEnumerable<string> section) =>
        section.Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..].Trim().Trim('"'));

    private static string Scalar(string service, string key)
    {
        Match m = Regex.Match(service, $@"^    {Regex.Escape(key)}: (.+)$", RegexOptions.Multiline);
        Assert.True(m.Success, $"{key} not set");
        return m.Groups[1].Value.Trim();
    }

    private static TimeSpan ParseDuration(string value)
    {
        Match m = Regex.Match(value, @"^(\d+)(s|m)$");
        Assert.True(m.Success, $"duration '{value}'");
        int n = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value == "m" ? TimeSpan.FromMinutes(n) : TimeSpan.FromSeconds(n);
    }

    /// <summary>The entry project and every project it references, transitively, as repo-relative paths.</summary>
    private static SortedSet<string> ProjectClosure(string entryProject)
    {
        string root = RepoRoot();
        var result = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([entryProject]);
        while (pending.TryPop(out string? project))
        {
            if (!result.Add(project))
            {
                continue;
            }

            string full = Path.Combine(root, project);
            foreach (Match m in ProjectReferenceRegex().Matches(File.ReadAllText(full)))
            {
                string referenced = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar)));
                pending.Push(Path.GetRelativePath(root, referenced).Replace('\\', '/'));
            }
        }

        return result;
    }

    private static string RepoFile(params string[] parts) => Path.Combine([RepoRoot(), .. parts]);

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

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*): ?(.*)$")]
    private static partial Regex EnvLineRegex();

    [GeneratedRegex(@"\$\{([A-Z0-9_]+)(?::-([^}]*))?\}")]
    private static partial Regex InterpolationRegex();

    [GeneratedRegex(@"- name: (MirbadLLM__\w+|ASPNETCORE_\w+)")]
    private static partial Regex K8sEnvNameRegex();

    [GeneratedRegex(@"^  (MirbadLLM__\w+):", RegexOptions.Multiline)]
    private static partial Regex SecretKeyRegex();

    [GeneratedRegex(@"^FROM (?:--platform=\S+ )?(\S+)", RegexOptions.Multiline)]
    private static partial Regex FromRegex();

    [GeneratedRegex(@"<ProjectReference Include=""([^""]+)""")]
    private static partial Regex ProjectReferenceRegex();

    [GeneratedRegex(@"__\d+$")]
    private static partial Regex ListIndexRegex();

    [GeneratedRegex(@"runs-on:\s*(.+)")]
    private static partial Regex RunsOnRegex();
}
