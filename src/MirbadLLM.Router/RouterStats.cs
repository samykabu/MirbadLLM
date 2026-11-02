using System.Collections.Concurrent;
using MirbadLLM.Core;

namespace MirbadLLM.Router;

/// <summary>Thread-safe per-served-model request counters.</summary>
public sealed class RouterStats : IRouterStats
{
    private readonly ConcurrentDictionary<string, Counters> _counters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called when a proxied request starts. Increments total and in-flight.</summary>
    public void RequestStarted(string servedName)
    {
        var counters = _counters.GetOrAdd(servedName, static _ => new Counters());
        Interlocked.Increment(ref counters.Total);
        Interlocked.Increment(ref counters.InFlight);
    }

    /// <summary>Called when a proxied request finishes. Decrements in-flight and, when <paramref name="failed"/>, increments failed.</summary>
    public void RequestCompleted(string servedName, bool failed)
    {
        if (!_counters.TryGetValue(servedName, out var counters))
        {
            return;
        }

        Interlocked.Decrement(ref counters.InFlight);
        if (failed)
        {
            Interlocked.Increment(ref counters.Failed);
        }
    }

    public IReadOnlyList<RouterModelStats> Snapshot()
        => _counters
            .Select(kvp => new RouterModelStats(
                kvp.Key,
                Interlocked.Read(ref kvp.Value.Total),
                Interlocked.Read(ref kvp.Value.Failed),
                Volatile.Read(ref kvp.Value.InFlight)))
            .ToArray();

    private sealed class Counters
    {
        public long Total;
        public long Failed;
        public int InFlight;
    }
}
