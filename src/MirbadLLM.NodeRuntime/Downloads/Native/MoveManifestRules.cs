using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.NodeRuntime.Downloads.Native;

/// <summary>The same portable payload and digest rules on the exporting agent, master, and importing agent.</summary>
public static class MoveManifestRules
{
    private static readonly NativeDownloadOptions Limits = new();

    public static long Validate(MoveFolderManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Version != 1 || manifest.Files is not { Count: > 0 } files || files.Count > Limits.MaxFiles ||
            files.Any(file => file is null || file.Size < 0 || !ContentDigest.IsHex(file.Sha256, 64) ||
                file.Sha256.Any(c => c is >= 'A' and <= 'F') || Excluded(file.Path)) ||
            RepoPathRules.CheckAll(files.Select(file => file.Path)) is not null ||
            !files.Select(file => file.Path).SequenceEqual(files.Select(file => file.Path).Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException("The checkpoint manifest contains unsupported or conflicting entries.");
        }

        if (JsonSerializer.SerializeToUtf8Bytes(manifest, AgentJson.Options).LongLength > Limits.MaxListingBytes ||
            MoveFolderManifest.IdFor(files) != manifest.ManifestId)
        {
            throw new InvalidDataException("The checkpoint manifest is too large or its ID does not match its payload.");
        }

        try
        {
            return files.Sum(file => file.Size);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("The checkpoint size exceeds the supported range.", ex);
        }
    }

    public static IReadOnlyList<string> EnumeratePayload(string directory, CancellationToken ct)
    {
        var paths = new List<string>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(directory));
        while (pending.TryPop(out var parent))
        {
            ct.ThrowIfCancellationRequested();
            if (RepoPathRules.IsLink(parent))
            {
                throw new InvalidDataException("A checkpoint directory is a link.");
            }

            foreach (var entry in parent.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                ct.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(directory, entry.FullName).Replace(Path.DirectorySeparatorChar, '/');
                if (Excluded(relative))
                {
                    continue;
                }

                if (RepoPathRules.IsLink(entry) || RepoPathRules.Check(relative) is not null)
                {
                    throw new InvalidDataException("The checkpoint contains an unsafe file or directory.");
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                    continue;
                }

                paths.Add(relative);
                if (paths.Count > Limits.MaxFiles)
                {
                    throw new InvalidDataException("The checkpoint has too many files.");
                }
            }
        }

        paths.Sort(StringComparer.Ordinal);
        if (RepoPathRules.CheckAll(paths) is not null)
        {
            throw new InvalidDataException("The checkpoint contains conflicting portable paths.");
        }

        return paths;
    }

    private static bool Excluded(string? relative)
    {
        if (relative is null)
        {
            return true;
        }

        var segments = relative.Split('/');
        return segments.Any(segment => segment is ".git" or ".hg" or ".svn") ||
            relative is ".cache/huggingface" || relative.StartsWith(".cache/huggingface/", StringComparison.Ordinal) ||
            relative is DownloadManifest.FileName or LegacyNames.DownloadMarkerFile ||
            relative == DownloadManifest.FileName + ".tmp";
    }
}
