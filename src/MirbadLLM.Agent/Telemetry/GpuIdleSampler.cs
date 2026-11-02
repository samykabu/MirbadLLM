using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// <c>GET /gpu/idle</c> (design §4.3, §6.5): samples GPU utilization for a few seconds before a benchmark, as today's
/// <c>ModelJobService.SampleGpuIdleAsync</c> does (one read per <see cref="TelemetryOptions.GpuIdleSampleInterval"/>,
/// <c>round(seconds / interval)</c> reads, a failed read simply adds nothing). The average is over the GPUs asked for
/// (all when none are named); each GPU's average is over its own readings.
/// </summary>
public sealed class GpuIdleSampler(IGpuTelemetryProbe probe, IOptions<TelemetryOptions> options, TimeProvider timeProvider)
{
    private readonly TelemetryOptions _options = options.Value;

    /// <summary>Samples for <paramref name="seconds"/> seconds.</summary>
    /// <param name="seconds">Window, 1 … <see cref="TelemetryOptions.GpuIdleMaxSeconds"/>.</param>
    /// <param name="gpus">GPU indices to include; null or empty = all.</param>
    /// <param name="ct">Cancelled when the caller goes away.</param>
    public async Task<GpuIdleResponse> SampleAsync(int seconds, IReadOnlyList<int>? gpus, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(seconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, _options.GpuIdleMaxSeconds);

        var interval = _options.GpuIdleSampleInterval > TimeSpan.Zero ? _options.GpuIdleSampleInterval : TimeSpan.FromSeconds(1);
        int reads = Math.Max(1, (int)Math.Round(TimeSpan.FromSeconds(seconds) / interval));
        var wanted = gpus is { Count: > 0 } ? new HashSet<int>(gpus) : null;

        var perGpu = new SortedDictionary<int, (string? Uuid, List<double> Values)>();
        int samples = 0;
        for (int i = 0; i < reads; i++)
        {
            if (i > 0)
            {
                await Task.Delay(interval, timeProvider, ct).ConfigureAwait(false);
            }

            GpuTelemetryReading reading;
            try
            {
                reading = await probe.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            bool any = false;
            foreach (var gpu in reading.Gpus)
            {
                if (wanted is not null && !wanted.Contains(gpu.Index))
                {
                    continue;
                }

                if (!perGpu.TryGetValue(gpu.Index, out var entry))
                {
                    entry = (gpu.Uuid, []);
                    perGpu[gpu.Index] = entry;
                }

                if (gpu.UtilizationPercent is { } utilization)
                {
                    entry.Values.Add(utilization);
                    any = true;
                }
            }

            if (any)
            {
                samples++;
            }
        }

        var result = perGpu
            .Select(kv => new GpuIdleSample(kv.Key, kv.Value.Uuid,
                kv.Value.Values.Count > 0 ? kv.Value.Values.Average() : null, kv.Value.Values.Count)
            {
                MaxPercent = kv.Value.Values.Count > 0 ? kv.Value.Values.Max() : null,
            })
            .ToList();
        var averages = result.Where(g => g.AveragePercent is not null).Select(g => g.AveragePercent!.Value).ToList();
        var maxima = result.Where(g => g.MaxPercent is not null).Select(g => g.MaxPercent!.Value).ToList();

        return new GpuIdleResponse
        {
            AveragePercent = averages.Count > 0 ? averages.Average() : null,
            MaxPercent = maxima.Count > 0 ? maxima.Max() : null,
            Samples = samples,
            PerGpu = result,
        };
    }
}
