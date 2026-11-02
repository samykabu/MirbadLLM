using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// The native llama.cpp runtime's part of the capability report (design-p3p4 §A.1.2, §A.7; MN-222): the feature
/// <see cref="AgentFeatures.RuntimeLlamaCppProcess"/> and a <see cref="RuntimeCapability"/> <c>{kind: llamacpp, delivery:
/// process}</c> whose <see cref="RuntimeCapability.Versions"/> lists every installed <c>llama-server</c> version from the
/// executable catalogue with its verification result and the node default; <see cref="RuntimeCapability.Available"/> is
/// true when at least one version is verified. Reported only when a process driver is registered and the catalogue has at
/// least one <c>llama-server</c> version, so a node without native llama.cpp (the DGX) reports exactly what it did. Never
/// removes what other contributors added and never throws: an unreadable catalogue reports nothing new.
/// </summary>
public sealed class LlamaCppRuntimeContributor(IExecutableCatalog catalog, IEnumerable<IRuntimeDriver> drivers)
    : ProcessRuntimeContributor(catalog, drivers, RuntimeKind.LlamaCpp, ExecutableNames.LlamaServer, AgentFeatures.RuntimeLlamaCppProcess);

/// <summary>
/// The MLX runtime's part of the capability report (docs/macos/implementation-plan.md, Phase 3): the feature
/// <see cref="AgentFeatures.RuntimeMlxProcess"/> and a <c>{kind: mlx, delivery: process}</c> runtime listing every installed
/// <c>mlx-lm-server</c> version, by the same rules as <see cref="LlamaCppRuntimeContributor"/>. Only a macOS agent has
/// those versions (the installer's <c>--with-mlx</c>), so other nodes report nothing new.
/// </summary>
public sealed class MlxRuntimeContributor(IExecutableCatalog catalog, IEnumerable<IRuntimeDriver> drivers)
    : ProcessRuntimeContributor(catalog, drivers, RuntimeKind.Mlx, ExecutableNames.MlxLmServer, AgentFeatures.RuntimeMlxProcess);

/// <summary>
/// One native runtime's part of the capability report: its feature flag and a process <see cref="RuntimeCapability"/> with
/// the catalogue's versions of its logical executable. Reported only when a process driver is registered and at least one
/// version is installed; never throws.
/// </summary>
public abstract class ProcessRuntimeContributor : ICapabilityContributor
{
    private readonly IExecutableCatalog _catalog;
    private readonly bool _processDriver;
    private readonly RuntimeKind _kind;
    private readonly string _executable;
    private readonly string _feature;

    /// <summary>Creates the contributor for <paramref name="kind"/>, run by <paramref name="executable"/>, flagged <paramref name="feature"/>.</summary>
    protected ProcessRuntimeContributor(
        IExecutableCatalog catalog, IEnumerable<IRuntimeDriver> drivers, RuntimeKind kind, string executable, string feature)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(drivers);
        _catalog = catalog;
        _processDriver = drivers.Any(d => d.Delivery == LaunchDelivery.Process);
        _kind = kind;
        _executable = executable;
        _feature = feature;
    }

    /// <inheritdoc />
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!_processDriver)
        {
            return Task.FromResult(current);
        }

        IReadOnlyList<ExecutableVersionState> states;
        try
        {
            states = _catalog.List();
        }
#pragma warning disable CA1031 // a contributor never throws; the health contributor says why the catalogue is unusable
        catch (Exception)
#pragma warning restore CA1031
        {
            return Task.FromResult(current);
        }

        var versions = states
            .Where(s => string.Equals(s.Name, _executable, StringComparison.Ordinal))
            .Select(s => new RuntimeVersion(s.Version, s.Cuda, s.Verified, s.IsDefault))
            .ToList();
        if (versions.Count == 0)
        {
            return Task.FromResult(current);
        }

        RuntimeCapability runtime = new()
        {
            Kind = _kind,
            Delivery = LaunchDelivery.Process,
            Available = versions.Any(v => v.Verified),
            Versions = versions,
        };
        var features = current.Features.Contains(_feature, StringComparer.Ordinal)
            ? current.Features
            : [.. current.Features, _feature];
        return Task.FromResult(current with
        {
            Features = features,
            Runtimes = [.. current.Runtimes.Where(r => !(r.Kind == _kind && r.Delivery == LaunchDelivery.Process)), runtime],
        });
    }
}
