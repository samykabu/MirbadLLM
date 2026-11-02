using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// Reads GPU telemetry by shelling out to <c>nvidia-smi</c>. Null (not thrown) when the binary is missing,
/// times out, or exits non-zero &mdash; monitoring must keep working on hosts without an NVIDIA driver.
/// </summary>
public sealed class NvidiaSmiGpuProbe(ILogger<NvidiaSmiGpuProbe> logger) : IGpuProbe
{
    private const string Executable = "nvidia-smi";
    private const string Arguments = "--query-gpu=utilization.gpu,power.draw,temperature.gpu,clocks.sm --format=csv,noheader,nounits";
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(5);

    private volatile bool _loggedUnavailable;

    public async Task<GpuSnapshot?> GetAsync(CancellationToken ct = default)
    {
        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo(Executable, Arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            process = Process.Start(startInfo);
            if (process is null)
            {
                LogUnavailableOnce();
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(ProcessTimeout);

            string output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                LogUnavailableOnce();
                return null;
            }

            return ParseLine(output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            LogUnavailableOnce();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUnavailableOnce(ex);
            return null;
        }
        finally
        {
            TryKill(process);
            process?.Dispose();
        }
    }

    /// <summary>
    /// Parses the first CSV line of <c>nvidia-smi --query-gpu=... --format=csv,noheader,nounits</c> output
    /// (e.g. <c>0, 10.17, 44, 2411</c>). <c>N/A</c>/<c>[N/A]</c> fields, and fields that fail to parse,
    /// become null.
    /// </summary>
    public static GpuSnapshot? ParseLine(string output)
    {
        string? firstLine = output
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

        if (firstLine is null)
        {
            return null;
        }

        string[] fields = firstLine.Split(',');
        if (fields.Length < 4)
        {
            return null;
        }

        return new GpuSnapshot(
            ParseField(fields[0]),
            ParseField(fields[1]),
            ParseField(fields[2]),
            ParseField(fields[3]));
    }

    private static double? ParseField(string field)
    {
        string trimmed = field.Trim();
        if (trimmed.Length == 0
            || string.Equals(trimmed, "N/A", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "[N/A]", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    }

    private void LogUnavailableOnce(Exception? exception = null)
    {
        if (_loggedUnavailable)
        {
            return;
        }

        _loggedUnavailable = true;
        if (exception is null)
        {
            NodeRuntimeLog.GpuProbeUnavailable(logger);
        }
        else
        {
            NodeRuntimeLog.GpuProbeUnavailableWithException(logger, exception);
        }
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
