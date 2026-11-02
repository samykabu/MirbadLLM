using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; hand-written fakes ignore the token.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-107: approved image digests and launch environment variables. The container driver creates from
/// <c>repository@digest</c> while the spec's tag stays the hash input, refuses tag-only and mismatched specs, passes the
/// environment to Docker and reports it back in <c>launch-config</c> with names only for secrets (never in labels, logs or
/// errors); the agent reports <c>launch.env</c> / <c>images.digest-pinned</c>; the master sends the node default image's
/// digest and sends environment variables only to nodes reporting <c>launch.env</c>.
/// </summary>
public sealed class ImagePolicyTests
{
    private const string NodeId = "pc-office";
    private const string Repo = "registry.local:5000/vllm/vllm-openai";
    private const string Tag = Repo + ":v0.11";
    private const string Secret = "hf_SuperSecretValue123";
    private static readonly string Digest = "sha256:" + new string('a', 64);
    private static readonly string OtherDigest = "sha256:" + new string('b', 64);
    private static readonly string Pinned = Repo + "@" + Digest;

    private static readonly ModelDefinition Saved = new()
    {
        Id = "m1",
        ServedName = "M1",
        ModelPath = "ckpt",
        MemoryBudgetGiB = 12,
    };

    // ------------------------------------------------------------ harness

    private static ContainerRuntimeOptions PinnedOptions(bool require = true, string modelsRoot = "/srv/m", string cacheRoot = "/srv/c")
    {
        var options = new ContainerRuntimeOptions
        {
            NodeId = NodeId,
            ModelsHostPath = modelsRoot,
            CacheHostPath = cacheRoot,
            RequireApprovedDigest = require,
        };
        options.ApprovedImages.Add(Pinned);
        return options;
    }

    private sealed class Harness
    {
        public Harness(ContainerRuntimeOptions? options = null)
        {
            Options = options ?? PinnedOptions();
            Driver = new ContainerRuntimeDriver(
                Docker.Client,
                new HttpClient(new FakeHttpMessageHandler { Handler = _ => new HttpResponseMessage(HttpStatusCode.OK) }),
                new ContainerRuntimeDriverTests.FakePortAllocator(),
                Microsoft.Extensions.Options.Options.Create(Options),
                NullLogger<ContainerRuntimeDriver>.Instance);
        }

        public PolicyDocker Docker { get; } = new();

        public ContainerRuntimeOptions Options { get; }

        public ContainerRuntimeDriver Driver { get; }

        public ContainerRuntimeDriverTests.RecordingStageSink Stages { get; } = new();

        public Task<RuntimeLaunchResult> LaunchAsync(LaunchSpec spec, string modelId = "m1") =>
            Driver.LaunchAsync(new RuntimeLaunch { ModelId = modelId, OperationId = "op-1", Mode = LaunchMode.EnsureStarted, Spec = spec, HostPort = 8101 }, Stages);
    }

    private static LaunchSpec Spec(
        string image = Tag,
        string? digest = null,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyList<string>? secrets = null,
        IReadOnlyList<MountSpec>? mounts = null) => new()
    {
        Kind = LaunchDelivery.Container,
        Image = image,
        ImageDigest = digest,
        Argv = ["--model", "/models/ckpt", "--served-model-name", "M1"],
        Env = env ?? new Dictionary<string, string>(),
        SecretEnvNames = secrets ?? [],
        ContainerPort = 8000,
        Mounts = mounts ??
        [
            new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, ReadOnly: true),
            new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false),
        ],
        Gpus = new GpuSelection { Mode = GpuSelectionMode.All },
        IpcHost = true,
        Labels = new Dictionary<string, string>
        {
            [ContainerLabels.ServedName] = "M1",
            [ContainerLabels.ConfigHash] = ModelConfigHash.Compute(image, ["--model", "/models/ckpt", "--served-model-name", "M1"]),
        },
    };

    private static Dictionary<string, string> WithSecret() => new()
    {
        ["VLLM_LOGGING_LEVEL"] = "INFO",
        ["HF_TOKEN"] = Secret,
    };

    private static async Task<AgentException> RejectedAsync(Harness h, LaunchSpec spec)
    {
        var ex = await Assert.ThrowsAsync<AgentException>(() => h.LaunchAsync(spec));
        Assert.Equal(ErrorCodes.SpecRejected, ex.Code);
        Assert.Equal(422, ex.StatusCode);
        Assert.Empty(h.Docker.Calls);
        Assert.Throws<AgentException>(() => h.Driver.CheckSpec("m1", spec));
        return ex;
    }

    // ------------------------------------------------------------ digests

    [Fact]
    public async Task TagOnlySpec_IsRefused_WhenDigestsAreRequired()
    {
        var h = new Harness();

        var ex = await RejectedAsync(h, Spec());

        Assert.Contains("approved image digest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TagOnlySpec_KeepsTheP1Path_WhenTagsAreAllowed()
    {
        var h = new Harness(PinnedOptions(require: false));
        h.Docker.AddImage(Tag, Digest);

        await h.LaunchAsync(Spec());

        var body = h.Docker.CreateBodies.Single();
        Assert.Equal(Tag, body["Image"]!.GetValue<string>());
        Assert.Null(body["Env"]);
        var labels = body["Labels"]!.AsObject();
        Assert.False(labels.ContainsKey(ContainerLaunchValidator.ImageLabel));
        Assert.False(labels.ContainsKey(ContainerLaunchValidator.ImageDigestLabel));
        Assert.False(labels.ContainsKey(ContainerLaunchValidator.EnvNamesLabel));
        Assert.Contains($"GET /v1.47/images/{Tag}/json", h.Docker.Calls);
    }

    [Fact]
    public async Task DigestMismatches_AreRefused()
    {
        var h = new Harness();

        await RejectedAsync(h, Spec(digest: OtherDigest));                                    // not approved
        await RejectedAsync(h, Spec(image: "evil/vllm:v0.11", digest: Digest));             // approved digest, other repository
        await RejectedAsync(h, Spec(image: Repo + "@" + OtherDigest, digest: Digest));       // reference pins another digest
        await RejectedAsync(h, Spec(digest: "sha256:ABC"));                                  // not a digest
    }

    [Fact]
    public async Task ApprovedDigest_CreatesFromRepoAtDigest_AndKeepsTheTagForTheHash()
    {
        var h = new Harness();
        h.Docker.AddImage(Tag, Digest);
        var spec = Spec(digest: Digest);

        await h.LaunchAsync(spec);

        var body = h.Docker.CreateBodies.Single();
        Assert.Equal(Pinned, body["Image"]!.GetValue<string>());
        Assert.Equal(Tag, body["Labels"]![ContainerLaunchValidator.ImageLabel]!.GetValue<string>());
        Assert.Equal(Digest, body["Labels"]![ContainerLaunchValidator.ImageDigestLabel]!.GetValue<string>());
        Assert.DoesNotContain(h.Docker.Calls, c => c.Contains("images/create", StringComparison.Ordinal));

        var config = await h.Driver.GetLaunchConfigAsync("m1");
        Assert.NotNull(config);
        Assert.Equal(Tag, config.Image);
        Assert.Equal(Digest, config.ImageDigest);
        Assert.Equal(spec.Labels[ContainerLabels.ConfigHash], ModelConfigHash.Compute(config.Image!, config.Argv));
    }

    [Fact]
    public async Task TagRepointedToAnotherImage_StillRunsTheApprovedDigest()
    {
        var h = new Harness();
        h.Docker.AddImage(Tag, OtherDigest);         // the local tag now points elsewhere
        h.Docker.Registry[Repo] = (Tag, Digest);     // the approved image is still in the registry

        await h.LaunchAsync(Spec(digest: Digest));

        Assert.Contains($"POST /v1.47/images/create?fromImage={Uri.EscapeDataString(Repo)}&tag={Uri.EscapeDataString(Digest)}", h.Docker.Calls);
        Assert.Equal(Pinned, h.Docker.Containers.Single().Image);
        Assert.Contains("reached:" + OperationStages.Pulling, h.Stages.Events);
    }

    [Theory]
    [InlineData("host:5000/a/b@sha256:0000000000000000000000000000000000000000000000000000000000000000", "host:5000/a/b", "sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("a/b:1.0@sha256:0000000000000000000000000000000000000000000000000000000000000000", "a/b", "sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("host:5000/a/b:1.0", "host:5000/a/b", "1.0")]
    [InlineData("a/b", "a/b", "latest")]
    public async Task Pull_SplitsTagsAndDigests(string image, string fromImage, string tag)
    {
        var docker = new PolicyDocker();

        await Assert.ThrowsAsync<DockerApiException>(() => docker.Client.PullImageAsync(image));

        Assert.Equal($"POST /v1.47/images/create?fromImage={Uri.EscapeDataString(fromImage)}&tag={Uri.EscapeDataString(tag)}", docker.Calls.Single());
    }

    // ------------------------------------------------------------ environment

    [Fact]
    public async Task Environment_GoesToDocker_LabelsHoldNamesOnly()
    {
        var h = new Harness();
        h.Docker.AddImage(Tag, Digest);

        await h.LaunchAsync(Spec(digest: Digest, env: WithSecret(), secrets: ["HF_TOKEN"]));

        var body = h.Docker.CreateBodies.Single();
        Assert.Equal(["VLLM_LOGGING_LEVEL=INFO", "HF_TOKEN=" + Secret], body["Env"]!.AsArray().Select(e => e!.GetValue<string>()));
        var labels = h.Docker.Containers.Single().Labels;
        Assert.Equal("VLLM_LOGGING_LEVEL,HF_TOKEN", labels[ContainerLaunchValidator.EnvNamesLabel]);
        Assert.Equal("HF_TOKEN", labels[ContainerLaunchValidator.SecretEnvNamesLabel]);
        Assert.DoesNotContain(labels.Values, v => v.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, (await h.Driver.GetAsync("m1"))!.Labels.Values);
    }

    [Fact]
    public async Task LaunchConfig_ReportsTheLaunchEnvironmentAndMounts_NeverSecretValues()
    {
        using var dirs = new TempModels();
        var options = PinnedOptions(modelsRoot: "/srv/m");
        options.ModelsLocalPath = dirs.Root;
        var h = new Harness(options);
        h.Docker.AddImage(Tag, Digest);
        MountSpec[] mounts =
        [
            new MountSpec(LogicalVolumes.Models, "ckpt", LogicalVolumes.ModelsTarget, ReadOnly: true),
            new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, ReadOnly: false),
        ];

        await h.LaunchAsync(Spec(digest: Digest, env: WithSecret(), secrets: ["HF_TOKEN"], mounts: mounts));
        var config = await h.Driver.GetLaunchConfigAsync("m1");

        Assert.NotNull(config);
        Assert.Equal(2, config.Env.Count);                    // the image's PATH is not part of the launch
        Assert.Equal("INFO", config.Env["VLLM_LOGGING_LEVEL"]);
        Assert.True(config.Env.ContainsKey("HF_TOKEN"));
        Assert.Null(config.Env["HF_TOKEN"]);
        Assert.Equal(mounts, config.Mounts);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(config, AgentJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LaunchConfig_OfAContainerFromBeforeP2_HasNoEnvironment_AndItsBindsAsMounts()
    {
        var h = new Harness(PinnedOptions(require: false));
        h.Docker.AddContainer("pre-1", "m1", Tag,
            new Dictionary<string, string> { [ContainerLabels.Model] = "m1", [ContainerLabels.Port] = "8101" },
            env: ["PATH=/usr/bin", "NVIDIA_VISIBLE_DEVICES=all"],
            binds: ["/srv/m:/models:ro", "/srv/c:/root/.cache", "/etc/other:/etc/other:ro"]);

        var config = await h.Driver.GetLaunchConfigAsync("m1");

        Assert.NotNull(config);
        Assert.Equal(Tag, config.Image);
        Assert.Null(config.ImageDigest);
        Assert.Empty(config.Env);
        Assert.Equal(
            [
                new MountSpec(LogicalVolumes.Models, null, LogicalVolumes.ModelsTarget, true),
                new MountSpec(LogicalVolumes.Cache, null, LogicalVolumes.CacheTarget, false),
            ],
            config.Mounts);
    }

    [Theory]
    [InlineData(@"C:\data\models", @"C:\data\models:/models:ro", null, true)]
    [InlineData("/srv/m", "/srv/m/sub/dir:/models:ro", "sub/dir", true)]
    [InlineData("/srv/m/", "/srv/m:/models", null, false)]
    public void Mounts_AreReadBackFromBinds_IncludingWindowsRootsAndSubPaths(string root, string bind, string? subPath, bool readOnly)
    {
        var h = new Harness(PinnedOptions(modelsRoot: root));

        var mount = Assert.Single(h.Driver.ReadMounts([bind]));

        Assert.Equal(new MountSpec(LogicalVolumes.Models, subPath, LogicalVolumes.ModelsTarget, readOnly), mount);
        Assert.Empty(h.Driver.ReadMounts(["/srv/other:/models:ro", "/srv/m:/models-x:ro"]));
    }

    public static TheoryData<Dictionary<string, string>, string[]> BadEnvironments => new()
    {
        { new Dictionary<string, string> { ["1ABC"] = "x" }, [] },
        { new Dictionary<string, string> { ["A-B"] = "x" }, [] },
        { new Dictionary<string, string> { ["A=B"] = "x" }, [] },
        { new Dictionary<string, string> { ["NVIDIA_VISIBLE_DEVICES"] = "all" }, [] },
        { new Dictionary<string, string> { ["CUDA_VISIBLE_DEVICES"] = "0" }, [] },
        { new Dictionary<string, string> { ["A"] = "x\0y" }, [] },
        { new Dictionary<string, string> { ["A"] = "x" }, ["HF_TOKEN"] },
        { new Dictionary<string, string> { ["HF_TOKEN"] = Secret }, ["HF_TOKEN", "HF_TOKEN"] },
        { new Dictionary<string, string>(), ["HF_TOKEN"] },
        { Enumerable.Range(0, ContainerLaunchValidator.MaxEnvVariables + 1).ToDictionary(i => "V" + i.ToString(CultureInfo.InvariantCulture), _ => "x"), [] },
    };

    [Theory]
    [MemberData(nameof(BadEnvironments))]
    public async Task BadEnvironments_AreRefused_WithoutShowingValues(Dictionary<string, string> env, string[] secrets)
    {
        var h = new Harness();

        var ex = await RejectedAsync(h, Spec(digest: Digest, env: env, secrets: secrets));

        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, ex.Error.ReadDetails<SpecRejectedDetails>()!.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ContainerLaunchValidator.ImageLabel)]
    [InlineData(ContainerLaunchValidator.EnvNamesLabel)]
    [InlineData(ContainerLaunchValidator.SecretEnvNamesLabel)]
    public async Task DriverLabels_CannotBeSpoofedBySpecs(string label)
    {
        var h = new Harness();
        var spec = Spec(digest: Digest);

        await RejectedAsync(h, spec with { Labels = new Dictionary<string, string>(spec.Labels) { [label] = "x" } });
    }

    [Fact]
    public async Task DockerErrorsThatEchoTheRequest_AreRedacted()
    {
        var h = new Harness();
        h.Docker.AddImage(Tag, Digest);
        h.Docker.CreateError = "invalid config: HF_TOKEN=" + Secret;

        var ex = await Assert.ThrowsAsync<DockerApiException>(() => h.LaunchAsync(Spec(digest: Digest, env: WithSecret(), secrets: ["HF_TOKEN"])));

        Assert.DoesNotContain(Secret, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, ex.ResponseBody, StringComparison.Ordinal);
        Assert.Contains("HF_TOKEN=" + Protocol.RedactedValue, ex.ResponseBody, StringComparison.Ordinal);
    }

    [Fact]
    public void PlansAndRequests_NeverPrintSecretValues()
    {
        var plan = ContainerLaunchValidator.Validate("m1", Spec(digest: Digest, env: WithSecret(), secrets: ["HF_TOKEN"]), PinnedOptions());
        var request = new CreateContainerRequest("ss-m1", Pinned, [], new Dictionary<string, string>(), [], 8000, 8101, "127.0.0.1", "n", "a", "host", plan.Environment.ToDockerEnv());
        var config = new ContainerLaunchConfig(Pinned, [], ["HF_TOKEN=" + Secret], []);

        Assert.Contains("VLLM_LOGGING_LEVEL=INFO", plan.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, plan.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, request.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, config.ToString(), StringComparison.Ordinal);
        Assert.Equal(["HF_TOKEN"], plan.Environment.SecretNames);
        Assert.Equal(Pinned, plan.CreateImage);
    }

    // ------------------------------------------------------------ feature flags

    private static NodeCapabilities BaseCapabilities(params string[] features) => new()
    {
        Protocol = new ProtocolRange(1, 1),
        AgentVersion = "1.0.0+test",
        Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
        Arch = "x86_64",
        Memory = new NodeMemoryInfo(MemoryModel.Discrete, 64),
        Features = features,
    };

    [Theory]
    [InlineData(true, new[] { AgentFeatures.RuntimeVllmContainer, AgentFeatures.LaunchEnv, AgentFeatures.ImagesDigestPinned })]
    [InlineData(false, new[] { AgentFeatures.RuntimeVllmContainer, AgentFeatures.LaunchEnv })]
    public async Task TheContributor_ReportsTheFlagsOfTheContainerDriver(bool requireDigest, string[] expected)
    {
        var h = new Harness(PinnedOptions(require: requireDigest));
        var contributor = new LaunchFeaturesContributor([h.Driver]);

        var once = await contributor.ContributeAsync(BaseCapabilities(AgentFeatures.RuntimeVllmContainer));
        var twice = await contributor.ContributeAsync(once);

        Assert.Equal(expected, once.Features);
        Assert.Equal(expected, twice.Features);
    }

    [Fact]
    public async Task TheContributor_ReportsNothing_WithoutTheContainerDriver()
    {
        var current = BaseCapabilities(AgentFeatures.TelemetryV1);

        Assert.Same(current, await new LaunchFeaturesContributor([]).ContributeAsync(current));
    }

    [Theory]
    [InlineData(false, new[] { AgentFeatures.LaunchEnv, AgentFeatures.ImagesDigestPinned })]
    [InlineData(true, new[] { AgentFeatures.LaunchEnv })]
    public async Task TheRealAgentsDriver_GivesTheFlagsItsConfigurationImplies(bool allowTagPulls, string[] expected)
    {
        await using var agent = await RealAgentHarness.StartAsync(configure: o => o.AllowTagPulls = allowTagPulls, ct: TestContext.Current.CancellationToken);
        var contributor = new LaunchFeaturesContributor(agent.App.Services.GetServices<IRuntimeDriver>());

        var report = await contributor.ContributeAsync(BaseCapabilities());

        Assert.Equal(expected, report.Features);
    }

    // ------------------------------------------------------------ master: node default image + digest, env gating

    private static NodeRecord Node(string? defaultImage = null, string? digest = null, params string[] features) => new()
    {
        Id = NodeId,
        Uid = "uid-pc",
        Address = "https://192.0.2.50:9443",
        DefaultVllmImage = defaultImage,
        DefaultVllmImageDigest = digest,
        LastCapabilities = BaseCapabilities(features),
    };

    [Fact]
    public void Builder_SendsTheNodeDefaultDigest_AndKeepsTheTagAndLegacyHash()
    {
        var pinnedNode = Node(Tag, Digest);
        var unpinnedNode = Node(Tag);
        string defaultImage = LaunchSpecBuilder.DefaultImage(pinnedNode, new MirbadLLMOptions { VllmImage = "global" });

        var pinned = LaunchSpecBuilder.Build(Saved, pinnedNode, defaultImage, 64, tuning: false);
        var unpinned = LaunchSpecBuilder.Build(Saved, unpinnedNode, defaultImage, 64, tuning: false);

        Assert.Equal(Tag, pinned.Spec.Image);
        Assert.Equal(Digest, pinned.Spec.ImageDigest);
        Assert.Null(unpinned.Spec.ImageDigest);
        Assert.Equal(ModelConfigHash.Compute(Saved, Tag, 64), pinned.ConfigHash);
        Assert.Equal(unpinned.ConfigHash, pinned.ConfigHash);
        Assert.NotEqual(unpinned.ExecutionFingerprint, pinned.ExecutionFingerprint); // the digest is part of the fingerprint

        // The agent accepts what the master built and creates it from the pinned reference.
        var plan = ContainerLaunchValidator.Validate("m1", pinned.Spec, PinnedOptions());
        Assert.Equal(Pinned, plan.CreateImage);
    }

    [Theory]
    [InlineData("own/image:1", null)]       // a model's own image carries no digest in the P2 definition
    [InlineData(null, "global")]            // the global fallback has none either
    public void Builder_SendsNoDigest_ForImagesOtherThanTheNodeDefault(string? own, string? defaultImageOverride)
    {
        var node = Node(Tag, Digest);
        string defaultImage = defaultImageOverride ?? LaunchSpecBuilder.DefaultImage(node, new MirbadLLMOptions());

        var built = LaunchSpecBuilder.Build(Saved with { Image = own }, node, defaultImage, 64, tuning: false);

        Assert.Null(built.Spec.ImageDigest);
    }

    [Fact]
    public void Builder_WithoutDefaultsOrEnvironment_BuildsTheP1Spec()
    {
        var dgx = new NodeRecord { Id = "dgx", Uid = "uid-1", Address = "https://127.0.0.1:9443" };
        var withFlags = dgx with { LastCapabilities = BaseCapabilities(AgentFeatures.LaunchEnv, AgentFeatures.ImagesDigestPinned) };

        var p1 = LaunchSpecBuilder.Build(Saved, dgx, "default-image", 120, tuning: false);
        var p2 = LaunchSpecBuilder.Build(Saved, withFlags, "default-image", 120, tuning: false, LaunchEnvironment.None);

        Assert.Equal(JsonSerializer.Serialize(p1.Spec, AgentJson.Options), JsonSerializer.Serialize(p2.Spec, AgentJson.Options));
        Assert.Null(p1.Spec.ImageDigest);
        Assert.Empty(p1.Spec.Env);
        Assert.Equal(ModelConfigHash.Compute(Saved, "default-image", 120), p1.ConfigHash);
    }

    [Fact]
    public void Builder_SendsEnvironment_OnlyToNodesReportingLaunchEnv()
    {
        var environment = new LaunchEnvironment(WithSecret(), ["HF_TOKEN"]);

        var refused = Assert.Throws<NotSupportedException>(() => LaunchSpecBuilder.Build(Saved, Node(), "img", 64, tuning: false, environment));
        Assert.Contains(AgentFeatures.LaunchEnv, refused.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() =>
            LaunchSpecBuilder.Build(Saved, Node(features: AgentFeatures.LaunchEnv), "img", 64, tuning: false, environment, nodeFeatures: []));

        var built = LaunchSpecBuilder.Build(Saved, Node(features: AgentFeatures.LaunchEnv), "img", 64, tuning: false, environment);
        var live = LaunchSpecBuilder.Build(Saved, Node(), "img", 64, tuning: false, environment, nodeFeatures: [AgentFeatures.LaunchEnv]);

        Assert.Equal(WithSecret(), built.Spec.Env);
        Assert.Equal(["HF_TOKEN"], built.Spec.SecretEnvNames);
        Assert.Equal(built.Spec.Env, live.Spec.Env);
        Assert.Equal(ExecutionFingerprint.Compute(built.Spec, "uid-pc", [], built.ConfigHash), built.ExecutionFingerprint);
        Assert.DoesNotContain(Secret, built.ExecutionFingerprint, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, environment.ToString(), StringComparison.Ordinal);

        // And the agent's validator accepts it.
        Assert.False(ContainerLaunchValidator.Validate("m1", built.Spec with { ImageDigest = null }, PinnedOptions(require: false)).Environment.IsEmpty);
    }

    // ------------------------------------------------------------ fakes

    private sealed class TempModels : IDisposable
    {
        public TempModels()
        {
            Directory.CreateDirectory(Path.Combine(Root, "ckpt"));
        }

        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ss-imagepolicy-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class PolicyContainer
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public required string Image { get; init; }

        public string State { get; set; } = "created";

        public required Dictionary<string, string> Labels { get; init; }

        public List<string> Cmd { get; init; } = [];

        public List<string> Env { get; init; } = [];

        public List<string> Binds { get; init; } = [];
    }

    /// <summary>
    /// A Docker Engine with images addressable by tag and by repository digest (as Docker resolves them), a registry for
    /// pulls by tag or digest, and containers that keep their <c>Env</c> and <c>Binds</c> (the image's <c>PATH</c> merged in).
    /// </summary>
    private sealed class PolicyDocker
    {
        private FakeHttpMessageHandler Handler { get; }
        private readonly List<(string Tag, string Digest)> _images = [];
        private int _next;

        public PolicyDocker()
        {
            Handler = new FakeHttpMessageHandler { Handler = Handle };
            Client = new DockerEngineClient(new HttpClient(Handler) { BaseAddress = new Uri("http://docker/v1.47/") });
        }

        public DockerEngineClient Client { get; }

        public List<string> Calls { get; } = [];

        public List<JsonNode> CreateBodies { get; } = [];

        public List<PolicyContainer> Containers { get; } = [];

        public Dictionary<string, (string Tag, string Digest)> Registry { get; } = new(StringComparer.Ordinal);

        public string? CreateError { get; set; }

        public void AddImage(string tag, string digest) => _images.Add((tag, digest));

        public void AddContainer(string id, string modelId, string image, Dictionary<string, string> labels, List<string> env, List<string> binds) =>
            Containers.Add(new PolicyContainer { Id = id, Name = "ss-" + modelId, Image = image, State = "running", Labels = labels, Env = env, Binds = binds });

        private bool Resolves(string reference) =>
            _images.Any(i => i.Tag == reference || ContainerLaunchValidator.Repository(i.Tag) + "@" + i.Digest == reference);

        private HttpResponseMessage Handle(HttpRequestMessage request)
        {
            string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            string query = request.RequestUri.Query;
            Calls.Add($"{request.Method} {path}{query}");
            string body = Handler.RequestBodies[^1];
            path = path["/v1.47/".Length..];

            if (path == "containers/json")
            {
                return Json(Containers.Where(c => Matches(c, query)).Select(c => new
                {
                    c.Id,
                    Names = new[] { "/" + c.Name },
                    c.State,
                    Status = "",
                    Created = 1790640000L,
                    c.Labels,
                }));
            }

            if (path == "containers/create")
            {
                if (CreateError is not null)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($$"""{"message":"{{CreateError}}"}""") };
                }

                var spec = JsonNode.Parse(body)!;
                CreateBodies.Add(spec);
                string image = spec["Image"]!.GetValue<string>();
                if (!Resolves(image))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"No such image"}""") };
                }

                var container = new PolicyContainer
                {
                    Id = "new-" + (++_next).ToString(CultureInfo.InvariantCulture),
                    Name = Uri.UnescapeDataString(query["?name=".Length..]),
                    Image = image,
                    Labels = spec["Labels"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()),
                    Cmd = [.. spec["Cmd"]!.AsArray().Select(a => a!.GetValue<string>())],
                    Env = ["PATH=/usr/local/bin:/usr/bin", .. spec["Env"]?.AsArray().Select(e => e!.GetValue<string>()) ?? []],
                    Binds = [.. spec["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>())],
                };
                Containers.Add(container);
                return Json(new { container.Id });
            }

            if (path.StartsWith("containers/", StringComparison.Ordinal))
            {
                string[] parts = path["containers/".Length..].Split('/', 2);
                var container = Containers.FirstOrDefault(c => c.Id == parts[0]);
                if (container is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
                }

                string action = parts.Length > 1 ? parts[1] : "";
                switch (request.Method.Method, action)
                {
                    case ("GET", "json"):
                        return Json(new
                        {
                            RestartCount = 0,
                            State = new { ExitCode = 0, OOMKilled = false },
                            Config = new { container.Image, container.Cmd, container.Env },
                            HostConfig = new { container.Binds },
                        });
                    case ("POST", "start"):
                        container.State = "running";
                        return new HttpResponseMessage(HttpStatusCode.NoContent);
                    case ("GET", "logs"):
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
                    default:
                        return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
            }

            if (path.StartsWith("networks/", StringComparison.Ordinal))
            {
                return Json(new { });
            }

            if (path == "images/create")
            {
                var q = query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                if (Registry.TryGetValue(q["fromImage"], out var entry) && (entry.Tag.EndsWith(":" + q["tag"], StringComparison.Ordinal) || entry.Digest == q["tag"]))
                {
                    _images.Add((entry.Tag, entry.Digest));
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"status":"Digest: {{entry.Digest}}"}""") };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"manifest unknown"}""") };
            }

            if (path.StartsWith("images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal))
            {
                return Resolves(path["images/".Length..^"/json".Length])
                    ? Json(new { Id = "sha256:img" })
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        private static bool Matches(PolicyContainer container, string query)
        {
            int start = query.IndexOf("filters=", StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(Uri.UnescapeDataString(query[(start + "filters=".Length)..]));
            return doc.RootElement.GetProperty("label").EnumerateArray().Select(e => e.GetString()!).All(f =>
            {
                int eq = f.IndexOf('=', StringComparison.Ordinal);
                return eq < 0 ? container.Labels.ContainsKey(f) : container.Labels.TryGetValue(f[..eq], out string? v) && v == f[(eq + 1)..];
            });
        }

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
