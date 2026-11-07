using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// File-backed <see cref="IApiKeyStore"/>: <c>{DataPath}/apikeys.json</c>, atomically written (temp file +
/// move) and guarded by a <see cref="SemaphoreSlim"/>. Unlike <see cref="JsonModelRegistry"/>, keys are kept
/// in an in-memory cache after the first load — required so <see cref="ApiKeyRecord.LastUsedAt"/> can be
/// updated on every hit without a disk write on every hit (writes are throttled to once a minute).
///
/// The file is an object with an <c>everConfiguredScopes</c> set and the original inference/metrics booleans.
/// The generalized set backs <see cref="HasAnyKeyAsync"/> for every scope and is never cleared, so deleting
/// every key of a scope (see <see cref="DeleteAsync"/>) cannot silently reopen its gate. The original fields
/// remain for downgrade compatibility. A pre-existing bare array or two-boolean object is migrated on load.
/// </summary>
public sealed class JsonApiKeyStore : IApiKeyStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly TimeSpan LastUsedFlushInterval = TimeSpan.FromMinutes(1);
    private const string KeyPrefix = "ssk_";

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JsonApiKeyStore> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private List<ApiKeyRecord> _keys = [];
    private readonly HashSet<ApiKeyScope> _everConfiguredScopes = [];
    private bool _initialized;
    private DateTimeOffset _lastFlushedAt;

    public JsonApiKeyStore(IOptions<MirbadLLMOptions> options, TimeProvider timeProvider, ILogger<JsonApiKeyStore> logger)
    {
        _filePath = Path.Combine(options.Value.DataPath, "apikeys.json");
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public void Dispose() => _lock.Dispose();

    public async Task<IReadOnlyList<ApiKeyRecord>> ListAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            return _keys.ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApiKeyCreated> CreateAsync(string name, ApiKeyScope scope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name must not be empty.", nameof(name));
        }

        string fullKey = GenerateKey();
        var record = new ApiKeyRecord(
            Id: Guid.NewGuid().ToString("N"),
            Name: name.Trim(),
            Scope: scope,
            KeyHash: Hash(fullKey),
            Prefix: DisplayPrefix(fullKey),
            CreatedAt: _timeProvider.GetUtcNow(),
            LastUsedAt: null,
            RevokedAt: null);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            _keys.Add(record);
            _everConfiguredScopes.Add(scope);

            await FlushUnlockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        return new ApiKeyCreated(record, fullKey);
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            int index = _keys.FindIndex(k => k.Id == id);
            if (index < 0 || _keys[index].RevokedAt is not null)
            {
                return false;
            }

            _keys[index] = _keys[index] with { RevokedAt = _timeProvider.GetUtcNow() };
            await FlushUnlockedAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApiKeyDeleteResult> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            int index = _keys.FindIndex(k => k.Id == id);
            if (index < 0)
            {
                return ApiKeyDeleteResult.NotFound;
            }

            var record = _keys[index];
            if (record.IsActive)
            {
                return ApiKeyDeleteResult.StillActive;
            }

            _keys.RemoveAt(index);
            await FlushUnlockedAsync(ct).ConfigureAwait(false);
            ApiKeyLog.Deleted(_logger, record.Name, record.Id, record.Scope);
            return ApiKeyDeleteResult.Deleted;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> HasActiveKeyAsync(ApiKeyScope scope, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            return _keys.Exists(k => k.IsActive && k.Scope == scope);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> HasAnyKeyAsync(ApiKeyScope scope, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            return _everConfiguredScopes.Contains(scope);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApiKeyRecord?> FindByPresentedKeyAsync(string presentedKey, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return null;
        }

        var presentedHashBytes = Convert.FromHexString(Hash(presentedKey));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);

            int index = -1;
            for (int i = 0; i < _keys.Count; i++)
            {
                if (!_keys[i].IsActive)
                {
                    continue;
                }

                if (CryptographicOperations.FixedTimeEquals(Convert.FromHexString(_keys[i].KeyHash), presentedHashBytes))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return null;
            }

            var now = _timeProvider.GetUtcNow();
            _keys[index] = _keys[index] with { LastUsedAt = now };

            if (now - _lastFlushedAt >= LastUsedFlushInterval)
            {
                await FlushUnlockedAsync(ct).ConfigureAwait(false);
            }

            return _keys[index];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ApiKeyImportResult> ImportAsync(IReadOnlyList<ApiKeyRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Id) || record.KeyHash is not { Length: 64 } || !record.KeyHash.All(Uri.IsHexDigit))
            {
                throw new ArgumentException($"API key record '{record.Id}' is invalid: it needs an id and a 64-character hex hash.", nameof(records));
            }
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(ct).ConfigureAwait(false);
            int added = 0;
            int skipped = 0;
            foreach (var record in records)
            {
                if (_keys.Exists(k => k.Id == record.Id || string.Equals(k.KeyHash, record.KeyHash, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                _keys.Add(record with { KeyHash = record.KeyHash.ToLowerInvariant() });
                added++;
                _everConfiguredScopes.Add(record.Scope);
            }

            if (added > 0)
            {
                await FlushUnlockedAsync(ct).ConfigureAwait(false);
                ApiKeyLog.Imported(_logger, added, skipped);
            }

            return new ApiKeyImportResult(added, skipped);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Must be called while holding <see cref="_lock"/>. Loads the file into <see cref="_keys"/> once,
    /// migrating a pre-existing bare-array file into the flagged object format.</summary>
    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bool needsMigrationFlush = false;
        if (File.Exists(_filePath))
        {
            string raw = await File.ReadAllTextAsync(_filePath, ct).ConfigureAwait(false);
            var node = JsonNode.Parse(raw);
            if (node is JsonArray)
            {
                // Legacy format: a bare array of records. Derive the "ever created" flags from what is
                // present (a revoked-only file still counts — the key existed) and migrate on next flush.
                _keys = JsonSerializer.Deserialize<List<ApiKeyRecord>>(raw, JsonOptions) ?? [];
                _everConfiguredScopes.UnionWith(_keys.Select(k => k.Scope));
                needsMigrationFlush = true;
            }
            else if (node is JsonObject obj)
            {
                _keys = obj["Keys"]?.Deserialize<List<ApiKeyRecord>>(JsonOptions) ?? [];
                var configured = obj["EverConfiguredScopes"]?.Deserialize<List<ApiKeyScope>>(JsonOptions) ?? [];
                _everConfiguredScopes.UnionWith(configured);
                if (obj["InferenceKeyEverCreated"]?.GetValue<bool>() == true)
                {
                    _everConfiguredScopes.Add(ApiKeyScope.Inference);
                }
                if (obj["MetricsKeyEverCreated"]?.GetValue<bool>() == true)
                {
                    _everConfiguredScopes.Add(ApiKeyScope.Metrics);
                }
                // A partially migrated document remains fail-closed for every scope represented by a record.
                _everConfiguredScopes.UnionWith(_keys.Select(k => k.Scope));
            }
            else
            {
                _keys = [];
            }
        }
        else
        {
            _keys = [];
        }

        _initialized = true;
        _lastFlushedAt = _timeProvider.GetUtcNow();

        if (needsMigrationFlush)
        {
            await FlushUnlockedAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Must be called while holding <see cref="_lock"/>.</summary>
    private async Task FlushUnlockedAsync(CancellationToken ct)
    {
        var payload = new StoredFile
        {
            // Keep the original fields so older MirbadLLM versions can still read their two scopes.
            InferenceKeyEverCreated = _everConfiguredScopes.Contains(ApiKeyScope.Inference),
            MetricsKeyEverCreated = _everConfiguredScopes.Contains(ApiKeyScope.Metrics),
            EverConfiguredScopes = _everConfiguredScopes.OrderBy(scope => scope).ToList(),
            Keys = _keys,
        };

        string tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, payload, JsonOptions, ct).ConfigureAwait(false);
        }

        // On Windows (dev/test) a reader or scanner briefly holding the target makes the replace fail with
        // access denied; retry a few times. On Linux the rename is atomic and succeeds first time.
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

        _lastFlushedAt = _timeProvider.GetUtcNow();
    }

    internal static string GenerateKey() => KeyPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    internal static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    internal static string DisplayPrefix(string key)
    {
        string body = key.StartsWith(KeyPrefix, StringComparison.Ordinal) ? key[KeyPrefix.Length..] : key;
        return body.Length <= 8 ? body : body[..8];
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class StoredFile
    {
        public bool InferenceKeyEverCreated { get; set; }

        public bool MetricsKeyEverCreated { get; set; }

        public List<ApiKeyScope> EverConfiguredScopes { get; set; } = [];

        public List<ApiKeyRecord> Keys { get; set; } = [];
    }
}

internal static partial class ApiKeyLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "API keys: deleted revoked key '{Name}' ({Id}, {Scope} scope)")]
    public static partial void Deleted(ILogger logger, string name, string id, ApiKeyScope scope);

    [LoggerMessage(Level = LogLevel.Information, Message = "API keys: imported {Added} key records from a backup ({Skipped} already present)")]
    public static partial void Imported(ILogger logger, int added, int skipped);
}
