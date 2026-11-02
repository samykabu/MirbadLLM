using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration;

/// <summary>
/// File-backed <see cref="ITokenStore"/>: <c>{DataPath}/tokens.json</c>, written atomically (temp file + move) under a
/// <see cref="SemaphoreSlim"/>. Each value is encrypted with ASP.NET Data Protection, whose keys the master keeps in
/// <c>{DataPath}/dataprotection-keys</c>; a copy of <c>tokens.json</c> alone cannot be read. A backup carries the
/// values in its passphrase-encrypted secrets instead (<see cref="ExportAsync"/>), so a restore onto a new master works.
/// </summary>
public sealed class JsonTokenStore : ITokenStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General) { WriteIndented = true };

    private readonly string _filePath;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<StoredToken>? _tokens;

    public JsonTokenStore(IOptions<MirbadLLMOptions> options, IDataProtectionProvider protection, TimeProvider timeProvider)
    {
        _filePath = Path.Combine(options.Value.DataPath, "tokens.json");
        _protector = protection.CreateProtector(LegacyProtectionPurposes.SavedTokens);
        _timeProvider = timeProvider;
    }

    public void Dispose() => _lock.Dispose();

    public Task<IReadOnlyList<SavedToken>> ListAsync(CancellationToken ct = default) =>
        LockedAsync<IReadOnlyList<SavedToken>>(tokens => tokens.Select(ToPublic).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList(), ct);

    public async Task<SavedToken> AddAsync(string name, string value, CancellationToken ct = default)
    {
        string trimmedName = RequireName(name);
        string trimmedValue = string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Paste the token.", nameof(value)) : value.Trim();
        return await LockedAsync(async tokens =>
        {
            RequireFreeName(tokens, trimmedName, exceptId: null);
            var now = _timeProvider.GetUtcNow();
            var token = new StoredToken
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = trimmedName,
                Hint = HintOf(trimmedValue),
                Protected = _protector.Protect(trimmedValue),
                CreatedAt = now,
                UpdatedAt = now,
            };
            tokens.Add(token);
            await FlushAsync(tokens, ct).ConfigureAwait(false);
            return ToPublic(token);
        }, ct).ConfigureAwait(false);
    }

    public async Task<SavedToken?> UpdateAsync(string id, string name, string? value, CancellationToken ct = default)
    {
        string trimmedName = RequireName(name);
        return await LockedAsync(async tokens =>
        {
            int index = tokens.FindIndex(t => t.Id == id);
            if (index < 0)
            {
                return null;
            }

            RequireFreeName(tokens, trimmedName, exceptId: id);
            var token = tokens[index];
            token.Name = trimmedName;
            if (!string.IsNullOrWhiteSpace(value))
            {
                token.Protected = _protector.Protect(value.Trim());
                token.Hint = HintOf(value.Trim());
            }

            token.UpdatedAt = _timeProvider.GetUtcNow();
            await FlushAsync(tokens, ct).ConfigureAwait(false);
            return ToPublic(token);
        }, ct).ConfigureAwait(false);
    }

    public Task<bool> DeleteAsync(string id, CancellationToken ct = default) =>
        LockedAsync(async tokens =>
        {
            if (tokens.RemoveAll(t => t.Id == id) == 0)
            {
                return false;
            }

            await FlushAsync(tokens, ct).ConfigureAwait(false);
            return true;
        }, ct);

    public Task<string?> GetValueAsync(string id, CancellationToken ct = default) =>
        LockedAsync(tokens => tokens.Find(t => t.Id == id) is { } token ? Unprotect(token) : null, ct);

    public Task<IReadOnlyList<SavedTokenSecret>> ExportAsync(CancellationToken ct = default) =>
        LockedAsync<IReadOnlyList<SavedTokenSecret>>(tokens => tokens
            .Select(t => new SavedTokenSecret { Id = t.Id, Name = t.Name, Value = Unprotect(t), CreatedAt = t.CreatedAt })
            .ToList(), ct);

    public Task<SavedTokenImportResult> ImportAsync(IReadOnlyList<SavedTokenSecret> tokens, CancellationToken ct = default) =>
        LockedAsync(async stored =>
        {
            int added = 0;
            foreach (var secret in tokens)
            {
                string name = secret.Name?.Trim() ?? "";
                if (name.Length == 0 || string.IsNullOrWhiteSpace(secret.Value) || stored.Exists(t => t.Id == secret.Id || NameEquals(t.Name, name)))
                {
                    continue;
                }

                var now = _timeProvider.GetUtcNow();
                stored.Add(new StoredToken
                {
                    Id = string.IsNullOrWhiteSpace(secret.Id) ? Guid.NewGuid().ToString("N") : secret.Id,
                    Name = name,
                    Hint = HintOf(secret.Value.Trim()),
                    Protected = _protector.Protect(secret.Value.Trim()),
                    CreatedAt = secret.CreatedAt == default ? now : secret.CreatedAt,
                    UpdatedAt = now,
                });
                added++;
            }

            if (added > 0)
            {
                await FlushAsync(stored, ct).ConfigureAwait(false);
            }

            return new SavedTokenImportResult(added, tokens.Count - added);
        }, ct);

    /// <summary>The last four characters, enough to tell tokens apart without revealing them.</summary>
    internal static string HintOf(string value) => value.Length <= 8 ? "" : value[^4..];

    private string Unprotect(StoredToken token)
    {
        try
        {
            return _protector.Unprotect(token.Protected);
        }
        catch (CryptographicException ex)
        {
            // The Data Protection keys were lost or replaced (a new data folder): the value cannot be recovered here.
            throw new InvalidOperationException(
                $"The saved token '{token.Name}' can no longer be decrypted on this master. Replace its value under Access > Tokens.", ex);
        }
    }

    private static string RequireName(string name) =>
        string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("Give the token a name.", nameof(name)) : name.Trim();

    private static void RequireFreeName(List<StoredToken> tokens, string name, string? exceptId)
    {
        if (tokens.Exists(t => t.Id != exceptId && NameEquals(t.Name, name)))
        {
            throw new ArgumentException($"A token named '{name}' already exists.", nameof(name));
        }
    }

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static SavedToken ToPublic(StoredToken t) => new(t.Id, t.Name, t.Hint, t.CreatedAt, t.UpdatedAt);

    private Task<T> LockedAsync<T>(Func<List<StoredToken>, T> action, CancellationToken ct) =>
        LockedAsync(tokens => Task.FromResult(action(tokens)), ct);

    private async Task<T> LockedAsync<T>(Func<List<StoredToken>, Task<T>> action, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _tokens ??= await LoadAsync(ct).ConfigureAwait(false);
            return await action(_tokens).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<StoredToken>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        await using var stream = File.OpenRead(_filePath);
        var file = await JsonSerializer.DeserializeAsync<StoredFile>(stream, JsonOptions, ct).ConfigureAwait(false);
        return file?.Tokens ?? [];
    }

    private async Task FlushAsync(List<StoredToken> tokens, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        string tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, new StoredFile { Tokens = tokens }, JsonOptions, ct).ConfigureAwait(false);
        }

        // As in JsonApiKeyStore: on Windows a scanner briefly holding the target makes the replace fail; retry a few times.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tempPath, _filePath, overwrite: true);
                DirectorySync.TryFlushParent(_filePath);
                break;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(20 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class StoredFile
    {
        public List<StoredToken> Tokens { get; set; } = [];
    }

    private sealed class StoredToken
    {
        public string Id { get; set; } = "";

        public string Name { get; set; } = "";

        public string Hint { get; set; } = "";

        /// <summary>The value, encrypted with Data Protection.</summary>
        public string Protected { get; set; } = "";

        public DateTimeOffset CreatedAt { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
