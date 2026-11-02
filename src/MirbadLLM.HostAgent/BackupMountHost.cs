using System.Runtime.InteropServices;

namespace MirbadLLM.HostAgent;

/// <summary>
/// The file-system side of the backup mount, abstracted so <see cref="BackupMountManager"/> is testable off-Linux
/// with an in-memory fake. Processes (mount, umount, systemctl, stat) go through <see cref="IProcessRunner"/>.
/// </summary>
internal interface IBackupMountHost
{
    /// <summary>The file's text, or null when it does not exist.</summary>
    string? ReadFile(string path);

    /// <summary>Writes atomically (temp file in the same directory, then rename) with exactly <paramref name="mode"/>.</summary>
    void WriteFile(string path, string content, UnixFileMode mode);

    /// <summary>Deletes the file; nothing happens when it does not exist.</summary>
    void DeleteFile(string path);

    /// <summary>The text of <c>/proc/self/mountinfo</c>.</summary>
    string ReadMountInfo();

    /// <summary>Total and free bytes of the file system holding <paramref name="path"/>, or null when unknown.</summary>
    (long Total, long Free)? GetSpace(string path);

    /// <summary>Creates a fresh, empty probe directory under <paramref name="root"/> and returns its path.</summary>
    string CreateProbeDirectory(string root);

    /// <summary>Removes an (unmounted, empty) probe directory; ignores failures.</summary>
    void DeleteProbeDirectory(string path);

    /// <summary>
    /// Writes, reads back and deletes a small file in <paramref name="directory"/> with the file-system identity of
    /// <paramref name="uid"/>/<paramref name="gid"/>. Null on success, otherwise what failed.
    /// </summary>
    string? ProbeWrite(string directory, int uid, int gid);
}

/// <summary>
/// The real host. <see cref="ProbeWrite"/> switches only its own, dedicated thread's file-system uid/gid
/// (<c>setfsuid</c>/<c>setfsgid</c> are per thread on Linux and drop root's file capabilities), so the probe proves
/// that the controller's user (uid 1654) can write — not merely root.
/// </summary>
internal sealed partial class LinuxBackupMountHost : IBackupMountHost
{
    public string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    public void WriteFile(string path, string content, UnixFileMode mode)
    {
        string directory = Path.GetDirectoryName(path) ?? "/";
        string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (OperatingSystem.IsLinux())
            {
                options.UnixCreateMode = mode;
            }

            using (var stream = new FileStream(temp, options))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
            }

            if (OperatingSystem.IsLinux())
            {
                // UnixCreateMode is masked by the umask; set the exact mode.
                File.SetUnixFileMode(temp, mode);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public string ReadMountInfo()
    {
        try
        {
            return File.ReadAllText("/proc/self/mountinfo");
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    public (long Total, long Free)? GetSpace(string path)
    {
        try
        {
            var drive = new DriveInfo(path);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public string CreateProbeDirectory(string root)
    {
        string path = Path.Combine(root, $"mirbadllm-probe-{Guid.NewGuid():N}");
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(path);
        }

        return path;
    }

    public void DeleteProbeDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still mounted (lazy unmount pending) or not empty; it lives in the agent's private /tmp and goes with it.
        }
    }

    public string? ProbeWrite(string directory, int uid, int gid)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "The write test needs Linux.";
        }

        string? result = null;
        var thread = new Thread(() => result = ProbeOnThisThread(directory, uid, gid))
        {
            IsBackground = true,
            Name = "backup-mount-probe",
        };
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20)))
        {
            return "The write test did not finish within 20 seconds; the share is too slow or hung.";
        }

        return result;
    }

    private static string? ProbeOnThisThread(string directory, int uid, int gid)
    {
        // Order matters: the gid first, while the thread still has root's fsuid.
        _ = NativeMethods.SetFsGid((uint)gid);
        _ = NativeMethods.SetFsUid((uint)uid);
        try
        {
            // setfsuid reports the previous value; a second call with the same id confirms it took effect.
            if (NativeMethods.SetFsUid((uint)uid) != uid || NativeMethods.SetFsGid((uint)gid) != gid)
            {
                return "Could not switch to the controller's user for the write test.";
            }

            string file = Path.Combine(directory, $".mirbadllm-write-test-{Guid.NewGuid():N}");
            byte[] payload = System.Text.Encoding.ASCII.GetBytes($"mirbadllm backup destination test {DateTimeOffset.UtcNow:O}\n");
            try
            {
                File.WriteAllBytes(file, payload);
                byte[] back = File.ReadAllBytes(file);
                if (!back.AsSpan().SequenceEqual(payload))
                {
                    return "A test file written to the share read back differently.";
                }
            }
            finally
            {
                TryDelete(file);
            }

            return File.Exists(file) ? "A test file on the share could not be deleted, so old backups could not be pruned." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"The share is mounted but the controller's user (uid {uid}) cannot write to it: {ex.Message}";
        }
        finally
        {
            _ = NativeMethods.SetFsUid(0);
            _ = NativeMethods.SetFsGid(0);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "setfsuid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static partial int SetFsUid(uint fsuid);

        [LibraryImport("libc", EntryPoint = "setfsgid")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        public static partial int SetFsGid(uint fsgid);
    }
}
