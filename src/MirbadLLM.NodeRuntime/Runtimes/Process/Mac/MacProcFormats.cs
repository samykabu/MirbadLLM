using System.Text;

namespace MirbadLLM.NodeRuntime.Runtimes.Process.Mac;

/// <summary>
/// The OS-neutral parts of the macOS process host (<see cref="MacProcessHost"/>, <see cref="MacNative"/>): the layouts of
/// <c>struct proc_bsdinfo</c> and <c>KERN_PROCARGS2</c>, and the <c>envp</c> block. Pure, so they are unit-tested on every OS.
/// </summary>
internal static class MacProcFormats
{
    /// <summary>What <c>proc_pidinfo(PROC_PIDTBSDINFO)</c> says about a process.</summary>
    /// <param name="Uid">Effective uid of the owner.</param>
    /// <param name="ProcessGroup">Process group id.</param>
    /// <param name="Start">Start time (microsecond precision, UTC).</param>
    /// <param name="Zombie">True when it has exited and waits to be reaped.</param>
    public readonly record struct BsdInfo(uint Uid, int ProcessGroup, DateTimeOffset Start, bool Zombie);

    /// <summary>
    /// Parses a <c>struct proc_bsdinfo</c>: <c>pbi_status</c> @4, <c>pbi_uid</c> @20, <c>pbi_pgid</c> @100,
    /// <c>pbi_start_tvsec</c> @120, <c>pbi_start_tvusec</c> @128 (little endian). OS-neutral, so it is unit-tested anywhere.
    /// </summary>
    internal static BsdInfo ParseBsdInfo(ReadOnlySpan<byte> info)
    {
        const uint SZOMB = 5;
        uint status = BitConverter.ToUInt32(info[4..8]);
        uint uid = BitConverter.ToUInt32(info[20..24]);
        int pgid = (int)BitConverter.ToUInt32(info[100..104]);
        long seconds = (long)BitConverter.ToUInt64(info[120..128]);
        long micros = (long)BitConverter.ToUInt64(info[128..136]);
        DateTimeOffset start = DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(micros * 10);
        return new BsdInfo(uid, pgid, start, status == SZOMB);
    }

    /// <summary>
    /// Parses a <c>KERN_PROCARGS2</c> buffer: <c>int argc</c>, the exec path and its NUL padding, then <c>argc</c>
    /// NUL-terminated strings (the environment follows and is not read). OS-neutral, unit-tested anywhere.
    /// </summary>
    internal static IReadOnlyList<string>? ParseProcArgs(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < sizeof(int))
        {
            return null;
        }

        int argc = BitConverter.ToInt32(buffer[..4]);
        int at = 4;
        int pathEnd = buffer[at..].IndexOf((byte)0);
        if (argc < 0 || pathEnd < 0)
        {
            return null;
        }

        at += pathEnd;
        while (at < buffer.Length && buffer[at] == 0)
        {
            at++;
        }

        var args = new List<string>(argc);
        for (int i = 0; i < argc; i++)
        {
            if (at >= buffer.Length)
            {
                return null;
            }

            int end = buffer[at..].IndexOf((byte)0);
            if (end < 0)
            {
                return null;
            }

            args.Add(Encoding.UTF8.GetString(buffer.Slice(at, end)));
            at += end + 1;
        }

        return args;
    }

    /// <summary>
    /// <c>NAME=value</c> entries for <c>envp</c>, sorted by name; names are case-sensitive here, but two names differing only
    /// in case are refused like on Windows, so a spec means the same on every node.
    /// </summary>
    internal static IReadOnlyList<string> BuildEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<string>(environment.Count);
        foreach (KeyValuePair<string, string> pair in environment.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Contains('=', StringComparison.Ordinal)
                || pair.Key.Contains('\0', StringComparison.Ordinal) || pair.Value is null
                || pair.Value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Invalid environment variable '{pair.Key}'.", nameof(environment));
            }

            if (!seen.Add(pair.Key))
            {
                throw new ArgumentException($"Environment variable '{pair.Key}' is given twice (names are case-insensitive).", nameof(environment));
            }

            entries.Add(pair.Key + "=" + pair.Value);
        }

        return entries;
    }

}
