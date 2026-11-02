using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Fencing;

/// <summary>DI registration of master fencing (design §4.2). Called by the master composition (MN-040).</summary>
public static class FencingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="MasterIdentity"/> as the singleton <see cref="IMasterIdentity"/> on
    /// <see cref="MirbadLLMOptions.DataPath"/> and binds <see cref="FencingOptions"/>. Resolve
    /// <see cref="IMasterIdentity"/> before the host starts so a second master fails at startup; the container
    /// disposes it at shutdown, which releases <c>master.lock</c>.
    /// </summary>
    public static IServiceCollection AddMasterFencing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOptions<FencingOptions>().BindConfiguration(FencingOptions.SectionName);
        services.AddSingleton(sp => MasterIdentity.Open(
            sp.GetRequiredService<IOptions<MirbadLLMOptions>>().Value.DataPath,
            sp.GetRequiredService<IOptions<FencingOptions>>().Value,
            sp.GetService<TimeProvider>(),
            sp.GetService<ILoggerFactory>()?.CreateLogger<MasterIdentity>()));
        services.AddSingleton<IMasterIdentity>(sp => sp.GetRequiredService<MasterIdentity>());
        return services;
    }
}
