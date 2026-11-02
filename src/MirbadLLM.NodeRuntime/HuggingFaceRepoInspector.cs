using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// <see cref="IHuggingFaceRepoInspector"/> implemented against the same <c>https://huggingface.co/api/models/{repo}?blobs=true</c>
/// listing <see cref="HuggingFaceDownloader"/> uses to size a download, so the "Inspect repo" step in the
/// editor and the zero-match pre-flight check in <see cref="HuggingFaceDownloader.StartAsync"/> see the same data.
/// </summary>
public sealed partial class HuggingFaceRepoInspector(HttpClient huggingFaceHttp) : IHuggingFaceRepoInspector
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RepoInspection> InspectAsync(string repoId, string? revision, string? token, CancellationToken ct = default)
    {
        var files = await ListFilesAsync(repoId, revision, token, ct).ConfigureAwait(false);
        return BuildInspection(repoId, revision, files);
    }

    /// <summary>Raw file listing (name + size) for a repo/revision. Never logs <paramref name="token"/>.</summary>
    /// <exception cref="HuggingFaceApiException">The repo/revision is gated, private (401/403) or unknown (404).</exception>
    public async Task<IReadOnlyList<RepoFile>> ListFilesAsync(string repoId, string? revision, string? token, CancellationToken ct = default)
    {
        string path = string.IsNullOrEmpty(revision)
            ? $"api/models/{repoId}?blobs=true"
            : $"api/models/{repoId}/revision/{Uri.EscapeDataString(revision)}?blobs=true";

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await huggingFaceHttp.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new HuggingFaceApiException(
                $"'{repoId}' looks like a gated or private model. Provide a Hugging Face access token that has been granted access to it.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new HuggingFaceApiException($"Model '{repoId}' was not found on Hugging Face (check the repo id and revision).");
        }

        response.EnsureSuccessStatusCode();

        var info = await response.Content.ReadFromJsonAsync<HfModelInfoDto>(JsonOptions, ct).ConfigureAwait(false);
        var siblings = info?.Siblings ?? [];
        return siblings
            .Where(s => s.RFilename is not null)
            .Select(s => new RepoFile(s.RFilename!, s.Size ?? s.Lfs?.Size ?? 0))
            .ToList();
    }

    /// <summary>Pure grouping/format-detection over an already-fetched file listing (no network call).</summary>
    public static RepoInspection BuildInspection(string repoId, string? revision, IReadOnlyList<RepoFile> files)
    {
        var ggufFiles = files.Where(f => f.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)).ToList();
        var otherFiles = files.Where(f => !f.Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)).ToList();
        bool hasSafetensors = otherFiles.Any(f => f.Name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase));

        RepoFormat format = (ggufFiles.Count > 0, hasSafetensors) switch
        {
            (true, true) => RepoFormat.Mixed,
            (true, false) => RepoFormat.Gguf,
            (false, true) => RepoFormat.Safetensors,
            _ => RepoFormat.Other,
        };

        var (variants, auxiliary) = GroupGgufVariants(ggufFiles);
        return new RepoInspection(repoId, revision, format, files, variants, auxiliary, otherFiles);
    }

    /// <summary>
    /// Groups <c>.gguf</c> files into quantization variants, keeping split parts (<c>-00001-of-00003.gguf</c>)
    /// together, and pulls out single-file auxiliary weights (a speculative-decoding draft model, a vision
    /// projector) that aren't a quant of the base model. A file with "draft" in its name, or the vision-projector
    /// naming convention <c>mmproj-*.gguf</c>, is auxiliary; everything else groups by its name with the split-part
    /// suffix stripped, and the variant's display name is that grouped name with the common repo-file prefix removed
    /// (e.g. "Qwen3.8-Flash-Next-Uncensored-Q4_K_M" -&gt; "Q4_K_M"). Note "Q8_0-MTP" (a full quant that bakes in MTP
    /// speculative-decoding layers, split across several parts) is not auxiliary — only a bare "*-MTP-draft.gguf"
    /// single file is; "draft" is the differentiator, not "MTP" alone.
    /// </summary>
    private static (IReadOnlyList<GgufVariant> Variants, IReadOnlyList<RepoFile> Auxiliary) GroupGgufVariants(IReadOnlyList<RepoFile> ggufFiles)
    {
        var auxiliary = new List<RepoFile>();
        var candidates = new List<(RepoFile File, string BaseName)>();

        foreach (var file in ggufFiles)
        {
            string stem = file.Name[..^".gguf".Length];
            string fileNameOnly = stem[(stem.LastIndexOf('/') + 1)..];
            bool isAuxiliary = stem.Contains("draft", StringComparison.OrdinalIgnoreCase) ||
                fileNameOnly.StartsWith("mmproj-", StringComparison.OrdinalIgnoreCase) ||
                fileNameOnly.StartsWith("mmproj.", StringComparison.OrdinalIgnoreCase);

            if (isAuxiliary)
            {
                auxiliary.Add(file);
                continue;
            }

            string baseName = SplitPartSuffix().Replace(stem, "");
            candidates.Add((file, baseName));
        }

        if (candidates.Count == 0)
        {
            return ([], auxiliary);
        }

        string prefix = LongestCommonPrefix(candidates.Select(c => c.BaseName).Distinct(StringComparer.Ordinal).ToList());

        var groups = candidates
            .GroupBy(c => c.BaseName, StringComparer.Ordinal)
            .Select(g =>
            {
                string label = g.Key.Length > prefix.Length ? g.Key[prefix.Length..].Trim('-') : g.Key;
                if (label.Length == 0)
                {
                    label = g.Key;
                }

                var groupFiles = g.Select(c => c.File).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
                return new GgufVariant(label, groupFiles, groupFiles.Sum(f => f.Bytes));
            })
            .OrderBy(v => v.Name, StringComparer.Ordinal)
            .ToList();

        return (groups, auxiliary);
    }

    private static string LongestCommonPrefix(List<string> values)
    {
        if (values.Count == 0)
        {
            return "";
        }

        string prefix = values[0];
        foreach (string value in values.Skip(1))
        {
            int max = Math.Min(prefix.Length, value.Length);
            int i = 0;
            while (i < max && prefix[i] == value[i])
            {
                i++;
            }

            prefix = prefix[..i];
            if (prefix.Length == 0)
            {
                break;
            }
        }

        return prefix;
    }

    [GeneratedRegex(@"-\d{1,6}-of-\d{1,6}$")]
    private static partial Regex SplitPartSuffix();

    private sealed class HfModelInfoDto
    {
        [JsonPropertyName("siblings")]
        public List<HfSiblingDto>? Siblings { get; set; }
    }

    private sealed class HfSiblingDto
    {
        [JsonPropertyName("rfilename")]
        public string? RFilename { get; set; }

        [JsonPropertyName("size")]
        public long? Size { get; set; }

        [JsonPropertyName("lfs")]
        public HfLfsDto? Lfs { get; set; }
    }

    private sealed class HfLfsDto
    {
        [JsonPropertyName("size")]
        public long? Size { get; set; }
    }
}
