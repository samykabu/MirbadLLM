using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Multinode.Contract;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// Images, model folders and downloads (design §4.3, §6.4, §10.3): approved digests only, plain folder names, in-use
/// folders refused, downloads as operations with progress events, the token never stored.
/// </summary>
internal sealed partial class FakeAgent
{
    private readonly Dictionary<string, ImageInfo> _images = new(StringComparer.Ordinal);
    private readonly HashSet<string> _approvedDigests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeFolder> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeDownload> _downloads = new(StringComparer.Ordinal);
    private readonly List<string?> _downloadTokens = [];
    private readonly Dictionary<(string Folder, string File), GgufSummary> _gguf = new();

    /// <summary>Approves an image digest on the node (agent.json, design §10.3).</summary>
    public void ApproveDigest(string digest)
    {
        lock (_gate)
        {
            _approvedDigests.Add(digest);
        }
    }

    /// <summary>The Hugging Face token of every accepted download start, in order (null = none was sent).</summary>
    public IReadOnlyList<string?> DownloadTokens
    {
        get
        {
            lock (_gate)
            {
                return [.. _downloadTokens];
            }
        }
    }

    /// <summary>Adds a checkpoint folder with a <c>config.json</c>.</summary>
    public void AddFolder(string folder, double weightsGiB = 8)
    {
        lock (_gate)
        {
            _folders[folder] = FakeFolder.WithConfig(folder, weightsGiB);
        }
    }

    /// <summary>
    /// Makes <c>GET /model-folders/{folder}/gguf</c> answer <paramref name="summary"/> for its folder and file (P3, MN-202).
    /// The route stays gated: add <see cref="AgentFeatures.GgufInspect"/> to the fake's features to reach it.
    /// </summary>
    public void SetGgufSummary(GgufSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        lock (_gate)
        {
            _gguf[(summary.Folder, summary.File)] = summary;
        }
    }

    private FakeReply GgufInspect(string folder, GgufInspectQuery query)
    {
        lock (_gate)
        {
            var candidates = _gguf.Where(g => g.Key.Folder == folder && (query.File is null || g.Key.File == query.File)).ToList();
            return candidates.Count switch
            {
                1 => FakeReply.Json(candidates[0].Value),
                0 => throw Fail(ErrorCodes.NotFound, $"No GGUF '{query.File}' in folder '{folder}' on node {NodeId}."),
                // As the real agent: the names also travel as structured details (MN-230, D130).
                _ => throw Fail(
                    ErrorCodes.InvalidRequest,
                    $"Folder '{folder}' holds several GGUFs; name one with ?file=.",
                    new GgufChoiceDetails([.. candidates.Select(c => c.Key.File).Order(StringComparer.Ordinal)])),
            };
        }
    }

    private void SeedContractFixture()
    {
        lock (_gate)
        {
            _approvedDigests.Add(AgentContractSeed.ImageDigest);
            _approvedDigests.Add(AgentContractSeed.PullImageDigest);
            AddImageLocked(AgentContractSeed.Image, AgentContractSeed.ImageDigest);
            _folders[AgentContractSeed.ModelFolder] = FakeFolder.WithConfig(AgentContractSeed.ModelFolder, 8);
            _folders[AgentContractSeed.ScratchFolder] = FakeFolder.WithConfig(AgentContractSeed.ScratchFolder, 2);
            SeedHostLocked();
        }
    }

    private void AddImageLocked(string reference, string? digest) =>
        _images[reference] = new ImageInfo
        {
            Ref = reference,
            Id = "sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            Digest = digest,
            Arch = "arm64",
            SizeBytes = 19L * 1024 * 1024 * 1024,
        };

    private static bool IsPlainName(string name) =>
        name.Length is > 0 and <= 200 && !name.StartsWith('.') && name.IndexOfAny(['/', '\\', ':']) < 0 && !name.Contains("..", StringComparison.Ordinal);

    // ---- Images ----

    private FakeReply ImagesList()
    {
        lock (_gate)
        {
            return FakeReply.Json<IReadOnlyList<ImageInfo>>([.. _images.Values.OrderBy(i => i.Ref, StringComparer.Ordinal)]);
        }
    }

    private FakeReply ImagePull(FakeCall call)
    {
        var request = call.Request<ImagePullRequest>();
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            if (request.Digest is { } digest ? !_approvedDigests.Contains(digest) : !_options.AllowTagPulls)
            {
                string reason = request.Digest is null ? "an approved digest is required" : $"digest {request.Digest} is not approved on this node";
                throw Fail(ErrorCodes.SpecRejected, "The pull was rejected: " + reason, new SpecRejectedDetails(reason));
            }

            var op = AcceptLocked(call);
            StartPullWork(op, request);
            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    private void StartPullWork(FakeOp op, ImagePullRequest request) => _ = Task.Run(async () =>
    {
        try
        {
            lock (_gate)
            {
                if (!Alive(op))
                {
                    return;
                }

                StageLocked(op, OperationStages.Pulling, new OperationProgress { Percent = 0, Message = "pulling " + request.Image });
            }

            await Task.Delay(_options.PullDuration, _time, op.Cts.Token);
            await _pulls.WaitAsync(op.Cts.Token);
            lock (_gate)
            {
                if (!Alive(op))
                {
                    return;
                }

                AddImageLocked(request.Image, request.Digest);
                StageLocked(op, OperationStages.Pulled, new OperationProgress { Percent = 100, Message = "pulled " + request.Image });
                CompleteLocked(op, OperationState.Succeeded, result: _images[request.Image]);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled or crashed.
        }
    }, CancellationToken.None);

    // ---- Model folders ----

    private FakeReply FoldersList()
    {
        lock (_gate)
        {
            return FakeReply.Json<IReadOnlyList<ModelFolderEntry>>([.. _folders.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => f.Value.ToEntry(f.Key))]);
        }
    }

    private FakeReply FolderInspect(string folder)
    {
        lock (_gate)
        {
            return _folders.TryGetValue(folder, out var entry) && entry.Inspection is { } inspection
                ? FakeReply.Json(inspection)
                : throw Fail(ErrorCodes.NotFound, $"No checkpoint folder '{folder}' with a config.json on node {NodeId}.");
        }
    }

    private FakeReply FolderDelete(FakeCall call)
    {
        string folder = call.Target;
        var request = call.Request<FolderDeleteRequest>();
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            if (!IsPlainName(folder))
            {
                throw Fail(ErrorCodes.InvalidRequest, $"'{folder}' is not a folder name under the models root.");
            }

            if (!string.Equals(request.Confirm, folder, StringComparison.Ordinal))
            {
                throw Fail(ErrorCodes.InvalidRequest, $"Confirm the deletion with the folder name '{folder}'.");
            }

            if (!_folders.ContainsKey(folder))
            {
                throw Fail(ErrorCodes.NotFound, $"No folder '{folder}' on node {NodeId}.");
            }

            if (FolderUserLocked(folder) is { } user)
            {
                throw Fail(ErrorCodes.InUse, $"Folder '{folder}' is used by {user}.");
            }

            var op = AcceptLocked(call);
            if (CheckCurrentLocked(op))
            {
                StageLocked(op, OperationStages.Deleting);
                _folders.Remove(folder);
                CompleteLocked(op, OperationState.Succeeded);
            }

            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    private string? FolderUserLocked(string folder)
    {
        string root = LogicalVolumes.ModelsTarget + "/" + folder;
        foreach (var instance in _instances.Values)
        {
            bool uses = instance.Spec.Argv.Any(a => MirbadLLM.NodeRuntime.Runtimes.ContainerRuntimeDriver.ArgumentUsesPath(a, root)) ||
                        instance.Spec.Mounts.Any(m => m.Volume == LogicalVolumes.Models && m.SubPath is { } sub &&
                                                      (sub == folder || sub.StartsWith(folder + "/", StringComparison.Ordinal)));
            if (uses)
            {
                return $"the instance of '{instance.ModelId}'";
            }
        }

        return _downloads.TryGetValue(folder, out var download) && download.Running ? "a running download" : null;
    }

    // ---- Downloads ----

    private FakeReply DownloadsList()
    {
        lock (_gate)
        {
            return FakeReply.Json<IReadOnlyList<NodeDownloadStatus>>([.. _downloads.Values.Select(d => d.Status).OrderBy(d => d.Folder, StringComparer.Ordinal)]);
        }
    }

    private FakeReply DownloadGet(string folder)
    {
        lock (_gate)
        {
            return _downloads.TryGetValue(folder, out var download)
                ? FakeReply.Json(download.Status)
                : throw Fail(ErrorCodes.NotFound, $"No download into '{folder}' on node {NodeId}.");
        }
    }

    private FakeReply DownloadStart(FakeCall call)
    {
        var request = call.Request<DownloadStartRequest>();
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            if (!IsPlainName(request.Folder) || string.IsNullOrWhiteSpace(request.RepoId))
            {
                throw Fail(ErrorCodes.InvalidRequest, $"'{request.Folder}' is not a folder name under the models root.");
            }

            if (_downloads.TryGetValue(request.Folder, out var running) && running.Running)
            {
                throw Fail(ErrorCodes.AlreadyRunning, $"A download into '{request.Folder}' is already running.");
            }

            bool exists = _folders.ContainsKey(request.Folder);
            if (exists && request.Existing == DownloadExistingMode.Refuse)
            {
                throw Fail(ErrorCodes.FolderExists, $"Folder '{request.Folder}' already exists on node {NodeId}.");
            }

            var op = AcceptLocked(call);
            _downloadTokens.Add(request.Token);
            var download = new FakeDownload(op, CreatedFolder: !exists)
            {
                Status = new NodeDownloadStatus
                {
                    Folder = request.Folder,
                    RepoId = request.RepoId,
                    Revision = request.Revision,
                    State = DownloadState.Queued,
                    OperationId = op.Id,
                    StartedAt = Now,
                },
            };
            _downloads[request.Folder] = download;
            PublishLocked(new DownloadEvent { Download = download.Status });
            StartDownloadWork(download);
            return FakeReply.Json(download.Status, call.Route.SuccessStatus);
        }
    }

    private void StartDownloadWork(FakeDownload download) => _ = Task.Run(() => RunDownloadAsync(download), CancellationToken.None);

    private async Task RunDownloadAsync(FakeDownload download)
    {
        var op = download.Op;
        var ct = op.Cts.Token;
        const long total = 4L * 1024 * 1024 * 1024;
        try
        {
            lock (_gate)
            {
                if (!Alive(op))
                {
                    return;
                }

                if (download.CreatedFolder)
                {
                    _folders[download.Status.Folder] = FakeFolder.Partial();
                }

                UpdateDownloadLocked(download, download.Status with { State = DownloadState.Downloading, TotalBytes = total });
                StageLocked(op, OperationStages.Downloading, new OperationProgress { Percent = 0, BytesDone = 0, BytesTotal = total });
            }

            for (int step = 1; step <= 4; step++)
            {
                await Task.Delay(_options.DownloadDuration / 4, _time, ct);
                if (step == 4)
                {
                    await _downloadsGate.WaitAsync(ct);
                }

                lock (_gate)
                {
                    if (!Alive(op))
                    {
                        return;
                    }

                    long done = total * step / 4;
                    UpdateDownloadLocked(download, download.Status with { BytesDownloaded = done, BytesPerSecond = 512d * 1024 * 1024 });
                    StageLocked(op, null, new OperationProgress { Percent = step * 25, BytesDone = done, BytesTotal = total });
                }
            }

            lock (_gate)
            {
                if (!Alive(op))
                {
                    return;
                }

                _folders[download.Status.Folder] = FakeFolder.WithConfig(download.Status.Folder, 4);
                UpdateDownloadLocked(download, download.Status with { State = DownloadState.Completed, EndedAt = Now });
                CompleteLocked(op, OperationState.Succeeded, result: download.Status);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled or crashed.
        }
    }

    private void UpdateDownloadLocked(FakeDownload download, NodeDownloadStatus status)
    {
        download.Status = status;
        PublishLocked(new DownloadEvent { Download = status });
    }

    private NodeDownloadStatus DownloadStatusOfLocked(FakeOp op) =>
        _downloads.TryGetValue(op.Target, out var download) && download.Op.Id == op.Id
            ? download.Status
            : op.Result is not null
                ? AgentJson.ReadResult<NodeDownloadStatus>(op.ToStatus())!
                : new NodeDownloadStatus { Folder = op.Target, RepoId = "", State = DownloadState.Failed, OperationId = op.Id, Error = "Unknown download." };

    /// <summary>Marks a running download Cancelled (its operation is completed by the caller).</summary>
    private void CancelDownloadLocked(FakeOp downloadOp)
    {
        downloadOp.Cts.Cancel();
        if (_downloads.TryGetValue(downloadOp.Target, out var download) && download.Op.Id == downloadOp.Id && download.Running)
        {
            if (download.CreatedFolder)
            {
                _folders.Remove(download.Status.Folder);
            }

            UpdateDownloadLocked(download, download.Status with { State = DownloadState.Cancelled, EndedAt = Now });
        }
    }

    private FakeReply DownloadCancel(FakeCall call)
    {
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            if (!_downloads.TryGetValue(call.Target, out var download))
            {
                throw Fail(ErrorCodes.NotFound, $"No download into '{call.Target}' on node {NodeId}.");
            }

            var op = AcceptLocked(call);
            if (download.Running)
            {
                CancelDownloadLocked(download.Op);
                CompleteLocked(download.Op, OperationState.Cancelled);
            }

            CompleteLocked(op, OperationState.Succeeded, result: download.Status);
            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    private void ResumeDownloadLocked(FakeOp op)
    {
        var request = op.Payload.Deserialize<DownloadStartRequest>(AgentJson.Options);
        if (_downloads.TryGetValue(op.Target, out var download))
        {
            var resumed = new FakeDownload(op, download.CreatedFolder) { Status = download.Status with { OperationId = op.Id } };
            _downloads[op.Target] = resumed;
            StartDownloadWork(resumed);
        }
        else if (request is not null)
        {
            var created = new FakeDownload(op, !_folders.ContainsKey(op.Target))
            {
                Status = new NodeDownloadStatus { Folder = op.Target, RepoId = request.RepoId, Revision = request.Revision, State = DownloadState.Queued, OperationId = op.Id, StartedAt = Now },
            };
            _downloads[op.Target] = created;
            StartDownloadWork(created);
        }
        else
        {
            CompleteLocked(op, OperationState.Failed, Error(ErrorCodes.InternalError, "The download request could not be recovered."));
        }
    }

    private sealed record FakeDownload(FakeOp Op, bool CreatedFolder)
    {
        public required NodeDownloadStatus Status { get; set; }

        public bool Running => Status.State is DownloadState.Queued or DownloadState.Downloading;
    }

    private sealed record FakeFolder(ModelFolderInspection? Inspection, long SizeBytes, bool IsPartial)
    {
        public static FakeFolder WithConfig(string folder, double weightsGiB) => new(
            new ModelFolderInspection(
                folder, "Qwen3ForCausalLM", 32768, "BF16", weightsGiB, false,
                new ModelFolderSuggestion(folder, "hermes", null, null, weightsGiB + ModelFolderInspection.RuntimeOverheadGiB + 4, "weights + overhead + KV"),
                KvBytesPerToken: 98304),
            (long)(weightsGiB * 1024 * 1024 * 1024),
            false);

        public static FakeFolder Partial() => new(null, 0, true);

        public ModelFolderEntry ToEntry(string folder) => new()
        {
            Folder = folder,
            SizeBytes = SizeBytes,
            HasConfig = Inspection is not null,
            Partial = IsPartial,
            WeightsGiB = Inspection?.WeightsGiB,
            Architecture = Inspection?.Architecture,
            DetectedFormat = Inspection?.DetectedFormat,
        };
    }
}
