namespace MirbadLLM.NodeRuntime;

/// <summary><c>GET /backup-mount</c> as reported by mirbadllm-hostd. Never carries the password.</summary>
public sealed record HostAgentBackupMount(
    string Target,
    string Unit,
    bool Configured,
    bool ForeignUnit,
    string? Type,
    string? Server,
    string? Share,
    string? Subfolder,
    string? Username,
    string? Domain,
    string? Version,
    string? Source,
    string? Options,
    bool Mounted,
    string? MountedSource,
    string? MountedType,
    long? TotalBytes,
    long? FreeBytes,
    DateTimeOffset? ConfiguredAt,
    string? LastError,
    string? Warning);

/// <summary><c>POST /backup-mount/test</c>: whether the share could be mounted and written by the controller's user.</summary>
public sealed record HostAgentBackupMountTest(bool Ok, string? Message);

/// <summary>
/// The body of <c>PUT /backup-mount</c> and <c>POST /backup-mount/test</c>. A class rather than a record so the
/// password never appears in a generated <c>ToString()</c> (and so in a log line).
/// </summary>
public sealed class HostAgentBackupMountRequest
{
    public string Type { get; set; } = "";

    public string Server { get; set; } = "";

    public string Share { get; set; } = "";

    public string? Subfolder { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? Domain { get; set; }

    public string? SmbVersion { get; set; }

    public string? NfsVersion { get; set; }
}

/// <summary>The host agent's backup-mount endpoints (implemented by <see cref="HostAgentClient"/>; faked in tests).</summary>
public interface IHostAgentBackupMountClient
{
    /// <exception cref="HostAgentUnavailableException">The agent is not installed or does not answer.</exception>
    Task<HostAgentBackupMount> GetBackupMountAsync(CancellationToken ct = default);

    Task<HostAgentBackupMountTest> TestBackupMountAsync(HostAgentBackupMountRequest request, CancellationToken ct = default);

    Task<HostAgentBackupMount> SetBackupMountAsync(HostAgentBackupMountRequest request, CancellationToken ct = default);

    Task<HostAgentBackupMount> RemoveBackupMountAsync(CancellationToken ct = default);
}
