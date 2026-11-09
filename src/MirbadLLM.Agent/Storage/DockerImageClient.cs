using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.Storage;

/// <summary>Aggregated progress of an image pull.</summary>
/// <param name="BytesDone">Bytes downloaded over all layers seen so far.</param>
/// <param name="BytesTotal">Bytes expected over all layers seen so far (0 when unknown).</param>
/// <param name="LayersDone">Layers complete (downloaded and extracted, or already present).</param>
/// <param name="LayersTotal">Layers seen.</param>
/// <param name="Message">Last status line, e.g. <c>Downloading</c>.</param>
public sealed record ImagePullProgress(long BytesDone, long BytesTotal, int LayersDone, int LayersTotal, string? Message);

/// <summary>Docker reported an error in a pull's progress stream (bad reference, missing manifest, registry error).</summary>
public sealed class ImagePullFailedException(string message) : Exception(message);

/// <summary>The image operations the storage package needs from the container engine (design §4.3 images).</summary>
public interface IContainerImageEngine
{
    /// <summary>Every local image, one entry per tag (or per repository digest for untagged images).</summary>
    Task<IReadOnlyList<ImageInfo>> ListAsync(CancellationToken ct = default);

    /// <summary>One image by reference (<c>repo:tag</c>, <c>repo@sha256:…</c> or id); null when absent.</summary>
    Task<ImageInfo?> InspectAsync(string reference, CancellationToken ct = default);

    /// <summary>
    /// Pulls <paramref name="repository"/> at <paramref name="tagOrDigest"/> (a tag or <c>sha256:…</c>), reporting
    /// aggregated progress. Throws <see cref="ImagePullFailedException"/> when Docker reports an error.
    /// </summary>
    Task PullAsync(string repository, string tagOrDigest, Action<ImagePullProgress> progress, CancellationToken ct = default);

    /// <summary>As the other overload, logging in to the registry when given a login. An engine that has no registry ignores it.</summary>
    Task PullAsync(string repository, string tagOrDigest, RegistryCredentials? registry, Action<ImagePullProgress> progress, CancellationToken ct = default) =>
        PullAsync(repository, tagOrDigest, progress, ct);
}

/// <summary>
/// <see cref="IContainerImageEngine"/> over the Docker Engine API (the same <see cref="HttpClient"/> the agent uses for
/// <see cref="DockerEngineClient"/>, base address <c>http://docker/v1.47/</c>). The pull reads Docker's newline-delimited
/// JSON progress stream as it arrives instead of buffering it, so progress is reported while layers download.
/// </summary>
public sealed class DockerImageClient(HttpClient docker) : IContainerImageEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImageInfo>> ListAsync(CancellationToken ct = default)
    {
        using var response = await docker.GetAsync("images/json", ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var items = await response.Content.ReadFromJsonAsync<List<ImageSummaryDto>>(JsonOptions, ct).ConfigureAwait(false) ?? [];

        var result = new List<ImageInfo>();
        foreach (var item in items)
        {
            string? arch = await TryReadArchitectureAsync(item.Id, ct).ConfigureAwait(false);
            result.AddRange(ToInfos(item.Id, item.RepoTags, item.RepoDigests, arch, item.Size));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ImageInfo?> InspectAsync(string reference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        using var response = await docker.GetAsync($"images/{reference}/json", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var dto = await response.Content.ReadFromJsonAsync<ImageInspectDto>(JsonOptions, ct).ConfigureAwait(false);
        if (dto is null)
        {
            return null;
        }

        var reference0 = ImageReference.Parse(reference);
        string? digest = reference0.Digest ?? DigestFor(reference0.Repository, dto.RepoDigests);
        return new ImageInfo
        {
            Ref = reference,
            Id = dto.Id,
            Digest = digest,
            Arch = dto.Architecture,
            SizeBytes = dto.Size,
        };
    }

    /// <inheritdoc />
    public Task PullAsync(string repository, string tagOrDigest, Action<ImagePullProgress> progress, CancellationToken ct = default) =>
        PullAsync(repository, tagOrDigest, null, progress, ct);

    /// <inheritdoc />
    public async Task PullAsync(string repository, string tagOrDigest, RegistryCredentials? registry, Action<ImagePullProgress> progress, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(repository);
        ArgumentException.ThrowIfNullOrEmpty(tagOrDigest);
        ArgumentNullException.ThrowIfNull(progress);

        string url = $"images/create?fromImage={Uri.EscapeDataString(repository)}&tag={Uri.EscapeDataString(tagOrDigest)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (registry is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Registry-Auth", registry.ToDockerHeader(ContainerImageRegistry.HostOf(repository)));
        }

        using var response = await docker.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var layers = new Dictionary<string, LayerProgress>(StringComparer.Ordinal);
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            PullLineDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize<PullLineDto>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (dto is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(dto.Error))
            {
                throw new ImagePullFailedException(dto.Error);
            }

            if (!string.IsNullOrEmpty(dto.Id) && dto.Status is { } status && IsLayerStatus(status))
            {
                var layer = layers.TryGetValue(dto.Id, out var existing) ? existing : new LayerProgress();
                if (dto.ProgressDetail is { Total: > 0 } detail && status.StartsWith("Downloading", StringComparison.Ordinal))
                {
                    layer.Total = detail.Total;
                    layer.Current = Math.Min(detail.Current, detail.Total);
                }

                if (status is "Download complete" or "Pull complete" or "Already exists")
                {
                    layer.Current = layer.Total;
                    layer.Done = status is "Pull complete" or "Already exists";
                }

                layers[dto.Id] = layer;
            }

            progress(new ImagePullProgress(
                layers.Values.Sum(l => l.Current),
                layers.Values.Sum(l => l.Total),
                layers.Values.Count(l => l.Done),
                layers.Count,
                dto.Status));
        }
    }

    /// <summary>One <see cref="ImageInfo"/> per tag; untagged images are listed by repository digest.</summary>
    internal static IEnumerable<ImageInfo> ToInfos(string? id, IReadOnlyList<string>? repoTags, IReadOnlyList<string>? repoDigests, string? arch, long size)
    {
        var tags = (repoTags ?? []).Where(t => t != "<none>:<none>").ToList();
        if (tags.Count > 0)
        {
            foreach (string tag in tags)
            {
                yield return new ImageInfo
                {
                    Ref = tag,
                    Id = id,
                    Digest = DigestFor(ImageReference.Parse(tag).Repository, repoDigests),
                    Arch = arch,
                    SizeBytes = size,
                };
            }

            yield break;
        }

        foreach (string repoDigest in (repoDigests ?? []).Where(d => !d.StartsWith("<none>", StringComparison.Ordinal)))
        {
            yield return new ImageInfo
            {
                Ref = repoDigest,
                Id = id,
                Digest = ImageReference.Parse(repoDigest).Digest,
                Arch = arch,
                SizeBytes = size,
            };
        }
    }

    private static string? DigestFor(string repository, IReadOnlyList<string>? repoDigests)
    {
        foreach (string entry in repoDigests ?? [])
        {
            var parsed = ImageReference.Parse(entry);
            if (parsed.Digest is not null && string.Equals(parsed.Repository, repository, StringComparison.Ordinal))
            {
                return parsed.Digest;
            }
        }

        return null;
    }

    private static bool IsLayerStatus(string status) =>
        status.StartsWith("Pulling fs layer", StringComparison.Ordinal) || status.StartsWith("Waiting", StringComparison.Ordinal) ||
        status.StartsWith("Downloading", StringComparison.Ordinal) || status.StartsWith("Verifying", StringComparison.Ordinal) ||
        status.StartsWith("Download complete", StringComparison.Ordinal) || status.StartsWith("Extracting", StringComparison.Ordinal) ||
        status.StartsWith("Pull complete", StringComparison.Ordinal) || status.StartsWith("Already exists", StringComparison.Ordinal);

    private async Task<string?> TryReadArchitectureAsync(string? id, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        using var response = await docker.GetAsync($"images/{id}/json", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await response.Content.ReadFromJsonAsync<ImageInspectDto>(JsonOptions, ct).ConfigureAwait(false);
        return dto?.Architecture;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new DockerApiException(response.StatusCode, body);
    }

    private sealed class LayerProgress
    {
        public long Current { get; set; }

        public long Total { get; set; }

        public bool Done { get; set; }
    }

    private sealed class ImageSummaryDto
    {
        [JsonPropertyName("Id")]
        public string? Id { get; set; }

        [JsonPropertyName("RepoTags")]
        public List<string>? RepoTags { get; set; }

        [JsonPropertyName("RepoDigests")]
        public List<string>? RepoDigests { get; set; }

        [JsonPropertyName("Size")]
        public long Size { get; set; }
    }

    private sealed class ImageInspectDto
    {
        [JsonPropertyName("Id")]
        public string? Id { get; set; }

        [JsonPropertyName("RepoDigests")]
        public List<string>? RepoDigests { get; set; }

        [JsonPropertyName("Architecture")]
        public string? Architecture { get; set; }

        [JsonPropertyName("Size")]
        public long Size { get; set; }
    }

    private sealed class PullLineDto
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }

        [JsonPropertyName("progressDetail")]
        public PullProgressDetailDto? ProgressDetail { get; set; }
    }

    private sealed class PullProgressDetailDto
    {
        [JsonPropertyName("current")]
        public long Current { get; set; }

        [JsonPropertyName("total")]
        public long Total { get; set; }
    }
}

/// <summary>
/// A parsed image reference <c>[registry[:port]/]repository[:tag][@sha256:…]</c>. The tag separator is the last
/// <c>:</c> after the last <c>/</c>, so a registry port is never mistaken for a tag.
/// </summary>
/// <param name="Repository">Repository including any registry host.</param>
/// <param name="Tag">Tag, or null.</param>
/// <param name="Digest">Digest (<c>sha256:…</c>), or null.</param>
public sealed record ImageReference(string Repository, string? Tag, string? Digest)
{
    private static readonly SearchValues<char> LowerHex = SearchValues.Create("0123456789abcdef");

    /// <summary>Parses a reference; never throws (an empty repository means an unusable reference).</summary>
    public static ImageReference Parse(string? reference)
    {
        string value = (reference ?? "").Trim();
        string? digest = null;
        int at = value.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            digest = value[(at + 1)..];
            value = value[..at];
        }

        int lastSlash = value.LastIndexOf('/');
        int colon = value.LastIndexOf(':');
        string? tag = null;
        if (colon > lastSlash)
        {
            tag = value[(colon + 1)..];
            value = value[..colon];
        }

        return new ImageReference(value, string.IsNullOrEmpty(tag) ? null : tag, string.IsNullOrEmpty(digest) ? null : digest);
    }

    /// <summary>True for <c>sha256:</c> + 64 lowercase hex digits.</summary>
    public static bool IsValidDigest(string? digest) =>
        digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal) &&
        digest.AsSpan(7).IndexOfAnyExcept(LowerHex) < 0;

    /// <summary><c>repository@digest</c>.</summary>
    public static string Pinned(string repository, string digest) =>
        string.Create(CultureInfo.InvariantCulture, $"{repository}@{digest}");
}
