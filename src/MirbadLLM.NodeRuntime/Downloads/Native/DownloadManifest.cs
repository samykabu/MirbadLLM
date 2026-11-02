using System.Text.Json;
using System.Text.Json.Serialization;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>Lifecycle of a native download as recorded in its manifest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DownloadManifestState>))]
public enum DownloadManifestState
{
    /// <summary>Running, or interrupted by an agent stop or crash (the next start resumes or pauses it).</summary>
    [JsonStringEnumMemberName("downloading")]
    Downloading,

    /// <summary>Interrupted while it used a token, which the agent never stores: waits for the token (design §6.4).</summary>
    [JsonStringEnumMemberName("paused")]
    Paused,

    /// <summary>Ended with an error; the files are kept so a new start resumes it.</summary>
    [JsonStringEnumMemberName("failed")]
    Failed,

    /// <summary>Cancelled by the operator; the files are kept so a new start resumes it.</summary>
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,

    /// <summary>Every file verified and the folder finalized.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,
}

/// <summary>Per-file state in the manifest.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DownloadFileState>))]
public enum DownloadFileState
{
    /// <summary>Not yet verified (absent, partial or unchecked).</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>Size and digest verified.</summary>
    [JsonStringEnumMemberName("verified")]
    Verified,
}

/// <summary>One file of a native download.</summary>
public sealed record DownloadManifestFile
{
    /// <summary>Repo-relative path (<c>/</c>-separated), already checked by <see cref="RepoPathRules"/>.</summary>
    public required string Path { get; init; }

    /// <summary>Size in bytes from the Hub listing.</summary>
    public required long Size { get; init; }

    /// <summary>LFS oid (SHA-256 of the content), lower-case hex; null for a small non-LFS file.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LfsSha256 { get; init; }

    /// <summary>Git blob SHA-1 of a non-LFS file, when the listing gave it.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BlobSha1 { get; init; }

    /// <summary>Progress of this file.</summary>
    public DownloadFileState State { get; init; } = DownloadFileState.Pending;

    /// <summary>The digest to verify (LFS SHA-256, else the git blob SHA-1, else none: size only).</summary>
    public ContentDigest? Digest() =>
        ContentDigest.IsHex(LfsSha256, 64) ? ContentDigest.Sha256(LfsSha256!)
        : ContentDigest.IsHex(BlobSha1, 40) ? ContentDigest.GitBlobSha1(BlobSha1!, Size)
        : null;
}

/// <summary>
/// <c>{models}/{folder}.partial/.mirbadllm-download.json</c> (design §6.4 "Manifest"): repo, the commit SHA the revision
/// resolved to, the allow patterns and per-file <c>{path, size, lfsSha256?, state}</c>. Kept in the finalized folder.
/// <para>It never holds a secret: <see cref="Authenticated"/> only says that a token was used, so that a restarted
/// agent knows to ask for it again (<see cref="DownloadManifestState.Paused"/>).</para>
/// </summary>
public sealed record DownloadManifest
{
    /// <summary>File name of the manifest inside the download folder.</summary>
    public const string FileName = ".mirbadllm-download.json";

    /// <summary>Current format version.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Format version.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Source kind (Hugging Face for this downloader; the Ollama puller, MN-221, writes its own kind).</summary>
    public ModelSourceKind Source { get; init; } = ModelSourceKind.HuggingFace;

    /// <summary>Repo id.</summary>
    public required string RepoId { get; init; }

    /// <summary>The revision as requested (branch, tag or SHA); null = the default branch.</summary>
    public string? Revision { get; init; }

    /// <summary>The immutable commit SHA every file is fetched at (<c>resolve/{sha}/{file}</c>).</summary>
    public required string CommitSha { get; init; }

    /// <summary>Allow patterns the file list was selected with.</summary>
    public required IReadOnlyList<string> AllowPatterns { get; init; }

    /// <summary>True when the download used a token (never the token itself).</summary>
    public bool Authenticated { get; init; }

    /// <summary>Lifecycle state.</summary>
    public DownloadManifestState State { get; init; } = DownloadManifestState.Downloading;

    /// <summary>When the download was first started.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last write.</summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Files, in download order.</summary>
    public required IReadOnlyList<DownloadManifestFile> Files { get; init; }

    /// <summary>Sum of the file sizes.</summary>
    [JsonIgnore]
    public long TotalBytes => Files.Sum(f => f.Size);

    /// <summary>Path of the manifest in <paramref name="folder"/>.</summary>
    public static string PathIn(string folder) => System.IO.Path.Combine(folder, FileName);

    /// <summary>
    /// Reads the manifest of <paramref name="folder"/>; null when there is none. Throws <see cref="InvalidDataException"/>
    /// for a manifest that is unreadable, of a newer version or not plausible (bad SHA, bad paths).
    /// </summary>
    public static DownloadManifest? TryRead(string folder)
    {
        string path = PathIn(folder);
        if (!File.Exists(path) && File.Exists(System.IO.Path.Combine(folder, LegacyNames.DownloadMarkerFile)))
        {
            path = System.IO.Path.Combine(folder, LegacyNames.DownloadMarkerFile); // written before the rename
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return null;
        }

        if (RepoPathRules.IsLink(info))
        {
            throw new InvalidDataException("The download manifest is a link.");
        }

        if (info.Length > 64L * 1024 * 1024)
        {
            throw new InvalidDataException("The download manifest is too large.");
        }

        DownloadManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DownloadManifest>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The download manifest is not valid JSON.", ex);
        }

        if (manifest is null || manifest.Version != CurrentVersion || !ContentDigest.IsHex(manifest.CommitSha, 40) ||
            string.IsNullOrWhiteSpace(manifest.RepoId) || manifest.Files is null || manifest.AllowPatterns is null ||
            RepoPathRules.CheckAll(manifest.Files.Select(f => f.Path)) is not null || manifest.Files.Any(f => f.Size < 0))
        {
            throw new InvalidDataException("The download manifest is not a version-1 manifest this agent can read.");
        }

        return manifest;
    }

    /// <summary>Writes the manifest atomically (temp file, flush to disk, rename) into <paramref name="folder"/>.</summary>
    public void Write(string folder)
    {
        string path = PathIn(folder);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, this, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
        DirectorySync.TryFlushParent(path);
        File.Delete(System.IO.Path.Combine(folder, LegacyNames.DownloadMarkerFile)); // superseded by the one just written
    }

    /// <summary>Bytes of the files present (verified files count fully, partial files by length up to their size).</summary>
    public long PresentBytes(string folder)
    {
        long total = 0;
        foreach (var file in Files)
        {
            var info = new FileInfo(System.IO.Path.Combine(folder, System.IO.Path.Combine(file.Path.Split('/'))));
            if (info.Exists && !RepoPathRules.IsLink(info))
            {
                total += Math.Min(info.Length, file.Size);
            }
        }

        return total;
    }
}
