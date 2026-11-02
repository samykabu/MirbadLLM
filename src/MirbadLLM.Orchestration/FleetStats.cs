using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// What the fleet has done since stats began (<see cref="StartedAt"/>): requests served, generated tokens (in all and
/// per local day) and the last crash.
/// </summary>
public sealed record FleetStatsSnapshot(
    DateTimeOffset StartedAt,
    long Requests,
    double Tokens,
    IReadOnlyDictionary<string, double> TokensByDay,
    DateTimeOffset? LastCrashAt)
{
    /// <summary>Tokens generated on the local day of <paramref name="now"/> (in <paramref name="zone"/>).</summary>
    public double TokensOn(DateTimeOffset now, TimeZoneInfo zone) => TokensByDay.GetValueOrDefault(FleetStatsStore.DayKey(now, zone));
}

/// <summary>
/// The fleet's persisted counters, <c>{DataPath}/fleet-stats.json</c>, written atomically (temp file + move, as the other
/// master stores do). Fed by <see cref="FleetStatsCollector"/> from signals that already exist (the router's counters,
/// the metrics history, crash records). An empty <see cref="MirbadLLMOptions.DataPath"/> keeps it in memory only. A file
/// that cannot be read starts fresh and is logged; it never stops the master. Fields an older version wrote (per-model
/// totals, quest XP) are ignored when read and no longer written.
/// </summary>
public sealed partial class FleetStatsStore
{
    public const string FileName = "fleet-stats.json";

    /// <summary>Days of per-day token totals kept.</summary>
    public const int DaysKept = 31;

    private readonly Lock _gate = new();
    private readonly Lock _writeGate = new();
    private readonly string? _path;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private State _state;
    private bool _dirty;

    public FleetStatsStore(IOptions<MirbadLLMOptions> options, TimeProvider? time = null, ILogger<FleetStatsStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger<FleetStatsStore>.Instance;
        _path = string.IsNullOrWhiteSpace(options.Value.DataPath) ? null : Path.Combine(options.Value.DataPath, FileName);
        _state = Load() ?? new State { StartedAt = _time.GetUtcNow() };
    }

    /// <summary>The time zone days are counted in (the master's local zone).</summary>
    public TimeZoneInfo Zone => _time.LocalTimeZone;

    /// <summary>"2026-11-02": the local day of <paramref name="at"/> in <paramref name="zone"/>.</summary>
    public static string DayKey(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public FleetStatsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new FleetStatsSnapshot(
                _state.StartedAt,
                _state.Requests,
                _state.Tokens,
                new Dictionary<string, double>(_state.TokensByDay, StringComparer.Ordinal),
                _state.LastCrashAt);
        }
    }

    /// <summary>Counts <paramref name="count"/> more requests served.</summary>
    public void AddRequests(long count)
    {
        if (count <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _state.Requests += count;
            _dirty = true;
        }
    }

    /// <summary>Counts <paramref name="tokens"/> generated at <paramref name="at"/> (its local day).</summary>
    public void AddTokens(double tokens, DateTimeOffset at)
    {
        if (!double.IsFinite(tokens) || tokens <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _state.Tokens += tokens;
            string day = DayKey(at, Zone);
            _state.TokensByDay[day] = _state.TokensByDay.GetValueOrDefault(day) + tokens;
            foreach (string old in _state.TokensByDay.Keys.Order(StringComparer.Ordinal).SkipLast(DaysKept).ToList())
            {
                _state.TokensByDay.Remove(old);
            }

            _dirty = true;
        }
    }

    /// <summary>Records a model crash at <paramref name="at"/> (only a later one moves the mark).</summary>
    public void RecordCrash(DateTimeOffset at)
    {
        lock (_gate)
        {
            if (_state.LastCrashAt is { } last && last >= at)
            {
                return;
            }

            _state.LastCrashAt = at;
            _dirty = true;
        }
    }

    /// <summary>Writes the counters when they changed since the last write.</summary>
    public void Flush()
    {
        // One writer at a time: the collector's timer and a shutdown flush share the temp file.
        lock (_writeGate)
        {
            string json;
            lock (_gate)
            {
                if (!_dirty || _path is null)
                {
                    return;
                }

                json = JsonSerializer.Serialize(_state, MasterStateJson.Options);
                _dirty = false;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, json);
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        File.Move(temp, _path, overwrite: true);
                        break;
                    }
                    catch (IOException) when (attempt < 5)
                    {
                        // A scanner can hold the file we just wrote for a moment (Windows); try again shortly.
                        Thread.Sleep(20 * attempt);
                    }
                }

                DirectorySync.FlushParent(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (_gate)
                {
                    _dirty = true; // try again on the next flush
                }

                WriteFailed(_logger, ex, _path);
            }
        }
    }

    private State? Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return null;
        }

        try
        {
            string text;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    text = File.ReadAllText(_path);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(20 * attempt); // held for a moment by a scanner (Windows): starting fresh would lose the counts
                }
            }

            var state = JsonSerializer.Deserialize<State>(text, MasterStateJson.Options);
            if (state is null)
            {
                return null;
            }

            // Dictionaries come back with the default comparer; keep the store's ordinal keys.
            state.TokensByDay = new Dictionary<string, double>(state.TokensByDay ?? [], StringComparer.Ordinal);
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            ReadFailed(_logger, ex, _path);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the fleet stats in {Path}; counting starts again.")]
    private static partial void ReadFailed(ILogger logger, Exception exception, string? path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the fleet stats to {Path}; retrying on the next flush.")]
    private static partial void WriteFailed(ILogger logger, Exception exception, string? path);

    private sealed class State
    {
        public DateTimeOffset StartedAt { get; set; }

        public long Requests { get; set; }

        public double Tokens { get; set; }

        public Dictionary<string, double> TokensByDay { get; set; } = new(StringComparer.Ordinal);

        public DateTimeOffset? LastCrashAt { get; set; }

    }
}

/// <summary>
/// Feeds <see cref="FleetStatsStore"/> once per sampling interval from what the master already measures: requests served
/// are the growth of each model's router counters (total − failed; a counter that went down restarted with the
/// controller and counts from zero); generated tokens are each new history sample's generation rate × the sampling
/// interval (samples without rates, the first after a start, add nothing); a crash is a crash record newer than the last
/// one seen. Writes at most once a minute, and on shutdown.
/// </summary>
public sealed class FleetStatsCollector(IServiceProvider services, FleetStatsStore store, TimeProvider? time = null) : BackgroundService
{
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, long> _served = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _cursor = new(StringComparer.Ordinal);
    private DateTimeOffset? _startedAt;
    private DateTimeOffset _flushedAt = DateTimeOffset.MinValue;

    private TimeSpan Interval => services.GetService<IOptions<MetricsOptions>>()?.Value.SampleInterval is { Ticks: > 0 } interval
        ? interval
        : TimeSpan.FromSeconds(5);

    /// <summary>One collection pass (the timer's work; tests call it directly).</summary>
    public async Task CollectAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        _startedAt ??= now - Interval; // history from before this collector started is not counted twice after a restart
        IReadOnlyList<ModelDefinition> models = services.GetService<IModelRegistry>() is { } registry
            ? await registry.ListAsync(ct).ConfigureAwait(false)
            : [];

        if (services.GetService<IRouterStats>() is { } router)
        {
            foreach (var stats in router.Snapshot())
            {
                long served = Math.Max(0, stats.TotalRequests - stats.FailedRequests);
                long last = _served.GetValueOrDefault(stats.ServedName);
                _served[stats.ServedName] = served;
                long delta = served >= last ? served - last : served;
                if (delta > 0)
                {
                    store.AddRequests(delta);
                }
            }
        }

        if (services.GetService<IMetricsHistory>() is { } history)
        {
            double seconds = Interval.TotalSeconds;
            foreach (var model in models)
            {
                var since = _cursor.GetValueOrDefault(model.Id, _startedAt.Value);
                foreach (var sample in history.GetModel(model.Id, now - since + Interval).Where(s => s.At > since).OrderBy(s => s.At))
                {
                    if (sample.HasRates)
                    {
                        store.AddTokens(sample.Kpis.Load.GenerationTokensPerSecond * seconds, sample.At);
                    }

                    _cursor[model.Id] = sample.At;
                }
            }
        }

        if (services.GetService<ModelRecoveryState>() is { } recovery)
        {
            foreach (var model in models)
            {
                if (recovery.CrashRestartOf(model.Id) is { } crash)
                {
                    store.RecordCrash(crash.At);
                }
            }
        }

        if (now - _flushedAt >= FlushEvery)
        {
            store.Flush();
            _flushedAt = now;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await CollectAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // A failed pass (the registry unreadable for a moment) is retried on the next tick.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        store.Flush();
    }
}
