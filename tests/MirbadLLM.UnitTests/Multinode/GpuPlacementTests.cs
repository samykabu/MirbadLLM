using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Fencing;
using MirbadLLM.Agent.Operations;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>Shared fixtures of the MN-106 tests: two equal 16 GB cards, a smaller third card, a discrete node record.</summary>
internal static class GpuPlacementFixtures
{
    public const string NodeId = "pc";
    public const string NodeUid = "uid-pc-0001";
    public const string Image = "local/vllm:p2";

    /// <summary>nvidia-smi's 16303 MiB, as the inventory reports it (MiB ÷ 1024).</summary>
    public const double CardGiB = 16303 / 1024.0;

    public const string GpuA = "GPU-00000000-0000-0000-0000-00000000000a";
    public const string GpuB = "GPU-00000000-0000-0000-0000-00000000000b";
    public const string GpuSmall = "GPU-00000000-0000-0000-0000-000000005060";

    public static IReadOnlyList<GpuCapability> Gpus(params (int Index, string Uuid, double TotalGiB)[] gpus) =>
        [.. gpus.Select(g => new GpuCapability { Index = g.Index, Uuid = g.Uuid, Name = g.TotalGiB == CardGiB ? "RTX 5060 Ti" : "RTX 3060", MemoryTotalGiB = g.TotalGiB })];

    public static IReadOnlyList<GpuCapability> ThreeCards() => Gpus((0, GpuA, CardGiB), (1, GpuB, CardGiB), (2, GpuSmall, 12));

    public static NodeRecord Discrete(IReadOnlyList<string>? features = null, IReadOnlyList<GpuCapability>? gpus = null) => new()
    {
        Id = NodeId,
        Uid = NodeUid,
        Address = "https://127.0.0.1:9444",
        Approved = true,
        MemoryReserveGiB = 6,
        GpuReserveGiB = new Dictionary<string, double> { [GpuA] = 1.5, [GpuSmall] = 2 },
        LastCapabilities = new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "2.0.0",
            Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
            Arch = "amd64",
            Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
            Gpus = gpus ?? ThreeCards(),
            Features = features ?? [.. FakeAgentOptions.P1Features, AgentFeatures.GpuSelectionUuids, AgentFeatures.AdmissionDiscrete],
        },
    };

    public static ModelDefinition Placed(params string[] gpus) => new()
    {
        Id = "coder",
        ServedName = "Coder",
        ModelPath = "coder-14b",
        MemoryBudgetGiB = 24,
        HostMemoryBudgetGiB = 10,
        MaxModelLen = 32_768,
        Placement = new ModelPlacement(NodeId, gpus, [.. gpus.Select((_, i) => i)]),
    };
}

/// <summary>
/// MN-106 (design §5.1, D25, D73), master side: a placement on discrete GPUs is launched by UUID with the per-GPU
/// utilization denominator and <c>--tensor-parallel-size</c>, read back from the launch metadata, gated on the node's
/// <c>gpus.uuids</c> feature; unified (DGX) launches stay byte-identical to P1.
/// </summary>
public sealed class GpuPlacementTests
{
    private const double DgxTotalGiB = LiveDefinitions.TotalGiB;

    private static readonly NodeRecord P1Dgx = new()
    {
        Id = "dgx",
        Uid = "8f0c2a4e-dgx-node-uid",
        Address = "https://127.0.0.1:9443",
        Approved = true,
        LastCapabilities = new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "1.0.0",
            Os = new NodeOsInfo("linux", "DGX OS"),
            Arch = "arm64",
            Memory = new NodeMemoryInfo(MemoryModel.Unified, DgxTotalGiB),
            Gpus = [new GpuCapability { Index = 0, Uuid = "GPU-gb10", Name = "NVIDIA GB10", SharesHostMemory = true }],
            Features = FakeAgentOptions.P1Features,
        },
    };

    /// <summary>The DGX after its agent is upgraded: it reports every P2 flag.</summary>
    private static readonly NodeRecord P2Dgx = P1Dgx with
    {
        LastCapabilities = P1Dgx.LastCapabilities! with
        {
            Features =
            [
                .. FakeAgentOptions.P1Features, AgentFeatures.GpuSelectionUuids, AgentFeatures.LaunchEnv,
                AgentFeatures.AdmissionDiscrete, AgentFeatures.ImagesDigestPinned,
            ],
        },
    };

    /// <summary>The legacy hashes MN-029 recorded from the unchanged pre-P1 code (LegacyHashGoldenTests).</summary>
    public static TheoryData<string, string> LiveGolden => new()
    {
        { "qwen36-35b-a3b-nvfp4", "6b9064a9c15081fb" },
        { "nemotron-3-5", "a3ec54f3f162246f" },
        { "qwen3-embedding", "775edf47c2192c64" },
    };

    private static ModelDefinition Live(string id) =>
        new[] { LiveDefinitions.Qwen35B, LiveDefinitions.Nemotron, LiveDefinitions.Embedding }.Single(m => m.Id == id) with
        {
            Placement = new ModelPlacement("dgx"),
        };

    // ------------------------------------------------------------ unified: byte-identical to P1

    [Theory]
    [MemberData(nameof(LiveGolden))]
    public void UnifiedDgxLaunch_IsByteIdenticalToP1_EvenWhenItsAgentReportsEveryP2Feature(string id, string golden)
    {
        var model = Live(id);

        var p1 = LaunchSpecBuilder.Build(model, P1Dgx, LiveDefinitions.Image, DgxTotalGiB, tuning: false);
        var p2 = LaunchSpecBuilder.Build(model, P2Dgx, LiveDefinitions.Image, DgxTotalGiB, tuning: false);

        Assert.Equal(golden, p1.ConfigHash);
        Assert.Equal(golden, p2.ConfigHash);
        Assert.Equal(p1.Spec.Argv, p2.Spec.Argv);
        Assert.Equal(VllmArgsBuilder.Build(model, DgxTotalGiB), p2.Spec.Argv);
        Assert.DoesNotContain(VllmArgsNormalizer.TensorParallelFlag, p2.Spec.Argv);
        Assert.Equal(GpuSelectionMode.All, p2.Spec.Gpus.Mode);
        Assert.Null(p2.Spec.Gpus.Uuids);
        Assert.False(p2.Spec.Labels.ContainsKey(ContainerLabels.GpuTotalGiB));
        Assert.False(p2.Spec.Labels.ContainsKey(ContainerLabels.GpuUuids));
        Assert.Equal(JsonSerializer.Serialize(p1.Spec, AgentJson.Options), JsonSerializer.Serialize(p2.Spec, AgentJson.Options));
        Assert.Equal(p1.ExecutionFingerprint, p2.ExecutionFingerprint);
        Assert.Equal(
            JsonSerializer.Serialize(LaunchSpecBuilder.Admission(model, P1Dgx, force: false), AgentJson.Options),
            JsonSerializer.Serialize(LaunchSpecBuilder.Admission(model, P2Dgx, force: false), AgentJson.Options));
        Assert.Equal(golden, LaunchSpecBuilder.ConfigHash(model, P2Dgx, LiveDefinitions.Image, DgxTotalGiB));
    }

    [Fact]
    public void UnifiedDgxSpec_SerializesExactlyAsTheP1Wire()
    {
        var built = LaunchSpecBuilder.Build(Live("nemotron-3-5"), P2Dgx, LiveDefinitions.Image, DgxTotalGiB, tuning: false);
        var gpus = JsonNode.Parse(JsonSerializer.Serialize(built.Spec, AgentJson.Options))!["gpus"]!.AsObject();

        Assert.Equal("""{"mode":"all"}""", gpus.ToJsonString());
    }

    [Fact]
    public void TensorParallelInExtraArgs_StaysRawOnAUnifiedPlacement_SoItsCommandLineIsUnchanged()
    {
        var model = Live("nemotron-3-5") with { ExtraArgs = ["--tensor-parallel-size", "1", "--trust-remote-code"] };

        var normalized = VllmArgsNormalizer.Normalize(model);
        var built = LaunchSpecBuilder.Build(model, P2Dgx, LiveDefinitions.Image, DgxTotalGiB, tuning: false);

        Assert.Empty(normalized.Lifted);
        Assert.Equal(model.ExtraArgs, normalized.Definition.ExtraArgs);
        var withoutTp = LaunchSpecBuilder.Build(model with { ExtraArgs = [] }, P2Dgx, LiveDefinitions.Image, DgxTotalGiB, tuning: false);
        Assert.Equal([.. withoutTp.Spec.Argv, "--tensor-parallel-size", "1", "--trust-remote-code"], built.Spec.Argv); // raw, last
        Assert.Equal(ModelConfigHash.Compute(model, LiveDefinitions.Image, DgxTotalGiB), built.ConfigHash);
        Assert.Equal(built.Spec.Argv, VllmArgsParser.Canonicalize(built.Spec.Argv));
    }

    // ------------------------------------------------------------ discrete: UUIDs, denominator, TP

    [Fact]
    public void OneGpu_UsesItsOwnCapacityAsTheDenominator_SelectsItByUuid_AndEmitsNoTensorParallelSize()
    {
        var node = GpuPlacementFixtures.Discrete();
        var model = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuB) with { MemoryBudgetGiB = 12 };

        var built = LaunchSpecBuilder.Build(model, node, GpuPlacementFixtures.Image, totalGiB: 64, tuning: false);

        Assert.Equal(GpuSelectionMode.Uuids, built.Spec.Gpus.Mode);
        Assert.Equal([GpuPlacementFixtures.GpuB], built.Spec.Gpus.Uuids);
        Assert.Equal(Utilization(12 / GpuPlacementFixtures.CardGiB), Arg(built.Spec.Argv, "--gpu-memory-utilization"));
        Assert.Equal("0.7537", Arg(built.Spec.Argv, "--gpu-memory-utilization")); // 12 GiB of 15.92 GiB, not of the 64 GiB host
        Assert.DoesNotContain(VllmArgsNormalizer.TensorParallelFlag, built.Spec.Argv);
        Assert.Equal(GpuPlacementFixtures.CardGiB.ToString("R", CultureInfo.InvariantCulture), built.Spec.Labels[ContainerLabels.GpuTotalGiB]);
        Assert.False(built.Spec.Labels.ContainsKey(ContainerLabels.GpuUuids)); // the agent writes it from the selection
        Assert.Equal(ModelConfigHash.Compute(GpuPlacementFixtures.Image, built.Spec.Argv), built.ConfigHash);
        Assert.Equal(built.ConfigHash, LaunchSpecBuilder.ConfigHash(model, node, GpuPlacementFixtures.Image, totalGiB: 64));
        Assert.Equal(
            ExecutionFingerprint.Compute(built.Spec with { Labels = new Dictionary<string, string>() }, GpuPlacementFixtures.NodeUid, [GpuPlacementFixtures.GpuB], built.ConfigHash),
            built.ExecutionFingerprint);
    }

    [Fact]
    public void TwoEqualGpus_SplitTheBudget_AndEmitTensorParallelSizeRightAfterTheUtilization()
    {
        var node = GpuPlacementFixtures.Discrete();
        var model = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuB, GpuPlacementFixtures.GpuA);

        var built = LaunchSpecBuilder.Build(model, node, GpuPlacementFixtures.Image, totalGiB: 64, tuning: false);

        var argv = built.Spec.Argv.ToList();
        int utilization = argv.IndexOf("--gpu-memory-utilization");
        Assert.Equal(Utilization(24 / 2.0 / GpuPlacementFixtures.CardGiB), argv[utilization + 1]);
        Assert.Equal([VllmArgsNormalizer.TensorParallelFlag, "2"], argv[(utilization + 2)..(utilization + 4)]);
        Assert.Single(argv, a => a == VllmArgsNormalizer.TensorParallelFlag);
        Assert.Equal([GpuPlacementFixtures.GpuB, GpuPlacementFixtures.GpuA], built.Spec.Gpus.Uuids); // saved order = rank order
        Assert.Equal(built.Spec.Argv, VllmArgsParser.Canonicalize(built.Spec.Argv)); // a builder output is its own canonical form
    }

    [Fact]
    public void TheGpuSelection_ChangesTheFingerprint_AndTheGpuCount_ChangesTheLegacyHash()
    {
        var node = GpuPlacementFixtures.Discrete();
        var onA = LaunchSpecBuilder.Build(GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA), node, GpuPlacementFixtures.Image, 64, tuning: false);
        var onB = LaunchSpecBuilder.Build(GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuB), node, GpuPlacementFixtures.Image, 64, tuning: false);
        var onBoth = LaunchSpecBuilder.Build(GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA, GpuPlacementFixtures.GpuB), node, GpuPlacementFixtures.Image, 64, tuning: false);
        var swapped = LaunchSpecBuilder.Build(GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuB, GpuPlacementFixtures.GpuA), node, GpuPlacementFixtures.Image, 64, tuning: false);

        Assert.Equal(onA.ConfigHash, onB.ConfigHash); // a move between two equal GPUs: same argv (design §5.1 "two fingerprints")
        Assert.NotEqual(onA.ExecutionFingerprint, onB.ExecutionFingerprint);
        Assert.NotEqual(onA.ConfigHash, onBoth.ConfigHash); // design §6.3: GPU count → TP and utilization
        Assert.Equal(onBoth.ConfigHash, swapped.ConfigHash);
        Assert.NotEqual(onBoth.ExecutionFingerprint, swapped.ExecutionFingerprint); // rank order
    }

    [Fact]
    public void RenumberedCards_KeepTheirModel_TheSpecDependsOnUuidsOnly()
    {
        var model = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuB);
        var before = GpuPlacementFixtures.Discrete();
        var after = GpuPlacementFixtures.Discrete(gpus: GpuPlacementFixtures.Gpus(
            (0, GpuPlacementFixtures.GpuSmall, 12), (1, GpuPlacementFixtures.GpuB, GpuPlacementFixtures.CardGiB), (2, GpuPlacementFixtures.GpuA, GpuPlacementFixtures.CardGiB)));

        var a = LaunchSpecBuilder.Build(model, before, GpuPlacementFixtures.Image, 64, tuning: false);
        var b = LaunchSpecBuilder.Build(model, after, GpuPlacementFixtures.Image, 64, tuning: false);

        Assert.Equal(JsonSerializer.Serialize(a.Spec, AgentJson.Options), JsonSerializer.Serialize(b.Spec, AgentJson.Options));
        Assert.Equal(a.ExecutionFingerprint, b.ExecutionFingerprint);
        Assert.Equal([GpuPlacementFixtures.GpuB], b.Spec.Gpus.Uuids);
    }

    [Fact]
    public void ANodeWithoutTheGpusUuidsFeature_IsNeverSentAGpuSelection()
    {
        var node = GpuPlacementFixtures.Discrete(features: FakeAgentOptions.P1Features);
        var model = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA);

        var refused = Assert.Throws<NotSupportedException>(() => LaunchSpecBuilder.Build(model, node, GpuPlacementFixtures.Image, 64, tuning: false));
        Assert.Contains(AgentFeatures.GpuSelectionUuids, refused.Message, StringComparison.Ordinal);

        // The features of the call win over the node's last report (the monitor's fresher view).
        var built = LaunchSpecBuilder.Build(model, node, GpuPlacementFixtures.Image, 64, tuning: false, nodeFeatures: [AgentFeatures.GpuSelectionUuids]);
        Assert.Equal(GpuSelectionMode.Uuids, built.Spec.Gpus.Mode);
    }

    [Fact]
    public void PlacedGpusWithoutAUsableDenominator_AreRefusedOnTheMaster()
    {
        var node = GpuPlacementFixtures.Discrete();

        var unknown = Assert.Throws<ArgumentException>(() =>
            LaunchSpecBuilder.Build(GpuPlacementFixtures.Placed("GPU-00000000-0000-0000-0000-0000000000ff"), node, GpuPlacementFixtures.Image, 64, tuning: false));
        Assert.Contains("GPU-00000000-0000-0000-0000-0000000000ff", unknown.Message, StringComparison.Ordinal);
        // Shaped like the agent's own refusal (W8 review MN-128): the admin API answers 422 gpu_missing, not 500.
        var agent = Assert.IsType<AgentException>(unknown.InnerException);
        Assert.Equal((ErrorCodes.GpuMissing, 422), (agent.Code, agent.StatusCode));
        Assert.Equal(["GPU-00000000-0000-0000-0000-0000000000ff"], agent.Error.Details!.Value.Deserialize<GpuMissingDetails>(AgentJson.Options)!.Uuids);

        var mixed = Assert.Throws<ArgumentException>(() =>
            LaunchSpecBuilder.Build(GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA, GpuPlacementFixtures.GpuSmall), node, GpuPlacementFixtures.Image, 64, tuning: false));
        Assert.Contains("equal GPUs", mixed.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.SpecRejected, Assert.IsType<AgentException>(mixed.InnerException).Code);

        // One placed card the last report lacks: the others give the denominator and the agent decides (gpu_missing).
        var partly = LaunchSpecBuilder.Build(
            GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA, "GPU-00000000-0000-0000-0000-0000000000ff"), node, GpuPlacementFixtures.Image, 64, tuning: false);
        Assert.Equal(GpuPlacementFixtures.CardGiB.ToString("R", CultureInfo.InvariantCulture), partly.Spec.Labels[ContainerLabels.GpuTotalGiB]);
    }

    [Fact]
    public void TensorParallelInExtraArgs_IsOwnedByAGpuPlacement_AndEmittedOnce()
    {
        var model = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA, GpuPlacementFixtures.GpuB) with
        {
            ExtraArgs = ["--trust-remote-code", "-tp", "4", "--tensor_parallel_size=8"],
        };

        var normalized = VllmArgsNormalizer.Normalize(model);
        var built = LaunchSpecBuilder.Build(model, GpuPlacementFixtures.Discrete(), GpuPlacementFixtures.Image, 64, tuning: false);

        Assert.Equal(["--trust-remote-code"], normalized.Definition.ExtraArgs);
        Assert.Equal(["-tp", "--tensor_parallel_size"], normalized.Lifted.Select(l => l.Flag));
        Assert.All(normalized.Lifted, l => Assert.Equal(VllmArgsNormalizer.TensorParallelField, l.Field));
        Assert.Equal("2", Arg(built.Spec.Argv, VllmArgsNormalizer.TensorParallelFlag));
        Assert.Single(built.Spec.Argv, a => a.Contains("tensor", StringComparison.Ordinal) || a == "-tp");

        var invalid = VllmArgsNormalizer.Normalize(model with { ExtraArgs = ["--tensor-parallel-size", "two"] });
        Assert.Single(invalid.Problems);
        Assert.Equal(["--tensor-parallel-size", "two"], invalid.Definition.ExtraArgs);
    }

    // ------------------------------------------------------------ read-back

    [Theory]
    [InlineData(1, 12.0)]
    [InlineData(2, 24.0)]
    [InlineData(2, 27.3)]
    public void ReadBack_FromTheLaunchMetadata_RoundTripsBudgetGpusAndHash(int gpuCount, double budget)
    {
        var node = GpuPlacementFixtures.Discrete();
        string[] uuids = gpuCount == 1 ? [GpuPlacementFixtures.GpuB] : [GpuPlacementFixtures.GpuB, GpuPlacementFixtures.GpuA];
        var model = GpuPlacementFixtures.Placed(uuids) with { MemoryBudgetGiB = budget };
        var built = LaunchSpecBuilder.Build(model, node, GpuPlacementFixtures.Image, totalGiB: 64, tuning: false);

        // What the agent answers on /launch-config (current indices: the cards were renumbered since).
        var launch = LaunchConfigOf(built, [.. uuids.Select((u, i) => new GpuRef(7 + i, u))]);
        var read = VllmArgsParser.Parse(launch, GpuPlacementFixtures.Image, hostTotalGiB: 64, template: model);

        Assert.Equal(budget, read.MemoryBudgetGiB);
        Assert.Equal(uuids, read.Placement!.GpuUuids);
        Assert.Equal(uuids.Select((_, i) => 7 + i), read.Placement.GpuIndices);
        Assert.Equal(GpuPlacementFixtures.NodeId, read.Placement.NodeId);
        Assert.Null(read.Image);
        Assert.Empty(read.ExtraArgs);
        Assert.Equal(built.ConfigHash, ModelConfigHash.Compute(launch.Image!, launch.Argv));
        Assert.Equal(built.ConfigHash, LaunchSpecBuilder.ConfigHash(read, node, GpuPlacementFixtures.Image, 64));
        Assert.Equal(built.ExecutionFingerprint, ExecutionFingerprint.Compute(launch, RuntimeKind.Vllm, GpuPlacementFixtures.NodeUid, built.ConfigHash));
    }

    [Fact]
    public void ReadBack_FromTheGpuUuidsLabelAlone_UsesTheLabelledDenominatorNotTheHost()
    {
        var model = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA, GpuPlacementFixtures.GpuB);
        var built = LaunchSpecBuilder.Build(model, GpuPlacementFixtures.Discrete(), GpuPlacementFixtures.Image, 64, tuning: false);
        var launch = LaunchConfigOf(built, gpus: []) with
        {
            Labels = new Dictionary<string, string>(built.Spec.Labels) { [ContainerLabels.GpuUuids] = $"{GpuPlacementFixtures.GpuA},{GpuPlacementFixtures.GpuB}" },
        };

        var read = VllmArgsParser.Parse(launch, GpuPlacementFixtures.Image, hostTotalGiB: 64, template: model);

        Assert.Equal(24, read.MemoryBudgetGiB);
        Assert.Equal([GpuPlacementFixtures.GpuA, GpuPlacementFixtures.GpuB], read.Placement!.GpuUuids);
        Assert.Equal(model.Placement!.GpuIndices, read.Placement.GpuIndices); // same GPUs: the saved display indices
    }

    [Fact]
    public void ReadBack_OfAnInstanceOnOtherGpus_ShowsThoseGpus()
    {
        var saved = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA);
        var launched = LaunchSpecBuilder.Build(GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuB), GpuPlacementFixtures.Discrete(), GpuPlacementFixtures.Image, 64, tuning: false);

        var read = VllmArgsParser.Parse(LaunchConfigOf(launched, [new GpuRef(-1, GpuPlacementFixtures.GpuB)]), GpuPlacementFixtures.Image, 64, saved);

        Assert.Equal([GpuPlacementFixtures.GpuB], read.Placement!.GpuUuids);
        Assert.Null(read.Placement.GpuIndices); // index unknown now, and not the saved card
        Assert.False(read.Placement.SameLocation(saved.Placement));
    }

    [Fact]
    public void ReadBack_OfAUnifiedInstance_IsExactlyTheP1Parse()
    {
        var model = Live("qwen36-35b-a3b-nvfp4");
        var built = LaunchSpecBuilder.Build(model, P1Dgx, LiveDefinitions.Image, DgxTotalGiB, tuning: false);
        var launch = LaunchConfigOf(built, gpus: []);

        var viaConfig = VllmArgsParser.Parse(launch, LiveDefinitions.Image, DgxTotalGiB, model);
        var viaArgv = VllmArgsParser.Parse(launch.Argv, launch.Image!, LiveDefinitions.Image, DgxTotalGiB, model);

        Assert.Equal(JsonSerializer.Serialize(viaArgv), JsonSerializer.Serialize(viaConfig));
        Assert.Equal(model.Placement, viaConfig.Placement);
    }

    // ------------------------------------------------------------ admission

    [Fact]
    public void Admission_OfADiscretePlacement_CarriesTheHostBudget_AndThePlacedGpusReserves_OnlyToADiscreteAdmissionAgent()
    {
        var model = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA, GpuPlacementFixtures.GpuB);

        var discrete = LaunchSpecBuilder.Admission(model, GpuPlacementFixtures.Discrete(), force: false);
        var older = LaunchSpecBuilder.Admission(model, GpuPlacementFixtures.Discrete(features: [AgentFeatures.GpuSelectionUuids]), force: true);

        Assert.Equal(24, discrete.BudgetGiB);
        Assert.Equal(10, discrete.HostBudgetGiB);
        Assert.Equal(6, discrete.ReserveGiB); // the host-RAM reserve on a discrete node
        Assert.Equal(new Dictionary<string, double> { [GpuPlacementFixtures.GpuA] = 1.5 }, discrete.GpuReservesGiB); // GPU B has none
        Assert.Equal(1.5, discrete.ReserveForGpu(GpuPlacementFixtures.GpuA));
        Assert.Equal(0, discrete.ReserveForGpu(GpuPlacementFixtures.GpuB));
        Assert.Null(older.GpuReservesGiB);
        Assert.DoesNotContain("gpuReservesGiB", JsonSerializer.Serialize(older, AgentJson.Options), StringComparison.Ordinal);
        Assert.True(older.Force);
    }

    [Fact]
    public void Admission_WithAnExplicitSwapSpace_SendsTheFitPreviewsHostEstimate_ElseNoHostBudget()
    {
        // MN-124: the agent sees no runtime arguments, so it estimated 4 GiB swap per GPU while the fit preview read
        // --swap-space; the master now sends its estimate when the model sets one, and nothing otherwise (bodies unchanged).
        var placed = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA, GpuPlacementFixtures.GpuB) with { HostMemoryBudgetGiB = null };
        var swap = placed with { ExtraArgs = ["--swap-space", "8"] };

        Assert.Null(LaunchSpecBuilder.Admission(placed, GpuPlacementFixtures.Discrete(), force: false).HostBudgetGiB);
        var admission = LaunchSpecBuilder.Admission(swap, GpuPlacementFixtures.Discrete(), force: false);
        Assert.Equal(PlacementFit.EstimateHostGiB(swap, 2), admission.HostBudgetGiB);
        Assert.Equal((8 * 2) + 2, admission.HostBudgetGiB);
        Assert.Equal(12, LaunchSpecBuilder.Admission(swap with { HostMemoryBudgetGiB = 12 }, GpuPlacementFixtures.Discrete(), force: false).HostBudgetGiB);

        // The unified DGX never gets one (P1 admission body).
        Assert.Null(LaunchSpecBuilder.Admission(swap with { Placement = new ModelPlacement("dgx") }, P2Dgx, force: false).HostBudgetGiB);
    }

    // ------------------------------------------------------------ helpers

    internal static string Utilization(double fraction) => fraction.ToString("F4", CultureInfo.InvariantCulture);

    private static string Arg(IReadOnlyList<string> argv, string flag) => argv[argv.ToList().IndexOf(flag) + 1];

    /// <summary>The <c>/launch-config</c> an agent reports for an instance created from <paramref name="built"/>.</summary>
    private static LaunchConfig LaunchConfigOf(BuiltLaunchSpec built, IReadOnlyList<GpuRef> gpus) => new()
    {
        Kind = LaunchDelivery.Container,
        Image = built.Image,
        Argv = built.Spec.Argv,
        Mounts = built.Spec.Mounts,
        Gpus = gpus,
        Labels = new Dictionary<string, string>(built.Spec.Labels)
        {
            [ContainerLabels.Model] = "coder",
            [ContainerLabels.Node] = GpuPlacementFixtures.NodeId,
        },
        ExecutionFingerprint = built.ExecutionFingerprint,
    };
}

/// <summary>
/// MN-106 against <see cref="FakeAgent"/> in discrete mode over the real agent client: the master's
/// <see cref="RemoteModelRuntime"/> launches a placement by UUID, reads it back through <c>/launch-config</c>, keeps it on
/// its card when the cards are renumbered, and maps a removed card's <c>gpu_missing</c> to today's exception.
/// </summary>
public sealed class DiscreteRemoteLaunchTests
{
    private static readonly FakeDiscreteGpu CardA = new(GpuPlacementFixtures.GpuA);
    private static readonly FakeDiscreteGpu CardB = new(GpuPlacementFixtures.GpuB);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ALaunchByUuid_ReadsBackItsBudgetAndGpus_AndIsNotRestartPending(int gpuCount)
    {
        string[] uuids = gpuCount == 1 ? [GpuPlacementFixtures.GpuB] : [GpuPlacementFixtures.GpuB, GpuPlacementFixtures.GpuA];
        double budget = 12 * gpuCount;
        var model = GpuPlacementFixtures.Placed(uuids) with { MemoryBudgetGiB = budget };
        await using var env = await DiscreteEnv.StartAsync(model);

        await env.Runtime.StartAsync(model.Id, ct: Ct);
        await env.WaitReadyAsync(model.Id);

        var launch = await env.LaunchConfigAsync(model.Id);
        Assert.Equal(uuids, launch.Gpus.Select(g => g.Uuid));
        Assert.Equal(gpuCount == 2, launch.Argv.Contains(VllmArgsNormalizer.TensorParallelFlag));
        Assert.Equal(16.ToString("R", CultureInfo.InvariantCulture), launch.Labels[ContainerLabels.GpuTotalGiB]);

        var read = await env.Runtime.GetLaunchedDefinitionAsync(model.Id, Ct);
        Assert.NotNull(read);
        Assert.Equal(budget, read.MemoryBudgetGiB); // utilization × 16 GiB × n, not × the 64 GiB host
        Assert.Equal(uuids, read.Placement!.GpuUuids);
        Assert.Equal(await env.Runtime.ComputeConfigHashAsync(model, Ct), await env.Runtime.GetLaunchedConfigHashAsync(model.Id, Ct));
        Assert.False((await env.Runtime.GetStatusAsync(model.Id, Ct)).RestartPending);
    }

    [Fact]
    public async Task RenumberedCards_TheModelRestartsOnItsSavedCard_AndAnotherGpuIsRestartPending()
    {
        var model = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuB) with { MemoryBudgetGiB = 12 };
        await using var env = await DiscreteEnv.StartAsync(model);
        await env.Runtime.StartAsync(model.Id, ct: Ct);
        await env.WaitReadyAsync(model.Id);
        Assert.Equal(1, Assert.Single((await env.LaunchConfigAsync(model.Id)).Gpus).Index);

        env.Agent.RenumberGpus(GpuPlacementFixtures.GpuB, GpuPlacementFixtures.GpuA); // a driver update: B is index 0 now
        await env.Runtime.RestartAsync(model.Id, ct: Ct);
        await env.WaitReadyAsync(model.Id);

        var gpu = Assert.Single((await env.LaunchConfigAsync(model.Id)).Gpus);
        Assert.Equal(GpuPlacementFixtures.GpuB, gpu.Uuid);
        Assert.Equal(0, gpu.Index);
        Assert.False((await env.Runtime.GetStatusAsync(model.Id, Ct)).RestartPending);

        // The saved placement moves to the other card: same argv (equal cards), other fingerprint → restart pending.
        env.Models.Add(model with { Placement = new ModelPlacement(GpuPlacementFixtures.NodeId, [GpuPlacementFixtures.GpuA]) });
        Assert.True((await env.Runtime.GetStatusAsync(model.Id, Ct)).RestartPending);
    }

    [Fact]
    public async Task ARemovedCard_IsRefusedWithGpuMissing_BeforeAnythingStarts()
    {
        var model = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA) with { MemoryBudgetGiB = 12 };
        await using var env = await DiscreteEnv.StartAsync(model);
        env.Agent.RemoveGpu(GpuPlacementFixtures.GpuA);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => env.Runtime.StartAsync(model.Id, ct: Ct));

        Assert.Contains(GpuPlacementFixtures.GpuA, refused.Message, StringComparison.Ordinal);
        Assert.Null(await env.Factory.GetClient(GpuPlacementFixtures.NodeId).GetRuntimeAsync(model.Id, Ct));
    }

    /// <summary>
    /// W8 review (MN-128): MN-105 and MN-106 end to end. On a node reporting <c>admission.discrete</c> two models start on
    /// their own GPUs side by side, a third that does not fit on a taken GPU is refused naming that GPU, and the master's
    /// exception carries the agent's per-GPU numbers for the "Start anyway?" dialog.
    /// </summary>
    [Fact]
    public async Task PerGpuAdmission_StartsOnSeparateGpus_AndARefusalNamesTheGpuWithItsNumbers()
    {
        var onA = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA) with { Id = "on-a", ServedName = "on-a", MemoryBudgetGiB = 12 };
        var onB = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuB) with { Id = "on-b", ServedName = "on-b", MemoryBudgetGiB = 12 };
        var alsoA = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA) with { Id = "also-a", ServedName = "also-a", MemoryBudgetGiB = 8 };
        await using var env = await DiscreteEnv.StartAsync(
            [.. FakeAgentOptions.P1Features, AgentFeatures.GpuSelectionUuids, AgentFeatures.AdmissionDiscrete], onA, onB, alsoA);
        await env.Agent.SetStartsHeldAsync(true);

        // Two GPUs start concurrently (one start per GPU, not per node).
        await env.Runtime.StartAsync("on-a", ct: Ct);
        await env.Runtime.StartAsync("on-b", ct: Ct);
        await env.Agent.SetStartsHeldAsync(false);
        await env.WaitReadyAsync("on-a");
        await env.WaitReadyAsync("on-b");

        var refused = await Assert.ThrowsAsync<ModelMemoryAdmissionException>(() => env.Runtime.StartAsync("also-a", ct: Ct));

        Assert.Contains("on node pc (GPU 0)", refused.Message, StringComparison.Ordinal);
        var details = AgentErrorMapper.AdmissionDetails(refused);
        Assert.NotNull(details);
        Assert.Equal(GpuPlacementFixtures.GpuA, details.Gpu);
        Assert.Equal(8, details.NeedGiB);
        Assert.True(details.FreeGiB < 8);
        Assert.Null(await env.Factory.GetClient(GpuPlacementFixtures.NodeId).GetRuntimeAsync("also-a", Ct));
    }

    /// <summary>A discrete <see cref="FakeAgent"/> (two 16 GiB cards, <c>gpus.uuids</c>) under a <see cref="RemoteModelRuntime"/>.</summary>
    private sealed class DiscreteEnv : IAsyncDisposable
    {
        private readonly CertificateAuthority _ca;
        private readonly IssuedCredential _master;
        private readonly TrustAnchorSet _trust;
        private readonly string _dataDir;

        private DiscreteEnv(
            CertificateAuthority ca, IssuedCredential master, TrustAnchorSet trust, string dataDir, FakeModelRegistry models, FakeAgent agent,
            JsonNodeRegistry nodes, AgentClientFactory factory, JsonAgentOperationReferenceStore store, AgentBackedStatusCache cache,
            IOptions<MirbadLLMOptions> options)
        {
            _ca = ca;
            _master = master;
            _trust = trust;
            _dataDir = dataDir;
            Models = models;
            Agent = agent;
            Nodes = nodes;
            Factory = factory;
            Store = store;
            Cache = cache;
            Runtime = new RemoteModelRuntime(models, nodes, cache, factory, new AgentOperationRunner(factory, store, nodes, TimeProvider.System), options,
                TimeProvider.System, runtimeOptions: new RemoteModelRuntimeOptions { PollInterval = TimeSpan.FromMilliseconds(20) });
        }

        public FakeModelRegistry Models { get; }

        public FakeAgent Agent { get; }

        public JsonNodeRegistry Nodes { get; }

        public AgentClientFactory Factory { get; }

        public JsonAgentOperationReferenceStore Store { get; }

        public AgentBackedStatusCache Cache { get; }

        public RemoteModelRuntime Runtime { get; }

        public static Task<DiscreteEnv> StartAsync(ModelDefinition model) =>
            StartAsync([.. FakeAgentOptions.P1Features, AgentFeatures.GpuSelectionUuids], model);

        public static async Task<DiscreteEnv> StartAsync(IReadOnlyList<string> features, params ModelDefinition[] models)
        {
            var ca = CertificateAuthority.Create();
            var agent = await FakeAgent.StartAsync(new FakeAgentOptions
            {
                Ca = ca,
                NodeId = GpuPlacementFixtures.NodeId,
                TotalMemoryGiB = 64,
                AvailableMemoryGiB = 60,
                DiscreteGpus = [CardA, CardB],
                Features = features,
            }, Ct);
            string dataDir = Path.Combine(Path.GetTempPath(), "mn106-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataDir);
            var options = Options.Create(new MirbadLLMOptions { DataPath = dataDir, VllmImage = GpuPlacementFixtures.Image });
            var nodes = new JsonNodeRegistry(options, TimeProvider.System);
            var record = new NodeRecord
            {
                Id = GpuPlacementFixtures.NodeId,
                Uid = agent.NodeUid,
                Address = agent.BaseAddress.AbsoluteUri,
                Approved = true,
                CertSpkiSha256 = agent.Target.AgentSpkiPin,
            };
            await nodes.UpsertAsync(record, Ct);
            var master = ca.IssueMasterClientCertificate();
            var trust = TrustAnchorSet.Create([ca.Certificate]);
            var factory = new AgentClientFactory(nodes, new TestMasterIdentity(), master, trust, TimeProvider.System);
            await factory.RefreshAsync(Ct);

            // The node's last capability report, as the node monitor stores it (GPUs with memory, features).
            var capabilities = await factory.GetClient(GpuPlacementFixtures.NodeId).GetCapabilitiesAsync(Ct);
            await nodes.UpsertAsync(record with { LastCapabilities = capabilities }, Ct);

            var store = new JsonAgentOperationReferenceStore(options, TimeProvider.System);
            var cache = new AgentBackedStatusCache(factory, GpuPlacementFixtures.NodeId, agent.NodeUid);
            await cache.RefreshNodeAsync(GpuPlacementFixtures.NodeId, Ct);
            var registry = new FakeModelRegistry();
            foreach (var model in models)
            {
                registry.Add(model);
            }

            return new DiscreteEnv(ca, master, trust, dataDir, registry, agent, nodes, factory, store, cache, options);
        }

        public async Task<LaunchConfig> LaunchConfigAsync(string modelId) =>
            await Factory.GetClient(GpuPlacementFixtures.NodeId).GetLaunchConfigAsync(modelId, Ct) ?? throw new InvalidOperationException($"No instance of {modelId}.");

        public async Task WaitReadyAsync(string modelId)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while ((await Factory.GetClient(GpuPlacementFixtures.NodeId).GetRuntimeAsync(modelId, Ct))?.State != ModelState.Ready)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException($"{modelId} did not become Ready.");
                }

                await Task.Delay(10, Ct);
            }

            await Cache.RefreshNodeAsync(GpuPlacementFixtures.NodeId, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Factory.DisposeAsync();
            await Agent.DisposeAsync();
            Store.Dispose();
            Nodes.Dispose();
            _master.Dispose();
            _trust.Dispose();
            _ca.Dispose();
            try
            {
                Directory.Delete(_dataDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }
}

/// <summary>
/// MN-106, agent side: the container driver selects GPUs by UUID (<c>DeviceIDs</c>), refuses a UUID that is not in the
/// inventory with 422 <c>gpu_missing</c>, writes the read-back labels and reports current indices; the launch handler
/// refreshes the inventory before it checks; the capability contributor reports <c>gpus.uuids</c>.
/// </summary>
public sealed class AgentGpuSelectionTests
{
    private const string NodeId = "pc";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LaunchSpec Spec(GpuSelection gpus, IReadOnlyDictionary<string, string>? labels = null) => new()
    {
        Kind = LaunchDelivery.Container,
        Image = GpuPlacementFixtures.Image,
        Argv = ["--model", "/models/coder-14b", "--gpu-memory-utilization", "0.7537"],
        ContainerPort = 8000,
        Mounts =
        [
            new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true),
            new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false),
        ],
        Gpus = gpus,
        IpcHost = true,
        Labels = labels ?? new Dictionary<string, string> { [ContainerLabels.ServedName] = "Coder" },
    };

    private static GpuSelection ByUuid(params string[] uuids) => new() { Mode = GpuSelectionMode.Uuids, Uuids = uuids };

    private static (ContainerRuntimeDriver Driver, ContainerRuntimeDriverTests.FakeDockerEngine Docker) Driver(IGpuInventory? inventory)
    {
        var docker = new ContainerRuntimeDriverTests.FakeDockerEngine();
        var driver = new ContainerRuntimeDriver(
            docker.Client,
            new HttpClient(new FakeHttpMessageHandler { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) }),
            new ContainerRuntimeDriverTests.FakePortAllocator(),
            Options.Create(new ContainerRuntimeOptions { NodeId = NodeId }),
            NullLogger<ContainerRuntimeDriver>.Instance,
            gpuInventory: inventory);
        return (driver, docker);
    }

    private static Task<RuntimeLaunchResult> LaunchAsync(ContainerRuntimeDriver driver, LaunchSpec spec, string modelId = "coder") =>
        driver.LaunchAsync(
            new RuntimeLaunch { ModelId = modelId, OperationId = "op-1", Mode = LaunchMode.Replace, Spec = spec, HostPort = 8101 },
            new ContainerRuntimeDriverTests.RecordingStageSink());

    [Fact]
    public async Task UuidSelection_RequestsExactlyThoseDevices_LabelsThem_AndReportsCurrentIndices()
    {
        var inventory = new FakeGpuInventory(GpuPlacementFixtures.ThreeCards());
        var (driver, docker) = Driver(inventory);

        await LaunchAsync(driver, Spec(ByUuid(GpuPlacementFixtures.GpuB, GpuPlacementFixtures.GpuA)));

        var body = docker.CreateBodies.Single();
        var device = body["HostConfig"]!["DeviceRequests"]!.AsArray().Single()!;
        Assert.Equal("nvidia", device["Driver"]!.GetValue<string>());
        Assert.Equal(0, device["Count"]!.GetValue<int>()); // Docker refuses Count together with DeviceIDs
        Assert.Equal([GpuPlacementFixtures.GpuB, GpuPlacementFixtures.GpuA], device["DeviceIDs"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal("""[["gpu"]]""", device["Capabilities"]!.ToJsonString());
        var labels = body["Labels"]!.AsObject();
        Assert.Equal($"{GpuPlacementFixtures.GpuB},{GpuPlacementFixtures.GpuA}", labels[ContainerLabels.GpuUuids]!.GetValue<string>());
        Assert.Equal(GpuPlacementFixtures.CardGiB.ToString("R", CultureInfo.InvariantCulture), labels[ContainerLabels.GpuTotalGiB]!.GetValue<string>());

        var config = await driver.GetLaunchConfigAsync("coder", Ct);
        Assert.Equal([new GpuRef(1, GpuPlacementFixtures.GpuB), new GpuRef(0, GpuPlacementFixtures.GpuA)], config!.Gpus);
        Assert.Equal(config.Gpus, (await driver.GetAsync("coder", Ct))!.Gpus);

        // The cards are renumbered (and A is pulled): the instance still names its cards, at their new indices.
        inventory.Set(GpuPlacementFixtures.Gpus((0, GpuPlacementFixtures.GpuB, GpuPlacementFixtures.CardGiB), (1, GpuPlacementFixtures.GpuSmall, 12)));
        Assert.Equal([new GpuRef(0, GpuPlacementFixtures.GpuB), new GpuRef(-1, GpuPlacementFixtures.GpuA)], (await driver.GetLaunchConfigAsync("coder", Ct))!.Gpus);
    }

    [Fact]
    public async Task TheMastersDenominatorLabel_IsKept()
    {
        var (driver, docker) = Driver(new FakeGpuInventory(GpuPlacementFixtures.ThreeCards()));
        var labels = new Dictionary<string, string> { [ContainerLabels.ServedName] = "Coder", [ContainerLabels.GpuTotalGiB] = "15.5" };

        await LaunchAsync(driver, Spec(ByUuid(GpuPlacementFixtures.GpuA), labels));

        Assert.Equal("15.5", docker.CreateBodies.Single()["Labels"]![ContainerLabels.GpuTotalGiB]!.GetValue<string>());
    }

    [Fact]
    public async Task AllGpus_CreatesExactlyTheP1DeviceRequest_AndNoGpuLabels()
    {
        var (driver, docker) = Driver(new FakeGpuInventory(GpuPlacementFixtures.ThreeCards()));

        await LaunchAsync(driver, Spec(new GpuSelection { Mode = GpuSelectionMode.All }));

        var body = docker.CreateBodies.Single();
        Assert.Equal("""[{"Driver":"nvidia","Count":-1,"Capabilities":[["gpu"]]}]""", body["HostConfig"]!["DeviceRequests"]!.ToJsonString());
        Assert.False(body["Labels"]!.AsObject().ContainsKey(ContainerLabels.GpuUuids));
        Assert.False(body["Labels"]!.AsObject().ContainsKey(ContainerLabels.GpuTotalGiB));
        Assert.Empty((await driver.GetLaunchConfigAsync("coder", Ct))!.Gpus);
    }

    [Fact]
    public void AMissingGpu_IsGpuMissingListingOnlyTheMissingUuids()
    {
        var (driver, docker) = Driver(new FakeGpuInventory(GpuPlacementFixtures.ThreeCards()));
        const string Gone = "GPU-00000000-0000-0000-0000-0000000000ff";

        var refused = Assert.Throws<AgentException>(() => driver.CheckSpec("coder", Spec(ByUuid(GpuPlacementFixtures.GpuA, Gone))));

        Assert.Equal(422, refused.StatusCode);
        Assert.Equal(ErrorCodes.GpuMissing, refused.Code);
        Assert.Equal([Gone], refused.Error.ReadDetails<GpuMissingDetails>()!.Uuids);
        Assert.Empty(docker.Calls);
    }

    [Fact]
    public async Task ADriverWithoutAnInventory_RefusesUuidSelection_AsAP1AgentDoes()
    {
        var (driver, docker) = Driver(inventory: null);

        var check = Assert.Throws<AgentException>(() => driver.CheckSpec("coder", Spec(ByUuid(GpuPlacementFixtures.GpuA))));
        var launch = await Assert.ThrowsAsync<AgentException>(() => LaunchAsync(driver, Spec(ByUuid(GpuPlacementFixtures.GpuA))));

        Assert.Equal(ErrorCodes.SpecRejected, check.Code);
        Assert.Equal(ErrorCodes.SpecRejected, launch.Code);
        Assert.False(driver.SelectsGpusByUuid);
        Assert.Empty(docker.Calls);
    }

    public static TheoryData<string> BadSelections => new()
    {
        "empty", "duplicate", "notAUuid", "noneMode", "allWithUuids", "reservedLabel",
    };

    [Theory]
    [MemberData(nameof(BadSelections))]
    public void AMalformedSelection_IsSpecRejected(string bad)
    {
        var (driver, _) = Driver(new FakeGpuInventory(GpuPlacementFixtures.ThreeCards()));
        var spec = bad switch
        {
            "empty" => Spec(ByUuid()),
            "duplicate" => Spec(ByUuid(GpuPlacementFixtures.GpuA, GpuPlacementFixtures.GpuA)),
            "notAUuid" => Spec(ByUuid("0")),
            "noneMode" => Spec(new GpuSelection { Mode = GpuSelectionMode.None }),
            "allWithUuids" => Spec(new GpuSelection { Mode = GpuSelectionMode.All, Uuids = [GpuPlacementFixtures.GpuA] }),
            "reservedLabel" => Spec(ByUuid(GpuPlacementFixtures.GpuA), new Dictionary<string, string> { [ContainerLabels.GpuUuids] = GpuPlacementFixtures.GpuB }),
            _ => throw new ArgumentOutOfRangeException(nameof(bad)),
        };

        var refused = Assert.Throws<AgentException>(() => driver.CheckSpec("coder", spec));

        Assert.Equal(ErrorCodes.SpecRejected, refused.Code);
        Assert.Equal(422, refused.StatusCode);
    }

    [Fact]
    public async Task TheLaunchHandler_RereadsTheInventoryBeforeChecking_SoACardPulledSinceTheLastReadIsMissing()
    {
        // The last read still lists card A; nvidia-smi now no longer does.
        var inventory = new FakeGpuInventory(GpuPlacementFixtures.ThreeCards())
        {
            OnRefresh = GpuPlacementFixtures.Gpus((0, GpuPlacementFixtures.GpuB, GpuPlacementFixtures.CardGiB)),
        };
        await using var agent = await AgentUnderTest.StartAsync(inventory);

        var missing = await agent.LaunchAsync("op-1", ByUuid(GpuPlacementFixtures.GpuA));
        var present = await agent.LaunchAsync("op-2", ByUuid(GpuPlacementFixtures.GpuB));

        Assert.Equal(ErrorCodes.GpuMissing, missing.Error?.Code);
        Assert.Equal(422, missing.StatusCode);
        Assert.Null(agent.Log.Find("op-1")); // nothing recorded or reserved for the refused launch
        Assert.Equal(202, present.StatusCode);
        Assert.Equal(2, inventory.Refreshes);
        await agent.Runtimes.WhenIdleAsync();
        Assert.Equal(
            [GpuPlacementFixtures.GpuB],
            agent.Docker.CreateBodies.Single()["HostConfig"]!["DeviceRequests"]![0]!["DeviceIDs"]!.AsArray().Select(d => d!.GetValue<string>()));
    }

    [Fact]
    public async Task TheLaunchHandler_DoesNotReadTheInventory_ForAnAllGpusLaunch()
    {
        var inventory = new FakeGpuInventory(GpuPlacementFixtures.ThreeCards());
        await using var agent = await AgentUnderTest.StartAsync(inventory);

        var result = await agent.LaunchAsync("op-1", new GpuSelection { Mode = GpuSelectionMode.All });

        Assert.Equal(202, result.StatusCode);
        Assert.Equal(0, inventory.Refreshes);
    }

    [Fact]
    public async Task TheContributor_ReportsGpusUuids_OnlyForAContainerDriverWithAnInventory()
    {
        var current = new NodeCapabilities
        {
            Protocol = new ProtocolRange(1, 1),
            AgentVersion = "2.0.0",
            Os = new NodeOsInfo("linux", "Ubuntu"),
            Arch = "amd64",
            Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
            Features = [AgentFeatures.RuntimeVllmContainer],
        };

        var withInventory = await new GpuSelectionContributor([Driver(new FakeGpuInventory([])).Driver]).ContributeAsync(current, Ct);
        var without = await new GpuSelectionContributor([Driver(null).Driver]).ContributeAsync(current, Ct);
        var noDriver = await new GpuSelectionContributor([]).ContributeAsync(current, Ct);
        var again = await new GpuSelectionContributor([Driver(new FakeGpuInventory([])).Driver]).ContributeAsync(withInventory, Ct);

        Assert.Equal([AgentFeatures.RuntimeVllmContainer, AgentFeatures.GpuSelectionUuids], withInventory.Features);
        Assert.Same(current, without);
        Assert.Same(current, noDriver);
        Assert.Same(withInventory, again); // never added twice
    }

    /// <summary>An <see cref="IGpuInventory"/> with a settable last read and what the next refresh finds.</summary>
    private sealed class FakeGpuInventory(IReadOnlyList<GpuCapability> last) : IGpuInventory
    {
        private IReadOnlyList<GpuCapability>? _last = last;

        public IReadOnlyList<GpuCapability>? OnRefresh { get; init; }

        public int Refreshes { get; private set; }

        public IReadOnlyList<GpuCapability>? Last => _last;

        public void Set(IReadOnlyList<GpuCapability> gpus) => _last = gpus;

        public Task<IReadOnlyList<GpuCapability>> RefreshAsync(CancellationToken ct = default)
        {
            Refreshes++;
            _last = OnRefresh ?? _last;
            return Task.FromResult(_last ?? []);
        }
    }

    /// <summary>The agent's launch path (gate, reservations, container driver over a fake Docker) without a listener.</summary>
    private sealed class AgentUnderTest : IAsyncDisposable
    {
        private static readonly MasterTriple Master = new("master-a", 1, "boot-a");
        private readonly string _stateDir;
        private readonly FencingState _fencing;
        private readonly ReservationManager _reservations;
        private readonly HttpClient _upstream;

        private AgentUnderTest(IGpuInventory inventory)
        {
            _stateDir = Path.Combine(Path.GetTempPath(), "mn106-agent-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_stateDir);
            var time = TimeProvider.System;
            Log = new OperationLog(new OperationLogOptions { StateDirectory = _stateDir, BootId = "boot-1" }, time);
            _fencing = new FencingState(_stateDir, time);
            var events = new EventBus("boot-1", time: time);
            Docker = new ContainerRuntimeDriverTests.FakeDockerEngine();
            IRuntimeDriver? driver = null;
            _reservations = new ReservationManager(
                new AdmissionOptions { NodeId = NodeId, MemoryModel = MemoryModel.Discrete },
                new FakeMemoryProbe(64, 60),
                new Instances(() => driver!),
                persistence: null,
                events,
                time);
            _upstream = new HttpClient(new FakeHttpMessageHandler { Handler = _ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) });
            driver = new ContainerRuntimeDriver(
                Docker.Client, _upstream, _reservations, Options.Create(new ContainerRuntimeOptions { NodeId = NodeId }),
                NullLogger<ContainerRuntimeDriver>.Instance, time, inventory);
            var gate = new MutationGate(Log, _fencing, events, time);
            Runtimes = new RuntimeOperations(
                [driver], gate, _reservations, new RuntimeStateBoard(events), events, time,
                new AgentRuntimeOptions { WatchInterval = TimeSpan.FromHours(1) }, gpuInventory: inventory);
        }

        public OperationLog Log { get; }

        public ContainerRuntimeDriverTests.FakeDockerEngine Docker { get; }

        public RuntimeOperations Runtimes { get; }

        public static Task<AgentUnderTest> StartAsync(IGpuInventory inventory) => Task.FromResult(new AgentUnderTest(inventory));

        public Task<MutationResult> LaunchAsync(string operationId, GpuSelection gpus)
        {
            var now = DateTimeOffset.UtcNow;
            var request = new LaunchRequest
            {
                OperationId = operationId,
                IssuedAt = now,
                ExpiresAt = now + OperationRetention.EnvelopeLifetime,
                Mode = LaunchMode.EnsureStarted,
                Spec = Spec(gpus),
                Admission = new AdmissionRequest { BudgetGiB = 12, ReserveGiB = 2, Force = true },
            };
            return Runtimes.LaunchAsync("coder-" + operationId, request, JsonSerializer.SerializeToElement(request, AgentJson.Options), Master, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Runtimes.WhenIdleAsync();
            Runtimes.Dispose();
            _reservations.Dispose();
            _fencing.Dispose();
            Log.Dispose();
            _upstream.Dispose();
            try
            {
                Directory.Delete(_stateDir, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        private sealed class Instances(Func<IRuntimeDriver> driver) : IRuntimeInstanceSource
        {
            public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) => driver().ListAsync(ct);
        }
    }
}

/// <summary>
/// MN-106 / D91: hostd names the GPU of every compute process (<c>--query-compute-apps=gpu_uuid,…</c>), the field is
/// omitted when null on the wire, and the collector shows a model's measured memory per GPU (even split without it).
/// </summary>
public sealed class GpuProcessPerGpuTests
{
    private const string Container = "960769d2c75febb6efba9d52da72b559ec2d1165c8bfb0410b3052ec6b63ff91";

    [Fact]
    public void Hostd_AsksForTheGpuUuid_AndParsesItFirst_NameMayContainCommas()
    {
        Assert.Equal(["--query-compute-apps=gpu_uuid,pid,process_name,used_memory", "--format=csv,noheader,nounits"], MirbadLLM.HostAgent.GpuProcessInventory.Arguments());

        var parsed = MirbadLLM.HostAgent.GpuProcessInventory.Parse(
            $"{GpuPlacementFixtures.GpuA}, 3414913, VLLM::EngineCore, 7000\n" +
            $"{GpuPlacementFixtures.GpuB}, 3414913, VLLM::EngineCore, 6500\n" +
            $"{GpuPlacementFixtures.GpuB}, 4000, python, worker, 1, 2\n" +
            "[N/A], 4001, x, 5\n" +
            "3062106, VLLM::EngineCore, 3646\n"); // a P1-shaped line (no UUID) is not this query's output

        Assert.Equal(4, parsed.Count);
        Assert.Equal(new MirbadLLM.HostAgent.GpuProcessInfo(3414913, "VLLM::EngineCore", 7000, null, null, GpuUuid: GpuPlacementFixtures.GpuA), parsed[0]);
        Assert.Equal(GpuPlacementFixtures.GpuB, parsed[1].GpuUuid);
        Assert.Equal(new MirbadLLM.HostAgent.GpuProcessInfo(4000, "python, worker, 1", 2, null, null, GpuUuid: GpuPlacementFixtures.GpuB), parsed[2]);
        // A row whose gpu_uuid is not a UUID keeps its memory with no GPU (W8 review MN-128: the DGX's measured memory must
        // not depend on the new column); the master splits it evenly.
        Assert.Equal(new MirbadLLM.HostAgent.GpuProcessInfo(4001, "x", 5, null, null, GpuUuid: null), parsed[3]);
    }

    [Fact]
    public void GpuProcess_OmitsANullGpuUuid_AndReadsOne()
    {
        var p1 = new GpuProcess(1, "x", 10, null, "a.service");

        Assert.Equal("""{"pid":1,"name":"x","usedMiB":10,"containerId":null,"unit":"a.service","displayName":null}""", JsonSerializer.Serialize(p1, AgentJson.Options));
        var read = JsonSerializer.Deserialize<GpuProcess>("""{"pid":2,"name":"y","usedMiB":5,"containerId":null,"unit":null,"gpuUuid":"GPU-x"}""", AgentJson.Options)!;
        Assert.Equal("GPU-x", read.GpuUuid);
    }

    [Fact]
    public void MeasuredPerGpu_SumsAModelsRowsPerGpu_AndFallsBackToTheEvenSplitWithoutUuids()
    {
        var sample = new NodeSample
        {
            Seq = 1,
            At = DateTimeOffset.UnixEpoch,
            Memory = new NodeMemorySnapshot { Model = MemoryModel.Discrete, HostTotalGiB = 64, HostAvailableGiB = 40 },
            Gpus =
            [
                new GpuSample { Index = 0, Uuid = GpuPlacementFixtures.GpuA, MemoryUsedGiB = 9, MemoryTotalGiB = 16 },
                new GpuSample { Index = 1, Uuid = GpuPlacementFixtures.GpuB, MemoryUsedGiB = 8, MemoryTotalGiB = 16 },
            ],
        };
        var definitions = new Dictionary<string, ModelDefinition>(StringComparer.Ordinal)
        {
            ["tp"] = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA, GpuPlacementFixtures.GpuB) with { Id = "tp" },
        };
        var running = new Dictionary<string, ModelMemoryAttribution.RunningModel>(StringComparer.Ordinal)
        {
            ["tp"] = new(Container[..12], 24),
        };
        var memory = new Dictionary<string, double> { ["tp"] = 15 };
        GpuProcess Row(string? gpu, long mib) => new(3414913, "VLLM::EngineCore", mib, Container, null, GpuUuid: gpu);

        var measured = RemoteTelemetryCollector.ToHostGpus(sample, memory, definitions,
            [Row(GpuPlacementFixtures.GpuA, 6 * 1024), Row(GpuPlacementFixtures.GpuA, 2 * 1024), Row(GpuPlacementFixtures.GpuB, 7 * 1024),
             new GpuProcess(99, "other", 512, null, "comfyui.service", GpuUuid: GpuPlacementFixtures.GpuB)], running)!;
        var older = RemoteTelemetryCollector.ToHostGpus(sample, memory, definitions, [Row(GpuPlacementFixtures.GpuA, 8 * 1024), Row(null, 7 * 1024)], running)!;
        var withoutProcesses = RemoteTelemetryCollector.ToHostGpus(sample, memory, definitions)!;

        Assert.Equal([8, 7], measured.Select(g => g.ModelMemoryGiB!["tp"]));
        Assert.Equal([7.5, 7.5], older.Select(g => g.ModelMemoryGiB!["tp"])); // one row without a GPU: even split
        Assert.Equal([7.5, 7.5], withoutProcesses.Select(g => g.ModelMemoryGiB!["tp"]));
    }

    [Fact]
    public void APlacedGpuWithoutARow_ShowsZero_ForAMeasuredModel()
    {
        var sample = new NodeSample
        {
            Seq = 1,
            At = DateTimeOffset.UnixEpoch,
            Memory = new NodeMemorySnapshot { Model = MemoryModel.Discrete, HostTotalGiB = 64, HostAvailableGiB = 40 },
            Gpus =
            [
                new GpuSample { Index = 0, Uuid = GpuPlacementFixtures.GpuA },
                new GpuSample { Index = 1, Uuid = GpuPlacementFixtures.GpuB },
            ],
        };
        var definitions = new Dictionary<string, ModelDefinition>(StringComparer.Ordinal)
        {
            ["tp"] = GpuPlacementFixtures.Placed(GpuPlacementFixtures.GpuA, GpuPlacementFixtures.GpuB) with { Id = "tp" },
        };
        var running = new Dictionary<string, ModelMemoryAttribution.RunningModel>(StringComparer.Ordinal) { ["tp"] = new(Container, 24) };

        var gpus = RemoteTelemetryCollector.ToHostGpus(sample, new Dictionary<string, double> { ["tp"] = 3 }, definitions,
            [new GpuProcess(1, "VLLM::EngineCore", 3 * 1024, Container, null, GpuUuid: GpuPlacementFixtures.GpuA)], running)!;

        Assert.Equal([3, 0], gpus.Select(g => g.ModelMemoryGiB!["tp"]));
    }
}
