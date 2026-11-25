using System.Globalization;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Gguf;

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
                file.Sha256.Any(c => c is >= 'A' and <= 'F') || Excluded(file.Path) ||
                file.GitBlobSha1 is { } blob && !ContentDigest.IsHex(blob, 40)) ||
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

        ValidateSplitGguf(files.Select(file => file.Path));
        try
        {
            return files.Sum(file => file.Size);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("The checkpoint size exceeds the supported range.", ex);
        }
    }

    public static void ValidateSplitGguf(IEnumerable<string> paths)
    {
        var names = paths.ToHashSet(StringComparer.Ordinal);
        var sets = new HashSet<(string Prefix, string Suffix, int Count)>();
        foreach (string path in names)
        {
            if (GgufReader.ParseSplitName(path) is not { } split)
            {
                continue;
            }

            if (split.Number < 1 || split.Number > split.Count || split.Count > Limits.MaxFiles)
            {
                throw new InvalidDataException($"Invalid GGUF shard numbering in '{path}'.");
            }

            int number = path.Length - "-00001-of-00002.gguf".Length;
            if (!sets.Add((path[..number], path[(number + 6)..], split.Count)))
            {
                continue;
            }

            for (int part = 1; part <= split.Count; part++)
            {
                string shard = string.Create(CultureInfo.InvariantCulture, $"{path[..number]}-{part:D5}{path[(number + 6)..]}");
                if (!names.Contains(shard))
                {
                    throw new InvalidDataException($"Checkpoint shard '{shard}' is missing (split set '{path}').");
                }
            }
        }
    }

    public static async Task ValidateSafetensorsIndexAsync(string path, Stream index, IReadOnlySet<string> names, CancellationToken ct)
    {
        if (index.Length > Limits.MaxListingBytes)
        {
            throw new InvalidDataException($"Checkpoint index '{path}' exceeds the size limit.");
        }

        try
        {
            index.Position = 0;
            using var json = await JsonDocument.ParseAsync(index, cancellationToken: ct).ConfigureAwait(false);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("weight_map", out var map) || map.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Checkpoint index '{path}' has no valid weight_map.");
            }

            var parent = path[..(path.LastIndexOf('/') + 1)];
            foreach (var member in map.EnumerateObject())
            {
                if (member.Value.ValueKind != JsonValueKind.String ||
                    member.Value.GetString() is not { } name || RepoPathRules.Check(name) is not null)
                {
                    throw new InvalidDataException($"Checkpoint index '{path}' contains an unsafe shard reference.");
                }

                string shard = parent + name;
                if (!names.Contains(shard))
                {
                    throw new InvalidDataException($"Checkpoint shard '{shard}' is missing (index '{path}').");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Checkpoint index '{path}' contains invalid JSON.", ex);
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
