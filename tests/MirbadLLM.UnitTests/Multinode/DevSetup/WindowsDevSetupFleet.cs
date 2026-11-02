using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Enrollment;
using MirbadLLM.Agent.Fakes;
using MirbadLLM.Agent.HostServices.Windows;
using MirbadLLM.Agent.Proxy;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes.Process;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Agent.Fakes;
using MirbadLLM.UnitTests.HostServices;
using MirbadLLM.UnitTests.Multinode;
using MirbadLLM.Web.Enrollment;
using AgentProgram = MirbadLLM.Agent.Program;
using MirbadLLM.Agent.Storage;

namespace MirbadLLM.UnitTests.Multinode.DevSetup;

/// <summary>
/// MN-236: the Windows development setup on one box — the real master (Development, Kestrel, the enroll listener and the
/// dev join-token helper), the existing Linux dev agent <c>dgx</c> (unified, <see cref="DevFakeRuntime"/>, bootstrap
/// enrollment) and a <b>Windows dev agent</b> <c>win-1</c> composed exactly as the owner's PC is (design-p3p4 §A.4.3):
/// <c>Agent:Docker:Enabled = false</c>, so <see cref="AddMirbadLLMAgent"/> registers the real
/// <see cref="ProcessRuntimeDriver"/> and its collaborators, with the fake <c>llama-server</c>
/// (<see cref="MirbadLLM.FakeLlamaServer.FakeLlamaServer"/>) registered as the logical executable
/// <c>llama-server</c> in a temp catalogue, and the fake runtime's discrete GPUs under it.
/// <para><b>Why the fake runtime is on as well.</b> The Windows dev node needs GPUs and host memory it does not have, so
/// <c>DevFakeRuntime:Memory = discrete:2x16</c> answers <c>nvidia-smi</c> and the memory probe. That registers an
/// <see cref="IRuntimeDriver"/> of its own, and with Docker off the process driver is <b>added next to it</b> rather than
/// replacing it (only the Docker-on path replaces) — the two-driver composition
/// <see cref="MirbadLLM.Agent.Proxy.RuntimeDriverUpstreamResolver"/>'s per-driver fold and
/// <see cref="MirbadLLM.Agent.Proxy.AgentProxy"/>'s key walk exist for (MN-234 review note, D131).</para>
/// <para>Off Windows the only seams injected are the ones the composition cannot supply itself
/// (<see cref="IProcessHost"/>, <see cref="IInstanceKeyProtector"/>, <see cref="IServiceControlManager"/>), which is what
/// lets the Linux CI leg run this setup; on Windows they take the place of the real job-object host, DPAPI and the
/// Service Control Manager, so no real process is started and no Windows service is touched.</para>
/// <para>The catalogue therefore holds <see cref="LlamaCppVersionFolder"/>'s stub <c>llama-server.exe</c> and its
/// <c>manifest.json</c> — the real catalogue code hashes and verifies it, and the real launch validator resolves the
/// argv against it — while <see cref="FakeProcessHost"/> serves the launch with the real
/// <see cref="MirbadLLM.FakeLlamaServer.FakeLlamaServer"/> in process, on the port and with the environment the spec
/// asks for. Exec'ing the published <c>fake-llama-server.exe</c> through <c>WindowsProcessHost</c> is the Windows-only
/// half of <c>ProcessRuntimeDriverTests</c>, and it is what the documented script in
/// <c>docs/multinode/dev-setup-windows.md</c> registers.</para>
/// </summary>
internal sealed class WindowsDevSetupFleet : IAsyncDisposable
{
    /// <summary>The Linux dev agent of <c>tools/dev-multinode</c> (unified, as the DGX).</summary>
    public const string DgxId = "dgx";

    /// <summary>The Windows dev agent (the node id of the P3 fixtures and of <c>agent.windows.example.json</c>).</summary>
    public const string WinId = LlamaCppFixtures.NodeId;

    /// <summary>The fake GPUs of <c>win-1</c>, in index order: the UUIDs the P3 model fixtures place on.</summary>
    public static readonly string[] WinGpus = [LlamaCppFixtures.Rtx5070, LlamaCppFixtures.Rtx5060Ti];

    /// <summary>The llama.cpp versions the dev catalogue holds, newest last; <see cref="DefaultLlamaCppVersion"/> is the default.</summary>
    public static readonly string[] LlamaCppVersions = ["b11410", "b11476"];

    /// <summary>The version the dev catalogue makes the node's default (the one <c>llama-cpp.lock.json</c> pins).</summary>
    public const string DefaultLlamaCppVersion = "b11476";

    /// <summary>Host ports each agent hands its models (<c>PortRangeStart..PortRangeEnd</c>).</summary>
    private const int ModelPortsPerAgent = 20;

    private readonly string _root;
    private readonly DevMaster _master;
    private readonly List<LlamaCppVersionFolder> _catalogue;
    private readonly int _winPort;
    private readonly int _winModelPorts;

    private WindowsDevSetupFleet(string root, DevMaster master, HttpClient http, List<LlamaCppVersionFolder> catalogue, int winPort, int winModelPorts)
    {
        _root = root;
        _master = master;
        _catalogue = catalogue;
        _winPort = winPort;
        _winModelPorts = winModelPorts;
        Http = http;
    }

    /// <summary>The master's admin API and <c>/v1</c>, over Kestrel.</summary>
    public HttpClient Http { get; }

    public IServiceProvider Services => _master.Services;

    /// <summary>The Linux dev agent, as <c>tools/dev-multinode/start.ps1</c> starts it.</summary>
    public WebApplication Dgx { get; private set; } = null!;

    /// <summary>The Windows dev agent (recreated by <see cref="RestartWindowsAgentAsync"/>).</summary>
    public WebApplication Win { get; private set; } = null!;

    /// <summary>The "operating system" of <c>win-1</c>: the process table its llama-server children live in.</summary>
    public FakeProcessTable WinProcesses { get; } = new();

    /// <summary>The job-object host of the current <c>win-1</c> agent (a new one after every restart).</summary>
    public FakeProcessHost WinHost { get; private set; } = null!;

    /// <summary>DPAPI's stand-in for the per-instance <c>LLAMA_API_KEY</c> files.</summary>
    public FakeInstanceKeyProtector WinKeys { get; } = new();

    /// <summary>The Service Control Manager <c>services.scm</c> is reported over.</summary>
    public FakeScm WinScm { get; } = new();

    public string WinModelsRoot => Path.Combine(_root, WinId, "models");

    public string WinStateDirectory => Path.Combine(_root, WinId, "state");

    public string WinCacheRoot => Path.Combine(_root, WinId, "cache");

    /// <summary>The <c>Agent:Executables</c> the dev setup generates: one entry for <c>llama-server</c>, every version in it.</summary>
    public ExecutableOptions LlamaCppExecutable() => LlamaCppVersionFolder.Options(DefaultLlamaCppVersion, [.. _catalogue]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Brings the whole dev setup up: the master, <c>dgx</c> in bootstrap mode, then <c>win-1</c> through
    /// <c>mirbadllm-agent join</c> with the dev helper's token and <c>POST /api/nodes/win-1/approve</c> — the two
    /// acceptance items "enrollment" and "approval" — and waits until the master has read both nodes' capability reports.
    /// </summary>
    public static async Task<WindowsDevSetupFleet> StartAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-win-dev-" + Guid.NewGuid().ToString("N")[..12]);
        var catalogue = new List<LlamaCppVersionFolder>();
        WindowsDevSetupFleet? fleet = null;
        try
        {
            // One reserved block, as the dev scripts lay the fixed ports out: the listeners, then a model-port range per
            // agent (the ranges must not overlap — each agent hands out its own, unaware of the other).
            int ports = TestPorts.ReserveBlock(3 + (2 * ModelPortsPerAgent));
            int enrollPort = ports;
            int dgxPort = ports + 1;
            int winPort = ports + 2;
            int dgxModelPorts = ports + 3;
            int winModelPorts = dgxModelPorts + ModelPortsPerAgent;
            string bootstrapDir = Path.Combine(root, "bootstrap");
            string joinDir = Path.Combine(root, "join");

            // The fake llama-server as the dev catalogue registers it: one folder per version with its manifest.json,
            // under the node's own runtimes root (install-agent.ps1's "C:\Program Files\mirbadllm-agent\runtimes").
            foreach (string version in LlamaCppVersions)
            {
                catalogue.Add(LlamaCppVersionFolder.Create(version, Path.Combine(root, WinId, "install")));
            }

            var master = new DevMaster(new Dictionary<string, string?>
            {
                ["MirbadLLM:DataPath"] = Path.Combine(root, "master-data"),
                ["MirbadLLM:Backup:Directory"] = Path.Combine(root, "backups"),
                ["MirbadLLM:HostAgentSocket"] = Path.Combine(root, "no-hostd.sock"),
                ["MirbadLLM:DockerEndpoint"] = "unix:///nonexistent/mirbadllm-tests/docker.sock",
                ["MirbadLLM:Enrollment:Port"] = enrollPort.ToString(CultureInfo.InvariantCulture),
                ["MirbadLLM:Enrollment:BootstrapDirectory"] = bootstrapDir,
                ["MirbadLLM:Enrollment:BootstrapEnrollEndpoint"] = $"https://127.0.0.1:{enrollPort}",
                ["MirbadLLM:Enrollment:BootstrapAgentAddress"] = $"https://127.0.0.1:{dgxPort}",
                ["MirbadLLM:DevJoinTokens:Nodes:0"] = WinId,
                ["MirbadLLM:DevJoinTokens:OutputDirectory"] = joinDir,
                ["MirbadLLM:DevJoinTokens:AutoApprove"] = "false", // approved through the node API below, as the scripts do
                // The job drain waits for a metrics sample newer than the job's start on every Ready model of the node
                // (ModelJobService.DrainAsync); the master pushes this interval to the agents when they connect.
                ["MirbadLLM:MetricsSampleIntervalSeconds"] = "1",
                ["MirbadLLM:StartupTimeout"] = "00:00:30",
            });
            master.UseKestrel();
            master.StartServer();
            var http = new HttpClient { BaseAddress = master.HttpAddress(), Timeout = TimeSpan.FromSeconds(120) };
            fleet = new WindowsDevSetupFleet(root, master, http, catalogue, winPort, winModelPorts);

            fleet.Dgx = await fleet.StartDgxAsync(bootstrapDir, dgxPort, dgxModelPorts);
            await fleet.JoinWindowsNodeAsync(joinDir);
            fleet.Win = await fleet.StartWindowsAgentAsync();
            await fleet.WaitOnlineAsync();
            return fleet;
        }
        catch
        {
            if (fleet is not null)
            {
                await fleet.DisposeAsync();
            }
            else
            {
                foreach (var folder in catalogue)
                {
                    folder.Dispose();
                }

                TryDelete(root);
            }

            throw;
        }
    }

    /// <summary>
    /// Stops the Windows agent as the SCM would (graceful: llama-server children keep running, their job handles close
    /// without killing them) and composes a fresh one over the same state directory and the same process table — the
    /// restart an agent upgrade or a reboot of the service performs. The new agent adopts what the old one started.
    /// </summary>
    public async Task<WebApplication> RestartWindowsAgentAsync()
    {
        await StopAsync(Win);
        await Win.DisposeAsync();
        WinHost.SimulateAgentDeath();
        Win = await TestPorts.RestartOnSamePortAsync(StartWindowsAgentAsync, Ct);
        return Win;
    }

    /// <summary>Saves <paramref name="model"/> through the admin API, so the master's placement validation runs.</summary>
    public async Task CreateModelAsync(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        using var created = await Http.PostAsJsonAsync("/api/models", model, Ct);
        string body = await created.Content.ReadAsStringAsync(Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, $"create {model.Id}: {(int)created.StatusCode} {body}");
    }

    /// <summary>Starts <paramref name="id"/> through the admin API, retrying the 409/503 the dev scripts retry.</summary>
    public async Task StartModelAsync(string id)
    {
        string last = "";
        await WaitUntilAsync(async () =>
        {
            using var start = await Http.PostAsync($"/api/models/{id}/start", content: null, Ct);
            last = $"{(int)start.StatusCode} {await start.Content.ReadAsStringAsync(Ct)}";
            Assert.True(start.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable, $"start {id}: {last}");
            return start.StatusCode == HttpStatusCode.OK;
        }, $"start {id} kept answering 409/503: {last}");
    }

    /// <summary>Waits until <paramref name="servedName"/> is listed by the master's <c>/v1/models</c>.</summary>
    public Task WaitServedAsync(string servedName) => WaitUntilAsync(async () =>
    {
        using var models = await Http.GetAsync("/v1/models", Ct);
        return models.StatusCode == HttpStatusCode.OK
            && (await models.Content.ReadAsStringAsync(Ct)).Contains($"\"{servedName}\"", StringComparison.Ordinal);
    }, $"{servedName} never appeared in /v1/models");

    /// <summary>The node record the master holds (with its last capability report).</summary>
    public async Task<NodeRecord> NodeAsync(string id) =>
        await Services.GetRequiredService<INodeRegistry>().GetAsync(id, Ct) ?? throw new InvalidOperationException($"no node record for '{id}'");

    public async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        using var response = await Http.GetAsync(path, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    public async ValueTask DisposeAsync()
    {
        if (Win is not null)
        {
            await TryStopAsync(Win);
            await Win.DisposeAsync();
        }

        if (Dgx is not null)
        {
            await TryStopAsync(Dgx);
            await Dgx.DisposeAsync();
        }

        await WinProcesses.RebootAsync();
        Http.Dispose();
        await _master.DisposeAsync();
        foreach (var folder in _catalogue)
        {
            folder.Dispose();
        }

        TryDelete(_root);
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string message, int seconds = 90)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(seconds);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, message);
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>The Linux dev agent of <c>tools/dev-multinode</c>: the fake runtime, unified memory, bootstrap enrollment.</summary>
    private async Task<WebApplication> StartDgxAsync(string bootstrapDir, int port, int modelPorts)
    {
        var options = BaseOptions(DgxId, port, modelPorts);
        options.BootstrapDirectory = bootstrapDir;
        options.BootstrapWait = TimeSpan.FromSeconds(90);
        options.Addresses.Add("127.0.0.1");
        Directory.CreateDirectory(Path.Combine(options.ModelsRoot, "fake-model"));
        await File.WriteAllTextAsync(
            Path.Combine(options.ModelsRoot, "fake-model", "config.json"),
            """{"architectures":["LlamaForCausalLM"],"model_type":"llama"}""",
            Ct);
        return await StartAgentAsync(options, DgxId, DevFakeSettings("unified:64", gpuUuids: null), configureServices: null);
    }

    /// <summary><c>mirbadllm-agent join</c> with the dev helper's token, then <c>POST /api/nodes/win-1/approve</c>.</summary>
    private async Task JoinWindowsNodeAsync(string joinDir)
    {
        string joinFile = Path.Combine(joinDir, DevJoinFile.FileName(WinId));
        await WaitUntilAsync(() => Task.FromResult(File.Exists(joinFile)), $"the dev join-token helper wrote no join file for {WinId}");
        var join = JsonSerializer.Deserialize<DevJoinFile>(await File.ReadAllTextAsync(joinFile, Ct), JsonSerializerOptions.Web)!;
        Assert.Equal(WinId, join.NodeId);
        using (var output = new StringWriter())
        using (var error = new StringWriter())
        {
            int exit = await JoinCommand.RunAsync(
                ["--enroll-url", join.EnrollUrl.ToString(), "--token", join.Token, "--ca-hash", join.CaHash, "--node-id", WinId,
                 "--state-dir", WinStateDirectory, "--address", $"https://127.0.0.1:{_winPort}"],
                output, error, ct: Ct);
            Assert.True(exit == JoinCommand.ExitOk, $"join exited {exit}: {output}{error}");
        }

        // Enrolled, not approved: the helper only mints tokens; the operator's approval is the node API (MN-102).
        Assert.False((await NodeAsync(WinId)).Approved);
        using var approve = await Http.PostAsync($"/api/nodes/{WinId}/approve", content: null, Ct);
        Assert.True(approve.StatusCode == HttpStatusCode.OK, $"approve {WinId}: {(int)approve.StatusCode} {await approve.Content.ReadAsStringAsync(Ct)}");
        Assert.True((await NodeAsync(WinId)).Approved);
    }

    /// <summary>The Windows dev agent: Docker off, the process driver, the dev catalogue, the fake runtime's GPUs.</summary>
    private async Task<WebApplication> StartWindowsAgentAsync()
    {
        AgentOptions options = WindowsDevOptions();
        Directory.CreateDirectory(options.ModelsRoot);
        foreach (string file in new[] { LlamaCppFixtures.Embedder().LlamaCpp!.ModelFile!, LlamaCppFixtures.Chat().LlamaCpp!.ModelFile! })
        {
            string path = Path.Combine(options.ModelsRoot, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                await File.WriteAllBytesAsync(path, [0x47, 0x47, 0x55, 0x46, 3, 0, 0, 0], Ct);
            }
        }

        // A vLLM model folder for the fake runtime's own driver (the second driver of this node).
        Directory.CreateDirectory(Path.Combine(options.ModelsRoot, "fake-model"));
        string config = Path.Combine(options.ModelsRoot, "fake-model", "config.json");
        if (!File.Exists(config))
        {
            await File.WriteAllTextAsync(config, """{"architectures":["LlamaForCausalLM"],"model_type":"llama"}""", Ct);
        }

        WinHost = new FakeProcessHost(WinProcesses);
        return await StartAgentAsync(options, WinId, DevFakeSettings("discrete:2x16", WinGpus), services =>
        {
            // The three seams a Docker-off node needs and that the composition can supply itself only on Windows
            // (design-p3p4 §A.4.3). Registered before AddMirbadLLMAgent, so its TryAdds keep them on Windows too: no
            // real process is started, no DPAPI blob is written and the real Service Control Manager is never opened.
            services.AddSingleton<IProcessHost>(WinHost);
            services.AddSingleton<IInstanceKeyProtector>(WinKeys);
            services.AddSingleton<IServiceControlManager>(WinScm);

            // No upstream cache on a dev node. RuntimeDriverUpstreamResolver keeps a resolved upstream for
            // AgentProxyOptions.UpstreamCacheDuration (1 s) and nothing invalidates it when the driver replaces an
            // instance on a new port, so a model that restarts inside that second — the fake llama-server does, and a
            // tuning candidate's relaunch is exactly that — can be sent to the previous instance's port and answer a
            // bogus 503 model_not_running ("…at http://127.0.0.1:{old}/ did not answer … actively refused"). It was
            // observed here while building MN-236 and is reported for MN-239; a real llama-server takes far longer than
            // a second to restart, which is why only a dev box trips it. Zero keeps the dev setup deterministic.
            services.Configure<AgentProxyOptions>(proxy => proxy.UpstreamCacheDuration = TimeSpan.Zero);
        });
    }

    /// <summary>The generated <c>agent.json</c> of the Windows dev agent (<c>agent.windows.example.json</c>, dev paths).</summary>
    public AgentOptions WindowsDevOptions()
    {
        AgentOptions options = BaseOptions(WinId, _winPort, _winModelPorts);

        // The one P3/P4 switch: Docker off is what makes this a process node (design-p3p4 §A.4.3). It must stay false on
        // a Windows dev agent — with it true the composition registers the container driver and no process delivery at
        // all. (The host memory probe no longer depends on it: MN-239 item 15 moved WindowsMemoryProbe behind
        // OperatingSystem.IsWindows() alone, so a Windows node reads Windows memory whatever the switch says.)
        options.Docker.Enabled = false;
        options.UpstreamHost = "127.0.0.1";

        // Mandatory on a process node: a native launch's argv may name it as {cache}, and without it the agent refuses
        // to start with an AgentStartupException (MN-234). install-agent.ps1 always writes it; a hand-written dev
        // config is the one place it can be forgotten, so the generator sets it here.
        options.CacheRoot = WinCacheRoot;
        options.CacheHostPath = WinCacheRoot;
        Directory.CreateDirectory(WinCacheRoot);

        // One entry per executable name, every llama-server version inside it: two entries for one name are refused in
        // validation, because each would derive its own default version (MN-234).
        options.Executables.Add(LlamaCppExecutable());
        options.Process.HealthPollInterval = TimeSpan.FromMilliseconds(100);
        options.Process.StartTimeout = TimeSpan.FromSeconds(30);
        return options;
    }

    /// <summary>The settings <c>tools/dev-multinode</c> writes for every dev agent, pointed at this fleet's folders.</summary>
    private AgentOptions BaseOptions(string nodeId, int port, int modelPorts)
    {
        string node = Path.Combine(_root, nodeId);
        string models = Path.Combine(node, "models");
        return new AgentOptions
        {
            NodeId = nodeId,
            StateDirectory = Path.Combine(node, "state"),
            ListenAddress = "127.0.0.1",
            Port = port,
            ModelsRoot = models,
            ModelsHostPath = models,
            CacheRoot = Path.Combine(node, "cache"),
            CacheHostPath = Path.Combine(node, "cache"),
            UseContainerNetwork = false,
            DockerStartupTimeout = TimeSpan.FromSeconds(5),
            HostdSocket = Path.Combine(node, "no-hostd.sock"),
            HostdExpected = false,
            ManageSystemdUnits = false,
            ManageContainers = false,
            ManageBackupMount = false,
            AllowTagPulls = true,
            PortRangeStart = modelPorts,
            PortRangeEnd = modelPorts + ModelPortsPerAgent - 1,
            ShutdownTimeout = TimeSpan.FromSeconds(10),
        };
    }

    private static Dictionary<string, string?> DevFakeSettings(string memory, string[]? gpuUuids)
    {
        var settings = new Dictionary<string, string?>
        {
            ["DevFakeRuntime:Enabled"] = "true",
            ["DevFakeRuntime:Memory"] = memory,
            ["DevFakeRuntime:HostMemoryGiB"] = "32",
            ["DevFakeRuntime:StartDelay"] = "00:00:00.300",
            ["DevFakeRuntime:TokenDelay"] = "00:00:00.002",
        };
        for (int i = 0; i < (gpuUuids?.Length ?? 0); i++)
        {
            settings[$"DevFakeRuntime:GpuUuids:{i}"] = gpuUuids![i];
        }

        return settings;
    }

    private static async Task<WebApplication> StartAgentAsync(
        AgentOptions options, string nodeId, Dictionary<string, string?> fake, Action<IServiceCollection>? configureServices)
    {
        Directory.CreateDirectory(options.StateDirectory);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetDirectoryName(options.StateDirectory.TrimEnd(Path.DirectorySeparatorChar)),
            EnvironmentName = Environments.Development,
        });
        builder.Logging.ClearProviders();
        if (Environment.GetEnvironmentVariable("SS_DEV_AGENT_LOG") == "1")
        {
            builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
            builder.Logging.SetMinimumLevel(LogLevel.Debug);
            builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        }

        builder.Configuration.AddInMemoryCollection(fake);
        builder.Services.AddSingleton<IDiskSpaceProbe>(new ContractNode.FixedDiskProbe());
        WebApplication app = await AgentProgram.CreateAsync(builder, options, startupLoggers: null, configureServices, Ct);
        try
        {
            await app.StartAsync(Ct);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    /// <summary>Both nodes Online, and the master has read each one's capability report into its registry record.</summary>
    private async Task WaitOnlineAsync()
    {
        var cache = Services.GetRequiredService<INodeStatusCache>();
        var registry = Services.GetRequiredService<INodeRegistry>();
        await WaitUntilAsync(() => Task.FromResult(cache.GetNode(DgxId)?.State == NodeState.Online), $"{DgxId} never came Online");
        await WaitUntilAsync(() => Task.FromResult(cache.GetNode(WinId)?.State == NodeState.Online), $"{WinId} never came Online");

        // The capability report carries the verified llama.cpp versions, and a launch needs the node's verified default
        // (LaunchSpecBuilder.ResolveExecutableVersion): wait for the catalogue's first hashing to have landed.
        await WaitUntilAsync(
            async () => (await registry.GetAsync(WinId, Ct))?.LastCapabilities is { } caps
                && caps.Runtimes.Any(r => r.Kind == RuntimeKind.LlamaCpp && r.Delivery == LaunchDelivery.Process
                    && (r.Versions ?? []).Any(v => v.Verified && v.Version == DefaultLlamaCppVersion && v.Default)),
            $"{WinId} never reported a verified default llama-server version");
        await WaitUntilAsync(
            async () => (await registry.GetAsync(DgxId, Ct))?.LastCapabilities is not null,
            $"{DgxId}'s capability report never reached the registry");
    }

    private static async Task StopAsync(WebApplication app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await app.StopAsync(timeout.Token);
    }

    private static async Task TryStopAsync(WebApplication app)
    {
        try
        {
            await StopAsync(app);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Already stopped, or stopping took longer than the teardown budget: the dispose below still releases it.
        }
    }

    private static void TryDelete(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// The benchmark and tuning sizes the dev setup runs with. Neither option class is bound to configuration
    /// (<c>AddJobs</c> registers both as plain singletons), so the dev setup overrides the registrations. The sizes are a
    /// development setup's, not the owner's: one repetition, 8 generated tokens, one concurrency level, no 64 Ki prefill.
    /// </summary>
    private static BenchmarkSuiteOptions DevBenchmarkSuite() => new()
    {
        Levels = [1, 2],
        Runs = 1,
        MaxTokens = 8,
        IncludePrefill = false,
        RequestTimeout = TimeSpan.FromSeconds(30),
        EmbeddingLevels = [1, 2],
        EmbeddingInputWords = [4, 16],
        ChatOutputTokens = [8],
        WorkloadLevels = [1],
        MaxConcurrency = 1,
        TelemetrySettleTimeout = TimeSpan.FromMilliseconds(200),
        TelemetrySettlePoll = TimeSpan.FromMilliseconds(25),
    };

    private static ModelJobOptions DevJobOptions()
    {
        var options = new ModelJobOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            DrainTimeout = TimeSpan.FromSeconds(5),
            GpuIdleSampleDuration = TimeSpan.FromSeconds(1), // the agent route's floor; one sample per job
            GpuIdleSampleInterval = TimeSpan.FromMilliseconds(100),
            CandidateStartTimeout = TimeSpan.FromSeconds(60),
            CleanupTimeout = TimeSpan.FromSeconds(30),
            SearchRepetitions = 1,
            ConfirmationRepetitions = 1,
            ConfirmationRandom = new Random(20_236),
        };

        // A dev box's absolute latencies say nothing (the fake llama-server's token delay is a test knob), so the
        // time-to-first-token guards and the objective-answer guard are off; the error-rate guard stays.
        options.Guards[TuningGoal.InteractiveChat] = new SloGuards(
            ShortTtftP95AtN1Seconds: null, ShortTtftP95AtN16Seconds: null, LongTtftMaxRatio: null, ObjectiveAnswers: false);
        options.Guards[TuningGoal.Embeddings] = new SloGuards(
            ShortTtftP95AtN1Seconds: null, ShortTtftP95AtN16Seconds: null, LongTtftMaxRatio: null, ObjectiveAnswers: false);
        return options;
    }

    /// <summary>The master's <c>Program</c> on Kestrel in Development, as <c>tools/dev-multinode/start.ps1</c> runs it.</summary>
    private sealed class DevMaster(Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        public Uri HttpAddress()
        {
            var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            string address = addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal));
            return new Uri(address.Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal).Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("urls", "http://127.0.0.1:0");
            builder.UseSetting("MirbadLLM:DataPath", settings["MirbadLLM:DataPath"]);
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var all = new Dictionary<string, string?>(settings)
                {
                    ["urls"] = "http://127.0.0.1:0",
                    ["MirbadLLM:AdminPassword"] = null,
                    ["Logging:LogLevel:Default"] = "Warning", // appsettings.Development.json says Information
                };
                config.AddInMemoryCollection(all);
            });

            // BenchmarkSuiteOptions and ModelJobOptions are plain singletons of AddJobs, not configuration-bound: the
            // dev setup replaces them so a benchmark and a tune finish in seconds instead of minutes.
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(DevBenchmarkSuite());
                services.AddSingleton(DevJobOptions());
            });
        }
    }
}
