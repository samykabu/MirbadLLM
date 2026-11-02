using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.HostServices.Windows;

/// <summary>
/// Registers the SCM host services (MN-228) for the Windows agent; the composition (MN-232) calls it on the Windows
/// configuration only, behind <c>OperatingSystem.IsWindows()</c>.
/// <para>It registers the same host-service package as <c>AddAgentHostServices</c> does on Linux, over the SCM:
/// <see cref="IHostdClient"/> = <see cref="ScmHostClient"/> over <see cref="WindowsServiceControlManager"/>,
/// <see cref="HostServiceOperations"/> and <see cref="HostServiceQueries"/> with units managed and no containers or
/// backup mount, <see cref="HostServiceRecoveryHandler"/> (desired-state actions reconciled, restarts after a crash end
/// Unknown) and <see cref="ScmHostServicesContributor"/> (<c>services.scm</c>). These replace whatever
/// <c>AddAgentHostServices</c> registered for them, in either order; the Linux <see cref="HostServicesContributor"/> stays
/// silent on Windows because <c>agent.windows.example.json</c> turns systemd units, containers and the backup mount off
/// (MN-232 keeps it so, or does not register it there).</para>
/// <para>Needs <see cref="IOperationLog"/> and <see cref="IFencingState"/> and, optionally, <see cref="IEventBus"/> and a
/// <see cref="TimeProvider"/>.</para>
/// </summary>
public static class ScmHostServicesServiceCollectionExtensions
{
    /// <summary>Adds the SCM host services over the real Service Control Manager.</summary>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddScmHostServices(this IServiceCollection services, ScmHostServicesOptions options) =>
        services.AddScmHostServices(options, _ => new WindowsServiceControlManager());

    /// <summary>Adds the SCM host services over <paramref name="scm"/> (tests pass a fake).</summary>
    public static IServiceCollection AddScmHostServices(
        this IServiceCollection services, ScmHostServicesOptions options, Func<IServiceProvider, IServiceControlManager> scm)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scm);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WhitelistPath, nameof(options.WhitelistPath));

        // Units managed (through the SCM), never containers or the backup mount: the shape HostServiceOperations and
        // HostServiceQueries use to decide which kinds are supported. Not the DI AgentHostServicesOptions, which stays
        // the Linux package's own. Manager = scm gives the shared package its SCM words (MN-239 item 1, D130): the
        // texts name the Service Control Manager, never hostd, a socket or a "masked" unit; the codes are unchanged.
        var hostOptions = new AgentHostServicesOptions
        {
            Systemd = true,
            Containers = false,
            BackupMount = false,
            HostdExpected = false,
            Manager = HostServiceManagers.Scm,
        };

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton(scm);
        services.TryAddSingleton(sp => new ScmDenyList([sp.GetRequiredService<ScmHostServicesOptions>().AgentServiceName]));
        services.TryAddSingleton(sp => new ScmWhitelist(
            sp.GetRequiredService<ScmHostServicesOptions>().WhitelistPath,
            sp.GetRequiredService<ScmDenyList>(),
            sp.GetRequiredService<ILogger<ScmWhitelist>>()));
        services.TryAddSingleton(sp => new ScmHostClient(
            sp.GetRequiredService<IServiceControlManager>(),
            sp.GetRequiredService<ScmWhitelist>(),
            sp.GetRequiredService<ScmDenyList>(),
            sp.GetRequiredService<ScmHostServicesOptions>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ScmHostClient>>()));

        services.Replace(ServiceDescriptor.Singleton<IHostdClient>(sp => sp.GetRequiredService<ScmHostClient>()));
        services.Replace(ServiceDescriptor.Singleton(sp => new HostServiceOperations(
            sp.GetRequiredService<ScmHostClient>(),
            containers: null,
            sp.GetRequiredService<IOperationLog>(),
            sp.GetRequiredService<IFencingState>(),
            hostOptions,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<HostServiceOperations>>(),
            sp.GetService<IEventBus>())));
        services.Replace(ServiceDescriptor.Singleton(sp => new HostServiceQueries(sp.GetRequiredService<ScmHostClient>(), containers: null, hostOptions)));

        // Recovery: the package's handler over the operations above, mapped once. When AddAgentHostServices ran first it
        // registered the handler and its mapping already (the handler then resolves the operations registered here).
        if (!services.Any(d => d.ServiceType == typeof(HostServiceRecoveryHandler)))
        {
            services.AddSingleton<HostServiceRecoveryHandler>();
            services.AddSingleton<IOperationRecoveryHandler>(sp => sp.GetRequiredService<HostServiceRecoveryHandler>());
        }

        services.TryAddSingleton(sp => new ScmHostServicesContributor(sp.GetRequiredService<IServiceControlManager>()));
        services.AddSingleton<ICapabilityContributor>(sp => sp.GetRequiredService<ScmHostServicesContributor>());
        return services;
    }
}
