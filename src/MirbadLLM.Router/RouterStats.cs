using System.Collections.Concurrent;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>Thread-safe per-served-model request counters, with the last hour of finished requests in 10 s buckets.</summary>
public sealed class RouterStats(TimeProvider? time = null) : IRouterStats
{
    /// <summary>Width of one bucket of finished requests (<see cref="FinishedIn"/> is exact to this).</summary>
    public static readonly TimeSpan BucketWidth = TimeSpan.FromSeconds(10);

    /// <summary>The longest window <see cref="FinishedIn"/> answers for; a longer one is cut to this.</summary>
    public static readonly TimeSpan Kept = TimeSpan.FromHours(1);

    private static readonly int Buckets = (int)(Kept.Ticks / BucketWidth.Ticks);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Counters> _counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called when a proxied request starts. Increments total and in-flight.</summary>
    public void RequestStarted(string servedName)
    {
        var counters = _counters.GetOrAdd(servedName, static _ => new Counters());
        Interlocked.Increment(ref counters.Total);
        Interlocked.Increment(ref counters.InFlight);
        Touch(counters);
    }

    /// <summary>Called when a proxied request finishes. Decrements in-flight and, when <paramref name="failed"/>, increments failed.</summary>
    public void RequestCompleted(string servedName, bool failed)
    {
        if (!_counters.TryGetValue(servedName, out var counters))
        {
            return;
        }

        Interlocked.Decrement(ref counters.InFlight);
        Touch(counters);
        if (failed)
        {
            Interlocked.Increment(ref counters.Failed);
        }

        long bucket = _time.GetUtcNow().UtcTicks / BucketWidth.Ticks;
        int slot = (int)(bucket % Buckets);
        lock (counters.Gate)
        {
            if (counters.SlotBucket[slot] != bucket)
            {
                // The slot last held a bucket an hour or more ago: start it over.
                counters.SlotBucket[slot] = bucket;
                counters.SlotFinished[slot] = 0;
                counters.SlotFailed[slot] = 0;
            }

            counters.SlotFinished[slot]++;
            if (failed)
            {
                counters.SlotFailed[slot]++;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RouterModelStats> FinishedIn(TimeSpan window)
    {
        long now = _time.GetUtcNow().UtcTicks / BucketWidth.Ticks;
        // The current (partial) bucket plus enough whole ones to cover the window.
        long count = Math.Clamp((long)Math.Ceiling(window.Ticks / (double)BucketWidth.Ticks), 1, Buckets);
        var result = new List<RouterModelStats>();
        foreach (var (name, counters) in _counters)
        {
            long finished = 0, failed = 0;
            lock (counters.Gate)
            {
                for (int i = 0; i < Buckets; i++)
                {
                    if (counters.SlotBucket[i] > now - count && counters.SlotBucket[i] <= now)
                    {
                        finished += counters.SlotFinished[i];
                        failed += counters.SlotFailed[i];
                    }
                }
            }

            if (finished > 0)
            {
                result.Add(new RouterModelStats(name, finished, failed, Volatile.Read(ref counters.InFlight)));
            }
        }

        return result;
    }

    /// <summary>
    /// Marks a request that waits for its model to start on request. Until the returned scope is disposed the model
    /// counts as in use (<see cref="InUse"/>), so it is not stopped between becoming Ready and the request reaching it.
    /// Not part of total or in-flight.
    /// </summary>
    public IDisposable BeginWaiting(string servedName) => new WaitScope(_counters.GetOrAdd(servedName, static _ => new Counters()));

    public IReadOnlyList<RouterModelStats> Snapshot()
        => _counters
            .Where(kvp => Interlocked.Read(ref kvp.Value.Total) > 0) // a model that only had a waiting request has no row yet
            .Select(kvp => new RouterModelStats(
                kvp.Key,
                Interlocked.Read(ref kvp.Value.Total),
                Interlocked.Read(ref kvp.Value.Failed),
                Volatile.Read(ref kvp.Value.InFlight)))
            .ToArray();

    /// <inheritdoc />
    public bool InUse(string servedName) =>
        _counters.TryGetValue(servedName, out var counters) &&
        (Volatile.Read(ref counters.InFlight) > 0 || Volatile.Read(ref counters.Waiting) > 0);

    /// <inheritdoc />
    public DateTimeOffset? LastRequestAt(string servedName) =>
        _counters.TryGetValue(servedName, out var counters) && Interlocked.Read(ref counters.LastRequestTicks) is > 0 and var ticks
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;

    private void Touch(Counters counters) => Interlocked.Exchange(ref counters.LastRequestTicks, _time.GetUtcNow().UtcTicks);

    private sealed class Counters
    {
        public long Total;
        public long Failed;
        public int InFlight;
        public int Waiting;
        public long LastRequestTicks;
        public readonly Lock Gate = new();
        public readonly long[] SlotBucket = new long[Buckets];
        public readonly long[] SlotFinished = new long[Buckets];
        public readonly long[] SlotFailed = new long[Buckets];
    }

    private sealed class WaitScope : IDisposable
    {
        private Counters? _counters;

        public WaitScope(Counters counters)
        {
            _counters = counters;
            Interlocked.Increment(ref counters.Waiting);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _counters, null) is { } counters)
            {
                Interlocked.Decrement(ref counters.Waiting);
            }
        }
    }
}
