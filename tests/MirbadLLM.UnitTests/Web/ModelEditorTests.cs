using AngleSharp.Html.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;
using FakeHttpMessageHandler = MirbadLLM.UnitTests.Orchestration.Fakes.FakeHttpMessageHandler;

#pragma warning disable CA1707 // underscore test names read better for the new folder/info-panel/custom-option scenarios below.

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// bUnit tests for the model editor page: Id-regex validation, create/save via the fake registry, folder
/// selection auto-fill, the info-panel toggle, and the max-model-len "Custom…" option.
/// </summary>
public sealed class ModelEditorTests : IDisposable
{
    /// <summary>How long an assertion waits for an async handler (save, download start) under a loaded full run.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private string CreateModelsRootWithFolder(string folder, string configJson, int weightsBytes = 1024)
    {
        string root = Path.Combine(Path.GetTempPath(), "mirbadllm-editor-tests-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(root);
        string dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "config.json"), configJson);
        File.WriteAllBytes(Path.Combine(dir, "model.safetensors"), new byte[weightsBytes]);
        return root;
    }

    private Bunit.BunitContext MakeContext(
        FakeModelRegistry registry, FakeModelRuntime? runtime = null, string? modelsRoot = null,
        FakeHuggingFaceRepoInspector? repoInspector = null, FakeHuggingFaceDownloader? downloader = null)
    {
        string root = modelsRoot ?? (_tempDirs.Count > 0 ? _tempDirs[^1] : CreateEmptyModelsRoot());

        var ctx = new Bunit.BunitContext();
        ctx.Services.AddSingleton<IModelRegistry>(registry);
        ctx.Services.AddSingleton<IModelRuntime>(runtime ?? new FakeModelRuntime());
        ctx.Services.AddSingleton<IMemoryProbe>(new FakeMemoryProbe(new MemorySnapshot(128, 64)));
        ctx.Services.AddSingleton(Options.Create(new MirbadLLMOptions()));
        ctx.Services.AddNodeSourcing(root);
        ctx.Services.AddSingleton<IHuggingFaceDownloader>(downloader ?? new FakeHuggingFaceDownloader());
        ctx.Services.AddSingleton<IHuggingFaceRepoInspector>(repoInspector ?? new FakeHuggingFaceRepoInspector());
        return ctx;
    }

    private string CreateEmptyModelsRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "mirbadllm-editor-tests-empty-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(root);
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task InvalidIdShowsValidationMessageAndDoesNotSave()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>();
        cut.Find("[data-testid=field-id]").Change("Not_A_Valid_Id");
        cut.Find("[data-testid=field-served-name]").Change("Some-Model");
        cut.Find("[data-testid=field-model-path]").Change("some/path");
        cut.Find("form").Submit();

        Assert.Contains("lowercase", cut.Markup);
        Assert.Empty(await registry.ListAsync(Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Wizard_LaterStepsStayLockedUntilTheStepsBeforeThemAreComplete()
    {
        using var ctx = MakeContext(new FakeModelRegistry());
        var cut = ctx.Render<ModelEditor>();

        // A fleet of one with no runtime choice starts at Checkpoint; nothing past it opens yet.
        Assert.Equal("step", cut.Find("[data-testid=wizard-step-checkpoint]").GetAttribute("aria-current"));
        foreach (string step in new[] { "identity", "memory", "tuning", "review" })
        {
            Assert.True(cut.Find($"[data-testid=wizard-step-{step}]").HasAttribute("disabled"), step);
        }

        Assert.True(cut.Find("[data-testid=wizard-next]").HasAttribute("disabled"));
        cut.Find("[data-testid=wizard-step-review]").Click();
        Assert.Equal("step", cut.Find("[data-testid=wizard-step-checkpoint]").GetAttribute("aria-current"));
    }

    [Fact]
    public void Wizard_NextChecksTheStepsFields_AndStepsOpenOnlyInOrder()
    {
        using var ctx = MakeContext(new FakeModelRegistry());
        var cut = ctx.Render<ModelEditor>();

        cut.Find("[data-testid=field-model-path]").Change("checkpoints/m");
        cut.Find("[data-testid=wizard-next]").Click();
        Assert.Equal("step", cut.Find("[data-testid=wizard-step-identity]").GetAttribute("aria-current"));
        Assert.True(cut.Find("[data-testid=wizard-step-review]").HasAttribute("disabled"));

        // An id that breaks its rule holds the step and says why.
        cut.Find("[data-testid=field-id]").Change("Bad_Id");
        cut.Find("[data-testid=field-served-name]").Change("M");
        cut.Find("[data-testid=wizard-next]").Click();
        Assert.Equal("step", cut.Find("[data-testid=wizard-step-identity]").GetAttribute("aria-current"));
        Assert.Contains("Fix the fields", cut.Find("[data-testid=wizard-next-hint]").TextContent, StringComparison.Ordinal);
        Assert.Contains("lowercase", cut.Markup, StringComparison.Ordinal);

        cut.Find("[data-testid=field-id]").Change("m");
        cut.Find("[data-testid=wizard-next]").Click();
        Assert.Equal("step", cut.Find("[data-testid=wizard-step-memory]").GetAttribute("aria-current"));
        cut.Find("[data-testid=wizard-next]").Click();
        cut.Find("[data-testid=wizard-next]").Click();
        Assert.Equal("step", cut.Find("[data-testid=wizard-step-review]").GetAttribute("aria-current"));

        // Clearing an earlier step's field shuts every step after it again.
        cut.Find("[data-testid=wizard-step-identity]").Click();
        cut.Find("[data-testid=field-served-name]").Change("");
        foreach (string step in new[] { "memory", "tuning", "review" })
        {
            Assert.True(cut.Find($"[data-testid=wizard-step-{step}]").HasAttribute("disabled"), step);
        }
    }

    [Fact]
    public async Task ValidSubmitSavesNewModelToRegistry()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>();
        cut.Find("[data-testid=field-id]").Change("new-model");
        cut.Find("[data-testid=field-served-name]").Change("New-Model");
        cut.Find("[data-testid=field-model-path]").Change("checkpoints/new-model");
        cut.Find("form").Submit();

        // SaveAsync is asynchronous: wait for the write instead of reading the registry right after the submit.
        cut.WaitForAssertion(() =>
        {
            var saved = registry.GetAsync("new-model", Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Assert.NotNull(saved);
            Assert.Equal("New-Model", saved!.ServedName);
            Assert.Equal("checkpoints/new-model", saved.ModelPath);
        }, Wait);
    }

    [Fact]
    public void DeleteButtonDisabledWhileModelIsRunning()
    {
        var model = new ModelDefinition { Id = "running-model", ServedName = "Running", ModelPath = "p" };
        var registry = new FakeModelRegistry([model]);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(model.Id, ModelState.Ready));
        using var ctx = MakeContext(registry, runtime);

        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));

        Assert.True(cut.Find("[data-testid=delete-button]").HasAttribute("disabled"));
    }

    // Qwen3.5-style hybrid config: 8 of 32 layers use full attention, 4 KV heads x 256 dims -> 16 KiB/token at fp8.
    private static string HybridConfigJson()
    {
        string layerTypes = string.Join(",", Enumerable.Range(0, 32).Select(i => i % 4 == 3 ? "\"full_attention\"" : "\"linear_attention\""));
        return $$"""
            { "architectures": ["Qwen3_5ForConditionalGeneration"],
              "text_config": { "num_hidden_layers": 32, "num_key_value_heads": 4, "head_dim": 256,
                               "max_position_embeddings": 262144, "layer_types": [{{layerTypes}}] } }
            """;
    }

    [Theory]
    [InlineData(262_144, 8.0, true)]    // 7 runtime + 4 KV > 8: the crash seen on the DGX, in miniature
    [InlineData(131_072, 12.0, false)]  // 7 runtime + 2 KV fits in 12
    public void BudgetBelowWhatTheContextNeeds_WarnsBeforeStarting(int maxModelLen, double budget, bool expectWarning)
    {
        string root = CreateModelsRootWithFolder("hybrid-model", HybridConfigJson());
        var model = new ModelDefinition
        {
            Id = "hybrid", ServedName = "Hybrid", ModelPath = "hybrid-model", MaxModelLen = maxModelLen, MemoryBudgetGiB = budget,
        };
        using var ctx = MakeContext(new FakeModelRegistry([model]), modelsRoot: root);

        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));

        Assert.Equal(expectWarning, cut.FindAll("[data-testid=memory-budget-too-small]").Count == 1);
    }

    [Fact]
    public void BudgetBelowTheMinimum_NoFitLineSaysItFits()
    {
        // 8 GiB at 262,144 tokens is below the model's minimum, while the node has 56 GiB free after the reserve: the
        // room is real, but the fit line must not call it a fit next to "vLLM will refuse to start".
        string root = CreateModelsRootWithFolder("hybrid-model", HybridConfigJson());
        var model = new ModelDefinition
        {
            Id = "hybrid", ServedName = "Hybrid", ModelPath = "hybrid-model", MaxModelLen = 262_144, MemoryBudgetGiB = 8,
        };
        using var ctx = MakeContext(new FakeModelRegistry([model]), modelsRoot: root);

        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));

        var hint = cut.Find("[data-testid=memory-budget-hint]");
        Assert.NotNull(hint.QuerySelector("[data-testid=memory-budget-too-small]"));
        Assert.Null(hint.QuerySelector(".budget-check--ok"));
        Assert.DoesNotContain("Fits right now", hint.TextContent, StringComparison.Ordinal);
        Assert.Contains("won't start", cut.Find("[data-testid=memory-budget-room-too-small]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Delete_SitsAloneInTheDangerZone_NotBesideSave()
    {
        var model = new ModelDefinition { Id = "old-model", ServedName = "Old", ModelPath = "Old-Checkpoint" };
        using var ctx = MakeContext(new FakeModelRegistry([model]));
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));

        Assert.NotNull(cut.Find("[data-testid=danger-zone] [data-testid=delete-button]"));
        Assert.Empty(cut.FindAll(".form-footer [data-testid=delete-button]"));
        Assert.NotNull(cut.Find(".form-footer [data-testid=save-button]"));
        // Edit mode carries the model header with Settings as the current tab.
        Assert.Equal("page", cut.Find("[data-testid=model-tab-settings]").GetAttribute("aria-current"));
    }

    [Fact]
    public void AddModel_HasItsOwnHeader_BackToModels()
    {
        using var ctx = MakeContext(new FakeModelRegistry());

        var cut = ctx.Render<ModelEditor>();

        Assert.Equal("Add model", cut.Find("[data-testid=page-title]").TextContent);
        Assert.Equal("models", cut.Find(".page-header-back").GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-testid=model-header]"));
        Assert.Empty(cut.FindAll("[data-testid=danger-zone]"));
    }

    [Fact]
    public void RunningModel_CountsItsOwnBudgetAsFree()
    {
        // 64 GiB available, 8 GiB reserve: 56 GiB free. The running model already holds 60 GiB,
        // which a restart releases first, so the same budget must read as fitting.
        var model = new ModelDefinition { Id = "big-model", ServedName = "Big", ModelPath = "p", MemoryBudgetGiB = 60 };
        var registry = new FakeModelRegistry([model]);
        var runtime = new FakeModelRuntime();
        runtime.SetStatus(new ModelStatus(model.Id, ModelState.Ready));
        using var ctx = MakeContext(registry, runtime);

        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));

        string hint = cut.Find("[data-testid=memory-budget-hint]").TextContent;
        Assert.Contains("Fits right now", hint);
        Assert.DoesNotContain("Won't fit", hint);
    }

    [Fact]
    public async Task Delete_AsksInAnInAppDialog_ThenDeletes()
    {
        var model = new ModelDefinition { Id = "old-model", ServedName = "Old", ModelPath = "Old-Checkpoint" };
        var registry = new FakeModelRegistry([model]);
        using var ctx = MakeContext(registry);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));
        cut.Find("[data-testid=delete-button]").Click();

        // Nothing is deleted until the dialog is confirmed; the dialog says the files stay on disk.
        Assert.NotNull(await registry.GetAsync(model.Id, Xunit.TestContext.Current.CancellationToken));
        var dialog = cut.Find("[data-testid=delete-dialog]");
        Assert.Equal("true", dialog.GetAttribute("data-open"));
        Assert.Contains("/models/Old-Checkpoint", dialog.TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=confirm-delete]").Click();

        cut.WaitForAssertion(() => Assert.Null(registry.GetAsync(model.Id, Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult()), Wait);
    }

    [Fact]
    public async Task Delete_OfAModelHeldByAJob_SaysSo_AndKeepsTheModel()
    {
        // The W7 review (MN-126) composes the ownership validator: a model a job holds cannot be deleted. The page must
        // show the refusal instead of failing the circuit.
        var ct = Xunit.TestContext.Current.CancellationToken;
        string dir = CreateEmptyModelsRoot();
        var options = Options.Create(new MirbadLLMOptions { DataPath = dir });
        using var store = new MirbadLLM.Orchestration.Multinode.OwnershipStore(options);
        using var registry = new JsonModelRegistry(
            options, Options.Create(new RegistryMigrationOptions()), [new MirbadLLM.Orchestration.Multinode.OwnershipValidator(store)], null);
        var model = new ModelDefinition { Id = "held-model", ServedName = "Held", ModelPath = "Held", Placement = new ModelPlacement("dgx") };
        await registry.UpsertAsync(model, ct);
        Assert.True((await store.TryAcquireAsync(new OwnershipRecord
        {
            Id = "job-1", Kind = OwnershipKind.Job, ModelId = model.Id, Stage = "benchmark", StartedAt = DateTimeOffset.UtcNow,
        }, ct)).Acquired);

        using var ctx = MakeContext(new FakeModelRegistry());
        ctx.Services.AddSingleton<IModelRegistry>(registry);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));
        cut.Find("[data-testid=delete-button]").Click();
        cut.Find("[data-testid=confirm-delete]").Click();

        cut.WaitForAssertion(() => Assert.Contains("held-model", cut.Find("[data-testid=save-error]").TextContent, StringComparison.Ordinal), Wait);
        Assert.NotNull(await registry.GetAsync(model.Id, ct));
    }

    [Fact]
    public void MemoryBudget_OverFreeMemory_StatesTheExactNumbers()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>();
        cut.Find("[data-testid=field-model-path]").Change("checkpoints/big-model");
        cut.Find("[data-testid=field-memory-budget]").Change("60");

        // 64 GiB available minus the default 8 GiB reserve = 56 GiB.
        var warning = cut.Find("[data-testid=memory-budget-warning]").TextContent;
        Assert.Contains("60.0 GiB", warning, StringComparison.Ordinal);
        Assert.Contains("56.0 GiB", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void PristineAddModel_HidesTheFitCheckUntilAPathExists()
    {
        // A fresh "Add model" page has no checkpoint yet, so the fit check has nothing honest to say:
        // it must stay hidden until a folder is chosen or a model path is typed, not flash "Won't fit"
        // against the default budget for a model that was never picked.
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>();

        Assert.Empty(cut.FindAll("[data-testid=memory-budget-warning]"));
        Assert.Empty(cut.FindAll(".budget-check--ok"));

        cut.Find("[data-testid=field-model-path]").Change("checkpoints/some-model");

        Assert.NotEmpty(cut.FindAll(".budget-check--ok"));
    }

    [Fact]
    public void EditingAnExistingModel_AlwaysShowsTheFitCheck()
    {
        var model = new ModelDefinition { Id = "existing-model", ServedName = "Existing", ModelPath = "p", MemoryBudgetGiB = 4 };
        var registry = new FakeModelRegistry([model]);
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));

        Assert.NotEmpty(cut.FindAll(".budget-check--ok"));
    }

    [Fact]
    public void KvCacheDtypeAndQuantizationSelects_HaveShortLabelsAndShowTheHintForTheSelection()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>();

        // Option text is short enough not to clip in a dropdown; the explanation moved to a field hint.
        var kvOptions = cut.FindAll("[data-testid=field-kv-cache-dtype] option");
        Assert.Contains(kvOptions, o => o.TextContent == "Auto");
        Assert.Contains(kvOptions, o => o.TextContent == "FP8");

        var quantOptions = cut.FindAll("[data-testid=field-quantization] option");
        Assert.Contains(quantOptions, o => o.TextContent == "None");
        Assert.Contains(quantOptions, o => o.TextContent == "FP8 (online)");

        // Default selection (KV cache dtype "fp8") shows its own explanation underneath.
        Assert.Contains("Halves KV cache memory vs. auto", cut.Find("[data-testid=field-kv-cache-dtype-hint]").TextContent);

        cut.Find("[data-testid=field-kv-cache-dtype]").Change("auto");
        Assert.Contains("Same dtype as the model weights", cut.Find("[data-testid=field-kv-cache-dtype-hint]").TextContent);

        cut.Find("[data-testid=field-quantization]").Change("fp8");
        Assert.Contains("Online quantization at load time", cut.Find("[data-testid=field-quantization-hint]").TextContent);
    }

    [Fact]
    public void EveryFormControlHasALabel()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>();
        cut.Find("[data-testid=source-download-tab] input").Change(true);

        var controls = cut.FindAll("input:not([type=hidden]), select, textarea");
        Assert.NotEmpty(controls);
        foreach (var control in controls)
        {
            string? id = control.GetAttribute("id");
            bool labelled = control.HasAttribute("aria-label")
                || control.Closest("label") is not null
                || (id is not null && cut.FindAll($"label[for='{id}']").Count > 0);
            Assert.True(labelled, $"Unlabelled control: {control.OuterHtml}");
        }
    }

    private const string Qwen3FolderConfig = """
        {
          "architectures": ["Qwen3ForCausalLM"],
          "dtype": "bfloat16",
          "max_position_embeddings": 32768,
          "num_hidden_layers": 4,
          "num_attention_heads": 8,
          "num_key_value_heads": 8,
          "head_dim": 64,
          "layer_types": ["full_attention", "full_attention", "full_attention", "full_attention"]
        }
        """;

    [Fact]
    public void SelectingFolder_AutoFillsServedNameModelPathAndParsers()
    {
        string root = CreateModelsRootWithFolder("test-folder", Qwen3FolderConfig);
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry, modelsRoot: root);

        var cut = ctx.Render<ModelEditor>();
        cut.Find("[data-testid=field-source-folder]").Change("test-folder");

        Assert.Equal("test-folder", ((IHtmlInputElement)cut.Find("[data-testid=field-model-path]")).Value);
        Assert.Equal("test-folder", ((IHtmlInputElement)cut.Find("[data-testid=field-served-name]")).Value);
        Assert.Equal("qwen3_coder", ((IHtmlSelectElement)cut.Find("[data-testid=field-tool-call-parser]")).Value);
        Assert.Equal("qwen3", ((IHtmlSelectElement)cut.Find("[data-testid=field-reasoning-parser]")).Value);
        Assert.Contains("Detected:", cut.Find("[data-testid=folder-inspection]").TextContent);
    }

    [Fact]
    public void InfoToggle_ShowsWhatWhyAndExampleSections()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>();

        // The browser opens the panel (native popover): the button targets it, the panel starts hidden.
        var toggle = cut.Find("[data-testid=info-toggle-field-id]");
        var panel = cut.Find("[data-testid=info-panel-field-id]");
        Assert.Equal(panel.Id, toggle.GetAttribute("popovertarget"));
        Assert.True(panel.HasAttribute("popover"));
        Assert.Contains("What it is", panel.TextContent);
        Assert.Contains("Why it matters", panel.TextContent);
        Assert.Contains("Example", panel.TextContent);
    }

    [Fact]
    public void MaxModelLenCustomOption_RevealsNumberInput()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>();
        Assert.Empty(cut.FindAll("[data-testid=field-max-model-len-custom]"));

        cut.Find("[data-testid=field-max-model-len]").Change("custom");

        Assert.Single(cut.FindAll("[data-testid=field-max-model-len-custom]"));
    }

    [Fact]
    public void InspectingAGgufRepo_ShowsTheVariantDropdownAndTheRuntimeNote()
    {
        var repoInspector = new FakeHuggingFaceRepoInspector();
        var q4 = new GgufVariant("Q4_K_M", [new RepoFile("model-Q4_K_M-00001-of-00001.gguf", 128_000_000_000)], 128_000_000_000);
        var q8 = new GgufVariant("Q8_0", [new RepoFile("model-Q8_0-00001-of-00001.gguf", 200_000_000_000)], 200_000_000_000);
        repoInspector.Seed("orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF",
            new RepoInspection("orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF", null, RepoFormat.Gguf, [], [q4, q8], [], []));
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry, repoInspector: repoInspector);

        var cut = ctx.Render<ModelEditor>();
        cut.Find("[data-testid=source-download-tab] input").Change(true);
        cut.Find("[data-testid=field-repo-id]").Change("orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF");
        cut.Find("[data-testid=inspect-repo-button]").Click();

        cut.WaitForAssertion(() => Assert.Contains("GGUF", cut.Find("[data-testid=repo-format]").TextContent), Wait);
        var options = cut.FindAll("[data-testid=field-gguf-variant] option");
        Assert.Equal(2, options.Count);
        Assert.Contains(options, o => o.TextContent.Contains("Q4_K_M") && o.TextContent.Contains("1 file"));
        Assert.Contains("MirbadLLM runs models with vLLM", cut.Find("[data-testid=gguf-runtime-note]").TextContent);

        // Picking the first variant sets Allow patterns to exactly its files.
        Assert.Equal("model-Q4_K_M-00001-of-00001.gguf", ((IHtmlInputElement)cut.Find("[data-testid=field-allow-patterns]")).Value);
    }

    [Fact]
    public void ZeroMatchDownloadFailure_ShowsTheErrorInline()
    {
        var downloader = new FakeHuggingFaceDownloader
        {
            ThrowOnStart = new ArgumentException(
                "No files match *.json, *.safetensors, *.jinja, *.txt, tokenizer*, *.model; this repo contains 52 .gguf files (GGUF format)."),
        };
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry, downloader: downloader);

        var cut = ctx.Render<ModelEditor>();
        cut.Find("[data-testid=source-download-tab] input").Change(true);
        cut.Find("[data-testid=field-repo-id]").Change("orcarouter/Qwen3.8-Flash-Next-Uncensored-GGUF");
        cut.Find("[data-testid=start-download-button]").Click();

        cut.WaitForElement("[data-testid=download-error]", Wait);
        var error = cut.Find("[data-testid=download-error]").TextContent;
        Assert.Contains("No files match", error);
        Assert.Contains("GGUF format", error);
        Assert.Empty(cut.FindAll("[data-testid=download-progress]"));
    }

    [Fact]
    public void DownloadRunningOnTheNode_IsShownAfterReload_AndCompletesIntoTheForm()
    {
        string root = CreateModelsRootWithFolder("Ornith-1.5-9B", HybridConfigJson());
        var downloader = new FakeHuggingFaceDownloader();
        downloader.Seed(new DownloadStatus("Ornith-1.5-9B", "ornith-ai/Ornith-1.5-9B", "main", DownloadState.Downloading, 512, 1024, null, null));
        using var ctx = MakeContext(new FakeModelRegistry(), modelsRoot: root, downloader: downloader);
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("models/new?source=download");

        // The page was refreshed while the node kept downloading: the editor picks the download up instead of an empty form.
        var cut = ctx.Render<ModelEditor>();
        cut.WaitForAssertion(() => Assert.Equal("Downloading", cut.Find("[data-testid=download-state]").TextContent), Wait);
        Assert.NotNull(cut.Find("[role=progressbar]"));
        Assert.Equal("ornith-ai/Ornith-1.5-9B", ((IHtmlInputElement)cut.Find("[data-testid=field-repo-id]")).Value);
        Assert.Equal("main", ((IHtmlInputElement)cut.Find("[data-testid=field-revision]")).Value);
        Assert.Equal("Ornith-1.5-9B", ((IHtmlInputElement)cut.Find("[data-testid=field-download-folder]")).Value);

        // The next poll tick finds it finished: the folder is applied as when the download was started on this page.
        downloader.Seed(new DownloadStatus("Ornith-1.5-9B", "ornith-ai/Ornith-1.5-9B", "main", DownloadState.Completed, 1024, 1024, null, null));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("Completed", cut.Find("[data-testid=download-state]").TextContent);
            Assert.Equal("Ornith-1.5-9B", ((IHtmlInputElement)cut.Find("[data-testid=field-model-path]")).Value);
        }, Wait);
        Assert.Equal("ornith-ai/Ornith-1.5-9B", ((IHtmlInputElement)cut.Find("[data-testid=field-source-repo]")).Value);
    }

    [Fact]
    public void FieldErrorsAndHints_AreWiredWithAriaDescribedBy_AndInvalidFieldsSaySo()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);
        var cut = ctx.Render<ModelEditor>();

        Assert.Equal("field-id-error", cut.Find("[data-testid=field-id]").GetAttribute("aria-describedby"));
        Assert.Equal("field-served-name-error", cut.Find("[data-testid=field-served-name]").GetAttribute("aria-describedby"));
        Assert.Equal("field-model-path-error", cut.Find("[data-testid=field-model-path]").GetAttribute("aria-describedby"));
        Assert.Equal("field-memory-budget-error memory-budget-hint", cut.Find("[data-testid=field-memory-budget]").GetAttribute("aria-describedby"));
        Assert.Equal("field-max-model-len-error", cut.Find("[data-testid=field-max-model-len]").GetAttribute("aria-describedby"));
        Assert.Equal("field-max-num-seqs-error", cut.Find("[data-testid=field-max-num-seqs]").GetAttribute("aria-describedby"));
        Assert.StartsWith("field-kv-cache-dtype-error", cut.Find("[data-testid=field-kv-cache-dtype]").GetAttribute("aria-describedby"), StringComparison.Ordinal);
        foreach (string id in new[] { "field-id-error", "field-served-name-error", "field-model-path-error", "field-memory-budget-error", "memory-budget-hint" })
        {
            Assert.NotNull(cut.Find($"#{id}"));
        }

        cut.Find("[data-testid=field-id]").Change("Not_A_Valid_Id");
        cut.Find("form").Submit();

        Assert.Equal("true", cut.Find("[data-testid=field-id]").GetAttribute("aria-invalid"));
        Assert.Contains("lowercase", cut.Find("#field-id-error").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewModel_PrefillsSuggestedTags_UntilTheyAreTouched()
    {
        string root = CreateModelsRootWithFolder("test-folder", Qwen3FolderConfig);
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry, modelsRoot: root);
        var cut = ctx.Render<ModelEditor>();

        cut.Find("[data-testid=field-source-folder]").Change("test-folder");

        // Qwen3 parsers: chat, reasoning and tool calling are suggested and, untouched, pressed.
        foreach (string tag in new[] { "chat", "reasoning", "tool-calling" })
        {
            var toggle = cut.Find($"[data-testid=tag-toggle-{tag}]");
            Assert.Equal("true", toggle.GetAttribute("aria-pressed"));
            Assert.Contains("suggested", toggle.TextContent, StringComparison.Ordinal);
        }

        Assert.Equal("false", cut.Find("[data-testid=tag-toggle-vision]").GetAttribute("aria-pressed"));

        cut.Find("[data-testid=field-id]").Change("new-model");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var saved = registry.GetAsync("new-model", Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Assert.Equal(["chat", "reasoning", "tool-calling"], saved!.Tags);
        }, Wait);
    }

    [Fact]
    public async Task Tags_ToggleAndCustomTags_AreSaved_AndSuggestionsAreNeverAppliedToAnExistingModel()
    {
        var model = new ModelDefinition { Id = "alpha", ServedName = "Alpha", ModelPath = "Alpha", Tags = ["vision"] };
        var registry = new FakeModelRegistry([model]);
        using var ctx = MakeContext(registry);
        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));

        // Suggested (default parsers) but not applied on an existing model.
        Assert.Contains("suggested", cut.Find("[data-testid=tag-toggle-chat]").TextContent, StringComparison.Ordinal);
        Assert.Equal("false", cut.Find("[data-testid=tag-toggle-chat]").GetAttribute("aria-pressed"));
        Assert.Equal("true", cut.Find("[data-testid=tag-toggle-vision]").GetAttribute("aria-pressed"));

        cut.Find("[data-testid=tag-toggle-vision]").Click();
        cut.Find("[data-testid=tag-toggle-code]").Click();
        cut.Find("[data-testid=field-tag-input]").Input("Long Context");
        cut.Find("[data-testid=field-tag-input]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        cut.Find("[data-testid=field-tag-input]").Input("!!!");
        cut.Find("[data-testid=tag-add]").Click();

        Assert.Contains("can't be a tag", cut.Find("[data-testid=tag-error]").TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=tag-custom-long-context]"));
        Assert.Equal("Remove tag long-context", cut.Find("[data-testid=tag-remove-long-context]").GetAttribute("aria-label"));

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var saved = registry.GetAsync("alpha", Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Assert.Equal(["code", "long-context"], saved!.Tags);
        }, Wait);
    }

    [Fact]
    public async Task Source_IsRecordedFromTheDownloadFlow_AndExtraCheckpointsAreSaved()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);
        var cut = ctx.Render<ModelEditor>();

        Assert.NotNull(cut.Find("[data-testid=no-source-hint]"));

        cut.Find("[data-testid=source-download-tab] input").Change(true);
        cut.Find("[data-testid=field-repo-id]").Change("ornith-ai/Ornith-1.5-9B");
        cut.Find("[data-testid=field-revision]").Change("main");
        cut.Find("[data-testid=start-download-button]").Click();

        // StartDownloadAsync is asynchronous: the source fields fill in once the download has started.
        cut.WaitForAssertion(() => Assert.Equal("ornith-ai/Ornith-1.5-9B", ((IHtmlInputElement)cut.Find("[data-testid=field-source-repo]")).Value), Wait);
        Assert.Equal("main", ((IHtmlInputElement)cut.Find("[data-testid=field-source-revision]")).Value);
        Assert.Empty(cut.FindAll("[data-testid=no-source-hint]"));

        // The download's own renders may still land: the race-free helpers retry a retired handler and wait for each one.
        cut.ClickAt("[data-testid=add-extra-source]");
        cut.WaitForElement("[data-testid=extra-source-repo-1]", Wait);
        cut.ChangeAt("[data-testid=extra-source-repo-1]", "Qwen/Qwen3-0.6B");
        cut.ChangeAt("[data-testid=extra-source-folder-1]", "Qwen3-0.6B");
        cut.ChangeAt("[data-testid=field-id]", "ornith");
        cut.ChangeAt("[data-testid=field-served-name]", "Ornith");
        cut.ChangeAt("[data-testid=field-model-path]", "Ornith-1.5-9B");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var saved = registry.GetAsync("ornith", Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Assert.NotNull(saved);
            Assert.Equal(
                [("ornith-ai/Ornith-1.5-9B", "Ornith-1.5-9B", "main"), ("Qwen/Qwen3-0.6B", "Qwen3-0.6B", (string?)null)],
                saved!.Sources.Select(s => (s.RepoId, s.Folder, s.Revision)));
            Assert.Null(saved.Sources[0].AllowPatterns); // the download defaults are not copied into the source
        }, Wait);
    }

    [Fact]
    public async Task Download_SendsThePickedSavedTokensValue_AndNeverRendersIt()
    {
        const string value = "hf_editorDownloadSecret0123456789";
        using var tokens = new JsonTokenStore(
            Options.Create(new MirbadLLMOptions { DataPath = CreateEmptyModelsRoot() }),
            new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), TimeProvider.System);
        var saved = await tokens.AddAsync("hf-personal", value, Xunit.TestContext.Current.CancellationToken);
        var downloader = new FakeHuggingFaceDownloader();
        using var ctx = MakeContext(new FakeModelRegistry(), downloader: downloader);
        ctx.Services.AddSingleton<ITokenStore>(tokens);
        var cut = ctx.Render<ModelEditor>();

        cut.Find("[data-testid=source-download-tab] input").Change(true);
        Assert.Contains("hf-personal (…6789)", cut.Find("[data-testid=field-token]").TextContent, StringComparison.Ordinal);
        cut.Find("[data-testid=field-repo-id]").Change("meta-llama/Llama-3.1-8B-Instruct");
        cut.Find("[data-testid=field-token]").Change(saved.Id);
        cut.Find("[data-testid=start-download-button]").Click();

        cut.WaitForAssertion(() => Assert.Equal(value, Assert.Single(downloader.StartCalls).Token), Wait);
        Assert.DoesNotContain(value, cut.Markup, StringComparison.Ordinal);

        // A token deleted after it was picked stops the next request with a message instead of sending none.
        await tokens.DeleteAsync(saved.Id, Xunit.TestContext.Current.CancellationToken);
        cut.ClickAt("[data-testid=inspect-repo-button]");
        cut.WaitForAssertion(() => Assert.Contains("The picked access token was deleted", cut.Markup, StringComparison.Ordinal), Wait);
    }

    [Fact]
    public async Task Source_HalfFilledExtraCheckpoint_BlocksSaving()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);
        var cut = ctx.Render<ModelEditor>();

        cut.Find("[data-testid=add-extra-source]").Click();
        cut.Find("[data-testid=extra-source-repo-1]").Change("Qwen/Qwen3-0.6B");
        cut.Find("[data-testid=field-id]").Change("m");
        cut.Find("[data-testid=field-served-name]").Change("M");
        cut.Find("[data-testid=field-model-path]").Change("M");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("repo id and a folder", cut.Find("[data-testid=save-error]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Null(await registry.GetAsync("m", Xunit.TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------ canonical configuration

    [Fact]
    public async Task ExtraArgs_OwnedFlags_AreListedLive_AndMovedIntoTheirFieldsOnSave()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>();
        cut.Find("[data-testid=field-id]").Change("lifted");
        cut.Find("[data-testid=field-served-name]").Change("Lifted");
        cut.Find("[data-testid=field-model-path]").Change("ckpt");
        cut.Find("[data-testid=field-extra-args]").Change(
            "--async-scheduling\n--speculative-config\n{\"method\":\"mtp\",\"num_speculative_tokens\":3,\"moe_backend\":\"triton\"}\n--enforce-eager");

        string notice = cut.Find("[data-testid=extra-args-lifted]").TextContent;
        Assert.Contains("--async-scheduling", notice, StringComparison.Ordinal);
        Assert.Contains("Async scheduling", notice, StringComparison.Ordinal);
        Assert.Contains("--speculative-config", notice, StringComparison.Ordinal);
        Assert.Contains("Speculative decoding", notice, StringComparison.Ordinal);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var saved = registry.GetAsync("lifted", Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult()!;
            Assert.True(saved.AsyncScheduling);
            Assert.Equal("""{"method":"mtp","moe_backend":"triton","num_speculative_tokens":3}""", saved.Speculative!.ToJson());
            Assert.Equal(["--enforce-eager"], saved.ExtraArgs);
        }, Wait);
    }

    [Fact]
    public void ExtraArgs_UnreadableOwnedFlag_IsShown_AndBlocksTheSave()
    {
        var registry = new FakeModelRegistry();
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>();
        cut.Find("[data-testid=field-id]").Change("bad");
        cut.Find("[data-testid=field-served-name]").Change("Bad");
        cut.Find("[data-testid=field-model-path]").Change("ckpt");
        cut.Find("[data-testid=field-extra-args]").Change("--max-num-seqs\nlots");

        Assert.Contains("--max-num-seqs", cut.Find("[data-testid=extra-args-problems]").TextContent, StringComparison.Ordinal);
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("--max-num-seqs", cut.Find("[data-testid=save-error]").TextContent, StringComparison.Ordinal), Wait);
    }

    [Fact]
    public async Task TypedFields_DraftModelAdvancedJsonTriStates_RoundTrip()
    {
        var model = new ModelDefinition
        {
            Id = "nemo",
            ServedName = "Nemo",
            ModelPath = "nemo",
            EnablePrefixCaching = null,
            AsyncScheduling = false,
            MambaCacheMode = "align",
            CompilationConfig = JsonFields.Parse("""{"mode":3}"""),
            Speculative = new SpeculativeDecoding("", 3) { DraftModel = "/models/draft", Extra = JsonFields.Parse("""{"moe_backend":"triton"}""") },
        };
        var registry = new FakeModelRegistry([model]);
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));
        Assert.Equal("/models/draft", cut.Find("[data-testid=field-speculative-draft-model]").GetAttribute("value"));
        Assert.Equal("""{"moe_backend":"triton"}""", cut.Find("[data-testid=field-speculative-extra]").GetAttribute("value"));
        Assert.Equal("align", cut.Find("[data-testid=field-mamba-cache-mode]").GetAttribute("value"));
        Assert.Equal("off", cut.Find("[data-testid=field-async-scheduling]").GetAttribute("value"));
        Assert.Equal("", cut.Find("[data-testid=field-prefix-caching]").GetAttribute("value"));

        cut.Find("[data-testid=field-prefix-caching]").Change("off");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var saved = registry.GetAsync("nemo", Xunit.TestContext.Current.CancellationToken).GetAwaiter().GetResult()!;
            Assert.False(saved.EnablePrefixCaching);
            Assert.Equal(model.Speculative, saved.Speculative);
            Assert.Equal(model.CompilationConfig, saved.CompilationConfig);
            Assert.False(saved.AsyncScheduling);
            Assert.Equal("align", saved.MambaCacheMode);
        }, Wait);
    }

    [Fact]
    public async Task SpeculativeAdvancedJson_MustBeAnObject()
    {
        var model = new ModelDefinition { Id = "spec", ServedName = "Spec", ModelPath = "spec", Speculative = new SpeculativeDecoding("mtp", 2) };
        var registry = new FakeModelRegistry([model]);
        using var ctx = MakeContext(registry);

        var cut = ctx.Render<ModelEditor>(parameters => parameters.Add(p => p.Id, model.Id));
        cut.Find("[data-testid=field-speculative-extra]").Change("[1,2]");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("JSON object", cut.Find("[data-testid=save-error]").TextContent, StringComparison.Ordinal), Wait);
        Assert.Equal(model, await registry.GetAsync("spec", Xunit.TestContext.Current.CancellationToken));
    }
}
