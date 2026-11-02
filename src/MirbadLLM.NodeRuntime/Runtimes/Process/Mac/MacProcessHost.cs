using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes.Process.Mac;

/// <summary>
/// <see cref="IProcessHost"/> on macOS (docs/macos/implementation-plan.md M2, Phase 2): the Windows host's contract with
/// POSIX means.
/// <list type="bullet">
/// <item>Start: <c>posix_spawn</c> as the leader of a new session (<c>POSIX_SPAWN_SETSID</c>, so it is in no terminal's
/// group and the agent's launchd job does not own it — the agent's plist sets <c>AbandonProcessGroup</c>), stdin the null
/// device, stdout and stderr appended to the log file, nothing else inherited (<c>POSIX_SPAWN_CLOEXEC_DEFAULT</c>),
/// the given environment only, the given working directory, default signal dispositions.</item>
/// <item>Identity: pid, start time (<c>proc_pidinfo</c>, microseconds) and image path (<c>proc_pidpath</c>); every action
/// on a pid re-checks it, so a reused pid is never touched.</item>
/// <item>Kill: SIGTERM to the process group, SIGKILL after <see cref="TerminateGrace"/>.</item>
/// <item>Orphan candidates: processes of the agent's own uid whose image lies under the folder; their arguments come from
/// <c>KERN_PROCARGS2</c>.</item>
/// </list>
/// The job name is a label only (as on Windows after an agent restart).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacProcessHost : IProcessHost
{
    /// <summary>How long a process gets between SIGTERM and SIGKILL (llama-server and mlx_lm.server stop in well under a second).</summary>
    internal static readonly TimeSpan TerminateGrace = TimeSpan.FromSeconds(10);

    private readonly ILogger _logger;
    private readonly uint _uid;

    /// <summary>Creates the host for the agent's own account.</summary>
    public MacProcessHost(ILogger<MacProcessHost>? logger = null)
        : this(logger, uid: null)
    {
    }

    /// <summary>Test seam: <paramref name="uid"/> replaces the agent's own uid as the candidate owner filter.</summary>
    internal MacProcessHost(ILogger? logger, uint? uid)
    {
        _logger = logger ?? NullLogger.Instance;
        _uid = uid ?? MacNative.EffectiveUid();
    }

    /// <inheritdoc />
    public Task<IHostedProcess> StartAsync(ProcessHostStart start, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        ct.ThrowIfCancellationRequested();
        RequireFullPath(start.ExecutablePath, nameof(start.ExecutablePath));
        RequireFullPath(start.WorkingDirectory, nameof(start.WorkingDirectory));
        RequireFullPath(start.LogPath, nameof(start.LogPath));
        ArgumentException.ThrowIfNullOrEmpty(start.JobName);

        IReadOnlyList<string> environment = MacProcFormats.BuildEnvironment(start.Environment);
        Directory.CreateDirectory(Path.GetDirectoryName(start.LogPath)!);
        string[] argv = [start.ExecutablePath, .. start.Arguments];

        int pid = MacNative.Spawn(start.ExecutablePath, argv, environment, start.WorkingDirectory, start.LogPath);
        // A process that already exited is still this agent's unreaped child (a zombie): its start time is readable and the
        // hosted process reports its exit code, as on Windows, where a quick exit is not a start failure either.
        ProcessIdentity identity = SettledIdentity(pid)
            ?? (MacNative.TryBsdInfo(pid) is { } zombie ? new ProcessIdentity(pid, zombie.Start, start.ExecutablePath) : null)
            ?? throw new Win32Exception(0, $"{start.ExecutablePath} (pid {pid}) could not be identified right after it was started.");

        MacProcessHostLog.Started(_logger, identity.ImagePath, identity.Pid, start.JobName);
        return Task.FromResult<IHostedProcess>(new MacHostedProcess(identity, start.JobName, ownChild: true));
    }

    /// <inheritdoc />
    public Task<IHostedProcess?> OpenAsync(ProcessIdentity expected, string jobName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        ct.ThrowIfCancellationRequested();

        string? mismatch = Mismatch(TryQueryIdentity(expected.Pid), expected);
        if (mismatch is not null)
        {
            MacProcessHostLog.NotAdopted(_logger, expected.Pid, mismatch);
            return Task.FromResult<IHostedProcess?>(null);
        }

        // A process this agent process spawned (a restart of the driver, not of the agent) is still its child to reap.
        bool ownChild = MacNative.TryReap(expected.Pid).Error != MacNative.ECHILD;
        MacProcessHostLog.Adopted(_logger, expected.Pid, expected.ImagePath, jobName);
        return Task.FromResult<IHostedProcess?>(new MacHostedProcess(expected, jobName, ownChild));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProcessIdentity>> ListCandidatesAsync(string imageFolder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(imageFolder);
        string[] prefixes = FolderPrefixes(imageFolder);
        var result = new List<ProcessIdentity>();
        int self = Environment.ProcessId;
        foreach (int pid in MacNative.AllPids())
        {
            ct.ThrowIfCancellationRequested();
            if (pid <= 1 || pid == self || MacNative.TryBsdInfo(pid) is not { Zombie: false } info || info.Uid != _uid)
            {
                continue;
            }

            if (TryQueryIdentity(pid) is { } identity
                && Array.Exists(prefixes, p => identity.ImagePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(identity);
            }
        }

        return Task.FromResult<IReadOnlyList<ProcessIdentity>>(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>?> ReadArgumentsAsync(ProcessIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ct.ThrowIfCancellationRequested();
        if (Mismatch(TryQueryIdentity(identity.Pid), identity) is not null || MacNative.TryArguments(identity.Pid) is not { } argv)
        {
            return Task.FromResult<IReadOnlyList<string>?>(null);
        }

        return Task.FromResult<IReadOnlyList<string>?>(argv.Count == 0 ? [] : argv.Skip(1).ToArray());
    }

    /// <inheritdoc />
    public async Task<bool> KillAsync(ProcessIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ct.ThrowIfCancellationRequested();
        if (!IsAlive(identity))
        {
            return false;
        }

        await TerminateAsync(identity, () => !IsAlive(identity), ownChild: false, ct).ConfigureAwait(false);
        MacProcessHostLog.KilledCandidate(_logger, identity.Pid, identity.ImagePath);
        return true;
    }

    /// <summary>
    /// SIGTERM to <paramref name="identity"/>'s process group (its own pid when it leads one, else the process alone),
    /// SIGKILL after <see cref="TerminateGrace"/>, then waits until <paramref name="exited"/> says it is gone. The identity
    /// is re-checked before each signal.
    /// </summary>
    /// <param name="identity">The process.</param>
    /// <param name="exited">True once it is gone (reaps it when it is the caller's child).</param>
    /// <param name="ownChild">
    /// True for an unreaped child of this process: its pid cannot be reused until it is reaped, so it is signalled by pid
    /// without re-reading its image, which a program that re-executes itself changes (macOS's <c>/bin/sh</c> shim does).
    /// </param>
    /// <param name="ct">Cancellation.</param>
    internal static async Task TerminateAsync(ProcessIdentity identity, Func<bool> exited, bool ownChild, CancellationToken ct)
    {
        Send(identity, MacNative.SIGTERM, ownChild);
        long deadline = Environment.TickCount64 + (long)TerminateGrace.TotalMilliseconds;
        while (!exited())
        {
            if (Environment.TickCount64 > deadline)
            {
                Send(identity, MacNative.SIGKILL, ownChild);
                deadline = long.MaxValue;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The identity once the image has settled: a program may re-execute itself right after it starts (macOS's
    /// <c>/bin/sh</c> becomes <c>/bin/bash</c> within milliseconds), and an identity recorded before that would never be
    /// adopted after an agent restart. Reads until two reads 50 ms apart agree (at most ~0.5 s).
    /// </summary>
    private static ProcessIdentity? SettledIdentity(int pid)
    {
        ProcessIdentity? previous = TryQueryIdentity(pid);
        for (int i = 0; i < 10 && previous is not null; i++)
        {
            Thread.Sleep(50);
            ProcessIdentity? current = TryQueryIdentity(pid);
            if (current is null || current == previous)
            {
                // Settled, or exited meanwhile: the last identity read is the one it had.
                return previous;
            }

            previous = current;
        }

        return previous;
    }

    /// <summary>True while a process with <paramref name="identity"/>'s pid, start time and image runs (zombies count as gone).</summary>
    internal static bool IsAlive(ProcessIdentity identity) =>
        MacNative.TryBsdInfo(identity.Pid) is { Zombie: false } info
        && info.Start.UtcTicks == identity.StartTime.UtcTicks
        && Mismatch(TryQueryIdentity(identity.Pid), identity) is null;

    /// <summary>The identity of a running process, or null when it is gone, a zombie or not queryable (tests, diagnostics).</summary>
    internal static ProcessIdentity? TryQueryIdentity(int pid)
    {
        if (MacNative.TryBsdInfo(pid) is not { Zombie: false } info || MacNative.TryImagePath(pid) is not { } path)
        {
            return null;
        }

        return new ProcessIdentity(pid, info.Start, path);
    }

    private static void Send(ProcessIdentity identity, int signal, bool ownChild)
    {
        if (!ownChild && !IsAlive(identity))
        {
            return;
        }

        bool leadsGroup = MacNative.TryBsdInfo(identity.Pid) is { } info && info.ProcessGroup == identity.Pid;
        int error = MacNative.Signal(leadsGroup ? -identity.Pid : identity.Pid, signal);
        if (error is not 0 and not MacNative.ESRCH)
        {
            throw new Win32Exception(error, $"kill({(leadsGroup ? "-" : "")}{identity.Pid}, {signal}) failed ({error}).");
        }
    }

    private static string? Mismatch(ProcessIdentity? actual, ProcessIdentity expected)
    {
        if (actual is null)
        {
            return "no such process, or its identity cannot be read";
        }

        if (actual.StartTime.UtcTicks != expected.StartTime.UtcTicks)
        {
            return $"start time {actual.StartTime:o} differs from the recorded {expected.StartTime:o} (pid reuse)";
        }

        // APFS is case-insensitive by default; the recorded path is what proc_pidpath returned.
        return string.Equals(actual.ImagePath, expected.ImagePath, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"image {actual.ImagePath} differs from the recorded {expected.ImagePath}";
    }

    /// <summary>The folder as given and with links resolved (<c>/var</c> → <c>/private/var</c>), each with a trailing slash.</summary>
    private static string[] FolderPrefixes(string imageFolder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(imageFolder));
        string? resolved = null;
        try
        {
            resolved = new DirectoryInfo(full).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            resolved ??= ResolveParents(full);
        }
        catch (IOException)
        {
            resolved = null;
        }

        return resolved is null || string.Equals(resolved, full, StringComparison.OrdinalIgnoreCase)
            ? [full + "/"]
            : [full + "/", Path.TrimEndingDirectorySeparator(resolved) + "/"];
    }

    /// <summary>The path with each existing symlinked ancestor resolved (proc_pidpath reports the real path).</summary>
    private static string? ResolveParents(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (parent is null)
        {
            return null;
        }

        string? resolvedParent = new DirectoryInfo(parent).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? ResolveParents(parent);
        return resolvedParent is null ? null : Path.Combine(resolvedParent, Path.GetFileName(path));
    }

    private static void RequireFullPath(string path, string name)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{name} must be an absolute path.", name);
        }
    }
}
