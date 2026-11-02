using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Contracts;

/// <summary>
/// The P3/P4 contract freeze (MN-202, design D107–D117): golden fixtures under <c>Fixtures/P3/</c> for every new DTO and
/// for the P3 shape of every extended one (the P1/P2 fixtures next to <see cref="GoldenJsonTests"/> stay byte-identical,
/// which is the compatibility proof for the DGX), plus the rules Waves 12–14 code against: every P3 field omitted at its
/// default, P3 flags disjoint from P1/P2, the GGUF route gated, the alternative features of shared routes, the new error
/// codes and health reasons, and the pure helpers (chat template, refused ExtraArgs, reserved env names, pooling and
/// <c>dimensions</c> rules). Argv in the launch fixtures is illustrative: MN-210 owns the golden llama-server argv.
/// </summary>
public sealed class P3ContractTests
{
    private static readonly JsonSerializerOptions WireStrict = new(AgentJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>The registry file options (default options, PascalCase, as <c>JsonModelRegistry</c>).</summary>
    private static readonly JsonSerializerOptions Registry = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions RegistryStrict = new(Registry) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>The master /api's options (web defaults + PascalCase enums), as in Program.cs.</summary>
    private static readonly JsonSerializerOptions Api = CreateApi();

    private static readonly JsonSerializerOptions ApiStrict = new(Api) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Every P3 fixture: path under <c>Fixtures/P3/</c>, its type and the options it is written with.</summary>
    private static readonly (string File, Type Type, string Options)[] P3Fixtures =
    [
        ("LaunchRequest.process.json", typeof(LaunchRequest), "wire"),
        ("LaunchConfig.process.json", typeof(LaunchConfig), "wire"),
        ("RuntimeStatus.process.json", typeof(RuntimeStatus), "wire"),
        ("AdmissionRequest.gpu-budgets.json", typeof(AdmissionRequest), "wire"),
        ("DownloadStartRequest.ollama.json", typeof(DownloadStartRequest), "wire"),
        ("NodeCapabilities.windows.json", typeof(NodeCapabilities), "wire"),
        ("ProcessRecordsDocument.json", typeof(ProcessRecordsDocument), "wire"),
        ("ExecutableManifest.json", typeof(ExecutableManifest), "wire"),
        ("LlamaCppLockFile.json", typeof(LlamaCppLockFile), "wire"),
        ("models.v2.llamacpp.json", typeof(ModelRegistryDocument), "registry"),
        ("Api/NodeFitResult.llamacpp.json", typeof(NodeFitResult), "api"),
        ("Api/BenchmarkProvenance.process.json", typeof(BenchmarkProvenance), "api"),
    ];

    public static TheoryData<string> P3FixtureFiles() => [.. P3Fixtures.Select(f => f.File)];

    // ---------------------------------------------------------------- golden fixtures

    [Theory]
    [MemberData(nameof(P3FixtureFiles))]
    public void P3Fixture_RoundTripsExactly(string file)
    {
        var (_, type, kind) = P3Fixtures.Single(f => f.File == file);
        (JsonSerializerOptions strict, JsonSerializerOptions write) = kind switch
        {
            "wire" => (WireStrict, AgentJson.Options),
            "registry" => (RegistryStrict, Registry),
            _ => (ApiStrict, Api),
        };

        string path = Path.Combine(FixturesPath(), "P3", file);
        Assert.True(File.Exists(path), $"Missing P3 fixture: {path}");
        JsonNode fixture = JsonNode.Parse(File.ReadAllText(path))!;

        object? value;
        try
        {
            value = fixture.Deserialize(type, strict);
        }
        catch (JsonException ex)
        {
            Assert.Fail($"P3/{file} does not match {type.Name}: {ex.Message}");
            return;
        }

        Assert.NotNull(value);
        JsonNode output = JsonSerializer.SerializeToNode(value, type, write)!;
        string? difference = Difference(fixture, output, "$");
        Assert.True(difference is null, $"P3/{file} does not round-trip: {difference}\nfixture: {fixture.ToJsonString()}\noutput:  {output.ToJsonString()}");
    }

    [Fact]
    public void EveryNewWireType_HasAFixture()
    {
        // Route DTOs (GgufInspectQuery, GgufSummary) are enforced by GoldenJsonTests; the rest are listed here.
        // Nested new types ride in the strict round-trip of their parent's P3 fixture.
        var covered = new Dictionary<Type, string>
        {
            [typeof(ProcessRecordsDocument)] = "ProcessRecordsDocument.json",
            [typeof(ProcessRecord)] = "ProcessRecordsDocument.json",
            [typeof(ProcessIdentity)] = "ProcessRecordsDocument.json",
            [typeof(ExecutableManifest)] = "ExecutableManifest.json",
            [typeof(ExecutableManifestFile)] = "ExecutableManifest.json",
            [typeof(LlamaCppLockFile)] = "LlamaCppLockFile.json",
            [typeof(LlamaCppLockAsset)] = "LlamaCppLockFile.json",
            [typeof(RuntimeVersion)] = "NodeCapabilities.windows.json",
            [typeof(OllamaSource)] = "DownloadStartRequest.ollama.json",
            [typeof(GgufMemoryEstimate)] = "Api/NodeFitResult.llamacpp.json",
        };
        Assert.All(covered, c => Assert.Contains(P3Fixtures, f => f.File == c.Value));
        Assert.True(File.Exists(Path.Combine(FixturesPath(), "GgufSummary.json")));
        Assert.True(File.Exists(Path.Combine(FixturesPath(), "GgufInspectQuery.json")));
        Assert.Contains(typeof(GgufSummary), AgentRoutes.DtoTypes);
        Assert.Contains(typeof(GgufInspectQuery), AgentRoutes.DtoTypes);
    }

    [Fact]
    public void FixtureValues_ReadAsTheContractSays()
    {
        var launch = Read<LaunchRequest>("LaunchRequest.process.json");
        Assert.Equal(LaunchDelivery.Process, launch.Spec.Kind);
        Assert.Equal(RuntimeKind.LlamaCpp, launch.Spec.Runtime);
        Assert.Equal(ExecutableNames.LlamaServer, launch.Spec.Executable);
        Assert.Equal("b11476", launch.Spec.ExecutableVersion);
        Assert.Contains(ProcessArgvTokens.Port, launch.Spec.Argv);
        Assert.Equal(8.9, launch.Admission.BudgetForGpu("GPU-7c1e9a52-3f60-4d2b-9a8e-5b0f2c6d1e44", 1));
        Assert.Equal(LlamaCppFlags.AlwaysArgs, launch.Spec.Argv.TakeLast(LlamaCppFlags.AlwaysArgs.Count));

        var caps = Read<NodeCapabilities>("NodeCapabilities.windows.json");
        Assert.Equal("windows", caps.Os.Family);
        Assert.Equal(HostServiceManagers.Scm, caps.HostServices!.Manager);
        var llama = Assert.Single(caps.Runtimes);
        Assert.Equal(["b11476"], llama.Versions!.Where(v => v.Verified && v.Default).Select(v => v.Version));
        Assert.DoesNotContain(AgentFeatures.RuntimeVllmContainer, caps.Features);
        Assert.DoesNotContain(AgentFeatures.DownloadsHfContainer, caps.Features);
        Assert.DoesNotContain(AgentFeatures.ServicesDocker, caps.Features);
        Assert.All(AgentFeatures.P3Reported, f => Assert.Contains(f, caps.Features));

        var download = Read<DownloadStartRequest>("DownloadStartRequest.ollama.json");
        Assert.Equal(ModelSourceKind.Ollama, download.Source);
        Assert.Equal(download.RepoId, download.Ollama!.Key);

        var registry = JsonSerializer.Deserialize<ModelRegistryDocument>(File.ReadAllText(Path.Combine(FixturesPath(), "P3", "models.v2.llamacpp.json")), Registry)!;
        Assert.Equal(PoolingMode.Embedding, PoolingDetection.Detect(registry.Models[0]));
        Assert.True(EmbeddingDimensions.Applies(registry.Models[0]));
        Assert.Equal(LlamaCppSplitMode.Layer, registry.Models[1].LlamaCpp!.EffectiveSplitMode(2));
        Assert.Equal(registry.Models[1].MemoryBudgetGiB, registry.Models[1].LlamaCpp!.GpuBudgetsGiB!.Sum(), 3);
        Assert.Equal(ModelSourceKind.Ollama, registry.Models[2].Sources[0].Kind);
        Assert.Equal(PoolingMode.None, PoolingDetection.Detect(registry.Models[2]));

        var records = Read<ProcessRecordsDocument>("ProcessRecordsDocument.json");
        Assert.Equal(ProcessRecordsDocument.CurrentSchemaVersion, records.SchemaVersion);
        Assert.Equal(18244, records.Processes[0].Process.Pid);
        Assert.Equal("9c4e21b07a5d3f18", Assert.Single(records.Pending).ArgvDigest);

        // Spike S1 (MN-203) values, folded in by MN-207 (D120): the pin by tag + commit, the R615 driver floor, and the
        // per-file hashes of the server, its implementation DLL and the CUDA DLLs (not only the 9 KB launcher).
        var lockFile = Read<LlamaCppLockFile>("LlamaCppLockFile.json");
        Assert.Equal(("b11476", "988190680", "615.00"), (lockFile.Version, lockFile.Commit, lockFile.MinDriver));
        Assert.Contains(lockFile.Files, f => f.Path == "llama-server-impl.dll");
        var manifest = Read<ExecutableManifest>("ExecutableManifest.json");
        Assert.Equal(lockFile.Files.Select(f => (f.Path, f.Sha256, f.Bytes)), manifest.Files.Select(f => (f.Path, f.Sha256, f.Bytes)));
    }

    [Fact]
    public void AlwaysArgs_FollowSpikeS1()
    {
        // C4: --no-webui is deprecated at b11476; C6: no host-RAM prompt cache; --fit off stays last (MN-207, D118).
        Assert.Contains("--no-ui", LlamaCppFlags.AlwaysArgs);
        Assert.DoesNotContain("--no-webui", LlamaCppFlags.AlwaysArgs);
        int cacheRam = LlamaCppFlags.AlwaysArgs.ToList().IndexOf("--cache-ram");
        Assert.Equal("0", LlamaCppFlags.AlwaysArgs[cacheRam + 1]);
        Assert.Equal(["--ui", "--cache-ram=8192", "-cram"], LlamaCppFlags.RefusedExtraArgs(["--ui", "--cache-ram=8192", "-cram", "4", "--temp", "0"]));
    }

    [Theory]
    [InlineData(8192, null, null, 2048)]   // Qwen3-Embedding (causal, key absent): ub 2048 even for 8192-token inputs (S1 C2)
    [InlineData(8192, null, true, 2048)]
    [InlineData(1024, null, null, 1024)]   // never above the per-slot context
    [InlineData(512, null, false, 512)]    // non-causal (BERT-style): the whole input in one micro-batch
    [InlineData(8192, null, false, 8192)]
    [InlineData(8192, 4096, null, 4096)]   // an explicit MicroBatchSize wins
    [InlineData(8192, 4096, false, 4096)]
    public void EmbeddingMicroBatch_IsCappedForCausalModelsOnly(int contextSize, int? microBatch, bool? causal, int expected) =>
        Assert.Equal(expected, new LlamaCppSettings { ModelFile = "m/m.gguf", Embedding = true, ContextSize = contextSize, MicroBatchSize = microBatch }.EmbeddingMicroBatch(causal));

    // ---------------------------------------------------------------- compatibility: P3 fields omitted at default

    [Fact]
    public void P2ShapedObjects_SerializeWithoutP3Keys()
    {
        var spec = JsonSerializer.SerializeToNode(new LaunchSpec { Kind = LaunchDelivery.Container }, AgentJson.Options)!.AsObject();
        Assert.False(spec.ContainsKey("executableVersion"));

        var config = JsonSerializer.SerializeToNode(new LaunchConfig(), AgentJson.Options)!.AsObject();
        Assert.False(config.ContainsKey("executableVersion"));

        var admission = JsonSerializer.SerializeToNode(new AdmissionRequest { BudgetGiB = 36, ReserveGiB = 8 }, AgentJson.Options)!.AsObject();
        Assert.False(admission.ContainsKey("gpuBudgetsGiB"));

        var runtime = JsonSerializer.SerializeToNode(new RuntimeCapability { Kind = RuntimeKind.Vllm, Delivery = LaunchDelivery.Container, Available = true }, AgentJson.Options)!.AsObject();
        Assert.False(runtime.ContainsKey("versions"));

        var download = JsonSerializer.SerializeToNode(new DownloadStartRequest
        {
            OperationId = "op", IssuedAt = DateTimeOffset.UnixEpoch, ExpiresAt = DateTimeOffset.UnixEpoch, RepoId = "a/b", Folder = "b",
        }, AgentJson.Options)!.AsObject();
        Assert.False(download.ContainsKey("source"));
        Assert.False(download.ContainsKey("ollama"));

        var settings = JsonSerializer.SerializeToNode(new LlamaCppSettings { ModelFile = "m/m.gguf" }, Registry)!.AsObject();
        string[] p1Keys =
        [
            "ModelFile", "ProjectorFile", "ContextSize", "Parallel", "GpuLayers", "FlashAttention", "CacheTypeK", "CacheTypeV",
            "BatchSize", "MicroBatchSize", "Delivery", "Image", "ExtraArgs",
        ];
        Assert.Equal(p1Keys, settings.Select(p => p.Key));

        var source = JsonSerializer.SerializeToNode(new ModelSource("a/b", "b"), Registry)!.AsObject();
        Assert.Equal(["RepoId", "Folder", "Revision", "AllowPatterns"], source.Select(p => p.Key));

        var provenance = JsonSerializer.SerializeToNode(new BenchmarkProvenance { NodeId = "dgx" }, Api)!.AsObject();
        Assert.False(provenance.ContainsKey("executableVersion"));

        var fit = JsonSerializer.SerializeToNode(new NodeFitResult { NodeId = "dgx", Model = MemoryModel.Unified, NeedGiB = 1, Message = "m" }, Api)!.AsObject();
        Assert.False(fit.ContainsKey("estimate"));

        var request = JsonSerializer.SerializeToNode(new DownloadRequest("a/b"), Api)!.AsObject();
        Assert.False(request.ContainsKey("source"));
        Assert.False(request.ContainsKey("ollama"));
    }

    [Fact]
    public void TheDgxFixtures_ReportNoP3FeatureAndCarryNoP3Field()
    {
        var dgx = JsonSerializer.Deserialize<NodeCapabilities>(File.ReadAllText(Path.Combine(FixturesPath(), "NodeCapabilities.json")), WireStrict)!;
        Assert.Empty(dgx.Features.Intersect(AgentFeatures.P3Reported, StringComparer.Ordinal));
        Assert.All(dgx.Runtimes, r => Assert.Null(r.Versions));

        foreach (string name in new[] { "LaunchRequest.json", "LaunchConfig.json", "AdmissionRequest.json", "DownloadStartRequest.json", "NodeCapabilities.json" })
        {
            string text = File.ReadAllText(Path.Combine(FixturesPath(), name));
            foreach (string key in new[] { "executableVersion", "gpuBudgetsGiB", "versions", "\"source\"", "\"ollama\"" })
            {
                Assert.DoesNotContain(key, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EnumWireNames_AreTheSameForEverySerializer()
    {
        foreach (var options in new[] { AgentJson.Options, Registry, Api })
        {
            Assert.Equal("\"ollama\"", JsonSerializer.Serialize(ModelSourceKind.Ollama, options));
            Assert.Equal("\"huggingface\"", JsonSerializer.Serialize(ModelSourceKind.HuggingFace, options));
            Assert.Equal("\"layer\"", JsonSerializer.Serialize(LlamaCppSplitMode.Layer, options));
            Assert.Equal("\"none\"", JsonSerializer.Serialize(LlamaCppSplitMode.None, options));
        }
    }

    // ---------------------------------------------------------------- features, routes, codes

    [Fact]
    public void Features_P1P2AndP3AreDisjointAndCoverEveryConstant()
    {
        var all = typeof(AgentFeatures).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(AgentFeatures.P3.Intersect(AgentFeatures.P1.Concat(AgentFeatures.P2), StringComparer.Ordinal));
        // macOS Phase 3: the flags only a Mac reports are a list of their own, disjoint from the phases'.
        Assert.Empty(AgentFeatures.MacOS.Intersect(AgentFeatures.P1.Concat(AgentFeatures.P2).Concat(AgentFeatures.P3), StringComparer.Ordinal));
        Assert.Equal(["runtime.mlx.process", "services.launchd"], AgentFeatures.MacOS);
        Assert.Equal(all.Order(StringComparer.Ordinal), AgentFeatures.P1.Concat(AgentFeatures.P2).Concat(AgentFeatures.P3).Concat(AgentFeatures.MacOS).Concat(AgentFeatures.Updates).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["admission.gpu-budgets", "downloads.hf-native", "downloads.ollama", "gguf.inspect", "runtime.llamacpp.process", "services.scm"],
            AgentFeatures.P3Reported.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Protocol_StaysVersionOne_AndTheCatalogueGainsOnlyTheGatedGgufRoute()
    {
        Assert.Equal(1, Protocol.MinVersion);
        Assert.Equal(1, Protocol.CurrentVersion);
        // The gated agent update (docs/multinode/agent-updates.md) is the 52nd.
        Assert.Equal(52, AgentRoutes.All.Count);
        Assert.Equal(AgentFeatures.AgentUpdate, AgentRoutes.AgentUpdate.Feature);
        var route = AgentRoutes.GgufInspect;
        Assert.Contains(route, AgentRoutes.All);
        Assert.Equal("GET /model-folders/{folder}/gguf", route.Method + " " + route.Path);
        Assert.Equal(AgentRouteInput.Query, route.Input);
        Assert.Equal(AgentFeatures.GgufInspect, route.Feature);
        Assert.False(route.IsMutation);
        Assert.Contains(ErrorCodes.GgufInvalid, route.ErrorCodes);
        Assert.Equal("/model-folders/Qwen3%20GGUF/gguf", route.Expand("Qwen3 GGUF"));
    }

    [Fact]
    public void AlternativeFeatures_OfferSharedRoutesThroughTheNewSubsystems()
    {
        Assert.True(AgentRoutes.DownloadStart.IsOfferedBy([AgentFeatures.DownloadsHfContainer]));
        Assert.True(AgentRoutes.DownloadStart.IsOfferedBy([AgentFeatures.DownloadsHfNative]));
        Assert.True(AgentRoutes.DownloadStart.IsOfferedBy([AgentFeatures.DownloadsOllama]));
        Assert.False(AgentRoutes.DownloadStart.IsOfferedBy([AgentFeatures.TelemetryV1]));

        AgentRoute[] hostMutations =
        [
            AgentRoutes.HostServiceStart, AgentRoutes.HostServiceStop, AgentRoutes.HostServiceRestart, AgentRoutes.HostServiceAutostart,
            AgentRoutes.HostWhitelistAdd, AgentRoutes.HostWhitelistUpdate, AgentRoutes.HostWhitelistRemove,
        ];
        Assert.All(hostMutations, r => Assert.True(r.IsOfferedBy([AgentFeatures.ServicesScm]), r.Name));
        Assert.All(hostMutations, r => Assert.True(r.IsOfferedBy([AgentFeatures.ServicesSystemd]), r.Name));
        Assert.All(hostMutations, r => Assert.False(r.IsOfferedBy([]), r.Name));

        // No other route gained an alternative, and an ungated route is always offered.
        var withAlternatives = AgentRoutes.All.Where(r => r.AlternativeFeatures.Count > 0).Select(r => r.Name).Order(StringComparer.Ordinal);
        Assert.Equal(hostMutations.Select(r => r.Name).Append(AgentRoutes.DownloadStart.Name).Order(StringComparer.Ordinal), withAlternatives);
        Assert.True(AgentRoutes.Status.IsOfferedBy([]));
        Assert.False(AgentRoutes.GgufInspect.IsOfferedBy(AgentFeatures.P1.Concat(AgentFeatures.P2)));
    }

    [Fact]
    public void ErrorCodes_P3CodesAre422AgentCodes_ThatTheApiPassesThrough()
    {
        string[] p3 = [ErrorCodes.ExecutableMissing, ErrorCodes.ExecutableUnverified, ErrorCodes.GgufInvalid, ErrorCodes.UnsupportedManifest];
        Assert.Equal(["executable_missing", "executable_unverified", "gguf_invalid", "unsupported_manifest"], p3);
        Assert.All(p3, c =>
        {
            Assert.Contains(c, ErrorCodes.AgentCodes);
            Assert.Contains(c, ErrorCodes.ApiCodes);
            Assert.Equal(422, ErrorMappings.AgentStatus(c));
            Assert.Equal(422, ErrorCodes.ApiStatus(c));
            Assert.Equal(typeof(ArgumentException), ErrorMappings.MasterExceptions[c]);
        });
        Assert.Contains(ErrorCodes.ExecutableMissing, AgentRoutes.RuntimeLaunch.ErrorCodes);
        Assert.Contains(ErrorCodes.ExecutableUnverified, AgentRoutes.RuntimeLaunch.ErrorCodes);
        Assert.Contains(ErrorCodes.UnsupportedManifest, AgentRoutes.DownloadStart.ErrorCodes);
        Assert.Equal("ollama", UpstreamRefusalDetails.Ollama);

        // /v1 clients see exactly P1's codes.
        Assert.Equal(9, ErrorCodes.RouterCodes.Count);
    }

    [Fact]
    public void HealthReasons_AddTheTwoAgentReasons()
    {
        var values = typeof(HealthReasonCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("executable_unverified", HealthReasonCodes.ExecutableUnverified);
        Assert.Equal("gpu_external_use_high", HealthReasonCodes.GpuExternalUseHigh);
    }

    [Fact]
    public void PlacementProblemCodes_AreUniqueSnakeCase()
    {
        var values = typeof(PlacementProblemCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(PlacementProblemCodes.GpuBudgetsMismatch, values);
        Assert.Contains(PlacementProblemCodes.ExecutableVersionUnavailable, values);
        Assert.Contains(PlacementProblemCodes.LlamaCppSettingsInvalid, values);
        Assert.All(values, v => Assert.Matches("^[a-z][a-z0-9_]*$", v));
    }

    // ---------------------------------------------------------------- pure helpers

    [Theory]
    [InlineData("builtin:chatml", "chatml", null)]
    [InlineData("builtin:llama3", "llama3", null)]
    [InlineData("chatml-ollama.jinja", null, "chatml-ollama.jinja")]
    [InlineData("template v2.jinja", null, "template v2.jinja")]
    public void ChatTemplate_Parses(string value, string? builtin, string? file)
    {
        Assert.True(LlamaCppChatTemplate.TryParse(value, out var template));
        Assert.Equal(builtin, template.BuiltinName);
        Assert.Equal(file, template.File);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("builtin:")]
    [InlineData("builtin:Chat ML")]
    [InlineData("../evil.jinja")]
    [InlineData("sub/dir.jinja")]
    [InlineData("sub\\dir.jinja")]
    [InlineData("C:evil.jinja")]
    [InlineData("file.jinja:stream")]
    [InlineData("..")]
    [InlineData(".hidden")]
    public void ChatTemplate_RefusesEverythingElse(string? value) =>
        Assert.False(LlamaCppChatTemplate.TryParse(value, out _));

    [Fact]
    public void LlamaCppFlags_RefuseOwnedAndPathValuedExtraArgs()
    {
        Assert.Equal(
            ["--port=9000", "-m", "--hf-token", "--chat-template-file", "--api-key"],
            LlamaCppFlags.RefusedExtraArgs(["--temp", "0.2", "--port=9000", "-m", "x.gguf", "--hf-token", "t", "--chat-template-file", "f", "--api-key", "k"]));
        Assert.Empty(LlamaCppFlags.RefusedExtraArgs(["--temp", "0.2", "--top-p", "0.9", "--split-mode", "row", "-sm", "row"]));
        Assert.True(LlamaCppFlags.IsFlag("--pooling=last", ["--pooling"]));
        Assert.False(LlamaCppFlags.IsFlag("last", ["--pooling"]));
        Assert.All(LlamaCppFlags.BuilderPathFlags, f => Assert.Contains(f, LlamaCppFlags.PathValued));
        Assert.Contains(ProcessArgvTokens.Port, LlamaCppFlags.AlwaysArgs);
        Assert.Equal(["--fit", "off"], LlamaCppFlags.AlwaysArgs.TakeLast(2));
        Assert.Equal(LogicalVolumes.ModelsToken, ProcessArgvTokens.Models);
        Assert.Equal(LogicalVolumes.CacheToken, ProcessArgvTokens.Cache);
    }

    [Fact]
    public void ProcessEnvironment_ReservesTheAgentOwnedNames_IgnoringCase()
    {
        Assert.Equal(
            ["CUDA_VISIBLE_DEVICES", "CUDA_CACHE_PATH", "CUDA_CACHE_MAXSIZE", "LLAMA_API_KEY", "PATH", "SystemRoot", "TEMP", "TMP"],
            ProcessEnvironment.Reserved);
        Assert.True(ProcessEnvironment.IsReserved("path"));
        Assert.True(ProcessEnvironment.IsReserved("llama_api_key"));
        Assert.False(ProcessEnvironment.IsReserved("HF_HUB_OFFLINE"));
        Assert.False(ProcessEnvironment.IsReserved(null));
    }

    [Fact]
    public void Pooling_LlamaCppEmbeddingModelsAreEmbeddings_VllmDetectionUnchanged()
    {
        var vllm = new ModelDefinition { Id = "e", ServedName = "e", ModelPath = "e", ExtraArgs = ["--runner", "pooling"] };
        Assert.Equal(PoolingMode.Embedding, PoolingDetection.Detect(vllm));
        Assert.Equal(PoolingMode.None, PoolingDetection.Detect(vllm with { ExtraArgs = [] }));

        // A llama.cpp setting on a vLLM model is ignored (the runtime decides).
        var stray = vllm with { ExtraArgs = [], LlamaCpp = new LlamaCppSettings { ModelFile = "e/e.gguf", Embedding = true } };
        Assert.Equal(PoolingMode.None, PoolingDetection.Detect(stray));
        Assert.Equal(PoolingMode.Embedding, PoolingDetection.Detect(stray with { Runtime = RuntimeKind.LlamaCpp }));
        Assert.True(LlamaCppPooling.IsValid(LlamaCppPooling.Last));
        Assert.False(LlamaCppPooling.IsValid("LAST"));
    }

    [Fact]
    public void EmbeddingDimensions_ApplyOnlyToLlamaCppEmbeddingModelsThatEmulate()
    {
        var llama = new ModelDefinition
        {
            Id = "e", ServedName = "e", ModelPath = "e", Runtime = RuntimeKind.LlamaCpp,
            LlamaCpp = new LlamaCppSettings { ModelFile = "e/e.gguf", Embedding = true },
        };
        Assert.True(EmbeddingDimensions.Applies(llama));
        Assert.False(EmbeddingDimensions.Applies(llama with { LlamaCpp = llama.LlamaCpp! with { EmulateDimensions = false } }));
        Assert.False(EmbeddingDimensions.Applies(llama with { LlamaCpp = llama.LlamaCpp! with { Embedding = false } }));
        Assert.False(EmbeddingDimensions.Applies(llama with { Runtime = RuntimeKind.Vllm }));
        Assert.False(EmbeddingDimensions.Applies(null));
        Assert.Equal("dimensions", EmbeddingDimensions.DimensionsField);
    }

    [Fact]
    public void LlamaCppSettings_DerivedValues()
    {
        var settings = new LlamaCppSettings { ModelFile = "m/m.gguf" };
        Assert.False(settings.EmulatesDimensions);
        Assert.True((settings with { Embedding = true }).EmulatesDimensions);
        Assert.Equal(LlamaCppSplitMode.None, settings.EffectiveSplitMode(1));
        Assert.Equal(LlamaCppSplitMode.Layer, settings.EffectiveSplitMode(2));
        Assert.Equal(LlamaCppSplitMode.None, (settings with { SplitMode = LlamaCppSplitMode.None }).EffectiveSplitMode(2));

        // The derived members are not part of the JSON.
        string json = JsonSerializer.Serialize(settings with { Embedding = true }, AgentJson.Options);
        Assert.DoesNotContain("emulatesDimensions", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"key\"", JsonSerializer.Serialize(new OllamaSource("r", "n", "t"), AgentJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public void AdmissionRequest_BudgetForGpu_PrefersTheMapThenTheEvenSplit()
    {
        var admission = new AdmissionRequest { BudgetGiB = 18, ReserveGiB = 8, GpuBudgetsGiB = new Dictionary<string, double> { ["GPU-a"] = 7.5 } };
        Assert.Equal(7.5, admission.BudgetForGpu("GPU-a", 2));
        Assert.Equal(9, admission.BudgetForGpu("GPU-b", 2));
        Assert.Equal(18, new AdmissionRequest { BudgetGiB = 18, ReserveGiB = 8 }.BudgetForGpu("GPU-a", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => admission.BudgetForGpu("GPU-a", 0));
    }

    [Fact]
    public void LlamaCppMetrics_AreUniqueAndPrefixed()
    {
        Assert.Equal(LlamaCppMetrics.Families.Count, LlamaCppMetrics.Families.Distinct(StringComparer.Ordinal).Count());
        Assert.All(LlamaCppMetrics.Families, f => Assert.StartsWith(LlamaCppMetrics.Prefix, f, StringComparison.Ordinal));
        Assert.Contains("llamacpp:requests_processing", LlamaCppMetrics.Families);
        Assert.Contains("llamacpp:tokens_predicted_total", LlamaCppMetrics.Families);
    }

    [Fact]
    public void Secrets_NeverAppearInToString()
    {
        const string secret = "hf_p3_fixture_secret";
        var download = new DownloadStartRequest
        {
            OperationId = "op", IssuedAt = DateTimeOffset.UnixEpoch, ExpiresAt = DateTimeOffset.UnixEpoch, RepoId = "a/b", Folder = "b",
            Token = secret, Source = ModelSourceKind.HuggingFace,
        };
        Assert.DoesNotContain(secret, download.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, new DownloadRequest("a/b", Token: secret) { Source = ModelSourceKind.HuggingFace }.ToString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- stubs and options

    [Fact]
    public async Task DefaultMembers_AreStubsUntilTheirTasks()
    {
        // A default interface member (not abstract), so every IAgentClient test double keeps compiling (AgentClient implements it).
        MethodInfo inspect = typeof(IAgentClient).GetMethod(nameof(IAgentClient.InspectGgufAsync))!;
        Assert.False(inspect.IsAbstract);

        IRuntimeDriver driver = new MinimalDriver();
        Assert.Null(await driver.GetUpstreamApiKeyAsync("m", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AgentOptions_BindTheP3Sections_WithDefaultsThatKeepTodaysAgents()
    {
        var defaults = new AgentOptions();
        Assert.True(defaults.Docker.Enabled);
        Assert.Empty(defaults.Executables);
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.Process.StartTimeout);
        Assert.Equal(2, defaults.Process.RestartBudget);
        Assert.Equal(TimeSpan.FromMinutes(10), defaults.Process.RestartWindow);
        Assert.Equal(50L * 1024 * 1024, defaults.Process.LogMaxBytes);

        const string json = """
            {
              "Agent": {
                "NodeId": "win-1",
                "StateDirectory": "C:\\ProgramData\\mirbadllm-agent\\state",
                "Docker": { "Enabled": false },
                "Executables": [
                  { "Name": "llama-server", "Default": "b11476",
                    "Versions": [ { "Version": "b11476", "Cuda": "13.4", "Path": "C:\\rt\\b11476\\llama-server.exe", "Manifest": "C:\\rt\\b11476\\manifest.json" } ] }
                ],
                "Process": { "StartTimeout": "00:20:00", "RestartBudget": 3, "LogMaxBytes": 1048576 }
              }
            }
            """;
        AgentOptions options = AgentOptions.FromConfiguration(new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build());
        Assert.False(options.Docker.Enabled);
        var exe = Assert.Single(options.Executables);
        Assert.Equal(ExecutableNames.LlamaServer, exe.Name);
        Assert.Equal("b11476", exe.Default);
        Assert.Equal("13.4", Assert.Single(exe.Versions).Cuda);
        Assert.Equal(TimeSpan.FromMinutes(20), options.Process.StartTimeout);
        Assert.Equal(3, options.Process.RestartBudget);
        Assert.Equal(TimeSpan.FromMinutes(10), options.Process.RestartWindow);
        Assert.Equal(1_048_576, options.Process.LogMaxBytes);

        AgentOptions dgx = AgentOptions.FromConfiguration(new ConfigurationBuilder().AddJsonFile(RepoFile("deploy", "agent", "agent.dgx.json"), optional: false, reloadOnChange: false).Build());
        Assert.True(dgx.Docker.Enabled);
        Assert.Empty(dgx.Executables);
    }

    [Fact]
    public void OwnerDecisionO1_ThePackageIsPinned_AndReferencedByTheAgentOnly()
    {
        string props = File.ReadAllText(RepoFile("Directory.Packages.props"));
        Assert.Contains("""<PackageVersion Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.12" />""", props, StringComparison.Ordinal);
        Assert.Contains("""<PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />""", props, StringComparison.Ordinal);

        // MN-215 (D107) adds the one versionless reference to the agent; no other project references the package.
        string agent = Path.GetFullPath(RepoFile("src", "MirbadLLM.Agent", "MirbadLLM.Agent.csproj"));
        Assert.Contains("""<PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" />""", File.ReadAllText(agent), StringComparison.Ordinal);
        foreach (string project in Directory.GetFiles(RepoFile("src"), "*.csproj", SearchOption.AllDirectories)
                     .Concat(Directory.GetFiles(RepoFile("tests"), "*.csproj", SearchOption.AllDirectories))
                     .Where(p => !string.Equals(Path.GetFullPath(p), agent, StringComparison.OrdinalIgnoreCase)))
        {
            Assert.DoesNotContain("Microsoft.Extensions.Hosting.WindowsServices", File.ReadAllText(project), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheFakeLlamaServer_IsBuiltNextToTheTests()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "fake-llama-server.dll");
        Assert.True(File.Exists(dll), $"{dll} is missing: the test project must reference tests/MirbadLLM.FakeLlamaServer.");
    }

    // ---------------------------------------------------------------- helpers

    private static T Read<T>(string file) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(FixturesPath(), "P3", file)), WireStrict)!;

    /// <summary>Exact structural comparison in both directions; numbers by value, timestamps by instant and offset.</summary>
    private static string? Difference(JsonNode? expected, JsonNode? actual, string path)
    {
        if (expected is null || actual is null)
        {
            return expected is null && actual is null ? null : $"{path}: {expected?.ToJsonString() ?? "null"} vs {actual?.ToJsonString() ?? "null"}";
        }

        if (expected.GetValueKind() != actual.GetValueKind())
        {
            return $"{path}: {expected.GetValueKind()} vs {actual.GetValueKind()}";
        }

        switch (expected)
        {
            case JsonObject e:
                var a = actual.AsObject();
                foreach (string key in e.Select(p => p.Key).Union(a.Select(p => p.Key)))
                {
                    if (!e.ContainsKey(key))
                    {
                        return $"{path}.{key}: missing from the fixture (the DTO writes it)";
                    }

                    if (!a.ContainsKey(key))
                    {
                        return $"{path}.{key}: not written by the DTO";
                    }

                    if (Difference(e[key], a[key], $"{path}.{key}") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            case JsonArray e:
                var arr = actual.AsArray();
                if (e.Count != arr.Count)
                {
                    return $"{path}: {e.Count} items vs {arr.Count}";
                }

                for (int i = 0; i < e.Count; i++)
                {
                    if (Difference(e[i], arr[i], $"{path}[{i}]") is { } inner)
                    {
                        return inner;
                    }
                }

                return null;
            default:
                return ScalarEquals(expected.AsValue(), actual.AsValue()) ? null : $"{path}: {expected.ToJsonString()} vs {actual.ToJsonString()}";
        }
    }

    private static bool ScalarEquals(JsonValue expected, JsonValue actual)
    {
        if (expected.GetValueKind() == JsonValueKind.Number)
        {
            return expected.GetValue<decimal>() == actual.GetValue<decimal>();
        }

        if (expected.GetValueKind() == JsonValueKind.String)
        {
            string e = expected.GetValue<string>();
            string a = actual.GetValue<string>();
            return string.Equals(e, a, StringComparison.Ordinal) ||
                   (DateTimeOffset.TryParse(e, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var de) &&
                    DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var da) &&
                    de == da && de.Offset == da.Offset);
        }

        return JsonNode.DeepEquals(expected, actual);
    }

    private static JsonSerializerOptions CreateApi()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static string FixturesPath([CallerFilePath] string sourceFile = "") => Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures");

    private static string RepoFile(params string[] parts) =>
        Path.Combine([Path.GetFullPath(Path.Combine(FixturesPath(), "..", "..", "..", "..")), .. parts]);

    private sealed class MinimalDriver : IRuntimeDriver
    {
        public LaunchDelivery Delivery => LaunchDelivery.Process;

        public Task<IReadOnlyList<RuntimeStatus>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RuntimeStatus>>([]);
        public Task<RuntimeStatus?> GetAsync(string modelId, CancellationToken ct = default) => Task.FromResult<RuntimeStatus?>(null);
        public Task<RuntimeLaunchResult> LaunchAsync(RuntimeLaunch launch, IRuntimeStageSink stages, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RuntimeStatus> StartCreatedAsync(string modelId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(string modelId, IRuntimeStageSink stages, CancellationToken ct = default) => Task.CompletedTask;
        public Task<LaunchConfig?> GetLaunchConfigAsync(string modelId, CancellationToken ct = default) => Task.FromResult<LaunchConfig?>(null);
        public IAsyncEnumerable<string> StreamLogsAsync(string modelId, int tail, bool follow, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Uri?> GetUpstreamAsync(string modelId, CancellationToken ct = default) => Task.FromResult<Uri?>(null);
        public Task<bool> IsFolderInUseAsync(string folder, CancellationToken ct = default) => Task.FromResult(false);
    }
}
