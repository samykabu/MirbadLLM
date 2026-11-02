using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>
/// Design §4.8 "Concurrency": automatic starts (boot auto-start in <see cref="ModelHealthMonitor"/>, restore auto-start in
/// <see cref="Recovery.RestoreJobService"/>) run as one lane per node: sequential within a node, parallel across nodes.
/// The agent stays authoritative (its start reservation answers <see cref="ModelStartConflictException"/> when a start
/// overlaps anyway); the lanes only keep the master from asking for overlapping starts on one node. P1 has a single
/// node, so everything lands in one lane, as before.
/// </summary>
public sealed class PerNodeStartLanes
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task> _tails = new(StringComparer.Ordinal);

    /// <summary>
    /// The node lane an automatic start of <paramref name="model"/> runs in: its placement's node, or
    /// <paramref name="bootstrapNodeId"/> for an unplaced model (in P1 the single-box runtime starts it on the box).
    /// </summary>
    public static string NodeOf(ModelDefinition model, string bootstrapNodeId)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Placement?.NodeId is { Length: > 0 } nodeId ? nodeId : bootstrapNodeId;
    }

    /// <summary>
    /// Runs <paramref name="start"/> after every start queued earlier on <paramref name="nodeId"/> has finished (whatever
    /// its outcome: a failed start never blocks the lane). Returns the task of this start.
    /// </summary>
    public Task Enqueue(string nodeId, Func<Task> start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(start);
        // The lane's tail is swapped under the lock; the start itself runs outside it (it may run synchronously up to
        // its first await, and must never do so while holding the lock).
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_gate)
        {
            previous = _tails.GetValueOrDefault(nodeId) ?? Task.CompletedTask;
            _tails[nodeId] = done.Task;
        }

        _ = RunAfterAsync(previous, start, done);
        return done.Task;
    }

    /// <summary>Completes when every start queued so far has finished; faults or cancels like the last start of a lane.</summary>
    public Task WhenAllAsync()
    {
        lock (_gate)
        {
            return Task.WhenAll([.. _tails.Values]);
        }
    }

    /// <summary>Completes when every start queued so far has finished, without observing their outcome.</summary>
    public async Task WhenAllSettledAsync() =>
        await WhenAllAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private static async Task RunAfterAsync(Task previous, Func<Task> start, TaskCompletionSource done)
    {
        // The earlier start's outcome belongs to its own caller; this one only waits for its turn.
        await previous.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        try
        {
            await start().ConfigureAwait(false);
            done.TrySetResult();
        }
        catch (OperationCanceledException ex)
        {
            done.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Handed to the caller through the returned task, not swallowed.
            done.TrySetException(ex);
        }
    }
}
