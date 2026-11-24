using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Web.Fakes;

/// <summary>Fixed router counters; <paramref name="finished"/> answers <see cref="FinishedIn"/> (none when not given).</summary>
public sealed class FakeRouterStats(IReadOnlyList<RouterModelStats> stats, IReadOnlyList<RouterModelStats>? finished = null) : IRouterStats
{
    /// <summary>The last window <see cref="FinishedIn"/> was asked for.</summary>
    public TimeSpan? LastWindow { get; private set; }

    public IReadOnlyList<RouterModelStats> Snapshot() => stats;

    public IReadOnlyList<RouterModelStats> FinishedIn(TimeSpan window)
    {
        LastWindow = window;
        return finished ?? [];
    }
}
