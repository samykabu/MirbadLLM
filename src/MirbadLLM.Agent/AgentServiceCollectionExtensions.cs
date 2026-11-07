using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Api;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.HostServices;
using MirbadLLM.Agent.Updates;
using MirbadLLM.Agent.HostServices.Windows;
using MirbadLLM.Agent.Operations;
using MirbadLLM.Agent.Pki;
using MirbadLLM.Agent.Proxy;
using MirbadLLM.Agent.Recovery;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Downloads.Native;
using MirbadLLM.NodeRuntime.Gguf;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.NodeRuntime.Runtimes.Process;
using MirbadLLM.NodeRuntime.Runtimes.Process.Mac;
using MirbadLLM.NodeRuntime.Runtimes.Process.Windows;

namespace MirbadLLM.Agent;

/// <summary>
/// The agent composition (MN-026, design §2.5, §4): every agent package registered once, over <b>one</b> boot id, one
/// operation log, one fencing state, one event bus and one <see cref="ReservationManager"/>, with every recovery handler
/// and operation canceller in place before <see cref="AgentReconciler.RunAsync"/> runs. <c>Program</c> calls, in order:
/// <code>
/// AgentHostBuilder.Configure(builder, options.ToHostOptions());   // listener, fencing, credentials (MN-014)
/// builder.Services.AddMirbadLLMAgent(options, boot);            // everything else
/// WindowsServiceHosting.Configure(builder);                      // a no-op outside the SCM (MN-215)
/// var app = builder.Build();
/// … wait for Docker (bounded, Docker on only), adopt the supervised processes, reconcile, read the feature list …
/// app.MapMirbadLLMAgent();                                      // pipeline + every route
/// await app.RunAsync();                                          // the listener opens here
/// </code>
/// Seams for tests and the dev setup (MN-042) are the services registered with <c>TryAdd</c> by the packages (memory
/// probe, <c>nvidia-smi</c>, Hugging Face downloader/inspector, image engine, upstream resolver, and on a process node
/// the process host, the instance-key protector and the service control manager): register a replacement before calling
/// <see cref="AddMirbadLLMAgent"/>. Two are <b>not</b> seams of that kind (MN-234 corrected this list): on a process
/// node <c>AddScmHostServices</c> <c>Replace</c>s the <c>IHostdClient</c>, the host-service operations and the queries,
/// so a pre-registered hostd fake is discarded there; and neither runtime options record is in DI at all — both are
/// handed to their driver through <c>Options.Create</c>. With Docker <b>on</b>, an <c>IRuntimeDriver</c> registered
/// before replaces the container driver; with Docker <b>off</b> the process driver is <i>added</i> next to it, which is
/// what MN-236's Windows dev setup wants (the fake runtime beside the real process driver) and what the per-instance
/// upstream resolver exists for.
/// <para><b>P3/P4 (MN-232, design-p3p4 §A.4).</b> One switch decides it all: <c>Agent:Docker:Enabled</c>
/// (<see cref="DockerAvailability"/>). With Docker <b>on</b> — every Linux agent and the DGX — the composition is P2's,
/// registration for registration, so the DGX's capability report and container launches are unchanged. With Docker
/// <b>off</b> (the Windows PC, <c>agent.windows.example.json</c>) the composition instead registers the
/// <see cref="ProcessRuntimeDriver"/> over <see cref="WindowsProcessHost"/>, <see cref="ProcessRecordStore"/>,
/// <see cref="InstanceKeyProtector"/>, <see cref="ExecutableCatalog"/> (with its background verifier),
/// <see cref="NvidiaSmiGpuStartMeter"/>, <see cref="GgufReader"/>, the native downloader, the Windows probes, the SCM
/// host services and the P3/P4 contributors — and <b>no</b> Docker client, container driver, image engine, Docker
/// contributor, Docker feature, <c>docker_unreachable</c> reason, startup Docker wait or
/// <see cref="DownloadHelperCleanup"/>. Every <c>[SupportedOSPlatform("windows")]</c> type sits behind
/// <see cref="OperatingSystem.IsWindows"/>; the platform-neutral rest needs only an <see cref="IProcessHost"/> and an
/// <see cref="IInstanceKeyProtector"/> — plus an <see cref="IServiceControlManager"/> for the SCM half — which is how
/// the process composition is exercised on Linux too.
/// <para>Two precisions MN-234 added. The native downloader and <c>downloads.hf-native</c> are the one P3 pair gated on
/// the Docker switch rather than on process delivery: a download needs a disk and a Hub client, not a runtime driver,
/// and it is the only downloader a node without Docker can run. And a node with Docker off that supplies neither a
/// process host nor a key protector composes no runtime driver at all; it starts (it enrolls, reports and serves host
/// services) and says so once in its log, because refusing to start would also refuse the Linux leg that proves these
/// guards.</para>
/// <para>Two MN-239 added. <see cref="WindowsMemoryProbe"/> is registered on <see cref="OperatingSystem.IsWindows"/>
/// alone, not behind the Docker switch: the host memory reader follows the operating system, and the <c>TryAdd</c>
/// default it replaces reads <c>/proc/meminfo</c> (item 15). And on a node whose host services go through the SCM the
/// Linux host-service package is configured with systemd units and the backup mount <b>off</b>, so the hostd
/// contributor cannot report <c>services.systemd</c>, <c>gpu-processes</c>, <c>backup-mount</c> or <c>hostd_missing</c>
/// beside <c>services.scm</c> however <c>agent.json</c> is edited (item 16); <c>Agent:ManageSystemdUnits</c>,
/// <c>Agent:ManageBackupMount</c> and <c>Agent:HostdExpected</c> are therefore ignored on such a node.</para>
/// </summary>
public static class AgentServiceCollectionExtensions
{
    /// <summary>Named <see cref="HttpClient"/> of the Docker Engine API (model containers, images, download helpers).</summary>
    public const string DockerHttpClientName = "mirbadllm-agent.docker";

    /// <summary>Named <see cref="HttpClient"/> of the runtime driver's <c>/health</c> probe to the models.</summary>
    public const string UpstreamHttpClientName = "mirbadllm-agent.upstream";

    /// <summary>Named <see cref="HttpClient"/> of the Hugging Face Hub API (repo listing before a download).</summary>
    public const string HuggingFaceHttpClientName = "mirbadllm-agent.huggingface";

    /// <summary>
    /// Named <see cref="HttpClient"/> the native downloader uses for the Hub and its CDN (design-p3p4 §A.8): a
    /// <see cref="SocketsHttpHandler"/> with <see cref="SocketsHttpHandler.AllowAutoRedirect"/> <b>false</b>, because
    /// <see cref="SecureDownloadTransport"/> follows redirects itself so that every hop is checked by the HTTPS rule
    /// before it is requested and the token never leaves the Hub's own origin.
    /// </summary>
    public const string NativeDownloadHttpClientName = "mirbadllm-agent.huggingface-native";

    /// <summary>
    /// Registers the agent packages (everything but the listener, which <see cref="AgentHostBuilder.Configure"/> adds):
    /// operation log (MN-013), events (MN-017), reservations (MN-016), the node's runtime driver — the container one
    /// (MN-015) with Docker on, the process one (MN-211) with Docker off — runtime and operation endpoints with the
    /// watcher (MN-031), proxy (MN-019), telemetry (MN-018), storage (MN-020), host services (MN-023, over the SCM on a
    /// process node), PKI rotation (MN-036) and reconciliation (MN-025), plus the graceful-shutdown drain.
    /// </summary>
    public static IServiceCollection AddMirbadLLMAgent(this IServiceCollection services, AgentOptions options, AgentBootInfo boot)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(boot);
        options.Validate();

        // The one P3/P4 switch (design-p3p4 §A.4.3): Docker off = the Windows PC, which runs models as native processes.
        DockerAvailability docker = DockerAvailability.From(options);

        // One boot id for /info, /status, /events, /telemetry and every operation-log line (W2 review MN-092 item 2).
        services.Replace(ServiceDescriptor.Singleton(boot));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(options);
        // Replace, not TryAdd (MN-234): every gate in this method reads the local `docker`, while MapMirbadLLMAgent's
        // /images/pull refusal and HostServicesServiceCollectionExtensions.ContainersOrNull read it back out of DI. A
        // pre-registered one (the class remarks invite pre-registration) could disagree with the composition and re-open
        // D129 (3) on a node that has no Docker client at all.
        services.Replace(ServiceDescriptor.Singleton(docker));

        // Registered first so it stops last: requests and the other hosted services are gone before it drains.
        services.AddHostedService<AgentDrain>();

        AddDocker(services, options, docker);

        // The host memory probe follows the operating system, not the delivery (MN-239 item 15): /proc/meminfo does not
        // exist on Windows, so without this a Windows node with Docker on would keep AddAgentTelemetry's TryAdd default
        // and answer /status, admission and the capability report from a reader that cannot work there. Before
        // AddProcessRuntime and AddAgentTelemetry, whose default it takes the place of.
        if (OperatingSystem.IsWindows())
        {
            AddWindowsMemoryProbe(services);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Nor on macOS, where the default would answer with the dev-box placeholder figures.
            services.TryAddSingleton<IMemoryProbe>(_ => new MacMemoryProbe());
            // Apple GPU utilisation from IOKit (Phase 4, M8), before AddAgentTelemetry, whose TryAdd default is nvidia-smi.
            // A registered nvidia-smi stand-in (the in-process test and dev agents that play a DGX or a PC on a Mac) keeps
            // the nvidia-smi probe over it.
            services.TryAddSingleton<IGpuTelemetryProbe>(sp => sp.GetService<INvidiaSmiRunner>() is { } runner and not NvidiaSmiProcessRunner
                ? ActivatorUtilities.CreateInstance<NvidiaSmiGpuTelemetryProbe>(sp)
                : AppleGpuTelemetryProbe.ForThisMac(sp.GetRequiredService<ILogger<AppleGpuTelemetryProbe>>()));
        }

        // Process delivery, before the packages whose TryAdd defaults it replaces (the GPU-process probe in
        // AddAgentTelemetry) and before the reservation manager, whose AdmissionOptions it completes.
        bool processNode = AddProcessRuntime(services, options, docker);

        services.AddAgentOperationLog(sp => new OperationLogOptions
        {
            StateDirectory = options.StateDirectory,
            BootId = boot.BootId,
            NodeUid = sp.GetRequiredService<IAgentCredentials>().NodeUid,
        });
        services.AddAgentEvents(boot.BootId);

        // The node's memory model, decided once from the GPU probe (P2 composition MN-130, D96/D101): it selects admission,
        // the /status reader and the P2 capability flags. A unified node (the DGX) keeps P1's reader, admission and features.
        services.TryAddSingleton<AgentMemoryModel>();
        services.TryAddSingleton<INodeMemoryReader>(sp => sp.GetRequiredService<AgentMemoryModel>().IsDiscrete
            ? sp.GetRequiredService<DiscreteNodeMemoryReader>() // per-GPU memory in /status (MN-104)
            : new ProbeNodeMemoryReader(sp.GetRequiredService<IMemoryProbe>(), MemoryModel.Unified));
        services.TryAddSingleton(sp => new AdmissionNodeMemory(
            sp.GetRequiredService<IMemoryProbe>(), sp.GetRequiredService<IGpuTelemetryProbe>(), sp.GetRequiredService<TimeProvider>()));

        // One reservation manager, built with the event bus (its only publisher, D46) and without persistence: MN-031
        // persists a reservation in the launch's admitted stage (W2 review MN-092 item 1). It is also the driver's port
        // allocator; the instance source resolves the drivers on first use to break the driver ↔ allocator cycle. On a
        // discrete node it admits per GPU and host (MN-105) over a dedicated reader without reuse window (D96).
        AdmissionOptions admission = options.ToAdmissionOptions();
        if (processNode)
        {
            // D127: a port another program on the PC already holds is skipped under the admission lock. Null on the DGX.
            // ProcessDelivery words a start conflict for llama.cpp instead of vLLM (MN-239 H2); false on the DGX.
            admission = admission with { PortInUse = ProcessPorts.IsTaken, ProcessDelivery = true };
        }

        services.TryAddSingleton(sp =>
        {
            bool discrete = sp.GetRequiredService<AgentMemoryModel>().IsDiscrete;
            return new ReservationManager(
                admission with
                {
                    MemoryModel = discrete ? MemoryModel.Discrete : MemoryModel.Unified,
                    // The reserves of every launch are learned and persisted on a process node (D129 (2)); null elsewhere.
                    OnAdmission = processNode ? LearnReserves(sp) : null,
                },
                sp.GetRequiredService<IMemoryProbe>(),
                new DeferredRuntimeInstanceSource(sp),
                persistence: null,
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<ReservationManager>>(),
                nodeMemory: discrete ? sp.GetRequiredService<AdmissionNodeMemory>().Reader : null);
        });
        services.TryAddSingleton<IReservationManager>(sp => sp.GetRequiredService<ReservationManager>());
        services.TryAddSingleton<IPortAllocator>(sp => sp.GetRequiredService<ReservationManager>());

        // The container driver, the Docker-only part of the composition: never registered on a node without Docker (its
        // DockerEngineClient does not exist there either), so a launch for a container spec is refused 422 spec_rejected
        // by LaunchSpecGuard instead of failing inside the engine.
        if (docker.Enabled)
        {
            ContainerRuntimeOptions runtime = options.ToContainerRuntimeOptions();
            services.TryAddSingleton(sp => new ContainerRuntimeDriver(
                sp.GetRequiredService<DockerEngineClient>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(UpstreamHttpClientName),
                sp.GetRequiredService<IPortAllocator>(),
                Options.Create(runtime),
                sp.GetRequiredService<ILogger<ContainerRuntimeDriver>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetService<IGpuInventory>())); // GPU selection by UUID and gpu_missing (MN-106)
            // A driver registered before (the dev setup's fake runtime, MN-042) replaces the container driver.
            if (!services.Any(d => d.ServiceType == typeof(IRuntimeDriver)))
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IRuntimeDriver, ContainerRuntimeDriver>(sp => sp.GetRequiredService<ContainerRuntimeDriver>()));
            }
        }

        services.AddAgentRuntimes();
        // RuntimeDriverUpstreamResolver asks every driver and remembers which one owns a model (MN-230, D130), so a
        // process model's upstream resolves on a node that also has a container driver.
        services.AddAgentProxy(proxy =>
        {
            proxy.MaxInFlight = options.ProxyMaxInFlight;
            proxy.UpstreamIdleTimeout = options.ProxyUpstreamIdleTimeout;
        });
        // An explicit Agent:MemoryModel forces the memory model (the DGX: Unified, D102); else the GPU probe decides.
        services.AddAgentTelemetry(boot.BootId, options.MemoryModel is { } forced ? t => t.MemoryModel = forced : null);

        AddDownloads(services, options, docker);
        services.AddAgentStorage(options.ConfigureStorage, docker);

        // Decided before the Linux host-service package is added, because it decides how that package is configured.
        bool scmHostServices = UsesScmHostServices(services, processNode);
        services.AddAgentHostServices(hostServices =>
        {
            options.ConfigureHostServices(hostServices);
            if (scmHostServices)
            {
                // MN-239 item 16: a node whose host services go through the SCM has no systemd, no hostd socket and no
                // backup mount, so the Linux HostServicesContributor must stay silent there whatever agent.json asks
                // for. It stayed silent only because agent.windows.example.json turns all three off: flip
                // Agent:ManageSystemdUnits on the Windows PC and it added services.systemd and gpu-processes beside
                // services.scm, overwrote the SCM contributor's HostServices capability with manager "systemd", and
                // opened the hostd-worded health path (hostd_missing) over ScmHostClient. The contributor stays
                // registered — it is the DGX's — and is simply told the truth about this node.
                hostServices.Systemd = false;
                hostServices.BackupMount = false;
                hostServices.HostdExpected = false;
            }
            else if (OperatingSystem.IsMacOS() && options.ManageLaunchdJobs)
            {
                // The macOS hostd drives launchd over the same socket API (Phase 4, M10): services.launchd instead of
                // services.systemd, no gpu-processes (no per-process GPU accounting on a Mac) and no backup mount
                // (backups live on the master). Only with Agent:ManageLaunchdJobs: without it agent.macos.example.json
                // already turns units and the backup mount off, and in-process agents playing a DGX on a Mac keep theirs.
                hostServices.Manager = MirbadLLM.Agent.Contracts.HostServiceManagers.Launchd;
                hostServices.Systemd = true;
                hostServices.BackupMount = false;
            }
        });
        AddScmHostServices(services, options, scmHostServices);

        // The pushed agent update (docs/multinode/agent-updates.md): the route always, agent.update only where the
        // installer put the root update helper (never the DGX container).
        services.AddAgentUpdates(options.StateDirectory);

        // Runtime capabilities and the docker_unreachable health reason: no other package reports them. With Docker off
        // neither is registered: the probe would need a Docker client, and the node reports no container runtime at all.
        if (docker.Enabled)
        {
            services.TryAddSingleton<DockerProbe>();
            services.TryAddSingleton(sp => new RuntimeCapabilityContributor(
                sp.GetRequiredService<DockerProbe>(), options.EnableNim && options.ApprovedImages.Count > 0));
            services.AddSingleton<ICapabilityContributor>(sp => sp.GetRequiredService<RuntimeCapabilityContributor>());
            services.AddSingleton<IHealthContributor>(sp => sp.GetRequiredService<RuntimeCapabilityContributor>());
        }

        // The P2 capability flags (MN-130), each from its own task's contributor, after the telemetry contributor (it sets
        // the memory model they read). Discrete nodes only: a unified node reports P1's features exactly (D101).
        // The report's memory model is the process decision (W9 review MN-132, D102), then the flags gated on it.
        services.AddSingleton<ICapabilityContributor>(sp => new MemoryModelReportContributor(
            sp.GetRequiredService<AgentMemoryModel>(), sp.GetRequiredService<ILogger<AgentMemoryModel>>()));
        services.AddSingleton<ICapabilityContributor>(sp => new DiscreteNodeContributor(
            new LaunchFeaturesContributor(sp.GetServices<IRuntimeDriver>()), sp.GetRequiredService<AgentMemoryModel>()));
        services.AddSingleton<ICapabilityContributor>(sp => new DiscreteNodeContributor(
            new GpuSelectionContributor(sp.GetServices<IRuntimeDriver>()), sp.GetRequiredService<AgentMemoryModel>()));
        services.AddSingleton<ICapabilityContributor>(sp => new DiscreteAdmissionContributor(sp.GetRequiredService<ReservationManager>()));

        // Apple Silicon (docs/macos/implementation-plan.md, Phase 1): the chip name and Metal's GPU working-set size,
        // after the memory model is decided (it only applies to a unified node).
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<ICapabilityContributor>(MacCapabilityContributor.ForThisMac());
        }

        // The P3/P4 capability flags and health reasons (design-p3p4 §A.9, D116/D130), only on a process node: the DGX
        // reports none of AgentFeatures.P3Reported.
        if (processNode)
        {
            // runtime.llamacpp.process with its versions (MN-222) and admission.gpu-budgets (MN-224).
            services.AddSingleton<ICapabilityContributor>(sp => new LlamaCppRuntimeContributor(
                sp.GetRequiredService<IExecutableCatalog>(), sp.GetServices<IRuntimeDriver>()));
            // runtime.mlx.process with its versions (docs/macos/implementation-plan.md, Phase 3): only a Mac installs mlx-lm,
            // and MLX processes run only through the macOS process host.
            if (OperatingSystem.IsMacOS())
            {
                services.AddSingleton<ICapabilityContributor>(sp => new MlxRuntimeContributor(
                    sp.GetRequiredService<IExecutableCatalog>(), sp.GetServices<IRuntimeDriver>()));
            }
            services.AddSingleton<ICapabilityContributor>(sp => new GpuBudgetsContributor(sp.GetRequiredService<ReservationManager>()));
            // gguf.inspect: the route's reader is registered, so the P3 master may call it (MN-209).
            services.AddSingleton<ICapabilityContributor>(sp => new GgufCapabilityContributor(sp.GetRequiredService<IGgufReader>()));
            // executable_unverified while a version's files do not match its install manifest (MN-222).
            services.AddSingleton<IHealthContributor>(sp => new ExecutableHealthContributor(sp.GetRequiredService<IExecutableCatalog>()));
            // gpu_external_use_high (MN-214, D129 (2)): the sampler's observer and a health contributor in one.
            services.AddSingleton<IHealthContributor>(sp => sp.GetRequiredService<ExternalGpuUseContributor>());
        }

        // Last of all the capability contributors (MN-214): with Docker off it removes any Docker feature, container
        // runtime, image or containers flag a mixed contributor still added. With Docker on it is the identity, so the
        // DGX's report is byte-identical.
        services.AddSingleton<ICapabilityContributor>(docker.Filter());

        // Health changes reach a connected master as events (MN-047 / D68), checked at the heartbeat cadence.
        services.AddHostedService(sp => new AgentHealthWatcher(
            sp.GetRequiredService<IEventBus>(),
            sp.GetServices<IHealthContributor>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AgentHealthWatcher>>(),
            options.HealthCheckInterval));

        services.AddAgentPki(options.ToHostOptions());
        services.AddAgentReconciliation();

        services.Configure<HostOptions>(host => host.ShutdownTimeout = options.ShutdownTimeout);
        return services;
    }

    /// <summary>
    /// Adds the request pipeline (<see cref="AgentHostBuilder.UsePipeline"/>: client certificate, protocol, feature,
    /// freshness and fencing checks, error shape) and maps every route of <see cref="AgentRoutes.All"/> exactly once.
    /// Call after reconciliation; the listener opens only when the application starts.
    /// </summary>
    public static WebApplication MapMirbadLLMAgent(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        AgentHostBuilder.UsePipeline(app);

        // A container route no feature gates (POST /images/pull) on a node without Docker: refused 503
        // docker_unavailable here, before the operation is accepted and durably recorded (D129 (3)). With Docker on
        // Refuse always answers null, so the pipeline is the DGX's.
        DockerAvailability docker = app.Services.GetRequiredService<DockerAvailability>();
        if (!docker.Enabled)
        {
            app.Use(async (context, next) =>
            {
                if (docker.Refuse(context.Features.Get<AgentRequestFeature>()?.Route) is { } refusal)
                {
                    await AgentErrorResults.WriteAsync(context, ErrorMappings.AgentStatus(refusal.Code), refusal).ConfigureAwait(false);
                    return;
                }

                await next(context).ConfigureAwait(false);
            });
        }

        // Event and log streams never end on their own: end them when the agent stops, so shutdown does not wait for the
        // master to disconnect. Other requests (proxied generations included) get the shutdown timeout to finish.
        IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        app.Use(async (context, next) =>
        {
            AgentRoute? route = context.Features.Get<AgentRequestFeature>()?.Route;
            if (route != AgentRoutes.Events && route != AgentRoutes.RuntimeLogs)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
            context.RequestAborted = linked.Token;
            await next(context).ConfigureAwait(false);
        });

        // Areas that map with MapAgentRoute (absolute paths, route metadata) …
        app.MapInfoEndpoints();
        app.MapPkiEndpoints();
        app.MapAgentProxyEndpoints();
        app.MapAgentRuntimeEndpoints();
        app.MapAgentOperationEndpoints();
        app.MapGgufEndpoints(); // MN-209's handler; the pipeline answers 501 where gguf.inspect is not reported (the DGX)

        // … and areas that map catalogue paths relative to the builder they are given (found by pattern in the catalogue).
        RouteGroupBuilder api = app.MapGroup(Protocol.BasePath);
        api.MapStatusEndpoints();
        api.MapTelemetryEndpoints();
        api.MapStorageEndpoints();
        api.MapHostServiceEndpoints();
        api.MapAgentUpdateEndpoints();
        return app;
    }

    /// <summary>
    /// The Docker Engine client and the model-upstream client. With Docker off (<paramref name="docker"/>) <b>no</b>
    /// Docker <see cref="HttpClient"/> and no <see cref="DockerEngineClient"/> are registered at all (design-p3p4 §A.4.3:
    /// nothing may touch the engine, and <c>npipe://./pipe/docker_engine</c> in <c>agent.windows.example.json</c> is
    /// unused), and the image engine is <see cref="DockerAvailability.Gate(Func{IContainerImageEngine})"/>'s: it lists
    /// nothing and refuses pulls. The model-upstream client is registered either way — the process driver's Ready probe
    /// uses it too.
    /// </summary>
    private static void AddDocker(IServiceCollection services, AgentOptions options, DockerAvailability docker)
    {
        if (docker.Enabled)
        {
            // No HttpClient timeout: image pulls and followed logs stream for as long as they take. Every other call is
            // bounded per request by DockerCallTimeoutHandler (W2 review MN-092 item 8: a hung create/start/stop would hold the
            // model lock and the start reservation).
            services.AddHttpClient(DockerHttpClientName, client =>
                {
                    client.BaseAddress = new Uri("http://docker/v1.47/");
                    client.Timeout = Timeout.InfiniteTimeSpan;
                })
                .ConfigurePrimaryHttpMessageHandler(() => DockerEngineClient.CreateHandler(options.DockerEndpoint))
                .AddHttpMessageHandler(() => new DockerCallTimeoutHandler(options.DockerCallTimeout));
            services.TryAddSingleton(sp => new DockerEngineClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(DockerHttpClientName)));
        }

        services.AddHttpClient(UpstreamHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });

        services.TryAddSingleton<IContainerImageEngine>(sp => docker.Gate(
            () => new DockerImageClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(DockerHttpClientName))));
    }

    /// <summary>
    /// The Hugging Face downloader. With Docker on it is the helper-container one (the DGX, unchanged); with Docker off
    /// it is <see cref="NativeDownloadService"/> (design-p3p4 §A.2.1) over a client without automatic redirects — see
    /// <see cref="NativeDownloadHttpClientName"/> — and <see cref="NativeDownloadContributor"/> reports
    /// <c>downloads.hf-native</c>, which is what makes <c>POST /downloads</c> reachable there
    /// (<see cref="AgentRoute.AlternativeFeatures"/>). The repo inspector is the same on both.
    /// </summary>
    private static void AddDownloads(IServiceCollection services, AgentOptions options, DockerAvailability docker)
    {
        services.AddHttpClient(HuggingFaceHttpClientName, client => client.BaseAddress = new Uri("https://huggingface.co/"));
        if (docker.Enabled)
        {
            services.TryAddSingleton<IHuggingFaceDownloader>(sp => new HuggingFaceDownloader(
                sp.GetRequiredService<DockerEngineClient>(),
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(HuggingFaceHttpClientName),
                Options.Create(options.ToDownloaderOptions()),
                Options.Create(options.ToModelStorageOptions()),
                sp.GetRequiredService<ILogger<HuggingFaceDownloader>>(),
                ResolveDownloadHelperUser(options, sp.GetRequiredService<ILogger<HuggingFaceDownloader>>())));
        }
        else
        {
            // AllowAutoRedirect = false: SecureDownloadTransport follows the Hub's redirect to its CDN itself, so every
            // hop passes the HTTPS rule before it is requested and the token stays on the Hub's own origin (§A.8).
            services.AddHttpClient(NativeDownloadHttpClientName, client => client.BaseAddress = new Uri("https://huggingface.co/"))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
            services.TryAddSingleton<NativeDownloadService>(sp => new NativeDownloadService(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(NativeDownloadHttpClientName),
                sp.GetRequiredService<ModelRootPaths>(),
                sp.GetRequiredService<IDiskSpaceProbe>(),
                sp.GetRequiredService<IOptions<AgentStorageOptions>>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<NativeDownloadService>>()));
            services.TryAddSingleton<IHuggingFaceDownloader>(sp => sp.GetRequiredService<NativeDownloadService>());
            services.AddSingleton<ICapabilityContributor>(new NativeDownloadContributor());
        }

        services.TryAddSingleton<IHuggingFaceRepoInspector>(sp =>
            new HuggingFaceRepoInspector(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HuggingFaceHttpClientName)));
    }

    /// <summary>
    /// Process delivery (design-p3p4 §A.1, §A.4.1; MN-211, MN-214, MN-222, MN-228). Returns true when this node runs
    /// models as native processes, which every P3/P4 registration is gated on.
    /// <para>The gate is the node's Docker switch: Docker off = the Windows PC. The <c>[SupportedOSPlatform("windows")]</c>
    /// implementations of process delivery — <see cref="WindowsProcessHost"/> and <see cref="InstanceKeyProtector"/> —
    /// sit behind <see cref="OperatingSystem.IsWindows"/> and are <c>TryAdd</c>ed, so a
    /// replacement registered before <see cref="AddMirbadLLMAgent"/> keeps its place. Everything else is
    /// platform-neutral and needs only an <see cref="IProcessHost"/> and an <see cref="IInstanceKeyProtector"/>: without
    /// them (a node with Docker off that is not Windows and injected none) nothing process-related is registered and the
    /// node simply has no runtime driver, which <c>Program</c> logs once (MN-234).</para>
    /// <para>Not <i>every</i> P3/P4 registration is gated on the return value: the native downloader and
    /// <c>downloads.hf-native</c> follow the Docker switch instead, and <see cref="WindowsMemoryProbe"/> follows the
    /// operating system alone (<see cref="AddWindowsMemoryProbe"/>, MN-239 item 15) — see the class remarks.</para>
    /// </summary>
    private static bool AddProcessRuntime(IServiceCollection services, AgentOptions options, DockerAvailability docker)
    {
        if (docker.Enabled)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            AddWindowsSeams(services);
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddMacSeams(services, options);
        }

        if (!services.Any(d => d.ServiceType == typeof(IProcessHost)) ||
            !services.Any(d => d.ServiceType == typeof(IInstanceKeyProtector)))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.CacheRoot))
        {
            throw new ArgumentException(
                "Agent:CacheRoot is required on a node that runs native processes (Agent:Docker:Enabled = false): a launch's " +
                "argv may name it as {cache}.",
                nameof(options));
        }

        services.TryAddSingleton<IProcessRecordStore>(_ => new ProcessRecordStore(Path.Combine(options.StateDirectory, ProcessRecordsDocument.FileName)));
        services.TryAddSingleton<IExecutableCatalog>(sp => new ExecutableCatalog(options.Executables, sp.GetRequiredService<ILogger<ExecutableCatalog>>()));
        services.TryAddSingleton<IGgufReader>(_ => new GgufReader());
        services.TryAddSingleton<IGpuStartMeter>(sp => new NvidiaSmiGpuStartMeter(
            sp.GetRequiredService<IGpuTelemetryProbe>(), sp.GetRequiredService<TimeProvider>()));

        // GPU processes without hostd: nvidia-smi --query-compute-apps mapped through processes.json (MN-214).
        // Registered before AddAgentTelemetry, whose TryAdd default is the hostd probe.
        services.TryAddSingleton<IGpuProcessProbe>(sp => new NvidiaSmiGpuProcessProbe(
            sp.GetRequiredService<INvidiaSmiRunner>(),
            sp.GetRequiredService<IProcessRecordStore>(),
            sp.GetRequiredService<ILogger<NvidiaSmiGpuProcessProbe>>()));

        // The reserves the master sent with the last launch, so gpu_external_use_high is judged right after a restart
        // and not only after the next launch (D129 (2)).
        services.TryAddSingleton(sp => new GpuReserveStore(
            GpuReserveStore.PathFor(options.StateDirectory), sp.GetRequiredService<ILogger<GpuReserveStore>>()));
        services.TryAddSingleton(sp => new ExternalGpuUseContributor(
            sp.GetRequiredService<IProcessRecordStore>(),
            new ExternalGpuUseOptions { GpuReservesGiB = sp.GetRequiredService<GpuReserveStore>().Load() },
            sp.GetRequiredService<ILogger<ExternalGpuUseContributor>>()));
        services.AddSingleton<ITelemetrySampleObserver>(sp => sp.GetRequiredService<ExternalGpuUseContributor>());

        // Shared with the agent proxy's upstream cache: the driver bumps it whenever an instance is created, adopted or
        // removed, so a relaunch on a new loopback port cannot be proxied to the old one (MN-239 bug B).
        services.TryAddSingleton<UpstreamChanges>();
        services.TryAddSingleton(sp => new ProcessRuntimeDriver(
            sp.GetRequiredService<IProcessHost>(),
            sp.GetRequiredService<IProcessRecordStore>(),
            sp.GetRequiredService<IExecutableCatalog>(),
            sp.GetRequiredService<IInstanceKeyProtector>(),
            sp.GetRequiredService<IPortAllocator>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(UpstreamHttpClientName),
            Options.Create(ToProcessRuntimeOptions(options)),
            sp.GetRequiredService<ILogger<ProcessRuntimeDriver>>(),
            sp.GetRequiredService<IGpuStartMeter>(),
            sp.GetService<IGpuInventory>(),
            timeProvider: null,
            sp.GetRequiredService<UpstreamChanges>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRuntimeDriver, ProcessRuntimeDriver>(
            sp => sp.GetRequiredService<ProcessRuntimeDriver>()));

        // The catalogue's first full hash (≈ 712 MiB) runs here, never before the listener opens (§A.4.1): a
        // BackgroundService, so its StartAsync cannot hold the host back.
        services.AddHostedService(sp => new ExecutableCatalogVerifier(
            sp.GetRequiredService<IExecutableCatalog>(), sp.GetRequiredService<ILogger<ExecutableCatalogVerifier>>()));
        return true;
    }

    /// <summary>
    /// The two <c>[SupportedOSPlatform("windows")]</c> implementations of <i>process delivery</i>: the job-object process
    /// host (MN-211) and the DPAPI instance-key protector (MN-222, machine scope with the service SID as entropy).
    /// <c>TryAdd</c>ed, so a fake registered before <see cref="AddMirbadLLMAgent"/> keeps its place. The Windows memory
    /// probe is <b>not</b> here: it is not part of process delivery (<see cref="AddWindowsMemoryProbe"/>).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddWindowsSeams(IServiceCollection services)
    {
        services.TryAddSingleton<IProcessHost>(sp => new WindowsProcessHost(sp.GetRequiredService<ILogger<WindowsProcessHost>>()));
        services.TryAddSingleton<IInstanceKeyProtector>(_ => new InstanceKeyProtector());
    }

    /// <summary>
    /// The macOS process delivery seams (docs/macos/implementation-plan.md M2/M3): <see cref="MacProcessHost"/>
    /// (<c>posix_spawn</c> in a new session, adoption by pid and start time) and <see cref="FileInstanceKeyProtector"/>
    /// (AES-GCM under a 0600 key file in the state folder: a daemon account has no unlocked keychain). <c>TryAdd</c>ed
    /// like the Windows ones.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void AddMacSeams(IServiceCollection services, AgentOptions options)
    {
        services.TryAddSingleton<IProcessHost>(sp => new MacProcessHost(sp.GetRequiredService<ILogger<MacProcessHost>>()));
        services.TryAddSingleton<IInstanceKeyProtector>(_ => new FileInstanceKeyProtector(options.StateDirectory));
    }

    /// <summary>
    /// The <c>GlobalMemoryStatusEx</c> host memory probe (MN-214), which answers <c>/status</c>, admission and the
    /// capability report's host memory. Registered on <see cref="OperatingSystem.IsWindows"/> <b>alone</b> — not behind
    /// the Docker switch (MN-239 item 15): <c>AddAgentTelemetry</c>'s <c>TryAdd</c> default reads
    /// <c>/proc/meminfo</c>, which no Windows node has, so a Windows host with Docker <i>on</i> used to end up with a
    /// reader that cannot work instead of the one design-p3p4 §A.4.3 names. <c>TryAdd</c>ed, so a fake registered before
    /// <see cref="AddMirbadLLMAgent"/> keeps its place; on every node that injects one (the dev setup and nearly every
    /// composition test) this registration is inert.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AddWindowsMemoryProbe(IServiceCollection services) =>
        services.TryAddSingleton<IMemoryProbe>(_ => new WindowsMemoryProbe());

    /// <summary>
    /// Whether this node's host services go through the Service Control Manager: a process node on Windows, or off
    /// Windows one that had an <see cref="IServiceControlManager"/> registered before <see cref="AddMirbadLLMAgent"/>
    /// (tests, the dev setup). Read twice — once to configure the Linux package, once to add the SCM one — so both see
    /// the same answer.
    /// </summary>
    private static bool UsesScmHostServices(IServiceCollection services, bool processNode) =>
        processNode && (OperatingSystem.IsWindows() || services.Any(d => d.ServiceType == typeof(IServiceControlManager)));

    /// <summary>
    /// The SCM host services (MN-228, design-p3p4 §A.4.3): on Windows over the real Service Control Manager, and off
    /// Windows over the <see cref="IServiceControlManager"/> something registered before. They replace the Linux hostd
    /// client, operations and queries, and report <c>services.scm</c>; the whitelist file lives in the agent's state
    /// directory and the deny list holds the agent's own service.
    /// </summary>
    private static void AddScmHostServices(IServiceCollection services, AgentOptions options, bool scmHostServices)
    {
        if (!scmHostServices)
        {
            return;
        }

        ScmHostServicesOptions scm = ScmHostServicesOptions.ForStateDirectory(options.StateDirectory);
        if (OperatingSystem.IsWindows())
        {
            services.AddScmHostServices(scm);
        }
        else
        {
            services.AddScmHostServices(scm, sp => sp.GetRequiredService<IServiceControlManager>());
        }
    }

    /// <summary><c>Agent:Process</c> plus this node's identity and roots, for <see cref="ProcessRuntimeDriver"/>.</summary>
    private static ProcessRuntimeOptions ToProcessRuntimeOptions(AgentOptions options) => new()
    {
        NodeId = options.NodeId,
        ModelsRoot = options.ModelsRoot,
        CacheRoot = options.CacheRoot ?? "",
        StateDirectory = options.StateDirectory,
        UpstreamHost = options.UpstreamHost,
        StartTimeout = options.Process.StartTimeout,
        RestartBudget = options.Process.RestartBudget,
        RestartWindow = options.Process.RestartWindow,
        LogMaxBytes = options.Process.LogMaxBytes,
        LogFilesKept = options.Process.LogFilesKept,
        HealthPollInterval = options.Process.HealthPollInterval,
    };

    /// <summary>
    /// The admission observer of a process node: every launch's per-GPU reserves reach
    /// <see cref="ExternalGpuUseContributor.RememberReserves"/> and <see cref="GpuReserveStore"/> (which writes only when
    /// they changed). It runs on the admission path of a launch, so it never lets anything escape.
    /// </summary>
    private static Action<AdmissionRequest> LearnReserves(IServiceProvider services)
    {
        var external = services.GetRequiredService<ExternalGpuUseContributor>();
        var store = services.GetRequiredService<GpuReserveStore>();
        var logger = services.GetRequiredService<ILogger<GpuReserveStore>>();
        return request =>
        {
            try
            {
                external.RememberReserves(request);
                store.Remember(request);
            }
#pragma warning disable CA1031 // a launch must never fail because the reserves could not be remembered
            catch (Exception ex)
#pragma warning restore CA1031
            {
                AgentCompositionLog.ReservesNotLearned(logger, ex);
            }
        };
    }

    /// <summary>
    /// <see cref="AgentOptions.DownloadHelperUser"/> resolved against this process (MN-126): in <c>auto</c> mode a
    /// non-root Linux agent runs the helper as itself, unless it cannot write the models root (then root, logged).
    /// </summary>
    internal static string? ResolveDownloadHelperUser(AgentOptions options, ILogger logger)
    {
        var identity = DownloadHelperUser.ReadProcessIdentity();
        bool rootWritable = true;
        string? user = DownloadHelperUser.Resolve(options.DownloadHelperUser, identity, () => rootWritable = DownloadHelperUser.CanCreateIn(options.ModelsRoot));
        if (!rootWritable)
        {
            AgentDownloadLog.HelperRunsAsImageUser(logger, options.ModelsRoot, identity?.Uid ?? -1);
        }

        return user;
    }
}

/// <summary>
/// Bounds every Docker Engine call except the streaming ones (image pulls, followed logs) to
/// <see cref="AgentOptions.DockerCallTimeout"/>: the limit covers sending the request and receiving the response
/// headers, which for create/start/stop/remove/inspect/list is the whole call. A call that times out fails like an
/// unreachable daemon (<see cref="TimeoutException"/>), so the operation ends and its lock and reservation are released.
/// </summary>
internal sealed class DockerCallTimeoutHandler(TimeSpan timeout) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (IsStreaming(request.RequestUri))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(timeout);
        try
        {
            return await base.SendAsync(request, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Docker did not answer {request.Method} {request.RequestUri?.AbsolutePath} within {timeout.TotalSeconds:0} s.", ex);
        }
    }

    internal static bool IsStreaming(Uri? uri)
    {
        if (uri is null)
        {
            return false;
        }

        string path = uri.AbsolutePath;
        return path.EndsWith("/images/create", StringComparison.Ordinal)
            || (path.EndsWith("/logs", StringComparison.Ordinal) && uri.Query.Contains("follow=true", StringComparison.Ordinal));
    }
}

/// <summary>
/// Is the Docker daemon answering? One cheap listing (labelled containers), cached briefly so <c>/status</c> and
/// <c>/capabilities</c> do not each cost a Docker call; also the bounded wait the host runs before reconciling (design
/// D56, W3 review MN-094 item 2).
/// </summary>
internal sealed partial class DockerProbe(DockerEngineClient docker, TimeProvider time, ILogger<DockerProbe> logger) : IDisposable
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Probe? _last;

    /// <summary>Null when Docker answered (now or within the last few seconds), else why not.</summary>
    public async Task<string?> CheckAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _last) is { } cached && time.GetUtcNow() - cached.At < CacheFor)
        {
            return cached.Error;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _last) is { } fresh && time.GetUtcNow() - fresh.At < CacheFor)
            {
                return fresh.Error;
            }

            string? error = await ProbeAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _last, new Probe(time.GetUtcNow(), error));
            return error;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Waits until Docker answers or <paramref name="limit"/> passed. True when it answered; false means the agent goes
    /// on without it (reconciliation then fails the launches that need it with <c>docker_unavailable</c>).
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan limit, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan delay = TimeSpan.FromMilliseconds(250);
        string? error;
        while ((error = await ProbeAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (watch.Elapsed >= limit)
            {
                LogDockerNotReady(logger, limit.TotalSeconds, error);
                return false;
            }

            LogWaitingForDocker(logger, error);
            await Task.Delay(delay, ct).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5_000));
        }

        Volatile.Write(ref _last, new Probe(time.GetUtcNow(), null));
        return true;
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    private async Task<string?> ProbeAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            _ = await docker.ListContainersAsync([ContainerLabels.Model], all: false, cts.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ex.Message;
        }
    }

    private sealed record Probe(DateTimeOffset At, string? Error);

    [LoggerMessage(EventId = 2601, Level = LogLevel.Information, Message = "Waiting for Docker before reconciling: {Error}")]
    private static partial void LogWaitingForDocker(ILogger logger, string error);

    [LoggerMessage(EventId = 2602, Level = LogLevel.Warning,
        Message = "Docker did not answer within {Seconds:0} s ({Error}); reconciling without it (launches that need Docker fail docker_unavailable)")]
    private static partial void LogDockerNotReady(ILogger logger, double seconds, string error);
}

/// <summary>
/// The container driver's part of the capability report (design §3.3: <c>runtime.vllm.container</c>, the vLLM container
/// runtime, available while Docker answers) and its Degraded reason (§3.5 <c>docker_unreachable</c>).
/// </summary>
internal sealed class RuntimeCapabilityContributor(DockerProbe docker, bool enableNim = false) : ICapabilityContributor, IHealthContributor
{
    public async Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        bool up = await docker.CheckAsync(ct).ConfigureAwait(false) is null;
        var kinds = new List<(RuntimeKind Kind, string Feature)>
        {
            (RuntimeKind.Vllm, AgentFeatures.RuntimeVllmContainer),
        };
        if (OperatingSystem.IsLinux())
        {
            kinds.Add((RuntimeKind.LlamaCpp, AgentFeatures.RuntimeLlamaCppContainer));
            kinds.Add((RuntimeKind.Sglang, AgentFeatures.RuntimeSglangContainer));
            if (enableNim)
            {
                kinds.Add((RuntimeKind.Nim, AgentFeatures.RuntimeNimContainer));
            }
        }

        var features = current.Features.ToList();
        foreach (var (_, feature) in kinds)
        {
            if (!features.Contains(feature, StringComparer.Ordinal))
            {
                features.Add(feature);
            }
        }

        var runtimeKinds = kinds.Select(k => k.Kind).ToHashSet();
        var runtimes = current.Runtimes
            .Where(r => r.Delivery != LaunchDelivery.Container || !runtimeKinds.Contains(r.Kind))
            .Concat(kinds.Select(k => new RuntimeCapability { Kind = k.Kind, Delivery = LaunchDelivery.Container, Available = up }))
            .ToList();
        return current with { Features = features, Runtimes = runtimes };
    }

    public async Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default)
    {
        string? error = await docker.CheckAsync(ct).ConfigureAwait(false);
        return error is null ? [] : [new HealthReason(HealthReasonCodes.DockerUnreachable, "Docker is unreachable: " + error)];
    }
}

/// <summary>
/// <see cref="IRuntimeInstanceSource"/> for the reservation manager that resolves the drivers on first use: the container
/// driver takes the reservation manager as its port allocator, so neither can be built from the other's constructor.
/// </summary>
internal sealed class DeferredRuntimeInstanceSource(IServiceProvider services) : IRuntimeInstanceSource
{
    private RuntimeDriverInstanceSource? _inner;

    public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) =>
        (_inner ??= new RuntimeDriverInstanceSource(services.GetServices<IRuntimeDriver>())).ListAsync(ct);
}

/// <summary>
/// Graceful shutdown (MN-026): stops last, after the listener closed and the watcher, sampler and compaction stopped, and
/// waits (within the host's shutdown timeout) for launches and stops already executing to reach a durable point. What
/// does not finish stays non-terminal in the operation log and is reconciled at the next start (design §4.4 item 5).
/// </summary>
internal sealed partial class AgentDrain(RuntimeOperations runtimes, ILogger<AgentDrain> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await runtimes.WhenIdleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            LogDrained(logger);
        }
        catch (OperationCanceledException)
        {
            LogNotDrained(logger);
        }
        catch (Exception ex)
        {
            // The operations record their own failures; nothing to add at shutdown.
            LogDrainFailed(logger, ex);
        }
    }

    [LoggerMessage(EventId = 2611, Level = LogLevel.Information, Message = "Agent stopped: no runtime operation is executing")]
    private static partial void LogDrained(ILogger logger);

    [LoggerMessage(EventId = 2612, Level = LogLevel.Warning,
        Message = "Agent stopped with runtime operations still executing; they stay open in the operation log and are reconciled at the next start")]
    private static partial void LogNotDrained(ILogger logger);

    [LoggerMessage(EventId = 2613, Level = LogLevel.Warning, Message = "A runtime operation failed while the agent stopped")]
    private static partial void LogDrainFailed(ILogger logger, Exception exception);
}

internal static partial class AgentCompositionLog
{
    [LoggerMessage(EventId = 2683, Level = LogLevel.Warning,
        Message = "A launch's GPU reserves could not be remembered; gpu_external_use_high keeps the reserves it had")]
    public static partial void ReservesNotLearned(ILogger logger, Exception exception);
}

internal static partial class AgentDownloadLog
{
    [LoggerMessage(EventId = 2630, Level = LogLevel.Warning,
        Message = "The agent (uid {Uid}) cannot write the models root {ModelsRoot}; the download helper keeps the image's default user (root), so the agent cannot delete the folders it writes")]
    public static partial void HelperRunsAsImageUser(ILogger logger, string modelsRoot, long uid);
}
