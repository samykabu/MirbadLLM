using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.HostServices;
using MirbadLLM.Agent.HostServices.Windows;
using MirbadLLM.Agent.Proxy;
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
using MirbadLLM.NodeRuntime.Runtimes.Process.Windows;
using MirbadLLM.UnitTests.Agent.Fakes;
using MirbadLLM.UnitTests.HostServices;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.P2Exit;
using AgentProgram = MirbadLLM.Agent.Program;

#pragma warning disable CA1707, CA2000, xUnit1051 // underscore test names; handler ownership in fakes; fakes ignore the token.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-232: the P3/P4 agent composition, which is what turns Waves 12–13 on. Every line here proves one bullet of the
/// W11 (MN-207, D121/D122), W12 (MN-219, D125–D129) and W13 (MN-230, D130) wiring checklists against the <b>real</b>
/// composition (<c>Program.CreateAsync</c> + <c>AddMirbadLLMAgent</c>), with only the node's outside world faked: the
/// process host, DPAPI, the Service Control Manager, <c>nvidia-smi</c> and the host memory probe. The fakes are
/// registered before the packages, exactly as the dev setup registers its own, which is what lets a Windows/process node
/// be composed and asserted on Linux as well as on Windows.
/// <para>The DGX is the other half of every assertion: it keeps Docker on, so it must compose as it did in P2 and report
/// not one flag of <see cref="AgentFeatures.P3Reported"/>.</para>
/// </summary>
public sealed class P3AgentCompositionTests
{
    private const string Gpu0 = "GPU-0a4f2d61-8b3c-4e7a-b1d9-2c5e8f0a3b17";
    private const string Gpu1 = "GPU-7c1e9a52-3f60-4d2b-9a8e-5b0f2c6d1e44";

    /// <summary>SHA-256 of <c>Contracts/Fixtures/NodeCapabilities.json</c> (LF, <c>.gitattributes</c>), frozen in Wave 1.</summary>
    private const string DgxFixtureSha256 = "ac80c5f2f41f9cf9c2f66aa9465629776e46f324276a8a6613b9839f327303d1";

    /// <summary><c>Program</c>'s "Adopted N supervised process(es)…" line (MN-232).</summary>
    private const int ProcessesRecoveredEvent = 2629;

    /// <summary><c>Program</c>'s "Reconciled … opening the listener" line.</summary>
    private const int ReconciledEvent = 2621;

    /// <summary><c>Program</c>'s "… has no process delivery" warning (MN-234; renumbered 2644 → 2684 by MN-239 H1).</summary>
    private const int NoRuntimeDeliveryEvent = 2684;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ W12: what a process node registers

    [Fact]
    public async Task ProcessNode_RegistersTheProcessDriverAndEveryCollaboratorOfIt()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        var sp = app.Services;

        // The driver is the node's only runtime driver, over the process host, the record store, the catalogue, the
        // key protector, the reservation manager as port allocator and the GPU start meter.
        var driver = Assert.IsType<ProcessRuntimeDriver>(Assert.Single(sp.GetServices<IRuntimeDriver>()));
        Assert.Equal(LaunchDelivery.Process, driver.Delivery);
        Assert.Same(driver, sp.GetRequiredService<ProcessRuntimeDriver>());
        Assert.Same(node.Host, sp.GetRequiredService<IProcessHost>());
        Assert.Same(node.Keys, sp.GetRequiredService<IInstanceKeyProtector>());

        var store = Assert.IsType<ProcessRecordStore>(sp.GetRequiredService<IProcessRecordStore>());
        Assert.Equal(Path.Combine(node.StateDirectory, ProcessRecordsDocument.FileName), store.FilePath);

        Assert.IsType<ExecutableCatalog>(sp.GetRequiredService<IExecutableCatalog>());
        Assert.IsType<GgufReader>(sp.GetRequiredService<IGgufReader>());
        Assert.IsType<NvidiaSmiGpuStartMeter>(sp.GetRequiredService<IGpuStartMeter>());
        Assert.IsType<NvidiaSmiGpuProcessProbe>(sp.GetRequiredService<IGpuProcessProbe>());
        Assert.IsType<NativeDownloadService>(sp.GetRequiredService<IHuggingFaceDownloader>());

        // gpu_external_use_high: one object, both the sampler's observer and a health contributor (MN-214, D129 (2)).
        var external = sp.GetRequiredService<ExternalGpuUseContributor>();
        Assert.Same(external, Assert.Single(sp.GetServices<ITelemetrySampleObserver>()));
        Assert.Contains(external, sp.GetServices<IHealthContributor>());
        Assert.Same(external, Assert.Single(ObserversOf(sp.GetRequiredService<TelemetrySampler>())));

        // The SCM host services replace the Linux hostd client, operations and queries (MN-228).
        Assert.IsType<ScmHostClient>(sp.GetRequiredService<IHostdClient>());
        Assert.Single(sp.GetServices<ICapabilityContributor>().OfType<ScmHostServicesContributor>());

        // The catalogue's first hashing is a BackgroundService (§A.4.1), never a blocking StartAsync.
        var verifier = Assert.Single(sp.GetServices<IHostedService>().OfType<ExecutableCatalogVerifier>());
        Assert.IsAssignableFrom<BackgroundService>(verifier);

        // MN-215's Windows service hosting is configured by Program.CreateAsync; outside the SCM it changes nothing.
        Assert.False(WindowsServiceHosting.IsWindowsService);
        Assert.Equal("ConsoleLifetime", sp.GetRequiredService<IHostLifetime>().GetType().Name);
    }

    [Fact]
    public async Task ProcessNode_ReportsTheP3FlagsAndNotOneDockerOne()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        await app.StartAsync(Ct);
        var catalog = app.Services.GetRequiredService<IExecutableCatalog>();
        await WaitUntilAsync(() => catalog.List() is [{ Verified: true }], "the catalogue was never verified");

        NodeCapabilities report = await app.Services.GetRequiredService<AgentCapabilityReporter>().GetAsync(Ct);

        // Every flag the frozen NodeCapabilities.windows.json carries, except downloads.ollama (MN-221 was not done).
        Assert.Contains(AgentFeatures.RuntimeLlamaCppProcess, report.Features);
        Assert.Contains(AgentFeatures.DownloadsHfNative, report.Features);
        Assert.Contains(AgentFeatures.GgufInspect, report.Features);
        Assert.Contains(AgentFeatures.GpuSelectionUuids, report.Features);
        Assert.Contains(AgentFeatures.LaunchEnv, report.Features);
        Assert.Contains(AgentFeatures.AdmissionDiscrete, report.Features);
        Assert.Contains(AgentFeatures.AdmissionGpuBudgets, report.Features);
        Assert.Contains(AgentFeatures.ServicesScm, report.Features);
        Assert.Contains(AgentFeatures.TelemetryV1, report.Features);
        Assert.DoesNotContain(AgentFeatures.DownloadsOllama, report.Features);

        // None of Docker's, and nothing of the Linux host-service package.
        foreach (string feature in DockerAvailability.DockerFeatures)
        {
            Assert.DoesNotContain(feature, report.Features);
        }

        Assert.DoesNotContain(AgentFeatures.ServicesSystemd, report.Features);
        Assert.DoesNotContain(AgentFeatures.GpuProcesses, report.Features);
        Assert.DoesNotContain(AgentFeatures.BackupMount, report.Features);
        Assert.Empty(report.Images);

        // The llama.cpp process runtime with its versions, and nothing delivered in a container.
        var llama = Assert.Single(report.Runtimes);
        Assert.Equal((RuntimeKind.LlamaCpp, LaunchDelivery.Process), (llama.Kind, llama.Delivery));
        Assert.True(llama.Available);
        var version = Assert.Single(llama.Versions!);
        Assert.Equal((node.Llama.Version, "13.4", true, true), (version.Version, version.Cuda, version.Verified, version.Default));

        Assert.Equal(MemoryModel.Discrete, report.Memory.Model);
        Assert.Equal(HostServiceManagers.Scm, report.HostServices!.Manager);
        Assert.False(report.HostServices.Containers);
        Assert.Null(report.BackupMount);
    }

    [Fact]
    public async Task DgxAgent_ComposesAsInP2_AndReportsNoP3Flag()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeDgxAsync(Ct);
        var sp = app.Services;

        NodeCapabilities report = await sp.GetRequiredService<AgentCapabilityReporter>().GetAsync(Ct);
        var reasons = await Health(sp, Ct);

        // Not one P3/P4 flag, and none of the P3 services exists on the DGX.
        foreach (string feature in AgentFeatures.P3Reported)
        {
            Assert.DoesNotContain(feature, report.Features);
        }

        Assert.DoesNotContain(HealthReasonCodes.ExecutableUnverified, reasons.Select(r => r.Code));
        Assert.DoesNotContain(HealthReasonCodes.GpuExternalUseHigh, reasons.Select(r => r.Code));
        Assert.Null(sp.GetService<ProcessRuntimeDriver>());
        Assert.Null(sp.GetService<IProcessRecordStore>());
        Assert.Null(sp.GetService<IExecutableCatalog>());
        Assert.Null(sp.GetService<IGgufReader>());
        Assert.Null(sp.GetService<IGpuStartMeter>());
        Assert.Null(sp.GetService<ExternalGpuUseContributor>());
        Assert.Null(sp.GetService<GpuReserveStore>());
        Assert.Empty(sp.GetServices<ICapabilityContributor>().OfType<GgufCapabilityContributor>());
        Assert.Empty(sp.GetServices<ICapabilityContributor>().OfType<ScmHostServicesContributor>());
        Assert.Empty(sp.GetServices<ICapabilityContributor>().OfType<NativeDownloadContributor>());
        Assert.Empty(sp.GetServices<ITelemetrySampleObserver>());

        // P2's composition: the container driver, the Docker client, the download helper, the hostd client.
        Assert.IsType<ContainerRuntimeDriver>(Assert.Single(sp.GetServices<IRuntimeDriver>()));
        Assert.NotNull(sp.GetService<DockerEngineClient>());
        Assert.IsType<HuggingFaceDownloader>(sp.GetRequiredService<IHuggingFaceDownloader>());
        Assert.Single(sp.GetServices<IHostedService>().OfType<DownloadHelperCleanup>());
        Assert.Contains(AgentFeatures.RuntimeVllmContainer, report.Features);
        Assert.Contains(AgentFeatures.DownloadsHfContainer, report.Features);
    }

    [Fact]
    public void TheDgxCapabilityFixture_IsByteIdentical_AndCarriesNoP3Flag()
    {
        // The DGX's frozen report (Wave 1). MN-232 must not change a byte of it.
        string path = Path.Combine(FixturesPath(), "NodeCapabilities.json");
        byte[] bytes = File.ReadAllBytes(path);

        Assert.Equal(DgxFixtureSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var dgx = JsonSerializer.Deserialize<NodeCapabilities>(bytes, AgentJson.Options)!;
        Assert.Equal(
            [AgentFeatures.RuntimeVllmContainer, AgentFeatures.DownloadsHfContainer, AgentFeatures.ServicesSystemd, AgentFeatures.GpuProcesses, AgentFeatures.TelemetryV1],
            dgx.Features);
        foreach (string feature in AgentFeatures.P3Reported)
        {
            Assert.DoesNotContain(feature, dgx.Features);
        }
    }

    [Fact]
    [Trait("Os", "Windows")]
    public async Task OnWindows_TheWindowsImplementationsAreTheDefaultsOfAProcessNode()
    {
        // Every [SupportedOSPlatform("windows")] registration of the composition, with no fake in the way.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Windows implementations exist only on Windows.");
            return;
        }

        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(windowsConcretes: true, ct: Ct);

        if (OperatingSystem.IsWindows())
        {
            AssertWindowsImplementations(app.Services);
        }
    }

    [Fact]
    [Trait("Os", "Linux")]
    public async Task OffWindows_WithoutAProcessHost_NothingProcessRelatedIsRegistered()
    {
        // The guards are OperatingSystem.IsWindows() and IsMacOS(): a Docker-off node that is neither and injected no
        // process host gets no driver, no catalogue and no services.scm, instead of a half-composed one.
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.Skip("On Windows and macOS the composition registers that OS's implementations itself.");
        }

        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(windowsConcretes: true, ct: Ct);
        var sp = app.Services;

        NodeCapabilities report = await sp.GetRequiredService<AgentCapabilityReporter>().GetAsync(Ct);

        Assert.Empty(sp.GetServices<IRuntimeDriver>());
        Assert.Null(sp.GetService<ProcessRuntimeDriver>());
        Assert.Null(sp.GetService<IExecutableCatalog>());
        Assert.Null(sp.GetService<IGgufReader>());
        Assert.DoesNotContain(AgentFeatures.RuntimeLlamaCppProcess, report.Features);
        Assert.DoesNotContain(AgentFeatures.GgufInspect, report.Features);
        Assert.DoesNotContain(AgentFeatures.ServicesScm, report.Features);

        // MN-234's ruling on item 7: such a node starts (it still enrolls, reports and serves host services), but it can
        // start no model at all, which the report shows only as an empty runtime list. The agent says so once at start.
        Assert.Contains(NoRuntimeDeliveryEvent, node.EventSnapshot);

        // Deliberate, and the one P3 flag that is *not* gated on process delivery (MN-234's ruling): a download needs a
        // disk and a Hub client, not a runtime driver, and the native downloader is the only one a node without Docker
        // can run. So downloads.hf-native is reported here and POST /downloads works, even though nothing can serve what
        // it fetches. Gating it on the process driver would leave such a node unable to download at all.
        Assert.Contains(AgentFeatures.DownloadsHfNative, report.Features);
        Assert.IsType<NativeDownloadService>(sp.GetRequiredService<IHuggingFaceDownloader>());
    }

    [Fact]
    [Trait("Os", "macOS")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public async Task OnMacOS_TheCompositionRegistersTheMacProcessHostAndFileKeyProtector()
    {
        // docs/macos/implementation-plan.md, Phase 2: a Docker-off Mac gets the process driver on its own seams.
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("Composes the macOS implementations.");
        }

        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(windowsConcretes: true, ct: Ct);
        var sp = app.Services;

        Assert.IsType<MirbadLLM.NodeRuntime.Runtimes.Process.Mac.MacProcessHost>(sp.GetRequiredService<IProcessHost>());
        Assert.IsType<FileInstanceKeyProtector>(sp.GetRequiredService<IInstanceKeyProtector>());
        Assert.NotNull(sp.GetService<ProcessRuntimeDriver>());
        Assert.Contains(sp.GetServices<IRuntimeDriver>(), d => d is ProcessRuntimeDriver);
        Assert.DoesNotContain(NoRuntimeDeliveryEvent, node.EventSnapshot);
    }

    // ================================================================ W12: ProcessRuntimeOptions and PortInUse

    [Fact]
    public async Task ProcessRuntimeOptions_ComeFromAgentProcess_AndTheNodesIdentityAndRoots()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(
            o =>
            {
                o.Process.StartTimeout = TimeSpan.FromMinutes(7);
                o.Process.RestartBudget = 5;
                o.Process.RestartWindow = TimeSpan.FromMinutes(3);
                o.Process.LogMaxBytes = 4096;
                o.Process.LogFilesKept = 9;
                o.Process.HealthPollInterval = TimeSpan.FromMilliseconds(250);
            },
            ct: Ct);

        ProcessRuntimeOptions options = ProcessOptionsOf(app.Services.GetRequiredService<ProcessRuntimeDriver>());

        Assert.Equal(node.Node.NodeId, options.NodeId);
        Assert.Equal(node.ModelsRoot, options.ModelsRoot);
        Assert.Equal(node.CacheRoot, options.CacheRoot);
        Assert.Equal(node.StateDirectory, options.StateDirectory);
        Assert.Equal("127.0.0.1", options.UpstreamHost);
        Assert.Equal(TimeSpan.FromMinutes(7), options.StartTimeout);
        Assert.Equal(5, options.RestartBudget);
        Assert.Equal(TimeSpan.FromMinutes(3), options.RestartWindow);
        Assert.Equal(4096, options.LogMaxBytes);
        Assert.Equal(9, options.LogFilesKept);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.HealthPollInterval);
    }

    [Fact]
    public async Task AdmissionSkipsAPortAnotherProgramHolds_OnAProcessNodeOnly()
    {
        // D127: AdmissionOptions.PortInUse = ProcessPorts.IsTaken on a process node, null on the DGX. Proven through the
        // allocator: a listener on the first port of the range is skipped there and handed out on a container node.
        using var taken = new TcpListener(System.Net.IPAddress.Loopback, 0);
        taken.Start();
        int port = ((System.Net.IPEndPoint)taken.LocalEndpoint).Port;

        await using var processNode = await P3Node.CreateAsync(Ct);
        await using WebApplication processAgent = await processNode.ComposeAsync(o => o.PortRangeStart = port, ct: Ct);
        int chosen = await processAgent.Services.GetRequiredService<IPortAllocator>().AllocatePortAsync("m1", "op-1", Ct);

        await using var dgxNode = await P3Node.CreateAsync(Ct);
        await using WebApplication dgxAgent = await dgxNode.ComposeDgxAsync(Ct, o => o.PortRangeStart = port);

        Assert.NotEqual(port, chosen);
        Assert.True(chosen > port, "the process node did not skip the port another program holds");

        // And the delegate itself: ProcessPorts.IsTaken on the process node, null on the DGX (a container node keeps P1).
        Func<int, bool>? processCheck = AdmissionOf(processAgent).PortInUse;
        Assert.NotNull(processCheck);
        Assert.Equal(nameof(ProcessPorts.IsTaken), processCheck.Method.Name);
        Assert.True(processCheck(port));
        Assert.Null(AdmissionOf(dgxAgent).PortInUse);
        Assert.NotNull(AdmissionOf(processAgent).OnAdmission);
        Assert.Null(AdmissionOf(dgxAgent).OnAdmission);
    }

    [Fact]
    public async Task ReservationManager_RefusesAPerGpuBudgetMapThatMissesOneOfTheStartsGpus()
    {
        // MN-230: the per-GPU budgets are validated against the start's GPUs on a node that admits per GPU.
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        var reservations = app.Services.GetRequiredService<IReservationManager>();

        await Assert.ThrowsAsync<ArgumentException>(() => reservations.TryReserveAsync(
            new StartReservationRequest
            {
                OperationId = "op-budgets",
                ModelId = "m1",
                GpuUuids = [Gpu0, Gpu1],
                Admission = new AdmissionRequest
                {
                    BudgetGiB = 10,
                    ReserveGiB = 1,
                    GpuBudgetsGiB = new Dictionary<string, double> { [Gpu0] = 10 },
                },
            },
            Ct));
    }

    // ================================================================ W12: Docker off

    [Fact]
    public async Task DockerOff_HasNoDockerClientDriverEngineContributorOrFeature_AndNoHelperCleanup()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        var sp = app.Services;

        Assert.Same(DockerAvailability.Off, sp.GetRequiredService<DockerAvailability>());
        Assert.Null(sp.GetService<DockerEngineClient>());
        Assert.Null(sp.GetService<ContainerRuntimeDriver>());
        Assert.Null(sp.GetService<DockerProbe>()); // nothing can even wait for Docker before RunAsync
        Assert.Empty(sp.GetServices<IRuntimeDriver>().OfType<ContainerRuntimeDriver>());
        Assert.Empty(sp.GetServices<IHostedService>().OfType<DownloadHelperCleanup>());

        // The image engine is the gated one: it lists nothing and never calls Docker, and a pull is not supported.
        var images = sp.GetRequiredService<IContainerImageEngine>();
        Assert.IsNotType<DockerImageClient>(images);
        Assert.Empty(await images.ListAsync(Ct));
        Assert.Null(await images.InspectAsync("vllm/vllm-openai:latest", Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => images.PullAsync("vllm/vllm-openai", "latest", _ => { }, Ct));

        // No docker_unreachable, whatever the Docker endpoint is, and the Docker-only contributor does not exist.
        var reasons = await Health(sp, Ct);
        Assert.DoesNotContain(HealthReasonCodes.DockerUnreachable, reasons.Select(r => r.Code));
        Assert.Empty(sp.GetServices<ICapabilityContributor>().OfType<RuntimeCapabilityContributor>());

        // DockerAvailability.Filter() is the last capability contributor (MN-214).
        var contributors = sp.GetServices<ICapabilityContributor>().ToList();
        Assert.Equal("FilterContributor", contributors[^1].GetType().Name);

        // The storage contributor does not claim downloads.hf-container in the first place, so the filter is a backstop
        // and not the only thing that keeps the flag off a node that cannot run a download helper container.
        NodeCapabilities storage = await sp.GetRequiredService<StorageCapabilities>().ContributeAsync(Empty(), Ct);
        Assert.DoesNotContain(AgentFeatures.DownloadsHfContainer, storage.Features);
    }

    [Fact]
    public async Task ImagePull_OnANodeWithoutDocker_Answers503DockerUnavailable_BeforeAnOperationIsAccepted()
    {
        // D129 (3): POST /images/pull has no feature gate, so the pipeline refuses it; before MN-232 the pull was
        // accepted (202) and failed inside the engine with NotSupportedException.
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        await app.StartAsync(Ct);
        using var client = new AgentContractClient(node.Target(app));

        AgentReply reply = await client.SendAsync(
            AgentRoutes.ImagePull,
            [],
            new
            {
                operationId = "op-pull-1",
                issuedAt = DateTimeOffset.UtcNow,
                expiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                image = "vllm/vllm-openai:v0.11.0",
            },
            ct: Ct);
        AgentReply operations = await client.SendAsync(AgentRoutes.OperationsList, [], ct: Ct);

        Assert.Equal(503, reply.Status);
        Assert.Equal(ErrorCodes.DockerUnavailable, reply.Read<AgentErrorResponse>().Error.Code);
        Assert.DoesNotContain(OperationKinds.ImagePull, operations.Read<IReadOnlyList<OperationStatus>>().Select(o => o.Kind));
        Assert.Equal(503, ErrorMappings.AgentStatus(ErrorCodes.DockerUnavailable));

        // GET /images stays a read that answers truthfully (an empty list), not a 503.
        Assert.DoesNotContain(AgentRoutes.ImagesList, DockerAvailability.DockerOnlyRoutes);
        Assert.Equal(200, (await client.SendAsync(AgentRoutes.ImagesList, [], ct: Ct)).Status);
    }

    // ================================================================ W11/W12: the order before app.RunAsync

    [Fact]
    public async Task BeforeRunAsync_TheKeySweepRunsFirst_ThenTheProcessAdoption()
    {
        // §A.4.1 / D122: MN-215's sweep of the service profile's CNG key files runs before any credential is loaded, and
        // the process adoption (OpenProcess + the orphan sweep) after it, still before app.RunAsync.
        await using var node = await P3Node.CreateAsync(Ct);
        await node.SeedAdoptableRecordAsync(Ct);
        string staleKey = node.StageStaleServiceKey();

        var observations = new List<bool>();
        await using WebApplication app = await node.ComposeAsync(
            keySweep: node.ServiceAccountKeySweep(),
            observeOpen: () => observations.Add(File.Exists(staleKey)),
            ct: Ct);

        Assert.False(File.Exists(staleKey), "the key sweep did not delete the stale CNG key file");
        Assert.NotEmpty(observations);
        Assert.All(observations, stillThere => Assert.False(stillThere, "the process was adopted before the key sweep ran"));

        // The adoption ran as its own step before the reconciler, not lazily inside it: its log line comes first, so a
        // reservation the reconciler rebuilds already sees the adopted instance.
        int adoptionLine = node.EventSnapshot.IndexOf(ProcessesRecoveredEvent);
        int reconciledLine = node.EventSnapshot.IndexOf(ReconciledEvent);
        Assert.True(adoptionLine >= 0, "the agent never logged the process adoption");
        Assert.True(reconciledLine > adoptionLine, $"adoption at {adoptionLine}, reconciliation at {reconciledLine}");

        var status = Assert.Single(await app.Services.GetRequiredService<ProcessRuntimeDriver>().ListAsync(Ct));
        Assert.Equal(P3Node.SeededModelId, status.ModelId);
        Assert.Equal([P3Node.SeededModelId], app.Services.GetRequiredService<ProcessRuntimeDriver>().LastRecovery!.Adopted);
    }

    [Fact]
    public async Task AProcessInstanceKnownOnlyToProcessesJson_IsReconciled_AndReportedToTheMaster()
    {
        // MN-232 listed AgentReconciler.cs in its Touches and changed nothing, on the reasoning that "reconciliation
        // covers process records" follows from RecoverAsync() running first plus RuntimeOperations' driver aggregation.
        // MN-234 confirms the reasoning and supplies the test it was missing: no test joined the two halves, so nothing
        // would have caught a reconciler that only ever asked the container driver.
        //
        // "Known only to processes.json" is the load-bearing part: the operation log is deleted, so the launch that
        // created the record is not recoverable and the reconciler must find the instance by listing every driver —
        // the path that would be dead if RuntimeOperations stopped aggregating.
        await using var node = await P3Node.CreateAsync(Ct);
        await node.SeedAdoptableRecordAsync(Ct);
        string operations = Path.Combine(node.StateDirectory, "operations");
        Assert.True(Directory.Exists(operations), "the seeding run wrote no operation log");
        Directory.Delete(operations, recursive: true);

        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        await app.StartAsync(Ct);

        // The reconciler itself: a second, idempotent pass over an empty operation log still adopts the instance, which
        // it can only have learned from the process driver's own listing.
        MirbadLLM.Agent.Recovery.ReconciliationReport report = await app.Services.GetRequiredService<MirbadLLM.Agent.Recovery.AgentReconciler>().RunAsync(Ct);
        Assert.Empty(report.Operations);
        Assert.Equal([P3Node.SeededModelId], report.Adopted.Select(a => a.ModelId));
        Assert.Equal(LaunchDelivery.Process, Assert.Single(report.Adopted).Delivery);

        // And the master's view: the instance is on /runtimes and in /status, as a process delivery.
        using var client = new AgentContractClient(node.Target(app));
        var runtimes = (await client.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct)).Read<IReadOnlyList<RuntimeStatus>>();
        Assert.Equal([P3Node.SeededModelId], runtimes.Select(r => r.ModelId));
        Assert.Equal(LaunchDelivery.Process, Assert.Single(runtimes).Delivery);
        var status = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>();
        Assert.Contains(P3Node.SeededModelId, status.Runtimes.Select(r => r.ModelId));

        // The negative control, so none of the above can pass vacuously: with the record gone, nothing is reported.
        await using var empty = await P3Node.CreateAsync(Ct);
        await using WebApplication bare = await empty.ComposeAsync(ct: Ct);
        await bare.StartAsync(Ct);
        Assert.Empty((await bare.Services.GetRequiredService<MirbadLLM.Agent.Recovery.AgentReconciler>().RunAsync(Ct)).Adopted);
        using var bareClient = new AgentContractClient(empty.Target(bare));
        Assert.Empty((await bareClient.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct)).Read<IReadOnlyList<RuntimeStatus>>());
    }

    [Fact]
    public async Task TheAdoptionOfAProcess_NeverWaitsForItsReadyProbe()
    {
        // The Ready probe of an adopted instance runs in the driver's own background supervision (§A.4.1). The seeded
        // instance's fake server takes a minute to load, so a start phase that awaited the probe would spend it before
        // app.RunAsync -- under the SCM, its whole 30 s.
        await using var slow = await P3Node.CreateAsync(Ct);
        await slow.SeedAdoptableRecordAsync(Ct, loadMs: 60_000);
        var elapsed = Stopwatch.StartNew();

        await using WebApplication adopting = await slow.ComposeAsync(ct: Ct);
        elapsed.Stop();

        // LastRecovery before anything asks the driver: RecoverAsync ran in Program.CreateAsync, not lazily on first use.
        var driver = adopting.Services.GetRequiredService<ProcessRuntimeDriver>();
        ProcessRecoveryReport? recovery = driver.LastRecovery;
        Assert.NotNull(recovery);
        Assert.Equal([P3Node.SeededModelId], recovery.Adopted);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15), "the start phase waited for the Ready probe");
        var adopted = Assert.Single(await driver.ListAsync(Ct));
        Assert.Equal((P3Node.SeededModelId, ModelState.Starting), (adopted.ModelId, adopted.State));
    }

    [Fact]
    public async Task TheCataloguesFirstHashing_RunsAfterTheHostStarted()
    {
        // §A.4.1: the catalogue's first full hash is a BackgroundService, so it starts with the host and never before
        // the listener; until it has run, Resolve answers 422 executable_unverified and the master retries.
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        var catalog = app.Services.GetRequiredService<IExecutableCatalog>();
        var verifier = Assert.Single(app.Services.GetServices<IHostedService>().OfType<ExecutableCatalogVerifier>());

        // The hosted verifier has not run: a BackgroundService's ExecuteTask is only set when the host starts. (The
        // catalogue itself answers 422 executable_unverified until something hashed it — proven on a fresh one, because
        // the feature fold at the end of CreateAsync already asks it to List and so starts a hash of its own.)
        Assert.Null(verifier.ExecuteTask);
        Assert.Equal(
            ErrorCodes.ExecutableUnverified,
            Assert.Throws<AgentException>(() =>
                new ExecutableCatalog([LlamaCppVersionFolder.Options(node.Llama.Version, node.Llama)])
                    .Resolve(ExecutableNames.LlamaServer, null)).Code);

        await app.StartAsync(Ct);

        Assert.NotNull(verifier.ExecuteTask);
        await WaitUntilAsync(() => catalog.List() is [{ Verified: true }], "the catalogue was never verified");
        Assert.NotNull(catalog.Resolve(ExecutableNames.LlamaServer, null));
    }

    [Fact]
    public async Task OnAProcessNode_NoHostedServiceBlocksTheStartOfTheHost()
    {
        // The same audit as WindowsServiceHostingTests.NoHostedServiceBlocksTheStartOfTheHost, on the Docker-off
        // composition: a new plain IHostedService fails this until it is reviewed and listed there as well.
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);

        var reviewed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft.AspNetCore.Hosting.GenericWebHostService", // Kestrel itself
            "MirbadLLM.Agent.AgentDrain",                        // StartAsync returns completed; it only drains on stop
        };
        var unreviewed = app.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => !typeof(BackgroundService).IsAssignableFrom(t) && !reviewed.Contains(t.FullName!))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(unreviewed);
        Assert.Empty(app.Services.GetServices<IHostedService>().OfType<DownloadHelperCleanup>());
    }

    // ================================================================ W12: the native downloader's Hub client

    [Fact]
    public void TheSwitchInDi_IsTheOneTheCompositionUsed_EvenIfSomethingRegisteredAnotherFirst()
    {
        // MN-234: every gate inside AddMirbadLLMAgent reads its local DockerAvailability, but MapMirbadLLMAgent's
        // /images/pull refusal and HostServicesServiceCollectionExtensions.ContainersOrNull read it back out of DI. With
        // TryAdd, a pre-registered one won and the two disagreed: a node with no Docker client at all would accept a pull
        // again (the D129 (3) regression) and ContainersOrNull would resolve HostContainers during the capability fold.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(DockerAvailability.On); // "someone registered one first"
        var options = ProcessOptions(Temp());
        options.Docker.Enabled = false;
        services.AddMirbadLLMAgent(options, new AgentBootInfo(TimeProvider.System, "1.3.0-test"));

        using ServiceProvider sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<DockerAvailability>();
        Assert.Same(DockerAvailability.Off, resolved);
        Assert.False(resolved.Enabled);
        Assert.NotNull(resolved.Refuse(AgentRoutes.ImagePull)); // the pipeline still refuses the pull
    }

    [Fact]
    public void TheNativeDownloadersHubClient_DoesNotFollowRedirectsItself()
    {
        // §A.8: SecureDownloadTransport follows the Hub's redirect to its CDN itself, checking every hop against the
        // HTTPS rule and keeping the token on the Hub's own origin, so the handler must not redirect behind its back.
        using ServiceProvider sp = Collection(ProcessOptions(Temp()), docker: false).BuildServiceProvider();
        var factory = sp.GetRequiredService<IHttpMessageHandlerFactory>();

        SocketsHttpHandler primary = PrimaryHandler(factory.CreateHandler(AgentServiceCollectionExtensions.NativeDownloadHttpClientName));
        Assert.False(primary.AllowAutoRedirect);

        // And the transport the downloader builds over that client is the one that follows them, token-scoped to the Hub.
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri("https://huggingface.co/") };
        var transport = new NativeSnapshotDownloader(client).Transport;
        Assert.Equal(new Uri("https://huggingface.co/"), transport.CredentialOrigin);
        Assert.True(transport.Options.MaxRedirects > 0);
    }

    // ================================================================ W12: the reserves survive a restart (D129 (2))

    [Fact]
    public async Task TheReservesOfALaunch_ArePersisted_AndJudgeExternalGpuUseRightAfterARestart()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        string reservesFile = Path.Combine(node.StateDirectory, GpuReserveStore.FileName);

        await using (WebApplication first = await node.ComposeAsync(ct: Ct))
        {
            Assert.False(File.Exists(reservesFile));
            var reserved = await first.Services.GetRequiredService<IReservationManager>().TryReserveAsync(
                new StartReservationRequest
                {
                    OperationId = "op-reserves",
                    ModelId = "m1",
                    GpuUuids = [Gpu1],
                    Admission = new AdmissionRequest
                    {
                        BudgetGiB = 1,
                        ReserveGiB = 1,
                        GpuReservesGiB = new Dictionary<string, double> { [Gpu0] = 2.5, [Gpu1] = 3 },
                    },
                },
                Ct);
            Assert.Equal(ReservationOutcome.Reserved, reserved.Outcome);
            Assert.True(File.Exists(reservesFile), "the learned reserves were not written to the state directory");
        }

        // A new agent over the same state directory: no launch yet, but the reserves are seeded, so one sample above
        // GPU 0's 2.5 GiB reserve twice raises gpu_external_use_high.
        await using WebApplication second = await node.ComposeAsync(ct: Ct);
        var external = second.Services.GetRequiredService<ExternalGpuUseContributor>();
        var sample = new NodeSample
        {
            Seq = 1,
            At = DateTimeOffset.UtcNow,
            Memory = new NodeMemorySnapshot { Model = MemoryModel.Discrete, HostTotalGiB = 127, HostAvailableGiB = 90 },
            Gpus = [new GpuSample { Index = 0, Uuid = Gpu0, Name = "RTX 5070", MemoryTotalGiB = 11.94, MemoryUsedGiB = 9 }],
        };

        await external.ObserveAsync(sample, Ct);
        await external.ObserveAsync(sample, Ct);
        var raised = Assert.Single(await external.CheckAsync(Ct));

        Assert.Equal(HealthReasonCodes.GpuExternalUseHigh, raised.Code);
        Assert.Contains("2.5 GiB reserve", raised.Message, StringComparison.Ordinal);
        Assert.Equal(new Dictionary<string, double> { [Gpu0] = 2.5, [Gpu1] = 3 }, new GpuReserveStore(reservesFile).Load());
    }

    [Fact]
    public void TheReserveStore_WritesOnlyWhenTheReservesChanged_AndSurvivesADamagedFile()
    {
        string directory = Temp();
        string path = GpuReserveStore.PathFor(directory);
        var store = new GpuReserveStore(path);

        Assert.Empty(store.Load());
        store.Remember(Admission(new Dictionary<string, double> { [Gpu0] = 4 }));
        DateTime written = File.GetLastWriteTimeUtc(path);

        store.Remember(Admission(new Dictionary<string, double> { [Gpu0] = 4 }));         // unchanged: no write
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        store.Remember(new AdmissionRequest { BudgetGiB = 1, ReserveGiB = 1 });            // no reserves at all: no write
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));

        store.Remember(Admission(new Dictionary<string, double> { [Gpu0] = 4, [Gpu1] = 6 }));
        Assert.Equal(new Dictionary<string, double> { [Gpu0] = 4, [Gpu1] = 6 }, new GpuReserveStore(path).Load());

        File.WriteAllText(path, "{ not json");
        Assert.Empty(new GpuReserveStore(path).Load());

        // MN-234: a write that fails must be retried by the next launch. The store used to mark the reserves as held
        // before the write, so one transient IOException (an AV lock, a full disk) left D129 (2) permanently off: every
        // later launch with the same reserves stopped at the "unchanged" check and the file was never written again.
        string retry = GpuReserveStore.PathFor(Temp());
        var retrying = new GpuReserveStore(retry);
        var reserves = new Dictionary<string, double> { [Gpu0] = 7 };
        using (File.Open(retry, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            retrying.Remember(Admission(reserves)); // the write cannot succeed; nothing may be remembered
        }

        retrying.Remember(Admission(reserves));
        Assert.Equal(reserves, new GpuReserveStore(retry).Load());

        static AdmissionRequest Admission(Dictionary<string, double> reserves) =>
            new() { BudgetGiB = 1, ReserveGiB = 1, GpuReservesGiB = reserves };
    }

    // ================================================================ W13: the per-instance upstream resolver (D130)

    [Fact]
    public async Task TheUpstreamResolver_IsPerInstance_WithAContainerAndAProcessDriverRegistered()
    {
        // Before MN-232 RuntimeDriverUpstreamResolver took one IRuntimeDriver, so DI handed it the last registration and
        // a process model's upstream resolved as "not running". AgentProxy's key lookup already walked every driver.
        var container = new StubDriver(LaunchDelivery.Container, "vllm-model", new Uri("http://ss-vllm-model:8000/"), apiKey: null);
        var process = new StubDriver(LaunchDelivery.Process, "llama-model", new Uri("http://127.0.0.1:8101/"), apiKey: "per-instance-key");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IRuntimeDriver>(container);
        services.AddSingleton<IRuntimeDriver>(process);
        services.AddAgentProxy();
        await using ServiceProvider sp = services.BuildServiceProvider();

        var resolver = Assert.IsType<RuntimeDriverUpstreamResolver>(sp.GetRequiredService<IProxyUpstreamResolver>());

        Assert.Equal(new Uri("http://ss-vllm-model:8000/"), await resolver.ResolveAsync("vllm-model", Ct));
        Assert.Equal(new Uri("http://127.0.0.1:8101/"), await resolver.ResolveAsync("llama-model", Ct));
        Assert.Null(await resolver.ResolveAsync("unknown", Ct));

        // The owner is remembered: the second resolve of the process model costs one call, to the process driver only.
        int containerCalls = container.UpstreamCalls;
        Assert.Equal(new Uri("http://127.0.0.1:8101/"), await resolver.ResolveAsync("llama-model", Ct));
        Assert.Equal(containerCalls, container.UpstreamCalls);

        // And the key of a process instance resolves through the drivers the proxy was given.
        Assert.Equal("per-instance-key", await UpstreamApiKeyAsync(sp.GetRequiredService<AgentProxy>(), "llama-model"));
        Assert.Null(await UpstreamApiKeyAsync(sp.GetRequiredService<AgentProxy>(), "vllm-model"));

        // MN-234: one driver that cannot answer must not silence the other. On the node this fold exists for — two
        // drivers, as MN-236's Windows dev setup composes (the fake runtime next to the process driver) — a Docker
        // outage makes the container driver throw, and a healthy llama.cpp model's /v1 traffic must still be forwarded.
        container.FailUpstream = true;
        resolver.Invalidate("llama-model", new Uri("http://127.0.0.1:8101/"));
        resolver.Invalidate("vllm-model", new Uri("http://ss-vllm-model:8000/"));
        Assert.Equal(new Uri("http://127.0.0.1:8101/"), await resolver.ResolveAsync("llama-model", Ct));
        Assert.Null(await resolver.ResolveAsync("vllm-model", Ct)); // its own driver is the one that is broken
    }

    [Fact]
    public async Task TheCompositionsResolver_AsksEveryDriverOfTheNode_NotJustTheLastRegistration()
    {
        // The composition's own resolver, not a hand-built one: a second driver registered before AddMirbadLLMAgent (the
        // dev fake runtime, MN-042/MN-236) sits in the enumerable next to the process driver, and a model only that
        // driver owns must still resolve. MN-234: with the fold reduced to the first driver this goes red, which the
        // previous version of this test did not (it only asserted the constructor's parameter type).
        var extra = new StubDriver(LaunchDelivery.Container, "dev-fake-model", new Uri("http://127.0.0.1:9101/"), apiKey: null);
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(
            extraServices: services => services.AddSingleton<IRuntimeDriver>(extra), ct: Ct);

        var resolver = Assert.IsType<RuntimeDriverUpstreamResolver>(app.Services.GetRequiredService<IProxyUpstreamResolver>());
        Assert.Single(app.Services.GetServices<IProxyUpstreamResolver>());
        Assert.Equal(2, app.Services.GetServices<IRuntimeDriver>().Count());
        Assert.Equal(
            typeof(IEnumerable<IRuntimeDriver>),
            Assert.Single(typeof(RuntimeDriverUpstreamResolver).GetConstructors()).GetParameters()[0].ParameterType);

        Assert.Equal(new Uri("http://127.0.0.1:9101/"), await resolver.ResolveAsync("dev-fake-model", Ct));
        Assert.True(extra.UpstreamCalls > 0, "the composed resolver never asked the second driver");
        Assert.Null(await resolver.ResolveAsync("not-running", Ct));
    }

    // ================================================================ W13: catalogue, health and SCM details

    [Fact]
    public async Task ATamperedExecutableVersion_IsUnverified_AndRaisesExecutableUnverified()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        node.Llama.Tamper("llama.dll", keepTime: false); // a planted or half-updated DLL, before anything hashed it
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        await app.StartAsync(Ct);
        var catalog = app.Services.GetRequiredService<IExecutableCatalog>();
        await WaitUntilAsync(
            () => catalog.List() is [{ Verified: false, Problem: not null and not ExecutableCatalog.NotVerifiedYet and not ExecutableCatalog.ReVerifying }],
            "the catalogue never reported the mismatch");

        var state = Assert.Single(catalog.List());
        Assert.False(state.Verified);
        var reason = Assert.Single(await Health(app.Services, Ct), r => r.Code == HealthReasonCodes.ExecutableUnverified);
        Assert.Contains(node.Llama.Version, reason.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.ExecutableUnverified,
            Assert.Throws<AgentException>(() => catalog.Resolve(ExecutableNames.LlamaServer, null)).Code);
    }

    [Fact]
    public async Task TheScmHostServices_KeepTheWhitelistInTheStateDirectory_AndDenyTheAgentsOwnService()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        var sp = app.Services;

        Assert.Equal(
            Path.Combine(node.StateDirectory, ScmWhitelist.FileName),
            sp.GetRequiredService<ScmHostServicesOptions>().WhitelistPath);
        Assert.Equal(Path.Combine(node.StateDirectory, ScmWhitelist.FileName), sp.GetRequiredService<ScmWhitelist>().FilePath);
        Assert.Equal(WindowsServiceHosting.ServiceName, sp.GetRequiredService<ScmDenyList>().Match(WindowsServiceHosting.ServiceName));
        Assert.NotNull(sp.GetRequiredService<ScmDenyList>().Match("WinDefend"));
        Assert.Same(node.Scm, sp.GetRequiredService<IServiceControlManager>());
    }

    // ================================================================ MN-239: one log line per event id

    [Fact]
    public void EveryLogEventIdOfTheAgentProcess_IsUsedByExactlyOneMessage()
    {
        // MN-239 H1. Event ids 2640–2644 were each used by *two* messages: the P2 memory-model lines
        // (AgentMemoryModel, operations-p2 §5.1) and the P3 lines W14 added (GpuReserveStore, AgentCompositionLog and
        // Program). An id in the Windows Event Log therefore no longer identified a line, and operations-p3p4 §11.5 had
        // to tell operators to "match the text, not the id". The P3 lines moved to the free 2680–2684 block and that
        // stopgap is gone; this keeps it that way.
        // The scan is over the sources of both assemblies that log into the agent's own Event Log — the agent and the
        // node runtime (the host agent is a separate Linux process with its own 1–17 range) — so it holds whether or
        // not the LoggerMessage generator keeps the attribute in metadata.
        var sites = new Dictionary<int, List<string>>();
        foreach (string project in new[] { "MirbadLLM.Agent", "MirbadLLM.NodeRuntime" })
        {
            foreach (string file in Directory.EnumerateFiles(RepoFile("src", project), "*.cs", SearchOption.AllDirectories))
            {
                // Hand-written sources only: a build with the default output layout would otherwise also offer the
                // generator's own copies under obj\ (the repo puts them in artifacts\, but a stray obj\ is harmless).
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (EventIdOn(lines[i]) is not { } id)
                    {
                        continue;
                    }

                    if (!sites.TryGetValue(id, out List<string>? where))
                    {
                        where = sites[id] = [];
                    }

                    where.Add($"{project}/{Path.GetFileName(file)}:{i + 1}");
                }
            }
        }

        // The scan has to see the ids this review moved, or it proves nothing at all.
        Assert.Contains(2684, sites.Keys); // Program's "no process delivery"
        Assert.Contains(2644, sites.Keys); // AgentMemoryModel's "Agent:MemoryModel forces …", which keeps the old id

        string[] shared = [.. sites.Where(kv => kv.Value.Count > 1)
            .OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key} → {string.Join(", ", kv.Value)}")];
        Assert.True(shared.Length == 0, "Event ids used by more than one log message: " + string.Join("; ", shared));
    }

    // ================================================================ MN-239: the two composition gates it carried

    [Fact]
    public async Task AnScmNode_ReportsOnlyTheScmServices_EvenWithSystemdUnitsManaged()
    {
        // MN-239 item 16. The Linux HostServicesContributor is registered on every node, including this one, and until
        // now it read the operator's Agent:ManageSystemdUnits straight out of agent.json. It stayed silent on Windows
        // only because agent.windows.example.json turns the three switches off: flip them and the node reported
        // services.systemd and gpu-processes beside services.scm, backup-mount on a PC with no backup mount, and
        // hostd_missing — the Linux-worded reason of carried item 1 — about an SCM that answers fine. The composition
        // now configures that package with systemd and the backup mount off whenever the SCM owns the host services.
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(
            o =>
            {
                o.ManageSystemdUnits = true;
                o.ManageBackupMount = true;
                o.HostdExpected = true;
            },
            ct: Ct);

        NodeCapabilities report = await app.Services.GetRequiredService<AgentCapabilityReporter>().GetAsync(Ct);
        IReadOnlyList<HealthReason> reasons = await Health(app.Services, Ct);

        Assert.Contains(AgentFeatures.ServicesScm, report.Features);
        Assert.DoesNotContain(AgentFeatures.ServicesSystemd, report.Features);
        Assert.DoesNotContain(AgentFeatures.GpuProcesses, report.Features);
        Assert.DoesNotContain(AgentFeatures.BackupMount, report.Features);
        Assert.Equal(HostServiceManagers.Scm, report.HostServices!.Manager);
        Assert.Null(report.BackupMount);
        Assert.DoesNotContain(HealthReasonCodes.HostdMissing, reasons.Select(r => r.Code));

        // The SCM half is unaffected: its own options say "units managed", so the host-service routes still work.
        Assert.IsType<ScmHostClient>(app.Services.GetRequiredService<IHostdClient>());
    }

    [Fact]
    public async Task AProcessNodesStartConflict_IsWordedForLlamaCpp_AndTheDgxKeepsVllm()
    {
        // MN-239 H2: the wiring half. AdmissionTexts.StartConflict's own wording is proven in
        // ReservationTests.StartConflict_OnAProcessNode_NamesTheRuntimeThatNodeRuns; this is the line that proves the
        // composition turns it on for a process node and leaves the DGX alone.
        await using var node = await P3Node.CreateAsync(Ct);

        await using (WebApplication process = await node.ComposeAsync(ct: Ct))
        {
            Assert.True(AdmissionOf(process).ProcessDelivery);
        }

        await using (WebApplication dgx = await node.ComposeDgxAsync(Ct))
        {
            Assert.False(AdmissionOf(dgx).ProcessDelivery);
        }
    }

    [Fact]
    [Trait("Os", "Windows")]
    public async Task OnWindows_TheMemoryProbeIsTheWindowsOne_WhateverTheDockerSwitchSays()
    {
        // MN-239 item 15. The host memory reader follows the operating system, not the delivery. AddProcessRuntime
        // returned at the Docker switch before it reached the Windows seams, so a Windows host with Docker *on* kept
        // AddAgentTelemetry's TryAdd default, ProcMeminfoMemoryProbe — which off Linux answers a configurable dev fake
        // (121 GiB total, 60 GiB available). Admission, the host-RAM reserve and /status then believed it, which is
        // worse than an error because the numbers look plausible. design-p3p4 §A.4.3 names WindowsMemoryProbe as *the*
        // Windows probe; it is now registered on OperatingSystem.IsWindows() alone.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("WindowsMemoryProbe exists only on Windows.");
            return;
        }

        await using var node = await P3Node.CreateAsync(Ct);

        // Docker on, nothing injected: P2's composition, on this Windows host.
        await using WebApplication app = await node.ComposeDgxAsync(Ct, extraServices: s => s.RemoveAll<IMemoryProbe>());
        var sp = app.Services;
        Assert.IsType<WindowsMemoryProbe>(sp.GetRequiredService<IMemoryProbe>());

        // Only the probe moved. The process host and the key protector stay behind the Docker-off gate, so this node is
        // still P2's: the container driver, no process driver, no catalogue, no services.scm.
        Assert.Null(sp.GetService<IProcessHost>());
        Assert.Null(sp.GetService<IInstanceKeyProtector>());
        Assert.IsType<ContainerRuntimeDriver>(Assert.Single(sp.GetServices<IRuntimeDriver>()));
        Assert.Null(sp.GetService<IExecutableCatalog>());
        Assert.Empty(sp.GetServices<ICapabilityContributor>().OfType<ScmHostServicesContributor>());
    }

    // ================================================================ W13: the master side D130 touched (MN-233 owns it)

    [Fact]
    public void TheMasterSideOfD130_StaysWired()
    {
        // MN-233 owns these files; this is the guard that the wiring MN-230 added is still there.
        // That the composition passes it is proven behaviourally by MN-233, in
        // P3CompositionTests.TheComposedJobService_MapsAClassicGoalToAWorkloadPreset_ForALlamaCppModel: a tune of a
        // llama.cpp model makes the master issue the GgufInspect route to the node. The source-text grep that used to
        // stand here is gone (MN-234): it passed on a comment and failed on a rename.
        ParameterInfo[] jobService = Assert.Single(
            typeof(MirbadLLM.Orchestration.Benchmarking.ModelJobService).GetConstructors()).GetParameters();
        Assert.Contains(jobService, p => p.Name == "inspectGguf");

        // PlacementFit keeps its agent client factory (the GGUF reads of the fit preview).
        Assert.Contains(
            Assert.Single(typeof(MirbadLLM.Orchestration.Multinode.PlacementFit).GetConstructors()).GetParameters(),
            p => p.ParameterType == typeof(IAgentClientFactory));

        // The last source-text tripwire — a grep of FakeAgent.Storage.cs for "new GgufChoiceDetails(" — is gone
        // (MN-239 item 14). It passed on a comment and broke on a rename. Driving the fake's own "several GGUFs"
        // refusal needs a fleet, so the replacement lives where there is one:
        // P3MasterExitTests.AFolderWithSeveralGgufs_RefusesWithTheNamesAsStructuredDetails_NotOnlyInTheSentence puts
        // two GGUFs in one folder, asks without ?file=, and reads the names off the structured details through the
        // editor's production path, with a message that names no files at all so a parse cannot pass it.
    }

    [Fact]
    public async Task ModelSourcing_PrefersGgufChoiceDetails_OverParsingTheRefusalText()
    {
        // MN-230: the agent's structured candidate list wins; the message parse stays for an older agent. MN-234: driven
        // through the production method, with a message that names *different* files, so the test cannot pass by reading
        // the sentence. (It used to grep ModelSourcing.cs for the call, which a comment would have satisfied.)
        var error = new AgentError
        {
            Code = ErrorCodes.InvalidRequest,
            Message = "The folder holds several GGUF models; name one with ?file=: z.gguf, y.gguf",
            Details = JsonSerializer.SerializeToElement(new GgufChoiceDetails(["a.gguf", "b.gguf"]), AgentJson.Options),
        };

        var structured = await MirbadLLM.Web.ModelSourcing.InspectGgufAsync(Provider(error), "win-1", "Qwen3-Embedding-4B-GGUF", null, Ct);
        Assert.Null(structured.Summary);
        Assert.Equal(["a.gguf", "b.gguf"], structured.Candidates);
        Assert.Equal(error.Message, structured.Error);

        // An older agent sends no details: the sentence is parsed, exactly as before.
        var parsed = await MirbadLLM.Web.ModelSourcing.InspectGgufAsync(
            Provider(error with { Details = null }), "win-1", "Qwen3-Embedding-4B-GGUF", null, Ct);
        Assert.Equal(["z.gguf", "y.gguf"], parsed.Candidates);

        static IServiceProvider Provider(AgentError error)
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAgentClientFactory>(new RefusingAgents(error));
            return services.BuildServiceProvider();
        }
    }

    // ================================================================ the example configuration

    [Fact]
    public void AgentWindowsExampleJson_BindsAndValidates_AndAgreesWithTheComposition()
    {
        AgentOptions options = AgentOptions.FromConfiguration(new ConfigurationBuilder()
            .AddJsonFile(RepoFile("deploy", "agent", "agent.windows.example.json"), optional: false, reloadOnChange: false)
            .Build());

        options.Validate(); // again, explicitly: the example must pass the validation MN-232 added
        Assert.False(options.Docker.Enabled);
        Assert.Same(DockerAvailability.Off, DockerAvailability.From(options));
        Assert.False(options.ManageSystemdUnits);
        Assert.False(options.ManageContainers);
        Assert.False(options.ManageBackupMount);
        Assert.False(options.HostdExpected);
        Assert.False(string.IsNullOrWhiteSpace(options.CacheRoot));

        var llama = Assert.Single(options.Executables);
        Assert.Equal(ExecutableNames.LlamaServer, llama.Name);
        var version = Assert.Single(llama.Versions);
        Assert.Equal(llama.Default, version.Version);
        Assert.Equal("13.4", version.Cuda);
        Assert.EndsWith(ExecutableCatalog.ManifestFileName, version.Manifest, StringComparison.Ordinal);

        Assert.Equal(TimeSpan.FromMinutes(10), options.Process.StartTimeout);
        Assert.Equal(2, options.Process.RestartBudget);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Process.RestartWindow);
        Assert.Equal(50L * 1024 * 1024, options.Process.LogMaxBytes);
        Assert.Equal(2, options.Process.LogFilesKept);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Process.HealthPollInterval);
    }

    [Fact]
    public async Task AProcessNodeWithoutACacheRoot_RefusesToStart_WithAReasonMainCanPrint()
    {
        // Agent:CacheRoot is mandatory on a process node ({cache} in a native launch's argv). It cannot live in
        // AgentOptions.Validate: the frozen P3ContractTests bind a Docker-off agent.json without one. The composition
        // refuses with an ArgumentException, and Program turns it into an AgentStartupException — the only shape Main
        // catches, so the operator reads the reason and the Windows Event Log carries it instead of an unhandled crash
        // under the SCM (MN-234).
        await using var node = await P3Node.CreateAsync(Ct);

        var startup = await Assert.ThrowsAsync<AgentStartupException>(
            () => node.ComposeAsync(o => o.CacheRoot = null, ct: Ct));
        Assert.Contains("Agent:CacheRoot is required", startup.Message, StringComparison.Ordinal);
        Assert.Contains(DockerAvailability.ConfigurationKey, startup.Message, StringComparison.Ordinal);

        // The composition itself keeps refusing on its own, for any caller that does not go through Program. The process
        // host and the key protector have to be there or the gate never reaches the cache-root check: off Windows the
        // composition registers neither by itself, which is the whole point of the gate.
        var options = node.Options();
        options.CacheRoot = "  ";
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IProcessHost>(node.Host);
        services.AddSingleton<IInstanceKeyProtector>(node.Keys);
        var direct = Assert.Throws<ArgumentException>(
            () => services.AddMirbadLLMAgent(options, new AgentBootInfo(TimeProvider.System, "1.3.0-test")));
        Assert.Contains("Agent:CacheRoot is required", direct.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewAgentOptions_AreValidated()
    {
        Assert.Contains("Versions", Refused(o => o.Executables.Add(new ExecutableOptions { Name = ExecutableNames.LlamaServer })));
        Assert.Contains("Name", Refused(o => o.Executables.Add(new ExecutableOptions())));
        Assert.Contains("Path and a Manifest", Refused(o => o.Executables.Add(WithVersions(("b1", "", "")))));
        Assert.Contains("twice", Refused(o =>
        {
            o.Executables.Add(WithVersions(("b1", "/p", "/m"), ("b1", "/p", "/m")));
        }));

        // MN-234: and twice as two entries. ExecutableCatalog derives the default per entry, so two blocks named
        // llama-server could both carry IsDefault and Find(name, null) would take whichever JSON ordering put first —
        // the node's default llama.cpp version, and with it every launch fingerprint, decided by file order.
        Assert.Contains("put every version of an executable in one entry", Refused(o =>
        {
            o.Executables.Add(WithVersions(("b1", "/p1", "/m1")));
            o.Executables.Add(WithVersions(("b2", "/p2", "/m2")));
        }));
        Assert.Contains("Default", Refused(o => o.Executables.Add(WithVersionsAnd("b2", [("b1", "/p", "/m")]))));
        Assert.Contains("StartTimeout", Refused(o => o.Process.StartTimeout = TimeSpan.Zero));
        Assert.Contains("RestartWindow", Refused(o => o.Process.RestartWindow = TimeSpan.Zero));
        Assert.Contains("HealthPollInterval", Refused(o => o.Process.HealthPollInterval = TimeSpan.Zero));
        Assert.Contains("RestartBudget", Refused(o => o.Process.RestartBudget = -1));
        Assert.Contains("LogFilesKept", Refused(o => o.Process.LogFilesKept = -1));
        Assert.Contains("LogMaxBytes", Refused(o => o.Process.LogMaxBytes = 0));

        // The DGX's own configuration still validates (its P1/P2 sections are untouched by the new rules).
        AgentOptions.FromConfiguration(new ConfigurationBuilder()
            .AddJsonFile(RepoFile("deploy", "agent", "agent.dgx.json"), optional: false, reloadOnChange: false).Build());

        static string Refused(Action<AgentOptions> configure)
        {
            var options = new AgentOptions { NodeId = "win-1", StateDirectory = Temp(), CacheRoot = Temp() };
            configure(options);
            return Assert.Throws<ArgumentException>(options.Validate).Message;
        }

        static ExecutableOptions WithVersions(params (string Version, string Path, string Manifest)[] versions) =>
            WithVersionsAnd(null, versions);

        static ExecutableOptions WithVersionsAnd(string? defaultVersion, (string Version, string Path, string Manifest)[] versions)
        {
            var options = new ExecutableOptions { Name = ExecutableNames.LlamaServer, Default = defaultVersion };
            foreach (var (version, path, manifest) in versions)
            {
                options.Versions.Add(new ExecutableVersionOptions { Version = version, Path = path, Manifest = manifest });
            }

            return options;
        }
    }

    // ================================================================ helpers

    /// <summary>An empty report, for folding one contributor on its own.</summary>
    private static NodeCapabilities Empty() => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "1.3.0-test",
        Os = new NodeOsInfo("windows", "Windows", "10.0"),
        Arch = "amd64",
        Cpu = new NodeCpuInfo(32),
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 127),
    };

    /// <summary>The event id of a <c>[LoggerMessage(EventId = N, …)]</c> line, or null when the line declares none.</summary>
    private static int? EventIdOn(string line)
    {
        int at = line.IndexOf("EventId", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        int i = at + "EventId".Length;
        while (i < line.Length && (line[i] == ' ' || line[i] == '='))
        {
            i++;
        }

        int start = i;
        while (i < line.Length && char.IsAsciiDigit(line[i]))
        {
            i++;
        }

        return i > start ? int.Parse(line.AsSpan(start, i - start), CultureInfo.InvariantCulture) : null;
    }

    private static IReadOnlyList<ITelemetrySampleObserver> ObserversOf(TelemetrySampler sampler) =>
        (IReadOnlyList<ITelemetrySampleObserver>)typeof(TelemetrySampler)
            .GetField("_observers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(sampler)!;

    private static AdmissionOptions AdmissionOf(WebApplication app) =>
        (AdmissionOptions)typeof(ReservationManager)
            .GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(app.Services.GetRequiredService<ReservationManager>())!;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertWindowsImplementations(IServiceProvider sp)
    {
        Assert.IsType<WindowsProcessHost>(sp.GetRequiredService<IProcessHost>());
        Assert.IsType<InstanceKeyProtector>(sp.GetRequiredService<IInstanceKeyProtector>());
        Assert.IsType<WindowsMemoryProbe>(sp.GetRequiredService<IMemoryProbe>());
        Assert.IsType<ScmHostClient>(sp.GetRequiredService<IHostdClient>());
        Assert.IsType<WindowsServiceControlManager>(sp.GetRequiredService<IServiceControlManager>());
    }

    private static async Task<IReadOnlyList<HealthReason>> Health(IServiceProvider services, CancellationToken ct)
    {
        var reasons = new List<HealthReason>();
        foreach (var contributor in services.GetServices<IHealthContributor>())
        {
            reasons.AddRange(await contributor.CheckAsync(ct));
        }

        return reasons;
    }

    private static ProcessRuntimeOptions ProcessOptionsOf(ProcessRuntimeDriver driver) =>
        (ProcessRuntimeOptions)typeof(ProcessRuntimeDriver)
            .GetField("_options", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(driver)!;

    private static Task<string?> UpstreamApiKeyAsync(AgentProxy proxy, string modelId) =>
        (Task<string?>)typeof(AgentProxy)
            .GetMethod("UpstreamApiKeyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(proxy, [modelId, CancellationToken.None])!;

    private static SocketsHttpHandler PrimaryHandler(HttpMessageHandler handler)
    {
        for (HttpMessageHandler? current = handler; current is not null;)
        {
            if (current is SocketsHttpHandler sockets)
            {
                return sockets;
            }

            current = current is DelegatingHandler delegating ? delegating.InnerHandler : null;
        }

        throw new InvalidOperationException("No SocketsHttpHandler in the chain.");
    }

    private static IServiceCollection Collection(AgentOptions options, bool docker)
    {
        options.Docker.Enabled = docker;
        var services = new ServiceCollection();
        services.AddLogging();
        return services.AddMirbadLLMAgent(options, new AgentBootInfo(TimeProvider.System, "1.3.0-test"));
    }

    private static AgentOptions ProcessOptions(string root) => new()
    {
        NodeId = "win-1",
        StateDirectory = Path.Combine(root, "state"),
        ModelsRoot = Path.Combine(root, "models"),
        CacheRoot = Path.Combine(root, "cache"),
        Port = 0,
    };

    private static string Temp()
    {
        string path = Path.Combine(Path.GetTempPath(), "ss-mn232-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what) =>
        await WaitUntilAsync(() => Task.FromResult(condition()), what);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(what);
    }

    private static string FixturesPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "Contracts", "Fixtures");

    private static string RepoFile(params string[] parts) =>
        Path.Combine([Path.GetFullPath(Path.Combine(RepoRoot(), ".")), .. parts]);

    private static string RepoRoot([CallerFilePath] string sourceFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", ".."));

    /// <summary>Agent clients whose <see cref="IAgentClient.InspectGgufAsync"/> always refuses with one error.</summary>
    private sealed class RefusingAgents(AgentError error) : IAgentClientFactory
    {
        public IAgentClient GetClient(string nodeId)
        {
            var client = DispatchProxy.Create<IAgentClient, RefusingClient>();
            ((RefusingClient)(object)client).Error = error;
            return client;
        }

        public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void RebuildHandlers(string nodeId)
        {
        }

        public void SetNegotiatedProtocol(string nodeId, int protocol)
        {
        }
    }

    public class RefusingClient : DispatchProxy
    {
        internal AgentError Error { get; set; } = new() { Code = ErrorCodes.InternalError, Message = "unset" };

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IAgentClient.InspectGgufAsync)
                ? Task.FromException<GgufSummary>(new AgentException(400, Error))
                : throw new NotSupportedException(targetMethod?.Name);
    }

    /// <summary>A runtime driver that owns exactly one model: the resolver must find it among several drivers.</summary>
    private sealed class StubDriver(LaunchDelivery delivery, string modelId, Uri upstream, string? apiKey) : IRuntimeDriver
    {
        private int _upstreamCalls;

        public int UpstreamCalls => Volatile.Read(ref _upstreamCalls);

        /// <summary>This driver's engine is unreachable: <see cref="GetUpstreamAsync"/> throws, as a Docker outage makes it.</summary>
        public bool FailUpstream { get; set; }

        public LaunchDelivery Delivery => delivery;

        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RuntimeStatus>>([]);

        public Task<RuntimeStatus?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<RuntimeStatus?>(null);

        public Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RuntimeStatus> StartCreatedAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopAsync(string id, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<LaunchConfig?> GetLaunchConfigAsync(string id, CancellationToken ct = default) => Task.FromResult<LaunchConfig?>(null);

        public IAsyncEnumerable<string> StreamLogsAsync(string id, int tail, bool follow, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Uri?> GetUpstreamAsync(string id, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _upstreamCalls);
            return FailUpstream
                ? Task.FromException<Uri?>(new TimeoutException("Docker did not answer GET /containers/json within 10 s."))
                : Task.FromResult(string.Equals(id, modelId, StringComparison.Ordinal) ? upstream : null);
        }

        public Task<string?> GetUpstreamApiKeyAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(string.Equals(id, modelId, StringComparison.Ordinal) ? apiKey : null);

        public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => Task.FromResult(false);
    }
}

/// <summary>
/// A node that runs models as native processes (MN-232's subject), composed by the real <c>Program.CreateAsync</c>. Only
/// the outside world is fake: the process table and host (MN-211's <see cref="FakeProcessHost"/>, whose "processes" are
/// the real fake llama-server in-process), DPAPI, the Service Control Manager, <c>nvidia-smi</c> (two discrete cards) and
/// the host memory probe. Composing a second time over the same node is an agent restart: a fresh process host over the
/// same table, with the old agent's job handles closed and the children still running.
/// </summary>
internal sealed class P3Node : IAsyncDisposable
{
    /// <summary>The model the seeded record belongs to.</summary>
    public const string SeededModelId = "qwen3-embedding-4b-win";

    private const string Alias = "qwen3-embedding-4b-win";
    private const string GgufFile = "Qwen3-Embedding-4B-GGUF/Qwen3-Embedding-4B-Q8_0.gguf";
    private const string Gpu1 = "GPU-7c1e9a52-3f60-4d2b-9a8e-5b0f2c6d1e44";

    private P3Node(ContractNode node, LlamaCppVersionFolder llama)
    {
        Node = node;
        Llama = llama;
        Host = new FakeProcessHost(Table);
        Directory.CreateDirectory(Path.Combine(node.ModelsRoot, Path.GetDirectoryName(GgufFile)!));
        File.WriteAllBytes(Path.Combine(node.ModelsRoot, GgufFile), [0x47, 0x47, 0x55, 0x46]);
    }

    public ContractNode Node { get; }

    public LlamaCppVersionFolder Llama { get; }

    public FakeProcessTable Table { get; } = new();

    public FakeProcessHost Host { get; private set; }

    public FakeInstanceKeyProtector Keys { get; } = new();

    public FakeScm Scm { get; } = new();

    public string StateDirectory => Node.StateDirectory;

    public string ModelsRoot => Node.ModelsRoot;

    public string CacheRoot => Node.CacheRoot;

    /// <summary>Event ids of the agent's own log lines, in the order they were written (the start order, for one).</summary>
    public List<int> Events { get; } = [];

    // The logger appends from host threads under this lock; read through a snapshot.
    public List<int> EventSnapshot { get { lock (Events) { return [.. Events]; } } }

    public static async Task<P3Node> CreateAsync(CancellationToken ct)
    {
        var node = await ContractNode.CreateAsync(enroll: true, ct, nodeId: "win-1");
        try
        {
            node.NvidiaSmi = FakeNvidiaSmi.Discrete(["GPU-0a4f2d61-8b3c-4e7a-b1d9-2c5e8f0a3b17", Gpu1]);
            return new P3Node(node, LlamaCppVersionFolder.Create("b11476", Path.Combine(node.Root, "install")));
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }
    }

    /// <summary>The options <c>agent.windows.example.json</c> has, pointed at this node's directories.</summary>
    public AgentOptions Options()
    {
        AgentOptions options = Node.CreateOptions();
        options.Docker.Enabled = false;
        options.UseContainerNetwork = false;
        options.UpstreamHost = "127.0.0.1";
        options.HostdExpected = false;
        options.ManageSystemdUnits = false;
        options.ManageContainers = false;
        options.ManageBackupMount = false;
        options.Executables.Add(LlamaCppVersionFolder.Options(Llama.Version, Llama));
        options.Process.HealthPollInterval = TimeSpan.FromMilliseconds(50);
        options.Process.StartTimeout = TimeSpan.FromSeconds(20);
        return options;
    }

    /// <summary>Composes the process agent (a restart when called again: a fresh host, the old handles closed).</summary>
    public Task<WebApplication> ComposeAsync(
        Action<AgentOptions>? configure = null,
        bool windowsConcretes = false,
        ServiceProfileKeySweep? keySweep = null,
        Action? observeOpen = null,
        Action<IServiceCollection>? extraServices = null,
        CancellationToken ct = default)
    {
        AgentOptions options = Options();
        configure?.Invoke(options);
        Host.SimulateAgentDeath();
        Host = new FakeProcessHost(Table);
        IProcessHost host = observeOpen is null ? Host : new ObservingProcessHost(Host, observeOpen);
        return ComposeCoreAsync(options, keySweep, services =>
        {
            if (windowsConcretes)
            {
                // Nothing injected: on Windows the composition's own [SupportedOSPlatform("windows")] registrations must
                // win, and off Windows a Docker-off node must end up without a process driver at all.
                services.RemoveAll<IMemoryProbe>();
                extraServices?.Invoke(services);
                return;
            }

            services.AddSingleton(host);
            services.AddSingleton<IInstanceKeyProtector>(Keys);
            services.AddSingleton<IServiceControlManager>(Scm);
            extraServices?.Invoke(services);
        }, ct);
    }

    /// <summary>
    /// The same node composed as the DGX is: Docker on, one GB10, P2's unified memory model. <paramref name="extraServices"/>
    /// adjusts the collection after the fakes (MN-239 item 15 removes the injected memory probe, so that the composition's
    /// own default for this host is what answers).
    /// </summary>
    public Task<WebApplication> ComposeDgxAsync(
        CancellationToken ct, Action<AgentOptions>? configure = null, Action<IServiceCollection>? extraServices = null)
    {
        AgentOptions options = Node.CreateOptions();
        options.MemoryModel = MemoryModel.Unified;
        options.AllowTagPulls = true;
        configure?.Invoke(options);
        return ComposeCoreAsync(options, keySweep: null, services => extraServices?.Invoke(services), ct);
    }

    /// <summary>A launch of the llama.cpp model through the composed driver, waited to Ready.</summary>
    public async Task<RuntimeStatus> LaunchAsync(WebApplication app, CancellationToken ct, int loadMs = 50)
    {
        var driver = app.Services.GetRequiredService<ProcessRuntimeDriver>();
        await app.Services.GetRequiredService<IExecutableCatalog>().RefreshAsync(ct);
        var result = await driver.LaunchAsync(
            new RuntimeLaunch
            {
                ModelId = SeededModelId,
                OperationId = "op-launch-1",
                Mode = LaunchMode.EnsureStarted,
                Spec = Spec(loadMs),
                HostPort = TestPorts.Reserve(),
            },
            new RecordingStages(),
            ct);
        Assert.NotNull(result.Instance);

        ModelState wanted = loadMs > 5_000 ? ModelState.Starting : ModelState.Ready;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (await driver.GetAsync(SeededModelId, ct) is { } status && status.State == wanted)
            {
                return status;
            }

            await Task.Delay(25, ct);
        }

        throw new TimeoutException("The launch never reached " + wanted + ".");
    }

    /// <summary>
    /// A process record the next composition can adopt: a first agent launches the model to Ready and then stops,
    /// leaving the process running and <c>processes.json</c> behind — what <c>RecoverAsync</c> finds at the next start.
    /// </summary>
    public async Task SeedAdoptableRecordAsync(CancellationToken ct, int loadMs = 50)
    {
        await using WebApplication first = await ComposeAsync(ct: ct);
        await first.StartAsync(ct);
        await LaunchAsync(first, ct, loadMs);
        await first.StopAsync(ct);
        using var store = new ProcessRecordStore(Path.Combine(StateDirectory, ProcessRecordsDocument.FileName));
        Assert.Single(await store.ListAsync(ct));
    }

    /// <summary>A CNG key file an unclean exit left in the service account's profile (MN-215, D122).</summary>
    public string StageStaleServiceKey()
    {
        string keys = Path.Combine(Node.Root, "appdata", "Microsoft", "Crypto", "Keys");
        Directory.CreateDirectory(keys);
        string file = Path.Combine(keys, "stale-cng-key");
        File.WriteAllText(file, "left behind by an unclean exit");
        return file;
    }

    /// <summary>The sweep as it runs under <c>NT SERVICE\mirbadllm-agent</c>, over this node's staged profile.</summary>
    public ServiceProfileKeySweep ServiceAccountKeySweep() =>
        new(ServiceProfileKeySweep.AgentServiceSid, Path.Combine(Node.Root, "appdata"), NullLogger.Instance);

    public AgentContractTarget Target(WebApplication app)
    {
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new AgentContractTarget(new Uri(address.TrimEnd('/') + "/"), Node.NodeId, Node.Ca, Node.MasterCertificate, Node.AgentSpki);
    }

    public async ValueTask DisposeAsync()
    {
        await Table.RebootAsync();
        Llama.Dispose();
        await Node.DisposeAsync();
    }

    private async Task<WebApplication> ComposeCoreAsync(
        AgentOptions options, ServiceProfileKeySweep? keySweep, Action<IServiceCollection> fakes, CancellationToken ct)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = Node.Root });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddProvider(new RecordingLoggerProvider(Events));
        WebApplication app = await AgentProgram.CreateAsync(builder, options, keySweep, startupLoggers: null, services =>
        {
            services.AddSingleton<IMemoryProbe>(new FixedMemoryProbe());
            services.AddSingleton<IDiskSpaceProbe>(new ContractNode.FixedDiskProbe());
            services.AddSingleton<INvidiaSmiRunner>(Node.NvidiaSmi!);
            services.AddSingleton(new AgentRuntimeOptions { WatchInterval = TimeSpan.FromMilliseconds(100) });
            services.Configure<TelemetryOptions>(o => o.SampleInterval = TimeSpan.FromMilliseconds(250));
            fakes(services);
        }, ct);
        return app;
    }

    /// <summary>The spec MN-210's builder emits for the OpenViking embedding model on a Windows node.</summary>
    private LaunchSpec Spec(int loadMs) => new()
    {
        Kind = LaunchDelivery.Process,
        Runtime = RuntimeKind.LlamaCpp,
        Executable = ExecutableNames.LlamaServer,
        ExecutableVersion = Llama.Version,
        Argv =
        [
            "-m", "{models}/" + GgufFile, "--alias", Alias, "-c", "16384", "-np", "2", "-ngl", "999", "-fa", "auto",
            "--embedding", "--pooling", "last", "--split-mode", "none", "--device", "CUDA0", .. LlamaCppFlags.AlwaysArgs,
        ],
        Env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FAKE_LLAMA_LOAD_MS"] = loadMs.ToString(CultureInfo.InvariantCulture),
        },
        SecretEnvNames = [],
        Gpus = new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [Gpu1] },
        Labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ContainerLabels.ServedName] = Alias,
            [ContainerLabels.ExecutionFingerprint] = "5e0c7a91d2b34f68",
            [ContainerLabels.BudgetGiB] = "8.9",
        },
    };

    private sealed class FixedMemoryProbe : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new MemorySnapshot(127, 100));
    }

    /// <summary>Records every log line's event id, so a test can assert the order of the start phases.</summary>
    private sealed class RecordingLoggerProvider(List<int> events) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Recorder(events);

        public void Dispose()
        {
        }

        private sealed class Recorder(List<int> events) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id != 0)
                {
                    lock (events)
                    {
                        events.Add(eventId.Id);
                    }
                }
            }
        }
    }

    private sealed class RecordingStages : IRuntimeStageSink
    {
        public Task BeforeStepAsync(string stage, CancellationToken ct) => Task.CompletedTask;

        public Task StageReachedAsync(string stage, OperationProgress? progress, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Runs <paramref name="observe"/> on every adoption, so a test can see what had already happened.</summary>
    private sealed class ObservingProcessHost(FakeProcessHost inner, Action observe) : IProcessHost
    {
        public Task<IHostedProcess> StartAsync(ProcessHostStart start, CancellationToken ct = default) => inner.StartAsync(start, ct);

        public Task<IHostedProcess?> OpenAsync(ProcessIdentity expected, string jobName, CancellationToken ct = default)
        {
            observe();
            return inner.OpenAsync(expected, jobName, ct);
        }

        public Task<IReadOnlyList<ProcessIdentity>> ListCandidatesAsync(string imageFolder, CancellationToken ct = default) =>
            inner.ListCandidatesAsync(imageFolder, ct);

        public Task<IReadOnlyList<string>?> ReadArgumentsAsync(ProcessIdentity identity, CancellationToken ct = default) =>
            inner.ReadArgumentsAsync(identity, ct);

        public Task<bool> KillAsync(ProcessIdentity identity, CancellationToken ct = default) => inner.KillAsync(identity, ct);
    }
}
