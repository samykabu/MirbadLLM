using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// The model editor's vLLM option catalog for a node (design §2.4 "the master merges the vLLM catalog per node"): the
/// fixed option lists of <see cref="VllmOptionsCatalog"/> plus the node's images from its capability report instead of
/// the local Docker socket. Images come from the live status cache when the node is known there, else from the last
/// report stored in the registry (an Offline node still shows its images). Image selection follows today's: references
/// containing <c>vllm</c>, plus the images the node's vLLM runtime capability lists, the default image (the node's
/// <see cref="NodeRecord.DefaultVllmImage"/>, else <c>MirbadLLM:VllmImage</c>) and the model's current image first.
/// </summary>
public sealed class RemoteVllmCatalogService
{
    private readonly INodeRegistry _nodes;
    private readonly INodeStatusCache? _status;
    private readonly IOptions<MirbadLLMOptions> _options;

    public RemoteVllmCatalogService(INodeRegistry nodes, IOptions<MirbadLLMOptions> options, INodeStatusCache? status = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(options);
        _nodes = nodes;
        _options = options;
        _status = status;
    }

    /// <summary>The catalog for <paramref name="nodeId"/>; an unknown node gets the fixed lists and the default images only.</summary>
    public async Task<VllmCatalog> GetAsync(string nodeId, string? currentImage, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        var record = await _nodes.GetAsync(nodeId, ct).ConfigureAwait(false);
        var capabilities = _status?.GetNode(nodeId)?.Capabilities ?? record?.LastCapabilities;
        string defaultImage = string.IsNullOrWhiteSpace(record?.DefaultVllmImage) ? _options.Value.VllmImage : record.DefaultVllmImage;
        return new VllmCatalog(
            VllmOptionsCatalog.ToolCallParsers,
            VllmOptionsCatalog.ReasoningParsers,
            VllmOptionsCatalog.Quantizations,
            VllmOptionsCatalog.KvCacheDtypes,
            VllmOptionsCatalog.CudaGraphModes,
            VllmOptionsCatalog.SpeculativeMethods,
            VllmOptionsCatalog.MaxModelLenPresets,
            VllmOptionsCatalog.MaxNumSeqsPresets,
            SelectImages(capabilities, defaultImage, currentImage));
    }

    /// <summary>The image list of the catalog from a capability report (see the class remarks).</summary>
    public static IReadOnlyList<string> SelectImages(NodeCapabilities? capabilities, string defaultImage, string? currentImage)
    {
        IEnumerable<string> named = capabilities?.Images.Select(i => i.Ref)
            .Where(tag => tag.Contains("vllm", StringComparison.OrdinalIgnoreCase)) ?? [];
        IEnumerable<string> runtime = capabilities?.Runtimes.Where(r => r.Kind == RuntimeKind.Vllm).SelectMany(r => r.Images ?? []) ?? [];
        var images = named.Concat(runtime)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrEmpty(defaultImage) && !images.Contains(defaultImage, StringComparer.Ordinal))
        {
            images.Insert(0, defaultImage);
        }

        if (!string.IsNullOrEmpty(currentImage) && !images.Contains(currentImage, StringComparer.Ordinal))
        {
            images.Insert(0, currentImage);
        }

        return images;
    }
}
