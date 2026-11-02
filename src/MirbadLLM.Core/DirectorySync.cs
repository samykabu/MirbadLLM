using System.Runtime.InteropServices;

namespace MirbadLLM.Core;

/// <summary>
/// Makes a rename (or a file creation) in a directory durable by fsync-ing the directory itself (Linux). Without it a
/// power loss can lose the new directory entry while later writes to other files survive: for the operation log that is
/// "snapshot rename lost, log truncation kept", i.e. accepted operations forgotten and re-executed on retry (W2 review
/// MN-092, D45). A no-op on other platforms (Windows agents are P4; NTFS journals the metadata).
/// <para>The agent's op log, fencing, enrollment and rotation state use <see cref="Flush"/> (a failure is an error); the
/// master's stores (<c>master.json</c>, <c>nodes.json</c>, <c>models.json</c>, API keys, operation references and
/// <c>PkiFiles</c>) use <see cref="TryFlushParent"/> after their rename: best effort, since each of them is fail-safe
/// when a rename is lost (D69, MN-047).</para>
/// </summary>
public static partial class DirectorySync
{
    private const int ReadOnly = 0; // O_RDONLY; opening a directory read-only is enough for fsync(2)

    /// <summary>fsyncs <paramref name="directory"/>; throws <see cref="IOException"/> when it cannot be opened or flushed.</summary>
    public static void Flush(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int fd = NativeMethods.Open(directory, ReadOnly);
        if (fd < 0)
        {
            throw new IOException($"Cannot open directory '{directory}' to flush it (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            if (NativeMethods.Fsync(fd) != 0)
            {
                throw new IOException($"fsync of directory '{directory}' failed (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
        finally
        {
            _ = NativeMethods.Close(fd);
        }
    }

    /// <summary>fsyncs the directory that contains <paramref name="path"/>.</summary>
    public static void FlushParent(string path) => Flush(Path.GetDirectoryName(Path.GetFullPath(path))!);

    /// <summary>
    /// Best effort <see cref="FlushParent"/>: false when the directory could not be flushed (the rename itself already
    /// happened; the caller's data is written, only its survival of a power loss in the next moments is not assured).
    /// </summary>
    public static bool TryFlushParent(string path)
    {
        try
        {
            FlushParent(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static partial int Open(string path, int flags);

        [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static partial int Fsync(int fd);

        [LibraryImport("libc", EntryPoint = "close")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static partial int Close(int fd);
    }
}
