using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.NodeRuntime.Runtimes.Process.Windows;

/// <summary>
/// The Windows <see cref="IProcessHost"/> (MN-212; design-p3p4 §A.1.3, D121; spike S4 / F2). P/Invoke into
/// <c>kernel32</c>, <c>advapi32</c>, <c>shell32</c> and <c>ntdll</c>, no package:
/// <list type="bullet">
/// <item>Start: <c>CreateProcessW</c> suspended with an explicit environment block, stdout and stderr bound to the log file
/// (append) and stdin to <c>NUL</c> through <c>STARTF_USESTDHANDLES</c>, and <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c> naming
/// only those handles, so the child inherits nothing else of the agent; then a new per-instance job (never kill-on-close),
/// assign, resume.</item>
/// <item>Adopt: <c>OpenProcess</c>, creation time + image path check, a <b>new</b> job (the old one cannot be re-opened by
/// name), assign; the new job nests under the orphaned one.</item>
/// <item>Sweep support: candidates by image folder and owner SID; their command line from the PEB
/// (<c>NtQueryInformationProcess(ProcessCommandLineInformation)</c>, split by <c>CommandLineToArgvW</c>).</item>
/// </list>
/// The host never kills anything it was not asked to kill by identity, and every identity is re-checked on a handle it
/// holds, so a reused PID is never touched.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessHost : IProcessHost
{
    private const int MaxImagePathChars = 32_768;

    private const uint AdoptAccess = WindowsNative.SYNCHRONIZE | WindowsNative.PROCESS_QUERY_LIMITED_INFORMATION
        | WindowsNative.PROCESS_SET_QUOTA | WindowsNative.PROCESS_TERMINATE;

    private const uint StartFlags = WindowsNative.CREATE_SUSPENDED | WindowsNative.CREATE_NO_WINDOW
        | WindowsNative.CREATE_NEW_PROCESS_GROUP | WindowsNative.CREATE_UNICODE_ENVIRONMENT
        | WindowsNative.EXTENDED_STARTUPINFO_PRESENT;

    private readonly ILogger _logger;
    private readonly SecurityIdentifier _owner;

    /// <summary>Creates the host and logs the agent's own job membership (S4: none today, so no breakaway is needed).</summary>
    public WindowsProcessHost(ILogger<WindowsProcessHost>? logger = null)
        : this(logger, owner: null)
    {
    }

    /// <summary>Test seam: <paramref name="owner"/> replaces the agent's own SID as the candidate owner filter.</summary>
    internal WindowsProcessHost(ILogger? logger, SecurityIdentifier? owner)
    {
        _logger = logger ?? NullLogger.Instance;
        using (WindowsIdentity current = WindowsIdentity.GetCurrent())
        {
            _owner = owner ?? current.User ?? throw new InvalidOperationException("The agent's account has no SID.");
        }

        try
        {
            bool inJob = IsAgentInJob();
            WindowsProcessHostLog.AgentJobMembership(_logger, inJob);
        }
        catch (Win32Exception ex)
        {
            WindowsProcessHostLog.AgentJobMembershipUnknown(_logger, ex);
        }
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

        string commandLine = WindowsCommandLine.Build(start.ExecutablePath, start.Arguments);
        string environment = BuildEnvironmentBlock(start.Environment);
        Directory.CreateDirectory(Path.GetDirectoryName(start.LogPath)!);

        using SafeFileHandle nul = OpenInheritable("NUL", WindowsNative.GENERIC_READ,
            WindowsNative.FILE_SHARE_READ | WindowsNative.FILE_SHARE_WRITE, WindowsNative.OPEN_EXISTING);
        // Append-only (every write lands at the end, so stdout and stderr interleave by line, never overwrite); shared
        // for reading (log tail), for other appenders (the driver's own markers) and for rename (rotation).
        using SafeFileHandle log = OpenInheritable(start.LogPath, WindowsNative.FILE_APPEND_DATA,
            WindowsNative.FILE_SHARE_READ | WindowsNative.FILE_SHARE_WRITE | WindowsNative.FILE_SHARE_DELETE,
            WindowsNative.OPEN_ALWAYS);

        WindowsNative.SafeKernelHandle job = CreateJob(start.JobName);
        WindowsNative.PROCESS_INFORMATION pi;
        try
        {
            pi = CreateSuspended(start.ExecutablePath, commandLine, environment, start.WorkingDirectory, nul, log);
        }
        catch
        {
            job.Dispose();
            throw;
        }

        var process = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
        ProcessIdentity identity;
        try
        {
            if (!WindowsNative.AssignProcessToJobObject(job, process))
            {
                throw WindowsNative.LastError("AssignProcessToJobObject");
            }

            identity = QueryIdentity(process, pi.dwProcessId);
            if (WindowsNative.ResumeThread(pi.hThread) == uint.MaxValue)
            {
                throw WindowsNative.LastError("ResumeThread");
            }
        }
        catch
        {
            // Never leave a half-started (suspended) process behind.
            WindowsNative.TerminateProcess(process, WindowsHostedProcess.KilledExitCode);
            process.Dispose();
            job.Dispose();
            throw;
        }
        finally
        {
            WindowsNative.CloseHandle(pi.hThread);
        }

        WindowsProcessHostLog.Started(_logger, identity.ImagePath, identity.Pid, start.JobName);
        return Task.FromResult<IHostedProcess>(new WindowsHostedProcess(process, job, identity, start.JobName));
    }

    /// <inheritdoc />
    public Task<IHostedProcess?> OpenAsync(ProcessIdentity expected, string jobName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        ct.ThrowIfCancellationRequested();

        SafeProcessHandle process = WindowsNative.OpenProcess(AdoptAccess, inheritHandle: false, expected.Pid);
        if (process.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            process.Dispose();
            WindowsProcessHostLog.NotOpened(_logger, expected.Pid, error);
            return Task.FromResult<IHostedProcess?>(null);
        }

        try
        {
            string? mismatch = Mismatch(TryQueryIdentity(process, expected.Pid), expected);
            if (mismatch is null && IsSignaled(process))
            {
                mismatch = "the process has exited";
            }

            if (mismatch is not null)
            {
                WindowsProcessHostLog.NotAdopted(_logger, expected.Pid, mismatch);
                process.Dispose();
                return Task.FromResult<IHostedProcess?>(null);
            }

            WindowsNative.SafeKernelHandle job = CreateJob(jobName);
            if (!WindowsNative.AssignProcessToJobObject(job, process))
            {
                var error = WindowsNative.LastError("AssignProcessToJobObject");
                job.Dispose();
                throw error;
            }

            WindowsProcessHostLog.Adopted(_logger, expected.Pid, expected.ImagePath, jobName);
            return Task.FromResult<IHostedProcess?>(new WindowsHostedProcess(process, job, expected, jobName));
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProcessIdentity>> ListCandidatesAsync(string imageFolder, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(imageFolder);
        string[] prefixes = FolderPrefixes(imageFolder);
        var result = new List<ProcessIdentity>();
        int self = Environment.ProcessId;
        foreach (int pid in ProcessIds())
        {
            ct.ThrowIfCancellationRequested();
            if (pid is 0 or 4 || pid == self)
            {
                continue;
            }

            using SafeProcessHandle process = WindowsNative.OpenProcess(WindowsNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (process.IsInvalid)
            {
                continue;
            }

            ProcessIdentity? identity = TryQueryIdentity(process, pid);
            if (identity is null || !Array.Exists(prefixes, p => identity.ImagePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            SecurityIdentifier? owner = TryQueryOwner(process);
            if (owner is not null && _owner.Equals(owner))
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
        using SafeProcessHandle process = WindowsNative.OpenProcess(WindowsNative.PROCESS_QUERY_LIMITED_INFORMATION, false, identity.Pid);
        if (process.IsInvalid || Mismatch(TryQueryIdentity(process, identity.Pid), identity) is not null)
        {
            return Task.FromResult<IReadOnlyList<string>?>(null);
        }

        string? commandLine = TryReadCommandLine(process);
        if (commandLine is null)
        {
            return Task.FromResult<IReadOnlyList<string>?>(null);
        }

        IReadOnlyList<string> argv = WindowsCommandLine.Split(commandLine);
        return Task.FromResult<IReadOnlyList<string>?>(argv.Count == 0 ? [] : argv.Skip(1).ToArray());
    }

    /// <inheritdoc />
    public async Task<bool> KillAsync(ProcessIdentity identity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ct.ThrowIfCancellationRequested();
        using SafeProcessHandle process = WindowsNative.OpenProcess(
            WindowsNative.PROCESS_TERMINATE | WindowsNative.SYNCHRONIZE | WindowsNative.PROCESS_QUERY_LIMITED_INFORMATION,
            false, identity.Pid);
        if (process.IsInvalid || Mismatch(TryQueryIdentity(process, identity.Pid), identity) is not null || IsSignaled(process))
        {
            return false;
        }

        if (!WindowsNative.TerminateProcess(process, WindowsHostedProcess.KilledExitCode))
        {
            // Fails with access denied while the process is already exiting (killed by somebody else): then it is gone.
            var error = WindowsNative.LastError("TerminateProcess");
            if (!await WaitSignaledAsync(process, ExitGrace, ct).ConfigureAwait(false))
            {
                throw error;
            }

            return false;
        }

        if (!await WaitSignaledAsync(process, Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false))
        {
            throw new TimeoutException($"pid {identity.Pid} did not exit after TerminateProcess.");
        }

        WindowsProcessHostLog.KilledCandidate(_logger, identity.Pid, identity.ImagePath);
        return true;
    }

    /// <summary>How long a process that refuses termination because it is already exiting gets to finish.</summary>
    internal static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    /// <summary>Polls the process handle until it is signaled (true) or <paramref name="timeout"/> passes (false).</summary>
    internal static async Task<bool> WaitSignaledAsync(SafeProcessHandle process, TimeSpan timeout, CancellationToken ct)
    {
        long deadline = timeout == Timeout.InfiniteTimeSpan ? long.MaxValue : Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!IsSignaled(process))
        {
            if (Environment.TickCount64 > deadline)
            {
                return false;
            }

            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>True when the agent process itself is in a job (<c>IsProcessInJob(self, NULL)</c>).</summary>
    internal static bool IsAgentInJob() =>
        WindowsNative.IsProcessInAnyJob(WindowsNative.GetCurrentProcess(), 0, out bool inJob)
            ? inJob
            : throw WindowsNative.LastError("IsProcessInJob");

    /// <summary>The identity of a running process, or null when it is gone or not queryable (tests, diagnostics).</summary>
    internal static ProcessIdentity? TryQueryIdentity(int pid)
    {
        using SafeProcessHandle process = WindowsNative.OpenProcess(WindowsNative.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        return process.IsInvalid || IsSignaled(process) ? null : TryQueryIdentity(process, pid);
    }

    /// <summary>The environment block: <c>NAME=value\0</c>, sorted case-insensitively as Windows expects, then a final <c>\0</c>.</summary>
    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in environment.OrderBy(p => p.Key.ToUpperInvariant(), StringComparer.Ordinal))
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

            sb.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        if (sb.Length == 0)
        {
            sb.Append('\0');
        }

        return sb.Append('\0').ToString();
    }

    private static unsafe WindowsNative.PROCESS_INFORMATION CreateSuspended(string executable, string commandLine,
        string environment, string workingDirectory, SafeFileHandle nul, SafeFileHandle log)
    {
        nint size = 0;
        WindowsNative.InitializeProcThreadAttributeList(0, 1, 0, ref size);
        if (size == 0)
        {
            throw WindowsNative.LastError("InitializeProcThreadAttributeList");
        }

        nint attributes = Marshal.AllocHGlobal(size);
        bool initialized = false;
        bool nulRef = false;
        bool logRef = false;
        try
        {
            if (!WindowsNative.InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
            {
                throw WindowsNative.LastError("InitializeProcThreadAttributeList");
            }

            initialized = true;
            nul.DangerousAddRef(ref nulRef);
            log.DangerousAddRef(ref logRef);
            nint nulHandle = nul.DangerousGetHandle();
            nint logHandle = log.DangerousGetHandle();

            // The only handles the child inherits (S4 / F2): stdin and the log file shared by stdout and stderr.
            nint* inherited = stackalloc nint[2];
            inherited[0] = nulHandle;
            inherited[1] = logHandle;
            if (!WindowsNative.UpdateProcThreadAttribute(attributes, 0, WindowsNative.PROC_THREAD_ATTRIBUTE_HANDLE_LIST,
                    inherited, 2 * sizeof(nint), 0, 0))
            {
                throw WindowsNative.LastError("UpdateProcThreadAttribute");
            }

            var startup = new WindowsNative.STARTUPINFOEXW
            {
                StartupInfo = new WindowsNative.STARTUPINFOW
                {
                    cb = sizeof(WindowsNative.STARTUPINFOEXW),
                    dwFlags = WindowsNative.STARTF_USESTDHANDLES,
                    hStdInput = nulHandle,
                    hStdOutput = logHandle,
                    hStdError = logHandle,
                },
                lpAttributeList = attributes,
            };

            // lpCommandLine must be writable: CreateProcessW may modify it in place.
            char[] commandBuffer = new char[commandLine.Length + 1];
            commandLine.CopyTo(0, commandBuffer, 0, commandLine.Length);
            WindowsNative.PROCESS_INFORMATION pi;
            fixed (char* app = executable)
            fixed (char* cmd = commandBuffer)
            fixed (char* env = environment)
            fixed (char* cwd = workingDirectory)
            {
                if (!WindowsNative.CreateProcess(app, cmd, 0, 0, inheritHandles: true, StartFlags, env, cwd, &startup, &pi))
                {
                    throw WindowsNative.LastError("CreateProcessW");
                }
            }

            return pi;
        }
        finally
        {
            if (logRef)
            {
                log.DangerousRelease();
            }

            if (nulRef)
            {
                nul.DangerousRelease();
            }

            if (initialized)
            {
                WindowsNative.DeleteProcThreadAttributeList(attributes);
            }

            Marshal.FreeHGlobal(attributes);
        }
    }

    private WindowsNative.SafeKernelHandle CreateJob(string jobName)
    {
        WindowsNative.SafeKernelHandle job = WindowsNative.CreateJobObject(0, jobName);
        if (job.IsInvalid)
        {
            var error = WindowsNative.LastError("CreateJobObjectW");
            job.Dispose();
            throw error;
        }

        if (Marshal.GetLastPInvokeError() == WindowsNative.ERROR_ALREADY_EXISTS)
        {
            // CreateJobObjectW opened somebody else's job of that name; never use it. The name is a label only.
            job.Dispose();
            WindowsProcessHostLog.JobNameInUse(_logger, jobName);
            job = WindowsNative.CreateJobObject(0, null);
            if (job.IsInvalid)
            {
                var error = WindowsNative.LastError("CreateJobObjectW");
                job.Dispose();
                throw error;
            }
        }

        // No SetInformationJobObject: the job is never JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE (S4 / F2).
        return job;
    }

    private static unsafe SafeFileHandle OpenInheritable(string path, uint access, uint share, uint disposition)
    {
        var sa = new WindowsNative.SECURITY_ATTRIBUTES
        {
            nLength = sizeof(WindowsNative.SECURITY_ATTRIBUTES),
            bInheritHandle = 1,
        };
        SafeFileHandle handle = WindowsNative.CreateFile(path, access, share, &sa, disposition, WindowsNative.FILE_ATTRIBUTE_NORMAL, 0);
        if (handle.IsInvalid)
        {
            var error = WindowsNative.LastError($"CreateFileW({path})");
            handle.Dispose();
            throw error;
        }

        return handle;
    }

    private static ProcessIdentity QueryIdentity(SafeProcessHandle process, int pid) =>
        TryQueryIdentity(process, pid) ?? throw WindowsNative.LastError("GetProcessTimes/QueryFullProcessImageNameW");

    private static ProcessIdentity? TryQueryIdentity(SafeProcessHandle process, int pid)
    {
        if (!WindowsNative.GetProcessTimes(process, out long creation, out _, out _, out _))
        {
            return null;
        }

        string? image = TryQueryImagePath(process);
        return image is null ? null : new ProcessIdentity(pid, new DateTimeOffset(DateTime.FromFileTimeUtc(creation)), image);
    }

    private static unsafe string? TryQueryImagePath(SafeProcessHandle process)
    {
        char* small = stackalloc char[1024];
        int size = 1024;
        if (WindowsNative.QueryFullProcessImageName(process, 0, small, ref size))
        {
            return new string(small, 0, size);
        }

        if (Marshal.GetLastPInvokeError() != WindowsNative.ERROR_INSUFFICIENT_BUFFER)
        {
            return null;
        }

        char[] large = new char[MaxImagePathChars];
        size = large.Length;
        fixed (char* p = large)
        {
            return WindowsNative.QueryFullProcessImageName(process, 0, p, ref size) ? new string(p, 0, size) : null;
        }
    }

    private static unsafe SecurityIdentifier? TryQueryOwner(SafeProcessHandle process)
    {
        if (!WindowsNative.OpenProcessToken(process, WindowsNative.TOKEN_QUERY, out WindowsNative.SafeKernelHandle token))
        {
            token.Dispose();
            return null;
        }

        using (token)
        {
            WindowsNative.GetTokenInformation(token, WindowsNative.TokenUser, null, 0, out int length);
            if (length <= 0)
            {
                return null;
            }

            byte[] buffer = new byte[length];
            fixed (byte* p = buffer)
            {
                if (!WindowsNative.GetTokenInformation(token, WindowsNative.TokenUser, p, length, out _))
                {
                    return null;
                }

                // TOKEN_USER { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }: the SID pointer comes first.
                nint sid = *(nint*)p;
                return new SecurityIdentifier(sid);
            }
        }
    }

    private static unsafe string? TryReadCommandLine(SafeProcessHandle process)
    {
        int length = 1024;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            byte[] buffer = new byte[length];
            fixed (byte* p = buffer)
            {
                int status = WindowsNative.NtQueryInformationProcess(process, WindowsNative.ProcessCommandLineInformation, p,
                    buffer.Length, out int needed);
                if (status >= 0)
                {
                    var us = *(WindowsNative.UNICODE_STRING*)p;
                    return us.Buffer == 0 ? string.Empty : new string((char*)us.Buffer, 0, us.Length / sizeof(char));
                }

                if (status != WindowsNative.STATUS_INFO_LENGTH_MISMATCH && status != WindowsNative.STATUS_BUFFER_TOO_SMALL)
                {
                    return null;
                }

                length = Math.Max(needed, length * 2);
            }
        }

        return null;
    }

    internal static bool IsSignaled(SafeProcessHandle process) =>
        WindowsNative.WaitForSingleObject(process, 0) == WindowsNative.WAIT_OBJECT_0;

    private static string? Mismatch(ProcessIdentity? actual, ProcessIdentity expected)
    {
        if (actual is null)
        {
            return "its identity cannot be read";
        }

        if (actual.StartTime.UtcTicks != expected.StartTime.UtcTicks)
        {
            return $"start time {actual.StartTime:o} differs from the recorded {expected.StartTime:o} (PID reuse)";
        }

        return string.Equals(actual.ImagePath, expected.ImagePath, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"image {actual.ImagePath} differs from the recorded {expected.ImagePath}";
    }

    private static int[] ProcessIds()
    {
        System.Diagnostics.Process[] all = System.Diagnostics.Process.GetProcesses();
        var ids = new int[all.Length];
        for (int i = 0; i < all.Length; i++)
        {
            ids[i] = all[i].Id;
            all[i].Dispose();
        }

        return ids;
    }

    /// <summary>The folder as given (full path) and as its final path (links and 8.3 names resolved), each with a trailing separator.</summary>
    private static string[] FolderPrefixes(string imageFolder)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(imageFolder));
        string? final = TryFinalPath(full);
        return final is null || string.Equals(final, full, StringComparison.OrdinalIgnoreCase)
            ? [full + Path.DirectorySeparatorChar]
            : [full + Path.DirectorySeparatorChar, final + Path.DirectorySeparatorChar];
    }

    private static unsafe string? TryFinalPath(string directory)
    {
        using SafeFileHandle handle = WindowsNative.CreateFile(directory, WindowsNative.FILE_READ_ATTRIBUTES,
            WindowsNative.FILE_SHARE_READ | WindowsNative.FILE_SHARE_WRITE | WindowsNative.FILE_SHARE_DELETE, null,
            WindowsNative.OPEN_EXISTING, WindowsNative.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        char[] buffer = new char[MaxImagePathChars];
        fixed (char* p = buffer)
        {
            int length = WindowsNative.GetFinalPathNameByHandle(handle, p, buffer.Length, 0);
            if (length <= 0 || length >= buffer.Length)
            {
                return null;
            }

            string path = new(p, 0, length);
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path[8..];
            }

            return Path.TrimEndingDirectorySeparator(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);
        }
    }

    private static void RequireFullPath(string path, string name)
    {
        if (string.IsNullOrEmpty(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException($"{name} must be an absolute path.", name);
        }
    }
}
