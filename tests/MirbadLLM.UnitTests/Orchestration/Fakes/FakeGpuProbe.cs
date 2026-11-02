using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Orchestration.Fakes;

/// <summary>Hand-written fake <see cref="IGpuProbe"/> returning a fixed (or null) snapshot.</summary>
internal sealed class FakeGpuProbe(GpuSnapshot? snapshot) : IGpuProbe
{
    public Task<GpuSnapshot?> GetAsync(CancellationToken ct = default) => Task.FromResult(snapshot);
}
