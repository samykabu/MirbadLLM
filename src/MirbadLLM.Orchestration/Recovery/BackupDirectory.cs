using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>One backup file in the backup directory.</summary>
public sealed record BackupFileInfo(string Name, long Bytes, DateTimeOffset CreatedAt);

/// <summary>Whether the backup directory can take a backup; <see cref="Problem"/> says why not, for the UI.</summary>
public sealed record BackupDirectoryCheck(string Path, bool Ready, string? Problem);

/// <summary>What is mounted at the backup directory as this process sees it (from <c>/proc/self/mountinfo</c>).</summary>
public sealed record BackupDirectoryMount(string FileSystemType, string Source)
{
    /// <summary>An SMB or NFS share, as opposed to a bind mount of a directory on the host's own disk.</summary>
    public bool IsNetworkShare => FileSystemType is "cifs" or "smb3" or "nfs" or "nfs4";
}

/// <summary>
/// The scheduled-backup directory (<see cref="BackupOptions.Directory"/>, a mounted host directory): atomic
/// writes, listing, pruning to the newest N, and safe lookup of a file by name for download. Only names of the
/// form <c>mirbadllm-backup-&lt;host&gt;-&lt;yyyyMMdd-HHmmss&gt;.json</c> are listed, pruned or served, so nothing
/// else in the directory is ever touched. The directory is never created here: a missing directory almost always
/// means a missing volume mount, and writing into the container's own filesystem would hide that.
/// </summary>
public sealed partial class BackupDirectory
{
    private readonly IOptions<BackupOptions> options;
    private readonly Func<string?> readMountInfo;

    public BackupDirectory(IOptions<BackupOptions> options)
        : this(options, ReadOwnMountInfo)
    {
    }

    /// <param name="options">The backup options.</param>
    /// <param name="readMountInfo">Returns the text of <c>/proc/self/mountinfo</c> (null when unavailable); replaceable for tests.</param>
    public BackupDirectory(IOptions<BackupOptions> options, Func<string?> readMountInfo)
    {
        this.options = options;
        this.readMountInfo = readMountInfo;
    }

    public string Path => options.Value.Directory?.Trim() ?? "";

    /// <summary>
    /// The topmost mount at exactly <see cref="Path"/> — inside the container that is the <c>/backups</c> bind mount,
    /// or the NAS share on top of it once the host mounted one and it propagated (<c>rslave</c>). Read fresh on every
    /// call; null when the path is not a mount point or mountinfo is unavailable (not Linux).
    /// </summary>
    public BackupDirectoryMount? CurrentMount()
    {
        if (!Enabled)
        {
            return null;
        }

        string? text;
        try
        {
            text = readMountInfo();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return FindTopMount(text, System.IO.Path.TrimEndingDirectorySeparator(Path));
    }

    /// <summary>Parses mountinfo (proc(5)) for the last-listed (topmost) mount whose mount point is <paramref name="target"/>.</summary>
    public static BackupDirectoryMount? FindTopMount(string? mountInfo, string target)
    {
        BackupDirectoryMount? found = null;
        foreach (string rawLine in (mountInfo ?? "").Split('\n'))
        {
            string[] fields = rawLine.TrimEnd('\r').Split(' ');
            int separator = Array.IndexOf(fields, "-");
            if (fields.Length < 7 || separator < 6 || separator + 2 >= fields.Length)
            {
                continue;
            }

            if (string.Equals(UnescapeMountField(fields[4]), target, StringComparison.Ordinal))
            {
                found = new BackupDirectoryMount(fields[separator + 1], UnescapeMountField(fields[separator + 2]));
            }
        }

        return found;
    }

    private static string UnescapeMountField(string value) =>
        MountEscape().Replace(value, m => ((char)Convert.ToInt32(m.Value[1..], 8)).ToString());

    private static string? ReadOwnMountInfo() =>
        OperatingSystem.IsLinux() && File.Exists("/proc/self/mountinfo") ? File.ReadAllText("/proc/self/mountinfo") : null;

    public bool Enabled => Path.Length > 0;

    /// <summary>Probes the directory by writing and deleting a small file.</summary>
    public BackupDirectoryCheck Check()
    {
        if (!Enabled)
        {
            return new BackupDirectoryCheck("", false, "Scheduled backups are off: MirbadLLM:Backup:Directory is empty.");
        }

        if (!Directory.Exists(Path))
        {
            return new BackupDirectoryCheck(Path, false,
                $"{Path} does not exist. Mount a host directory there (MIRBADLLM_BACKUPS in deploy/.env) and restart mirbadllm.");
        }

        string probe = System.IO.Path.Combine(Path, $".mirbadllm-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return new BackupDirectoryCheck(Path, true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BackupDirectoryCheck(Path, false,
                $"{Path} is not writable by mirbadllm (uid 1654): {ex.Message}");
        }
    }

    /// <summary>Writes <paramref name="content"/> as <paramref name="fileName"/>: temp file, then a move, so a reader never sees half a file.</summary>
    /// <exception cref="ArgumentException">The name is not a backup file name.</exception>
    public async Task<BackupFileInfo> WriteAsync(string fileName, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!IsBackupFileName(fileName))
        {
            throw new ArgumentException($"'{fileName}' is not a backup file name.", nameof(fileName));
        }

        string target = System.IO.Path.Combine(Path, fileName);
        string temp = System.IO.Path.Combine(Path, $".{fileName}.tmp");
        await File.WriteAllBytesAsync(temp, content, ct).ConfigureAwait(false);

        // On Windows (dev/test) a reader or scanner briefly holding the target makes the replace fail with
        // access denied; retry a few times. On Linux the rename is atomic and succeeds first time.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: true);
                break;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(20 * attempt, ct).ConfigureAwait(false);
            }
        }

        return Describe(new FileInfo(target));
    }

    /// <summary>Backup files, newest first. Empty when the directory is missing or unreadable.</summary>
    public IReadOnlyList<BackupFileInfo> List()
    {
        if (!Enabled || !Directory.Exists(Path))
        {
            return [];
        }

        try
        {
            var directory = new DirectoryInfo(Path);
            return directory.EnumerateFiles("mirbadllm-backup-*.json")
                .Concat(directory.EnumerateFiles(LegacyNames.BackupFilePrefix + "*.json")) // written before the rename
                .Where(f => IsBackupFileName(f.Name))
                .Select(Describe)
                .OrderByDescending(f => f.CreatedAt)
                .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> backup files; returns how many were deleted.</summary>
    public int Prune(int keep)
    {
        int deleted = 0;
        foreach (var file in List().Skip(Math.Max(1, keep)))
        {
            try
            {
                File.Delete(System.IO.Path.Combine(Path, file.Name));
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left for the next run.
            }
        }

        return deleted;
    }

    /// <summary>
    /// Full path of an existing backup file, or null. <paramref name="name"/> must be a bare backup file name —
    /// no directory parts, no traversal — and the resolved path must still be inside the backup directory.
    /// </summary>
    public string? Resolve(string? name)
    {
        if (!Enabled || name is null || !IsBackupFileName(name) || !string.Equals(System.IO.Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            return null;
        }

        string root = System.IO.Path.GetFullPath(Path);
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, name));
        string rootWithSeparator = root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.Ordinal) || !File.Exists(full))
        {
            return null;
        }

        return full;
    }

    /// <summary><c>mirbadllm-backup-&lt;host&gt;-&lt;yyyyMMdd-HHmmss&gt;.json</c>, nothing else.</summary>
    public static bool IsBackupFileName(string? name) => name is not null && FileNamePattern().IsMatch(name);

    private static BackupFileInfo Describe(FileInfo file)
    {
        var match = FileNamePattern().Match(file.Name);
        DateTimeOffset created = match.Success &&
            DateTime.TryParseExact(match.Groups["stamp"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp)
            ? new DateTimeOffset(stamp, TimeSpan.Zero)
            : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
        return new BackupFileInfo(file.Name, file.Length, created);
    }

    [GeneratedRegex(@"\\[0-7]{3}")]
    private static partial Regex MountEscape();

    // Also the files written before the rename (LegacyNames.BackupFilePrefix).
    [GeneratedRegex(@"^(?:mirbadllm|spark-serve)-backup-[A-Za-z0-9][A-Za-z0-9.-]{0,63}-(?<stamp>\d{8}-\d{6})\.json$")]
    private static partial Regex FileNamePattern();
}
