using MirbadLLM.Core;

namespace MirbadLLM.UnitTests.Router.Fakes;

/// <summary>Hand-written fake of <see cref="IApiKeyStore"/>. Bypasses real hashing — keys are looked up by
/// their plaintext value directly — since these tests exercise the router/metrics gating rules, not the
/// storage implementation (see <c>Auth/JsonApiKeyStoreTests</c> for that).</summary>
internal sealed class FakeApiKeyStore : IApiKeyStore
{
    private readonly Dictionary<string, ApiKeyRecord> _byPlainKey = new(StringComparer.Ordinal);
    private readonly List<ApiKeyRecord> _all = [];
    private readonly HashSet<ApiKeyScope> _everCreated = [];

    /// <summary>Adds an active key with the given plaintext value and scope, returning its record.</summary>
    public ApiKeyRecord AddActiveKey(string plainKey, ApiKeyScope scope, string name = "test-key")
    {
        var record = new ApiKeyRecord(
            Guid.NewGuid().ToString("N"), name, scope, "fake-hash",
            plainKey.Length > 8 ? plainKey[..8] : plainKey,
            DateTimeOffset.UtcNow, null, null);
        _byPlainKey[plainKey] = record;
        _all.Add(record);
        _everCreated.Add(scope);
        return record;
    }

    /// <summary>Adds an already-revoked key, for tests of revoked-only actions (e.g. deletion).</summary>
    public ApiKeyRecord AddRevokedKey(string plainKey, ApiKeyScope scope, string name = "test-key")
    {
        var record = AddActiveKey(plainKey, scope, name);
        Revoke(plainKey);
        return _byPlainKey.TryGetValue(plainKey, out var revoked) ? revoked : record;
    }

    /// <summary>Revokes the key previously added with this plaintext value, if any.</summary>
    public void Revoke(string plainKey)
    {
        if (!_byPlainKey.TryGetValue(plainKey, out var record))
        {
            return;
        }

        var revoked = record with { RevokedAt = DateTimeOffset.UtcNow };
        _byPlainKey[plainKey] = revoked;
        int index = _all.FindIndex(r => r.Id == record.Id);
        if (index >= 0)
        {
            _all[index] = revoked;
        }
    }

    public Task<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApiKeyRecord>>(_all.ToArray());

    public Task<ApiKeyCreated> CreateAsync(string name, ApiKeyScope scope, CancellationToken ct = default)
    {
        string plainKey = "ssk_" + Guid.NewGuid().ToString("N");
        var record = AddActiveKey(plainKey, scope, name);
        return Task.FromResult(new ApiKeyCreated(record, plainKey));
    }

    public Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        int index = _all.FindIndex(r => r.Id == id && r.RevokedAt is null);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        var revoked = _all[index] with { RevokedAt = DateTimeOffset.UtcNow };
        _all[index] = revoked;
        foreach (var plain in _byPlainKey.Keys.ToList())
        {
            if (_byPlainKey[plain].Id == id)
            {
                _byPlainKey[plain] = revoked;
            }
        }

        return Task.FromResult(true);
    }

    public Task<ApiKeyDeleteResult> DeleteAsync(string id, CancellationToken ct = default)
    {
        int index = _all.FindIndex(r => r.Id == id);
        if (index < 0)
        {
            return Task.FromResult(ApiKeyDeleteResult.NotFound);
        }

        if (_all[index].IsActive)
        {
            return Task.FromResult(ApiKeyDeleteResult.StillActive);
        }

        var removed = _all[index];
        _all.RemoveAt(index);
        foreach (var plain in _byPlainKey.Where(kv => kv.Value.Id == removed.Id).Select(kv => kv.Key).ToList())
        {
            _byPlainKey.Remove(plain);
        }

        // Mirrors JsonApiKeyStore: HasAnyKeyAsync stays a one-way switch even after the last key of a scope
        // is deleted, so it is tracked separately from _all rather than derived by scanning it.
        _everCreated.Add(removed.Scope);
        return Task.FromResult(ApiKeyDeleteResult.Deleted);
    }

    public Task<bool> HasActiveKeyAsync(ApiKeyScope scope, CancellationToken ct = default)
        => Task.FromResult(_all.Exists(r => r.IsActive && r.Scope == scope));

    public Task<bool> HasAnyKeyAsync(ApiKeyScope scope, CancellationToken ct = default)
        => Task.FromResult(_all.Exists(r => r.Scope == scope) || _everCreated.Contains(scope));

    public Task<ApiKeyRecord?> FindByPresentedKeyAsync(string presentedKey, CancellationToken ct = default)
        => Task.FromResult(_byPlainKey.TryGetValue(presentedKey, out var record) && record.IsActive ? record : null);

    public List<ApiKeyRecord> Imported { get; } = [];

    public Task<ApiKeyImportResult> ImportAsync(IReadOnlyList<ApiKeyRecord> records, CancellationToken ct = default)
    {
        int added = 0;
        int skipped = 0;
        foreach (var record in records)
        {
            if (_all.Exists(r => r.Id == record.Id || r.KeyHash == record.KeyHash))
            {
                skipped++;
                continue;
            }

            _all.Add(record);
            _everCreated.Add(record.Scope);
            Imported.Add(record);
            added++;
        }

        return Task.FromResult(new ApiKeyImportResult(added, skipped));
    }
}
