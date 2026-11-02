using System.Reflection;

namespace MirbadLLM.Agent.Hosting;

/// <summary>
/// Identity of this agent process (design §4.3 <c>/info</c>, §4.7): a random boot id, the start time and the agent
/// version. One instance per host; every package that reports a boot id (events, telemetry cursors, operation records)
/// must use this one.
/// </summary>
public sealed class AgentBootInfo
{
    /// <summary>Creates the boot identity now.</summary>
    public AgentBootInfo(TimeProvider time, string? agentVersion = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        BootId = Guid.NewGuid().ToString("N");
        StartedAt = time.GetUtcNow();
        AgentVersion = string.IsNullOrWhiteSpace(agentVersion) ? DefaultVersion() : agentVersion;
    }

    /// <summary>Random id of this boot (no <c>/</c>).</summary>
    public string BootId { get; }

    /// <summary>When this boot started.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Agent informational version, e.g. <c>1.0.0+78b39d2</c>.</summary>
    public string AgentVersion { get; }

    private static string DefaultVersion()
    {
        Assembly assembly = typeof(AgentBootInfo).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
