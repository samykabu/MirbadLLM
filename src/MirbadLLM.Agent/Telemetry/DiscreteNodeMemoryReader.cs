using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// <see cref="INodeMemoryReader"/> with per-GPU memory (design §4.3 <c>NodeMemorySnapshot</c>, §6.1 discrete; MN-104):
/// the host pool from <see cref="IMemoryProbe"/> plus, on a discrete node, every GPU's VRAM total/used/free from
/// <see cref="IGpuTelemetryProbe"/> (<c>nvidia-smi … memory.used,memory.total,memory.free</c>). Free is
/// <c>memory.free</c> as the driver reports it (it excludes the driver's reserved VRAM, so it is less than total − used);
/// total − used only when the driver gives no free value.
/// <para><b>Unified nodes keep P1 exactly</b> (D66): when no GPU reports its own VRAM (the GB10's <c>[N/A]</c>), or before
/// the first successful GPU read, the snapshot is <c>{model: unified, host…, gpus: []}</c>, byte-identical to
/// <see cref="ProbeNodeMemoryReader"/> with no GPUs, so the composition (MN-130) can register this reader on every node.</para>
/// <para>GPU reads are reused for <see cref="MaxGpuAge"/> (measured on the <see cref="TimeProvider"/>) so a burst of
/// <c>/status</c> and admission reads runs <c>nvidia-smi</c> once. When <c>nvidia-smi</c> fails the last known GPUs are
/// returned (the contract says "return the last known value"); a failing host probe returns the last snapshot when there
/// is one and throws otherwise.</para>
/// </summary>
public sealed class DiscreteNodeMemoryReader : INodeMemoryReader, IDisposable
{
    /// <summary>Default <see cref="MaxGpuAge"/>: shorter than the 5 s sampler interval, long enough to absorb a burst.</summary>
    public static readonly TimeSpan DefaultMaxGpuAge = TimeSpan.FromSeconds(1);

    private readonly IMemoryProbe _host;
    private readonly IGpuTelemetryProbe _gpus;
    private readonly TimeProvider _time;
    private readonly MemoryModel? _forcedModel;
    private readonly SemaphoreSlim _gpuGate = new(1, 1);
    private GpuView? _gpuView;
    private long _gpuReadAt;
    private NodeMemorySnapshot? _last;

    /// <param name="host">Host memory probe (<c>/proc/meminfo</c> on Linux).</param>
    /// <param name="gpus">Per-GPU telemetry probe.</param>
    /// <param name="time">Clock for <see cref="MaxGpuAge"/>.</param>
    /// <param name="forcedModel">Forces the memory model (<see cref="TelemetryOptions.MemoryModel"/>); null = derived from the GPUs.</param>
    /// <param name="maxGpuAge">How long a GPU read is reused; null = <see cref="DefaultMaxGpuAge"/>.</param>
    public DiscreteNodeMemoryReader(IMemoryProbe host, IGpuTelemetryProbe gpus, TimeProvider time, MemoryModel? forcedModel = null, TimeSpan? maxGpuAge = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _gpus = gpus ?? throw new ArgumentNullException(nameof(gpus));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _forcedModel = forcedModel;
        MaxGpuAge = maxGpuAge ?? DefaultMaxGpuAge;
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxGpuAge, TimeSpan.Zero, nameof(maxGpuAge));
    }

    /// <summary>How long a GPU read is reused before <c>nvidia-smi</c> runs again.</summary>
    public TimeSpan MaxGpuAge { get; }

    /// <inheritdoc/>
    public async Task<NodeMemorySnapshot> ReadAsync(CancellationToken ct = default)
    {
        MemorySnapshot host;
        try
        {
            host = await _host.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested && Volatile.Read(ref _last) is not null)
        {
            return Volatile.Read(ref _last)!;
        }

        var view = await ReadGpusAsync(ct).ConfigureAwait(false);
        var snapshot = new NodeMemorySnapshot
        {
            Model = view.Model,
            HostTotalGiB = host.TotalGiB,
            HostAvailableGiB = host.AvailableGiB,
            Gpus = view.Gpus,
        };
        Volatile.Write(ref _last, snapshot);
        return snapshot;
    }

    /// <inheritdoc/>
    public void Dispose() => _gpuGate.Dispose();

    /// <summary>
    /// Builds the GPU part from one telemetry read: the memory model and, on a discrete node, one entry per GPU with a
    /// UUID in index order. Unified (or no GPUs) → no entries, as in P1.
    /// </summary>
    public static (MemoryModel Model, IReadOnlyList<GpuMemorySnapshot> Gpus) FromSamples(IReadOnlyList<GpuSample> samples, MemoryModel? forcedModel = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var model = forcedModel ?? (samples.Any(g => g.MemoryTotalGiB is not null) ? MemoryModel.Discrete : MemoryModel.Unified);
        if (model == MemoryModel.Unified)
        {
            return (model, []);
        }

        IReadOnlyList<GpuMemorySnapshot> gpus =
        [
            .. samples
                .Where(g => g.Uuid is not null)
                .OrderBy(g => g.Index)
                .Select(g => new GpuMemorySnapshot(g.Index, g.Uuid!, g.MemoryTotalGiB, g.MemoryUsedGiB, FreeOf(g))),
        ];
        return (model, gpus);
    }

    /// <summary><c>memory.free</c> when reported, else total − used (never negative), else null.</summary>
    public static double? FreeOf(GpuSample gpu)
    {
        ArgumentNullException.ThrowIfNull(gpu);
        return gpu.MemoryFreeGiB is { } free
            ? Math.Max(0, free)
            : gpu.MemoryTotalGiB is { } total && gpu.MemoryUsedGiB is { } used ? Math.Max(0, total - used) : null;
    }

    private async Task<GpuView> ReadGpusAsync(CancellationToken ct)
    {
        await _gpuGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gpuView is { } fresh && _time.GetElapsedTime(_gpuReadAt) < MaxGpuAge)
            {
                return fresh;
            }

            GpuTelemetryReading reading;
            try
            {
                reading = await _gpus.ReadAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reading = new GpuTelemetryReading(GpuProbeStatus.Failed, [], ex.Message);
            }

            _gpuReadAt = _time.GetTimestamp();
            if (reading.Status == GpuProbeStatus.Ok)
            {
                var (model, gpus) = FromSamples(reading.Gpus, _forcedModel);
                _gpuView = new GpuView(model, gpus);
            }
            else if (_gpuView is null)
            {
                // Never read: P1's unified view (forced model honoured), without GPU entries. Not cached, so the next read asks again.
                return new GpuView(_forcedModel ?? MemoryModel.Unified, []);
            }

            return _gpuView;
        }
        finally
        {
            _gpuGate.Release();
        }
    }

    private sealed record GpuView(MemoryModel Model, IReadOnlyList<GpuMemorySnapshot> Gpus);
}
