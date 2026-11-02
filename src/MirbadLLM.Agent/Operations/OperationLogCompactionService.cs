using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Operations;

/// <summary>
/// Compacts the operation log once at start and then every <see cref="OperationRetention.CompactionInterval"/> (daily,
/// design §4.4 items 1 and 6). A failed compaction is logged and retried at the next tick; the log stays correct either
/// way (it only grows).
/// </summary>
public sealed class OperationLogCompactionService : BackgroundService
{
    private readonly IOperationLog _log;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    /// <summary>Creates the service with the default interval.</summary>
    public OperationLogCompactionService(IOperationLog log, TimeProvider timeProvider, ILogger<OperationLogCompactionService>? logger = null)
        : this(log, timeProvider, OperationRetention.CompactionInterval, logger)
    {
    }

    /// <summary>Creates the service with an explicit interval (tests).</summary>
    public OperationLogCompactionService(IOperationLog log, TimeProvider timeProvider, TimeSpan interval, ILogger<OperationLogCompactionService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        _log = log;
        _time = timeProvider;
        _interval = interval;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>Number of compactions that completed (diagnostics and tests).</summary>
    public int CompletedRuns => Volatile.Read(ref _completedRuns);

    private int _completedRuns;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CompactOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await CompactOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
    }

    private async Task CompactOnceAsync(CancellationToken ct)
    {
        try
        {
            await _log.CompactAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _completedRuns);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            OperationLogMessages.CompactionFailed(_logger, ex);
        }
    }
}
