namespace MirbadLLM.NodeRuntime.Abstractions;

/// <summary>
/// One logical executable in <c>agent.json</c> (<c>Agent:Executables[]</c>, design-p3p4 §A.1.2; frozen by MN-202, D116),
/// written by <c>install-agent.ps1 -LlamaCpp</c> (MN-216) and read by <c>ExecutableCatalog</c> (MN-222):
/// <code>
/// "Executables": [ { "Name": "llama-server", "Default": "b11476",
///   "Versions": [ { "Version": "b11476", "Cuda": "13.4",
///                   "Path": "C:\\Program Files\\mirbadllm-agent\\runtimes\\llama.cpp\\b11476\\llama-server.exe",
///                   "Manifest": "C:\\Program Files\\mirbadllm-agent\\runtimes\\llama.cpp\\b11476\\manifest.json" } ] } ]
/// </code>
/// </summary>
public sealed class ExecutableOptions
{
    /// <summary>Logical name the master sends (<c>MirbadLLM.Agent.Contracts.ExecutableNames</c>), e.g. <c>llama-server</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>The version used when a launch names none; must be one of <see cref="Versions"/>. Null = the only version.</summary>
    public string? Default { get; set; }

    /// <summary>Installed versions, each in its own read-only folder.</summary>
    public IList<ExecutableVersionOptions> Versions { get; } = [];
}

/// <summary>One installed version of an <see cref="ExecutableOptions"/>.</summary>
public sealed class ExecutableVersionOptions
{
    /// <summary>Version (release tag), e.g. <c>b11476</c>.</summary>
    public string Version { get; set; } = "";

    /// <summary>Absolute path of the executable.</summary>
    public string Path { get; set; } = "";

    /// <summary>Absolute path of the install manifest (<see cref="ExecutableManifest"/>) written by the installer.</summary>
    public string Manifest { get; set; } = "";

    /// <summary>CUDA variant, e.g. <c>13.4</c>; null for a CPU build.</summary>
    public string? Cuda { get; set; }
}

/// <summary>
/// <c>manifest.json</c> in an executable's version folder (design-p3p4 §A.2.3; frozen by MN-202, D116): SHA-256 of every
/// file the installer unpacked (the lock file's <c>files</c> list, 55 files for llama.cpp <c>b11476</c>). The catalogue
/// hashes <b>every</b> listed file, not only the executable: <c>llama-server.exe</c> is a 9 KB launcher and the server
/// code lives in <c>llama-server-impl.dll</c>, <c>llama*.dll</c>, <c>ggml*.dll</c> and the CUDA DLLs (spike S1 / C3;
/// MN-207, D120). It does so in the background at startup and again whenever a file's size or modification time changes.
/// A missing or mismatching listed file, or any <c>.exe</c>/<c>.dll</c> in the folder that the manifest does not list (a
/// planted DLL would be loaded from the application folder first), makes that version unverified. camelCase JSON.
/// </summary>
public sealed record ExecutableManifest
{
    /// <summary>Manifest schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Logical name, e.g. <c>llama-server</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Version, e.g. <c>b11476</c>.</summary>
    public required string Version { get; init; }

    /// <summary>CUDA variant, when any.</summary>
    public string? Cuda { get; init; }

    /// <summary>Where the files came from (the lock file's asset names).</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Every file of the folder, relative to the manifest's folder.</summary>
    public required IReadOnlyList<ExecutableManifestFile> Files { get; init; }

    /// <summary>
    /// True when <see cref="Files"/> is the folder's complete content (docs/macos/implementation-plan.md, Phase 3): any
    /// other file in it, of any kind, is a mismatch, not only an unlisted executable. An interpreter's folder needs this
    /// (MLX's virtualenv): an unlisted <c>.py</c> or <c>.pth</c> file would be code Python runs. Symlinks to files count as
    /// the file they point to (hashed through the link); links to folders are not followed. Omitted (false) in the
    /// llama.cpp manifests.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool AllFiles { get; init; }
}

/// <summary>One file in an <see cref="ExecutableManifest"/>.</summary>
/// <param name="Path">Path relative to the version folder, <c>/</c>-separated.</param>
/// <param name="Sha256">Lowercase hex SHA-256.</param>
/// <param name="Bytes">Size in bytes.</param>
public sealed record ExecutableManifestFile(string Path, string Sha256, long Bytes);

/// <summary>An executable version resolved for a launch.</summary>
/// <param name="Name">Logical name.</param>
/// <param name="Version">Resolved version (the default when the launch named none).</param>
/// <param name="Path">Verified absolute path.</param>
/// <param name="Folder">The version folder (working directory, the child's only <c>PATH</c> entry, orphan scan root).</param>
/// <param name="Cuda">CUDA variant, when any.</param>
public sealed record ResolvedExecutable(string Name, string Version, string Path, string Folder, string? Cuda);

/// <summary>State of one installed version as the catalogue last verified it.</summary>
/// <param name="Name">Logical name.</param>
/// <param name="Version">Version.</param>
/// <param name="Cuda">CUDA variant, when any.</param>
/// <param name="Verified">True when every manifest file matched.</param>
/// <param name="IsDefault">True for the node default.</param>
/// <param name="Problem">Why it is unverified (first mismatching file, unreadable manifest); null when verified.</param>
public sealed record ExecutableVersionState(string Name, string Version, string? Cuda, bool Verified, bool IsDefault, string? Problem);

/// <summary>
/// Logical executable name + version → verified path (design-p3p4 §A.1.2; frozen by MN-202, D116; implemented by MN-222
/// from <c>Agent:Executables</c>). Read by the process driver (<c>CheckSpec</c> and launch), the llama.cpp capability
/// contributor (<c>RuntimeCapability.Versions</c>) and the <c>executable_unverified</c> health contributor.
/// </summary>
public interface IExecutableCatalog
{
    /// <summary>Every configured version with its last verification result.</summary>
    IReadOnlyList<ExecutableVersionState> List();

    /// <summary>
    /// Resolves <paramref name="name"/> at <paramref name="version"/> (null = its default). Throws
    /// <c>AgentException</c> 422 <c>executable_missing</c> for an unknown name or version and 422
    /// <c>executable_unverified</c> when the version failed verification. Re-verifies first when a file's size or
    /// modification time changed since the last check. Synchronous, so <c>IRuntimeDriver.CheckSpec</c> can call it.
    /// </summary>
    ResolvedExecutable Resolve(string name, string? version);

    /// <summary>Re-hashes every version now (startup; the catalogue may also do it in the background).</summary>
    Task RefreshAsync(CancellationToken ct = default);
}
