using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore cancellation.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-120: <see cref="RemoteTelemetryCollector"/> fills <see cref="HostSample.Gpus"/> on discrete nodes (design §8.3 "one
/// chart per GPU plus host RAM"; MN-101 froze the shape): one entry per GPU from the telemetry and the memory snapshot,
/// with each model's memory split evenly over the GPUs its placement names. Unified nodes keep <c>Gpus</c> null, so their
/// samples are exactly P1's.
/// </summary>
public sealed class HostGpuSampleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UnifiedSamplesHaveNoPerGpuReadings()
    {
        var sample = Sample(MemoryModel.Unified, gpus: [new GpuSample { Index = 0, Uuid = "GPU-gb10", UtilizationPercent = 40 }]);

        Assert.Null(RemoteTelemetryCollector.ToHostGpus(sample, new Dictionary<string, double> { ["m"] = 36 }, Definitions()));
    }

    [Fact]
    public void DiscreteGpus_MergeTelemetryAndTheMemorySnapshot_InIndexOrder()
    {
        var sample = Sample(MemoryModel.Discrete,
            gpus:
            [
                new GpuSample { Index = 1, Uuid = "GPU-b", UtilizationPercent = 20, MemoryUsedGiB = 3, MemoryTotalGiB = 16 },
                new GpuSample { Index = 0, Uuid = "GPU-a", UtilizationPercent = 70 }, // no memory fields in telemetry
            ],
            memory:
            [
                new GpuMemorySnapshot(0, "GPU-a", TotalGiB: 24, UsedGiB: 10, FreeGiB: 14),
                new GpuMemorySnapshot(2, "GPU-c", TotalGiB: 8, UsedGiB: 1, FreeGiB: 7), // missing from telemetry
            ]);

        var gpus = RemoteTelemetryCollector.ToHostGpus(sample, null, Definitions());

        Assert.NotNull(gpus);
        Assert.Equal([0, 1, 2], gpus.Select(g => g.Index));
        Assert.Equal(["GPU-a", "GPU-b", "GPU-c"], gpus.Select(g => g.Uuid));
        Assert.Equal([70, 20, null], gpus.Select(g => g.UtilizationPercent));
        Assert.Equal([10, 3, 1], gpus.Select(g => g.MemoryUsedGiB));
        Assert.Equal([24, 16, 8], gpus.Select(g => g.MemoryTotalGiB));
        Assert.All(gpus, g => Assert.Null(g.ModelMemoryGiB)); // nothing known about models
    }

    [Fact]
    public void ModelMemory_IsSplitOverThePlacedGpus_ByUuidThenIndex_AndTheOnlyGpuTakesAnUnplacedModel()
    {
        var sample = Sample(MemoryModel.Discrete, gpus:
        [
            new GpuSample { Index = 0, Uuid = "GPU-a", MemoryUsedGiB = 15, MemoryTotalGiB = 16 },
            new GpuSample { Index = 1, Uuid = "GPU-b", MemoryUsedGiB = 15, MemoryTotalGiB = 16 },
        ]);
        var definitions = Definitions(
            Model("tp", new ModelPlacement("pc", GpuUuids: ["GPU-b", "GPU-a"])),
            Model("by-index", new ModelPlacement("pc", GpuIndices: [1])),
            Model("unknown-gpus", new ModelPlacement("pc")));

        var gpus = RemoteTelemetryCollector.ToHostGpus(
            sample, new Dictionary<string, double> { ["tp"] = 20, ["by-index"] = 6, ["unknown-gpus"] = 4 }, definitions)!;

        Assert.Equal(new Dictionary<string, double> { ["tp"] = 10 }, gpus[0].ModelMemoryGiB);
        Assert.Equal(new Dictionary<string, double> { ["tp"] = 10, ["by-index"] = 6 }, gpus[1].ModelMemoryGiB);
        // Two GPUs and no GPUs in the placement: the model cannot be told apart, so no GPU claims it.

        var single = Sample(MemoryModel.Discrete, gpus: [new GpuSample { Index = 0, Uuid = "GPU-a", MemoryUsedGiB = 5, MemoryTotalGiB = 16 }]);
        var only = RemoteTelemetryCollector.ToHostGpus(single, new Dictionary<string, double> { ["unknown-gpus"] = 4 }, definitions)!;
        Assert.Equal(new Dictionary<string, double> { ["unknown-gpus"] = 4 }, Assert.Single(only).ModelMemoryGiB);
    }

    [Fact]
    public void LlamaCppModelMemory_FollowsItsPerGpuBudgets_NotAnEvenSplit()
    {
        // MN-239 item 10 (D131): the launch sends LlamaCpp.GpuBudgetsGiB per GPU, so a 15/5 GiB split must not read
        // 10/10 on the headroom bars. The budgets are in placement order, which is not the GPUs' index order here.
        var sample = Sample(MemoryModel.Discrete, gpus:
        [
            new GpuSample { Index = 0, Uuid = "GPU-a", MemoryUsedGiB = 6, MemoryTotalGiB = 16 },
            new GpuSample { Index = 1, Uuid = "GPU-b", MemoryUsedGiB = 15.5, MemoryTotalGiB = 16 },
        ]);
        var definitions = Definitions(
            Llama("split", new ModelPlacement("pc", GpuUuids: ["GPU-b", "GPU-a"]), [15, 5]),
            Llama("even", new ModelPlacement("pc", GpuUuids: ["GPU-a", "GPU-b"]), null),
            Llama("mismatched", new ModelPlacement("pc", GpuUuids: ["GPU-a", "GPU-b"]), [15, 5, 5]));

        var gpus = RemoteTelemetryCollector.ToHostGpus(
            sample, new Dictionary<string, double> { ["split"] = 20, ["even"] = 8, ["mismatched"] = 20 }, definitions)!;

        // GPU-b is the placement's first GPU, so it takes the 15 GiB budget; GPU-a the 5 GiB one.
        Assert.Equal(5, gpus[0].ModelMemoryGiB!["split"]);
        Assert.Equal(15, gpus[1].ModelMemoryGiB!["split"]);

        // No own budgets, or budgets that do not match the placement: the even split the agent then admits.
        Assert.Equal(4, gpus[0].ModelMemoryGiB!["even"]);
        Assert.Equal(4, gpus[1].ModelMemoryGiB!["even"]);
        Assert.Equal(10, gpus[0].ModelMemoryGiB!["mismatched"]);
        Assert.Equal(10, gpus[1].ModelMemoryGiB!["mismatched"]);
    }

    [Fact]
    public void AMeasuredTotalOfALlamaCppModel_IsSplitInTheSameProportion()
    {
        // The figure may be the measured total rather than the budget; the proportion is what the budgets decide.
        var sample = Sample(MemoryModel.Discrete, gpus:
        [
            new GpuSample { Index = 0, Uuid = "GPU-a", MemoryUsedGiB = 5, MemoryTotalGiB = 16 },
            new GpuSample { Index = 1, Uuid = "GPU-b", MemoryUsedGiB = 13, MemoryTotalGiB = 16 },
        ]);
        var definitions = Definitions(Llama("split", new ModelPlacement("pc", GpuUuids: ["GPU-a", "GPU-b"]), [5, 15]));

        var gpus = RemoteTelemetryCollector.ToHostGpus(sample, new Dictionary<string, double> { ["split"] = 16 }, definitions)!;

        Assert.Equal(4, gpus[0].ModelMemoryGiB!["split"]);
        Assert.Equal(12, gpus[1].ModelMemoryGiB!["split"]);
    }

    [Fact]
    public void APlacedGpuThatIsGone_KeepsTheOtherGpusShare()
    {
        var sample = Sample(MemoryModel.Discrete, gpus: [new GpuSample { Index = 0, Uuid = "GPU-a", MemoryUsedGiB = 9, MemoryTotalGiB = 16 }]);
        var definitions = Definitions(Model("tp", new ModelPlacement("pc", GpuUuids: ["GPU-a", "GPU-removed"])));

        var gpus = RemoteTelemetryCollector.ToHostGpus(sample, new Dictionary<string, double> { ["tp"] = 16 }, definitions)!;

        // Budget ÷ n with n = the placement's GPU count (design §5.1), not the GPUs still present.
        Assert.Equal(8, Assert.Single(gpus).ModelMemoryGiB!["tp"]);
    }

    [Fact]
    public async Task TheCollectorStoresPerGpuSamples_ForADiscreteNode_AndNoneForAUnifiedOne()
    {
        var time = new ManualTimeProvider();
        var registry = new FakeModelRegistry();
        registry.Add(new ModelDefinition
        {
            Id = "tp",
            ServedName = "TP",
            ModelPath = "p",
            MemoryBudgetGiB = 24,
            Placement = new ModelPlacement("pc", GpuUuids: ["GPU-a", "GPU-b"]),
        });
        var nodes = new FakeNodeRegistry();
        nodes.Records["dgx"] = new NodeRecord { Id = "dgx", Uid = "u1", Address = "https://127.0.0.1:9443", Approved = true };
        nodes.Records["pc"] = new NodeRecord { Id = "pc", Uid = "u2", Address = "https://127.0.0.1:9444", Approved = true };
        var now = time.GetUtcNow();
        var agents = new PageAgents
        {
            ["dgx"] = Page(Sample(MemoryModel.Unified, at: now, gpus: [new GpuSample { Index = 0, Uuid = "GPU-gb10", UtilizationPercent = 5 }])),
            ["pc"] = Page(Sample(MemoryModel.Discrete, at: now,
                gpus:
                [
                    new GpuSample { Index = 0, Uuid = "GPU-a", UtilizationPercent = 60, MemoryUsedGiB = 13, MemoryTotalGiB = 16 },
                    new GpuSample { Index = 1, Uuid = "GPU-b", UtilizationPercent = 50, MemoryUsedGiB = 12.5, MemoryTotalGiB = 16 },
                ],
                models: [new ModelMetricsScrape { ModelId = "tp", At = now, Text = "vllm:num_requests_running 0\n" }])),
        };
        var history = new InMemoryMetricsHistory(Options.Create(new MetricsOptions()), time);
        using var collector = new RemoteTelemetryCollector(nodes, agents, registry, history, Options.Create(new MetricsOptions()), time,
            NullLogger<RemoteTelemetryCollector>.Instance);

        await collector.CollectOnceAsync();

        Assert.Null(Assert.Single(history.GetHost("dgx", TimeSpan.FromHours(1))).Gpus);
        var pc = Assert.Single(history.GetHost("pc", TimeSpan.FromHours(1)));
        Assert.Equal(24, pc.ModelMemoryGiB!["tp"]); // the node-wide view is unchanged (budget, no GPU processes)
        Assert.NotNull(pc.Gpus);
        Assert.Equal(["GPU-a", "GPU-b"], pc.Gpus.Select(g => g.Uuid));
        Assert.Equal([60, 50], pc.Gpus.Select(g => g.UtilizationPercent));
        Assert.All(pc.Gpus, g => Assert.Equal(12, g.ModelMemoryGiB!["tp"]));
        Assert.Equal(60, pc.Gpu!.UtilizationPercent); // the single-GPU view stays the lowest index
    }

    private static NodeSample Sample(
        MemoryModel model,
        DateTimeOffset? at = null,
        IReadOnlyList<GpuSample>? gpus = null,
        IReadOnlyList<GpuMemorySnapshot>? memory = null,
        IReadOnlyList<ModelMetricsScrape>? models = null) => new()
        {
            Seq = 1,
            At = at ?? T0,
            Memory = new NodeMemorySnapshot { Model = model, HostTotalGiB = 64, HostAvailableGiB = 40, Gpus = memory ?? [] },
            Gpus = gpus ?? [],
            Models = models ?? [],
        };

    private static TelemetryPage Page(NodeSample sample) => new() { BootId = "boot-1", Samples = [sample] };

    private static ModelDefinition Model(string id, ModelPlacement placement) =>
        new() { Id = id, ServedName = id, ModelPath = "p", Placement = placement };

    /// <summary>A llama.cpp model on <paramref name="placement"/>, with its own per-GPU budgets when given.</summary>
    private static ModelDefinition Llama(string id, ModelPlacement placement, IReadOnlyList<double>? gpuBudgetsGiB) =>
        Model(id, placement) with
        {
            Runtime = RuntimeKind.LlamaCpp,
            LlamaCpp = new LlamaCppSettings { ModelFile = "m.gguf", GpuBudgetsGiB = gpuBudgetsGiB },
        };

    private static Dictionary<string, ModelDefinition> Definitions(params ModelDefinition[] models) =>
        models.ToDictionary(m => m.Id, StringComparer.Ordinal);

    /// <summary>Agents that answer one telemetry page each; every other route fails (the collector only pulls telemetry).</summary>
    private sealed class PageAgents : IAgentClientFactory
    {
        private readonly Dictionary<string, IAgentClient> _clients = new(StringComparer.Ordinal);

        public TelemetryPage this[string nodeId]
        {
            set => _clients[nodeId] = TelemetryOnlyClient.Create(value);
        }

        public IAgentClient GetClient(string nodeId) => _clients.TryGetValue(nodeId, out var c) ? c : throw new KeyNotFoundException(nodeId);

        public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

        public void RebuildHandlers(string nodeId) => throw new NotSupportedException();

        public void SetNegotiatedProtocol(string nodeId, int version) => throw new NotSupportedException();
    }

    /// <summary>An <see cref="IAgentClient"/> that only implements <c>GetTelemetryAsync</c> (a page once, then empty).</summary>
    public class TelemetryOnlyClient : DispatchProxy
    {
        private TelemetryPage? _page;

        public static IAgentClient Create(TelemetryPage page)
        {
            var client = DispatchProxy.Create<IAgentClient, TelemetryOnlyClient>();
            ((TelemetryOnlyClient)(object)client)._page = page;
            return client;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IAgentClient.GetTelemetryAsync))
            {
                throw new NotSupportedException(targetMethod?.Name);
            }

            var page = _page!;
            _page = page with { Samples = [] };
            return Task.FromResult(page);
        }
    }
}
