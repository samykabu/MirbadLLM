using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

#pragma warning disable CA1707, CA2000 // underscore test names; HttpResponseMessage ownership passes to HttpClient in fakes.

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-214: <c>Agent:Docker:Enabled = false</c> (design-p3p4 §A.4.3). The real Docker-facing contributors (the container
/// runtime's capability and <c>docker_unreachable</c>, the storage package's image listing) folded by the real
/// <see cref="AgentCapabilityReporter"/>: with Docker off they never reach the Docker Engine and the report carries no
/// Docker feature, runtime, image or reason; with Docker on (the default, every Linux agent and the DGX) the switch is the
/// identity and the report is byte-identical to the one without the switch.
/// </summary>
public sealed class NoDockerModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ss-mn214-" + Guid.NewGuid().ToString("N"));

    public NoDockerModeTests() => Directory.CreateDirectory(Path.Combine(_root, "Qwen3-Embedding-4B-GGUF"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ the switch

    [Fact]
    public void Switch_DefaultsToOn_AndReadsAgentDockerEnabled()
    {
        Assert.True(DockerAvailability.From(Config()).Enabled);
        Assert.True(DockerAvailability.From(Config(("Agent:Docker:Enabled", "true"))).Enabled);
        Assert.False(DockerAvailability.From(Config(("Agent:Docker:Enabled", "false"))).Enabled);
        Assert.False(DockerAvailability.From(Config(("Agent:Docker:Enabled", "False"))).Enabled);

        var options = new AgentOptions();
        Assert.Same(DockerAvailability.On, DockerAvailability.From(options));
        Config(("Agent:Docker:Enabled", "false")).GetSection("Agent").Bind(options);
        Assert.Same(DockerAvailability.Off, DockerAvailability.From(options));
    }

    [Fact]
    public void AgentJson_DockerSection_TurnsDockerOff()
    {
        // The shape agent.windows.example.json gets from MN-216: "Agent": { "Docker": { "Enabled": false } }.
        const string json = """{ "Agent": { "NodeId": "win-1", "Docker": { "Enabled": false } } }""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        Assert.False(DockerAvailability.From(configuration).Enabled);
        var options = new AgentOptions();
        configuration.GetSection(AgentOptions.SectionName).Bind(options);
        Assert.False(DockerAvailability.From(options).Enabled);
    }

    // ------------------------------------------------------------------ capabilities

    [Fact]
    public async Task DockerOff_ReportHasNoDockerFeatures_AndDockerIsNeverCalled()
    {
        var docker = new CountingDocker(HttpStatusCode.OK, "[]");
        var report = await Fold(DockerAvailability.Off, docker);

        Assert.Equal(0, docker.Calls);
        Assert.Empty(report.Features.Intersect(DockerAvailability.DockerFeatures));
        Assert.DoesNotContain(report.Runtimes, r => r.Delivery == LaunchDelivery.Container);
        Assert.Empty(report.Images);

        // Non-Docker parts stay: the model roots with their folder count.
        var models = Assert.Single(report.ModelRoots, r => r.Name == LogicalVolumes.Models);
        Assert.Equal(1, models.FolderCount);
    }

    [Fact]
    public async Task DockerOn_IsTheIdentity_ByteForByte()
    {
        var gated = new CountingDocker(HttpStatusCode.OK, "[]");
        var plain = new CountingDocker(HttpStatusCode.OK, "[]");

        var withSwitch = await Fold(DockerAvailability.On, gated);
        var without = await Fold(docker: null, plain);

        Assert.True(gated.Calls > 0);
        Assert.Contains(AgentFeatures.RuntimeVllmContainer, withSwitch.Features);
        Assert.Contains(AgentFeatures.DownloadsHfContainer, withSwitch.Features);
        RuntimeKind[] expected = OperatingSystem.IsLinux()
            ? [RuntimeKind.Vllm, RuntimeKind.LlamaCpp, RuntimeKind.Sglang]
            : [RuntimeKind.Vllm];
        Assert.Equal(expected, withSwitch.Runtimes.Select(runtime => runtime.Kind));
        Assert.All(withSwitch.Runtimes, runtime =>
        {
            Assert.Equal(LaunchDelivery.Container, runtime.Delivery);
            Assert.True(runtime.Available);
        });
        Assert.Equal(
            JsonSerializer.Serialize(without, AgentJson.Options),
            JsonSerializer.Serialize(withSwitch, AgentJson.Options));
    }

    [Fact]
    public void DgxCapabilityFixture_IsUnchangedWithDockerOn()
    {
        string path = Path.Combine(FixturesPath(), "NodeCapabilities.json");
        string json = File.ReadAllText(path);
        var dgx = JsonSerializer.Deserialize<NodeCapabilities>(json, AgentJson.Options)!;

        var applied = DockerAvailability.On.Apply(dgx);

        Assert.Same(dgx, applied);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(JsonSerializer.Serialize(applied, AgentJson.Options))));
    }

    [Fact]
    public void DockerOff_StripsWhatMixedContributorsAdd()
    {
        var dgx = JsonSerializer.Deserialize<NodeCapabilities>(File.ReadAllText(Path.Combine(FixturesPath(), "NodeCapabilities.json")), AgentJson.Options)!;
        var report = dgx with
        {
            Features = [.. dgx.Features, AgentFeatures.ServicesDocker, AgentFeatures.ImagesDigestPinned, AgentFeatures.LaunchEnv, AgentFeatures.RuntimeLlamaCppProcess],
            Runtimes =
            [
                .. dgx.Runtimes,
                new RuntimeCapability { Kind = RuntimeKind.LlamaCpp, Delivery = LaunchDelivery.Process, Available = true },
            ],
            HostServices = new HostServicesCapability("systemd", HostdAvailable: true, HostdVersion: null, Containers: true),
        };

        var stripped = DockerAvailability.Off.Apply(report);

        Assert.Equal(["services.systemd", "gpu-processes", "telemetry.v1", "launch.env", "runtime.llamacpp.process"], stripped.Features);
        Assert.Equal(LaunchDelivery.Process, Assert.Single(stripped.Runtimes).Delivery);
        Assert.Empty(stripped.Images);
        Assert.False(stripped.HostServices!.Containers);
        Assert.Equal("systemd", stripped.HostServices.Manager);

        // Nothing Docker left: the same instance comes back.
        Assert.Same(stripped, DockerAvailability.Off.Apply(stripped));
    }

    // ------------------------------------------------------------------ health

    [Fact]
    public async Task DockerOff_NoDockerHealthReason_EvenWithADeadDaemon()
    {
        var ct = TestContext.Current.CancellationToken;
        var dead = new CountingDocker(HttpStatusCode.InternalServerError, "{\"message\":\"daemon is down\"}");
        var runtime = RuntimeContributor(dead);

        var on = await DockerAvailability.On.Gate((IHealthContributor)runtime).CheckAsync(ct);
        Assert.Equal(HealthReasonCodes.DockerUnreachable, Assert.Single(on).Code);
        int callsWhileOn = dead.Calls;
        Assert.True(callsWhileOn > 0);

        var off = await DockerAvailability.Off.Gate((IHealthContributor)new RuntimeCapabilityContributorProbe(RuntimeContributor(dead))).CheckAsync(ct);
        Assert.Empty(off);
        Assert.Equal(callsWhileOn, dead.Calls);
    }

    [Fact]
    public async Task DockerOff_HealthFilterDropsOnlyDockerReasons()
    {
        var ct = TestContext.Current.CancellationToken;
        var mixed = new FixedHealth(
            new HealthReason(HealthReasonCodes.DockerUnreachable, "Docker is unreachable: no pipe."),
            new HealthReason(HealthReasonCodes.ModelRootLow, "The models volume has 3.0 % free (below 5 %)."));

        Assert.Equal([HealthReasonCodes.ModelRootLow], (await DockerAvailability.Off.Filter(mixed).CheckAsync(ct)).Select(r => r.Code));
        Assert.Same(mixed, DockerAvailability.On.Filter(mixed));
        Assert.Equal(2, (await DockerAvailability.On.Filter(mixed).CheckAsync(ct)).Count);
    }

    // ------------------------------------------------------------------ the start-up wait and images

    [Fact]
    public async Task DockerOff_NoStartupWait()
    {
        var ct = TestContext.Current.CancellationToken;
        int waits = 0;
        Task<bool> Wait(CancellationToken _)
        {
            waits++;
            return Task.FromResult(true);
        }

        Assert.False(await DockerAvailability.Off.WaitForDockerAsync(Wait, ct));
        Assert.Equal(0, waits);
        Assert.True(await DockerAvailability.On.WaitForDockerAsync(Wait, ct));
        Assert.Equal(1, waits);

        // Through the composition: with Docker off the probe is not even resolved (none is registered here).
        using var empty = new ServiceCollection().BuildServiceProvider();
        Assert.False(await DockerAvailability.Off.WaitForDockerAsync(empty, TimeSpan.FromMinutes(2), ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DockerAvailability.On.WaitForDockerAsync(empty, TimeSpan.FromMinutes(2), ct));
    }

    [Fact]
    public async Task DockerOff_ImageEngineListsNothing_AndRefusesPulls()
    {
        var ct = TestContext.Current.CancellationToken;
        var docker = new CountingDocker(HttpStatusCode.OK, "[]");
        var engine = DockerAvailability.Off.Gate(new DockerImageClient(docker.Client()));

        Assert.Empty(await engine.ListAsync(ct));
        Assert.Null(await engine.InspectAsync("vllm/vllm-openai:latest", ct));
        await Assert.ThrowsAsync<NotSupportedException>(() => engine.PullAsync("vllm/vllm-openai", "latest", _ => { }, ct));
        Assert.Equal(0, docker.Calls);

        var real = new DockerImageClient(docker.Client());
        Assert.Same(real, DockerAvailability.On.Gate(real));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The capability fold with the real contributors that touch Docker: the container runtime (through
    /// <c>DockerProbe</c>) and storage (images through <see cref="DockerImageClient"/>), gated and filtered by
    /// <paramref name="docker"/> as the composition does, or ungated when null (today's composition).
    /// </summary>
    private async Task<NodeCapabilities> Fold(DockerAvailability? docker, CountingDocker engine)
    {
        ICapabilityContributor runtime = RuntimeContributor(engine);
        IContainerImageEngine images = new DockerImageClient(engine.Client());
        var contributors = new List<ICapabilityContributor>
        {
            docker is null ? runtime : docker.Gate(runtime),
            new StorageCapabilities(
                docker is null ? images : docker.Gate(images),
                new ModelRootPaths(_root),
                new FixedDisk(),
                Options.Create(new AgentStorageOptions { ModelsRoot = _root })),
        };
        if (docker is not null)
        {
            contributors.Add(docker.Filter());
        }

        var reporter = new AgentCapabilityReporter(
            new AgentBootInfo(TimeProvider.System, "1.3.0-test"), contributors, NullLogger<AgentCapabilityReporter>.Instance, new FixedMemory());
        return await reporter.GetAsync(TestContext.Current.CancellationToken);
    }

    private static RuntimeCapabilityContributor RuntimeContributor(CountingDocker engine) =>
        new(new DockerProbe(new DockerEngineClient(engine.Client()), TimeProvider.System, NullLogger<DockerProbe>.Instance));

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    private static string FixturesPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "Contracts", "Fixtures");

    /// <summary>The Docker Engine API as a counting fake: every request is counted and answered the same way.</summary>
    private sealed class CountingDocker(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public HttpClient Client() => new(this, disposeHandler: false) { BaseAddress = new Uri("http://docker/v1.47/") };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    /// <summary>Forwards to a runtime contributor's health check (a distinct instance for the Off gate).</summary>
    private sealed class RuntimeCapabilityContributorProbe(RuntimeCapabilityContributor inner) : IHealthContributor
    {
        public Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default) => inner.CheckAsync(ct);
    }

    private sealed class FixedHealth(params HealthReason[] reasons) : IHealthContributor
    {
        public Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HealthReason>>(reasons);
    }

    private sealed class FixedDisk : IDiskSpaceProbe
    {
        public DiskSpace? Probe(string path) => new(FreeBytes: 805_306_368_000, TotalBytes: 2_000_398_934_016);
    }

    private sealed class FixedMemory : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new MemorySnapshot(127, 90));
    }
}
