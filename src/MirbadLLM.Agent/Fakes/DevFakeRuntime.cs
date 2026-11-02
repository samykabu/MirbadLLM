using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Http;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Fakes;

/// <summary>
/// Turns the agent into a development node without GPUs or Docker (MN-042): <see cref="FakeRuntimeDriver"/> replaces the
/// container driver, a fixed memory model replaces <c>/proc/meminfo</c>, and the Docker Engine client talks to
/// <see cref="NoDockerHandler"/> (an empty engine) instead of a daemon, so nothing on the developer's machine is started,
/// stopped or pulled. A <b>unified</b> fake (<c>unified:64</c>, the DGX) has no <c>nvidia-smi</c>; a <b>discrete</b> fake
/// (<c>discrete:2x16</c>, MN-122) answers <c>nvidia-smi</c> with <see cref="DevFakeNvidiaSmi"/>, so the real GPU inventory,
/// telemetry and capability report see its GPUs, and reports the P2 feature its driver implements
/// (<see cref="DevFakeCapabilityContributor"/>). Everything else is the real agent: enrollment, mTLS, fencing, the operation
/// log, admission, events, the proxy and telemetry.
/// <para><b>Guard.</b> Enabling it outside the Development environment (<c>DOTNET_ENVIRONMENT</c> /
/// <c>ASPNETCORE_ENVIRONMENT</c>) or with a listener that is not loopback-only refuses to start the agent
/// (<see cref="AgentStartupException"/>): a production node can never serve fake models or be reached as one.</para>
/// </summary>
public static class DevFakeRuntime
{
    /// <summary>Command-line switch equivalent to <c>DevFakeRuntime:Enabled=true</c>.</summary>
    public const string Switch = "--dev-fake-runtime";

    /// <summary>
    /// Reads <see cref="DevFakeRuntimeOptions"/> from <paramref name="configuration"/>. Null when the fake runtime is off;
    /// otherwise the registrations to apply before the agent packages. Throws <see cref="AgentStartupException"/> when it
    /// is on where it must not be.
    /// </summary>
    public static Action<IServiceCollection>? Configure(IConfiguration configuration, IHostEnvironment environment, AgentOptions agent)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(agent);
        var fake = new DevFakeRuntimeOptions();
        configuration.GetSection(DevFakeRuntimeOptions.SectionName).Bind(fake);
        if (!fake.Enabled)
        {
            return null;
        }

        if (!environment.IsDevelopment())
        {
            throw new AgentStartupException(
                $"{DevFakeRuntimeOptions.SectionName} (fake models, no Docker) is a development-only setting and the environment is " +
                $"'{environment.EnvironmentName}'. Set DOTNET_ENVIRONMENT=Development on a developer machine, or remove it.");
        }

        if (!IsLoopbackOnly(agent.ListenAddress))
        {
            throw new AgentStartupException(
                $"{DevFakeRuntimeOptions.SectionName} needs a loopback-only listener (Agent:ListenAddress 127.0.0.1, ::1 or localhost); " +
                $"'{agent.ListenAddress ?? "*"}' would expose a fake node on the network.");
        }

        try
        {
            fake.Validate();
        }
        catch (ArgumentException ex)
        {
            throw new AgentStartupException(ex.Message, ex);
        }

        return services => Register(services, agent, fake);
    }

    /// <summary>True when <paramref name="listenAddress"/> binds loopback only.</summary>
    internal static bool IsLoopbackOnly(string? listenAddress)
    {
        if (string.IsNullOrWhiteSpace(listenAddress))
        {
            return false;
        }

        string address = listenAddress.Trim();
        return string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(address.Trim('[', ']'), out IPAddress? ip) && IPAddress.IsLoopback(ip));
    }

    private static void Register(IServiceCollection services, AgentOptions agent, DevFakeRuntimeOptions fake)
    {
        services.AddSingleton(fake);
        services.AddSingleton(sp => new FakeRuntimeDriver(
            agent.ToContainerRuntimeOptions(),
            () => sp.GetRequiredService<IPortAllocator>(),
            fake,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<FakeRuntimeDriver>>()));

        // Registered before the packages: AddMirbadLLMAgent then leaves the container driver out.
        services.AddSingleton<IRuntimeDriver>(sp => sp.GetRequiredService<FakeRuntimeDriver>());
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new NoDockerFilter());
        if (!fake.IsDiscrete)
        {
            // Unified (the DGX of P1): one pool, no nvidia-smi, P1's features only.
            services.AddSingleton<IMemoryProbe>(sp => new FakeMemoryProbe(fake.MemoryGiB, sp.GetRequiredService<FakeRuntimeDriver>()));
            services.AddSingleton<INvidiaSmiRunner>(new NoNvidiaSmi());
            return;
        }

        // Discrete (MN-122): the real GPU inventory, telemetry probe and capability contributor read a fake nvidia-smi
        // whose per-GPU memory follows the running fake models; the host pool is the configured RAM (model budgets live in
        // VRAM). /status uses the per-GPU reader on this node only (the swap on real nodes is MN-130's).
        services.AddSingleton<IMemoryProbe>(new FixedMemoryProbe(fake.MemoryGiB));
        services.AddSingleton<INvidiaSmiRunner>(sp => new DevFakeNvidiaSmi(fake, sp.GetRequiredService<FakeRuntimeDriver>()));
        services.AddSingleton<INodeMemoryReader>(sp => sp.GetRequiredService<DiscreteNodeMemoryReader>());
        services.AddSingleton<ICapabilityContributor, DevFakeCapabilityContributor>();
    }

    /// <summary>Unified memory of a fake node: total from the settings, minus the budgets of the running fake models.</summary>
    private sealed class FakeMemoryProbe(double totalGiB, FakeRuntimeDriver driver) : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new MemorySnapshot(totalGiB, Math.Max(0, totalGiB - driver.UsedGiB)));
    }

    /// <summary>Host RAM of a discrete fake node: the configured total, all of it available.</summary>
    private sealed class FixedMemoryProbe(double totalGiB) : IMemoryProbe
    {
        public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(new MemorySnapshot(totalGiB, totalGiB));
    }

    private sealed class NoNvidiaSmi : INvidiaSmiRunner
    {
        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default) => Task.FromResult(NvidiaSmiResult.NotFound);
    }

    /// <summary>The agent's Docker Engine client gets <see cref="NoDockerHandler"/> as its transport.</summary>
    private sealed class NoDockerFilter : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (builder.Name == AgentServiceCollectionExtensions.DockerHttpClientName)
            {
                builder.PrimaryHandler = new NoDockerHandler();
            }
        };
    }
}

/// <summary>
/// An empty Docker Engine for the fake runtime: listings (containers, images, networks) answer empty, <c>_ping</c> answers
/// OK, and anything that would change or inspect something (pulls, download helpers, host-service containers) answers 503
/// with a message saying the dev setup has no Docker. The agent's Docker probe therefore sees an answering engine and the
/// node is not Degraded for <c>docker_unreachable</c>.
/// </summary>
internal sealed class NoDockerHandler : HttpMessageHandler
{
    /// <summary>The message of every refused call.</summary>
    public const string Message = "The development fake runtime has no Docker Engine (DevFakeRuntime is on).";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string path = request.RequestUri?.AbsolutePath ?? "";
        if (request.Method == HttpMethod.Get && (path.EndsWith("/containers/json", StringComparison.Ordinal)
            || path.EndsWith("/images/json", StringComparison.Ordinal) || path.EndsWith("/networks", StringComparison.Ordinal)))
        {
            return Task.FromResult(Answer(HttpStatusCode.OK, "[]"));
        }

        if (path.EndsWith("/_ping", StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK") });
        }

        return Task.FromResult(Answer(HttpStatusCode.ServiceUnavailable, "{\"message\":\"" + Message + "\"}"));
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
