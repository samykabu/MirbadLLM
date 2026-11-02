using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Web.Fakes;

public sealed class FakeMemoryProbe(MemorySnapshot snapshot) : IMemoryProbe
{
    public Task<MemorySnapshot> GetAsync(CancellationToken ct = default) => Task.FromResult(snapshot);
}
