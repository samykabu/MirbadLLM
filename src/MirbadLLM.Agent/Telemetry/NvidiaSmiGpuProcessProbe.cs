using Microsoft.Extensions.Logging;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Telemetry;

/// <summary>
/// GPU processes on a node without hostd (the Windows PC, design-p3p4 §A.4.3, MN-214): <c>nvidia-smi
/// --query-compute-apps</c>, each row mapped to a model through the PIDs in <c>processes.json</c>
/// (<see cref="IProcessRecordStore"/>). The <see cref="IGpuProcessProbe"/> the sampler uses there instead of
/// <see cref="HostdGpuProcessProbe"/> (MN-232 registers it).
/// <para>Rows (<see cref="Parse"/>): a model's process becomes <c>GpuProcess(pid, file name, MiB, ContainerId: null,
/// Unit: null, DisplayName: model id, GpuUuid)</c>; any other process (the desktop: <c>dwm.exe</c>, a browser, a game,
/// the owner's own <c>llama-server.exe</c>) is listed by its file name with a null <see cref="GpuProcess.DisplayName"/>.
/// Under WDDM <c>nvidia-smi</c> reports <c>[N/A]</c> memory for every process ([R11]): such a row carries
/// <see cref="GpuProcess.UsedMiB"/> 0, which here means "not reported", never a measurement (the frozen record has no
/// nullable memory); per-model memory on Windows is the "measured at start" delta (<see cref="NvidiaSmiGpuStartMeter"/>),
/// and the external use is the GPU's <c>memory.used</c> minus those deltas (<see cref="ExternalGpuUseContributor"/>). Only
/// the file name of a process is reported, not its path (paths under a user profile name the user).</para>
/// <para>Unavailable (null) when <c>nvidia-smi</c> is missing or fails or its output cannot be read; a failing record
/// store only loses the model mapping (every row is then listed as not mirbadllm's).</para>
/// </summary>
public sealed partial class NvidiaSmiGpuProcessProbe(
    INvidiaSmiRunner runner,
    IProcessRecordStore records,
    ILogger<NvidiaSmiGpuProcessProbe> logger) : IGpuProcessProbe
{
    /// <summary>
    /// The query. <c>used_memory</c> is read so a driver that reports it (TCC, Linux) is shown; <c>process_name</c> is
    /// last because it is a path, which may contain commas.
    /// </summary>
    public const string Arguments = "--query-compute-apps=gpu_uuid,pid,used_memory,process_name --format=csv,noheader,nounits";

    private const int RequiredColumns = 4;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GpuProcess>?> GetAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Arguments, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }

        IReadOnlyDictionary<int, string> models;
        try
        {
            models = ModelsByPid(await records.ListAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecordsUnreadable(logger, ex);
            models = new Dictionary<int, string>();
        }

        return Parse(result.Output, models);
    }

    /// <summary>
    /// Parses the CSV of <see cref="Arguments"/> (CRLF or LF; <c>[N/A]</c> memory = 0, see the class remarks), naming a
    /// row's model when its PID is in <paramref name="modelsByPid"/>. Null when a row has too few columns or no PID (not
    /// the output that was asked for). Empty output (no compute process) is an empty list.
    /// </summary>
    public static IReadOnlyList<GpuProcess>? Parse(string output, IReadOnlyDictionary<int, string> modelsByPid)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(modelsByPid);
        var processes = new List<GpuProcess>();
        foreach (var row in NvidiaSmiCsv.Rows(output))
        {
            if (row.Length < RequiredColumns || NvidiaSmiCsv.Number(row[1]) is not { } pidValue ||
                pidValue is < 0 or > int.MaxValue || pidValue != Math.Floor(pidValue))
            {
                return null;
            }

            int pid = (int)pidValue;
            long usedMiB = NvidiaSmiCsv.Number(row[2]) is { } mib and >= 0 ? (long)Math.Round(mib, MidpointRounding.AwayFromZero) : 0;
            string? path = NvidiaSmiCsv.Text(string.Join(",", row[3..]));
            processes.Add(new GpuProcess(
                pid,
                FileName(path) ?? "unknown",
                usedMiB,
                ContainerId: null,
                Unit: null,
                DisplayName: modelsByPid.GetValueOrDefault(pid),
                GpuUuid: NvidiaSmiCsv.Text(row[0])));
        }

        return processes;
    }

    /// <summary>PID → model id of every record (the instance's current process).</summary>
    public static IReadOnlyDictionary<int, string> ModelsByPid(IEnumerable<ProcessRecord> processRecords)
    {
        ArgumentNullException.ThrowIfNull(processRecords);
        var map = new Dictionary<int, string>();
        foreach (var record in processRecords)
        {
            map.TryAdd(record.Process.Pid, record.ModelId);
        }

        return map;
    }

    /// <summary>The last segment of a Windows or POSIX path, independent of the OS the agent runs on.</summary>
    internal static string? FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string trimmed = path.Trim().TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(['\\', '/']);
        string name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.Length == 0 ? null : name;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "processes.json could not be read; GPU processes are listed without their models.")]
    private static partial void LogRecordsUnreadable(ILogger logger, Exception exception);
}
