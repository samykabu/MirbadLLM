using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Nodes;

namespace MirbadLLM.Orchestration.Multinode;

/// <summary>Tunables of <see cref="MoveService"/>, bound from <c>MirbadLLM:Moves</c>.</summary>
public sealed class MoveServiceOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "MirbadLLM:Moves";

    /// <summary>How often a running download, image pull or stop is re-read.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a move waits before trying again while a node is not serving (a paused move retries this often).</summary>
    public TimeSpan RetryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How often the stale-instance reconciliation looks at the serving nodes.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long an unplace waits for the stop to finish.</summary>
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a committed move keeps trying to start the model on its target while the target is not serving; after
    /// that the move ends (the placement stays committed) and the operator starts the model.
    /// </summary>
    public TimeSpan StartTargetTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long an unplace found after a master restart waits for its node to be serving before it is given up.</summary>
    public TimeSpan ResumeNodeWait { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// A move or unplace request the master refuses (design §5.6, §6.7): ProblemDetails with <see cref="Code"/> in <c>code</c>
/// and the status <see cref="ErrorCodes.ApiStatus"/> gives it (or the one passed).
/// </summary>
public sealed class MoveRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public MoveRefusedException()
        : this(ErrorCodes.InvalidState, "The move was refused.")
    {
    }

    /// <summary>Creates the exception with a message (code <c>invalid_state</c>).</summary>
    public MoveRefusedException(string message)
        : this(ErrorCodes.InvalidState, message)
    {
    }

    /// <summary>Creates the exception with a message and inner exception (code <c>invalid_state</c>).</summary>
    public MoveRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = ErrorCodes.InvalidState;
        StatusCode = ErrorCodes.ApiStatus(Code);
    }

    /// <summary>Creates the refusal.</summary>
    public MoveRefusedException(string code, string message, int? statusCode = null)
        : base(message)
    {
        Code = code;
        StatusCode = statusCode ?? ErrorCodes.ApiStatus(code);
    }

    /// <summary>The <see cref="ErrorCodes"/> value.</summary>
    public string Code { get; }

    /// <summary>HTTP status.</summary>
    public int StatusCode { get; }
}

/// <summary>Who asked for a move or unplace: the portal session and audit identity.</summary>
/// <param name="Actor">An <see cref="AuditActors"/> value (<c>AuditActorResolver</c>).</param>
/// <param name="ClientKey">The caller's session hash or IP, used for audit context.</param>
public sealed record MoveCaller(string Actor, string ClientKey)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public System.Security.Claims.ClaimsPrincipal? User { get; init; }
}

/// <summary>One checkpoint download of a move as the target node reports it.</summary>
/// <param name="RepoId">Hugging Face repo.</param>
/// <param name="Folder">Folder on the target node.</param>
/// <param name="State">The download's state, null when the node has no download for the folder (not started, or the node did not answer).</param>
/// <param name="BytesDownloaded">Bytes present.</param>
/// <param name="TotalBytes">Expected total; 0 when unknown.</param>
/// <param name="Error">Error of a failed download.</param>
public sealed record MoveDownloadView(string RepoId, string Folder, DownloadState? State, long BytesDownloaded, long TotalBytes, string? Error);

/// <summary>
/// <c>GET /api/models/{id}/move</c>: the move record (live, or the last finished one since the master started) and what the
/// move dialog needs to show its progress and offer the force-commit (design §6.7, §12.2).
/// </summary>
public sealed record MoveStatusView
{
    /// <summary>The record (stage, nodes, error). For a finished move its stage is <c>done</c> or <c>failed</c>.</summary>
    public required OwnershipRecord Record { get; init; }

    /// <summary>True while the record holds the model.</summary>
    public bool Active { get; init; }

    /// <summary>True when the source node is Online or Degraded (false for an unplaced source).</summary>
    public bool SourceServing { get; init; }

    /// <summary>True when the target node is Online or Degraded.</summary>
    public bool TargetServing { get; init; }

    /// <summary>True when the move is paused at <c>stopping-source</c> and the source is not serving.</summary>
    public bool CanForceCommit { get; init; }

    /// <summary>The consequence the force-commit confirmation states (design §6.7); null when it cannot be forced.</summary>
    public string? ForceCommitConsequence { get; init; }

    /// <summary>The checkpoint downloads on the target (while downloading).</summary>
    public IReadOnlyList<MoveDownloadView> Downloads { get; init; } = [];
}

/// <summary>
/// Moves and unplaces models (design §6.4, §6.7; MN-109) as persisted ownership records in <c>ownership.json</c>, so each
/// survives a master restart and resumes from its stage.
/// <para><b>Move</b> (<c>POST /api/models/{id}/move</c>), stages in order (<see cref="MoveStages"/>):</para>
/// <list type="number">
/// <item><c>downloading</c>: every source of the model is downloaded on the target (agent download, existing folders are
/// completed and verified: <see cref="DownloadExistingMode.Complete"/>) and the image the target will run is pulled when
/// it is missing. The source keeps serving meanwhile. A failed download or a refused image fails the move here, before
/// anything stopped.</item>
/// <item><c>stopping-source</c>: the instance on the source node is stopped. While the source is not serving the move
/// <b>pauses</b> here (the target stays stopped, the placement is not committed) and retries by itself; the operator may
/// force-commit (<see cref="ForceCommitAsync"/>) after confirming the consequence.</item>
/// <item><c>source-stopped</c>: confirmed by status (no instance on the source).</item>
/// <item><c>placement-committed</c>: the registry write under the record (<see cref="ModelWriteScope.AsOwner"/>; the
/// validator increments the placement generation once).</item>
/// <item><c>starting-target</c>: the model is started on the target when it was running before the stop.</item>
/// <item><c>done</c>: the record is released.</item>
/// </list>
/// <para>The placement is never committed before the source stop is confirmed, unless force-committed. Every agent
/// operation of a move carries <c>owner = move:{record id}</c> (audit actor <c>system:move</c>) and its id is written
/// into the record before it is sent, so a restarted master asks the agent about it (design §4.4) instead of sending
/// another one. A move that fails or is cancelled before the commit leaves the registry unchanged and starts the model
/// again on the source when the move had stopped it there.</para>
/// <para><b>Stale instances</b> (design §6.7 "reconciliation stops that model there"): every
/// <see cref="MoveServiceOptions.SweepInterval"/> each serving node's instances are compared with the registry; an
/// instance of an existing model that is not placed on that node (moved away while the node was offline, force-committed,
/// unplaced while offline) is stopped there. Tuning candidates, models held by a job or restore, a move's instance on its
/// own target, and instances of models the registry does not know are left alone; a master with fewer than two
/// non-revoked nodes skips the pass entirely, so a single-DGX master never stops anything here (W8 review MN-128).</para>
/// <para><b>Unplace</b> (<c>POST /api/models/{id}/unplace</c>): an <see cref="OwnershipKind.Edit"/> record
/// (<c>unplace-…</c>), the model stopped on its node and confirmed gone, then <c>placement: null</c> written under the
/// record. The node must be serving (the stop must be confirmed).</para>
/// </summary>
public sealed partial class MoveService : IHostedService, IDisposable
{
    /// <summary>The checkpoint name after the registry write of the commit and before the stage write (crash tests).</summary>
    internal const string RegistryCommittedCheckpoint = "registry-committed";

    /// <summary>Stage of an unplace record while it stops the model.</summary>
    public const string UnplaceStopping = "stopping";

    /// <summary>Stage of an unplace record while it writes the registry.</summary>
    public const string UnplaceWriting = "unplacing";

    private const string MoveIdPrefix = "move-";
    private const string StartRetryKey = "/start";
    private const string UnplaceIdPrefix = "unplace-";
    private const int FinishedKept = 64;

    private readonly IModelRegistry _models;
    private readonly StartupTimeTracker? _startupTimes;
    private readonly INodeRegistry _nodes;
    private readonly INodeStatusCache _cache;
    private readonly IAgentClientFactory _clients;
    private readonly AgentOperationRunner _runner;
    private readonly IOwnershipStore _ownership;
    private readonly IModelRuntime _runtime;
    private readonly MirbadLLMOptions _options;
    private readonly MoveServiceOptions _moveOptions;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IAuditLog _audit;
    private readonly IAdminReauthenticator? _reauth;
    private readonly NodeStatusCache? _cacheIndex;
    private readonly PlacementValidator _dryRun;

    private readonly ConcurrentDictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OwnershipRecord> _finished = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _sweeping = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _waitingSince = new(StringComparer.Ordinal);

    // Hugging Face tokens of running moves, by record id: memory only (never in ownership.json, logs or audit lines),
    // dropped when the record leaves downloading; a move resumed after a master restart has none (W8 review MN-128).
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _resume;
    private Task? _sweep;
    private int _disposed;

    /// <summary>Creates the service.</summary>
    public MoveService(
        IModelRegistry models,
        INodeRegistry nodes,
        INodeStatusCache cache,
        IAgentClientFactory clients,
        AgentOperationRunner runner,
        IOwnershipStore ownership,
        IModelRuntime runtime,
        IOptions<MirbadLLMOptions> options,
        MoveServiceOptions? moveOptions = null,
        TimeProvider? timeProvider = null,
        ILogger<MoveService>? logger = null,
        IAuditLog? audit = null,
        IAdminReauthenticator? reauth = null,
        IOptions<RegistryMigrationOptions>? migration = null,
        StartupTimeTracker? startupTimes = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(ownership);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        _models = models;
        _startupTimes = startupTimes;
        _nodes = nodes;
        _cache = cache;
        _clients = clients;
        _runner = runner;
        _ownership = ownership;
        _runtime = runtime;
        _options = options.Value;
        _moveOptions = moveOptions ?? new MoveServiceOptions();
        _time = timeProvider ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _audit = audit ?? NullAuditLog.Instance;
        _reauth = reauth;
        _cacheIndex = cache as NodeStatusCache;
        // Target checks before anything runs: the registry's own validator over the move's placement, as a create
        // (no stored definition), so it never writes, never sees an ownership record and stamps no generation.
        _dryRun = new PlacementValidator(nodes, ownership: null, migration);
    }

    /// <summary>
    /// Test seam (crash simulation): awaited after each stage is persisted (with the stage name) and after the commit's
    /// registry write (<see cref="RegistryCommittedCheckpoint"/>). A hook that never returns until the token is cancelled
    /// is a master stopped right there.
    /// </summary>
    internal Func<string, OwnershipRecord, CancellationToken, Task>? CheckpointForTests { get; set; }

    /// <summary>The consequence a force-commit confirms (design §6.7).</summary>
    public static string ForceCommitConsequenceFor(string sourceNodeId) =>
        $"If {sourceNodeId} comes back, the old instance may still be running there until it is stopped; no traffic is sent to it.";

    // ================================================================ hosting

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _resume = Task.Run(() => ResumeAllAsync(_lifetime.Token), CancellationToken.None);
        _sweep = Task.Run(() => SweepLoopAsync(_lifetime.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // A host torn down out of order can stop the service after the container disposed it (W9 review MN-132: an
        // ObjectDisposedException from WebApplicationFactory's dispose). Dispose already cancelled every worker.
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return; // disposed between the check and the cancel
        }

        var tasks = _workers.Values.Select(w => w.Task).Append(_resume ?? Task.CompletedTask).Append(_sweep ?? Task.CompletedTask);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            // Shutdown bound reached: the records stay at their stage and resume at the next boot.
        }
    }

    /// <inheritdoc />
    /// <remarks>Idempotent: the container disposes the instance once per registration (singleton and hosted service).</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task ResumeAllAsync(CancellationToken ct)
    {
        try
        {
            foreach (var record in await _ownership.ListAsync(ct).ConfigureAwait(false))
            {
                if (record.Kind == OwnershipKind.Move && record.Move is not null)
                {
                    MoveServiceLog.Resuming(_logger, record.ModelId, record.Id, record.Stage);
                    StartWorker(record.Id);
                }
                else if (record.Kind == OwnershipKind.Edit && record.Id.StartsWith(UnplaceIdPrefix, StringComparison.Ordinal))
                {
                    MoveServiceLog.Resuming(_logger, record.ModelId, record.Id, record.Stage);
                    _ = TrackWorker(record.Id, token => ResumeUnplaceAsync(record, token));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            MoveServiceLog.ResumeFailed(_logger, ex);
        }
    }

    // ================================================================ move: API

    /// <summary>
    /// Starts a move (design §5.6, §6.7) and returns its record at <c>downloading</c>; the stages run in the background.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such model.</exception>
    /// <exception cref="MoveRefusedException"><c>model_not_movable</c>, <c>node_revoked</c>, <c>node_draining</c>, <c>node_offline</c> (target), <c>invalid_request</c>, <c>reauthentication_failed</c>, <c>rate_limited</c>.</exception>
    /// <exception cref="PlacementValidationException">The target placement is invalid (400 <c>placement_invalid</c>).</exception>
    /// <exception cref="ModelBusyException">Another record holds the model.</exception>
    public async Task<OwnershipRecord> StartMoveAsync(string modelId, ModelMoveRequest request, MoveCaller caller, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        var model = await _models.GetAsync(modelId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown model '{modelId}'.");
        string targetId = request.TargetNodeId ?? "";
        try
        {
            var record = await PrepareMoveAsync(model, request, caller, ct).ConfigureAwait(false);
            var acquired = await _ownership.TryAcquireAsync(record, ct).ConfigureAwait(false);
            if (!acquired.Acquired)
            {
                throw new ModelBusyException(modelId, acquired.Holder!);
            }

            _finished.TryRemove(modelId, out _);
            if (!string.IsNullOrWhiteSpace(request.HfToken))
            {
                _tokens[record.Id] = request.HfToken.Trim();
            }

            await AuditAsync(caller.Actor, AuditActions.ModelMove, targetId, modelId, AuditResults.Accepted, null,
                $"move from {Describe(record.Move!.SourceNodeId)} to {targetId}").ConfigureAwait(false);
            MoveServiceLog.Started(_logger, modelId, record.Id, record.Move.SourceNodeId, targetId);
            StartWorker(record.Id);
            return record;
        }
        catch (Exception ex) when (RefusalCode(ex) is { } code)
        {
            await AuditAsync(caller.Actor, AuditActions.ModelMove, Protocol.IsValidNodeId(targetId) ? targetId : null, modelId,
                AuditResults.Refused, code, null).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The live move record of <paramref name="modelId"/> (else the last finished one) with its progress; null when none.</summary>
    public async Task<MoveStatusView?> GetAsync(string modelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        var live = await _ownership.GetForModelAsync(modelId, ct).ConfigureAwait(false);
        bool active = live is { Kind: OwnershipKind.Move, Move: not null };
        var record = active ? live! : _finished.TryGetValue(modelId, out var finished) ? finished : null;
        if (record?.Move is not { } move)
        {
            return null;
        }

        bool sourceServing = move.SourceNodeId.Length > 0 && Serving(move.SourceNodeId);
        bool canForce = active && record.Stage == MoveStages.StoppingSource && move.SourceNodeId.Length > 0 && !sourceServing;
        IReadOnlyList<MoveDownloadView> downloads = [];
        if (active && record.Stage == MoveStages.Downloading &&
            await _models.GetAsync(modelId, ct).ConfigureAwait(false) is { } model)
        {
            downloads = await ReadDownloadsAsync(move.TargetNodeId, model, ct).ConfigureAwait(false);
        }

        return new MoveStatusView
        {
            Record = record,
            Active = active,
            SourceServing = sourceServing,
            TargetServing = Serving(move.TargetNodeId),
            CanForceCommit = canForce,
            ForceCommitConsequence = canForce ? ForceCommitConsequenceFor(move.SourceNodeId) : null,
            Downloads = downloads,
        };
    }

    /// <summary>
    /// Commits a move paused at <c>stopping-source</c> because its source is not serving (design §6.7): the operator typed
    /// the source node id using the signed-in session after reading <see cref="ForceCommitConsequenceFor"/>. The record goes to
    /// <c>source-stopped</c> with <see cref="MoveDetails.ForceCommittedAt"/> and <see cref="MoveDetails.SourceCleanupPending"/>
    /// set; the stale source instance is stopped when the source is back (the stale-instance reconciliation).
    /// </summary>
    public async Task<OwnershipRecord> ForceCommitAsync(string modelId, ModelMoveForceCommitRequest request, MoveCaller caller, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(caller);
        if (await _models.GetAsync(modelId, ct).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Unknown model '{modelId}'.");
        }

        string? sourceId = null;
        try
        {
            var record = await _ownership.GetForModelAsync(modelId, ct).ConfigureAwait(false);
            if (record is not { Kind: OwnershipKind.Move, Move: { } move })
            {
                throw new MoveRefusedException(ErrorCodes.InvalidState, $"Model '{modelId}' has no move in progress.");
            }

            sourceId = move.SourceNodeId;
            if (record.Stage != MoveStages.StoppingSource || move.SourceNodeId.Length == 0)
            {
                throw new MoveRefusedException(ErrorCodes.InvalidState,
                    $"The move of model '{modelId}' is at stage {record.Stage}; only a move paused at {MoveStages.StoppingSource} can be force-committed.");
            }

            if (Serving(move.SourceNodeId))
            {
                throw new MoveRefusedException(ErrorCodes.InvalidState,
                    $"Node '{move.SourceNodeId}' is serving: the move stops the model there and continues by itself; force-commit is only for a source that is gone.");
            }

            // The typed confirmation first: validate the operator's intent before authorizing the session.
            if (!string.Equals(request.Confirm, move.SourceNodeId, StringComparison.Ordinal))
            {
                throw new MoveRefusedException(ErrorCodes.ConfirmationRequired,
                    $"Type the source node id '{move.SourceNodeId}' to confirm. {ForceCommitConsequenceFor(move.SourceNodeId)}");
            }

            Reauthenticate(caller);

            DateTimeOffset now = _time.GetUtcNow();
            var (updated, applied) = await TransitionAsync(record, MoveStages.SourceStopped, r => r with
            {
                OutstandingOperationIds = [],
                Error = null,
                Move = r.Move! with { ForceCommittedAt = now, SourceCleanupPending = true },
            }, ct).ConfigureAwait(false);
            if (!applied || updated is null)
            {
                throw new MoveRefusedException(ErrorCodes.InvalidState,
                    $"The move of model '{modelId}' moved on meanwhile (stage {updated?.Stage ?? "released"}); reload it.");
            }

            await AuditAsync(caller.Actor, AuditActions.ModelMoveForceCommit, move.SourceNodeId, modelId, AuditResults.Succeeded, null,
                $"to {move.TargetNodeId}; source not stopped: {ForceCommitConsequenceFor(move.SourceNodeId)}").ConfigureAwait(false);
            MoveServiceLog.ForceCommitted(_logger, modelId, record.Id, move.SourceNodeId);
            Wake(record.Id);
            return updated;
        }
        catch (Exception ex) when (RefusalCode(ex) is { } code)
        {
            await AuditAsync(caller.Actor, AuditActions.ModelMoveForceCommit, sourceId is { Length: > 0 } ? sourceId : null, modelId,
                AuditResults.Refused, code, null).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Cancels a move before its source stop is confirmed (<c>downloading</c> or <c>stopping-source</c>): its downloads are
    /// cancelled, the registry stays unchanged and a source instance the move stopped is started again. Returns the record
    /// at <c>failed</c>.
    /// </summary>
    public async Task<OwnershipRecord> CancelAsync(string modelId, MoveCaller caller, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(caller);
        if (await _models.GetAsync(modelId, ct).ConfigureAwait(false) is null)
        {
            throw new KeyNotFoundException($"Unknown model '{modelId}'.");
        }

        try
        {
            var record = await _ownership.GetForModelAsync(modelId, ct).ConfigureAwait(false);
            if (record is not { Kind: OwnershipKind.Move, Move: not null })
            {
                throw new MoveRefusedException(ErrorCodes.InvalidState, $"Model '{modelId}' has no move in progress.");
            }

            if (record.Stage is not (MoveStages.Downloading or MoveStages.StoppingSource))
            {
                throw new MoveRefusedException(ErrorCodes.InvalidState,
                    $"The move of model '{modelId}' is at stage {record.Stage}: the source stop is confirmed or the placement committed, so it can no longer be cancelled.");
            }

            var (updated, applied) = await TransitionAsync(record, MoveStages.Failed, r => r with { Error = "Cancelled by the operator." }, ct)
                .ConfigureAwait(false);
            if (!applied || updated is null)
            {
                throw new MoveRefusedException(ErrorCodes.InvalidState,
                    $"The move of model '{modelId}' moved on meanwhile (stage {updated?.Stage ?? "released"}); it can no longer be cancelled.");
            }

            await AuditAsync(caller.Actor, AuditActions.ModelMove, record.Move!.TargetNodeId, modelId, AuditResults.Succeeded, null,
                "cancel at " + record.Stage).ConfigureAwait(false);
            if (!_workers.ContainsKey(record.Id))
            {
                StartWorker(record.Id);
            }

            Wake(record.Id);
            return updated;
        }
        catch (Exception ex) when (RefusalCode(ex) is { } code)
        {
            await AuditAsync(caller.Actor, AuditActions.ModelMove, null, modelId, AuditResults.Refused, code, "cancel").ConfigureAwait(false);
            throw;
        }
    }

    private async Task<OwnershipRecord> PrepareMoveAsync(ModelDefinition model, ModelMoveRequest request, MoveCaller caller, CancellationToken ct)
    {
        string modelId = model.Id;
        string targetId = request.TargetNodeId ?? "";
        if (await _ownership.GetForModelAsync(modelId, ct).ConfigureAwait(false) is { } holder)
        {
            throw new ModelBusyException(modelId, holder);
        }

        if (!Protocol.IsValidNodeId(targetId))
        {
            throw new MoveRefusedException(ErrorCodes.InvalidRequest, $"'{targetId}' is not a node id.");
        }

        string? sourceId = model.Placement?.NodeId;
        if (string.Equals(sourceId, targetId, StringComparison.Ordinal))
        {
            throw new MoveRefusedException(ErrorCodes.InvalidRequest,
                $"Model '{modelId}' is already on node '{targetId}'; changing its GPUs there is an edit, not a move.");
        }

        CheckMovable(model);

        var target = await _nodes.GetAsync(targetId, ct).ConfigureAwait(false);
        if (target is { Revoked: true })
        {
            throw new MoveRefusedException(ErrorCodes.NodeRevoked, $"Node '{targetId}' is revoked; models cannot be moved there.");
        }

        if (target is { Draining: true })
        {
            throw new MoveRefusedException(ErrorCodes.NodeDraining, $"Node '{targetId}' is draining; it takes no new placements until it is undrained.");
        }

        // The registry's placement checks (node exists and is approved, GPUs, runtime), and the GPU indices mapped to UUIDs.
        var candidate = new ModelPlacement(
            targetId,
            request.GpuUuids is { Count: > 0 } uuids ? [.. uuids] : null,
            request.GpuUuids is { Count: > 0 } ? null : request.GpuIndices is { Count: > 0 } indices ? [.. indices] : null);
        var validated = await _dryRun.ValidateAsync(new ModelDefinitionChange(null, model with { Placement = candidate }), ct).ConfigureAwait(false);
        var placement = validated?.Placement ?? candidate;

        if (!Serving(targetId))
        {
            throw new MoveRefusedException(ErrorCodes.NodeOffline,
                $"Node '{targetId}' is {StateOf(targetId)}: the checkpoint is downloaded there first, so it must be serving to start a move.");
        }

        NodeRecord? source = sourceId is null ? null : await _nodes.GetAsync(sourceId, ct).ConfigureAwait(false);
        bool running = sourceId is not null && await IsRunningAsync(modelId, ct).ConfigureAwait(false);
        if (running)
        {
            Reauthenticate(caller);
        }

        DateTimeOffset now = _time.GetUtcNow();
        return new OwnershipRecord
        {
            Id = MoveIdPrefix + Guid.NewGuid().ToString("N")[..16],
            Kind = OwnershipKind.Move,
            ModelId = modelId,
            NodeUids = source is null ? [target!.Uid] : [source.Uid, target!.Uid],
            PlacementGeneration = model.Placement?.Generation ?? 0,
            Stage = MoveStages.Downloading,
            StartedAt = now,
            UpdatedAt = now,
            Actor = caller.Actor,
            Description = $"a move to node {targetId}",
            Move = new MoveDetails
            {
                SourceNodeId = sourceId ?? "",
                SourceNodeUid = source?.Uid ?? "",
                TargetNodeId = targetId,
                TargetNodeUid = target.Uid,
                TargetGpuUuids = placement.GpuUuids is { Count: > 0 } selected ? [.. selected] : [],
                WasRunning = running,
            },
        };
    }

    /// <summary>A move re-downloads from the model's sources (design §6.4): the checkpoint folder needs a Hugging Face source.</summary>
    private static void CheckMovable(ModelDefinition model)
    {
        if (model.Sources.Count == 0)
        {
            throw new MoveRefusedException(ErrorCodes.ModelNotMovable,
                $"Model '{model.Id}' has no Hugging Face sources (it was added from a folder), so it cannot be moved by re-downloading it on the other node. " +
                "Add it there from Hugging Face, or record its sources first.");
        }

        if (!model.Sources.Any(s => string.Equals(s.Folder, model.ModelPath, StringComparison.Ordinal)))
        {
            throw new MoveRefusedException(ErrorCodes.ModelNotMovable,
                $"None of the sources of model '{model.Id}' downloads its checkpoint folder '{model.ModelPath}', so it cannot be moved by re-downloading it.");
        }

        foreach (var source in model.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.RepoId) ||
                !string.Equals(RemoteHuggingFaceDownloader.FolderFor(new DownloadRequest(source.RepoId, Folder: source.Folder)), source.Folder, StringComparison.Ordinal))
            {
                throw new MoveRefusedException(ErrorCodes.ModelNotMovable,
                    $"Source '{source.RepoId}' of model '{model.Id}' cannot be re-downloaded into '{source.Folder}' (a folder name under the models root is needed).");
            }
        }
    }

    // ================================================================ move: worker

    private void StartWorker(string recordId)
    {
        _ = TrackWorker(recordId, ct => RunMoveAsync(recordId, ct));
    }

    private Task TrackWorker(string recordId, Func<CancellationToken, Task> run)
    {
        if (_lifetime.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        var worker = new Worker();
        if (!_workers.TryAdd(recordId, worker))
        {
            Wake(recordId);
            return Task.CompletedTask;
        }

        worker.Task = Task.Run(async () =>
        {
            try
            {
                await run(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Master stopping: the record keeps its stage and resumes at the next boot.
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            {
                MoveServiceLog.WorkerFailed(_logger, ex, recordId);
            }
            finally
            {
                _workers.TryRemove(new KeyValuePair<string, Worker>(recordId, worker));
            }
        }, CancellationToken.None);
        return worker.Task;
    }

    private void Wake(string recordId)
    {
        if (_workers.TryGetValue(recordId, out var worker))
        {
            worker.Wake();
        }
    }

    private async Task RunMoveAsync(string recordId, CancellationToken ct)
    {
        using var scope = AuditScope.Begin(AuditActors.SystemMove);
        string? lastStage = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var record = await ReadAsync(recordId, ct).ConfigureAwait(false);
            if (record?.Move is null)
            {
                return; // released (finished by another worker)
            }

            if (!string.Equals(record.Stage, lastStage, StringComparison.Ordinal))
            {
                lastStage = record.Stage;
                await CheckpointAsync(record.Stage, record, ct).ConfigureAwait(false);
            }

            try
            {
                switch (record.Stage)
                {
                    case MoveStages.Downloading:
                        await DownloadStageAsync(record, ct).ConfigureAwait(false);
                        break;
                    case MoveStages.StoppingSource:
                        await StopSourceStageAsync(record, ct).ConfigureAwait(false);
                        break;
                    case MoveStages.SourceStopped:
                        await CommitStageAsync(record, ct).ConfigureAwait(false);
                        break;
                    case MoveStages.PlacementCommitted:
                    case MoveStages.StartingTarget:
                        await StartTargetStageAsync(record, ct).ConfigureAwait(false);
                        break;
                    case MoveStages.Done:
                    case MoveStages.Failed:
                        await FinishAsync(record, ct).ConfigureAwait(false);
                        _waitingSince.TryRemove(recordId, out _);
                        _waitingSince.TryRemove(recordId + StartRetryKey, out _);
                        return;
                    default:
                        await FailAsync(record, $"Unknown move stage '{record.Stage}'.", ct).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not InvalidDataException)
            {
                // Anything unexpected (an agent answer the stage did not foresee, a store hiccup): note it, wait, try the
                // stage again. The record keeps its stage, so nothing is skipped.
                MoveServiceLog.StageRetry(_logger, record.ModelId, record.Id, record.Stage, ex.Message);
                await NoteAsync(record, $"{record.Stage}: {ex.Message} (trying again)", ct).ConfigureAwait(false);
                await DelayAsync(record.Id, _moveOptions.RetryInterval, ct).ConfigureAwait(false);
            }
        }
    }

    // ---- downloading -------------------------------------------------------------------------------------------------

    private async Task DownloadStageAsync(OwnershipRecord record, CancellationToken ct)
    {
        var move = record.Move!;
        string targetId = move.TargetNodeId;
        var model = await _models.GetAsync(record.ModelId, ct).ConfigureAwait(false);
        if (model is null)
        {
            await FailAsync(record, $"Model '{record.ModelId}' no longer exists.", ct).ConfigureAwait(false);
            return;
        }

        if (!await WaitForNodeAsync(record, targetId, "target", ct).ConfigureAwait(false))
        {
            return; // the record changed while waiting
        }

        try
        {
            foreach (var source in model.Sources.DistinctBy(s => s.Folder, StringComparer.Ordinal))
            {
                if (!await EnsureDownloadedAsync(record, targetId, source, ct).ConfigureAwait(false))
                {
                    return;
                }
            }

            if (!await EnsureImageAsync(record, targetId, model, ct).ConfigureAwait(false))
            {
                return;
            }

            var folders = await _clients.GetClient(targetId).ListModelFoldersAsync(ct).ConfigureAwait(false);
            if (!folders.Any(f => string.Equals(f.Folder, model.ModelPath, StringComparison.Ordinal) && !f.Partial && f.SizeBytes > 0))
            {
                await FailAsync(record, $"The checkpoint folder '{model.ModelPath}' is not complete on node '{targetId}' after its download.", ct).ConfigureAwait(false);
                return;
            }
        }
        catch (MoveFailedException ex)
        {
            await FailAsync(record, ex.Message, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (IsTransient(ex))
        {
            await NoteAsync(record, $"Node '{targetId}' did not answer ({ex.Message}); the move tries again.", ct).ConfigureAwait(false);
            await DelayAsync(record.Id, _moveOptions.RetryInterval, ct).ConfigureAwait(false);
            return;
        }

        // Whether the model runs right before its stop decides whether it is started on the target (an operator may have
        // started or stopped it while the checkpoint downloaded).
        bool wasRunning = move.SourceNodeId.Length > 0 && Serving(move.SourceNodeId)
            ? await IsRunningOnAsync(move.SourceNodeId, record.ModelId, ct).ConfigureAwait(false) ?? move.WasRunning
            : move.WasRunning;
        await TransitionAsync(record, MoveStages.StoppingSource, r => r with
        {
            OutstandingOperationIds = [],
            Error = null,
            Move = r.Move! with { WasRunning = wasRunning },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads one source on the target (or waits for the one running into that folder); true once it completed, false
    /// when the record left <c>downloading</c> meanwhile.
    /// </summary>
    private async Task<bool> EnsureDownloadedAsync(OwnershipRecord record, string nodeId, ModelSource source, CancellationToken ct)
    {
        string folder = source.Folder;
        var client = _clients.GetClient(nodeId);
        while (true)
        {
            var current = await ReadAsync(record.Id, ct).ConfigureAwait(false);
            if (current?.Stage != MoveStages.Downloading)
            {
                return false;
            }

            var status = await client.GetDownloadAsync(folder, ct).ConfigureAwait(false);
            bool ours = status?.OperationId is { } op && current.OutstandingOperationIds.Contains(op, StringComparer.Ordinal);
            switch (status?.State)
            {
                case DownloadState.Queued or DownloadState.Downloading:
                    await DelayAsync(record.Id, _moveOptions.PollInterval, ct).ConfigureAwait(false);
                    continue;
                case DownloadState.Completed when ours:
                    return true;
                case DownloadState.Failed or DownloadState.Cancelled when ours:
                    throw new MoveFailedException(
                        $"The download of {source.RepoId} into '{folder}' on node '{nodeId}' ended {status!.State.ToString().ToLowerInvariant()}" +
                        (string.IsNullOrWhiteSpace(status.Error) ? "." : ": " + status.Error.Trim()) +
                        (status.State == DownloadState.Failed && !_tokens.ContainsKey(record.Id)
                            ? " If the repository is gated or private, start the move again with a Hugging Face token (it is kept in memory only, so a move resumed after a master restart has none)."
                            : ""));
            }

            // Nothing of ours: download (an existing folder is verified and completed, design §6.4 "complete").
            var envelope = _runner.CreateEnvelope();
            await RememberOperationAsync(record.Id, envelope.OperationId, ct).ConfigureAwait(false);
            var body = new DownloadStartRequest
            {
                OperationId = envelope.OperationId,
                IssuedAt = envelope.IssuedAt,
                ExpiresAt = envelope.ExpiresAt,
                RepoId = source.RepoId,
                Revision = source.Revision,
                Folder = folder,
                AllowPatterns = source.AllowPatterns is { Count: > 0 } patterns ? patterns : null,
                Token = _tokens.TryGetValue(record.Id, out string? token) ? token : null,
                Existing = DownloadExistingMode.Complete,
            };
            try
            {
                await _runner.SubmitAsync(nodeId, AgentRoutes.DownloadStart, folder, body,
                    (c, r, t) => c.StartDownloadAsync(r, t), s => s.State switch
                    {
                        DownloadState.Completed => OperationState.Succeeded,
                        DownloadState.Failed => OperationState.Failed,
                        DownloadState.Cancelled => OperationState.Cancelled,
                        _ => null,
                    }, errorOf: null, owner: AuditOwners.Move(record.Id), ct).ConfigureAwait(false);
                MoveServiceLog.DownloadStarted(_logger, record.ModelId, record.Id, source.RepoId, folder, nodeId);
            }
            catch (AgentException ex) when (ex.Code == ErrorCodes.AlreadyRunning)
            {
                // Someone else's download into the folder: wait for it, then verify with one of ours.
                await DelayAsync(record.Id, _moveOptions.PollInterval, ct).ConfigureAwait(false);
            }
            catch (AgentException ex) when (ex.StatusCode is >= 400 and < 500)
            {
                throw new MoveFailedException($"Node '{nodeId}' refused the download of {source.RepoId} into '{folder}': {ex.Message}");
            }
        }
    }

    /// <summary>Pulls the image the model will run on the target when the node does not have it; false when the record left <c>downloading</c>.</summary>
    private async Task<bool> EnsureImageAsync(OwnershipRecord record, string nodeId, ModelDefinition model, CancellationToken ct)
    {
        var node = await _nodes.GetAsync(nodeId, ct).ConfigureAwait(false)
            ?? throw new MoveFailedException($"Node '{nodeId}' is no longer registered.");
        if (LaunchSpecBuilder.ImageToPull(model, node, _options) is not (var image, var digest))
        {
            return true; // a native process, or nothing to pull: a node without Docker would never answer an image request
        }

        var client = _clients.GetClient(nodeId);
        while (true)
        {
            var current = await ReadAsync(record.Id, ct).ConfigureAwait(false);
            if (current?.Stage != MoveStages.Downloading)
            {
                return false;
            }

            var images = await client.ListImagesAsync(ct).ConfigureAwait(false);
            if (images.Any(i => string.Equals(i.Ref, image, StringComparison.Ordinal) && (digest is null || string.Equals(i.Digest, digest, StringComparison.Ordinal))))
            {
                return true;
            }

            // An earlier pull of ours that is still running (after a master restart) is waited for, not sent again.
            OperationStatus? running = null;
            foreach (string op in current.OutstandingOperationIds)
            {
                if (await client.GetOperationAsync(op, ct).ConfigureAwait(false) is { Kind: OperationKinds.ImagePull } status)
                {
                    if (!status.State.IsTerminal())
                    {
                        running = status;
                    }
                    else if (status.State != OperationState.Succeeded)
                    {
                        throw new MoveFailedException($"The image {image} could not be pulled on node '{nodeId}': {status.Error?.Message ?? status.State.ToString()}");
                    }
                }
            }

            if (running is not null)
            {
                await DelayAsync(record.Id, _moveOptions.PollInterval, ct).ConfigureAwait(false);
                continue;
            }

            if (current.OutstandingOperationIds.Count > 0 && await PulledByUsAsync(client, current, ct).ConfigureAwait(false))
            {
                return true; // our pull succeeded (the image list names it by another reference)
            }

            var envelope = _runner.CreateEnvelope();
            await RememberOperationAsync(record.Id, envelope.OperationId, ct).ConfigureAwait(false);
            var pull = new ImagePullRequest
            {
                OperationId = envelope.OperationId,
                IssuedAt = envelope.IssuedAt,
                ExpiresAt = envelope.ExpiresAt,
                Image = image,
                Digest = digest,
            };
            try
            {
                await _runner.SubmitAsync(nodeId, AgentRoutes.ImagePull, image, pull,
                    (c, r, t) => c.PullImageAsync(r, t), owner: AuditOwners.Move(record.Id), ct).ConfigureAwait(false);
                MoveServiceLog.ImagePull(_logger, record.ModelId, record.Id, image, nodeId);
            }
            catch (AgentException ex) when (ex.StatusCode is >= 400 and < 500)
            {
                throw new MoveFailedException($"Node '{nodeId}' refused the image {image}: {ex.Message}");
            }

            await DelayAsync(record.Id, _moveOptions.PollInterval, ct).ConfigureAwait(false);
        }
    }

    private static async Task<bool> PulledByUsAsync(IAgentClient client, OwnershipRecord record, CancellationToken ct)
    {
        foreach (string op in record.OutstandingOperationIds)
        {
            if (await client.GetOperationAsync(op, ct).ConfigureAwait(false) is { Kind: OperationKinds.ImagePull, State: OperationState.Succeeded })
            {
                return true;
            }
        }

        return false;
    }

    // ---- stopping-source ---------------------------------------------------------------------------------------------

    private async Task StopSourceStageAsync(OwnershipRecord record, CancellationToken ct)
    {
        var move = record.Move!;
        string sourceId = move.SourceNodeId;
        if (sourceId.Length == 0)
        {
            await TransitionAsync(record, MoveStages.SourceStopped, r => r with { OutstandingOperationIds = [], Error = null }, ct).ConfigureAwait(false);
            return; // an unplaced model runs nowhere
        }

        if (!Serving(sourceId))
        {
            // Paused (design §6.7): the target stays stopped and the placement is not committed until the stop is confirmed.
            await NoteAsync(record,
                $"Node '{sourceId}' is {StateOf(sourceId)}: the move waits until the model is confirmed stopped there. " +
                $"If {sourceId} is gone for good, force-commit the move. " + ForceCommitConsequenceFor(sourceId), ct).ConfigureAwait(false);
            await DelayAsync(record.Id, _moveOptions.RetryInterval, ct).ConfigureAwait(false);
            return;
        }

        var client = _clients.GetClient(sourceId);
        try
        {
            // Outstanding stops of this move (also after a master restart) are waited for, never sent twice.
            foreach (string op in record.OutstandingOperationIds)
            {
                if (await client.GetOperationAsync(op, ct).ConfigureAwait(false) is { } status && !status.State.IsTerminal())
                {
                    await DelayAsync(record.Id, _moveOptions.PollInterval, ct).ConfigureAwait(false);
                    return;
                }
            }

            if (await client.GetRuntimeAsync(record.ModelId, ct).ConfigureAwait(false) is null)
            {
                // Confirmed by status: no instance on the source.
                await TransitionAsync(record, MoveStages.SourceStopped, r => r with { OutstandingOperationIds = [], Error = null }, ct).ConfigureAwait(false);
                return;
            }

            var envelope = _runner.CreateEnvelope();
            await RememberOperationAsync(record.Id, envelope.OperationId, ct).ConfigureAwait(false);
            var stop = new StopRequest { OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt };
            try
            {
                await _runner.SubmitAsync(sourceId, AgentRoutes.RuntimeStop, record.ModelId, stop,
                    (c, r, t) => c.StopAsync(record.ModelId, r, t), owner: AuditOwners.Move(record.Id), ct).ConfigureAwait(false);
                MoveServiceLog.StoppingSource(_logger, record.ModelId, record.Id, sourceId);
            }
            catch (AgentException ex) when (ex.Code == ErrorCodes.NotFound)
            {
                // Gone meanwhile: the next pass confirms it.
            }

            await DelayAsync(record.Id, _moveOptions.PollInterval, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransient(ex) || ex is AgentException)
        {
            await NoteAsync(record, $"The model could not be stopped on node '{sourceId}' yet ({ex.Message}); the move tries again.", ct).ConfigureAwait(false);
            await DelayAsync(record.Id, _moveOptions.RetryInterval, ct).ConfigureAwait(false);
        }
    }

    // ---- placement-committed -----------------------------------------------------------------------------------------

    private async Task CommitStageAsync(OwnershipRecord record, CancellationToken ct)
    {
        var move = record.Move!;
        var model = await _models.GetAsync(record.ModelId, ct).ConfigureAwait(false);
        if (model is null)
        {
            await FailAsync(record, $"Model '{record.ModelId}' no longer exists.", ct).ConfigureAwait(false);
            return;
        }

        var target = new ModelPlacement(move.TargetNodeId, move.TargetGpuUuids.Count > 0 ? [.. move.TargetGpuUuids] : null);
        if (model.Placement is not { } stored || !stored.SameLocation(target))
        {
            // Still the placement the move started from (nobody else may change it while the record holds the model).
            bool fromSource = move.SourceNodeId.Length == 0
                ? model.Placement is null
                : model.Placement is { } p && p.NodeId == move.SourceNodeId && p.Generation == record.PlacementGeneration;
            if (!fromSource)
            {
                await FailAsync(record,
                    $"The placement of model '{record.ModelId}' changed during the move (now {Describe(model.Placement)}); nothing was committed.", ct).ConfigureAwait(false);
                return;
            }

            try
            {
                using (ModelWriteScope.AsOwner(record.Id))
                {
                    // The holder's write (D87): the model as stored now, with the target placement; the validator increments
                    // the generation once (an unplaced model starts from the record's generation, MN-108).
                    await _models.UpsertAsync(model with { Placement = target with { Generation = record.PlacementGeneration } }, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is PlacementValidationException or PlacementConflictException or ModelBusyException or ArgumentException)
            {
                await FailAsync(record, $"The placement on node '{move.TargetNodeId}' was refused: {ex.Message}", ct).ConfigureAwait(false);
                return;
            }

            await CheckpointAsync(RegistryCommittedCheckpoint, record, ct).ConfigureAwait(false);
        }

        long generation = (await _models.GetAsync(record.ModelId, ct).ConfigureAwait(false))?.Placement?.Generation ?? record.PlacementGeneration;
        MoveServiceLog.Committed(_logger, record.ModelId, record.Id, move.TargetNodeId, generation);
        _startupTimes?.Forget(record.ModelId); // the next start is on the target's hardware (design §6.5 item 8, MN-124)
        await RefreshRoutingAsync(record.ModelId).ConfigureAwait(false);
        await TransitionAsync(record, MoveStages.PlacementCommitted, r => r with { PlacementGeneration = generation, Error = null }, ct)
            .ConfigureAwait(false);
    }

    // ---- starting-target ---------------------------------------------------------------------------------------------

    private async Task StartTargetStageAsync(OwnershipRecord record, CancellationToken ct)
    {
        var move = record.Move!;
        if (!move.WasRunning)
        {
            await TransitionAsync(record, MoveStages.Done, r => r with { OutstandingOperationIds = [] }, ct).ConfigureAwait(false);
            return;
        }

        if (record.Stage == MoveStages.PlacementCommitted)
        {
            await TransitionAsync(record, MoveStages.StartingTarget, null, ct).ConfigureAwait(false);
            return;
        }

        string targetId = move.TargetNodeId;
        if (!Serving(targetId))
        {
            DateTimeOffset since = _waitingSince.GetOrAdd(record.Id, _time.GetUtcNow());
            if (_time.GetUtcNow() - since > _moveOptions.StartTargetTimeout)
            {
                await TransitionAsync(record, MoveStages.Done, r => r with
                {
                    Error = string.Create(CultureInfo.InvariantCulture,
                        $"Node '{targetId}' was not serving for {_moveOptions.StartTargetTimeout.TotalMinutes:0} min; the model is placed there but was not started."),
                }, ct).ConfigureAwait(false);
                return;
            }

            await NoteAsync(record, $"Node '{targetId}' is {StateOf(targetId)}: the model is started there once it is back.", ct).ConfigureAwait(false);
            await DelayAsync(record.Id, _moveOptions.RetryInterval, ct).ConfigureAwait(false);
            return;
        }

        _waitingSince.TryRemove(record.Id, out _);

        // Reconcile first (design §4.4, also after a master restart): an instance already there is the move's own start; a
        // launch of the model still running there is waited for, never sent twice.
        var client = _clients.GetClient(targetId);
        if (await client.GetRuntimeAsync(record.ModelId, ct).ConfigureAwait(false) is { State: ModelState.Starting or ModelState.Ready })
        {
            await TransitionAsync(record, MoveStages.Done, r => r with { OutstandingOperationIds = [], Error = null }, ct).ConfigureAwait(false);
            return;
        }

        var active = await client.ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }, ct).ConfigureAwait(false);
        if (active.Any(o => o.Kind == OperationKinds.Launch && string.Equals(o.Target, record.ModelId, StringComparison.Ordinal) && !o.State.IsTerminal()))
        {
            await NoteAsync(record, $"A start of the model on node '{targetId}' is still running; the move waits for it.", ct).ConfigureAwait(false);
            await DelayAsync(record.Id, _moveOptions.PollInterval, ct).ConfigureAwait(false);
            return;
        }

        string? error = null;
        try
        {
            using (ModelWriteScope.AsOwner(record.Id))
            {
                await _runtime.StartAsync(record.ModelId, force: false, ct).ConfigureAwait(false);
            }

            MoveServiceLog.StartedOnTarget(_logger, record.ModelId, record.Id, targetId);
        }
        catch (Exception ex) when (IsTransient(ex) || ex is NodeUnavailableException { IsTransient: true } || ex is ModelStartConflictException)
        {
            // Not through, or another start holds the node's (or GPU's) start slot right now: try again, for at most
            // StartTargetTimeout (W8 review MN-128: a start conflict is momentary, it must not end the move).
            DateTimeOffset since = _waitingSince.GetOrAdd(record.Id + StartRetryKey, _time.GetUtcNow());
            if (_time.GetUtcNow() - since > _moveOptions.StartTargetTimeout)
            {
                error = string.Create(CultureInfo.InvariantCulture,
                    $"The model is placed on node '{targetId}' but could not be started there within {_moveOptions.StartTargetTimeout.TotalMinutes:0} min: {ex.Message}");
            }
            else
            {
                await NoteAsync(record, $"The start on node '{targetId}' did not get through ({ex.Message}); the move tries again.", ct).ConfigureAwait(false);
                await DelayAsync(record.Id, _moveOptions.RetryInterval, ct).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex) when (ex is ModelMemoryAdmissionException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Refused for good (memory admission, a spec or GPU the node refuses, gpu_missing, ...): the placement is
            // committed, so the move ends here with the reason (audited as failed) and the operator starts the model later
            // (e.g. "Start anyway?"). ModelMemoryAdmissionException and ArgumentException derive from neither of the
            // others, so without them a refusal was retried forever while the record held the model (W8 review MN-128).
            error = $"The model is placed on node '{targetId}' but its start was refused: {ex.Message}";
        }

        _waitingSince.TryRemove(record.Id + StartRetryKey, out _);
        await TransitionAsync(record, MoveStages.Done, r => r with { OutstandingOperationIds = [], Error = error }, ct).ConfigureAwait(false);
    }

    // ---- done / failed -----------------------------------------------------------------------------------------------

    private async Task FinishAsync(OwnershipRecord record, CancellationToken ct)
    {
        var move = record.Move!;
        if (record.Stage == MoveStages.Failed)
        {
            await CancelOurDownloadsAsync(record, ct).ConfigureAwait(false);
            await RestartSourceAsync(record, ct).ConfigureAwait(false);
        }

        bool succeeded = record.Stage == MoveStages.Done;
        string detail = succeeded
            ? $"moved from {Describe(move.SourceNodeId)} to {move.TargetNodeId}" +
              (move.ForceCommittedAt is not null ? $" (force-committed; the instance on {move.SourceNodeId} is stopped when it returns)" : "") +
              (record.Error is { } note ? "; " + note : "")
            : $"move to {move.TargetNodeId} not committed: {record.Error ?? "failed"}";
        await AuditAsync(AuditActors.SystemMove, AuditActions.ModelMove, move.TargetNodeId, record.ModelId,
            succeeded && record.Error is null ? AuditResults.Succeeded : AuditResults.Failed, null, detail).ConfigureAwait(false);

        _finished[record.ModelId] = record with { UpdatedAt = _time.GetUtcNow() };
        _tokens.TryRemove(record.Id, out _);
        TrimFinished();
        await _ownership.ReleaseAsync(record.Id, CancellationToken.None).ConfigureAwait(false);
        MoveServiceLog.Finished(_logger, record.ModelId, record.Id, record.Stage, record.Error ?? "");
        await RefreshRoutingAsync(record.ModelId).ConfigureAwait(false);
    }

    private async Task CancelOurDownloadsAsync(OwnershipRecord record, CancellationToken ct)
    {
        if (await _models.GetAsync(record.ModelId, ct).ConfigureAwait(false) is not { } model)
        {
            return;
        }

        string nodeId = record.Move!.TargetNodeId;
        foreach (var source in model.Sources.DistinctBy(s => s.Folder, StringComparer.Ordinal))
        {
            try
            {
                var client = _clients.GetClient(nodeId);
                if (await client.GetDownloadAsync(source.Folder, ct).ConfigureAwait(false) is { State: DownloadState.Queued or DownloadState.Downloading } running &&
                    running.OperationId is { } op && record.OutstandingOperationIds.Contains(op, StringComparer.Ordinal))
                {
                    var envelope = _runner.CreateEnvelope();
                    var cancel = new DownloadCancelRequest { OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt };
                    await _runner.SubmitAsync(nodeId, AgentRoutes.DownloadCancel, source.Folder, cancel,
                        (c, r, t) => c.CancelDownloadAsync(source.Folder, r, t), owner: AuditOwners.Move(record.Id), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsTransient(ex) || ex is AgentException)
            {
                MoveServiceLog.CleanupFailed(_logger, record.ModelId, record.Id, ex.Message);
            }
        }
    }

    /// <summary>A move that stopped the model and then failed or was cancelled starts it again on the source (best effort).</summary>
    private async Task RestartSourceAsync(OwnershipRecord record, CancellationToken ct)
    {
        var move = record.Move!;
        if (!move.WasRunning || move.SourceNodeId.Length == 0 || move.ForceCommittedAt is not null || !Serving(move.SourceNodeId))
        {
            return;
        }

        try
        {
            if (await _models.GetAsync(record.ModelId, ct).ConfigureAwait(false) is not { Placement: { } placement } ||
                placement.NodeId != move.SourceNodeId ||
                await _clients.GetClient(move.SourceNodeId).GetRuntimeAsync(record.ModelId, ct).ConfigureAwait(false) is not null)
            {
                return; // not stopped by us, or not placed there
            }

            using (ModelWriteScope.AsOwner(record.Id))
            {
                await _runtime.StartAsync(record.ModelId, force: false, ct).ConfigureAwait(false);
            }

            MoveServiceLog.SourceRestarted(_logger, record.ModelId, record.Id, move.SourceNodeId);
        }
        catch (Exception ex) when (IsTransient(ex) || ex is InvalidOperationException or AgentException)
        {
            MoveServiceLog.CleanupFailed(_logger, record.ModelId, record.Id, ex.Message);
        }
    }

    private async Task FailAsync(OwnershipRecord record, string reason, CancellationToken ct) =>
        await TransitionAsync(record, MoveStages.Failed, r => r with { Error = reason }, ct).ConfigureAwait(false);

    // ================================================================ unplace

    /// <summary>
    /// Unplaces a model (design §5.6 "explicit unplace (stops it first)"): the model is stopped on its node and confirmed
    /// gone, then <c>placement: null</c> is written under an ownership record. Unplacing an unplaced model is a no-op.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No such model.</exception>
    /// <exception cref="MoveRefusedException"><c>node_offline</c> (the stop cannot be confirmed), <c>reauthentication_failed</c>.</exception>
    /// <exception cref="ModelBusyException">Another record holds the model.</exception>
    /// <exception cref="TimeoutException">The stop did not finish in time (the model stays placed).</exception>
    public async Task<ModelDefinition> UnplaceAsync(string modelId, ModelUnplaceRequest? request, MoveCaller caller, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);
        ArgumentNullException.ThrowIfNull(caller);
        var model = await _models.GetAsync(modelId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown model '{modelId}'.");
        if (model.Placement is not { } placement)
        {
            return model;
        }

        string nodeId = placement.NodeId;
        OwnershipRecord? record = null;
        try
        {
            if (await _ownership.GetForModelAsync(modelId, ct).ConfigureAwait(false) is { } holder)
            {
                throw new ModelBusyException(modelId, holder);
            }

            if (!Serving(nodeId))
            {
                throw new MoveRefusedException(ErrorCodes.NodeOffline,
                    $"Node '{nodeId}' is {StateOf(nodeId)}, so model '{modelId}' cannot be confirmed stopped there; it stays placed. " +
                    $"Bring the node back, move the model, or revoke the node with unplaceModels=true.");
            }

            if (await IsRunningOnAsync(nodeId, modelId, ct).ConfigureAwait(false) != false)
            {
                Reauthenticate(caller);
            }

            var node = await _nodes.GetAsync(nodeId, ct).ConfigureAwait(false);
            DateTimeOffset now = _time.GetUtcNow();
            record = new OwnershipRecord
            {
                Id = UnplaceIdPrefix + Guid.NewGuid().ToString("N")[..16],
                Kind = OwnershipKind.Edit,
                ModelId = modelId,
                NodeUids = node is null ? [] : [node.Uid],
                PlacementGeneration = placement.Generation,
                Stage = UnplaceStopping,
                StartedAt = now,
                UpdatedAt = now,
                Actor = caller.Actor,
                Description = $"an unplace from node {nodeId}",
                ReferenceId = nodeId,
            };
            var acquired = await _ownership.TryAcquireAsync(record, ct).ConfigureAwait(false);
            if (!acquired.Acquired)
            {
                record = null;
                throw new ModelBusyException(modelId, acquired.Holder!);
            }
        }
        catch (Exception ex) when (RefusalCode(ex) is { } code)
        {
            await AuditAsync(caller.Actor, AuditActions.ModelUnplace, nodeId, modelId, AuditResults.Refused, code, null).ConfigureAwait(false);
            throw;
        }

        // From here the work is not tied to the request: a client that goes away does not leave the model half unplaced. A
        // master that stops meanwhile keeps the record, and the next boot finishes the unplace (ResumeUnplaceAsync).
        bool keepForResume = false;
        try
        {
            var unplaced = await RunUnplaceAsync(record!, nodeId, _lifetime.Token).ConfigureAwait(false);
            await AuditAsync(caller.Actor, AuditActions.ModelUnplace, nodeId, modelId, AuditResults.Succeeded, null, "stopped and unplaced").ConfigureAwait(false);
            return unplaced;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            keepForResume = true;
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AuditAsync(caller.Actor, AuditActions.ModelUnplace, nodeId, modelId, AuditResults.Failed, (ex as AgentException)?.Code, ex.Message)
                .ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (!keepForResume)
            {
                await _ownership.ReleaseAsync(record!.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<ModelDefinition> RunUnplaceAsync(OwnershipRecord record, string nodeId, CancellationToken ct)
    {
        using var scope = AuditScope.Begin(AuditActors.SystemMove);
        if (record.Stage == UnplaceStopping)
        {
            await StopAndConfirmAsync(nodeId, record.ModelId, record.Id, ct).ConfigureAwait(false);
            record = await _ownership.UpdateAsync(record.Id, r => r with { Stage = UnplaceWriting }, ct).ConfigureAwait(false) ?? record;
            await CheckpointAsync(UnplaceWriting, record, ct).ConfigureAwait(false);
        }

        var current = await _models.GetAsync(record.ModelId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Unknown model '{record.ModelId}'.");
        if (current.Placement is { } placement && placement.NodeId == nodeId)
        {
            using (ModelWriteScope.AsOwner(record.Id))
            {
                await _models.UpsertAsync(current with { Placement = null }, ct).ConfigureAwait(false);
            }
        }

        MoveServiceLog.Unplaced(_logger, record.ModelId, nodeId);
        await RefreshRoutingAsync(record.ModelId).ConfigureAwait(false);
        return await _models.GetAsync(record.ModelId, ct).ConfigureAwait(false) ?? current with { Placement = null };
    }

    /// <summary>After a master restart: finishes an unplace whose node is serving within <see cref="MoveServiceOptions.ResumeNodeWait"/>, else gives it up (the model stays placed).</summary>
    private async Task ResumeUnplaceAsync(OwnershipRecord record, CancellationToken ct)
    {
        string nodeId = record.ReferenceId ?? "";
        DateTimeOffset deadline = _time.GetUtcNow() + _moveOptions.ResumeNodeWait;
        while (!Serving(nodeId) && _time.GetUtcNow() < deadline && record.Stage == UnplaceStopping)
        {
            await Task.Delay(_moveOptions.RetryInterval, _time, ct).ConfigureAwait(false);
        }

        try
        {
            if (record.Stage == UnplaceStopping && !Serving(nodeId))
            {
                throw new MoveRefusedException(ErrorCodes.NodeOffline, $"Node '{nodeId}' is {StateOf(nodeId)} after the master restart; the unplace was not finished and the model stays placed.");
            }

            await RunUnplaceAsync(record, nodeId, ct).ConfigureAwait(false);
            await AuditAsync(record.Actor ?? AuditActors.SystemMove, AuditActions.ModelUnplace, nodeId, record.ModelId, AuditResults.Succeeded, null,
                "finished after a master restart").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MoveServiceLog.CleanupFailed(_logger, record.ModelId, record.Id, ex.Message);
            await AuditAsync(record.Actor ?? AuditActors.SystemMove, AuditActions.ModelUnplace, nodeId, record.ModelId, AuditResults.Failed,
                (ex as MoveRefusedException)?.Code, ex.Message).ConfigureAwait(false);
        }

        await _ownership.ReleaseAsync(record.Id, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Stops the model's instance on a node (owner = the record) and waits until the node shows none.</summary>
    private async Task StopAndConfirmAsync(string nodeId, string modelId, string recordId, CancellationToken ct)
    {
        var client = _clients.GetClient(nodeId);
        DateTimeOffset deadline = _time.GetUtcNow() + _moveOptions.StopTimeout;
        bool sent = false;
        while (await client.GetRuntimeAsync(modelId, ct).ConfigureAwait(false) is not null)
        {
            if (_time.GetUtcNow() > deadline)
            {
                throw new TimeoutException($"Model '{modelId}' did not stop on node '{nodeId}' within {_moveOptions.StopTimeout.TotalSeconds:0} s; it stays placed there.");
            }

            if (!sent)
            {
                var envelope = _runner.CreateEnvelope();
                var stop = new StopRequest { OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt };
                try
                {
                    await _runner.SubmitAsync(nodeId, AgentRoutes.RuntimeStop, modelId, stop,
                        (c, r, t) => c.StopAsync(modelId, r, t), owner: AuditOwners.Move(recordId), ct).ConfigureAwait(false);
                }
                catch (AgentException ex) when (ex.Code == ErrorCodes.NotFound)
                {
                    // Gone meanwhile.
                }

                sent = true;
            }

            await Task.Delay(_moveOptions.PollInterval, _time, ct).ConfigureAwait(false);
        }
    }

    // ================================================================ stale instances

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_moveOptions.SweepInterval, _time, ct).ConfigureAwait(false);
                await SweepStaleInstancesAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
            {
                MoveServiceLog.SweepFailed(_logger, ex);
            }
        }
    }

    /// <summary>
    /// One pass of the stale-instance reconciliation (see the class remarks); returns the number of stops sent. Each stop is
    /// sent once per pass and waited for in the background.
    /// </summary>
    public async Task<int> SweepStaleInstancesAsync(CancellationToken ct = default)
    {
        // A master with one node (the single DGX of P1) has nowhere a model could have moved from: every instance there is
        // the placed one or none of the sweep's business (an unplaced model's, an adopted container's), so the pass stops
        // nothing (W8 review MN-128). A stale instance needs a second node, where a move or a force-commit can leave one.
        // Fleet members only (approved, non-revoked; W9 review MN-132, D102): a node waiting for approval adds no source.
        if (!await NodeFleet.IsMultiNodeAsync(_nodes, ct).ConfigureAwait(false))
        {
            return 0;
        }

        var models = (await _models.ListAsync(ct).ConfigureAwait(false)).ToDictionary(m => m.Id, StringComparer.Ordinal);
        var holders = (await _ownership.ListAsync(ct).ConfigureAwait(false)).ToDictionary(r => r.ModelId, StringComparer.Ordinal);
        int sent = 0;
        foreach (var node in _cache.ListNodes().Where(n => n.IsServing))
        {
            foreach (var instance in node.Runtimes)
            {
                if (instance.Tuning || !models.TryGetValue(instance.ModelId, out var model) ||
                    string.Equals(model.Placement?.NodeId, node.NodeId, StringComparison.Ordinal))
                {
                    continue; // a tuning candidate, an unknown container, or the placed instance
                }

                if (holders.TryGetValue(model.Id, out var holder) && holder.Kind is not (OwnershipKind.Move or OwnershipKind.Edit))
                {
                    continue; // a job or restore decides about its own instances
                }

                if (holder is { Kind: OwnershipKind.Move, Move: { } move } && string.Equals(move.TargetNodeId, node.NodeId, StringComparison.Ordinal))
                {
                    continue; // the move's own start on its target (the registry read may predate its commit)
                }

                string key = node.NodeId + "/" + model.Id;
                if (!_sweeping.TryAdd(key, 0))
                {
                    continue;
                }

                sent++;
                _ = Task.Run(() => StopStaleAsync(node.NodeId, model, key, ct), CancellationToken.None);
            }
        }

        return sent;
    }

    private async Task StopStaleAsync(string nodeId, ModelDefinition model, string key, CancellationToken ct)
    {
        using var scope = AuditScope.Begin(AuditActors.SystemMove);
        try
        {
            MoveServiceLog.StaleInstance(_logger, model.Id, nodeId, Describe(model.Placement));
            await StopAndConfirmAsync(nodeId, model.Id, "cleanup-" + model.Id, ct).ConfigureAwait(false);
            await AuditAsync(AuditActors.SystemMove, AuditActions.ModelStop, nodeId, model.Id, AuditResults.Succeeded, null,
                $"stale instance stopped (the model is {Describe(model.Placement)})").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && (IsTransient(ex) || ex is AgentException or InvalidOperationException or TimeoutException))
        {
            MoveServiceLog.CleanupFailed(_logger, model.Id, "cleanup", ex.Message);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _sweeping.TryRemove(key, out _);
        }
    }

    // ================================================================ helpers

    private async Task<OwnershipRecord?> ReadAsync(string recordId, CancellationToken ct) =>
        (await _ownership.ListAsync(ct).ConfigureAwait(false)).FirstOrDefault(r => r.Id == recordId);

    /// <summary>
    /// Moves the record from its current stage to <paramref name="toStage"/> when it is still at the stage it was read at
    /// (a force-commit or cancel may have moved it on); returns the stored record and whether this call changed it.
    /// </summary>
    private async Task<(OwnershipRecord? Record, bool Applied)> TransitionAsync(
        OwnershipRecord from, string toStage, Func<OwnershipRecord, OwnershipRecord>? change, CancellationToken ct)
    {
        bool applied = false;
        var stored = await _ownership.UpdateAsync(from.Id, r =>
        {
            if (!string.Equals(r.Stage, from.Stage, StringComparison.Ordinal))
            {
                return r;
            }

            applied = true;
            return (change?.Invoke(r) ?? r) with { Stage = toStage };
        }, ct).ConfigureAwait(false);
        if (applied && stored is not null)
        {
            MoveServiceLog.Stage(_logger, stored.ModelId, stored.Id, from.Stage, toStage);
        }

        if (applied && from.Stage == MoveStages.Downloading)
        {
            _tokens.TryRemove(from.Id, out _); // only the target's downloads use it
        }

        return (stored, applied);
    }

    /// <summary>Writes a waiting note into the record (only when it changes).</summary>
    private async Task NoteAsync(OwnershipRecord record, string note, CancellationToken ct)
    {
        if (string.Equals(record.Error, note, StringComparison.Ordinal))
        {
            return;
        }

        await _ownership.UpdateAsync(record.Id, r => string.Equals(r.Stage, record.Stage, StringComparison.Ordinal) ? r with { Error = note } : r, ct)
            .ConfigureAwait(false);
    }

    private async Task RememberOperationAsync(string recordId, string operationId, CancellationToken ct) =>
        await _ownership.UpdateAsync(recordId, r => r with { OutstandingOperationIds = [.. r.OutstandingOperationIds, operationId] }, ct).ConfigureAwait(false);

    private async Task CheckpointAsync(string checkpoint, OwnershipRecord record, CancellationToken ct)
    {
        if (CheckpointForTests is { } hook)
        {
            await hook(checkpoint, record, ct).ConfigureAwait(false);
        }
    }

    private async Task DelayAsync(string recordId, TimeSpan interval, CancellationToken ct)
    {
        Task wake = _workers.TryGetValue(recordId, out var worker) ? worker.WakeTask : Task.Delay(Timeout.Infinite, ct);
        await Task.WhenAny(Task.Delay(interval, _time, ct), wake).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>Waits while <paramref name="nodeId"/> is not serving; false when the record left its stage meanwhile.</summary>
    private async Task<bool> WaitForNodeAsync(OwnershipRecord record, string nodeId, string role, CancellationToken ct)
    {
        while (!Serving(nodeId))
        {
            await NoteAsync(record, $"The {role} node '{nodeId}' is {StateOf(nodeId)}: the move waits for it.", ct).ConfigureAwait(false);
            await DelayAsync(record.Id, _moveOptions.RetryInterval, ct).ConfigureAwait(false);
            var current = await ReadAsync(record.Id, ct).ConfigureAwait(false);
            if (current?.Stage != record.Stage)
            {
                return false;
            }

            record = current;
        }

        return true;
    }

    private bool Serving(string nodeId) => nodeId.Length > 0 && _cache.GetNode(nodeId) is { IsServing: true };

    private string StateOf(string nodeId) =>
        _cache.GetNode(nodeId) is { } live ? live.State.ToString().ToLowerInvariant() : "not connected";

    /// <summary>True when the model's instance (on its placed node, or its last-seen one) is Starting or Ready.</summary>
    private async Task<bool> IsRunningAsync(string modelId, CancellationToken ct)
    {
        var status = await _runtime.GetStatusAsync(modelId, ct).ConfigureAwait(false);
        return status.State is ModelState.Starting or ModelState.Ready;
    }

    /// <summary>Whether the instance on <paramref name="nodeId"/> is Starting or Ready; null when the node cannot tell.</summary>
    private async Task<bool?> IsRunningOnAsync(string nodeId, string modelId, CancellationToken ct)
    {
        try
        {
            var runtime = await _clients.GetClient(nodeId).GetRuntimeAsync(modelId, ct).ConfigureAwait(false);
            return runtime is { State: ModelState.Starting or ModelState.Ready };
        }
        catch (Exception ex) when (IsTransient(ex) || ex is AgentException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<MoveDownloadView>> ReadDownloadsAsync(string nodeId, ModelDefinition model, CancellationToken ct)
    {
        var views = new List<MoveDownloadView>();
        foreach (var source in model.Sources.DistinctBy(s => s.Folder, StringComparer.Ordinal))
        {
            NodeDownloadStatus? status = null;
            try
            {
                status = await _clients.GetClient(nodeId).GetDownloadAsync(source.Folder, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex) || ex is AgentException)
            {
                // Shown without progress.
            }

            views.Add(new MoveDownloadView(source.RepoId, source.Folder, status?.State, status?.BytesDownloaded ?? 0, status?.TotalBytes ?? 0, status?.Error));
        }

        return views;
    }

    private void Reauthenticate(MoveCaller caller)
    {
        if (_reauth is null || !_reauth.LoginEnabled)
        {
            throw new MoveRefusedException(ErrorCodes.ReauthenticationFailed,
                "Admin login is disabled (no MirbadLLM:AdminPassword), so this action cannot be confirmed.");
        }

        switch (_reauth.Verify(caller.User))
        {
            case AdminReauthOutcome.Verified:
                return;
            case AdminReauthOutcome.RateLimited:
                throw new MoveRefusedException(ErrorCodes.RateLimited, "Too many password attempts. Try again in a minute.", 429);
            case AdminReauthOutcome.LoginDisabled:
                throw new MoveRefusedException(ErrorCodes.ReauthenticationFailed, "Admin login is disabled, so this action cannot be confirmed.");
            default:
                throw new MoveRefusedException(ErrorCodes.ReauthenticationFailed,
                    "Sign in to the portal to perform this action.");
        }
    }

    private async Task RefreshRoutingAsync(string modelId)
    {
        try
        {
            if (_cacheIndex is not null)
            {
                await _cacheIndex.RefreshModelsAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await _cache.RefreshModelAsync(modelId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            MoveServiceLog.CleanupFailed(_logger, modelId, "routing", ex.Message); // the node monitor's poll catches up
        }
    }

    private async Task AuditAsync(string actor, string action, string? nodeId, string modelId, string result, string? errorCode, string? detail)
    {
        try
        {
            await _audit.WriteAsync(new AuditRecord
            {
                Time = _time.GetUtcNow(),
                Actor = actor,
                Action = action,
                NodeId = nodeId,
                ModelId = modelId,
                Result = result,
                ErrorCode = errorCode,
                Detail = detail,
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MoveServiceLog.AuditFailed(_logger, ex, action); // IAuditLog never fails the action it records
        }
    }

    private void TrimFinished()
    {
        if (_finished.Count <= FinishedKept)
        {
            return;
        }

        foreach (var old in _finished.OrderBy(f => f.Value.UpdatedAt).Take(_finished.Count - FinishedKept).ToList())
        {
            _finished.TryRemove(old.Key, out _);
        }
    }

    /// <summary>The error code of a refusal answered to the caller (for its audit line), or null for anything else.</summary>
    private static string? RefusalCode(Exception ex) => ex switch
    {
        MoveRefusedException refused => refused.Code,
        ModelBusyException => ErrorCodes.ModelBusy,
        PlacementValidationException => ErrorCodes.PlacementInvalid,
        PlacementConflictException => ErrorCodes.PlacementChanged,
        _ => null,
    };

    private static bool IsTransient(Exception ex) =>
        ex is AgentUnreachableException or TimeoutException or KeyNotFoundException ||
        ex is AgentException { StatusCode: >= 500 };

    private static string Describe(string nodeId) => nodeId.Length == 0 ? "unplaced" : nodeId;

    private static string Describe(ModelPlacement? placement) =>
        placement is null ? "unplaced" : string.Create(CultureInfo.InvariantCulture, $"on node '{placement.NodeId}', generation {placement.Generation}");

    private sealed class Worker
    {
        private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task { get; set; } = Task.CompletedTask;

        public Task WakeTask => Volatile.Read(ref _wake).Task;

        public void Wake() =>
            Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>A stage failed for good (refused download or image): the move fails before the commit.</summary>
    private sealed class MoveFailedException(string message) : Exception(message);
}

/// <summary>DI registration of <see cref="MoveService"/> (MN-109; composed by the master, MN-130 owns the final composition).</summary>
public static class MoveServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="MoveServiceOptions"/> from <c>MirbadLLM:Moves</c> and registers <see cref="MoveService"/> as a
    /// singleton and hosted service (it resumes the move and unplace records of <c>ownership.json</c> at boot and runs the
    /// stale-instance reconciliation). Expects the orchestration services (registry, nodes, status cache, agent clients,
    /// operation runner, ownership store, runtime); uses <see cref="IAuditLog"/> and <see cref="IAdminReauthenticator"/>
    /// when registered.
    /// </summary>
    public static IServiceCollection AddMirbadLLMMoves(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<MoveServiceOptions>().Bind(configuration.GetSection(MoveServiceOptions.SectionName));
        services.AddSingleton(sp => new MoveService(
            sp.GetRequiredService<IModelRegistry>(),
            sp.GetRequiredService<INodeRegistry>(),
            sp.GetRequiredService<INodeStatusCache>(),
            sp.GetRequiredService<IAgentClientFactory>(),
            sp.GetRequiredService<AgentOperationRunner>(),
            sp.GetRequiredService<IOwnershipStore>(),
            sp.GetRequiredService<IModelRuntime>(),
            sp.GetRequiredService<IOptions<MirbadLLMOptions>>(),
            sp.GetRequiredService<IOptions<MoveServiceOptions>>().Value,
            sp.GetService<TimeProvider>(),
            sp.GetService<ILogger<MoveService>>(),
            sp.GetService<IAuditLog>(),
            sp.GetService<IAdminReauthenticator>(),
            sp.GetService<IOptions<RegistryMigrationOptions>>(),
            sp.GetService<StartupTimeTracker>()));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MoveService>());
        return services;
    }
}

/// <summary>Source-generated log messages of <see cref="MoveService"/>.</summary>
internal static partial class MoveServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Move {RecordId} of model {ModelId} started: {SourceNodeId} -> {TargetNodeId}.")]
    public static partial void Started(ILogger logger, string modelId, string recordId, string sourceNodeId, string targetNodeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Resuming {RecordId} of model {ModelId} at stage {Stage} after a master restart.")]
    public static partial void Resuming(ILogger logger, string modelId, string recordId, string stage);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not read ownership.json to resume moves; they resume at the next start.")]
    public static partial void ResumeFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Move {RecordId} of model {ModelId}: {From} -> {To}.")]
    public static partial void Stage(ILogger logger, string modelId, string recordId, string from, string to);

    [LoggerMessage(Level = LogLevel.Information, Message = "Move {RecordId} of model {ModelId}: downloading {RepoId} into {Folder} on node {NodeId}.")]
    public static partial void DownloadStarted(ILogger logger, string modelId, string recordId, string repoId, string folder, string nodeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Move {RecordId} of model {ModelId}: pulling {Image} on node {NodeId}.")]
    public static partial void ImagePull(ILogger logger, string modelId, string recordId, string image, string nodeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Move {RecordId} of model {ModelId}: stopping it on node {NodeId}.")]
    public static partial void StoppingSource(ILogger logger, string modelId, string recordId, string nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Move {RecordId} of model {ModelId} force-committed while node {NodeId} is not serving.")]
    public static partial void ForceCommitted(ILogger logger, string modelId, string recordId, string nodeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Move {RecordId} of model {ModelId}: placement committed on node {NodeId} (generation {Generation}).")]
    public static partial void Committed(ILogger logger, string modelId, string recordId, string nodeId, long generation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Move {RecordId} of model {ModelId}: started on node {NodeId}.")]
    public static partial void StartedOnTarget(ILogger logger, string modelId, string recordId, string nodeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Move {RecordId} of model {ModelId}: started again on its source node {NodeId}.")]
    public static partial void SourceRestarted(ILogger logger, string modelId, string recordId, string nodeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Move {RecordId} of model {ModelId} ended at {Stage}. {Error}")]
    public static partial void Finished(ILogger logger, string modelId, string recordId, string stage, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Move {RecordId} of model {ModelId} at {Stage}: {Reason}; trying again.")]
    public static partial void StageRetry(ILogger logger, string modelId, string recordId, string stage, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Model {ModelId} ({RecordId}): clean-up step failed: {Reason}")]
    public static partial void CleanupFailed(ILogger logger, string modelId, string recordId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Move worker {RecordId} stopped; it resumes at the next master start.")]
    public static partial void WorkerFailed(ILogger logger, Exception exception, string recordId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Model {ModelId} unplaced from node {NodeId}.")]
    public static partial void Unplaced(ILogger logger, string modelId, string nodeId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stopping a stale instance of model {ModelId} on node {NodeId} (the model is {Placement}).")]
    public static partial void StaleInstance(ILogger logger, string modelId, string nodeId, string placement);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stale-instance reconciliation pass failed; it runs again.")]
    public static partial void SweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the audit line for {Action}.")]
    public static partial void AuditFailed(ILogger logger, Exception exception, string action);
}
