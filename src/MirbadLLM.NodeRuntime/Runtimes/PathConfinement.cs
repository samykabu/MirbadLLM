namespace MirbadLLM.NodeRuntime.Runtimes;

/// <summary>
/// Confines a relative path to a root after resolving symlinks (design §10.3: "an optional <c>subPath</c> must be a
/// plain relative path that resolves, after resolving symlinks, inside the volume root").
/// </summary>
public static class PathConfinement
{
    private const int MaxLinkHops = 40;

    private static readonly char[] Separators = ['/', '\\'];

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Why <paramref name="relativePath"/> is not a plain relative path (empty, rooted, drive or <c>\</c>, <c>.</c> or
    /// <c>..</c> segments, empty segments, control characters); null when it is one.
    /// </summary>
    public static string? CheckPlainRelative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return "the path is empty";
        }

        if (relativePath.Contains('\\', StringComparison.Ordinal) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            return "the path must use '/' and contain no ':' or '\\'";
        }

        if (relativePath.StartsWith('/') || Path.IsPathRooted(relativePath))
        {
            return "the path must be relative";
        }

        if (relativePath.Any(char.IsControl))
        {
            return "the path contains control characters";
        }

        foreach (string segment in relativePath.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return "the path must not contain empty, '.' or '..' segments";
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> (already a plain relative path, see <see cref="CheckPlainRelative"/>)
    /// under <paramref name="root"/>, following every symlink on the way, and returns the resolved path relative to the
    /// resolved root with '/' separators. Null when it escapes the root, does not exist or the links loop.
    /// </summary>
    public static string? ResolveInside(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentException.ThrowIfNullOrEmpty(relativePath);

        string? realRoot = RealPath(root);
        if (realRoot is null || !Directory.Exists(realRoot))
        {
            return null;
        }

        string? resolved = RealPath(Path.Combine(realRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (resolved is null || !IsInside(realRoot, resolved) || (!Directory.Exists(resolved) && !File.Exists(resolved)))
        {
            return null;
        }

        return Path.GetRelativePath(realRoot, resolved).Replace('\\', '/');
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or below it (both absolute and resolved).</summary>
    public static bool IsInside(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        string trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        string trimmedPath = Path.TrimEndingDirectorySeparator(path);
        if (string.Equals(trimmedRoot, trimmedPath, PathComparison))
        {
            return true;
        }

        string prefix = trimmedRoot.EndsWith(Path.DirectorySeparatorChar) ? trimmedRoot : trimmedRoot + Path.DirectorySeparatorChar;
        return trimmedPath.StartsWith(prefix, PathComparison);
    }

    /// <summary>
    /// The absolute path with every symlink component resolved (like <c>realpath</c>, but missing trailing components
    /// are kept as they are). Null when links loop or a link cannot be read.
    /// </summary>
    internal static string? RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full) ?? "";
        var pending = new List<string>(full[current.Length..].Split(Separators, StringSplitOptions.RemoveEmptyEntries));
        int hops = 0;

        while (pending.Count > 0)
        {
            string segment = pending[0];
            pending.RemoveAt(0);
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            string candidate = Path.Combine(current, segment);
            string? target;
            try
            {
                target = Directory.Exists(candidate) ? new DirectoryInfo(candidate).LinkTarget : new FileInfo(candidate).LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (target is null)
            {
                current = candidate;
                continue;
            }

            if (++hops > MaxLinkHops)
            {
                return null;
            }

            // Re-walk the link target component by component (it may itself contain links or '..').
            pending.InsertRange(0, target.Split(Separators, StringSplitOptions.RemoveEmptyEntries)
                .Skip(Path.IsPathRooted(target) ? CountRootSegments(target) : 0));
            if (Path.IsPathRooted(target))
            {
                current = Path.GetPathRoot(Path.GetFullPath(target)) ?? current;
            }
        }

        return current;
    }

    /// <summary>Segments of a rooted path's root (<c>C:</c> on Windows, none for <c>/</c>).</summary>
    private static int CountRootSegments(string rootedPath)
    {
        string root = Path.GetPathRoot(rootedPath) ?? "";
        return root.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
