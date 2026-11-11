using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.HostAgent;
using MirbadLLM.UnitTests.Orchestration;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-224 (design-p3p4 §A.1.5, §A.1.10, design §6.1; D129 (4)): the master's llama.cpp placement rules and fit. Unequal
/// GPUs are allowed for llama.cpp (the vLLM equal-GPU rule is unchanged); a save is refused with
/// <c>gpu_budgets_mismatch</c>, <c>executable_version_unavailable</c> or <c>llamacpp_settings_invalid</c>; a model split
/// over the PC's 12 + 16 GB cards without its own budgets gets per-GPU budgets in the proportion of its emitted
/// <c>--tensor-split</c>, sent only to a node reporting <c>admission.gpu-budgets</c> and shown by the fit preview; the
/// preview carries the GGUF estimate and the host RAM of a partial offload.
/// </summary>
public sealed class LlamaCppPlacementTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-mn224-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    // ------------------------------------------------------------------ placement rules

    [Fact]
    public async Task ALlamaCppModel_OnUnequalGpus_IsAccepted_WhileVllmKeepsTheEqualGpuRule()
    {
        var registry = await RegistryAsync(LlamaCppFixtures.Node());

        await registry.UpsertAsync(LlamaCppFixtures.Split(), Ct);
        var saved = (await registry.GetAsync(LlamaCppFixtures.Split().Id, Ct))!;
        Assert.Equal([LlamaCppFixtures.Rtx5070, LlamaCppFixtures.Rtx5060Ti], saved.Placement!.GpuUuids);
        Assert.Equal([0, 1], saved.Placement.GpuIndices);

        // Without budgets too (the tensor split then follows each card's capacity minus its reserve).
        await registry.UpsertAsync(SplitWithoutBudgets("split-even"), Ct);

        // The same two cards for a vLLM model: tensor parallelism still needs equal GPUs (D25).
        var vllm = LiveDefinitions.Embedding with
        {
            Id = "vllm-tp2",
            ServedName = "vllm-tp2",
            MemoryBudgetGiB = 18,
            Placement = new ModelPlacement(LlamaCppFixtures.NodeId, [LlamaCppFixtures.Rtx5070, LlamaCppFixtures.Rtx5060Ti]),
            LlamaCpp = null,
        };
        var refused = await Assert.ThrowsAsync<PlacementValidationException>(() => registry.UpsertAsync(vllm, Ct));
        var problem = Assert.Single(refused.Problems, p => p.Code == PlacementProblemCodes.GpuCapacityMismatch);
        Assert.Contains("Tensor parallelism needs GPUs of the same model and memory", problem.Message, StringComparison.Ordinal);
        Assert.Equal(LlamaCppFixtures.Rtx5060Ti, problem.GpuUuid);
    }

    [Fact]
    public async Task BudgetsThatDoNotMatchThePlacement_AreRefusedWithGpuBudgetsMismatch_AlsoOnASettingsOnlyEdit()
    {
        var registry = await RegistryAsync(LlamaCppFixtures.Node());
        var split = LlamaCppFixtures.Split();

        var wrongSum = await Assert.ThrowsAsync<PlacementValidationException>(() =>
            registry.UpsertAsync(split with { LlamaCpp = split.LlamaCpp! with { GpuBudgetsGiB = [7.5, 9] } }, Ct));
        var sum = Assert.Single(wrongSum.Problems);
        Assert.Equal(PlacementProblemCodes.GpuBudgetsMismatch, sum.Code);
        Assert.Contains("sum to 16.5 GiB, not its budget of 18 GiB", sum.Message, StringComparison.Ordinal);

        var wrongCount = await Assert.ThrowsAsync<PlacementValidationException>(() =>
            registry.UpsertAsync(split with { LlamaCpp = split.LlamaCpp! with { GpuBudgetsGiB = [18] } }, Ct));
        Assert.Equal(PlacementProblemCodes.GpuBudgetsMismatch, Assert.Single(wrongCount.Problems).Code);

        // Saved right, then a settings-only edit (same node and GPUs) that breaks the sum: still refused.
        await registry.UpsertAsync(split, Ct);
        var saved = (await registry.GetAsync(split.Id, Ct))!;
        var edit = await Assert.ThrowsAsync<PlacementValidationException>(() =>
            registry.UpsertAsync(saved with { MemoryBudgetGiB = 20 }, Ct));
        Assert.Equal(PlacementProblemCodes.GpuBudgetsMismatch, Assert.Single(edit.Problems).Code);
        Assert.Equal(18, (await registry.GetAsync(split.Id, Ct))!.MemoryBudgetGiB);
    }

    [Fact]
    public async Task APinnedVersionTheNodeDoesNotReportAsVerified_IsRefusedWithExecutableVersionUnavailable()
    {
        var registry = await RegistryAsync(LlamaCppFixtures.Node());
        var embedder = LlamaCppFixtures.Embedder();

        // Not installed at all.
        var missing = await Assert.ThrowsAsync<PlacementValidationException>(() =>
            registry.UpsertAsync(embedder with { LlamaCpp = embedder.LlamaCpp! with { ExecutableVersion = "b99999" } }, Ct));
        var problem = Assert.Single(missing.Problems);
        Assert.Equal(PlacementProblemCodes.ExecutableVersionUnavailable, problem.Code);
        Assert.Equal(
            "Model 'qwen3-embedding-4b-win' pins llama-server b99999, which node 'win-1' does not report as installed and verified " +
            "(it reports b11476, b11390 (not verified)); install that version on the node, or pin one it has (or none, for its default).",
            problem.Message);

        // Installed but not verified (the fixture's b11390).
        var unverified = await Assert.ThrowsAsync<PlacementValidationException>(() =>
            registry.UpsertAsync(embedder with { LlamaCpp = embedder.LlamaCpp! with { ExecutableVersion = "b11390" } }, Ct));
        Assert.Equal(PlacementProblemCodes.ExecutableVersionUnavailable, Assert.Single(unverified.Problems).Code);

        // The verified version, and no pin (the node's default), are accepted; a later pin change on the saved model is
        // checked too, and a fine one passes.
        await registry.UpsertAsync(embedder with { LlamaCpp = embedder.LlamaCpp! with { ExecutableVersion = null } }, Ct);
        var saved = (await registry.GetAsync(embedder.Id, Ct))!;
        var repin = await Assert.ThrowsAsync<PlacementValidationException>(() =>
            registry.UpsertAsync(saved with { LlamaCpp = saved.LlamaCpp! with { ExecutableVersion = "b99999" } }, Ct));
        Assert.Equal(PlacementProblemCodes.ExecutableVersionUnavailable, Assert.Single(repin.Problems).Code);
        await registry.UpsertAsync(saved with { LlamaCpp = saved.LlamaCpp! with { ExecutableVersion = "b11476" } }, Ct);
    }

    [Theory]
    [InlineData("ctx", "-c")]
    [InlineData("ngl", "--n-gpu-layers")]
    [InlineData("port", "--port")]
    [InlineData("pooling", null)]
    [InlineData("split-none", null)]
    public async Task InvalidLlamaCppSettings_AreRefusedWithLlamaCppSettingsInvalid(string what, string? flag)
    {
        var registry = await RegistryAsync(LlamaCppFixtures.Node());
        var split = LlamaCppFixtures.Split();
        var settings = what switch
        {
            "pooling" => split.LlamaCpp! with { Pooling = "bogus" },
            "split-none" => split.LlamaCpp! with { SplitMode = LlamaCppSplitMode.None },
            _ => split.LlamaCpp! with { ExtraArgs = [flag!, "8"] },
        };

        var refused = await Assert.ThrowsAsync<PlacementValidationException>(() => registry.UpsertAsync(split with { LlamaCpp = settings }, Ct));

        var problem = Assert.Single(refused.Problems);
        Assert.Equal(PlacementProblemCodes.LlamaCppSettingsInvalid, problem.Code);
        Assert.Contains(what switch
        {
            "pooling" => "Pooling 'bogus' is not one of",
            "split-none" => "placed on 2 GPUs with split mode 'none'",
            _ => flag!,
        }, problem.Message, StringComparison.Ordinal);
        Assert.Null(await registry.GetAsync(split.Id, Ct));
    }

    [Fact]
    public async Task ASettingsOnlyEdit_WithARefusedExtraArg_IsRefused_WhileAVllmEditIsNotLookedAt()
    {
        var registry = await RegistryAsync(LlamaCppFixtures.Node());
        await registry.UpsertAsync(LlamaCppFixtures.Embedder(), Ct);
        var saved = (await registry.GetAsync(LlamaCppFixtures.Embedder().Id, Ct))!;

        var refused = await Assert.ThrowsAsync<PlacementValidationException>(() =>
            registry.UpsertAsync(saved with { LlamaCpp = saved.LlamaCpp! with { ExtraArgs = ["-ub", "8192"] } }, Ct));
        Assert.Equal(PlacementProblemCodes.LlamaCppSettingsInvalid, Assert.Single(refused.Problems).Code);

        // vLLM flags the llama.cpp list would refuse mean nothing for a vLLM model.
        var vllm = LiveDefinitions.Embedding with
        {
            Id = "vllm-1",
            ServedName = "vllm-1",
            MemoryBudgetGiB = 8,
            Placement = new ModelPlacement(LlamaCppFixtures.NodeId, [LlamaCppFixtures.Rtx5060Ti]),
            LlamaCpp = null,
        };
        var node = LlamaCppFixtures.Node();
        var registry2 = await RegistryAsync(node with
        {
            Id = "pc-vllm",
            LastCapabilities = node.LastCapabilities! with
            {
                Runtimes = [new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = true }],
            },
        });
        await registry2.UpsertAsync(vllm with { Placement = new ModelPlacement("pc-vllm", [LlamaCppFixtures.Rtx5060Ti]) }, Ct);
        var placedVllm = (await registry2.GetAsync(vllm.Id, Ct))!;
        await registry2.UpsertAsync(placedVllm with { ExtraArgs = ["--port", "1", "-c", "4"] }, Ct);
    }

    // ------------------------------------------------------------------ per-GPU budgets: admission and fit agree

    [Fact]
    public async Task UnequalGpusWithoutBudgets_GetPerGpuBudgetsInTheTensorSplitProportion_InAdmissionAndFit()
    {
        // 12 + 16 GB, budget 18, no budgets (D129 (4)): weights 11.94 − 0 and 15.93 − 2.5 = 13.43, so 8.471 + 9.529 GiB.
        var node = LlamaCppFixtures.Node();
        var model = SplitWithoutBudgets("coder-14b-split");

        var budgets = PlacementFit.LlamaCppGpuBudgets(model, node)!;
        Assert.Equal(18 * 11.94 / 25.37, budgets[0], 9);
        Assert.Equal(18 * 13.43 / 25.37, budgets[1], 9);
        Assert.Equal(18, budgets.Sum(), 9);

        // They match the emitted --tensor-split (0.471,0.529) …
        var built = LaunchSpecBuilder.Build(model, node, "img", 127, tuning: false);
        string split = built.Spec.Argv[built.Spec.Argv.ToList().IndexOf("--tensor-split") + 1];
        Assert.Equal("0.471,0.529", split);
        Assert.Equal(LlamaCppArgsBuilder.FormatTensorSplit(LlamaCppArgsBuilder.TensorSplit(budgets)), split);

        // … are what the launch sends to a node reporting admission.gpu-budgets …
        var admission = LaunchSpecBuilder.Admission(model, node, force: false);
        Assert.Equal(budgets[0], admission.GpuBudgetsGiB![LlamaCppFixtures.Rtx5070], 9);
        Assert.Equal(budgets[1], admission.GpuBudgetsGiB[LlamaCppFixtures.Rtx5060Ti], 9);
        Assert.Equal(budgets[0], admission.BudgetForGpu(LlamaCppFixtures.Rtx5070, 2), 9);
        Assert.Equal(18, admission.BudgetGiB);

        // … and what the fit preview shows per GPU.
        var fit = await FitAsync(node, model, Memory(small: 9, large: 13));
        Assert.Equal(budgets[0], fit.Gpus[0].NeedGiB, 9);
        Assert.Equal(budgets[1], fit.Gpus[1].NeedGiB, 9);
        Assert.True(fit.Fits); // 8.47 ≤ 9 and 9.53 ≤ 13 − 2.5
        Assert.Equal(18, fit.NeedGiB);
        Assert.Contains("GPU 0 needs 8.5 GiB, 9 GiB free after the 0 GiB reserve; GPU 1 needs 9.5 GiB, 10.5 GiB free after the 2.5 GiB reserve", fit.Message, StringComparison.Ordinal);

        // The even split would have refused the 12 GB card (9 > 8.6); the per-GPU budget fits it.
        var tight = await FitAsync(node, model, Memory(small: 8.6, large: 13));
        Assert.True(tight.Gpus[0].Fits);
    }

    [Fact]
    public void ALlamaCppLaunch_SendsItsOwnHostBudget_NotTheAgentsVllmEstimate()
    {
        // MN-230 (D130): without a host budget the agent falls back to vLLM's 4 GiB × GPUs + 2 (6 GiB on one card,
        // 10 on two), while the fit preview estimates 1 GiB for a fully offloaded llama.cpp model. The launch now
        // carries the preview's own figure, so preview and admission agree.
        var node = LlamaCppFixtures.Node();
        var split = LlamaCppFixtures.Split();                      // two GPUs, no explicit host budget
        var single = LlamaCppFixtures.Chat();                      // one GPU, no explicit host budget

        Assert.Equal(PlacementFit.EstimateHostGiB(split, 2), LaunchSpecBuilder.Admission(split, node, force: false).HostBudgetGiB);
        Assert.Equal(1, LaunchSpecBuilder.Admission(split, node, force: false).HostBudgetGiB);
        Assert.Equal(1, LaunchSpecBuilder.Admission(single, node, force: false).HostBudgetGiB);

        // An explicit host budget still wins (the embedder fixture has one), and a vLLM model without --swap-space
        // still sends nothing (P2).
        Assert.Equal(2, LaunchSpecBuilder.Admission(LlamaCppFixtures.Embedder(), node, force: false).HostBudgetGiB);
        Assert.Equal(12, LaunchSpecBuilder.Admission(split with { HostMemoryBudgetGiB = 12 }, node, force: false).HostBudgetGiB);
        var vllm = new ModelDefinition { Id = "qwen", ServedName = "qwen", ModelPath = "qwen", MemoryBudgetGiB = 40, Placement = new ModelPlacement(node.Id, [LlamaCppFixtures.Rtx5070]) };
        Assert.Null(LaunchSpecBuilder.Admission(vllm, node, force: false).HostBudgetGiB);
    }

    [Fact]
    public async Task WithoutTheFlag_TheMasterSendsNoBudgets_AndTheFitShowsTheEvenSplit()
    {
        string[] withoutFlag = [.. LlamaCppFixtures.Capabilities().Features.Where(f => f != AgentFeatures.AdmissionGpuBudgets)];
        var node = LlamaCppFixtures.Node(features: withoutFlag);

        foreach (var model in new[] { SplitWithoutBudgets("coder-14b-split"), LlamaCppFixtures.Split() })
        {
            var admission = LaunchSpecBuilder.Admission(model, node, force: false);
            Assert.Null(admission.GpuBudgetsGiB);
            Assert.False(JsonSerializer.SerializeToNode(admission, AgentJson.Options)!.AsObject().ContainsKey("gpuBudgetsGiB"));
            Assert.Equal(9, admission.BudgetForGpu(LlamaCppFixtures.Rtx5070, 2)); // the agent splits evenly (P2)

            var fit = await FitAsync(node, model, Memory(small: 8.6, large: 12));
            Assert.Equal([9d, 9d], fit.Gpus.Select(g => g.NeedGiB));
            Assert.False(fit.Gpus[0].Fits);
            Assert.Contains("needs 9 GiB per GPU", fit.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OwnBudgets_AreShownPerGpu_AndOtherStartingSplitsCountTheirOwnShare()
    {
        var node = LlamaCppFixtures.Node();
        var fit = await FitAsync(node, LlamaCppFixtures.Split(), Memory(small: 7, large: 13));

        Assert.Equal([7.5, 10.5], fit.Gpus.Select(g => g.NeedGiB));
        Assert.Equal([false, true], fit.Gpus.Select(g => g.Fits));
        Assert.False(fit.Fits);
        Assert.StartsWith("Does not fit on win-1 now: GPU 0 needs 7.5 GiB, 7 GiB free", fit.Message, StringComparison.Ordinal);

        // A vLLM model on the same node keeps P2's per-GPU need and text.
        var vllm = LiveDefinitions.Embedding with
        {
            MemoryBudgetGiB = 8,
            Placement = new ModelPlacement(LlamaCppFixtures.NodeId, [LlamaCppFixtures.Rtx5060Ti]),
            LlamaCpp = null,
        };
        var vllmFit = await FitAsync(node, vllm, Memory(small: 7, large: 13));
        Assert.Equal(8, Assert.Single(vllmFit.Gpus).NeedGiB);
        Assert.Null(vllmFit.Estimate);
        Assert.StartsWith("Fits on win-1: needs 8 GiB per GPU (GPU 1: 10.5 GiB free after the 2.5 GiB reserve)", vllmFit.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ Apple Silicon: Metal's working set (macOS M7)

    [Theory]
    [InlineData(4.0, true, "Fits on mac-1: needs 4 GiB, 5.3 GiB free after the 1 GiB reserve.")]
    [InlineData(5.3, true, "Fits on mac-1: needs 5.3 GiB, 5.3 GiB free after the 1 GiB reserve.")]
    [InlineData(6.0, false, "Does not fit on mac-1: needs 6 GiB, but its GPU can keep at most 5.3 GiB resident (Metal's working set).")]
    public async Task OnAMac_TheFitIsCappedByMetalsWorkingSet_NotTheFreeRam(double budget, bool fits, string message)
    {
        // 8 GiB M2: 7 GiB free after the 1 GiB reserve, but Metal keeps at most 5.33 GiB resident.
        var mac = LlamaCppFixtures.Node() with
        {
            Id = "mac-1",
            MemoryReserveGiB = 1,
            LastCapabilities = LlamaCppFixtures.Node().LastCapabilities! with
            {
                Os = new NodeOsInfo("macos", "macOS 27.0.1"),
                Memory = new NodeMemoryInfo(MemoryModel.Unified, 8, GpuWorkingSetGiB: 5.33),
                Gpus = [],
            },
        };
        var model = LlamaCppFixtures.Embedder() with { MemoryBudgetGiB = budget, Placement = new ModelPlacement("mac-1") };
        var memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 8, HostAvailableGiB = 8 };

        NodeFitResult fit = await FitAsync(mac, model, memory);

        Assert.Equal(MemoryModel.Unified, fit.Model);
        Assert.Equal(fits, fit.Fits);
        Assert.Equal(5.33, fit.FreeGiB!.Value, 2);
        Assert.Equal(message, fit.Message);
    }

    [Fact]
    public async Task WithoutAWorkingSet_TheUnifiedFitIsUnchanged()
    {
        var dgxLike = LlamaCppFixtures.Node() with
        {
            Id = "uni-1",
            MemoryReserveGiB = 1,
            LastCapabilities = LlamaCppFixtures.Node().LastCapabilities! with { Memory = new NodeMemoryInfo(MemoryModel.Unified, 8), Gpus = [] },
        };
        var model = LlamaCppFixtures.Embedder() with { MemoryBudgetGiB = 6, Placement = new ModelPlacement("uni-1") };

        NodeFitResult fit = await FitAsync(dgxLike, model, new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 8, HostAvailableGiB = 8 });

        Assert.True(fit.Fits);
        Assert.Equal(7, fit.FreeGiB);
    }

    // ------------------------------------------------------------------ CPU-only nodes (Agent:Accelerator = cpu)

    /// <summary>A 32 GiB PC without a usable GPU, as its agent reports it: unified memory, no GPU, accelerator <c>cpu</c>.</summary>
    private static NodeRecord CpuNode() => LlamaCppFixtures.Node() with
    {
        Id = "cpu-1",
        MemoryReserveGiB = 4,
        GpuReserveGiB = new Dictionary<string, double>(),
        LastCapabilities = LlamaCppFixtures.Capabilities() with
        {
            Accelerator = NodeAccelerators.Cpu,
            Memory = new NodeMemoryInfo(MemoryModel.Unified, 32),
            Gpus = [],
            Features =
            [
                .. LlamaCppFixtures.Capabilities().Features.Where(f => f is not (AgentFeatures.AdmissionDiscrete
                    or AgentFeatures.AdmissionGpuBudgets or AgentFeatures.GpuSelectionUuids or AgentFeatures.LaunchEnv)),
            ],
        },
    };

    [Fact]
    public async Task ACpuOnlyNode_TakesAModelWithoutGpus_AndRefusesASelectedGpu()
    {
        var registry = await RegistryAsync(CpuNode());
        var embedder = LlamaCppFixtures.Embedder() with { HostMemoryBudgetGiB = null };

        await registry.UpsertAsync(embedder with { Placement = new ModelPlacement("cpu-1") }, Ct);
        var saved = (await registry.GetAsync(embedder.Id, Ct))!;
        Assert.Equal("cpu-1", saved.Placement!.NodeId);
        Assert.Empty(saved.Placement.GpuUuids ?? []);

        var withGpu = embedder with { Id = "with-gpu", ServedName = "with-gpu", Placement = new ModelPlacement("cpu-1", [LlamaCppFixtures.Rtx5070]) };
        var refused = await Assert.ThrowsAsync<PlacementValidationException>(() => registry.UpsertAsync(withGpu, Ct));
        Assert.Contains(refused.Problems, p => p.Code == PlacementProblemCodes.GpusOnUnifiedNode);
    }

    [Fact]
    public async Task OnACpuOnlyNode_TheFitIsHostRam_AndTheEstimateCountsEveryWeightAndNoCudaContext()
    {
        var node = CpuNode();
        // GPU layers 0 is what an operator would type on a machine without a GPU; the weights still count.
        var embedder = LlamaCppFixtures.Embedder();
        var model = embedder with
        {
            MemoryBudgetGiB = 8.5,
            HostMemoryBudgetGiB = null,
            Placement = new ModelPlacement("cpu-1"),
            LlamaCpp = embedder.LlamaCpp! with { GpuLayers = 0, GpuBudgetsGiB = null },
        };
        var memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 32, HostAvailableGiB = 12 };

        NodeFitResult fit = await FitAsync(node, model, memory, new GgufAgents(GgufReaderTests.Qwen3Embedding));

        Assert.Equal(MemoryModel.Unified, fit.Model);
        Assert.Equal(8, fit.FreeGiB); // 12 GiB available minus the 4 GiB reserve: host memory, no GPU figure
        Assert.False(fit.Fits);
        Assert.Empty(fit.Gpus);
        var estimate = fit.Estimate!;
        Assert.Equal(0, estimate.CudaContextGiB);
        Assert.Equal(0, estimate.HostGiB);
        Assert.Equal(3.98, estimate.WeightsGiB);
        Assert.Equal(8.41, estimate.BudgetGiB); // the NVIDIA estimate of the same file (8.86) without its 0.45 GiB CUDA context

        // The launch admits the same single budget against the node's memory reserve: no host or per-GPU split.
        var admission = LaunchSpecBuilder.Admission(model, node, force: false, estimate: estimate);
        Assert.Equal((8.5, 4d), (admission.BudgetGiB, admission.ReserveGiB));
        Assert.Null(admission.HostBudgetGiB);
        Assert.Null(admission.GpuBudgetsGiB);

        // And the argv names no device, so the CPU build is not asked for a CUDA card.
        var spec = LaunchSpecBuilder.BuildLlamaCpp(model, node, tuning: false).Spec;
        Assert.DoesNotContain("--device", spec.Argv);
        Assert.Equal(GpuSelectionMode.All, spec.Gpus.Mode);
    }

    // ------------------------------------------------------------------ Vulkan nodes (Agent:Accelerator = vulkan, experimental)

    /// <summary>The PC of the fixtures as a Vulkan node reports itself: the same two cards, accelerator <c>vulkan</c>.</summary>
    private static NodeRecord VulkanNode(string id = LlamaCppFixtures.NodeId, RuntimeCapability? runtime = null)
    {
        var caps = LlamaCppFixtures.Capabilities();
        return LlamaCppFixtures.Node() with
        {
            Id = id,
            LastCapabilities = caps with { Accelerator = NodeAccelerators.Vulkan, Runtimes = runtime is null ? caps.Runtimes : [runtime] },
        };
    }

    [Fact]
    public void OnAVulkanNode_TheArgvNamesVulkanDevices_InPlacementOrder_AndAnNvidiaNodeKeepsCuda()
    {
        var split = LlamaCppFixtures.Split(); // placed on the RTX 5070, then the RTX 5060 Ti

        var vulkan = LaunchSpecBuilder.BuildLlamaCpp(split, VulkanNode(), tuning: false);
        var nvidia = LaunchSpecBuilder.BuildLlamaCpp(split, LlamaCppFixtures.Node(), tuning: false);

        static string DeviceOf(LaunchSpec spec) => spec.Argv[spec.Argv.ToList().IndexOf("--device") + 1];
        Assert.Equal("Vulkan0,Vulkan1", DeviceOf(vulkan.Spec));
        Assert.Equal("CUDA0,CUDA1", DeviceOf(nvidia.Spec));
        // The GPUs are still selected by UUID in placement order; the agent turns them into Vulkan indices.
        Assert.Equal([LlamaCppFixtures.Rtx5070, LlamaCppFixtures.Rtx5060Ti], vulkan.Spec.Gpus.Uuids);
        Assert.Equal(nvidia.Spec.Argv.Where(a => !a.StartsWith("CUDA", StringComparison.Ordinal)), vulkan.Spec.Argv.Where(a => !a.StartsWith("Vulkan", StringComparison.Ordinal)));
        Assert.NotEqual(nvidia.ExecutionFingerprint, vulkan.ExecutionFingerprint);

        Assert.Equal("Vulkan0", LlamaCppArgsBuilder.Devices(1, vulkan: true));
        Assert.Equal("CUDA0,CUDA1,CUDA2", LlamaCppArgsBuilder.Devices(3));
    }

    [Fact]
    public async Task AVulkanNode_GivesAContainerOneGpu_WhileANativeProcessMayTakeSeveral()
    {
        var container = new RuntimeCapability { Kind = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Container, Available = true };
        var registry = await RegistryAsync(VulkanNode(), VulkanNode("amd-linux", container));

        // Windows: a native process selects its devices by index, in order.
        await registry.UpsertAsync(LlamaCppFixtures.Split(), Ct);

        // Linux: a container gets one render node.
        var split = SplitWithoutBudgets("split-linux");
        var inContainer = split with
        {
            Placement = new ModelPlacement("amd-linux", [LlamaCppFixtures.Rtx5070, LlamaCppFixtures.Rtx5060Ti]),
            LlamaCpp = split.LlamaCpp! with { Delivery = LaunchDelivery.Container, Image = "ghcr.io/ggml-org/llama.cpp:server-vulkan", ExecutableVersion = null },
        };
        var refused = await Assert.ThrowsAsync<PlacementValidationException>(() => registry.UpsertAsync(inContainer, Ct));
        var problem = Assert.Single(refused.Problems);
        Assert.Equal(PlacementProblemCodes.RuntimeUnavailable, problem.Code);
        Assert.Equal(
            "Node 'amd-linux' runs llama.cpp containers through Vulkan (experimental), one GPU per model; 2 are selected. Select a single GPU.",
            problem.Message);

        await registry.UpsertAsync(inContainer with { Placement = new ModelPlacement("amd-linux", [LlamaCppFixtures.Rtx5070]) }, Ct);
        Assert.Equal([LlamaCppFixtures.Rtx5070], (await registry.GetAsync("split-linux", Ct))!.Placement!.GpuUuids);
    }

    // ------------------------------------------------------------------ the GGUF estimate and host RAM

    [Fact]
    public async Task TheFit_CarriesTheGgufEstimate_ReadFromTheNode()
    {
        var node = LlamaCppFixtures.Node();
        var agents = new GgufAgents(GgufReaderTests.Qwen3Embedding);

        var fit = await FitAsync(node, LlamaCppFixtures.Embedder(), Memory(small: 4, large: 13), agents);

        Assert.Equal([("Qwen3-Embedding-4B-GGUF", "Qwen3-Embedding-4B-Q8_0.gguf")], agents.Calls);
        var estimate = fit.Estimate!;
        Assert.Equal(LlamaCppMemoryEstimate.Estimate(GgufReaderTests.Qwen3Embedding, LlamaCppFixtures.Embedder().LlamaCpp!), estimate);
        Assert.Equal(8.86, estimate.BudgetGiB); // ≈ 8.9 GiB at the emitted ub 2048 (D118), not 13.5
        Assert.Equal(0, estimate.HostGiB);
        Assert.Equal(8.9, fit.NeedGiB); // the budget admission uses
        Assert.True(fit.Fits);
        Assert.EndsWith(" GGUF estimate: 8.9 GiB.", fit.Message, StringComparison.Ordinal);
        Assert.Equal(2, fit.Host!.NeedGiB); // the model's own host budget
        Assert.True(JsonSerializer.SerializeToNode(fit, AgentJson.Options)!.AsObject().ContainsKey("estimate"));
    }

    [Fact]
    public async Task APartialOffload_NeedsTheCpuLayersInHostRam()
    {
        // 18 of 36 layers on the GPU: half the 3.98 GiB of tensors stay on the CPU, so the host part is 1 + 1.99 GiB
        // (design §6.1), unless the model names its own host budget.
        var node = LlamaCppFixtures.Node();
        var embedder = LlamaCppFixtures.Embedder();
        var partial = embedder with { HostMemoryBudgetGiB = null, LlamaCpp = embedder.LlamaCpp! with { GpuLayers = 18 } };

        var fit = await FitAsync(node, partial, Memory(small: 4, large: 13), new GgufAgents(GgufReaderTests.Qwen3Embedding));

        Assert.Equal(1.99, fit.Estimate!.HostGiB);
        Assert.Equal(2.99, fit.Host!.NeedGiB, 9);
        Assert.True(fit.Host.Estimated);
        Assert.Contains("host RAM needs 3 GiB (estimate)", fit.Message, StringComparison.Ordinal);
        Assert.EndsWith(" of layers in host RAM.", fit.Message, StringComparison.Ordinal);

        // Without a summary the llama.cpp host estimate stays 1 GiB (all layers on the GPU).
        var blind = await FitAsync(node, partial, Memory(small: 4, large: 13));
        Assert.Null(blind.Estimate);
        Assert.Equal(1, blind.Host!.NeedGiB);
        Assert.Equal(1, PlacementFit.LlamaCppHostGiB(null));
    }

    [Fact]
    public async Task ThePreview_ReadsANodesGgufHeaderOnce_AndCachesTheFailureToo()
    {
        // MN-239 item 12. MN-233 composed this class with agent clients, so the preview became a remote read on the
        // editor's keystroke path: RefreshFitAsync is bound to ten handlers, loops over every node and then re-checks
        // the selected one. Twice the same question must cost one GgufInspect — and, because a node that does not
        // answer costs a whole GgufTimeout, the failure has to be cached as well or the stall is still there.
        var node = LlamaCppFixtures.Node();
        var model = LlamaCppFixtures.Embedder();

        var answering = new GgufAgents(GgufReaderTests.Qwen3Embedding);
        var fit = await NewFitAsync(node, Memory(small: 4, large: 13), answering);
        var first = await fit.CheckAsync(node.Id, new NodeFitRequest { Definition = model }, Ct);
        var second = await fit.CheckAsync(node.Id, new NodeFitRequest { Definition = model }, Ct);
        Assert.NotNull(first!.Estimate);
        Assert.Equal(first.Estimate.BudgetGiB, second!.Estimate!.BudgetGiB);
        Assert.Single(answering.Calls);

        // The launch path asks the same instance for the same file (MN-239-17) and is served from that one read.
        Assert.NotNull(await fit.EstimateAsync(node, model, Ct));
        Assert.Single(answering.Calls);

        // A node that hangs: the first pass pays GgufTimeout, the second pays nothing.
        var hanging = new GgufAgents(null) { Hang = true };
        var slow = await NewFitAsync(node, Memory(small: 4, large: 13), hanging);
        Assert.Null((await slow.CheckAsync(node.Id, new NodeFitRequest { Definition = model }, Ct))!.Estimate);
        Assert.Null((await slow.CheckAsync(node.Id, new NodeFitRequest { Definition = model }, Ct))!.Estimate);
        Assert.Single(hanging.Calls);

        // A different file on the same node is a different question.
        var other = model with { LlamaCpp = model.LlamaCpp! with { ModelFile = "other-folder/other.gguf" } };
        Assert.Null((await slow.CheckAsync(node.Id, new NodeFitRequest { Definition = other }, Ct))!.Estimate);
        Assert.Equal(2, hanging.Calls.Count);
    }

    [Fact]
    public async Task ATunesGgufRead_IsBounded_AndSaysWhyItGaveUp()
    {
        // MN-239 item 13. The swallow stays (D130: a null means "the candidates' memory was not checked", and the
        // tuning plan says so), but the agent layer has only a 5 s *connect* timeout: a node that accepted the
        // connection and then never answered held the tuning thread for as long as it liked. Nothing reverted the
        // thread; the job simply never finished.
        var model = LlamaCppFixtures.Embedder();
        var hanging = new GgufAgents(null) { Hang = true };
        var logs = new CapturingLogger<PlacementFit>();

        var read = Task.Run(
            () => ServiceCollectionExtensions.InspectRemoteGguf(
                hanging, logs, LlamaCppFixtures.NodeId, model, TimeSpan.FromMilliseconds(100)),
            Ct);
        Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Single(hanging.Calls);
        Assert.Contains(logs.Entries, e => e.Message.Contains("did not answer the GGUF header read", StringComparison.Ordinal));

        // And the case where nothing is sent at all is distinguishable in the log, which it was not before.
        logs.Entries.Clear();
        var unaddressable = model with { LlamaCpp = model.LlamaCpp! with { ModelFile = "deep/folder/file.gguf" } };
        var never = new GgufAgents(GgufReaderTests.Qwen3Embedding);
        Assert.Null(ServiceCollectionExtensions.InspectRemoteGguf(never, logs, LlamaCppFixtures.NodeId, unaddressable));
        Assert.Empty(never.Calls);
        Assert.Contains(logs.Entries, e => e.Message.Contains("is not a file directly inside a top-level folder", StringComparison.Ordinal));

        // The production ceiling is finite: that, not its exact value, is the invariant.
        Assert.True(ServiceCollectionExtensions.TuningGgufTimeout > TimeSpan.Zero);
        Assert.True(ServiceCollectionExtensions.TuningGgufTimeout < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task NoEstimate_WithoutTheFeature_OnAFailingOrSlowNode_OrWithoutClients()
    {
        var embedder = LlamaCppFixtures.Embedder();
        string[] noInspect = [.. LlamaCppFixtures.Capabilities().Features.Where(f => f != AgentFeatures.GgufInspect)];

        var unsupported = new GgufAgents(GgufReaderTests.Qwen3Embedding);
        var withoutFeature = await FitAsync(LlamaCppFixtures.Node(features: noInspect), embedder, Memory(4, 13), unsupported);
        Assert.Null(withoutFeature.Estimate);
        Assert.Empty(unsupported.Calls); // never asked

        // MN-234: nor on a node that is not approved yet. Approval is the master's trust gate — "before it, nothing is
        // sent to the node" (D130, ApiNodeScope) — and MN-233 gave this class the agent clients, so POST
        // /api/nodes/{id}/fit and the model editor could read a GGUF off a node the operator has not accepted. The fit
        // verdict is still answered (that is MN-108's design, and the Nodes page shows it before approval); only the
        // remote read is withheld. CheckAsync's IsServing check does not cover it: a node the status cache has not
        // polled yet reads as null, exactly as this harness composes it.
        var pending = new GgufAgents(GgufReaderTests.Qwen3Embedding);
        var unapproved = await FitAsync(
            LlamaCppFixtures.Node() with { Approved = false, ApprovedAt = null }, embedder, Memory(4, 13), pending);
        Assert.Null(unapproved.Estimate);
        Assert.Empty(pending.Calls);
        Assert.NotNull(unapproved.Gpus); // the verdict itself is still computed

        var failing = new GgufAgents(null) { Fail = new InvalidOperationException("gguf_invalid") };
        var afterFailure = await FitAsync(LlamaCppFixtures.Node(), embedder, Memory(4, 13), failing);
        Assert.Null(afterFailure.Estimate);
        Assert.Single(failing.Calls);
        Assert.True(afterFailure.Fits);

        var hanging = new GgufAgents(null) { Hang = true };
        var started = DateTime.UtcNow;
        var afterTimeout = await FitAsync(LlamaCppFixtures.Node(), embedder, Memory(4, 13), hanging);
        Assert.Null(afterTimeout.Estimate);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.Zero, PlacementFit.GgufTimeout + TimeSpan.FromSeconds(20));

        var none = await FitAsync(LlamaCppFixtures.Node(), embedder, Memory(4, 13));
        Assert.Null(none.Estimate);
        Assert.False(JsonSerializer.SerializeToNode(none, AgentJson.Options)!.AsObject().ContainsKey("estimate"));
        Assert.Equal(afterFailure.Message, none.Message);
    }

    // ------------------------------------------------------------------ helpers

    private static ModelDefinition SplitWithoutBudgets(string id) =>
        LlamaCppFixtures.Split() with { Id = id, ServedName = id, LlamaCpp = LlamaCppFixtures.Split().LlamaCpp! with { GpuBudgetsGiB = null } };

    private async Task<IModelRegistry> RegistryAsync(params NodeRecord[] nodes)
    {
        var options = Options.Create(new MirbadLLMOptions { DataPath = Path.Combine(_dataDir, Guid.NewGuid().ToString("N")) });
        var nodeRegistry = new JsonNodeRegistry(options, TimeProvider.System);
        foreach (var node in nodes)
        {
            await nodeRegistry.UpsertAsync(node, Ct);
        }

        var services = new ServiceCollection();
        services.AddSingleton<IOptions<MirbadLLMOptions>>(options);
        services.AddSingleton<INodeRegistry>(nodeRegistry);
        services.AddPlacementValidation();
        var provider = services.BuildServiceProvider();
        var registry = new JsonModelRegistry(
            options,
            Options.Create(new RegistryMigrationOptions()),
            provider.GetServices<IModelDefinitionValidator>(),
            Options.Create(new ModelRegistryOptions { SeedPolicy = ModelSeedPolicy.None }));
        _disposables.Add(registry);
        _disposables.Add(nodeRegistry);
        _disposables.Add(provider);
        return registry;
    }

    /// <summary>The PC's two cards with free VRAM (GiB) and 100 GiB of host memory available, read now.</summary>
    private static NodeMemorySnapshot Memory(double small, double large) => new()
    {
        Model = MemoryModel.Discrete,
        HostTotalGiB = 127,
        HostAvailableGiB = 100,
        Gpus =
        [
            new GpuMemorySnapshot(0, LlamaCppFixtures.Rtx5070, 11.94, 11.94 - small, small),
            new GpuMemorySnapshot(1, LlamaCppFixtures.Rtx5060Ti, 15.93, 15.93 - large, large),
        ],
    };

    private async Task<NodeFitResult> FitAsync(NodeRecord node, ModelDefinition model, NodeMemorySnapshot memory, IAgentClientFactory? agents = null)
    {
        var fit = await NewFitAsync(node, memory, agents);
        return (await fit.CheckAsync(node.Id, new NodeFitRequest { Definition = model }, Ct))!;
    }

    /// <summary>One <see cref="PlacementFit"/> the caller can ask more than once (its GGUF cache is per instance).</summary>
    private async Task<PlacementFit> NewFitAsync(NodeRecord node, NodeMemorySnapshot memory, IAgentClientFactory? agents = null)
    {
        var options = Options.Create(new MirbadLLMOptions { DataPath = Path.Combine(_dataDir, Guid.NewGuid().ToString("N")) });
        var nodes = new JsonNodeRegistry(options, TimeProvider.System);
        _disposables.Add(nodes);
        await nodes.UpsertAsync(node, Ct);
        var models = new JsonModelRegistry(options, Options.Create(new RegistryMigrationOptions()), [], Options.Create(new ModelRegistryOptions { SeedPolicy = ModelSeedPolicy.None }));
        _disposables.Add(models);

        return new PlacementFit(nodes, models, new OneReading(node.Id, memory), status: null, new FixedTime(), agents);
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class OneReading(string nodeId, NodeMemorySnapshot memory) : INodeTelemetry
    {
        public TimeSpan MaxSampleAge => TimeSpan.FromSeconds(15);

        public NodeTelemetryReading? GetLatest(string id) =>
            id == nodeId ? new NodeTelemetryReading(nodeId, "boot-1", new NodeSample { Seq = 1, At = Now, Memory = memory }, Now) : null;
    }

    /// <summary>Agent clients that answer only <see cref="IAgentClient.InspectGgufAsync"/> (with a summary, a failure or never).</summary>
    private sealed class GgufAgents(GgufSummary? summary) : IAgentClientFactory
    {
        public List<(string Folder, string? File)> Calls { get; } = [];

        public Exception? Fail { get; init; }

        public bool Hang { get; init; }

        public IAgentClient GetClient(string nodeId)
        {
            var client = DispatchProxy.Create<IAgentClient, GgufOnlyClient>();
            ((GgufOnlyClient)(object)client).Inspect = InspectAsync;
            return client;
        }

        private async Task<GgufSummary> InspectAsync(string folder, string? file, CancellationToken ct)
        {
            lock (Calls)
            {
                Calls.Add((folder, file));
            }

            if (Hang)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Fail is { } fail ? throw fail : summary!;
        }

        public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

        public void RebuildHandlers(string nodeId) => throw new NotSupportedException();

        public void SetNegotiatedProtocol(string nodeId, int version) => throw new NotSupportedException();
    }
}

/// <summary>An <see cref="IAgentClient"/> whose only member is the GGUF inspection (MN-224's fit tests).</summary>
#pragma warning disable CA1852 // DispatchProxy needs an unsealed type.
internal class GgufOnlyClient : DispatchProxy
#pragma warning restore CA1852
{
    public Func<string, string?, CancellationToken, Task<GgufSummary>>? Inspect { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(IAgentClient.InspectGgufAsync) && args is { Length: 3 })
        {
            return Inspect!((string)args[0]!, (string?)args[1], args[2] is CancellationToken ct ? ct : default);
        }

        throw new NotSupportedException($"{targetMethod.Name} is not part of this fake.");
    }
}
