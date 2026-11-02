using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.HostServices;

/// <summary>
/// Registers the host-service package (MN-023) for the agent host (MN-026): the hostd client over its Unix socket, the
/// Docker client for non-model containers, the operation executor, the read routes, the recovery handler and the
/// capability/health contributors. Needs <see cref="IOperationLog"/> and <see cref="IFencingState"/> (MN-013/MN-014)
/// and, optionally, <see cref="IEventBus"/> (MN-017) and a <see cref="TimeProvider"/>. Map the routes with
/// <c>app.MapGroup(Protocol.BasePath).MapHostServiceEndpoints()</c>.
/// </summary>
public static class HostServicesServiceCollectionExtensions
{
    private const string HostdHttpClientName = "mirbadllm-agent.hostd";
    private const string DockerHttpClientName = "mirbadllm-agent.docker-services";

    /// <summary>Adds the host-service package. <paramref name="configure"/> adjusts <see cref="AgentHostServicesOptions"/>.</summary>
    public static IServiceCollection AddAgentHostServices(this IServiceCollection services, Action<AgentHostServicesOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var optionsBuilder = services.AddOptions<AgentHostServicesOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<AgentHostServicesOptions>>().Value);

        services.AddHttpClient(HostdHttpClientName, (sp, client) =>
            {
                client.BaseAddress = new Uri("http://hostd/");
                client.Timeout = sp.GetRequiredService<AgentHostServicesOptions>().HostdTimeout;
            })
            .ConfigurePrimaryHttpMessageHandler(sp => HostAgentClient.CreateHandler(sp.GetRequiredService<AgentHostServicesOptions>().HostdSocket));

        services.AddHttpClient(DockerHttpClientName, (sp, client) =>
            {
                client.BaseAddress = new Uri("http://docker/v1.47/");
                client.Timeout = sp.GetRequiredService<AgentHostServicesOptions>().DockerTimeout;
            })
            .ConfigurePrimaryHttpMessageHandler(sp => DockerEngineClient.CreateHandler(sp.GetRequiredService<AgentHostServicesOptions>().DockerEndpoint));

        services.TryAddSingleton<IHostdClient>(sp =>
        {
            string socket = sp.GetRequiredService<AgentHostServicesOptions>().HostdSocket;
            var client = new HostAgentClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(HostdHttpClientName), () => File.Exists(socket));
            return new HostdClient(client, () => File.Exists(socket));
        });

        services.TryAddSingleton(sp => new HostContainers(
            new DockerServiceClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(DockerHttpClientName)),
            sp.GetRequiredService<AgentHostServicesOptions>()));

        services.TryAddSingleton(sp => new HostServiceOperations(
            sp.GetRequiredService<IHostdClient>(),
            ContainersOrNull(sp),
            sp.GetRequiredService<IOperationLog>(),
            sp.GetRequiredService<IFencingState>(),
            sp.GetRequiredService<AgentHostServicesOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<HostServiceOperations>>(),
            sp.GetService<IEventBus>()));

        services.TryAddSingleton(sp => new HostServiceQueries(
            sp.GetRequiredService<IHostdClient>(),
            ContainersOrNull(sp),
            sp.GetRequiredService<AgentHostServicesOptions>()));

        services.TryAddSingleton(sp => new HostServicesContributor(
            sp.GetRequiredService<IHostdClient>(),
            sp.GetRequiredService<AgentHostServicesOptions>(),
            ContainersOrNull(sp) is not null));
        services.AddSingleton<ICapabilityContributor>(sp => sp.GetRequiredService<HostServicesContributor>());
        services.AddSingleton<IHealthContributor>(sp => sp.GetRequiredService<HostServicesContributor>());

        services.TryAddSingleton<HostServiceRecoveryHandler>();
        services.AddSingleton<IOperationRecoveryHandler>(sp => sp.GetRequiredService<HostServiceRecoveryHandler>());
        return services;
    }

    /// <summary>
    /// The non-model container manager, or null when this node does not manage containers — because the configuration says
    /// so (<see cref="AgentHostServicesOptions.Containers"/>), or because the node runs no Docker at all
    /// (<see cref="Hosting.DockerAvailability"/>, MN-232: no <c>services.docker</c> feature, no containers flag, no Docker
    /// client built). No <see cref="Hosting.DockerAvailability"/> registered = Docker on, exactly as before.
    /// </summary>
    private static HostContainers? ContainersOrNull(IServiceProvider sp) =>
        sp.GetRequiredService<AgentHostServicesOptions>().Containers && (sp.GetService<Hosting.DockerAvailability>()?.Enabled ?? true)
            ? sp.GetRequiredService<HostContainers>()
            : null;
}
