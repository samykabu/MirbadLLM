using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// Shared plumbing of the P1 development exit tests (MN-043, design §13.6 and §14 P1): waits on conditions (never fixed
/// sleeps), throwaway data directories, <c>models.json</c>/<c>nodes.json</c> seeding, and the two node set-ups every exit
/// test drives through the real master composition (<see cref="ExitMaster"/>): <see cref="FakeNode"/> (FakeAgent with its
/// failure knobs) and <see cref="RealNode"/> (the real agent composition, restartable over the same state).
/// </summary>
internal static class ExitEnv
{
    /// <summary>The bootstrap node id (D4).</summary>
    public const string NodeId = "dgx";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Upper bound of every condition wait (a hang fails the test instead of blocking the run).</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public static Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null) =>
        UntilAsync(() => Task.FromResult(condition()), what, timeout);

    /// <summary>Polls <paramref name="condition"/> every 50 ms until it holds; fails with <paramref name="what"/> after the timeout.</summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? Patience);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                Assert.Fail($"Timed out waiting for {what}.");
            }

            await Task.Delay(50, Ct);
        }
    }

    /// <summary>Polls until <paramref name="probe"/> returns a value.</summary>
    public static async Task<T> UntilValueAsync<T>(Func<Task<T?>> probe, string what, TimeSpan? timeout = null)
        where T : class
    {
        T? value = null;
        await UntilAsync(async () => (value = await probe()) is not null, what, timeout);
        return value!;
    }

    public static string NewDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), $"ss-exit-{prefix}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteDirectory(string path)
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
                Thread.Sleep(100); // best-effort cleanup of a handle Windows releases late
            }
        }
    }

    /// <summary>A model placed on <c>dgx</c> with the seeded folder (the image decides whether a launch pulls).</summary>
    public static ModelDefinition Model(string id, string? servedName = null, string image = AgentContractSeed.Image, double budgetGiB = 1) => new()
    {
        Id = id,
        ServedName = servedName ?? id,
        Image = image,
        ModelPath = AgentContractSeed.ModelFolder,
        MemoryBudgetGiB = budgetGiB,
        MaxModelLen = 8192,
        MaxNumSeqs = 16,
        Placement = new ModelPlacement(NodeId),
    };

    /// <summary><c>models.json</c> with only <paramref name="models"/> (the first load seeds a default model; it is removed).</summary>
    public static async Task SeedModelsAsync(string dataDir, params ModelDefinition[] models)
    {
        using var registry = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }));
        foreach (var existing in await registry.ListAsync(Ct))
        {
            await registry.DeleteAsync(existing.Id, Ct);
        }

        foreach (var model in models)
        {
            await registry.UpsertAsync(model, Ct);
        }
    }

    /// <summary>Writes the node into <c>nodes.json</c> as the enrolled, approved bootstrap node (what enrollment leaves).</summary>
    public static async Task RegisterNodeAsync(string dataDir, string uid, Uri address, string spki)
    {
        using var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), TimeProvider.System);
        await registry.UpsertAsync(new NodeRecord
        {
            Id = NodeId,
            Uid = uid,
            Address = address.AbsoluteUri,
            Approved = true,
            CertSpkiSha256 = spki,
        }, Ct);
    }

    /// <summary>Raises <c>master.json</c>'s epoch before a master boots on <paramref name="dataDir"/> (the boot adds one).</summary>
    public static async Task RaiseEpochAsync(string dataDir, long epoch)
    {
        await using var identity = MirbadLLM.Orchestration.Fencing.MasterIdentity.Open(dataDir);
        await identity.RaiseEpochToAsync(epoch, Ct);
    }

    /// <summary>Copies a master data directory (a restore of an older <c>/data</c>), without the lock file.</summary>
    public static void CopyDataDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "master.lock")
            {
                continue;
            }

            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static async Task<(HttpStatusCode Status, string Body)> SendAsync(Task<HttpResponseMessage> request)
    {
        using var response = await request;
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }
}

/// <summary>
/// The real master composition (<c>MirbadLLM.Web</c>'s <c>Program</c>) with every background service on (node monitor,
/// registry sync, telemetry, rotation, …), over <paramref name="dataDir"/>, with the test CA as its configured CA (D63,
/// "tests use the keys only to share a test CA"; null = the master's own CA in <c>{DataPath}/pki</c>, as on the DGX) and no
/// admin password. Legacy host settings point nowhere. <paramref name="configure"/> runs last (P2 tests add services the P1
/// composition does not have yet, e.g. MN-115's takeover service).
/// </summary>
internal sealed class ExitMaster(
    string dataDir, CertificateAuthority? ca, IReadOnlyDictionary<string, string?>? settings = null, Action<IWebHostBuilder>? configure = null)
    : WebApplicationFactory<global::Program>
{
    private HttpClient? _http;

    public string DataDir => dataDir;

    /// <summary>A client of the master's HTTP API (<c>/api</c>, <c>/v1</c>).</summary>
    public HttpClient Http => _http ??= CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public INodeStatusCache Cache => Get<INodeStatusCache>();

    public IMasterIdentity Identity => Get<IMasterIdentity>();

    /// <summary>The master's own client of the node (its handlers, master certificate and master triple).</summary>
    public IAgentClient Client => Get<IAgentClientFactory>().GetClient(ExitEnv.NodeId);

    /// <summary>Boots the host (resolving the services starts it) and waits for <c>dgx</c> to reach <paramref name="state"/>.</summary>
    public async Task<ExitMaster> StartAsync(NodeState? state = NodeState.Online)
    {
        _ = Services;
        if (state is { } expected)
        {
            await WaitForNodeAsync(n => n.State == expected, $"dgx {expected}");
        }

        return this;
    }

    public async Task<NodeLiveStatus> WaitForNodeAsync(Func<NodeLiveStatus, bool> predicate, string what)
    {
        NodeLiveStatus? node = null;
        await ExitEnv.UntilAsync(() => (node = Cache.GetNode(ExitEnv.NodeId)) is { } n && predicate(n), what);
        return node!;
    }

    /// <summary>Waits until the master's cache shows <paramref name="modelId"/> in <paramref name="state"/>.</summary>
    public Task WaitForModelAsync(string modelId, ModelState state) =>
        ExitEnv.UntilAsync(() => Cache.GetRuntime(modelId)?.State == state, $"{modelId} {state} in the master's cache");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (settings?.TryGetValue("urls", out string? urls) == true)
        {
            builder.UseSetting("urls", urls); // Kestrel hosting (UseKestrel): the master's own HTTP address
        }

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var all = new Dictionary<string, string?>
            {
                ["MirbadLLM:DataPath"] = dataDir,
                ["MirbadLLM:AdminPassword"] = null,
                ["MirbadLLM:DockerEndpoint"] = "unix:///nonexistent/mirbadllm-tests/docker.sock",
                ["MirbadLLM:HostAgentSocket"] = Path.Combine(dataDir, "no-hostd.sock"),
                ["MirbadLLM:ModelsLocalPath"] = Path.Combine(dataDir, "models"),
                ["MirbadLLM:ModelsHostPath"] = Path.Combine(dataDir, "models"),
                ["MirbadLLM:CacheHostPath"] = Path.Combine(dataDir, "cache"),
                ["MirbadLLM:HealthPollInterval"] = "00:10:00",
                ["MirbadLLM:Backup:Directory"] = Path.Combine(dataDir, "backups"),
                ["MirbadLLM:Enrollment:BootstrapDirectory"] = Path.Combine(dataDir, "bootstrap"),
                ["Logging:LogLevel:Default"] = "Warning",
            };
            if (ca is not null)
            {
                all["MirbadLLM:Pki:CaCertPem"] = ca.CertificatePem;
                all["MirbadLLM:Pki:CaKeyPem"] = ca.ExportPrivateKeyPem();
            }

            foreach ((string key, string? value) in settings ?? new Dictionary<string, string?>())
            {
                all[key] = value;
            }

            config.AddInMemoryCollection(all);
        });
        configure?.Invoke(builder);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _http?.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A <see cref="FakeAgent"/> as the enrolled node <c>dgx</c> of one master data directory (more can be registered).</summary>
internal sealed class FakeNode : IAsyncDisposable
{
    private readonly List<string> _directories = [];

    private FakeNode(CertificateAuthority ca, FakeAgent agent, string dataDir)
    {
        Ca = ca;
        Agent = agent;
        DataDir = dataDir;
        _directories.Add(dataDir);
    }

    public CertificateAuthority Ca { get; }

    public FakeAgent Agent { get; }

    /// <summary>The first master's data directory (node registered, models seeded).</summary>
    public string DataDir { get; }

    public static async Task<FakeNode> StartAsync(FakeAgentOptions? options = null, params ModelDefinition[] models)
    {
        var ca = CertificateAuthority.Create();
        var agent = await FakeAgent.StartAsync((options ?? new FakeAgentOptions()) with { Ca = ca }, ExitEnv.Ct);
        var node = new FakeNode(ca, agent, ExitEnv.NewDirectory("master"));
        await node.PrepareAsync(node.DataDir, models);
        return node;
    }

    /// <summary>Another master data directory with this node registered and <paramref name="models"/> seeded.</summary>
    public async Task<string> NewDataDirAsync(params ModelDefinition[] models)
    {
        string dir = ExitEnv.NewDirectory("master");
        _directories.Add(dir);
        await PrepareAsync(dir, models);
        return dir;
    }

    /// <summary>Tracks a directory for deletion on dispose.</summary>
    public string Track(string dir)
    {
        _directories.Add(dir);
        return dir;
    }

    public ExitMaster Master(
        string? dataDir = null, IReadOnlyDictionary<string, string?>? settings = null, Action<IWebHostBuilder>? configure = null) =>
        new(dataDir ?? DataDir, Ca, settings, configure);

    public async ValueTask DisposeAsync()
    {
        await Agent.DisposeAsync();
        Ca.Dispose();
        foreach (string dir in _directories)
        {
            ExitEnv.DeleteDirectory(dir);
        }
    }

    private async Task PrepareAsync(string dir, ModelDefinition[] models)
    {
        await ExitEnv.RegisterNodeAsync(dir, Agent.NodeUid, Agent.BaseAddress, Agent.CurrentSpki);
        await ExitEnv.SeedModelsAsync(dir, models);
    }
}

/// <summary>
/// The real agent composition (<c>MirbadLLM.Agent.Program.CreateAsync</c>, MN-026's <see cref="RealAgentHarness"/>) as the
/// enrolled node <c>dgx</c>, on a fixed loopback port so it can be stopped and started again over the same state (operation
/// log, fencing triple, identity), Docker and model server.
/// </summary>
internal sealed class RealNode : IAsyncDisposable
{
    private readonly Action<MirbadLLM.Agent.AgentOptions>? _configure;

    private RealNode(ContractNode node, string dataDir, int port, Action<MirbadLLM.Agent.AgentOptions>? configure)
    {
        Node = node;
        DataDir = dataDir;
        Port = port;
        _configure = configure;
    }

    public ContractNode Node { get; }

    public string DataDir { get; }

    public int Port { get; }

    public RealAgentHarness? Agent { get; private set; }

    public static async Task<RealNode> StartAsync(
        Action<MirbadLLM.Agent.AgentOptions>? configure = null, Action<ContractNode>? prepare = null, params ModelDefinition[] models)
    {
        var contract = await ContractNode.CreateAsync(enroll: true, ExitEnv.Ct, nodeId: ExitEnv.NodeId);
        prepare?.Invoke(contract);
        var node = new RealNode(contract, ExitEnv.NewDirectory("master"), TestPorts.Reserve(), configure);
        await node.StartAgentAsync();
        await ExitEnv.RegisterNodeAsync(node.DataDir, contract.NodeUid, node.Agent!.BaseAddress, contract.AgentSpki);
        await ExitEnv.SeedModelsAsync(node.DataDir, models);
        return node;
    }

    public ExitMaster Master(IReadOnlyDictionary<string, string?>? settings = null, Action<IWebHostBuilder>? configure = null) =>
        new(DataDir, Node.Ca, settings, configure);

    /// <summary>Starts the agent on <see cref="Port"/> (reserved), retrying while a stopped agent's listener releases it.</summary>
    public async Task StartAgentAsync()
    {
        Agent = await TestPorts.RestartOnSamePortAsync(() => RealAgentHarness.StartAsync(Node, o =>
        {
            o.Port = Port;
            o.AllowTagPulls = true; // the P1 DGX setting (today's images are tags)
            _configure?.Invoke(o);
        }, ExitEnv.Ct), ExitEnv.Ct);
    }

    public async Task StopAgentAsync()
    {
        if (Agent is { } agent)
        {
            Agent = null;
            await agent.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAgentAsync();
        await Node.DisposeAsync();
        ExitEnv.DeleteDirectory(DataDir);
    }
}
