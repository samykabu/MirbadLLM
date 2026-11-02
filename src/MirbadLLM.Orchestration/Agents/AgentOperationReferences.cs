using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Agents;

/// <summary>Whether a master operation reference still waits for its agent operation.</summary>
public enum AgentOperationReferenceStatus
{
    /// <summary>Written before the request was sent; the outcome is not known yet. Observed again after a master restart.</summary>
    Open,

    /// <summary>The outcome is known (<see cref="AgentOperationReference.Outcome"/>).</summary>
    Closed,
}

/// <summary>How a master operation reference ended (design §4.4 "Master side").</summary>
public enum AgentOperationOutcome
{
    /// <summary>The agent operation succeeded.</summary>
    Succeeded,

    /// <summary>The agent operation failed (its error is recorded).</summary>
    Failed,

    /// <summary>The agent operation was cancelled.</summary>
    Cancelled,

    /// <summary>The agent could not verify the outcome (host-service actions, design §4.4 item 8).</summary>
    Unknown,

    /// <summary>The agent answered the submission with an HTTP error: nothing was accepted (never retried automatically).</summary>
    Refused,

    /// <summary>The agent no longer knows the id and the envelope expired (410 rule, §4.4 item 6): read the current state.</summary>
    Expired,

    /// <summary>The agent does not know the id while the envelope is valid: the request never arrived (master crash before send).</summary>
    NotAccepted,

    /// <summary>The node was re-enrolled (uid changed), revoked or deleted; the operation cannot be observed any more.</summary>
    Lost,
}

/// <summary>
/// One master-side operation reference in <c>{DataPath}/operations.json</c> (design §4.4 "Master side"), written
/// <b>before</b> the mutation is sent and closed on a terminal status, so a master restart resumes observing
/// (<c>GET /operations/{id}</c>) instead of issuing a replacement. Never holds secrets (the digest is computed with secrets
/// redacted; the body is not stored).
/// </summary>
public sealed record AgentOperationReference
{
    /// <summary>Operation id (the envelope's).</summary>
    public required string OperationId { get; init; }

    /// <summary>Node id.</summary>
    public required string NodeId { get; init; }

    /// <summary>Node uid at submission; a different uid at resume means the node was re-enrolled (closed as <see cref="AgentOperationOutcome.Lost"/>).</summary>
    public required string NodeUid { get; init; }

    /// <summary>Operation kind (<see cref="OperationKinds"/>).</summary>
    public required string Action { get; init; }

    /// <summary>Target: model id, folder, unit, rotation id…</summary>
    public required string Target { get; init; }

    /// <summary>The master's payload digest (<see cref="AgentPayloadDigest"/>).</summary>
    public required string PayloadDigest { get; init; }

    /// <summary>Envelope issue time.</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Envelope expiry.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The job, move, restore or edit record that owns it (design §6.7); null for plain actions.</summary>
    public string? Owner { get; init; }

    /// <summary>When the reference was written.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Open or closed.</summary>
    public AgentOperationReferenceStatus Status { get; init; }

    /// <summary>Outcome once closed.</summary>
    public AgentOperationOutcome? Outcome { get; init; }

    /// <summary>Agent error code or message of a failed, refused or unknown outcome.</summary>
    public string? Error { get; init; }

    /// <summary>When it was closed.</summary>
    public DateTimeOffset? ClosedAt { get; init; }
}

/// <summary><c>{DataPath}/operations.json</c>.</summary>
public sealed record AgentOperationReferenceDocument
{
    /// <summary>Current schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>File name under the data path.</summary>
    public const string FileName = "operations.json";

    /// <summary>Schema version.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>References, oldest first.</summary>
    public IReadOnlyList<AgentOperationReference> References { get; init; } = [];
}

/// <summary>Durable master-side operation references (design §4.4 "Master side").</summary>
public interface IAgentOperationReferenceStore
{
    /// <summary>Persists a new open reference (flushed to disk) before the request is sent. Throws when the id exists.</summary>
    Task AddAsync(AgentOperationReference reference, CancellationToken ct = default);

    /// <summary>The reference with this id, or null.</summary>
    Task<AgentOperationReference?> GetAsync(string operationId, CancellationToken ct = default);

    /// <summary>Open references, oldest first (resumed after a master restart).</summary>
    Task<IReadOnlyList<AgentOperationReference>> ListOpenAsync(CancellationToken ct = default);

    /// <summary>Every retained reference, oldest first.</summary>
    Task<IReadOnlyList<AgentOperationReference>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Closes a reference with its outcome (persisted). Idempotent: an already closed reference is returned unchanged.
    /// Null when the id is unknown.
    /// </summary>
    Task<AgentOperationReference?> CloseAsync(string operationId, AgentOperationOutcome outcome, string? errorCode, CancellationToken ct = default);
}

/// <summary>
/// File-backed <see cref="IAgentOperationReferenceStore"/>: <c>{DataPath}/operations.json</c>, written like
/// <c>nodes.json</c> (temp file, flushed to disk, then moved over the target; one semaphore). Loaded once and kept in memory
/// (the master owns <c>/data</c> exclusively, design §4.2); a failed write changes nothing in memory.
/// <para>Retention: open references are never pruned; a closed one is kept until its envelope expiry plus the retention
/// margin, and at most <see cref="MaxClosedReferences"/> closed ones (newest kept).</para>
/// </summary>
public sealed class JsonAgentOperationReferenceStore : IAgentOperationReferenceStore, IDisposable
{
    /// <summary>Closed references kept at most.</summary>
    public const int MaxClosedReferences = 1_000;

    private readonly string _filePath;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<AgentOperationReference>? _references;

    public JsonAgentOperationReferenceStore(IOptions<MirbadLLMOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _filePath = Path.Combine(options.Value.DataPath, AgentOperationReferenceDocument.FileName);
        _time = timeProvider;
    }

    public void Dispose() => _lock.Dispose();

    public async Task AddAsync(AgentOperationReference reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentException.ThrowIfNullOrEmpty(reference.OperationId);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<AgentOperationReference> current = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            if (current.Any(r => r.OperationId == reference.OperationId))
            {
                throw new InvalidOperationException($"Operation reference '{reference.OperationId}' already exists; operation ids are never reused.");
            }

            AgentOperationReference stored = reference with
            {
                Status = AgentOperationReferenceStatus.Open,
                Outcome = null,
                ClosedAt = null,
                CreatedAt = reference.CreatedAt == default ? _time.GetUtcNow() : reference.CreatedAt,
            };
            await WriteUnlockedAsync([.. current, stored], ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AgentOperationReference?> GetAsync(string operationId, CancellationToken ct = default)
    {
        IReadOnlyList<AgentOperationReference> all = await ListAsync(ct).ConfigureAwait(false);
        return all.FirstOrDefault(r => r.OperationId == operationId);
    }

    public async Task<IReadOnlyList<AgentOperationReference>> ListOpenAsync(CancellationToken ct = default)
    {
        IReadOnlyList<AgentOperationReference> all = await ListAsync(ct).ConfigureAwait(false);
        return [.. all.Where(r => r.Status == AgentOperationReferenceStatus.Open)];
    }

    public async Task<IReadOnlyList<AgentOperationReference>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return [.. await LoadUnlockedAsync(ct).ConfigureAwait(false)];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AgentOperationReference?> CloseAsync(string operationId, AgentOperationOutcome outcome, string? errorCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(operationId);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<AgentOperationReference> current = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            int index = current.FindIndex(r => r.OperationId == operationId);
            if (index < 0)
            {
                return null;
            }

            AgentOperationReference existing = current[index];
            if (existing.Status == AgentOperationReferenceStatus.Closed)
            {
                return existing;
            }

            AgentOperationReference closed = existing with
            {
                Status = AgentOperationReferenceStatus.Closed,
                Outcome = outcome,
                Error = errorCode,
                ClosedAt = _time.GetUtcNow(),
            };
            List<AgentOperationReference> next = [.. current];
            next[index] = closed;
            await WriteUnlockedAsync(next, ct).ConfigureAwait(false);
            return closed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Must hold <see cref="_lock"/>.</summary>
    private async Task<List<AgentOperationReference>> LoadUnlockedAsync(CancellationToken ct)
    {
        if (_references is not null)
        {
            return _references;
        }

        if (!File.Exists(_filePath))
        {
            _references = [];
            return _references;
        }

        AgentOperationReferenceDocument? document;
        await using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
        {
            document = await JsonSerializer.DeserializeAsync<AgentOperationReferenceDocument>(stream, MasterStateJson.Options, ct).ConfigureAwait(false);
        }

        if (document is not null && document.SchemaVersion > AgentOperationReferenceDocument.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"{AgentOperationReferenceDocument.FileName} has schema version {document.SchemaVersion}; this build supports up to " +
                $"{AgentOperationReferenceDocument.CurrentSchemaVersion}. Refusing to read or overwrite it.");
        }

        _references = [.. (document?.References ?? []).Where(r => r is not null)];
        return _references;
    }

    /// <summary>Must hold <see cref="_lock"/>. Prunes, writes temp + flush + move, then publishes in memory.</summary>
    private async Task WriteUnlockedAsync(List<AgentOperationReference> references, CancellationToken ct)
    {
        List<AgentOperationReference> retained = Prune(references, _time.GetUtcNow());
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, new AgentOperationReferenceDocument { References = retained }, MasterStateJson.Options, ct).ConfigureAwait(false);

            // To the disk before the rename: the reference must survive a crash right after the request is sent (§4.4).
            stream.Flush(flushToDisk: true);
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, _filePath, overwrite: true);
                DirectorySync.TryFlushParent(_filePath); // best effort (D69)
                break;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(20 * attempt, ct).ConfigureAwait(false);
            }
        }

        _references = retained;
    }

    private static List<AgentOperationReference> Prune(List<AgentOperationReference> references, DateTimeOffset now)
    {
        var keepClosed = references
            .Where(r => r.Status == AgentOperationReferenceStatus.Closed && r.ExpiresAt + OperationRetention.RetentionMargin > now)
            .OrderByDescending(r => r.ClosedAt ?? r.CreatedAt)
            .Take(MaxClosedReferences)
            .Select(r => r.OperationId)
            .ToHashSet(StringComparer.Ordinal);
        return [.. references.Where(r => r.Status == AgentOperationReferenceStatus.Open || keepClosed.Contains(r.OperationId))];
    }
}
