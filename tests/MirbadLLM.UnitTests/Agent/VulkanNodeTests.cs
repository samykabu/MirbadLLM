using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// <c>Agent:Accelerator = vulkan</c> (experimental): GPUs read without <c>nvidia-smi</c>. The parsers and rules are tested
/// with crafted structures, sysfs trees written here and fake sources; <see cref="OnThisPc_TheVulkanSource_AgreesWithNvidiaSmi"/>
/// is the one test that touches real hardware, and only reads it.
/// </summary>
public sealed class VulkanNodeTests : IDisposable
{
    private const string GpuA = "GPU-0a4f2d61-8b3c-4e7a-b1d9-2c5e8f0a3b17";
    private const string GpuB = "GPU-7c1e9a52-3f60-4d2b-9a8e-5b0f2c6d1e44";
    private const double GiB = 1024d * 1024 * 1024;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbad-vulkan-" + Guid.NewGuid().ToString("N")[..12]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ the Vulkan structures

    [Fact]
    public void AVulkanDevice_IsReadFromTheLoadersStructures_WithOnlyItsDeviceLocalHeapsAsVram()
    {
        byte[] uuid = Convert.FromHexString("8c822d0960c635e06c1ea9adb2a03ab7");
        byte[] properties = Properties(vendor: 0x1002, device: 0x744c, type: 2, name: "AMD Radeon RX 7900 XT");
        byte[] id = IdProperties(uuid, luid: 0x0001f262, luidValid: true);
        byte[] memory = Memory((20UL * 1024 * 1024 * 1024, true), (64UL * 1024 * 1024 * 1024, false), (256UL * 1024 * 1024, true));

        VulkanAdapter adapter = VulkanAdapters.Parse(2, properties, id, memory);

        Assert.Equal(2, adapter.Index);
        Assert.Equal("8c822d09-60c6-35e0-6c1e-a9adb2a03ab7", adapter.DeviceUuid);
        Assert.Equal("AMD Radeon RX 7900 XT", adapter.Name);
        Assert.Equal((0x1002u, 0x744cu, VulkanDeviceType.Discrete), (adapter.VendorId, adapter.DeviceId, adapter.Type));
        Assert.Equal(0x0001f262ul, adapter.Luid);
        Assert.Equal((20UL * 1024 * 1024 * 1024) + (256UL * 1024 * 1024), adapter.DedicatedBytes); // host-visible heaps are not VRAM

        // Without a valid LUID none is reported, and an unknown device type is not mistaken for a discrete card.
        VulkanAdapter other = VulkanAdapters.Parse(0, Properties(0x8086, 1, type: 9, "x"), IdProperties(uuid, luid: 5, luidValid: false), memory);
        Assert.Null(other.Luid);
        Assert.Equal(VulkanDeviceType.Other, other.Type);
    }

    [Theory]
    [InlineData("luid_0x00000000_0x0001bd18_phys_0", true, 0x0001bd18ul)]
    [InlineData("luid_0x00000002_0x0001F262_phys_1", true, 0x000000020001f262ul)]
    [InlineData("_Total", false, 0ul)]
    [InlineData("luid_0x00000000", false, 0ul)]
    [InlineData("luid_zz_0x1_phys_0", false, 0ul)]
    [InlineData(null, false, 0ul)]
    public void ACounterInstance_IsMatchedByItsLuid(string? instance, bool parsed, ulong expected)
    {
        Assert.Equal(parsed, WindowsGpuMemoryCounters.TryParseLuid(instance, out ulong luid));
        Assert.Equal(expected, luid);
    }

    // ------------------------------------------------------------------ Windows: adapters + system-wide usage

    [Fact]
    public void OnlyDiscreteAdapters_AreListed_WithTheMemoryInUseOfTheirOwnLuid()
    {
        VulkanAdapter[] adapters =
        [
            Adapter(0, "aaaaaaaa-0000-0000-0000-000000000001", "AMD Radeon RX 7800 XT", VulkanDeviceType.Discrete, luid: 11, gib: 16),
            Adapter(1, "bbbbbbbb-0000-0000-0000-000000000002", "Intel(R) Graphics", VulkanDeviceType.Integrated, luid: 12, gib: 72),
            Adapter(2, "cccccccc-0000-0000-0000-000000000003", "Intel(R) Arc(TM) A770", VulkanDeviceType.Discrete, luid: 13, gib: 16),
            Adapter(3, "aaaaaaaa-0000-0000-0000-000000000001", "AMD Radeon RX 7800 XT", VulkanDeviceType.Discrete, luid: 11, gib: 16), // a second driver
            Adapter(4, "00000000-0000-0000-0000-000000000000", "Unnamed", VulkanDeviceType.Discrete, luid: 14, gib: 8),
            Adapter(5, "dddddddd-0000-0000-0000-000000000004", "llvmpipe", VulkanDeviceType.Cpu, luid: null, gib: 0),
        ];
        var usage = new Dictionary<ulong, long> { [11] = (long)(3.5 * GiB), [12] = 0, [13] = (long)(0.25 * GiB), [99] = 1 };

        GpuAdapterRead read = VulkanGpuSource.ToRead(adapters, usage, usageError: null);

        Assert.Null(read.Error);
        Assert.Equal(
            [(0, "GPU-aaaaaaaa-0000-0000-0000-000000000001", 16d, 3.5), (2, "GPU-cccccccc-0000-0000-0000-000000000003", 16d, 0.25)],
            read.Adapters!.Select(a => (a.Index, a.Uuid, a.TotalGiB!.Value, a.UsedGiB!.Value)));
    }

    [Fact]
    public void MemoryInUseThatCannotBeRead_StaysUnknown_AndIsNeverFilledIn()
    {
        VulkanAdapter[] adapters =
        [
            Adapter(0, "aaaaaaaa-0000-0000-0000-000000000001", "AMD Radeon RX 7800 XT", VulkanDeviceType.Discrete, luid: 11, gib: 16),
            Adapter(1, "cccccccc-0000-0000-0000-000000000003", "Intel(R) Arc(TM) A770", VulkanDeviceType.Discrete, luid: null, gib: 16),
        ];

        // The counters answer, but have no instance for one card (and the other has no LUID to match).
        GpuAdapterRead partial = VulkanGpuSource.ToRead(adapters, new Dictionary<ulong, long> { [11] = (long)GiB }, null);
        Assert.Equal(new double?[] { 1, null }, partial.Adapters!.Select(a => a.UsedGiB));
        Assert.Equal("Windows lists no GPU memory counter for Intel(R) Arc(TM) A770.", partial.Error);

        // The counters cannot be read at all (the service account lacks the right, for example).
        GpuAdapterRead none = VulkanGpuSource.ToRead(adapters, usage: null, "PdhCollectQueryData failed with PDH status 0xC0000BC6.");
        Assert.All(none.Adapters!, a => Assert.Null(a.UsedGiB));
        Assert.All(none.Adapters!, a => Assert.Equal(16, a.TotalGiB));
        Assert.Equal("GPU memory in use cannot be read: PdhCollectQueryData failed with PDH status 0xC0000BC6.", none.Error);
    }

    [Fact]
    public void ALoaderThatFails_IsAFailedRead_AndTheAdapterListIsOnlyReadAgainWhenAsked()
    {
        int enumerations = 0;
        bool broken = true;
        var source = new VulkanGpuSource(
            () =>
            {
                enumerations++;
                if (broken)
                {
                    throw new DllNotFoundException("Unable to load DLL 'vulkan-1'");
                }

                return [Adapter(0, "aaaaaaaa-0000-0000-0000-000000000001", "AMD Radeon RX 7800 XT", VulkanDeviceType.Discrete, luid: 11, gib: 16)];
            },
            () => (new Dictionary<ulong, long> { [11] = (long)(2 * GiB) }, null));

        GpuAdapterRead failed = source.Read();
        Assert.Null(failed.Adapters);
        Assert.Contains("vulkan-1", failed.Error, StringComparison.Ordinal);

        broken = false;
        Assert.Equal(2, source.Read().Adapters!.Single().UsedGiB);
        source.Read();
        Assert.Equal(2, enumerations); // the failed one and one successful; memory in use is read every time
        source.Read(reenumerate: true);
        Assert.Equal(3, enumerations);
    }

    // ------------------------------------------------------------------ Linux: amdgpu's sysfs files

    [Fact]
    public void OnLinux_OnlyCardsWithVramCounters_AreListed_ByPciAddress_WithTheirRenderNode()
    {
        string sys = Path.Combine(_root, "sys");
        Card(sys, "card0", driverVram: null, used: null, slot: "0000:00:02.0", vendor: "0x8086", device: "0x7d67", render: "renderD128"); // Intel: no counters
        Card(sys, "card1", driverVram: 17163091968, used: 1288490188, slot: "0000:03:00.0", vendor: "0x1002", device: "0x747e", render: "renderD129", product: "AMD Radeon RX 7800 XT");
        Card(sys, "card2", driverVram: 8573157376, used: null, slot: "0000:0A:00.0", vendor: "0x1002", device: "0x73ff", render: null);
        Directory.CreateDirectory(Path.Combine(sys, "card1-DP-1")); // a connector, not a card

        GpuAdapterRead read = new LinuxDrmGpuSource(sys, "/dev/dri").Read();

        Assert.Equal(2, read.Adapters!.Count);
        GpuAdapter amd = read.Adapters[0];
        Assert.Equal((1, "GPU-pci-0000-03-00-0", "AMD Radeon RX 7800 XT"), (amd.Index, amd.Uuid, amd.Name));
        Assert.Equal(17163091968 / GiB, amd.TotalGiB);
        Assert.Equal(1288490188 / GiB, amd.UsedGiB);
        Assert.Equal(["/dev/dri/renderD129"], amd.DeviceNodes);

        // A card whose used counter cannot be read keeps an unknown figure, is named by its PCI ids and has no device node.
        GpuAdapter second = read.Adapters[1];
        Assert.Equal((2, "GPU-pci-0000-0a-00-0", "AMD GPU 1002:73ff"), (second.Index, second.Uuid, second.Name));
        Assert.Null(second.UsedGiB);
        Assert.Empty(second.DeviceNodes);
        Assert.Equal("mem_info_vram_used could not be read for AMD GPU 1002:73ff.", read.Error);

        // The UUIDs pass the launch validators' GPU UUID rule.
        Assert.All(read.Adapters, a => Assert.Matches("^(GPU|MIG)-[A-Za-z0-9][A-Za-z0-9/-]*$", a.Uuid));
    }

    [Fact]
    public void OnLinux_WithoutADrmFolder_TheReadFails_AndAnIntelOnlyMachineListsNoGpu()
    {
        GpuAdapterRead missing = new LinuxDrmGpuSource(Path.Combine(_root, "nothing"), "/dev/dri").Read();
        Assert.Null(missing.Adapters);
        Assert.Contains("does not exist", missing.Error, StringComparison.Ordinal);

        string sys = Path.Combine(_root, "intel");
        Card(sys, "card0", driverVram: null, used: null, slot: "0000:00:02.0", vendor: "0x8086", device: "0x56a0", render: "renderD128");
        GpuAdapterRead intel = new LinuxDrmGpuSource(sys, "/dev/dri").Read();
        Assert.Empty(intel.Adapters!);
        Assert.Null(intel.Error);
    }

    // ------------------------------------------------------------------ the probe and inventory over a source

    [Fact]
    public async Task FreeVram_IsTotalMinusUsed_AndUnknownAsSoonAsEitherIsMissing()
    {
        var source = new FakeSource(new GpuAdapterRead(
        [
            new GpuAdapter(2, GpuA, "AMD Radeon RX 7800 XT", 16, 3.5, []),
            new GpuAdapter(0, GpuB, "Intel(R) Arc(TM) A770", 16, null, []),
        ], "Windows lists no GPU memory counter for Intel(R) Arc(TM) A770."));
        var gpus = new AdapterGpus(source, NullLogger<AdapterGpus>.Instance);

        GpuTelemetryReading reading = await gpus.ReadAsync(Ct);

        Assert.Equal(GpuProbeStatus.Ok, reading.Status);
        Assert.Equal([0, 2], reading.Gpus.Select(g => g.Index));
        GpuSample known = reading.Gpus[1];
        Assert.Equal((16d, 3.5, 12.5), (known.MemoryTotalGiB!.Value, known.MemoryUsedGiB!.Value, known.MemoryFreeGiB!.Value));
        GpuSample unknown = reading.Gpus[0];
        Assert.Equal(16, unknown.MemoryTotalGiB);
        Assert.Null(unknown.MemoryUsedGiB);
        Assert.Null(unknown.MemoryFreeGiB);
        // Nothing the source does not read is reported: no utilization, power, temperature or clock.
        Assert.All(reading.Gpus, g => Assert.True(g.UtilizationPercent is null && g.PowerWatts is null && g.TemperatureC is null && g.SmClockMHz is null));

        // The node says why a GPU takes no model.
        HealthReason reason = Assert.Single(await gpus.CheckAsync(Ct));
        Assert.Equal(HealthReasonCodes.GpuProbeFailing, reason.Code);
        Assert.Contains("no GPU memory counter for Intel(R) Arc(TM) A770", reason.Message, StringComparison.Ordinal);
        Assert.Contains("A model is not started on a GPU whose free memory is unknown.", reason.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInventory_ListsTheGpusByUuid_AndKeepsTheLastListingThroughAFailedRead()
    {
        var source = new FakeSource(new GpuAdapterRead(
        [
            new GpuAdapter(0, GpuA, "AMD Radeon RX 7800 XT", 16, 1, ["/dev/dri/renderD129"]),
            new GpuAdapter(1, GpuB, "AMD Radeon RX 7600", 8, 0.5, []),
        ]));
        var gpus = new AdapterGpus(source, NullLogger<AdapterGpus>.Instance);
        IGpuInventory inventory = gpus;
        Assert.Null(inventory.Last);
        Assert.False(gpus.Answered);

        var listed = await gpus.GetAsync(Ct);

        Assert.True(gpus.Answered);
        Assert.Equal([(0, GpuA, 16d, false), (1, GpuB, 8d, false)], listed.Select(g => (g.Index, g.Uuid, g.MemoryTotalGiB!.Value, g.SharesHostMemory)));
        Assert.Equal(MemoryModel.Discrete, NvidiaSmiGpuInventory.MemoryModelOf(listed));
        Assert.Equal(["GPU-gone"], inventory.Missing([GpuA, "GPU-gone"]));
        Assert.Equal(["/dev/dri/renderD129"], gpus.DeviceNodesFor(GpuA));
        Assert.Null(gpus.DeviceNodesFor(GpuB));
        Assert.Empty(await gpus.CheckAsync(Ct));

        // The source stops answering: telemetry fails (the node is Degraded through the GPU health check), and the placed
        // GPUs do not all turn "missing".
        source.Next = new GpuAdapterRead(null, "The Vulkan loader could not list the GPUs: device lost");
        GpuTelemetryReading failed = await gpus.ReadAsync(Ct);
        Assert.Equal((GpuProbeStatus.Failed, "The Vulkan loader could not list the GPUs: device lost"), (failed.Status, failed.Error));
        Assert.Empty(failed.Gpus);
        Assert.Empty(inventory.Missing([GpuA, GpuB]));
        Assert.Contains("device lost", Assert.Single(await new GpuHealthContributor(gpus).CheckAsync(Ct)).Message, StringComparison.Ordinal);

        // A refresh asks the source to list the adapters again.
        source.Next = new GpuAdapterRead([new GpuAdapter(0, GpuB, "AMD Radeon RX 7600", 8, 0.5, [])]);
        Assert.Equal([GpuB], (await inventory.RefreshAsync(Ct)).Select(g => g.Uuid));
        Assert.True(source.LastReenumerate);
        Assert.Equal([GpuA], inventory.Missing([GpuA, GpuB]));
    }

    // ------------------------------------------------------------------ a composed Vulkan node

    [Fact]
    public async Task AVulkanNode_ReportsItsGpus_AdmitsPerGpu_AndRefusesAGpuWhoseFreeMemoryIsUnknown()
    {
        var source = new FakeSource(new GpuAdapterRead(
        [
            new GpuAdapter(0, GpuA, "AMD Radeon RX 7800 XT", 16, 4, []),
            new GpuAdapter(2, GpuB, "Intel(R) Arc(TM) A770", 16, null, []),
        ], "Windows lists no GPU memory counter for Intel(R) Arc(TM) A770."));
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(
            o => o.Accelerator = "vulkan", extraServices: s => s.AddSingleton<IGpuAdapterSource>(source), ct: Ct);

        NodeCapabilities report = await app.Services.GetRequiredService<AgentCapabilityReporter>().GetAsync(Ct);

        // The node's own nvidia-smi stand-in lists two NVIDIA cards with the same UUIDs; what is reported comes from the
        // Vulkan source (its names and its indices), never from nvidia-smi.
        Assert.Equal(NodeAccelerators.Vulkan, report.Accelerator);
        Assert.Equal(MemoryModel.Discrete, report.Memory.Model);
        Assert.Equal([(0, GpuA, "AMD Radeon RX 7800 XT"), (2, GpuB, "Intel(R) Arc(TM) A770")], report.Gpus.Select(g => (g.Index, g.Uuid, g.Name)));
        Assert.All(report.Gpus, g => Assert.True(g.MemoryTotalGiB == 16 && g.ComputeCapability is null && g.Driver is null));
        Assert.Contains(AgentFeatures.GpuSelectionUuids, report.Features);
        Assert.Contains(AgentFeatures.AdmissionDiscrete, report.Features);
        Assert.Contains(AgentFeatures.AdmissionGpuBudgets, report.Features);
        var llama = Assert.Single(report.Runtimes);
        Assert.Equal((RuntimeKind.LlamaCpp, LaunchDelivery.Process), (llama.Kind, llama.Delivery));

        var reservations = app.Services.GetRequiredService<ReservationManager>();
        Assert.True(reservations.PerGpuAdmission);

        // 16 GiB total, 4 in use: 12 free, minus the 1 GiB reserve.
        var tooBig = await reservations.TryReserveAsync(Request("op-1", "too-big", 11.5, GpuA), Ct);
        Assert.Equal(ReservationOutcome.AdmissionRefused, tooBig.Outcome);
        var details = JsonSerializer.Deserialize<AdmissionRefusedDetails>(tooBig.Error!.Details!.Value, AgentJson.Options)!;
        Assert.Equal((11.5, 11d, 12d, GpuA), (details.NeedGiB, details.FreeGiB, details.AvailableGiB, details.Gpu));
        Assert.Equal(ReservationOutcome.Reserved, (await reservations.TryReserveAsync(Request("op-2", "fits", 11, GpuA), Ct)).Outcome);

        // The other card's memory in use cannot be read: refused whatever the size, with the reason, unless forced.
        var unknown = await reservations.TryReserveAsync(Request("op-3", "small", 1, GpuB), Ct);
        Assert.Equal(ReservationOutcome.AdmissionRefused, unknown.Outcome);
        Assert.Equal(AdmissionTexts.OnNode(AdmissionTexts.GpuMemoryUnknown("small", 1), "win-1", "GPU 2"), unknown.Error!.Message);
        var forced = await reservations.TryReserveAsync(Request("op-4", "small", 1, GpuB, force: true), Ct);
        Assert.Equal(ReservationOutcome.Reserved, forced.Outcome);

        // And the node says so in its health.
        var reasons = new List<HealthReason>();
        foreach (var contributor in app.Services.GetServices<IHealthContributor>())
        {
            reasons.AddRange(await contributor.CheckAsync(Ct));
        }

        Assert.Contains(reasons, r => r.Code == HealthReasonCodes.GpuProbeFailing && r.Message.Contains("Intel(R) Arc(TM) A770", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AVulkanProcess_GetsItsGpusAsVulkanIndices_InPlacementOrder_AndNoCudaSelection()
    {
        // The model of P3Node is placed on GpuB, which the Vulkan loader lists third (index 2).
        var source = new FakeSource(new GpuAdapterRead(
        [
            new GpuAdapter(0, GpuA, "AMD Radeon RX 7800 XT", 16, 1, []),
            new GpuAdapter(2, GpuB, "AMD Radeon RX 7600", 8, 0.5, []),
        ]));
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(
            o => o.Accelerator = "vulkan", extraServices: s => s.AddSingleton<IGpuAdapterSource>(source), ct: Ct);
        await app.StartAsync(Ct);

        RuntimeStatus status = await node.LaunchAsync(app, Ct);

        var environment = node.Host.Starts[^1].Environment;
        Assert.Equal("2", environment[ProcessEnvironment.VulkanVisibleDevices]);
        Assert.Equal("~implicit~", environment["VK_LOADER_LAYERS_DISABLE"]);
        Assert.False(environment.ContainsKey(ProcessEnvironment.CudaVisibleDevices));
        Assert.Equal([new GpuRef(2, GpuB)], status.Gpus);
    }

    [Fact]
    public async Task ANvidiaProcess_KeepsItsCudaSelection_AndGetsNoVulkanVariable()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        await app.StartAsync(Ct);

        await node.LaunchAsync(app, Ct);

        var environment = node.Host.Starts[^1].Environment;
        Assert.Equal(GpuB, environment[ProcessEnvironment.CudaVisibleDevices]);
        Assert.False(environment.ContainsKey(ProcessEnvironment.VulkanVisibleDevices));
        Assert.False(environment.ContainsKey("VK_LOADER_LAYERS_DISABLE"));
    }

    // ------------------------------------------------------------------ containers on a Vulkan node (Linux)

    [Fact]
    public async Task AVulkanContainer_GetsTheRenderNodeOfItsOneGpu_AndNoNvidiaRequest()
    {
        var gpus = new AdapterGpus(
            new FakeSource(new GpuAdapterRead(
            [
                new GpuAdapter(1, GpuA, "AMD Radeon RX 7800 XT", 16, 1, ["/dev/dri/renderD129"]),
                new GpuAdapter(2, GpuB, "AMD Radeon RX 7600", 8, 0.5, []),
            ])),
            NullLogger<AdapterGpus>.Instance);
        await gpus.GetAsync(Ct);
        var (driver, docker) = Driver(gpus);

        await driver.LaunchAsync(
            new RuntimeLaunch { ModelId = "small", OperationId = "op-1", Mode = LaunchMode.Replace, Spec = Spec(ByUuid(GpuA)), HostPort = 8101 },
            new ContainerRuntimeDriverTests.RecordingStageSink(),
            Ct);

        JsonObject host = docker.CreateBodies.Single()["HostConfig"]!.AsObject();
        Assert.False(host.ContainsKey("DeviceRequests"));
        Assert.Equal(
            """[{"PathOnHost":"/dev/dri/renderD129","PathInContainer":"/dev/dri/renderD129","CgroupPermissions":"rwm"}]""",
            host["Devices"]!.ToJsonString());

        // What the agent cannot name it does not pass: two GPUs, no selection, or a GPU without a render node.
        Assert.Contains("gets one GPU", Refusal(driver, ByUuid(GpuA, GpuB)).Message, StringComparison.Ordinal);
        Assert.Contains("must select its GPU by UUID", Refusal(driver, new GpuSelection()).Message, StringComparison.Ordinal);
        Assert.Contains("has no DRM render node", Refusal(driver, ByUuid(GpuB)).Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.GpuMissing, Refusal(driver, ByUuid("GPU-pci-0000-99-00-0")).Code);
    }

    // ------------------------------------------------------------------ real hardware (read-only)

    [Fact]
    [Trait("Os", "Windows")]
    public void OnThisPc_TheVulkanSource_AgreesWithNvidiaSmi()
    {
        // The only check of the vendor-neutral Windows path against an independent reading: a PC with NVIDIA cards, where
        // nvidia-smi reports the same GPUs. It reads the Vulkan loader and the performance counters and changes nothing.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The Vulkan source reads Windows performance counters.");
            return;
        }

        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll")))
        {
            Assert.Skip("No Vulkan runtime on this PC.");
        }

        var nvidia = NvidiaSmi("--query-gpu=uuid,name,memory.total,memory.used --format=csv,noheader,nounits");
        if (nvidia.Count == 0)
        {
            Assert.Skip("No NVIDIA GPU with nvidia-smi on this PC to compare with.");
        }

        GpuAdapterRead read = VulkanGpuSource.ForWindows().Read();
        Assert.NotNull(read.Adapters);
        var after = NvidiaSmi("--query-gpu=uuid,name,memory.total,memory.used --format=csv,noheader,nounits");

        foreach (var (uuid, name, totalMiB, usedBefore) in nvidia)
        {
            // The same identity string and name as nvidia-smi.
            GpuAdapter adapter = Assert.Single(read.Adapters!, a => a.Uuid == uuid);
            Assert.Equal(name, adapter.Name);

            // VRAM total: what the driver hands out, i.e. nvidia-smi's total minus the driver's own reserve (under 1 GiB).
            Assert.InRange(adapter.TotalGiB!.Value, (totalMiB / 1024) - 1, totalMiB / 1024);

            // Memory in use across all processes: within 1 GiB of nvidia-smi's readings taken just before and after.
            Assert.True(adapter.UsedGiB is not null, read.Error);
            double usedAfter = after.Single(g => g.Uuid == uuid).UsedMiB;
            Assert.InRange(adapter.UsedGiB!.Value, (Math.Min(usedBefore, usedAfter) / 1024) - 1, (Math.Max(usedBefore, usedAfter) / 1024) + 1);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static List<(string Uuid, string Name, double TotalMiB, double UsedMiB)> NvidiaSmi(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("nvidia-smi", arguments) { RedirectStandardOutput = true, UseShellExecute = false })!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            if (process.ExitCode != 0)
            {
                return [];
            }

            return
            [
                .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => line.Split(',', StringSplitOptions.TrimEntries))
                    .Where(f => f.Length == 4)
                    .Select(f => (f[0], f[1], double.Parse(f[2], CultureInfo.InvariantCulture), double.Parse(f[3], CultureInfo.InvariantCulture))),
            ];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    private static StartReservationRequest Request(string operationId, string modelId, double budgetGiB, string gpu, bool force = false) => new()
    {
        OperationId = operationId,
        ModelId = modelId,
        Admission = new AdmissionRequest { BudgetGiB = budgetGiB, HostBudgetGiB = 1, ReserveGiB = 4, GpuReserveGiB = 1, Force = force },
        GpuUuids = [gpu],
    };

    private static VulkanAdapter Adapter(int index, string uuid, string name, VulkanDeviceType type, ulong? luid, double gib) =>
        new(index, uuid, name, 0x1002, 0x7000, type, luid, (ulong)(gib * GiB));

    private static byte[] Properties(uint vendor, uint device, uint type, string name)
    {
        byte[] buffer = new byte[VulkanAdapters.PropertiesSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24), vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(28), device);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32), type);
        Encoding.UTF8.GetBytes(name).CopyTo(buffer.AsSpan(36));
        return buffer;
    }

    private static byte[] IdProperties(byte[] uuid, ulong luid, bool luidValid)
    {
        byte[] buffer = new byte[VulkanAdapters.IdPropertiesSize];
        uuid.CopyTo(buffer.AsSpan(16));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(48), luid);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(60), luidValid ? 1u : 0u);
        return buffer;
    }

    private static byte[] Memory(params (ulong Size, bool DeviceLocal)[] heaps)
    {
        byte[] buffer = new byte[VulkanAdapters.MemoryPropertiesSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(260), (uint)heaps.Length);
        for (int i = 0; i < heaps.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(264 + (i * 16)), heaps[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(264 + (i * 16) + 8), heaps[i].DeviceLocal ? 1u : 0u);
        }

        return buffer;
    }

    /// <summary>One <c>/sys/class/drm/cardN</c> as the kernel lays it out: <c>device/{vendor,device,uevent,mem_info_vram_*}</c> and <c>device/drm/renderD*</c>.</summary>
    private static void Card(string sys, string card, long? driverVram, long? used, string slot, string vendor, string device, string? render, string? product = null)
    {
        string dev = Path.Combine(sys, card, "device");
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, "vendor"), vendor + "\n");
        File.WriteAllText(Path.Combine(dev, "device"), device + "\n");
        File.WriteAllText(Path.Combine(dev, "uevent"), $"DRIVER=amdgpu\nPCI_CLASS=30000\nPCI_ID=1002:747E\nPCI_SLOT_NAME={slot}\n");
        if (driverVram is { } total)
        {
            File.WriteAllText(Path.Combine(dev, "mem_info_vram_total"), total.ToString(CultureInfo.InvariantCulture) + "\n");
        }

        if (used is { } bytes)
        {
            File.WriteAllText(Path.Combine(dev, "mem_info_vram_used"), bytes.ToString(CultureInfo.InvariantCulture) + "\n");
        }

        if (product is not null)
        {
            File.WriteAllText(Path.Combine(dev, "product_name"), product + "\n");
        }

        if (render is not null)
        {
            Directory.CreateDirectory(Path.Combine(dev, "drm", render));
        }
    }

    private static GpuSelection ByUuid(params string[] uuids) => new() { Mode = GpuSelectionMode.Uuids, Uuids = uuids };

    private static LaunchSpec Spec(GpuSelection gpus) => new()
    {
        Kind = LaunchDelivery.Container,
        Runtime = RuntimeKind.LlamaCpp,
        Image = "ghcr.io/ggml-org/llama.cpp:server-vulkan",
        Argv = ["-m", "/models/small/model.gguf", "--host", "0.0.0.0", "--port", "8000", "--device", "Vulkan0"],
        ContainerPort = 8000,
        Mounts = [new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true)],
        Gpus = gpus,
        IpcHost = true,
        Labels = new Dictionary<string, string> { [ContainerLabels.ServedName] = "small" },
    };

    private static AgentError Refusal(ContainerRuntimeDriver driver, GpuSelection gpus) =>
        Assert.Throws<AgentException>(() => driver.CheckSpec("small", Spec(gpus))).Error;

    private static (ContainerRuntimeDriver Driver, ContainerRuntimeDriverTests.FakeDockerEngine Docker) Driver(AdapterGpus gpus)
    {
        var docker = new ContainerRuntimeDriverTests.FakeDockerEngine();
        var driver = new ContainerRuntimeDriver(
            docker.Client,
            new HttpClient(new FakeHttpMessageHandler { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) }),
            new ContainerRuntimeDriverTests.FakePortAllocator(),
            Microsoft.Extensions.Options.Options.Create(new ContainerRuntimeOptions { NodeId = "pc-1", GpuAccess = ContainerGpuAccess.DeviceNodes }),
            NullLogger<ContainerRuntimeDriver>.Instance,
            gpuInventory: gpus,
            gpuDeviceNodes: gpus.DeviceNodesFor);
        return (driver, docker);
    }

    private sealed class FakeSource(GpuAdapterRead first) : IGpuAdapterSource
    {
        public GpuAdapterRead Next { get; set; } = first;

        public bool LastReenumerate { get; private set; }

        public GpuAdapterRead Read(bool reenumerate = false)
        {
            LastReenumerate = reenumerate;
            return Next;
        }
    }
}
