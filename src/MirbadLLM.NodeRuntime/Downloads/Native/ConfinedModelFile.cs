using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>Opens a regular, singly linked model file without following path links.</summary>
public static partial class ConfinedModelFile
{
    public readonly record struct Identity(ulong Device, ulong FileId, long Size, long Modified, long Changed);

    public static FileStream? OpenManifest(string root, string folder) =>
        RepoPathRules.Check(folder) is null && !folder.Contains('/')
            ? OpenCore(root, $"{folder}/{DownloadManifest.FileName}", false) : null;

    public static FileStream? Open(string root, string path) =>
        RepoPathRules.Check(path) is null ? OpenCore(root, path, false) : null;

    public static FileStream? OpenWrite(string root, string path) =>
        RepoPathRules.Check(path) is null ? OpenCore(root, path, true) : null;

    private static FileStream? OpenCore(string root, string path, bool write)
    {
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return null;
        }

        root = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine(root, path));
        if (!RepoPathRules.IsInside(root, fullPath))
        {
            return null;
        }

        SafeFileHandle? handle = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // openat2 confines the lookup in the kernel, including a rename or symlink swap during traversal.
                const ulong noLinks = 0x04;
                const ulong beneath = 0x08;
                var rootFlags = new OpenHow { Flags = 0x200000, Resolve = noLinks };
                using var directory = new SafeFileHandle((nint)OpenAt2(437, -100, root, ref rootFlags, 24), ownsHandle: true);
                if (directory.IsInvalid)
                {
                    return null;
                }

                var flags = new OpenHow
                {
                    Flags = 0x800 | 0x80000 | (write ? 0x42UL : 0),
                    Mode = write ? 0x1a4UL : 0,
                    Resolve = noLinks | beneath,
                };
                handle = new SafeFileHandle((nint)OpenAt2(437, directory.DangerousGetHandle().ToInt32(), path, ref flags, 24), ownsHandle: true);
            }
            else if (OperatingSystem.IsMacOS())
            {
                const int noFollow = 0x100;
                const int directory = 0x100000;
                const int closeOnExec = 0x1000000;
                using var start = new SafeFileHandle((nint)OpenAt(-2, "/", directory | closeOnExec), ownsHandle: true);
                if (start.IsInvalid)
                {
                    return null;
                }

                string[] parts = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                handle = new SafeFileHandle((nint)OpenAt(start.DangerousGetHandle().ToInt32(), parts[0],
                    directory | noFollow | closeOnExec), ownsHandle: true);
                for (int i = 1; i < parts.Length && !handle.IsInvalid; i++)
                {
                    int flags = i == parts.Length - 1 ? 4 | (write ? 0x202 : 0) : directory;
                    var next = new SafeFileHandle((nint)OpenAt(handle.DangerousGetHandle().ToInt32(), parts[i],
                        flags | noFollow | closeOnExec, 0x1a4), ownsHandle: true);
                    handle.Dispose();
                    handle = next;
                }

                if (handle.IsInvalid || !string.Equals(PathFromMacHandle(handle), fullPath, StringComparison.Ordinal))
                {
                    return null;
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                string current = root;
                if (!string.Equals(WindowsFinalPath.FinalPathOfDirectory(root), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                foreach (string segment in path.Split('/'))
                {
                    current = Path.Combine(current, segment);
                    if (RepoPathRules.IsLink(new FileInfo(current)))
                    {
                        return null;
                    }
                }

                string parent = Path.GetDirectoryName(fullPath)!;
                if (!string.Equals(WindowsFinalPath.FinalPathOfDirectory(parent), parent, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                handle = File.OpenHandle(fullPath, write ? FileMode.OpenOrCreate : FileMode.Open,
                    write ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
                if (!string.Equals(WindowsFinalPath.FinalPathOf(handle), fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            if (handle is null || handle.IsInvalid || Identify(handle) is null)
            {
                return null;
            }

            var stream = new FileStream(handle, write ? FileAccess.ReadWrite : FileAccess.Read, bufferSize: 128 * 1024);
            handle = null;
            return stream;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    public static Identity? Identify(SafeFileHandle file)
    {
        if (OperatingSystem.IsLinux() && Statx(file, "", 0x1000, 0x7ff, out var linux) == 0 &&
            (linux.Mask & 0x7c5) == 0x7c5 && (linux.Mode & 0xf000) == 0x8000 && linux.Links == 1)
        {
            return new Identity(((ulong)linux.DeviceMajor << 32) | linux.DeviceMinor, linux.Inode, linux.Size,
                Nanoseconds(linux.ModifiedSeconds, linux.ModifiedNanos), Nanoseconds(linux.ChangedSeconds, linux.ChangedNanos));
        }

        if (OperatingSystem.IsMacOS() && MacStat(file, out var mac) == 0 && (mac.Mode & 0xf000) == 0x8000 && mac.Links == 1)
        {
            return new Identity(mac.Device, mac.Inode, mac.Size, Nanoseconds(mac.ModifiedSeconds, mac.ModifiedNanos),
                Nanoseconds(mac.ChangedSeconds, mac.ChangedNanos));
        }

        if (OperatingSystem.IsWindows() && WindowsStat(file, out var windows) != 0 && windows.Links == 1 &&
            (windows.Attributes & (uint)(FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) == 0)
        {
            return new Identity(windows.Volume, ((ulong)windows.InodeHigh << 32) | windows.InodeLow,
                ((long)windows.SizeHigh << 32) | windows.SizeLow, ((long)windows.ModifiedHigh << 32) | windows.ModifiedLow,
                File.GetCreationTimeUtc(file).Ticks);
        }

        return null;
    }

    private static long Nanoseconds(long seconds, long nanos) => unchecked(seconds * 1_000_000_000L + nanos);

    private static string? PathFromMacHandle(SafeFileHandle file)
    {
        byte[] path = new byte[1024];
        return MacFilePath(file, 50, path) == 0 && Array.IndexOf(path, (byte)0) is > 0 and var end
            ? System.Text.Encoding.UTF8.GetString(path, 0, end) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStat
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public long Size;
        [FieldOffset(96)] public long ChangedSeconds;
        [FieldOffset(104)] public uint ChangedNanos;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanos;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct MacFileStat
    {
        [FieldOffset(0)] public uint Device;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort Links;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(48)] public long ModifiedSeconds;
        [FieldOffset(56)] public long ModifiedNanos;
        [FieldOffset(64)] public long ChangedSeconds;
        [FieldOffset(72)] public long ChangedNanos;
        [FieldOffset(96)] public long Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowsFileStat
    {
        public uint Attributes;
        public uint CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, ModifiedLow, ModifiedHigh;
        public uint Volume, SizeHigh, SizeLow, Links, InodeHigh, InodeLow;
    }

    [LibraryImport("libc", EntryPoint = "syscall", StringMarshalling = StringMarshalling.Utf8)]
    private static partial long OpenAt2(long syscall, int directory, string path, ref OpenHow how, nuint size);

    [LibraryImport("libc", EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Statx(SafeFileHandle file, string path, int flags, uint mask, out LinuxStat stat);

    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenAt(int directory, string path, int flags, int mode = 0);

    [LibraryImport("libc", EntryPoint = "fstat")]
    private static partial int MacStat(SafeFileHandle file, out MacFileStat stat);

    [LibraryImport("libc", EntryPoint = "fcntl")]
    private static partial int MacFilePath(SafeFileHandle file, int command, [Out] byte[] path);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int WindowsStat(SafeFileHandle file, out WindowsFileStat stat);
}
