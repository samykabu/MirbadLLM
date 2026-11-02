using System.Text;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Multinode.Fakes;

/// <summary>
/// Operations (design §4.4): the durable log <c>{state}/operations/log.jsonl</c> of <see cref="OperationLogEntry"/> lines
/// (accepted, stage, completed; written before the agent answers), payload-bound deduplication, expiry and retention,
/// the fencing triple in <c>{state}/fencing.json</c>, cancellation, and restart with reconciliation.
/// </summary>
internal sealed partial class FakeAgent
{
    private readonly Dictionary<string, FakeOp> _ops = new(StringComparer.Ordinal);
    private readonly List<FakeOp> _opOrder = [];
    private FileStream? _log;

    private string LogPath => Path.Combine(_stateDir, OperationRetention.LogRelativePath);

    private string FencingPath => Path.Combine(_stateDir, "fencing.json");

    // ---- Fencing (design §4.2 layer 2) ----

    private void FenceLocked(MasterTriple presented)
    {
        var accepted = _acceptedMaster;
        if (accepted is null || presented.Epoch > accepted.Epoch)
        {
            // Persisted (flushed to disk, then renamed) before it becomes the accepted triple, as the real agent does.
            string temp = FencingPath + ".tmp";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, presented, AgentJson.Options);
                file.Flush(flushToDisk: true);
            }

            File.Move(temp, FencingPath, overwrite: true);
            _acceptedMaster = presented;
            return;
        }

        if (presented.Epoch < accepted.Epoch)
        {
            throw Fail(ErrorCodes.StaleMaster, $"Master epoch {presented.Epoch} is lower than the accepted epoch {accepted.Epoch}.");
        }

        if (!presented.SameBoot(accepted))
        {
            throw Fail(ErrorCodes.CompetingMaster, $"Another master ({accepted.InstanceId}, boot {accepted.BootId}) drives this node at epoch {accepted.Epoch}.");
        }
    }

    private void LoadFencing()
    {
        _acceptedMaster = File.Exists(FencingPath)
            ? JsonSerializer.Deserialize<MasterTriple>(File.ReadAllText(FencingPath), AgentJson.Options)
            : null;
    }

    /// <summary>True when the operation's master is still the accepted one (re-checked before destructive stages, §4.2).</summary>
    private bool IsCurrentLocked(FakeOp op) => op.Master.SameBoot(_acceptedMaster);

    // ---- Deduplication and acceptance (design §4.3 envelope, §4.4 items 1, 2, 6) ----

    /// <summary>
    /// Under the state lock: returns the stored operation for the same id, kind, target and digest; 409
    /// <c>operation_id_conflict</c> for the same id with anything else; for an unknown id, 410 <c>operation_expired</c> when
    /// its envelope expired and 400 <c>clock_skew</c> when <c>issuedAt</c> is more than 5 minutes ahead. False = a new operation.
    /// </summary>
    private bool TryDuplicateLocked(FakeCall call, out FakeReply reply)
    {
        var mutation = call.Mutation;
        var now = Now;
        if (_ops.TryGetValue(mutation.OperationId, out var op))
        {
            if (op.Kind == call.Kind && op.Target == call.Target && op.Digest == call.Digest)
            {
                // A stored refusal, and a host-service action that did not succeed, answer with their error again (as the
                // real agent's HostServiceOperations.ResultFor and storage replays do).
                if (op.Error is { } refusal && (op.Refusal ||
                    (IsHostKind(op.Kind) && op.State is OperationState.Failed or OperationState.Cancelled or OperationState.Unknown)))
                {
                    throw new AgentException(StatusOf(refusal.Code), refusal);
                }

                reply = DuplicateReplyLocked(call.Route, op);
                return true;
            }

            throw Fail(ErrorCodes.OperationIdConflict, $"Operation id {op.Id} was already used for {op.Kind} on '{op.Target}' with another payload.");
        }

        if (mutation.ExpiresAt <= now)
        {
            throw Fail(ErrorCodes.OperationExpired, $"Operation {mutation.OperationId} expired at {mutation.ExpiresAt:O}; it is not executed.");
        }

        // After the id lookup, as the real log does: a known id is answered from the log whatever the clock says (§4.4 item 2).
        if (mutation.IssuedAt > now + OperationRetention.MaxIssuedAtFutureSkew)
        {
            throw Fail(ErrorCodes.ClockSkew, "The envelope's issuedAt is more than 5 minutes in the future.");
        }

        reply = default;
        return false;
    }

    private FakeReply DuplicateReplyLocked(AgentRoute route, FakeOp op)
    {
        if (route == AgentRoutes.DownloadStart)
        {
            return FakeReply.Json(DownloadStatusOfLocked(op), route.SuccessStatus);
        }

        return FakeReply.Json(op.ToStatus(), route.SuccessStatus);
    }

    /// <summary>Durably records a new operation as Accepted and publishes it.</summary>
    private FakeOp AcceptLocked(FakeCall call)
    {
        PruneLocked();
        var mutation = call.Mutation;
        var op = new FakeOp
        {
            Id = mutation.OperationId,
            Kind = call.Kind,
            Target = call.Target,
            Digest = call.Digest,
            Master = call.Master!,
            Payload = call.Redacted,
            Request = mutation,
            IssuedAt = mutation.IssuedAt,
            ExpiresAt = mutation.ExpiresAt,
            StartedAt = Now,
            State = OperationState.Accepted,
            Stage = OperationStages.Accepted,
            Cts = CancellationTokenSource.CreateLinkedTokenSource(_boot.Cts.Token),
        };
        AppendLogLocked(new OperationLogEntry
        {
            Type = OperationLogEntryType.Accepted,
            OperationId = op.Id,
            At = op.StartedAt,
            BootId = _boot.Id,
            NodeUid = NodeUid,
            Kind = op.Kind,
            Target = op.Target,
            PayloadDigest = op.Digest,
            Payload = op.Payload,
            IssuedAt = op.IssuedAt,
            ExpiresAt = op.ExpiresAt,
            Master = op.Master,
            State = op.State,
            Stage = op.Stage,
        });
        _ops[op.Id] = op;
        _opOrder.Add(op);
        PublishLocked(new OperationEvent { Operation = op.ToStatus() });
        return op;
    }

    /// <summary>Durably records a stage (Accepted becomes Running), publishes it, and fires a crash trigger if armed.</summary>
    private void StageLocked(FakeOp op, string? stage, OperationProgress? progress = null, ReservationInfo? reservation = null)
    {
        if (op.State.IsTerminal())
        {
            return;
        }

        op.State = OperationState.Running;
        op.Stage = stage ?? op.Stage;
        op.Progress = progress ?? op.Progress;
        op.Reservation = reservation ?? op.Reservation;
        AppendLogLocked(new OperationLogEntry
        {
            Type = OperationLogEntryType.Stage,
            OperationId = op.Id,
            At = Now,
            BootId = _boot.Id,
            State = op.State,
            Stage = op.Stage,
            Progress = progress,
            Reservation = reservation,
        });
        PublishLocked(new OperationEvent { Operation = op.ToStatus() });
        if (stage is not null && _crashAfter is { } crash && crash.Kind == op.Kind && crash.Stage == stage)
        {
            _crashAfter = null;
            CrashLocked();
        }
    }

    /// <summary>Durably records the terminal state; completing a terminal operation again changes nothing.</summary>
    private void CompleteLocked(FakeOp op, OperationState state, AgentError? error = null, object? result = null)
    {
        if (op.State.IsTerminal())
        {
            return;
        }

        op.State = state;
        op.Stage = OperationStages.Completed;
        op.Error = error;
        op.Result = result is null ? null : AgentJson.ToElement(result);
        op.EndedAt = Now;
        ReleaseReservationLocked(op);
        AppendLogLocked(new OperationLogEntry
        {
            Type = OperationLogEntryType.Completed,
            OperationId = op.Id,
            At = op.EndedAt.Value,
            BootId = _boot.Id,
            State = state,
            Stage = op.Stage,
            Error = error,
            Result = op.Result,
        });
        PublishLocked(new OperationEvent { Operation = op.ToStatus() });
    }

    /// <summary>
    /// Retention (design §4.4 item 6): terminal records past <c>expiresAt</c> + 10 minutes are pruned, except the most
    /// recent <see cref="FakeAgentOptions.MinRetainedRecords"/>; active operations are never pruned.
    /// </summary>
    private void PruneLocked()
    {
        var now = Now;
        int keepFrom = _opOrder.Count - _options.MinRetainedRecords;
        for (int i = 0; i < keepFrom; i++)
        {
            var op = _opOrder[i];
            if (op.State.IsTerminal() && op.ExpiresAt + OperationRetention.RetentionMargin < now)
            {
                _ops.Remove(op.Id);
                _opOrder[i] = null!;
            }
        }

        _opOrder.RemoveAll(o => o is null);
    }

    private void AppendLogLocked(OperationLogEntry entry)
    {
        _log ??= new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entry, AgentJson.Options) + "\n");
        _log.Write(line);
        _log.Flush();
    }

    private void CloseLogLocked()
    {
        _log?.Dispose();
        _log = null;
    }

    // ---- Operation routes ----

    private FakeReply OperationGet(string operationId)
    {
        lock (_gate)
        {
            return _ops.TryGetValue(operationId, out var op)
                ? FakeReply.Json(op.ToStatus())
                : throw Fail(ErrorCodes.NotFound, $"No operation {operationId}.");
        }
    }

    private FakeReply OperationsList(OperationsQuery query)
    {
        lock (_gate)
        {
            IEnumerable<FakeOp> ops = _opOrder;
            if (query.State == OperationsQuery.ActiveState)
            {
                ops = ops.Where(o => !o.State.IsTerminal());
            }

            return FakeReply.Json<IReadOnlyList<OperationStatus>>([.. ops.Select(o => o.ToStatus())]);
        }
    }

    /// <summary>
    /// <c>POST /operations/{id}/cancel</c> (design §4.4 item 7): cancels a launch before its instance exists (releasing the
    /// reservation), an image pull or a download. A launch whose instance exists → 409 <c>invalid_state</c> (stop it). An
    /// operation that already ended → the cancel succeeds with the target's final status as result, like the real agent
    /// and the design's "already finished → succeeded with the final state" (W2 review MN-092, D39).
    /// </summary>
    private FakeReply OperationCancel(FakeCall call)
    {
        lock (_gate)
        {
            if (TryDuplicateLocked(call, out var duplicate))
            {
                return duplicate;
            }

            if (!_ops.TryGetValue(call.Target, out var target))
            {
                throw Fail(ErrorCodes.NotFound, $"No operation {call.Target}.");
            }

            if (target.State.IsTerminal())
            {
                var noop = AcceptLocked(call);
                CompleteLocked(noop, OperationState.Succeeded, result: target.ToStatus());
                return FakeReply.Json(noop.ToStatus(), call.Route.SuccessStatus);
            }

            bool cancellable = target.Kind switch
            {
                OperationKinds.Launch => !target.InstanceCreated,
                OperationKinds.ImagePull or OperationKinds.Download => true,
                _ => false,
            };
            if (!cancellable)
            {
                throw Fail(ErrorCodes.InvalidState, target.Kind == OperationKinds.Launch
                    ? $"The instance of launch {target.Id} already exists; stop the model instead."
                    : $"Operation {target.Id} ({target.Kind}) cannot be cancelled.");
            }

            var op = AcceptLocked(call);
            target.Cts.Cancel();
            if (target.Kind == OperationKinds.Download)
            {
                CancelDownloadLocked(target);
            }

            CompleteLocked(target, OperationState.Cancelled);
            CompleteLocked(op, OperationState.Succeeded, result: target.ToStatus());
            return FakeReply.Json(op.ToStatus(), call.Route.SuccessStatus);
        }
    }

    // ---- Restart and reconciliation (design §4.4 item 5, §4.6) ----

    /// <summary>
    /// Restarts the agent process (new boot id, seq from 0, empty ring, streams closed). Instances keep running (they are
    /// containers); the operation log and the fencing triple are reloaded from the state directory; non-terminal
    /// operations are reconciled as in design §4.4 items 5 and 8; reservations are rebuilt.
    /// <paramref name="loseRuntimes"/> also drops instances to simulate a host reboot.
    /// </summary>
    public Task RestartAsync(bool loseRuntimes = false)
    {
        lock (_gate)
        {
            _boot.Cts.Cancel();
            AbortSubscribersLocked();
            _retired.Add(_boot.Cts);
            _boot = new Boot(NewBootId(), Now);
            _seq = 0;
            _ring.Clear();
            _crashed = false;
            _telemetrySeq = 0;
            _samples.Clear();
            if (loseRuntimes)
            {
                _instances.Clear(); // A host reboot loses its processes/containers, unlike an agent-only restart.
            }

            TakeSampleLocked();
            CloseLogLocked();
            LoadFencing();
            ReloadOperationsLocked();
            ReconcileLocked();
        }

        return Task.CompletedTask;
    }

    private void ReloadOperationsLocked()
    {
        foreach (var op in _opOrder)
        {
            _retired.Add(op.Cts);
        }

        _ops.Clear();
        _opOrder.Clear();
        _reservations.Clear();
        if (!File.Exists(LogPath))
        {
            return;
        }

        foreach (string line in File.ReadAllLines(LogPath))
        {
            OperationLogEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<OperationLogEntry>(line, AgentJson.Options);
            }
            catch (JsonException)
            {
                continue; // Torn last line.
            }

            if (entry is null)
            {
                continue;
            }

            if (entry.Type == OperationLogEntryType.Accepted)
            {
                var op = new FakeOp
                {
                    Id = entry.OperationId,
                    Kind = entry.Kind ?? "",
                    Target = entry.Target ?? "",
                    Digest = entry.PayloadDigest ?? "",
                    Master = entry.Master ?? new MasterTriple("", 0, ""),
                    Payload = entry.Payload ?? default,
                    IssuedAt = entry.IssuedAt ?? entry.At,
                    ExpiresAt = entry.ExpiresAt ?? entry.At,
                    StartedAt = entry.At,
                    State = entry.State ?? OperationState.Accepted,
                    Stage = entry.Stage,
                    Cts = CancellationTokenSource.CreateLinkedTokenSource(_boot.Cts.Token),
                };
                _ops[op.Id] = op;
                _opOrder.Add(op);
            }
            else if (_ops.TryGetValue(entry.OperationId, out var op))
            {
                op.State = entry.State ?? op.State;
                op.Stage = entry.Stage ?? op.Stage;
                op.Progress = entry.Progress ?? op.Progress;
                op.Reservation = entry.Reservation ?? op.Reservation;
                if (entry.Type == OperationLogEntryType.Completed)
                {
                    op.Error = entry.Error;
                    op.Result = entry.Result;
                    op.EndedAt = entry.At;
                    op.Reservation = null;
                }
            }
        }
    }

    private void ReconcileLocked()
    {
        foreach (var op in _opOrder.Where(o => !o.State.IsTerminal()).ToList())
        {
            switch (op.Kind)
            {
                case OperationKinds.Launch:
                    ReconcileLaunchLocked(op);
                    break;
                case OperationKinds.Stop:
                    StartStopWork(op, op.Target);
                    break;
                case OperationKinds.ImagePull:
                    StartPullWork(op, op.Payload.Deserialize<ImagePullRequest>(AgentJson.Options)!);
                    break;
                case OperationKinds.Download:
                    ResumeDownloadLocked(op);
                    break;
                case OperationKinds.FolderDelete:
                    _folders.Remove(op.Target);
                    CompleteLocked(op, OperationState.Succeeded);
                    break;
                case OperationKinds.PkiRotationStart or OperationKinds.PkiRotationCert or OperationKinds.PkiRotationCommit or OperationKinds.PkiRotationAbort:
                    // The rotation stage on the agent is the truth; the master queries it (§10.2).
                    CompleteLocked(op, OperationState.Succeeded, result: _rotations.TryGetValue(RotationIdOf(op), out var rotation) ? RotationStatusLocked(rotation) : null);
                    break;
                default:
                    ReconcileHostOperationLocked(op);
                    break;
            }
        }
    }

    private static string RotationIdOf(FakeOp op) => op.Target;

    /// <summary>One operation as the fake keeps it; the persisted form is the log.</summary>
    private sealed class FakeOp
    {
        public required string Id { get; init; }

        public required string Kind { get; init; }

        public required string Target { get; init; }

        public required string Digest { get; init; }

        public required MasterTriple Master { get; init; }

        public JsonElement Payload { get; init; }

        public AgentMutation? Request { get; init; }

        public DateTimeOffset IssuedAt { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }

        public DateTimeOffset StartedAt { get; init; }

        public DateTimeOffset? EndedAt { get; set; }

        public OperationState State { get; set; }

        public string? Stage { get; set; }

        public AgentError? Error { get; set; }

        /// <summary>Refused by its executor right after acceptance (storage, host services); replays answer the error.</summary>
        public bool Refusal { get; set; }

        public OperationProgress? Progress { get; set; }

        public JsonElement? Result { get; set; }

        public ReservationInfo? Reservation { get; set; }

        /// <summary>Launch: the GPUs the instance gets.</summary>
        public IReadOnlyList<GpuRef> LaunchGpus { get; set; } = [];

        /// <summary>Launch: the instance exists (cancel now means stop).</summary>
        public bool InstanceCreated { get; set; }

        public required CancellationTokenSource Cts { get; init; }

        public OperationStatus ToStatus() => new()
        {
            Id = Id,
            Kind = Kind,
            Target = Target,
            PayloadDigest = Digest,
            State = State,
            Stage = Stage,
            Error = Error,
            Progress = Progress,
            IssuedAt = IssuedAt,
            ExpiresAt = ExpiresAt,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            Result = Result,
        };
    }
}
