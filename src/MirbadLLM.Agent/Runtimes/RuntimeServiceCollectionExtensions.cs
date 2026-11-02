using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// Registers the runtime and operation endpoints' services (MN-031). The host (MN-026) also registers the four
/// collaborators they code against: <c>IOperationLog</c> (MN-013), <c>IFencingState</c> (MN-014),
/// <c>IReservationManager</c> (MN-016), <c>IEventBus</c> (MN-017), and at least one <c>IRuntimeDriver</c> (MN-015).
/// </summary>
public static class RuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="MutationGate"/>, <see cref="RuntimeStateBoard"/>, <see cref="RuntimeOperations"/> (also as the
    /// launch <see cref="IOperationCanceller"/>), <see cref="OperationCancellation"/> and the hosted
    /// <see cref="RuntimeWatcher"/>.
    /// </summary>
    public static IServiceCollection AddAgentRuntimes(this IServiceCollection services, AgentRuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options ?? new AgentRuntimeOptions());
        services.TryAddSingleton<MutationGate>();
        services.TryAddSingleton<RuntimeStateBoard>();
        services.TryAddSingleton<RuntimeOperations>();
        services.AddSingleton<IOperationCanceller>(sp => sp.GetRequiredService<RuntimeOperations>());
        services.TryAddSingleton<OperationCancellation>();
        services.TryAddSingleton<RuntimeWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<RuntimeWatcher>());
        return services;
    }
}
