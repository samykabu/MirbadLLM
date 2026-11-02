using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Agent.Operations;

/// <summary>
/// The agent's durable operation log (design §4.4 items 1–8): <c>{state}/operations/log.jsonl</c> (one
/// <see cref="OperationLogEntry"/> per line, appended and flushed to disk before any method returns) compacted into
/// <c>{state}/operations/operations.json</c>.
/// <para>Durability rules:</para>
/// <list type="bullet">
/// <item>Every line is written in full and <c>FileStream.Flush(flushToDisk: true)</c>'d before the in-memory state changes
/// and before the caller can answer the master. A failed write throws <see cref="OperationLogUnavailableException"/>,
/// changes nothing, and the next write first cuts the file back to the last complete line.</item>
/// <item>On load, an unparseable line (a torn last line after a crash, or corruption) is ignored and counted in
/// <see cref="TornLinesIgnored"/>; a torn tail is cut off before the next append so it cannot merge with a new line.</item>
/// <item>Compaction writes the snapshot to a temp file, flushes it to disk, renames it over <c>operations.json</c> and only
/// then truncates the log. The snapshot records the log prefix it contains, so a crash between rename and truncation
/// does not replay that prefix.</item>
/// </list>
/// <para>Stage convention for the terminal line: <c>Succeeded</c> records <see cref="OperationStages.Completed"/>;
/// <c>Failed</c>, <c>Cancelled</c> and <c>Unknown</c> keep the last stage reached (e.g. <c>dispatched</c>), so the status
/// shows where the operation stopped (matches the MN-001 log fixture).</para>
/// <para>Thread safety: writes are serialized; reads never wait for disk I/O.</para>
/// </summary>
public sealed class OperationLog : IOperationLog, IHealthContributor, IDisposable
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly OperationLogOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _stateLock = new();

    // Guarded by _stateLock. Insertion order of _order = acceptance order.
    private readonly Dictionary<string, StoredOperation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sequence = new(StringComparer.Ordinal);
    private long _nextSequence;

    // Guarded by _writeLock.
    private FileStream? _stream;
    private long _committedLength;
    private bool _needsRepair;
    private bool _disposed;
    private int _disposeStarted;

    // Read without a lock by health checks.
    private volatile string? _unavailableReason;

    /// <summary>Loads the snapshot and replays the log. Throws when an existing file cannot be read or is from a newer build.</summary>
    public OperationLog(OperationLogOptions options, TimeProvider timeProvider, ILogger<OperationLog>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrEmpty(options.StateDirectory);
        ArgumentException.ThrowIfNullOrEmpty(options.BootId);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MinRetainedRecords);

        _options = options;
        _time = timeProvider;
        _logger = (ILogger?)logger ?? NullLogger.Instance;

        Load();

        // Open the log now so an unwritable state directory shows up in health before the first request.
        try
        {
            EnsureWritable();
        }
        catch (OperationLogUnavailableException)
        {
            // Reported through _unavailableReason; requests are refused until it recovers.
        }
    }

    /// <inheritdoc />
    public int TornLinesIgnored { get; private set; }

    /// <summary>Lines ignored on load because their operation had no accepted record (diagnostics).</summary>
    public int OrphanLinesIgnored { get; private set; }

    /// <summary>Why the log is not writable, or null when it is.</summary>
    public string? UnavailableReason => _unavailableReason;

    // ---- IOperationLog: writes -------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<OperationAcceptance> AcceptAsync(OperationSubmission submission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentException.ThrowIfNullOrEmpty(submission.OperationId);
        ArgumentException.ThrowIfNullOrEmpty(submission.Kind);
        ArgumentNullException.ThrowIfNull(submission.Target);
        ArgumentNullException.ThrowIfNull(submission.Master);

        JsonElement redacted = OperationPayload.Redact(submission.Payload);
        string digest = OperationPayload.ComputeDigest(redacted);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // A known id is answered from the log whatever its envelope says now (§4.4 item 2).
            StoredOperation? existing = Get(submission.OperationId);
            if (existing is not null)
            {
                OperationStatus stored = existing.Status;
                bool same = string.Equals(stored.Kind, submission.Kind, StringComparison.Ordinal) &&
                            string.Equals(stored.Target, submission.Target, StringComparison.Ordinal) &&
                            string.Equals(stored.PayloadDigest, digest, StringComparison.Ordinal);
                return same
                    ? new OperationAcceptance(OperationAcceptOutcome.Duplicate, stored)
                    : new OperationAcceptance(OperationAcceptOutcome.Conflict, null);
            }

            DateTimeOffset now = _time.GetUtcNow();
            if (submission.IssuedAt > now + OperationRetention.MaxIssuedAtFutureSkew)
            {
                return new OperationAcceptance(OperationAcceptOutcome.IssuedInFuture, null);
            }

            // Unknown id + expired envelope: it may have existed and been pruned, so it is never executed (§4.4 item 6).
            if (submission.ExpiresAt <= now)
            {
                return new OperationAcceptance(OperationAcceptOutcome.Expired, null);
            }

            ct.ThrowIfCancellationRequested();
            var entry = new OperationLogEntry
            {
                Type = OperationLogEntryType.Accepted,
                OperationId = submission.OperationId,
                At = now,
                BootId = _options.BootId,
                NodeUid = _options.NodeUid,
                Kind = submission.Kind,
                Target = submission.Target,
                PayloadDigest = digest,
                Payload = redacted,
                IssuedAt = submission.IssuedAt,
                ExpiresAt = submission.ExpiresAt,
                Master = submission.Master,
                State = OperationState.Accepted,
                Stage = OperationStages.Accepted,
            };

            OperationStatus status = AppendAndApply(entry)!.Status;
            return new OperationAcceptance(OperationAcceptOutcome.Accepted, status);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">The log does not know <paramref name="operationId"/>.</exception>
    /// <exception cref="ArgumentException">The update names a terminal state (use <see cref="CompleteAsync"/>).</exception>
    public async Task<OperationStatus> RecordStageAsync(string operationId, OperationStageUpdate update, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        ArgumentNullException.ThrowIfNull(update);
        if (update.State is { } requested && requested.IsTerminal())
        {
            throw new ArgumentException($"Terminal state {requested} must be recorded with CompleteAsync.", nameof(update));
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StoredOperation current = Get(operationId) ?? throw UnknownOperation(operationId);

            // A late stage or progress report after the operation ended changes nothing.
            if (current.Status.State.IsTerminal())
            {
                return current.Status;
            }

            OperationState? newState = update.State;
            if (newState is null && update.Stage is not null && current.Status.State == OperationState.Accepted)
            {
                newState = OperationState.Running;
            }

            if (newState == current.Status.State)
            {
                newState = null;
            }

            ct.ThrowIfCancellationRequested();
            var entry = new OperationLogEntry
            {
                Type = OperationLogEntryType.Stage,
                OperationId = operationId,
                At = _time.GetUtcNow(),
                BootId = _options.BootId,
                State = newState,
                Stage = update.Stage,
                Progress = update.Progress,
                Reservation = update.Reservation,
            };

            return AppendAndApply(entry)!.Status;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">The log does not know <paramref name="operationId"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="state"/> is not terminal.</exception>
    public async Task<OperationStatus> CompleteAsync(string operationId, OperationState state, AgentError? failure = null, JsonElement? result = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        if (!state.IsTerminal())
        {
            throw new ArgumentException($"{state} is not a terminal state.", nameof(state));
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StoredOperation current = Get(operationId) ?? throw UnknownOperation(operationId);
            if (current.Status.State.IsTerminal())
            {
                return current.Status;
            }

            ct.ThrowIfCancellationRequested();
            var entry = new OperationLogEntry
            {
                Type = OperationLogEntryType.Completed,
                OperationId = operationId,
                At = _time.GetUtcNow(),
                BootId = _options.BootId,
                State = state,
                Stage = state == OperationState.Succeeded
                    ? OperationStages.Completed
                    : current.Status.Stage ?? OperationStages.Accepted,
                Error = failure,
                Result = result is { ValueKind: not JsonValueKind.Undefined } r ? r.Clone() : null,
            };

            return AppendAndApply(entry)!.Status;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task CompactAsync(CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FileStream stream = EnsureWritable();

            byte[] covered;
            try
            {
                covered = new byte[_committedLength];
                stream.Position = 0;
                stream.ReadExactly(covered);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Unavailable("reading the log for compaction failed", ex);
            }

            DateTimeOffset now = _time.GetUtcNow();
            List<(StoredOperation Op, long Seq)> all;
            lock (_stateLock)
            {
                all = _operations.Values.Select(o => (o, _sequence[o.Status.Id])).OrderBy(x => x.Item2).ToList();
            }

            var newestTerminal = all
                .Where(x => x.Op.Status.State.IsTerminal())
                .OrderByDescending(x => x.Op.Status.EndedAt ?? x.Op.Status.StartedAt)
                .ThenByDescending(x => x.Seq)
                .Take(_options.MinRetainedRecords)
                .Select(x => x.Op.Status.Id)
                .ToHashSet(StringComparer.Ordinal);

            List<StoredOperation> kept = all
                .Where(x => !x.Op.Status.State.IsTerminal() ||
                            newestTerminal.Contains(x.Op.Status.Id) ||
                            now < x.Op.Status.ExpiresAt + OperationRetention.RetentionMargin)
                .Select(x => x.Op)
                .ToList();

            var snapshot = new OperationLogSnapshot
            {
                CompactedAt = now,
                CoveredLogBytes = covered.LongLength,
                CoveredLogSha256 = Convert.ToBase64String(SHA256.HashData(covered)),
                Operations = kept,
            };

            WriteSnapshot(snapshot);

            // The snapshot is durable; only now drop the log lines it contains.
            try
            {
                stream.SetLength(0);
                stream.Flush(flushToDisk: true);
                _committedLength = 0;
                _needsRepair = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Safe: the next load skips the prefix the snapshot covers. Cut back on the next write.
                _needsRepair = true;
                throw Unavailable("truncating the log after compaction failed", ex);
            }

            var keptIds = kept.Select(o => o.Status.Id).ToHashSet(StringComparer.Ordinal);
            int pruned;
            lock (_stateLock)
            {
                List<string> drop = _operations.Keys.Where(id => !keptIds.Contains(id)).ToList();
                foreach (string id in drop)
                {
                    _operations.Remove(id);
                    _sequence.Remove(id);
                }

                pruned = drop.Count;
            }

            int active = kept.Count(o => !o.Status.State.IsTerminal());
            OperationLogMessages.Compacted(_logger, kept.Count, active, pruned);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- IOperationLog: reads --------------------------------------------------------------------------------------

    /// <inheritdoc />
    public OperationStatus? Find(string operationId) => Get(operationId)?.Status;

    /// <inheritdoc />
    public IReadOnlyList<OperationStatus> ListActive() =>
        Snapshot(o => !o.Status.State.IsTerminal()).Select(x => x.Op.Status).ToList();

    /// <inheritdoc />
    public IReadOnlyList<OperationStatus> ListRecent(int max)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        return Snapshot(o => o.Status.State.IsTerminal())
            .OrderByDescending(x => x.Op.Status.EndedAt ?? x.Op.Status.StartedAt)
            .ThenByDescending(x => x.Seq)
            .Take(max)
            .Select(x => x.Op.Status)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<RecoverableOperation> ListRecoverable() =>
        Snapshot(o => !o.Status.State.IsTerminal())
            .Select(x => new RecoverableOperation
            {
                Status = x.Op.Status,
                Master = x.Op.Master,
                Payload = x.Op.Payload,
                Reservation = x.Op.Reservation,
            })
            .ToList();

    // ---- IHealthContributor ----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<HealthReason>> CheckAsync(CancellationToken ct = default)
    {
        if (_unavailableReason is null)
        {
            return [];
        }

        if (Volatile.Read(ref _disposeStarted) == 1)
        {
            return [new HealthReason(HealthReasonCodes.StateUnavailable, "Operation log is closed.")];
        }

        // Try to recover without waiting behind a write in progress.
        if (await _writeLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            try
            {
                if (!_disposed)
                {
                    EnsureWritable();
                }
            }
            catch (OperationLogUnavailableException)
            {
                // Still unavailable; reported below.
            }
            finally
            {
                _writeLock.Release();
            }
        }

        string? reason = _unavailableReason;
        return reason is null ? [] : [new HealthReason(HealthReasonCodes.StateUnavailable, "Operation log is not writable: " + reason)];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 1)
        {
            return;
        }

        _writeLock.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream?.Dispose();
            _stream = null;
        }
        finally
        {
            _writeLock.Release();
        }

        _writeLock.Dispose();
    }

    // ---- Write path ------------------------------------------------------------------------------------------------

    /// <summary>Must hold <see cref="_writeLock"/>. Appends one line durably, then folds it into memory.</summary>
    private StoredOperation? AppendAndApply(OperationLogEntry entry)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(entry, AgentJson.Options);
        byte[] line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = NewLine[0];

        FileStream stream = EnsureWritable();
        try
        {
            stream.Position = _committedLength;
            stream.Write(line);
            stream.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Part of the line may be on disk: cut back to the last complete line before the next append.
            _needsRepair = true;
            CloseStream();
            throw Unavailable("appending to the log failed", ex);
        }

        _committedLength += line.Length;
        lock (_stateLock)
        {
            return Apply(entry);
        }
    }

    /// <summary>Must hold <see cref="_writeLock"/>. Opens the log (creating the directory) and repairs a torn tail.</summary>
    private FileStream EnsureWritable()
    {
        try
        {
            if (_stream is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_options.LogPath)!);
                bool created = !File.Exists(_options.LogPath);
                _stream = new FileStream(_options.LogPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                if (created)
                {
                    DirectorySync.FlushParent(_options.LogPath); // the new file's entry is durable before any acceptance (D45)
                }

                if (_stream.Length < _committedLength)
                {
                    // The file shrank under us (or was replaced): never append after a gap.
                    throw new IOException($"{_options.LogPath} is shorter than the {_committedLength} bytes already committed.");
                }

                if (_stream.Length != _committedLength)
                {
                    _needsRepair = true;
                }
            }

            if (_needsRepair)
            {
                _stream.SetLength(_committedLength);
                _stream.Flush(flushToDisk: true);
                _needsRepair = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            CloseStream();
            throw Unavailable("opening the log failed", ex);
        }

        if (_unavailableReason is not null)
        {
            _unavailableReason = null;
            OperationLogMessages.Recovered(_logger);
        }

        return _stream;
    }

    private void CloseStream()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (IOException)
        {
            // The handle is gone either way.
        }

        _stream = null;
    }

    private OperationLogUnavailableException Unavailable(string what, Exception ex)
    {
        string reason = $"{what}: {ex.Message}";
        _unavailableReason = reason;
        OperationLogMessages.WriteFailed(_logger, reason, ex);
        return new OperationLogUnavailableException($"Operation log unavailable ({reason}).", ex);
    }

    /// <summary>Temp file, flushed to disk, then renamed over <c>operations.json</c> (MN-090 rule).</summary>
    private void WriteSnapshot(OperationLogSnapshot snapshot)
    {
        string path = _options.CompactedPath;
        string tempPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, AgentJson.Options);
                stream.Flush(flushToDisk: true);
            }

            // On Windows (dev/test) a scanner briefly holding the target makes the replace fail; retry. Atomic on Linux.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(20 * attempt);
                }
            }

            // The rename must be durable before the caller truncates the log (D45).
            DirectorySync.FlushParent(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
            throw Unavailable("writing the compacted log failed", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwritten by the next compaction.
        }
    }

    // ---- Load and fold ---------------------------------------------------------------------------------------------

    private void Load()
    {
        OperationLogSnapshot? snapshot = ReadSnapshot();
        if (snapshot is not null)
        {
            foreach (StoredOperation operation in snapshot.Operations)
            {
                if (operation?.Status?.Id is { Length: > 0 } id && operation.Master is not null && !_operations.ContainsKey(id))
                {
                    _operations[id] = operation;
                    _sequence[id] = _nextSequence++;
                }
            }
        }

        byte[] log = File.Exists(_options.LogPath) ? File.ReadAllBytes(_options.LogPath) : [];
        int offset = 0;
        if (snapshot is { CoveredLogBytes: > 0, CoveredLogSha256: { } coveredHash } &&
            snapshot.CoveredLogBytes <= log.Length &&
            Convert.ToBase64String(SHA256.HashData(log.AsSpan(0, (int)snapshot.CoveredLogBytes))) == coveredHash)
        {
            // Crash after the snapshot rename, before the truncation: these lines are already in the snapshot.
            offset = (int)snapshot.CoveredLogBytes;
        }

        int torn = 0;
        int orphans = 0;
        int position = offset;
        while (position < log.Length)
        {
            int newline = Array.IndexOf(log, NewLine[0], position);
            if (newline < 0)
            {
                // Torn tail: no newline was written, so the write never completed (and was never acknowledged).
                if (!IsBlank(log.AsSpan(position)))
                {
                    torn++;
                }

                break;
            }

            ReadOnlySpan<byte> line = log.AsSpan(position, newline - position);
            position = newline + 1;
            if (IsBlank(line))
            {
                continue;
            }

            OperationLogEntry? entry = Parse(line);
            if (entry is null)
            {
                torn++;
            }
            else if (Apply(entry) is null)
            {
                orphans++;
            }
        }

        // Everything up to the last complete line stays; a torn tail is cut before the next append.
        _committedLength = position;
        TornLinesIgnored = torn;
        OrphanLinesIgnored = orphans;
        if (torn > 0)
        {
            OperationLogMessages.TornLinesIgnored(_logger, torn, _options.LogPath);
        }

        if (orphans > 0)
        {
            OperationLogMessages.OrphanLinesIgnored(_logger, orphans, _options.LogPath);
        }
    }

    private OperationLogSnapshot? ReadSnapshot()
    {
        if (!File.Exists(_options.CompactedPath))
        {
            return null;
        }

        OperationLogSnapshot? snapshot;
        try
        {
            using var stream = new FileStream(_options.CompactedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            snapshot = JsonSerializer.Deserialize<OperationLogSnapshot>(stream, AgentJson.Options);
        }
        catch (JsonException ex)
        {
            // Written atomically, so this is real corruption: refuse to start rather than forget operations (which
            // could re-execute them).
            throw new InvalidDataException($"{_options.CompactedPath} is not a valid operation log snapshot.", ex);
        }

        if (snapshot is not null && snapshot.SchemaVersion > OperationLogSnapshot.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"{_options.CompactedPath} has schema version {snapshot.SchemaVersion}; this build supports up to " +
                $"{OperationLogSnapshot.CurrentSchemaVersion}. Refusing to read or overwrite it.");
        }

        return snapshot;
    }

    private static OperationLogEntry? Parse(ReadOnlySpan<byte> line)
    {
        try
        {
            OperationLogEntry? entry = JsonSerializer.Deserialize<OperationLogEntry>(line, AgentJson.Options);
            return entry is { OperationId.Length: > 0 } ? entry : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsBlank(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Folds one line into memory (callers hold <see cref="_stateLock"/> or run in the constructor). Returns the new state,
    /// the unchanged state when the line does not apply (duplicate accepted line, change after a terminal state), or null
    /// when the operation is unknown or the accepted line is incomplete.
    /// </summary>
    private StoredOperation? Apply(OperationLogEntry entry)
    {
        _operations.TryGetValue(entry.OperationId, out StoredOperation? current);
        switch (entry.Type)
        {
            case OperationLogEntryType.Accepted:
                if (current is not null)
                {
                    return current;
                }

                if (entry.Kind is null || entry.Target is null || entry.PayloadDigest is null || entry.Master is null ||
                    entry.IssuedAt is null || entry.ExpiresAt is null)
                {
                    return null;
                }

                var accepted = new StoredOperation
                {
                    Status = new OperationStatus
                    {
                        Id = entry.OperationId,
                        Kind = entry.Kind,
                        Target = entry.Target,
                        PayloadDigest = entry.PayloadDigest,
                        State = entry.State ?? OperationState.Accepted,
                        Stage = entry.Stage ?? OperationStages.Accepted,
                        IssuedAt = entry.IssuedAt.Value,
                        ExpiresAt = entry.ExpiresAt.Value,
                        StartedAt = entry.At,
                    },
                    Master = entry.Master,
                    Payload = entry.Payload,
                    BootId = entry.BootId,
                    NodeUid = entry.NodeUid,
                };
                _operations[entry.OperationId] = accepted;
                _sequence[entry.OperationId] = _nextSequence++;
                return accepted;

            case OperationLogEntryType.Stage:
                if (current is null)
                {
                    return null;
                }

                if (current.Status.State.IsTerminal())
                {
                    return current;
                }

                bool stageChanged = entry.Stage is not null && entry.Stage != current.Status.Stage;
                var staged = current with
                {
                    Status = current.Status with
                    {
                        State = entry.State ?? current.Status.State,
                        Stage = entry.Stage ?? current.Status.Stage,

                        // A new stage without progress clears the previous stage's progress.
                        Progress = entry.Progress ?? (stageChanged ? null : current.Status.Progress),
                    },
                    Reservation = entry.Reservation ?? current.Reservation,
                };
                _operations[entry.OperationId] = staged;
                return staged;

            case OperationLogEntryType.Completed:
                if (current is null)
                {
                    return null;
                }

                if (current.Status.State.IsTerminal() || entry.State is not { } final || !final.IsTerminal())
                {
                    return current;
                }

                var completed = current with
                {
                    Status = current.Status with
                    {
                        State = final,
                        Stage = entry.Stage ?? current.Status.Stage,
                        Error = entry.Error,
                        Result = entry.Result,
                        EndedAt = entry.At,
                    },
                    Reservation = null,
                };
                _operations[entry.OperationId] = completed;
                return completed;

            default:
                return null;
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private StoredOperation? Get(string operationId)
    {
        lock (_stateLock)
        {
            return _operations.TryGetValue(operationId, out StoredOperation? operation) ? operation : null;
        }
    }

    private List<(StoredOperation Op, long Seq)> Snapshot(Func<StoredOperation, bool> filter)
    {
        lock (_stateLock)
        {
            return _operations.Values
                .Where(filter)
                .Select(o => (o, _sequence[o.Status.Id]))
                .OrderBy(x => x.Item2)
                .ToList();
        }
    }

    private static KeyNotFoundException UnknownOperation(string operationId) =>
        new($"Operation '{operationId}' is not in the operation log.");
}
