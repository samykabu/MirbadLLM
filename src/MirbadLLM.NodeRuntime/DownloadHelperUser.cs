using System.Globalization;

namespace MirbadLLM.NodeRuntime;

/// <summary>
/// Which user the Hugging Face download helper container runs as (MN-126). The vLLM image's default user is root,
/// so folders a root helper writes are root-owned and a non-root agent (uid 1654 in the DGX agent container, the
/// <c>mirbadllm-agent</c> service user on a Linux PC) cannot delete them. <see cref="Auto"/> runs the helper as the
/// agent process's own effective uid:gid instead, read from <c>/proc/self/status</c>.
/// </summary>
public static class DownloadHelperUser
{
    /// <summary>The agent's own uid:gid on Linux when it is not root and can write the models root; else the image default.</summary>
    public const string Auto = "auto";

    /// <summary>The image's default user (root for the vLLM image): the behaviour before MN-126.</summary>
    public const string Image = "image";

    /// <summary>Writable home for a helper that runs as a uid with no passwd entry in the image (Docker then sets HOME=/).</summary>
    public const string HelperHome = "/tmp";

    private const string ProcStatusPath = "/proc/self/status";

    /// <summary>
    /// The Docker <c>User</c> for the helper, or null for the image default.
    /// </summary>
    /// <param name="configured"><see cref="Auto"/> (also null or blank), <see cref="Image"/>, or a literal <c>uid[:gid]</c> / name passed as is.</param>
    /// <param name="processIdentity">The agent's effective uid and gid; null when unknown (not Linux, no <c>/proc</c>).</param>
    /// <param name="modelsRootWritable">
    /// Whether the agent process can create entries in the models root. Only asked in <see cref="Auto"/> mode for a
    /// non-root identity: a helper running as that identity could not create the model folder either, so the image
    /// default is kept (downloads keep working; deleting what they write keeps needing root, as before).
    /// </param>
    public static string? Resolve(string? configured, (long Uid, long Gid)? processIdentity, Func<bool> modelsRootWritable)
    {
        ArgumentNullException.ThrowIfNull(modelsRootWritable);
        string value = configured?.Trim() ?? "";
        if (string.Equals(value, Image, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (value.Length > 0 && !string.Equals(value, Auto, StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        if (processIdentity is not { } identity || identity.Uid == 0 || !modelsRootWritable())
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{identity.Uid}:{identity.Gid}");
    }

    /// <summary>The effective uid and gid (second field of the <c>Uid:</c> / <c>Gid:</c> lines) of a <c>/proc/*/status</c> dump; null when either is missing.</summary>
    public static (long Uid, long Gid)? ParseProcStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        long? uid = null;
        long? gid = null;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                uid = EffectiveId(line);
            }
            else if (line.StartsWith("Gid:", StringComparison.Ordinal))
            {
                gid = EffectiveId(line);
            }
        }

        return uid is { } u && gid is { } g ? (u, g) : null;
    }

    /// <summary>This process's effective uid and gid on Linux; null elsewhere or when <c>/proc/self/status</c> cannot be read.</summary>
    public static (long Uid, long Gid)? ReadProcessIdentity()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return ParseProcStatus(File.ReadAllText(ProcStatusPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when this process can create a file directly in <paramref name="directory"/> (a probe file, removed at once).</summary>
    public static bool CanCreateIn(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, $".ss-write-probe-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long? EffectiveId(string line)
    {
        // "Uid:\t<real>\t<effective>\t<saved>\t<filesystem>"
        string[] fields = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 3 && long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : null;
    }
}
