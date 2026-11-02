using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// W9 review (MN-132, D102): the agent's memory model is decided once per process (<see cref="AgentMemoryModel"/>, MN-130),
/// and the capability report follows that decision — its memory model and the P2 flags — so admission, <c>/status</c> and
/// what the master is told never disagree. Before, a node whose <c>nvidia-smi</c> failed at startup admitted as unified
/// but, once the probe worked, reported itself discrete with <c>gpus.uuids</c>/<c>launch.env</c>.
/// </summary>
public sealed class AgentMemoryModelTests
{
    private const string Uuid0 = "GPU-00000000-0000-0000-0000-00000000d000";
    private const string Gb10Uuid = "GPU-00000000-0000-0000-0000-0000000000b1";

    private static readonly NvidiaSmiResult Failed = new(true, 9, "", "NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AProbeThatFailsAtStartupKeepsTheNodeUnifiedInAdmissionAndInTheReport()
    {
        var runner = new SwitchableNvidiaSmi { Answer = _ => Failed };
        var inventory = new NvidiaSmiGpuInventory(runner);
        var memory = Model(inventory);

        Assert.Equal(MemoryModel.Unified, memory.Model);
        Assert.False(inventory.Answered);

        // nvidia-smi works later and sees a 16 GiB GPU: the telemetry contributor alone would now say discrete.
        runner.Answer = args => args == NvidiaSmiGpuInventory.Arguments
            ? new NvidiaSmiResult(true, 0, $"0, {Uuid0}, NVIDIA Test 16G, 16384, 12.0, 617.14\n", "")
            : Failed;
        var report = await Report(inventory, memory);

        Assert.True(inventory.Answered);
        Assert.Equal(MemoryModel.Unified, report.Memory.Model);
        Assert.Single(report.Gpus);
        Assert.DoesNotContain("p2.test-flag", report.Features);
        Assert.Equal(MemoryModel.Unified, memory.Model); // decided once
    }

    [Fact]
    public async Task ADiscreteProbeReportsDiscreteAndPassesTheP2Flags()
    {
        var inventory = new NvidiaSmiGpuInventory(new SwitchableNvidiaSmi
        {
            Answer = _ => new NvidiaSmiResult(true, 0, $"0, {Uuid0}, NVIDIA Test 16G, 16384, 12.0, 617.14\n", ""),
        });
        var memory = Model(inventory);

        var report = await Report(inventory, memory);

        Assert.Equal(MemoryModel.Discrete, memory.Model);
        Assert.True(inventory.Answered);
        Assert.Equal(MemoryModel.Discrete, report.Memory.Model);
        Assert.Contains("p2.test-flag", report.Features);
    }

    [Fact]
    public async Task TheGb10AndANodeWithoutNvidiaSmiStayExactlyP1()
    {
        var gb10 = new NvidiaSmiGpuInventory(new SwitchableNvidiaSmi
        {
            Answer = _ => new NvidiaSmiResult(true, 0, $"0, {Gb10Uuid}, NVIDIA GB10, [N/A], 12.1, 580.95.05\n", ""),
        });
        var none = new NvidiaSmiGpuInventory(new SwitchableNvidiaSmi { Answer = _ => NvidiaSmiResult.NotFound });

        foreach (var inventory in new[] { gb10, none })
        {
            var memory = Model(inventory);
            var report = await Report(inventory, memory);
            Assert.Equal(MemoryModel.Unified, memory.Model);
            Assert.True(inventory.Answered);
            Assert.Equal(MemoryModel.Unified, report.Memory.Model);
            Assert.Equal([AgentFeatures.TelemetryV1], report.Features);
        }
    }

    [Fact]
    public async Task AForcedMemoryModelIsWhatTheNodeReports()
    {
        var gb10 = new NvidiaSmiGpuInventory(new SwitchableNvidiaSmi
        {
            Answer = _ => new NvidiaSmiResult(true, 0, $"0, {Gb10Uuid}, NVIDIA GB10, [N/A], 12.1, 580.95.05\n", ""),
        });
        var memory = Model(gb10, MemoryModel.Discrete);

        var report = await Report(gb10, memory);

        Assert.Equal(MemoryModel.Discrete, report.Memory.Model);
        Assert.Contains("p2.test-flag", report.Features);
    }

    [Fact]
    public async Task AForcedModelThatDiffersFromTheProbeLogsInformationOnceAndNeverTheRestartAdvice()
    {
        // MN-124: Agent:MemoryModel=Unified on a PC with a 16 GiB card is the operator's choice: 2644 (information), not
        // 2643 ("restart the agent"), which stays for a startup decision the probe no longer agrees with.
        var log = new MirbadLLM.UnitTests.Multinode.P2Exit.EventLog();
        using var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(log).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace));
        var logger = factory.CreateLogger<AgentMemoryModel>();
        var pc = new NvidiaSmiGpuInventory(new SwitchableNvidiaSmi
        {
            Answer = _ => new NvidiaSmiResult(true, 0, $"0, {Uuid0}, NVIDIA Test 16G, 16384, 12.0, 617.14\n", ""),
        });
        var forced = Model(pc, MemoryModel.Unified);
        Assert.True(forced.IsForced);
        var forcedReport = new MemoryModelReportContributor(forced, logger); // one per process, as composed

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(MemoryModel.Unified, (await Report(pc, forced, forcedReport)).Memory.Model);
        }

        Assert.Equal(0, log.Count(2643));
        Assert.Single(log.Records, r => r.EventId == 2644 && r.Level == Microsoft.Extensions.Logging.LogLevel.Information);

        // Not forced, decided unified at startup (failing probe), then the probe works: the restart advice, once.
        var runner = new SwitchableNvidiaSmi { Answer = _ => Failed };
        var recovering = new NvidiaSmiGpuInventory(runner);
        var decided = Model(recovering);
        Assert.False(decided.IsForced);
        Assert.Equal(MemoryModel.Unified, decided.Model);
        runner.Answer = args => args == NvidiaSmiGpuInventory.Arguments
            ? new NvidiaSmiResult(true, 0, $"0, {Uuid0}, NVIDIA Test 16G, 16384, 12.0, 617.14\n", "")
            : Failed;
        var decidedReport = new MemoryModelReportContributor(decided, logger);
        await Report(recovering, decided, decidedReport);
        await Report(recovering, decided, decidedReport);
        Assert.Single(log.Records, r => r.EventId == 2643 && r.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Equal(1, log.Count(2644));
    }

    private static AgentMemoryModel Model(NvidiaSmiGpuInventory inventory, MemoryModel? forced = null) =>
        new(inventory, Options.Create(new TelemetryOptions { MemoryModel = forced }), NullLogger<AgentMemoryModel>.Instance);

    /// <summary>The composition's order: telemetry, the report's memory model, then a gated P2 flag.</summary>
    private static async Task<NodeCapabilities> Report(
        NvidiaSmiGpuInventory inventory, AgentMemoryModel memory, MemoryModelReportContributor? reportContributor = null)
    {
        ICapabilityContributor[] contributors =
        [
            new TelemetryCapabilityContributor(inventory),
            reportContributor ?? new MemoryModelReportContributor(memory, NullLogger<AgentMemoryModel>.Instance),
            new DiscreteNodeContributor(new FlagContributor(), memory),
        ];
        var report = new NodeCapabilities
        {
            Protocol = Protocol.Range,
            AgentVersion = "1.0.0",
            Features = [],
            Os = new NodeOsInfo("linux", "Ubuntu"),
            Arch = "x64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 64),
        };
        foreach (var contributor in contributors)
        {
            report = await contributor.ContributeAsync(report, Ct);
        }

        return report;
    }

    private sealed class FlagContributor : ICapabilityContributor
    {
        public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default) =>
            Task.FromResult(current with { Features = [.. current.Features, "p2.test-flag"] });
    }

    private sealed class SwitchableNvidiaSmi : INvidiaSmiRunner
    {
        public required Func<string, NvidiaSmiResult> Answer { get; set; }

        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default) => Task.FromResult(Answer(arguments));
    }
}
