using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.HostServices.Windows;

/// <summary>
/// The SCM host services' part of <c>GET /capabilities</c> (MN-228, design §3.3, design-p3p4 §A.4.3, §A.9): feature
/// <see cref="AgentFeatures.ServicesScm"/> (it offers the host-service routes through
/// <see cref="AgentRoute.AlternativeFeatures"/>) and <see cref="HostServicesCapability"/> with
/// <c>Manager = </c><see cref="HostServiceManagers.Scm"/>, <c>HostdAvailable</c> = the SCM answers, no containers. It
/// never reports <c>services.systemd</c>, <c>services.docker</c>, <c>gpu-processes</c> or <c>backup-mount</c>.
/// Registered only on the Windows configuration (MN-232); the DGX's report is unchanged.
/// </summary>
public sealed class ScmHostServicesContributor(IServiceControlManager scm) : ICapabilityContributor
{
    /// <inheritdoc/>
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        bool available;
        try
        {
            available = scm.IsAvailable;
        }
        catch (ScmException)
        {
            available = false;
        }

        var features = current.Features.ToList();
        if (!features.Contains(AgentFeatures.ServicesScm, StringComparer.Ordinal))
        {
            features.Add(AgentFeatures.ServicesScm);
        }

        return Task.FromResult(current with
        {
            Features = features,
            HostServices = new HostServicesCapability(HostServiceManagers.Scm, available, HostdVersion: null, Containers: false),
        });
    }
}
