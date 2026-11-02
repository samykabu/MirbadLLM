using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Operations;

/// <summary>Registration of the operation log for the agent composition (MN-026).</summary>
public static class OperationLogServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OperationLog"/> as <see cref="IOperationLog"/> and as an <see cref="IHealthContributor"/>
    /// (<c>state_unavailable</c>), plus the daily <see cref="OperationLogCompactionService"/>. Uses the registered
    /// <see cref="TimeProvider"/> (or <see cref="TimeProvider.System"/>).
    /// </summary>
    public static IServiceCollection AddAgentOperationLog(this IServiceCollection services, Func<IServiceProvider, OperationLogOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new OperationLog(
            options(sp),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<OperationLog>>()));
        services.AddSingleton<IOperationLog>(sp => sp.GetRequiredService<OperationLog>());
        // Not the log itself: the container disposes a singleton once per registration, at the position its factory first
        // ran, in reverse order. The health contributors are resolved late (by the health watcher at host start), so an
        // alias returning the log would dispose it before the services that write to it when they stop (downloads,
        // image pulls: ObjectDisposedException at agent shutdown, W8 review MN-128). The adapter is not disposable.
        services.AddSingleton<IHealthContributor>(sp => new OperationLogHealth(sp.GetRequiredService<OperationLog>()));
        services.AddSingleton<IHostedService>(sp => new OperationLogCompactionService(
            sp.GetRequiredService<IOperationLog>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ILogger<OperationLogCompactionService>>()));
        return services;
    }
}

/// <summary>The operation log's health part, without its lifetime (see <see cref="OperationLogServiceCollectionExtensions.AddAgentOperationLog"/>).</summary>
internal sealed class OperationLogHealth(OperationLog log) : IHealthContributor
{
    /// <inheritdoc />
    public Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default) => log.CheckAsync(ct);
}
