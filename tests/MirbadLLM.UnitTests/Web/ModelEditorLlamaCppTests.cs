using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Multinode;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Pages;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// MN-226: the model editor for llama.cpp (design-p3p4 §A.7). The page tests render the real editor over a real master
/// (<see cref="MoveCluster"/>: the master composition with its registry validators and fit preview, <c>dgx</c> a P1 vLLM
/// node, <c>pc-office</c> a discrete 2 × 16 GiB node that also reports a llama.cpp native-process runtime with versions and
/// <see cref="AgentFeatures.GgufInspect"/>, its GGUF headers set through <see cref="FakeAgent.SetGgufSummary"/>), so every
/// header read goes through <see cref="IAgentClient.InspectGgufAsync"/> and every save through the code the API uses.
/// </summary>
public sealed class ModelEditorLlamaCppTests
{
    private const string Gpu0 = "GPU-fake-d000-0000-0000-000000000000";
    private const string Gpu1 = "GPU-fake-d001-0000-0000-000000000000";
    private const string Folder = "Qwen3-Embedding-4B-GGUF";
    private const string File = "Qwen3-Embedding-4B-Q8_0.gguf";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    /// <summary>The llama.cpp runtime the PC reports (b11476 the verified default, b11400 verified, b11300 broken).</summary>
    private static readonly RuntimeCapability LlamaRuntime = new()
    {
        Kind = RuntimeKind.LlamaCpp,
        Delivery = LaunchDelivery.Process,
        Available = true,
        Versions =
        [
            new RuntimeVersion("b11476", "13.4", Verified: true, Default: true),
            new RuntimeVersion("b11400", "13.4", Verified: true),
            new RuntimeVersion("b11300", "13.4", Verified: false),
        ],
    };

    /// <summary>Qwen3-Embedding-4B Q8_0's header (the frozen MN-202 fixture: 36 layers, 8 KV heads, last pooling, causal).</summary>
    private static GgufSummary Embedder(string folder = Folder) =>
        JsonSerializer.Deserialize<GgufSummary>(
            System.IO.File.ReadAllText(Path.Combine(LlamaCppFixtures.P3Fixtures(), "..", "GgufSummary.json")), AgentJson.Options)! with { Folder = folder };

    // ================================================================ the runtime option

    [Fact]
    public async Task RuntimeOption_IsOnlyOfferedOnANodeThatReportsLlamaCpp()
    {
        await using var cluster = await LlamaCluster.StartAsync();
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => Quiet(p)));

        cut.ChangeAt("[data-testid=field-node]", "dgx");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=placement-runtime]")), Wait);
        Assert.Empty(cut.FindAll("[data-testid=placement-runtime-llamacpp]"));
        Assert.Empty(cut.FindAll("[data-testid=placement-runtime-hint]"));
        Assert.Contains("vLLM", cut.Find("[data-testid=placement-runtime]").TextContent, StringComparison.Ordinal);

        cut.ChangeAt("[data-testid=field-node]", "pc-office");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=placement-runtime-llamacpp]")), Wait);
        Assert.False(cut.Find("[data-testid=placement-runtime-llamacpp]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid=placement-runtime-vllm]").HasAttribute("checked"));
        Assert.Contains("also runs llama.cpp (native process)", cut.Find("[data-testid=placement-runtime-hint]").TextContent, StringComparison.Ordinal);

        cut.ChangeAt("[data-testid=placement-runtime-llamacpp]", "llamacpp");
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=llamacpp-section]")), Wait);
        Assert.Contains("as a native process on pc-office", cut.Find("[data-testid=placement-runtime-hint]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=field-max-model-len]")); // the vLLM sections are gone
        Assert.Empty(cut.FindAll("[data-testid=field-cuda-graph-mode]"));

        // Back to a node without llama.cpp: the option and the llama.cpp sections go with it.
        cut.ChangeAt("[data-testid=field-node]", "dgx");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=placement-runtime-llamacpp]")), Wait);
        Assert.Empty(cut.FindAll("[data-testid=llamacpp-section]"));
        Assert.NotNull(cut.Find("[data-testid=field-max-model-len]"));
    }

    [Fact]
    public void RuntimeOption_IsOfferedOnAFleetOfOneWindowsPc_WithoutThePlacementSection()
    {
        // MN-239 item 3 (D131): the runtime is the node's property, not the fleet's. One approved PC that reports
        // llama.cpp must be able to take a llama.cpp model; the Placement section still belongs to a fleet of several.
        string root = Path.Combine(Path.GetTempPath(), "ss-mn239-one-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var ctx = new BunitContext();
            OneNode(ctx, root, WindowsPc());

            var cut = ctx.Render<ModelEditor>(p => Quiet(p));
            cut.WaitForElement("[data-testid=model-form]", Wait);

            Assert.Empty(cut.FindAll("[data-testid=placement-section]"));
            Assert.NotNull(cut.Find("[data-testid=field-node-readonly]"));
            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=placement-runtime-llamacpp]")), Wait);
            Assert.Contains("also runs llama.cpp (native process)", cut.Find("[data-testid=placement-runtime-hint]").TextContent, StringComparison.Ordinal);

            cut.ChangeAt("[data-testid=placement-runtime-llamacpp]", "llamacpp");

            cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=llamacpp-section]")), Wait);
            Assert.Empty(cut.FindAll("[data-testid=field-max-model-len]")); // the vLLM sections are gone
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FleetOfOneWithDiscreteGpus_ChoosesGpus_AndSavesThemOnAnExistingModel()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-one-discrete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string Gpu1 = "GPU-second";
            var twoGpus = WindowsPc() with
            {
                Features = [AgentFeatures.GpuSelectionUuids],
                Runtimes = [],
                Gpus =
                [
                    new GpuCapability { Index = 0, Uuid = Gpu0, Name = "NVIDIA GeForce RTX 5060 Ti", MemoryTotalGiB = 16 },
                    new GpuCapability { Index = 1, Uuid = Gpu1, Name = "NVIDIA GeForce RTX 5060 Ti", MemoryTotalGiB = 16 },
                ],
            };
            var model = new ModelDefinition { Id = "m", ServedName = "m", ModelPath = "m", Placement = new ModelPlacement("pc-office", [Gpu0], [0]) };
            using var ctx = new BunitContext();
            OneNode(ctx, root, twoGpus);
            var registry = (FakeModelRegistry)ctx.Services.GetRequiredService<IModelRegistry>();
            await registry.UpsertAsync(model, Xunit.TestContext.Current.CancellationToken);

            // A new model must choose, and the step says so.
            var add = ctx.Render<ModelEditor>(p => Quiet(p));
            add.WaitForElement("[data-testid=single-node-gpus]", Wait);
            Assert.Equal("GPUs", add.Find("[data-testid=wizard-step-where] .wizard-step-label").TextContent.Trim());
            Assert.Equal("Choose at least one GPU.", add.Find("[data-testid=wizard-next-hint]").TextContent.Trim());

            // An existing model shows its GPUs, and a change is saved on its placement.
            var edit = ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m"));
            edit.WaitForElement("[data-testid=single-node-gpus]", Wait);
            edit.Find($"[data-testid=single-node-gpus-check-{Gpu1}]").Change(true);
            edit.Find("form").Submit();
            edit.WaitForAssertion(() => Assert.Equal(2, registry.GetAsync("m", Xunit.TestContext.Current.CancellationToken).Result!.Placement!.GpuUuids!.Count), Wait);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RuntimeOption_StaysHiddenOnAFleetOfOneNodeWithoutLlamaCpp()
    {
        // The DGX's own page is exactly as before: no runtime option where there is no second runtime.
        string root = Path.Combine(Path.GetTempPath(), "ss-mn239-dgx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var ctx = new BunitContext();
            OneNode(ctx, root, WindowsPc() with { Runtimes = [], Features = [], Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS") });

            var cut = ctx.Render<ModelEditor>(p => Quiet(p));
            cut.WaitForElement("[data-testid=model-form]", Wait);

            Assert.Empty(cut.FindAll("[data-testid=placement-runtime]"));
            Assert.Empty(cut.FindAll("[data-testid=placement-section]"));
            Assert.NotNull(cut.Find("[data-testid=field-max-model-len]"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The Windows PC's capability report: a native llama.cpp process runtime with versions, one 16 GiB card.</summary>
    private static NodeCapabilities WindowsPc() => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "1.2.0",
        Os = new NodeOsInfo("windows", "Windows 11 Pro", "10.0.26300"),
        Arch = "amd64",
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
        Features = [AgentFeatures.RuntimeLlamaCppProcess, AgentFeatures.GgufInspect],
        Gpus = [new GpuCapability { Index = 0, Uuid = Gpu0, Name = "NVIDIA GeForce RTX 5060 Ti", MemoryTotalGiB = 16 }],
        Runtimes = [LlamaRuntime],
    };

    /// <summary>A master whose fleet is one approved node (no Placement section), with the editor's own services.</summary>
    private static void OneNode(BunitContext ctx, string root, NodeCapabilities capabilities)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry());
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(64, 40)));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        ctx.Services.AddNodeSourcing(root);
        ctx.Services.AddSingleton<IHuggingFaceDownloader>(new FakeHuggingFaceDownloader());
        ctx.Services.AddSingleton<IHuggingFaceRepoInspector>(new FakeHuggingFaceRepoInspector());
        ctx.Services.AddSingleton<INodeRegistry>(new ListOnlyNodeRegistry(
            ListOnlyNodeRegistry.Node("pc-office") with { Approved = true, LastCapabilities = capabilities }));
    }

    // ================================================================ a new model from a folder

    [Fact]
    public async Task NewModel_FromAGgufFolder_ReadsTheHeader_PrefillsIt_ShowsTheLiveEstimate_AndSavesEveryField()
    {
        await using var cluster = await LlamaCluster.StartAsync();
        cluster.Cluster.Pc.AddFolder(Folder, 4);
        cluster.Cluster.Pc.SetGgufSummary(Embedder());
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => Quiet(p)));
        cut.ChangeAt("[data-testid=field-node]", "pc-office");
        cut.WaitForElement("[data-testid=placement-runtime-llamacpp]", Wait);
        cut.ChangeAt("[data-testid=placement-runtime-llamacpp]", "llamacpp");
        cut.WaitForAssertion(() => Assert.Contains(Folder, cut.Find("[data-testid=field-source-folder]").TextContent, StringComparison.Ordinal), Wait);

        cut.ChangeAt("[data-testid=field-source-folder]", Folder);

        // The header (through the node's agent) and the pre-fill from it: embedding with last pooling, causal, so no micro-batch.
        cut.WaitForAssertion(() => Assert.Equal("qwen3", cut.Find("[data-testid=gguf-summary-arch]").TextContent.Trim()), Wait);
        Assert.Equal("36", cut.Find("[data-testid=gguf-summary-layers]").TextContent.Trim());
        Assert.Equal("last", cut.Find("[data-testid=gguf-summary-pooling]").TextContent.Trim());
        Assert.True(cut.Find("[data-testid=field-llama-embedding]").HasAttribute("checked"));
        Assert.Equal("last", cut.Find("[data-testid=field-llama-pooling]").GetAttribute("value"));
        Assert.Equal("", cut.Find("[data-testid=field-llama-ubatch]").GetAttribute("value"));
        Assert.Equal(Folder, cut.Find("[data-testid=field-model-path]").GetAttribute("value"));
        Assert.Equal("qwen3-embedding-4b-gguf", cut.Find("[data-testid=field-id]").GetAttribute("value"));

        // The live estimate follows the fields: OpenViking's 8 192 × 2 slots is the design's 8.9 GiB (weights / KV / compute).
        cut.ChangeAt("[data-testid=field-llama-context]", "8192");
        cut.ChangeAt("[data-testid=field-llama-parallel]", "2");
        cut.WaitForAssertion(() => Assert.Equal("8.9 GiB", cut.Find("[data-testid=llama-estimate-budget]").TextContent.Trim()), Wait);
        Assert.Equal("4.0 GiB", cut.Find("[data-testid=llama-estimate-weights]").TextContent.Trim());
        Assert.Equal("2.3 GiB", cut.Find("[data-testid=llama-estimate-kv]").TextContent.Trim());
        Assert.Equal("1.4 GiB", cut.Find("[data-testid=llama-estimate-compute]").TextContent.Trim());
        Assert.Contains("-b -ub 2048", cut.Find("[data-testid=field-llama-ubatch-hint]").TextContent, StringComparison.Ordinal);
        Assert.Equal("8.86", cut.Find("[data-testid=field-memory-budget]").GetAttribute("value")); // a new model takes the estimate

        // Fit per GPU next to the header: both 16 GiB cards hold 8.9 GiB.
        cut.WaitForAssertion(() =>
        {
            var lines = cut.FindAll("[data-testid=gguf-fit] li");
            Assert.Equal(2, lines.Count);
            Assert.All(lines, l => Assert.Equal("true", l.GetAttribute("data-fits")));
            Assert.Contains("GPU 0 · NVIDIA GeForce RTX 5060 Ti · 16.0 GiB: the GGUF estimate needs 8.9 GiB", lines[0].TextContent, StringComparison.Ordinal);
        }, Wait);

        // Every other field, then save.
        cut.ChangeAt($"[data-testid=placement-gpus-check-{Gpu1}]", true);
        cut.ChangeAt("[data-testid=field-llama-gpu-layers]", "30");
        cut.ChangeAt("[data-testid=field-llama-flash]", "on");
        cut.ChangeAt("[data-testid=field-llama-ctk]", "q8_0");
        cut.ChangeAt("[data-testid=field-llama-ctv]", "q4_0");
        cut.ChangeAt("[data-testid=field-llama-ubatch]", "1024");
        cut.ChangeAt("[data-testid=field-llama-threads]", "6");
        cut.ChangeAt("[data-testid=field-llama-pooling]", "mean");
        cut.ChangeAt("[data-testid=field-llama-dimensions]", false);
        cut.ChangeAt("[data-testid=field-llama-version]", "b11400");
        InputAt(cut, "[data-testid=field-llama-extra-args]", "--seed 42\n-sm none");
        cut.ChangeAt("[data-testid=field-memory-budget]", "9.5");
        cut.ChangeAt("[data-testid=field-llama-autostart]", true);
        Assert.Empty(cut.FindAll("[data-testid=llama-extra-args-refused]"));
        Submit(cut);

        var models = cluster.Master.Get<IModelRegistry>();
        ModelDefinition? saved = null;
        cut.WaitForAssertion(() => Assert.NotNull(saved = models.GetAsync("qwen3-embedding-4b-gguf", Ct).GetAwaiter().GetResult()), Wait);
        Assert.Equal(RuntimeKind.LlamaCpp, saved!.Runtime);
        Assert.Equal(Folder, saved.ModelPath);
        Assert.Equal(9.5, saved.MemoryBudgetGiB);
        Assert.True(saved.AutoStart);
        Assert.Equal("pc-office", saved.Placement!.NodeId);
        Assert.Equal([Gpu1], saved.Placement.GpuUuids);
        var s = saved.LlamaCpp!;
        Assert.Equal($"{Folder}/{File}", s.ModelFile);
        Assert.Null(s.ProjectorFile);
        Assert.Equal((8192, 2, 30), (s.ContextSize, s.Parallel, s.GpuLayers));
        Assert.Equal((true, "q8_0", "q4_0"), (s.FlashAttention, s.CacheTypeK, s.CacheTypeV));
        Assert.Equal(((int?)null, (int?)1024, (int?)6), (s.BatchSize, s.MicroBatchSize, s.Threads));
        Assert.Equal((true, "mean", (bool?)false), (s.Embedding, s.Pooling, s.EmulateDimensions));
        Assert.Equal(LaunchDelivery.Process, s.Delivery); // implied by the node (Windows: a native process)
        Assert.Equal("b11400", s.ExecutableVersion);
        Assert.Equal(["--seed", "42", "-sm", "none"], s.ExtraArgs);
        Assert.Null(s.GpuBudgetsGiB);
        Assert.Null(s.ChatTemplate);
        Assert.Empty(LlamaCppArgsBuilder.Problems(saved)); // launchable as saved
        Assert.Empty(saved.ExtraArgs);
        Assert.Equal(["embedding"], saved.Tags); // suggested from the embedding mode, not from vLLM's parser defaults
        Assert.Equal(((string?)null, (string?)null), (saved.ToolCallParser, saved.ReasoningParser));
    }

    [Fact]
    public async Task NonCausalEmbeddingGguf_PrefillsTheMicroBatchWithTheContext_UntilOneIsTyped()
    {
        await using var cluster = await LlamaCluster.StartAsync();
        cluster.Cluster.Pc.AddFolder("bge-m3-gguf", 1);
        cluster.Cluster.Pc.SetGgufSummary(Embedder("bge-m3-gguf") with
        {
            File = "bge-m3-F16.gguf", Architecture = "bert", CausalAttention = false, PoolingType = "cls", ContextLength = 8192,
        });
        using var ctx = cluster.Context();
        var cut = await NewLlamaCppAsync(ctx);

        cut.ChangeAt("[data-testid=field-source-folder]", "bge-m3-gguf");

        cut.WaitForAssertion(() => Assert.Equal("8192", cut.Find("[data-testid=field-llama-ubatch]").GetAttribute("value")), Wait);
        Assert.Equal("8192", cut.Find("[data-testid=field-llama-context]").GetAttribute("value")); // capped at the trained context
        Assert.Contains("non-causal", cut.Find("[data-testid=field-llama-ubatch-hint]").TextContent, StringComparison.Ordinal);
        Assert.Equal("cls", cut.Find("[data-testid=field-llama-pooling]").GetAttribute("value"));

        cut.ChangeAt("[data-testid=field-llama-context]", "4096");
        cut.WaitForAssertion(() => Assert.Equal("4096", cut.Find("[data-testid=field-llama-ubatch]").GetAttribute("value")), Wait); // follows the context

        cut.ChangeAt("[data-testid=field-llama-ubatch]", "2048");
        cut.ChangeAt("[data-testid=field-llama-context]", "6000");
        cut.WaitForAssertion(() => Assert.Equal("6000", cut.Find("[data-testid=field-llama-context]").GetAttribute("value")), Wait);
        Assert.Equal("2048", cut.Find("[data-testid=field-llama-ubatch]").GetAttribute("value")); // typed: kept
    }

    [Fact]
    public async Task FolderWithSeveralGgufs_ListsTheAgentsCandidates_AndReadsTheChosenOne()
    {
        await using var cluster = await LlamaCluster.StartAsync();
        cluster.Cluster.Pc.AddFolder(Folder, 8);
        cluster.Cluster.Pc.SetGgufSummary(Embedder());
        cluster.Cluster.Pc.SetGgufSummary(Embedder() with { File = "Qwen3-Embedding-4B-F16.gguf", FileType = "F16", TensorBytes = 8_000_000_000, FileBytes = 8_000_100_000 });
        using var ctx = cluster.Context();
        var cut = await NewLlamaCppAsync(ctx);

        cut.ChangeAt("[data-testid=field-source-folder]", Folder);

        // The agent's refusal carries the candidates as structured details (MN-230), so the picker offers them.
        cut.WaitForAssertion(() => Assert.Contains("several GGUFs", cut.Find("[data-testid=gguf-error]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("[data-testid=gguf-summary]"));
        Assert.Equal(
            ["", "Qwen3-Embedding-4B-F16.gguf", File],
            cut.FindAll("[data-testid=field-gguf-file] option").Select(o => o.GetAttribute("value")));
        cut.ChangeAt("[data-testid=field-gguf-file]", "Qwen3-Embedding-4B-F16.gguf");
        cut.ClickAt("[data-testid=gguf-read-button]");
        cut.WaitForAssertion(() => Assert.StartsWith("Qwen3-Embedding-4B-F16.gguf", cut.Find("[data-testid=gguf-summary-file]").TextContent.Trim(), StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("[data-testid=gguf-error]"));
    }

    // ================================================================ a Hugging Face GGUF repo

    [Fact]
    public async Task HfGgufRepo_ListsTheVariantsWithSizePartsAndFitPerGpu_ProjectorsApart_AndTheDownloadReadsTheChosenFile()
    {
        await using var cluster = await LlamaCluster.StartAsync();
        var inspector = new FakeHuggingFaceRepoInspector();
        inspector.Seed("Qwen/Qwen3-Embedding-4B-GGUF", new RepoInspection(
            "Qwen/Qwen3-Embedding-4B-GGUF", null, RepoFormat.Gguf, [],
            [
                new GgufVariant("Q8_0", [new RepoFile(File, 4_279_660_224)], 4_279_660_224),
                new GgufVariant("F16", [new RepoFile("F16/Qwen3-Embedding-4B-F16-00002-of-00002.gguf", 4_000_000_000), new RepoFile("F16/Qwen3-Embedding-4B-F16-00001-of-00002.gguf", 4_100_000_000)], 8_100_000_000),
                new GgufVariant("BF16-XL", [new RepoFile("big.gguf", 17L * 1024 * 1024 * 1024)], 17L * 1024 * 1024 * 1024),
            ],
            [new RepoFile("mmproj-F16.gguf", 600_000_000)],
            [new RepoFile("README.md", 100)]));
        using var ctx = cluster.Context(inspector);
        var cut = await NewLlamaCppAsync(ctx);
        cut.ChangeAt("#source-download", "download");
        cut.Find("[data-testid=field-repo-id]").Change("Qwen/Qwen3-Embedding-4B-GGUF");
        cut.ClickAt("[data-testid=inspect-repo-button]");

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid=gguf-variants] tbody tr").Count), Wait);
        Assert.Empty(cut.FindAll("[data-testid=gguf-runtime-note]")); // vLLM's "experimental GGUF" warning is not llama.cpp's
        var f16 = cut.Find("[data-testid=gguf-variant-f16]");
        Assert.Equal("7.54 GiB", f16.QuerySelector("[data-label=Size]")!.TextContent.Trim());
        Assert.Equal("2", f16.QuerySelector("[data-label=Parts]")!.TextContent.Trim());
        cut.WaitForAssertion(() =>
        {
            var q8 = cut.FindAll("[data-testid=gguf-variant-q8-0-fit] li");
            Assert.Equal(2, q8.Count); // one line per GPU of pc-office
            Assert.Contains("the GGUF weights need at least 4.8 GiB", q8[0].TextContent, StringComparison.Ordinal);
            Assert.Equal("true", q8[0].GetAttribute("data-fits"));
            Assert.All(cut.FindAll("[data-testid=gguf-variant-bf16-xl-fit] li"), l => Assert.Equal("false", l.GetAttribute("data-fits")));
        }, Wait);
        Assert.Contains("mmproj-F16.gguf", cut.Find("[data-testid=gguf-projectors]").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=gguf-variant-q8-0-radio]").HasAttribute("checked"));
        Assert.Equal(File, cut.Find("[data-testid=field-allow-patterns]").GetAttribute("value"));

        // The split variant and the projector: the download fetches exactly their files.
        cut.ChangeAt("[data-testid=gguf-variant-f16-radio]", "on");
        cut.ChangeAt("[data-testid=gguf-projector-mmproj-f16-gguf]", "on");
        cut.WaitForAssertion(() => Assert.Equal("F16/Qwen3-Embedding-4B-F16-00001-of-00002.gguf, F16/Qwen3-Embedding-4B-F16-00002-of-00002.gguf, mmproj-F16.gguf",
            cut.Find("[data-testid=field-allow-patterns]").GetAttribute("value")), Wait);

        // Back to Q8_0, then download: the completed folder's header of the chosen file is read and fills the form.
        cut.ChangeAt("[data-testid=gguf-variant-q8-0-radio]", "on");
        cut.ChangeAt("[data-testid=gguf-projector-none]", "on");
        cut.Find("[data-testid=field-download-folder]").Change(Folder);
        cluster.Cluster.Pc.SetGgufSummary(Embedder());
        cut.ClickAt("[data-testid=start-download-button]");

        cut.WaitForAssertion(() => Assert.Equal(File, cut.Find("[data-testid=gguf-summary-file]").TextContent.Trim()), Wait);
        Assert.Equal(Folder, cut.Find("[data-testid=field-model-path]").GetAttribute("value"));
        Assert.Equal("Qwen/Qwen3-Embedding-4B-GGUF", cut.Find("[data-testid=field-source-repo]").GetAttribute("value"));
        Assert.NotNull(cut.Find("[data-testid=llama-estimate-budget]"));
    }

    // ================================================================ extra args

    [Fact]
    public async Task ExtraArgs_TypedAndOwnedFlagsAreRefused_TheHintListsThem_AndTheSaveSaysSo()
    {
        await using var cluster = await LlamaCluster.StartAsync();
        cluster.Cluster.Pc.AddFolder(Folder, 4);
        cluster.Cluster.Pc.SetGgufSummary(Embedder());
        using var ctx = cluster.Context();
        var cut = await NewLlamaCppAsync(ctx);
        cut.ChangeAt("[data-testid=field-source-folder]", Folder);
        cut.WaitForElement("[data-testid=gguf-summary]", Wait);
        cut.ChangeAt($"[data-testid=placement-gpus-check-{Gpu0}]", true);

        // The hint names the typed flags (D126) and the UI / --cache-ram switches (D118); -sm row stays allowed.
        string hint = cut.Find("[data-testid=llama-extra-args-hint]").TextContent;
        foreach (string flag in new[] { "-c, --ctx-size", "-ngl", "-ub, --ubatch-size", "--pooling", "-ts, --tensor-split", "--cache-ram", "--no-webui", "--hf-token", "-sm row" })
        {
            Assert.Contains(flag, hint, StringComparison.Ordinal);
        }

        InputAt(cut, "[data-testid=field-llama-extra-args]", "-sm row\n-c 32768\n--cache-ram 8192\n--ngl=10\n-ngl 10");
        cut.WaitForAssertion(() => Assert.Equal("A save refuses -c --cache-ram -ngl: use its field above, or leave it to MirbadLLM.",
            cut.Find("[data-testid=llama-extra-args-refused]").TextContent.Trim()), Wait);
        Assert.Equal("true", cut.Find("[data-testid=field-llama-extra-args]").GetAttribute("aria-invalid"));

        Submit(cut);
        cut.WaitForAssertion(() => Assert.StartsWith("Extra args: -c --cache-ram -ngl cannot be set there",
            cut.Find("[data-testid=save-error]").TextContent.Trim(), StringComparison.Ordinal), Wait);
        Assert.Null(await cluster.Master.Get<IModelRegistry>().GetAsync("qwen3-embedding-4b-gguf", Ct));

        InputAt(cut, "[data-testid=field-llama-extra-args]", "-sm row");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=llama-extra-args-refused]")), Wait);
    }

    // ================================================================ two GPUs: per-GPU budgets and the tensor split

    [Fact]
    public async Task TwoGpus_ShowThePerGpuBudgets_AndTheTensorSplit_RefuseAMismatch_AndSaveThem()
    {
        await using var cluster = await LlamaCluster.StartAsync();
        cluster.Cluster.Pc.AddFolder(Folder, 4);
        cluster.Cluster.Pc.SetGgufSummary(Embedder());
        using var ctx = cluster.Context();
        var cut = await NewLlamaCppAsync(ctx);
        cut.ChangeAt("[data-testid=field-source-folder]", Folder);
        cut.WaitForElement("[data-testid=gguf-summary]", Wait);
        cut.ChangeAt("[data-testid=field-llama-context]", "8192");
        cut.ChangeAt("[data-testid=field-llama-parallel]", "2");
        cut.ChangeAt("[data-testid=field-memory-budget]", "10");

        cut.ChangeAt($"[data-testid=placement-gpus-check-{Gpu0}]", true);
        cut.WaitForAssertion(() => Assert.Contains("--split-mode none", cut.Find("[data-testid=placement-gpus-hint]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("[data-testid=placement-gpus-budgets]"));
        cut.ChangeAt($"[data-testid=placement-gpus-check-{Gpu1}]", true);

        // Without budgets the split follows capacity (two equal cards: an even split).
        cut.WaitForAssertion(() => Assert.Contains("--split-mode layer --tensor-split 0.5,0.5",
            cut.Find("[data-testid=placement-gpus-hint]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Contains("Empty: the layers are split", cut.Find("[data-testid=placement-gpus-budgets-hint]").TextContent, StringComparison.Ordinal);

        cut.ChangeAt("[data-testid=placement-gpus-budget-0]", "4");
        cut.WaitForAssertion(() => Assert.Equal("Type a budget for every ticked GPU, or clear them all.",
            cut.Find("[data-testid=placement-gpus-budgets-error]").TextContent.Trim()), Wait);
        cut.ChangeAt("[data-testid=placement-gpus-budget-1]", "5");
        cut.WaitForAssertion(() => Assert.Equal("They add up to 9.0 GiB, not the 10.0 GiB memory budget.",
            cut.Find("[data-testid=placement-gpus-budgets-error]").TextContent.Trim()), Wait);
        Submit(cut);
        cut.WaitForAssertion(() => Assert.StartsWith("Per-GPU budgets: They add up to 9.0 GiB",
            cut.Find("[data-testid=save-error]").TextContent.Trim(), StringComparison.Ordinal), Wait);

        cut.ChangeAt("[data-testid=placement-gpus-budget-1]", "6");
        cut.WaitForAssertion(() => Assert.Contains("--tensor-split 0.4,0.6", cut.Find("[data-testid=placement-gpus-hint]").TextContent, StringComparison.Ordinal), Wait);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=placement-gpus-budgets-error]")), Wait);
        // The estimate on two GPUs (two CUDA contexts: 9.31 GiB) split as the budgets split the layers.
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("9.3 GiB", cut.Find("[data-testid=llama-estimate-budget]").TextContent.Trim());
            Assert.Equal("3.7 GiB + 5.6 GiB", cut.Find("[data-testid=llama-estimate-per-gpu]").TextContent.Trim());
        }, Wait);
        Submit(cut);

        var models = cluster.Master.Get<IModelRegistry>();
        ModelDefinition? saved = null;
        cut.WaitForAssertion(() => Assert.NotNull(saved = models.GetAsync("qwen3-embedding-4b-gguf", Ct).GetAwaiter().GetResult()), Wait);
        Assert.Equal([Gpu0, Gpu1], saved!.Placement!.GpuUuids);
        Assert.Equal([4d, 6d], saved.LlamaCpp!.GpuBudgetsGiB);
        Assert.Equal(10, saved.MemoryBudgetGiB);
    }

    // ================================================================ editing

    [Fact]
    public async Task EditOfALlamaCppModel_LoadsEveryField_AndAnUnchangedSaveWritesThemBackAsTheyWere()
    {
        var model = OnPc(LlamaCppFixtures.Embedder()) with
        {
            LlamaCpp = LlamaCppFixtures.Embedder().LlamaCpp! with
            {
                FlashAttention = false, BatchSize = null, MicroBatchSize = 4096, Threads = 8, ChatTemplate = LlamaCppChatTemplate.ChatMl,
                Reasoning = false, ExecutableVersion = "b11400", ExtraArgs = ["-sm", "none", "--seed", "7"], ProjectorFile = Folder + "/mmproj-F16.gguf",
            },
        };
        await using var cluster = await LlamaCluster.StartAsync(model);
        cluster.Cluster.Pc.AddFolder(Folder, 4);
        cluster.Cluster.Pc.SetGgufSummary(Embedder());
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => Quiet(p.Add(x => x.Id, model.Id))));

        cut.WaitForElement("[data-testid=llamacpp-section]", Wait);
        Assert.True(cut.Find("[data-testid=placement-runtime-llamacpp]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=placement-runtime-vllm]").HasAttribute("disabled")); // the runtime is fixed once added
        Assert.Equal("8192", cut.Find("[data-testid=field-llama-context]").GetAttribute("value"));
        Assert.Equal("2", cut.Find("[data-testid=field-llama-parallel]").GetAttribute("value"));
        Assert.Equal("off", cut.Find("[data-testid=field-llama-flash]").GetAttribute("value"));
        Assert.Equal("f16", cut.Find("[data-testid=field-llama-ctk]").GetAttribute("value"));
        Assert.Equal("4096", cut.Find("[data-testid=field-llama-ubatch]").GetAttribute("value"));
        Assert.Equal("8", cut.Find("[data-testid=field-llama-threads]").GetAttribute("value"));
        Assert.Equal("b11400", cut.Find("[data-testid=field-llama-version]").GetAttribute("value"));
        Assert.Equal("-sm none\n--seed 7", cut.Find("[data-testid=field-llama-extra-args]").GetAttribute("value") ?? cut.Find("[data-testid=field-llama-extra-args]").TextContent);
        Assert.Equal("8.9", cut.Find("[data-testid=field-memory-budget]").GetAttribute("value"));
        // The versions the node reports: the broken one cannot be picked.
        Assert.True(cut.Find("[data-testid=field-llama-version] option[value=b11300]").HasAttribute("disabled"));
        Assert.Contains("Node default (b11476)", cut.Find("[data-testid=field-llama-version]").TextContent, StringComparison.Ordinal);
        // The header behind the estimate is read for an existing model too (its settings are not overwritten by it).
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=llama-estimate-budget]")), Wait);

        Submit(cut);

        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        cut.WaitForAssertion(() => Assert.NotEmpty(nav.History), Wait);
        var saved = (await cluster.Master.Get<IModelRegistry>().GetAsync(model.Id, Ct))!;
        Assert.Equal(JsonSerializer.Serialize(model.LlamaCpp), JsonSerializer.Serialize(saved.LlamaCpp));
        Assert.Equal((model.Runtime, model.ModelPath, model.MemoryBudgetGiB, model.AutoStart), (saved.Runtime, saved.ModelPath, saved.MemoryBudgetGiB, saved.AutoStart));
        Assert.Equal(model.Placement!.GpuUuids, saved.Placement!.GpuUuids);
    }

    [Fact]
    public async Task EditOfALlamaCppModel_CanPointAtAnotherGgufInItsFolder_WithoutLosingItsSettings()
    {
        var model = OnPc(LlamaCppFixtures.Embedder());
        await using var cluster = await LlamaCluster.StartAsync(model);
        cluster.Cluster.Pc.AddFolder(Folder, 8);
        cluster.Cluster.Pc.SetGgufSummary(Embedder());
        cluster.Cluster.Pc.SetGgufSummary(Embedder() with
        {
            File = "Qwen3-Embedding-4B-F16.gguf", FileType = "F16", TensorBytes = 8_000_000_000, FileBytes = 8_000_100_000, ContextLength = 4096,
        });
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => Quiet(p.Add(x => x.Id, model.Id))));

        // The stored file's header is read on load (the GGUF section of an existing model).
        cut.WaitForAssertion(() => Assert.Equal(File, cut.Find("[data-testid=gguf-summary-file]").TextContent.Trim()), Wait);
        Assert.Equal("4.0 GiB", cut.Find("[data-testid=llama-estimate-weights]").TextContent.Trim());

        cut.ChangeAt("[data-testid=field-gguf-file]", "Qwen3-Embedding-4B-F16.gguf");
        cut.ClickAt("[data-testid=gguf-read-button]");
        cut.WaitForAssertion(() => Assert.Equal("7.5 GiB", cut.Find("[data-testid=llama-estimate-weights]").TextContent.Trim()), Wait);
        Assert.Equal("8192", cut.Find("[data-testid=field-llama-context]").GetAttribute("value")); // an edit's settings are not re-filled
        Submit(cut);

        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        cut.WaitForAssertion(() => Assert.True(nav.History.Count > 0,
            "not saved: " + string.Join(" | ", cut.FindAll("[data-testid=save-error], [data-testid=save-error-details], [data-testid=save-notice], [data-testid=apply-changes]").Select(e => e.TextContent.Trim()))), Wait);
        var saved = (await cluster.Master.Get<IModelRegistry>().GetAsync(model.Id, Ct))!;
        Assert.Equal($"{Folder}/Qwen3-Embedding-4B-F16.gguf", saved.LlamaCpp!.ModelFile);
        Assert.Equal(8192, saved.LlamaCpp.ContextSize);
    }

    [Fact]
    public async Task EditOfARunningLlamaCppModel_AsksToRestart_WithOldToNewFromTheLaunchedArgv()
    {
        var model = OnPc(LlamaCppFixtures.Embedder());
        await using var cluster = await LlamaCluster.StartAsync(model);
        cluster.Cluster.Pc.AddFolder(Folder, 4);
        cluster.Cluster.Pc.SetGgufSummary(Embedder());
        var runtime = cluster.Master.Get<IModelRuntime>();
        await runtime.StartAsync(model.Id, ct: Ct);
        await ExitEnv.UntilAsync(async () => (await runtime.GetStatusAsync(model.Id, Ct)).State == ModelState.Ready, "the embedder Ready on pc-office");
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => Quiet(p.Add(x => x.Id, model.Id))));
        cut.WaitForElement("[data-testid=llamacpp-section]", Wait);

        cut.ChangeAt("[data-testid=field-llama-parallel]", "4");
        cut.ChangeAt("[data-testid=field-llama-dimensions]", false); // router-side: no restart for this one
        Submit(cut);

        cut.WaitForAssertion(() =>
        {
            var changes = cut.FindAll("[data-testid=apply-changes] li")
                .Select(li => System.Text.RegularExpressions.Regex.Replace(li.TextContent, @"\s+", " ").Trim()).ToList();
            Assert.Equal(["Parallel slots: 2 → 4"], changes);
        }, Wait);
        var saved = (await cluster.Master.Get<IModelRegistry>().GetAsync(model.Id, Ct))!;
        Assert.Equal((4, (bool?)false), (saved.LlamaCpp!.Parallel, saved.LlamaCpp.EmulateDimensions));
    }

    [Fact]
    public async Task VllmModel_OnALlamaCppNode_RendersAndSavesExactlyAsBefore()
    {
        var model = MoveCluster.Model("m2", "m2-ckpt") with { Placement = new ModelPlacement("pc-office", [Gpu0], [0]), EnablePrefixCaching = true };
        await using var cluster = await LlamaCluster.StartAsync(model);
        using var ctx = cluster.Context();
        var cut = Loaded(ctx.Render<ModelEditor>(p => Quiet(p.Add(x => x.Id, "m2"))));

        Assert.Empty(cut.FindAll("[data-testid=llamacpp-section]"));
        Assert.NotNull(cut.Find("[data-testid=field-max-model-len]"));
        Assert.True(cut.Find("[data-testid=placement-runtime-vllm]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=placement-runtime-llamacpp]").HasAttribute("disabled"));
        Assert.Contains("Every setting is a vLLM launch argument", cut.Markup, StringComparison.Ordinal);
        // A vLLM placement keeps the equal-capacity rule (the GPUs here are equal; the llama.cpp budgets never show).
        cut.ChangeAt($"[data-testid=placement-gpus-check-{Gpu1}]", true);
        cut.WaitForAssertion(() => Assert.Contains("--tensor-parallel-size 2", cut.Find("[data-testid=placement-gpus-hint]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Empty(cut.FindAll("[data-testid=placement-gpus-budgets]"));
        cut.ChangeAt($"[data-testid=placement-gpus-check-{Gpu1}]", false);
        cut.WaitForAssertion(() => Assert.DoesNotContain("--tensor-parallel-size 2", cut.Find("[data-testid=placement-gpus-hint]").TextContent, StringComparison.Ordinal), Wait);

        await cut.InvokeAsync(() => cut.Find("[data-testid=field-prefix-caching]").Change("off"));
        Submit(cut);

        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        cut.WaitForAssertion(() => Assert.NotEmpty(nav.History), Wait);
        var saved = (await cluster.Master.Get<IModelRegistry>().GetAsync("m2", Ct))!;
        Assert.Equal(
            JsonSerializer.Serialize(model with { EnablePrefixCaching = false, Placement = null, Tags = [] }),
            JsonSerializer.Serialize(saved with { Placement = null, Tags = [] }));
        Assert.Equal(model.Placement!.GpuUuids, saved.Placement!.GpuUuids);
        Assert.Equal(RuntimeKind.Vllm, saved.Runtime);
        Assert.Null(saved.LlamaCpp);
    }

    // ================================================================ pure rules (LlamaCppEditing)

    [Fact]
    public void Form_RoundTripsEveryStoredSetting()
    {
        var stored = new LlamaCppSettings
        {
            ModelFile = "f/sub/m-00001-of-00002.gguf", ProjectorFile = "f/mmproj.gguf", ContextSize = 4096, Parallel = 3, GpuLayers = 20,
            FlashAttention = true, CacheTypeK = "q8_0", CacheTypeV = "iq4_nl", BatchSize = 1024, MicroBatchSize = 256,
            Delivery = LaunchDelivery.Process, Image = "kept", ExtraArgs = ["-sm", "row", "--seed", "-1"], Embedding = true, Pooling = "rank",
            EmulateDimensions = false, ChatTemplate = "tpl.jinja", Reasoning = true, Threads = 4, GpuBudgetsGiB = [3.5, 4.5],
            SplitMode = LlamaCppSplitMode.Layer, ExecutableVersion = "b11400",
        };

        var form = LlamaCppForm.From(stored, "f");
        var back = form.ToSettings("f", LaunchDelivery.Process, gpuCount: 2);

        Assert.Equal("sub/m-00001-of-00002.gguf", form.File);
        Assert.Equal("-sm row\n--seed -1", form.ExtraArgsText);
        Assert.Equal(JsonSerializer.Serialize(stored), JsonSerializer.Serialize(back));
        Assert.Null(form.ToSettings("f", LaunchDelivery.Process, gpuCount: 1).GpuBudgetsGiB); // budgets only on two or more GPUs
        Assert.Equal("builtin:chatml", LlamaCppForm.From(stored with { ChatTemplate = "builtin:chatml" }, "f").ToSettings("f", LaunchDelivery.Process, 2).ChatTemplate);
    }

    [Theory]
    [InlineData("-sm row", new[] { "-sm", "row" })]
    [InlineData("  --seed   42 \n\n-c=4096\r\n", new[] { "--seed", "42", "-c=4096" })]
    [InlineData("", new string[0])]
    [InlineData("--override-kv \"k=str:a b\"", new[] { "--override-kv", "k=str:a b" })]
    [InlineData("--grammar \"a \"\"b\"\" c\"\n-sm row", new[] { "--grammar", "a \"b\" c", "-sm", "row" })]
    public void ExtraArgs_AreSplitIntoArgvElements(string text, string[] expected) =>
        Assert.Equal(expected, LlamaCppEditing.ParseExtraArgs(text));

    [Fact]
    public void ExtraArgs_RoundTrip_KeepsAnElementThatHoldsASpace()
    {
        // MN-239 item 4 (D131): the text is what the form edits, so a stored element with a space must come back as one
        // element — an unrelated save used to split "--override-kv k=str:a b" into three argv elements.
        string[] stored = ["--override-kv", "k=str:a b", "--grammar", "root ::= \"x\"", "-sm", "row"];

        string text = LlamaCppEditing.FormatExtraArgs(stored);

        Assert.Equal("--override-kv \"k=str:a b\"\n--grammar \"root ::= \"\"x\"\"\"\n-sm row", text);
        Assert.Equal(stored, LlamaCppEditing.ParseExtraArgs(text));
    }

    [Fact]
    public void Refusals_CoverTheTypedFlags_AndSplitModeStaysAllowed()
    {
        Assert.Equal(["-c", "--pooling", "-ts=1,1", "--cache-ram", "--host"],
            LlamaCppEditing.RefusedExtraArgs("-sm row\n-c 8192\n--pooling last\n-ts=1,1\n--cache-ram 0\n--host 0.0.0.0\n--seed 1"));
        Assert.Empty(LlamaCppEditing.RefusedExtraArgs("-sm row\n--seed 1"));
    }

    [Fact]
    public void Candidates_AreReadFromTheAgentsRefusal()
    {
        Assert.Equal(["a.gguf", "b-00001-of-00002.gguf"],
            LlamaCppEditing.CandidatesFrom("'m' has several model GGUFs; choose one with ?file=: a.gguf, b-00001-of-00002.gguf."));
        Assert.Empty(LlamaCppEditing.CandidatesFrom("Folder 'm' holds several GGUFs; name one with ?file=."));
        Assert.Empty(LlamaCppEditing.CandidatesFrom(null));
    }

    [Fact]
    public void RestartNeeded_ListsLlamaCppChanges_ButNothingForVllmModels_OrTheRouterOnlyEmulation()
    {
        var vllm = new ModelDefinition { Id = "m", ServedName = "m", ModelPath = "m", LlamaCpp = new LlamaCppSettings { ModelFile = "a" } };
        Assert.Empty(LlamaCppEditing.RestartNeeded(vllm, vllm with { LlamaCpp = new LlamaCppSettings { ModelFile = "b" } }));

        var before = LlamaCppFixtures.Embedder();
        var after = before with
        {
            LlamaCpp = before.LlamaCpp! with { ContextSize = 4096, ExecutableVersion = "b11400", EmulateDimensions = false, ExtraArgs = ["-sm", "row"] },
        };
        Assert.Equal(
            ["Context per request: 8,192 tokens → 4,096 tokens", "llama.cpp version: node default → b11400", "llama.cpp extra args: none → -sm row"],
            LlamaCppEditing.RestartNeeded(before, after).Select(c => c.ToString()));
    }

    [Fact]
    public void GgufFit_AgainstAGpu_SaysWhatItMeasures_TheNeedAndTheRoom()
    {
        var room = new GgufGpuRoom("GPU 1 · NVIDIA GeForce RTX 5060 Ti · 15.9 GiB", 10.9, 2.5);
        Assert.Equal(
            "GPU 1 · NVIDIA GeForce RTX 5060 Ti · 15.9 GiB: the GGUF estimate needs 8.9 GiB, 10.9 GiB free after the 2.5 GiB reserve",
            LlamaCppEditing.FitText(8.86, room));
        Assert.True(LlamaCppEditing.Fits(8.86, room));
        Assert.False(LlamaCppEditing.Fits(11, room));
        Assert.Null(LlamaCppEditing.Fits(1, room with { FreeGiB = null }));
        Assert.Equal(4.8, LlamaCppEditing.MinimumNeedGiB(4_279_660_224)); // 3.99 GiB × 1.10 + 0.45

        // MN-239 item 2 (D130): the editor shows two verdicts against the same free memory — the GGUF's own need and
        // the model's memory budget, the number admission uses — and they can disagree (5 GiB per GPU against 9.3).
        // Each line names what it measures, so neither can be read as contradicting the other.
        var fit = new NodeFitResult
        {
            NodeId = "pc-office",
            Model = MemoryModel.Discrete,
            Fits = true,
            NeedGiB = 10,
            Message = "Fits on pc-office: needs 10 GiB, 22 GiB free after the 2.5 GiB reserve.",
        };
        Assert.Equal("the GGUF estimate needs 9.3 GiB", LlamaCppEditing.NeedText(9.3));
        Assert.Equal("the GGUF weights need at least 4.8 GiB", LlamaCppEditing.NeedText(4.8, atLeast: true));
        Assert.Equal("Memory budget: " + fit.Message, PlacementEditing.FitLine(fit));
        Assert.Equal("", PlacementEditing.FitLine(null));
    }

    // ================================================================ helpers

    /// <summary>
    /// No live refresh during a test (it re-renders every 5 s and retires handler ids under a loaded full run); the fit is
    /// re-read on every change the tests make anyway.
    /// </summary>
    private static ComponentParameterCollectionBuilder<ModelEditor> Quiet(ComponentParameterCollectionBuilder<ModelEditor> p) =>
        p.Add(x => x.LiveRefreshInterval, TimeSpan.FromHours(1));

    /// <summary>Submits the editor's form, retrying when a render retired the handler id first (nothing was delivered then).</summary>
    private static void Submit(IRenderedComponent<ModelEditor> cut)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                cut.InvokeAsync(() => cut.Find("form").SubmitAsync()).GetAwaiter().GetResult();
                return;
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 5)
            {
            }
        }
    }

    /// <summary>Raises <c>oninput</c> on <paramref name="selector"/>, retrying a retired handler id like <see cref="Submit"/>.</summary>
    private static void InputAt(IRenderedComponent<ModelEditor> cut, string selector, string value)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                cut.InvokeAsync(() => cut.Find(selector).InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = value })).GetAwaiter().GetResult();
                return;
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException) when (attempt < 5)
            {
            }
        }
    }

    /// <summary>
    /// <paramref name="model"/> on pc-office's GPU 0, not auto-started: the fixture's AutoStart would let the master start it
    /// at boot, and an edit of a running model asks to restart instead of leaving the page (the tests that want that start it).
    /// </summary>
    private static ModelDefinition OnPc(ModelDefinition model) => model with { Placement = new ModelPlacement("pc-office", [Gpu0], [0]), AutoStart = false };

    /// <summary>Add model on pc-office with the llama.cpp runtime picked and the folder list loaded.</summary>
    private static async Task<IRenderedComponent<ModelEditor>> NewLlamaCppAsync(BunitContext ctx)
    {
        var cut = Loaded(ctx.Render<ModelEditor>(p => Quiet(p)));
        cut.ChangeAt("[data-testid=field-node]", "pc-office");
        cut.WaitForElement("[data-testid=placement-runtime-llamacpp]", Wait);
        cut.ChangeAt("[data-testid=placement-runtime-llamacpp]", "llamacpp");
        cut.WaitForElement("[data-testid=llamacpp-section]", Wait);
        await Task.CompletedTask;
        return cut;
    }

    /// <summary>Waits for the editor's asynchronous load (the Placement section is its last part).</summary>

    private static IRenderedComponent<ModelEditor> Loaded(IRenderedComponent<ModelEditor> cut)
    {
        cut.WaitForElement("[data-testid=placement-section]", Wait);
        cut.WaitForElement("[data-testid=model-form]", Wait);
        return cut;
    }

    /// <summary>
    /// A <see cref="MoveCluster"/> whose <c>pc-office</c> is a discrete 2 × 16 GiB node reporting a llama.cpp native-process
    /// runtime (<see cref="LlamaRuntime"/>) and <see cref="AgentFeatures.GgufInspect"/>, its master, and bUnit contexts over
    /// the master's own services.
    /// </summary>
    private sealed class LlamaCluster : IAsyncDisposable
    {
        private static readonly IReadOnlyList<string> PcFeatures =
        [
            .. FakeAgentOptions.P1Features, AgentFeatures.GpuSelectionUuids, AgentFeatures.AdmissionDiscrete,
            AgentFeatures.RuntimeLlamaCppProcess, AgentFeatures.GgufInspect,
        ];

        private LlamaCluster(MoveCluster cluster, MoveMaster master)
        {
            Cluster = cluster;
            Master = master;
        }

        public MoveCluster Cluster { get; }

        public MoveMaster Master { get; }

        public static async Task<LlamaCluster> StartAsync(params ModelDefinition[] models)
        {
            var cluster = await MoveCluster.StartAsync(
                pcOptions: o => o with
                {
                    DiscreteGpus = FakeDiscreteGpu.TwoBy16, Features = PcFeatures, TotalMemoryGiB = 64, AvailableMemoryGiB = 60, ExtraRuntimes = [LlamaRuntime],
                },
                models: models);
            try
            {
                var master = await cluster.StartMasterAsync();
                await ExitEnv.UntilAsync(async () => (await master.Get<INodeRegistry>().GetAsync("pc-office", Ct))?.LastCapabilities is { } caps &&
                    caps.Gpus.Count == 2 && caps.Runtimes.Any(r => r.Kind == RuntimeKind.LlamaCpp), "pc-office's GPUs and llama.cpp runtime in the registry");
                return new LlamaCluster(cluster, master);
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }
        }

        public BunitContext Context(IHuggingFaceRepoInspector? inspector = null)
        {
            var ctx = new BunitContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.AddAuthorization().SetAuthorized("admin");
            var master = Master.Factory.Services;
            void Copy<T>()
                where T : class
            {
                if (master.GetService<T>() is { } instance)
                {
                    ctx.Services.AddSingleton(instance);
                }
            }

            Copy<IModelRegistry>();
            Copy<INodeRegistry>();
            Copy<INodeStatusCache>();
            Copy<INodeTelemetry>();
            Copy<IModelRuntime>();
            Copy<IModelLauncher>();
            Copy<IMemoryProbe>();
            Copy<IHuggingFaceDownloader>();
            if (inspector is null)
            {
                Copy<IHuggingFaceRepoInspector>();
            }
            else
            {
                ctx.Services.AddSingleton(inspector);
            }

            Copy<RemoteModelFolders>();
            Copy<RemoteVllmCatalogService>();
            Copy<MoveService>();
            Copy<IOwnershipStore>();
            Copy<IMaintenanceMode>();
            Copy<IAgentClientFactory>();
            Copy<MirbadLLM.Orchestration.Agents.AgentOperationRunner>();
            Copy<TimeProvider>();
            ctx.Services.AddSingleton(master.GetRequiredService<IOptions<MirbadLLMOptions>>());
            return ctx;
        }

        public async ValueTask DisposeAsync()
        {
            await Master.DisposeAsync();
            await Cluster.DisposeAsync();
        }
    }
}
/// <summary>
/// MN-226: the vLLM editor renders exactly as before llama.cpp joined it. The fixtures were rendered from these same fakes
/// by the code before MN-226 (commit caeedfa): the single DGX (edit, add from a folder, add from a GGUF repo with vLLM's
/// own variant select and warning) and two nodes that report no llama.cpp (add on the PC, edit on it).
/// </summary>
public sealed partial class ModelEditorVllmSnapshotTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public static TheoryData<string> Scenarios => ["single-edit", "single-add-folder", "single-add-gguf-repo", "multi-add-pc", "multi-edit-pc"];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void VllmEditorMarkup_IsTheSameAsBeforeMn226(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-mn226-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string markup = Normalize(Render(scenario, root));
            string path = FixturePath($"editor-vllm-{scenario}.html");
            if (Environment.GetEnvironmentVariable("MIRBADLLM_RECORD_MN226") is { Length: > 0 })
            {
                File.WriteAllText(path, markup);
                return;
            }

            markup.MarkupMatches(Normalize(File.ReadAllText(path)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Render(string scenario, string root)
    {
        var model = new ModelDefinition
        {
            Id = "m1", ServedName = "M1", ModelPath = "m1", Placement = new ModelPlacement(scenario.StartsWith("multi", StringComparison.Ordinal) ? "pc" : "dgx"),
            Sources = [new ModelSource("org/m1", "m1")], Tags = ["chat"],
        };
        var inspector = new FakeHuggingFaceRepoInspector();
        inspector.Seed("org/gguf", new RepoInspection("org/gguf", null, RepoFormat.Gguf, [],
            [new GgufVariant("Q8_0", [new RepoFile("m-Q8_0.gguf", 4_279_660_224)], 4_279_660_224), new GgufVariant("Q4_K_M", [new RepoFile("m-Q4_K_M.gguf", 2_500_000_000)], 2_500_000_000)],
            [], [new RepoFile("config.json", 100)]));
        INodeRegistry? nodes = scenario.StartsWith("multi", StringComparison.Ordinal)
            ? new ListOnlyNodeRegistry(ListOnlyNodeRegistry.Node("dgx") with { Approved = true }, ListOnlyNodeRegistry.Node("pc") with { Approved = true })
            : null;
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(new FakeModelRegistry([model]));
        ctx.Services.AddSingleton<IModelRuntime>(new FakeModelRuntime());
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(128, 64)));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        ctx.Services.AddNodeSourcing(root);
        ctx.Services.AddSingleton<IHuggingFaceDownloader>(new FakeHuggingFaceDownloader());
        ctx.Services.AddSingleton<IHuggingFaceRepoInspector>(inspector);
        if (nodes is not null)
        {
            ctx.Services.AddSingleton(nodes);
        }

        var cut = scenario.EndsWith("edit", StringComparison.Ordinal) || scenario.EndsWith("edit-pc", StringComparison.Ordinal)
            ? ctx.Render<ModelEditor>(p => p.Add(x => x.Id, "m1").Add(x => x.LiveRefreshInterval, TimeSpan.FromHours(1)))
            : ctx.Render<ModelEditor>(p => p.Add(x => x.LiveRefreshInterval, TimeSpan.FromHours(1)));
        cut.WaitForElement("[data-testid=model-form]", Wait);
        switch (scenario)
        {
            case "single-add-gguf-repo":
                cut.ChangeAt("#source-download", "download");
                cut.Find("[data-testid=field-repo-id]").Change("org/gguf");
                cut.ClickAt("[data-testid=inspect-repo-button]");
                cut.WaitForElement("[data-testid=gguf-runtime-note]", Wait);
                break;
            case "multi-add-pc":
                cut.WaitForElement("[data-testid=placement-section]", Wait);
                cut.ChangeAt("[data-testid=field-node]", "pc");
                cut.WaitForElement("[data-testid=placement-runtime]", Wait);
                break;
            case "multi-edit-pc":
                cut.WaitForElement("[data-testid=placement-section]", Wait);
                break;
        }

        return cut.Markup;
    }

    private static string FixturePath(string name, [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", name);

    /// <summary>Leaves out what differs between runs: scope attributes, element references, handler ids, the dialog counter.</summary>
    private static string Normalize(string markup) =>
        DialogId().Replace(EventHandlerId().Replace(ElementReference().Replace(ScopeAttribute().Replace(markup, ""), ""), "$1=\"\""), "dialog-n");

    [System.Text.RegularExpressions.GeneratedRegex(@"dialog-\d+")]
    private static partial System.Text.RegularExpressions.Regex DialogId();

    [System.Text.RegularExpressions.GeneratedRegex(@"\sb-[a-z0-9]{10}(?:="""")?")]
    private static partial System.Text.RegularExpressions.Regex ScopeAttribute();

    [System.Text.RegularExpressions.GeneratedRegex(@"\sblazor:elementReference=""[^""]*""")]
    private static partial System.Text.RegularExpressions.Regex ElementReference();

    [System.Text.RegularExpressions.GeneratedRegex(@"(blazor:on\w+)=""\d+""")]
    private static partial System.Text.RegularExpressions.Regex EventHandlerId();
}
