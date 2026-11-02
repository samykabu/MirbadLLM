namespace MirbadLLM.Core;

/// <summary>What a managed API key is allowed to reach.</summary>
public enum ApiKeyScope
{
    /// <summary>Grants <c>/v1/*</c> (the OpenAI-compatible router).</summary>
    Inference,

    /// <summary>Grants <c>/metrics</c> only.</summary>
    Metrics,
}

/// <summary>
/// Persisted view of a managed API key. Never holds the plaintext key — only its SHA-256 hash
/// (<see cref="KeyHash"/>, lowercase hex) and a short display prefix (<see cref="Prefix"/>, the first 8
/// characters after <c>ssk_</c>). The full key is only ever available once, at creation, via
/// <see cref="ApiKeyCreated"/>.
/// </summary>
public sealed record ApiKeyRecord(
    string Id,
    string Name,
    ApiKeyScope Scope,
    string KeyHash,
    string Prefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt)
{
    public bool IsActive => RevokedAt is null;
}

/// <summary>Returned once, at creation time: the persisted record plus the full plaintext key.</summary>
public sealed record ApiKeyCreated(ApiKeyRecord Record, string FullKey);

/// <summary>Outcome of <see cref="IApiKeyStore.ImportAsync"/>: records added, and records skipped because a key with
/// the same id or the same hash is already in the store.</summary>
public sealed record ApiKeyImportResult(int Added, int Skipped);

/// <summary>Outcome of <see cref="IApiKeyStore.DeleteAsync"/>.</summary>
public enum ApiKeyDeleteResult
{
    /// <summary>The record existed, was revoked, and has been permanently removed.</summary>
    Deleted,

    /// <summary>No record with that id exists.</summary>
    NotFound,

    /// <summary>The record exists but is still active; it must be revoked first.</summary>
    StillActive,
}

/// <summary>
/// Persistent store of managed <see cref="ApiKeyRecord"/>s. Implementations must never persist a plaintext
/// key — only its hash — and should update <see cref="ApiKeyRecord.LastUsedAt"/> on a successful lookup
/// (throttling the persisted write; the in-memory value may update more often than the file does).
/// </summary>
public interface IApiKeyStore
{
    Task<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken ct = default);

    /// <summary>Generates a new key, persists its hash under <paramref name="name"/> and <paramref name="scope"/>,
    /// and returns the plaintext key alongside the stored record. The plaintext is never retrievable again.</summary>
    Task<ApiKeyCreated> CreateAsync(string name, ApiKeyScope scope, CancellationToken ct = default);

    /// <summary>Marks a key revoked. Returns false when <paramref name="id"/> is unknown or already revoked.</summary>
    Task<bool> RevokeAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Permanently removes a revoked key's record. Refuses (<see cref="ApiKeyDeleteResult.StillActive"/>) while
    /// the key is active — it must be revoked first — and never lowers the store's "ever had a key of this
    /// scope" flag (see <see cref="HasAnyKeyAsync"/>), so deleting every key of a scope does not reopen its gate.
    /// </summary>
    Task<ApiKeyDeleteResult> DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>True when at least one active (non-revoked) key with the given scope exists.</summary>
    Task<bool> HasActiveKeyAsync(ApiKeyScope scope, CancellationToken ct = default);

    /// <summary>
    /// True when a key with the given scope has ever been created — active or revoked. This is the gate
    /// used to decide whether auth is <em>required</em> at all: it is a one-way switch, so revoking your
    /// only key does not silently reopen the endpoint (use <see cref="HasActiveKeyAsync"/> when you need
    /// "is there a currently-usable key", not "has this ever been configured").
    /// </summary>
    Task<bool> HasAnyKeyAsync(ApiKeyScope scope, CancellationToken ct = default);

    /// <summary>
    /// Looks up the active key matching <paramref name="presentedKey"/> using a constant-time hash
    /// comparison. Null when empty, or when no active key matches. On a hit, records the use as
    /// <see cref="ApiKeyRecord.LastUsedAt"/>.
    /// </summary>
    Task<ApiKeyRecord?> FindByPresentedKeyAsync(string presentedKey, CancellationToken ct = default);

    /// <summary>
    /// Adds records taken from a backup as they are (hashes, prefixes, dates, revocation), so keys handed out
    /// before a reinstall keep working. Merges by <see cref="ApiKeyRecord.Id"/>: a record whose id — or whose hash —
    /// is already stored is skipped, never duplicated. Raises the "ever had a key of this scope" flag for every
    /// scope imported (see <see cref="HasAnyKeyAsync"/>).
    /// </summary>
    /// <exception cref="ArgumentException">A record has no id or a hash that is not 64 hex characters.</exception>
    Task<ApiKeyImportResult> ImportAsync(IReadOnlyList<ApiKeyRecord> records, CancellationToken ct = default);
}
