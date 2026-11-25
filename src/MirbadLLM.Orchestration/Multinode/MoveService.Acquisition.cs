using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Downloads.Native;
using MirbadLLM.Orchestration.Agents;

namespace MirbadLLM.Orchestration.Multinode;

public sealed partial class MoveService
{
    private readonly MoveTransferRelay _transferRelay;
    private readonly HuggingFaceHubClient? _hub;

    private static MoveFolderWork[] BuildFolderWork(ModelDefinition model)
    {
        if (!model.NeedsCheckpoint)
        {
            return [];
        }

        var folders = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string? path in RemoteModelFolders.CheckpointPaths(model))
        {
            Add(path);
        }

        foreach (string arg in model.ExtraArgs)
        {
            int equals = arg.StartsWith('-') ? arg.IndexOf('=', StringComparison.Ordinal) : -1;
            string value = arg[(equals + 1)..].Trim('"', '\'');
            if (value.StartsWith('/') || value.StartsWith('\\') || value.Length > 1 && value[1] == ':' ||
                value.Contains("/models/", StringComparison.Ordinal))
            {
                Add(value);
            }
        }

        var sources = model.Sources.ToLookup(source => source.Folder, StringComparer.Ordinal);
        return folders.Select(pair =>
        {
            var matches = sources[pair.Key].ToArray();
            if (matches.Length > 1 || matches.Any(source => source.Kind != ModelSourceKind.HuggingFace ||
                !HuggingFaceHubClient.IsValidRepoId(source.RepoId)))
            {
                throw new MoveRefusedException(ErrorCodes.ModelNotMovable,
                    $"Folder '{pair.Key}' needs one unambiguous Hugging Face source.");
            }

            return new MoveFolderWork(pair.Key, matches.FirstOrDefault()?.RepoId ?? "", [.. pair.Value.Order(StringComparer.Ordinal)]);
        }).ToArray();

        void Add(string? reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                return;
            }

            string relative = reference.StartsWith("/models/", StringComparison.Ordinal) ? reference[8..] : reference;
            string? folder = RemoteModelFolders.TopFolder(relative);
            if (folder is null || folder.StartsWith('.') || RepoPathRules.Check(relative) is not null)
            {
                throw new MoveRefusedException(ErrorCodes.ModelNotMovable,
                    $"Checkpoint reference '{reference}' cannot be moved. Use a path under /models/<folder>.");
            }

            if (!folders.TryGetValue(folder, out var required))
            {
                required = new HashSet<string>(StringComparer.Ordinal);
                folders.Add(folder, required);
            }

            if (relative.Length > folder.Length)
            {
                required.Add(relative[(folder.Length + 1)..]);
            }
        }
    }

    private async Task<bool> SupportsTransferAsync(string nodeId, CancellationToken ct) =>
        (await _clients.GetClient(nodeId).GetCapabilitiesAsync(ct).ConfigureAwait(false)).Features
            .Contains(AgentFeatures.MoveTransfer, StringComparer.Ordinal);

    private async Task<bool> AcquireFoldersAsync(OwnershipRecord record, ModelDefinition model, CancellationToken ct)
    {
        var move = record.Move!;
        var work = move.Folders ?? BuildFolderWork(model);
        if (move.Folders is null)
        {
            await _ownership.UpdateAsync(record.Id, current => current.Stage == MoveStages.Downloading
                ? current with { Move = current.Move! with { Folders = work } } : current, ct).ConfigureAwait(false);
        }

        if (work.Count == 0)
        {
            return true;
        }

        var target = _clients.GetClient(move.TargetNodeId);
        foreach (var saved in work.Where(folder => folder.State != DownloadState.Completed))
        {
            var folder = saved;
            var source = model.Sources.FirstOrDefault(entry => entry.Folder == folder.Folder && entry.RepoId == folder.RepoId);
            await CheckScopeAsync(record, ct).ConfigureAwait(false);
            if (source is not null)
            {
                var running = await target.GetDownloadAsync(folder.Folder, ct).ConfigureAwait(false);
                while (running is { State: DownloadState.Queued or DownloadState.Downloading })
                {
                    await DelayAsync(record.Id, _moveOptions.PollInterval, ct).ConfigureAwait(false);
                    await CheckScopeAsync(record, ct).ConfigureAwait(false);
                    running = await target.GetDownloadAsync(folder.Folder, ct).ConfigureAwait(false);
                }

                if (running is { State: DownloadState.Completed, OperationId: { } operation } &&
                    record.OutstandingOperationIds.Contains(operation, StringComparer.Ordinal) &&
                    (_hub is null || folder.PinnedRevision == running.Revision && folder.DownloadOperationId == operation))
                {
                    await SaveFolderAsync(record, folder with
                    {
                        State = DownloadState.Completed, TotalBytes = running.TotalBytes, BytesDownloaded = running.BytesDownloaded,
                        Route = folder.Route ?? "hub",
                    }, ct).ConfigureAwait(false);
                    continue;
                }
            }

            if (folder.Route is not ("hub" or "mirror") && move.SourceNodeId.Length != 0 &&
                await SupportsTransferAsync(move.TargetNodeId, ct).ConfigureAwait(false))
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        if (await SupportsTransferAsync(move.SourceNodeId, ct).ConfigureAwait(false))
                        {
                            folder = await _transferRelay.CopyAsync(record, folder, async (next, token) =>
                            {
                                folder = next;
                                await SaveFolderAsync(record, next, token).ConfigureAwait(false);
                            }, token => CheckScopeAsync(record, token), ct).ConfigureAwait(false);
                            await SaveFolderAsync(record, folder, ct).ConfigureAwait(false);
                        }
                        break;
                    }
                    catch (AgentUnreachableException ex) when (ex.InnerException is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError })
                    {
                        throw new MoveFailedException($"The placed source could not be authenticated for '{folder.Folder}'.");
                    }
                    catch (Exception ex) when (IsTransient(ex) || ex is AgentException { Code: ErrorCodes.NotFound })
                    {
                        if (ex is not AgentException && attempt < 2)
                        {
                            await DelayAsync(record.Id, _moveOptions.RetryInterval, ct).ConfigureAwait(false);
                            continue;
                        }

                        if (source is null || folder.ManifestId is not null)
                        {
                            throw;
                        }

                        folder = folder with { Error = "Placed source did not provide a snapshot." };
                        break;
                    }
                }
            }

            if (folder.State == DownloadState.Completed)
            {
                continue;
            }

            if (source is null || folder.ManifestId is not null)
            {
                throw new MoveFailedException($"No verified transfer is available for checkpoint folder '{folder.Folder}'.");
            }

            string? revision = folder.PinnedRevision ?? source.Revision;
            if (_hub is not null)
            {
                _tokens.TryGetValue(record.Id, out string? token);
                var snapshot = await _hub.GetSnapshotAsync(source.RepoId, revision, token, ct).ConfigureAwait(false);
                var patterns = source.AllowPatterns is { Count: > 0 } custom ? custom : HuggingFacePatterns.DefaultAllowPatterns;
                var selected = snapshot.Files.Where(file => HuggingFacePatterns.MatchesAny(file.Path, patterns)).ToArray();
                if (selected.Length == 0 || selected.Any(file => file.LfsSha256 is null && file.BlobSha1 is null) ||
                    folder.RequiredFiles.Any(path => selected.All(file => file.Path != path)))
                {
                    throw new MoveFailedException($"The pinned Hub selection cannot verify every required file in '{folder.Folder}'.");
                }

                var capabilities = await target.GetCapabilitiesAsync(ct).ConfigureAwait(false);
                if (!capabilities.Features.Contains(AgentFeatures.DownloadsHfNative, StringComparer.Ordinal) &&
                    !capabilities.Features.Contains(AgentFeatures.MoveTransfer, StringComparer.Ordinal))
                {
                    throw new MoveFailedException($"Upgrade node '{move.TargetNodeId}' to verify pinned Hub files for '{folder.Folder}'.");
                }

                revision = snapshot.CommitSha;
            }

            folder = folder with { PinnedRevision = revision, State = DownloadState.Queued };
            await SaveFolderAsync(record, folder, ct).ConfigureAwait(false);
            if (!await EnsureDownloadedAsync(record, move.TargetNodeId, source with { Revision = revision }, ct).ConfigureAwait(false))
            {
                return false;
            }

            var result = await target.GetDownloadAsync(folder.Folder, ct).ConfigureAwait(false);
            await SaveFolderAsync(record, folder with
            {
                Route = folder.Route ?? "hub", State = DownloadState.Completed,
                TotalBytes = result?.TotalBytes ?? 0, BytesDownloaded = result?.BytesDownloaded ?? 0,
            }, ct, preserveRoute: true).ConfigureAwait(false);
        }

        return true;
    }

    private Task<OwnershipRecord?> SaveFolderAsync(OwnershipRecord record, MoveFolderWork next, CancellationToken ct, bool preserveRoute = false) =>
        _ownership.UpdateAsync(record.Id, current => current.Stage == MoveStages.Downloading && current.Move?.Folders is { } folders
            ? current with
            {
                Move = current.Move with
                {
                    Folders = folders.Select(item => item.Folder != next.Folder ? item :
                        preserveRoute ? next with { Route = item.Route ?? next.Route, DownloadOperationId = item.DownloadOperationId } : next).ToArray(),
                },
                UpdatedAt = _time.GetUtcNow(),
            } : current, ct);

    private async Task CheckScopeAsync(OwnershipRecord record, CancellationToken ct)
    {
        var move = record.Move!;
        var current = await _ownership.GetForModelAsync(record.ModelId, ct).ConfigureAwait(false);
        var model = await _models.GetAsync(record.ModelId, ct).ConfigureAwait(false);
        var target = await _nodes.GetAsync(move.TargetNodeId, ct).ConfigureAwait(false);
        var source = move.SourceNodeId.Length == 0 ? null : await _nodes.GetAsync(move.SourceNodeId, ct).ConfigureAwait(false);
        if (current?.Id != record.Id || current.Stage != MoveStages.Downloading || model is null ||
            (model.Placement?.NodeId ?? "") != move.SourceNodeId || (model.Placement?.Generation ?? 0) != record.PlacementGeneration ||
            target is not { Approved: true, Revoked: false, Draining: false } || target.Uid != move.TargetNodeUid ||
            move.SourceNodeId.Length != 0 && (source is not { Approved: true, Revoked: false } || source.Uid != move.SourceNodeUid))
        {
            throw new MoveFailedException("Checkpoint acquisition lost its move ownership or node approval.");
        }
    }
}
