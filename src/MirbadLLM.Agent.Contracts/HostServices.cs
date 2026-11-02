namespace MirbadLLM.Agent.Contracts;

/// <summary>Query of <c>GET /agent/v1/host/services</c> (design §4.3).</summary>
public sealed record HostServicesQuery
{
    /// <summary><c>all</c> (<see cref="IncludeAll"/>) adds discovery (every unit and non-mirbadllm container); null = managed only.</summary>
    public string? Include { get; init; }

    /// <summary>The <see cref="Include"/> value for discovery.</summary>
    public const string IncludeAll = "all";
}

/// <summary>
/// <c>GET /agent/v1/host/services</c> (design §4.3, §9.1): hostd's units (its shape, forwarded) and the node's containers.
/// The master keeps admin re-authentication, protected-service confirms and the container whitelist; hostd keeps the
/// unit whitelist and deny list.
/// </summary>
public sealed record HostServicesResponse
{
    /// <summary>True when hostd answered.</summary>
    public bool HostdAvailable { get; init; }

    /// <summary>Why hostd is unavailable. Null when available.</summary>
    public string? HostdError { get; init; }

    /// <summary>Whitelisted units (hostd <c>services</c>).</summary>
    public IReadOnlyList<HostUnit> Units { get; init; } = [];

    /// <summary>Every unit hostd discovered (<c>include=all</c> only).</summary>
    public IReadOnlyList<HostDiscoveredUnit>? Discovered { get; init; }

    /// <summary>hostd discovery errors (<c>include=all</c> only).</summary>
    public IReadOnlyList<string>? DiscoveryErrors { get; init; }

    /// <summary>hostd deny-list patterns (<c>include=all</c> only).</summary>
    public IReadOnlyList<string>? DenyPatterns { get; init; }

    /// <summary>True when Docker answered.</summary>
    public bool ContainersAvailable { get; init; }

    /// <summary>Why containers are unavailable. Null when available.</summary>
    public string? ContainersError { get; init; }

    /// <summary>
    /// Containers on the node except the agent's own, the master's, model (<c>ss-*</c>) and download containers. The master
    /// filters by its whitelist (or shows all with <c>include=all</c>).
    /// </summary>
    public IReadOnlyList<HostContainerInfo> Containers { get; init; } = [];
}

/// <summary>One whitelisted unit as mirbadllm-hostd reports it (<c>GET /services</c>), forwarded unchanged (design §4.3).</summary>
public sealed record HostUnit
{
    /// <summary>Whitelist name.</summary>
    public required string Name { get; init; }

    /// <summary>systemd unit.</summary>
    public required string Unit { get; init; }

    /// <summary><c>system</c> or <c>user</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>User for user units.</summary>
    public string? User { get; init; }

    /// <summary>Display name.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Description.</summary>
    public string Description { get; init; } = "";

    /// <summary>Protected (needs typed confirmation to stop, restart or lose autostart).</summary>
    public bool Protected { get; init; }

    /// <summary>What goes wrong when it stops.</summary>
    public string? Risk { get; init; }

    /// <summary>systemd ActiveState.</summary>
    public string ActiveState { get; init; } = "";

    /// <summary>systemd SubState.</summary>
    public string SubState { get; init; } = "";

    /// <summary>systemd UnitFileState.</summary>
    public string UnitFileState { get; init; } = "";

    /// <summary>Main PID.</summary>
    public int? MainPid { get; init; }

    /// <summary>Active since, Unix seconds.</summary>
    public long? ActiveSinceUnix { get; init; }

    /// <summary>Memory in bytes.</summary>
    public long? MemoryBytes { get; init; }

    /// <summary>GPU memory in bytes.</summary>
    public long? GpuMemoryBytes { get; init; }

    /// <summary>hostd's error for this unit.</summary>
    public string? Error { get; init; }
}

/// <summary>One discovered unit (hostd <c>GET /services?include=all</c> <c>discovered</c>), forwarded unchanged.</summary>
public sealed record HostDiscoveredUnit
{
    /// <summary>Name it has (or would get) in the whitelist.</summary>
    public required string Name { get; init; }

    /// <summary>systemd unit.</summary>
    public required string Unit { get; init; }

    /// <summary><c>system</c> or <c>user</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>User for user units.</summary>
    public string? User { get; init; }

    /// <summary>Description.</summary>
    public string Description { get; init; } = "";

    /// <summary>systemd ActiveState.</summary>
    public string ActiveState { get; init; } = "";

    /// <summary>systemd SubState.</summary>
    public string SubState { get; init; } = "";

    /// <summary>systemd UnitFileState.</summary>
    public string UnitFileState { get; init; } = "";

    /// <summary>Active since, Unix seconds.</summary>
    public long? ActiveSinceUnix { get; init; }

    /// <summary>Memory in bytes.</summary>
    public long? MemoryBytes { get; init; }

    /// <summary>GPU memory in bytes.</summary>
    public long? GpuMemoryBytes { get; init; }

    /// <summary>Already whitelisted.</summary>
    public bool Managed { get; init; }

    /// <summary>On hostd's deny list.</summary>
    public bool Denied { get; init; }

    /// <summary>The deny pattern that matched.</summary>
    public string? DeniedBy { get; init; }
}

/// <summary>
/// hostd discovery (<c>GET /services?include=all</c>) as the agent's hostd client returns it (<c>MirbadLLM.Agent.Abstractions.IHostdClient</c>).
/// </summary>
public sealed record HostUnitDiscovery
{
    /// <summary>Whitelisted units.</summary>
    public IReadOnlyList<HostUnit> Services { get; init; } = [];

    /// <summary>Every discovered unit.</summary>
    public IReadOnlyList<HostDiscoveredUnit> Discovered { get; init; } = [];

    /// <summary>Discovery errors.</summary>
    public IReadOnlyList<string> DiscoveryErrors { get; init; } = [];

    /// <summary>Deny-list patterns.</summary>
    public IReadOnlyList<string> DenyPatterns { get; init; } = [];
}

/// <summary>
/// A non-model container on the node (design §4.3 host containers): Docker's list row, inspect details and current memory,
/// so the master builds the Services page row without another call.
/// </summary>
public sealed record HostContainerInfo
{
    /// <summary>Full container id.</summary>
    public required string Id { get; init; }

    /// <summary>Name without the leading <c>/</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Docker state (<c>running</c>, <c>exited</c>, …).</summary>
    public required string State { get; init; }

    /// <summary>Docker status text.</summary>
    public string Status { get; init; } = "";

    /// <summary>Image reference.</summary>
    public string Image { get; init; } = "";

    /// <summary>Labels.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; init; } = new Dictionary<string, string>();

    /// <summary>Restart policy name (<c>no</c>, <c>always</c>, <c>unless-stopped</c>, <c>on-failure</c>).</summary>
    public string? RestartPolicy { get; init; }

    /// <summary>Health status when the image defines a health check.</summary>
    public string? Health { get; init; }

    /// <summary>Exit code of the last run.</summary>
    public int? ExitCode { get; init; }

    /// <summary>True when the last run was OOM-killed.</summary>
    public bool OomKilled { get; init; }

    /// <summary>When it last started.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>Current memory usage in bytes (running only).</summary>
    public long? MemoryBytes { get; init; }
}

/// <summary>
/// <c>POST /agent/v1/host/services/{name}/start|stop|restart</c> (design §4.3): synchronous (200), bounded by hostd's 60 s
/// action timeout; hostd re-checks protected/deny. Result: the unit after the action (<see cref="HostUnit"/>).
/// </summary>
public sealed record HostServiceActionRequest : AgentMutation
{
    /// <summary>The unit's name when it is protected and the action stops or restarts it.</summary>
    public string? Confirm { get; init; }
}

/// <summary><c>POST /agent/v1/host/services/{name}/autostart</c> (design §4.3). Result: <see cref="HostUnit"/>.</summary>
public sealed record HostServiceAutostartRequest : AgentMutation
{
    /// <summary>Start at boot.</summary>
    public required bool Enabled { get; init; }

    /// <summary>The unit's name when it is protected and autostart is turned off.</summary>
    public string? Confirm { get; init; }
}

/// <summary><c>POST /agent/v1/host/whitelist</c> (design §4.3): forwarded to hostd with hostd's body. Result: <see cref="HostUnit"/>.</summary>
public sealed record HostWhitelistAddRequest : AgentMutation
{
    /// <summary>systemd unit.</summary>
    public required string Unit { get; init; }

    /// <summary><c>system</c> or <c>user</c>.</summary>
    public required string Scope { get; init; }

    /// <summary>Display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Protected flag.</summary>
    public bool Protected { get; init; }

    /// <summary>Typed name (hostd re-checks it).</summary>
    public string? Confirm { get; init; }
}

/// <summary><c>PATCH /agent/v1/host/whitelist/{name}</c> (design §4.3). Result: <see cref="HostUnit"/>.</summary>
public sealed record HostWhitelistUpdateRequest : AgentMutation
{
    /// <summary>New display name; null = unchanged.</summary>
    public string? DisplayName { get; init; }

    /// <summary>New protected flag; null = unchanged.</summary>
    public bool? Protected { get; init; }

    /// <summary>Typed name (needed to unprotect).</summary>
    public string? Confirm { get; init; }
}

/// <summary><c>DELETE /agent/v1/host/whitelist/{name}</c> (design §4.3): the unit keeps running, it just is not managed any more.</summary>
public sealed record HostWhitelistRemoveRequest : AgentMutation;

/// <summary>
/// <c>POST /agent/v1/host/containers/{name}/start|stop|restart</c> (design §4.3, §10.3): the master enforces its container
/// whitelist; the agent refuses its own container, the master's (<c>mirbadllm</c>), <c>ss-*</c> model containers and
/// download containers (403 <c>denied</c>). Result: <see cref="HostContainerInfo"/>.
/// </summary>
public sealed record HostContainerActionRequest : AgentMutation
{
    /// <summary>Stop/restart timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// <c>POST /agent/v1/host/containers/{name}/autostart</c> (design §4.3): sets the restart policy the master chose (the
/// master remembers the policy to restore). Desired-state action. Result: <see cref="HostContainerInfo"/>.
/// </summary>
public sealed record HostContainerAutostartRequest : AgentMutation
{
    /// <summary><c>no</c>, <c>always</c>, <c>unless-stopped</c> or <c>on-failure</c>.</summary>
    public required string RestartPolicy { get; init; }
}

/// <summary>
/// Backup-share settings (hostd's <c>PUT /backup-mount</c> and <c>POST /backup-mount/test</c> body, design §9.3). A
/// sealed class, not a record, so the password never appears in a generated <c>ToString()</c>.
/// </summary>
public sealed class BackupMountSettings
{
    /// <summary><c>smb</c> or <c>nfs</c>.</summary>
    public string Type { get; set; } = "";

    /// <summary>Server host or IP.</summary>
    public string Server { get; set; } = "";

    /// <summary>Share or export.</summary>
    public string Share { get; set; } = "";

    /// <summary>Folder inside the share.</summary>
    public string? Subfolder { get; set; }

    /// <summary>SMB user.</summary>
    public string? Username { get; set; }

    /// <summary>SMB password. Secret: passes through once to hostd, never stored or digested.</summary>
    public string? Password { get; set; }

    /// <summary>SMB domain.</summary>
    public string? Domain { get; set; }

    /// <summary>SMB protocol version.</summary>
    public string? SmbVersion { get; set; }

    /// <summary>NFS version.</summary>
    public string? NfsVersion { get; set; }
}

/// <summary>
/// <c>PUT /agent/v1/host/backup-mount</c> (design §4.3): forwarded to hostd. Not verifiable after a crash (the password is
/// not stored), so a dispatched-without-result operation ends Unknown. Result: <see cref="BackupMountInfo"/>.
/// </summary>
public sealed record BackupMountSetRequest : AgentMutation
{
    /// <summary>Share settings (password redacted from the stored operation).</summary>
    public required BackupMountSettings Settings { get; init; }

    /// <summary>Never prints the settings (they hold the password).</summary>
    public override string ToString() => $"BackupMountSetRequest {{ OperationId = {OperationId} }}";
}

/// <summary><c>DELETE /agent/v1/host/backup-mount</c> (design §4.3): desired-state action. Result: <see cref="BackupMountInfo"/>.</summary>
public sealed record BackupMountRemoveRequest : AgentMutation;

/// <summary>
/// <c>POST /agent/v1/host/backup-mount/test</c> (design §4.3, §4.4 item 8): read-only check, not an operation (no
/// envelope).
/// </summary>
public sealed record BackupMountTestRequest
{
    /// <summary>Share settings to test.</summary>
    public required BackupMountSettings Settings { get; init; }

    /// <summary>Never prints the settings (they hold the password).</summary>
    public override string ToString() => "BackupMountTestRequest { }";
}

/// <summary>hostd's <c>POST /backup-mount/test</c> answer.</summary>
/// <param name="Ok">True when the share mounted and the controller's user could write to it.</param>
/// <param name="Message">What went wrong (or a warning).</param>
public sealed record BackupMountTestResult(bool Ok, string? Message);

/// <summary>hostd's <c>GET /backup-mount</c> (design §9.3), forwarded unchanged. Never carries the password.</summary>
public sealed record BackupMountInfo
{
    /// <summary>Mount target on the host.</summary>
    public required string Target { get; init; }

    /// <summary>systemd mount unit.</summary>
    public required string Unit { get; init; }

    /// <summary>True when a share is configured.</summary>
    public bool Configured { get; init; }

    /// <summary>True when the mount unit was not written by hostd.</summary>
    public bool ForeignUnit { get; init; }

    /// <summary>Share type.</summary>
    public string? Type { get; init; }

    /// <summary>Server.</summary>
    public string? Server { get; init; }

    /// <summary>Share.</summary>
    public string? Share { get; init; }

    /// <summary>Subfolder.</summary>
    public string? Subfolder { get; init; }

    /// <summary>User.</summary>
    public string? Username { get; init; }

    /// <summary>Domain.</summary>
    public string? Domain { get; init; }

    /// <summary>Protocol version.</summary>
    public string? Version { get; init; }

    /// <summary>Mount source.</summary>
    public string? Source { get; init; }

    /// <summary>Mount options.</summary>
    public string? Options { get; init; }

    /// <summary>True when mounted now.</summary>
    public bool Mounted { get; init; }

    /// <summary>Source of the current mount.</summary>
    public string? MountedSource { get; init; }

    /// <summary>Filesystem type of the current mount.</summary>
    public string? MountedType { get; init; }

    /// <summary>Size of the mounted filesystem.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Free bytes on it.</summary>
    public long? FreeBytes { get; init; }

    /// <summary>When it was configured.</summary>
    public DateTimeOffset? ConfiguredAt { get; init; }

    /// <summary>Last mount error.</summary>
    public string? LastError { get; init; }

    /// <summary>Warning for the UI.</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// Values of <c>HostServicesCapability.Manager</c> (design §3.3, §9.1, design-p3p4 §A.4.3) and the frozen mapping of a
/// Windows service onto <see cref="HostUnit"/> / <see cref="HostDiscoveredUnit"/> (MN-202, D116; implemented by MN-228,
/// shown by MN-227), so the Services page needs no Windows-specific shape: <c>Unit</c> = the service key name,
/// <c>Scope</c> = <see cref="ScmScope"/>, <c>User</c> = null, <c>ActiveState</c> from the SCM state (running →
/// <c>active</c>, stopped and paused → <c>inactive</c>, start pending and continue pending → <c>activating</c>, stop and
/// pause pending → <c>deactivating</c>), <c>SubState</c> = the SCM state in snake case (<c>running</c>, <c>stopped</c>,
/// <c>start_pending</c>, …), <c>UnitFileState</c> from the start type (automatic and automatic-delayed → <c>enabled</c>,
/// manual → <c>disabled</c>, disabled → <c>masked</c>), <c>MainPid</c> = the service process id. Feature
/// <see cref="AgentFeatures.ServicesScm"/> enables the same <c>/host/services*</c> and <c>/host/whitelist*</c> routes
/// (<see cref="AgentRoute.AlternativeFeatures"/>).
/// </summary>
public static class HostServiceManagers
{
    /// <summary>systemd through hostd (Linux).</summary>
    public const string Systemd = "systemd";

    /// <summary>The Windows Service Control Manager (P4).</summary>
    public const string Scm = "scm";

    /// <summary>
    /// launchd through the macOS hostd (docs/macos/implementation-plan.md M10): <c>Unit</c> = the job's label, <c>Scope</c>
    /// <c>system</c> (a LaunchDaemon) or <c>user</c> (a LaunchAgent in the user's session), <c>ActiveState</c> active
    /// (running) or inactive (loaded but not running, or not loaded), <c>SubState</c> launchd's own state
    /// (<c>running</c>, <c>not running</c>, <c>not loaded</c>), <c>UnitFileState</c> enabled/disabled from launchd's
    /// override (whether the job loads at boot or login). Feature <see cref="AgentFeatures.ServicesLaunchd"/>.
    /// </summary>
    public const string Launchd = "launchd";

    /// <summary>The only scope of a Windows service.</summary>
    public const string ScmScope = "system";
}
