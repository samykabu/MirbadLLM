using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes;
using MirbadLLM.NodeRuntime.Runtimes.Process;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// MN-107's part of the capability report (design §3.3, D73): <see cref="AgentFeatures.LaunchEnv"/> while the container
/// driver runs this node's instances (it accepts <c>spec.env</c>/<c>secretEnvNames</c> and reports names only for
/// secrets), and <see cref="AgentFeatures.ImagesDigestPinned"/> when that driver also refuses tag-only specs
/// (<c>AllowTagPulls</c> off). A node whose instances run elsewhere (the dev setup's fake driver) reports neither. Never
/// removes what other contributors added and never throws. Registered by the P2 composition (MN-130).
/// <para>The process driver (MN-211) accepts <c>spec.env</c> too — a llama.cpp launch's knobs travel in it — so a
/// Windows/process node reports <see cref="AgentFeatures.LaunchEnv"/> as well (MN-232; the frozen
/// <c>NodeCapabilities.windows.json</c> carries it, and without it the master refuses a llama.cpp model that sets any
/// environment variable). <see cref="AgentFeatures.ImagesDigestPinned"/> stays container-only.</para>
/// </summary>
public sealed class LaunchFeaturesContributor(IEnumerable<IRuntimeDriver> drivers) : ICapabilityContributor
{
    private readonly IRuntimeDriver[] _drivers = [.. drivers];

    /// <inheritdoc />
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        var container = _drivers.OfType<ContainerRuntimeDriver>().FirstOrDefault(d => d.Delivery == LaunchDelivery.Container);
        bool process = _drivers.OfType<ProcessRuntimeDriver>().Any(d => d.Delivery == LaunchDelivery.Process);
        if (container is null && !process)
        {
            return Task.FromResult(current);
        }

        var features = new List<string>(current.Features);
        if ((container is not null && ContainerRuntimeDriver.AcceptsEnvironment) || (process && ProcessRuntimeDriver.AcceptsEnvironment))
        {
            Add(features, AgentFeatures.LaunchEnv);
        }

        if (container is { RequiresApprovedDigest: true })
        {
            Add(features, AgentFeatures.ImagesDigestPinned);
        }

        return Task.FromResult(features.Count == current.Features.Count ? current : current with { Features = features });
    }

    private static void Add(List<string> features, string feature)
    {
        if (!features.Contains(feature, StringComparer.Ordinal))
        {
            features.Add(feature);
        }
    }
}
