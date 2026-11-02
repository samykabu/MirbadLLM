using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Fencing;
using MirbadLLM.Agent.Hosting;

namespace MirbadLLM.Agent.Pki;

/// <summary>Registration of the agent's PKI package for the agent composition (MN-026).</summary>
public static class PkiServiceCollectionExtensions
{
    /// <summary>
    /// Registers two-phase rotation and trust (design §10.2): <see cref="FileAgentCredentials"/> (the committed files),
    /// <see cref="RotatingAgentCredentials"/> as <b>the</b> <see cref="IAgentCredentials"/> (replacing any earlier
    /// registration, so it works before or after <see cref="AgentHostBuilder.Configure"/>), <see cref="AgentPkiService"/>
    /// and <see cref="PkiRecoveryHandler"/> (an <see cref="IOperationRecoveryHandler"/>). Needs <see cref="IOperationLog"/>
    /// and <see cref="IFencingState"/>; uses <see cref="AgentListenerTls"/> and <see cref="IEventBus"/> when registered.
    /// Map the routes with <c>app.MapPkiEndpoints()</c>.
    /// </summary>
    public static IServiceCollection AddAgentPki(this IServiceCollection services, AgentHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new FileAgentCredentials(options, sp.GetRequiredService<ILogger<FileAgentCredentials>>()));
        services.TryAddSingleton(sp => new RotatingAgentCredentials(
            sp.GetRequiredService<FileAgentCredentials>(),
            options,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RotatingAgentCredentials>>()));
        services.Replace(ServiceDescriptor.Singleton<IAgentCredentials>(sp => sp.GetRequiredService<RotatingAgentCredentials>()));
        services.TryAddSingleton(sp => new AgentPkiService(
            sp.GetRequiredService<RotatingAgentCredentials>(),
            sp.GetRequiredService<IOperationLog>(),
            sp.GetRequiredService<IFencingState>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<AgentPkiService>>(),
            sp.GetService<AgentListenerTls>(),
            sp.GetService<IEventBus>()));
        services.TryAddSingleton<PkiRecoveryHandler>();
        services.AddSingleton<IOperationRecoveryHandler>(sp => sp.GetRequiredService<PkiRecoveryHandler>());
        return services;
    }
}
