using System.Globalization;
using System.Text.Json;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>Whether a checkpoint folder is already under the models root.</summary>
public interface ICheckpointFolders
{
    /// <summary>True when <c>{models root}/{folder}</c> exists and holds at least one file outside huggingface_hub's <c>.cache</c>.</summary>
    bool IsPresent(string folder);
}

/// <summary>How a model in the backup compares with the current registry.</summary>
public enum RestoreModelChange
{
    New,
    Changed,
    Identical,
}

/// <summary>One source of a model in the preview: whether its folder is already on disk and, if not, the download size when it could be estimated.</summary>
public sealed record RestoreSourcePreview(ModelSource Source, bool Present, long? DownloadBytes, string? EstimateError);

/// <summary>One model of the backup, as the restore preview shows it.</summary>
/// <param name="ChangedFields">For <see cref="RestoreModelChange.Changed"/>: the settings that differ, in the editor's words.</param>
/// <param name="CheckpointPresent">The primary checkpoint folder (<see cref="ModelDefinition.ModelPath"/>) is already on disk.</param>
/// <param name="Manual">The checkpoint is missing and no source can download it: it has to be copied back by hand.</param>
/// <param name="DownloadBytes">Sum of the known sizes of the sources to download (0 when nothing needs downloading).</param>
/// <param name="DownloadSizeUnknown">At least one source to download has no size estimate.</param>
/// <param name="Problem">Why restoring this model will fail as it stands (an invalid definition, a served name taken by another model).</param>
public sealed record RestoreModelPreview(
    ModelDefinition Model,
    RestoreModelChange Change,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<RestoreSourcePreview> Sources,
    bool CheckpointPresent,
    bool Manual,
    long DownloadBytes,
    bool DownloadSizeUnknown,
    string? Problem)
{
    public bool NeedsDownload => Sources.Any(s => !s.Present);

    /// <summary>
    /// Configuration import (design §9.4, MN-113): how the model's backup node maps onto this master — same uid (kept), slug
    /// only or no match (the operator picks a target, <see cref="RestoreTarget"/>), or unplaced. Null when the planner has no
    /// node registry (the single-box wiring keeps the backup's placement as before).
    /// </summary>
    public RestorePlacementPreview? Placement { get; init; }
}

/// <summary>Everything the restore page shows before anything changes.</summary>
public sealed record RestorePreview(
    DateTimeOffset CreatedAt,
    string AppVersion,
    string HostName,
    IReadOnlyList<RestoreModelPreview> Models,
    bool HasSecrets,
    bool SecretsUnlocked,
    int ApiKeysNew,
    int ApiKeysExisting,
    int HostContainers)
{
    /// <summary>The backup's schema version (1 = single box, 2 = multi-node, design §5.5).</summary>
    public int SchemaVersion { get; init; } = BackupDocument.CurrentSchemaVersion;

    /// <summary>The nodes registered here that a model may be placed on (target picker); empty without a node registry.</summary>
    public IReadOnlyList<RestoreNodeChoice> TargetNodes { get; init; } = [];

    /// <summary>The backup's nodes (version 2), for the "models from node X" group picker; empty for version 1.</summary>
    public IReadOnlyList<BackupNode> BackupNodes { get; init; } = [];

    /// <summary>True when every placed model of the backup kept its node by uid (nothing for the operator to pick).</summary>
    public bool AllPlacementsMatched => Models.All(m => m.Placement is null || !m.Placement.NeedsTarget);

    /// <summary>
    /// Each backup node (version 2): the node here with the same uid, the operator settings an import would change on it,
    /// and its storage as last reported, compared with the node here. Empty without a node registry or nodes section.
    /// </summary>
    public IReadOnlyList<RestoreNodePreview> Nodes { get; init; } = [];
}

/// <summary>One backup node in a configuration import preview.</summary>
/// <param name="BackupNodeId">The node's id in the backup.</param>
/// <param name="LocalNodeId">The node here with the same uid; null = none, so nothing is applied to any node.</param>
/// <param name="SettingChanges">Operator settings that differ here, in words ("memory reserve 8 → 12 GiB"); applied with the node-settings option.</param>
/// <param name="Storage">Storage and images as the node last reported them, compared with the node here. Never applied:
/// host paths and image policy live in the node's <c>agent.json</c>, which the master neither reads nor writes.</param>
/// <param name="StorageDiffers">At least one storage line differs from the node here.</param>
public sealed record RestoreNodePreview(
    string BackupNodeId,
    string? LocalNodeId,
    IReadOnlyList<string> SettingChanges,
    IReadOnlyList<string> Storage,
    bool StorageDiffers);

/// <summary>
/// The node operator settings a configuration import applies to the node here with the same uid (display name, labels,
/// memory and GPU reserves, default images with their approved digests, concurrent starts). Never identity or trust: id,
/// uid, addresses, approval, draining, revocation and certificate pins stay as they are here.
/// </summary>
public static class RestoreNodeSettings
{
    /// <summary>The node here with <paramref name="backup"/>'s uid (not revoked), or null.</summary>
    public static NodeRecord? Match(BackupNode backup, IReadOnlyList<NodeRecord> local)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(local);
        return local.FirstOrDefault(n => !n.Revoked && string.Equals(n.Uid, backup.Uid, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><paramref name="local"/> with <paramref name="backup"/>'s operator settings.</summary>
    public static NodeRecord Apply(NodeRecord local, BackupNode backup)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(backup);
        return local with
        {
            DisplayName = backup.DisplayName,
            Labels = [.. backup.Labels],
            MemoryReserveGiB = backup.MemoryReserveGiB,
            GpuReserveGiB = new Dictionary<string, double>(backup.GpuReserveGiB, StringComparer.Ordinal),
            MaxConcurrentStarts = backup.MaxConcurrentStarts,
            DefaultVllmImage = backup.DefaultVllmImage,
            DefaultVllmImageDigest = backup.DefaultVllmImageDigest,
            DefaultLlamaCppImage = backup.DefaultLlamaCppImage,
            DefaultLlamaCppImageDigest = backup.DefaultLlamaCppImageDigest,
        };
    }

    /// <summary>The settings <see cref="Apply"/> would change, in words; empty when they are the same.</summary>
    public static IReadOnlyList<string> Changes(NodeRecord local, BackupNode backup)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(backup);
        var changes = new List<string>();
        void Add(string label, string? here, string? then)
        {
            if (!string.Equals(here ?? "", then ?? "", StringComparison.Ordinal))
            {
                changes.Add($"{label} {Show(here)} → {Show(then)}");
            }
        }

        Add("display name", local.DisplayName, backup.DisplayName);
        Add("labels", string.Join(", ", local.Labels), string.Join(", ", backup.Labels));
        Add("memory reserve", GiB(local.MemoryReserveGiB), GiB(backup.MemoryReserveGiB));
        Add("GPU reserves", Reserves(local.GpuReserveGiB), Reserves(backup.GpuReserveGiB));
        Add("concurrent starts", local.MaxConcurrentStarts?.ToString(CultureInfo.InvariantCulture), backup.MaxConcurrentStarts?.ToString(CultureInfo.InvariantCulture));
        Add("default vLLM image", local.DefaultVllmImage, backup.DefaultVllmImage);
        Add("vLLM image digest", local.DefaultVllmImageDigest, backup.DefaultVllmImageDigest);
        Add("default llama.cpp image", local.DefaultLlamaCppImage, backup.DefaultLlamaCppImage);
        Add("llama.cpp image digest", local.DefaultLlamaCppImageDigest, backup.DefaultLlamaCppImageDigest);
        return changes;
    }

    /// <summary>
    /// Storage and images of the backup's last report against the node here's, one line each. The master only knows what the
    /// agent reports (volume size, folder count, images present), never its host paths, approved images, tag-pull policy,
    /// download helper image or port range.
    /// </summary>
    public static (IReadOnlyList<string> Lines, bool Differs) CompareStorage(NodeCapabilities? backup, NodeCapabilities? here)
    {
        if (backup is null)
        {
            return (["No storage report in the backup."], false);
        }

        if (here is null)
        {
            return (["This node has not reported its storage yet."], false);
        }

        var lines = new List<string>();
        bool differs = false;
        foreach (var root in backup.ModelRoots)
        {
            var now = here.ModelRoots.FirstOrDefault(r => string.Equals(r.Name, root.Name, StringComparison.Ordinal));
            if (now is null)
            {
                lines.Add($"'{root.Name}' volume: in the backup, not reported here.");
                differs = true;
                continue;
            }

            bool sizeDiffers = root.TotalBytes > 0 && Math.Abs(root.TotalBytes - now.TotalBytes) > root.TotalBytes / 20;
            differs |= sizeDiffers;
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"'{root.Name}' volume: {BackupDestinationStatus.FormatBytes(root.TotalBytes)} with {root.FolderCount} folder(s) in the backup, " +
                $"{BackupDestinationStatus.FormatBytes(now.TotalBytes)} with {now.FolderCount} here{(sizeDiffers ? " (different size)" : "")}."));
        }

        var missing = backup.Images.Select(i => i.Ref).Except(here.Images.Select(i => i.Ref), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            differs = true;
            lines.Add($"Images not on this node: {string.Join(", ", missing)}.");
        }

        return (lines, differs);
    }

    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "(none)" : value;

    private static string GiB(double value) => value.ToString("0.##", CultureInfo.InvariantCulture) + " GiB";

    private static string Reserves(IReadOnlyDictionary<string, double>? reserves) =>
        string.Join(", ", (reserves ?? new Dictionary<string, double>()).OrderBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => $"{r.Key[..Math.Min(12, r.Key.Length)]} {GiB(r.Value)}"));
}

/// <summary>
/// Compares a parsed backup with the current configuration — never changing anything: per model new / changed
/// (which settings) / identical, whether each checkpoint folder is already on disk, what has to be downloaded
/// (and how big, when Hugging Face answers), which models have no source and need a manual copy, and how many
/// API keys are new.
/// </summary>
/// <remarks>
/// Reads backup versions 1 and 2 alike (design §5.5): a version-1 backup's models are shown placed on the bootstrap
/// node, as the restore writes them (<see cref="BackupDocument.WithDefaultPlacement"/>), and the Services count is the
/// bootstrap node's list. A definition here without a placement counts as being on the bootstrap node when compared
/// (in P1 the single-box runtime runs it there).
/// </remarks>
/// <param name="bootstrapNodeId">The bootstrap node id; null = <see cref="RegistryMigrationOptions.DefaultBootstrapNodeId"/>.</param>
/// <param name="nodes">
/// The node registry (P2, MN-113): with it the preview classifies every placement by node uid (design §9.4) and checks
/// folders on each model's target node; without it (the single-box wiring) placements are shown as in the backup.
/// </param>
/// <param name="nodeResources">Checkpoint folders per node; null = <paramref name="folders"/> for every node.</param>
public sealed class RestorePlanner(
    IModelRegistry registry,
    IApiKeyStore apiKeys,
    ICheckpointFolders folders,
    IHuggingFaceRepoInspector? repoInspector = null,
    string? bootstrapNodeId = null,
    INodeRegistry? nodes = null,
    IRestoreNodeResources? nodeResources = null)
{
    private readonly string _bootstrapNodeId =
        string.IsNullOrWhiteSpace(bootstrapNodeId) ? RegistryMigrationOptions.DefaultBootstrapNodeId : bootstrapNodeId.Trim();

    private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["servedName"] = "served name",
        ["modelPath"] = "model path",
        ["quantization"] = "quantization",
        ["kvCacheDtype"] = "KV cache dtype",
        ["maxModelLen"] = "max model len",
        ["maxNumSeqs"] = "max num seqs",
        ["maxNumBatchedTokens"] = "max batched tokens",
        ["memoryBudgetGiB"] = "memory budget",
        ["toolCallParser"] = "tool call parser",
        ["reasoningParser"] = "reasoning parser",
        ["enablePrefixCaching"] = "prefix caching",
        ["speculative"] = "speculative decoding",
        ["cudaGraphMode"] = "CUDA graph mode",
        ["image"] = "image",
        ["extraArgs"] = "extra args",
        ["autoStart"] = "auto-start",
        ["tags"] = "tags",
        ["sources"] = "sources",
        ["placement"] = "node placement",
        ["runtime"] = "runtime",
        ["llamaCpp"] = "llama.cpp settings",
        ["hostMemoryBudgetGiB"] = "host memory budget",
        ["keepServingDuringJobs"] = "keep serving during jobs",
        ["startOnRequest"] = "start on request",
        ["idleStopMinutes"] = "stop after idle",
        ["stopToMakeRoom"] = "may be stopped to make room",
    };

    /// <param name="secrets">The decrypted secrets, or null when the backup has none or they were not unlocked.</param>
    /// <param name="huggingFaceToken">Optional token for estimating sizes of gated repos; used for this call only.</param>
    public async Task<RestorePreview> PreviewAsync(
        BackupDocument backup, BackupSecrets? secrets, string? huggingFaceToken = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        backup = backup.WithDefaultPlacement(_bootstrapNodeId);
        var current = (await registry.ListAsync(ct).ConfigureAwait(false)).ToDictionary(m => m.Id, StringComparer.Ordinal);
        IReadOnlyList<NodeRecord>? localNodes = nodes is null ? null : await nodes.ListAsync(ct).ConfigureAwait(false);

        var models = new List<RestoreModelPreview>(backup.Models.Count);
        foreach (var original in backup.Models.Select(VllmArgsNormalizer.Apply))
        {
            RestorePlacementPreview? placement = null;
            var model = original;
            if (localNodes is not null)
            {
                placement = RestorePlacements.Classify(backup, original, localNodes, _bootstrapNodeId);
                var resolved = RestorePlacements.Resolve(backup, original, localNodes, target: null, _bootstrapNodeId);
                model = original with { Placement = resolved.Placement };
            }

            current.TryGetValue(model.Id, out var existing);
            var changed = existing is null ? [] : ChangedFields(PlacedForComparison(existing), model);
            var change = existing is null ? RestoreModelChange.New : changed.Count == 0 ? RestoreModelChange.Identical : RestoreModelChange.Changed;

            // Folders are checked on the node the model lands on (design §9.4); an unplaced model has none to check.
            var nodeFolders = FoldersOn(model);
            var sources = new List<RestoreSourcePreview>();
            foreach (var source in model.Sources)
            {
                if (nodeFolders.IsPresent(source.Folder))
                {
                    sources.Add(new RestoreSourcePreview(source, true, null, null));
                    continue;
                }

                var (bytes, error) = await EstimateAsync(source, huggingFaceToken, ct).ConfigureAwait(false);
                sources.Add(new RestoreSourcePreview(source, false, bytes, error));
            }

            bool checkpointPresent = !model.NeedsCheckpoint || nodeFolders.IsPresent(model.ModelPath);
            bool primaryHasSource = model.Sources.Any(s => string.Equals(s.Folder, model.ModelPath, StringComparison.Ordinal));
            var missing = sources.Where(s => !s.Present).ToList();

            models.Add(new RestoreModelPreview(
                model,
                change,
                changed,
                sources,
                checkpointPresent,
                Manual: !checkpointPresent && !primaryHasSource,
                DownloadBytes: missing.Sum(s => s.DownloadBytes ?? 0),
                DownloadSizeUnknown: missing.Any(s => s.DownloadBytes is null),
                Problem: FindProblem(model, current.Values))
            {
                Placement = placement,
            });
        }

        int keysNew = 0;
        int keysExisting = 0;
        if (secrets is not null)
        {
            var currentKeys = await apiKeys.ListAsync(ct).ConfigureAwait(false);
            foreach (var key in secrets.ApiKeys)
            {
                bool exists = currentKeys.Any(k => k.Id == key.Id || string.Equals(k.KeyHash, key.KeyHash, StringComparison.OrdinalIgnoreCase));
                if (exists)
                {
                    keysExisting++;
                }
                else
                {
                    keysNew++;
                }
            }
        }

        return new RestorePreview(
            backup.CreatedAt,
            backup.AppVersion,
            backup.HostName,
            models,
            backup.Secrets is not null,
            secrets is not null,
            keysNew,
            keysExisting,
            RestorePlacements.ServicesForBootstrap(backup, localNodes, _bootstrapNodeId).Lists?.Containers?.Count ?? 0)
        {
            SchemaVersion = backup.SchemaVersion,
            TargetNodes = localNodes is null ? [] : RestorePlacements.TargetChoices(localNodes),
            BackupNodes = backup.Nodes ?? [],
            Nodes = localNodes is null ? [] : [.. (backup.Nodes ?? []).Where(n => !n.Revoked).Select(n => PreviewNode(n, localNodes))],
        };
    }

    private static RestoreNodePreview PreviewNode(BackupNode node, IReadOnlyList<NodeRecord> local)
    {
        var here = RestoreNodeSettings.Match(node, local);
        if (here is null)
        {
            return new RestoreNodePreview(node.Id, null, [], [], false);
        }

        var (storage, differs) = RestoreNodeSettings.CompareStorage(node.LastCapabilities, here.LastCapabilities);
        return new RestoreNodePreview(node.Id, here.Id, RestoreNodeSettings.Changes(here, node), storage, differs);
    }

    /// <summary>The folders of the model's node; without a node registry an unplaced model runs on the bootstrap node (P1).</summary>
    private ICheckpointFolders FoldersOn(ModelDefinition model) =>
        model.Placement is null
            ? nodes is null ? nodeResources?.FoldersFor(_bootstrapNodeId) ?? folders : NoFolders.Instance
            : nodeResources?.FoldersFor(model.Placement.NodeId) ?? folders;

    private ModelDefinition PlacedForComparison(ModelDefinition existing) =>
        existing.Placement is null ? existing with { Placement = new ModelPlacement(_bootstrapNodeId) } : existing;

    /// <summary>Which settings differ, compared field by field as they serialize (so new fields are covered without a list to keep in step).</summary>
    public static IReadOnlyList<string> ChangedFields(ModelDefinition before, ModelDefinition after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var a = JsonSerializer.SerializeToElement(before, BackupDocument.JsonOptions);
        var b = JsonSerializer.SerializeToElement(after, BackupDocument.JsonOptions);
        var names = a.EnumerateObject().Select(p => p.Name).Union(b.EnumerateObject().Select(p => p.Name), StringComparer.Ordinal);

        var changed = new List<string>();
        foreach (string name in names)
        {
            string left = a.TryGetProperty(name, out var l) ? l.GetRawText() : "null";
            string right = b.TryGetProperty(name, out var r) ? r.GetRawText() : "null";
            if (!string.Equals(left, right, StringComparison.Ordinal))
            {
                changed.Add(FieldLabels.GetValueOrDefault(name, name));
            }
        }

        return changed;
    }

    private static string? FindProblem(ModelDefinition model, IEnumerable<ModelDefinition> current)
    {
        try
        {
            JsonModelRegistry.Validate(model);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }

        var clash = current.FirstOrDefault(m => m.Id != model.Id && string.Equals(m.ServedName, model.ServedName, StringComparison.OrdinalIgnoreCase));
        return clash is null ? null : $"Served name '{model.ServedName}' is already used by model '{clash.Id}' here; rename or delete that model first.";
    }

    private async Task<(long? Bytes, string? Error)> EstimateAsync(ModelSource source, string? token, CancellationToken ct)
    {
        if (repoInspector is null)
        {
            return (null, null);
        }

        try
        {
            var inspection = await repoInspector.InspectAsync(source.RepoId, source.Revision, string.IsNullOrEmpty(token) ? null : token, ct).ConfigureAwait(false);
            var patterns = source.AllowPatterns is { Count: > 0 } ? source.AllowPatterns : HuggingFacePatterns.DefaultAllowPatterns;
            return (inspection.AllFiles.Where(f => HuggingFacePatterns.MatchesAny(f.Name, patterns)).Sum(f => f.Bytes), null);
        }
        catch (Exception ex) when (ex is HuggingFaceApiException or HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return (null, ex.Message);
        }
    }
}

/// <summary>
/// Per-node checkpoint folders and downloader for a configuration import (design §9.4: folder presence is checked on the
/// target node, downloads run per node). The composition (MN-130) supplies one over the agents
/// (<c>RemoteCheckpointFolders</c>/<c>RemoteHuggingFaceDownloader</c> per node); <see cref="SingleNodeRestoreResources"/> is
/// the single-box default.
/// </summary>
public interface IRestoreNodeResources
{
    /// <summary>The checkpoint folders of <paramref name="nodeId"/>.</summary>
    ICheckpointFolders FoldersFor(string nodeId);

    /// <summary>The downloader that writes into <paramref name="nodeId"/>'s models root.</summary>
    IHuggingFaceDownloader DownloaderFor(string nodeId);
}

/// <summary>The same folders and downloader for every node (the single-box wiring; P1 behaviour).</summary>
public sealed class SingleNodeRestoreResources(ICheckpointFolders folders, IHuggingFaceDownloader downloader) : IRestoreNodeResources
{
    /// <inheritdoc/>
    public ICheckpointFolders FoldersFor(string nodeId) => folders;

    /// <inheritdoc/>
    public IHuggingFaceDownloader DownloaderFor(string nodeId) => downloader;
}

/// <summary>No folder is present (an unplaced model has no node to look on).</summary>
internal sealed class NoFolders : ICheckpointFolders
{
    public static NoFolders Instance { get; } = new();

    public bool IsPresent(string folder) => false;
}

/// <summary>A node registered here that a restored model can be placed on (the target picker).</summary>
/// <param name="Id">Node id.</param>
/// <param name="DisplayName">Display name, when set.</param>
/// <param name="Approved">Approved by the operator (an unapproved node cannot run anything yet).</param>
/// <param name="Draining">Draining: no new starts.</param>
/// <param name="Discrete">
/// The node's last report is discrete: without a GPU choice the restore picks its GPUs (<see cref="RestorePlacements.DefaultGpus"/>, D105).
/// </param>
public sealed record RestoreNodeChoice(string Id, string? DisplayName, bool Approved, bool Draining, bool Discrete = false)
{
    /// <summary>"Office PC (pc-office)" or the id.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) || DisplayName == Id ? Id : $"{DisplayName} ({Id})";
}

/// <summary>How one backed-up model's placement maps onto this master, before the operator chooses anything.</summary>
/// <param name="Match">Same uid, slug only, no match, or unplaced in the backup (design §9.4).</param>
/// <param name="BackupNodeId">The node the model was on in the backup; null when unplaced there.</param>
/// <param name="DefaultTargetNodeId">
/// Where the model goes without a choice: the node here with the same uid, or — for a backup without a nodes section
/// (version 1, P1 compatibility) — the bootstrap node. Null = unplaced unless the operator picks a target.
/// </param>
/// <param name="Detail">Plain-words explanation for the preview.</param>
public sealed record RestorePlacementPreview(RestorePlacementMatch Match, string? BackupNodeId, string? DefaultTargetNodeId, string Detail)
{
    /// <summary>True when the operator should pick a target (slug only or no match, and no default).</summary>
    public bool NeedsTarget => Match is RestorePlacementMatch.SlugOnly or RestorePlacementMatch.None && DefaultTargetNodeId is null;
}

/// <summary>The placement a configuration import writes for one model, or why it cannot.</summary>
/// <param name="Placement">The placement to write; null = unplaced.</param>
/// <param name="Error">Set when the operator's target is not usable (not registered here, revoked); the model is not restored.</param>
/// <param name="Detail">What was decided, for the progress table.</param>
/// <param name="Notable">The restore decided something the operator did not choose (GPUs picked, or unplaced for want of GPUs): a job note.</param>
public sealed record ResolvedRestorePlacement(ModelPlacement? Placement, string? Error, string Detail, bool Notable = false);

/// <summary>
/// The configuration-import placement rule (design §9.4): a model keeps its node only when a node here has the backup
/// node's <b>uid</b> (a matching slug may be another machine); otherwise the operator picks a target node per model
/// (<see cref="RestoreTarget"/>) or the model is restored unplaced. A backup without a nodes section (version 1) places
/// every model on the bootstrap node, as P1 did (D33). Node trust state is never imported here.
/// </summary>
public static class RestorePlacements
{
    /// <summary>Classifies <paramref name="model"/> (from <paramref name="backup"/>, already <see cref="BackupDocument.WithDefaultPlacement"/>).</summary>
    public static RestorePlacementPreview Classify(BackupDocument backup, ModelDefinition model, IReadOnlyList<NodeRecord> local, string bootstrapNodeId)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(local);
        if (model.Placement is not { } placement)
        {
            return new RestorePlacementPreview(RestorePlacementMatch.Unplaced, null, null,
                "Unplaced in the backup; it stays unplaced unless you pick a node.");
        }

        string backupNodeId = placement.NodeId;
        var bySlug = local.FirstOrDefault(n => !n.Revoked && string.Equals(n.Id, backupNodeId, StringComparison.Ordinal));
        if (backup.Nodes is null)
        {
            // No uids in the file (version 1, or a version-2 file from the single-box wiring): P1 placed everything on the
            // bootstrap node, and so does this, unless the operator picks another node.
            bool bootstrap = string.Equals(backupNodeId, bootstrapNodeId, StringComparison.Ordinal);
            return bootstrap
                ? new RestorePlacementPreview(RestorePlacementMatch.SlugOnly, backupNodeId, bootstrapNodeId,
                    $"The backup has no node uids; placed on the bootstrap node '{bootstrapNodeId}' as before.")
                : new RestorePlacementPreview(bySlug is null ? RestorePlacementMatch.None : RestorePlacementMatch.SlugOnly, backupNodeId, null,
                    $"The backup has no node uids for '{backupNodeId}': pick a target node or leave it unplaced.");
        }

        var backupNode = backup.Nodes.FirstOrDefault(n => string.Equals(n.Id, backupNodeId, StringComparison.Ordinal));
        if (backupNode is not null)
        {
            var sameUid = local.FirstOrDefault(n => !n.Revoked && string.Equals(n.Uid, backupNode.Uid, StringComparison.OrdinalIgnoreCase));
            if (sameUid is not null)
            {
                return new RestorePlacementPreview(RestorePlacementMatch.SameUid, backupNodeId, sameUid.Id,
                    sameUid.Id == backupNodeId
                        ? $"Same node '{sameUid.Id}' (its uid matches)."
                        : $"Same machine, registered here as '{sameUid.Id}' (its uid matches '{backupNodeId}').");
            }
        }

        return bySlug is not null
            ? new RestorePlacementPreview(RestorePlacementMatch.SlugOnly, backupNodeId, null,
                $"A node named '{backupNodeId}' is here, but its uid differs (maybe another machine): {PickHint}")
            : new RestorePlacementPreview(RestorePlacementMatch.None, backupNodeId, null,
                $"No node here matches '{backupNodeId}': {PickHint}");
    }

    private const string PickHint = "pick a target node or leave it unplaced.";

    /// <summary>
    /// The placement to write for <paramref name="model"/>. <paramref name="local"/> null = no node registry (the
    /// single-box wiring): the backup's placement, or the operator's target unchecked. With a registry the uid rule of
    /// <see cref="Classify"/> applies, and an operator's <paramref name="target"/> overrides it (it must name a node
    /// registered here that is not revoked).
    /// </summary>
    public static ResolvedRestorePlacement Resolve(
        BackupDocument backup, ModelDefinition model, IReadOnlyList<NodeRecord>? local, RestoreTarget? target, string bootstrapNodeId)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(model);
        if (local is null)
        {
            if (target is null)
            {
                return new ResolvedRestorePlacement(model.Placement, null, model.Placement is null ? "Unplaced." : $"On node '{model.Placement.NodeId}'.");
            }

            return target.TargetNodeId is null
                ? new ResolvedRestorePlacement(null, null, "Left unplaced, as chosen.")
                : new ResolvedRestorePlacement(new ModelPlacement(target.TargetNodeId, NonEmpty(target.GpuUuids)), null, $"Placed on '{target.TargetNodeId}', as chosen.");
        }

        var match = Classify(backup, model, local, bootstrapNodeId);
        if (target is not null)
        {
            if (target.TargetNodeId is null)
            {
                return new ResolvedRestorePlacement(null, null, "Left unplaced, as chosen.");
            }

            var node = local.FirstOrDefault(n => string.Equals(n.Id, target.TargetNodeId, StringComparison.Ordinal));
            if (node is null)
            {
                return new ResolvedRestorePlacement(null, $"Target node '{target.TargetNodeId}' is not registered here.", "");
            }

            if (node.Revoked)
            {
                return new ResolvedRestorePlacement(null, $"Target node '{target.TargetNodeId}' is revoked.", "");
            }

            // Choosing the uid-matched node itself keeps the backup's GPUs (UUIDs belong to that machine); any other node
            // gets the GPUs chosen with it, or on a discrete node a default choice (MN-124, D105).
            bool sameMachine = match.Match == RestorePlacementMatch.SameUid && match.DefaultTargetNodeId == node.Id;
            var gpus = NonEmpty(target.GpuUuids);
            if (gpus is null && sameMachine)
            {
                return new ResolvedRestorePlacement(
                    new ModelPlacement(node.Id, model.Placement?.GpuUuids, model.Placement?.GpuIndices), null, $"Placed on '{node.Id}', as chosen.");
            }

            if (gpus is null && node.LastCapabilities is { Memory.Model: MemoryModel.Discrete } discrete)
            {
                int count = Math.Max(1, model.Placement?.GpuUuids?.Count ?? 0);
                if (DefaultGpus(discrete.Gpus, count) is not { } picked)
                {
                    return new ResolvedRestorePlacement(null, null,
                        $"Restored unplaced: node '{node.Id}' has no {GpuWord(count)} of equal memory for it; place it in the model editor.", Notable: true);
                }

                return new ResolvedRestorePlacement(
                    new ModelPlacement(node.Id, [.. picked.Select(g => g.Uuid)], [.. picked.Select(g => g.Index)]), null,
                    $"Placed on '{node.Id}' {string.Join(", ", picked.Select(g => "GPU " + g.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)))} " +
                    "(chosen automatically: the largest equal cards); change the GPUs in the model editor if needed.", Notable: true);
            }

            return new ResolvedRestorePlacement(new ModelPlacement(node.Id, gpus), null, $"Placed on '{node.Id}', as chosen.");
        }

        if (match.DefaultTargetNodeId is not { } defaultNode)
        {
            return new ResolvedRestorePlacement(null, null,
                match.Match == RestorePlacementMatch.Unplaced
                    ? "Unplaced in the backup."
                    : match.Detail.Replace(PickHint, "restored unplaced (no target picked).", StringComparison.Ordinal));
        }

        return new ResolvedRestorePlacement(
            match.Match == RestorePlacementMatch.SameUid
                ? new ModelPlacement(defaultNode, model.Placement?.GpuUuids, model.Placement?.GpuIndices)
                : new ModelPlacement(defaultNode),
            null,
            match.Detail);
    }

    /// <summary>
    /// The Services lists to import into this master's store, which belongs to the bootstrap node (P1/P2 keep one
    /// <c>host-services.json</c>): the backup node with the bootstrap node's uid, else the backup's lists for the same
    /// node id (a reinstalled DGX re-enrolls with a new uid; the container names still apply), else none.
    /// </summary>
    public static (HostServicesBackup? Lists, string? FromNodeId) ServicesForBootstrap(
        BackupDocument backup, IReadOnlyList<NodeRecord>? local, string bootstrapNodeId)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (backup.Nodes is not null && local?.FirstOrDefault(n => n.Id == bootstrapNodeId) is { } here)
        {
            var sameUid = backup.Nodes.FirstOrDefault(n => string.Equals(n.Uid, here.Uid, StringComparison.OrdinalIgnoreCase));
            if (sameUid is not null && backup.HostServicesFor(sameUid.Id) is { } lists)
            {
                return (lists, sameUid.Id);
            }
        }

        var bySlug = backup.HostServicesFor(bootstrapNodeId);
        return bySlug is null ? (null, null) : (bySlug, bootstrapNodeId);
    }

    /// <summary>The nodes a restored model may be placed on: every registered node that is not revoked, by id.</summary>
    public static IReadOnlyList<RestoreNodeChoice> TargetChoices(IReadOnlyList<NodeRecord> local)
    {
        ArgumentNullException.ThrowIfNull(local);
        return [.. local.Where(n => !n.Revoked).OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select(n => new RestoreNodeChoice(n.Id, n.DisplayName, n.Approved, n.Draining,
                n.LastCapabilities?.Memory.Model == MemoryModel.Discrete))];
    }

    /// <summary>
    /// The default GPUs for a model restored onto a discrete node without a GPU choice (MN-124, D105): <paramref name="count"/>
    /// GPUs of the same product and memory (tensor parallel needs equal cards, D25), from the largest such cards, lowest
    /// indices first. Null when the node has no <paramref name="count"/> equal cards.
    /// </summary>
    public static IReadOnlyList<GpuCapability>? DefaultGpus(IReadOnlyList<GpuCapability> gpus, int count)
    {
        ArgumentNullException.ThrowIfNull(gpus);
        return gpus
            .Where(g => g.MemoryTotalGiB is > 0)
            .GroupBy(g => (g.Name, Total: g.MemoryTotalGiB!.Value))
            .Where(g => g.Count() >= count)
            .OrderByDescending(g => g.Key.Total)
            .ThenBy(g => g.Min(x => x.Index))
            .Select(g => (IReadOnlyList<GpuCapability>)[.. g.OrderBy(x => x.Index).Take(count)])
            .FirstOrDefault();
    }

    private static string GpuWord(int count) =>
        count == 1 ? "GPU" : count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " GPUs";

    private static IReadOnlyList<string>? NonEmpty(IReadOnlyList<string>? gpus) => gpus is { Count: > 0 } ? gpus : null;
}
