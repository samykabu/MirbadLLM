using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Web.Fakes;

public sealed class FakeRouterStats(IReadOnlyList<RouterModelStats> stats) : IRouterStats
{
    public IReadOnlyList<RouterModelStats> Snapshot() => stats;
}
