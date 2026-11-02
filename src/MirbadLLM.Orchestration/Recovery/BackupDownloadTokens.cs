using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MirbadLLM.Orchestration.Recovery;

/// <summary>
/// Hands a backup the Blazor page generated to the browser: the page stores the bytes under a random token and
/// points the browser at <c>/api/backup/download/{token}</c>. A token works once and only for
/// <see cref="Lifetime"/>; nothing is written to disk.
/// </summary>
public sealed class BackupDownloadTokens(TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public string Issue(byte[] content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(fileName);

        var now = timeProvider.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }

        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _entries[token] = new Entry(content, fileName, now + Lifetime);
        return token;
    }

    /// <summary>The file for <paramref name="token"/>, removing it so the token can't be used again. False when unknown or expired.</summary>
    public bool TryRedeem(string? token, out byte[] content, out string fileName)
    {
        content = [];
        fileName = "";
        if (string.IsNullOrEmpty(token) || !_entries.TryRemove(token, out var entry) || entry.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return false;
        }

        content = entry.Content;
        fileName = entry.FileName;
        return true;
    }

    private sealed record Entry(byte[] Content, string FileName, DateTimeOffset ExpiresAt);
}
