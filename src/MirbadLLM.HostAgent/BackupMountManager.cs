using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MirbadLLM.HostAgent;

/// <summary>What <c>GET /backup-mount</c> returns. Never carries the password.</summary>
internal sealed record BackupMountStatus(
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

internal enum BackupMountOutcome
{
    Ok,
    Invalid,
    Conflict,
    Failed,
}

internal sealed record BackupMountResult(BackupMountOutcome Outcome, string? Message, BackupMountStatus? Status = null);

/// <summary>Non-secret configuration kept in <c>$STATE_DIRECTORY/backup-mount.json</c> (root, 0600).</summary>
internal sealed record BackupMountState(
    string Type,
    string Server,
    string Share,
    string? Subfolder,
    string? Username,
    string? Domain,
    string Version,
    DateTimeOffset ConfiguredAt,
    string? LastError)
{
    public BackupMountSpec? ToSpec() =>
        BackupMountValidator.TryValidate(new BackupMountRequest
        {
            Type = Type,
            Server = Server,
            Share = Share,
            Subfolder = Subfolder,
            Username = Username,
            Password = Type == "smb" ? "x" : null,
            Domain = Domain,
            SmbVersion = Type == "smb" ? Version : null,
            NfsVersion = Type == "nfs" ? Version : null,
        }, out var spec, out _, out _) ? spec : null;
}

/// <summary>
/// Mounts a NAS share at the fixed backup directory (<see cref="BackupMountPaths.Target"/>) through a systemd
/// <c>.mount</c> unit. Every change is tested first: the share is mounted in a private probe directory, a file is
/// written, read back and deleted as the controller's uid, and it is unmounted again — only then are the credentials
/// file and the unit written, and systemd mounts it. A failed test leaves the previous configuration as it was; a
/// failed switch restores the previous files. A unit the agent did not write (one made by hand) is never touched.
/// Commands: <c>/usr/bin/mount</c>, <c>/usr/bin/umount</c>, <c>/usr/bin/systemctl</c> and <c>/usr/bin/stat</c>,
/// always with an argument vector. The password is never logged, never on a command line, never in the state file.
/// </summary>
internal sealed class BackupMountManager(
    IProcessRunner runner,
    IBackupMountHost host,
    BackupMountPaths paths,
    ILogger<BackupMountManager> logger) : IDisposable
{
    public const string MountPath = "/usr/bin/mount";
    public const string UmountPath = "/usr/bin/umount";
    public const string StatPath = "/usr/bin/stat";

    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UmountTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SystemctlTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SystemctlMountTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SpaceTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackupMountPaths Paths => paths;

    public async Task<BackupMountStatus> GetStatusAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await StatusAsync(warning: null).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>POST /backup-mount/test</c>: validation and the probe mount; nothing is changed.</summary>
    public async Task<BackupMountResult> TestAsync(BackupMountRequest? request, CancellationToken ct)
    {
        if (!BackupMountValidator.TryValidate(request, out var spec, out string? password, out string? error))
        {
            return new BackupMountResult(BackupMountOutcome.Invalid, error);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? problem = await ProbeAsync(spec!, password, ct).ConfigureAwait(false);
            string source = BackupMountRendering.Source(spec!);
            Log.BackupMountTested(logger, spec!.TypeName, source, problem is null ? "ok" : "failed", problem ?? "");
            return problem is null
                ? new BackupMountResult(BackupMountOutcome.Ok, $"Connected to {source} and wrote a test file as the controller's user.")
                : new BackupMountResult(BackupMountOutcome.Failed, problem);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>PUT /backup-mount</c>: test, then switch the backup directory to the share.</summary>
    public async Task<BackupMountResult> ApplyAsync(BackupMountRequest? request, CancellationToken ct)
    {
        if (!BackupMountValidator.TryValidate(request, out var validated, out string? password, out string? error))
        {
            return new BackupMountResult(BackupMountOutcome.Invalid, error);
        }

        var spec = validated!;
        string source = BackupMountRendering.Source(spec);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? previousUnit = host.ReadFile(paths.UnitPath);
            if ((previousUnit is not null && !IsManaged(previousUnit)) || host.ReadFile(paths.AutomountPath) is not null)
            {
                return new BackupMountResult(BackupMountOutcome.Conflict, ForeignUnitMessage());
            }

            if (previousUnit is null && MountInfoParser.FindTop(host.ReadMountInfo(), paths.Target) is { } existing && IsNetworkFileSystem(existing.FileSystemType))
            {
                // Mounted by something else (an /etc/fstab line, a hand-made unit elsewhere): a unit here would fight it.
                return new BackupMountResult(BackupMountOutcome.Conflict,
                    $"{existing.Source} is already mounted at {paths.Target} by something other than mirbadllm (an /etc/fstab line?). Remove that mount first.");
            }

            string? problem = await ProbeAsync(spec, password, ct).ConfigureAwait(false);
            if (problem is not null)
            {
                Log.BackupMountRefused(logger, spec.TypeName, source, "test-failed", problem);
                return new BackupMountResult(BackupMountOutcome.Failed, problem);
            }

            string? previousCredentials = host.ReadFile(paths.CredentialsPath);
            try
            {
                if (spec.Type == BackupShareType.Smb)
                {
                    host.WriteFile(paths.CredentialsPath, BackupMountRendering.Credentials(spec.Username!, password!, spec.Domain), UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                else
                {
                    host.DeleteFile(paths.CredentialsPath);
                }

                host.WriteFile(paths.UnitPath, BackupMountRendering.Unit(spec, paths),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Restore(paths.CredentialsPath, previousCredentials, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Restore(paths.UnitPath, previousUnit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                string message = $"Could not write the mount configuration: {ex.Message}. Reinstall the host agent (sudo bash deploy/hostd/install-hostd.sh) so it may write {paths.CredentialsDirectory} and {paths.UnitDirectory}.";
                Log.BackupMountRefused(logger, spec.TypeName, source, "write-failed", ex.Message);
                return new BackupMountResult(BackupMountOutcome.Failed, message);
            }

            string? switchError = await SystemctlAsync(SystemctlTimeout, ["daemon-reload"], ct).ConfigureAwait(false)
                ?? await SystemctlAsync(SystemctlTimeout, ["enable", "--no-ask-password", "--", paths.UnitName], ct).ConfigureAwait(false)
                // restart, not start: when a share was already mounted, the new options only apply after a remount.
                ?? await SystemctlAsync(SystemctlMountTimeout, ["restart", "--no-ask-password", "--", paths.UnitName], ct).ConfigureAwait(false);

            if (switchError is not null)
            {
                Restore(paths.CredentialsPath, previousCredentials, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Restore(paths.UnitPath, previousUnit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                await SystemctlAsync(SystemctlTimeout, ["daemon-reload"], CancellationToken.None).ConfigureAwait(false);
                if (previousUnit is not null)
                {
                    await SystemctlAsync(SystemctlMountTimeout, ["restart", "--no-ask-password", "--", paths.UnitName], CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await SystemctlAsync(SystemctlTimeout, ["disable", "--no-ask-password", "--", paths.UnitName], CancellationToken.None).ConfigureAwait(false);
                }

                string message = $"The test passed but systemd could not mount {source}: {switchError}. The previous destination was put back.";
                SaveLastError(message);
                Log.BackupMountRefused(logger, spec.TypeName, source, "switch-failed", switchError);
                return new BackupMountResult(BackupMountOutcome.Failed, message);
            }

            var state = new BackupMountState(spec.TypeName, spec.Server, spec.Share, spec.Subfolder, spec.Username, spec.Domain, spec.Version, DateTimeOffset.UtcNow, null);
            host.WriteFile(paths.StateFile, JsonSerializer.Serialize(state, JsonOptions), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            string summary = BackupMountRendering.Summary(spec);
            Log.BackupMountApplied(logger, spec.TypeName, source, summary, paths.Target);
            return new BackupMountResult(BackupMountOutcome.Ok, null, await StatusAsync(warning: null).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>DELETE /backup-mount</c>: unmount, remove the unit and the credentials, back to the local disk.</summary>
    public async Task<BackupMountResult> RemoveAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? unit = host.ReadFile(paths.UnitPath);
            if ((unit is not null && !IsManaged(unit)) || host.ReadFile(paths.AutomountPath) is not null)
            {
                return new BackupMountResult(BackupMountOutcome.Conflict, ForeignUnitMessage());
            }

            if (unit is not null)
            {
                string? stopError = await SystemctlAsync(SystemctlMountTimeout, ["disable", "--now", "--no-ask-password", "--", paths.UnitName], ct).ConfigureAwait(false);
                if (stopError is not null)
                {
                    string message = $"Could not unmount {paths.Target}: {stopError}. Nothing was removed; try again when no backup is being written.";
                    Log.BackupMountRefused(logger, "-", paths.Target, "remove-failed", stopError);
                    return new BackupMountResult(BackupMountOutcome.Failed, message);
                }
            }

            host.DeleteFile(paths.UnitPath);
            host.DeleteFile(paths.CredentialsPath);
            host.DeleteFile(paths.StateFile);
            if (unit is not null)
            {
                await SystemctlAsync(SystemctlTimeout, ["daemon-reload"], ct).ConfigureAwait(false);
                await SystemctlAsync(SystemctlTimeout, ["reset-failed", "--", paths.UnitName], ct).ConfigureAwait(false);
            }

            Log.BackupMountRemoved(logger, paths.Target);
            string? warning = await CheckLocalDirectoryAsync(ct).ConfigureAwait(false);
            return new BackupMountResult(BackupMountOutcome.Ok, null, await StatusAsync(warning).ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Mounts the share at a fresh private directory (the agent's own /tmp, invisible to the host and the
    /// controller), proves the controller's uid can write there, and unmounts. Null on success.
    /// </summary>
    private async Task<string?> ProbeAsync(BackupMountSpec spec, string? password, CancellationToken ct)
    {
        string directory;
        try
        {
            directory = host.CreateProbeDirectory(paths.ProbeRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Could not create a test mount point: {ex.Message}";
        }

        string credentials = directory + ".cred";
        bool mounted = false;
        try
        {
            if (spec.Type == BackupShareType.Smb)
            {
                host.WriteFile(credentials, BackupMountRendering.Credentials(spec.Username!, password!, spec.Domain), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            var result = await runner.RunAsync(MountPath, MountArguments(spec, credentials, directory), MountTimeout, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return BackupMountRendering.ExplainMountFailure(spec, result);
            }

            mounted = true;
            return host.ProbeWrite(directory, BackupMountRendering.AppUid, BackupMountRendering.AppGid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The connection test failed: {ex.Message}";
        }
        finally
        {
            if (mounted)
            {
                var umount = await runner.RunAsync(UmountPath, ["-n", "--", directory], UmountTimeout, CancellationToken.None).ConfigureAwait(false);
                if (!umount.Succeeded)
                {
                    await runner.RunAsync(UmountPath, ["-n", "-l", "--", directory], UmountTimeout, CancellationToken.None).ConfigureAwait(false);
                }
            }

            try
            {
                host.DeleteFile(credentials);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Private /tmp; removed with the agent at the latest.
            }

            host.DeleteProbeDirectory(directory);
        }
    }

    /// <summary>The exact <c>mount</c> argument vector of the probe.</summary>
    public static IReadOnlyList<string> MountArguments(BackupMountSpec spec, string credentialsPath, string directory)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return
        [
            "-n",
            "-t", spec.Type == BackupShareType.Smb ? "cifs" : "nfs",
            "-o", BackupMountRendering.Options(spec, credentialsPath, forUnit: false),
            "--",
            BackupMountRendering.Source(spec),
            directory,
        ];
    }

    private async Task<BackupMountStatus> StatusAsync(string? warning)
    {
        var state = ReadState();
        var spec = state?.ToSpec();
        string? unit = host.ReadFile(paths.UnitPath);
        bool foreign = (unit is not null && !IsManaged(unit)) || host.ReadFile(paths.AutomountPath) is not null;
        var mount = MountInfoParser.FindTop(host.ReadMountInfo(), paths.Target);
        bool mounted = mount is not null && IsNetworkFileSystem(mount.FileSystemType);

        long? total = null;
        long? free = null;
        if (mounted)
        {
            // statvfs on a share that just went away can block; never let it hold the status for long.
            var space = Task.Run(() => host.GetSpace(paths.Target));
            if (await Task.WhenAny(space, Task.Delay(SpaceTimeout)).ConfigureAwait(false) == space && space.Result is { } s)
            {
                total = s.Total;
                free = s.Free;
            }
        }

        if (foreign)
        {
            warning ??= ForeignUnitMessage();
        }

        return new BackupMountStatus(
            paths.Target,
            paths.UnitName,
            Configured: spec is not null && unit is not null && !foreign,
            ForeignUnit: foreign,
            state?.Type,
            state?.Server,
            state?.Share,
            state?.Subfolder,
            state?.Username,
            state?.Domain,
            state?.Version,
            spec is null ? null : BackupMountRendering.Source(spec),
            spec is null ? null : BackupMountRendering.Summary(spec),
            mounted,
            mount?.Source,
            mount?.FileSystemType,
            total,
            free,
            state?.ConfiguredAt,
            state?.LastError,
            warning);
    }

    public static bool IsNetworkFileSystem(string fsType) =>
        fsType is "cifs" or "smb3" or "nfs" or "nfs4";

    private async Task<string?> CheckLocalDirectoryAsync(CancellationToken ct)
    {
        var stat = await runner.RunAsync(StatPath, ["-c", "%u:%g:%a", "--", paths.Target], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        string fix = string.Create(CultureInfo.InvariantCulture,
            $"sudo install -d -o {BackupMountRendering.AppUid} -g {BackupMountRendering.AppGid} -m 0750 {paths.Target}");
        if (!stat.Succeeded)
        {
            return $"{paths.Target} does not exist on the DGX any more, so scheduled backups have nowhere to go. Create it: {fix}";
        }

        string expected = string.Create(CultureInfo.InvariantCulture, $"{BackupMountRendering.AppUid}:{BackupMountRendering.AppGid}:");
        return stat.StdOut.Trim().StartsWith(expected, StringComparison.Ordinal)
            ? null
            : $"{paths.Target} on the DGX is owned by {stat.StdOut.Trim()} (uid:gid:mode), so mirbadllm may not be able to write to it. Fix it: {fix}";
    }

    private BackupMountState? ReadState()
    {
        try
        {
            string? json = host.ReadFile(paths.StateFile);
            return json is null ? null : JsonSerializer.Deserialize<BackupMountState>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void SaveLastError(string message)
    {
        if (ReadState() is { } state)
        {
            try
            {
                host.WriteFile(paths.StateFile, JsonSerializer.Serialize(state with { LastError = message }, JsonOptions), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The error is returned to the caller anyway.
            }
        }
    }

    private void Restore(string path, string? previous, UnixFileMode mode)
    {
        try
        {
            if (previous is null)
            {
                host.DeleteFile(path);
            }
            else
            {
                host.WriteFile(path, previous, mode);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.BackupMountRefused(logger, "-", path, "restore-failed", ex.Message);
        }
    }

    private async Task<string?> SystemctlAsync(TimeSpan timeout, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await runner.RunAsync(SystemctlCommands.SystemctlPath, arguments, timeout, ct).ConfigureAwait(false);
        return result.Succeeded ? null : $"systemctl {arguments[0]} failed ({result.Describe()})";
    }

    public void Dispose() => _gate.Dispose();

    private static bool IsManaged(string unitText) =>
        unitText.StartsWith(BackupMountRendering.ManagedMarker, StringComparison.Ordinal);

    private string ForeignUnitMessage() =>
        $"A hand-made mount unit for {paths.Target} exists ({paths.UnitPath} or its .automount), not written by mirbadllm. "
        + $"Remove it first: sudo systemctl disable --now '{paths.AutomountName}' '{paths.UnitName}'; sudo rm -f '{paths.AutomountPath}' '{paths.UnitPath}'";
}
