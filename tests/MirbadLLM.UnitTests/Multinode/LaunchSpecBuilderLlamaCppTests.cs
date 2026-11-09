using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Orchestration;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// The P3 fixtures the MN-210 tests build from: the owner's PC as the frozen <c>NodeCapabilities.windows.json</c> reports
/// it (RTX 5070 11.94 GiB at index 0, RTX 5060 Ti 15.93 GiB at index 1, <c>b11476</c> verified default) and the three
/// llama.cpp definitions of <c>models.v2.llamacpp.json</c> (OpenViking's embedder on the 5060 Ti, a two-GPU chat split,
/// the planner).
/// </summary>
internal static class LlamaCppFixtures
{
    public const string NodeId = "win-1";
    public const string NodeUid = "uid-win-0001";
    public const string Rtx5070 = "GPU-0a4f2d61-8b3c-4e7a-b1d9-2c5e8f0a3b17";
    public const string Rtx5060Ti = "GPU-7c1e9a52-3f60-4d2b-9a8e-5b0f2c6d1e44";

    private static readonly JsonSerializerOptions Registry = new() { WriteIndented = true };

    public static NodeCapabilities Capabilities() =>
        JsonSerializer.Deserialize<NodeCapabilities>(File.ReadAllText(Path.Combine(P3Fixtures(), "NodeCapabilities.windows.json")), AgentJson.Options)!;

    /// <summary>The PC's node record; <paramref name="features"/> replaces the reported features, <paramref name="versions"/> the llama.cpp versions.</summary>
    public static NodeRecord Node(IReadOnlyList<string>? features = null, IReadOnlyList<RuntimeVersion>? versions = null, IReadOnlyDictionary<string, double>? gpuReserves = null)
    {
        var caps = Capabilities();
        if (features is not null)
        {
            caps = caps with { Features = features };
        }

        if (versions is not null)
        {
            caps = caps with { Runtimes = [caps.Runtimes[0] with { Versions = versions }] };
        }

        return new NodeRecord
        {
            Id = NodeId,
            Uid = NodeUid,
            Address = "https://192.0.2.50:9443",
            Approved = true,
            MemoryReserveGiB = 8,
            GpuReserveGiB = gpuReserves ?? new Dictionary<string, double> { [Rtx5060Ti] = 2.5 },
            LastCapabilities = caps,
        };
    }

    /// <summary>A model of <c>models.v2.llamacpp.json</c>: 0 = the OpenViking embedder, 1 = the two-GPU chat split, 2 = the planner.</summary>
    public static ModelDefinition Model(int index)
    {
        var document = JsonSerializer.Deserialize<ModelRegistryDocument>(File.ReadAllText(Path.Combine(P3Fixtures(), "models.v2.llamacpp.json")), Registry)!;
        return document.Models[index];
    }

    /// <summary>OpenViking's <c>Qwen3-Embedding-4B</c> Q8_0 as it moves to the PC (§A.5.1): fixture model 0.</summary>
    public static ModelDefinition Embedder() => Model(0);

    /// <summary>A chat GGUF on the 5060 Ti with the built-in ChatML template and reasoning off.</summary>
    public static ModelDefinition Chat() => Model(2) with
    {
        Placement = new ModelPlacement(NodeId, [Rtx5060Ti], [1]),
        LlamaCpp = Model(2).LlamaCpp! with { ExecutableVersion = null },
    };

    /// <summary>The fixture model 1: <c>coder-14b</c> split 7.5 + 10.5 GiB over both cards.</summary>
    public static ModelDefinition Split() => Model(1);

    public static string P3Fixtures([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "Contracts", "Fixtures", "P3");
}

/// <summary>
/// MN-210: <see cref="LaunchSpecBuilder"/> builds <c>kind: process, runtime: llamacpp</c> specs (design-p3p4 §A.1.2–§A.1.6)
/// for nodes reporting <c>runtime.llamacpp.process</c>, with a concrete executable version in the spec and the
/// fingerprint, GPUs by UUID, no mounts, no reserved env, and <c>gpuBudgetsGiB</c> only for <c>admission.gpu-budgets</c>;
/// every vLLM spec stays exactly as it was.
/// </summary>
public sealed class LaunchSpecBuilderLlamaCppTests
{
    // ---------------------------------------------------------------- the spec

    [Fact]
    public void Build_Embedder_IsAProcessSpecWithTheFrozenShape()
    {
        var built = LaunchSpecBuilder.Build(LlamaCppFixtures.Embedder(), LlamaCppFixtures.Node(), "ignored-image", totalGiB: 127, tuning: false);
        var spec = built.Spec;

        Assert.Equal(LaunchDelivery.Process, spec.Kind);
        Assert.Equal(RuntimeKind.LlamaCpp, spec.Runtime);
        Assert.Equal(ExecutableNames.LlamaServer, spec.Executable);
        Assert.Equal("b11476", spec.ExecutableVersion);
        Assert.Null(spec.Image);
        Assert.Null(spec.ImageDigest);
        Assert.Empty(spec.Mounts);
        Assert.Empty(spec.Env);
        Assert.Empty(spec.SecretEnvNames);
        Assert.False(spec.IpcHost);
        Assert.Equal("/health", spec.HealthPath);
        Assert.Equal(GpuSelectionMode.Uuids, spec.Gpus.Mode);
        Assert.Equal([LlamaCppFixtures.Rtx5060Ti], spec.Gpus.Uuids);
        Assert.Equal(LlamaCppArgsBuilder.Build(LlamaCppFixtures.Embedder(), 1), spec.Argv);

        // No legacy config hash for llama.cpp (§A.1.6): the label is absent and the request field is null.
        Assert.Equal(string.Empty, built.ConfigHash);
        Assert.Null(built.LegacyConfigHash);
        Assert.Equal(string.Empty, built.Image);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [ContainerLabels.ServedName] = "qwen3-embedding-4b-win",
                [ContainerLabels.ExecutionFingerprint] = built.ExecutionFingerprint,
                [ContainerLabels.BudgetGiB] = "8.9",
            },
            spec.Labels);
        Assert.Equal(string.Empty, LaunchSpecBuilder.ConfigHash(LlamaCppFixtures.Embedder(), LlamaCppFixtures.Node(), "img", 127));
    }

    [Fact]
    public void Build_Embedder_MatchesTheFrozenLaunchFixture()
    {
        // LaunchRequest.process.json (MN-202/MN-207) has the same model without cache types: the builder's argv for it is
        // exactly the fixture's, and the wire shape carries the version and no image.
        var fixture = JsonSerializer.Deserialize<LaunchRequest>(
            File.ReadAllText(Path.Combine(LlamaCppFixtures.P3Fixtures(), "LaunchRequest.process.json")), AgentJson.Options)!;
        var model = LlamaCppFixtures.Embedder() with
        {
            LlamaCpp = LlamaCppFixtures.Embedder().LlamaCpp! with { CacheTypeK = null, CacheTypeV = null },
        };

        var built = LaunchSpecBuilder.Build(model, LlamaCppFixtures.Node(), "img", 127, tuning: false);

        Assert.Equal(fixture.Spec.Argv, built.Spec.Argv);
        Assert.Equal(fixture.Spec.Executable, built.Spec.Executable);
        Assert.Equal(fixture.Spec.ExecutableVersion, built.Spec.ExecutableVersion);
        Assert.Equal(fixture.Spec.Gpus.Uuids, built.Spec.Gpus.Uuids);
        Assert.Equal(fixture.Spec.Mounts, built.Spec.Mounts);
        Assert.Equal(fixture.Spec.Env, built.Spec.Env);
        var json = JsonSerializer.SerializeToNode(built.Spec, AgentJson.Options)!.AsObject();
        Assert.Equal("process", json["kind"]!.GetValue<string>());
        Assert.Equal("llamacpp", json["runtime"]!.GetValue<string>());
        Assert.Equal("b11476", json["executableVersion"]!.GetValue<string>());
        Assert.Null(json["image"]);
    }

    [Fact]
    public void Build_TuningCandidate_CarriesTheTuningLabel()
    {
        var built = LaunchSpecBuilder.Build(LlamaCppFixtures.Embedder(), LlamaCppFixtures.Node(), "img", 127, tuning: true);
        Assert.Equal("1", built.Spec.Labels[ContainerLabels.Tuning]);
    }

    [Fact]
    public void Build_TwoGpuSplit_SelectsBothUuidsInPlacementOrder()
    {
        var built = LaunchSpecBuilder.Build(LlamaCppFixtures.Split(), LlamaCppFixtures.Node(), "img", 127, tuning: false);

        Assert.Equal([LlamaCppFixtures.Rtx5070, LlamaCppFixtures.Rtx5060Ti], built.Spec.Gpus.Uuids);
        Assert.Equal("b11476", built.Spec.ExecutableVersion); // the model's pin
        int split = built.Spec.Argv.ToList().IndexOf("--tensor-split");
        Assert.Equal("0.417,0.583", built.Spec.Argv[split + 1]); // 7.5 / 18 and 10.5 / 18
        Assert.Equal("CUDA0,CUDA1", built.Spec.Argv[built.Spec.Argv.ToList().IndexOf("--device") + 1]);
    }

    [Fact]
    public void Build_TwoGpuSplitWithoutBudgets_IsProportionalToCapacityMinusReserve()
    {
        var model = LlamaCppFixtures.Split() with { LlamaCpp = LlamaCppFixtures.Split().LlamaCpp! with { GpuBudgetsGiB = null } };

        var built = LaunchSpecBuilder.Build(model, LlamaCppFixtures.Node(), "img", 127, tuning: false);

        // 11.94 - 0 and 15.93 - 2.5 = 13.43 of 25.37 → 0.4706 and 0.5294 (§A.1.5 default).
        Assert.Equal("0.471,0.529", built.Spec.Argv[built.Spec.Argv.ToList().IndexOf("--tensor-split") + 1]);
        var weights = LaunchSpecBuilder.TensorSplitWeights(model, LlamaCppFixtures.Node())!;
        Assert.Equal(2, weights.Count);
        Assert.Equal(11.94, weights[0], 9);
        Assert.Equal(13.43, weights[1], 9);
    }

    [Fact]
    public void Build_SplitOntoAGpuTheNodeNoLongerReports_IsRefusedAsGpuMissing()
    {
        var model = LlamaCppFixtures.Split() with
        {
            LlamaCpp = LlamaCppFixtures.Split().LlamaCpp! with { GpuBudgetsGiB = null },
            Placement = new ModelPlacement(LlamaCppFixtures.NodeId, [LlamaCppFixtures.Rtx5070, "GPU-gone"], [0, 2]),
        };

        var refused = Assert.Throws<ArgumentException>(() => LaunchSpecBuilder.Build(model, LlamaCppFixtures.Node(), "img", 127, tuning: false));

        Assert.Contains("GPU-gone", refused.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.GpuMissing, Assert.IsType<AgentException>(refused.InnerException).Error.Code);
    }

    [Fact]
    public void Build_BudgetsThatDoNotMatchThePlacement_AreRefused()
    {
        var wrongCount = LlamaCppFixtures.Split() with { LlamaCpp = LlamaCppFixtures.Split().LlamaCpp! with { GpuBudgetsGiB = [18] } };
        var wrongSum = LlamaCppFixtures.Split() with { LlamaCpp = LlamaCppFixtures.Split().LlamaCpp! with { GpuBudgetsGiB = [7.5, 9] } };

        Assert.Contains("1 per-GPU budget", Assert.Throws<ArgumentException>(() =>
            LaunchSpecBuilder.Build(wrongCount, LlamaCppFixtures.Node(), "img", 127, tuning: false)).Message, StringComparison.Ordinal);
        Assert.Contains("sum to 16.5", Assert.Throws<ArgumentException>(() =>
            LaunchSpecBuilder.Build(wrongSum, LlamaCppFixtures.Node(), "img", 127, tuning: false)).Message, StringComparison.Ordinal);
        Assert.Equal(
            [PlacementProblemCodes.GpuBudgetsMismatch],
            LlamaCppArgsBuilder.GpuBudgetProblems(wrongSum, 2).Select(p => p.Code));
        Assert.Empty(LlamaCppArgsBuilder.GpuBudgetProblems(LlamaCppFixtures.Split(), 2));
    }

    // ---------------------------------------------------------------- feature gates and refusals

    [Fact]
    public void Build_NodeWithoutTheProcessRuntime_IsRefusedNamingTheFeature()
    {
        var dgxLike = LlamaCppFixtures.Node(features: [AgentFeatures.RuntimeVllmContainer, AgentFeatures.GpuSelectionUuids, AgentFeatures.LaunchEnv]);

        var refused = Assert.Throws<NotSupportedException>(() =>
            LaunchSpecBuilder.Build(LlamaCppFixtures.Embedder(), dgxLike, "img", 127, tuning: false));

        Assert.Contains(AgentFeatures.RuntimeLlamaCppProcess, refused.Message, StringComparison.Ordinal);
        Assert.Contains("'win-1'", refused.Message, StringComparison.Ordinal);

        // The live feature list wins over the stored report, as for the other gates.
        Assert.Throws<NotSupportedException>(() =>
            LaunchSpecBuilder.Build(LlamaCppFixtures.Embedder(), LlamaCppFixtures.Node(), "img", 127, tuning: false, nodeFeatures: []));
        Assert.Equal(LaunchDelivery.Process, LaunchSpecBuilder.Build(
            LlamaCppFixtures.Embedder(), dgxLike, "img", 127, tuning: false, nodeFeatures: [AgentFeatures.RuntimeLlamaCppProcess]).Spec.Kind);
    }

    [Fact]
    public void Build_ContainerDelivery_IsNotSupportedYet()
    {
        var container = LlamaCppFixtures.Embedder() with
        {
            LlamaCpp = LlamaCppFixtures.Embedder().LlamaCpp! with { Delivery = LaunchDelivery.Container },
        };

        var refused = Assert.Throws<NotSupportedException>(() => LaunchSpecBuilder.Build(container, LlamaCppFixtures.Node(), "img", 127, tuning: false));
        Assert.Contains("container", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_MlxStaysNotSupported()
    {
        var mlx = LlamaCppFixtures.Embedder() with { Runtime = RuntimeKind.Mlx, LlamaCpp = null };
        Assert.Throws<NotSupportedException>(() => LaunchSpecBuilder.Build(mlx, LlamaCppFixtures.Node(), "img", 127, tuning: false));
    }

    [Fact]
    public void Build_RefusedExtraArgs_AreRefusedBeforeAnythingIsSent()
    {
        var model = LlamaCppFixtures.Embedder() with
        {
            LlamaCpp = LlamaCppFixtures.Embedder().LlamaCpp! with { ExtraArgs = ["--temp", "0.2", "--port=9000", "--lora", "x.gguf"] },
        };

        var refused = Assert.Throws<ArgumentException>(() => LaunchSpecBuilder.Build(model, LlamaCppFixtures.Node(), "img", 127, tuning: false));

        Assert.Contains("--port=9000, --lora", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LLAMA_API_KEY")]
    [InlineData("cuda_visible_devices")] // Windows names are case-insensitive
    [InlineData("PATH")]
    public void Build_ReservedEnvironmentNames_AreRefused(string name)
    {
        var environment = new LaunchEnvironment(new Dictionary<string, string> { [name] = "x" }, []);

        var refused = Assert.Throws<ArgumentException>(() =>
            LaunchSpecBuilder.Build(LlamaCppFixtures.Embedder(), LlamaCppFixtures.Node(), "img", 127, tuning: false, environment));

        Assert.Equal(ErrorCodes.SpecRejected, Assert.IsType<AgentException>(refused.InnerException).Error.Code);
        Assert.Contains(name, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_OtherEnvironment_GoesOnlyToNodesWithLaunchEnv_AndIsFingerprinted()
    {
        var environment = new LaunchEnvironment(new Dictionary<string, string> { ["GGML_CUDA_FORCE_MMQ"] = "1" }, []);
        var withoutEnv = LlamaCppFixtures.Node(features: [AgentFeatures.RuntimeLlamaCppProcess]);

        Assert.Throws<NotSupportedException>(() =>
            LaunchSpecBuilder.Build(LlamaCppFixtures.Embedder(), withoutEnv, "img", 127, tuning: false, environment));
        var built = LaunchSpecBuilder.Build(LlamaCppFixtures.Embedder(), LlamaCppFixtures.Node(), "img", 127, tuning: false, environment);
        var plain = LaunchSpecBuilder.Build(LlamaCppFixtures.Embedder(), LlamaCppFixtures.Node(), "img", 127, tuning: false);

        Assert.Equal("1", built.Spec.Env["GGML_CUDA_FORCE_MMQ"]);
        Assert.NotEqual(plain.ExecutionFingerprint, built.ExecutionFingerprint);
    }

    // ---------------------------------------------------------------- executable version

    [Fact]
    public void ExecutableVersion_IsThePinElseTheVerifiedNodeDefault()
    {
        var node = LlamaCppFixtures.Node(versions: [new RuntimeVersion("b11390", "13.4", Verified: true), new RuntimeVersion("b11476", "13.4", Verified: true, Default: true)]);
        var pinned = LlamaCppFixtures.Embedder() with { LlamaCpp = LlamaCppFixtures.Embedder().LlamaCpp! with { ExecutableVersion = "b11390" } };

        Assert.Equal("b11476", LaunchSpecBuilder.ResolveExecutableVersion(LlamaCppFixtures.Embedder(), node));
        Assert.Equal("b11390", LaunchSpecBuilder.ResolveExecutableVersion(pinned, node));
        Assert.Equal("b11390", LaunchSpecBuilder.Build(pinned, node, "img", 127, tuning: false).Spec.ExecutableVersion);
    }

    [Theory]
    [InlineData(false, true)]  // the default exists but fails verification
    [InlineData(true, false)]  // verified, but nothing is the default
    public void ExecutableVersion_WithoutAVerifiedDefault_IsRefusedNamingTheNode(bool verified, bool isDefault)
    {
        var node = LlamaCppFixtures.Node(versions: [new RuntimeVersion("b11476", "13.4", verified, isDefault)]);

        var refused = Assert.Throws<NotSupportedException>(() =>
            LaunchSpecBuilder.Build(LlamaCppFixtures.Embedder(), node, "img", 127, tuning: false));

        Assert.Contains("node 'win-1'", refused.Message, StringComparison.Ordinal);
        Assert.Contains("b11476", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutableVersion_NoneInstalled_IsRefusedNamingTheNode()
    {
        var node = LlamaCppFixtures.Node(versions: []);
        var refused = Assert.Throws<NotSupportedException>(() => LaunchSpecBuilder.ResolveExecutableVersion(LlamaCppFixtures.Embedder(), node));
        Assert.Contains("node 'win-1' reports no verified default version (none installed)", refused.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- execution fingerprint

    [Fact]
    public void Fingerprint_CoversTheExecutableVersionAndTheArgv()
    {
        var node = LlamaCppFixtures.Node(versions: [new RuntimeVersion("b11390", "13.4", true), new RuntimeVersion("b11476", "13.4", true, Default: true)]);
        var model = LlamaCppFixtures.Embedder();
        var built = LaunchSpecBuilder.Build(model, node, "img", 127, tuning: false);

        Assert.Equal(
            ExecutionFingerprint.Compute(built.Spec with { Labels = new Dictionary<string, string>() }, LlamaCppFixtures.NodeUid, [LlamaCppFixtures.Rtx5060Ti], null, "b11476"),
            built.ExecutionFingerprint);

        string otherVersion = LaunchSpecBuilder.Build(model with { LlamaCpp = model.LlamaCpp! with { ExecutableVersion = "b11390" } }, node, "img", 127, false).ExecutionFingerprint;
        string otherContext = LaunchSpecBuilder.Build(model with { LlamaCpp = model.LlamaCpp! with { ContextSize = 4096 } }, node, "img", 127, false).ExecutionFingerprint;
        string otherExtra = LaunchSpecBuilder.Build(model with { LlamaCpp = model.LlamaCpp! with { ExtraArgs = ["--temp", "0"] } }, node, "img", 127, false).ExecutionFingerprint;
        string otherGpu = LaunchSpecBuilder.Build(model with { Placement = new ModelPlacement(LlamaCppFixtures.NodeId, [LlamaCppFixtures.Rtx5070], [0]) }, node, "img", 127, false).ExecutionFingerprint;

        Assert.Equal(5, new[] { built.ExecutionFingerprint, otherVersion, otherContext, otherExtra, otherGpu }.Distinct().Count());

        // Pinning the version the node uses by default launches the same process: same fingerprint.
        Assert.Equal(built.ExecutionFingerprint,
            LaunchSpecBuilder.Build(model with { LlamaCpp = model.LlamaCpp! with { ExecutableVersion = "b11476" } }, node, "img", 127, false).ExecutionFingerprint);
    }

    [Fact]
    public void Fingerprint_OfTheReadBack_EqualsTheLaunch()
    {
        // The agent's launch-config keeps argv tokens and reports the resolved version (D117): the master's recomputation
        // from it equals the fingerprint it launched with.
        var built = LaunchSpecBuilder.Build(LlamaCppFixtures.Split(), LlamaCppFixtures.Node(), "img", 127, tuning: false);
        var config = new LaunchConfig
        {
            Kind = LaunchDelivery.Process,
            Executable = built.Spec.Executable,
            ExecutableVersion = built.Spec.ExecutableVersion,
            Argv = built.Spec.Argv,
            Env = new Dictionary<string, string?>(),
            Mounts = [],
            Gpus = [new GpuRef(0, LlamaCppFixtures.Rtx5070), new GpuRef(1, LlamaCppFixtures.Rtx5060Ti)],
            Labels = built.Spec.Labels,
            ExecutionFingerprint = built.ExecutionFingerprint,
        };

        Assert.Equal(built.ExecutionFingerprint,
            ExecutionFingerprint.Compute(config, RuntimeKind.LlamaCpp, LlamaCppFixtures.NodeUid, legacyConfigHash: null, config.ExecutableVersion));
    }

    // ---------------------------------------------------------------- admission

    [Fact]
    public void Admission_SendsPerGpuBudgetsByUuid_OnlyToNodesReportingTheFeature()
    {
        var model = LlamaCppFixtures.Split();

        var withFlag = LaunchSpecBuilder.Admission(model, LlamaCppFixtures.Node(), force: false);
        var withoutFlag = LaunchSpecBuilder.Admission(model, LlamaCppFixtures.Node(features: [AgentFeatures.RuntimeLlamaCppProcess, AgentFeatures.AdmissionDiscrete]), force: false);
        var liveWithout = LaunchSpecBuilder.Admission(model, LlamaCppFixtures.Node(), force: false, nodeFeatures: [AgentFeatures.AdmissionDiscrete]);

        Assert.Equal(new Dictionary<string, double> { [LlamaCppFixtures.Rtx5070] = 7.5, [LlamaCppFixtures.Rtx5060Ti] = 10.5 }, withFlag.GpuBudgetsGiB);
        Assert.Equal(7.5, withFlag.BudgetForGpu(LlamaCppFixtures.Rtx5070, 2));
        Assert.Equal(18, withFlag.BudgetGiB);
        Assert.Null(withoutFlag.GpuBudgetsGiB);
        Assert.Equal(9, withoutFlag.BudgetForGpu(LlamaCppFixtures.Rtx5070, 2)); // P2's even split
        Assert.Null(liveWithout.GpuBudgetsGiB);
        Assert.False(JsonSerializer.SerializeToNode(withoutFlag, AgentJson.Options)!.AsObject().ContainsKey("gpuBudgetsGiB"));
    }

    [Fact]
    public void Admission_WithoutBudgets_SendsNone_AndKeepsTheP2Fields()
    {
        var admission = LaunchSpecBuilder.Admission(LlamaCppFixtures.Embedder(), LlamaCppFixtures.Node(), force: true);

        Assert.Null(admission.GpuBudgetsGiB);
        Assert.Equal(8.9, admission.BudgetGiB);
        Assert.Equal(2, admission.HostBudgetGiB);
        Assert.Equal(8, admission.ReserveGiB);
        Assert.Equal(new Dictionary<string, double> { [LlamaCppFixtures.Rtx5060Ti] = 2.5 }, admission.GpuReservesGiB);
        Assert.True(admission.Force);
    }

    [Fact]
    public void Admission_MismatchedBudgets_AreRefused()
    {
        var model = LlamaCppFixtures.Split() with { LlamaCpp = LlamaCppFixtures.Split().LlamaCpp! with { GpuBudgetsGiB = [18] } };
        Assert.Throws<ArgumentException>(() => LaunchSpecBuilder.Admission(model, LlamaCppFixtures.Node(), force: false));
    }

    // ---------------------------------------------------------------- vLLM unchanged

    [Fact]
    public void Vllm_SpecAndAdmission_AreUnchangedOnANodeReportingEveryP3Feature()
    {
        // The P3 flags must not leak into vLLM launches: same spec, hashes and admission body as on a P2 node.
        string[] p2 = [AgentFeatures.RuntimeVllmContainer, AgentFeatures.GpuSelectionUuids, AgentFeatures.LaunchEnv, AgentFeatures.AdmissionDiscrete];
        string[] p3 = [.. p2, .. AgentFeatures.P3Reported];
        var vllm = LiveDefinitions.Embedding with
        {
            MemoryBudgetGiB = 8,
            Placement = new ModelPlacement(LlamaCppFixtures.NodeId, [LlamaCppFixtures.Rtx5060Ti], [1]),
            LlamaCpp = null,
        };
        var node = LlamaCppFixtures.Node();

        var before = LaunchSpecBuilder.Build(vllm, node, "local/vllm:p2", 127, tuning: false, nodeFeatures: p2);
        var after = LaunchSpecBuilder.Build(vllm, node, "local/vllm:p2", 127, tuning: false, nodeFeatures: p3);

        Assert.Equal(JsonSerializer.Serialize(before.Spec, AgentJson.Options), JsonSerializer.Serialize(after.Spec, AgentJson.Options));
        Assert.Equal((before.ConfigHash, before.ExecutionFingerprint), (after.ConfigHash, after.ExecutionFingerprint));
        Assert.NotEqual(string.Empty, after.ConfigHash);
        Assert.Equal(after.ConfigHash, after.LegacyConfigHash);
        Assert.Equal(LaunchDelivery.Container, after.Spec.Kind);
        Assert.Null(after.Spec.ExecutableVersion);
        Assert.Equal(
            JsonSerializer.Serialize(LaunchSpecBuilder.Admission(vllm, node, false, p2), AgentJson.Options),
            JsonSerializer.Serialize(LaunchSpecBuilder.Admission(vllm, node, false, p3), AgentJson.Options));

        // A stray LlamaCpp block with budgets on a vLLM definition sends nothing new either.
        var stray = vllm with { LlamaCpp = new LlamaCppSettings { ModelFile = "x/x.gguf", GpuBudgetsGiB = [36] } };
        Assert.Null(LaunchSpecBuilder.Admission(stray, node, false, p3).GpuBudgetsGiB);
    }

    [Fact]
    public void LlamaCppContainer_UsesNodeImageDigest_AndTranslatesProcessTokens()
    {
        var node = ContainerNode() with
        {
            DefaultLlamaCppImage = "ghcr.io/example/llama:1",
            DefaultLlamaCppImageDigest = Digest,
        };
        var model = ContainerModel(RuntimeKind.LlamaCpp) with
        {
            LlamaCpp = new LlamaCppSettings { ModelFile = "repo/model.gguf", Delivery = LaunchDelivery.Container },
        };

        var built = LaunchSpecBuilder.Build(model, node, "unused", 120, false,
            nodeFeatures: [AgentFeatures.RuntimeLlamaCppContainer]);

        Assert.Equal(RuntimeKind.LlamaCpp, built.Spec.Runtime);
        Assert.Equal("ghcr.io/example/llama:1", built.Spec.Image);
        Assert.Equal(Digest, built.Spec.ImageDigest);
        Assert.Contains("/models/repo/model.gguf", built.Spec.Argv);
        Assert.Contains("0.0.0.0", built.Spec.Argv);
        Assert.DoesNotContain(built.Spec.Argv, arg => arg.Contains(ProcessArgvTokens.Models, StringComparison.Ordinal));
    }

    [Fact]
    public void SglangContainer_OwnsOpenAiRoutingArguments()
    {
        var model = ContainerModel(RuntimeKind.Sglang) with
        {
            Container = new ContainerRuntimeSettings { Image = "lmsysorg/sglang:1", ImageDigest = Digest, ExtraArgs = ["--tp", "2"] },
        };
        var built = LaunchSpecBuilder.Build(model, ContainerNode(), "unused", 120, false,
            nodeFeatures: [AgentFeatures.RuntimeSglangContainer]);

        Assert.Equal(8000, built.Spec.ContainerPort);
        Assert.Equal("/health", built.Spec.HealthPath);
        Assert.Contains("--enable-metrics", built.Spec.Argv);
        Assert.Contains("/models/repo/model", built.Spec.Argv);
        Assert.Contains("served", built.Spec.Argv);
        Assert.Equal(["--tp", "2"], built.Spec.Argv.TakeLast(2));
    }

    [Fact]
    public void SglangContainer_SetsTpSize_FromTheGpuCount_UnlessExtraArgsSetIt()
    {
        var node = ContainerNode();
        ModelDefinition On(int gpus, params string[] extra) => ContainerModel(RuntimeKind.Sglang) with
        {
            Container = new ContainerRuntimeSettings { Image = "lmsysorg/sglang:1", ImageDigest = Digest, ExtraArgs = extra },
            Placement = new ModelPlacement(node.Id, [.. Enumerable.Range(0, gpus).Select(i => $"GPU-{i}")]),
        };
        IReadOnlyList<string> Argv(ModelDefinition model) => LaunchSpecBuilder.Build(model, node, "unused", 120, false,
            nodeFeatures: [AgentFeatures.RuntimeSglangContainer, AgentFeatures.GpuSelectionUuids]).Spec.Argv;

        Assert.Equal("2", Argv(On(2))[Argv(On(2)).ToList().IndexOf("--tp-size") + 1]);
        // One GPU keeps the same arguments, so a running model's fingerprint doesn't change.
        Assert.DoesNotContain("--tp-size", Argv(On(1)));
        Assert.DoesNotContain("--tp-size", Argv(On(2, "--tp", "1")));
        Assert.DoesNotContain("--tp-size", Argv(On(2, "--tp=1")));
        Assert.Equal(["--tp-size", "1"], Argv(On(2, "--tp-size", "1")).TakeLast(2));
    }

    [Theory]
    [InlineData("dflash", "--speculative-num-draft-tokens")]
    [InlineData("NGRAM", "--speculative-num-draft-tokens")]
    [InlineData("EAGLE3", "--speculative-num-steps")]
    [InlineData("STANDALONE", "--speculative-num-steps")]
    public void SglangContainer_TurnsSpeculativeIntoFlags_BeforeTheExtraArgs(string method, string depthFlag)
    {
        var model = ContainerModel(RuntimeKind.Sglang) with
        {
            Container = new ContainerRuntimeSettings { Image = "lmsysorg/sglang:1", ImageDigest = Digest, ExtraArgs = ["--tp", "2"] },
            Speculative = new SpeculativeDecoding(method, 7) { DraftModel = "/models/drafter" },
        };
        var built = LaunchSpecBuilder.Build(model, ContainerNode(), "unused", 120, false, nodeFeatures: [AgentFeatures.RuntimeSglangContainer]);

        Assert.Equal(
            ["--speculative-algorithm", method.ToUpperInvariant(), "--speculative-draft-model-path", "/models/drafter", depthFlag, "7", "--tp", "2"],
            built.Spec.Argv.TakeLast(8));
    }

    [Theory]
    [InlineData("", "/models/d", "needs an algorithm")]
    [InlineData("--evil", "/models/d", "needs an algorithm")]
    [InlineData("DFLASH", "/etc/passwd", "must be a folder under /models")]
    [InlineData("DFLASH", "/models/../etc", "must be a folder under /models")]
    public void SglangSpeculative_RefusesBadSettings(string method, string draft, string message)
    {
        var model = ContainerModel(RuntimeKind.Sglang) with
        {
            Container = new ContainerRuntimeSettings { Image = "lmsysorg/sglang:1" },
            Speculative = new SpeculativeDecoding(method, 7) { DraftModel = draft },
        };
        Assert.Contains(message, SglangSpeculativeArgs.Problem(model), StringComparison.Ordinal);
    }

    [Fact]
    public void ImageToPull_IsTheImageEachRuntimeActuallyRuns()
    {
        var node = ContainerNode() with { DefaultVllmImage = "vllm:default", DefaultLlamaCppImage = "llama:default", DefaultLlamaCppImageDigest = Digest };
        var options = new MirbadLLMOptions();
        var model = ContainerModel(RuntimeKind.Sglang) with { Container = null };

        Assert.Equal(("vllm:default", null), LaunchSpecBuilder.ImageToPull(model with { Runtime = RuntimeKind.Vllm }, node, options)!.Value);
        Assert.Equal(("sg:1", Digest), LaunchSpecBuilder.ImageToPull(model with { Container = new ContainerRuntimeSettings { Image = "sg:1", ImageDigest = Digest } }, node, options)!.Value);
        Assert.Equal(("nim:1", Digest), LaunchSpecBuilder.ImageToPull(model with { Runtime = RuntimeKind.Nim, Container = new ContainerRuntimeSettings { Image = "nim:1", ImageDigest = Digest } }, node, options)!.Value);
        Assert.Equal(("llama:default", Digest), LaunchSpecBuilder.ImageToPull(model with { Runtime = RuntimeKind.LlamaCpp, LlamaCpp = new LlamaCppSettings { ModelFile = "m/a.gguf" } }, node, options)!.Value);
        Assert.Equal(("llama:1", null), LaunchSpecBuilder.ImageToPull(model with { Runtime = RuntimeKind.LlamaCpp, LlamaCpp = new LlamaCppSettings { ModelFile = "m/a.gguf", Image = "llama:1" } }, node, options)!.Value);

        // A native process needs no image: a node without Docker would never answer an image request.
        Assert.Null(LaunchSpecBuilder.ImageToPull(model with { Runtime = RuntimeKind.LlamaCpp, LlamaCpp = new LlamaCppSettings { ModelFile = "m/a.gguf", Delivery = LaunchDelivery.Process } }, node, options));
        Assert.Null(LaunchSpecBuilder.ImageToPull(model with { Runtime = RuntimeKind.Mlx }, node, options));
    }

    [Theory]
    [InlineData("--port 9000")]
    [InlineData("--port=9000")]
    [InlineData("--port")]
    public void ContainerExtraArgs_RefuseAReservedFlag_HoweverItIsWritten(string arg)
    {
        Assert.NotNull(LaunchSpecBuilder.ContainerExtraArgProblem(RuntimeKind.Sglang, [arg]));
    }

    [Fact]
    public void SglangSpeculative_RefusesAFlagRepeatedInExtraArgs()
    {
        var model = ContainerModel(RuntimeKind.Sglang) with
        {
            Container = new ContainerRuntimeSettings { Image = "lmsysorg/sglang:1", ExtraArgs = ["--speculative-algorithm=EAGLE"] },
            Speculative = new SpeculativeDecoding("DFLASH", 16),
        };
        Assert.Contains("repeats a speculative decoding setting", SglangSpeculativeArgs.Problem(model), StringComparison.Ordinal);
    }

    [Fact]
    public void NimContainer_RequiresDigest_UsesSecretsAndCacheReadiness()
    {
        var model = ContainerModel(RuntimeKind.Nim) with
        {
            Container = new ContainerRuntimeSettings { Image = "nvcr.io/nim/model:1", ImageDigest = Digest },
        };
        var environment = new LaunchEnvironment(
            new Dictionary<string, string> { ["NGC_API_KEY"] = "secret" }, ["NGC_API_KEY"]);
        var built = LaunchSpecBuilder.Build(model, ContainerNode(), "unused", 120, false, environment,
            [AgentFeatures.RuntimeNimContainer, AgentFeatures.LaunchEnv]);

        Assert.Equal("/v1/health/ready", built.Spec.HealthPath);
        Assert.Equal(["NGC_API_KEY"], built.Spec.SecretEnvNames);
        Assert.Equal("served", built.Spec.Env["NIM_SERVED_MODEL_NAME"]);
        Assert.Single(built.Spec.Mounts);
        Assert.Equal(LogicalVolumes.Cache, built.Spec.Mounts[0].Volume);
    }

    [Fact]
    public void NimContainer_SendsSavedVariables_AndTheFingerprintIgnoresASecretsValue()
    {
        var model = ContainerModel(RuntimeKind.Nim) with
        {
            Container = new ContainerRuntimeSettings
            {
                Image = "nvcr.io/nim/a:1",
                ImageDigest = Digest,
                Env = new Dictionary<string, string> { ["NIM_MODEL_PROFILE"] = "fast" },
                SecretEnv = new Dictionary<string, string> { ["NGC_API_KEY"] = "my ngc key" },
            },
        };
        string[] features = [AgentFeatures.RuntimeNimContainer, AgentFeatures.LaunchEnv];
        var withValue = LaunchSpecBuilder.Build(model, ContainerNode(), "unused", 120, false,
            new LaunchEnvironment(new Dictionary<string, string> { ["NGC_API_KEY"] = "secret" }, ["NGC_API_KEY"]), features);
        var withoutValue = LaunchSpecBuilder.Build(model, ContainerNode(), "unused", 120, false, nodeFeatures: features);

        Assert.Equal("secret", withValue.Spec.Env["NGC_API_KEY"]);
        Assert.Equal("fast", withValue.Spec.Env["NIM_MODEL_PROFILE"]);
        Assert.Equal(["NGC_API_KEY"], withoutValue.Spec.SecretEnvNames);
        Assert.False(withoutValue.Spec.Env.ContainsKey("NGC_API_KEY"));
        Assert.Equal(withValue.ExecutionFingerprint, withoutValue.ExecutionFingerprint);
    }

    [Theory]
    [InlineData("NGC_API_KEY", "looks like a secret")]
    [InlineData("MY_token", "looks like a secret")]
    [InlineData("1BAD", "not a valid")]
    [InlineData("NIM_SERVED_MODEL_NAME", "set from the served name")]
    public void ContainerVariables_RefuseWhatMustNotBePlain(string name, string message)
    {
        var settings = new ContainerRuntimeSettings { Image = "a:1", Env = new Dictionary<string, string> { [name] = "x" } };
        Assert.Contains(message, LaunchSpecBuilder.ContainerEnvProblem(settings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nvcr.io/nim/a:1", null)]
    [InlineData("nvcr.io/nim/a@sha256:abc", null)]
    [InlineData("evil.example/nim:1", "but the image is on evil.example")]
    [InlineData("nim/a:1", "but the image is on docker.io")]
    public void ARegistryLogin_IsOnlyForItsOwnRegistry(string image, string? message)
    {
        var settings = new ContainerRuntimeSettings { Image = image, RegistryLogin = new ContainerRegistryLogin("nvcr.io", "$oauthtoken", "ngc") };
        string? problem = LaunchSpecBuilder.ContainerEnvProblem(settings);
        if (message is null)
        {
            Assert.Null(problem);
        }
        else
        {
            Assert.Contains(message, problem, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Credentials_ComeFromSavedTokens_AndAMissingOneStopsTheStart()
    {
        var tokens = new OneToken("NGC", "the-value");
        var model = ContainerModel(RuntimeKind.Nim) with
        {
            Container = new ContainerRuntimeSettings
            {
                Image = "nvcr.io/nim/a:1",
                SecretEnv = new Dictionary<string, string> { ["NGC_API_KEY"] = "ngc" },
                RegistryLogin = new ContainerRegistryLogin("nvcr.io", "$oauthtoken", "ngc"),
            },
        };

        var resolved = await ContainerCredentials.ResolveAsync(tokens, model, Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("the-value", resolved.Environment.Variables["NGC_API_KEY"]);
        Assert.Equal(["NGC_API_KEY"], resolved.Environment.SecretNames);
        Assert.Equal("the-value", resolved.Registry!.Password);
        Assert.DoesNotContain("the-value", resolved.Registry.ToString(), StringComparison.Ordinal);

        var missing = await Assert.ThrowsAsync<ModelStartConflictException>(() => ContainerCredentials.ResolveAsync(new OneToken("other", "x"), model, Xunit.TestContext.Current.CancellationToken));
        Assert.Contains("'ngc'", missing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("the-value", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegistryPassword_IsRedacted_InAStoredPayload_AndInTheDockerHeaderGoesToItsHost()
    {
        var login = new RegistryCredentials { Username = "$oauthtoken", Password = "the-value" };
        string header = login.ToDockerHeader("nvcr.io").Replace('-', '+').Replace('_', '/');
        string json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header));
        Assert.Contains("\"serveraddress\":\"nvcr.io\"", json, StringComparison.Ordinal);
        Assert.Contains("the-value", json, StringComparison.Ordinal);

        var pull = new ImagePullRequest { OperationId = "op", IssuedAt = DateTimeOffset.UnixEpoch, ExpiresAt = DateTimeOffset.UnixEpoch, Image = "nvcr.io/nim/a:1", Registry = login };
        string stored = OperationPayload.Redact(System.Text.Json.JsonSerializer.SerializeToElement(pull, AgentJson.Options)).GetRawText();
        Assert.DoesNotContain("the-value", stored, StringComparison.Ordinal);
    }

    private sealed class OneToken(string name, string value) : ITokenStore
    {
        public Task<IReadOnlyList<SavedToken>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SavedToken>>([new SavedToken("id", name, "alue", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);

        public Task<string?> GetValueAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(id == "id" ? value : null);

        public Task<SavedToken> AddAsync(string name, string value, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<SavedToken?> UpdateAsync(string id, string name, string? value, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<SavedTokenSecret>> ExportAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<SavedTokenImportResult> ImportAsync(IReadOnlyList<SavedTokenSecret> tokens, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private const string Digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static ModelDefinition ContainerModel(RuntimeKind runtime) => new()
    {
        Id = "runtime-model",
        ServedName = "served",
        ModelPath = "repo/model",
        Runtime = runtime,
        MemoryBudgetGiB = 16,
    };

    private static NodeRecord ContainerNode() => new()
    {
        Id = "linux-1",
        Uid = "uid-linux-1",
        Address = "https://linux-1:9443",
        Approved = true,
    };
}
