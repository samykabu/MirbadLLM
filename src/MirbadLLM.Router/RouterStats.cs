using System.Collections.Concurrent;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>Thread-safe per-served-model request counters.</summary>
public sealed class RouterStats(TimeProvider? time = null) : IRouterStats
{
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
