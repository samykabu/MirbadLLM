namespace MirbadLLM.NodeRuntime.Abstractions;

/// <summary>
/// What <see cref="IProcessHost.StartAsync"/> starts (design-p3p4 §A.1.3 steps 1–5; frozen by MN-202, D116; corrected by
/// MN-207 after spike S4, D121). Everything is already resolved and confined by the OS-neutral <c>ProcessRuntimeDriver</c>
/// (MN-211): the host does no token substitution, no path check and no shell.
/// </summary>
public sealed record ProcessHostStart
{
    /// <summary>Absolute path of the verified executable (from <see cref="IExecutableCatalog"/>), used as the application name.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Arguments after substitution (no <c>argv[0]</c>); the host quotes them into one command line with one tested function.</summary>
    public required IReadOnlyList<string> Arguments { get; init; }

    /// <summary>The complete environment block. Nothing is inherited from the agent's own environment.</summary>
    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    /// <summary>Working directory (the executable's version folder).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>
    /// Log file stdout and stderr are bound to (a file handle, not a pipe, so the child survives an agent restart);
    /// opened for append, shared for reading. Stdin is the null device. These handles are the <b>only</b> ones the child
    /// inherits (Windows: <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> through <c>STARTUPINFOEX</c>; spike S4 / F2).
    /// </summary>
    public required string LogPath { get; init; }

    /// <summary>
    /// Name of the per-instance job (Windows job object <c>mirbadllm-ss-{id}</c>; a Linux host maps it to its own
    /// grouping). The job is <b>never</b> kill-on-close: the agent holds its only handle, so a kill-on-close job would kill
    /// the child whenever the agent stops, upgrades or crashes (spike S4 / F2). The kernel also drops the name with the
    /// last handle, so the name is a label for logs and diagnostics, not a key to re-open the job.
    /// </summary>
    public required string JobName { get; init; }
}

/// <summary>
/// Identity of a supervised process, persisted in its <see cref="ProcessRecord"/>: a PID alone can be reused, so adoption
/// compares the start time and the image path too (design-p3p4 §A.1.3 "Adoption").
/// </summary>
/// <param name="Pid">Process id.</param>
/// <param name="StartTime">Process creation time (UTC) as the OS reports it.</param>
/// <param name="ImagePath">Full path of the process image, as the OS reports it (final path, links resolved).</param>
public sealed record ProcessIdentity(int Pid, DateTimeOffset StartTime, string ImagePath);

/// <summary>A started or adopted process the host supervises (design-p3p4 §A.1.3). Disposing releases the handles only; it never kills.</summary>
public interface IHostedProcess : IAsyncDisposable
{
    /// <summary>The process identity.</summary>
    ProcessIdentity Identity { get; }

    /// <summary>The label of the job the process is in now (<see cref="ProcessHostStart.JobName"/>; after adoption, the new job's).</summary>
    string JobName { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>Exit code once exited; null while running or when the OS gives none.</summary>
    int? ExitCode { get; }

    /// <summary>Completes with the exit code when the process exits (works for adopted processes too).</summary>
    Task<int?> WaitForExitAsync(CancellationToken ct = default);

    /// <summary>
    /// Kills the process tree (Windows: <c>TerminateJobObject</c> on the job the host created or assigned it to) and waits
    /// until the process is gone. No-op once exited.
    /// </summary>
    Task KillTreeAsync(CancellationToken ct = default);
}

/// <summary>
/// The OS part of process delivery (design-p3p4 §A.1.1; frozen by MN-202, D116; corrected by MN-207 after spike S4, D121):
/// start, adopt, wait, kill and sweep orphans. <c>WindowsProcessHost</c> (MN-212, P/Invoke into <c>kernel32</c> and
/// <c>ntdll</c>, no package) implements it now; a <c>SystemdUserProcessHost</c> (design §6.6) can come later (P3b) without
/// touching the driver. Tests use an in-memory <c>FakeProcessHost</c> (MN-211). The driver never calls an OS API itself.
/// </summary>
public interface IProcessHost
{
    /// <summary>
    /// Starts the process <b>suspended</b>, assigns it to a new job named <see cref="ProcessHostStart.JobName"/> (never with
    /// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>), binds its std handles (the only handles it inherits), then resumes it
    /// (Windows: <c>CreateProcessW</c> with <c>CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_NEW_PROCESS_GROUP |
    /// CREATE_UNICODE_ENVIRONMENT | EXTENDED_STARTUPINFO_PRESENT</c>). A failure before resume kills the half-started
    /// process. The child outlives the agent, so nothing but the agent's own adoption and orphan sweep cleans up after it
    /// (spike S4 / F2).
    /// </summary>
    Task<IHostedProcess> StartAsync(ProcessHostStart start, CancellationToken ct = default);

    /// <summary>
    /// Re-opens a recorded process after an agent restart: null when no process has <paramref name="expected"/>'s PID, or
    /// its start time or image path differ (PID reuse, a reboot), so it is never adopted. Otherwise opens the process
    /// (Windows: <c>SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA | PROCESS_TERMINATE</c>),
    /// <b>creates a new job</b> labelled <paramref name="jobName"/> (no kill-on-close) and assigns the process to it, so
    /// <see cref="IHostedProcess.KillTreeAsync"/> works again. The old job cannot be re-opened by name: the kernel dropped
    /// the name with the old agent's last handle (<c>OpenJobObjectW</c> fails with 2). The new job nests under the
    /// orphaned one (Windows 8+). Spike S4 / F2 verified each step.
    /// </summary>
    Task<IHostedProcess?> OpenAsync(ProcessIdentity expected, string jobName, CancellationToken ct = default);

    /// <summary>
    /// Running processes whose image lies under <paramref name="imageFolder"/> and whose owner is the agent's own account
    /// (candidate orphans: started by an agent that died between create and record). A process outside the folder or owned
    /// by anybody else (such as the owner's own <c>llama-server.exe</c> on port 8000, spike S1 / C11) is never listed.
    /// Being listed is not enough to be killed: the driver kills a candidate only when its arguments
    /// (<see cref="ReadArgumentsAsync"/>) match one of the agent's own records or pending starts
    /// (<see cref="ProcessRecordsDocument.Pending"/>).
    /// </summary>
    Task<IReadOnlyList<ProcessIdentity>> ListCandidatesAsync(string imageFolder, CancellationToken ct = default);

    /// <summary>
    /// The arguments of a running process (<c>argv[1..]</c>; Windows: the command line from
    /// <c>NtQueryInformationProcess(ProcessCommandLineInformation)</c>, split by <c>CommandLineToArgvW</c>), re-checked
    /// against <paramref name="identity"/>; null when it is gone, no longer matches or cannot be read. The orphan sweep
    /// compares their digest with <see cref="ProcessRecord.ArgvDigest"/> and <see cref="PendingProcessStart.ArgvDigest"/>:
    /// the command line the agent built is its marker (MN-207, D121). The default answers null, so a host that cannot read
    /// command lines never lets the sweep kill anything.
    /// </summary>
    Task<IReadOnlyList<string>?> ReadArgumentsAsync(ProcessIdentity identity, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>?>(null);

    /// <summary>Kills a candidate orphan by identity (re-checked before the kill); false when it is gone or no longer matches.</summary>
    Task<bool> KillAsync(ProcessIdentity identity, CancellationToken ct = default);
}
