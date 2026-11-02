using System.Text.Json.Serialization;

namespace MirbadLLM.Core;

/// <summary>Lifecycle of a Hugging Face checkpoint download running as a helper container.</summary>
public enum DownloadState
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Requests a download of <see cref="RepoId"/> into <c>{models root}/{Folder}</c>.</summary>
/// <param name="RepoId">Hugging Face repo id, e.g. <c>ornith-ai/Ornith-1.5-9B</c>.</param>
/// <param name="Revision">Branch, tag or commit SHA. Null means the repo's default branch.</param>
/// <param name="Token">Hugging Face access token; only needed for gated/private repos. Never logged or put on a command line.</param>
/// <param name="Folder">Destination folder name under the models root. Null/empty defaults to the repo name (the part after the last <c>/</c>).</param>
/// <param name="AllowPatterns">Glob patterns of files to fetch. Null/empty uses the default set (weights, config, tokenizer).</param>
public sealed record DownloadRequest(
    string RepoId,
    string? Revision = null,
    string? Token = null,
    string? Folder = null,
    IReadOnlyList<string>? AllowPatterns = null)
{
    /// <summary>
    /// Source kind (MN-202, D116; <c>POST /api/downloads</c> with an Ollama source is MN-229, deferred with O7). Default
    /// Hugging Face: every existing caller is unchanged. For <see cref="ModelSourceKind.Ollama"/>, <see cref="RepoId"/> is
    /// <c>{name}:{tag}</c> and <see cref="Ollama"/> is required.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ModelSourceKind Source { get; init; } = ModelSourceKind.HuggingFace;

    /// <summary>The Ollama-registry model when <see cref="Source"/> is <see cref="ModelSourceKind.Ollama"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OllamaSource? Ollama { get; init; }

    /// <summary>Never prints the Hugging Face token (W9 review MN-132, design §10.4).</summary>
    public override string ToString() =>
        $"DownloadRequest {{ RepoId = {RepoId}, Revision = {Revision}, Token = {(Token is null ? "null" : "***")}, Folder = {Folder} }}";
}

/// <summary>Live view of one download.</summary>
/// <param name="BytesDownloaded">Bytes currently present in the target folder (proxy for progress).</param>
/// <param name="TotalBytes">Expected total from the Hugging Face repo listing at the matched files. 0 when unknown.</param>
/// <param name="BytesPerSecond">Null until two samples have been observed.</param>
/// <param name="Error">Set when <see cref="State"/> is <see cref="DownloadState.Failed"/>.</param>
public sealed record DownloadStatus(
    string Folder,
    string RepoId,
    string? Revision,
    DownloadState State,
    long BytesDownloaded,
    long TotalBytes,
    double? BytesPerSecond,
    string? Error);

/// <summary>Starts, lists and cancels Hugging Face checkpoint downloads (implemented as Docker helper containers).</summary>
public interface IHuggingFaceDownloader
{
    /// <exception cref="ArgumentException">The request is invalid (empty repo id, or no repo file matches the effective allow-patterns).</exception>
    Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default);

    Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default);

    /// <summary>Stops and removes the download container. Returns false when no download exists for <paramref name="folder"/>.</summary>
    Task<bool> CancelAsync(string folder, CancellationToken ct = default);
}

/// <summary>Detected file format of a Hugging Face repo, from its file listing's extensions.</summary>
public enum RepoFormat
{
    Safetensors,
    Gguf,
    Mixed,
    Other,
}

/// <summary>One file in a Hugging Face repo listing (name relative to the repo root, size in bytes).</summary>
public sealed record RepoFile(string Name, long Bytes);

/// <summary>
/// One GGUF quantization variant: its files with any split parts (<c>-00001-of-00003.gguf</c> etc.) kept
/// together, and their combined size.
/// </summary>
public sealed record GgufVariant(string Name, IReadOnlyList<RepoFile> Files, long TotalBytes)
{
    public int FileCount => Files.Count;
}

/// <summary>
/// Result of inspecting a Hugging Face repo's file listing without downloading anything: its detected
/// <see cref="Format"/> and, for a GGUF (or mixed) repo, the quantization <see cref="Variants"/> (split parts
/// grouped), companion <see cref="AuxiliaryFiles"/> that aren't part of any variant (a speculative-decoding
/// draft model, a vision projector), and every non-GGUF file (<see cref="OtherFiles"/>: config, tokenizer,
/// README, safetensors weights for a mixed repo).
/// </summary>
public sealed record RepoInspection(
    string RepoId,
    string? Revision,
    RepoFormat Format,
    IReadOnlyList<RepoFile> AllFiles,
    IReadOnlyList<GgufVariant> Variants,
    IReadOnlyList<RepoFile> AuxiliaryFiles,
    IReadOnlyList<RepoFile> OtherFiles);

/// <summary>Inspects a Hugging Face repo's file listing (format, GGUF variants) without downloading anything.</summary>
public interface IHuggingFaceRepoInspector
{
    Task<RepoInspection> InspectAsync(string repoId, string? revision, string? token, CancellationToken ct = default);
}
