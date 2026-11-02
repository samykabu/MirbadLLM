using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;

#pragma warning disable CA1707 // underscore test names.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>The macOS Phase 3 fixtures: the M2 Mac as it reports itself with mlx-lm 0.32.2, and a 4-bit Qwen3 MLX model on it.</summary>
internal static class MlxFixtures
{
    public const string NodeId = "mac-1";
    public const string Version = "0.32.2";

    public static NodeRecord Mac(IReadOnlyList<RuntimeVersion>? versions = null, IReadOnlyList<string>? features = null)
    {
        var pc = LlamaCppFixtures.Node();
        return pc with
        {
            Id = NodeId,
            Uid = "uid-mac-0001",
            Address = "https://192.0.2.176:9443",
            MemoryReserveGiB = 1,
            GpuReserveGiB = new Dictionary<string, double>(),
            LastCapabilities = pc.LastCapabilities! with
            {
                Os = new NodeOsInfo("macos", "macOS 27.0.1"),
                Memory = new NodeMemoryInfo(MemoryModel.Unified, 8, GpuWorkingSetGiB: 5.33),
                Gpus = [],
                Features = features ??
                    [AgentFeatures.RuntimeLlamaCppProcess, AgentFeatures.RuntimeMlxProcess, AgentFeatures.LaunchEnv, AgentFeatures.DownloadsHfNative],
                Runtimes =
                [
                    new RuntimeCapability
                    {
                        Kind = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Process, Available = true,
                        Versions = [new RuntimeVersion("b11476", null, true, true)],
                    },
                    new RuntimeCapability
                    {
                        Kind = RuntimeKind.Mlx, Delivery = LaunchDelivery.Process, Available = true,
                        Versions = versions ?? [new RuntimeVersion(Version, null, true, true), new RuntimeVersion("0.31.0", null, false, false)],
                    },
                ],
            },
        };
    }

    public static ModelDefinition Model() => new()
    {
        Id = "qwen3-0-6b-mlx",
        ServedName = "qwen3-0.6b-mlx",
        ModelPath = "Qwen3-0.6B-4bit",
        MemoryBudgetGiB = 2.5,
        Runtime = RuntimeKind.Mlx,
        Mlx = new MlxSettings { ModelFolder = "Qwen3-0.6B-4bit" },
        Placement = new ModelPlacement(NodeId),
    };
}

/// <summary>
/// macOS Phase 3 (docs/macos/implementation-plan.md): the master's MLX side. The <c>mlx_lm.server</c> argv is canonical
/// and refuses every flag it does not know by its exact spelling (argparse would expand <c>--hos</c> to <c>--host</c>),
/// the estimate follows the folder's weights and KV size, the read-back inverts the builder, the launch spec is a
/// process spec with no GPUs for a node reporting <c>runtime.mlx.process</c>, and the registry refuses MLX on nodes that
/// do not run it (the DGX, the Windows PC) with a clear reason.
/// </summary>
public sealed class MlxTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-mlx-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = [];

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

    // ---------------------------------------------------------------- argv

    [Fact]
    public void Build_Defaults_IsTheCanonicalArgv()
    {
        string[] expected =
        [
            "--model", "{models}/Qwen3-0.6B-4bit",
            "--host", "127.0.0.1", "--port", "{port}",
            "--decode-concurrency", "4", "--prompt-concurrency", "4",
            "--prompt-cache-bytes", "536870912",
        ];

        Assert.Equal(expected, MlxArgsBuilder.Build(MlxFixtures.Model()));
    }

    [Fact]
    public void Build_EverySetting_InOrder_ExtraArgsLast_ChatTemplateArgsCompacted()
    {
        var model = MlxFixtures.Model() with
        {
            Mlx = new MlxSettings
            {
                ModelFolder = "Qwen3-4B-4bit",
                Parallel = 16,
                PromptCacheGiB = 1.25,
                MaxTokens = 2048,
                KvBits = 8,
                ChatTemplateArgs = "{ \"enable_thinking\" : false }",
                TrustRemoteCode = true,
                DraftModelFolder = "Qwen3-0.6B-4bit",
                NumDraftTokens = 4,
                AdapterFolder = "adapters/qwen-lora",
                ExtraArgs = ["--temp", "0.6", "--top-k", "-1", "--log-level", "WARNING"],
            },
        };

        string[] expected =
        [
            "--model", "{models}/Qwen3-4B-4bit",
            "--adapter-path", "{models}/adapters/qwen-lora",
            "--draft-model", "{models}/Qwen3-0.6B-4bit", "--num-draft-tokens", "4",
            "--host", "127.0.0.1", "--port", "{port}",
            "--decode-concurrency", "16", "--prompt-concurrency", "8",
            "--prompt-cache-bytes", "1342177280",
            "--kv-bits", "8",
            "--max-tokens", "2048",
            "--chat-template-args", "{\"enable_thinking\":false}",
            "--trust-remote-code",
            "--temp", "0.6", "--top-k", "-1", "--log-level", "WARNING",
        ];

        Assert.Equal(expected, MlxArgsBuilder.Build(model));
    }

    [Theory]
    [InlineData("--host")] // owned
    [InlineData("--port=9000")]
    [InlineData("--model")]
    [InlineData("--max-tokens")]
    [InlineData("--trust-remote-code")]
    [InlineData("--hos")] // argparse abbreviations of owned flags
    [InlineData("--mod")]
    [InlineData("--draft")]
    [InlineData("--pipeline")] // refused outright
    [InlineData("-h")]
    [InlineData("--help")]
    [InlineData("--temperature")] // not a flag of the pinned server
    [InlineData("{models}/x")] // path tokens are the builder's
    [InlineData("{port}")]
    public void RefusedExtraArgs_AnythingButTheAllowedFlagsSpelledExactly(string arg)
    {
        Assert.Equal([arg], MlxFlags.RefusedExtraArgs(["--temp", "0.2", arg]));

        var model = MlxFixtures.Model() with { Mlx = MlxFixtures.Model().Mlx! with { ExtraArgs = ["--temp", "0.2", arg] } };
        var problem = Assert.Single(MlxArgsBuilder.Problems(model));
        Assert.Equal(PlacementProblemCodes.MlxSettingsInvalid, problem.Code);
        Assert.Contains(arg, problem.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => MlxArgsBuilder.Build(model));
    }

    [Fact]
    public void AllowedExtraArgs_AndNegativeNumbers_Pass()
    {
        string[] extra = ["--temp", "0.7", "--top-p=0.9", "--min-p", "-0.0", "--top-k", "-1", "--chat-template", "{{ messages }}", "--allowed-origins", "*"];
        Assert.Empty(MlxFlags.RefusedExtraArgs(extra));
    }

    [Theory]
    [InlineData("ModelFolder", "../escape")]
    [InlineData("ModelFolder", "/abs/path")]
    [InlineData("ModelFolder", "")]
    [InlineData("DraftModelFolder", "a/../b")]
    [InlineData("AdapterFolder", "C:\\x")]
    public void Problems_FoldersMustBePlainRelativePaths(string field, string value)
    {
        var settings = MlxFixtures.Model().Mlx!;
        settings = field switch
        {
            "ModelFolder" => settings with { ModelFolder = value },
            "DraftModelFolder" => settings with { DraftModelFolder = value },
            _ => settings with { AdapterFolder = value },
        };

        var problem = Assert.Single(MlxArgsBuilder.Problems(MlxFixtures.Model() with { Mlx = settings }));
        Assert.Equal(PlacementProblemCodes.MlxSettingsInvalid, problem.Code);
        Assert.Contains("must be a relative path inside the models folder", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Problems_RangesJsonAndDraftTokens()
    {
        var bad = MlxFixtures.Model().Mlx! with
        {
            ContextSize = 0,
            Parallel = 65,
            PromptCacheGiB = -1,
            MaxTokens = 0,
            KvBits = 6,
            NumDraftTokens = 3,
            ChatTemplateArgs = "[1,2]",
            ExecutableVersion = "latest",
        };

        var messages = MlxArgsBuilder.Problems(MlxFixtures.Model() with { Mlx = bad }).Select(p => p.Message).ToList();

        Assert.Equal(8, messages.Count);
        Assert.Contains("The context size must be 1 to 1048576 tokens per request (is 0).", messages);
        Assert.Contains("Parallel requests must be 1 to 64 (is 65).", messages);
        Assert.Contains("The prompt cache must be 0 to 1024 GiB (is -1).", messages);
        Assert.Contains("The default answer length must be at least 1 token (is 0).", messages);
        Assert.Contains("The KV cache bits must be 4 or 8 (is 6).", messages);
        Assert.Contains("Draft tokens are set without a draft model folder.", messages);
        Assert.Contains("The chat template arguments must be a JSON object, e.g. {\"enable_thinking\": false}.", messages);
        Assert.Contains("The mlx-lm version 'latest' is not a version number (e.g. 0.32.2).", messages);
    }

    [Fact]
    public void Problems_WithoutSettings_IsAMismatch()
    {
        var problem = Assert.Single(MlxArgsBuilder.Problems(MlxFixtures.Model() with { Mlx = null }));
        Assert.Equal(PlacementProblemCodes.MlxSettingsMismatch, problem.Code);
    }

    // ---------------------------------------------------------------- estimate

    [Fact]
    public void Estimate_WeightsPlus16BitKvForContextTimesParallel_PlusPromptCacheAndOverhead()
    {
        // Qwen3-0.6B: 28 layers × 8 KV heads × 128 head dim × 2 (K and V) = 57 344 bytes/token at 8 bits.
        var folder = Inspection(weightsGiB: 0.31, kvBytesPerToken: 57_344);
        var estimate = MlxArgsBuilder.Estimate(new MlxSettings { ModelFolder = "x", ContextSize = 8192, Parallel = 4 }, folder);

        double kv = 57_344.0 * 2 * 8192 * 4 / 1_073_741_824.0; // 3.5 GiB at 16 bits
        Assert.Equal(kv, estimate.KvGiB, 6);
        Assert.Equal(0.5, estimate.PromptCacheGiB);
        Assert.Equal(MlxArgsBuilder.RuntimeOverheadGiB, estimate.OverheadGiB);
        Assert.Equal(Math.Round((1.10 * (0.31 + kv)) + 0.5 + 0.5, 2), estimate.BudgetGiB);
    }

    [Fact]
    public void Estimate_QuantizedKv_CountsOneRequestAtItsBits()
    {
        var folder = Inspection(weightsGiB: 2.2, kvBytesPerToken: 73_728);
        var estimate = MlxArgsBuilder.Estimate(new MlxSettings { ModelFolder = "x", ContextSize = 32_768, Parallel = 8, KvBits = 4 }, folder);

        Assert.Equal(73_728.0 * 0.5 * 32_768 / 1_073_741_824.0, estimate.KvGiB, 6); // mlx-lm serves one request at a time then
    }

    // ---------------------------------------------------------------- read-back

    [Fact]
    public void Parse_InvertsBuild_ForEverySetting()
    {
        var model = MlxFixtures.Model() with
        {
            MemoryBudgetGiB = 3,
            Mlx = new MlxSettings
            {
                ModelFolder = "Qwen3-4B-4bit",
                ContextSize = 16_384,
                Parallel = 2,
                PromptCacheGiB = 0.3,
                MaxTokens = 1024,
                KvBits = 4,
                ChatTemplateArgs = "{\"enable_thinking\": false}",
                TrustRemoteCode = true,
                DraftModelFolder = "draft",
                NumDraftTokens = 2,
                AdapterFolder = "lora",
                ExtraArgs = ["--temp", "0.2"],
            },
        };
        var argv = MlxArgsBuilder.Build(model);

        var parsed = MlxArgsParser.Parse(argv, model with { Mlx = model.Mlx! with { ExtraArgs = [] } });

        Assert.Equal(model.Mlx, parsed.Mlx);
        Assert.Equal(argv, MlxArgsBuilder.Build(parsed));
    }

    [Fact]
    public void Parse_LaunchConfig_ReadsTheBudgetAndKeepsNoPinForTheNodeDefault()
    {
        var model = MlxFixtures.Model();
        var launch = new LaunchConfig
        {
            Kind = LaunchDelivery.Process,
            Executable = ExecutableNames.MlxLmServer,
            ExecutableVersion = MlxFixtures.Version,
            Argv = MlxArgsBuilder.Build(model with { Mlx = model.Mlx! with { Parallel = 8 } }),
            Labels = new Dictionary<string, string> { [ContainerLabels.BudgetGiB] = "3.75" },
        };

        var parsed = MlxArgsParser.Parse(launch, model, MlxFixtures.Mac());

        Assert.Equal(3.75, parsed.MemoryBudgetGiB);
        Assert.Equal(8, parsed.Mlx!.Parallel);
        Assert.Null(parsed.Mlx.ExecutableVersion); // the node's default: the template pins none

        var pinned = MlxArgsParser.Parse(launch with { ExecutableVersion = "0.31.0" }, model, MlxFixtures.Mac());
        Assert.Equal("0.31.0", pinned.Mlx!.ExecutableVersion);
    }

    // ---------------------------------------------------------------- launch spec

    [Fact]
    public void BuildMlx_IsAProcessSpec_WithoutGpus_AtTheNodesDefaultVersion()
    {
        var model = MlxFixtures.Model();
        var built = LaunchSpecBuilder.Build(model, MlxFixtures.Mac(), "ignored-image", totalGiB: 8, tuning: false);
        var spec = built.Spec;

        Assert.Equal(LaunchDelivery.Process, spec.Kind);
        Assert.Equal(RuntimeKind.Mlx, spec.Runtime);
        Assert.Equal(ExecutableNames.MlxLmServer, spec.Executable);
        Assert.Equal(MlxFixtures.Version, spec.ExecutableVersion);
        Assert.Null(spec.Image);
        Assert.Empty(spec.Mounts);
        Assert.Equal(GpuSelectionMode.All, spec.Gpus.Mode);
        Assert.Equal("/health", spec.HealthPath);
        Assert.Equal(MlxArgsBuilder.Build(model), spec.Argv);
        Assert.Equal(string.Empty, built.ConfigHash);
        Assert.Equal(
            new Dictionary<string, string>
            {
                [ContainerLabels.ServedName] = "qwen3-0.6b-mlx",
                [ContainerLabels.ExecutionFingerprint] = built.ExecutionFingerprint,
                [ContainerLabels.BudgetGiB] = "2.5",
            },
            spec.Labels);

        // Any argv or version change is a new fingerprint ("restart pending").
        var more = LaunchSpecBuilder.BuildMlx(model with { Mlx = model.Mlx! with { Parallel = 8 } }, MlxFixtures.Mac(), tuning: false);
        var pinned = LaunchSpecBuilder.BuildMlx(model with { Mlx = model.Mlx! with { ExecutableVersion = "0.31.0" } }, MlxFixtures.Mac(), tuning: false);
        Assert.NotEqual(built.ExecutionFingerprint, more.ExecutionFingerprint);
        Assert.NotEqual(built.ExecutionFingerprint, pinned.ExecutionFingerprint);
        Assert.Equal("0.31.0", pinned.Spec.ExecutableVersion);
    }

    [Fact]
    public void BuildMlx_OnANodeWithoutMlx_IsRefusedWithTheReason()
    {
        var pc = LlamaCppFixtures.Node();
        var ex = Assert.Throws<NotSupportedException>(() =>
            LaunchSpecBuilder.Build(MlxFixtures.Model() with { Placement = new ModelPlacement(pc.Id) }, pc, "img", 127, tuning: false));
        Assert.Contains("node 'win-1' cannot run MLX (feature 'runtime.mlx.process'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMlx_WithoutAVerifiedDefault_NamesTheVersions()
    {
        var mac = MlxFixtures.Mac(versions: [new RuntimeVersion("0.31.0", null, false, true)]);
        var ex = Assert.Throws<NotSupportedException>(() => LaunchSpecBuilder.BuildMlx(MlxFixtures.Model(), mac, tuning: false));
        Assert.Contains("pins no mlx-lm-server version and node 'mac-1' reports no verified default version (0.31.0 unverified default)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("install or repair MLX on the node", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PYTHONPATH")]
    [InlineData("pythonstartup")]
    [InlineData("HF_HUB_OFFLINE")]
    [InlineData("HF_ENDPOINT")]
    [InlineData("TRANSFORMERS_CACHE")]
    [InlineData("DYLD_INSERT_LIBRARIES")]
    [InlineData("HOME")]
    [InlineData("PATH")]
    public void BuildMlx_RefusesEnvironmentTheAgentFixes(string name)
    {
        var environment = new LaunchEnvironment(new Dictionary<string, string>(StringComparer.Ordinal) { [name] = "x" }, []);
        var ex = Assert.ThrowsAny<ArgumentException>(() =>
            LaunchSpecBuilder.BuildMlx(MlxFixtures.Model(), MlxFixtures.Mac(), tuning: false, environment));
        Assert.Contains(name, ex.Message, StringComparison.Ordinal);

        var ok = new LaunchEnvironment(new Dictionary<string, string>(StringComparer.Ordinal) { ["MLX_METAL_DEBUG"] = "0" }, []);
        Assert.Equal("0", LaunchSpecBuilder.BuildMlx(MlxFixtures.Model(), MlxFixtures.Mac(), tuning: false, ok).Spec.Env["MLX_METAL_DEBUG"]);
    }

    // ---------------------------------------------------------------- placement

    [Fact]
    public async Task AnMlxModel_IsAcceptedOnTheMac()
    {
        var registry = await RegistryAsync(MlxFixtures.Mac(), LlamaCppFixtures.Node());

        await registry.UpsertAsync(MlxFixtures.Model(), Ct);

        var saved = (await registry.GetAsync(MlxFixtures.Model().Id, Ct))!;
        Assert.Equal(RuntimeKind.Mlx, saved.Runtime);
        Assert.Equal(MlxFixtures.Model().Mlx, saved.Mlx);
    }

    [Fact]
    public async Task AnMlxModel_IsRefusedOnANodeWithoutMlx_WithAClearReason()
    {
        var registry = await RegistryAsync(MlxFixtures.Mac(), LlamaCppFixtures.Node());

        var refused = await Assert.ThrowsAsync<PlacementValidationException>(() =>
            registry.UpsertAsync(MlxFixtures.Model() with { Placement = new ModelPlacement(LlamaCppFixtures.NodeId, [LlamaCppFixtures.Rtx5060Ti]) }, Ct));

        var problem = Assert.Single(refused.Problems, p => p.Code == PlacementProblemCodes.RuntimeUnavailable);
        Assert.StartsWith("Node 'win-1' cannot run MLX as a native process (its last report lists llama.cpp native process", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MlxSettingsMismatch_AndInvalidSettings_AreRefusedOnSave()
    {
        var registry = await RegistryAsync(MlxFixtures.Mac());

        var missing = await Assert.ThrowsAsync<PlacementValidationException>(() => registry.UpsertAsync(MlxFixtures.Model() with { Mlx = null }, Ct));
        Assert.Equal(PlacementProblemCodes.MlxSettingsMismatch, Assert.Single(missing.Problems).Code);

        var vllmWithMlx = MlxFixtures.Model() with { Runtime = RuntimeKind.Vllm };
        var stray = await Assert.ThrowsAsync<PlacementValidationException>(() => registry.UpsertAsync(vllmWithMlx, Ct));
        Assert.Contains(stray.Problems, p => p.Code == PlacementProblemCodes.MlxSettingsMismatch);

        var invalid = MlxFixtures.Model() with { Mlx = MlxFixtures.Model().Mlx! with { ExtraArgs = ["--hos", "0.0.0.0"] } };
        var bad = await Assert.ThrowsAsync<PlacementValidationException>(() => registry.UpsertAsync(invalid, Ct));
        Assert.Equal(PlacementProblemCodes.MlxSettingsInvalid, Assert.Single(bad.Problems).Code);

        // Checked again on a settings-only edit of a saved model.
        await registry.UpsertAsync(MlxFixtures.Model(), Ct);
        var saved = (await registry.GetAsync(MlxFixtures.Model().Id, Ct))!;
        var edit = await Assert.ThrowsAsync<PlacementValidationException>(() =>
            registry.UpsertAsync(saved with { Mlx = saved.Mlx! with { KvBits = 3 } }, Ct));
        Assert.Equal(PlacementProblemCodes.MlxSettingsInvalid, Assert.Single(edit.Problems).Code);
    }

    [Fact]
    public async Task APinnedMlxVersionTheMacDoesNotReportAsVerified_IsRefused()
    {
        var registry = await RegistryAsync(MlxFixtures.Mac());
        var pinned = MlxFixtures.Model() with { Mlx = MlxFixtures.Model().Mlx! with { ExecutableVersion = "0.31.0" } };

        var refused = await Assert.ThrowsAsync<PlacementValidationException>(() => registry.UpsertAsync(pinned, Ct));

        var problem = Assert.Single(refused.Problems);
        Assert.Equal(PlacementProblemCodes.ExecutableVersionUnavailable, problem.Code);
        Assert.Equal(
            "Model 'qwen3-0-6b-mlx' pins mlx-lm-server 0.31.0, which node 'mac-1' does not report as installed and verified " +
            "(it reports 0.32.2, 0.31.0 (not verified)); install that version on the node, or pin one it has (or none, for its default).",
            problem.Message);
        await registry.UpsertAsync(pinned with { Mlx = pinned.Mlx! with { ExecutableVersion = MlxFixtures.Version } }, Ct);
    }

    [Fact]
    public void AnMlxModel_IsAProcessModel_ForMemoryAttribution()
    {
        Assert.True(ModelMemoryAttribution.IsProcessDelivery(MlxFixtures.Model()));
    }

    private static ModelFolderInspection Inspection(double weightsGiB, double kvBytesPerToken) =>
        new("Qwen3-0.6B-4bit", "Qwen3ForCausalLM", 40_960, "MLX 4-bit", weightsGiB, false,
            new ModelFolderSuggestion("Qwen3-0.6B-4bit", null, null, null, 4, "test"), kvBytesPerToken);

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
}
