using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.NodeRuntime.Abstractions;

namespace MirbadLLM.Agent.Storage;

/// <summary>
/// Model folders on the node (design §4.3 <c>/model-folders</c>, §10.3): list with size/config/partial flags, inspect with
/// today's <see cref="ModelFolderInspector"/> (moved to NodeRuntime), and delete as an operation.
/// <para>Delete refuses names that are not one plain segment (<c>..</c>, separators, absolute paths), links (even ones
/// pointing inside the root), anything that does not resolve directly under the models root, a mismatched
/// confirmation, and folders in use (409 <c>in_use</c>: a runtime instance per <see cref="IRuntimeDriver.IsFolderInUseAsync"/>
/// or a running download). The master's "referenced by a placed model" check is separate (MN-032). The tree is removed
/// without following any link inside it.</para>
/// </summary>
public sealed class ModelFolderService(
    ModelRootPaths paths,
    IEnumerable<IRuntimeDriver> drivers,
    IAgentDownloads downloads,
    StorageOperations operations,
    ILogger<ModelFolderService> logger)
{
    private readonly IRuntimeDriver[] _drivers = drivers.ToArray();

    /// <summary>
    /// Every visible folder under the models root (hidden ones skipped; links only when they resolve inside the root),
    /// with its size (links inside not followed), <c>config.json</c> presence, the checkpoint summary, and
    /// <see cref="ModelFolderEntry.Partial"/> for <c>{folder}.partial</c> or a running download.
    /// </summary>
    public async Task<IReadOnlyList<ModelFolderEntry>> ListAsync(CancellationToken ct = default)
    {
        string root = paths.Root;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var running = (await downloads.ListAsync(ct).ConfigureAwait(false))
            .Where(d => d.State is DownloadState.Queued or DownloadState.Downloading)
            .Select(d => d.Folder)
            .ToHashSet(StringComparer.Ordinal);

        var inspector = CreateInspector();
        var result = new List<ModelFolderEntry>();
        foreach (string dir in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            string folder = Path.GetFileName(dir);
            if (ModelRootPaths.NameError(folder) is not null)
            {
                continue;
            }

            var location = paths.Locate(folder);
            if (location.ResolvedPath is not { } content)
            {
                continue;
            }

            bool hasConfig = HasPlainConfig(content);
            ModelFolderInspection? inspection = null;
            if (hasConfig)
            {
                try
                {
                    inspection = inspector.Inspect(folder);
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    // An unreadable config still lists the folder, without the checkpoint summary.
                }
            }

            result.Add(new ModelFolderEntry
            {
                Folder = folder,
                SizeBytes = ModelRootPaths.SizeOf(content),
                HasConfig = hasConfig,
                Partial = folder.EndsWith(".partial", StringComparison.Ordinal) || running.Contains(folder),
                WeightsGiB = inspection?.WeightsGiB,
                Architecture = inspection?.Architecture,
                DetectedFormat = inspection?.DetectedFormat,
            });
        }

        return result;
    }

    /// <summary>Today's inspection of one folder; 404 <c>not_found</c> without a folder or a <c>config.json</c>.</summary>
    public ModelFolderInspection Inspect(string folder)
    {
        var location = paths.Locate(folder);
        if (location.IsLink && location.ResolvedPath is null)
        {
            throw StorageErrors.InvalidRequest($"'{folder}' is a link that does not resolve inside the models root.");
        }

        if (location.ResolvedPath is not { } content)
        {
            throw StorageErrors.NotFound($"No folder '{folder}' on this node.");
        }

        if (!HasPlainConfig(content))
        {
            throw StorageErrors.NotFound($"'{folder}' has no config.json.");
        }

        try
        {
            return CreateInspector().Inspect(folder) ?? throw StorageErrors.NotFound($"'{folder}' has no config.json.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw StorageErrors.InvalidRequest($"'{folder}/config.json' cannot be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Executes an accepted delete: validation and in-use refusals throw (the caller fails the operation before any
    /// stage), then the <c>deleting</c> stage is recorded, fencing is re-checked, the tree is removed and the operation
    /// completes. An I/O failure mid-delete ends the operation Failed (stage <c>deleting</c>).
    /// </summary>
    public async Task<OperationStatus> DeleteAsync(string folder, FolderDeleteRequest request, OperationStatus accepted, MasterTriple master, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(accepted);
        ArgumentNullException.ThrowIfNull(master);
        var location = paths.Locate(folder);
        if (!string.Equals(request.Confirm, folder, StringComparison.Ordinal))
        {
            throw StorageErrors.InvalidRequest($"Type the folder name to confirm: confirm must equal '{folder}'.");
        }

        if (location.IsLink)
        {
            throw StorageErrors.InvalidRequest($"'{folder}' is a symbolic link; folder delete refuses links.");
        }

        if (!location.Exists)
        {
            throw StorageErrors.NotFound($"No folder '{folder}' on this node.");
        }

        await EnsureNotInUseAsync(folder, ct).ConfigureAwait(false);
        return await RunDeleteAsync(location, accepted.Id, master, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Recovers a delete interrupted by an agent restart (design §4.4 items 5, 8: removing a folder is a desired-state
    /// action): already gone → Succeeded; superseded master → Failed <c>superseded</c>; link or in use → Failed;
    /// otherwise the delete is finished.
    /// </summary>
    public async Task ResumeDeleteAsync(RecoverableOperation operation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        string id = operation.Status.Id;
        try
        {
            var location = paths.Locate(operation.Status.Target);
            if (location.IsLink)
            {
                throw StorageErrors.InvalidRequest($"'{location.Folder}' is a symbolic link; folder delete refuses links.");
            }

            if (!location.Exists)
            {
                await operations.CompleteAsync(id, OperationState.Succeeded, ct: ct).ConfigureAwait(false);
                return;
            }

            await EnsureNotInUseAsync(location.Folder, ct).ConfigureAwait(false);
            await RunDeleteAsync(location, id, operation.Master, ct).ConfigureAwait(false);
        }
        catch (AgentException ex)
        {
            await operations.CompleteAsync(id, OperationState.Failed, ex.Error, ct: ct).ConfigureAwait(false);
        }
    }

    private async Task EnsureNotInUseAsync(string folder, CancellationToken ct)
    {
        foreach (var driver in _drivers)
        {
            if (await driver.IsFolderInUseAsync(folder, ct).ConfigureAwait(false))
            {
                throw StorageErrors.Create(ErrorCodes.InUse, $"'{folder}' is used by a runtime instance on this node; stop it first.");
            }
        }

        if (await downloads.IsFolderInUseAsync(folder, ct).ConfigureAwait(false))
        {
            throw StorageErrors.Create(ErrorCodes.InUse, $"A download is writing into '{folder}'; cancel it first.");
        }
    }

    private async Task<OperationStatus> RunDeleteAsync(FolderLocation location, string operationId, MasterTriple master, CancellationToken ct)
    {
        if (!operations.Fencing.IsCurrent(master))
        {
            throw StorageErrors.Create(ErrorCodes.Superseded, "A newer master drives this node; the delete was not run.");
        }

        await operations.StageAsync(operationId, new OperationStageUpdate
        {
            Stage = OperationStages.Deleting,
            State = OperationState.Running,
        }, ct).ConfigureAwait(false);

        try
        {
            ModelRootPaths.DeleteTree(location.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StorageLog.FolderDeleteFailed(logger, ex, operationId, location.Folder);
            return await operations.CompleteAsync(operationId, OperationState.Failed,
                StorageErrors.Error(ErrorCodes.InternalError, $"'{location.Folder}' could not be fully deleted: {ex.Message}"), ct: CancellationToken.None)
                .ConfigureAwait(false);
        }

        StorageLog.FolderDeleted(logger, operationId, location.Folder);
        return await operations.CompleteAsync(operationId, OperationState.Succeeded, ct: CancellationToken.None).ConfigureAwait(false);
    }

    private ModelFolderInspector CreateInspector() =>
        new(Options.Create(new ModelStorageOptions { ModelsLocalPath = paths.Root }));

    /// <summary>A regular <c>config.json</c> (a linked config is never read: it could point outside the root).</summary>
    private static bool HasPlainConfig(string directory)
    {
        var config = new FileInfo(Path.Combine(directory, "config.json"));
        return config.Exists && !ModelRootPaths.IsLink(config);
    }
}
