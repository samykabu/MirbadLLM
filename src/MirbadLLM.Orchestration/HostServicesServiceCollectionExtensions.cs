using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Orchestration;

public static class HostServicesServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IHostServiceManager"/> for the Services page over the bootstrap node's agent (design §9.1, MN-040):
    /// units and containers go through <see cref="RemoteHostUnitBackend"/> and <see cref="RemoteHostContainerBackend"/>,
    /// while admin re-authentication, typed confirmations and the container whitelist (<see cref="HostServicesStore"/>)
    /// stay on the master. Also registers the backup-mount client (<see cref="RemoteBackupMountClient"/>, DGX agent →
    /// hostd). Call after <c>AddMirbadLLMOrchestration</c>, which registers the agent clients.
    /// </summary>
    public static IServiceCollection AddMirbadLLMHostServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<HostServicesOptions>()
            .Bind(configuration.GetSection(MirbadLLMOptions.SectionName));

        services.AddSingleton(sp =>
            new HostServicesStore(Path.Combine(sp.GetRequiredService<IOptions<MirbadLLMOptions>>().Value.DataPath, "host-services.json")));

        services.AddSingleton<IHostAgentBackupMountClient>(sp => new RemoteBackupMountClient(
            ServiceCollectionExtensions.BootstrapNodeId(sp),
            sp.GetRequiredService<IAgentClientFactory>(),
            sp.GetRequiredService<AgentOperationRunner>()));

        // HostServiceManager has two public constructors of the same arity (local sockets / backends): build it with a
        // factory (W3 review MN-094).
        services.AddSingleton<IHostServiceManager>(sp =>
        {
            string nodeId = ServiceCollectionExtensions.BootstrapNodeId(sp);
            var clients = sp.GetRequiredService<IAgentClientFactory>();
            var runner = sp.GetRequiredService<AgentOperationRunner>();
            return new HostServiceManager(
                new RemoteHostUnitBackend(nodeId, clients, runner),
                new RemoteHostContainerBackend(nodeId, clients, runner),
                sp.GetRequiredService<HostServicesStore>(),
                sp.GetRequiredService<IOptions<HostServicesOptions>>(),
                sp.GetRequiredService<ILogger<HostServiceManager>>(),
                // Registered by the web host (admin password + login rate limiter); without it whitelist changes are refused.
                sp.GetService<IAdminReauthenticator>());
        });

        return services;
    }
}
