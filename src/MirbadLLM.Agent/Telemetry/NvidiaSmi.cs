using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>Outcome of one <c>nvidia-smi</c> run.</summary>
/// <param name="Found">False when the binary is not installed (a CPU-only node: not a failure).</param>
/// <param name="ExitCode">Exit code; -1 when it did not finish (timeout) or did not start.</param>
/// <param name="Output">Standard output.</param>
/// <param name="Error">Standard error, or why the run failed.</param>
public sealed record NvidiaSmiResult(bool Found, int ExitCode, string Output, string Error)
{
    /// <summary>The binary is missing.</summary>
    public static NvidiaSmiResult NotFound { get; } = new(false, -1, "", "nvidia-smi is not installed.");

    /// <summary>True when it ran and exited 0.</summary>
    public bool Succeeded => Found && ExitCode == 0;
}

/// <summary>Runs <c>nvidia-smi</c> with arguments. Seam for tests; the real one is <see cref="NvidiaSmiProcessRunner"/>.</summary>
public interface INvidiaSmiRunner
{
    /// <summary>Runs <c>nvidia-smi {arguments}</c>. Never throws except for cancellation of <paramref name="ct"/>.</summary>
    Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default);
}

/// <summary>
/// Runs the real <c>nvidia-smi</c> (as today's <c>NvidiaSmiGpuProbe</c>): 5 s timeout, the process tree is killed when
/// it does not finish. A missing binary is <see cref="NvidiaSmiResult.NotFound"/>, not an error. On Windows the binary is
/// looked up on <c>PATH</c> (DCH drivers install it in <c>System32</c>) and then in the older drivers' fixed folder
/// <c>%ProgramFiles%\NVIDIA Corporation\NVSMI</c> (MN-104).
/// </summary>
public sealed class NvidiaSmiProcessRunner : INvidiaSmiRunner
{
    private const string Executable = "nvidia-smi";

    /// <summary>
    /// The executables to try in order: <c>nvidia-smi</c> on <c>PATH</c>, then, on Windows, the pre-DCH install folder under
    /// <paramref name="programFiles"/> (the 64-bit Program Files). Pure, for tests.
    /// </summary>
    public static IReadOnlyList<string> Candidates(bool windows, string? programFiles) =>
        windows && !string.IsNullOrWhiteSpace(programFiles)
            ? [Executable, Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")]
            : [Executable];

    /// <summary>Per-run timeout.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default)
    {
        Process? process = null;
        try
        {
            process = StartFirstFound(arguments);
            if (process is null)
            {
                return NvidiaSmiResult.NotFound;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Timeout);

            var stdout = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            string output = await stdout.ConfigureAwait(false);
            string error = await stderr.ConfigureAwait(false);
            return new NvidiaSmiResult(true, process.ExitCode, output, error.Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new NvidiaSmiResult(true, -1, "", string.Create(CultureInfo.InvariantCulture,
                $"nvidia-smi did not answer within {Timeout.TotalSeconds:0} s."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new NvidiaSmiResult(true, -1, "", ex.Message);
        }
        finally
        {
            TryKill(process);
            process?.Dispose();
        }
    }

    private static Process? StartFirstFound(string arguments)
    {
        var candidates = Candidates(OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("ProgramW6432")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        foreach (string executable in candidates)
        {
            if (Path.IsPathRooted(executable) && !File.Exists(executable))
            {
                continue;
            }

            var startInfo = new ProcessStartInfo(executable, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            try
            {
                if (Process.Start(startInfo) is { } process)
                {
                    return process;
                }
            }
            catch (Win32Exception)
            {
                // Not found (or not executable): try the next candidate.
            }
        }

        return null;
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
        catch (Win32Exception)
        {
        }
    }
}

/// <summary>Parsing helpers for <c>nvidia-smi --format=csv,noheader,nounits</c> output.</summary>
public static class NvidiaSmiCsv
{
    private const double MiBPerGiB = 1024;

    /// <summary>Non-empty lines split on commas, fields trimmed.</summary>
    public static IEnumerable<string[]> Rows(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length > 0)
            {
                yield return line.Split(',', StringSplitOptions.TrimEntries);
            }
        }
    }

    /// <summary>A number; null for empty, <c>N/A</c>, <c>[N/A]</c>, <c>[Not Supported]</c> or anything unparsable.</summary>
    public static double? Number(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        string trimmed = field.Trim();
        return trimmed.Length == 0 || trimmed[0] == '[' || string.Equals(trimmed, "N/A", StringComparison.OrdinalIgnoreCase)
            ? null
            : double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;
    }

    /// <summary>A MiB field as GiB (MiB ÷ 1024); null when not reported.</summary>
    public static double? GiB(string field) => Number(field) is { } mib ? mib / MiBPerGiB : null;

    /// <summary>A text field; null for empty or <c>N/A</c>-style values.</summary>
    public static string? Text(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        string trimmed = field.Trim();
        return trimmed.Length == 0 || trimmed[0] == '[' || string.Equals(trimmed, "N/A", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }
}
