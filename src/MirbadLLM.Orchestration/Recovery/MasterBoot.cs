using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>
/// How this master process booted (design §9.4 recovery mode, D76; composed by MN-130): decided once, before anything
/// touches the data path, by <see cref="Prepare"/> (<see cref="FleetRecovery.PrepareBoot"/>), and registered as a
/// singleton so the composition and the UI read the same answer.
/// </summary>
/// <param name="Mode">The effective mode: <see cref="RecoveryMode.FleetRestore"/> composes the restricted recovery host.</param>
/// <param name="RecoveryOptionStillSet">
/// True on a normal boot while <c>MirbadLLM__Recovery__Mode=FleetRestore</c> is still configured although a fleet
/// recovery already finished on this volume (the option is ignored, one-shot): the UI shows a red warning until it is removed.
/// </param>
public sealed record MasterBoot(RecoveryMode Mode, bool RecoveryOptionStillSet = false)
{
    /// <summary>A normal boot with no recovery option (the default when no <see cref="MasterBoot"/> is registered).</summary>
    public static MasterBoot Normal { get; } = new(RecoveryMode.None);

    /// <summary>True in recovery mode: no seeding, CA generation, bootstrap node, auto-start, scheduler or agent connections.</summary>
    public bool IsRecovery => Mode == RecoveryMode.FleetRestore;

    /// <summary>
    /// Reads <c>MirbadLLM:Recovery</c>, finishes or discards a staged fleet recovery in <paramref name="dataPath"/> and
    /// returns the mode to boot in. Run once at startup, before the master lock, the registry or the PKI touch the data path.
    /// </summary>
    public static MasterBoot Prepare(IConfiguration configuration, string dataPath, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        var options = configuration.GetSection(RecoveryOptions.SectionName).Get<RecoveryOptions>() ?? new RecoveryOptions();
        RecoveryMode mode = FleetRecovery.PrepareBoot(options, dataPath, logger);
        return new MasterBoot(mode, mode == RecoveryMode.None && options.Mode == RecoveryMode.FleetRestore);
    }

    /// <summary>The <see cref="MasterBoot"/> registered in <paramref name="services"/> (as an instance), else <see cref="Normal"/>.</summary>
    public static MasterBoot Of(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.LastOrDefault(d => d.ServiceType == typeof(MasterBoot))?.ImplementationInstance as MasterBoot ?? Normal;
    }
}
