using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Abstractions;

namespace MirbadLLM.Agent.Events;

/// <summary>Registers the event bus and its status view (MN-026 composes the agent with it).</summary>
public static class EventsServiceCollectionExtensions
{
    /// <summary>
    /// Adds one <see cref="AgentStatusView"/> and one <see cref="EventBus"/> applying events to it, exposed as
    /// <see cref="IEventBus"/>. <paramref name="bootId"/> must be the boot id <c>/info</c> reports. The caller still
    /// registers an <see cref="INodeMemoryReader"/> and the <see cref="IHealthContributor"/>s.
    /// </summary>
    public static IServiceCollection AddAgentEvents(this IServiceCollection services, string bootId, EventBusOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(bootId);

        services.AddSingleton<AgentStatusView>();
        services.AddSingleton(sp => new EventBus(
            bootId,
            options,
            sp.GetService<TimeProvider>(),
            sp.GetRequiredService<AgentStatusView>()));
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<EventBus>());
        return services;
    }
}
