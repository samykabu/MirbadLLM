using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MirbadLLM.Agent.Recovery;

/// <summary>
/// Registers the startup reconciliation (MN-025). The host (MN-026) resolves <see cref="AgentReconciler"/> and awaits
/// <see cref="AgentReconciler.RunAsync"/> after the services are built and before the listener opens (before
/// <c>app.Run</c>/<c>StartAsync</c>, so the runtime watcher and other hosted services start on a reconciled state). It
/// needs <c>AddAgentRuntimes</c> (MN-031), the reservation manager (MN-016) and the packages' recovery handlers and
/// cancellers (<c>AddAgentHostServices</c>, <c>AddAgentStorage</c>, and the PKI package's when it registers one).
/// </summary>
public static class RecoveryServiceCollectionExtensions
{
    /// <summary>Adds <see cref="AgentReconciler"/>.</summary>
    public static IServiceCollection AddAgentReconciliation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<AgentReconciler>();
        return services;
    }
}
