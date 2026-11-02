using Microsoft.Extensions.Logging;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Multinode;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>Where one model is in a restore.</summary>
public enum RestoreModelState
{
    /// <summary>Waiting for its turn.</summary>
    Queued,

    /// <summary>A checkpoint folder is being downloaded.</summary>
    Downloading,

    /// <summary>Definition restored and every checkpoint folder present; not set to auto-start.</summary>
    Ready,

    /// <summary>Auto-start: waiting for another model on its node to finish starting (starts run one at a time per node).</summary>
    WaitingToStart,

    /// <summary>Auto-start: the container was created and is loading.</summary>
    Started,

    /// <summary>Auto-start refused: its memory budget does not fit right now. Left stopped.</summary>
    NotStarted,

    /// <summary>Not selected, or an existing model the owner chose to keep.</summary>
    Skipped,

    /// <summary>
    /// Configuration import (design §9.4): the definition was restored without a node (unplaced in the backup, no node here
    /// with the same uid and no target picked, or "leave unplaced" chosen). Nothing is downloaded or started.
    /// </summary>
    Unplaced,

    /// <summary>The checkpoint is missing and there is no source to download it from.</summary>
    Manual,

    Failed,

    Cancelled,
}

public enum RestoreJobState
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>One model's progress. <see cref="Percent"/> is set while downloading (null when the size is unknown).</summary>
public sealed record RestoreModelProgress(
    string Id,
    string Name,
    RestoreModelState State,
    string? Detail,
    long BytesDone,
    long BytesTotal,
    double? Percent);

/// <summary>Immutable view of a restore job for the UI and the API.</summary>
public sealed record RestoreJobSnapshot(
    string Id,
    RestoreJobState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<RestoreModelProgress> Models,
    IReadOnlyList<string> Notes,
    string? Error)
{
    public bool IsRunning => State == RestoreJobState.Running;
}

/// <summary>What the owner chose on the restore page.</summary>
/// <param name="ModelIds">The backup's models to restore; the rest are reported as skipped.</param>
/// <param name="ReplaceExisting">True: overwrite a model that already exists here. False: keep it and skip it.</param>
/// <param name="HuggingFaceToken">Optional, for gated repos; held in memory for this job only.</param>
/// <param name="Targets">
/// Configuration import (design §9.4, MN-113): the operator's target node per model whose backup node has no node here
/// with the same uid (or to override one); a target with a null node leaves the model unplaced. Models without a target
/// keep their node only on a uid match, else they are restored unplaced.
/// </param>
/// <param name="Actor">Audit actor (<see cref="AuditActors"/>): the admin session; null = <see cref="AuditActors.SystemRestore"/>.</param>
/// <param name="StartRunning">Also start the restored models that were running when the backup was made, as auto-start models start.</param>
/// <param name="ApplyNodeSettings">Apply the backup's node operator settings to the nodes here with the same uid, and the agent-update setting.</param>
/// <param name="ImportHistory">Add the backup's benchmark and tuning results and start times that are not here yet.</param>
/// <param name="ApplyDestination">The backup destination to apply again (through the destination manager and its session check); null = leave it.</param>
/// <param name="ClientKey">The caller's client key for the session checks (managed services, destination).</param>
public sealed record RestoreRequest(
    BackupDocument Backup,
    BackupSecrets? Secrets,
    IReadOnlyCollection<string> ModelIds,
    bool ReplaceExisting,
    bool ImportApiKeys,
    bool ImportHostServices,
    string? HuggingFaceToken = null,
    IReadOnlyList<RestoreTarget>? Targets = null,
    string? Actor = null,
    bool StartRunning = true,
    bool ApplyNodeSettings = true,
    bool ImportHistory = true,
    BackupDestinationRequest? ApplyDestination = null,
    string ClientKey = "restore")
{
    /// <summary>The signed-in admin session: managed services and the backup destination are changed only with it, as on their pages.</summary>
    public System.Security.Claims.ClaimsPrincipal? User { get; init; }

    /// <summary>Redacts the Hugging Face token, the share password and the secrets (records print every member otherwise).</summary>
    public override string ToString() =>
        $"RestoreRequest {{ Models = {ModelIds.Count}, ReplaceExisting = {ReplaceExisting}, ImportApiKeys = {ImportApiKeys}, " +
        $"ImportHostServices = {ImportHostServices}, StartRunning = {StartRunning}, ApplyNodeSettings = {ApplyNodeSettings}, " +
        $"ImportHistory = {ImportHistory}, ApplyDestination = {ApplyDestination is not null}, Targets = {Targets?.Count ?? 0}, " +
        $"HuggingFaceToken = {(HuggingFaceToken is null ? "null" : "***")} }}";
}

/// <summary>Timing for <see cref="RestoreJobService"/> (shortened by tests).</summary>
public sealed class RestoreJobOptions
{
    /// <summary>How often a running download is polled.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long to wait before retrying a start refused because another model is still starting.</summary>
    public TimeSpan StartRetryInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Give up an auto-start after waiting this long for the start slot.</summary>
    public TimeSpan StartRetryTimeout { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Runs one restore at a time, in the background, and keeps its progress in memory (it survives page reloads,
/// not a controller restart). The order is fixed: write the selected definitions, merge API keys by id, restore
/// the Services container list; then, one model after another, download every missing checkpoint folder from its
/// sources (strictly one download at a time) and, once all of a model's folders are present and it is set to
/// auto-start or was running when the backup was made (<see cref="RestoreRequest.StartRunning"/>), queue its start on its node — waiting and retrying while another model on that node is still
/// starting, and leaving it stopped (reported) when its memory budget does not fit. A failure in one model never
/// stops the others.
/// </summary>
/// <remarks>
/// <para>This is the <see cref="RestoreMode.ConfigurationImport"/> mode (design §9.4, MN-113): model definitions, tags,
/// sources, API keys and the Services list; never the CA, node trust state or fencing data (fleet recovery is
/// <see cref="FleetRecovery"/>). A version-1 backup's models are placed on the bootstrap node
/// (<see cref="BackupDocument.WithDefaultPlacement"/>). With a node registry (<paramref name="nodes"/>) a version-2
/// placement is kept only when a node here has the backup node's uid (<see cref="RestorePlacements"/>); otherwise the
/// operator's <see cref="RestoreRequest.Targets"/> decide, and a model without one is restored unplaced. Without a
/// registry (the single-box wiring) the backup's placements are kept as before.</para>
/// <para>Downloads run per node: one at a time within a node and, with per-node resources
/// (<paramref name="nodeResources"/>), in parallel across nodes; with the single-box default every download shares one
/// lane, strictly one at a time. Auto-starts run in a <see cref="PerNodeStartLanes"/> (design §4.8). A node that is not
/// serving (<see cref="NodeUnavailableException"/>, D51) leaves its models stopped, reported once per node (W3 review).</para>
/// </remarks>
/// <param name="bootstrapNodeId">The bootstrap node id; null = <see cref="RegistryMigrationOptions.DefaultBootstrapNodeId"/>.</param>
/// <param name="nodes">Node registry for the uid rule; null = keep the backup's placements (single-box wiring).</param>
/// <param name="nodeResources">Folders and downloader per node; null = <paramref name="folders"/>/<paramref name="downloader"/> for all.</param>
/// <param name="audit">Audit log (design §10.5); null = <see cref="NullAuditLog"/>.</param>
/// <param name="ownership">
/// Ownership records (design §6.7): each restored model is held by a restore record (<see cref="OwnershipKind.Restore"/>,
/// id <c>restore-{job}-{model}</c>) from before its definition is written until its downloads and auto-start are done, and
/// the definition is written as the holder (<see cref="ModelWriteScope.AsOwner"/>), so the registry's validators let it
/// place or re-place the model and refuse other placement writers meanwhile. A model another record holds (a move, a job,
/// a placement edit) is skipped with the holder named. Records are released on every path of the job; one left by a crash
/// is released at boot (<see cref="RestoreOwnershipRecovery"/>) or by the next restore. Null = no holds (P1 behaviour).
/// </param>
/// <param name="destinations">Applies the backup destination again (<see cref="RestoreRequest.ApplyDestination"/>).</param>
/// <param name="agentUpdates">The agent-update setting, set with the node settings.</param>
/// <param name="dataPath">The master's data path, where benchmark and tuning results go; null = history is not imported.</param>
/// <param name="startupTimes">The start-time measurements the history adds to.</param>
/// <param name="hostServiceManagers">Each node's Services manager, to add back managed units with the Services list.</param>
public sealed partial class RestoreJobService(
    IModelRegistry registry,
    IApiKeyStore apiKeys,
    IModelRuntime runtime,
    IHuggingFaceDownloader downloader,
    ICheckpointFolders folders,
    RestoreJobOptions jobOptions,
    TimeProvider timeProvider,
    ILogger<RestoreJobService> logger,
    HostServicesStore? hostServices = null,
    string? bootstrapNodeId = null,
    INodeRegistry? nodes = null,
    IRestoreNodeResources? nodeResources = null,
    IAuditLog? audit = null,
    IOwnershipStore? ownership = null,
    ITokenStore? tokens = null,
    IBackupDestinationManager? destinations = null,
    AgentUpdateSettingsStore? agentUpdates = null,
    string? dataPath = null,
    StartupTimeTracker? startupTimes = null,
    INodeHostServiceManagers? hostServiceManagers = null)
{
    private const string SharedLane = "";

    /// <summary>Id prefix of the restore records this service writes (<c>restore-{jobId}-{modelId}</c>).</summary>
    public const string RecordIdPrefix = "restore-";

    private readonly string _bootstrapNodeId =
        string.IsNullOrWhiteSpace(bootstrapNodeId) ? RegistryMigrationOptions.DefaultBootstrapNodeId : bootstrapNodeId.Trim();

    private readonly Lock _gate = new();
    private Job? _job;

    /// <summary>Raised (on a background thread) whenever the snapshot changes.</summary>
    public event Action<RestoreJobSnapshot>? Changed;

    public RestoreJobSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _job?.Snapshot();
            }
        }
    }

    /// <summary>Completes when the current job finishes (immediately when there is none).</summary>
    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _job?.Task ?? Task.CompletedTask;
            }
        }
    }

    /// <exception cref="InvalidOperationException">A restore is already running.</exception>
    public RestoreJobSnapshot Start(RestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with { Backup = request.Backup.WithDefaultPlacement(_bootstrapNodeId) };
        lock (_gate)
        {
            if (_job is { } running && running.State == RestoreJobState.Running)
            {
                throw new InvalidOperationException("A restore is already running.");
            }

            var selected = new HashSet<string>(request.ModelIds, StringComparer.Ordinal);
            var rows = request.Backup.Models
                .Select(m => new RestoreModelProgress(
                    m.Id, m.ServedName,
                    selected.Contains(m.Id) ? RestoreModelState.Queued : RestoreModelState.Skipped,
                    selected.Contains(m.Id) ? null : "Not selected.",
                    0, 0, null))
                .ToList();

            var job = new Job(Guid.NewGuid().ToString("N")[..12], timeProvider.GetUtcNow(), rows);
            _job?.Cancellation.Dispose();
            _job = job;
            job.Task = Task.Run(() => RunAsync(job, request, job.Cancellation.Token));
            Log.Started(logger, job.Id, selected.Count);
            return job.Snapshot();
        }
    }

    /// <summary>Stops the running job: the download in flight is cancelled and models not reached yet are marked Cancelled.</summary>
    public async Task CancelAsync()
    {
        Job? job;
        lock (_gate)
        {
            job = _job;
        }

        if (job is null || job.State != RestoreJobState.Running)
        {
            return;
        }

        await job.Cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await job.Task!.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }
    }

    /// <summary>Forgets a finished job so the page offers a new restore. A running job is kept.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_job is { State: not RestoreJobState.Running })
            {
                _job.Cancellation.Dispose();
                _job = null;
            }
        }
    }

    private async Task RunAsync(Job job, RestoreRequest request, CancellationToken ct)
    {
        // The restore's agent operations (downloads, auto-starts) are system:restore's in the audit log (MN-116, MN-130).
        using var audit = AuditScope.Begin(AuditActors.SystemRestore);
        var starts = new PerNodeStartLanes();
        try
        {
            // Restore records left by a restore that never finished (the process stopped meanwhile) hold their models
            // until released; this process runs one restore at a time, so every one but this job's is stale.
            await ReleaseStaleOwnershipAsync(ct).ConfigureAwait(false);
            var restored = await RestoreSettingsAsync(job, request, ct).ConfigureAwait(false);

            // With a node registry an unplaced definition is really unplaced (P2): nothing to download or start. Without
            // one (the single-box wiring) it runs on the bootstrap node, as in P1.
            bool unplacedIsUnplaced = nodes is not null;
            foreach (var model in restored.Where(m => unplacedIsUnplaced && m.Placement is null))
            {
                SetModel(job, model.Id, RestoreModelState.Unplaced,
                    "Definition restored without a node: place it from the model editor, then it downloads and starts there.");
                await ReleaseHoldAsync(job, model.Id).ConfigureAwait(false);
            }

            // Auto-start models start, and with StartRunning so do the models that were running when the backup was made.
            var toStart = restored.Where(m => m.AutoStart || (request.StartRunning && request.Backup.WasRunning(m.Id)))
                .Select(m => m.Id).ToHashSet(StringComparer.Ordinal);

            // One lane per node (sequential within it); the single-box default shares one lane for every download.
            var lanes = restored
                .Where(m => !unplacedIsUnplaced || m.Placement is not null)
                .GroupBy(m => nodeResources is null ? SharedLane : PerNodeStartLanes.NodeOf(m, _bootstrapNodeId), StringComparer.Ordinal)
                .Select(lane => RestoreLaneAsync(job, [.. lane], request.HuggingFaceToken, toStart, starts, ct))
                .ToList();
            await Task.WhenAll(lanes).ConfigureAwait(false);

            await starts.WhenAllAsync().ConfigureAwait(false);
            Finish(job, RestoreJobState.Completed, null);
            await AuditAsync(request, AuditResults.Succeeded, $"configuration import: {restored.Count} definition(s)").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Queued starts see the same token; let them settle so none reports after the job is marked cancelled.
            await starts.WhenAllSettledAsync().ConfigureAwait(false);
            Update(job, rows => rows.Select(r => r.State is RestoreModelState.Queued or RestoreModelState.Downloading or RestoreModelState.WaitingToStart
                ? r with { State = RestoreModelState.Cancelled, Detail = "Restore cancelled.", Percent = null }
                : r).ToList());
            Finish(job, RestoreJobState.Cancelled, null);
            await AuditAsync(request, AuditResults.Failed, "configuration import cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await starts.WhenAllSettledAsync().ConfigureAwait(false);
            Log.Failed(logger, ex, job.Id);
            Finish(job, RestoreJobState.Failed, ex.Message);
            await AuditAsync(request, AuditResults.Failed, "configuration import failed").ConfigureAwait(false);
        }
        finally
        {
            // Every path ends here (completed, cancelled, failed): no restore record outlives its job in this process. One
            // left by a crash is released by the next restore or at boot (ReleaseStaleOwnershipAsync).
            await ReleaseAllHoldsAsync(job).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Releases the restore records (design §6.7, <see cref="OwnershipKind.Restore"/>) that no running restore of this
    /// process holds, i.e. records left by a restore that a master restart interrupted. Run at boot
    /// (<see cref="RestoreOwnershipRecovery"/>) and before every restore. Only records this service writes (id prefix
    /// <see cref="RecordIdPrefix"/>) are touched. Returns how many it released.
    /// </summary>
    public async Task<int> ReleaseStaleOwnershipAsync(CancellationToken ct = default)
    {
        if (ownership is null)
        {
            return 0;
        }

        HashSet<string> held;
        lock (_gate)
        {
            held = _job is { State: RestoreJobState.Running } running
                ? new HashSet<string>(running.Holds.Values, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }

        int released = 0;
        foreach (var record in await ownership.ListAsync(ct).ConfigureAwait(false))
        {
            if (record.Kind == OwnershipKind.Restore && record.Id.StartsWith(RecordIdPrefix, StringComparison.Ordinal) && !held.Contains(record.Id) &&
                await ownership.ReleaseAsync(record.Id, ct).ConfigureAwait(false))
            {
                Log.StaleRecordReleased(logger, record.Id, record.ModelId);
                released++;
            }
        }

        return released;
    }

    /// <summary>
    /// Takes this job's restore record on <paramref name="modelId"/>. Returns the holder when another record holds the
    /// model (a move, a job, a placement edit); null when this job now holds it (its id is in <see cref="Job.Holds"/>).
    /// </summary>
    private async Task<OwnershipRecord?> AcquireHoldAsync(Job job, string modelId, ModelDefinition? existing, RestoreRequest request, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var record = new OwnershipRecord
        {
            Id = RecordIdPrefix + job.Id + "-" + modelId,
            Kind = OwnershipKind.Restore,
            ModelId = modelId,
            PlacementGeneration = existing?.Placement?.Generation ?? 0,
            Stage = "restoring",
            StartedAt = now,
            UpdatedAt = now,
            Actor = request.Actor ?? AuditActors.SystemRestore,
            Description = "a restore (configuration import)",
            ReferenceId = job.Id,
        };
        var result = await ownership!.TryAcquireAsync(record, ct).ConfigureAwait(false);
        if (!result.Acquired)
        {
            return result.Holder ?? record;
        }

        lock (_gate)
        {
            job.Holds[modelId] = record.Id;
        }

        return null;
    }

    /// <summary>Releases this job's record on <paramref name="modelId"/>, if it holds one. Never throws.</summary>
    private async Task ReleaseHoldAsync(Job job, string modelId)
    {
        string? recordId;
        lock (_gate)
        {
            if (!job.Holds.Remove(modelId, out recordId))
            {
                return;
            }
        }

        try
        {
            await ownership!.ReleaseAsync(recordId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The next restore, or the boot sweep, releases it (ReleaseStaleOwnershipAsync).
            Log.ReleaseFailed(logger, ex, modelId);
        }
    }

    private async Task ReleaseAllHoldsAsync(Job job)
    {
        List<string> models;
        lock (_gate)
        {
            models = [.. job.Holds.Keys];
        }

        foreach (string modelId in models)
        {
            await ReleaseHoldAsync(job, modelId).ConfigureAwait(false);
        }
    }

    /// <summary>One node's models, one after another (downloads strictly sequential within the lane).</summary>
    private async Task RestoreLaneAsync(
        Job job, List<ModelDefinition> models, string? token, HashSet<string> toStart, PerNodeStartLanes starts, CancellationToken ct)
    {
        foreach (var model in models)
        {
            ct.ThrowIfCancellationRequested();
            bool startQueued = false;
            try
            {
                startQueued = await RestoreCheckpointsAsync(job, model, token, toStart.Contains(model.Id), starts, ct).ConfigureAwait(false);
            }
            finally
            {
                if (!startQueued)
                {
                    await ReleaseHoldAsync(job, model.Id).ConfigureAwait(false); // a queued start releases it when it ends
                }
            }
        }
    }

    /// <summary>One audit line for the job (design §10.5); never a secret (counts and the mode only).</summary>
    private Task AuditAsync(RestoreRequest request, string result, string detail) =>
        (audit ?? NullAuditLog.Instance).WriteAsync(new AuditRecord
        {
            Time = timeProvider.GetUtcNow(),
            Actor = request.Actor ?? AuditActors.SystemRestore,
            Action = AuditActions.Restore,
            Target = request.Backup.HostName,
            Result = result,
            Detail = detail,
        }, CancellationToken.None);

    /// <summary>Writes the selected definitions, then keys and the Services list. Returns the models whose definitions were written.</summary>
    private async Task<List<ModelDefinition>> RestoreSettingsAsync(Job job, RestoreRequest request, CancellationToken ct)
    {
        var selected = new HashSet<string>(request.ModelIds, StringComparer.Ordinal);
        IReadOnlyList<NodeRecord>? localNodes = nodes is null ? null : await nodes.ListAsync(ct).ConfigureAwait(false);
        var targets = (request.Targets ?? []).GroupBy(t => t.ModelId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        var restored = new List<ModelDefinition>();
        foreach (var backupModel in request.Backup.Models.Where(m => selected.Contains(m.Id)))
        {
            ct.ThrowIfCancellationRequested();
            var existing = await registry.GetAsync(backupModel.Id, ct).ConfigureAwait(false);
            if (existing is not null && !request.ReplaceExisting)
            {
                SetModel(job, backupModel.Id, RestoreModelState.Skipped, "Kept the definition already here.");
                continue;
            }

            // With an ownership store the model is held by a restore record from here until its checkpoints and auto-start
            // are done (design §6.7: no move, job or placement edit during its restore), and its definition is written as
            // that record's holder (ModelWriteScope.AsOwner): the registry's validators (MN-108) then let the restore place
            // an unplaced model or change its placement, and refuse any other writer that would change the placement or
            // delete the model meanwhile. A model another record holds is skipped, naming the holder.
            string? recordId = null;
            if (ownership is not null)
            {
                OwnershipRecord? holder;
                try
                {
                    holder = await AcquireHoldAsync(job, backupModel.Id, existing, request, ct).ConfigureAwait(false);
                }
                catch (ArgumentException ex)
                {
                    SetModel(job, backupModel.Id, RestoreModelState.Failed, $"Definition not restored: {ex.Message}");
                    continue;
                }

                if (holder is not null)
                {
                    SetModel(job, backupModel.Id, RestoreModelState.Skipped,
                        $"Not restored: {ModelBusyException.DescribeHolder(holder)} holds the model (stage {holder.Stage}). Restore it again once that finishes.");
                    continue;
                }

                lock (_gate)
                {
                    recordId = job.Holds[backupModel.Id];
                }

                // Re-read under the hold: what the restore replaces is fixed from here (a write that landed between the first
                // read and the hold, e.g. a move commit, is seen now).
                existing = await registry.GetAsync(backupModel.Id, ct).ConfigureAwait(false);
            }

            var resolved = RestorePlacements.Resolve(
                request.Backup, backupModel, localNodes, targets.GetValueOrDefault(backupModel.Id), _bootstrapNodeId);
            if (resolved.Error is not null)
            {
                SetModel(job, backupModel.Id, RestoreModelState.Failed, $"Definition not restored: {resolved.Error}");
                await ReleaseHoldAsync(job, backupModel.Id).ConfigureAwait(false);
                continue;
            }

            // The stored generation travels with the placement, whatever the backup carried: the placement validator (MN-108,
            // D75) keeps it when node and GPUs stay, increments it when they change or the model was unplaced here, and
            // starts a new model at 0. Without a hold (no ownership store) placing a model unplaced here is refused (409).
            var placement = resolved.Placement is null
                ? null
                : resolved.Placement with { Generation = existing?.Placement?.Generation ?? 0 };
            var model = backupModel with { Placement = placement };
            try
            {
                using (recordId is null ? null : ModelWriteScope.AsOwner(recordId))
                {
                    await registry.UpsertAsync(model, ct).ConfigureAwait(false);
                }

                // What the registry stored (validated placement, stamped generation) is what downloads and starts.
                restored.Add(await registry.GetAsync(model.Id, ct).ConfigureAwait(false) ?? model);
                if (resolved.Notable)
                {
                    AddNote(job, $"'{model.Id}': {resolved.Detail}");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or PlacementConflictException or ModelBusyException)
            {
                SetModel(job, model.Id, RestoreModelState.Failed, $"Definition not restored: {ex.Message}");
                await ReleaseHoldAsync(job, model.Id).ConfigureAwait(false);
            }
        }

        AddNote(job, $"Restored {restored.Count} model definition{(restored.Count == 1 ? "" : "s")}.");

        if (request.ImportApiKeys && request.Secrets is { ApiKeys.Count: > 0 } secrets)
        {
            var result = await apiKeys.ImportAsync(secrets.ApiKeys, ct).ConfigureAwait(false);
            AddNote(job, $"API keys: {result.Added} imported, {result.Skipped} already here.");
        }

        if (request.ImportApiKeys && tokens is not null && request.Secrets is { Tokens.Count: > 0 } withTokens)
        {
            var result = await tokens.ImportAsync(withTokens.Tokens, ct).ConfigureAwait(false);
            AddNote(job, $"Saved tokens: {result.Added} imported, {result.Skipped} already here or with a name in use.");
        }

        if (request.ImportHostServices && hostServices is not null)
        {
            var from = RestorePlacements.ServicesForBootstrap(request.Backup, localNodes, _bootstrapNodeId);
            if (from.Lists is { } services)
            {
                await hostServices.ImportAsync(services, ct).ConfigureAwait(false);
                string source = from.FromNodeId is null || from.FromNodeId == _bootstrapNodeId ? "" : $" (from the backup's node '{from.FromNodeId}')";
                AddNote(job, $"Services: {services.Containers?.Count ?? 0} managed container{(services.Containers?.Count == 1 ? "" : "s")} restored{source}.");
            }

            await ImportOtherNodesServicesAsync(job, request.Backup, localNodes, from.FromNodeId, ct).ConfigureAwait(false);
        }

        if (request.ImportHostServices)
        {
            await ImportHostUnitsAsync(job, request, localNodes, ct).ConfigureAwait(false);
        }

        if (request.ApplyNodeSettings)
        {
            await ApplyNodeSettingsAsync(job, request.Backup, localNodes, ct).ConfigureAwait(false);
        }

        if (request.ImportHistory && request.Backup.History is { } history)
        {
            int runs = string.IsNullOrEmpty(dataPath) ? 0 : await BackupHistoryFiles.ImportAsync(dataPath, history, ct).ConfigureAwait(false);
            int times = startupTimes?.ImportMissing(history.StartupTimes ?? new Dictionary<string, double>()) ?? 0;
            AddNote(job, $"History: {runs} benchmark or tuning result(s) and {times} start time(s) added; results already here were kept.");
        }

        if (request.ApplyDestination is { } destination && destinations is not null)
        {
            try
            {
                var status = await destinations.ApplyAsync(destination, request.User, request.ClientKey, ct).ConfigureAwait(false);
                AddNote(job, $"Backup destination: {status.Configured?.Source ?? "the local disk"} applied again.");
            }
            catch (Exception ex) when (ex is AdminReauthenticationException or BackupDestinationException)
            {
                AddNote(job, $"Backup destination not applied: {ex.Message} Set it on the Back up tab.");
            }
        }

        return restored;
    }

    /// <summary>
    /// The backup's node operator settings on the nodes here with the same uid (<see cref="RestoreNodeSettings"/>), and the
    /// fleet's agent-update setting. Identity and trust (approval, draining, revocation, pins) are never touched.
    /// </summary>
    private async Task ApplyNodeSettingsAsync(Job job, BackupDocument backup, IReadOnlyList<NodeRecord>? localNodes, CancellationToken ct)
    {
        if (nodes is not null && localNodes is not null && backup.Nodes is { } backupNodes)
        {
            var applied = new List<string>();
            var unmatched = new List<string>();
            foreach (var backupNode in backupNodes.Where(n => !n.Revoked))
            {
                if (RestoreNodeSettings.Match(backupNode, localNodes) is not { } local)
                {
                    unmatched.Add(backupNode.Id);
                }
                else if (RestoreNodeSettings.Changes(local, backupNode).Count > 0 &&
                    await nodes.UpdateAsync(local.Id, n => RestoreNodeSettings.Apply(n, backupNode), ct).ConfigureAwait(false) is not null)
                {
                    applied.Add(local.Id);
                }
            }

            AddNote(job, applied.Count == 0
                ? "Node settings: nothing to change."
                : $"Node settings applied to {string.Join(", ", applied.Select(n => $"'{n}'"))}.");
            if (unmatched.Count > 0)
            {
                AddNote(job, $"Node settings of {string.Join(", ", unmatched.Select(n => $"'{n}'"))} were not applied: no node here has the same uid.");
            }
        }

        if (backup.AgentUpdatesAutomatic is bool automatic && agentUpdates is { CanPersist: true } && agentUpdates.Automatic != automatic)
        {
            agentUpdates.SetAutomatic(automatic);
            AddNote(job, $"Automatic agent updates turned {(automatic ? "on" : "off")}, as in the backup.");
        }
    }

    /// <summary>
    /// The managed systemd/Windows services of each backup node, added back on the node here with the same uid through its
    /// Services manager, with the restoring admin's session (the Services page's check). Units already managed there are
    /// kept; one the node refuses (gone, denied, name taken) is noted.
    /// </summary>
    private async Task ImportHostUnitsAsync(Job job, RestoreRequest request, IReadOnlyList<NodeRecord>? localNodes, CancellationToken ct)
    {
        if (hostServiceManagers is null || request.Backup.HostUnits is not { Count: > 0 } perNode)
        {
            return;
        }

        foreach (var (backupNodeId, units) in perNode.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var backupNode = request.Backup.Nodes?.FirstOrDefault(n => n.Id == backupNodeId);
            string? nodeId = localNodes is null
                ? backupNodeId == _bootstrapNodeId ? backupNodeId : null
                : backupNode is null ? null : RestoreNodeSettings.Match(backupNode, localNodes)?.Id;
            if (nodeId is null)
            {
                AddNote(job, $"Managed services of '{backupNodeId}' were not restored: no node here has the same uid.");
                continue;
            }

            int added = 0;
            var failed = new List<string>();
            try
            {
                var manager = await hostServiceManagers.ForNodeAsync(nodeId, ct).ConfigureAwait(false)
                    ?? throw new HostServiceException("its Services are not reachable from this master");
                var current = (await manager.ListAsync(ct).ConfigureAwait(false)).Services;
                foreach (var unit in units.Where(u => !current.Any(c =>
                    string.Equals(c.Name, u.Name, StringComparison.Ordinal) || string.Equals(c.Unit, u.Unit, StringComparison.OrdinalIgnoreCase))))
                {
                    try
                    {
                        await manager.AddToWhitelistAsync(
                            new HostServiceWhitelistAdd(unit.Kind, unit.Unit, unit.DisplayName, unit.Protected, unit.Name, null, request.ClientKey) { User = request.User },
                            ct).ConfigureAwait(false);
                        added++;
                    }
                    catch (Exception ex) when (ex is HostServiceException or AdminReauthenticationException)
                    {
                        failed.Add($"{unit.Name} ({ex.Message})");
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                AddNote(job, $"Managed services of '{nodeId}' were not restored: {ex.Message}");
                continue;
            }

            AddNote(job, $"Managed services of '{nodeId}': {added} added, {units.Count - added - failed.Count} already managed." +
                (failed.Count == 0 ? "" : $" Not added: {string.Join("; ", failed)}."));
        }
    }

    /// <summary>
    /// The other nodes' Services lists of a version-2 backup (design §5.5 per-node <c>hostServices</c>; W7 review MN-126):
    /// each goes to the node here with the <b>same uid</b> (the configuration-import rule of §9.4; a matching slug alone may
    /// be another machine), into its <c>host-services.{nodeId}.json</c> next to <c>host-services.json</c>. Lists of nodes
    /// with no uid match stay in the backup (noted). Without a node registry (the single-box wiring) nothing but the
    /// bootstrap node's lists is imported, as before.
    /// </summary>
    private async Task ImportOtherNodesServicesAsync(Job job, BackupDocument backup, IReadOnlyList<NodeRecord>? localNodes, string? bootstrapSource, CancellationToken ct)
    {
        if (localNodes is null || backup.HostServices?.Nodes is not { Count: > 0 } perNode ||
            Path.GetDirectoryName(Path.GetFullPath(hostServices!.FilePath)) is not { } dataPath)
        {
            return;
        }

        var unmatched = new List<string>();
        foreach (var (backupNodeId, lists) in perNode.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (backupNodeId == bootstrapSource || lists is null)
            {
                continue; // imported into the bootstrap node's list above
            }

            var backupNode = backup.Nodes?.FirstOrDefault(n => n.Id == backupNodeId);
            var local = backupNode is null
                ? null
                : localNodes.FirstOrDefault(n => !n.Revoked && string.Equals(n.Uid, backupNode.Uid, StringComparison.OrdinalIgnoreCase));
            if (local is null || local.Id == _bootstrapNodeId)
            {
                unmatched.Add(backupNodeId);
                continue;
            }

            int count = await NodeHostServicesFiles.ImportAsync(dataPath, local.Id, lists, ct).ConfigureAwait(false);
            string source = local.Id == backupNodeId ? "" : $" (from the backup's node '{backupNodeId}')";
            AddNote(job, $"Services of node '{local.Id}': {count} managed container{(count == 1 ? "" : "s")} restored{source}.");
        }

        if (unmatched.Count > 0)
        {
            AddNote(job, $"The Services lists of {string.Join(", ", unmatched.Select(n => $"'{n}'"))} were not restored: no node here has the same uid.");
        }
    }

    /// <summary>Downloads the model's missing folders; true when its start was queued (the start then releases its hold).</summary>
    private async Task<bool> RestoreCheckpointsAsync(Job job, ModelDefinition model, string? token, bool start, PerNodeStartLanes starts, CancellationToken ct)
    {
        string nodeId = PerNodeStartLanes.NodeOf(model, _bootstrapNodeId);
        var nodeFolders = nodeResources?.FoldersFor(nodeId) ?? folders;
        var nodeDownloader = nodeResources?.DownloaderFor(nodeId) ?? downloader;
        bool primaryHasSource = model.Sources.Any(s => string.Equals(s.Folder, model.ModelPath, StringComparison.Ordinal));
        if (!primaryHasSource && !nodeFolders.IsPresent(model.ModelPath))
        {
            SetModel(job, model.Id, RestoreModelState.Manual,
                $"No download source: copy the checkpoint into /models/{model.ModelPath} on node '{nodeId}', then start it from the Dashboard.");
            return false;
        }

        var missing = model.Sources.Where(s => !nodeFolders.IsPresent(s.Folder)).ToList();
        long[] done = new long[missing.Count];
        long[] totals = new long[missing.Count];
        for (int i = 0; i < missing.Count; i++)
        {
            var source = missing[i];
            string? error = await DownloadAsync(job, nodeDownloader, model, source, token, i, done, totals, ct).ConfigureAwait(false);
            if (error is not null)
            {
                SetModel(job, model.Id, RestoreModelState.Failed, $"Download of {source.RepoId} into {source.Folder} failed: {error}");
                return false;
            }
        }

        if (!start)
        {
            SetModel(job, model.Id, RestoreModelState.Ready,
                missing.Count == 0 ? "Checkpoint already on disk." : $"Downloaded {missing.Count} checkpoint folder{(missing.Count == 1 ? "" : "s")}.");
            return false;
        }

        SetModel(job, model.Id, RestoreModelState.WaitingToStart, $"Queued to start on node {nodeId} (models start one at a time per node).");
        _ = starts.Enqueue(nodeId, () => StartQueuedAsync(job, model, ct));
        return true;
    }

    /// <summary>One queued auto-start; any failure but cancellation is this model's alone.</summary>
    private async Task StartQueuedAsync(Job job, ModelDefinition model, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await StartAsync(job, model, ct).ConfigureAwait(false);
        }
        catch (NodeUnavailableException ex)
        {
            LeftStoppedNodeUnavailable(job, model, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.StartFailed(logger, ex, job.Id, model.Id);
            SetModel(job, model.Id, RestoreModelState.Failed, $"Start failed: {ex.Message}");
        }
        finally
        {
            await ReleaseHoldAsync(job, model.Id).ConfigureAwait(false);
        }
    }

    /// <summary>Downloads one source and waits for it. Null on success, otherwise why it failed.</summary>
    private async Task<string?> DownloadAsync(
        Job job, IHuggingFaceDownloader nodeDownloader, ModelDefinition model, ModelSource source, string? token, int index, long[] done, long[] totals, CancellationToken ct)
    {
        string detail = $"Downloading {source.RepoId} into {source.Folder}";
        SetProgress(job, model.Id, detail, done, totals);

        var request = new DownloadRequest(source.RepoId, source.Revision, token, source.Folder, source.AllowPatterns);
        try
        {
            var started = await nodeDownloader.StartAsync(request, ct).ConfigureAwait(false);
            totals[index] = started.TotalBytes;
        }
        catch (InvalidOperationException)
        {
            // A download into this folder is already running (e.g. started from the editor): follow it instead.
        }
        catch (Exception ex) when (ex is ArgumentException or HuggingFaceApiException or HttpRequestException or DockerApiException)
        {
            return ex.Message;
        }

        try
        {
            while (true)
            {
                await Task.Delay(jobOptions.PollInterval, timeProvider, ct).ConfigureAwait(false);
                var status = await nodeDownloader.GetAsync(source.Folder, ct).ConfigureAwait(false);
                if (status is null)
                {
                    return "the download disappeared (cancelled elsewhere?)";
                }

                done[index] = status.BytesDownloaded;
                totals[index] = Math.Max(totals[index], status.TotalBytes);
                switch (status.State)
                {
                    case DownloadState.Completed:
                        done[index] = totals[index] > 0 ? totals[index] : status.BytesDownloaded;
                        SetProgress(job, model.Id, detail, done, totals);
                        return null;
                    case DownloadState.Failed:
                        return FirstLine(status.Error) ?? "the download container failed";
                    case DownloadState.Cancelled:
                        return "the download was cancelled";
                    default:
                        SetProgress(job, model.Id, detail, done, totals);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                await nodeDownloader.CancelAsync(source.Folder, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or DockerApiException or InvalidOperationException)
            {
                Log.CancelDownloadFailed(logger, ex, source.Folder);
            }

            throw;
        }
    }

    /// <summary>Auto-start, one model at a time per node: retry while another model is starting, leave it stopped when memory does not fit.</summary>
    private async Task StartAsync(Job job, ModelDefinition model, CancellationToken ct)
    {
        var status = await runtime.GetStatusAsync(model.Id, ct).ConfigureAwait(false);
        if (status.State is ModelState.Starting or ModelState.Ready)
        {
            SetModel(job, model.Id, RestoreModelState.Started, "Already running; apply the restored settings from the Dashboard if it shows a restart pending.");
            return;
        }

        var deadline = timeProvider.GetUtcNow() + jobOptions.StartRetryTimeout;
        while (true)
        {
            try
            {
                await runtime.StartAsync(model.Id, force: false, ct).ConfigureAwait(false);
                SetModel(job, model.Id, RestoreModelState.Started, "Started; loading takes 2–5 minutes.");
                return;
            }
            catch (ModelStartConflictException ex)
            {
                if (timeProvider.GetUtcNow() >= deadline)
                {
                    SetModel(job, model.Id, RestoreModelState.Failed,
                        $"Not started: '{ex.StartingModelId}' was still starting after {jobOptions.StartRetryTimeout.TotalMinutes:0} min. Start it from the Dashboard.");
                    return;
                }

                SetModel(job, model.Id, RestoreModelState.WaitingToStart,
                    $"Waiting for '{ex.StartingModelId ?? "another model"}' to finish starting (models start one at a time per node).");
                await Task.Delay(jobOptions.StartRetryInterval, timeProvider, ct).ConfigureAwait(false);
            }
            catch (ModelMemoryAdmissionException ex)
            {
                SetModel(job, model.Id, RestoreModelState.NotStarted, $"Left stopped: {ex.Message}");
                return;
            }
            catch (NodeUnavailableException ex)
            {
                LeftStoppedNodeUnavailable(job, model, ex);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or DockerApiException or InvalidOperationException or KeyNotFoundException)
            {
                SetModel(job, model.Id, RestoreModelState.Failed, $"Start failed: {ex.Message}");
                return;
            }
        }
    }

    /// <summary>
    /// The model's node is not serving, unplaced here, draining or revoked (D51): the model stays stopped (not failed) and
    /// the node is reported once in the notes (W3 review MN-094).
    /// </summary>
    private void LeftStoppedNodeUnavailable(Job job, ModelDefinition model, NodeUnavailableException ex)
    {
        string nodeId = ex.NodeId ?? PerNodeStartLanes.NodeOf(model, _bootstrapNodeId);
        SetModel(job, model.Id, RestoreModelState.NotStarted,
            $"Left stopped: node '{nodeId}' cannot start it now ({ex.Message}). Start it from the Dashboard once the node is back.");
        bool first;
        lock (_gate)
        {
            first = job.UnavailableNodes.Add(nodeId);
        }

        if (first)
        {
            AddNote(job, $"Node '{nodeId}' is not available ({ex.Reason}): its auto-start models are left stopped.");
        }
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? null : lines[^1];
    }

    private void SetProgress(Job job, string id, string detail, long[] done, long[] totals)
    {
        long d = done.Sum();
        long t = totals.Sum();
        double? percent = t > 0 ? Math.Clamp(100.0 * d / t, 0, 100) : null;
        Update(job, rows => rows.Select(r => r.Id == id
            ? r with { State = RestoreModelState.Downloading, Detail = detail, BytesDone = d, BytesTotal = t, Percent = percent }
            : r).ToList());
    }

    private void SetModel(Job job, string id, RestoreModelState state, string? detail) =>
        Update(job, rows => rows.Select(r => r.Id == id ? r with { State = state, Detail = detail, Percent = null } : r).ToList());

    private void AddNote(Job job, string note)
    {
        RestoreJobSnapshot snapshot;
        lock (_gate)
        {
            job.Notes.Add(note);
            snapshot = job.Snapshot();
        }

        Changed?.Invoke(snapshot);
    }

    private void Update(Job job, Func<List<RestoreModelProgress>, List<RestoreModelProgress>> change)
    {
        RestoreJobSnapshot snapshot;
        lock (_gate)
        {
            job.Rows = change(job.Rows);
            snapshot = job.Snapshot();
        }

        Changed?.Invoke(snapshot);
    }

    private void Finish(Job job, RestoreJobState state, string? error)
    {
        RestoreJobSnapshot snapshot;
        lock (_gate)
        {
            job.State = state;
            job.Error = error;
            job.FinishedAt = timeProvider.GetUtcNow();
            snapshot = job.Snapshot();
        }

        Log.Finished(logger, job.Id, state);
        Changed?.Invoke(snapshot);
    }

    private sealed class Job(string id, DateTimeOffset startedAt, List<RestoreModelProgress> rows)
    {
        public string Id { get; } = id;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public List<RestoreModelProgress> Rows { get; set; } = rows;

        public List<string> Notes { get; } = [];

        public RestoreJobState State { get; set; } = RestoreJobState.Running;

        public DateTimeOffset? FinishedAt { get; set; }

        public string? Error { get; set; }

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Task { get; set; }

        /// <summary>Restore records this job holds, by model id (guarded by the service's gate).</summary>
        public Dictionary<string, string> Holds { get; } = new(StringComparer.Ordinal);

        /// <summary>Nodes already reported as unavailable (guarded by the service's gate).</summary>
        public HashSet<string> UnavailableNodes { get; } = new(StringComparer.Ordinal);

        public RestoreJobSnapshot Snapshot() => new(Id, State, StartedAt, FinishedAt, [.. Rows], [.. Notes], Error);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Restore {JobId}: started for {Count} model(s).")]
        public static partial void Started(ILogger logger, string jobId, int count);

        [LoggerMessage(Level = LogLevel.Information, Message = "Restore {JobId}: finished ({State}).")]
        public static partial void Finished(ILogger logger, string jobId, RestoreJobState state);

        [LoggerMessage(Level = LogLevel.Error, Message = "Restore {JobId}: failed.")]
        public static partial void Failed(ILogger logger, Exception exception, string jobId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Restore {JobId}: auto-start of {ModelId} failed.")]
        public static partial void StartFailed(ILogger logger, Exception exception, string jobId, string modelId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Restore: could not cancel the download into {Folder}.")]
        public static partial void CancelDownloadFailed(ILogger logger, Exception exception, string folder);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Restore: could not release the restore record of {ModelId}; the next restore or boot releases it.")]
        public static partial void ReleaseFailed(ILogger logger, Exception exception, string modelId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Restore: released the stale restore record {RecordId} of {ModelId} (left by an interrupted restore).")]
        public static partial void StaleRecordReleased(ILogger logger, string recordId, string modelId);
    }
}

/// <summary>
/// Boot sweep for restore records (design §6.7): a configuration import runs in memory, so a restore record still in
/// <c>ownership.json</c> when the master starts was left by a restore that a restart interrupted, and would hold its model
/// (409 <c>model_busy</c> for moves, jobs and placement edits) forever. Releases them once at start
/// (<see cref="RestoreJobService.ReleaseStaleOwnershipAsync"/>); an unreadable ownership file is logged and left alone.
/// </summary>
public sealed partial class RestoreOwnershipRecovery(RestoreJobService restores, ILogger<RestoreOwnershipRecovery> logger) : Microsoft.Extensions.Hosting.IHostedService
{
    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await restores.ReleaseStaleOwnershipAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            LogSweepFailed(logger, ex);
        }
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restore: the boot sweep of stale restore records failed; the next restore retries it.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
