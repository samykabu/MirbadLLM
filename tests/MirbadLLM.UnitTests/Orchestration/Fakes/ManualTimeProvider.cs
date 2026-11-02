namespace MirbadLLM.UnitTests.Orchestration.Fakes;

/// <summary>Deterministic <see cref="TimeProvider"/> fake: <see cref="Advance"/> moves the clock explicitly.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
