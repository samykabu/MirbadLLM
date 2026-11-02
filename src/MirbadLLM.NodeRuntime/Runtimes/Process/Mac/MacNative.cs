using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MirbadLLM.NodeRuntime.Runtimes.Process.Mac;

/// <summary>
/// The <c>libSystem</c> calls behind <see cref="MacProcessHost"/> (docs/macos/implementation-plan.md M2): <c>posix_spawn</c>
/// with file actions and attributes, <c>waitpid</c>, <c>kill</c>, and the <c>libproc</c>/<c>sysctl</c> queries that give a
/// process's start time, owner, image path and arguments. P/Invoke only, no package.
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe partial class MacNative
{
    private const string LibSystem = "libSystem.dylib";

    // <fcntl.h>
    public const int O_RDONLY = 0x0000;
    public const int O_WRONLY = 0x0001;
    public const int O_APPEND = 0x0008;
    public const int O_CREAT = 0x0200;

    // <spawn.h> (Apple values; SETSID needs macOS 10.15, CLOEXEC_DEFAULT is an Apple extension)
    public const short POSIX_SPAWN_SETSIGDEF = 0x0004;
    public const short POSIX_SPAWN_SETSIGMASK = 0x0008;
    public const short POSIX_SPAWN_SETSID = 0x0400;
    public const short POSIX_SPAWN_CLOEXEC_DEFAULT = 0x4000;

    // <sys/wait.h>, <signal.h>, <errno.h>
    public const int WNOHANG = 0x1;
    public const int SIGKILL = 9;
    public const int SIGTERM = 15;
    public const int EPERM = 1;
    public const int ESRCH = 3;
    public const int EINTR = 4;
    public const int ECHILD = 10;

    // <sys/proc_info.h>, <sys/sysctl.h>
    private const int PROC_PIDTBSDINFO = 3;
    private const int ProcBsdInfoSize = 136;
    private const int PROC_PIDPATHINFO_MAXSIZE = 4096;
    private const int CTL_KERN = 1;
    private const int KERN_ARGMAX = 8;
    private const int KERN_PROCARGS2 = 49;

    /// <summary>Spawns <paramref name="path"/> detached in its own session; returns its pid. Throws on failure.</summary>
    public static int Spawn(string path, IReadOnlyList<string> argv, IReadOnlyList<string> envp, string workingDirectory, string logPath)
    {
        IntPtr actions = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero;
        var allocations = new List<IntPtr>();
        try
        {
            Check(posix_spawn_file_actions_init(ref actions), "posix_spawn_file_actions_init");
            Check(posix_spawnattr_init(ref attributes), "posix_spawnattr_init");

            // stdin: the null device; stdout: the log, append-only; stderr: the same file. With CLOEXEC_DEFAULT these three
            // are the only descriptors the child inherits (the Windows host's handle list).
            Check(posix_spawn_file_actions_addopen(ref actions, 0, "/dev/null", O_RDONLY, 0), "addopen stdin");
            Check(posix_spawn_file_actions_addopen(ref actions, 1, logPath, O_WRONLY | O_APPEND | O_CREAT, 0b110_100_000), "addopen stdout");
            Check(posix_spawn_file_actions_adddup2(ref actions, 1, 2), "adddup2 stderr");
            Check(posix_spawn_file_actions_addchdir_np(ref actions, workingDirectory), "addchdir");

            // Every signal back to its default disposition and none blocked: the .NET runtime ignores some (SIGPIPE) and
            // a spawned child would inherit that.
            uint all = 0xFFFF_FFFF;
            uint none = 0;
            Check(posix_spawnattr_setsigdefault(ref attributes, ref all), "posix_spawnattr_setsigdefault");
            Check(posix_spawnattr_setsigmask(ref attributes, ref none), "posix_spawnattr_setsigmask");
            Check(posix_spawnattr_setflags(ref attributes,
                POSIX_SPAWN_SETSID | POSIX_SPAWN_CLOEXEC_DEFAULT | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK), "posix_spawnattr_setflags");

            IntPtr[] argvBlock = NullTerminated(argv, allocations);
            IntPtr[] envBlock = NullTerminated(envp, allocations);
            fixed (IntPtr* argvPtr = argvBlock)
            fixed (IntPtr* envPtr = envBlock)
            {
                int error = posix_spawn(out int pid, path, ref actions, ref attributes, argvPtr, envPtr);
                if (error != 0)
                {
                    throw new Win32Exception(error, $"posix_spawn {path} failed ({error}: {Marshal.GetPInvokeErrorMessage(error)}).");
                }

                return pid;
            }
        }
        finally
        {
            if (actions != IntPtr.Zero)
            {
                _ = posix_spawn_file_actions_destroy(ref actions);
            }

            if (attributes != IntPtr.Zero)
            {
                _ = posix_spawnattr_destroy(ref attributes);
            }

            foreach (IntPtr allocation in allocations)
            {
                Marshal.FreeCoTaskMem(allocation);
            }
        }
    }

    /// <summary>The BSD info of <paramref name="pid"/>, or null when it is gone or not visible.</summary>
    public static MacProcFormats.BsdInfo? TryBsdInfo(int pid)
    {
        byte* buffer = stackalloc byte[ProcBsdInfoSize];
        int read = proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, buffer, ProcBsdInfoSize);
        if (read < ProcBsdInfoSize)
        {
            return null;
        }

        return MacProcFormats.ParseBsdInfo(new ReadOnlySpan<byte>(buffer, ProcBsdInfoSize));
    }

    /// <summary>The executable path of <paramref name="pid"/>, or null.</summary>
    public static string? TryImagePath(int pid)
    {
        byte* buffer = stackalloc byte[PROC_PIDPATHINFO_MAXSIZE];
        int length = proc_pidpath(pid, buffer, PROC_PIDPATHINFO_MAXSIZE);
        return length <= 0 ? null : Encoding.UTF8.GetString(buffer, length);
    }

    /// <summary>The argv of <paramref name="pid"/> (<c>KERN_PROCARGS2</c>), including <c>argv[0]</c>; null when unreadable.</summary>
    public static IReadOnlyList<string>? TryArguments(int pid)
    {
        int argMax = 0;
        nuint size = sizeof(int);
        int* argMaxName = stackalloc int[] { CTL_KERN, KERN_ARGMAX };
        if (sysctl(argMaxName, 2, (byte*)&argMax, ref size, IntPtr.Zero, 0) != 0 || argMax <= 0)
        {
            return null;
        }

        byte[] buffer = new byte[argMax];
        size = (nuint)argMax;
        int* name = stackalloc int[] { CTL_KERN, KERN_PROCARGS2, pid };
        fixed (byte* p = buffer)
        {
            if (sysctl(name, 3, p, ref size, IntPtr.Zero, 0) != 0)
            {
                return null;
            }
        }

        return MacProcFormats.ParseProcArgs(buffer.AsSpan(0, (int)size));
    }

    /// <summary>Every pid on the system.</summary>
    public static int[] AllPids()
    {
        int count = proc_listallpids(null, 0);
        if (count <= 0)
        {
            return [];
        }

        int[] pids = new int[count + 64];
        fixed (int* p = pids)
        {
            count = proc_listallpids(p, pids.Length * sizeof(int));
        }

        return count <= 0 ? [] : pids[..Math.Min(count, pids.Length)];
    }

    /// <summary><c>waitpid(pid, WNOHANG)</c>: (reaped, exit code) for a child of this process; reaped false while it runs.</summary>
    public static (bool Reaped, int? ExitCode, int Error) TryReap(int pid)
    {
        int result;
        int status;
        do
        {
            result = waitpid(pid, out status, WNOHANG);
        }
        while (result == -1 && Marshal.GetLastPInvokeError() == EINTR);

        if (result == -1)
        {
            return (false, null, Marshal.GetLastPInvokeError());
        }

        if (result == 0)
        {
            return (false, null, 0);
        }

        // WIFEXITED: (status & 0x7f) == 0 → WEXITSTATUS (status >> 8) & 0xff; else killed by signal 128 + (status & 0x7f).
        int termination = status & 0x7f;
        return (true, termination == 0 ? (status >> 8) & 0xff : 128 + termination, 0);
    }

    /// <summary><c>kill(pid, signal)</c>; returns 0 or the errno.</summary>
    public static int Signal(int pid, int signal) => kill(pid, signal) == 0 ? 0 : Marshal.GetLastPInvokeError();

    /// <summary>The effective uid of this process.</summary>
    public static uint EffectiveUid() => geteuid();

    private static IntPtr[] NullTerminated(IReadOnlyList<string> values, List<IntPtr> allocations)
    {
        var block = new IntPtr[values.Count + 1];
        for (int i = 0; i < values.Count; i++)
        {
            block[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
            allocations.Add(block[i]);
        }

        return block;
    }

    private static void Check(int error, string call)
    {
        if (error != 0)
        {
            throw new Win32Exception(error, $"{call} failed ({error}: {Marshal.GetPInvokeErrorMessage(error)}).");
        }
    }

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn(out int pid, string path, ref IntPtr fileActions, ref IntPtr attributes, IntPtr* argv, IntPtr* envp);

    [LibraryImport(LibSystem)]
    private static partial int posix_spawn_file_actions_init(ref IntPtr actions);

    [LibraryImport(LibSystem)]
    private static partial int posix_spawn_file_actions_destroy(ref IntPtr actions);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn_file_actions_addopen(ref IntPtr actions, int fd, string path, int oflag, ushort mode);

    [LibraryImport(LibSystem)]
    private static partial int posix_spawn_file_actions_adddup2(ref IntPtr actions, int fd, int newFd);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn_file_actions_addchdir_np(ref IntPtr actions, string path);

    [LibraryImport(LibSystem)]
    private static partial int posix_spawnattr_init(ref IntPtr attributes);

    [LibraryImport(LibSystem)]
    private static partial int posix_spawnattr_destroy(ref IntPtr attributes);

    [LibraryImport(LibSystem)]
    private static partial int posix_spawnattr_setflags(ref IntPtr attributes, short flags);

    [LibraryImport(LibSystem)]
    private static partial int posix_spawnattr_setsigdefault(ref IntPtr attributes, ref uint signals);

    [LibraryImport(LibSystem)]
    private static partial int posix_spawnattr_setsigmask(ref IntPtr attributes, ref uint signals);

    [LibraryImport(LibSystem, SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);

    [LibraryImport(LibSystem, SetLastError = true)]
    private static partial int kill(int pid, int signal);

    [LibraryImport(LibSystem)]
    private static partial uint geteuid();

    [LibraryImport(LibSystem)]
    private static partial int proc_pidinfo(int pid, int flavor, ulong arg, byte* buffer, int bufferSize);

    [LibraryImport(LibSystem)]
    private static partial int proc_pidpath(int pid, byte* buffer, uint bufferSize);

    [LibraryImport(LibSystem)]
    private static partial int proc_listallpids(int* buffer, int bufferSize);

    [LibraryImport(LibSystem, SetLastError = true)]
    private static partial int sysctl(int* name, uint nameLength, byte* oldValue, ref nuint oldLength, IntPtr newValue, nuint newLength);
}
