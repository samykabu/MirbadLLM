using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Pki;

namespace MirbadLLM.Orchestration;

/// <summary>Where the master CA comes from when it is not on disk (design §10.2, §11.2), bound from <c>MirbadLLM:Pki</c>.</summary>
public sealed class MasterPkiOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "MirbadLLM:Pki";

    /// <summary>Directory below <see cref="MirbadLLMOptions.DataPath"/> holding the CA and the master's certificates.</summary>
    public const string DirectoryName = "pki";

    /// <summary><c>MirbadLLM__Pki__CaCertPem</c>: the CA certificate supplied by configuration (topology b); null = on disk.</summary>
    public string? CaCertPem { get; set; }

    /// <summary><c>MirbadLLM__Pki__CaKeyPem</c>: the CA key supplied by configuration; set together with <see cref="CaCertPem"/>.</summary>
    public string? CaKeyPem { get; set; }

    /// <summary>The PKI directory of a data path (<c>{DataPath}/pki</c>).</summary>
    public static string DirectoryOf(string dataPath) => Path.Combine(dataPath, DirectoryName);
}

public static class ServiceCollectionExtensions
{
    private const string HuggingFaceHttpClientName = "mirbadllm.huggingface";
    private const string BenchmarkHttpClientName = "mirbadllm.benchmark";

    /// <summary>
    /// The master's orchestration over node agents (design §2.4, MN-040). Nothing here talks to Docker, hostd, local
    /// probes or local model folders: model instances, telemetry, downloads and folders live on the agents.
    /// <list type="bullet">
    /// <item>State: <see cref="IModelRegistry"/> (with the v1 → v2 migration options bound first), <see cref="IApiKeyStore"/>,
    /// <see cref="INodeRegistry"/> (one <see cref="JsonNodeRegistry"/>), master fencing (<see cref="IMasterIdentity"/>;
    /// the host resolves it before anything touches <c>DataPath</c>), operation references.</item>
    /// <item>PKI: the master <see cref="CertificateAuthority"/> (<see cref="CaStore"/> in <c>{DataPath}/pki</c> or
    /// <c>MirbadLLM:Pki</c>), <see cref="MasterCertificates"/> (the host registers it first with the enroll listener's
    /// addresses; otherwise it is issued without extra names) and <see cref="CertificateRotationService"/>.</item>
    /// <item>Agents: <see cref="AgentClientFactory"/>, <see cref="AgentOperationRunner"/>, <see cref="NodeStatusCache"/>,
    /// <see cref="NodeRegistrySync"/> and <see cref="NodeMonitor"/> (started before the health monitor),
    /// <see cref="RemoteModelRuntime"/> as <see cref="IModelRuntime"/> and <see cref="IModelLauncher"/>.</item>
    /// <item>Monitoring: <see cref="InMemoryMetricsHistory"/> filled by <see cref="RemoteTelemetryCollector"/> with the
    /// remote probes (<see cref="RemoteTelemetryServiceCollectionExtensions.AddRemoteTelemetry"/>).</item>
    /// <item>Sourcing: <see cref="RemoteHuggingFaceDownloader"/>, <see cref="RemoteModelFolders"/>,
    /// <see cref="RemoteVllmCatalogService"/> on the bootstrap node; <see cref="IHuggingFaceRepoInspector"/> (HTTP only)
    /// stays on the master.</item>
    /// <item>Jobs: <see cref="ModelJobService"/> with <see cref="AgentJobTargets"/> (target, GPU idle, recovery gate).</item>
    /// </list>
    /// The router's agent path is registered by <c>AddMirbadLLMAgentRouting</c> (MirbadLLM.Router).
    /// <para>P2 (MN-130): the PKI repairs a CA rotation's promotion before the CA loads and validates agents under
    /// <see cref="MasterTrustStore"/> (<see cref="IMasterTrustAnchors"/>), with <see cref="CaRotationService"/> hosted over
    /// the same master certificates and rotation service; the node cache reports <c>ca_rotation_pending</c>; jobs scope
    /// their windows by <see cref="NodeScopedMaintenancePolicy"/>. With a <see cref="MasterBoot"/> in recovery mode
    /// registered first (design §9.4, D76) nothing touches the PKI directory (an in-memory CA, never written), the
    /// registry seeds nothing, and <c>/v1</c> answers 503 "restoring" (<see cref="RecoveryMaintenanceWindow"/>); the host
    /// removes the background services.</para>
    /// </summary>
    public static IServiceCollection AddMirbadLLMOrchestration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<MirbadLLMOptions>()
            .Bind(configuration.GetSection(MirbadLLMOptions.SectionName));

        services.AddOptions<MetricsOptions>()
            .Bind(configuration.GetSection(MirbadLLMOptions.SectionName));

        services.AddOptions<MasterPkiOptions>()
            .Bind(configuration.GetSection(MasterPkiOptions.SectionName));

        // Before the first registry load: the v1 → v2 migration runs once and reads these (W1 review MN-090).
        RegistryMigrationOptions.AddRegistryMigrationOptions(services, configuration);

        services.AddOptions<RecoveryOptions>().Bind(configuration.GetSection(RecoveryOptions.SectionName));
        MasterBoot boot = MasterBoot.Of(services);
        if (boot.IsRecovery)
        {
            // Recovery mode (D76): a missing models.json stays empty until the backup is installed.
            services.Configure<ModelRegistryOptions>(o => o.SeedPolicy = ModelSeedPolicy.None);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddMasterFencing();

        // Placement validation and ownership (MN-108, composed by the W7 review MN-126): the ownership store and, in this
        // order, the ownership and placement validators run under the registry lock on every save and delete (D76). A
        // single-DGX save that keeps its placement is not re-validated and keeps generation 0, so P1 saves are unchanged.
        services.AddPlacementValidation();
        services.AddSingleton<IModelRegistry>(sp => new JsonModelRegistry(
            sp.GetRequiredService<IOptions<MirbadLLMOptions>>(),
            sp.GetRequiredService<IOptions<RegistryMigrationOptions>>(),
            sp.GetServices<IModelDefinitionValidator>(),
            sp.GetService<IOptions<ModelRegistryOptions>>()));
        services.AddSingleton<IApiKeyStore, JsonApiKeyStore>();
        services.AddDataProtection(); // the host may configure it further (Program.cs persists its keys under DataPath)
        services.AddSingleton<ITokenStore, JsonTokenStore>();

        // The Overview's game layer: persisted fleet counters ({DataPath}/fleet-stats.json) fed from the router, the metrics
        // history and crash records, and the targets its service levels are measured against.
        services.AddOptions<OverviewOptions>().Bind(configuration.GetSection(OverviewOptions.SectionName));
        services.AddSingleton<FleetStatsStore>();
        services.AddHostedService<FleetStatsCollector>();

        // One JsonNodeRegistry (fixed temp-file name), INodeRegistry forwarding to it (W1 review MN-090).
        services.AddSingleton<JsonNodeRegistry>();
        services.AddSingleton<INodeRegistry>(sp => sp.GetRequiredService<JsonNodeRegistry>());

        AddMasterPki(services, boot);
        AddAgents(services);

        services.AddSingleton<StartupTimeTracker>();

        // The fit preview (MN-108), composed once with the agent clients (MN-233): a node reporting gguf.inspect answers the
        // GGUF summary, so GET/POST /api/nodes/{id}/fit and the editor carry NodeFitResult.Estimate (design-p3p4 §A.1.10,
        // MN-224). The endpoints and the Razor pages prefer this instance over building their own, which had no agents.
        services.TryAddSingleton(sp => new PlacementFit(
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetService<INodeTelemetry>(),
            sp.GetService<INodeStatusCache>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IAgentClientFactory>()));

        // Monitoring: history keyed by node, filled from the agents' telemetry; remote IMemoryProbe/IGpuProbe/IGpuProcessProbe.
        services.AddSingleton<InMemoryMetricsHistory>();
        services.AddSingleton<IMetricsHistory>(sp => sp.GetRequiredService<InMemoryMetricsHistory>());
        services.AddSingleton<IModelMetricsReader, HistoryModelMetricsReader>();
        services.AddRemoteTelemetry();

        // Model sourcing on the bootstrap node (P1 has one node): downloads, folders and the vLLM catalog through its agent.
        services.AddHttpClient(HuggingFaceHttpClientName, client =>
        {
            client.BaseAddress = new Uri("https://huggingface.co/");
        });
        services.AddSingleton<IHuggingFaceDownloader>(sp => new RemoteHuggingFaceDownloader(
            BootstrapNodeId(sp), sp.GetRequiredService<IAgentClientFactory>(), sp.GetRequiredService<AgentOperationRunner>()));
        services.AddSingleton<IHuggingFaceRepoInspector>(sp => new HuggingFaceRepoInspector(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HuggingFaceHttpClientName)));
        services.AddSingleton(sp => new RemoteModelFolders(
            sp.GetRequiredService<IAgentClientFactory>(), sp.GetRequiredService<AgentOperationRunner>(), sp.GetRequiredService<IModelRegistry>()));
        services.AddSingleton(sp => new RemoteVllmCatalogService(
            sp.GetRequiredService<INodeRegistry>(), sp.GetRequiredService<IOptions<MirbadLLMOptions>>(), sp.GetService<INodeStatusCache>()));

        // The bootstrap node shares the master's host while the master writes its bootstrap bundle (topology a; P5 turns
        // bootstrap off): its benchmark runs are labelled agent-mtls-loopback although its address is a container name.
        // Per-node jobs (MN-111): MirbadLLM:Jobs:MaxConcurrent (default 2, D15) across nodes, one per node.
        services.AddOptions<JobOptions>().Bind(configuration.GetSection(JobOptions.SectionName));
        AddJobs(services, coLocatedBootstrapNode: configuration.GetValue("MirbadLLM:Enrollment:BootstrapEnabled", defaultValue: true));
        if (boot.IsRecovery)
        {
            // Every /v1 call answers 503 maintenance "restoring" through the router's global gate (D76, D79).
            services.RemoveAll<IMaintenanceMode>();
            services.AddSingleton<IMaintenanceMode>(sp => new RecoveryMaintenanceWindow(sp.GetRequiredService<TimeProvider>()));
        }

        return services;
    }

    /// <summary>The bootstrap node id (<c>MirbadLLM:Nodes:BootstrapNodeId</c>, default <c>dgx</c>): P1's only node.</summary>
    public static string BootstrapNodeId(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetRequiredService<IOptions<RegistryMigrationOptions>>().Value.EffectiveBootstrapNodeId;
    }

    private static void AddMasterPki(IServiceCollection services, MasterBoot boot)
    {
        services.AddSingleton(sp =>
        {
            var pki = sp.GetRequiredService<IOptions<MasterPkiOptions>>().Value;
            string directory = MasterPkiOptions.DirectoryOf(sp.GetRequiredService<IOptions<MirbadLLMOptions>>().Value.DataPath);
            var time = sp.GetRequiredService<TimeProvider>();
            if (boot.IsRecovery)
            {
                // No CA generation in recovery mode (D76): the configured CA, or a throw-away one that is never written. The
                // backup's CA arrives with the install, and the restart loads it.
                return !string.IsNullOrWhiteSpace(pki.CaCertPem) && !string.IsNullOrWhiteSpace(pki.CaKeyPem)
                    ? CertificateAuthority.Load(pki.CaCertPem, pki.CaKeyPem, time)
                    : CertificateAuthority.Create(time);
            }

            // CA rotation step 3 boot repair before the CA loads (MN-116): a promotion a crash split is completed first.
            MasterTrustStore.RecoverCaFiles(directory);
            return CaStore.LoadOrCreate(directory, pki.CaCertPem, pki.CaKeyPem, time);
        });

        // The host registers the one MasterCertificates first, with the enroll listener's addresses (W3 review MN-094).
        services.TryAddSingleton(sp => MasterCertificates.LoadOrIssue(
            sp.GetRequiredService<CertificateAuthority>(),
            MasterPkiOptions.DirectoryOf(sp.GetRequiredService<IOptions<MirbadLLMOptions>>().Value.DataPath),
            enrollAddresses: null,
            sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton(sp => new CertificateRotationService(
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<AgentClientFactory>(),
            sp.GetRequiredService<CertificateAuthority>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<CertificateRotationService>>(),
            options: null,
            sp.GetRequiredService<MasterCertificates>()));

        // The master's trust anchors (pki/trust.json; absent = the boot CA alone, written only by a CA rotation) and the CA
        // rotation procedure over the same master certificates and rotation service (MN-116, D80, D86).
        services.AddSingleton(sp => MasterTrustStore.Load(
            MasterPkiOptions.DirectoryOf(sp.GetRequiredService<IOptions<MirbadLLMOptions>>().Value.DataPath),
            sp.GetRequiredService<CertificateAuthority>().Certificate,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IMasterTrustAnchors>(sp => sp.GetRequiredService<MasterTrustStore>());
        services.AddSingleton(sp => new CaRotationService(
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<AgentClientFactory>(),
            sp.GetRequiredService<MasterTrustStore>(),
            sp.GetRequiredService<MasterCertificates>(),
            sp.GetRequiredService<CertificateRotationService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IAuditLog>(),
            sp.GetRequiredService<ILogger<CaRotationService>>(),
            options: null,
            pki: sp.GetRequiredService<IOptions<MasterPkiOptions>>().Value));
    }

    private static void AddAgents(IServiceCollection services)
    {
        // Agents are validated under every anchor of the trust store (both roots during a CA rotation, MN-116).
        services.AddSingleton(sp => new AgentClientFactory(
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<IMasterIdentity>(),
            sp.GetRequiredService<MasterCertificates>().ClientCredential,
            sp.GetRequiredService<IMasterTrustAnchors>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AgentClientFactory>>()));
        services.AddSingleton<IAgentClientFactory>(sp => sp.GetRequiredService<AgentClientFactory>());

        services.AddSingleton<JsonAgentOperationReferenceStore>(sp => new JsonAgentOperationReferenceStore(
            sp.GetRequiredService<IOptions<MirbadLLMOptions>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAgentOperationReferenceStore>(sp => sp.GetRequiredService<JsonAgentOperationReferenceStore>());
        services.AddSingleton(sp => new AgentOperationRunner(
            sp.GetRequiredService<IAgentClientFactory>(),
            sp.GetRequiredService<IAgentOperationReferenceStore>(),
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AgentOperationRunner>>()));

        services.AddSingleton(sp =>
        {
            // Degraded ca_rotation_pending while an open CA rotation has not reached the node (MN-116); never on P1.
            var trust = sp.GetRequiredService<MasterTrustStore>();
            return new NodeStatusCache(
                sp.GetRequiredService<IAgentClientFactory>(),
                sp.GetRequiredService<IMasterIdentity>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IModelRegistry>(),
                sp.GetRequiredService<ILogger<NodeStatusCache>>(),
                options: null,
                degradedReason: trust.PendingReason);
        });
        services.AddSingleton<INodeStatusCache>(sp => sp.GetRequiredService<NodeStatusCache>());

        services.AddSingleton<ModelRecoveryState>();
        services.AddSingleton<OperatorStops>();

        // With the runner and the reference store: it closes launch references and routes stale_master from any caller to
        // the node state (W3 review MN-094, D52).
        services.AddSingleton(sp => new NodeMonitor(
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<NodeStatusCache>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AgentOperationRunner>(),
            sp.GetRequiredService<IAgentOperationReferenceStore>(),
            sp.GetRequiredService<ILogger<NodeMonitor>>(),
            sp.GetRequiredService<ModelRecoveryState>()));
        services.AddSingleton(sp => new NodeRegistrySync(
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<AgentClientFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<NodeMonitor>(),
            interval: null,
            sp.GetRequiredService<ILogger<NodeRegistrySync>>()));

        services.AddSingleton(sp => new RemoteModelRuntime(
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<INodeStatusCache>(),
            sp.GetRequiredService<IAgentClientFactory>(),
            sp.GetRequiredService<AgentOperationRunner>(),
            sp.GetRequiredService<IOptions<MirbadLLMOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RemoteModelRuntime>>(),
            runtimeOptions: null,
            // The same preview instance the editor and /api/nodes/{id}/fit use, so a llama.cpp launch's host budget is
            // the number the operator was shown and the GGUF read is usually already cached (MN-239-17, item 12).
            sp.GetRequiredService<PlacementFit>(),
            sp.GetRequiredService<ModelRecoveryState>()));
        services.AddSingleton<IModelRuntime>(sp => sp.GetRequiredService<RemoteModelRuntime>());
        services.AddSingleton<IModelLauncher>(sp => sp.GetRequiredService<RemoteModelRuntime>());

        // Hosted-service order matters (they start in registration order): the factory knows every enrolled node before the
        // monitor's loops run, and the monitor runs before the health monitor's boot auto-start, which waits for the
        // bootstrap node's first snapshot (W3 review MN-094, D51).
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<NodeRegistrySync>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<NodeMonitor>());
        services.AddSingleton<IHostedService>(sp => new ModelHealthMonitor(
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetRequiredService<IModelRuntime>(),
            sp.GetRequiredService<IOptions<MirbadLLMOptions>>(),
            sp.GetRequiredService<ILogger<ModelHealthMonitor>>(),
            sp.GetRequiredService<StartupTimeTracker>(),
            sp.GetRequiredService<IOptions<RegistryMigrationOptions>>(),
            sp.GetRequiredService<ModelRecoveryState>(),
            sp.GetRequiredService<INodeStatusCache>(),
            sp.GetRequiredService<IOwnershipStore>(),
            sp.GetRequiredService<OperatorStops>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CertificateRotationService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CaRotationService>());
    }

    private static void AddJobs(IServiceCollection services, bool coLocatedBootstrapNode)
    {
        // Benchmarks and tuning: one job at a time, the router's maintenance window, results on disk; the load, the GPU idle
        // check and boot recovery go through the agents (MN-033).
        services.AddHttpClient(BenchmarkHttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.AddSingleton<MaintenanceModeService>();
        services.AddSingleton<IMaintenanceMode>(sp => sp.GetRequiredService<MaintenanceModeService>());
        services.AddSingleton<BenchmarkStore>();
        services.AddSingleton(new BenchmarkSuiteOptions());
        services.AddSingleton(new ModelJobOptions());
        services.AddSingleton(sp => new NodeScopedMaintenancePolicy(
            NodeScopedMaintenancePolicy.ReadConfigured(sp.GetService<IConfiguration>()), sp.GetRequiredService<INodeRegistry>()));
        services.AddSingleton(sp => new AgentJobTargets(
            sp.GetRequiredService<IAgentClientFactory>(),
            sp.GetRequiredService<INodeStatusCache>(),
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetRequiredService<IAgentOperationReferenceStore>(),
            sp.GetRequiredService<TimeProvider>(),
            new AgentJobTargetsOptions { CoLocatedNodeIds = coLocatedBootstrapNode ? [BootstrapNodeId(sp)] : [] }));
        services.AddSingleton<IBenchmarkTargetResolver>(sp => sp.GetRequiredService<AgentJobTargets>());
        services.AddSingleton<IGpuIdleSampler>(sp => sp.GetRequiredService<AgentJobTargets>());
        services.AddSingleton<IJobRecoveryGate>(sp => sp.GetRequiredService<AgentJobTargets>());
        services.AddSingleton<IBenchmarkProvenanceSource>(sp => new NodeBenchmarkProvenanceSource(
            sp.GetRequiredService<INodeStatusCache>(), sp.GetRequiredService<IModelRegistry>(), BootstrapNodeId(sp)));
        services.AddSingleton<IBenchmarkSuite>(sp => new BenchmarkSuite(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(BenchmarkHttpClientName),
            sp.GetRequiredService<IMetricsHistory>(),
            sp.GetRequiredService<BenchmarkSuiteOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            random: null,
            sp.GetRequiredService<IBenchmarkTargetResolver>()));
        services.AddSingleton(sp =>
        {
            var folders = sp.GetRequiredService<RemoteModelFolders>();
            return new ModelJobService(
                sp.GetRequiredService<IModelRegistry>(),
                sp.GetRequiredService<IModelRuntime>(),
                sp.GetRequiredService<IModelLauncher>(),
                sp.GetRequiredService<IBenchmarkSuite>(),
                sp.GetRequiredService<BenchmarkStore>(),
                sp.GetRequiredService<MaintenanceModeService>(),
                sp.GetRequiredService<IMetricsHistory>(),
                sp.GetRequiredService<IGpuProbe>(),
                sp.GetRequiredService<StartupTimeTracker>(),
                sp.GetRequiredService<IOptions<MirbadLLMOptions>>(),
                sp.GetRequiredService<ModelJobOptions>(),
                sp.GetRequiredService<ILogger<ModelJobService>>(),
                sp.GetRequiredService<IBenchmarkTargetResolver>(),
                inspect: null,
                timeProvider: sp.GetRequiredService<TimeProvider>(),
                gpuIdle: sp.GetRequiredService<IGpuIdleSampler>(),
                limits: sp.GetRequiredService<IOptions<JobOptions>>().Value,
                bootstrapNodeId: BootstrapNodeId(sp),
                ownership: sp.GetRequiredService<IOwnershipStore>(),
                // One switch for node-scoped maintenance, shared with the router (D83), decided per job by the policy (D101):
                // unset = node-scoped only with two or more nodes, so the single DGX keeps P1's global windows.
                nodeScopedMaintenance: false,
                inspectOnNode: (node, folder) => InspectRemoteFolder(folders, node, folder),
                // Provenance and node/GPU-scoped noise of every run (MN-112).
                provenance: sp.GetRequiredService<IBenchmarkProvenanceSource>(),
                nodeScopedPolicy: sp.GetRequiredService<NodeScopedMaintenancePolicy>(),
                // A llama.cpp model's GGUF header for its tuning candidates' memory checks (MN-225); never called for vLLM.
                inspectGguf: (node, model) => InspectRemoteGguf(
                    sp.GetRequiredService<IAgentClientFactory>(), sp.GetRequiredService<ILogger<ModelJobService>>(), node, model));
        });
        services.AddSingleton<IHostedService>(sp => new ModelJobRecoveryService(
            sp.GetRequiredService<ModelJobService>(), sp.GetRequiredService<IJobRecoveryGate>()));
    }

    /// <summary>
    /// The tuning gates' synchronous folder inspection through the node's agent. Jobs run on background threads, so the
    /// blocking read is acceptable there (never call it from a render path); an unreachable node reads as "no inspection",
    /// as an unreadable local folder did.
    /// </summary>
    private static ModelFolderInspection? InspectRemoteFolder(RemoteModelFolders folders, string nodeId, string folder)
    {
        try
        {
            return Task.Run(() => folders.InspectAsync(nodeId, folder)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// A llama.cpp model's GGUF summary through its node's agent (MN-225), blocking as <see cref="InspectRemoteFolder"/> (jobs
    /// run on background threads). Null when the model file is not addressable by the route (not directly inside a top-level
    /// folder), the node does not offer it, or the read fails: the candidates' memory is then not checked.
    /// <para><b>Bounded, and it says why it gave up</b> (MN-239 item 13). The swallow itself is correct and stays — D130
    /// made "not checked" a designed degradation and <c>LlamaCppTuning.Fit</c> prints it in the plan — but the agent
    /// layer has only a 5 s <i>connect</i> timeout, so a node that accepted the connection and then never answered held
    /// the tuning thread for as long as it liked. <see cref="TuningGgufTimeout"/> bounds the whole read, and one debug
    /// line names which of the three cases it was.</para>
    /// </summary>
    internal static GgufSummary? InspectRemoteGguf(
        IAgentClientFactory clients, ILogger logger, string nodeId, ModelDefinition model, TimeSpan? readTimeout = null)
    {
        if (LlamaCppTuning.GgufLocation(model) is not { } location)
        {
            Log.TuningGgufNotAddressable(logger, model.Id, nodeId, model.LlamaCpp?.ModelFile);
            return null;
        }

        var limit = readTimeout ?? TuningGgufTimeout;
        using var timeout = new CancellationTokenSource(limit);
        try
        {
            return Task.Run(
                    () => clients.GetClient(nodeId).InspectGgufAsync(location.Folder, location.File, timeout.Token),
                    timeout.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Log.TuningGgufTimedOut(logger, model.Id, nodeId, limit.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.TuningGgufUnread(logger, model.Id, nodeId, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// How long a tune waits for one node's GGUF header before giving up on the candidates' memory check (MN-239 item
    /// 13). Longer than the interactive <see cref="PlacementFit.GgufTimeout"/> — a job is not a render path — but
    /// finite, which is the point: a header read on a healthy node is milliseconds.
    /// </summary>
    internal static readonly TimeSpan TuningGgufTimeout = TimeSpan.FromSeconds(15);
}
