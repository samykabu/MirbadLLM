using System.Globalization;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// The storage package's part of the capability report and health (design §3.3, §3.5): local images, the <c>models</c>
/// (and, when configured, <c>cache</c>) roots with free space and folder count, the <c>downloads.hf-container</c>
/// feature, and the Degraded reason <c>model_root_low</c> below 5 % free.
/// <para>The feature says what this agent can do, not whether Docker answers right now (design §3.5, §3.6; W4 review
/// MN-096): while Docker is down the download routes answer 503 <c>docker_unavailable</c> (the clear reason of a Degraded
/// node, with <c>docker_unreachable</c> in its health) instead of 501 <c>not_supported</c>, and the master keeps offering
/// downloads.</para>
/// <para>With Docker off (<see cref="DockerAvailability"/>, MN-214/MN-232) the node cannot run a download helper
/// container, so <c>downloads.hf-container</c> is not reported here either (the native downloader's
/// <see cref="NativeDownloadContributor"/> reports <c>downloads.hf-native</c> instead) and the gated image engine lists
/// nothing. <see cref="DockerAvailability.Filter"/> would strip the feature anyway; this keeps the contributor from
/// claiming it in the first place. Null (tests, every P1/P2 composition) = Docker on, byte-identical to before.</para>
/// </summary>
public sealed class StorageCapabilities(
    IContainerImageEngine images,
    ModelRootPaths paths,
    IDiskSpaceProbe disk,
    IOptions<AgentStorageOptions> options,
    DockerAvailability? docker = null) : ICapabilityContributor, IHealthContributor
{
    private readonly AgentStorageOptions _options = options.Value;
    private readonly bool _docker = docker?.Enabled ?? true;

    /// <inheritdoc />
    public async Task<NodeCapabilities> ContributeAsync(NodeCapabilities current, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        IReadOnlyList<ImageInfo> found = [];
        try
        {
            found = await images.ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (AgentImageService.IsDockerFailure(ex))
        {
            // No images while Docker is down; the health reports docker_unreachable.
        }

        var roots = current.ModelRoots.Where(r => r.Name is not LogicalVolumes.Models and not LogicalVolumes.Cache).ToList();
        roots.Add(RootInfo(LogicalVolumes.Models, paths.Root));
        if (!string.IsNullOrWhiteSpace(_options.CacheRoot))
        {
            roots.Add(RootInfo(LogicalVolumes.Cache, _options.CacheRoot));
        }

        var features = current.Features.ToList();
        if (_docker && !features.Contains(AgentFeatures.DownloadsHfContainer, StringComparer.Ordinal))
        {
            features.Add(AgentFeatures.DownloadsHfContainer);
        }

        var mergedImages = current.Images.Concat(found)
            .DistinctBy(i => (i.Ref, i.Digest))
            .ToList();

        return current with { Images = mergedImages, ModelRoots = roots, Features = features };
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default)
    {
        var reasons = new List<HealthReason>();
        AddLowSpace(reasons, LogicalVolumes.Models, paths.Root);
        if (!string.IsNullOrWhiteSpace(_options.CacheRoot))
        {
            AddLowSpace(reasons, LogicalVolumes.Cache, _options.CacheRoot);
        }

        return Task.FromResult<IReadOnlyList<HealthReason>>(reasons);
    }

    private void AddLowSpace(List<HealthReason> reasons, string name, string path)
    {
        if (disk.Probe(path) is { TotalBytes: > 0 } space &&
            (double)space.FreeBytes / space.TotalBytes < _options.LowFreeSpaceFraction)
        {
            reasons.Add(new HealthReason(HealthReasonCodes.ModelRootLow, string.Create(CultureInfo.InvariantCulture,
                $"The {name} volume has {(double)space.FreeBytes / space.TotalBytes:P1} free (below {_options.LowFreeSpaceFraction:P0}).")));
        }
    }

    private ModelRootInfo RootInfo(string name, string path)
    {
        var space = disk.Probe(path);
        int folders = 0;
        try
        {
            if (Directory.Exists(path))
            {
                folders = Directory.EnumerateDirectories(path).Count(d => !Path.GetFileName(d).StartsWith('.'));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            folders = 0;
        }

        return new ModelRootInfo(name, space?.FreeBytes ?? 0, space?.TotalBytes ?? 0, folders);
    }
}
