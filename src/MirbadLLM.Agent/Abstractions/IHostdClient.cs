using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Abstractions;

/// <summary>
/// The agent's view of mirbadllm-hostd (design §2.4, §4.3 host services, §10.3), wrapping today's <c>HostAgentClient</c>
/// calls over the Unix socket (moved to NodeRuntime by MN-002; adapted in MN-023). hostd is not changed.
/// <para>Invariants: hostd owns the unit whitelist, the deny list and typed-name confirms and re-checks them; this client
/// only forwards names. Failures throw the <see cref="HostServiceException"/> family (unavailable socket →
/// <see cref="HostServiceException"/> with hostd's message; deny → <see cref="HostServiceDeniedException"/>; missing
/// confirm → <see cref="HostServiceConfirmationRequiredException"/>; unknown → <see cref="HostServiceNotFoundException"/>).
/// Calls are bounded by hostd's own 60 s action timeout; a timeout is an ambiguous outcome (Unknown, §4.4 item 8). The
/// share password passes through once and is never logged.</para>
/// </summary>
public interface IHostdClient
{
    /// <summary>True when the hostd socket exists.</summary>
    bool IsInstalled { get; }

    /// <summary>Whitelisted units (<c>GET /services</c>).</summary>
    Task<IReadOnlyList<HostUnit>> ListAsync(CancellationToken ct = default);

    /// <summary>Managed and discovered units (<c>GET /services?include=all</c>).</summary>
    Task<HostUnitDiscovery> DiscoverAsync(CancellationToken ct = default);

    /// <summary><c>POST /whitelist</c>.</summary>
    Task<HostUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default);

    /// <summary><c>PATCH /whitelist/{name}</c>.</summary>
    Task<HostUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default);

    /// <summary><c>DELETE /whitelist/{name}</c>.</summary>
    Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default);

    /// <summary><c>POST /services/{name}/{start|stop|restart}</c>.</summary>
    Task<HostUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default);

    /// <summary><c>POST /services/{name}/autostart</c>.</summary>
    Task<HostUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default);

    /// <summary><c>GET /gpu-processes</c>; null when unavailable (no hostd, an older one, no <c>nvidia-smi</c>).</summary>
    Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default);

    /// <summary><c>GET /backup-mount</c>.</summary>
    Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default);

    /// <summary><c>POST /backup-mount/test</c>.</summary>
    Task<BackupMountTestResult> TestBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default);

    /// <summary><c>PUT /backup-mount</c>.</summary>
    Task<BackupMountInfo> SetBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default);

    /// <summary><c>DELETE /backup-mount</c>.</summary>
    Task<BackupMountInfo> RemoveBackupMountAsync(CancellationToken ct = default);
}
