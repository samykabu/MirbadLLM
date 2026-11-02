using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.HostServices;

/// <summary>
/// The host-service package's part of <c>GET /capabilities</c> (design §3.3) and of the Degraded reasons (§3.5).
/// <para>Features come from configuration, not from hostd's current health, so the master keeps showing the Services
/// tabs (with hostd's error, as today) while hostd is briefly down: <c>services.systemd</c> and <c>gpu-processes</c> when
/// units are managed, <c>services.docker</c> for containers, <c>backup-mount</c> when mounts are forwarded.
/// <see cref="HostServicesCapability.HostdAvailable"/> reports the live socket state.</para>
/// <para>Health: <c>hostd_missing</c> when the socket is gone on a node that had it (seen during this agent's lifetime,
/// or <see cref="AgentHostServicesOptions.HostdExpected"/>).</para>
/// <para>Docker off (MN-214/MN-232): <paramref name="dockerAvailable"/> is false on a node that runs no Docker, so
/// <c>services.docker</c> and <see cref="HostServicesCapability.Containers"/> are never reported. On the Windows PC the
/// whole contributor also stays silent, because <c>agent.windows.example.json</c> turns units, containers and the backup
/// mount off and the SCM contributor (MN-228) reports <c>services.scm</c> instead.</para>
/// </summary>
public sealed class HostServicesContributor(IHostdClient hostd, AgentHostServicesOptions options, bool dockerAvailable)
    : ICapabilityContributor, IHealthContributor
{
    private volatile bool _hostdSeen;

    /// <inheritdoc/>
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        bool installed = SafeInstalled();
        bool containers = options.Containers && dockerAvailable;
        var features = current.Features.ToList();

        void Add(string feature)
        {
            if (!features.Contains(feature, StringComparer.Ordinal))
            {
                features.Add(feature);
            }
        }

        bool launchd = string.Equals(options.Manager, HostServiceManagers.Launchd, StringComparison.Ordinal);
        if (options.Systemd && launchd)
        {
            // macOS hostd: launchd jobs, no GPU processes to attribute (Phase 4, M10).
            Add(AgentFeatures.ServicesLaunchd);
        }
        else if (options.Systemd)
        {
            Add(AgentFeatures.ServicesSystemd);
            Add(AgentFeatures.GpuProcesses);
        }

        if (containers)
        {
            Add(AgentFeatures.ServicesDocker);
        }

        if (options.BackupMount)
        {
            Add(AgentFeatures.BackupMount);
        }

        var result = current with
        {
            Features = features,
            HostServices = options.Systemd || containers
                ? new HostServicesCapability(options.Systemd ? (launchd ? HostServiceManagers.Launchd : HostServiceManagers.Systemd) : "none",
                    options.Systemd && installed, HostdVersion: null, containers)
                : current.HostServices,
            BackupMount = options.BackupMount ? new BackupMountCapability(true) : current.BackupMount,
        };
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default)
    {
        if (!options.Systemd && !options.BackupMount)
        {
            return Task.FromResult<IReadOnlyList<HealthReason>>([]);
        }

        if (SafeInstalled())
        {
            _hostdSeen = true;
            return Task.FromResult<IReadOnlyList<HealthReason>>([]);
        }

        return Task.FromResult<IReadOnlyList<HealthReason>>(_hostdSeen || options.HostdExpected
            ? [new HealthReason(HealthReasonCodes.HostdMissing,
                $"mirbadllm-hostd is not answering on {options.HostdSocket}: services, the backup mount and GPU processes are unavailable.")]
            : []);
    }

    private bool SafeInstalled()
    {
        try
        {
            return hostd.IsInstalled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
