using Microsoft.Extensions.Logging;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>How the last GPU read went.</summary>
public enum GpuProbeStatus
{
    /// <summary>Read (possibly zero GPUs listed).</summary>
    Ok,

    /// <summary>No <c>nvidia-smi</c> on the node: a CPU-only or non-NVIDIA node, healthy.</summary>
    NotInstalled,

    /// <summary><c>nvidia-smi</c> exists but failed (non-zero exit, timeout, unparsable output): Degraded (§3.5).</summary>
    Failed,
}

/// <summary>One per-GPU telemetry read.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="Gpus">Per-GPU values in index order; empty unless <see cref="GpuProbeStatus.Ok"/>.</param>
/// <param name="Error">Why it failed, for <see cref="GpuProbeStatus.Failed"/>.</param>
public sealed record GpuTelemetryReading(GpuProbeStatus Status, IReadOnlyList<GpuSample> Gpus, string? Error = null);

/// <summary>Per-GPU telemetry for the sampler and <c>/gpu/idle</c> (design §8.1).</summary>
public interface IGpuTelemetryProbe
{
    /// <summary>Reads every GPU now. Never throws except for cancellation.</summary>
    Task<GpuTelemetryReading> ReadAsync(CancellationToken ct = default);

    /// <summary>The last read, or null before the first (the health contributor reuses it).</summary>
    GpuTelemetryReading? Last { get; }
}

/// <summary>
/// Per-GPU telemetry from <c>nvidia-smi --query-gpu</c>: utilization, power, temperature, SM clock, product name and, on
/// discrete GPUs, VRAM used/total/free (GB10 reports <c>[N/A]</c> memory, which becomes null). Extends today's single-GPU
/// <c>NvidiaSmiGpuProbe</c> with index and UUID so samples carry every GPU. The same query works on Linux and Windows
/// (CRLF output, WDDM's <c>[N/A]</c> fields).
/// </summary>
public sealed partial class NvidiaSmiGpuTelemetryProbe(INvidiaSmiRunner runner, ILogger<NvidiaSmiGpuTelemetryProbe> logger) : IGpuTelemetryProbe
{
    /// <summary>
    /// The query, in the column order <see cref="Parse"/> expects. <c>memory.free</c> and <c>name</c> (MN-104) come last so
    /// the first eight columns keep P1's order; <c>name</c> is last because it is free text.
    /// </summary>
    public const string Arguments =
        "--query-gpu=index,uuid,utilization.gpu,power.draw,temperature.gpu,clocks.sm,memory.used,memory.total,memory.free,name --format=csv,noheader,nounits";

    /// <summary>Columns every row must have (P1's eight); <c>memory.free</c> and <c>name</c> are read when present.</summary>
    private const int RequiredColumns = 8;
    private volatile GpuTelemetryReading? _last;
    private GpuProbeStatus? _loggedStatus;

    /// <inheritdoc />
    public GpuTelemetryReading? Last => _last;

    /// <inheritdoc />
    public async Task<GpuTelemetryReading> ReadAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Arguments, ct).ConfigureAwait(false);
        GpuTelemetryReading reading;
        if (!result.Found)
        {
            reading = new GpuTelemetryReading(GpuProbeStatus.NotInstalled, []);
        }
        else if (!result.Succeeded)
        {
            reading = new GpuTelemetryReading(GpuProbeStatus.Failed, [], Describe(result));
        }
        else
        {
            var gpus = Parse(result.Output);
            reading = gpus is null
                ? new GpuTelemetryReading(GpuProbeStatus.Failed, [], "nvidia-smi returned output that could not be read.")
                : new GpuTelemetryReading(GpuProbeStatus.Ok, gpus);
        }

        LogTransition(reading);
        _last = reading;
        return reading;
    }

    /// <summary>
    /// Parses the CSV of <see cref="Arguments"/> (one line per GPU). Null when a line has too few columns or no index
    /// (the output is not what was asked for); missing values become null. Rows with only P1's eight columns parse with
    /// a null <see cref="GpuSample.MemoryFreeGiB"/> and <see cref="GpuSample.Name"/>; a name containing commas is kept whole.
    /// </summary>
    public static IReadOnlyList<GpuSample>? Parse(string output)
    {
        var gpus = new List<GpuSample>();
        foreach (var row in NvidiaSmiCsv.Rows(output))
        {
            if (row.Length < RequiredColumns || NvidiaSmiCsv.Number(row[0]) is not { } index)
            {
                return null;
            }

            gpus.Add(new GpuSample
            {
                Index = (int)index,
                Uuid = NvidiaSmiCsv.Text(row[1]),
                UtilizationPercent = NvidiaSmiCsv.Number(row[2]),
                PowerWatts = NvidiaSmiCsv.Number(row[3]),
                TemperatureC = NvidiaSmiCsv.Number(row[4]),
                SmClockMHz = NvidiaSmiCsv.Number(row[5]),
                MemoryUsedGiB = NvidiaSmiCsv.GiB(row[6]),
                MemoryTotalGiB = NvidiaSmiCsv.GiB(row[7]),
                MemoryFreeGiB = row.Length > 8 ? NvidiaSmiCsv.GiB(row[8]) : null,
                Name = row.Length > 9 ? NvidiaSmiCsv.Text(string.Join(", ", row[9..])) : null,
            });
        }

        return gpus.OrderBy(g => g.Index).ToList();
    }

    internal static string Describe(NvidiaSmiResult result)
    {
        string detail = result.Error.Length > 0 ? result.Error : result.Output.Trim();
        if (detail.Length > 300)
        {
            detail = detail[..300];
        }

        return result.ExitCode >= 0
            ? $"nvidia-smi exited with code {result.ExitCode}: {detail}"
            : detail;
    }

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
                LogNotInstalled(logger);
                break;
            case GpuProbeStatus.Failed:
                LogFailed(logger, reading.Error ?? "");
                break;
            default:
                LogRecovered(logger, reading.Gpus.Count);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "nvidia-smi is not installed; samples carry no GPU telemetry.")]
    private static partial void LogNotInstalled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "nvidia-smi fails; the node reports Degraded until it answers again: {Error}")]
    private static partial void LogFailed(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "nvidia-smi answers; {Count} GPU(s) in telemetry.")]
    private static partial void LogRecovered(ILogger logger, int count);
}
