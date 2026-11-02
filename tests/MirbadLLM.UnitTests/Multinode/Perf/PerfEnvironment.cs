using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Proxy;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.Orchestration;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;
using AgentProgram = MirbadLLM.Agent.Program;

namespace MirbadLLM.UnitTests.Multinode.Perf;

/// <summary>
/// The system under measurement (MN-046), composed as <see cref="MasterAgentCompositionTests"/> does it: the real master
/// (<c>MirbadLLM.Web</c>'s <c>Program</c> on Kestrel, with its enroll listener) bootstraps the real agent as <c>dgx</c>,
/// the agent enrolls, the node comes Online and two models start through the agent on the fake Docker engine:
/// <list type="bullet">
/// <item><c>qwen</c>, served by the node's <see cref="FakeModelServer"/> (the model of the latency scenarios);</item>
/// <item><c>perf-upstream</c>, served by <see cref="PerfUpstreamServer"/> (64 MiB sink and bulk stream).</item>
/// </list>
/// Everything runs in one process on loopback. The one difference from <see cref="RealAgentHarness"/>: the agent's
/// <see cref="IProxyUpstreamResolver"/> maps each model id to its server (that harness maps every model to the
/// FakeModelServer) and, like the production resolver, keeps a positive answer for
/// <see cref="AgentProxyOptions.UpstreamCacheDuration"/> instead of asking the fake Docker engine on every request.
/// </summary>
internal sealed class PerfEnvironment : IAsyncDisposable
{
    public const string FakeModelId = "qwen";
    public const string UpstreamModelId = "perf-upstream";

    private readonly ContractNode _node;
    private readonly PerfMasterFactory _master;
    private readonly WebApplication _agent;

    private PerfEnvironment(ContractNode node, PerfMasterFactory master, WebApplication agent, PerfUpstreamServer upstream, Uri masterUri, TimeSpan setup)
    {
        _node = node;
        _master = master;
        _agent = agent;
        Upstream = upstream;
        MasterUri = masterUri;
        SetupTime = setup;
        Client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = int.MaxValue,
            MaxResponseDrainSize = 0, // an abandoned response closes the connection (no 2 s drain, MN-010 note)
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>The master's HTTP address (client → master <c>/v1</c> → agent over mTLS h2 → model).</summary>
    public Uri MasterUri { get; }

    /// <summary>The FakeModelServer's address (client → model directly, the baseline).</summary>
    public Uri FakeModelUri => _node.Model.BaseUri;

    public FakeModelServer FakeModel => _node.Model;

    public PerfUpstreamServer Upstream { get; }

    /// <summary>The one client every scenario uses for both paths (HTTP/1.1 to the master and to the models).</summary>
    public HttpClient Client { get; }

    /// <summary>The master's global upload budget (design §7.2), read to sample the bytes it holds.</summary>
    public UploadBudget UploadBudget => _master.Services.GetRequiredService<UploadBudget>();

    /// <summary>
    /// The largest <c>/v1</c> request body the master takes: the router raises the server's limit for its proxied routes to
    /// <see cref="MirbadLLM.Agent.Contracts.Protocol.MaxProxyRequestBodyBytes"/> (MN-047; before, Kestrel's global default
    /// of 30,000,000 bytes applied, MN-046). Other endpoints keep <see cref="MasterKestrelMaxRequestBodySize"/>.
    /// </summary>
    public static long MasterMaxRequestBodySize => MirbadLLM.Agent.Contracts.Protocol.MaxProxyRequestBodyBytes;

    /// <summary>The master's global Kestrel <c>Limits.MaxRequestBodySize</c> as configured (null = unlimited).</summary>
    public long? MasterKestrelMaxRequestBodySize =>
        _master.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>>().Value.Limits.MaxRequestBodySize;

    /// <summary>Cumulative time at the end of each setup phase.</summary>
    public IReadOnlyList<string> SetupPhases { get; private init; } = [];

    /// <summary>How long composing and starting everything took.</summary>
    public TimeSpan SetupTime { get; }

    public static async Task<PerfEnvironment> StartAsync(CancellationToken ct)
    {
        long started = Stopwatch.GetTimestamp();
        var phases = new List<string>();
        void Mark(string phase) => phases.Add($"{phase} {Stopwatch.GetElapsedTime(started).TotalSeconds:0.000}s");
        int enrollPort = TestPorts.Reserve();
        int agentPort = TestPorts.Reserve();
        var node = await ContractNode.CreateAsync(enroll: false, ct, nodeId: "dgx");
        PerfMasterFactory? master = null;
        WebApplication? agent = null;
        PerfUpstreamServer? upstream = null;
        try
        {
            upstream = await PerfUpstreamServer.CreateAsync(ct);
            string dataDir = Path.Combine(node.Root, "master-data");
            string bootstrapDir = Path.Combine(node.Root, "bootstrap");
            await SeedModelsAsync(dataDir, ct);

            master = new PerfMasterFactory(dataDir, new Dictionary<string, string?>
            {
                ["MirbadLLM:Enrollment:Port"] = enrollPort.ToString(CultureInfo.InvariantCulture),
                ["MirbadLLM:Enrollment:BootstrapDirectory"] = bootstrapDir,
                ["MirbadLLM:Enrollment:BootstrapEnrollEndpoint"] = $"https://127.0.0.1:{enrollPort}",
                ["MirbadLLM:Enrollment:BootstrapAgentAddress"] = $"https://127.0.0.1:{agentPort}",
                ["MirbadLLM:HealthPollInterval"] = "00:00:00.200",
            });
            master.UseKestrel();
            master.StartServer();
            Uri masterUri = master.HttpAddress();
            Mark("master");

            var upstreams = new Dictionary<string, Uri>(StringComparer.Ordinal)
            {
                [FakeModelId] = node.Model.BaseUri,
                [UpstreamModelId] = upstream.BaseUri,
            };
            agent = await BuildAgentAsync(node, upstreams, o =>
            {
                o.Port = agentPort;
                o.BootstrapDirectory = bootstrapDir;
                o.BootstrapWait = TimeSpan.FromSeconds(60);
                o.Addresses.Add("127.0.0.1");
                o.AllowTagPulls = true;
            }, ct);
            await agent.StartAsync(ct);
            Mark("agent");

            // Setup only (never measured): re-read nodes.json now instead of waiting for the 5 s registry poll.
            var cache = master.Services.GetRequiredService<INodeStatusCache>();
            var sync = master.Services.GetRequiredService<MirbadLLM.Orchestration.Nodes.NodeRegistrySync>();
            await WaitUntilAsync(async () =>
            {
                await sync.SyncAsync(ct);
                return cache.GetNode("dgx")?.State == NodeState.Online;
            }, "dgx never came Online", ct);
            Mark("online");

            using var admin = new HttpClient { BaseAddress = masterUri };
            foreach (string id in new[] { FakeModelId, UpstreamModelId })
            {
                using var start = await admin.PostAsync($"/api/models/{id}/start", content: null, ct);
                if (start.StatusCode != HttpStatusCode.OK)
                {
                    throw new InvalidOperationException($"Starting {id} answered {(int)start.StatusCode}: {await start.Content.ReadAsStringAsync(ct)}");
                }
            }

            Mark("started");
            await WaitUntilAsync(async () =>
            {
                using var models = await admin.GetAsync("/v1/models", ct);
                string body = await models.Content.ReadAsStringAsync(ct);
                return models.StatusCode == HttpStatusCode.OK
                    && body.Contains($"\"{FakeModelId}\"", StringComparison.Ordinal)
                    && body.Contains($"\"{UpstreamModelId}\"", StringComparison.Ordinal);
            }, "the models never appeared in /v1/models", ct);

            Mark("ready");
            return new PerfEnvironment(node, master, agent, upstream, masterUri, Stopwatch.GetElapsedTime(started)) { SetupPhases = phases };
        }
        catch
        {
            if (agent is not null)
            {
                await agent.DisposeAsync();
            }

            if (master is not null)
            {
                await master.DisposeAsync();
            }

            if (upstream is not null)
            {
                await upstream.DisposeAsync();
            }

            await node.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            await _agent.StopAsync();
        }
        finally
        {
            await _agent.DisposeAsync();
            await _master.DisposeAsync();
            await Upstream.DisposeAsync();
            await _node.DisposeAsync();
        }
    }

    /// <summary><see cref="ContractNode.BuildAgentAsync"/> with a per-model upstream resolver (see the class remarks).</summary>
    private static Task<WebApplication> BuildAgentAsync(ContractNode node, IReadOnlyDictionary<string, Uri> upstreams, Action<MirbadLLM.Agent.AgentOptions> configure, CancellationToken ct)
    {
        var options = node.CreateOptions();
        configure(options);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = node.Root });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        return AgentProgram.CreateAsync(builder, options, startupLoggers: null, configureServices: services =>
        {
            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new TransportFilter(node));
            services.AddSingleton<IMemoryProbe>(new FixedMemoryProbe());
            services.AddSingleton<IDiskSpaceProbe>(new ContractNode.FixedDiskProbe());
            services.AddSingleton<INvidiaSmiRunner>(new NoNvidiaSmi());
            services.AddSingleton<IHostdClient>(node.Hostd);
            services.AddSingleton<IHuggingFaceDownloader>(node.Downloads);
            services.AddSingleton<IHuggingFaceRepoInspector>(node.Downloads);
            services.AddSingleton<IProxyUpstreamResolver>(sp => new PerModelResolver(
                sp.GetServices<IRuntimeDriver>(), upstreams, sp.GetRequiredService<IOptions<AgentProxyOptions>>().Value.UpstreamCacheDuration));
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

    private static async Task SeedModelsAsync(string dataDir, CancellationToken ct)
    {
        Directory.CreateDirectory(dataDir);
        using var registry = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }));
        foreach (var existing in await registry.ListAsync(ct))
        {
            await registry.DeleteAsync(existing.Id, ct);
        }

        foreach (string id in new[] { FakeModelId, UpstreamModelId })
        {
            await registry.UpsertAsync(new ModelDefinition
            {
                Id = id,
                ServedName = id,
                Image = AgentContractSeed.Image,
                ModelPath = AgentContractSeed.ModelFolder,
                MemoryBudgetGiB = 1,
                MaxModelLen = 8192,
                MaxNumSeqs = 16,
                Placement = new ModelPlacement("dgx"),
            }, ct);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message, CancellationToken ct)
    {
        var deadline = Stopwatch.GetTimestamp() + (60 * Stopwatch.Frequency);
        while (!await condition())
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                throw new TimeoutException(message);
            }

            await Task.Delay(50, ct);
        }
    }

    /// <summary>Copy of <see cref="MasterAgentCompositionTests"/>' private factory: <c>Program</c> on real Kestrel.</summary>
    private sealed class PerfMasterFactory(string dataDir, Dictionary<string, string?> settings) : WebApplicationFactory<Program>
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
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Error));
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
        }
    }

    /// <summary>
    /// Maps a running model to its server. "Running" comes from the agent's runtime driver (the fake Docker engine), and a
    /// positive answer is kept for the production resolver's cache duration.
    /// </summary>
    private sealed class PerModelResolver(IEnumerable<IRuntimeDriver> drivers, IReadOnlyDictionary<string, Uri> upstreams, TimeSpan cacheFor) : IProxyUpstreamResolver
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _confirmedAt = new(StringComparer.Ordinal);

        public async ValueTask<Uri?> ResolveAsync(string modelId, CancellationToken ct)
        {
            if (!upstreams.TryGetValue(modelId, out var upstream))
            {
                return null;
            }

            if (_confirmedAt.TryGetValue(modelId, out long at) && Stopwatch.GetElapsedTime(at) < cacheFor)
            {
                return upstream;
            }

            foreach (var driver in drivers)
            {
                if (await driver.GetUpstreamAsync(modelId, ct) is not null)
                {
                    _confirmedAt[modelId] = Stopwatch.GetTimestamp();
                    return upstream;
                }
            }

            return null;
        }

        public void Invalidate(string modelId, Uri upstream) => _confirmedAt.TryRemove(modelId, out _);
    }

    private sealed class TransportFilter(ContractNode node) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = new ContractTransport(node);
        };
    }

    private sealed class FixedMemoryProbe : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new MemorySnapshot(128, 100));
    }

    private sealed class NoNvidiaSmi : INvidiaSmiRunner
    {
        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default) => Task.FromResult(NvidiaSmiResult.NotFound);
    }
}
