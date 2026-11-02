using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>One file of a Hub snapshot.</summary>
/// <param name="Path">Repo-relative path.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="LfsSha256">LFS oid (SHA-256), lower-case hex; null for a non-LFS file.</param>
/// <param name="BlobSha1">Git blob SHA-1 (non-LFS files only); null when absent.</param>
public sealed record HubFile(string Path, long Size, string? LfsSha256, string? BlobSha1);

/// <summary>A repo at an immutable commit (design §6.4 "Immutable revision").</summary>
/// <param name="RepoId">Repo id.</param>
/// <param name="CommitSha">40-hex commit SHA the requested revision resolved to.</param>
/// <param name="Files">Every file of the repo at that commit.</param>
public sealed record HubSnapshot(string RepoId, string CommitSha, IReadOnlyList<HubFile> Files);

/// <summary>
/// The Hugging Face Hub calls of the native downloader: <c>GET api/models/{repo}[/revision/{rev}]?blobs=true</c> resolves
/// the revision to its commit SHA and lists files with sizes, LFS SHA-256 and blob ids; files are then fetched at
/// <c>{repo}/resolve/{sha}/{path}</c> only, so a branch that moves mid-download cannot mix files. The token, when given,
/// is the <c>Authorization</c> header of these requests (sent to the Hub's own origin only), never logged or put in a
/// message.
/// </summary>
public sealed partial class HuggingFaceHubClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SecureDownloadTransport _transport;
    private readonly Uri _endpoint;

    /// <summary>Creates the client.</summary>
    /// <param name="transport">Transport whose credential origin is the Hub endpoint.</param>
    /// <param name="endpoint">Hub base URL (default <c>https://huggingface.co/</c>).</param>
    public HuggingFaceHubClient(SecureDownloadTransport transport, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(endpoint);
        _transport = transport;
        _endpoint = endpoint.AbsoluteUri.EndsWith('/') ? endpoint : new Uri(endpoint.AbsoluteUri + "/");
    }

    /// <summary>The Hub base URL.</summary>
    public Uri Endpoint => _endpoint;

    /// <summary>True for a plausible repo id (<c>name</c> or <c>owner/name</c>, Hub characters only).</summary>
    public static bool IsValidRepoId(string? repoId) => repoId is { Length: > 0 and <= 200 } && RepoIdRegex().IsMatch(repoId);

    /// <summary>True for a revision the Hub could know (no control characters, at most 255 characters).</summary>
    public static bool IsValidRevision(string? revision) =>
        revision is null || (revision.Length is > 0 and <= 255 && !revision.Any(char.IsControl) && !revision.Contains("..", StringComparison.Ordinal));

    /// <summary>
    /// Resolves <paramref name="revision"/> (null = default branch) to a commit SHA and lists the repo's files at it.
    /// Throws <see cref="HuggingFaceApiException"/> for 401/403/404 (as <see cref="HuggingFaceRepoInspector"/>),
    /// <see cref="DownloadRefusedException"/> for a non-HTTPS target or an implausible answer, and lets transport errors
    /// (<see cref="HttpRequestException"/>, timeouts) through.
    /// </summary>
    public async Task<HubSnapshot> GetSnapshotAsync(string repoId, string? revision, string? token, CancellationToken ct)
    {
        if (!IsValidRepoId(repoId))
        {
            throw new DownloadRefusedException($"'{RepoPathRules.Printable(repoId ?? "")}' is not a Hugging Face repo id.");
        }

        if (!IsValidRevision(revision))
        {
            throw new DownloadRefusedException("The revision is not a valid branch, tag or commit.");
        }

        string relative = revision is null
            ? $"api/models/{repoId}?blobs=true"
            : $"api/models/{repoId}/revision/{Uri.EscapeDataString(revision)}?blobs=true";
        using var response = await _transport.GetAsync(new Uri(_endpoint, relative), Credential(token), null, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new HuggingFaceApiException(
                $"'{repoId}' looks like a gated or private model. Provide a Hugging Face access token that has been granted access to it.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HuggingFaceApiException($"Model '{repoId}' was not found on Hugging Face (check the repo id and revision).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The Hugging Face API answered HTTP {(int)response.StatusCode} for '{repoId}'.", null, response.StatusCode);
        }

        if (response.Content.Headers.ContentLength is { } declared && declared > _transport.Options.MaxListingBytes)
        {
            throw new DownloadRefusedException($"The Hugging Face listing of '{repoId}' is larger than {_transport.Options.MaxListingBytes} bytes.");
        }

        byte[] body = await ReadBoundedAsync(response, _transport.Options.MaxListingBytes, ct).ConfigureAwait(false);
        ModelInfoDto? info;
        try
        {
            info = JsonSerializer.Deserialize<ModelInfoDto>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new DownloadRefusedException($"The Hugging Face listing of '{repoId}' is not valid JSON.", ex);
        }

        if (info?.Sha is not { } sha || !ContentDigest.IsHex(sha, 40))
        {
            throw new DownloadRefusedException($"The Hugging Face listing of '{repoId}' has no commit SHA, so the revision cannot be pinned.");
        }

        var files = new List<HubFile>();
        foreach (var sibling in info.Siblings ?? [])
        {
            if (sibling.RFilename is not { } name)
            {
                continue;
            }

            long? size = sibling.Lfs?.Size ?? sibling.Size;
            if (size is not { } bytes || bytes < 0)
            {
                throw new DownloadRefusedException($"The Hugging Face listing of '{repoId}' has no size for '{RepoPathRules.Printable(name)}'.");
            }

            string? lfs = sibling.Lfs?.Sha256 is { } oid && ContentDigest.IsHex(oid, 64) ? oid.ToLowerInvariant() : null;
            if (sibling.Lfs is not null && lfs is null)
            {
                throw new DownloadRefusedException($"The Hugging Face listing of '{repoId}' has no valid LFS SHA-256 for '{RepoPathRules.Printable(name)}'.");
            }

            string? blob = lfs is null && sibling.BlobId is { } id && ContentDigest.IsHex(id, 40) ? id.ToLowerInvariant() : null;
            files.Add(new HubFile(name, bytes, lfs, blob));
            if (files.Count > _transport.Options.MaxFiles)
            {
                throw new DownloadRefusedException($"'{repoId}' has more than {_transport.Options.MaxFiles} files.");
            }
        }

        return new HubSnapshot(repoId, sha.ToLowerInvariant(), files);
    }

    /// <summary>URL of <paramref name="path"/> at <paramref name="commitSha"/> (<c>{repo}/resolve/{sha}/{path}</c>, each segment escaped).</summary>
    public Uri FileUrl(string repoId, string commitSha, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        return new Uri(_endpoint, $"{repoId}/resolve/{commitSha}/{escaped}");
    }

    /// <summary>The <c>Authorization</c> value for <paramref name="token"/>; null without a token.</summary>
    public static AuthenticationHeaderValue? Credential(string? token) =>
        string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    throw new DownloadRefusedException($"The Hugging Face listing is larger than {limit} bytes.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,95}(/[A-Za-z0-9][A-Za-z0-9._-]{0,95})?$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoIdRegex();

    private sealed class ModelInfoDto
    {
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("siblings")]
        public List<SiblingDto>? Siblings { get; set; }
    }

    private sealed class SiblingDto
    {
        [JsonPropertyName("rfilename")]
        public string? RFilename { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("blobId")]
        public string? BlobId { get; set; }

        [JsonPropertyName("lfs")]
        public LfsDto? Lfs { get; set; }
    }

    private sealed class LfsDto
    {
        [JsonPropertyName("sha256")]
        public string? Sha256 { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }
    }
}
