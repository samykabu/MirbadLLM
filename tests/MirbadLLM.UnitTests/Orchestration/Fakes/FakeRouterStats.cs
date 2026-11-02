using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Orchestration.Fakes;

/// <summary>Hand-written fake <see cref="IRouterStats"/> returning a fixed snapshot.</summary>
internal sealed class FakeRouterStats(IReadOnlyList<RouterModelStats> snapshot) : IRouterStats
{
    public IReadOnlyList<RouterModelStats> Snapshot() => snapshot;
}
