using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>Where scheduled backups go.</summary>
public enum BackupDestinationKind
{
    /// <summary>The DGX's own disk (<c>/srv/mirbadllm/backups</c>, mounted at <c>/backups</c>).</summary>
    Local,

    Smb,

    Nfs,
}

/// <summary>
/// A destination as the page submits it, and as a backup's encrypted secrets carry it. A class, not a record, so the
/// share password can never reach a log line through a generated <c>ToString()</c>. The host agent keeps the password
/// root-only; the master keeps it only Data-Protection-encrypted (<see cref="BackupDestinationSecretStore"/>) so backups
/// with a passphrase can carry it.
/// </summary>
public sealed class BackupDestinationRequest
{
    public override string ToString() =>
        $"BackupDestinationRequest {{ Kind = {Kind}, Server = {Server}, Share = {Share}, Password = {(Password is null ? "null" : "***")} }}";

    public BackupDestinationKind Kind { get; set; } = BackupDestinationKind.Smb;

    public string Server { get; set; } = "";

    /// <summary>SMB: the share name. NFS: the export path.</summary>
    public string Share { get; set; } = "";

    public string? Subfolder { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public string? Domain { get; set; }

    public string SmbVersion { get; set; } = "3.0";

    public string NfsVersion { get; set; } = "4.1";
}

/// <summary>
/// A configured network destination without any secret: what the page shows, and what a backup file records so a
/// restore can say where backups were going (the password must be entered again to reconnect).
/// </summary>
public sealed record BackupDestinationInfo(
    string Type,
    string Server,
    string Share,
    string? Subfolder,
    string? Username,
    string? Domain,
    string Version,
    string Source);

/// <summary>The effective backup destination: what the host agent configured and what the controller actually sees.</summary>
public sealed record BackupDestinationStatus(
    bool AgentAvailable,
    string? AgentProblem,
    BackupDestinationKind Kind,
    BackupDestinationInfo? Configured,
    string HostPath,
    bool HostMounted,
    long? TotalBytes,
    long? FreeBytes,
    BackupDirectoryMount? ContainerMount,
    string? LastError,
    string? Warning,
    bool ForeignUnit)
{
    /// <summary>The backup directory inside the controller is the configured share (it propagated into the container).</summary>
    public bool ContainerSeesShare => ContainerMount?.IsNetworkShare == true;

    /// <summary>Backups are meant to go to a share, but the controller's directory is not that share right now.</summary>
    public bool ShareMissing => Kind != BackupDestinationKind.Local && !ContainerSeesShare;

    /// <summary>One line for the page, e.g. "//192.0.2.200/backups (mounted, 1.2 TB free)".</summary>
    public string Describe()
    {
        if (Kind == BackupDestinationKind.Local || Configured is null)
        {
            return ContainerSeesShare
                ? $"{ContainerMount!.Source} (a network share mounted outside mirbadllm)"
                : $"This DGX's disk ({HostPath})";
        }

        if (ContainerSeesShare)
        {
            return FreeBytes is { } free
                ? string.Create(CultureInfo.InvariantCulture, $"{Configured.Source} (mounted, {FormatBytes(free)} free of {FormatBytes(TotalBytes ?? 0)})")
                : $"{Configured.Source} (mounted)";
        }

        return HostMounted
            ? $"{Configured.Source} (mounted on the DGX, not yet visible to mirbadllm)"
            : $"{Configured.Source} (not mounted)";
    }

    /// <summary>Decimal units like the NAS's own UI: 1.2 TB, 980 GB.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}

/// <summary>A connection test's outcome, shown inline on the page.</summary>
public sealed record BackupDestinationTestResult(bool Ok, string Message);

/// <summary>Changing the destination failed (refused by the agent, the test failed, or the agent is missing).</summary>
public sealed class BackupDestinationException : Exception
{
    public BackupDestinationException()
    {
    }

    public BackupDestinationException(string message)
        : base(message)
    {
    }

    public BackupDestinationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The Backup page's destination panel and the backup guards, over the host agent's backup-mount endpoints.</summary>
public interface IBackupDestinationManager
{
    /// <summary>Never throws for a missing or failing agent: <see cref="BackupDestinationStatus.AgentAvailable"/> says so.</summary>
    Task<BackupDestinationStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Mounts the share privately on the host, writes a test file as the controller's user, unmounts. Changes nothing.</summary>
    Task<BackupDestinationTestResult> TestAsync(BackupDestinationRequest request, CancellationToken ct = default);

    /// <summary>Checks the signed-in session, then has the agent test and mount the share at the backup directory.</summary>
    /// <exception cref="AdminReauthenticationException">Admin login is disabled or no authenticated portal session was supplied.</exception>
    /// <exception cref="BackupDestinationException">The agent refused or the switch failed; the old destination stays.</exception>
    Task<BackupDestinationStatus> ApplyAsync(BackupDestinationRequest request, System.Security.Claims.ClaimsPrincipal? user, string clientKey, CancellationToken ct = default);

    /// <summary>Checks the signed-in session, then unmounts the share: backups go to the local disk again.</summary>
    Task<BackupDestinationStatus> DisconnectAsync(System.Security.Claims.ClaimsPrincipal? user, string clientKey, CancellationToken ct = default);

    /// <summary>The configured network destination for the backup document (no secrets), or null for the local disk.</summary>
    Task<BackupDestinationInfo?> DescribeForBackupAsync(CancellationToken ct = default);

    /// <summary>
    /// Null when a scheduled backup may be written now; otherwise why not — a share is configured but the backup
    /// directory is not that share, so the file would silently land on the local disk underneath.
    /// </summary>
    Task<string?> CheckBeforeBackupAsync(CancellationToken ct = default);

    /// <summary>
    /// The configured network destination with every setting needed to apply it again, for a backup's encrypted secrets
    /// only: the SMB password when the master kept it from the last save (null otherwise). Null for the local disk.
    /// </summary>
    Task<BackupDestinationRequest?> ExportForBackupAsync(CancellationToken ct = default) => Task.FromResult<BackupDestinationRequest?>(null);
}

/// <summary>
/// <see cref="IBackupDestinationManager"/> over the host agent (<see cref="IHostAgentBackupMountClient"/>). Admin
/// session checks happen here, below the UI. The share password is never logged or echoed back; after a successful
/// apply it is kept Data-Protection-encrypted in <paramref name="saved"/> so a backup with a passphrase can carry it in its
/// encrypted secrets (the owner's decision; a restore can then reconnect the share without retyping it). The last known
/// configuration is cached (without secrets) so the backup guard still knows a share was configured when the agent stops
/// answering.
/// </summary>
/// <param name="saved">Where the applied settings are kept for backups; null = never kept (the password then never reaches a backup).</param>
public sealed partial class BackupDestinationManager(
    IHostAgentBackupMountClient? client,
    BackupDirectory directory,
    ILogger<BackupDestinationManager> logger,
    IAdminReauthenticator? reauth = null,
    BackupDestinationSecretStore? saved = null) : IBackupDestinationManager
{
    public const string HostPath = "/srv/mirbadllm/backups";

    public const string AgentMissingMessage =
        "The host agent isn't installed, so backups stay on this DGX's disk. Install it on the DGX: sudo bash deploy/hostd/install-hostd.sh";

    public const string AgentOutdatedMessage =
        "The host agent on the DGX is older than this feature. Reinstall it (sudo bash deploy/hostd/install-hostd.sh); CI does not update it.";

    public const string LoginDisabledMessage =
        "Changing the backup destination needs admin login. Set MirbadLLM__AdminPassword (and restart mirbadllm) first.";

    private volatile BackupDestinationInfo? _lastKnown;

    public async Task<BackupDestinationStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var containerMount = directory.CurrentMount();
        if (client is null)
        {
            return Unavailable(AgentMissingMessage, containerMount);
        }

        try
        {
            var mount = await client.GetBackupMountAsync(ct).ConfigureAwait(false);
            return ToStatus(mount, containerMount);
        }
        catch (HostAgentUnavailableException ex)
        {
            return Unavailable(ex.Message.Contains("not installed", StringComparison.Ordinal) ? AgentMissingMessage : $"The host agent did not answer: {ex.Message}", containerMount);
        }
        catch (HostServiceNotFoundException)
        {
            return Unavailable(AgentOutdatedMessage, containerMount);
        }
        catch (HostServiceException ex)
        {
            return Unavailable($"The host agent could not report the backup mount: {ex.Message}", containerMount);
        }
    }

    public async Task<BackupDestinationTestResult> TestAsync(BackupDestinationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = ToAgentRequest(request);
        if (body is null)
        {
            return new BackupDestinationTestResult(true, $"Backups are written to {HostPath} on this DGX's disk; there is nothing to test.");
        }

        try
        {
            var result = await RequireClient().TestBackupMountAsync(body, ct).ConfigureAwait(false);
            LogTested(logger, body.Type, body.Server, result.Ok);
            return new BackupDestinationTestResult(result.Ok, result.Message ?? (result.Ok ? "Connected." : "The test failed."));
        }
        catch (Exception ex) when (ex is HostServiceException)
        {
            return new BackupDestinationTestResult(false, Explain(ex));
        }
    }

    public async Task<BackupDestinationStatus> ApplyAsync(BackupDestinationRequest request, System.Security.Claims.ClaimsPrincipal? user, string clientKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireAdmin(user, clientKey, "apply");
        var body = ToAgentRequest(request);
        if (body is null)
        {
            return await DisconnectVerifiedAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var mount = await RequireClient().SetBackupMountAsync(body, ct).ConfigureAwait(false);
            LogApplied(logger, body.Type, body.Server, body.Share);
            Keep(() => saved?.Save(request));
            return ToStatus(mount, directory.CurrentMount());
        }
        catch (Exception ex) when (ex is HostServiceException)
        {
            LogRefused(logger, "apply", Explain(ex));
            throw new BackupDestinationException(Explain(ex), ex);
        }
    }

    public async Task<BackupDestinationStatus> DisconnectAsync(System.Security.Claims.ClaimsPrincipal? user, string clientKey, CancellationToken ct = default)
    {
        RequireAdmin(user, clientKey, "disconnect");
        return await DisconnectVerifiedAsync(ct).ConfigureAwait(false);
    }

    public async Task<BackupDestinationInfo?> DescribeForBackupAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        return status.AgentAvailable ? status.Configured : _lastKnown;
    }

    public async Task<BackupDestinationRequest?> ExportForBackupAsync(CancellationToken ct = default)
    {
        if (await DescribeForBackupAsync(ct).ConfigureAwait(false) is not { } info)
        {
            return null;
        }

        // The configured share decides; the kept settings only add the password, and only when they describe that share.
        var kept = saved?.Load();
        bool same = kept is not null && kept.Kind.ToString().Equals(info.Type, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(kept.Server.Trim(), info.Server, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(kept.Share.Trim(), info.Share, StringComparison.Ordinal);
        return FromInfo(info, same ? kept!.Password : null);
    }

    /// <summary>A request that applies <paramref name="info"/> again (NFS needs nothing more; SMB needs <paramref name="password"/>).</summary>
    public static BackupDestinationRequest FromInfo(BackupDestinationInfo info, string? password)
    {
        ArgumentNullException.ThrowIfNull(info);
        bool nfs = info.Type == "nfs";
        var request = new BackupDestinationRequest
        {
            Kind = nfs ? BackupDestinationKind.Nfs : BackupDestinationKind.Smb,
            Server = info.Server,
            Share = info.Share,
            Subfolder = info.Subfolder,
            Username = info.Username,
            Domain = info.Domain,
            Password = nfs ? null : password,
        };
        if (info.Version.Length > 0)
        {
            if (nfs)
            {
                request.NfsVersion = info.Version;
            }
            else
            {
                request.SmbVersion = info.Version;
            }
        }

        return request;
    }

    public async Task<string?> CheckBeforeBackupAsync(CancellationToken ct = default)
    {
        var status = await GetStatusAsync(ct).ConfigureAwait(false);
        var configured = status.AgentAvailable ? status.Configured : _lastKnown;
        if (configured is null || status.ContainerSeesShare)
        {
            return null;
        }

        string why = status.AgentAvailable
            ? status.HostMounted
                ? "it is mounted on the DGX but not visible inside mirbadllm (restart the controller with this version's deploy/compose.yaml)"
                : "it is not mounted on the DGX (is the NAS reachable?)"
            : "the host agent does not answer, and the backup directory is not a network share";
        return $"Backups are set to go to {configured.Source}, but {why}. The backup was not written, so it does not land on the DGX's local disk by mistake.";
    }

    private async Task<BackupDestinationStatus> DisconnectVerifiedAsync(CancellationToken ct)
    {
        try
        {
            var mount = await RequireClient().RemoveBackupMountAsync(ct).ConfigureAwait(false);
            LogDisconnected(logger);
            Keep(() => saved?.Clear());
            return ToStatus(mount, directory.CurrentMount());
        }
        catch (Exception ex) when (ex is HostServiceException)
        {
            LogRefused(logger, "disconnect", Explain(ex));
            throw new BackupDestinationException(Explain(ex), ex);
        }
    }

    /// <summary>The share is already switched; failing to keep its settings for backups costs a log line, not the change.</summary>
    private void Keep(Action change)
    {
        try
        {
            change();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            LogRefused(logger, "keeping the settings for backups", ex.Message);
        }
    }

    private IHostAgentBackupMountClient RequireClient() =>
        client ?? throw new HostAgentUnavailableException("The host agent is not installed (no socket).");

    private void RequireAdmin(System.Security.Claims.ClaimsPrincipal? user, string clientKey, string change)
    {
        if (reauth is null || !reauth.LoginEnabled)
        {
            throw new AdminReauthenticationException(AdminReauthOutcome.LoginDisabled, LoginDisabledMessage);
        }

        var outcome = reauth.Verify(user);
        if (outcome == AdminReauthOutcome.Verified)
        {
            return;
        }

        LogReauthRefused(logger, change, outcome.ToString());
        throw outcome switch
        {
            AdminReauthOutcome.RateLimited => new AdminReauthenticationException(outcome, "Too many password attempts. Try again in a minute."),
            AdminReauthOutcome.LoginDisabled => new AdminReauthenticationException(outcome, LoginDisabledMessage),
            _ => new AdminReauthenticationException(outcome, "Sign in to the portal to perform this action. Nothing was changed."),
        };
    }

    private static HostAgentBackupMountRequest? ToAgentRequest(BackupDestinationRequest request) => request.Kind switch
    {
        BackupDestinationKind.Smb => new HostAgentBackupMountRequest
        {
            Type = "smb",
            Server = request.Server.Trim(),
            Share = request.Share.Trim(),
            Subfolder = Blank(request.Subfolder),
            Username = Blank(request.Username),
            Password = string.IsNullOrEmpty(request.Password) ? null : request.Password,
            Domain = Blank(request.Domain),
            SmbVersion = request.SmbVersion,
        },
        BackupDestinationKind.Nfs => new HostAgentBackupMountRequest
        {
            Type = "nfs",
            Server = request.Server.Trim(),
            Share = request.Share.Trim(),
            Subfolder = Blank(request.Subfolder),
            NfsVersion = request.NfsVersion,
        },
        _ => null,
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Explain(Exception ex) => ex switch
    {
        HostAgentUnavailableException u => u.Message.Contains("not installed", StringComparison.Ordinal) ? AgentMissingMessage : $"The host agent did not answer: {u.Message}",
        HostServiceNotFoundException => AgentOutdatedMessage,
        _ => ex.Message,
    };

    private BackupDestinationStatus ToStatus(HostAgentBackupMount mount, BackupDirectoryMount? containerMount)
    {
        BackupDestinationInfo? info = null;
        var kind = BackupDestinationKind.Local;
        if (mount.Configured && mount.Type is "smb" or "nfs" && mount.Server is not null && mount.Share is not null)
        {
            kind = mount.Type == "smb" ? BackupDestinationKind.Smb : BackupDestinationKind.Nfs;
            info = new BackupDestinationInfo(mount.Type, mount.Server, mount.Share, mount.Subfolder, mount.Username, mount.Domain,
                mount.Version ?? "", mount.Source ?? $"{mount.Server}/{mount.Share}");
        }

        _lastKnown = info;
        return new BackupDestinationStatus(true, null, kind, info, mount.Target, mount.Mounted, mount.TotalBytes, mount.FreeBytes,
            containerMount, mount.LastError, mount.Warning, mount.ForeignUnit);
    }

    private BackupDestinationStatus Unavailable(string problem, BackupDirectoryMount? containerMount)
    {
        var last = _lastKnown;
        return new BackupDestinationStatus(false, problem,
            last is null ? BackupDestinationKind.Local : last.Type == "smb" ? BackupDestinationKind.Smb : BackupDestinationKind.Nfs,
            last, HostPath, false, null, null, containerMount, null, null, false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup destination: tested {Type} share on {Server}: {Ok}")]
    private static partial void LogTested(ILogger logger, string type, string server, bool ok);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup destination: {Type} share {Share} on {Server} mounted at the backup directory")]
    private static partial void LogApplied(ILogger logger, string type, string server, string share);

    [LoggerMessage(Level = LogLevel.Information, Message = "Backup destination: back to the local disk")]
    private static partial void LogDisconnected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup destination: {Change} failed: {Reason}")]
    private static partial void LogRefused(ILogger logger, string change, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Backup destination: {Change} refused, admin re-authentication {Outcome}")]
    private static partial void LogReauthRefused(ILogger logger, string change, string outcome);
}

/// <summary>
/// The last applied network destination on the master, <c>{DataPath}/backup-destination.json</c>, with the SMB password
/// encrypted by ASP.NET Data Protection (as <see cref="JsonTokenStore"/> keeps saved tokens): a copy of the file alone does
/// not reveal it. It exists so a backup with a passphrase can carry the password in its encrypted secrets; it is never shown,
/// logged or sent anywhere else. Best effort: a file that cannot be read or decrypted counts as none.
/// </summary>
public sealed class BackupDestinationSecretStore(string dataPath, IDataProtectionProvider protection)
{
    public const string FileName = "backup-destination.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IDataProtector _protector = protection.CreateProtector(LegacyProtectionPurposes.BackupDestination);
    private readonly string _path = Path.Combine(dataPath, FileName);
    private readonly Lock _gate = new();

    /// <summary>Keeps <paramref name="request"/>'s settings, the password encrypted. A local destination clears the file.</summary>
    public void Save(BackupDestinationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == BackupDestinationKind.Local)
        {
            Clear();
            return;
        }

        var stored = new Stored(request.Kind, request.Server.Trim(), request.Share.Trim(), request.Subfolder, request.Username,
            request.Domain, request.SmbVersion, request.NfsVersion,
            string.IsNullOrEmpty(request.Password) ? null : _protector.Protect(request.Password));
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json));
            File.Move(temp, _path, overwrite: true);
        }
    }

    /// <summary>The kept settings with the password decrypted, or null.</summary>
    public BackupDestinationRequest? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path) || JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path), Json) is not { } s)
                {
                    return null;
                }

                return new BackupDestinationRequest
                {
                    Kind = s.Kind,
                    Server = s.Server,
                    Share = s.Share,
                    Subfolder = s.Subfolder,
                    Username = s.Username,
                    Domain = s.Domain,
                    SmbVersion = s.SmbVersion,
                    NfsVersion = s.NfsVersion,
                    Password = s.ProtectedPassword is null ? null : _protector.Unprotect(s.ProtectedPassword),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
            {
                return null; // the Data Protection keys changed, or the file is damaged: save the destination again
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            File.Delete(_path);
        }
    }

    private sealed record Stored(
        BackupDestinationKind Kind, string Server, string Share, string? Subfolder, string? Username, string? Domain,
        string SmbVersion, string NfsVersion, string? ProtectedPassword);
}
