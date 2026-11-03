using System.Collections.Concurrent;

namespace MirbadLLM.Orchestration;

/// <summary>
/// Counts the operator's stops (and cancelled starts) per model during this controller's lifetime. A boot auto-start that
/// was queued before the operator stopped the model compares the count and leaves the model stopped (D51: a model you
/// stopped stays stopped). Not persisted: on the next controller boot, auto-start models start again by design.
/// </summary>
public sealed class OperatorStops
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    public void Record(string modelId) => _counts.AddOrUpdate(modelId, 1, (_, n) => n + 1);

    public long Count(string modelId) => _counts.GetValueOrDefault(modelId);
}
