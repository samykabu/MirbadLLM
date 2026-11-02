using System.Globalization;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.NodeRuntime;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// Apple GPU telemetry for the sampler on a Mac (docs/macos/implementation-plan.md M8, Phase 4): utilisation from IOKit's
/// <c>IOAccelerator</c> <c>PerformanceStatistics</c> ("Device Utilization %"), the chip and core count as the name. No
/// root, no <c>powermetrics</c>, so power, temperature and clock stay null ("—" on the pages). Memory stays null too: the
/// GPU uses the unified memory the host figures already show, and a GPU memory total would make the sampler call the
/// node discrete (<see cref="TelemetrySampler"/>, <see cref="DiscreteNodeMemoryReader"/>). The sample carries no UUID —
/// IOKit gives none, and a unified node's samples are only read as the single-GPU view.
/// </summary>
public sealed partial class AppleGpuTelemetryProbe(Func<IReadOnlyList<IoAcceleratorReading>?> read, ILogger<AppleGpuTelemetryProbe> logger)
    : IGpuTelemetryProbe
{
    /// <summary>IOKit's busy figure for the whole GPU (renderer and tiler are parts of it).</summary>
    public const string UtilizationKey = "Device Utilization %";

    private volatile GpuTelemetryReading? _last;
    private GpuProbeStatus? _loggedStatus;

    /// <inheritdoc />
    public GpuTelemetryReading? Last => _last;

    /// <inheritdoc />
    public Task<GpuTelemetryReading> ReadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        GpuTelemetryReading reading;
        try
        {
            reading = ToReading(read());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.MarshalDirectiveException)
        {
            reading = new GpuTelemetryReading(GpuProbeStatus.Failed, [], $"IOKit could not be read: {ex.Message}");
        }

        LogTransition(reading);
        _last = reading;
        return Task.FromResult(reading);
    }

    /// <summary>
    /// The samples of an IOKit read: one per accelerator in IOKit's order. Null (the query failed) is
    /// <see cref="GpuProbeStatus.Failed"/>; no accelerator at all is <see cref="GpuProbeStatus.NotInstalled"/>, like a
    /// node without <c>nvidia-smi</c>. A utilisation outside 0–100 is dropped rather than shown.
    /// </summary>
    public static GpuTelemetryReading ToReading(IReadOnlyList<IoAcceleratorReading>? accelerators)
    {
        if (accelerators is null)
        {
            return new GpuTelemetryReading(GpuProbeStatus.Failed, [], "IOKit did not list the GPU (IOAccelerator).");
        }

        if (accelerators.Count == 0)
        {
            return new GpuTelemetryReading(GpuProbeStatus.NotInstalled, []);
        }

        var gpus = accelerators.Select((a, index) => new GpuSample
        {
            Index = index,
            UtilizationPercent = a.Statistics.TryGetValue(UtilizationKey, out long busy) && busy is >= 0 and <= 100 ? busy : null,
            Name = NameOf(a),
        }).ToList();
        return new GpuTelemetryReading(GpuProbeStatus.Ok, gpus);
    }

    /// <summary><c>Apple M2 GPU (10 cores)</c>, or as much of it as IOKit gave.</summary>
    public static string NameOf(IoAcceleratorReading accelerator)
    {
        ArgumentNullException.ThrowIfNull(accelerator);
        string model = string.IsNullOrWhiteSpace(accelerator.Model) ? "Apple" : accelerator.Model.Trim();
        return accelerator.CoreCount is { } cores and > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{model} GPU ({cores} cores)")
            : $"{model} GPU";
    }

    /// <summary>The probe wired to this Mac's IOKit (registered on macOS only; anywhere else every read fails).</summary>
    public static AppleGpuTelemetryProbe ForThisMac(ILogger<AppleGpuTelemetryProbe> logger) =>
        new(() => OperatingSystem.IsMacOS() ? MacIoAccelerator.Read() : null, logger);

    private void LogTransition(GpuTelemetryReading reading)
    {
        if (_loggedStatus == reading.Status)
        {
            return;
        }

        _loggedStatus = reading.Status;
        switch (reading.Status)
        {
            case GpuProbeStatus.NotInstalled:
                LogNoGpu(logger);
                break;
            case GpuProbeStatus.Failed:
                LogFailed(logger, reading.Error ?? "");
                break;
            default:
                LogReading(logger, reading.Gpus.Count);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "IOKit lists no GPU (IOAccelerator); samples carry no GPU telemetry.")]
    private static partial void LogNoGpu(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Apple GPU telemetry fails; the node reports Degraded until it reads again: {Error}")]
    private static partial void LogFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Apple GPU telemetry from IOKit: {Count} GPU(s).")]
    private static partial void LogReading(ILogger logger, int count);
}
