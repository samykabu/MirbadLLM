using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.HostServices;

/// <summary>
/// A hostd call got no answer in time (the HTTP timeout fired). The action may or may not have run on the host, so a
/// host-service operation ends <see cref="OperationState.Unknown"/> (design §4.4 item 8).
/// </summary>
public sealed class HostdTimeoutException : HostServiceException
{
    /// <summary>Creates the exception.</summary>
    public HostdTimeoutException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    public HostdTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the transport failure.</summary>
    public HostdTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// <see cref="IHostdClient"/> over today's <see cref="HostAgentClient"/> (moved to NodeRuntime by MN-002): the same Unix
/// socket calls, mapped to the contract DTOs. hostd is not changed. A timed-out call surfaces as
/// <see cref="HostdTimeoutException"/>; every other failure keeps the <see cref="HostServiceException"/> type
/// <see cref="HostAgentClient"/> throws (unavailable, denied, confirmation required, not found, conflict).
/// </summary>
public sealed class HostdClient(HostAgentClient client, Func<bool> isInstalled) : IHostdClient
{
    /// <inheritdoc/>
    public bool IsInstalled => isInstalled();

    /// <inheritdoc/>
    public Task<IReadOnlyList<HostUnit>> ListAsync(CancellationToken ct = default) =>
        CallAsync<IReadOnlyList<HostUnit>>(async () => [.. (await client.ListAsync(ct).ConfigureAwait(false)).Select(Map)]);

    /// <inheritdoc/>
    public Task<HostUnitDiscovery> DiscoverAsync(CancellationToken ct = default) =>
        CallAsync(async () =>
        {
            var d = await client.DiscoverAsync(ct).ConfigureAwait(false);
            return new HostUnitDiscovery
            {
                Services = [.. d.Services.Select(Map)],
                Discovered = [.. d.Discovered.Select(Map)],
                DiscoveryErrors = d.DiscoveryErrors,
                DenyPatterns = d.DenyPatterns,
            };
        });

    /// <inheritdoc/>
    public Task<HostUnit> AddToWhitelistAsync(string unit, string scope, string? displayName, bool isProtected, string? confirm, CancellationToken ct = default) =>
        CallAsync(async () => Map(await client.AddToWhitelistAsync(unit, scope, displayName, isProtected, confirm, ct).ConfigureAwait(false)));

    /// <inheritdoc/>
    public Task<HostUnit> UpdateWhitelistAsync(string name, string? displayName, bool? isProtected, string? confirm, CancellationToken ct = default) =>
        CallAsync(async () => Map(await client.UpdateWhitelistAsync(name, displayName, isProtected, confirm, ct).ConfigureAwait(false)));

    /// <inheritdoc/>
    public Task RemoveFromWhitelistAsync(string name, CancellationToken ct = default) =>
        CallAsync(async () =>
        {
            await client.RemoveFromWhitelistAsync(name, ct).ConfigureAwait(false);
            return true;
        });

    /// <inheritdoc/>
    public Task<HostUnit> RunAsync(string name, HostServiceAction action, string? confirm, CancellationToken ct = default) =>
        CallAsync(async () => Map(await client.RunAsync(name, action, confirm, ct).ConfigureAwait(false)));

    /// <inheritdoc/>
    public Task<HostUnit> SetAutostartAsync(string name, bool enabled, string? confirm, CancellationToken ct = default) =>
        CallAsync(async () => Map(await client.SetAutostartAsync(name, enabled, confirm, ct).ConfigureAwait(false)));

    /// <inheritdoc/>
    public Task<IReadOnlyList<GpuProcess>?> GetGpuProcessesAsync(CancellationToken ct = default) => client.GetGpuProcessesAsync(ct);

    /// <inheritdoc/>
    public Task<BackupMountInfo> GetBackupMountAsync(CancellationToken ct = default) =>
        CallAsync(async () => Map(await client.GetBackupMountAsync(ct).ConfigureAwait(false)));

    /// <inheritdoc/>
    public Task<BackupMountTestResult> TestBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default) =>
        CallAsync(async () =>
        {
            var result = await client.TestBackupMountAsync(Map(settings), ct).ConfigureAwait(false);
            return new BackupMountTestResult(result.Ok, result.Message);
        });

    /// <inheritdoc/>
    public Task<BackupMountInfo> SetBackupMountAsync(BackupMountSettings settings, CancellationToken ct = default) =>
        CallAsync(async () => Map(await client.SetBackupMountAsync(Map(settings), ct).ConfigureAwait(false)));

    /// <inheritdoc/>
    public Task<BackupMountInfo> RemoveBackupMountAsync(CancellationToken ct = default) =>
        CallAsync(async () => Map(await client.RemoveBackupMountAsync(ct).ConfigureAwait(false)));

    /// <summary>
    /// True when <paramref name="ex"/> (or the transport failure it wraps) is an HTTP timeout. <see cref="HostAgentClient"/>
    /// only wraps a <see cref="TaskCanceledException"/> when the caller's token was not cancelled, so inside the wrapper it
    /// always means the HTTP timeout fired.
    /// </summary>
    internal static bool IsTimeout(Exception ex) =>
        ex is HostdTimeoutException or TimeoutException or TaskCanceledException
        || (ex is HostAgentUnavailableException { InnerException: { } inner } && IsTimeout(inner));

    private static async Task<T> CallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (HostAgentUnavailableException ex) when (ex.InnerException is { } inner && IsTimeout(inner))
        {
            throw new HostdTimeoutException("mirbadllm-hostd did not answer in time; the outcome is unknown.", ex);
        }
    }

    internal static HostUnit Map(HostAgentUnit u) => new()
    {
        Name = u.Name,
        Unit = u.Unit,
        Scope = u.Scope,
        User = u.User,
        DisplayName = u.DisplayName ?? "",
        Description = u.Description ?? "",
        Protected = u.Protected,
        Risk = u.Risk,
        ActiveState = u.ActiveState ?? "",
        SubState = u.SubState ?? "",
        UnitFileState = u.UnitFileState ?? "",
        MainPid = u.MainPid,
        ActiveSinceUnix = u.ActiveSinceUnix,
        MemoryBytes = u.MemoryBytes,
        GpuMemoryBytes = u.GpuMemoryBytes,
        Error = u.Error,
    };

    private static HostDiscoveredUnit Map(HostAgentDiscoveredUnit u) => new()
    {
        Name = u.Name,
        Unit = u.Unit,
        Scope = u.Scope,
        User = u.User,
        Description = u.Description ?? "",
        ActiveState = u.ActiveState ?? "",
        SubState = u.SubState ?? "",
        UnitFileState = u.UnitFileState ?? "",
        ActiveSinceUnix = u.ActiveSinceUnix,
        MemoryBytes = u.MemoryBytes,
        GpuMemoryBytes = u.GpuMemoryBytes,
        Managed = u.Managed,
        Denied = u.Denied,
        DeniedBy = u.DeniedBy,
    };

    private static BackupMountInfo Map(HostAgentBackupMount m) => new()
    {
        Target = m.Target,
        Unit = m.Unit,
        Configured = m.Configured,
        ForeignUnit = m.ForeignUnit,
        Type = m.Type,
        Server = m.Server,
        Share = m.Share,
        Subfolder = m.Subfolder,
        Username = m.Username,
        Domain = m.Domain,
        Version = m.Version,
        Source = m.Source,
        Options = m.Options,
        Mounted = m.Mounted,
        MountedSource = m.MountedSource,
        MountedType = m.MountedType,
        TotalBytes = m.TotalBytes,
        FreeBytes = m.FreeBytes,
        ConfiguredAt = m.ConfiguredAt,
        LastError = m.LastError,
        Warning = m.Warning,
    };

    private static HostAgentBackupMountRequest Map(BackupMountSettings s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new HostAgentBackupMountRequest
        {
            Type = s.Type,
            Server = s.Server,
            Share = s.Share,
            Subfolder = s.Subfolder,
            Username = s.Username,
            Password = s.Password,
            Domain = s.Domain,
            SmbVersion = s.SmbVersion,
            NfsVersion = s.NfsVersion,
        };
    }
}
