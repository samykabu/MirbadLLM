using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Fencing;

/// <summary>
/// The master's fencing identity (design §4.2 layers 1–3, master side; §11.3).
/// <list type="number">
/// <item><see cref="Open"/> takes <c>{DataPath}/master.lock</c> with <see cref="FileShare.None"/> and keeps it until
/// <see cref="Dispose"/>. On Windows that is a sharing violation for any other open; on Linux .NET takes an exclusive
/// <c>flock</c>, which every other .NET process (and the same process) respects. The lock lives on the volume, so it
/// also covers two pods sharing a <c>ReadWriteOnce</c> PVC on one Kubernetes node. A second master fails with
/// <see cref="MasterLockException"/>. The Linux lock is advisory and .NET ignores <c>flock</c> errors other than
/// "already locked" (and skips locking entirely under <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c>), so after taking it
/// the master proves it by opening the file a second time, which must be refused; if that open succeeds, locking is
/// not effective on this volume and the master refuses to start rather than run without layer 1 (MN-090).</item>
/// <item>It then loads <c>master.json</c> (creating the instance id once), increments the epoch and writes it back
/// atomically and flushed to disk <b>before</b> the object is returned, so no agent call can carry an epoch that is
/// not on disk. Each boot gets a new random boot id.</item>
/// <item>Takeover is only detected here (flag file or option), never started: the takeover service reads the agents'
/// epochs, builds a <see cref="TakeoverRecord"/> (<see cref="TakeoverRules"/>) and calls
/// <see cref="CompleteTakeoverAsync"/>.</item>
/// </list>
/// Register as a singleton and resolve it before the host starts so a second master stops at startup; the container
/// disposes it (releasing the lock) at shutdown.
/// </summary>
public sealed class MasterIdentity : IMasterIdentity, IDisposable, IAsyncDisposable
{
    private readonly string _dataPath;
    private readonly string _statePath;
    private readonly string _flagPath;
    private readonly bool _takeoverOption;
    private readonly string? _takeoverOptionId;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DateTimeOffset _createdAt;

    private FileStream? _lock;
    private MasterTriple _current;
    private IReadOnlyList<TakeoverRecord> _takeovers;
    private bool _takeoverCompleted;

    private MasterIdentity(
        string dataPath, FileStream lockStream, MasterStateDocument state, string bootId, bool takeoverOption,
        string? takeoverOptionId, TimeProvider time, ILogger logger)
    {
        _dataPath = dataPath;
        _statePath = Path.Combine(dataPath, MasterStateDocument.FileName);
        _flagPath = Path.Combine(dataPath, MasterStateDocument.TakeoverFlagFileName);
        _lock = lockStream;
        _takeoverOption = takeoverOption;
        _takeoverOptionId = string.IsNullOrWhiteSpace(takeoverOptionId) ? null : takeoverOptionId.Trim();
        _time = time;
        _logger = logger;
        _createdAt = state.CreatedAt;
        _current = new MasterTriple(state.MasterInstanceId, state.Epoch, bootId);
        _takeovers = state.Takeovers;
    }

    /// <inheritdoc />
    public string InstanceId => _current.InstanceId;

    /// <inheritdoc />
    public long Epoch => Volatile.Read(ref _current).Epoch;

    /// <inheritdoc />
    public string BootId => _current.BootId;

    /// <inheritdoc />
    public MasterTriple Current => Volatile.Read(ref _current);

    /// <summary>The data directory this identity locks.</summary>
    public string DataPath => _dataPath;

    /// <summary>Full path of the takeover flag file (<c>{DataPath}/takeover</c>).</summary>
    public string TakeoverFlagPath => _flagPath;

    /// <inheritdoc />
    /// <remarks>
    /// The flag file is checked on every read, so creating it while the master runs is noticed. The option counts
    /// until a takeover completes in this process, and not at all once <c>master.json</c> holds a record with its
    /// one-shot <see cref="FencingOptions.TakeoverId"/> (MN-115, W1 review MN-090).
    /// </remarks>
    public bool TakeoverRequested => File.Exists(_flagPath) || TakeoverOptionPending;

    /// <summary>True while <c>MirbadLLM:Fencing:Takeover</c> is set (used or not): the UI shows a red warning (MN-090).</summary>
    public bool TakeoverOptionSet => _takeoverOption;

    /// <summary>The option's one-shot <see cref="FencingOptions.TakeoverId"/>, when set.</summary>
    public string? TakeoverOptionId => _takeoverOptionId;

    /// <summary>
    /// True when the option is set and still asks for a takeover: not completed in this process, and its
    /// <see cref="TakeoverOptionId"/> (when any) not yet in <c>master.json</c>.
    /// </summary>
    public bool TakeoverOptionPending =>
        _takeoverOption && !Volatile.Read(ref _takeoverCompleted) && !TakeoverOptionConsumed;

    /// <summary>True when the option's <see cref="TakeoverOptionId"/> names a takeover record already persisted.</summary>
    public bool TakeoverOptionConsumed =>
        _takeoverOptionId is { } id && Takeovers.Any(r => string.Equals(r.TakeoverId, id, StringComparison.Ordinal));

    /// <inheritdoc />
    public IReadOnlyList<TakeoverRecord> Takeovers => Volatile.Read(ref _takeovers);

    /// <summary>
    /// Takes the data directory lock, loads or creates <c>master.json</c>, increments and persists the epoch, and
    /// generates this boot's id. Throws <see cref="MasterLockException"/> when another master holds the lock and
    /// <see cref="InvalidDataException"/> when <c>master.json</c> is unreadable (it is never silently replaced: a new
    /// identity would be refused by every agent).
    /// </summary>
    public static MasterIdentity Open(
        string dataPath, FencingOptions? options = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        TimeProvider time = timeProvider ?? TimeProvider.System;
        ILogger log = logger ?? NullLogger.Instance;
        string fullPath = Path.GetFullPath(dataPath);
        Directory.CreateDirectory(fullPath);

        FileStream lockStream = AcquireLock(
            Path.Combine(fullPath, MasterStateDocument.LockFileName), options?.LockWait ?? FencingOptions.DefaultLockWait, time, log);
        try
        {
            string statePath = Path.Combine(fullPath, MasterStateDocument.FileName);
            DateTimeOffset now = time.GetUtcNow();
            MasterStateDocument? existing = ReadState(statePath);
            MasterStateDocument state = existing ?? new MasterStateDocument
            {
                MasterInstanceId = NewId(),
                Epoch = 0,
                CreatedAt = now,
            };

            if (state.Epoch == long.MaxValue)
            {
                throw new InvalidDataException($"'{statePath}' holds the maximum epoch; it cannot be incremented.");
            }

            state = state with
            {
                SchemaVersion = MasterStateDocument.CurrentSchemaVersion,
                Epoch = state.Epoch + 1,
                UpdatedAt = now,
                Takeovers = state.Takeovers ?? [],
            };
            WriteState(statePath, state);

            var identity = new MasterIdentity(
                fullPath, lockStream, state, NewId(), options?.Takeover ?? false, options?.TakeoverId, time, log);
            if (existing is null)
            {
                FencingLog.Created(log, state.MasterInstanceId, fullPath);
            }

            FencingLog.Booted(log, state.MasterInstanceId, identity.BootId, state.Epoch, fullPath);
            if (File.Exists(identity._flagPath))
            {
                FencingLog.TakeoverRequested(log, identity._flagPath);
            }
            else if (identity.TakeoverOptionConsumed)
            {
                // One-shot (MN-115): the option's takeover already ran on an earlier boot.
                TakeoverLog.OptionAlreadyUsed(log, identity._takeoverOptionId!);
            }
            else if (options?.Takeover == true)
            {
                FencingLog.TakeoverRequested(log, "MirbadLLM__Fencing__Takeover");
            }

            return identity;
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_lock is null, this);
            MasterTriple current = _current;
            if (epoch <= current.Epoch)
            {
                return current;
            }

            PersistUnlocked(epoch, _takeovers);
            return Publish(current, epoch);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A record with a new <see cref="TakeoverRecord.TakeoverId"/> is a new takeover: its epoch must be above the
    /// current one (otherwise it was computed from a stale epoch and <see cref="InvalidOperationException"/> asks the
    /// caller to recompute), it is appended, and the flag file is deleted after the write. A record with a known id
    /// replaces the stored one (a continuation, design §4.2 step 3); the epoch is raised to the highest of
    /// <see cref="TakeoverRecord.Epoch"/> and its continuations' <see cref="TakeoverContinuation.NewEpoch"/>, and the
    /// flag is left alone. The epoch and the record are persisted in one atomic write before anything else changes.
    /// </remarks>
    public async Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.TakeoverId, nameof(record));
        if (record.Epoch < 1)
        {
            throw new ArgumentException("A takeover epoch must be positive.", nameof(record));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        MasterTriple result;
        bool isNew;
        try
        {
            ObjectDisposedException.ThrowIf(_lock is null, this);
            MasterTriple current = _current;
            List<TakeoverRecord> records = [.. _takeovers];
            int index = records.FindIndex(r => string.Equals(r.TakeoverId, record.TakeoverId, StringComparison.Ordinal));
            isNew = index < 0;

            long target = record.Epoch;
            foreach (TakeoverContinuation continuation in record.Continuations ?? [])
            {
                target = Math.Max(target, continuation.NewEpoch);
            }

            if (isNew && target <= current.Epoch)
            {
                throw new InvalidOperationException(
                    $"Takeover epoch {target} is not above the current epoch {current.Epoch}; recompute the takeover.");
            }

            TakeoverRecord stored = record with
            {
                At = record.At == default ? _time.GetUtcNow() : record.At,
                Answered = record.Answered ?? [],
                Unreachable = record.Unreachable ?? [],
                Continuations = record.Continuations ?? [],
            };
            if (isNew)
            {
                records.Add(stored);
            }
            else
            {
                records[index] = stored;
            }

            long epoch = Math.Max(current.Epoch, target);
            PersistUnlocked(epoch, records);
            Volatile.Write(ref _takeovers, records.AsReadOnly());
            result = Publish(current, epoch);
            if (isNew)
            {
                Volatile.Write(ref _takeoverCompleted, true);
            }

            FencingLog.TakeoverCompleted(_logger, stored.TakeoverId, epoch, stored.Answered.Count, stored.Unreachable.Count);
        }
        finally
        {
            _gate.Release();
        }

        if (isNew)
        {
            DeleteFlag();
        }

        return result;
    }

    /// <summary>
    /// Releases the data directory lock. Idempotent. Waits for an in-flight epoch raise or takeover to finish first, so
    /// <c>master.json</c> is never written after the lock is released (MN-090).
    /// </summary>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            Interlocked.Exchange(ref _lock, null)?.Dispose();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private MasterTriple Publish(MasterTriple previous, long epoch)
    {
        if (epoch == previous.Epoch)
        {
            return previous;
        }

        MasterTriple next = previous with { Epoch = epoch };
        Volatile.Write(ref _current, next);
        FencingLog.EpochRaised(_logger, previous.Epoch, epoch);
        return next;
    }

    private void PersistUnlocked(long epoch, IReadOnlyList<TakeoverRecord> takeovers)
    {
        WriteState(_statePath, new MasterStateDocument
        {
            MasterInstanceId = _current.InstanceId,
            Epoch = epoch,
            CreatedAt = _createdAt,
            UpdatedAt = _time.GetUtcNow(),
            Takeovers = takeovers,
        });
    }

    private void DeleteFlag()
    {
        try
        {
            File.Delete(_flagPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FencingLog.FlagDeleteFailed(_logger, ex, _flagPath);
        }
    }

    private static FileStream AcquireLock(string lockPath, TimeSpan wait, TimeProvider time, ILogger log)
    {
        FileStream held;
        long start = time.GetTimestamp();
        bool waited = false;
        while (true)
        {
            try
            {
                held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException ex)
            {
                // The previous master may still be letting go (its process is exiting, or an in-process host is disposing).
                if (time.GetElapsedTime(start) >= wait)
                {
                    throw new MasterLockException(lockPath, ex);
                }

                if (!waited)
                {
                    waited = true;
                    FencingLog.LockBusy(log, lockPath, wait);
                }

                Thread.Sleep(LockRetryInterval);
            }
        }

        if (!LockIsEffective(lockPath))
        {
            held.Dispose();
            throw new MasterLockException(
                $"File locking is not effective on the volume holding '{lockPath}': a second exclusive open of the lock file " +
                "succeeded. The master cannot guarantee it is the only one using this data directory (design §4.2 layer 1). " +
                "Use a local volume that supports flock (not NFS mounted with nolock/local_lock), and do not set " +
                "DOTNET_SYSTEM_IO_DISABLEFILELOCKING.");
        }

        return held;
    }

    private static readonly TimeSpan LockRetryInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// True when a second exclusive open of <paramref name="lockPath"/> is refused while this process holds it. On Windows
    /// that is a sharing violation; on Linux a second <c>flock</c> on a new open file description fails, so the probe
    /// sees exactly what another master would see. Internal for tests.
    /// </summary>
    internal static bool LockIsEffective(string lockPath)
    {
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static MasterStateDocument? ReadState(string statePath)
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        MasterStateDocument? state;
        try
        {
            byte[] bytes = File.ReadAllBytes(statePath);
            state = JsonSerializer.Deserialize<MasterStateDocument>(bytes, MasterStateJson.Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"'{statePath}' is not valid master state ({ex.Message}). Restore it from a backup; it is not " +
                "recreated automatically because a new identity would be refused by every agent.", ex);
        }

        if (state is null)
        {
            throw new InvalidDataException($"'{statePath}' is empty. Restore it from a backup.");
        }

        if (state.SchemaVersion > MasterStateDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"'{statePath}' has schema version {state.SchemaVersion}; this build reads up to " +
                $"{MasterStateDocument.CurrentSchemaVersion}. Run a newer mirbadllm.");
        }

        if (!IsValidId(state.MasterInstanceId) || state.Epoch < 0)
        {
            throw new InvalidDataException(
                $"'{statePath}' has an invalid masterInstanceId or epoch. Restore it from a backup.");
        }

        return state;
    }

    private static void WriteState(string statePath, MasterStateDocument state)
    {
        string temp = statePath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, MasterStateJson.Options);
            stream.Flush(flushToDisk: true);
        }

        // On Windows (dev/test) a scanner briefly holding the target can make the replace fail; retry a few times.
        // On Linux the rename is atomic and succeeds first time.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, statePath, overwrite: true);
                DirectorySync.TryFlushParent(statePath); // a lost rename reverts the epoch: fail-safe, flushed anyway (D69)
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && !id.Contains('/', StringComparison.Ordinal) && !id.Any(char.IsWhiteSpace);

    private static string NewId() => Guid.NewGuid().ToString("N");
}
