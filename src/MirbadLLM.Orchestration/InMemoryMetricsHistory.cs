using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// A stretch of time without host samples for a node that the master knows nothing was measured in (design §8.1):
/// the agent restarted (<see cref="AgentRestarted"/>) or samples were dropped from its ring before the master pulled
/// them (<see cref="SamplesDropped"/>). Charts show it as a gap; nothing is interpolated.
/// </summary>
/// <param name="From">Time of the last sample before the gap.</param>
/// <param name="To">Time of the first sample after the gap.</param>
/// <param name="Reason"><see cref="AgentRestarted"/> or <see cref="SamplesDropped"/>.</param>
public sealed record MetricsGap(DateTimeOffset From, DateTimeOffset To, string Reason)
{
    /// <summary>The agent's boot id changed: nothing was sampled while it was down.</summary>
    public const string AgentRestarted = "agent_restarted";

    /// <summary>The agent reported <c>gap: true</c> in the same boot: samples were evicted from its ring before the pull.</summary>
    public const string SamplesDropped = "samples_dropped";
}

/// <summary>
/// Thread-safe rolling in-memory history filled by <c>RemoteTelemetryCollector</c> (host samples keyed by node, design
/// §8.1). Read access goes through
/// <see cref="IMetricsHistory"/>, whose host series is the <see cref="DefaultNodeId"/>'s (the bootstrap node); the
/// writers use this concrete type's <see cref="AddHost(HostSample)"/>, <see cref="AddHost(string, HostSample)"/>,
/// <see cref="AddModel"/>, <see cref="AddGap"/> and <see cref="RemoveModel"/>. Samples are kept in time order: a
/// back-filled sample (after a partition) is inserted where it belongs, not appended.
/// </summary>
public sealed class InMemoryMetricsHistory(
    IOptions<MetricsOptions> options,
    TimeProvider timeProvider,
    IOptions<RegistryMigrationOptions>? migration = null) : IMetricsHistory
{
    private readonly MetricsOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, HostBuffer> _hosts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ModelBuffer> _models = new(StringComparer.Ordinal);

    /// <summary>
    /// The node whose host series <see cref="IMetricsHistory.GetHost(TimeSpan)"/> and <see cref="AddHost(HostSample)"/>
    /// use: <c>MirbadLLM:Nodes:BootstrapNodeId</c> (default <c>dgx</c>).
    /// </summary>
    public string DefaultNodeId { get; } = migration?.Value.EffectiveBootstrapNodeId ?? RegistryMigrationOptions.DefaultBootstrapNodeId;

    /// <summary>Adds a host sample of the <see cref="DefaultNodeId"/> (single-host composition).</summary>
    public void AddHost(HostSample sample) => AddHost(DefaultNodeId, sample);

    /// <summary>Adds a host sample of <paramref name="nodeId"/>, in time order.</summary>
    public void AddHost(string nodeId, HostSample sample)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(sample);
        var buffer = _hosts.GetOrAdd(nodeId, static _ => new HostBuffer());
        lock (buffer.Gate)
        {
            InsertInTimeOrder(buffer.Samples, sample, static s => s.At);
            TrimByAge(buffer.Samples, s => s.At);
            TrimByAge(buffer.Gaps, g => g.To);
        }
    }

    /// <summary>Records a gap in <paramref name="nodeId"/>'s host series (design §8.1: honest numbers, no interpolation).</summary>
    public void AddGap(string nodeId, MetricsGap gap)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);
        ArgumentNullException.ThrowIfNull(gap);
        var buffer = _hosts.GetOrAdd(nodeId, static _ => new HostBuffer());
        lock (buffer.Gate)
        {
            InsertInTimeOrder(buffer.Gaps, gap, static g => g.From);
            TrimByAge(buffer.Gaps, g => g.To);
        }
    }

    public void AddModel(string modelId, ModelSample sample)
    {
        var buffer = _models.GetOrAdd(modelId, static _ => new ModelBuffer());
        lock (buffer.Gate)
        {
            InsertInTimeOrder(buffer.Samples, sample, static s => s.At);
            TrimByAge(buffer.Samples, s => s.At);
            if (buffer.Latest is null || sample.At >= buffer.Latest.At)
            {
                buffer.Latest = sample;
            }
        }
    }

    /// <summary>Drops all history for a model no longer in the registry.</summary>
    public void RemoveModel(string modelId) => _models.TryRemove(modelId, out _);

    /// <summary>Drops the host series and gaps of a node (revoked or deleted).</summary>
    public void RemoveNode(string nodeId) => _hosts.TryRemove(nodeId, out _);

    /// <summary>Nodes with a host series, in ordinal order.</summary>
    public IReadOnlyList<string> HostNodeIds() => [.. _hosts.Keys.Order(StringComparer.Ordinal)];

    public IReadOnlyList<HostSample> GetHost(TimeSpan window) => GetHost(DefaultNodeId, window);

    /// <summary>Host samples of <paramref name="nodeId"/> within <paramref name="window"/>, oldest first.</summary>
    public IReadOnlyList<HostSample> GetHost(string nodeId, TimeSpan window)
    {
        if (!_hosts.TryGetValue(nodeId, out var buffer))
        {
            return [];
        }

        var cutoff = timeProvider.GetUtcNow() - window;
        lock (buffer.Gate)
        {
            return buffer.Samples.Where(s => s.At >= cutoff).ToList();
        }
    }

    /// <summary>Gaps of <paramref name="nodeId"/> that end within <paramref name="window"/>, oldest first.</summary>
    public IReadOnlyList<MetricsGap> GetGaps(string nodeId, TimeSpan window)
    {
        if (!_hosts.TryGetValue(nodeId, out var buffer))
        {
            return [];
        }

        var cutoff = timeProvider.GetUtcNow() - window;
        lock (buffer.Gate)
        {
            return buffer.Gaps.Where(g => g.To >= cutoff).ToList();
        }
    }

    public IReadOnlyList<ModelSample> GetModel(string modelId, TimeSpan window)
    {
        if (!_models.TryGetValue(modelId, out var buffer))
        {
            return [];
        }

        var cutoff = timeProvider.GetUtcNow() - window;
        lock (buffer.Gate)
        {
            return buffer.Samples.Where(s => s.At >= cutoff).ToList();
        }
    }

    public IReadOnlyDictionary<string, ModelKpis> Latest()
    {
        var result = new Dictionary<string, ModelKpis>(StringComparer.Ordinal);
        var cutoff = timeProvider.GetUtcNow() - _options.LatestFreshness;
        foreach (var (modelId, buffer) in _models)
        {
            lock (buffer.Gate)
            {
                // A model whose samples stopped (stopped, crashed, deleted) is not running now, whatever retention keeps.
                if (buffer.Latest is { } latest && latest.At >= cutoff)
                {
                    result[modelId] = latest.Kpis;
                }
            }
        }

        return result;
    }

    /// <summary>Appends when <paramref name="item"/> is the newest (the usual case), else inserts after every item not later than it.</summary>
    private static void InsertInTimeOrder<T>(List<T> items, T item, Func<T, DateTimeOffset> at)
    {
        var time = at(item);
        if (items.Count == 0 || at(items[^1]) <= time)
        {
            items.Add(item);
            return;
        }

        int low = 0;
        int high = items.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (at(items[mid]) <= time)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        items.Insert(low, item);
    }

    private void TrimByAge<T>(List<T> samples, Func<T, DateTimeOffset> at)
    {
        var cutoff = timeProvider.GetUtcNow() - _options.RetentionWindow;
        samples.RemoveAll(s => at(s) < cutoff);
    }

    private sealed class HostBuffer
    {
        public readonly object Gate = new();
        public readonly List<HostSample> Samples = [];
        public readonly List<MetricsGap> Gaps = [];
    }

    private sealed class ModelBuffer
    {
        public readonly object Gate = new();
        public readonly List<ModelSample> Samples = [];
        public ModelSample? Latest;
    }
}
