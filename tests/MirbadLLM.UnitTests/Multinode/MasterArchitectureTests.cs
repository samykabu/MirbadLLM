using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Auth;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.Web;
using MirbadLLM.Web.Enrollment;

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// The master composition (MN-040, plan §3.5 "no DockerEngineClient resolvable in the master container"): the real
/// <c>Program.cs</c> host resolves no host-side component, boots against <see cref="Fakes.FakeAgent"/> as the bootstrap
/// node <c>dgx</c> and serves <c>/healthz</c> and <c>/v1</c> through it, and a second master on the same data path fails at
/// startup.
/// </summary>
public sealed class MasterArchitectureTests
{
    /// <summary>Types that live on the agent (design §2.4) and must not be part of the master's container.</summary>
    private static readonly Type[] HostComponents =
    [
        typeof(DockerEngineClient), typeof(DockerServiceClient), typeof(ProcMeminfoMemoryProbe), typeof(NvidiaSmiGpuProbe),
        typeof(HostAgentClient), typeof(HuggingFaceDownloader), typeof(HostAgentGpuProcessProbe), typeof(ModelFolderInspector),
    ];

    /// <summary>
    /// The pre-P1 single-host types kept unregistered as parity references until MN-047 recorded their behaviour as
    /// fixtures and deleted them (W4 review MN-096, D60).
    /// </summary>
    private static readonly string[] DeletedSingleHostTypes =
    [
        "MirbadLLM.Orchestration.DockerModelRuntime",
        "MirbadLLM.Orchestration.MetricsSampler",
        "MirbadLLM.Orchestration.UpstreamUri",
        "MirbadLLM.Orchestration.ModelEndpointResolver",
        "MirbadLLM.Orchestration.VllmCatalogService",
        "MirbadLLM.Orchestration.Benchmarking.UpstreamBenchmarkTargetResolver",
        "MirbadLLM.Orchestration.Recovery.LocalCheckpointFolders",
    ];

    private static readonly ModelDefinition Model = new()
    {
        Id = "qwen",
        ServedName = "qwen",
        ModelPath = AgentContractSeed.ModelFolder,
        MemoryBudgetGiB = 1,
        MaxModelLen = 8192,
        MaxNumSeqs = 16,
        Placement = new ModelPlacement("dgx"),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheMasterContainerHoldsNoHostComponent()
    {
        List<ServiceDescriptor> descriptors = [];
        using var factory = new AuthWebFactory { AdminPassword = null };
        using var captured = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services => descriptors.AddRange(services)));
        var services = captured.Services;
        var isService = services.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceProviderIsService>();

        foreach (var type in HostComponents)
        {
            Assert.False(isService.IsService(type), $"{type.Name} is registered in the master.");
            Assert.DoesNotContain(descriptors, d => Mentions(d, type));
        }

        Assert.IsType<RemoteModelRuntime>(services.GetRequiredService<IModelRuntime>());
        Assert.IsType<RemoteModelRuntime>(services.GetRequiredService<IModelLauncher>());
        Assert.IsType<AgentEndpointResolver>(services.GetRequiredService<IModelEndpointResolver>());
        Assert.IsType<RemoteMemoryProbe>(services.GetRequiredService<IMemoryProbe>());
        Assert.IsType<RemoteGpuProbe>(services.GetRequiredService<IGpuProbe>());
        Assert.IsType<RemoteGpuProcessProbe>(services.GetRequiredService<IGpuProcessProbe>());
        Assert.IsType<RemoteHuggingFaceDownloader>(services.GetRequiredService<IHuggingFaceDownloader>());
        Assert.IsType<RemoteCheckpointFolders>(services.GetRequiredService<ICheckpointFolders>());
        Assert.IsType<RemoteBackupMountClient>(services.GetRequiredService<IHostAgentBackupMountClient>());
        Assert.IsType<JsonNodeRegistry>(services.GetRequiredService<INodeRegistry>());
        Assert.Same(services.GetRequiredService<JsonNodeRegistry>(), services.GetRequiredService<INodeRegistry>());
        Assert.Same(services.GetRequiredService<AgentClientFactory>(), services.GetRequiredService<IAgentClientFactory>());
        Assert.Same(services.GetRequiredService<NodeStatusCache>(), services.GetRequiredService<INodeStatusCache>());
        Assert.NotNull(services.GetRequiredService<IHostServiceManager>());
        Assert.NotNull(services.GetRequiredService<RestoreJobService>());
        Assert.NotNull(services.GetRequiredService<BackupService>());

        // One master certificate set, shared by the enroll listener and the rotation service (W3 review MN-094).
        Assert.Single(descriptors, d => d.ServiceType == typeof(MirbadLLM.Orchestration.Pki.MasterCertificates));
    }

    [Fact]
    public void TheDeletedSingleHostTypesDoNotExist()
    {
        var assemblies = new[] { typeof(RemoteModelRuntime).Assembly, typeof(AgentEndpointResolver).Assembly, typeof(MasterHosting).Assembly };

        foreach (string name in DeletedSingleHostTypes)
        {
            Assert.All(assemblies, a => Assert.Null(a.GetType(name)));
        }
    }

    [Fact]
    public async Task TheMasterBootsWithTheFakeAgentAsDgxAndServesHealthzAndV1()
    {
        using var factory = new AuthWebFactory { AdminPassword = null, KeepBackgroundServices = true };
        await SeedModelsAsync(factory.DataDir, Model with { AutoStart = true });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cache = factory.Services.GetRequiredService<INodeStatusCache>();
        await WaitUntilAsync(() => cache.GetNode("dgx")?.State == NodeState.Online, "dgx never came Online");

        using (var health = await client.GetAsync("/healthz", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        // Folders and the catalog come from the node's agent, not from a local /models.
        using (var folders = await client.GetAsync("/api/model-folders", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, folders.StatusCode);
            Assert.Contains(AgentContractSeed.ModelFolder, await folders.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // Boot auto-start waited for the node's first snapshot, then started the model through the agent.
        await WaitUntilAsync(async () =>
        {
            using var models = await client.GetAsync("/v1/models", Ct);
            return models.StatusCode == HttpStatusCode.OK && (await models.Content.ReadAsStringAsync(Ct)).Contains("\"qwen\"", StringComparison.Ordinal);
        }, "qwen never appeared in /v1/models");

        using var chat = await client.PostAsJsonAsync(
            "/v1/chat/completions",
            new { model = "qwen", messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4 },
            Ct);
        string body = await chat.Content.ReadAsStringAsync(Ct);
        Assert.True(chat.StatusCode == HttpStatusCode.OK, $"{(int)chat.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.TryGetProperty("choices", out _), body);

        // Today's answer for an unknown model is unchanged through the agent path.
        using var unknown = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "nope", messages = Array.Empty<object>() }, Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Contains("model_not_found", await unknown.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FolderReadsAnswer503WithTheReasonWhenTheNodeCannotBeReached()
    {
        // W4 review MN-096: an unreachable node is not "no folders" (empty list) or "no such folder" (404).
        using var factory = new AuthWebFactory { AdminPassword = null, KeepBackgroundServices = true };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cache = factory.Services.GetRequiredService<INodeStatusCache>();
        await WaitUntilAsync(() => cache.GetNode("dgx")?.State == NodeState.Online, "dgx never came Online");
        using (var online = await client.GetAsync($"/api/model-folders/{AgentContractSeed.ModelFolder}", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, online.StatusCode);
        }

        factory.Agent.SetPartitioned(true);

        foreach (string path in new[] { "/api/model-folders", $"/api/model-folders/{AgentContractSeed.ModelFolder}" })
        {
            using var response = await client.GetAsync(path, Ct);
            string body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable, $"{path}: {(int)response.StatusCode} {body}");
            Assert.Contains("dgx", body, StringComparison.Ordinal);
        }

        // The catalog still answers from the node's last report.
        using var catalog = await client.GetAsync("/api/catalog", Ct);
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
    }

    [Fact]
    public void ASecondMasterOnTheSameDataPathFailsAtStartup()
    {
        using var first = new AuthWebFactory { AdminPassword = null };
        _ = first.Services.GetRequiredService<IMasterIdentity>();

        using var second = new AuthWebFactory(first.DataDir) { AdminPassword = null };
        var failure = Record.Exception(() => second.Services);
        Assert.NotNull(failure);
        Assert.True(Unwrap(failure) is MasterLockException, failure.ToString());
    }

    [Fact]
    public async Task EnrollIsOnlyServedOnTheEnrollListener()
    {
        using var factory = new AuthWebFactory { AdminPassword = null };
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.PostAsJsonAsync("/enroll", new { token = "x", nodeId = "pc" }, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);

        // The endpoint answers 404 off the enroll listener; the status-code page re-executes that POST as /not-found,
        // which answers 400 like any other unknown POST does today. Either way enrollment never ran.
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{(int)response.StatusCode} {body}");
        Assert.DoesNotContain(ErrorCodes.EnrollmentRefused, body, StringComparison.Ordinal);
        Assert.DoesNotContain(ErrorCodes.InvalidRequest, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBootstrapNodeRecordIsWrittenOnceOnFirstBoot()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-bootstrap-node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            var options = new MirbadLLMOptions { DataPath = dataDir, MemoryReserveGiB = 6 };
            using var registry = new JsonNodeRegistry(Options.Create(options), TimeProvider.System);
            var enrollment = new EnrollmentOptions();
            using var provider = Provider(options, registry, enrollment);

            await MasterHosting.EnsureBootstrapNodeAsync(provider, Ct);
            var created = Assert.Single(await registry.ListAsync(Ct));
            Assert.Equal("dgx", created.Id);
            Assert.Equal(MasterHosting.DefaultBootstrapAgentAddress, created.Address);
            Assert.Equal(6, created.MemoryReserveGiB);
            Assert.False(created.Approved);
            Assert.Null(created.CertSpkiSha256);

            // Never overwritten (enrollment owns the record from here on).
            enrollment.BootstrapAgentAddress = "https://127.0.0.1:9443";
            await MasterHosting.EnsureBootstrapNodeAsync(provider, Ct);
            var kept = Assert.Single(await registry.ListAsync(Ct));
            Assert.Equal(created.Uid, kept.Uid);
            Assert.Equal(created.Address, kept.Address);
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task NoBootstrapNodeRecordWhenBootstrapIsOff()
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-bootstrap-node-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            var options = new MirbadLLMOptions { DataPath = dataDir };
            using var registry = new JsonNodeRegistry(Options.Create(options), TimeProvider.System);
            using var provider = Provider(options, registry, new EnrollmentOptions { BootstrapEnabled = false, BootstrapAgentAddress = "https://10.0.0.5:9443" });

            await MasterHosting.EnsureBootstrapNodeAsync(provider, Ct);

            Assert.Empty(await registry.ListAsync(Ct));
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryChangesReachTheAgentClientFactory()
    {
        await using var agent = await Fakes.FakeAgent.StartAsync(ct: Ct);
        string dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-registry-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDir);
        try
        {
            using var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), TimeProvider.System);
            using var master = agent.Ca.IssueMasterClientCertificate();
            using var trust = MirbadLLM.Pki.TrustAnchorSet.Create([agent.Ca.Certificate]);
            await using var factory = new AgentClientFactory(registry, new TestMasterIdentity(), master, trust, TimeProvider.System);
            using var sync = new NodeRegistrySync(registry, factory, TimeProvider.System);

            // The bootstrap record before enrollment: no pin, no client.
            var record = new NodeRecord { Id = agent.NodeId, Uid = "0123456789abcdef0123456789abcdef", Address = agent.BaseAddress.AbsoluteUri };
            await registry.UpsertAsync(record, Ct);
            Assert.True(await sync.SyncAsync(Ct));
            Assert.Empty(factory.NodeIds);
            Assert.False(await sync.SyncAsync(Ct));

            // Enrollment writes uid, pin and approval: the next pass gives the node a working client.
            await registry.UpsertAsync(record with { Uid = agent.NodeUid, Approved = true, CertSpkiSha256 = agent.CurrentSpki }, Ct);
            Assert.True(await sync.SyncAsync(Ct));
            Assert.Equal([agent.NodeId], factory.NodeIds);
            var info = await factory.GetClient(agent.NodeId).GetInfoAsync(Ct);
            Assert.Equal(agent.NodeUid, info.NodeUid);

            // Fields that do not decide the connection do not rebuild anything.
            await registry.UpsertAsync((await registry.GetAsync(agent.NodeId, Ct))! with { DisplayName = "DGX Spark", LastContact = DateTimeOffset.UtcNow }, Ct);
            Assert.False(await sync.SyncAsync(Ct));
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("urls")]
    [InlineData("http_ports")] // the container image's ASPNETCORE_HTTP_PORTS=8080 (CI and the DGX), W4 review MN-096
    public async Task TheEnrollListenerIsAddedNextToTheConfiguredUrls(string setting)
    {
        int enrollPort = TestPorts.Reserve();
        using var ca = MirbadLLM.Pki.CertificateAuthority.Create();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // The configured HTTP endpoint binds port 0 (Kestrel reports what it got); the enroll port must be known up front.
            [setting] = setting == "urls" ? "http://127.0.0.1:0" : "0",
            ["MirbadLLM:Enrollment:Port"] = enrollPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.Services.AddSingleton(ca);
        builder.Services.AddSingleton<INodeRegistry>(new EmptyNodeRegistry());
        builder.Services.AddSingleton(Options.Create(new RegistryMigrationOptions()));
        builder.Services.AddMirbadLLMEnrollment(builder.Configuration);
        builder.Services.PostConfigure<EnrollmentOptions>(o => o.BootstrapEnabled = false);
        MasterHosting.AddEnrollListener(builder);
        await using var app = builder.Build();
        app.UseEnrollListenerGate();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync(Ct);
        int httpPort = app.Services.GetRequiredService<IServer>()
            .Features.GetRequiredFeature<IServerAddressesFeature>().Addresses
            .Select(a => new Uri(a.Replace("://*", "://localhost", StringComparison.Ordinal).Replace("://+", "://localhost", StringComparison.Ordinal)))
            .Single(u => u.Scheme == Uri.UriSchemeHttp).Port;

        // The configured URL still serves the application.
        using var http = new HttpClient();
        Assert.Equal("pong", await http.GetStringAsync(new Uri($"http://127.0.0.1:{httpPort}/ping"), Ct));

        // The enroll port is TLS with the CA-issued listener certificate, and only POST /enroll is served there.
        System.Security.Cryptography.X509Certificates.X509Certificate2? presented = null;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                presented = certificate is null ? null : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                // Trust only a certificate issued by the test CA (the listener does not send the CA in its chain, D35).
                return presented is not null && presented.Issuer == ca.Certificate.Subject;
            } },
        };
        using var tls = new HttpClient(handler);
        using var response = await tls.GetAsync(new Uri($"https://127.0.0.1:{enrollPort}/ping"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotNull(presented);
        Assert.Equal(ca.Certificate.Subject, presented!.Issuer);
        presented.Dispose();
        await app.StopAsync(Ct);
    }

    /// <summary>A registry with no nodes (the enroll listener test never enrolls).</summary>
    private sealed class EmptyNodeRegistry : INodeRegistry
    {
        public Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NodeRecord>>([]);

        public Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<NodeRecord?>(null);

        public Task UpsertAsync(NodeRecord node, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> RevokeAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JoinTokenRecord>>([]);
    }

    /// <summary>models.json with only <paramref name="models"/> (the first load seeds a default model; it is removed).</summary>
    private static async Task SeedModelsAsync(string dataDir, params ModelDefinition[] models)
    {
        Directory.CreateDirectory(dataDir);
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

    private static ServiceProvider Provider(MirbadLLMOptions options, JsonNodeRegistry registry, EnrollmentOptions enrollment)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(Options.Create(enrollment));
        services.AddSingleton(Options.Create(new RegistryMigrationOptions()));
        services.AddSingleton<INodeRegistry>(registry);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        return services.BuildServiceProvider();
    }

    private static bool Mentions(ServiceDescriptor descriptor, Type type) =>
        descriptor.ServiceType == type
        || (descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType == type || descriptor.KeyedImplementationInstance?.GetType() == type
                || descriptor.KeyedImplementationFactory?.Method.ReturnType == type
            : descriptor.ImplementationType == type || descriptor.ImplementationInstance?.GetType() == type
                || descriptor.ImplementationFactory?.Method.ReturnType == type);

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException or System.Reflection.TargetInvocationException or InvalidOperationException { InnerException: not null }
            && exception is not MasterLockException && exception.InnerException is { } inner)
        {
            exception = inner;
        }

        return exception;
    }

    private static Task WaitUntilAsync(Func<bool> condition, string message) =>
        WaitUntilAsync(() => Task.FromResult(condition()), message);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, message);
            await Task.Delay(50, Ct);
        }
    }
}
