using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Multinode;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>
/// MN-210: the <c>llama-server</c> argv (design-p3p4 §A.1.5–§A.1.7, D118) and its read-back. Goldens: OpenViking's
/// embedder as spike S1 launched it on the RTX 5060 Ti, a chat GGUF with the built-in ChatML template and reasoning off,
/// and the two-GPU split of the P3 fixture.
/// </summary>
public sealed class LlamaCppArgsBuilderTests
{
    private static readonly string[] Always = ["--host", "127.0.0.1", "--port", "{port}", "--metrics", "--no-ui", "--cache-ram", "0", "--fit", "off"];

    private static readonly JsonSerializerOptions Registry = new();

    // ---------------------------------------------------------------- goldens

    [Fact]
    public void Build_OpenVikingEmbedder_IsSpikeS1sRecommendedLaunch()
    {
        // S1 / C2: ub 2048 for the causal Qwen3-Embedding (8 102-token inputs embed identically to ub 8192), -b = -ub,
        // -c = 8192 per slot × 2 slots, one GPU by UUID → CUDA0, the b11476 always-args.
        string[] expected =
        [
            "-m", "{models}/Qwen3-Embedding-4B-GGUF/Qwen3-Embedding-4B-Q8_0.gguf",
            "--alias", "qwen3-embedding-4b-win",
            "-c", "16384", "-np", "2", "-ngl", "999", "-fa", "auto", "-ctk", "f16", "-ctv", "f16",
            "-b", "2048", "-ub", "2048",
            "--embedding", "--pooling", "last",
            "--split-mode", "none", "--device", "CUDA0",
            .. Always,
        ];

        Assert.Equal(expected, LlamaCppArgsBuilder.Build(LlamaCppFixtures.Embedder(), gpuCount: 1));
    }

    [Fact]
    public void Build_ChatGgufWithBuiltinChatMlAndReasoningOff()
    {
        string[] expected =
        [
            "-m", "{models}/ov-intent-analysis-v7-q8/model.gguf",
            "--alias", "ov-planner-win",
            "-c", "16384", "-np", "4", "-ngl", "999", "-fa", "auto",
            "--split-mode", "none", "--device", "CUDA0",
            "--chat-template", "chatml",
            "--reasoning", "off",
            .. Always,
        ];

        Assert.Equal(expected, LlamaCppArgsBuilder.Build(LlamaCppFixtures.Chat(), gpuCount: 1));
    }

    [Fact]
    public void Build_TwoGpuChatSplit_EverySettingInOrder_ExtraArgsLast()
    {
        string[] expected =
        [
            "-m", "{models}/Coder-14B-GGUF/coder-14b-Q5_K_M-00001-of-00002.gguf",
            "--alias", "coder-14b",
            "-c", "65536", "-np", "2", "-ngl", "999", "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0",
            "-b", "2048", "-ub", "512",
            "--split-mode", "layer", "--tensor-split", "0.417,0.583", "--device", "CUDA0,CUDA1",
            "--chat-template", "chatml",
            "--reasoning", "off",
            "-t", "8",
            .. Always,
            "--temp", "0.2",
        ];

        Assert.Equal(expected, LlamaCppArgsBuilder.Build(LlamaCppFixtures.Split(), gpuCount: 2, splitWeights: [7.5, 10.5]));
    }

    [Fact]
    public void Build_AlwaysArgsAreTheFrozenListWithFitOff()
    {
        var argv = LlamaCppArgsBuilder.Build(LlamaCppFixtures.Embedder(), 1);
        int start = argv.Count - LlamaCppFlags.AlwaysArgs.Count;
        Assert.Equal(LlamaCppFlags.AlwaysArgs, argv.Skip(start));
        Assert.Equal(["--fit", "off"], argv.TakeLast(2));
        Assert.Equal(1, argv.Count(a => a == "--fit"));
        Assert.Equal(start, LlamaCppArgsParser.IndexOfAlwaysArgs(argv));
    }

    [Theory]
    [InlineData("guoxuter/ov_intent_analysis_sft:v7_q8")]
    [InlineData("Qwen3 Embedding 4B")]
    [InlineData("a=b --port 1")]
    public void Build_AliasIsTheExactServedName_AsOneElement(string servedName)
    {
        var argv = LlamaCppArgsBuilder.Build(LlamaCppFixtures.Embedder() with { ServedName = servedName }, 1);
        Assert.Equal(servedName, argv[argv.ToList().IndexOf("--alias") + 1]);
        Assert.Equal(1, argv.Count(a => a == "--alias"));
    }

    [Theory]
    [InlineData(8192, 2, "16384")]
    [InlineData(32768, 4, "131072")]
    [InlineData(1, 1, "1")]
    public void Build_ContextIsPerSlotTimesParallel(int contextSize, int parallel, string c)
    {
        var model = LlamaCppFixtures.Chat() with { LlamaCpp = LlamaCppFixtures.Chat().LlamaCpp! with { ContextSize = contextSize, Parallel = parallel } };
        var argv = LlamaCppArgsBuilder.Build(model, 1).ToList();
        Assert.Equal(c, argv[argv.IndexOf("-c") + 1]);
        Assert.Equal(parallel.ToString(System.Globalization.CultureInfo.InvariantCulture), argv[argv.IndexOf("-np") + 1]);
    }

    [Theory]
    [InlineData(8192, null, null, "2048")]   // causal default (the builder has no GGUF): min(ContextSize, 2048)
    [InlineData(1024, null, null, "1024")]   // never above the per-slot context
    [InlineData(8192, 4096, null, "4096")]   // an explicit MicroBatchSize wins
    [InlineData(8192, null, 512, "2048")]    // BatchSize is ignored with embeddings (llama-server forces n_batch = n_ubatch)
    [InlineData(8192, 8192, 512, "8192")]    // the editor's non-causal choice: MicroBatchSize = ContextSize
    public void Build_EmbeddingBatchEqualsMicroBatch(int contextSize, int? microBatch, int? batch, string expected)
    {
        var model = LlamaCppFixtures.Embedder() with
        {
            LlamaCpp = LlamaCppFixtures.Embedder().LlamaCpp! with { ContextSize = contextSize, MicroBatchSize = microBatch, BatchSize = batch },
        };
        var argv = LlamaCppArgsBuilder.Build(model, 1).ToList();
        Assert.Equal(expected, argv[argv.IndexOf("-b") + 1]);
        Assert.Equal(expected, argv[argv.IndexOf("-ub") + 1]);
        Assert.Equal(1, argv.Count(a => a == "-b"));
    }

    [Fact]
    public void Build_ChatModelWithoutBatchSizes_LeavesLlamaCppsDefaults()
    {
        var argv = LlamaCppArgsBuilder.Build(LlamaCppFixtures.Chat(), 1);
        Assert.DoesNotContain("-b", argv);
        Assert.DoesNotContain("-ub", argv);
        Assert.DoesNotContain("--embedding", argv);
        Assert.DoesNotContain("--pooling", argv);
    }

    [Fact]
    public void Build_OptionalFlags_FollowTheirSettings()
    {
        var s = LlamaCppFixtures.Chat().LlamaCpp!;
        var model = LlamaCppFixtures.Chat() with
        {
            LlamaCpp = s with
            {
                FlashAttention = false,
                Reasoning = true,
                ChatTemplate = "chatml-ollama.jinja",
                ProjectorFile = "ov-intent-analysis-v7-q8/mmproj.gguf",
                GpuLayers = 20,
            },
        };

        var argv = LlamaCppArgsBuilder.Build(model, 1).ToList();

        Assert.Equal("off", argv[argv.IndexOf("-fa") + 1]);
        Assert.Equal("on", argv[argv.IndexOf("--reasoning") + 1]);
        Assert.Equal("20", argv[argv.IndexOf("-ngl") + 1]);
        Assert.Equal("{models}/ov-intent-analysis-v7-q8/chatml-ollama.jinja", argv[argv.IndexOf("--chat-template-file") + 1]);
        Assert.DoesNotContain("--chat-template", argv);
        Assert.Equal(["-m", "{models}/ov-intent-analysis-v7-q8/model.gguf", "--mmproj", "{models}/ov-intent-analysis-v7-q8/mmproj.gguf"], argv.Take(4));

        var auto = LlamaCppArgsBuilder.Build(LlamaCppFixtures.Chat() with { LlamaCpp = s with { Reasoning = null, ChatTemplate = null } }, 1);
        Assert.DoesNotContain("--reasoning", auto);
        Assert.DoesNotContain("--chat-template", auto);
        Assert.DoesNotContain("-t", auto);
    }

    [Fact]
    public void Build_EmbeddingWithoutPooling_UsesTheGgufsOwn()
    {
        var model = LlamaCppFixtures.Embedder() with { LlamaCpp = LlamaCppFixtures.Embedder().LlamaCpp! with { Pooling = null } };
        var argv = LlamaCppArgsBuilder.Build(model, 1);
        Assert.Contains("--embedding", argv);
        Assert.DoesNotContain("--pooling", argv);
    }

    // ---------------------------------------------------------------- GPUs

    [Fact]
    public void Build_GpusAreRenumberedCudaDevices_NeverPhysicalIndices()
    {
        // The embedder sits on physical GPU 1; inside the process it is CUDA0 (CUDA_VISIBLE_DEVICES = its UUID).
        Assert.Equal(1, LlamaCppFixtures.Embedder().Placement!.GpuIndices![0]);
        var argv = LlamaCppArgsBuilder.Build(LlamaCppFixtures.Embedder(), 1).ToList();
        Assert.Equal("CUDA0", argv[argv.IndexOf("--device") + 1]);
        Assert.Equal("CUDA0,CUDA1,CUDA2", LlamaCppArgsBuilder.Devices(3));
    }

    [Fact]
    public void Build_WithoutPlacedGpus_EmitsNoDeviceSelection()
    {
        var argv = LlamaCppArgsBuilder.Build(LlamaCppFixtures.Embedder(), gpuCount: 0);
        Assert.DoesNotContain("--device", argv);
        Assert.DoesNotContain("--split-mode", argv);
    }

    [Theory]
    [InlineData(new[] { 7.5, 10.5 }, "0.417,0.583")]
    [InlineData(new[] { 12.0, 16.0 }, "0.429,0.571")]  // the design's 12 + 16 GB example
    [InlineData(new[] { 1.0, 1.0 }, "0.5,0.5")]
    [InlineData(new[] { 1.0, 1.0, 1.0 }, "0.333,0.333,0.333")]
    public void TensorSplit_IsBudgetOverSum_ThreeDecimals(double[] weights, string expected) =>
        Assert.Equal(expected, LlamaCppArgsBuilder.FormatTensorSplit(LlamaCppArgsBuilder.TensorSplit(weights)));

    [Fact]
    public void Build_SplitModeNoneOnTwoGpus_IsRefused_AndLayerNeedsOneWeightPerGpu()
    {
        var none = LlamaCppFixtures.Split() with { LlamaCpp = LlamaCppFixtures.Split().LlamaCpp! with { SplitMode = LlamaCppSplitMode.None } };
        Assert.Contains("split mode 'none'", Assert.Throws<ArgumentException>(() => LlamaCppArgsBuilder.Build(none, 2, [7.5, 10.5])).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => LlamaCppArgsBuilder.Build(LlamaCppFixtures.Split(), 2, splitWeights: null));
        Assert.Throws<ArgumentException>(() => LlamaCppArgsBuilder.Build(LlamaCppFixtures.Split(), 2, splitWeights: [18]));

        // The derived default on two GPUs is layer.
        var derived = LlamaCppFixtures.Split() with { LlamaCpp = LlamaCppFixtures.Split().LlamaCpp! with { SplitMode = null } };
        Assert.Equal("layer", LlamaCppArgsBuilder.Build(derived, 2, [1, 1])[LlamaCppArgsBuilder.Build(derived, 2, [1, 1]).ToList().IndexOf("--split-mode") + 1]);
    }

    // ---------------------------------------------------------------- refusals

    public static TheoryData<string> RefusedExtraArgs() => new()
    {
        "--host 0.0.0.0",
        "--port=9000",
        "--api-key k",
        "--alias other",
        "-m x.gguf",
        "--fit on",
        "--log-file x.log",
        "--device CUDA1",
        "--ui",
        "--webui",
        "--cache-ram 8192",
        "-cram 8192",
        "--lora a.gguf",
        "-hf org/repo",
        "--chat-template-file t.jinja",
        "--mmproj p.gguf",

        // MN-219 (D126): the typed settings admission and the read-back rely on (llama-server takes the last value, so an
        // ExtraArg would silently override the typed one and skew the budget).
        "-c 32768",
        "--ctx-size=32768",
        "-np 4",
        "--parallel 4",
        "-ngl 10",
        "--n-gpu-layers 10",
        "-b 8192",
        "-ub 8192",
        "--ubatch-size 8192",
        "-ctk f32",
        "--cache-type-v f32",
        "-fa off",
        "--embedding",
        "--embeddings",
        "--pooling mean",
        "-ts 1,1",
        "--tensor-split 3,1",
        "-mg 1",
    };

    [Theory]
    [MemberData(nameof(RefusedExtraArgs))]
    public void Problems_RefuseOwnedAndPathValuedExtraArgs(string refusedArgs)
    {
        string[] extraArgs = refusedArgs.Split(' ');
        var model = LlamaCppFixtures.Chat() with { LlamaCpp = LlamaCppFixtures.Chat().LlamaCpp! with { ExtraArgs = ["--temp", "0.2", .. extraArgs] } };

        var problem = Assert.Single(LlamaCppArgsBuilder.Problems(model));

        Assert.Equal(PlacementProblemCodes.LlamaCppSettingsInvalid, problem.Code);
        Assert.Contains(extraArgs[0], problem.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("--temp", problem.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => LlamaCppArgsBuilder.Build(model, 1));
    }

    [Fact]
    public void Problems_AllowedExtraArgs_PassThroughVerbatim()
    {
        string[] extra = ["--temp", "0.2", "--top-p", "0.9", "-sm", "row", "--no-mmap"];
        var model = LlamaCppFixtures.Chat() with { LlamaCpp = LlamaCppFixtures.Chat().LlamaCpp! with { ExtraArgs = extra } };
        Assert.Empty(LlamaCppArgsBuilder.Problems(model));
        Assert.Equal(extra, LlamaCppArgsBuilder.Build(model, 1).TakeLast(extra.Length));
    }

    [Theory]
    [InlineData("/abs/model.gguf")]
    [InlineData("C:/models/model.gguf")]
    [InlineData("folder\\model.gguf")]
    [InlineData("../escape/model.gguf")]
    [InlineData("folder/../../model.gguf")]
    [InlineData("folder//model.gguf")]
    [InlineData("")]
    public void Problems_ModelFileMustBeAPlainRelativePath(string modelFile)
    {
        var model = LlamaCppFixtures.Chat() with { LlamaCpp = LlamaCppFixtures.Chat().LlamaCpp! with { ModelFile = modelFile } };
        Assert.Contains(LlamaCppArgsBuilder.Problems(model), p => p.Code == PlacementProblemCodes.LlamaCppSettingsInvalid && p.Message.Contains("model file", StringComparison.Ordinal));
    }

    [Fact]
    public void Problems_NameEachInvalidSetting()
    {
        var s = LlamaCppFixtures.Embedder().LlamaCpp!;
        Assert.Contains("Pooling 'avg'", Single(s with { Pooling = "avg" }), StringComparison.Ordinal);
        Assert.Contains("chat template", Single(s with { ChatTemplate = "../t.jinja" }), StringComparison.Ordinal);
        Assert.Contains("cache type", Single(s with { CacheTypeK = "--fit" }), StringComparison.Ordinal);
        Assert.Contains("context size", Single(s with { ContextSize = 0 }), StringComparison.Ordinal);
        Assert.Contains("Parallel", Single(s with { Parallel = 0 }), StringComparison.Ordinal);
        Assert.Contains("micro-batch", Single(s with { MicroBatchSize = 0 }), StringComparison.Ordinal);
        Assert.Contains("Threads", Single(s with { Threads = 0 }), StringComparison.Ordinal);
        Assert.Contains("projector", Single(s with { ProjectorFile = "../p.gguf" }), StringComparison.Ordinal);
        Assert.Empty(LlamaCppArgsBuilder.Problems(LlamaCppFixtures.Embedder()));

        var missing = Assert.Single(LlamaCppArgsBuilder.Problems(LlamaCppFixtures.Embedder() with { LlamaCpp = null }));
        Assert.Equal(PlacementProblemCodes.LlamaCppSettingsMismatch, missing.Code);

        static string Single(LlamaCppSettings settings) =>
            Assert.Single(LlamaCppArgsBuilder.Problems(LlamaCppFixtures.Embedder() with { LlamaCpp = settings })).Message;
    }

    // ---------------------------------------------------------------- read-back

    public static TheoryData<string> Variants() => new()
    {
        "embedder", "embedder-no-cache-types", "embedder-micro-4096", "embedder-noncausal", "embedder-no-pooling",
        "chat", "chat-file-template", "chat-batches", "chat-threads-reasoning-on", "split", "split-derived-mode",
        "split-equal-budgets", "extra-args", "unplaced",
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void Parse_OfBuild_RebuildsTheSameArgv_AndKeepsTheSavedSettings(string variant)
    {
        var (model, gpus, weights) = Variant(variant);
        var argv = LlamaCppArgsBuilder.Build(model, gpus, weights);

        var parsed = LlamaCppArgsParser.Parse(argv, model, LlamaCppFixtures.Node());

        Assert.Equal(argv, LlamaCppArgsBuilder.Build(parsed, gpus, weights));
        Assert.Equal(model.ServedName, parsed.ServedName);
        Assert.Equal(RuntimeKind.LlamaCpp, parsed.Runtime);
        // An unchanged definition reads back as unchanged (no spurious "old → new" rows).
        Assert.Equal(Json(model.LlamaCpp!), Json(parsed.LlamaCpp!));
    }

    [Theory]
    [MemberData(nameof(Variants))]
    public void Parse_WithoutASavedDefinition_StillRebuildsTheSameArgv(string variant)
    {
        var (model, gpus, weights) = Variant(variant);
        var argv = LlamaCppArgsBuilder.Build(model, gpus, weights);
        var blank = new ModelDefinition { Id = model.Id, ServedName = "", ModelPath = model.ModelPath, Placement = model.Placement };

        var parsed = LlamaCppArgsParser.Parse(argv, blank);

        Assert.Equal(model.ServedName, parsed.ServedName);
        Assert.Equal(argv, LlamaCppArgsBuilder.Build(parsed, gpus, weights ?? parsed.LlamaCpp!.GpuBudgetsGiB));
    }

    [Fact]
    public void Parse_ShowsWhatChanged()
    {
        var launched = LlamaCppArgsBuilder.Build(LlamaCppFixtures.Embedder(), 1);
        var edited = LlamaCppFixtures.Embedder() with
        {
            LlamaCpp = LlamaCppFixtures.Embedder().LlamaCpp! with { ContextSize = 4096, Parallel = 4, MicroBatchSize = 1024, CacheTypeK = "q8_0" },
        };

        var old = LlamaCppArgsParser.Parse(launched, edited).LlamaCpp!;

        Assert.Equal((8192, 2, (int?)null, "f16"), (old.ContextSize, old.Parallel, old.MicroBatchSize, old.CacheTypeK));
    }

    [Fact]
    public void Parse_ExtraArgsAfterTheAlwaysArgs_StayExtraArgs_EvenWhenTheyNameTypedFlags()
    {
        // The builder refuses typed flags in ExtraArgs (MN-219, D126), but a hand-built spec can still carry them after the
        // always-args: the parser keeps them raw, in order, and the typed value stays the one before the always-args.
        var model = LlamaCppFixtures.Chat();
        Assert.Throws<ArgumentException>(() => LlamaCppArgsBuilder.Build(model with { LlamaCpp = model.LlamaCpp! with { ExtraArgs = ["-ngl", "20"] } }, 1));
        var parsed = LlamaCppArgsParser.Parse([.. LlamaCppArgsBuilder.Build(model, 1), "-ngl", "20", "--temp", "0"], LlamaCppFixtures.Chat()).LlamaCpp!;

        Assert.Equal(["-ngl", "20", "--temp", "0"], parsed.ExtraArgs);
        Assert.Equal(999, parsed.GpuLayers);
    }

    [Fact]
    public void Parse_HandBuiltArgvWithASubstitutedPortOrNoCanonicalBlock()
    {
        var argv = LlamaCppArgsBuilder.Build(LlamaCppFixtures.Chat(), 1).Select(a => a == ProcessArgvTokens.Port ? "8101" : a).ToList();
        var parsed = LlamaCppArgsParser.Parse(argv, LlamaCppFixtures.Chat()).LlamaCpp!;
        Assert.Empty(parsed.ExtraArgs);

        string[] handBuilt = ["--port", "{port}", "-m", "{models}/m/m.gguf", "--host", "127.0.0.1", "--alias", "x", "--temp", "0.1", "--fit", "off", "--metrics"];
        var loose = LlamaCppArgsParser.Parse(handBuilt, new ModelDefinition { Id = "x", ServedName = "", ModelPath = "m" });
        Assert.Equal("m/m.gguf", loose.LlamaCpp!.ModelFile);
        Assert.Equal("x", loose.ServedName);
        Assert.Equal(["--temp", "0.1"], loose.LlamaCpp.ExtraArgs);
    }

    [Fact]
    public void Parse_LaunchConfig_ReadsVersionGpusAndBudget()
    {
        var node = LlamaCppFixtures.Node();
        var built = LaunchSpecBuilder.Build(LlamaCppFixtures.Embedder(), node, "img", 127, tuning: false);
        var config = new LaunchConfig
        {
            Kind = LaunchDelivery.Process,
            Executable = built.Spec.Executable,
            ExecutableVersion = "b11476",
            Argv = built.Spec.Argv,
            Gpus = [new GpuRef(1, LlamaCppFixtures.Rtx5060Ti)],
            Labels = built.Spec.Labels,
        };

        var same = LlamaCppArgsParser.Parse(config, LlamaCppFixtures.Embedder(), node);
        Assert.Null(same.LlamaCpp!.ExecutableVersion); // the node default the template did not pin: no spurious pin
        Assert.Equal(8.9, same.MemoryBudgetGiB);
        Assert.Equal([LlamaCppFixtures.Rtx5060Ti], same.Placement!.GpuUuids);
        Assert.Equal(Json(LlamaCppFixtures.Embedder().LlamaCpp!), Json(same.LlamaCpp));

        var older = LlamaCppArgsParser.Parse(config with { ExecutableVersion = "b11390" }, LlamaCppFixtures.Embedder(), node);
        Assert.Equal("b11390", older.LlamaCpp!.ExecutableVersion);

        var resized = LlamaCppArgsParser.Parse(config with { Labels = new Dictionary<string, string>(config.Labels) { [ContainerLabels.BudgetGiB] = "7.6" } }, LlamaCppFixtures.Embedder(), node);
        Assert.Equal(7.6, resized.MemoryBudgetGiB);
    }

    [Fact]
    public void Parse_TensorSplitThatIsNotTheSavedBudgets_ReadsAsBudgets()
    {
        var argv = LlamaCppArgsBuilder.Build(LlamaCppFixtures.Split(), 2, [9, 9]);
        var parsed = LlamaCppArgsParser.Parse(argv, LlamaCppFixtures.Split(), LlamaCppFixtures.Node()).LlamaCpp!;
        Assert.Equal([9.0, 9.0], parsed.GpuBudgetsGiB!);
    }

    private static (ModelDefinition Model, int Gpus, IReadOnlyList<double>? Weights) Variant(string name)
    {
        var embedder = LlamaCppFixtures.Embedder();
        var e = embedder.LlamaCpp!;
        var chat = LlamaCppFixtures.Chat();
        var c = chat.LlamaCpp!;
        var split = LlamaCppFixtures.Split();
        var s = split.LlamaCpp!;
        return name switch
        {
            "embedder" => (embedder, 1, null),
            "embedder-no-cache-types" => (embedder with { LlamaCpp = e with { CacheTypeK = null, CacheTypeV = null } }, 1, null),
            "embedder-micro-4096" => (embedder with { LlamaCpp = e with { MicroBatchSize = 4096, BatchSize = 512 } }, 1, null),
            "embedder-noncausal" => (embedder with { LlamaCpp = e with { MicroBatchSize = e.ContextSize } }, 1, null),
            "embedder-no-pooling" => (embedder with { LlamaCpp = e with { Pooling = null } }, 1, null),
            "chat" => (chat, 1, null),
            "chat-file-template" => (chat with { LlamaCpp = c with { ChatTemplate = "chatml-ollama.jinja", ProjectorFile = "ov-intent-analysis-v7-q8/mm.gguf" } }, 1, null),
            "chat-batches" => (chat with { LlamaCpp = c with { BatchSize = 4096, MicroBatchSize = 1024, FlashAttention = true } }, 1, null),
            "chat-threads-reasoning-on" => (chat with { LlamaCpp = c with { Threads = 12, Reasoning = true, CacheTypeV = "q4_0" } }, 1, null),
            "split" => (split, 2, s.GpuBudgetsGiB),
            "split-derived-mode" => (split with { LlamaCpp = s with { SplitMode = null } }, 2, s.GpuBudgetsGiB),
            "split-equal-budgets" => (split with { LlamaCpp = s with { GpuBudgetsGiB = [9, 9] } }, 2, [9, 9]),
            // MN-219 (D126): typed flags are refused in ExtraArgs now; a row split and sampling flags still pass.
            "extra-args" => (chat with { LlamaCpp = c with { ExtraArgs = ["-sm", "row", "--temp", "0"] } }, 1, null),
            "unplaced" => (chat with { Placement = null }, 0, null),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
        };
    }

    private static string Json(LlamaCppSettings settings) => JsonSerializer.Serialize(settings, Registry);
}
