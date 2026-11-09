using System.Text.Json;
using System.Text.Json.Serialization;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Contracts;

/// <summary>
/// <c>POST /agent/v1/images/pull</c> (design §4.3): an operation (202) with progress events. Only approved digests are
/// pulled (§10.3); anything else is 422 <c>spec_rejected</c>.
/// </summary>
public sealed record ImagePullRequest : AgentMutation
{
    /// <summary>Image reference.</summary>
    public required string Image { get; init; }

    /// <summary>Expected repository digest. Null only where the node allows tag pulls (P1 DGX).</summary>
    public string? Digest { get; init; }

    /// <summary>The login for a private registry. Its password is never stored, so such a pull can't resume after an agent restart.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RegistryCredentials? Registry { get; init; }
}

/// <summary>A registry login for one pull. The field is named <c>password</c> so stored operation payloads redact it.</summary>
public sealed record RegistryCredentials
{
    public required string Username { get; init; }

    public required string Password { get; init; }

    /// <summary>The value of Docker's <c>X-Registry-Auth</c> header for a pull from <paramref name="host"/>.</summary>
    public string ToDockerHeader(string host)
    {
        string server = host == "docker.io" ? "https://index.docker.io/v1/" : host;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>
        {
            ["username"] = Username,
            ["password"] = Password,
            ["serveraddress"] = server,
        });
        return Convert.ToBase64String(json).Replace('+', '-').Replace('/', '_');
    }

    public override string ToString() => $"RegistryCredentials {{ Username = {Username}, Password = *** }}";
}

/// <summary>
/// One folder under the node's models root (design §4.3 <c>GET /model-folders</c>). The checkpoint summary fields are
/// filled for folders with a readable <c>config.json</c> (what the "add model" list shows today).
/// </summary>
public sealed record ModelFolderEntry
{
    /// <summary>Folder name under the models root.</summary>
    public required string Folder { get; init; }

    /// <summary>Size on disk.</summary>
    public long SizeBytes { get; init; }

    /// <summary>True when the folder has a <c>config.json</c>.</summary>
    public bool HasConfig { get; init; }

    /// <summary>True for an unfinished download (<c>{folder}.partial</c> or a running helper).</summary>
    public bool Partial { get; init; }

    /// <summary>Weights on disk in GiB (from the inspection), when it has a config.</summary>
    public double? WeightsGiB { get; init; }

    /// <summary>Architecture from <c>config.json</c>.</summary>
    public string? Architecture { get; init; }

    /// <summary>Detected weights format (e.g. <c>NVFP4</c>, <c>BF16</c>).</summary>
    public string? DetectedFormat { get; init; }
}

/// <summary>
/// <c>POST /agent/v1/model-folders/{folder}/delete</c> (design §4.3, §10.3): 200 with the operation; 409 <c>in_use</c>
/// when an instance or download uses the folder. Symlinks and anything outside the models root are refused. The master
/// separately refuses folders referenced by any placed model.
/// </summary>
public sealed record FolderDeleteRequest : AgentMutation
{
    /// <summary>Must equal the folder name (typed confirmation).</summary>
    public required string Confirm { get; init; }
}

/// <summary>What a download does when the destination folder already exists (design §4.3, §6.4).</summary>
public enum DownloadExistingMode
{
    /// <summary>409 <c>folder_exists</c> (default).</summary>
    Refuse,

    /// <summary>Verify present files and fetch only what is missing.</summary>
    Complete,
}

/// <summary>
/// <c>POST /agent/v1/downloads</c> (design §4.3, §6.4): 202 with the download status. The token passes through in memory
/// only: it is redacted from the operation log and digest, and (container helper) removed with the finished helper
/// container. <see cref="ToString"/> never prints it.
/// </summary>
public sealed record DownloadStartRequest : AgentMutation
{
    /// <summary>Hugging Face repo id; for an Ollama source <c>{name}:{tag}</c> (<see cref="OllamaSource.Key"/>).</summary>
    public required string RepoId { get; init; }

    /// <summary>Branch, tag or commit SHA. Null = default branch.</summary>
    public string? Revision { get; init; }

    /// <summary>Destination folder under the models root (validated: plain name, no <c>..</c>, no symlinks).</summary>
    public required string Folder { get; init; }

    /// <summary>Glob patterns. Null/empty = the downloader's default set.</summary>
    public IReadOnlyList<string>? AllowPatterns { get; init; }

    /// <summary>Hugging Face token for gated/private repos. Secret.</summary>
    public string? Token { get; init; }

    /// <summary>Existing-folder behaviour.</summary>
    public DownloadExistingMode Existing { get; init; } = DownloadExistingMode.Refuse;

    /// <summary>
    /// Source kind (design-p3p4 §A.2; MN-202, D116). Omitted at its default (Hugging Face), so every P1/P2 body is
    /// unchanged. <see cref="ModelSourceKind.Ollama"/> is sent only to agents reporting <see cref="AgentFeatures.DownloadsOllama"/>
    /// and needs <see cref="Ollama"/>. A Hugging Face download runs in the container helper where the node reports
    /// <see cref="AgentFeatures.DownloadsHfContainer"/>, else in the native downloader (<see cref="AgentFeatures.DownloadsHfNative"/>):
    /// the agent chooses, the body is the same.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ModelSourceKind Source { get; init; } = ModelSourceKind.HuggingFace;

    /// <summary>The Ollama-registry model when <see cref="Source"/> is <see cref="ModelSourceKind.Ollama"/>; omitted otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OllamaSource? Ollama { get; init; }

    /// <summary>Redacts <see cref="Token"/>.</summary>
    public override string ToString() =>
        $"DownloadStartRequest {{ OperationId = {OperationId}, RepoId = {RepoId}, Revision = {Revision}, Folder = {Folder}, Token = {(Token is null ? "null" : Protocol.RedactedValue)}, Existing = {Existing}, Source = {Source} }}";
}

/// <summary>
/// A download on a node (design §4.3 <c>DownloadStatus</c>): today's <see cref="DownloadStatus"/> fields plus the
/// operation that started it and <see cref="NeedsToken"/> (a paused authenticated native download after an agent
/// restart, §6.4).
/// </summary>
public sealed record NodeDownloadStatus
{
    /// <summary>Destination folder.</summary>
    public required string Folder { get; init; }

    /// <summary>Repo id.</summary>
    public required string RepoId { get; init; }

    /// <summary>Requested revision.</summary>
    public string? Revision { get; init; }

    /// <summary>Lifecycle state.</summary>
    public required DownloadState State { get; init; }

    /// <summary>Bytes present in the target folder.</summary>
    public long BytesDownloaded { get; init; }

    /// <summary>Expected total; 0 when unknown.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Null until two samples were observed.</summary>
    public double? BytesPerSecond { get; init; }

    /// <summary>Error when Failed.</summary>
    public string? Error { get; init; }

    /// <summary>Operation that started it.</summary>
    public string? OperationId { get; init; }

    /// <summary>True when an authenticated download is paused and needs the token again (§6.4).</summary>
    public bool NeedsToken { get; init; }

    /// <summary>When it started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>When it ended.</summary>
    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>Today's <see cref="DownloadStatus"/> view.</summary>
    public DownloadStatus ToDownloadStatus() =>
        new(Folder, RepoId, Revision, State, BytesDownloaded, TotalBytes, BytesPerSecond, Error);
}

/// <summary>
/// <c>POST /agent/v1/downloads/{folder}/cancel</c> (design §4.3): 200 with the operation; a download that already
/// finished → Succeeded with its final state as the result.
/// </summary>
public sealed record DownloadCancelRequest : AgentMutation;
