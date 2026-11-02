using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Proxy;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Contract;
using AgentProgram = MirbadLLM.Agent.Program;

namespace MirbadLLM.UnitTests.Multinode.Chaos;

/// <summary>
/// One DGX-like deployment in process, for chaos runs: the <b>real</b> master composition (<c>MirbadLLM.Web</c>'s
/// <c>Program</c> on Kestrel over <see cref="DataDirectory"/>) and the <b>real</b> agent composition
/// (<c>MirbadLLM.Agent.Program.CreateAsync</c> over a <see cref="ContractNode"/>: state directory, fake Docker engine,
/// hostd, Hugging Face, <see cref="Fakes.FakeModelServer"/>). The master reaches the agent only through
/// <see cref="Proxy"/> (the network) and its client factory is decorated by <see cref="ChaosAgentClientFactory"/>
/// (lost responses). The agent enrolls as <c>dgx</c> from the master's bootstrap bundle, exactly as on the DGX.
/// <para>Both sides can be stopped and started again over the same directories: the agent abruptly ("crash": its host is
/// stopped with an already-cancelled token, every connection reset) or gracefully; the master over the same data folder
/// (a new process would get a new port; the node record keeps pointing at the relay).</para>
/// </summary>
internal sealed class ChaosCluster : IAsyncDisposable
{
    public const string NodeId = "dgx";

    private readonly ConcurrentQueue<string> _trace = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private MasterFactory? _master;
    private WebApplication? _agent;
    private int _serial;

    private ChaosCluster(ContractNode node, int seed)
    {
        Node = node;
        Seed = seed;
        DataDirectory = Path.Combine(node.Root, "master-data");
        BootstrapDirectory = Path.Combine(node.Root, "bootstrap");
        Proxy = new FaultProxy(0);
        Hostd = new ChaosHostd(node.Hostd);
        Downloads = new ChaosDownloads(node.Downloads);
        string tag = seed.ToString("x8", CultureInfo.InvariantCulture);
        HfToken = "hf_chaosTOKEN" + tag + "Zq9";
        SharePassword = "chaos-share-PW-" + tag + "-x7";
    }

    public int Seed { get; }

    /// <summary>The master's enroll listener port (reserved, kept across master restarts).</summary>
    public int EnrollPort { get; } = TestPorts.Reserve();

    public ContractNode Node { get; }

    public FaultProxy Proxy { get; }

    public ChaosFaults Faults { get; } = new();

    public ChaosHostd Hostd { get; }

    public ChaosDownloads Downloads { get; }

    public ChaosLog MasterLog { get; } = new();

    public ChaosLog AgentLog { get; } = new();

    /// <summary>Operation ids the agent answered with a success status (across agent restarts).</summary>
    public AgentRequestRecorder AgentRequests { get; } = new();

    public string DataDirectory { get; }

    public string BootstrapDirectory { get; }

    /// <summary>The Hugging Face token every chaos download carries (must never be written anywhere).</summary>
    public string HfToken { get; }

    /// <summary>The backup share password every chaos mount carries (must never be written anywhere).</summary>
    public string SharePassword { get; }

    /// <summary>Times the settle loop re-snapshotted the node for the known stale-health finding.</summary>
    public int StaleHealthRefreshes { get; set; }

    /// <summary>Exceptions a stop or dispose of either side threw (each is an invariant violation).</summary>
    public ConcurrentQueue<string> ShutdownFailures { get; } = new();

    public bool MasterRunning => _master is not null;

    public bool AgentRunning => _agent is not null;

    /// <summary>The running master's services (throws while it is down).</summary>
    public IServiceProvider MasterServices => (_master ?? throw new InvalidOperationException("The master is down.")).Services;

    /// <summary>The running master's admin/<c>/v1</c> client (throws while it is down).</summary>
    public HttpClient Http { get; private set; } = null!;

    /// <summary>Models seeded into the master's registry (all placed on <c>dgx</c>).</summary>
    public static IReadOnlyList<ModelDefinition> Models { get; } =
    [
        Model("qwen", autoStart: false),
        Model("phi", autoStart: true),
    ];

    public IReadOnlyList<string> Trace => [.. _trace];

    /// <summary>A cluster-wide serial (unique download folders across plans).</summary>
    public int NextSerial() => Interlocked.Increment(ref _serial);

    public void Note(string line) => _trace.Enqueue(string.Create(CultureInfo.InvariantCulture, $"{_clock.Elapsed.TotalSeconds,7:0.000}s {line}"));

    /// <summary>Master up, agent enrolled from the bootstrap bundle, node Online.</summary>
    public static async Task<ChaosCluster> StartAsync(int seed, CancellationToken ct, Action<AgentOptions>? firstAgent = null)
    {
        var node = await ContractNode.CreateAsync(enroll: false, ct, nodeId: NodeId);
        var cluster = new ChaosCluster(node, seed);
        try
        {
            await SeedModelsAsync(cluster.DataDirectory, ct);
            await cluster.StartMasterAsync(ct);
            await cluster.StartAgentAsync(ct, firstAgent);
            await cluster.WaitOnlineAsync(TimeSpan.FromSeconds(30), ct);
            await cluster.WaitAutoStartedAsync(ct);
            return cluster;
        }
        catch
        {
            await cluster.DisposeAsync();
            throw;
        }
    }

    // ------------------------------------------------------------------ master

    public async Task StartMasterAsync(CancellationToken ct)
    {
        if (_master is not null)
        {
            return;
        }

        // The enroll listener keeps its reserved port across master restarts (as 8443 on the DGX); a restart retries
        // while the previous master's listener releases it.
        _master = await TestPorts.RestartOnSamePortAsync(() => Task.FromResult(StartMasterFactory()), ct);
        Note("master started");
        ct.ThrowIfCancellationRequested();
    }

    private MasterFactory StartMasterFactory()
    {
        var factory = new MasterFactory(DataDirectory, new Dictionary<string, string?>
        {
            ["MirbadLLM:Enrollment:Port"] = EnrollPort.ToString(CultureInfo.InvariantCulture),
            ["MirbadLLM:Enrollment:BootstrapDirectory"] = BootstrapDirectory,
            ["MirbadLLM:Enrollment:BootstrapEnrollEndpoint"] = $"https://127.0.0.1:{EnrollPort}",
            ["MirbadLLM:Enrollment:BootstrapAgentAddress"] = $"https://127.0.0.1:{Proxy.Port}",
        }, MasterLog, Faults);
        try
        {
            factory.UseKestrel();
            factory.StartServer();
            Http = new HttpClient { BaseAddress = factory.HttpAddress(), Timeout = TimeSpan.FromSeconds(30) };
            return factory;
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    public async Task StopMasterAsync()
    {
        if (_master is not { } master)
        {
            return;
        }

        _master = null;
        Http.Dispose();
        try
        {
            await master.DisposeAsync();
            Note("master stopped");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Reported by the invariants (with the logs), instead of escaping without context.
            ShutdownFailures.Enqueue($"master shutdown threw {ex.GetType().Name}: {ex.Message}");
            Note($"master shutdown threw {ex}");
        }
    }

    // ------------------------------------------------------------------ agent

    /// <summary>Starts the agent over the node's state directory (bootstrap mode, as <c>agent.json</c> on the DGX).</summary>
    public async Task StartAgentAsync(CancellationToken ct, Action<AgentOptions>? configure = null)
    {
        if (_agent is not null)
        {
            return;
        }

        WebApplication app = await BuildAgentAsync(o =>
        {
            o.BootstrapDirectory = BootstrapDirectory;
            o.BootstrapWait = TimeSpan.FromSeconds(60);
            o.Addresses.Add("127.0.0.1");
            o.AllowTagPulls = true;
            o.DockerStartupTimeout = TimeSpan.FromSeconds(1);
            configure?.Invoke(o);
        }, ct);
        try
        {
            await app.StartAsync(ct);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Proxy.TargetPort = new Uri(address).Port;
        _agent = app;
        Note($"agent started (port {Proxy.TargetPort})");
    }

    /// <summary>Stops the agent: <paramref name="crash"/> = no graceful shutdown (already-cancelled stop token, connections reset).</summary>
    public async Task StopAgentAsync(bool crash)
    {
        if (_agent is not { } app)
        {
            return;
        }

        _agent = null;
        Proxy.TargetPort = 0;
        Proxy.ResetAll();
        if (crash)
        {
            using var now = new CancellationTokenSource();
            await now.CancelAsync();
            try
            {
                await app.StopAsync(now.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
        else
        {
            await app.StopAsync(CancellationToken.None);
        }

        try
        {
            await app.DisposeAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShutdownFailures.Enqueue($"agent shutdown threw {ex.GetType().Name}: {ex.Message}");
            Note($"agent shutdown threw {ex}");
        }

        Note(crash ? "agent crashed" : "agent stopped");
    }

    /// <summary>The agent composed as <c>ContractNode.BuildAgentAsync</c> does, with this cluster's hostd/download fakes and log capture.</summary>
    private Task<WebApplication> BuildAgentAsync(Action<AgentOptions> configure, CancellationToken ct)
    {
        AgentOptions options = Node.CreateOptions();
        configure(options);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = Node.Root });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(AgentLog);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("MirbadLLM", LogLevel.Trace);
        var node = Node;
        return AgentProgram.CreateAsync(builder, options, startupLoggers: null, configureServices: services =>
        {
            services.AddSingleton<IStartupFilter>(AgentRequests);
            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new TransportFilter(node));
            services.AddSingleton<IMemoryProbe>(new FixedMemoryProbe());
            services.AddSingleton<IDiskSpaceProbe>(new ContractNode.FixedDiskProbe());
            services.AddSingleton<INvidiaSmiRunner>(new NoNvidiaSmi());
            services.AddSingleton<IHostdClient>(Hostd);
            services.AddSingleton<IHuggingFaceDownloader>(Downloads);
            services.AddSingleton<IHuggingFaceRepoInspector>(Downloads);
            services.AddSingleton<IProxyUpstreamResolver>(sp => new ModelServerResolver(sp.GetServices<IRuntimeDriver>(), node.Model));
            services.AddSingleton(new TelemetryHttpClient(new ContractTransport(node)));
            services.AddSingleton(new AgentRuntimeOptions { WatchInterval = TimeSpan.FromMilliseconds(100) });
            services.Configure<TelemetryOptions>(o => o.SampleInterval = TimeSpan.FromMilliseconds(250));
            services.Configure<AgentStorageOptions>(o =>
            {
                o.DownloadPollInterval = TimeSpan.FromMilliseconds(50);
                o.ProgressRecordInterval = TimeSpan.FromMilliseconds(200);
            });
        }, ct);
    }

    // ------------------------------------------------------------------ outside world

    /// <summary>Docker (engine and download helper) down or back.</summary>
    public void SetDockerDown(bool down)
    {
        Node.Docker.Down = down;
        Node.Downloads.DockerDown = down;
    }

    /// <summary>The real agent's view of itself, through the master's real (undecorated) client and the relay.</summary>
    public IAgentClient AgentClient() => MasterServices.GetRequiredService<AgentClientFactory>().GetClient(NodeId);

    public NodeLiveStatus? CachedNode() => MasterServices.GetRequiredService<INodeStatusCache>().GetNode(NodeId);

    public async Task WaitOnlineAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (CachedNode()?.State != NodeState.Online)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"dgx did not come Online within {timeout.TotalSeconds:0} s (state {CachedNode()?.State}, reasons {string.Join("; ", CachedNode()?.Reasons ?? [])}).");
            }

            await Task.Delay(50, ct);
        }
    }

    /// <summary>Boot auto-start has run: every auto-start model is Ready on the agent (a later start would conflict with it).</summary>
    public async Task WaitAutoStartedAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        foreach (var model in Models.Where(m => m.AutoStart))
        {
            while ((await AgentClient().GetRuntimeAsync(model.Id, ct))?.State != ModelState.Ready)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException($"Boot auto-start did not bring '{model.Id}' Ready within 30 s.");
                }

                await Task.Delay(50, ct);
            }
        }
    }

    /// <summary>Model containers in the fake Docker engine: (container name, model label, state).</summary>
    public async Task<List<(string Name, string Model, string State)>> ModelContainersAsync(CancellationToken ct)
    {
        string filter = Uri.EscapeDataString("{\"label\":[\"" + ContainerLabels.Model + "\"]}");
        if (Node.Docker.Down)
        {
            throw new InvalidOperationException("Read the engine only while Docker is up.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://docker/v1.47/containers/json?all=1&filters={filter}");
        using var response = await Node.Docker.HandleAsync(request, ct);
        var rows = await response.Content.ReadFromJsonAsync<List<DockerRow>>(ct) ?? [];
        return [.. rows.Select(r => (r.Names.FirstOrDefault()?.TrimStart('/') ?? r.Id, r.Labels.GetValueOrDefault(ContainerLabels.Model) ?? "", r.State))];
    }

    public async ValueTask DisposeAsync()
    {
        Faults.ClearDrops();
        Proxy.Heal(reset: false);
        SetDockerDown(false);
        Hostd.Mode = HostdMode.Up;
        try
        {
            await StopAgentAsync(crash: false);
        }
        finally
        {
            try
            {
                await StopMasterAsync();
            }
            finally
            {
                await Proxy.DisposeAsync();
                await Node.DisposeAsync();
            }
        }
    }

    private static ModelDefinition Model(string id, bool autoStart) => new()
    {
        Id = id,
        ServedName = id,
        Image = AgentContractSeed.Image,
        ModelPath = AgentContractSeed.ModelFolder,
        MemoryBudgetGiB = 1,
        MaxModelLen = 8192,
        MaxNumSeqs = 16,
        AutoStart = autoStart,
        Placement = new ModelPlacement(NodeId),
    };

    private static async Task SeedModelsAsync(string dataDir, CancellationToken ct)
    {
        Directory.CreateDirectory(dataDir);
        using var registry = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }));
        foreach (var existing in await registry.ListAsync(ct))
        {
            await registry.DeleteAsync(existing.Id, ct);
        }

        foreach (var model in Models)
        {
            await registry.UpsertAsync(model, ct);
        }
    }

    private sealed record DockerRow(string Id, List<string> Names, string State, Dictionary<string, string> Labels);

    /// <summary><c>Program</c> on real Kestrel over the data folder, with log capture and the fault-injecting client factory.</summary>
    private sealed class MasterFactory(string dataDir, Dictionary<string, string?> settings, ChaosLog log, ChaosFaults faults)
        : WebApplicationFactory<Program>
    {
        public Uri HttpAddress()
        {
            var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            string address = addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal));
            return new Uri(address.Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal).Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("urls", "http://127.0.0.1:0");
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(log);
                logging.SetMinimumLevel(LogLevel.Information);
                logging.AddFilter("MirbadLLM", LogLevel.Trace);
            });
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var all = new Dictionary<string, string?>
                {
                    ["urls"] = "http://127.0.0.1:0",
                    ["MirbadLLM:DataPath"] = dataDir,
                    ["MirbadLLM:AdminPassword"] = null,
                    ["MirbadLLM:DockerEndpoint"] = "unix:///nonexistent/mirbadllm-tests/docker.sock",
                    ["MirbadLLM:HostAgentSocket"] = Path.Combine(dataDir, "no-hostd.sock"),
                    ["MirbadLLM:Backup:Directory"] = Path.Combine(dataDir, "backups"),
                };
                foreach ((string key, string? value) in settings)
                {
                    all[key] = value;
                }

                config.AddInMemoryCollection(all);
            });
            builder.ConfigureTestServices(services =>
                services.AddSingleton<IAgentClientFactory>(sp => new ChaosAgentClientFactory(sp.GetRequiredService<AgentClientFactory>(), faults)));
        }
    }

    private sealed class FixedMemoryProbe : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new MemorySnapshot(128, 100));
    }

    private sealed class NoNvidiaSmi : INvidiaSmiRunner
    {
        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default) => Task.FromResult(NvidiaSmiResult.NotFound);
    }

    private sealed class TransportFilter(ContractNode node) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = new ContractTransport(node);
        };
    }

    private sealed class ModelServerResolver(IEnumerable<IRuntimeDriver> drivers, Fakes.FakeModelServer model) : IProxyUpstreamResolver
    {
        public async ValueTask<Uri?> ResolveAsync(string modelId, CancellationToken ct)
        {
            foreach (var driver in drivers)
            {
                if (await driver.GetUpstreamAsync(modelId, ct) is not null)
                {
                    return model.BaseUri;
                }
            }

            return null;
        }

        public void Invalidate(string modelId, Uri upstream)
        {
        }
    }
}

/// <summary>How hostd behaves.</summary>
internal enum HostdMode
{
    Up,

    /// <summary>The socket is gone: every call fails at once (nothing was sent).</summary>
    Down,

    /// <summary>Calls are sent and never answered (the HTTP timeout fires): the action's outcome is unknown.</summary>
    Hanging,
}

/// <summary>The contract hostd with outage windows; records every backup-mount password it is handed.</summary>
internal sealed class ChaosHostd(ContractHostd inner) : IHostdClient
{
    private int _passwords;

    public volatile HostdMode Mode;

    /// <summary>Backup-mount calls that carried a password (proves the secret travelled through master and agent).</summary>
    public int PasswordsReceived => Volatile.Read(ref _passwords);

    public bool IsInstalled => inner.IsInstalled;

    public Task<IReadOnlyList<HostUnit>> ListAsync(CancellationToken ct = default) => Call(() => inner.ListAsync(ct));

    public Task<HostUnitDiscovery> DiscoverAsync(CancellationToken ct = default) => Call(() => inner.DiscoverAsync(ct));

    public Task<HostUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default) =>
        Call(() => inner.AddToWhitelistAsync(unit, scope, displayName, isProtected, confirm, ct));

    public Task<HostUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default) =>
        Call(() => inner.UpdateWhitelistAsync(name, displayName, isProtected, confirm, ct));

    public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default) =>
        Call(async () =>
        {
            await inner.RemoveFromWhitelistAsync(name, ct);
            return true;
        });

    public Task<HostUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default) =>
        Call(() => inner.RunAsync(name, action, confirm, ct));

    public Task<HostUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default) =>
        Call(() => inner.SetAutostartAsync(name, enabled, confirm, ct));

    public Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default) => Call(() => inner.GetGpuProcessesAsync(ct));

    public Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default) => Call(() => inner.GetBackupMountAsync(ct));

    public Task<BackupMountTestResult> TestBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default)
    {
        Count(settings);
        return Call(() => inner.TestBackupMountAsync(settings, ct));
    }

    public Task<BackupMountInfo> SetBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default)
    {
        Count(settings);
        return Call(() => inner.SetBackupMountAsync(settings, ct));
    }

    public Task<BackupMountInfo> RemoveBackupMountAsync(CancellationToken ct = default) => Call(() => inner.RemoveBackupMountAsync(ct));

    private void Count(BackupMountSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.Password))
        {
            Interlocked.Increment(ref _passwords);
        }
    }

    private async Task<T> Call<T>(Func<Task<T>> call)
    {
        switch (Mode)
        {
            case HostdMode.Down:
                throw new HostAgentUnavailableException("chaos: hostd socket refused the connection.");
            case HostdMode.Hanging:
                // The request reached hostd; the answer never comes back (the action itself ran).
                _ = await call();
                await Task.Delay(50);
                throw new MirbadLLM.Agent.HostServices.HostdTimeoutException("chaos: hostd did not answer in time.");
            default:
                return await call();
        }
    }
}

/// <summary>The contract Hugging Face fakes; records every token a download or inspection is handed.</summary>
internal sealed class ChaosDownloads(ContractDownloads inner) : IHuggingFaceDownloader, IHuggingFaceRepoInspector
{
    private int _tokens;

    /// <summary>Calls that carried a token (proves the secret travelled through master and agent).</summary>
    public int TokensReceived => Volatile.Read(ref _tokens);

    public Task<RepoInspection> InspectAsync(string repoId, string? revision, string? token, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(token))
        {
            Interlocked.Increment(ref _tokens);
        }

        return inner.InspectAsync(repoId, revision, token, ct);
    }

    public Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(request.Token))
        {
            Interlocked.Increment(ref _tokens);
        }

        return inner.StartAsync(request, ct);
    }

    public Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);

    public Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default) => inner.GetAsync(folder, ct);

    public Task<bool> CancelAsync(string folder, CancellationToken ct = default) => inner.CancelAsync(folder, ct);
}
