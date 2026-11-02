using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Enrollment;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.P1Exit;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123: plumbing of the P2 development exit tests (design §13.6, §14 P2). A <see cref="P2Fleet"/> is the real master
/// composition (<c>MirbadLLM.Web</c>'s <c>Program</c> on Kestrel with its enroll listener and its own CA in
/// <c>{DataPath}/pki</c>, as on the DGX, with an admin password) and real agents (<c>MirbadLLM.Agent.Program.CreateAsync</c>
/// over <see cref="ContractNode"/>: only Docker, hostd, <c>nvidia-smi</c>, the Hub and the model server are faked): the
/// DGX enrolls from the bootstrap bundle (unified GB10, <c>Agent:MemoryModel = Unified</c> as <c>agent.dgx.json</c>), PCs
/// join with a token from <c>POST /api/nodes/join-tokens</c> and are approved with the pin through the node API.
/// </summary>
internal static class P2Env
{
    /// <summary>The admin password of every <see cref="P2Fleet"/> master.</summary>
    public const string Password = "p2-exit-admin-password";

    /// <summary>The GB10 as <c>nvidia-smi</c> reports it (placeholder UUID).</summary>
    public const string Gb10Uuid = "GPU-00000000-0000-0000-0000-0000000000b1";

    /// <summary>Two equal 16 GiB cards of a PC (placeholder UUIDs).</summary>
    public static readonly string[] PcGpus = ["GPU-00000000-0000-0000-0000-00000000c000", "GPU-00000000-0000-0000-0000-00000000c001"];

    /// <summary>A second PC's cards (placeholder UUIDs).</summary>
    public static readonly string[] Pc2Gpus = ["GPU-00000000-0000-0000-0000-00000000d000", "GPU-00000000-0000-0000-0000-00000000d001"];

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The master API's JSON (camelCase, enums as strings).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerOptions.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>A definition with the seeded checkpoint folder and image (the API shape the editor posts).</summary>
    public static object ModelBody(string id, string? nodeId, IReadOnlyList<string>? gpus = null, double budgetGiB = 1, string? extraArgs = null) => new
    {
        id,
        servedName = id,
        image = AgentContractSeed.Image,
        modelPath = AgentContractSeed.ModelFolder,
        memoryBudgetGiB = budgetGiB,
        maxModelLen = 8192,
        maxNumSeqs = 4,
        extraArgs,
        placement = nodeId is null ? null : new { nodeId, gpuUuids = gpus },
    };

    /// <summary>A stored definition (seeding <c>models.json</c> before a boot).</summary>
    public static ModelDefinition Model(string id, string nodeId, IReadOnlyList<string>? gpus = null, double budgetGiB = 1) => new()
    {
        Id = id,
        ServedName = id,
        Image = AgentContractSeed.Image,
        ModelPath = AgentContractSeed.ModelFolder,
        MemoryBudgetGiB = budgetGiB,
        MaxModelLen = 8192,
        MaxNumSeqs = 16,
        Placement = new ModelPlacement(nodeId, gpus),
    };

    public static async Task<(HttpStatusCode Status, string Body)> SendAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The <c>code</c> of a ProblemDetails body, or null.</summary>
    public static string? CodeOf(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every raw line of <c>{DataPath}/audit/*.jsonl</c> (what an operator or a backup would read).</summary>
    public static string ReadAuditText(string dataDir)
    {
        string dir = Path.Combine(dataDir, AuditLogFiles.DirectoryName);
        if (!Directory.Exists(dir))
        {
            return "";
        }

        var text = new System.Text.StringBuilder();
        foreach (string file in Directory.EnumerateFiles(dir, "*.jsonl").Order(StringComparer.Ordinal))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text.Append(reader.ReadToEnd());
        }

        return text.ToString();
    }
}

/// <summary>
/// <c>nvidia-smi</c> of one node, changeable while the agent runs or between restarts: the cards in index order (a renumber
/// reorders them, a removal drops one), or failing as a broken driver does (exit 9, no output).
/// </summary>
internal sealed class FakeNvidiaSmi : INvidiaSmiRunner
{
    private readonly Lock _gate = new();
    private List<string> _uuids;
    private readonly bool _gb10;

    private FakeNvidiaSmi(IEnumerable<string> uuids, bool gb10)
    {
        _uuids = [.. uuids];
        _gb10 = gb10;
    }

    /// <summary>While set, every run exits 9 with nothing on stdout (the driver is not loaded yet).</summary>
    public bool Failing
    {
        get => Volatile.Read(ref _failing);
        set => Volatile.Write(ref _failing, value);
    }

    private bool _failing;

    /// <summary>Runs answered so far.</summary>
    public int Runs;

    /// <summary>A GB10: one GPU without memory fields (unified).</summary>
    public static FakeNvidiaSmi Gb10() => new([P2Env.Gb10Uuid], gb10: true);

    /// <summary>Discrete 16 GiB cards, 0.5 GiB used each, in index order.</summary>
    public static FakeNvidiaSmi Discrete(IEnumerable<string> uuids) => new(uuids, gb10: false);

    /// <summary>The cards in their current index order.</summary>
    public IReadOnlyList<string> Uuids
    {
        get
        {
            lock (_gate)
            {
                return [.. _uuids];
            }
        }
    }

    /// <summary>New index order (every current UUID once).</summary>
    public void Renumber(params string[] uuidsInIndexOrder)
    {
        lock (_gate)
        {
            Assert.Equal(_uuids.Order(StringComparer.Ordinal), uuidsInIndexOrder.Order(StringComparer.Ordinal));
            _uuids = [.. uuidsInIndexOrder];
        }
    }

    /// <summary>Removes a card; later cards move down one index.</summary>
    public void Remove(string uuid)
    {
        lock (_gate)
        {
            Assert.True(_uuids.Remove(uuid), $"{uuid} is not on the node");
        }
    }

    public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Runs);
        if (Failing)
        {
            return Task.FromResult(new NvidiaSmiResult(true, 9, "", "NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver."));
        }

        List<string> uuids;
        lock (_gate)
        {
            uuids = [.. _uuids];
        }

        string output = arguments switch
        {
            NvidiaSmiGpuInventory.Arguments when _gb10 => $"0, {uuids[0]}, NVIDIA GB10, [N/A], 12.1, 580.95.05\n",
            NvidiaSmiGpuTelemetryProbe.Arguments when _gb10 => $"0, {uuids[0]}, 3, 41.25, 55, 2405, [N/A], [N/A], [N/A], NVIDIA GB10\n",
            NvidiaSmiGpuInventory.Arguments => string.Concat(uuids.Select((u, i) => $"{i}, {u}, NVIDIA Test 16G, 16384, 12.0, 617.14\n")),
            NvidiaSmiGpuTelemetryProbe.Arguments => string.Concat(uuids.Select((u, i) => $"{i}, {u}, 0, 10.00, 30, 210, 512, 16384, 15872, NVIDIA Test 16G\n")),
            _ => "",
        };
        return Task.FromResult(new NvidiaSmiResult(true, 0, output, ""));
    }
}

/// <summary>Log records with their event ids (hand-written provider), for an agent or a master.</summary>
internal sealed class EventLog : ILoggerProvider
{
    private readonly ConcurrentQueue<(int EventId, LogLevel Level, string Category, string Message)> _records = new();

    public IReadOnlyList<(int EventId, LogLevel Level, string Category, string Message)> Records => [.. _records];

    public int Count(int eventId) => _records.Count(r => r.EventId == eventId);

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(EventLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner._records.Enqueue((eventId.Id, logLevel, category, formatter(state, exception)));
    }
}

/// <summary>
/// The real master composition on Kestrel (HTTP on a loopback port, the enroll listener on <c>MirbadLLM:Enrollment:Port</c>),
/// over a data directory, with the background services on and an admin password; legacy host settings point nowhere.
/// <see cref="Admin"/> is a client with a cookie jar (log in with <see cref="LoginAsync"/>); <see cref="Anonymous"/> has none.
/// </summary>
internal sealed partial class P2Master(string dataDir, IReadOnlyDictionary<string, string?> settings, Action<IServiceCollection>? services)
    : WebApplicationFactory<global::Program>
{
    private HttpClient? _admin;
    private HttpClient? _anonymous;

    public string DataDir => dataDir;

    public EventLog Log { get; } = new();

    public CookieContainer Cookies { get; } = new();

    public Uri HttpAddress
    {
        get
        {
            var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            return new Uri(addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal)));
        }
    }

    /// <summary>The admin's browser: cookies kept, redirects not followed.</summary>
    public HttpClient Admin => _admin ??= new HttpClient(new HttpClientHandler { CookieContainer = Cookies, AllowAutoRedirect = false, UseProxy = false })
    {
        BaseAddress = HttpAddress,
        Timeout = TimeSpan.FromSeconds(60),
    };

    /// <summary>A client without a session (inference clients, a logged-out browser).</summary>
    public HttpClient Anonymous => _anonymous ??= new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        BaseAddress = HttpAddress,
        Timeout = TimeSpan.FromSeconds(60),
    };

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public INodeStatusCache Cache => Get<INodeStatusCache>();

    public INodeRegistry Nodes => Get<INodeRegistry>();

    public IModelRegistry Models => Get<IModelRegistry>();

    public IAgentClient Client(string nodeId) => Get<IAgentClientFactory>().GetClient(nodeId);

    /// <summary>Boots the host on Kestrel.</summary>
    public P2Master Boot()
    {
        UseKestrel();
        StartServer();
        return this;
    }

    /// <summary>Logs in through <c>/login</c> (antiforgery form) as a browser does.</summary>
    public async Task LoginAsync(string password = P2Env.Password, HttpClient? client = null)
    {
        client ??= Admin;
        using var page = await client.GetAsync("/login", P2Env.Ct);
        page.EnsureSuccessStatusCode();
        var match = AntiforgeryToken().Match(await page.Content.ReadAsStringAsync(P2Env.Ct));
        Assert.True(match.Success, "no antiforgery token on /login");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = match.Groups[1].Value,
            ["password"] = password,
            ["returnUrl"] = "",
        });
        using var login = await client.PostAsync("/login/submit", form, P2Env.Ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.ToString());
    }

    public async Task<NodeLiveStatus> WaitForNodeAsync(string nodeId, Func<NodeLiveStatus, bool> predicate, string what, TimeSpan? timeout = null)
    {
        NodeLiveStatus? node = null;
        await ExitEnv.UntilAsync(() => (node = Cache.GetNode(nodeId)) is { } n && predicate(n), what, timeout);
        return node!;
    }

    public Task WaitForModelAsync(string modelId, ModelState state) =>
        ExitEnv.UntilAsync(() => Cache.GetRuntime(modelId)?.State == state, $"{modelId} {state} in the master's cache");

    public Task<(HttpStatusCode Status, string Body)> PostAsync(string path, object? body = null) =>
        P2Env.SendAsync(body is null ? Admin.PostAsync(path, content: null, P2Env.Ct) : Admin.PostAsJsonAsync(path, body, P2Env.Ct));

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        var (status, body) = await P2Env.SendAsync(Admin.GetAsync(path, P2Env.Ct));
        Assert.True(status == HttpStatusCode.OK, $"GET {path}: {(int)status} {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>Posts and asserts the status (and the ProblemDetails code when given); returns the body.</summary>
    public async Task<string> ExpectAsync(string path, object? body, HttpStatusCode status, string? code = null)
    {
        var (actual, text) = await PostAsync(path, body);
        Assert.True(actual == status, $"POST {path}: expected {(int)status} {code}, got {(int)actual} {text}");
        if (code is not null)
        {
            Assert.Equal(code, P2Env.CodeOf(text));
        }

        return text;
    }

    /// <summary>Creates a model through <c>POST /api/models</c> (201 expected).</summary>
    public Task CreateModelAsync(object body) => ExpectAsync("/api/models", body, HttpStatusCode.Created);

    /// <summary>Starts a model through the admin API, retrying while the node's start slot or state says wait; then Ready.</summary>
    public async Task StartModelAsync(string id, bool waitReady = true)
    {
        string last = "";
        await ExitEnv.UntilAsync(async () =>
        {
            var (status, body) = await PostAsync($"/api/models/{id}/start");
            last = $"{(int)status} {body}";
            Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable, $"start {id}: {last}");
            return status == HttpStatusCode.OK;
        }, $"start {id} kept answering 409/503");
        if (waitReady)
        {
            await WaitForModelAsync(id, ModelState.Ready);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("urls", "http://127.0.0.1:0");
        // Host settings reach Program's own reads before Build (the data path and the recovery option, MasterBoot.Prepare).
        builder.UseSetting("MirbadLLM:DataPath", dataDir);
        if (settings.TryGetValue("MirbadLLM:Recovery:Mode", out string? mode))
        {
            builder.UseSetting("MirbadLLM:Recovery:Mode", mode);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddFilter("Microsoft", LogLevel.Warning);
            logging.AddFilter("System", LogLevel.Warning);
            logging.AddProvider(Log);
        });
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var all = new Dictionary<string, string?>
            {
                ["urls"] = "http://127.0.0.1:0",
                ["MirbadLLM:DataPath"] = dataDir,
                ["MirbadLLM:AdminPassword"] = P2Env.Password,
                ["MirbadLLM:DockerEndpoint"] = "unix:///nonexistent/mirbadllm-tests/docker.sock",
                ["MirbadLLM:HostAgentSocket"] = Path.Combine(dataDir, "no-hostd.sock"),
                ["MirbadLLM:ModelsLocalPath"] = Path.Combine(dataDir, "models"),
                ["MirbadLLM:ModelsHostPath"] = Path.Combine(dataDir, "models"),
                ["MirbadLLM:CacheHostPath"] = Path.Combine(dataDir, "cache"),
                ["MirbadLLM:HealthPollInterval"] = "00:10:00",
                ["MirbadLLM:Backup:Directory"] = Path.Combine(dataDir, "backups"),
                ["MirbadLLM:Moves:PollInterval"] = "00:00:00.050",
                ["MirbadLLM:Moves:RetryInterval"] = "00:00:00.150",
                ["MirbadLLM:Moves:SweepInterval"] = "00:00:00.250",
                ["MirbadLLM:Moves:StopTimeout"] = "00:00:30",
            };
            foreach ((string key, string? value) in settings)
            {
                all[key] = value;
            }

            config.AddInMemoryCollection(all);
        });
        if (services is not null)
        {
            builder.ConfigureTestServices(services);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _admin?.Dispose();
            _anonymous?.Dispose();
        }

        base.Dispose(disposing);
    }

    [GeneratedRegex("""name="__RequestVerificationToken" value="([^"]+)""")]
    private static partial Regex AntiforgeryToken();
}

/// <summary>One real agent of a <see cref="P2Fleet"/>, restartable on its port over the same state, Docker and GPUs.</summary>
internal sealed class P2Agent : IAsyncDisposable
{
    private readonly Action<AgentOptions> _configure;

    public P2Agent(ContractNode node, int port, FakeNvidiaSmi smi, Action<AgentOptions> configure)
    {
        Node = node;
        Port = port;
        Smi = smi;
        _configure = configure;
        node.NvidiaSmi = smi;
        node.ExtraServices = services => services.AddSingleton<ILoggerProvider>(Log);
    }

    public string NodeId => Node.NodeId;

    public ContractNode Node { get; }

    public int Port { get; }

    public FakeNvidiaSmi Smi { get; }

    /// <summary>The agent's log (event ids), across restarts.</summary>
    public EventLog Log { get; } = new();

    public RealAgentHarness? Agent { get; private set; }

    public ContractDocker Docker => Node.Docker;

    /// <summary>Options changed for the next start only.</summary>
    public Action<AgentOptions>? NextStart { get; set; }

    public T Get<T>()
        where T : notnull => Agent!.App.Services.GetRequiredService<T>();

    public async Task StartAsync()
    {
        var once = NextStart;
        NextStart = null;
        Agent = await TestPorts.RestartOnSamePortAsync(() => RealAgentHarness.StartAsync(Node, o =>
        {
            o.Port = Port;
            o.Addresses.Add("127.0.0.1");
            o.AllowTagPulls = true;
            _configure(o);
            once?.Invoke(o);
        }, P2Env.Ct), P2Env.Ct);
    }

    public async Task StopAsync()
    {
        if (Agent is { } agent)
        {
            Agent = null;
            await agent.DisposeAsync();
        }
    }

    /// <summary>Model containers' create bodies for <paramref name="modelId"/>.</summary>
    public IReadOnlyList<string> CreateBodiesOf(string modelId) =>
        [.. Docker.CreateBodies.Where(b => b.Contains($"\"mirbadllm.model\":\"{modelId}\"", StringComparison.Ordinal))];

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await Node.DisposeAsync();
    }
}

/// <summary>Options of a <see cref="P2Fleet"/>.</summary>
internal sealed record P2FleetOptions
{
    /// <summary>Models in <c>models.json</c> before the first boot (placed on <c>dgx</c> unless they say otherwise).</summary>
    public IReadOnlyList<ModelDefinition> Models { get; init; } = [];

    /// <summary>More master settings (override the defaults).</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

    /// <summary>An 8 s Offline rule instead of 15 s (stopped agents read Offline sooner).</summary>
    public bool FastOffline { get; init; } = true;

    /// <summary>More master services (test seams).</summary>
    public Action<IServiceCollection>? Services { get; init; }

    /// <summary>The DGX agent's options (after the bootstrap defaults).</summary>
    public Action<AgentOptions>? DgxOptions { get; init; }
}

/// <summary>
/// A P2 fleet: the real master (own CA, admin password, enroll listener) with the DGX enrolled from its bootstrap bundle,
/// plus PCs joined with operator tokens (<see cref="JoinAsync"/>). The admin client is logged in.
/// </summary>
internal sealed class P2Fleet : IAsyncDisposable
{
    private readonly P2FleetOptions _options;
    private readonly Dictionary<string, P2Agent> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _settings;

    private P2Fleet(P2FleetOptions options, string root, int enrollPort, P2Agent dgx)
    {
        _options = options;
        Root = root;
        EnrollPort = enrollPort;
        _agents[dgx.NodeId] = dgx;
        DataDir = Path.Combine(root, "master-data");
        BootstrapDir = Path.Combine(root, "bootstrap");
        _settings = new Dictionary<string, string?>
        {
            ["MirbadLLM:Enrollment:Port"] = enrollPort.ToString(CultureInfo.InvariantCulture),
            ["MirbadLLM:Enrollment:BootstrapDirectory"] = BootstrapDir,
            ["MirbadLLM:Enrollment:BootstrapEnrollEndpoint"] = $"https://127.0.0.1:{enrollPort}",
            ["MirbadLLM:Enrollment:BootstrapAgentAddress"] = $"https://127.0.0.1:{dgx.Port}",
        };
        foreach ((string key, string? value) in options.Settings)
        {
            _settings[key] = value;
        }
    }

    public string Root { get; }

    public string DataDir { get; }

    public string BootstrapDir { get; }

    public int EnrollPort { get; }

    public P2Master Master { get; private set; } = null!;

    public P2Agent Dgx => _agents["dgx"];

    public P2Agent this[string nodeId] => _agents[nodeId];

    /// <summary>The settings the master boots with (change them before <see cref="RestartMasterAsync"/>).</summary>
    public IDictionary<string, string?> Settings => _settings;

    /// <summary>Boots the master, enrolls the DGX from its bundle (Online), logs in.</summary>
    public static async Task<P2Fleet> StartAsync(P2FleetOptions? options = null)
    {
        options ??= new P2FleetOptions();
        string root = Path.Combine(Path.GetTempPath(), "ss-p2exit-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        var node = await ContractNode.CreateAsync(enroll: false, P2Env.Ct, nodeId: "dgx");
        string bootstrapDir = Path.Combine(root, "bootstrap");
        var dgx = new P2Agent(node, TestPorts.Reserve(), FakeNvidiaSmi.Gb10(), o =>
        {
            o.BootstrapDirectory = bootstrapDir;
            o.BootstrapWait = TimeSpan.FromSeconds(60);
            o.MemoryModel = MemoryModel.Unified; // agent.dgx.json (D102)
            options.DgxOptions?.Invoke(o);
        });
        var fleet = new P2Fleet(options, root, TestPorts.Reserve(), dgx);
        try
        {
            await ExitEnv.SeedModelsAsync(fleet.DataDir, [.. options.Models]);
            fleet.Master = fleet.NewMaster().Boot();
            await dgx.StartAsync();
            await fleet.Master.WaitForNodeAsync("dgx", n => n.State == NodeState.Online, "dgx Online");
            await ExitEnv.UntilAsync(async () => (await fleet.Master.Nodes.GetAsync("dgx", P2Env.Ct))?.LastCapabilities is not null,
                "dgx capabilities persisted");
            await fleet.Master.LoginAsync();
            return fleet;
        }
        catch
        {
            await fleet.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// A PC joins: the operator mints a token (<c>POST /api/nodes/join-tokens</c> with the password), the agent runs
    /// <c>join</c> against the enroll listener, and (unless <paramref name="approve"/> is false) the operator approves the
    /// pending pin the node API shows; then the agent starts and the node comes Online.
    /// </summary>
    public async Task<P2Agent> JoinAsync(string nodeId, IReadOnlyList<string>? gpus = null, bool approve = true, Action<AgentOptions>? configure = null, bool start = true)
    {
        var node = await ContractNode.CreateAsync(enroll: false, P2Env.Ct, nodeId: nodeId);
        var agent = new P2Agent(node, TestPorts.Reserve(), FakeNvidiaSmi.Discrete(gpus ?? P2Env.PcGpus), configure ?? (_ => { }));
        _agents[nodeId] = agent;
        JoinTokenCreateResponse token = await CreateJoinTokenAsync(nodeId);
        await RunJoinAsync(agent, token);
        if (approve)
        {
            await ApproveAsync(nodeId);
        }

        if (start)
        {
            await agent.StartAsync();
            if (approve)
            {
                await Master.WaitForNodeAsync(nodeId, n => n.State == NodeState.Online, $"{nodeId} Online");
                await ExitEnv.UntilAsync(async () => (await Master.Nodes.GetAsync(nodeId, P2Env.Ct))?.LastCapabilities is not null,
                    $"{nodeId} capabilities persisted");
            }
        }

        return agent;
    }

    /// <summary><c>POST /api/nodes/join-tokens</c> with the admin password.</summary>
    public async Task<JoinTokenCreateResponse> CreateJoinTokenAsync(string nodeId, bool rekey = false)
    {
        var (status, body) = await Master.PostAsync("/api/nodes/join-tokens", new { nodeId, password = P2Env.Password, rekey });
        Assert.True(status == HttpStatusCode.OK || status == HttpStatusCode.Created, $"join token for {nodeId}: {(int)status} {body}");
        var created = JsonSerializer.Deserialize<JoinTokenCreateResponse>(body, P2Env.Json)!;
        _issuedTokens.Add(created.Token);
        return created;
    }

    /// <summary>Every join and rekey token minted through <see cref="CreateJoinTokenAsync"/> (secrets the audit and logs must not hold).</summary>
    public IReadOnlyList<string> IssuedTokens => [.. _issuedTokens];

    private readonly ConcurrentBag<string> _issuedTokens = [];

    /// <summary>Runs <c>mirbadllm-agent join</c> (or <c>join --rekey</c>) for the node's state directory.</summary>
    public async Task<int> RunJoinAsync(P2Agent agent, JoinTokenCreateResponse token, bool rekey = false, string? nodeUid = null, bool expectOk = true)
    {
        var args = new List<string>();
        if (rekey)
        {
            args.Add(JoinCommand.RekeyFlag);
        }

        args.AddRange(["--enroll-url", $"https://127.0.0.1:{EnrollPort}", "--token", token.Token, "--ca-hash", token.CaHash,
            "--node-id", agent.NodeId, "--state-dir", agent.Node.StateDirectory, "--address", $"https://127.0.0.1:{agent.Port}"]);
        if (nodeUid is not null)
        {
            args.AddRange(["--node-uid", nodeUid]);
        }

        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = await JoinCommand.RunAsync([.. args], output, error, ct: P2Env.Ct);
        if (expectOk)
        {
            Assert.True(exit == JoinCommand.ExitOk, $"join exited {exit}: {output}{error}");
        }

        Assert.DoesNotContain(token.Token, output.ToString() + error, StringComparison.Ordinal);
        return exit;
    }

    /// <summary>Approves the pending node with the pin <c>GET /api/nodes/{id}</c> shows.</summary>
    public async Task<NodeRecord> ApproveAsync(string nodeId)
    {
        JsonElement view = await Master.GetJsonAsync($"/api/nodes/{nodeId}");
        string? pin = view.GetProperty("node").GetProperty("pendingCertSpkiSha256").GetString();
        Assert.False(string.IsNullOrEmpty(pin), $"{nodeId} has no pending pin");
        await Master.ExpectAsync($"/api/nodes/{nodeId}/approve", new NodeApproveRequest { ExpectedSpkiSha256 = pin }, HttpStatusCode.OK);
        var record = (await Master.Nodes.GetAsync(nodeId, P2Env.Ct))!;
        Assert.True(record.Approved);
        Assert.Equal(pin, record.CertSpkiSha256);
        return record;
    }

    /// <summary>
    /// Stops the master (a crash or a redeploy) and boots a new one over the same data, logged in.
    /// <paramref name="whileStopped"/> runs between the two (the data directory is unlocked: copy or restore <c>/data</c>).
    /// </summary>
    public async Task RestartMasterAsync(bool waitForDgx = true, Func<Task>? whileStopped = null)
    {
        await Master.DisposeAsync();
        if (whileStopped is not null)
        {
            await whileStopped();
        }

        Master = await TestPorts.RestartOnSamePortAsync(() => Task.FromResult(NewMaster().Boot()), P2Env.Ct);
        if (waitForDgx)
        {
            await Master.WaitForNodeAsync("dgx", n => n.State == NodeState.Online, "dgx Online after the master restart");
        }

        await Master.LoginAsync();
    }

    /// <summary>The audit log as raw text (every line written so far).</summary>
    public string AuditText() => P2Env.ReadAuditText(DataDir);

    /// <summary>Audit records through the composed reader (newest first).</summary>
    public async Task<IReadOnlyList<AuditRecord>> AuditAsync(AuditQuery? query = null) =>
        await Master.Get<IAuditLog>().ReadAsync(query ?? new AuditQuery { Max = 1000 }, P2Env.Ct);

    public async ValueTask DisposeAsync()
    {
        foreach (var agent in _agents.Values)
        {
            await agent.StopAsync();
        }

        if (Master is not null)
        {
            await Master.DisposeAsync();
        }

        foreach (var agent in _agents.Values)
        {
            await agent.DisposeAsync();
        }

        ExitEnv.DeleteDirectory(Root);
    }

    private P2Master NewMaster() => new(DataDir, _settings, services =>
    {
        if (_options.FastOffline)
        {
            // An 8 s Offline rule instead of 15 s (the real agent heartbeats every 5 s on /events) and fast reconnects; the
            // CA-rotation reason stays wired as Program composes it.
            services.AddSingleton(sp => new NodeStatusCache(
                sp.GetRequiredService<IAgentClientFactory>(),
                sp.GetRequiredService<IMasterIdentity>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IModelRegistry>(),
                sp.GetRequiredService<ILogger<NodeStatusCache>>(),
                new NodeMonitorOptions
                {
                    OfflineAfter = TimeSpan.FromSeconds(8),
                    ReconnectInitialDelay = TimeSpan.FromMilliseconds(200),
                    ReconnectMaxDelay = TimeSpan.FromSeconds(1),
                },
                degradedReason: sp.GetRequiredService<MasterTrustStore>().PendingReason));
        }

        _options.Services?.Invoke(services);
    });
}
