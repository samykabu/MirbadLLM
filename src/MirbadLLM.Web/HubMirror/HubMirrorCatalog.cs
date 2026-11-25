using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Downloads.Native;

namespace MirbadLLM.Web.HubMirror;

public sealed class HubMirrorOptions
{
    public const string SectionName = "MirbadLLM:HubMirror";
    public bool Enabled { get; set; }
    public int Port { get; set; } = 8444;
    public string? ModelsRoot { get; set; }
}

/// <summary>Finds candidate folders. The content verifier establishes provenance using the official digest.</summary>
public sealed class HubMirrorCatalog(string root, IModelRegistry models, string bootstrapNodeId)
{
    public string Root { get; } = Path.GetFullPath(root);

    public async Task<IReadOnlyList<string>> FindCandidatesAsync(string repoId, string commit, string path, CancellationToken ct)
    {
        var registered = (await models.ListAsync(ct).ConfigureAwait(false))
            .Where(model => model.Placement is null || model.Placement.NodeId == bootstrapNodeId)
            .SelectMany(model => model.Sources)
            .Where(source => source.Kind == ModelSourceKind.HuggingFace &&
                string.Equals(source.RepoId, repoId, StringComparison.OrdinalIgnoreCase) &&
                (!ContentDigest.IsHex(source.Revision, 40) || string.Equals(source.Revision, commit, StringComparison.OrdinalIgnoreCase)))
            .Select(source => source.Folder).Where(IsFolder);
        var candidates = new HashSet<string>(registered, StringComparer.Ordinal);
        if (!Directory.Exists(Root) || RepoPathRules.IsLink(new DirectoryInfo(Root)))
        {
            return [];
        }

        foreach (string directory in Directory.EnumerateDirectories(Root))
        {
            ct.ThrowIfCancellationRequested();
            string folder = Path.GetFileName(directory);
            if (!IsFolder(folder) || candidates.Contains(folder))
            {
                continue;
            }

            try
            {
                using var file = ConfinedModelFile.OpenManifest(Root, folder);
                if (file is not null && DownloadManifest.Read(file) is
                    { Source: ModelSourceKind.HuggingFace, State: DownloadManifestState.Completed } manifest &&
                    string.Equals(manifest.RepoId, repoId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(manifest.CommitSha, commit, StringComparison.Ordinal) &&
                    manifest.Files.Any(entry => entry.Path == path && entry.State == DownloadFileState.Verified))
                {
                    candidates.Add(folder);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Unreadable or invalid local hints do not authorize content.
            }
        }

        return [.. candidates];
    }

    private static bool IsFolder(string folder) =>
        RepoPathRules.Check(folder) is null && !folder.Contains('/') && !folder.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);
}
