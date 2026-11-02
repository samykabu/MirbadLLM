using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// Reports <c>downloads.hf-native</c> (design-p3p4 §A.2.1, §A.9; <see cref="AgentFeatures.DownloadsHfNative"/>) on an
/// agent whose <c>IHuggingFaceDownloader</c> is <see cref="NativeDownloadService"/>. <c>POST /downloads</c> is offered
/// through <see cref="AgentRoute.AlternativeFeatures"/>, so the master starts downloads on a node that reports only this
/// flag. Registered by the composition (MN-232) together with the service; never on the DGX, which keeps the container
/// helper and reports exactly P2's features.
/// </summary>
public sealed class NativeDownloadContributor : ICapabilityContributor
{
    /// <inheritdoc />
    public Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.Features.Contains(AgentFeatures.DownloadsHfNative, StringComparer.Ordinal))
        {
            return Task.FromResult(current);
        }

        return Task.FromResult(current with { Features = [.. current.Features, AgentFeatures.DownloadsHfNative] });
    }
}
