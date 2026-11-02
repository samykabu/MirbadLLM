using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>
/// Windows confinement by handle (design-p3p4 §A.2.3): the final path of an opened file (<c>GetFinalPathNameByHandle</c>,
/// which resolves junctions, symbolic links and mount points) must lie under the final path of the download folder,
/// compared case-insensitively. Catches a link planted between the name check and the open.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsFinalPath
{
    private const uint FileShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0x0;

    /// <summary>Why <paramref name="file"/> is not under <paramref name="root"/>, or null when it is.</summary>
    public static string? Check(SafeFileHandle file, string root)
    {
        string? rootFinal = FinalPathOfDirectory(root);
        string? fileFinal = FinalPathOf(file);
        if (rootFinal is null || fileFinal is null)
        {
            return "its final path could not be read, so it cannot be proven to stay inside the download folder.";
        }

        string prefix = rootFinal.TrimEnd('\\') + "\\";
        return fileFinal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && fileFinal.Length > prefix.Length
            ? null
            : "it resolves outside the download folder (a link or junction on the way); links are never followed.";
    }

    /// <summary>Why <paramref name="directory"/> is not <paramref name="root"/> or under it after links are resolved, or null.</summary>
    public static string? CheckDirectory(string directory, string root)
    {
        string? rootFinal = FinalPathOfDirectory(root);
        string? directoryFinal = FinalPathOfDirectory(directory);
        if (rootFinal is null || directoryFinal is null)
        {
            return "its folder's final path could not be read, so it cannot be proven to stay inside the download folder.";
        }

        string trimmedRoot = rootFinal.TrimEnd('\\');
        return string.Equals(directoryFinal.TrimEnd('\\'), trimmedRoot, StringComparison.OrdinalIgnoreCase) ||
            directoryFinal.StartsWith(trimmedRoot + "\\", StringComparison.OrdinalIgnoreCase)
            ? null
            : "its folder resolves outside the download folder (a link or junction on the way); links are never followed.";
    }

    /// <summary>The final path of an existing directory, without the <c>\\?\</c> prefix; null when it cannot be read.</summary>
    public static string? FinalPathOfDirectory(string directory)
    {
        using var handle = NativeMethods.CreateFile(directory, 0, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        return handle.IsInvalid ? null : FinalPathOf(handle);
    }

    /// <summary>The final path of an open handle, without the <c>\\?\</c> / <c>\\?\UNC\</c> prefix; null on failure.</summary>
    public static unsafe string? FinalPathOf(SafeFileHandle handle)
    {
        char[] buffer = new char[1024];
        while (true)
        {
            uint length;
            fixed (char* p = buffer)
            {
                length = NativeMethods.GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, FileNameNormalized);
            }

            if (length == 0)
            {
                return null;
            }

            if (length >= buffer.Length)
            {
                if (length > 32_768)
                {
                    return null;
                }

                buffer = new char[length + 1];
                continue;
            }

            string path = new(buffer, 0, (int)length);
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                return @"\\" + path[8..];
            }

            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        }
    }

    private static unsafe partial class NativeMethods
    {
        [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial uint GetFinalPathNameByHandle(SafeFileHandle file, char* path, uint length, uint flags);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static partial SafeFileHandle CreateFile(
            string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);
    }
}
