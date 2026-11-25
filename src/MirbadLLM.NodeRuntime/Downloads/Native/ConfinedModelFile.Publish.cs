using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

public static partial class ConfinedModelFile
{
    /// <summary>Moves a verified staging file into an existing folder without following parent links or replacing a file.</summary>
    public static void Publish(string root, string source, string destination)
    {
        if (RepoPathRules.Check(source) is not null || RepoPathRules.Check(destination) is not null ||
            !source.Contains('/') || !destination.Contains('/'))
        {
            throw new InvalidDataException("The checkpoint publication path is invalid.");
        }

        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string[] from = source.Split('/');
        string[] to = destination.Split('/');
        if (OperatingSystem.IsWindows())
        {
            PublishWindows(root, from, to);
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            throw new IOException("Confined checkpoint publication is unavailable on this operating system.");
        }

        bool mac = OperatingSystem.IsMacOS();
        // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC. Linux arm/arm64 number the first two differently from x86-64
        // (0x10000/0x20000 there mean O_DIRECT/O_LARGEFILE).
        bool armLinux = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;
        int flags = mac ? 0x100000 | 0x100 | 0x1000000
            : armLinux ? 0x4000 | 0x8000 | 0x80000
            : 0x10000 | 0x20000 | 0x80000;
        using var baseDirectory = new SafeFileHandle(OpenAt(mac ? -2 : -100, "/", flags), ownsHandle: true);
        using var rootHandle = OpenUnixParent(baseDirectory, root.Split('/', StringSplitOptions.RemoveEmptyEntries), flags, create: false);
        using var sourceParent = OpenUnixParent(rootHandle, from.AsSpan(0, from.Length - 1), flags, create: false);
        using var targetRoot = OpenUnixParent(rootHandle, to.AsSpan(0, 1), flags, create: false);
        using var targetParent = OpenUnixParent(targetRoot, to.AsSpan(1, to.Length - 2), flags, create: true);
        try
        {
            int moved = mac ? RenameMac(sourceParent, from[^1], targetParent, to[^1], 4)
                : RenameLinux(sourceParent, from[^1], targetParent, to[^1], 1);
            if (moved != 0)
            {
                throw PublishConflict();
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new IOException("The operating system does not support handle-relative, no-overwrite checkpoint publication.", ex);
        }
    }

    private static SafeFileHandle OpenUnixParent(SafeFileHandle start, ReadOnlySpan<string> components, int flags, bool create)
    {
        SafeFileHandle parent = new(OpenAt(start.DangerousGetHandle().ToInt32(), ".", flags), ownsHandle: true);
        try
        {
            foreach (string part in components)
            {
                if (parent.IsInvalid)
                {
                    throw PublishConflict();
                }

                int fd = parent.DangerousGetHandle().ToInt32();
                int next = OpenAt(fd, part, flags);
                if (next < 0 && create)
                {
                    _ = MakeDirectoryAt(parent, part, 0x1ed);
                    next = OpenAt(fd, part, flags);
                }

                parent.Dispose();
                parent = new SafeFileHandle(next, ownsHandle: true);
            }

            if (parent.IsInvalid)
            {
                throw PublishConflict();
            }

            var opened = parent;
            parent = null!;
            return opened;
        }
        finally
        {
            parent?.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void PublishWindows(string root, string[] from, string[] to)
    {
        var held = new List<SafeFileHandle>();
        try
        {
            LockDirectory(root);
            var sourcePath = BuildPath(from, create: false);
            var targetPath = BuildPath(to, create: true);
            if (RepoPathRules.IsLink(new FileInfo(sourcePath)) || !MoveWithoutReplacement(sourcePath, targetPath, 0))
            {
                throw PublishConflict();
            }
        }
        finally
        {
            foreach (var directory in held)
            {
                directory.Dispose();
            }
        }

        string BuildPath(string[] parts, bool create)
        {
            string current = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current = Path.Combine(current, parts[i]);
                if (create && i > 0 && !Directory.Exists(current) && !RepoPathRules.IsLink(new DirectoryInfo(current)))
                {
                    Directory.CreateDirectory(current);
                }

                LockDirectory(current);
            }

            return Path.Combine(current, parts[^1]);
        }

        void LockDirectory(string path)
        {
            var handle = WindowsFinalPath.OpenStableDirectory(path);
            held.Add(handle);
            if (handle.IsInvalid || WindowsStat(handle, out var stat) == 0 ||
                (stat.Attributes & (uint)(FileAttributes.Directory | FileAttributes.ReparsePoint)) != (uint)FileAttributes.Directory ||
                !string.Equals(WindowsFinalPath.FinalPathOf(handle), path, StringComparison.OrdinalIgnoreCase))
            {
                throw PublishConflict();
            }
        }
    }

    private static IOException PublishConflict() =>
        new("The checkpoint publication path changed, is linked, or already contains the destination file.");

    [LibraryImport("libc", EntryPoint = "mkdirat", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int MakeDirectoryAt(SafeFileHandle directory, string path, uint mode);

    [LibraryImport("libc", EntryPoint = "renameat2", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameLinux(SafeFileHandle sourceParent, string source, SafeFileHandle targetParent, string target, uint flags);

    [LibraryImport("libc", EntryPoint = "renameatx_np", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int RenameMac(SafeFileHandle sourceParent, string source, SafeFileHandle targetParent, string target, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWithoutReplacement(string source, string target, uint flags);
}
