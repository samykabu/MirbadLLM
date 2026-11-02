using System.Buffers;
using System.Text.RegularExpressions;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>
/// Confinement of repo file names under a download folder (design §6.4 "Path confinement", design-p3p4 §A.2.3). The
/// Windows rules apply on every OS, so a repo that downloads on Linux also downloads on Windows and the reverse:
/// <list type="bullet">
/// <item>a name is a relative <c>/</c>-separated path: no leading <c>/</c>, no <c>\</c> (so no UNC or <c>\\?\</c>), no
/// <c>:</c> (drive letters, alternate data streams), no empty, <c>.</c> or <c>..</c> segment, no control or
/// <c>&lt;&gt;"|?*</c> characters;</item>
/// <item>no segment is a reserved device name (<c>CON</c>, <c>PRN</c>, <c>AUX</c>, <c>NUL</c>, <c>COM0–9</c>,
/// <c>LPT0–9</c>, also with an extension or the superscript digits) or ends with a dot or a space;</item>
/// <item>names are unique case-insensitively (NTFS would merge them), and none is the download manifest;</item>
/// <item>the full path resolves under the folder; directories are created one segment at a time and an existing link
/// (symlink, junction, any reparse point) anywhere on the way is refused, never followed; no link is ever created.</item>
/// </list>
/// </summary>
public static partial class RepoPathRules
{
    /// <summary>Longest repo-relative path accepted.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>Longest path segment accepted (NTFS and ext4 limit).</summary>
    public const int MaxSegmentLength = 255;

    private static readonly SearchValues<char> ForbiddenChars = SearchValues.Create("\\:<>\"|?*");

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Why <paramref name="repoPath"/> cannot be written under a download folder, or null when it can.</summary>
    public static string? Check(string? repoPath)
    {
        if (string.IsNullOrEmpty(repoPath))
        {
            return "an empty file name";
        }

        string shown = Printable(repoPath);
        if (repoPath.Length > MaxPathLength)
        {
            return $"'{shown}' is longer than {MaxPathLength} characters";
        }

        if (repoPath.Any(char.IsControl))
        {
            return $"'{shown}' contains control characters";
        }

        if (repoPath.AsSpan().IndexOfAny(ForbiddenChars) >= 0)
        {
            return $"'{shown}' contains '\\', ':' or another character Windows does not allow (drive letters, UNC paths and alternate data streams are refused)";
        }

        if (repoPath.StartsWith('/') || Path.IsPathRooted(repoPath))
        {
            return $"'{shown}' is an absolute path";
        }

        string[] segments = repoPath.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return $"'{shown}' has an empty, '.' or '..' segment";
            }

            if (segment.Length > MaxSegmentLength)
            {
                return $"'{shown}' has a segment longer than {MaxSegmentLength} characters";
            }

            if (segment.EndsWith('.') || segment.EndsWith(' ') || segment.StartsWith(' '))
            {
                return $"'{shown}' has a segment that starts with a space or ends with a dot or a space";
            }

            if (ReservedDeviceName().IsMatch(segment))
            {
                return $"'{shown}' uses a reserved Windows device name ('{Printable(segment)}')";
            }
        }

        if (segments.Length == 1 && (segments[0].StartsWith(DownloadManifest.FileName, StringComparison.OrdinalIgnoreCase)
            || segments[0].StartsWith(LegacyNames.DownloadMarkerFile, StringComparison.OrdinalIgnoreCase)))
        {
            return $"'{shown}' would overwrite the download manifest";
        }

        return null;
    }

    /// <summary>
    /// The first problem of a file set: a bad name (<see cref="Check"/>) or two names equal case-insensitively, or a file
    /// that is also a directory of another; null when every name is acceptable.
    /// </summary>
    public static string? CheckAll(IEnumerable<string> repoPaths)
    {
        ArgumentNullException.ThrowIfNull(repoPaths);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (string path in repoPaths)
        {
            if (Check(path) is { } error)
            {
                return error;
            }

            if (!seen.Add(path))
            {
                return $"'{Printable(path)}' appears twice (names are compared case-insensitively)";
            }

            files.Add(path);
            int slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                directories.Add(path[..slash]);
                slash = path.LastIndexOf('/', slash - 1);
            }
        }

        string? clash = files.FirstOrDefault(directories.Contains);
        return clash is null ? null : $"'{Printable(clash)}' is both a file and a directory";
    }

    /// <summary>
    /// The full path of <paramref name="repoPath"/> under <paramref name="folder"/>, with its parent directories created.
    /// Throws <see cref="DownloadPathException"/> when the name is bad, the path leaves the folder, or a directory or the
    /// file itself is a link.
    /// </summary>
    public static string PrepareTarget(string folder, string repoPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        if (Check(repoPath) is { } error)
        {
            throw new DownloadPathException($"Refused repo file {error}.");
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (IsLink(new DirectoryInfo(root)))
        {
            throw new DownloadPathException("The download folder is a link; downloads never write through links.");
        }

        string[] segments = repoPath.Split('/');
        string current = root;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = Path.Combine(current, segments[i]);
            var directory = new DirectoryInfo(current);
            if (IsLink(directory) || (!directory.Exists && File.Exists(current)))
            {
                throw new DownloadPathException($"'{Printable(repoPath)}': '{Printable(segments[i])}' is a link or a file; links are never followed.");
            }

            if (!directory.Exists)
            {
                Directory.CreateDirectory(current);
            }
        }

        string full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
        if (!IsInside(root, full))
        {
            throw new DownloadPathException($"'{Printable(repoPath)}' does not resolve inside the download folder.");
        }

        var file = new FileInfo(full);
        if (IsLink(file) || Directory.Exists(full))
        {
            throw new DownloadPathException($"'{Printable(repoPath)}' exists as a link or a directory; links are never followed.");
        }

        return full;
    }

    /// <summary>
    /// The first link (symlink, junction, reparse point) at or under <paramref name="folder"/>, as a path relative to it,
    /// or null. Links are not followed while searching.
    /// </summary>
    public static string? FindLink(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        var root = new DirectoryInfo(folder);
        if (!root.Exists)
        {
            return null;
        }

        if (IsLink(root))
        {
            return ".";
        }

        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0 };
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos("*", options))
            {
                if (IsLink(entry))
                {
                    return Path.GetRelativePath(root.FullName, entry.FullName).Replace('\\', '/');
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }

        return null;
    }

    /// <summary>True for a symbolic link, junction or other reparse point (or an entry whose kind cannot be read).</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        try
        {
            info.Refresh();
            return info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/> (both full paths).</summary>
    public static bool IsInside(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison) && path.Length > prefix.Length;
    }

    /// <summary>A name safe to print (control characters replaced, at most 120 characters).</summary>
    public static string Printable(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new string(value.Select(c => char.IsControl(c) ? '?' : c).Take(120).ToArray());
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDeviceName();
}
