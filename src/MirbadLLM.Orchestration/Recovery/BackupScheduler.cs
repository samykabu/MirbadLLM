using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>Outcome of the last scheduled (or "back up now") run since the controller started.</summary>
public sealed record BackupRunResult(DateTimeOffset At, bool Succeeded, string? FileName, long Bytes, bool HasSecrets, int Pruned, string? Error);

/// <summary>What the Backup page shows about scheduled backups.</summary>
public sealed record BackupScheduleStatus(
    bool Enabled,
    string Directory,
    string DailyAt,
    bool DailyAtValid,
    int Keep,
    bool PassphraseConfigured,
    DateTimeOffset? NextRunAt,
    BackupRunResult? LastRun,
    BackupDirectoryCheck DirectoryCheck,
    IReadOnlyList<BackupFileInfo> Files);

/// <summary>
/// Writes one backup a day to <see cref="BackupOptions.Directory"/> at <see cref="BackupOptions.DailyAt"/> (the
/// controller's local time), then prunes to <see cref="BackupOptions.Keep"/> files. Scheduled backups carry the
/// encrypted secrets section only when <see cref="BackupOptions.Passphrase"/> is set. Every failure — a missing or
/// read-only directory included — is logged and recorded for the UI; the service never stops the host.
/// </summary>
public sealed partial class BackupScheduler(
    BackupService backups,
    BackupDirectory directory,
    IOptions<BackupOptions> options,
    TimeProvider timeProvider,
    ILogger<BackupScheduler> logger,
    IBackupDestinationManager? destinations = null) : BackgroundService
{
    private static readonly TimeOnly DefaultDailyAt = new(3, 0);

    private readonly SemaphoreSlim _runGate = new(1, 1);
    private BackupRunResult? _lastRun;
    private DateTimeOffset? _nextRunAt;

    public BackupRunResult? LastRun => _lastRun;

    public BackupScheduleStatus GetStatus()
    {
        var o = options.Value;
        return new BackupScheduleStatus(
            directory.Enabled,
            directory.Path,
            o.DailyAt,
            o.ParsedDailyAt is not null,
            o.Keep,
            !string.IsNullOrEmpty(o.Passphrase),
            directory.Enabled ? _nextRunAt ?? NextRun(timeProvider.GetLocalNow(), o.ParsedDailyAt ?? DefaultDailyAt, timeProvider.LocalTimeZone) : null,
            _lastRun,
            directory.Check(),
            directory.List());
    }

    /// <summary>Writes a backup to the directory now and prunes. Never throws for an I/O problem: it is in the result.</summary>
    public async Task<BackupRunResult> RunNowAsync(CancellationToken ct = default)
    {
        await _runGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BackupRunResult result;
            // Re-checked on every run: a NAS share can come and go between runs.
            string? destinationProblem = destinations is null ? null : await destinations.CheckBeforeBackupAsync(ct).ConfigureAwait(false);
            var check = directory.Check();
            if (destinationProblem is not null)
            {
                result = new BackupRunResult(timeProvider.GetUtcNow(), false, null, 0, false, 0, destinationProblem);
                Log.BackupFailed(logger, null, destinationProblem);
            }
            else if (!check.Ready)
            {
                result = new BackupRunResult(timeProvider.GetUtcNow(), false, null, 0, false, 0, check.Problem);
                Log.BackupFailed(logger, null, check.Problem ?? "directory not ready");
            }
            else
            {
                try
                {
                    var document = await backups.CreateAsync(options.Value.Passphrase, ct).ConfigureAwait(false);
                    var file = await directory.WriteAsync(BackupService.FileName(document), BackupService.Serialize(document), ct).ConfigureAwait(false);
                    int pruned = directory.Prune(options.Value.Keep);
                    result = new BackupRunResult(timeProvider.GetUtcNow(), true, file.Name, file.Bytes, document.Secrets is not null, pruned, null);
                    Log.BackupWritten(logger, file.Name, file.Bytes, pruned);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result = new BackupRunResult(timeProvider.GetUtcNow(), false, null, 0, false, 0, ex.Message);
                    Log.BackupFailed(logger, ex, ex.Message);
                }
            }

            _lastRun = result;
            return result;
        }
        finally
        {
            _runGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!directory.Enabled)
        {
            Log.SchedulerDisabled(logger);
            return;
        }

        var at = options.Value.ParsedDailyAt;
        if (at is null)
        {
            Log.InvalidDailyAt(logger, options.Value.DailyAt);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetLocalNow();
                var next = NextRun(now, at ?? DefaultDailyAt, timeProvider.LocalTimeZone);
                _nextRunAt = next;
                await Task.Delay(next - now, timeProvider, stoppingToken).ConfigureAwait(false);
                await RunNowAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let a backup problem stop the host; RunNowAsync already records its own failures, so this
                // is only reached by something unexpected. Pause a minute so a persistent fault can't spin.
                Log.BackupFailed(logger, ex, ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(1), timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    public override void Dispose()
    {
        _runGate.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// The next wall-clock <paramref name="at"/> in <paramref name="zone"/> strictly after <paramref name="now"/>
    /// (today if it is still ahead, otherwise tomorrow), with that day's UTC offset.
    /// </summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeOnly at, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var candidate = local.Date + at.ToTimeSpan();
        if (candidate <= local.DateTime)
        {
            candidate = candidate.AddDays(1);
        }

        return new DateTimeOffset(candidate, zone.GetUtcOffset(candidate));
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Backup: wrote {FileName} ({Bytes} bytes), pruned {Pruned} old file(s).")]
        public static partial void BackupWritten(ILogger logger, string fileName, long bytes, int pruned);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Backup: scheduled backup failed: {Reason}")]
        public static partial void BackupFailed(ILogger logger, Exception? exception, string reason);

        [LoggerMessage(Level = LogLevel.Information, Message = "Backup: scheduled backups are off (MirbadLLM:Backup:Directory is empty).")]
        public static partial void SchedulerDisabled(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Backup: MirbadLLM:Backup:DailyAt '{DailyAt}' is not HH:mm; using 03:00.")]
        public static partial void InvalidDailyAt(ILogger logger, string dailyAt);
    }
}
