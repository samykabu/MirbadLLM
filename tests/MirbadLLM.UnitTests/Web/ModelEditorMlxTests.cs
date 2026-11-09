using System.Globalization;
using System.Text.Json;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.ModelEditor;
using MirbadLLM.Web.Components.Pages;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// The model editor for MLX (docs/macos/implementation-plan.md, Phase 3 "UI"). The page tests render the real editor for a
/// fleet of one approved node: <c>mac-mini</c>, an 8 GiB Apple Silicon node reporting an MLX native-process runtime with
/// versions, whose models root is a temporary folder read through the master's folder sourcing (the agent's own
/// <see cref="ModelFolderInspector"/>), so the estimate is computed from a real folder inspection.
/// </summary>
public sealed class ModelEditorMlxTests : IDisposable
{
    private const string Node = "mac-mini";
    private const string Folder = "Qwen3-0.6B-4bit";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    /// <summary>A Qwen3-style config: 4 layers × 8 KV heads × 64 dims, so the KV cache per token is known.</summary>
    private const string Config = """
        {
          "architectures": ["Qwen3ForCausalLM"],
          "dtype": "bfloat16",
          "max_position_embeddings": 40960,
          "num_hidden_layers": 4,
          "num_attention_heads": 8,
          "num_key_value_heads": 8,
          "head_dim": 64,
          "layer_types": ["full_attention", "full_attention", "full_attention", "full_attention"]
        }
        """;

    /// <summary>The MLX runtime the Mac reports (0.32.2 the verified default, 0.31.0 verified, 0.30.0 broken).</summary>
    private static readonly RuntimeCapability MlxRuntime = new()
    {
        Kind = RuntimeKind.Mlx,
        Delivery = LaunchDelivery.Process,
        Available = true,
        Versions =
        [
            new RuntimeVersion("0.32.2", null, Verified: true, Default: true),
            new RuntimeVersion("0.31.0", null, Verified: true),
            new RuntimeVersion("0.30.0", null, Verified: false),
        ],
    };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ss-mlx-editor-" + Guid.NewGuid().ToString("N"));

    public ModelEditorMlxTests()
    {
        string dir = Path.Combine(_root, Folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), Config);
        File.WriteAllBytes(Path.Combine(dir, "model.safetensors"), new byte[256 * 1024]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    // ================================================================ the runtime option

    [Fact]
    public void RuntimeOption_MlxIsOffered_OnlyOnANodeThatReportsIt()
    {
        using var ctx = Context(new FakeModelRegistry(), MacMini());
        var cut = Rendered(ctx.Render<ModelEditor>(p => Quiet(p)));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=placement-runtime-mlx]")), Wait);
        Assert.Empty(cut.FindAll("[data-testid=placement-runtime-llamacpp]")); // the Mac reports no llama.cpp here
        Assert.True(cut.Find("[data-testid=placement-runtime-vllm]").HasAttribute("checked"));
        Assert.Equal("mac-mini also runs MLX (native process).", Text(cut, "[data-testid=placement-runtime-hint]"));
        Assert.Equal("vLLM or MLX", Text(cut, "[data-testid=wizard-step-where] .wizard-step-hint"));

        cut.ChangeAt("[data-testid=placement-runtime-mlx]", "mlx");

        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid=placement-runtime-mlx]").HasAttribute("checked")), Wait);
        Assert.Equal("mlx_lm.server serves an MLX checkpoint folder, as a native process on mac-mini.", Text(cut, "[data-testid=placement-runtime-hint]"));
        Assert.NotNull(cut.Find("[data-testid=mlx-section]"));
        Assert.Empty(cut.FindAll("[data-testid=field-max-model-len]")); // the vLLM sections are gone
        Assert.Empty(cut.FindAll("[data-testid=field-cuda-graph-mode]"));
    }

    [Fact]
    public void RuntimeOption_NoMlxOnANodeThatDoesNotReportIt()
    {
        var pc = MacMini() with { Runtimes = [new RuntimeCapability { Kind = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Process, Available = true }] };
        using var ctx = Context(new FakeModelRegistry(), pc);
        var cut = Rendered(ctx.Render<ModelEditor>(p => Quiet(p)));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=placement-runtime-llamacpp]")), Wait);
        Assert.Empty(cut.FindAll("[data-testid=placement-runtime-mlx]"));
        Assert.DoesNotContain("MLX", cut.Find("[data-testid=placement-runtime]").TextContent, StringComparison.Ordinal);
        Assert.Equal("mac-mini also runs llama.cpp (native process).", Text(cut, "[data-testid=placement-runtime-hint]"));
    }

    [Fact]
    public void RuntimePicker_Offers_IsTrueForAnMlxNode_AndForAnMlxModel()
    {
        var mlxNode = new PlacementNode(NodeRecord(MacMini()), null);
        var bareNode = new PlacementNode(NodeRecord(MacMini() with { Runtimes = [] }), null);

        Assert.True(RuntimePicker.Offers(mlxNode, RuntimeKind.Vllm));
        Assert.False(RuntimePicker.Offers(bareNode, RuntimeKind.Vllm));
        Assert.True(RuntimePicker.Offers(bareNode, RuntimeKind.Mlx)); // an MLX model keeps its (locked) runtime
        Assert.False(RuntimePicker.Offers(null, RuntimeKind.Mlx));
        // A container MLX listing is not one the node can run: MLX is a native process.
        Assert.Null(MlxEditing.Runtime(MacMini() with { Runtimes = [MlxRuntime with { Delivery = LaunchDelivery.Container }] }));
    }

    // ================================================================ the wizard

    [Fact]
    public void Wizard_ForMlx_IsWhereCheckpointIdentityMlxSettingsReview_WithoutTuning_AndTheCheckpointNeedsTheFolder()
    {
        using var ctx = Context(new FakeModelRegistry(), MacMini());
        var cut = NewMlx(ctx);

        Assert.Equal(["where", "checkpoint", "identity", "memory", "review"], Steps(cut));
        Assert.Empty(cut.FindAll("[data-testid=wizard-step-tuning]"));
        Assert.Equal("MLX settings", Text(cut, "[data-testid=wizard-step-memory] .wizard-step-label"));
        Assert.Equal("Context, cache, budget", Text(cut, "[data-testid=wizard-step-memory] .wizard-step-hint"));

        cut.ClickAt("[data-testid=wizard-next]");
        cut.WaitForAssertion(() => Assert.Equal("step", cut.Find("[data-testid=wizard-step-checkpoint]").GetAttribute("aria-current")), Wait);
        Assert.Contains("mlx-community/Qwen3-0.6B-4bit", Text(cut, "[data-testid=mlx-checkpoint-hint]"), StringComparison.Ordinal);
        Assert.Contains("config.json", Text(cut, "[data-testid=mlx-checkpoint-hint]"), StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=gguf-picker]")); // no GGUF picker for MLX
        Assert.True(cut.Find("[data-testid=wizard-next]").HasAttribute("disabled"));
        Assert.Equal("Choose a checkpoint folder.", Text(cut, "[data-testid=wizard-next-hint]"));

        cut.ChangeAt("[data-testid=field-source-folder]", Folder);

        cut.WaitForAssertion(() => Assert.False(cut.Find("[data-testid=wizard-next]").HasAttribute("disabled")), Wait);
        Assert.Empty(cut.FindAll("[data-testid=gguf-picker]"));
        cut.ClickAt("[data-testid=wizard-next]");
        cut.WaitForAssertion(() => Assert.Equal("step", cut.Find("[data-testid=wizard-step-identity]").GetAttribute("aria-current")), Wait);
        Assert.Equal("qwen3-0-6b-4bit", cut.Find("[data-testid=field-id]").GetAttribute("value"));
        cut.ClickAt("[data-testid=wizard-next]");
        cut.WaitForAssertion(() => Assert.Equal("step", cut.Find("[data-testid=wizard-step-memory]").GetAttribute("aria-current")), Wait);
        Assert.Equal("Next: Review", cut.Find("[data-testid=wizard-next]").TextContent.Trim());
        cut.ClickAt("[data-testid=wizard-next]");

        cut.WaitForAssertion(() => Assert.Equal("step", cut.Find("[data-testid=wizard-step-review]").GetAttribute("aria-current")), Wait);
        string review = Squash(cut.Find("[data-testid=wizard-review]").TextContent);
        Assert.Contains("mac-mini · MLX", review, StringComparison.Ordinal);
        Assert.Contains("8,192 tokens per request × 4", review, StringComparison.Ordinal);
        Assert.DoesNotContain("Tuning", review, StringComparison.Ordinal);
    }

    [Fact]
    public void Wizard_ForSglang_HasNoTuningStep_NamesItsRuntime_AndSuggestsTheNodesImages()
    {
        var node = MacMini() with
        {
            Runtimes = [new RuntimeCapability { Kind = RuntimeKind.Sglang, Delivery = LaunchDelivery.Container, Available = true }],
            Images = [new ImageInfo { Ref = "lmsysorg/sglang:1" }, new ImageInfo { Ref = "mirbadllm/agent:1" }],
        };
        using var ctx = Context(new FakeModelRegistry(), node);
        var cut = Rendered(ctx.Render<ModelEditor>(p => Quiet(p)));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=placement-runtime-sglang]")), Wait);
        Assert.Equal("mac-mini also runs SGLang.", Text(cut, "[data-testid=placement-runtime-hint]"));
        Assert.Equal("vLLM or SGLang", Text(cut, "[data-testid=wizard-step-where] .wizard-step-hint"));

        cut.ChangeAt("[data-testid=placement-runtime-sglang]", "sglang");

        cut.WaitForAssertion(() => Assert.Equal(["where", "checkpoint", "identity", "memory", "review"], Steps(cut)), Wait);
        Assert.Equal("Container settings", Text(cut, "[data-testid=wizard-step-memory] .wizard-step-label"));
        Assert.Contains("--mem-fraction-static", Text(cut, "[data-testid=field-container-budget-hint]"), StringComparison.Ordinal);
        Assert.Equal(["lmsysorg/sglang:1"], cut.FindAll("[data-testid=field-image-suggestions] option").Select(o => o.GetAttribute("value")));
    }

    [Fact]
    public async Task Wizard_ForNim_SkipsTheCheckpoint_AndSavesTokenNamesNotValues()
    {
        const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        var node = MacMini() with { Runtimes = [new RuntimeCapability { Kind = RuntimeKind.Nim, Delivery = LaunchDelivery.Container, Available = true }] };
        var registry = new FakeModelRegistry();
        using var ctx = Context(registry, node);
        var cut = Rendered(ctx.Render<ModelEditor>(p => Quiet(p)));
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=placement-runtime-nim]")), Wait);

        cut.ChangeAt("[data-testid=placement-runtime-nim]", "nim");

        cut.WaitForAssertion(() => Assert.Equal(["where", "identity", "memory", "review"], Steps(cut)), Wait);
        cut.Find("[data-testid=field-id]").Change("llama-nim");
        cut.Find("[data-testid=field-served-name]").Change("meta/llama");
        cut.Find("[data-testid=field-image]").Change("nvcr.io/nim/a:1");
        cut.Find("[data-testid=field-image-digest]").Change(Digest);
        cut.Find("[data-testid=field-container-env]").Change("NIM_MODEL_PROFILE=fast");
        cut.Find("[data-testid=field-container-secret-env]").Change("NGC_API_KEY=ngc");
        cut.Find("[data-testid=field-registry-token]").Change("ngc");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.NotNull(registry.GetAsync("llama-nim", Ct).Result), Wait);
        var saved = (await registry.GetAsync("llama-nim", Ct))!;
        Assert.Equal("llama-nim", saved.ModelPath);
        Assert.Empty(saved.Sources);
        Assert.Equal("fast", saved.Container!.Env!["NIM_MODEL_PROFILE"]);
        Assert.Equal("ngc", saved.Container.SecretEnv!["NGC_API_KEY"]);
        Assert.Equal(new ContainerRegistryLogin("nvcr.io", "$oauthtoken", "ngc"), saved.Container.RegistryLogin);
    }

    // ================================================================ settings, estimate and save

    [Fact]
    public async Task NewMlxModel_ShowsTheEstimateFromTheFolder_AndSavesRuntimeMlxWithEverySetting()
    {
        var registry = new FakeModelRegistry();
        using var ctx = Context(registry, MacMini());
        var cut = NewMlx(ctx);
        cut.ChangeAt("[data-testid=field-source-folder]", Folder);

        // The estimate is MlxArgsBuilder.Estimate over the folder's inspection; a new model takes it as its budget.
        var inspection = new ModelFolderInspector(Options.Create(new ModelStorageOptions { ModelsLocalPath = _root })).Inspect(Folder)!;
        Assert.True(inspection.KvBytesPerToken > 0);
        var expected = MlxArgsBuilder.Estimate(new MlxSettings { ModelFolder = Folder }, inspection);
        cut.WaitForAssertion(() => Assert.Equal(UiFormat.GiB(expected.BudgetGiB), Text(cut, "[data-testid=mlx-estimate-budget]")), Wait);
        Assert.Equal(UiFormat.GiB(expected.KvGiB), Text(cut, "[data-testid=mlx-estimate-kv]"));
        Assert.Equal(UiFormat.GiB(0.5), Text(cut, "[data-testid=mlx-estimate-prompt-cache]"));
        Assert.Equal(UiFormat.GiB(MlxArgsBuilder.RuntimeOverheadGiB), Text(cut, "[data-testid=mlx-estimate-overhead]"));
        Assert.Equal(MlxEditing.Formula(expected), Text(cut, "[data-testid=mlx-estimate-formula]"));
        Assert.Matches(@"^1\.10 × \(\d+\.\d weights \+ \d+\.\d KV\) \+ 0\.5 prompt cache \+ 0\.5 overhead = \d+\.\d GiB$", Text(cut, "[data-testid=mlx-estimate-formula]"));
        Assert.Equal(Budget(expected.BudgetGiB), cut.Find("[data-testid=field-memory-budget]").GetAttribute("value"));

        // Twice the parallel requests: twice the KV cache, and the budget follows (it was not typed).
        cut.ChangeAt("[data-testid=mlx-parallel]", "8");
        var doubled = MlxArgsBuilder.Estimate(new MlxSettings { ModelFolder = Folder, Parallel = 8 }, inspection);
        Assert.Equal(2 * expected.KvGiB, doubled.KvGiB, 9);
        cut.WaitForAssertion(() => Assert.Equal(UiFormat.GiB(doubled.KvGiB), Text(cut, "[data-testid=mlx-estimate-kv]")), Wait);
        Assert.Equal(Budget(doubled.BudgetGiB), cut.Find("[data-testid=field-memory-budget]").GetAttribute("value"));

        // A typed budget stays; "Use estimate" puts the estimate back.
        cut.ChangeAt("[data-testid=field-memory-budget]", "6");
        cut.WaitForAssertion(() => Assert.Contains("Above the estimate", Text(cut, "[data-testid=memory-budget-vs-estimate]"), StringComparison.Ordinal), Wait);
        cut.ClickAt("[data-testid=mlx-use-estimate]");
        cut.WaitForAssertion(() => Assert.Equal(Budget(doubled.BudgetGiB), cut.Find("[data-testid=field-memory-budget]").GetAttribute("value")), Wait);
        Assert.Empty(cut.FindAll("[data-testid=memory-budget-vs-estimate]"));

        // A quantized KV cache serves one request at a time: the hint says so and the KV term counts one request at 4 bits.
        cut.ChangeAt("[data-testid=mlx-kv-bits]", "4");
        cut.WaitForAssertion(() => Assert.Contains("one request at a time", Text(cut, "[data-testid=mlx-kv-bits-hint]"), StringComparison.Ordinal), Wait);
        var quantized = MlxArgsBuilder.Estimate(new MlxSettings { ModelFolder = Folder, Parallel = 8, KvBits = 4 }, inspection);
        Assert.Equal(UiFormat.GiB(quantized.KvGiB), Text(cut, "[data-testid=mlx-estimate-kv]"));

        // Trust remote code warns what it does.
        Assert.Empty(cut.FindAll("[data-testid=mlx-trust-remote-code-warning]"));
        cut.ChangeAt("[data-testid=mlx-trust-remote-code]", true);
        cut.WaitForAssertion(() => Assert.Contains("runs Python code from the checkpoint folder as the agent's account", Text(cut, "[data-testid=mlx-trust-remote-code-warning]"), StringComparison.Ordinal), Wait);

        // The version picker: the node default, the verified pins, the broken one disabled.
        var versions = cut.FindAll("[data-testid=mlx-version] option");
        Assert.Equal(["Node default (0.32.2)", "0.32.2 · node default", "0.31.0", "0.30.0 · not verified"], versions.Select(o => o.TextContent.Trim()));
        Assert.True(versions[3].HasAttribute("disabled"));

        // An invalid chat template argument is flagged live and blocks the save.
        Input(cut, "[data-testid=mlx-chat-template-args]", "[1]");
        cut.WaitForAssertion(() => Assert.Contains("JSON object", Text(cut, "[data-testid=mlx-chat-template-args-error]"), StringComparison.Ordinal), Wait);
        Input(cut, "[data-testid=mlx-chat-template-args]", "{\"enable_thinking\": false}");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=mlx-chat-template-args-error]")), Wait);

        cut.ChangeAt("[data-testid=mlx-context]", "4096");
        cut.ChangeAt("[data-testid=mlx-prompt-cache]", "1.5");
        cut.ChangeAt("[data-testid=mlx-max-tokens]", "2048");
        cut.ChangeAt("[data-testid=mlx-draft-model]", "Qwen3-0.6B-4bit-draft");
        cut.ChangeAt("[data-testid=mlx-draft-tokens]", "2");
        cut.ChangeAt("[data-testid=mlx-adapter]", "adapters/support-lora");
        cut.ChangeAt("[data-testid=mlx-version]", "0.31.0");
        Input(cut, "[data-testid=mlx-extra-args]", "--temp 0.7\n--log-level DEBUG");
        cut.ChangeAt("[data-testid=mlx-autostart]", true);
        cut.ChangeAt("[data-testid=field-memory-budget]", "3.5");
        Assert.Empty(cut.FindAll("[data-testid=mlx-extra-args-refused]"));
        Submit(cut);

        ModelDefinition? saved = null;
        cut.WaitForAssertion(() => Assert.NotNull(saved = registry.GetAsync("qwen3-0-6b-4bit", Ct).GetAwaiter().GetResult()), Wait);
        Assert.Equal(RuntimeKind.Mlx, saved!.Runtime);
        Assert.Null(saved.LlamaCpp);
        Assert.Equal(Folder, saved.ModelPath);
        Assert.Equal(3.5, saved.MemoryBudgetGiB);
        Assert.True(saved.AutoStart);
        Assert.Equal(Node, saved.Placement!.NodeId);
        Assert.Null(saved.ToolCallParser); // no vLLM parsers on an MLX model
        Assert.Null(saved.ReasoningParser);
        Assert.Empty(saved.ExtraArgs);
        Assert.Equal(["chat"], saved.Tags);
        var s = saved.Mlx!;
        Assert.Equal(Folder, s.ModelFolder);
        Assert.Equal((4096, 8, 1.5), (s.ContextSize, s.Parallel, s.PromptCacheGiB));
        Assert.Equal(((int?)2048, (int?)4), (s.MaxTokens, s.KvBits));
        Assert.Equal("{\"enable_thinking\": false}", s.ChatTemplateArgs);
        Assert.True(s.TrustRemoteCode);
        Assert.Equal(("Qwen3-0.6B-4bit-draft", (int?)2, "adapters/support-lora"), (s.DraftModelFolder, s.NumDraftTokens, s.AdapterFolder));
        Assert.Equal("0.31.0", s.ExecutableVersion);
        Assert.Equal(["--temp", "0.7", "--log-level", "DEBUG"], s.ExtraArgs);
        Assert.Empty(MlxArgsBuilder.Problems(saved)); // launchable as saved
    }

    [Fact]
    public async Task RefusedExtraArgs_AreShownLive_AndBlockTheSave()
    {
        var registry = new FakeModelRegistry();
        using var ctx = Context(registry, MacMini());
        var cut = NewMlx(ctx);
        cut.ChangeAt("[data-testid=field-source-folder]", Folder);
        cut.WaitForElement("[data-testid=mlx-estimate]", Wait);

        Input(cut, "[data-testid=mlx-extra-args]", "--temp 0.7\n--host 0.0.0.0\n--decode-concurrency 32\n--hos x");

        cut.WaitForAssertion(() => Assert.Contains("--host --decode-concurrency --hos", Text(cut, "[data-testid=mlx-extra-args-refused]"), StringComparison.Ordinal), Wait);
        Assert.Equal("true", cut.Find("[data-testid=mlx-extra-args]").GetAttribute("aria-invalid"));
        foreach (string allowed in MlxFlags.AllowedInExtraArgs)
        {
            Assert.Contains(allowed, Text(cut, "[data-testid=mlx-extra-args-hint]"), StringComparison.Ordinal);
        }

        Submit(cut);

        cut.WaitForAssertion(() => Assert.Contains("--host --decode-concurrency --hos cannot be set there", Text(cut, "[data-testid=save-error]"), StringComparison.Ordinal), Wait);
        Assert.Empty(await registry.ListAsync(Ct));
    }

    // ================================================================ editing an MLX model

    [Fact]
    public async Task EditOfAnMlxModel_ShowsTheMlxSection_LocksTheRuntime_AndAnUnchangedSaveKeepsTheSettings()
    {
        var model = Stored();
        var registry = new FakeModelRegistry([model]);
        using var ctx = Context(registry, MacMini());
        var cut = ctx.Render<ModelEditor>(p => Quiet(p.Add(x => x.Id, model.Id)));
        cut.WaitForElement("[data-testid=mlx-section]", Wait);

        Assert.Empty(cut.FindAll("[data-testid=field-max-model-len]"));
        Assert.Empty(cut.FindAll("[data-testid=field-cuda-graph-mode]"));
        Assert.Contains("Every setting is an mlx_lm.server launch argument", cut.Markup, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=placement-runtime-mlx]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=placement-runtime-vllm]").HasAttribute("disabled"));
        Assert.Contains("The runtime is chosen when a model is added.", Text(cut, "[data-testid=placement-runtime-hint]"), StringComparison.Ordinal);
        Assert.Equal("16384", cut.Find("[data-testid=mlx-context]").GetAttribute("value"));
        Assert.Equal("2", cut.Find("[data-testid=mlx-parallel]").GetAttribute("value"));
        Assert.Equal("--temp 0.6", cut.Find("[data-testid=mlx-extra-args]").GetAttribute("value"));
        cut.WaitForElement("[data-testid=mlx-estimate]", Wait); // from the stored folder's inspection
        Assert.Equal("2.5", cut.Find("[data-testid=field-memory-budget]").GetAttribute("value")); // a stored budget is kept

        Submit(cut);

        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        cut.WaitForAssertion(() => Assert.NotEmpty(nav.History), Wait);
        var saved = (await registry.GetAsync(model.Id, Ct))!;
        Assert.Equal(JsonSerializer.Serialize(model.Mlx), JsonSerializer.Serialize(saved.Mlx));
        Assert.Equal(RuntimeKind.Mlx, saved.Runtime);
    }

    [Fact]
    public async Task EditOfARunningMlxModel_AsksToRestart_ForAnMlxSettingChange()
    {
        var model = Stored();
        var registry = new FakeModelRegistry([model]);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(model.Id, ModelState.Ready, HostPort: 8102));
        using var ctx = Context(registry, MacMini(), runtime);
        var cut = ctx.Render<ModelEditor>(p => Quiet(p.Add(x => x.Id, model.Id)));
        cut.WaitForElement("[data-testid=mlx-section]", Wait);

        cut.ChangeAt("[data-testid=mlx-parallel]", "6");
        Submit(cut);

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find("[data-testid=apply-dialog]").GetAttribute("data-open")), Wait);
        var changes = cut.FindAll("[data-testid=apply-changes] li").Select(li => Squash(li.TextContent)).ToList();
        Assert.Equal(["Parallel requests: 2 → 6"], changes);
        Assert.Equal(6, (await registry.GetAsync(model.Id, Ct))!.Mlx!.Parallel); // saved before asking
    }

    [Fact]
    public void RuntimeLabels_NameMlx_InTheHeaderTheListsAndTheModelPlaces()
    {
        var model = Stored();
        Assert.Equal("MLX", MirbadLLM.Web.Components.Ui.ModelHeader.RuntimeLabel(model.Runtime));
        Assert.Equal("MLX", MirbadLLM.Web.Components.Ui.FleetNodes.RuntimeLabel(model.Runtime));
        Assert.Equal("mac-mini · MLX", new ModelPlace(model.Id, model.ServedName, ModelState.Ready, Node, Node, RuntimeKind.Mlx, 16_384).Where);
    }

    // ================================================================ pure rules (MlxEditing)

    [Fact]
    public void Form_RoundTripsEveryStoredSetting()
    {
        var stored = new MlxSettings
        {
            ModelFolder = Folder, ContextSize = 4096, Parallel = 3, PromptCacheGiB = 0.25, MaxTokens = 1000, KvBits = 8,
            ChatTemplateArgs = "{\"enable_thinking\":false}", TrustRemoteCode = true, DraftModelFolder = "draft", NumDraftTokens = 4,
            AdapterFolder = "lora", ExecutableVersion = "0.31.0", ExtraArgs = ["--temp", "0.7", "--chat-template", "a b"],
        };

        var form = MlxForm.From(stored);

        Assert.Equal("8", form.KvBits);
        Assert.Equal("--temp 0.7\n--chat-template \"a b\"", form.ExtraArgsText);
        Assert.Equal(JsonSerializer.Serialize(stored), JsonSerializer.Serialize(form.ToSettings(Folder)));
        Assert.Null(new MlxForm().ToSettings(Folder).KvBits);
        Assert.Null(new MlxForm { DraftModelFolder = "  " }.ToSettings(Folder).DraftModelFolder);
    }

    [Fact]
    public void RestartNeeded_ListsMlxArgumentChanges_ButNotTheContextSize_AndNothingForOtherRuntimes()
    {
        var before = Stored();
        var after = before with
        {
            Mlx = before.Mlx! with { Parallel = 8, KvBits = 4, ChatTemplateArgs = "{\"enable_thinking\": true}", ExecutableVersion = "0.31.0", ExtraArgs = [] },
        };

        Assert.Equal(
            [
                "Parallel requests: 2 → 8", "KV cache bits: full precision → 4-bit",
                "Chat template arguments: {\"enable_thinking\":false} → {\"enable_thinking\":true}", "mlx-lm version: node default → 0.31.0",
                "MLX extra args: --temp 0.6 → none",
            ],
            MlxEditing.RestartNeeded(before, after).Select(c => c.ToString()));

        // The context only sizes the budget (mlx_lm.server does not enforce it), and whitespace in the JSON is no change.
        Assert.Empty(MlxEditing.RestartNeeded(before, before with { Mlx = before.Mlx! with { ContextSize = 4096, ChatTemplateArgs = "{ \"enable_thinking\" : false }" } }));

        var vllm = new ModelDefinition { Id = "m", ServedName = "m", ModelPath = "m" };
        Assert.Empty(MlxEditing.RestartNeeded(vllm, vllm with { MaxNumSeqs = 4 }));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("{\"enable_thinking\": false}", null)]
    [InlineData("[1, 2]", "Must be a JSON object, e.g. {\"enable_thinking\": false}.")]
    [InlineData("{oops", "Not valid JSON: write an object, e.g. {\"enable_thinking\": false}.")]
    public void ChatTemplateArgs_MustBeAJsonObject(string text, string? problem) =>
        Assert.Equal(problem, MlxEditing.ChatTemplateArgsProblem(text));

    [Fact]
    public void RefusedExtraArgs_AreEveryFlagNotAllowed_AndThePathTokens()
    {
        Assert.Equal(["--model", "--max-tok", "--pipeline", "{models}/x"],
            MlxEditing.RefusedExtraArgs("--temp 0.7\n--model x\n--max-tok 9\n--pipeline\n--top-p 0.9 {models}/x\n--min-p -0.5"));
        Assert.Empty(MlxEditing.RefusedExtraArgs("--temp 0.7\n--top-k 20"));
    }

    // ================================================================ helpers

    private static ModelDefinition Stored() => new()
    {
        Id = "qwen3-mlx",
        ServedName = "Qwen3 0.6B (MLX)",
        ModelPath = Folder,
        MemoryBudgetGiB = 2.5,
        Runtime = RuntimeKind.Mlx,
        ToolCallParser = null,
        ReasoningParser = null,
        Placement = new ModelPlacement(Node),
        Tags = ["chat"],
        Mlx = new MlxSettings
        {
            ModelFolder = Folder, ContextSize = 16_384, Parallel = 2, ChatTemplateArgs = "{\"enable_thinking\":false}", ExtraArgs = ["--temp", "0.6"],
        },
    };

    /// <summary>The Mac's capability report: unified 8 GiB, an MLX native-process runtime with versions.</summary>
    private static NodeCapabilities MacMini() => new()
    {
        Protocol = Protocol.Range,
        AgentVersion = "1.3.0",
        Os = new NodeOsInfo("macos", "macOS 15.5", "24.5"),
        Arch = "arm64",
        Memory = new NodeMemoryInfo(MemoryModel.Unified, 8),
        Features = [AgentFeatures.RuntimeMlxProcess],
        Gpus = [],
        Runtimes = [MlxRuntime],
    };

    private static NodeRecord NodeRecord(NodeCapabilities capabilities) =>
        ListOnlyNodeRegistry.Node(Node) with { Approved = true, LastCapabilities = capabilities };

    /// <summary>A master whose fleet is the one approved node (no Placement section), with the editor's own services.</summary>
    private BunitContext Context(FakeModelRegistry registry, NodeCapabilities capabilities, FakeModelRuntime? runtime = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddSingleton<IModelRegistry>(registry);
        ctx.Services.AddSingleton<IModelRuntime>(runtime ?? new FakeModelRuntime());
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(8, 6)));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        ctx.Services.AddNodeSourcing(_root);
        ctx.Services.AddSingleton<IHuggingFaceDownloader>(new FakeHuggingFaceDownloader());
        ctx.Services.AddSingleton<IHuggingFaceRepoInspector>(new FakeHuggingFaceRepoInspector());
        ctx.Services.AddSingleton<INodeRegistry>(new ListOnlyNodeRegistry(NodeRecord(capabilities)));
        return ctx;
    }

    /// <summary>Add model with the MLX runtime picked.</summary>
    private static IRenderedComponent<ModelEditor> NewMlx(BunitContext ctx)
    {
        var cut = Rendered(ctx.Render<ModelEditor>(p => Quiet(p)));
        cut.WaitForElement("[data-testid=placement-runtime-mlx]", Wait);
        cut.ChangeAt("[data-testid=placement-runtime-mlx]", "mlx");
        cut.WaitForElement("[data-testid=mlx-section]", Wait);
        return cut;
    }

    private static IRenderedComponent<ModelEditor> Rendered(IRenderedComponent<ModelEditor> cut)
    {
        cut.WaitForElement("[data-testid=model-form]", Wait);
        return cut;
    }

    private static ComponentParameterCollectionBuilder<ModelEditor> Quiet(ComponentParameterCollectionBuilder<ModelEditor> p) =>
        p.Add(x => x.LiveRefreshInterval, TimeSpan.FromHours(1));

    private static List<string> Steps(IRenderedComponent<ModelEditor> cut) =>
        [.. cut.FindAll("[data-testid=wizard-steps] .wizard-step-btn").Select(b => b.GetAttribute("data-testid")!["wizard-step-".Length..])];

    private static string Text(IRenderedComponent<ModelEditor> cut, string selector) => Squash(cut.Find(selector).TextContent);

    private static string Squash(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    private static string Budget(double gib) => gib.ToString("0.##", CultureInfo.InvariantCulture);

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
    private static void Input(IRenderedComponent<ModelEditor> cut, string selector, string value)
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
}
