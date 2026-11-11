using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// <c>Agent:Accelerator = cpu</c>: a node without a usable GPU. The agents are composed by the real <c>Program</c>
/// (<see cref="P3Node"/>) with a fake <c>nvidia-smi</c> that lists two NVIDIA cards, so every assertion also shows that
/// the setting, not the absence of a driver, decides: the cards are never read.
/// </summary>
public sealed class AcceleratorTests
{
    private const string Gpu = "GPU-0a4f2d61-8b3c-4e7a-b1d9-2c5e8f0a3b17";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("cuda", null)]
    [InlineData("CPU", NodeAccelerators.Cpu)]
    [InlineData(" vulkan ", NodeAccelerators.Vulkan)]
    public void TheSetting_IsReadCaseInsensitively_AndCudaIsTheUnsetDefault(string? value, string? expected)
    {
        var options = Options();
        options.Accelerator = value;

        options.Validate();

        Assert.Equal(expected, options.EffectiveAccelerator);
        Assert.Equal(
            expected switch
            {
                NodeAccelerators.Cpu => ContainerGpuAccess.None,
                NodeAccelerators.Vulkan => ContainerGpuAccess.DeviceNodes,
                _ => ContainerGpuAccess.Nvidia,
            },
            options.ToContainerRuntimeOptions().GpuAccess);
    }

    [Fact]
    public void AnUnknownAccelerator_OrAMemoryModelThatContradictsIt_IsRefusedAtStart()
    {
        var unknown = Options();
        unknown.Accelerator = "rocm";
        Assert.Contains("cuda, cpu or vulkan", Assert.Throws<ArgumentException>(unknown.Validate).Message);

        var contradicting = Options();
        contradicting.Accelerator = "cpu";
        contradicting.MemoryModel = MemoryModel.Discrete;
        Assert.Contains("Agent:MemoryModel", Assert.Throws<ArgumentException>(contradicting.Validate).Message);
    }

    [Fact]
    public async Task ACpuContainerNode_ReportsCpu_NoGpu_OnlyLlamaCpp_AndIsNotDegraded()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeDgxAsync(Ct, o =>
        {
            o.MemoryModel = null;
            o.Accelerator = "cpu";
        });

        NodeCapabilities report = await app.Services.GetRequiredService<AgentCapabilityReporter>().GetAsync(Ct);

        Assert.Equal(NodeAccelerators.Cpu, report.Accelerator);
        Assert.Empty(report.Gpus); // the two NVIDIA cards of the fake nvidia-smi are never read
        Assert.Equal(MemoryModel.Unified, report.Memory.Model);
        Assert.DoesNotContain(report.Runtimes, r => r.Kind != RuntimeKind.LlamaCpp);
        if (OperatingSystem.IsLinux())
        {
            var llama = Assert.Single(report.Runtimes);
            Assert.Equal((RuntimeKind.LlamaCpp, LaunchDelivery.Container), (llama.Kind, llama.Delivery));
            Assert.Contains(AgentFeatures.RuntimeLlamaCppContainer, report.Features);
        }

        // No CUDA-only runtime is offered, and downloads do not need the vLLM image.
        Assert.DoesNotContain(AgentFeatures.RuntimeVllmContainer, report.Features);
        Assert.DoesNotContain(AgentFeatures.RuntimeSglangContainer, report.Features);
        Assert.DoesNotContain(AgentFeatures.DownloadsHfContainer, report.Features);
        Assert.Contains(AgentFeatures.DownloadsHfNative, report.Features);
        Assert.Single(app.Services.GetServices<ICapabilityContributor>().OfType<NativeDownloadContributor>());
        Assert.IsType<NativeDownloadService>(app.Services.GetRequiredService<IHuggingFaceDownloader>());

        // Per-GPU admission and GPU selection are not claimed, and no missing nvidia-smi degrades the node.
        Assert.DoesNotContain(AgentFeatures.AdmissionDiscrete, report.Features);
        Assert.DoesNotContain(AgentFeatures.GpuSelectionUuids, report.Features);
        Assert.DoesNotContain(HealthReasonCodes.GpuProbeFailing, (await Health(app.Services)).Select(r => r.Code));
    }

    [Fact]
    public async Task ACpuProcessNode_ReportsCpu_NoGpu_AndTheLlamaCppProcessRuntime()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(o => o.Accelerator = "cpu", ct: Ct);
        await app.StartAsync(Ct);
        var catalog = app.Services.GetRequiredService<IExecutableCatalog>();
        await catalog.RefreshAsync(Ct);

        NodeCapabilities report = await app.Services.GetRequiredService<AgentCapabilityReporter>().GetAsync(Ct);

        Assert.Equal(NodeAccelerators.Cpu, report.Accelerator);
        Assert.Empty(report.Gpus);
        Assert.Equal(MemoryModel.Unified, report.Memory.Model);
        var llama = Assert.Single(report.Runtimes);
        Assert.Equal((RuntimeKind.LlamaCpp, LaunchDelivery.Process), (llama.Kind, llama.Delivery));
        Assert.True(llama.Available);
        Assert.Contains(AgentFeatures.RuntimeLlamaCppProcess, report.Features);
        Assert.Contains(AgentFeatures.GgufInspect, report.Features);
        Assert.DoesNotContain(AgentFeatures.AdmissionDiscrete, report.Features);
        Assert.DoesNotContain(AgentFeatures.AdmissionGpuBudgets, report.Features);
        Assert.DoesNotContain(HealthReasonCodes.GpuProbeFailing, (await Health(app.Services)).Select(r => r.Code));
    }

    [Fact]
    public async Task WithoutTheSetting_TheSameNodeStillReportsItsNvidiaCards_AndNoAccelerator()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);

        NodeCapabilities report = await app.Services.GetRequiredService<AgentCapabilityReporter>().GetAsync(Ct);

        Assert.Null(report.Accelerator);
        Assert.Equal(2, report.Gpus.Count);
        Assert.Equal(MemoryModel.Discrete, report.Memory.Model);
        Assert.DoesNotContain("accelerator", System.Text.Json.JsonSerializer.Serialize(report, AgentJson.Options), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ACpuNode_CountsTheWholeBudgetAgainstHostMemory()
    {
        // The node's host probe answers 128 GiB total, 100 GiB available.
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(o => o.Accelerator = "cpu", ct: Ct);
        var reservations = app.Services.GetRequiredService<ReservationManager>();
        Assert.False(reservations.PerGpuAdmission);

        var refused = await reservations.TryReserveAsync(Request("op-1", "too-big", budgetGiB: 97, reserveGiB: 4), Ct);
        var admitted = await reservations.TryReserveAsync(Request("op-2", "fits", budgetGiB: 96, reserveGiB: 4), Ct);

        Assert.Equal(ReservationOutcome.AdmissionRefused, refused.Outcome);
        var details = System.Text.Json.JsonSerializer.Deserialize<AdmissionRefusedDetails>(refused.Error!.Details!.Value, AgentJson.Options)!;
        Assert.Equal((97d, 96d, 100d, 4d), (details.NeedGiB, details.FreeGiB, details.AvailableGiB, details.ReserveGiB));
        Assert.Null(details.Gpu);
        Assert.Equal(ReservationOutcome.Reserved, admitted.Outcome);
        Assert.Empty(admitted.Reservation!.Gpus);
    }

    [Fact]
    public async Task ACpuNode_CreatesItsContainerWithoutAnyGpuRequest()
    {
        var (driver, docker) = Driver(ContainerGpuAccess.None);

        await driver.LaunchAsync(
            new RuntimeLaunch { ModelId = "small", OperationId = "op-1", Mode = LaunchMode.Replace, Spec = Spec(new GpuSelection()), HostPort = 8101 },
            new ContainerRuntimeDriverTests.RecordingStageSink(),
            Ct);

        JsonObject host = docker.CreateBodies.Single()["HostConfig"]!.AsObject();
        Assert.False(host.ContainsKey("DeviceRequests"));
        Assert.False(host.ContainsKey("Devices"));
    }

    [Fact]
    public void ACpuNode_RefusesALaunchThatSelectsAGpu()
    {
        var (driver, _) = Driver(ContainerGpuAccess.None);

        var refused = Assert.Throws<AgentException>(() => driver.CheckSpec(
            "small", Spec(new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [Gpu] })));

        Assert.Equal(ErrorCodes.GpuMissing, refused.Error.Code);
    }

    // ------------------------------------------------------------------ helpers

    private static AgentOptions Options() => new()
    {
        NodeId = "pc-1",
        StateDirectory = Path.Combine(Path.GetTempPath(), "mirbad-accelerator-tests"),
        ModelsRoot = "/models",
    };

    private static StartReservationRequest Request(string operationId, string modelId, double budgetGiB, double reserveGiB) => new()
    {
        OperationId = operationId,
        ModelId = modelId,
        Admission = new AdmissionRequest { BudgetGiB = budgetGiB, ReserveGiB = reserveGiB },
    };

    private static LaunchSpec Spec(GpuSelection gpus) => new()
    {
        Kind = LaunchDelivery.Container,
        Runtime = RuntimeKind.LlamaCpp,
        Image = "ghcr.io/ggml-org/llama.cpp:server",
        Argv = ["-m", "/models/small/model.gguf", "--host", "0.0.0.0", "--port", "8000"],
        ContainerPort = 8000,
        Mounts = [new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true)],
        Gpus = gpus,
        IpcHost = true,
        Labels = new Dictionary<string, string> { [ContainerLabels.ServedName] = "small" },
    };

    private static (ContainerRuntimeDriver Driver, ContainerRuntimeDriverTests.FakeDockerEngine Docker) Driver(
        ContainerGpuAccess access, Func<string, IReadOnlyList<string>?>? deviceNodes = null, IGpuInventory? inventory = null)
    {
        var docker = new ContainerRuntimeDriverTests.FakeDockerEngine();
        var driver = new ContainerRuntimeDriver(
            docker.Client,
            new HttpClient(new FakeHttpMessageHandler { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) }),
            new ContainerRuntimeDriverTests.FakePortAllocator(),
            Microsoft.Extensions.Options.Options.Create(new ContainerRuntimeOptions { NodeId = "pc-1", GpuAccess = access }),
            NullLogger<ContainerRuntimeDriver>.Instance,
            gpuInventory: inventory ?? new NoGpus(),
            gpuDeviceNodes: deviceNodes);
        return (driver, docker);
    }

    private static async Task<IReadOnlyList<HealthReason>> Health(IServiceProvider services)
    {
        var reasons = new List<HealthReason>();
        foreach (var contributor in services.GetServices<IHealthContributor>())
        {
            reasons.AddRange(await contributor.CheckAsync(Ct));
        }

        return reasons;
    }

    /// <summary>The inventory of a node whose <c>nvidia-smi</c> is absent: nothing was ever read.</summary>
    private sealed class NoGpus : IGpuInventory
    {
        public IReadOnlyList<GpuCapability>? Last => null;

        public Task<IReadOnlyList<GpuCapability>> RefreshAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GpuCapability>>([]);
    }
}
