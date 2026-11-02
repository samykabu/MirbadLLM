using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Orchestration.Fakes;

internal sealed class FakeMemoryProbe(double totalGiB, double availableGiB) : IMemoryProbe
{
    public Task<MemorySnapshot> GetAsync(CancellationToken ct = default)
        => Task.FromResult(new MemorySnapshot(totalGiB, availableGiB));
}
