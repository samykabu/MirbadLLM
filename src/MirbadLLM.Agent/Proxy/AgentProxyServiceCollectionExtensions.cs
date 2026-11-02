using Microsoft.Extensions.DependencyInjection.Extensions;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Proxy;

/// <summary>DI registration of the agent inference proxy (design §7.2); called by the agent composition (MN-026).</summary>
public static class AgentProxyServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="AgentProxy"/> and, unless one is already registered, the runtime-driver-backed
    /// <see cref="IProxyUpstreamResolver"/> (it needs an <c>IRuntimeDriver</c>, a <see cref="TimeProvider"/> and the
    /// <see cref="UpstreamChanges"/> signal in DI — the last is registered here, and the process driver bumps it so a
    /// replaced instance's old port is never proxied to, MN-239 bug B). Map the route with <c>MapAgentProxyEndpoints</c>.
    /// </summary>
    public static IServiceCollection AddAgentProxy(this IServiceCollection services, Action<AgentProxyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<AgentProxyOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<UpstreamChanges>();
        services.TryAddSingleton<IProxyUpstreamResolver, RuntimeDriverUpstreamResolver>();
        services.TryAddSingleton<AgentProxy>();
        return services;
    }
}
