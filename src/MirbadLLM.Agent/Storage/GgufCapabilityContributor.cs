using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// MN-209's part of the capability report (design-p3p4 §A.1.10, §A.9; D116): <see cref="AgentFeatures.GgufInspect"/>, so
/// a P3 master calls <c>GET /model-folders/{folder}/gguf</c> on this node. It reports the flag whenever it is registered
/// with a reader; the agent composition (MN-232) registers it only where llama.cpp models run (the Windows/process
/// configuration), so the DGX agent keeps P2's features exactly (none of <see cref="AgentFeatures.P3Reported"/>). Never
/// removes what other contributors added, never adds the flag twice, never throws.
/// </summary>
public sealed class GgufCapabilityContributor : ICapabilityContributor
{
    /// <summary>Creates the contributor over the route's reader (its presence is what the flag promises).</summary>
    public GgufCapabilityContributor(IGgufReader reader) => ArgumentNullException.ThrowIfNull(reader);

    /// <inheritdoc />
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Features.Contains(AgentFeatures.GgufInspect, StringComparer.Ordinal))
        {
            return Task.FromResult(current);
        }

        return Task.FromResult(current with { Features = [.. current.Features, AgentFeatures.GgufInspect] });
    }
}
