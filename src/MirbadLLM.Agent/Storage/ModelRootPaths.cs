using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;

namespace MirbadLLM.Agent.Storage;

/// <summary>Where a validated folder name points (see <see cref="ModelRootPaths.Locate"/>).</summary>
/// <param name="Folder">The folder name as given (one path segment).</param>
/// <param name="Path">Full path of <c>{models root}/{folder}</c> (the link itself when <see cref="IsLink"/>).</param>
/// <param name="Exists">True when a directory (or a link to one) exists there.</param>
/// <param name="IsLink">True when the entry is a symbolic link, junction or other reparse point.</param>
/// <param name="ResolvedPath">
/// The directory whose content is meant: <see cref="Path"/> itself, or the final link target when that target lies inside
/// the models root. Null when the entry does not exist or a link escapes the root.
/// </param>
public sealed record FolderLocation(string Folder, string Path, bool Exists, bool IsLink, string? ResolvedPath);

/// <summary>
/// Path confinement for the <c>models</c> logical volume (design §10.3 "Paths in argv/folders", §6.4 "Path confinement"):
/// a folder is one plain path segment (no <c>..</c>, no separators, no absolute or drive paths), its full path must be a
/// direct child of the models root, links are resolved and must stay inside the root, deletion refuses links and never
/// follows a link inside the tree, and sizes never count bytes behind a link.
/// </summary>
public sealed partial class ModelRootPaths
{
    private static readonly char[] ForbiddenNameChars =
        [.. System.IO.Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Creates the confinement rules for <see cref="AgentStorageOptions.ModelsRoot"/>.</summary>
    public ModelRootPaths(IOptions<AgentStorageOptions> options)
        : this(options?.Value.ModelsRoot ?? throw new ArgumentNullException(nameof(options)))
    {
    }

    /// <summary>Creates the confinement rules for a models root.</summary>
    public ModelRootPaths(string modelsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        ConfiguredRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(modelsRoot));
    }

    /// <summary>The configured root (full path, as configured).</summary>
    public string ConfiguredRoot { get; }

    /// <summary>
    /// The root every path is confined to: <see cref="ConfiguredRoot"/>, or its final target when the root itself is a
    /// link (an operator may point <c>/models</c> at another disk; that is configuration, not a folder escape).
    /// </summary>
    public string Root
    {
        get
        {
            var info = new DirectoryInfo(ConfiguredRoot);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(target.FullName));
            }

            return ConfiguredRoot;
        }
    }

    /// <summary>
    /// Why <paramref name="folder"/> is not an acceptable folder name, or null when it is: one non-empty segment, at most
    /// 255 characters, no separators or <c>:</c>, no control characters, not hidden (leading <c>.</c>, which also covers
    /// <c>.</c> and <c>..</c>), no trailing space or dot, and not rooted.
    /// </summary>
    public static string? NameError(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return "The folder name is empty.";
        }

        if (folder.Length > 255)
        {
            return "The folder name is longer than 255 characters.";
        }

        if (folder.StartsWith('.'))
        {
            return $"'{Printable(folder)}' is not allowed: folder names must not start with '.' (no '..', no hidden folders).";
        }

        if (folder.IndexOfAny(ForbiddenNameChars) >= 0 || folder.Any(char.IsControl))
        {
            return $"'{Printable(folder)}' is not a plain folder name: separators, ':' and control characters are not allowed.";
        }

        if (folder.EndsWith(' ') || folder.EndsWith('.') || folder.StartsWith(' '))
        {
            return $"'{Printable(folder)}' is not allowed: folder names must not start or end with a space or end with '.'.";
        }

        if (System.IO.Path.IsPathRooted(folder))
        {
            return $"'{Printable(folder)}' is an absolute path; give a folder name under the models root.";
        }

        return null;
    }

    /// <summary>
    /// Why <paramref name="folder"/> cannot be a download destination, or null: <see cref="NameError"/> plus the
    /// container helper's own character set (<c>A–Z a–z 0–9 . _ -</c>), so the folder the helper writes is exactly the
    /// folder that was validated (the helper would otherwise rewrite other characters).
    /// </summary>
    public static string? DownloadNameError(string? folder) =>
        NameError(folder) ?? (DownloadNameRegex().IsMatch(folder!)
            ? null
            : $"'{Printable(folder!)}' is not a valid download folder: use letters, digits, '.', '_' and '-' only.");

    /// <summary>
    /// Validates <paramref name="folder"/> and locates it under the root. Throws <see cref="AgentException"/> 400
    /// <c>invalid_request</c> for a bad name or a path that does not stay a direct child of the root.
    /// </summary>
    public FolderLocation Locate(string? folder)
    {
        if (NameError(folder) is { } error)
        {
            throw StorageErrors.InvalidRequest(error);
        }

        string root = Root;
        string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, folder!));
        string? parent = System.IO.Path.GetDirectoryName(path);
        if (parent is null || !string.Equals(System.IO.Path.TrimEndingDirectorySeparator(parent), root, PathComparison))
        {
            throw StorageErrors.InvalidRequest($"'{Printable(folder!)}' does not resolve to a folder directly under the models root.");
        }

        var info = new DirectoryInfo(path);
        bool isLink = IsLink(info);
        if (!isLink)
        {
            return new FolderLocation(folder!, path, info.Exists, IsLink: false, info.Exists ? path : null);
        }

        string? resolved = null;
        try
        {
            if (info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                string full = System.IO.Path.GetFullPath(target.FullName);
                if (IsInside(root, full) && Directory.Exists(full))
                {
                    resolved = full;
                }
            }
        }
        catch (IOException)
        {
            // A dangling or cyclic link resolves to nothing; it is still a link (and is never followed).
        }

        return new FolderLocation(folder!, path, info.Exists || resolved is not null, IsLink: true, resolved);
    }

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="root"/> (both full paths).</summary>
    public static bool IsInside(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        string prefix = System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison) && path.Length > prefix.Length;
    }

    /// <summary>True for a symbolic link, junction or any other reparse point (never followed by this class).</summary>
    public static bool IsLink(FileSystemInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        try
        {
            return info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Bytes of the regular files under <paramref name="directory"/>; links (files or directories) are not followed or counted.</summary>
    public static long SizeOf(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };

        long total = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", options))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LinkTarget is null)
                {
                    total += info.Length;
                }
            }
            catch (IOException)
            {
                // Mid-write or just removed; skip this sample.
            }
        }

        return total;
    }

    /// <summary>
    /// Deletes <paramref name="directory"/> and everything under it without following links: a link inside the tree is
    /// removed as a link, its target is never touched. The caller has already refused a <paramref name="directory"/>
    /// that is itself a link.
    /// </summary>
    public static void DeleteTree(string directory)
    {
        var root = new DirectoryInfo(directory);
        if (!root.Exists)
        {
            return;
        }

        if (IsLink(root))
        {
            throw new IOException($"Refusing to delete through a link: {directory}");
        }

        DeleteContents(root);
        ClearReadOnly(root);
        root.Delete(recursive: false);
    }

    private static void DeleteContents(DirectoryInfo directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };

        foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
        {
            if (IsLink(entry))
            {
                RemoveLink(entry);
            }
            else if (entry is DirectoryInfo child)
            {
                DeleteContents(child);
                ClearReadOnly(child);
                child.Delete(recursive: false);
            }
            else
            {
                ClearReadOnly(entry);
                entry.Delete();
            }
        }
    }

    private static void RemoveLink(FileSystemInfo link)
    {
        if (OperatingSystem.IsWindows() && link.Attributes.HasFlag(FileAttributes.Directory))
        {
            // A directory symlink or junction: RemoveDirectory removes the link, never the target's content.
            Directory.Delete(link.FullName, recursive: false);
        }
        else
        {
            // unlink(2) removes the link itself, whatever it points at.
            File.Delete(link.FullName);
        }
    }

    private static void ClearReadOnly(FileSystemInfo info)
    {
        if (OperatingSystem.IsWindows() && info.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            info.Attributes &= ~FileAttributes.ReadOnly;
        }
    }

    private static string Printable(string value) =>
        new(value.Select(c => char.IsControl(c) ? '?' : c).Take(80).ToArray());

    [GeneratedRegex("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadNameRegex();
}
