using System.Globalization;
using Microsoft.Extensions.Configuration;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// Reads host memory from <c>/proc/meminfo</c> on Linux (the DGX Spark). On non-Linux dev boxes there is
/// no such file, so it returns a configurable fake instead (<c>MirbadLLM:DevMemoryTotalGiB</c>, default
/// 121; <c>MirbadLLM:DevMemoryAvailableGiB</c>, default 60).
/// </summary>
public sealed class ProcMeminfoMemoryProbe(IConfiguration configuration) : IMemoryProbe
{
    private const string MemInfoPath = "/proc/meminfo";
    private const double KiBPerGiB = 1024 * 1024;

    public async Task<MemorySnapshot> GetAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux() && File.Exists(MemInfoPath))
        {
            string text = await File.ReadAllTextAsync(MemInfoPath, ct).ConfigureAwait(false);
            return ParseMemInfo(text);
        }

        double totalGiB = configuration.GetValue("MirbadLLM:DevMemoryTotalGiB", 121.0);
        double availableGiB = configuration.GetValue("MirbadLLM:DevMemoryAvailableGiB", 60.0);
        return new MemorySnapshot(totalGiB, availableGiB);
    }

    /// <summary>Parses the <c>MemTotal</c> / <c>MemAvailable</c> lines (in kB) of a <c>/proc/meminfo</c> dump into GiB.</summary>
    public static MemorySnapshot ParseMemInfo(string text)
    {
        double? totalKiB = null;
        double? availableKiB = null;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                totalKiB = ParseKiBValue(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                availableKiB = ParseKiBValue(line);
            }
        }

        if (totalKiB is null || availableKiB is null)
        {
            throw new FormatException("Expected 'MemTotal:' and 'MemAvailable:' lines in /proc/meminfo.");
        }

        return new MemorySnapshot(totalKiB.Value / KiBPerGiB, availableKiB.Value / KiBPerGiB);
    }

    private static double ParseKiBValue(string line)
    {
        // Format: "MemTotal:       132271932 kB"
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out double value))
        {
            throw new FormatException($"Could not parse /proc/meminfo line: '{line}'.");
        }

        return value;
    }
}
