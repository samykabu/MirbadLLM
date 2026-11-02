using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// The 15-minute telemetry ring (design §8.1) behind <c>GET /telemetry</c>. Samples get a per-boot seq starting at 1;
/// samples older than <see cref="TelemetryOptions.RingWindow"/> (relative to the newest) or beyond
/// <see cref="TelemetryOptions.RingCapacity"/> are evicted.
/// <para>Cursor rules (<see cref="Read"/>):</para>
/// <list type="bullet">
/// <item>No cursor (first pull): the oldest kept samples, <c>gap: false</c>.</item>
/// <item>Same boot, cursor still in the ring (or at the newest): the samples after it, <c>gap: false</c>.</item>
/// <item>Same boot, cursor evicted (a partition longer than the window): the oldest kept samples, <c>gap: true</c>.</item>
/// <item>Another boot (the agent restarted): the oldest samples of this boot, <c>gap: true</c>; the downtime stays a
/// gap, nothing is invented.</item>
/// </list>
/// Thread-safe: one lock covers append, eviction and reads.
/// </summary>
public sealed class TelemetryRing : ITelemetryRing
{
    private readonly Lock _gate = new();
    private readonly LinkedList<NodeSample> _samples = new();
    private readonly TimeSpan _window;
    private readonly int _capacity;
    private long _lastSeq;

    /// <summary>Creates the ring for one agent boot.</summary>
    /// <param name="bootId">This boot's id (the same one the event bus uses).</param>
    /// <param name="options">Window and capacity.</param>
    public TelemetryRing(string bootId, IOptions<TelemetryOptions> options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootId);
        ArgumentNullException.ThrowIfNull(options);
        BootId = bootId;
        _window = options.Value.RingWindow > TimeSpan.Zero ? options.Value.RingWindow : Protocol.TelemetryRingWindow;
        _capacity = Math.Max(1, options.Value.RingCapacity);
    }

    /// <inheritdoc />
    public string BootId { get; }

    /// <inheritdoc />
    public NodeSample? Latest
    {
        get
        {
            lock (_gate)
            {
                return _samples.Last?.Value;
            }
        }
    }

    /// <summary>Number of samples currently kept.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <inheritdoc />
    public NodeSample Append(NodeSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        lock (_gate)
        {
            var stored = sample with { Seq = ++_lastSeq };
            _samples.AddLast(stored);

            var cutoff = stored.At - _window;
            while (_samples.First is { } first &&
                   (_samples.Count > _capacity || (first != _samples.Last && first.Value.At < cutoff)))
            {
                _samples.RemoveFirst();
            }

            return stored;
        }
    }

    /// <inheritdoc />
    public TelemetryPage Read(string? bootId, long? afterSeq, int max)
    {
        int limit = Math.Max(1, max);
        lock (_gate)
        {
            bool sameBoot = bootId is not null && string.Equals(bootId, BootId, StringComparison.Ordinal);
            if (bootId is not null && !sameBoot)
            {
                return Page(0, limit, gap: true);
            }

            if (!sameBoot || afterSeq is not { } cursor)
            {
                return Page(0, limit, gap: false);
            }

            if (_samples.First is not { } first || cursor >= _lastSeq)
            {
                // Up to date (or a cursor ahead of this boot, which only a confused caller sends): nothing new.
                return new TelemetryPage { BootId = BootId };
            }

            long oldest = first.Value.Seq;
            return cursor + 1 < oldest
                ? Page(0, limit, gap: true)
                : Page(cursor, limit, gap: false);
        }
    }

    private TelemetryPage Page(long afterSeq, int limit, bool gap)
    {
        var page = new List<NodeSample>(Math.Min(limit, _samples.Count));
        foreach (var sample in _samples)
        {
            if (sample.Seq <= afterSeq)
            {
                continue;
            }

            page.Add(sample);
            if (page.Count == limit)
            {
                break;
            }
        }

        return new TelemetryPage { BootId = BootId, Samples = page, Gap = gap };
    }
}
