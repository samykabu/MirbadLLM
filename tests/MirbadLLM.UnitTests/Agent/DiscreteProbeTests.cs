using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-104: the discrete GPU probe (per-GPU UUID, name, VRAM total/used/free, utilization, index), the
/// <see cref="IGpuInventory"/> implementation, <see cref="DiscreteNodeMemoryReader"/>, the capability report and the
/// FakeAgent's discrete mode. Every parser test runs on <c>nvidia-smi</c> output in <c>Fixtures/nvidia-smi</c>:
/// <list type="bullet">
/// <item><c>rtx5070-rtx5060ti.windows.*</c>: recorded on a Windows 11 PC (driver 617.14, RTX 5070 12 GB + RTX 5060 Ti
/// 16 GB) with the exact argument strings the probes send; the two GPU UUIDs are replaced by placeholders (MN-126: no
/// hardware identifiers of the owner's machines in the repository). The recording had CRLF line ends; git stores it with
/// LF, so the tests feed both.</item>
/// <item><c>gb10.linux.*</c>: the DGX GB10's shape as P1 observed it (memory fields <c>[N/A]</c>, compute 12.1, driver
/// 580.95.05) rewritten for the MN-104 column list; the UUID is a placeholder.</item>
/// <item><c>rtx4060ti-x2.linux.*</c>: a Linux two-card PC (2 × RTX 4060 Ti 16 GB), the shape of the brief's acceptance.</item>
/// </list>
/// </summary>
public sealed class DiscreteProbeTests
{
    private const string Rtx5070 = "GPU-00000000-0000-0000-0000-000000005070";
    private const string Rtx5060Ti = "GPU-00000000-0000-0000-0000-000000005060";
    private const string Gb10 = "GPU-5967aa52-1c3e-4b7d-9f20-6d8e1a2b3c4d";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> LineEnds => ["\n", "\r\n"];

    // ------------------------------------------------------------------ telemetry probe (per-GPU, MN-104 columns)

    [Fact]
    public void TheTelemetryQueryAsksForFreeVramAndNameAfterP1sColumns()
    {
        Assert.Equal(
            "--query-gpu=index,uuid,utilization.gpu,power.draw,temperature.gpu,clocks.sm,memory.used,memory.total,memory.free,name --format=csv,noheader,nounits",
            NvidiaSmiGpuTelemetryProbe.Arguments);
    }

    [Theory]
    [MemberData(nameof(LineEnds))]
    public async Task RecordedWindowsTwoGpuTelemetryParsesEveryGpu(string lineEnd)
    {
        var runner = new RecordingNvidiaSmi(Fixture("rtx5070-rtx5060ti.windows.telemetry.csv", lineEnd));
        var probe = new NvidiaSmiGpuTelemetryProbe(runner, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance);

        var reading = await probe.ReadAsync(Ct);

        Assert.Equal([NvidiaSmiGpuTelemetryProbe.Arguments], runner.Calls);
        Assert.Equal(GpuProbeStatus.Ok, reading.Status);
        Assert.Equal(2, reading.Gpus.Count);
        var a = reading.Gpus[0];
        Assert.Equal((0, Rtx5070, "NVIDIA GeForce RTX 5070"), (a.Index, a.Uuid, a.Name));
        Assert.Equal(0, a.UtilizationPercent);
        Assert.Equal(3.31, a.PowerWatts);
        Assert.Equal(12227 / 1024.0, a.MemoryTotalGiB);
        Assert.Equal(10987 / 1024.0, a.MemoryUsedGiB);
        Assert.Equal(957 / 1024.0, a.MemoryFreeGiB);
        var b = reading.Gpus[1];
        Assert.Equal((1, Rtx5060Ti, "NVIDIA GeForce RTX 5060 Ti"), (b.Index, b.Uuid, b.Name));
        Assert.Equal(11, b.UtilizationPercent);
        Assert.Equal(16311 / 1024.0, b.MemoryTotalGiB);
        Assert.Equal(631 / 1024.0, b.MemoryFreeGiB);
    }

    [Fact]
    public void FreeVramIsTheDriversValueNotTotalMinusUsed()
    {
        // The recording shows the driver's reserved VRAM: free (957 MiB) < total − used (1240 MiB). Admission must use free.
        var gpu = NvidiaSmiGpuTelemetryProbe.Parse(Fixture("rtx5070-rtx5060ti.windows.telemetry.csv", "\r\n"))![0];

        Assert.True(gpu.MemoryFreeGiB < gpu.MemoryTotalGiB - gpu.MemoryUsedGiB);
        Assert.Equal(957 / 1024.0, DiscreteNodeMemoryReader.FreeOf(gpu));
        Assert.Equal(2.0, DiscreteNodeMemoryReader.FreeOf(new GpuSample { Index = 0, MemoryTotalGiB = 16, MemoryUsedGiB = 14 }));
        Assert.Equal(0, DiscreteNodeMemoryReader.FreeOf(new GpuSample { Index = 0, MemoryTotalGiB = 16, MemoryUsedGiB = 17 }));
        Assert.Null(DiscreteNodeMemoryReader.FreeOf(new GpuSample { Index = 0 }));
    }

    [Fact]
    public void LinuxTwoCardTelemetryParsesIncludingNotSupportedPower()
    {
        var gpus = NvidiaSmiGpuTelemetryProbe.Parse(Fixture("rtx4060ti-x2.linux.telemetry.csv", "\n"))!;

        Assert.Equal([0, 1], gpus.Select(g => g.Index));
        Assert.All(gpus, g => Assert.Equal("NVIDIA GeForce RTX 4060 Ti", g.Name));
        Assert.Equal(87, gpus[0].UtilizationPercent);
        Assert.Equal(1873 / 1024.0, gpus[0].MemoryFreeGiB);
        Assert.Null(gpus[1].PowerWatts);
        Assert.Equal(15973 / 1024.0, gpus[1].MemoryFreeGiB);
    }

    [Fact]
    public void Gb10TelemetryKeepsMemoryNullAndOmitsFreeOnTheWire()
    {
        var gpu = Assert.Single(NvidiaSmiGpuTelemetryProbe.Parse(Fixture("gb10.linux.telemetry.csv", "\n"))!);

        Assert.Equal((0, Gb10, "NVIDIA GB10"), (gpu.Index, gpu.Uuid, gpu.Name));
        Assert.Equal(96, gpu.UtilizationPercent);
        Assert.Null(gpu.MemoryTotalGiB);
        Assert.Null(gpu.MemoryUsedGiB);
        Assert.Null(gpu.MemoryFreeGiB);
        string json = JsonSerializer.Serialize(gpu, AgentJson.Options);
        Assert.DoesNotContain("memoryFreeGiB", json, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"NVIDIA GB10\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void P1sEightColumnRowsStillParseWithoutFreeAndName()
    {
        var gpus = NvidiaSmiGpuTelemetryProbe.Parse(Fixture("rtx5070-rtx5060ti.windows.telemetry-p1.csv", "\r\n"))!;

        Assert.Equal([Rtx5070, Rtx5060Ti], gpus.Select(g => g.Uuid));
        Assert.Equal(16311 / 1024.0, gpus[1].MemoryTotalGiB);
        Assert.All(gpus, g => Assert.Null(g.MemoryFreeGiB));
        Assert.All(gpus, g => Assert.Null(g.Name));
        Assert.Null(NvidiaSmiGpuTelemetryProbe.Parse("0, GPU-a, 1, 2, 3, 4, 5\n")); // seven columns is not our query
    }

    [Fact]
    public void ANameWithACommaIsKeptWhole()
    {
        var gpu = Assert.Single(NvidiaSmiGpuTelemetryProbe.Parse("0, GPU-a, 1, 2, 3, 4, 100, 200, 90, Quadro RTX 8000, rev B\n")!);

        Assert.Equal("Quadro RTX 8000, rev B", gpu.Name);
        Assert.Equal(90 / 1024.0, gpu.MemoryFreeGiB);
    }

    // ------------------------------------------------------------------ inventory (IGpuInventory) and capabilities

    [Theory]
    [MemberData(nameof(LineEnds))]
    public async Task RecordedWindowsInventoryIsDiscreteWithUuidNameAndTotal(string lineEnd)
    {
        var runner = new RecordingNvidiaSmi(Fixture("rtx5070-rtx5060ti.windows.inventory.csv", lineEnd));
        var inventory = new NvidiaSmiGpuInventory(runner);

        var gpus = await inventory.RefreshAsync(Ct);

        Assert.Equal([NvidiaSmiGpuInventory.Arguments], runner.Calls);
        Assert.Same(gpus, inventory.Last);
        Assert.Equal([(0, Rtx5070, "NVIDIA GeForce RTX 5070"), (1, Rtx5060Ti, "NVIDIA GeForce RTX 5060 Ti")],
            gpus.Select(g => (g.Index, g.Uuid, g.Name)));
        Assert.Equal([12227 / 1024.0, 16311 / 1024.0], gpus.Select(g => g.MemoryTotalGiB));
        Assert.All(gpus, g => Assert.False(g.SharesHostMemory));
        Assert.All(gpus, g => Assert.Equal(("12.0", "617.14"), (g.ComputeCapability, g.Driver)));
        Assert.Equal(MemoryModel.Discrete, NvidiaSmiGpuInventory.MemoryModelOf(gpus));
    }

    [Fact]
    public async Task Gb10InventoryIsUnified()
    {
        var inventory = new NvidiaSmiGpuInventory(new RecordingNvidiaSmi(Fixture("gb10.linux.inventory.csv", "\n")));

        var gpu = Assert.Single(await inventory.RefreshAsync(Ct));

        Assert.Equal((Gb10, "NVIDIA GB10", "12.1"), (gpu.Uuid, gpu.Name, gpu.ComputeCapability));
        Assert.Null(gpu.MemoryTotalGiB);
        Assert.True(gpu.SharesHostMemory);
        Assert.Equal(MemoryModel.Unified, NvidiaSmiGpuInventory.MemoryModelOf([gpu]));
    }

    [Fact]
    public async Task InventoryFollowsRenumberingAndRemovalAndNamesMissingUuids()
    {
        string recorded = Fixture("rtx5070-rtx5060ti.windows.inventory.csv", "\r\n");
        var runner = new RecordingNvidiaSmi(recorded);
        var inventory = new NvidiaSmiGpuInventory(runner);
        IGpuInventory view = inventory;

        Assert.Null(view.Last);
        Assert.Equal([Rtx5070], view.Missing([Rtx5070])); // before the first read every UUID is missing

        await view.RefreshAsync(Ct);
        Assert.Empty(view.Missing([Rtx5060Ti, Rtx5070]));

        // A driver update swaps the indices: the UUIDs stay, the indices follow.
        runner.Output = Renumbered(recorded);
        var renumbered = await view.RefreshAsync(Ct);
        Assert.Equal([(0, Rtx5060Ti), (1, Rtx5070)], renumbered.Select(g => (g.Index, g.Uuid)));
        Assert.Same(renumbered, await inventory.GetAsync(Ct)); // the capability report sees the refresh too

        // The 5070 is pulled: nvidia-smi lists the 5060 Ti alone, at index 0, and only the 5070 is missing.
        runner.Output = "0" + recorded.Split('\n').Single(l => l.Contains(Rtx5060Ti, StringComparison.Ordinal))[1..];
        await view.RefreshAsync(Ct);
        Assert.Equal([Rtx5070], view.Missing([Rtx5060Ti, Rtx5070]));
        Assert.Equal(0, Assert.Single(view.Last!).Index);
    }

    [Fact]
    public async Task AFailingNvidiaSmiKeepsTheLastInventoryAndAMissingOneClearsIt()
    {
        var runner = new RecordingNvidiaSmi(Fixture("rtx5070-rtx5060ti.windows.inventory.csv", "\n"));
        IGpuInventory inventory = new NvidiaSmiGpuInventory(runner);
        var first = await inventory.RefreshAsync(Ct);

        runner.Result = new NvidiaSmiResult(true, 15, "", "Unable to determine the device handle for GPU0000:01:00.0: GPU is lost.");
        Assert.Empty(await inventory.RefreshAsync(Ct));
        Assert.Same(first, inventory.Last);
        Assert.Empty(inventory.Missing([Rtx5070])); // a transient failure is not gpu_missing

        runner.Result = NvidiaSmiResult.NotFound;
        Assert.Empty(await inventory.RefreshAsync(Ct));
        Assert.Null(inventory.Last);
        Assert.Equal([Rtx5070], inventory.Missing([Rtx5070]));
    }

    [Fact]
    public async Task CapabilitiesOnTheWindowsPcAreDiscreteWithoutAnyP2Flag()
    {
        var contributor = new TelemetryCapabilityContributor(new NvidiaSmiGpuInventory(
            new RecordingNvidiaSmi(Fixture("rtx5070-rtx5060ti.windows.inventory.csv", "\r\n"))));

        var result = await contributor.ContributeAsync(BaseCapabilities(), Ct);

        Assert.Equal(MemoryModel.Discrete, result.Memory.Model);
        Assert.Equal(64, result.Memory.TotalGiB);
        Assert.Equal([Rtx5070, Rtx5060Ti], result.Gpus.Select(g => g.Uuid));
        Assert.Equal(16311 / 1024.0, result.Gpus[1].MemoryTotalGiB);
        // The telemetry package reports only telemetry.v1: admission.discrete is MN-105's contributor, gpus.uuids MN-106's.
        Assert.Equal([AgentFeatures.TelemetryV1], result.Features);
        Assert.Empty(result.Features.Intersect(AgentFeatures.P2));
    }

    [Fact]
    public async Task CapabilitiesOnTheGb10StayUnified()
    {
        var contributor = new TelemetryCapabilityContributor(new NvidiaSmiGpuInventory(
            new RecordingNvidiaSmi(Fixture("gb10.linux.inventory.csv", "\n"))));
        var current = BaseCapabilities();

        var result = await contributor.ContributeAsync(current, Ct);

        Assert.Equal(current.Memory, result.Memory);
        Assert.True(Assert.Single(result.Gpus).SharesHostMemory);
        Assert.Equal([AgentFeatures.TelemetryV1], result.Features);
    }

    // ------------------------------------------------------------------ DiscreteNodeMemoryReader

    [Fact]
    public async Task TheReaderReportsPerGpuTotalUsedAndFreeOnTheWindowsPc()
    {
        using var reader = Reader(new RecordingNvidiaSmi(Fixture("rtx5070-rtx5060ti.windows.telemetry.csv", "\r\n")), new ManualTime());

        var memory = await reader.ReadAsync(Ct);

        Assert.Equal(MemoryModel.Discrete, memory.Model);
        Assert.Equal((64.0, 40.0), (memory.HostTotalGiB, memory.HostAvailableGiB));
        Assert.Equal(
            [
                new GpuMemorySnapshot(0, Rtx5070, 12227 / 1024.0, 10987 / 1024.0, 957 / 1024.0),
                new GpuMemorySnapshot(1, Rtx5060Ti, 16311 / 1024.0, 15420 / 1024.0, 631 / 1024.0),
            ],
            memory.Gpus);
    }

    [Fact]
    public async Task OnTheGb10TheReaderAnswersExactlyLikeP1()
    {
        var host = new FixedHost(new MemorySnapshot(121.7, 60.25));
        using var reader = new DiscreteNodeMemoryReader(host, TelemetryProbe(new RecordingNvidiaSmi(Fixture("gb10.linux.telemetry.csv", "\n"))), new ManualTime());
        var p1 = new ProbeNodeMemoryReader(host, MemoryModel.Unified);

        var ours = await reader.ReadAsync(Ct);
        var theirs = await p1.ReadAsync(Ct);

        Assert.Equal(MemoryModel.Unified, ours.Model);
        Assert.Empty(ours.Gpus);
        Assert.Equal(JsonSerializer.Serialize(theirs, AgentJson.Options), JsonSerializer.Serialize(ours, AgentJson.Options));
    }

    [Fact]
    public async Task TheReaderReusesAGpuReadForMaxGpuAgeOnTheTimeProvider()
    {
        var runner = new RecordingNvidiaSmi(Fixture("rtx4060ti-x2.linux.telemetry.csv", "\n"));
        var time = new ManualTime();
        using var reader = Reader(runner, time);

        await reader.ReadAsync(Ct);
        time.Advance(TimeSpan.FromMilliseconds(999));
        await reader.ReadAsync(Ct);
        Assert.Single(runner.Calls);

        time.Advance(TimeSpan.FromMilliseconds(1));
        runner.Output = runner.Output.Replace(", 1873,", ", 1000,", StringComparison.Ordinal);
        var fresh = await reader.ReadAsync(Ct);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(1000 / 1024.0, fresh.Gpus[0].FreeGiB);
    }

    [Fact]
    public async Task WhenNvidiaSmiFailsTheReaderKeepsTheLastGpusButNeverInventsThem()
    {
        var runner = new RecordingNvidiaSmi("") { Result = new NvidiaSmiResult(true, 9, "", "Failed to initialize NVML: Driver/library version mismatch") };
        var time = new ManualTime();
        using var reader = Reader(runner, time, maxGpuAge: TimeSpan.Zero);

        // Never read successfully: P1's unified view, and the next read asks again (not cached).
        var before = await reader.ReadAsync(Ct);
        Assert.Equal(MemoryModel.Unified, before.Model);
        Assert.Empty(before.Gpus);
        await reader.ReadAsync(Ct);
        Assert.Equal(2, runner.Calls.Count);

        runner.Result = null;
        runner.Output = Fixture("rtx4060ti-x2.linux.telemetry.csv", "\n");
        var good = await reader.ReadAsync(Ct);
        Assert.Equal(2, good.Gpus.Count);

        runner.Result = new NvidiaSmiResult(true, -1, "", "nvidia-smi did not answer within 5 s.");
        var failing = await reader.ReadAsync(Ct);
        Assert.Equal(MemoryModel.Discrete, failing.Model);
        Assert.Equal(good.Gpus, failing.Gpus);
    }

    [Fact]
    public async Task AFailingHostProbeReturnsTheLastSnapshotOrThrowsWhenThereIsNone()
    {
        var host = new FixedHost(new MemorySnapshot(64, 40));
        using var reader = new DiscreteNodeMemoryReader(host, TelemetryProbe(new RecordingNvidiaSmi(Fixture("rtx4060ti-x2.linux.telemetry.csv", "\n"))), new ManualTime());

        host.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => reader.ReadAsync(Ct));

        host.Fail = false;
        var good = await reader.ReadAsync(Ct);
        host.Fail = true;
        Assert.Same(good, await reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task AForcedMemoryModelWins()
    {
        using var reader = new DiscreteNodeMemoryReader(new FixedHost(new MemorySnapshot(64, 40)),
            TelemetryProbe(new RecordingNvidiaSmi(Fixture("rtx4060ti-x2.linux.telemetry.csv", "\n"))), new ManualTime(), MemoryModel.Unified);

        var memory = await reader.ReadAsync(Ct);

        Assert.Equal(MemoryModel.Unified, memory.Model);
        Assert.Empty(memory.Gpus);
    }

    [Fact]
    public async Task TheSamplersPerGpuMemoryUsesTheDriversFreeValue()
    {
        var options = Options.Create(new TelemetryOptions());
        var ring = new TelemetryRing("boot-1", options);
        using var http = new TelemetryHttpClient(new NoHttp());
        var sampler = new TelemetrySampler(
            ring,
            new FixedHost(new MemorySnapshot(64, 40)),
            TelemetryProbe(new RecordingNvidiaSmi(Fixture("rtx5070-rtx5060ti.windows.telemetry.csv", "\r\n"))),
            Array.Empty<IRuntimeDriver>(),
            http,
            options,
            new ManualTime(),
            NullLogger<TelemetrySampler>.Instance);

        var sample = await sampler.SampleOnceAsync(Ct);

        Assert.Equal(MemoryModel.Discrete, sample!.Memory.Model);
        Assert.Equal([957 / 1024.0, 631 / 1024.0], sample.Memory.Gpus.Select(g => g.FreeGiB));
        Assert.Equal(["NVIDIA GeForce RTX 5070", "NVIDIA GeForce RTX 5060 Ti"], sample.Gpus.Select(g => g.Name));
    }

    // ------------------------------------------------------------------ runner and DI

    [Fact]
    public void OnWindowsTheRunnerAlsoTriesThePreDchNvsmiFolder()
    {
        Assert.Equal(["nvidia-smi", Path.Combine(@"C:\Program Files", "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")],
            NvidiaSmiProcessRunner.Candidates(windows: true, @"C:\Program Files"));
        Assert.Equal(["nvidia-smi"], NvidiaSmiProcessRunner.Candidates(windows: true, null));
        Assert.Equal(["nvidia-smi"], NvidiaSmiProcessRunner.Candidates(windows: false, "/opt"));
    }

    [Fact]
    public async Task TelemetryRegistersTheInventoryAndTheReaderButLeavesINodeMemoryReaderToTheComposition()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IMemoryProbe>(new FixedHost(new MemorySnapshot(64, 40)));
        services.AddSingleton<INvidiaSmiRunner>(new RecordingNvidiaSmi(Fixture("rtx5070-rtx5060ti.windows.inventory.csv", "\n")));
        services.AddAgentTelemetry("boot-1");
        await using var provider = services.BuildServiceProvider();

        var inventory = provider.GetRequiredService<IGpuInventory>();
        Assert.Same(provider.GetRequiredService<NvidiaSmiGpuInventory>(), inventory);
        Assert.NotNull(provider.GetService<DiscreteNodeMemoryReader>());
        Assert.Null(provider.GetService<INodeMemoryReader>());
        Assert.Equal(2, (await inventory.RefreshAsync(Ct)).Count);
    }

    // ------------------------------------------------------------------ FakeAgent discrete mode

    [Fact]
    public async Task TheFakeAgentIsUnifiedAndP1ByDefault()
    {
        await using var agent = await FakeAgent.StartAsync(ct: Ct);
        using var client = new AgentContractClient(agent.Target);

        var caps = (await client.SendAsync(AgentRoutes.Capabilities, [], ct: Ct)).Read<NodeCapabilities>();
        var status = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>();

        Assert.False(agent.IsDiscrete);
        Assert.Equal(MemoryModel.Unified, caps.Memory.Model);
        Assert.True(Assert.Single(caps.Gpus).SharesHostMemory);
        Assert.Equal([new GpuMemorySnapshot(0, new FakeAgentOptions().GpuUuid)], status.Memory.Gpus);
        Assert.Empty(caps.Features.Intersect(AgentFeatures.P2));
        Assert.Throws<InvalidOperationException>(() => agent.RemoveGpu("GPU-x"));
    }

    [Fact]
    public async Task TheDiscreteFakeReportsEveryGpuInCapabilitiesStatusAndTelemetry()
    {
        var gpus = FakeDiscreteGpu.TwoBy16;
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions
        {
            DiscreteGpus = gpus, TotalMemoryGiB = 64, AvailableMemoryGiB = 48, TelemetryInterval = TimeSpan.FromMilliseconds(20),
        }, Ct);
        using var client = new AgentContractClient(agent.Target);

        var caps = (await client.SendAsync(AgentRoutes.Capabilities, [], ct: Ct)).Read<NodeCapabilities>();
        Assert.Equal(MemoryModel.Discrete, caps.Memory.Model);
        Assert.Equal(gpus.Select((g, i) => (i, g.Uuid, (double?)16, false)), caps.Gpus.Select(g => (g.Index, g.Uuid, g.MemoryTotalGiB, g.SharesHostMemory)));
        Assert.Empty(caps.Features.Intersect(AgentFeatures.P2)); // discrete mode alone adds no P2 flag

        var status = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>();
        Assert.Equal(MemoryModel.Discrete, status.Memory.Model);
        Assert.Equal(48, status.Memory.HostAvailableGiB);
        Assert.All(status.Memory.Gpus, g => Assert.Equal((16.0, 0.5, 15.5), (g.TotalGiB, g.UsedGiB, g.FreeGiB)));

        var page = await EventuallyAsync(async () =>
        {
            var p = (await client.SendAsync(AgentRoutes.Telemetry, [], ct: Ct)).Read<TelemetryPage>();
            return p.Samples.Count > 0 ? p : null;
        });
        var sample = page.Samples[^1];
        Assert.Equal(gpus.Select(g => g.Uuid), sample.Gpus.Select(g => g.Uuid));
        Assert.All(sample.Gpus, g => Assert.Equal((16.0, 15.5, "NVIDIA GeForce RTX 5060 Ti"), (g.MemoryTotalGiB!.Value, g.MemoryFreeGiB!.Value, g.Name)));
    }

    [Fact]
    public async Task TheDiscreteFakeLaunchesByUuidTakesVramPerGpuAndRefusesARemovedCard()
    {
        var gpus = FakeDiscreteGpu.TwoBy16;
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions { DiscreteGpus = gpus }, Ct);
        using var client = new AgentContractClient(agent.Target);

        var launch = AgentContractSuite.LaunchFor("model-a", budgetGiB: 8, gpus: new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [gpus[1].Uuid] });
        var accepted = await client.SendAsync(AgentRoutes.RuntimeLaunch, ["model-a"], launch, ct: Ct);
        Assert.Equal(202, accepted.Status);
        var ready = await EventuallyAsync(async () =>
        {
            var list = (await client.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct)).Read<List<RuntimeStatus>>();
            return list.FirstOrDefault(r => r.ModelId == "model-a" && r.State == ModelState.Ready);
        });
        Assert.Equal([new GpuRef(1, gpus[1].Uuid)], ready.Gpus);

        var memory = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>().Memory;
        Assert.Equal([15.5, 7.5], memory.Gpus.Select(g => g.FreeGiB));

        // Renumbered: the card keeps its UUID, its index changes.
        agent.RenumberGpus(gpus[1].Uuid, gpus[0].Uuid);
        var renumbered = (await client.SendAsync(AgentRoutes.Capabilities, [], ct: Ct)).Read<NodeCapabilities>();
        Assert.Equal([gpus[1].Uuid, gpus[0].Uuid], renumbered.Gpus.Select(g => g.Uuid));
        memory = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>().Memory;
        Assert.Equal([(0, 7.5), (1, 15.5)], memory.Gpus.Select(g => (g.Index, g.FreeGiB!.Value)));

        // Removed: a launch naming it is refused with gpu_missing listing exactly that UUID.
        agent.RemoveGpu(gpus[0].Uuid);
        var refused = await client.SendAsync(AgentRoutes.RuntimeLaunch, ["model-b"],
            AgentContractSuite.LaunchFor("model-b", budgetGiB: 4, gpus: new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [gpus[0].Uuid, gpus[1].Uuid] }), ct: Ct);
        Assert.Equal(422, refused.Status);
        var error = refused.Read<AgentErrorResponse>().Error;
        Assert.Equal(ErrorCodes.GpuMissing, error.Code);
        Assert.Equal([gpus[0].Uuid], error.Details!.Value.Deserialize<GpuMissingDetails>(AgentJson.Options)!.Uuids);
    }

    [Fact]
    public async Task TheDiscreteFakesGpuIdleCoversEveryGpuAndRefusesAnUnknownIndex()
    {
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions { DiscreteGpus = FakeDiscreteGpu.TwoBy16 }, Ct);
        using var client = new AgentContractClient(agent.Target);

        var unknown = await client.SendAsync(AgentRoutes.GpuIdle, [], query: "?seconds=1&gpus=2", ct: Ct);
        Assert.Equal(400, unknown.Status);

        var idle = (await client.SendAsync(AgentRoutes.GpuIdle, [], query: "?seconds=1&gpus=1", ct: Ct)).Read<GpuIdleResponse>();
        Assert.Equal((1, FakeDiscreteGpu.TwoBy16[1].Uuid), (Assert.Single(idle.PerGpu).Index, idle.PerGpu[0].Uuid));
        var all = (await client.SendAsync(AgentRoutes.GpuIdle, [], query: "?seconds=1", ct: Ct)).Read<GpuIdleResponse>();
        Assert.Equal([0, 1], all.PerGpu.Select(g => g.Index));
    }

    [Fact]
    public async Task TheDiscreteFakesKnobsRejectBadInput()
    {
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions { DiscreteGpus = FakeDiscreteGpu.TwoBy16 }, Ct);
        string a = FakeDiscreteGpu.TwoBy16[0].Uuid;

        Assert.Throws<ArgumentException>(() => agent.RenumberGpus(a, a));
        Assert.Throws<ArgumentException>(() => agent.RenumberGpus(a));
        Assert.Throws<ArgumentException>(() => agent.RemoveGpu("GPU-not-here"));
        Assert.Throws<ArgumentException>(() => agent.AddGpu(new FakeDiscreteGpu(a)));

        agent.AddGpu(new FakeDiscreteGpu("GPU-new", "NVIDIA GeForce RTX 5070", 12));
        agent.SetGpuUsed("GPU-new", 2);
        Assert.Equal(("GPU-new", 2.0), (agent.DiscreteGpus[2].Uuid, agent.DiscreteGpus[2].UsedGiB));
    }

    // ------------------------------------------------------------------ helpers

    private static string Fixture(string name, string lineEnd, [CallerFilePath] string sourceFile = "")
    {
        string text = File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "nvidia-smi", name));
        Assert.False(string.IsNullOrWhiteSpace(text), $"fixture {name} is empty");
        return text.ReplaceLineEndings(lineEnd);
    }

    /// <summary>The recording with its two rows swapped and their indices exchanged (a renumbering).</summary>
    private static string Renumbered(string recorded)
    {
        var rows = recorded.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(r => r.TrimEnd('\r')).ToArray();
        return $"0{rows[1][1..]}\r\n1{rows[0][1..]}\r\n";
    }

    private static NodeCapabilities BaseCapabilities() => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "1.0.0",
        Os = new NodeOsInfo("windows", "Windows 11", "10.0.26300"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(MemoryModel.Unified, 64),
    };

    private static NvidiaSmiGpuTelemetryProbe TelemetryProbe(INvidiaSmiRunner runner) =>
        new(runner, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance);

    private static DiscreteNodeMemoryReader Reader(INvidiaSmiRunner runner, TimeProvider time, TimeSpan? maxGpuAge = null) =>
        new(new FixedHost(new MemorySnapshot(64, 40)), TelemetryProbe(runner), time, maxGpuAge: maxGpuAge);

    private static async Task<T> EventuallyAsync<T>(Func<Task<T?>> probe) where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20, Ct);
        }
    }

    /// <summary><c>nvidia-smi</c> answering with recorded output (or a fixed result) and recording the arguments.</summary>
    private sealed class RecordingNvidiaSmi(string output) : INvidiaSmiRunner
    {
        public string Output { get; set; } = output;

        public NvidiaSmiResult? Result { get; set; }

        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default)
        {
            Calls.Enqueue(arguments);
            return Task.FromResult(Result ?? new NvidiaSmiResult(true, 0, Output, ""));
        }
    }

    private sealed class FixedHost(MemorySnapshot snapshot) : IMemoryProbe
    {
        public bool Fail { get; set; }

        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) =>
            Fail ? Task.FromException<MemorySnapshot>(new IOException("/proc/meminfo unreadable")) : Task.FromResult(snapshot);
    }

    /// <summary>A clock that moves only when told, for timestamps as well as wall time.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1_000_000;
        private DateTimeOffset _now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by)
        {
            _ticks += by.Ticks;
            _now += by;
        }
    }

    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no model is running");
    }
}
