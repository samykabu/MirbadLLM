using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Proxy;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Agent.Storage;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;
using AgentProgram = MirbadLLM.Agent.Program;

namespace MirbadLLM.UnitTests.Agent;

/// <summary>
/// MN-026: the shared agent contract suite (MN-022) against the <b>real</b> agent, composed by
/// <c>Program.CreateAsync</c> exactly as <c>mirbadllm-agent</c> runs, over real mTLS + HTTP/2 on loopback. Only the
/// node's outside world is fake: the Docker Engine (<see cref="ContractDocker"/>), hostd, the Hugging Face Hub,
/// <c>nvidia-smi</c>, <c>/proc/meminfo</c>, and the models' upstream (<see cref="FakeModelServer"/>).
/// <para>Launch environment variables and mounts in <c>/launch-config</c> run since MN-107, GPU selection by UUID
/// (<c>gpu_missing</c>) since MN-106.</para>
/// </summary>
public sealed class RealAgentContractTests : AgentContractSuite
{
    /// <inheritdoc/>
    protected override async Task<IAgentContractHarness> StartAgentAsync() => await RealAgentHarness.StartAsync(ct: Ct);

    /// <inheritdoc/>
    protected override async Task<IAgentContractHarness> StartAgentWithoutHostFeaturesAsync() =>
        await RealAgentHarness.StartAsync(configure: o =>
        {
            o.ManageSystemdUnits = false;
            o.ManageContainers = false;
            o.ManageBackupMount = false;
        }, ct: Ct);
}

/// <summary>MN-026 composition: one boot id, routes, feature gate, start order, identity checks, restart, configuration.</summary>
public sealed class RealAgentCompositionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InfoStatusEventsAndTelemetryReportOneBootId()
    {
        await using var agent = await RealAgentHarness.StartAsync(ct: Ct);
        using var client = new AgentContractClient(agent.Target);

        var info = (await client.SendAsync(AgentRoutes.Info, [], ct: Ct)).Read<InfoResponse>();
        var status = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>();
        TelemetryPage? page = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (page is not { Samples.Count: > 0 })
        {
            Assert.True(DateTime.UtcNow < deadline, "No telemetry sample.");
            page = (await client.SendAsync(AgentRoutes.Telemetry, [], ct: Ct)).Read<TelemetryPage>();
            await Task.Delay(50, Ct);
        }

        await using var events = await client.OpenEventsAsync(info.BootId, status.Seq, ct: Ct);
        var heartbeat = Assert.IsType<HeartbeatEvent>((await events.NextAsync(Protocol.HeartbeatInterval + TimeSpan.FromSeconds(5), Ct))?.Event);

        Assert.Equal(info.BootId, status.BootId);
        Assert.Equal(info.BootId, page.BootId);
        Assert.Equal(info.BootId, heartbeat.BootId);
        Assert.Equal(agent.App.Services.GetRequiredService<AgentBootInfo>().BootId, info.BootId);
    }

    [Fact]
    public async Task EveryCatalogueRouteResolvesToExactlyOneEndpoint()
    {
        await using var agent = await RealAgentHarness.StartAsync(ct: Ct);
        var endpoints = agent.App.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>().ToList();
        var catalog = agent.App.Services.GetRequiredService<AgentRouteCatalog>();

        foreach (var route in AgentRoutes.All)
        {
            var matches = endpoints.Where(e => Methods(e).Any(m => m == route.Method || route.Method == AgentHttpMethods.Any || m == "*")
                && catalog.Find(e, route.Method == AgentHttpMethods.Any ? AgentHttpMethods.Get : route.Method) == route).ToList();
            Assert.True(matches.Count == 1, $"{route.Method} {route.Path}: {matches.Count} endpoints.");
        }

        static IEnumerable<string> Methods(RouteEndpoint endpoint) =>
            endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["*"];
    }

    [Fact]
    public async Task OneReservationManagerAndEveryRecoveryHandlerAndCancellerAreComposed()
    {
        await using var agent = await RealAgentHarness.StartAsync(ct: Ct);
        var services = agent.App.Services;

        var manager = Assert.IsType<ReservationManager>(services.GetRequiredService<IReservationManager>());
        Assert.Same(manager, services.GetRequiredService<IPortAllocator>());
        Assert.Same(manager, services.GetRequiredService<ReservationManager>());
        Assert.Single(services.GetServices<IRuntimeDriver>());
        Assert.Same(services.GetRequiredService<IEventBus>(), services.GetRequiredService<IEventBus>());

        var recovered = services.GetServices<IOperationRecoveryHandler>().SelectMany(h => h.Kinds).ToList();
        Assert.Equal(recovered.Count, recovered.Distinct(StringComparer.Ordinal).Count());
        foreach (string kind in new[]
                 {
                     OperationKinds.ServiceRestart, OperationKinds.ContainerStop, OperationKinds.BackupMountSet,
                     OperationKinds.Download, OperationKinds.ImagePull, OperationKinds.FolderDelete,
                     OperationKinds.PkiRotationStart, OperationKinds.PkiRotationCommit, OperationKinds.PkiTrustSet,
                 })
        {
            Assert.Contains(kind, recovered);
        }

        var cancellable = services.GetServices<IOperationCanceller>().SelectMany(c => c.Kinds).ToList();
        Assert.Contains(OperationKinds.Launch, cancellable);
        Assert.Contains(OperationKinds.ImagePull, cancellable);
        Assert.Contains(OperationKinds.Download, cancellable);
    }

    [Fact]
    public async Task RoutesOfDisabledFeaturesAreNotSupportedAndTheCapabilitiesSayWhy()
    {
        await using var agent = await RealAgentHarness.StartAsync(configure: o =>
        {
            o.ManageSystemdUnits = false;
            o.ManageBackupMount = false;
        }, ct: Ct);
        using var client = new AgentContractClient(agent.Target);

        var caps = (await client.SendAsync(AgentRoutes.Capabilities, [], ct: Ct)).Read<NodeCapabilities>();
        var services = await client.SendAsync(AgentRoutes.HostServiceStart, [AgentContractSeed.Service],
            new HostServiceActionRequest { OperationId = AgentContractSuite.NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) }, ct: Ct);
        var mount = await client.SendAsync(AgentRoutes.BackupMountGet, [], ct: Ct);
        var containers = await client.SendAsync(AgentRoutes.HostContainerGet, [AgentContractSeed.Container], ct: Ct);

        Assert.DoesNotContain(AgentFeatures.ServicesSystemd, caps.Features);
        Assert.DoesNotContain(AgentFeatures.BackupMount, caps.Features);
        Assert.Contains(AgentFeatures.ServicesDocker, caps.Features);
        Assert.Contains(AgentFeatures.RuntimeVllmContainer, caps.Features);
        Assert.Contains(caps.Runtimes, r => r.Kind == RuntimeKind.Vllm && r.Delivery == LaunchDelivery.Container && r.Available);
        Assert.Equal(501, services.Status);
        Assert.Contains(AgentFeatures.ServicesSystemd, services.Read<AgentErrorResponse>().Error.Message, StringComparison.Ordinal);
        Assert.Equal(ErrorCodes.NotSupported, services.Read<AgentErrorResponse>().Error.Code);
        Assert.Equal(501, mount.Status);
        Assert.Equal(200, containers.Status);
        Assert.Null((await client.SendAsync(AgentRoutes.Info, [], ct: Ct)).Read<InfoResponse>().AcceptedMaster);
    }

    [Fact]
    public async Task TheAgentRefusesToStartWithoutCertificates()
    {
        await using var node = await ContractNode.CreateAsync(enroll: false, Ct);

        var refused = await Assert.ThrowsAsync<AgentStartupException>(() => node.BuildAgentAsync(ct: Ct));

        Assert.Contains("not enrolled", refused.Message, StringComparison.Ordinal);
        Assert.Empty(node.Docker.Calls);
        Assert.False(File.Exists(Path.Combine(node.StateDirectory, "fencing.json")));
    }

    [Fact]
    public async Task BootstrapModeWithoutABundleRefusesToStartWhenNotEnrolled()
    {
        await using var node = await ContractNode.CreateAsync(enroll: false, Ct);
        string bundles = Path.Combine(node.Root, "bootstrap");
        Directory.CreateDirectory(bundles);

        var refused = await Assert.ThrowsAsync<AgentStartupException>(() => node.BuildAgentAsync(o =>
        {
            o.BootstrapDirectory = bundles;
            o.BootstrapWait = TimeSpan.Zero;
        }, Ct));

        Assert.Contains(BootstrapBundle.FileName, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BootstrapModeStartsWithTheExistingIdentityWhenNoBundleIsWaiting()
    {
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        string bundles = Path.Combine(node.Root, "bootstrap");
        Directory.CreateDirectory(bundles);

        await using var agent = await RealAgentHarness.StartAsync(node, o =>
        {
            o.BootstrapDirectory = bundles;
            o.BootstrapWait = TimeSpan.FromMinutes(5);
        }, Ct);
        using var client = new AgentContractClient(agent.Target);

        Assert.Equal(200, (await client.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
    }

    [Fact]
    public async Task AnEnrolledAgentWithABundleForAnUnreachableMasterStartsWithItsIdentity()
    {
        // W4 review MN-096: an agent that still has an identity retries an unreachable enroll listener only briefly (at
        // most a minute), then serves with that identity and leaves the bundle for the next start.
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        string bundles = Path.Combine(node.Root, "bootstrap");
        Directory.CreateDirectory(bundles);
        string bundlePath = Path.Combine(bundles, BootstrapBundle.FileName);
        await File.WriteAllTextAsync(bundlePath, JsonSerializer.Serialize(new BootstrapBundle
        {
            NodeId = node.NodeId,
            Token = "unused-token",
            CaCertPem = node.Ca.CertificatePem,
            CaSpkiSha256 = node.Ca.SpkiSha256,
            EnrollEndpoint = new Uri("https://127.0.0.1:1"),
            CreatedAt = DateTimeOffset.UtcNow,
        }, AgentJson.Options), Ct);

        await using var agent = await RealAgentHarness.StartAsync(node, o =>
        {
            o.BootstrapDirectory = bundles;
            o.BootstrapWait = TimeSpan.FromSeconds(2);
        }, Ct);
        using var client = new AgentContractClient(agent.Target);

        Assert.Equal(200, (await client.SendAsync(AgentRoutes.Info, [], ct: Ct)).Status);
        Assert.True(File.Exists(bundlePath));
        Assert.True(AgentProgram.EnrolledBootstrapWait <= TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ACertificateForAnotherNodeIsRefused()
    {
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);

        var refused = await Assert.ThrowsAsync<AgentStartupException>(() => node.BuildAgentAsync(o => o.NodeId = "other-node", Ct));

        Assert.Contains(node.NodeId, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReconciliationAdoptsWhatRunsBeforeTheListenerOpens()
    {
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        node.Docker.AddModelContainer("legacy-model", "running", port: 8150);

        await using var agent = await RealAgentHarness.StartAsync(node, ct: Ct);
        using var client = new AgentContractClient(agent.Target);

        var runtimes = (await client.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct)).Read<List<RuntimeStatus>>();
        var adopted = Assert.Single(runtimes);
        Assert.Equal("legacy-model", adopted.ModelId);
        Assert.Equal(8150, adopted.HostPort);
        var first = node.Docker.Calls.FindIndex(c => c.Contains("/containers/json", StringComparison.Ordinal));
        Assert.True(first >= 0);
    }

    [Fact]
    public async Task DockerDownAtStartIsWaitedForThenReportedAsDegraded()
    {
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        node.Docker.Down = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await using var agent = await RealAgentHarness.StartAsync(node, o => o.DockerStartupTimeout = TimeSpan.FromSeconds(1), Ct);
        using var client = new AgentContractClient(agent.Target);
        var status = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>();
        var caps = (await client.SendAsync(AgentRoutes.Capabilities, [], ct: Ct)).Read<NodeCapabilities>();

        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1));
        Assert.Contains(status.Health.Reasons, r => r.Code == HealthReasonCodes.DockerUnreachable);
        Assert.Contains(caps.Runtimes, r => r.Kind == RuntimeKind.Vllm && !r.Available);
    }

    [Fact]
    public async Task ADownloadWhileDockerIsDownIsDockerUnavailableNotUnsupported()
    {
        // Features are capabilities, not health (W4 review MN-096): the route stays offered and says why it cannot run.
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        node.Docker.Down = true;
        node.Downloads.DockerDown = true;

        await using var agent = await RealAgentHarness.StartAsync(node, o => o.DockerStartupTimeout = TimeSpan.Zero, Ct);
        using var client = new AgentContractClient(agent.Target);
        var caps = (await client.SendAsync(AgentRoutes.Capabilities, [], ct: Ct)).Read<NodeCapabilities>();
        var reply = await client.SendAsync(AgentRoutes.DownloadStart, [], AgentContractSuite.Download("model-new"), ct: Ct);

        Assert.Contains(AgentFeatures.DownloadsHfContainer, caps.Features);
        Assert.Equal(ErrorCodes.DockerUnavailable, reply.Read<AgentErrorResponse>().Error.Code);
        Assert.Equal(ErrorMappings.AgentStatus(ErrorCodes.DockerUnavailable), reply.Status);
    }

    [Fact]
    public async Task ALaunchInterruptedByARestartFinishesAfterReconciliation()
    {
        await using var node = await ContractNode.CreateAsync(enroll: true, Ct);
        var launch = AgentContractSuite.LaunchFor("contract-a");
        string oldBoot;
        await using (var first = await RealAgentHarness.StartAsync(node, ct: Ct))
        {
            await first.SetStartsHeldAsync(true);
            using var client = new AgentContractClient(first.Target);
            Assert.Equal(202, (await client.SendAsync(AgentRoutes.RuntimeLaunch, ["contract-a"], launch, ct: Ct)).Status);
            await EventuallyAsync(async () =>
                (await client.SendAsync(AgentRoutes.RuntimeGet, ["contract-a"], ct: Ct)) is { Status: 200 } r && r.Read<RuntimeStatus>().State == ModelState.Starting,
                "the instance to start");
            oldBoot = (await client.SendAsync(AgentRoutes.Info, [], ct: Ct)).Read<InfoResponse>().BootId;
        }

        node.StartsHeld = false;
        await using var second = await RealAgentHarness.StartAsync(node, ct: Ct);
        using var again = new AgentContractClient(second.Target);

        var info = (await again.SendAsync(AgentRoutes.Info, [], ct: Ct)).Read<InfoResponse>();
        Assert.NotEqual(oldBoot, info.BootId);
        Assert.Equal(again.Master, info.AcceptedMaster);
        OperationStatus? final = null;
        await EventuallyAsync(async () =>
        {
            final = (await again.SendAsync(AgentRoutes.OperationGet, [launch.OperationId], ct: Ct)).Read<OperationStatus>();
            return final.State.IsTerminal();
        }, "the launch to finish");
        Assert.Equal(OperationState.Succeeded, final!.State);
        var runtime = (await again.SendAsync(AgentRoutes.RuntimeGet, ["contract-a"], ct: Ct)).Read<RuntimeStatus>();
        Assert.Equal(ModelState.Ready, runtime.State);
        Assert.Single(node.Docker.ModelContainers);
        await EventuallyAsync(async () => (await again.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>().Reservations.Count == 0,
            "the reservation to be released");
    }

    [Fact]
    public async Task StoppingEndsOpenEventStreamsAndReturnsWithinTheShutdownTimeout()
    {
        await using var agent = await RealAgentHarness.StartAsync(configure: o => o.ShutdownTimeout = TimeSpan.FromSeconds(20), ct: Ct);
        using var client = new AgentContractClient(agent.Target);
        var status = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>();
        await using var events = await client.OpenEventsAsync(status.BootId, status.Seq, ct: Ct);
        Assert.Equal(200, events.Status);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await agent.App.StopAsync(Ct);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Stopping took {watch.Elapsed}.");
        Assert.Null(await events.NextAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public void AgentJsonBindsEveryOptionAndValidates()
    {
        string json = """
            {
              "Agent": {
                "NodeId": "dgx",
                "StateDirectory": "/var/lib/mirbadllm-agent",
                "ListenAddress": "0.0.0.0",
                "Port": 9444,
                "ModelsRoot": "/models",
                "ModelsHostPath": "/srv/m",
                "CacheRoot": "/cache",
                "CacheHostPath": "/srv/c",
                "DockerEndpoint": "unix:///run/docker.sock",
                "DockerNetwork": "net",
                "UseContainerNetwork": false,
                "UpstreamHost": "10.0.0.1",
                "DockerStartupTimeout": "00:00:30",
                "PortRangeStart": 9000,
                "PortRangeEnd": 9100,
                "MemoryReserveGiB": 4,
                "ApprovedImages": [ "vllm/vllm-openai@sha256:1111111111111111111111111111111111111111111111111111111111111111" ],
                "AllowTagPulls": true,
                "HostdSocket": "/run/h.sock",
                "ManageBackupMount": false,
                "BootstrapDirectory": "/run/mirbadllm-bootstrap",
                "Addresses": [ "192.0.2.191" ]
              }
            }
            """;
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var options = AgentOptions.FromConfiguration(configuration);

        Assert.Equal("dgx", options.NodeId);
        Assert.Equal(9444, options.Port);
        Assert.Equal(TimeSpan.FromSeconds(30), options.DockerStartupTimeout);
        Assert.Equal(["vllm/vllm-openai@sha256:1111111111111111111111111111111111111111111111111111111111111111"], options.ApprovedImages);
        Assert.Equal(["192.0.2.191"], options.Addresses);
        var runtime = options.ToContainerRuntimeOptions();
        Assert.Equal("dgx", runtime.NodeId);
        Assert.Equal("/srv/m", runtime.ModelsHostPath);
        Assert.Equal("/models", runtime.ModelsLocalPath);
        Assert.False(runtime.UseContainerNetwork);
        Assert.False(runtime.RequireApprovedDigest);
        Assert.Single(runtime.ApprovedImages);
        var storage = new AgentStorageOptions();
        options.ConfigureStorage(storage);
        Assert.True(storage.AllowTagPulls);
        Assert.Single(storage.ApprovedImages);
        Assert.Equal(9000, options.ToAdmissionOptions().PortRangeStart);
        Assert.Equal(4, options.ToAdmissionOptions().DefaultReserveGiB);
        var hostServices = new MirbadLLM.Agent.HostServices.AgentHostServicesOptions();
        options.ConfigureHostServices(hostServices);
        Assert.Equal("/run/h.sock", hostServices.HostdSocket);
        Assert.False(hostServices.BackupMount);
        Assert.Equal("/srv/m", options.ToDownloaderOptions().ModelsHostPath);

        Assert.Throws<ArgumentException>(() => new AgentOptions { NodeId = "DGX!", StateDirectory = "/s" }.Validate());
        Assert.Throws<ArgumentException>(() => new AgentOptions { NodeId = "dgx" }.Validate());
        var badImage = new AgentOptions { NodeId = "dgx", StateDirectory = "/s" };
        badImage.ApprovedImages.Add("vllm/vllm-openai:latest");
        Assert.Throws<ArgumentException>(badImage.Validate);
        new AgentOptions { NodeId = "dgx", StateDirectory = "/s" }.Validate();
    }

    [Fact]
    public void TheCommandLineTakesTheConfigurationPathAndPassesTheRestToTheHost()
    {
        Assert.True(AgentProgram.TryParseArguments(["--config", "/etc/a.json", "--urls", "x"], out string? path, out string[] rest, out _));
        Assert.Equal("/etc/a.json", path);
        Assert.Equal(["--urls", "x"], rest);
        Assert.False(AgentProgram.TryParseArguments(["--config"], out _, out _, out string? error));
        Assert.Contains("--config", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DockerCallsAreBoundedButStreamsAreNot()
    {
        var slow = new SlowHandler();
        using var client = new HttpClient(new DockerCallTimeoutHandler(TimeSpan.FromMilliseconds(200)) { InnerHandler = slow })
        {
            BaseAddress = new Uri("http://docker/v1.47/"),
        };

        await Assert.ThrowsAsync<TimeoutException>(() => client.PostAsync("containers/abc/start", null, Ct));
        using var pull = await client.PostAsync("images/create?fromImage=x&tag=1", null, Ct);
        using var logs = await client.GetAsync("containers/abc/logs?stdout=1&follow=true", Ct);

        Assert.Equal(HttpStatusCode.OK, pull.StatusCode);
        Assert.Equal(HttpStatusCode.OK, logs.StatusCode);
        Assert.True(DockerCallTimeoutHandler.IsStreaming(new Uri("http://docker/v1.47/images/create?fromImage=x")));
        Assert.False(DockerCallTimeoutHandler.IsStreaming(new Uri("http://docker/v1.47/containers/a/logs?follow=false")));
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(20, Ct);
        }
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

/// <summary>
/// The real agent in process: <c>Program.CreateAsync</c> over a <see cref="ContractNode"/>, listening with mTLS on a
/// random loopback port. Starting it runs the whole start order (credentials, Docker wait, reconciliation, routes).
/// </summary>
internal sealed class RealAgentHarness : IAgentContractHarness
{
    private readonly ContractNode _node;
    private readonly bool _ownsNode;

    private RealAgentHarness(ContractNode node, bool ownsNode, WebApplication app, Uri baseAddress)
    {
        _node = node;
        _ownsNode = ownsNode;
        App = app;
        BaseAddress = baseAddress;
    }

    public WebApplication App { get; }

    public Uri BaseAddress { get; }

    public AgentContractTarget Target => new(BaseAddress, _node.NodeId, _node.Ca, _node.MasterCertificate, _node.AgentSpki);

    /// <summary>Starts an agent over <paramref name="node"/> (a fresh node owned by the harness when null).</summary>
    public static async Task<RealAgentHarness> StartAsync(ContractNode? node = null, Action<AgentOptions>? configure = null, CancellationToken ct = default)
    {
        bool owns = node is null;
        node ??= await ContractNode.CreateAsync(enroll: true, ct);
        try
        {
            WebApplication app = await node.BuildAgentAsync(configure, ct);
            try
            {
                await app.StartAsync(ct);
                string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
                return new RealAgentHarness(node, owns, app, new Uri(address.TrimEnd('/') + "/"));
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }
        catch
        {
            if (owns)
            {
                await node.DisposeAsync();
            }

            throw;
        }
    }

    public Task SetStartsHeldAsync(bool held)
    {
        _node.StartsHeld = held;
        return Task.CompletedTask;
    }

    public Task SetStartsFailingAsync(bool failing)
    {
        _node.Docker.StartsFailing = failing;
        return Task.CompletedTask;
    }

    public Task SetPullsHeldAsync(bool held)
    {
        _node.Docker.SetPullsHeld(held);
        return Task.CompletedTask;
    }

    public Task SetDownloadsHeldAsync(bool held)
    {
        _node.Downloads.Held = held;
        return Task.CompletedTask;
    }

    public Task SetDockerAvailableAsync(bool available)
    {
        _node.Docker.Down = !available;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        // Release whatever a test left held, so stopping does not wait on a fake.
        _node.StartsHeld = false;
        _node.Docker.SetPullsHeld(false);
        _node.Docker.SetStopsHeld(false);
        _node.Downloads.Held = false;
        try
        {
            await App.StopAsync(CancellationToken.None);
        }
        finally
        {
            await App.DisposeAsync();
            if (_ownsNode)
            {
                await _node.DisposeAsync();
            }
        }
    }
}

/// <summary>
/// What survives an agent restart on one node: the state, models and cache directories, the master CA and the agent's
/// enrolled identity, Docker, hostd, the Hugging Face Hub, and the model server behind every instance.
/// </summary>
internal sealed class ContractNode : IAsyncDisposable
{
    private ContractNode(string root, CertificateAuthority ca, IssuedCredential master, FakeModelServer model, string nodeId)
    {
        NodeId = nodeId;
        Root = root;
        Ca = ca;
        MasterCredential = master;
        MasterCertificate = master.CreateTlsCertificate();
        Model = model;
        StateDirectory = Path.Combine(root, "state");
        ModelsRoot = Path.Combine(root, "models");
        CacheRoot = Path.Combine(root, "cache");
        Docker = new ContractDocker();
        Hostd = new ContractHostd();
        Downloads = new ContractDownloads(ModelsRoot);
    }

    public string NodeId { get; }

    public string NodeUid { get; } = "uid-" + Guid.NewGuid().ToString("N")[..12];

    public string Root { get; }

    public string StateDirectory { get; }

    public string ModelsRoot { get; }

    public string CacheRoot { get; }

    public CertificateAuthority Ca { get; }

    public IssuedCredential MasterCredential { get; }

    public X509Certificate2 MasterCertificate { get; }

    public string AgentSpki { get; private set; } = "";

    public FakeModelServer Model { get; }

    public ContractDocker Docker { get; }

    public ContractHostd Hostd { get; }

    public ContractDownloads Downloads { get; }

    /// <summary>While set, every instance's <c>/health</c> answers 503 (the model is still loading).</summary>
    public volatile bool StartsHeld;

    /// <summary>The node's <c>nvidia-smi</c> (MN-130 composition tests: a GB10 or discrete GPUs); null = not installed.</summary>
    public INvidiaSmiRunner? NvidiaSmi { get; set; }

    /// <summary>More agent services, registered after the node's fakes (MN-123 exit tests: a log capture).</summary>
    public Action<IServiceCollection>? ExtraServices { get; set; }

    /// <summary>A node with its directories, fakes and seed folders; <paramref name="enroll"/> writes an identity as <c>join</c> would.</summary>
    public static async Task<ContractNode> CreateAsync(bool enroll, CancellationToken ct, string nodeId = "contract-node")
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-real-agent-" + Guid.NewGuid().ToString("N")[..12]);
        var ca = CertificateAuthority.Create();
        var model = await FakeModelServer.CreateAsync(ct: ct);
        var node = new ContractNode(root, ca, ca.IssueMasterClientCertificate(), model, nodeId);
        Directory.CreateDirectory(node.StateDirectory);
        Directory.CreateDirectory(node.CacheRoot);
        node.SeedFolder(AgentContractSeed.ModelFolder);
        node.SeedFolder(AgentContractSeed.ScratchFolder);
        if (enroll)
        {
            node.Enroll();
        }

        return node;
    }

    /// <summary>Writes the identity enrollment would have written (key, CA, node uid, certificate).</summary>
    public void Enroll()
    {
        using ECDsa key = PkiDefaults.CreateKey();
        using IssuedCertificate certificate = Ca.IssueAgentCertificate(CertificateSigningRequests.CreatePem(key, NodeId), NodeId, ["127.0.0.1"]);
        PkiFiles.WriteSecret(Path.Combine(StateDirectory, FileAgentCredentials.KeyFileName), key.ExportPkcs8PrivateKeyPem());
        PkiFiles.WritePublic(Path.Combine(StateDirectory, FileAgentCredentials.CaFileName), Ca.CertificatePem);
        PkiFiles.WritePublic(Path.Combine(StateDirectory, FileAgentCredentials.NodeUidFileName), NodeUid + "\n");
        PkiFiles.WritePublic(Path.Combine(StateDirectory, FileAgentCredentials.CertificateFileName), certificate.CertificatePem);
        AgentSpki = certificate.SpkiSha256;
    }

    /// <summary>The options a DGX-like node would have in <c>agent.json</c>, pointed at this node's directories.</summary>
    public AgentOptions CreateOptions()
    {
        var options = new AgentOptions
        {
            NodeId = NodeId,
            StateDirectory = StateDirectory,
            ListenAddress = "127.0.0.1",
            Port = 0,
            ModelsRoot = ModelsRoot,
            ModelsHostPath = ModelsRoot,
            CacheRoot = CacheRoot,
            CacheHostPath = CacheRoot,
            DockerEndpoint = "tcp://docker.invalid:2375",
            UseContainerNetwork = false,
            DockerStartupTimeout = TimeSpan.FromSeconds(10),
            HostdSocket = Path.Combine(Root, "hostd.sock"),
            HostdExpected = true,
            ShutdownTimeout = TimeSpan.FromSeconds(10),
        };
        options.ApprovedImages.Add(ImageReference.Pinned(ImageReference.Parse(AgentContractSeed.Image).Repository, AgentContractSeed.ImageDigest));
        options.ApprovedImages.Add(ImageReference.Pinned(ImageReference.Parse(AgentContractSeed.PullImage).Repository, AgentContractSeed.PullImageDigest));
        return options;
    }

    /// <summary>
    /// Composes the agent with <c>Program.CreateAsync</c>; only the node's outside world is replaced (registered before
    /// the packages, so their <c>TryAdd</c> defaults step aside).
    /// </summary>
    public Task<WebApplication> BuildAgentAsync(Action<AgentOptions>? configure = null, CancellationToken ct = default)
    {
        AgentOptions options = CreateOptions();
        configure?.Invoke(options);
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = Root });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        return AgentProgram.CreateAsync(builder, options, startupLoggers: null, configureServices: services =>
        {
            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new TransportFilter(this));
            services.AddSingleton<IMemoryProbe>(new FixedMemoryProbe());
            services.AddSingleton<IDiskSpaceProbe>(new FixedDiskProbe());
            services.AddSingleton<INvidiaSmiRunner>(NvidiaSmi ?? new NoNvidiaSmi());
            services.AddSingleton<IHostdClient>(Hostd);
            services.AddSingleton<IHuggingFaceDownloader>(Downloads);
            services.AddSingleton<IHuggingFaceRepoInspector>(Downloads);
            services.AddSingleton<IProxyUpstreamResolver>(sp => new ModelServerResolver(sp.GetServices<IRuntimeDriver>(), Model));
            services.AddSingleton(new TelemetryHttpClient(new ContractTransport(this)));
            services.AddSingleton(new AgentRuntimeOptions { WatchInterval = TimeSpan.FromMilliseconds(100) });
            services.Configure<TelemetryOptions>(o => o.SampleInterval = TimeSpan.FromMilliseconds(250));
            services.Configure<AgentStorageOptions>(o =>
            {
                o.DownloadPollInterval = TimeSpan.FromMilliseconds(50);
                o.ProgressRecordInterval = TimeSpan.FromMilliseconds(200);
            });
            ExtraServices?.Invoke(services);
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await ((IAsyncDisposable)Model).DisposeAsync();
        // MasterCertificate is left to the finalizer: test clients present it, and a connection attempt a disposed client
        // abandoned can still be in its handshake; disposing it under SChannel is a use-after-free (MN-047).
        MasterCredential.Dispose();
        Ca.Dispose();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100);
            }
        }
    }

    private void SeedFolder(string folder)
    {
        string path = Path.Combine(ModelsRoot, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "config.json"), """{"architectures":["LlamaForCausalLM"],"model_type":"llama","hidden_size":64,"num_hidden_layers":2}""");
        File.WriteAllBytes(Path.Combine(path, "model.safetensors"), new byte[1024]);
    }

    private sealed class FixedMemoryProbe : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new MemorySnapshot(128, 100));
    }

    // Host disk pressure must not change the fake node's health in composition tests.
    internal sealed class FixedDiskProbe : IDiskSpaceProbe
    {
        public DiskSpace? Probe(string path) => new(100L << 30, 128L << 30);
    }

    private sealed class NoNvidiaSmi : INvidiaSmiRunner
    {
        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default) => Task.FromResult(NvidiaSmiResult.NotFound);
    }

    /// <summary>Every named <see cref="HttpClient"/> of the agent (Docker, upstream health) goes to the fakes.</summary>
    private sealed class TransportFilter(ContractNode node) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = new ContractTransport(node);
        };
    }

    /// <summary>The proxy reaches the one <see cref="FakeModelServer"/> for every model the driver says is running.</summary>
    private sealed class ModelServerResolver(IEnumerable<IRuntimeDriver> drivers, FakeModelServer model) : IProxyUpstreamResolver
    {
        public async ValueTask<Uri?> ResolveAsync(string modelId, CancellationToken ct)
        {
            foreach (var driver in drivers)
            {
                if (await driver.GetUpstreamAsync(modelId, ct) is not null)
                {
                    return model.BaseUri;
                }
            }

            return null;
        }

        public void Invalidate(string modelId, Uri upstream)
        {
        }
    }
}

/// <summary>
/// The node's network as the agent sees it: <c>http://docker/…</c> is the Docker Engine (<see cref="ContractDocker"/>);
/// anything else is a model's published port, served by the node's <see cref="FakeModelServer"/> (with <c>/health</c>
/// answering 503 while starts are held).
/// </summary>
internal sealed class ContractTransport(ContractNode node) : HttpMessageHandler
{
    private static readonly HttpClient ModelClient = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request.RequestUri);
        if (request.RequestUri.Host == "docker")
        {
            return await node.Docker.HandleAsync(request, cancellationToken);
        }

        if (request.RequestUri.AbsolutePath == "/health" && node.StartsHeld)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        using var forward = new HttpRequestMessage(request.Method, new Uri(node.Model.BaseUri, request.RequestUri.PathAndQuery.TrimStart('/')));
        if (request.Content is not null)
        {
            forward.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
        }

        var response = await ModelClient.SendAsync(forward, cancellationToken);
        var copy = new HttpResponseMessage(response.StatusCode)
        {
            Content = new ByteArrayContent(await response.Content.ReadAsByteArrayAsync(cancellationToken)),
        };
        response.Dispose();
        return copy;
    }
}

/// <summary>
/// A stateful Docker Engine API (v1.47) for one node, thread-safe: model containers (create/start/stop/remove/inspect/
/// logs, label filters), images (list, inspect, pull from a tiny registry with a hold knob), networks, and non-model
/// containers for the host-service routes (inspect by name, start/stop/restart, restart policy, stats). Seeded with the
/// contract fixture: <see cref="AgentContractSeed.Image"/> present, <see cref="AgentContractSeed.PullImage"/> pullable,
/// <see cref="AgentContractSeed.Container"/> running.
/// </summary>
internal sealed class ContractDocker
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UtcNow;
    private readonly Lock _gate = new();
    private readonly List<FakeImage> _images = [];
    private readonly List<FakeContainer> _containers = [];
    private readonly Dictionary<string, (string Tag, string Digest)> _registry = new(StringComparer.Ordinal);
    private readonly List<string> _calls = [];
    private readonly List<string> _createBodies = [];
    private TaskCompletionSource _pulls = Released();
    private TaskCompletionSource _stops = Released();
    private int _next;

    public ContractDocker()
    {
        var image = ImageReference.Parse(AgentContractSeed.Image);
        _images.Add(new FakeImage(NewImageId(), [AgentContractSeed.Image], [ImageReference.Pinned(image.Repository, AgentContractSeed.ImageDigest)]));
        var pull = ImageReference.Parse(AgentContractSeed.PullImage);
        _registry[pull.Repository] = (pull.Tag!, AgentContractSeed.PullImageDigest);
        _containers.Add(new FakeContainer("c-contract", AgentContractSeed.Container, "local/contract-tool:1", "running", new Dictionary<string, string>(), [], "no"));
        _containers.Add(new FakeContainer("c-master", "mirbadllm", "local/mirbadllm:1", "running", new Dictionary<string, string>(), [], "unless-stopped"));
        _containers.Add(new FakeContainer("c-agent", "mirbadllm-agent", "local/mirbadllm-agent:1", "running", new Dictionary<string, string>(), [], "unless-stopped"));
    }

    /// <summary>Every call fails as if the daemon were down.</summary>
    public volatile bool Down;

    /// <summary>Model containers exit at once when started (the launch fails).</summary>
    public volatile bool StartsFailing;

    public List<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The JSON body of every <c>POST /containers/create</c>, in order (MN-130 composition tests).</summary>
    public List<string> CreateBodies
    {
        get
        {
            lock (_gate)
            {
                return [.. _createBodies];
            }
        }
    }

    public List<string> ModelContainers
    {
        get
        {
            lock (_gate)
            {
                return [.. _containers.Where(c => c.Labels.ContainsKey(ContainerLabels.Model)).Select(c => c.Name)];
            }
        }
    }

    public void SetPullsHeld(bool held)
    {
        lock (_gate)
        {
            if (held && _pulls.Task.IsCompleted)
            {
                _pulls = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else if (!held)
            {
                _pulls.TrySetResult();
            }
        }
    }

    /// <summary>While held, every <c>POST /containers/{id}/stop</c> waits (MN-123 exit tests: a stop in progress).</summary>
    public void SetStopsHeld(bool held)
    {
        lock (_gate)
        {
            if (held && _stops.Task.IsCompleted)
            {
                _stops = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else if (!held)
            {
                _stops.TrySetResult();
            }
        }
    }

    /// <summary>A model container as today's controller creates it (no node or operation label), e.g. to be adopted.</summary>
    public void AddModelContainer(string modelId, string state, int port)
    {
        lock (_gate)
        {
            _containers.Add(new FakeContainer(
                "pre-" + modelId, "ss-" + modelId, AgentContractSeed.Image, state,
                new Dictionary<string, string>
                {
                    [ContainerLabels.Model] = modelId,
                    [ContainerLabels.Port] = port.ToString(CultureInfo.InvariantCulture),
                    [ContainerLabels.ServedName] = modelId,
                },
                ["--model", "/models/" + AgentContractSeed.ModelFolder], "unless-stopped") { StartedAt = DateTimeOffset.UtcNow });
        }
    }

    public async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Down)
        {
            throw new HttpRequestException("Connection refused (docker.sock)", null, HttpStatusCode.ServiceUnavailable);
        }

        string path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
        int version = path.IndexOf("/v1.", StringComparison.Ordinal);
        if (version >= 0)
        {
            path = path[(path.IndexOf('/', version + 1) + 1)..];
        }

        var query = ParseQuery(request.RequestUri.Query);
        string method = request.Method.Method;
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        lock (_gate)
        {
            _calls.Add($"{method} /{path}{request.RequestUri.Query}");
        }

        if (method == "POST" && path == "images/create")
        {
            return await PullAsync(query, ct);
        }

        if (method == "POST" && path.StartsWith("containers/", StringComparison.Ordinal) && path.EndsWith("/stop", StringComparison.Ordinal))
        {
            Task stops;
            lock (_gate)
            {
                stops = _stops.Task;
            }

            await stops.WaitAsync(ct);
        }

        lock (_gate)
        {
            return Handle(method, path, query, body);
        }
    }

    private HttpResponseMessage Handle(string method, string path, Dictionary<string, string> query, string? body)
    {
        switch (method, path)
        {
            case ("GET", "containers/json"):
                return ListContainers(query);
            case ("POST", "containers/create"):
                _createBodies.Add(body!);
                return CreateContainer(query["name"], body!);
            case ("GET", "images/json"):
                return Json(_images.Select(i => new { i.Id, i.RepoTags, i.RepoDigests, Size = 19L << 30 }));
            case ("POST", "networks/create"):
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"Id":"net"}""") };
        }

        if (path.StartsWith("networks/", StringComparison.Ordinal) && method == "GET")
        {
            return Json(new { Name = path["networks/".Length..] });
        }

        if (path.StartsWith("images/", StringComparison.Ordinal) && path.EndsWith("/json", StringComparison.Ordinal) && method == "GET")
        {
            var image = FindImage(path["images/".Length..^"/json".Length]);
            return image is null
                ? Error(HttpStatusCode.NotFound, "No such image")
                : Json(new { image.Id, image.RepoDigests, image.RepoTags, Architecture = "arm64", Size = 19L << 30 });
        }

        if (path.StartsWith("containers/", StringComparison.Ordinal))
        {
            string rest = path["containers/".Length..];
            int slash = rest.IndexOf('/', StringComparison.Ordinal);
            string key = slash < 0 ? rest : rest[..slash];
            string action = slash < 0 ? "" : rest[(slash + 1)..];
            var container = _containers.FirstOrDefault(c => c.Id == key || c.Name == key);
            if (container is null)
            {
                return Error(HttpStatusCode.NotFound, $"No such container: {key}");
            }

            return ContainerAction(method, action, container, body);
        }

        return Error(HttpStatusCode.BadRequest, $"{method} {path} is not faked");
    }

    private HttpResponseMessage ContainerAction(string method, string action, FakeContainer container, string? body)
    {
        switch (method, action)
        {
            case ("GET", "json"):
                return Json(new
                {
                    container.Id,
                    Name = "/" + container.Name,
                    container.RestartCount,
                    Created = Epoch.ToString("O", CultureInfo.InvariantCulture),
                    State = new
                    {
                        Status = container.State,
                        Running = container.State == "running",
                        container.ExitCode,
                        OOMKilled = false,
                        StartedAt = (container.StartedAt ?? DateTimeOffset.MinValue).ToString("O", CultureInfo.InvariantCulture),
                    },
                    Config = new { container.Image, container.Cmd, container.Labels, container.Env },
                    HostConfig = new { RestartPolicy = new { Name = container.RestartPolicy }, container.Binds },
                });
            case ("GET", "logs"):
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(LogFrames(container)) };
            case ("GET", "stats"):
                return Json(new { memory_stats = new { usage = 64L << 20, stats = new { inactive_file = 0L } } });
            case ("POST", "start"):
                if (container.State == "running")
                {
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                bool fails = StartsFailing && container.Labels.ContainsKey(ContainerLabels.Model);
                container.State = fails ? "exited" : "running";
                container.ExitCode = fails ? 1 : 0;
                container.StartedAt = DateTimeOffset.UtcNow;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case ("POST", "stop"):
                if (container.State != "running")
                {
                    return new HttpResponseMessage(HttpStatusCode.NotModified);
                }

                container.State = "exited";
                container.ExitCode = 0;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case ("POST", "restart"):
                container.State = "running";
                container.StartedAt = DateTimeOffset.UtcNow;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case ("POST", "update"):
                container.RestartPolicy = JsonNode.Parse(body!)?["RestartPolicy"]?["Name"]?.GetValue<string>() ?? container.RestartPolicy;
                return Json(new { Warnings = Array.Empty<string>() });
            case ("DELETE", ""):
                _containers.Remove(container);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            default:
                return Error(HttpStatusCode.BadRequest, $"{method} {action} is not faked");
        }
    }

    private HttpResponseMessage ListContainers(Dictionary<string, string> query)
    {
        bool all = query.TryGetValue("all", out string? a) && a is "true" or "1";
        var filters = new List<string>();
        if (query.TryGetValue("filters", out string? json))
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("label", out var labels))
            {
                filters.AddRange(labels.EnumerateArray().Select(l => l.GetString()!));
            }
        }

        var rows = _containers
            .Where(c => all || c.State == "running")
            .Where(c => filters.All(f =>
            {
                int eq = f.IndexOf('=', StringComparison.Ordinal);
                return eq < 0 ? c.Labels.ContainsKey(f) : c.Labels.TryGetValue(f[..eq], out string? v) && v == f[(eq + 1)..];
            }))
            .Select(c => new
            {
                c.Id,
                Names = new[] { "/" + c.Name },
                c.Image,
                c.State,
                Status = c.State == "running" ? "Up 1 minute" : $"Exited ({c.ExitCode})",
                Created = Epoch.ToUnixTimeSeconds(),
                c.Labels,
            });
        return Json(rows);
    }

    private HttpResponseMessage CreateContainer(string name, string body)
    {
        if (_containers.Any(c => c.Name == name))
        {
            return Error(HttpStatusCode.Conflict, $"Conflict. The container name \"/{name}\" is already in use.");
        }

        var spec = JsonNode.Parse(body)!;
        string image = spec["Image"]!.GetValue<string>();
        if (FindImage(image) is null)
        {
            return Error(HttpStatusCode.NotFound, $"No such image: {image}");
        }

        var labels = spec["Labels"]?.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()) ?? [];
        var cmd = spec["Cmd"]?.AsArray().Select(a => a!.GetValue<string>()).ToList() ?? [];
        string policy = spec["HostConfig"]?["RestartPolicy"]?["Name"]?.GetValue<string>() ?? "no";
        // Docker merges the image's environment in front of the create call's (MN-107).
        List<string> env = ["PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin", .. spec["Env"]?.AsArray().Select(e => e!.GetValue<string>()) ?? []];
        var binds = spec["HostConfig"]?["Binds"]?.AsArray().Select(b => b!.GetValue<string>()).ToList() ?? [];
        string id = "ctr-" + Interlocked.Increment(ref _next).ToString(CultureInfo.InvariantCulture);
        _containers.Add(new FakeContainer(id, name, image, "created", labels, cmd, policy) { Env = env, Binds = binds });
        return Json(new { Id = id, Warnings = Array.Empty<string>() }, HttpStatusCode.Created);
    }

    private async Task<HttpResponseMessage> PullAsync(Dictionary<string, string> query, CancellationToken ct)
    {
        Task held;
        lock (_gate)
        {
            held = _pulls.Task;
        }

        await held.WaitAsync(ct);
        string repository = query["fromImage"];
        string tagOrDigest = query.GetValueOrDefault("tag") ?? "latest";
        lock (_gate)
        {
            if (!_registry.TryGetValue(repository, out var entry) || (entry.Tag != tagOrDigest && entry.Digest != tagOrDigest))
            {
                return Error(HttpStatusCode.NotFound, $"manifest for {repository}:{tagOrDigest} not found: manifest unknown");
            }

            if (FindImage($"{repository}:{entry.Tag}") is null)
            {
                _images.Add(new FakeImage(NewImageId(), [$"{repository}:{entry.Tag}"], [ImageReference.Pinned(repository, entry.Digest)]));
            }

            string progress = string.Join('\n',
                $$"""{"status":"Pulling from {{repository}}","id":"{{entry.Tag}}"}""",
                """{"status":"Pulling fs layer","id":"layer1"}""",
                """{"status":"Downloading","id":"layer1","progressDetail":{"current":512,"total":1024}}""",
                """{"status":"Download complete","id":"layer1"}""",
                """{"status":"Pull complete","id":"layer1"}""",
                $$"""{"status":"Digest: {{entry.Digest}}"}""") + "\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(progress, Encoding.UTF8, "application/json") };
        }
    }

    private FakeImage? FindImage(string reference)
    {
        var parsed = ImageReference.Parse(reference);
        string tagged = parsed.Repository + ":" + (parsed.Tag ?? "latest");
        return _images.FirstOrDefault(i =>
            i.Id == reference ||
            (parsed.Digest is { } digest
                ? i.RepoDigests.Contains(ImageReference.Pinned(parsed.Repository, digest))
                : i.RepoTags.Contains(tagged)));
    }

    private static byte[] LogFrames(FakeContainer container)
    {
        string[] lines = container.State == "exited" && container.ExitCode != 0
            ? ["INFO: loading model", "Traceback (most recent call last):", "torch.OutOfMemoryError: CUDA out of memory. Tried to allocate 2.00 GiB"]
            : ["INFO: Started server process", "INFO: Application startup complete."];
        var frames = new List<byte>();
        foreach (string line in lines)
        {
            byte[] payload = Encoding.UTF8.GetBytes(line + "\n");
            var header = new byte[8];
            header[0] = 1;
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), payload.Length);
            frames.AddRange(header);
            frames.AddRange(payload);
        }

        return [.. frames];
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=', StringComparison.Ordinal);
            string key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            result[key] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return result;
    }

    private static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string message) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json") };

    private static TaskCompletionSource Released()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        released.SetResult();
        return released;
    }

    private static string NewImageId() => "sha256:" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private sealed record FakeImage(string Id, List<string> RepoTags, List<string> RepoDigests);

    private sealed class FakeContainer(
        string id, string name, string image, string state, Dictionary<string, string> labels, List<string> cmd, string restartPolicy)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public string Image { get; } = image;

        public string State { get; set; } = state;

        public int ExitCode { get; set; }

        public int RestartCount { get; }

        public DateTimeOffset? StartedAt { get; set; }

        public Dictionary<string, string> Labels { get; } = labels;

        public List<string> Cmd { get; } = cmd;

        public string RestartPolicy { get; set; } = restartPolicy;

        /// <summary><c>Config.Env</c>: the image's own variables plus the create call's (MN-107).</summary>
        public List<string> Env { get; init; } = [];

        /// <summary><c>HostConfig.Binds</c> (MN-107).</summary>
        public List<string> Binds { get; init; } = [];
    }
}

/// <summary>
/// mirbadllm-hostd for the contract fixture, with hostd's own rules: whitelisted units only, typed name for stopping,
/// restarting or disabling a protected unit, a deny list for discovered units, conflicts on a second add.
/// </summary>
internal sealed class ContractHostd : IHostdClient
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HostUnit> _units = new(StringComparer.Ordinal)
    {
        [AgentContractSeed.Service] = Unit(AgentContractSeed.Service, isProtected: false),
        [AgentContractSeed.ProtectedService] = Unit(AgentContractSeed.ProtectedService, isProtected: true),
    };

    private BackupMountInfo _mount = new() { Target = "/srv/mirbadllm/backups", Unit = "srv-mirbadllm-backups.mount" };

    public bool IsInstalled => true;

    public Task<IReadOnlyList<HostUnit>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<HostUnit>>([.. _units.Values]);
        }
    }

    public Task<HostUnitDiscovery> DiscoverAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var discovered = _units.Values.Select(u => new HostDiscoveredUnit { Name = u.Name, Unit = u.Unit, Scope = u.Scope, Managed = true, ActiveState = u.ActiveState })
                .ToList();
            if (!_units.ContainsKey("contract-discovered"))
            {
                discovered.Add(new HostDiscoveredUnit { Name = "contract-discovered", Unit = "contract-discovered.service", Scope = "system", ActiveState = "active" });
            }

            discovered.Add(new HostDiscoveredUnit { Name = "ssh", Unit = "ssh.service", Scope = "system", Denied = true, DeniedBy = "ssh*" });
            return Task.FromResult(new HostUnitDiscovery { Services = [.. _units.Values], Discovered = discovered, DenyPatterns = ["ssh*"] });
        }
    }

    public Task<HostUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default)
    {
        lock (_gate)
        {
            string name = unit.EndsWith(".service", StringComparison.Ordinal) ? unit[..^".service".Length] : unit;
            if (_units.Values.Any(u => u.Unit == unit))
            {
                throw new HostServiceConflictException($"{unit} is already managed.");
            }

            var added = Unit(name, isProtected) with { Unit = unit, Scope = scope, DisplayName = displayName ?? name };
            _units[name] = added;
            return Task.FromResult(added);
        }
    }

    public Task<HostUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var unit = Find(name);
            _units[name] = unit with { DisplayName = displayName ?? unit.DisplayName, Protected = isProtected ?? unit.Protected };
            return Task.FromResult(_units[name]);
        }
    }

    public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _ = Find(name);
            _units.Remove(name);
            return Task.CompletedTask;
        }
    }

    public Task<HostUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var unit = Find(name);
            if (unit.Protected && action != HostServiceAction.Start && confirm != name)
            {
                throw new HostServiceConfirmationRequiredException($"'{name}' is protected: type its name to {action.ToString().ToLowerInvariant()} it.");
            }

            _units[name] = action == HostServiceAction.Stop
                ? unit with { ActiveState = "inactive", SubState = "dead" }
                : unit with { ActiveState = "active", SubState = "running" };
            return Task.FromResult(_units[name]);
        }
    }

    public Task<HostUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var unit = Find(name);
            if (unit.Protected && !enabled && confirm != name)
            {
                throw new HostServiceConfirmationRequiredException($"'{name}' is protected: type its name to disable it.");
            }

            _units[name] = unit with { UnitFileState = enabled ? "enabled" : "disabled" };
            return Task.FromResult(_units[name]);
        }
    }

    public Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GpuProcess>?>([]);

    public Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_mount);
        }
    }

    public Task<BackupMountTestResult> TestBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default) =>
        Task.FromResult(new BackupMountTestResult(true, null));

    public Task<BackupMountInfo> SetBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _mount = _mount with
            {
                Configured = true, Mounted = true, Type = settings.Type, Server = settings.Server, Share = settings.Share, Username = settings.Username,
                Source = $"//{settings.Server}/{settings.Share}", MountedSource = $"//{settings.Server}/{settings.Share}",
            };
            return Task.FromResult(_mount);
        }
    }

    public Task<BackupMountInfo> RemoveBackupMountAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            _mount = _mount with { Configured = false, Mounted = false, Source = null, MountedSource = null };
            return Task.FromResult(_mount);
        }
    }

    private HostUnit Find(string name) =>
        _units.TryGetValue(name, out var unit) ? unit : throw new HostServiceNotFoundException($"'{name}' is not in the allowlist.");

    private static HostUnit Unit(string name, bool isProtected) => new()
    {
        Name = name,
        Unit = name + ".service",
        Scope = "system",
        DisplayName = name,
        Protected = isProtected,
        ActiveState = "active",
        SubState = "running",
        UnitFileState = "enabled",
        MainPid = 42,
    };
}

/// <summary>
/// The Hugging Face side of downloads (the helper container and the Hub listing): a download writes its folder and
/// completes the next time it is read, unless <see cref="Held"/>.
/// </summary>
internal sealed class ContractDownloads(string modelsRoot) : IHuggingFaceDownloader, IHuggingFaceRepoInspector
{
    private static readonly RepoFile[] Files = [new("config.json", 100), new("model.safetensors", 900)];
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DownloadStatus> _downloads = new(StringComparer.Ordinal);

    public volatile bool Held;

    /// <summary>While set, starting a download fails like the helper container's Docker call would.</summary>
    public volatile bool DockerDown;

    /// <summary>MN-043: while set, the Hub refuses a download start with this message (e.g. a gated repo, D55).</summary>
    public volatile string? HubRefusal;

    public Task<RepoInspection> InspectAsync(string repoId, string? revision, string? token, CancellationToken ct = default) =>
        Task.FromResult(new RepoInspection(repoId, revision, RepoFormat.Safetensors, Files, [], [], Files));

    public Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
    {
        if (DockerDown)
        {
            throw new HttpRequestException("Docker is not answering (contract node).");
        }

        if (HubRefusal is { } refusal)
        {
            throw new HuggingFaceApiException(refusal);
        }

        lock (_gate)
        {
            string folder = request.Folder!;
            if (_downloads.TryGetValue(folder, out var existing) && existing.State is DownloadState.Queued or DownloadState.Downloading)
            {
                throw new InvalidOperationException($"A download into '{folder}' is already in progress.");
            }

            Directory.CreateDirectory(Path.Combine(modelsRoot, folder));
            var status = new DownloadStatus(folder, request.RepoId, request.Revision, DownloadState.Downloading, 0, 1000, null, null);
            _downloads[folder] = status;
            return Task.FromResult(status);
        }
    }

    public Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<DownloadStatus>>([.. _downloads.Keys.Select(Advance)]);
        }
    }

    public Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_downloads.ContainsKey(folder) ? Advance(folder) : null);
        }
    }

    public Task<bool> CancelAsync(string folder, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_downloads.TryGetValue(folder, out var status))
            {
                return Task.FromResult(false);
            }

            if (status.State is DownloadState.Queued or DownloadState.Downloading)
            {
                _downloads[folder] = status with { State = DownloadState.Cancelled };
            }

            return Task.FromResult(true);
        }
    }

    private DownloadStatus Advance(string folder)
    {
        var status = _downloads[folder];
        if (status.State == DownloadState.Downloading && !Held)
        {
            string path = Path.Combine(modelsRoot, folder);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "config.json"), """{"architectures":["LlamaForCausalLM"],"model_type":"llama"}""");
            File.WriteAllBytes(Path.Combine(path, "model.safetensors"), new byte[900]);
            status = _downloads[folder] = status with { State = DownloadState.Completed, BytesDownloaded = 1000 };
        }

        return status;
    }
}
