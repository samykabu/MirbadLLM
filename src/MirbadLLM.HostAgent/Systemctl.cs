using System.Globalization;

namespace MirbadLLM.HostAgent;

internal enum UnitAction
{
    Start,
    Stop,
    Restart,
}

/// <summary>
/// Builds systemctl argument vectors. The unit name always comes from the allowlist (never from a request),
/// sits after <c>--</c> so it can never be read as an option, and every argument is a separate element.
/// </summary>
internal static class SystemctlCommands
{
    public const string SystemctlPath = "/usr/bin/systemctl";
    public const string NvidiaSmiPath = "/usr/bin/nvidia-smi";

    public const string ShowProperties = "Id,ActiveState,SubState,UnitFileState,MainPID,ActiveEnterTimestamp,MemoryCurrent,ControlGroup";

    /// <summary>User units are reached through the user's own manager: <c>systemctl --user -M dgxuser@ …</c>.</summary>
    public static IReadOnlyList<string> ScopeArgs(AllowedUnit unit) =>
        unit.Scope == UnitScope.User ? ["--user", "-M", $"{unit.User}@"] : [];

    public static IReadOnlyList<string> Show(AllowedUnit unit) =>
        [.. ScopeArgs(unit), "show", "--no-pager", "--timestamp=unix", $"--property={ShowProperties}", "--", unit.Unit];

    /// <summary><c>--no-block</c>: queue the job and return; the caller polls status to watch it finish.</summary>
    public static IReadOnlyList<string> Action(AllowedUnit unit, UnitAction action) =>
        [.. ScopeArgs(unit), Verb(action), "--no-block", "--no-ask-password", "--", unit.Unit];

    public static IReadOnlyList<string> Autostart(AllowedUnit unit, bool enabled) =>
        [.. ScopeArgs(unit), enabled ? "enable" : "disable", "--no-ask-password", "--", unit.Unit];

    /// <summary>Properties read for every discovered unit (one batched <c>systemctl show</c> per scope).</summary>
    public const string DiscoveryProperties = "Id,Description,LoadState,ActiveState,SubState,UnitFileState,FragmentPath,MainPID,ActiveEnterTimestamp,MemoryCurrent,ControlGroup";

    public static IReadOnlyList<string> DiscoveryScopeArgs(UnitScope scope, string? user) =>
        scope == UnitScope.User ? ["--user", "-M", $"{user}@"] : [];

    public static IReadOnlyList<string> ListUnits(UnitScope scope, string? user) =>
        [.. DiscoveryScopeArgs(scope, user), "list-units", "--type=service", "--all", "--no-pager", "--output=json"];

    public static IReadOnlyList<string> ListUnitFiles(UnitScope scope, string? user) =>
        [.. DiscoveryScopeArgs(scope, user), "list-unit-files", "--type=service", "--no-pager", "--output=json"];

    /// <summary>Batched show. The unit names come from systemctl's own listings and are validated; they sit after <c>--</c>.</summary>
    public static IReadOnlyList<string> ShowMany(UnitScope scope, string? user, IEnumerable<string> units) =>
        [.. DiscoveryScopeArgs(scope, user), "show", "--no-pager", "--timestamp=unix", $"--property={DiscoveryProperties}", "--", .. units];

    public static IReadOnlyList<string> GpuApps() =>
        ["--query-compute-apps=pid,used_memory", "--format=csv,noheader,nounits"];

    public static string Verb(UnitAction action) => action switch
    {
        UnitAction.Start => "start",
        UnitAction.Stop => "stop",
        UnitAction.Restart => "restart",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static bool TryParseAction(string? text, out UnitAction action)
    {
        switch (text)
        {
            case "start":
                action = UnitAction.Start;
                return true;
            case "stop":
                action = UnitAction.Stop;
                return true;
            case "restart":
                action = UnitAction.Restart;
                return true;
            default:
                action = default;
                return false;
        }
    }
}

/// <summary>The properties of one unit from <c>systemctl show --timestamp=unix</c>.</summary>
internal sealed record UnitProperties(
    string ActiveState,
    string SubState,
    string UnitFileState,
    int? MainPid,
    DateTimeOffset? ActiveSince,
    long? MemoryBytes,
    string? ControlGroup);

internal static class UnitStatusParser
{
    /// <summary>Parses <c>Key=Value</c> lines. Unset or sentinel values (<c>[not set]</c>, UINT64_MAX, empty) become null.</summary>
    public static UnitProperties Parse(string output) => Parse(ParseValues(output));

    /// <summary><c>Key=Value</c> lines into a dictionary; the first occurrence of a key wins.</summary>
    public static Dictionary<string, string> ParseValues(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            int eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            values.TryAdd(line[..eq], line[(eq + 1)..]);
        }

        return values;
    }

    /// <summary>A multi-unit <c>systemctl show</c> dump (blocks separated by a blank line), keyed by each block's <c>Id</c>.</summary>
    public static Dictionary<string, Dictionary<string, string>> ParseBlocks(string output)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (string block in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n"))
        {
            var values = ParseValues(block);
            if (values.TryGetValue("Id", out string? id) && id.Length > 0)
            {
                result.TryAdd(id, values);
            }
        }

        return result;
    }

    public static UnitProperties Parse(IReadOnlyDictionary<string, string> values)
    {
        string Get(string key) => values.TryGetValue(key, out string? v) ? v.Trim() : "";

        int? mainPid = int.TryParse(Get("MainPID"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0 ? pid : null;

        DateTimeOffset? since = null;
        string ts = Get("ActiveEnterTimestamp");
        if (ts.StartsWith('@') && long.TryParse(ts.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) && seconds > 0)
        {
            since = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        long? memory = ulong.TryParse(Get("MemoryCurrent"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong mem) && mem != ulong.MaxValue
            ? (long)Math.Min(mem, long.MaxValue)
            : null;

        string cgroup = Get("ControlGroup");

        return new UnitProperties(
            Get("ActiveState") is { Length: > 0 } a ? a : "unknown",
            Get("SubState"),
            Get("UnitFileState"),
            mainPid,
            since,
            memory,
            cgroup.Length == 0 ? null : cgroup);
    }
}

/// <summary>Maps <c>nvidia-smi</c> compute processes to units through each PID's cgroup.</summary>
internal static class GpuCgroupMapper
{
    private const long BytesPerMiB = 1024 * 1024;

    /// <summary>Parses <c>pid, used_memory</c> CSV (MiB, no header, no units) into PID → bytes. Unparseable lines are skipped.</summary>
    public static IReadOnlyDictionary<int, long> ParseComputeApps(string output)
    {
        var result = new Dictionary<int, long>();
        foreach (string raw in output.Split('\n'))
        {
            string[] fields = raw.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 2
                || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long mib))
            {
                continue;
            }

            result[pid] = result.GetValueOrDefault(pid) + (mib * BytesPerMiB);
        }

        return result;
    }

    /// <summary>The cgroup v2 path from a <c>/proc/&lt;pid&gt;/cgroup</c> file (the <c>0::</c> line).</summary>
    public static string? ParseCgroupFile(string content)
    {
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("0::", StringComparison.Ordinal))
            {
                return line[3..];
            }
        }

        return null;
    }

    /// <summary>
    /// Sum of GPU memory of processes inside <paramref name="unitCgroup"/> (the unit's cgroup or any child of it):
    /// 0 when none of them uses the GPU, null when the unit has no cgroup (not running).
    /// </summary>
    public static long? SumForCgroup(string? unitCgroup, IReadOnlyDictionary<int, long> gpuByPid, ICgroupReader reader)
    {
        if (string.IsNullOrEmpty(unitCgroup) || unitCgroup == "/")
        {
            return null;
        }

        long total = 0;
        foreach (var (pid, bytes) in gpuByPid)
        {
            string? path = reader.Read(pid);
            if (path is null)
            {
                continue;
            }

            if (path == unitCgroup || path.StartsWith(unitCgroup + "/", StringComparison.Ordinal))
            {
                total += bytes;
            }
        }

        return total;
    }
}
