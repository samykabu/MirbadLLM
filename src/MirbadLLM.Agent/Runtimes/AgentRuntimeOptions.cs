namespace MirbadLLM.Agent.Runtimes;

/// <summary>Timings of the runtime operations and the runtime watcher (design §4.5, §4.8). Defaults suit production.</summary>
public sealed class AgentRuntimeOptions
{
    /// <summary>How often the watcher lists the drivers' instances (state changes, Ready, failures).</summary>
    public TimeSpan WatchInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A pull longer than this makes the launch re-check admission right before create (design §4.8).</summary>
    public TimeSpan SlowPullThreshold { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long <c>/operations/{id}/cancel</c> waits for a cancelled launch to reach its terminal state.</summary>
    public TimeSpan CancelWait { get; init; } = TimeSpan.FromSeconds(10);
}
