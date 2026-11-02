using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Fakes;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;
using DevFakeDriver = MirbadLLM.Agent.Runtimes.FakeRuntimeDriver;

namespace MirbadLLM.UnitTests.Multinode.DevSetup;

/// <summary>
/// MN-122: the discrete mode of the development fake runtime (<c>discrete:2x16</c>) below the agent: the settings, the
/// fake <c>nvidia-smi</c> as the agent's real parsers read it, and the fake driver's GPU selection (<c>gpu_missing</c>
/// before reserving, GPUs and read-back labels of a <c>uuids</c> launch, VRAM per GPU). The end-to-end run with the real
/// master and agents is <see cref="DevMultinodeSmokeTests"/>.
/// </summary>
public sealed class DevFakeDiscreteTests
{
    private const string Gpu0 = "GPU-00000000-0000-0000-0000-00000000de00";
    private const string Gpu1 = "GPU-00000000-0000-0000-0000-00000000de01";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void DiscreteSettingsGiveTheGpusInIndexOrder()
    {
        var fake = new DevFakeRuntimeOptions { Enabled = true, Memory = "discrete:2x16", HostMemoryGiB = 32 };
        fake.Validate();

        Assert.True(fake.IsDiscrete);
        Assert.Equal(MemoryModel.Discrete, fake.MemoryModel);
        Assert.Equal(32, fake.MemoryGiB);
        Assert.Equal([new DevFakeGpu(0, Gpu0, "Fake GPU (dev)", 16), new DevFakeGpu(1, Gpu1, "Fake GPU (dev)", 16)], fake.Gpus);

        // Configured UUIDs keep their order: listing them the other way round renumbers the cards.
        var renumbered = new DevFakeRuntimeOptions { Memory = "discrete:2x16", GpuUuids = [Gpu1, Gpu0], GpuName = "RTX 5060 Ti" };
        renumbered.Validate();
        Assert.Equal([(0, Gpu1), (1, Gpu0)], renumbered.Gpus.Select(g => (g.Index, g.Uuid)));
        Assert.All(renumbered.Gpus, g => Assert.Equal("RTX 5060 Ti", g.Name));

        // Unified stays P1: one pool, no GPU list.
        var unified = new DevFakeRuntimeOptions { Memory = "unified:64" };
        unified.Validate();
        Assert.False(unified.IsDiscrete);
        Assert.Equal(64, unified.MemoryGiB);
        Assert.Empty(unified.Gpus);
    }

    [Theory]
    [InlineData("discrete:0x16", null, "discrete:<n>x<GiB>")]
    [InlineData("discrete:17x16", null, "discrete:<n>x<GiB>")]
    [InlineData("discrete:2x0", null, "discrete:<n>x<GiB>")]
    [InlineData("discrete:2", null, "discrete:<n>x<GiB>")]
    [InlineData("discrete:x16", null, "discrete:<n>x<GiB>")]
    [InlineData("discrete:2x16", "GPU-00000000-0000-0000-0000-00000000de00", "1 UUID(s) for 2 GPU(s)")]
    [InlineData("discrete:2x16", "GPU-a|GPU-a", "is not a GPU UUID")]
    [InlineData("discrete:2x16", "GPU-00000000-a|GPU-00000000-a", "more than once")]
    [InlineData("discrete:1x16", "card-0", "is not a GPU UUID")]
    [InlineData("unified:64", "GPU-00000000-a", "for discrete nodes")]
    [InlineData("hybrid:64", null, "is not unified:<GiB> or discrete")]
    public void BadSettingsAreRefusedNamingTheProblem(string memory, string? uuids, string message)
    {
        var fake = new DevFakeRuntimeOptions { Memory = memory, GpuUuids = uuids?.Split('|').ToList() };
        var ex = Assert.Throws<ArgumentException>(fake.Validate);
        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BadHostMemoryOrGpuNameIsRefused()
    {
        Assert.Contains("HostMemoryGiB", Assert.Throws<ArgumentException>(new DevFakeRuntimeOptions { Memory = "discrete:2x16", HostMemoryGiB = 0 }.Validate).Message, StringComparison.Ordinal);
        Assert.Contains("commas", Assert.Throws<ArgumentException>(new DevFakeRuntimeOptions { Memory = "discrete:2x16", GpuName = "RTX, 16 GB" }.Validate).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFakeNvidiaSmiReadsAsTwoDiscreteGpusThroughTheAgentsParsers()
    {
        await using var h = new Harness();

        IReadOnlyList<GpuCapability> inventory = await new NvidiaSmiGpuInventory(h.NvidiaSmi).GetAsync(Ct);
        Assert.Equal([(0, Gpu0), (1, Gpu1)], inventory.Select(g => (g.Index, g.Uuid)));
        Assert.All(inventory, g =>
        {
            Assert.Equal(16, g.MemoryTotalGiB);
            Assert.False(g.SharesHostMemory);
            Assert.Equal("12.0", g.ComputeCapability);
            Assert.Equal("Fake GPU (dev)", g.Name);
        });
        Assert.Equal(MemoryModel.Discrete, NvidiaSmiGpuInventory.MemoryModelOf(inventory));

        GpuTelemetryReading reading = await new NvidiaSmiGpuTelemetryProbe(h.NvidiaSmi, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance).ReadAsync(Ct);
        Assert.Equal(GpuProbeStatus.Ok, reading.Status);
        Assert.All(reading.Gpus, g =>
        {
            Assert.Equal(DevFakeRuntimeOptions.GpuBaselineUsedGiB, g.MemoryUsedGiB);
            Assert.Equal(16 - DevFakeRuntimeOptions.GpuBaselineUsedGiB, g.MemoryFreeGiB);
            Assert.Equal(0, g.UtilizationPercent);
        });

        // A query the agent does not make fails like an unknown nvidia-smi query, so a new caller is noticed.
        NvidiaSmiResult other = await h.NvidiaSmi.RunAsync("--query-compute-apps=pid,used_memory --format=csv", Ct);
        Assert.True(other.Found);
        Assert.False(other.Succeeded);
    }

    [Fact]
    public async Task AUuidsLaunchRunsOnTheSelectedGpuWithReadBackLabels()
    {
        await using var h = new Harness();
        var spec = Spec(new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [Gpu1] });
        h.Driver.CheckSpec("m1", spec);

        RuntimeLaunchResult result = await h.Driver.LaunchAsync(h.Launch("m1", spec), new NoStages(), Ct);

        Assert.Equal(RuntimeLaunchOutcome.Created, result.Outcome);
        Assert.Equal([new GpuRef(1, Gpu1)], result.Instance.Gpus);
        Assert.Equal(Gpu1, result.Instance.Labels[ContainerLabels.GpuUuids]);
        Assert.Equal("16", result.Instance.Labels[ContainerLabels.GpuTotalGiB]);
        LaunchConfig? config = await h.Driver.GetLaunchConfigAsync("m1", Ct);
        Assert.Equal([new GpuRef(1, Gpu1)], config!.Gpus);

        // Its 4 GiB budget is VRAM used on GPU 1 only, as nvidia-smi reports it.
        var gpus = await h.TelemetryAsync();
        Assert.Equal(DevFakeRuntimeOptions.GpuBaselineUsedGiB, gpus[0].MemoryUsedGiB);
        Assert.Equal(DevFakeRuntimeOptions.GpuBaselineUsedGiB + 4, gpus[1].MemoryUsedGiB);
        Assert.Equal(0, gpus[0].UtilizationPercent);
        Assert.True(gpus[1].UtilizationPercent > 0);

        await h.Driver.StopAsync("m1", new NoStages(), Ct);
        Assert.All(await h.TelemetryAsync(), g => Assert.Equal(DevFakeRuntimeOptions.GpuBaselineUsedGiB, g.MemoryUsedGiB));
    }

    [Fact]
    public async Task ATensorParallelLaunchSplitsItsBudgetOverItsGpus()
    {
        await using var h = new Harness();
        await h.Driver.LaunchAsync(h.Launch("tp", Spec(new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [Gpu0, Gpu1] })), new NoStages(), Ct);

        Assert.All(await h.TelemetryAsync(), g => Assert.Equal(DevFakeRuntimeOptions.GpuBaselineUsedGiB + 2, g.MemoryUsedGiB));
    }

    [Fact]
    public async Task AnAllLaunchSitsWhereVllmPutsIt()
    {
        // What a master sends before MN-106: no selection. vLLM then uses GPU 0, or the first N with tensor parallelism.
        await using var h = new Harness();
        RuntimeLaunchResult one = await h.Driver.LaunchAsync(h.Launch("all", Spec(new GpuSelection())), new NoStages(), Ct);
        Assert.Empty(one.Instance.Gpus);
        Assert.False(one.Instance.Labels.ContainsKey(ContainerLabels.GpuUuids));
        var gpus = await h.TelemetryAsync();
        Assert.Equal(DevFakeRuntimeOptions.GpuBaselineUsedGiB + 4, gpus[0].MemoryUsedGiB);
        Assert.Equal(DevFakeRuntimeOptions.GpuBaselineUsedGiB, gpus[1].MemoryUsedGiB);

        await h.Driver.LaunchAsync(h.Launch("all-tp2", Spec(new GpuSelection(), "--tensor-parallel-size", "2"), port: 1), new NoStages(), Ct);
        gpus = await h.TelemetryAsync();
        Assert.Equal(DevFakeRuntimeOptions.GpuBaselineUsedGiB + 6, gpus[0].MemoryUsedGiB);
        Assert.Equal(DevFakeRuntimeOptions.GpuBaselineUsedGiB + 2, gpus[1].MemoryUsedGiB);
    }

    [Theory]
    [InlineData(new[] { "--tensor-parallel-size", "2" }, 2)]
    [InlineData(new[] { "--tensor-parallel-size=3" }, 3)]
    [InlineData(new[] { "-tp", "4" }, 4)]
    [InlineData(new[] { "--tensor-parallel-size", "x" }, 1)]
    [InlineData(new[] { "--tensor-parallel-size" }, 1)]
    [InlineData(new string[0], 1)]
    public void TensorParallelSizeIsReadFromTheArgv(string[] argv, int size) =>
        Assert.Equal(size, DevFakeDriver.TensorParallelSize(argv));

    [Fact]
    public async Task AMissingGpuIsRefusedBeforeAnythingRuns()
    {
        await using var h = new Harness();
        const string gone = "GPU-00000000-0000-0000-0000-0000000dead0";
        var spec = Spec(new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [Gpu0, gone] });

        var ex = Assert.Throws<AgentException>(() => h.Driver.CheckSpec("m1", spec));
        Assert.Equal(ErrorCodes.GpuMissing, ex.Code);
        Assert.Equal(422, ex.StatusCode);
        Assert.Equal([gone], ex.Error.ReadDetails<GpuMissingDetails>()!.Uuids);
        Assert.Contains("dev-b", ex.Message, StringComparison.Ordinal);

        // The launch itself refuses too (a replay that skipped the check) and leaves no instance.
        await Assert.ThrowsAsync<AgentException>(() => h.Driver.LaunchAsync(h.Launch("m1", spec), new NoStages(), Ct));
        Assert.Empty(await h.Driver.ListAsync(Ct));

        // An empty or repeated selection is a malformed spec.
        Assert.Equal(ErrorCodes.SpecRejected, Assert.Throws<AgentException>(() => h.Driver.CheckSpec("m1", Spec(new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [] }))).Code);
        Assert.Equal(ErrorCodes.SpecRejected, Assert.Throws<AgentException>(() => h.Driver.CheckSpec("m1", Spec(new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [Gpu0, Gpu0] }))).Code);
    }

    [Fact]
    public async Task AUnifiedFakeRefusesAUuidSelectionAsTheDgxDoes()
    {
        await using var h = new Harness("unified:64");
        var ex = Assert.Throws<AgentException>(() => h.Driver.CheckSpec("m1", Spec(new GpuSelection { Mode = GpuSelectionMode.Uuids, Uuids = [Gpu0] })));
        Assert.Equal(ErrorCodes.SpecRejected, ex.Code);
        Assert.Equal(0, h.Driver.GpuUsedGiB(Gpu0));
    }

    private static LaunchSpec Spec(GpuSelection gpus, params string[] extraArgs) => new()
    {
        Kind = LaunchDelivery.Container,
        Image = "vllm/vllm-openai:dev",
        Argv = ["--model", "/models/fake-model", "--served-model-name", "fake", .. extraArgs],
        ContainerPort = 8000,
        Mounts =
        [
            new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true),
            new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false),
        ],
        Gpus = gpus,
        IpcHost = true,
        Labels = new Dictionary<string, string> { [ContainerLabels.BudgetGiB] = "4" },
    };

    private sealed class NoStages : IRuntimeStageSink
    {
        public Task BeforeStepAsync(string stage, CancellationToken ct) => Task.CompletedTask;

        public Task StageReachedAsync(string stage, OperationProgress? progress, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A discrete (or unified) fake driver and its nvidia-smi, as <see cref="DevFakeRuntime"/> composes them.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly int _ports = TestPorts.ReserveBlock(4);

        public Harness(string memory = "discrete:2x16")
        {
            Fake = new DevFakeRuntimeOptions { Enabled = true, Memory = memory, StartDelay = TimeSpan.Zero, TokenDelay = TimeSpan.Zero };
            Fake.Validate();
            Driver = new DevFakeDriver(
                new ContainerRuntimeOptions { NodeId = "dev-b" },
                () => throw new InvalidOperationException("The tests pass a host port."),
                Fake,
                TimeProvider.System,
                NullLogger<DevFakeDriver>.Instance);
            NvidiaSmi = new DevFakeNvidiaSmi(Fake, Driver);
        }

        public DevFakeRuntimeOptions Fake { get; }

        public DevFakeDriver Driver { get; }

        public DevFakeNvidiaSmi NvidiaSmi { get; }

        public RuntimeLaunch Launch(string modelId, LaunchSpec spec, int port = 0) => new()
        {
            ModelId = modelId,
            OperationId = "op-" + modelId,
            Mode = LaunchMode.EnsureStarted,
            Spec = spec,
            HostPort = _ports + port,
        };

        public async Task<IReadOnlyList<GpuSample>> TelemetryAsync()
        {
            GpuTelemetryReading reading = await new NvidiaSmiGpuTelemetryProbe(NvidiaSmi, NullLogger<NvidiaSmiGpuTelemetryProbe>.Instance).ReadAsync(Ct);
            Assert.Equal(GpuProbeStatus.Ok, reading.Status);
            return reading.Gpus;
        }

        public ValueTask DisposeAsync() => Driver.DisposeAsync();
    }
}
