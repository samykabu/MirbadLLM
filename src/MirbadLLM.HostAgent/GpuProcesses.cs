using System.Globalization;
using System.Text.RegularExpressions;

namespace MirbadLLM.HostAgent;

/// <summary>
/// One GPU compute process (<c>GET /gpu-processes</c>): what nvidia-smi reports, plus who owns it — the Docker
/// container (64-hex id) or the systemd unit its cgroup sits in. Both null when the cgroup names neither.
/// <paramref name="DisplayName"/> is the whitelist's name for that unit (e.g. "ComfyUI"), when it is managed.
/// <paramref name="GpuUuid"/> is the GPU the memory is on (MN-106, D91; nvidia-smi lists a process once per GPU it uses).
/// Older agents ignore the field; without it (an older hostd) the master splits a model's memory evenly over its GPUs.
/// </summary>
internal sealed record GpuProcessInfo(int Pid, string Name, long UsedMiB, string? ContainerId, string? Unit, string? DisplayName = null, string? GpuUuid = null);

/// <summary>
/// Lists GPU compute processes with their owners. Read-only: runs <c>nvidia-smi</c> by argument vector (no shell,
/// short timeout) and reads <c>/proc/&lt;pid&gt;/cgroup</c>. The agent runs in the host PID namespace, so it sees
/// the PIDs the controller container can't.
/// </summary>
internal sealed partial class GpuProcessInventory(IProcessRunner runner, ICgroupReader cgroups)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static IReadOnlyList<string> Arguments() =>
        ["--query-compute-apps=gpu_uuid,pid,process_name,used_memory", "--format=csv,noheader,nounits"];

    /// <summary>The processes, or null when nvidia-smi is missing, fails or times out.</summary>
    public async Task<IReadOnlyList<GpuProcessInfo>?> ListAsync(CancellationToken ct)
    {
        ProcessResult run;
        try
        {
            run = await runner.RunAsync(SystemctlCommands.NvidiaSmiPath, Arguments(), Timeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        if (!run.Succeeded)
        {
            return null;
        }

        return Parse(run.StdOut)
            .Select(p =>
            {
                var (container, unit) = Owner(cgroups.Read(p.Pid));
                return p with { ContainerId = container, Unit = unit };
            })
            .ToList();
    }

    /// <summary>
    /// Parses <c>gpu_uuid, pid, process_name, used_memory</c> CSV (MiB, no header, no units). The name can itself contain
    /// commas (a path), so the UUID is the first field, the PID the second, the memory the last and the name everything
    /// between. Lines whose PID or memory don't parse (<c>[N/A]</c>) are skipped. A first field that is not a GPU/MIG UUID
    /// (<c>[N/A]</c>, empty) keeps the row with no GPU (the master then splits evenly, as for an older hostd): the DGX's
    /// measured model memory must not depend on the new column (W8 review MN-128).
    /// </summary>
    public static IReadOnlyList<GpuProcessInfo> Parse(string output)
    {
        var result = new List<GpuProcessInfo>();
        foreach (string raw in output.Split('\n'))
        {
            string row = raw.Trim();
            int uuidEnd = row.IndexOf(',', StringComparison.Ordinal);
            if (uuidEnd < 0)
            {
                continue;
            }

            string uuid = row[..uuidEnd].Trim();
            string? gpuUuid = uuid.StartsWith("GPU-", StringComparison.Ordinal) || uuid.StartsWith("MIG-", StringComparison.Ordinal) ? uuid : null;
            string line = row[(uuidEnd + 1)..];
            int first = line.IndexOf(',', StringComparison.Ordinal);
            int last = line.LastIndexOf(',');
            if (first < 0 || last <= first
                || !int.TryParse(line.AsSpan(0, first).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
                || !long.TryParse(line.AsSpan(last + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long mib))
            {
                continue;
            }

            result.Add(new GpuProcessInfo(pid, line[(first + 1)..last].Trim(), mib, null, null, GpuUuid: gpuUuid));
        }

        return result;
    }

    /// <summary>
    /// The owner named by a cgroup v2 path: a Docker container (<c>/system.slice/docker-&lt;64hex&gt;.scope</c> with the
    /// systemd driver, <c>/docker/&lt;64hex&gt;</c> with cgroupfs), else the innermost <c>.service</c> unit
    /// (<c>/system.slice/comfyui.service</c>, or a user unit under <c>user@1000.service/app.slice/</c>).
    /// </summary>
    public static (string? ContainerId, string? Unit) Owner(string? cgroupPath)
    {
        if (string.IsNullOrEmpty(cgroupPath))
        {
            return (null, null);
        }

        if (DockerScope().Match(cgroupPath) is { Success: true } docker)
        {
            return (docker.Groups[1].Value, null);
        }

        string? unit = cgroupPath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(segment => segment.EndsWith(".service", StringComparison.Ordinal) && !segment.StartsWith("user@", StringComparison.Ordinal));
        return (null, unit);
    }

    [GeneratedRegex(@"(?:/docker-|/docker/)([0-9a-f]{64})(?:\.scope)?(?:/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex DockerScope();
}
