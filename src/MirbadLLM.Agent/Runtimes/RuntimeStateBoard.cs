using System.Collections.Concurrent;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Runtimes;

/// <summary>
/// The agent's view of its runtime instances and the single place that turns their changes into
/// <see cref="RuntimeEvent"/>s (design §4.7). Each change is applied and published atomically through
/// <see cref="IEventBus.Publish(Action, AgentEvent)"/>, so a status snapshot that reads <see cref="Current"/> inside
/// <see cref="IEventBus.Snapshot{T}"/> is consistent with its watermark.
/// <para>Two writers feed it: the runtime operations report what a launch or stop just did
/// (<see cref="Report"/>), and the <see cref="RuntimeWatcher"/> applies full listings (<see cref="Apply"/>). A listing taken
/// before a report never overwrites that report (see <see cref="Version"/>), and a model whose launch is creating its
/// instance (<see cref="Claim"/>) is left to that launch until it reports.</para>
/// </summary>
public sealed class RuntimeStateBoard
{
    private readonly IEventBus _events;
    private readonly ConcurrentDictionary<string, RuntimeStatus> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _reportedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _claims = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _version;

    /// <summary>Creates the board.</summary>
    public RuntimeStateBoard(IEventBus events) => _events = events;

    /// <summary>
    /// Monotonic counter of <see cref="Report"/> calls. Read it before listing the drivers and pass it to
    /// <see cref="Apply"/>: models reported after that point keep the reported state.
    /// </summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>
    /// Starts an observation of the drivers (a full listing, or one model's read) and returns its ticket, to pass to
    /// <see cref="Apply"/> or <see cref="ApplyOne"/>. Tickets order observations by when they started: an observation
    /// never overwrites what a later-started one (or a later <see cref="Report"/>) published, whichever finishes first
    /// (W8 review MN-128: a launch's read of the existing instance, taken just before the instance turned Ready, was
    /// published after the watcher's Ready and showed the master Ready → Starting).
    /// </summary>
    public long BeginObservation()
    {
        lock (_gate)
        {
            return ++_version;
        }
    }

    /// <summary>
    /// Applies one model's instance as read from its driver under <paramref name="ticket"/> (<see cref="BeginObservation"/>),
    /// with <see cref="Apply"/>'s rules: skipped while a launch claims the model or when a later observation or report
    /// already published it; otherwise published when it changed (null = gone).
    /// </summary>
    public void ApplyOne(string modelId, RuntimeStatus? instance, long ticket)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        lock (_gate)
        {
            if ((_reportedAt.TryGetValue(modelId, out long reportedAt) && reportedAt > ticket) || _claims.ContainsKey(modelId))
            {
                return;
            }

            _reportedAt[modelId] = ticket;
            PublishIfChanged(modelId, instance);
        }
    }

    /// <summary>Every known instance, ordered by model id.</summary>
    public IReadOnlyList<RuntimeStatus> Current =>
        [.. _known.Values.OrderBy(r => r.ModelId, StringComparer.Ordinal)];

    /// <summary>The known instance of a model, or null (Stopped).</summary>
    public RuntimeStatus? Find(string modelId) => _known.GetValueOrDefault(modelId);

    /// <summary>Reports a model's instance now (null = gone) and publishes a runtime event when it changed.</summary>
    public void Report(string modelId, RuntimeStatus? instance)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        lock (_gate)
        {
            _version++;
            _reportedAt[modelId] = _version;
            PublishIfChanged(modelId, instance);
        }
    }

    /// <summary>
    /// Claims a model for a launch that is about to create its instance; dispose after the launch has reported (or failed).
    /// While claimed, <see cref="Apply"/> leaves the model alone: the driver creates and starts the container before the
    /// launch reports it Starting, and a listing taken in between can already see it Ready (a fast <c>/health</c>), which
    /// would publish Ready → Starting → Ready to the master (MN-047). The launch's own report comes first instead.
    /// </summary>
    public IDisposable Claim(string modelId)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        lock (_gate)
        {
            _claims[modelId] = _claims.GetValueOrDefault(modelId) + 1;
        }

        return new ModelClaim(this, modelId);
    }

    /// <summary>
    /// Applies a full listing taken when <see cref="Version"/> was <paramref name="listedAtVersion"/>: publishes new and
    /// changed instances and removals, skipping models reported after the listing started.
    /// </summary>
    public void Apply(IReadOnlyList<RuntimeStatus> listing, long listedAtVersion)
    {
        ArgumentNullException.ThrowIfNull(listing);
        lock (_gate)
        {
            var byModel = new Dictionary<string, RuntimeStatus>(StringComparer.Ordinal);
            foreach (var instance in listing)
            {
                byModel.TryAdd(instance.ModelId, instance);
            }

            foreach (string modelId in _known.Keys.Union(byModel.Keys).Order(StringComparer.Ordinal).ToList())
            {
                if ((_reportedAt.TryGetValue(modelId, out long reportedAt) && reportedAt > listedAtVersion) || _claims.ContainsKey(modelId))
                {
                    continue;
                }

                _reportedAt[modelId] = Math.Max(reportedAt, listedAtVersion); // an earlier-started read must not overwrite this
                PublishIfChanged(modelId, byModel.GetValueOrDefault(modelId));
            }
        }
    }

    private void Release(string modelId)
    {
        lock (_gate)
        {
            if (_claims.TryGetValue(modelId, out int count) && count > 1)
            {
                _claims[modelId] = count - 1;
            }
            else
            {
                _claims.Remove(modelId);
            }
        }
    }

    private void PublishIfChanged(string modelId, RuntimeStatus? instance)
    {
        var previous = _known.GetValueOrDefault(modelId);
        if (SameObservable(previous, instance))
        {
            if (instance is not null)
            {
                _known[modelId] = instance; // keep the freshest copy (labels, measured memory) without an event
            }

            return;
        }

        _events.Publish(
            () =>
            {
                if (instance is null)
                {
                    _known.TryRemove(modelId, out _);
                }
                else
                {
                    _known[modelId] = instance;
                }
            },
            new RuntimeEvent { ModelId = modelId, Runtime = instance });
    }

    /// <summary>
    /// True when nothing the master acts on changed: state, instance, port, error, restart count, creating operation and
    /// the hashes. <see cref="RuntimeStatus.Since"/> and measured memory alone do not produce events.
    /// </summary>
    internal static bool SameObservable(RuntimeStatus? a, RuntimeStatus? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.State == b.State
            && string.Equals(a.InstanceId, b.InstanceId, StringComparison.Ordinal)
            && a.HostPort == b.HostPort
            && string.Equals(a.Error, b.Error, StringComparison.Ordinal)
            && a.RestartCount == b.RestartCount
            && string.Equals(a.OperationId, b.OperationId, StringComparison.Ordinal)
            && string.Equals(a.ConfigHash, b.ConfigHash, StringComparison.Ordinal)
            && string.Equals(a.ExecutionFingerprint, b.ExecutionFingerprint, StringComparison.Ordinal)
            && a.Tuning == b.Tuning;
    }

    private sealed class ModelClaim(RuntimeStateBoard board, string modelId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                board.Release(modelId);
            }
        }
    }
}
