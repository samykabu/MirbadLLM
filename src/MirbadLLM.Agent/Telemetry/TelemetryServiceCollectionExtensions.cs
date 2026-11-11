using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>DI registration of the telemetry package, for the agent host composition (MN-026).</summary>
public static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ring (<see cref="ITelemetryRing"/>), the sampler (hosted), the GPU probes, the GPU inventory
    /// (<see cref="IGpuInventory"/>) and <see cref="DiscreteNodeMemoryReader"/>, <c>/gpu/idle</c>, the capability and health
    /// contributors, and — only when nothing else did — <see cref="IMemoryProbe"/>
    /// (<c>ProcMeminfoMemoryProbe</c>) and <see cref="IGpuProcessProbe"/> (hostd through <see cref="IHostdClient"/>
    /// when registered). Map the routes with <see cref="Api.TelemetryEndpoints.MapTelemetryEndpoints"/>.
    /// </summary>
    /// <param name="services">The agent's services.</param>
    /// <param name="bootId">This agent boot's id; the same value the event bus uses.</param>
    /// <param name="configure">Optional settings (e.g. the sample interval the master sent).</param>
    public static IServiceCollection AddAgentTelemetry(this IServiceCollection services, string bootId, Action<TelemetryOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootId);

        var optionsBuilder = services.AddOptions<TelemetryOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IMemoryProbe>(sp => new ProcMeminfoMemoryProbe(
            sp.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build()));
        services.TryAddSingleton<IGpuProcessProbe>(sp => new HostdGpuProcessProbe(
            sp.GetService<IHostdClient>(), sp.GetRequiredService<IOptions<TelemetryOptions>>()));

        services.TryAddSingleton<INvidiaSmiRunner, NvidiaSmiProcessRunner>();
        services.TryAddSingleton<IGpuTelemetryProbe, NvidiaSmiGpuTelemetryProbe>();
        services.TryAddSingleton<NvidiaSmiGpuInventory>();
        services.TryAddSingleton<IGpuInventory>(sp => sp.GetRequiredService<NvidiaSmiGpuInventory>());
        // What the memory model and the capability report read at start: nvidia-smi's inventory, unless a Vulkan node
        // registered its own (Agent:Accelerator = vulkan).
        services.TryAddSingleton<IGpuStartupInventory>(sp => sp.GetRequiredService<NvidiaSmiGpuInventory>());

        // Per-GPU memory for /status (MN-104). Registered as itself only: the composition (MN-130) decides whether it
        // replaces the P1 INodeMemoryReader; on a unified node it answers exactly as P1 does (D66).
        services.TryAddSingleton(sp => new DiscreteNodeMemoryReader(
            sp.GetRequiredService<IMemoryProbe>(),
            sp.GetRequiredService<IGpuTelemetryProbe>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<TelemetryOptions>>().Value.MemoryModel));
        services.TryAddSingleton<GpuIdleSampler>();
        services.TryAddSingleton<TelemetryHttpClient>();
        services.TryAddSingleton<ITelemetryRing>(sp => new TelemetryRing(bootId, sp.GetRequiredService<IOptions<TelemetryOptions>>()));

        services.TryAddSingleton(sp => new TelemetrySampler(
            sp.GetRequiredService<ITelemetryRing>(),
            sp.GetRequiredService<IMemoryProbe>(),
            sp.GetRequiredService<IGpuTelemetryProbe>(),
            sp.GetServices<NodeRuntime.Abstractions.IRuntimeDriver>(),
            sp.GetRequiredService<TelemetryHttpClient>(),
            sp.GetRequiredService<IOptions<TelemetryOptions>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TelemetrySampler>>(),
            sp.GetService<IGpuProcessProbe>(),
            // The measured-delta hook (MN-232): ExternalGpuUseContributor on a process node; nothing on a P1/P2 node,
            // where the sampler behaves exactly as before.
            sp.GetServices<ITelemetrySampleObserver>()));
        services.AddHostedService(sp => sp.GetRequiredService<TelemetrySampler>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ICapabilityContributor, TelemetryCapabilityContributor>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthContributor, GpuHealthContributor>());
        return services;
    }
}
