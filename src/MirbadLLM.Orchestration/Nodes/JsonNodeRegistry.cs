using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;

namespace MirbadLLM.Orchestration.Nodes;

/// <summary>
/// File-backed <see cref="INodeRegistry"/>: <c>{DataPath}/nodes.json</c> (design §5.3), handled like
/// <see cref="JsonModelRegistry"/>: one <see cref="SemaphoreSlim"/> guards every read and write, and every write goes to a
/// temp file that is then moved over the target. The file is read on every call (no cache), so a failed write leaves
/// nothing changed in memory and the file stays the single source of truth.
/// <para>Join tokens (design §3.2): 32 random bytes, base64url; only the base64url SHA-256 of the token's UTF-8 bytes is
/// stored. <see cref="TryConsumeJoinTokenAsync"/> compares hashes in constant time under the semaphore and persists
/// <c>usedAt</c> before returning, so one token issues at most one certificate.</para>
/// <para>A missing file is an empty registry and is not created by reads. A file with a newer schema version, or one
/// that does not parse, makes every call throw rather than risk overwriting it.</para>
/// </summary>
public sealed class JsonNodeRegistry : INodeRegistry, IDisposable
{
    /// <summary>
    /// Shortest join-token lifetime accepted (design §3.2: configurable 10 min – 24 h). MN-102's options validation uses
    /// this constant; the registry enforces it too (MN-090), so no caller can mint a shorter- or longer-lived token.
    /// </summary>
    public static readonly TimeSpan MinJoinTokenLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Longest join-token lifetime accepted (design §3.2: configurable 10 min – 24 h).</summary>
    public static readonly TimeSpan MaxJoinTokenLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// The one rekey eligibility rule (design §9.4 step 5, §10.2 item 6, D73/D78): why <paramref name="node"/> cannot be
    /// re-paired with <c>join --rekey</c>, or null when it can. A node can be re-paired when it exists, is not revoked and has
    /// enrolled, i.e. holds a pin: the current one (approved; the rekey keeps the approval) or the pending one (enrolled,
    /// still awaiting approval; the rekey keeps it pending, MN-114). Used where a rekey token is issued
    /// (<see cref="CreateJoinTokenAsync"/>, under the lock, and the node API's <c>rekey: true</c>, MN-102) and where it is
    /// redeemed (<c>/enroll/rekey</c>, MN-114, which also checks the node uid), so the three never disagree.
    /// </summary>
    public static string? RekeyRefusal(NodeRecord? node) => node switch
    {
        null => "unknown node",
        { Revoked: true } => "node revoked",
        { CertSpkiSha256: null, PendingCertSpkiSha256: null } => "node never enrolled",
        _ => null,
    };

    private const int TokenBytes = 32;
    private const int HashBytes = 32;

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public JsonNodeRegistry(IOptions<MirbadLLMOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _filePath = Path.Combine(options.Value.DataPath, NodeRegistryDocument.FileName);
        _timeProvider = timeProvider;
    }

    public void Dispose() => _lock.Dispose();

    public async Task<IReadOnlyList<NodeRecord>> ListAsync(CancellationToken ct = default)
    {
        var document = await ReadAsync(ct).ConfigureAwait(false);
        return document.Nodes;
    }

    public async Task<NodeRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        var document = await ReadAsync(ct).ConfigureAwait(false);
        return document.Nodes.FirstOrDefault(n => n.Id == id);
    }

    public async Task UpsertAsync(NodeRecord node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        node = Normalize(node);
        Validate(node);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var existing = document.Nodes.FirstOrDefault(n => n.Id == node.Id);
            EnsureNotUnrevoked(existing, node);
            if (node.CreatedAt == default)
            {
                node = node with { CreatedAt = existing?.CreatedAt ?? _timeProvider.GetUtcNow() };
            }

            await WriteUnlockedAsync(document with { Nodes = Replace(document.Nodes, node) }, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<NodeRecord?> UpdateAsync(string id, Func<NodeRecord, NodeRecord> update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var existing = document.Nodes.FirstOrDefault(n => n.Id == id);
            if (existing is null)
            {
                return null;
            }

            var updated = update(existing) ?? throw new InvalidOperationException("The node update returned null.");
            if (updated.Id != existing.Id)
            {
                throw new ArgumentException($"A node update may not change the id ('{existing.Id}' -> '{updated.Id}').", nameof(update));
            }

            updated = Normalize(updated);
            Validate(updated);
            EnsureNotUnrevoked(existing, updated);
            await WriteUnlockedAsync(document with { Nodes = Replace(document.Nodes, updated) }, ct).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Marks the node revoked, deletes its pins and, in the same locked write (MN-090), expires every unused join token
    /// bound to it, so a token issued before the revoke can no longer enroll the node again.
    /// </summary>
    public async Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var existing = document.Nodes.FirstOrDefault(n => n.Id == id);
            if (existing is null)
            {
                return false;
            }

            var now = _timeProvider.GetUtcNow();
            var revoked = existing with
            {
                Revoked = true,
                RevokedAt = existing.RevokedAt ?? now,
                CertSpkiSha256 = null,
                PendingCertSpkiSha256 = null,
            };
            await WriteUnlockedAsync(
                document with { Nodes = Replace(document.Nodes, revoked), JoinTokens = BurnUnused(document.JoinTokens, id, now) },
                ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Removes the record and, in the same locked write (MN-090), expires the node's unused join tokens.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            if (!document.Nodes.Any(n => n.Id == id))
            {
                return false;
            }

            await WriteUnlockedAsync(
                document with
                {
                    Nodes = document.Nodes.Where(n => n.Id != id).ToList(),
                    JoinTokens = BurnUnused(document.JoinTokens, id, _timeProvider.GetUtcNow()),
                },
                ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<JoinTokenIssue> CreateJoinTokenAsync(string nodeId, TimeSpan lifetime, JoinTokenPurpose purpose, CancellationToken ct = default)
    {
        if (!Protocol.IsValidNodeId(nodeId))
        {
            throw new ArgumentException($"Node id '{nodeId}' is invalid; it must match {Protocol.NodeIdPattern}.", nameof(nodeId));
        }

        if (lifetime < MinJoinTokenLifetime || lifetime > MaxJoinTokenLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), lifetime, "A join token lifetime must be between 10 minutes and 24 hours.");
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown join token purpose.");
        }

        string token = Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));
        var now = _timeProvider.GetUtcNow();
        var record = new JoinTokenRecord
        {
            Sha256 = HashToken(token),
            NodeId = nodeId,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            UsedAt = null,
            Purpose = purpose,
        };

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var node = document.Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node is { Revoked: true })
            {
                throw new InvalidOperationException($"Node '{nodeId}' is revoked; a revoked id is not reused (delete the record first).");
            }

            // A rekey token is only minted for a node that can be re-paired (RekeyRefusal), checked under the lock so it
            // cannot race a revoke or a reject (MN-102).
            if (purpose == JoinTokenPurpose.Rekey && RekeyRefusal(node) is { } refusal)
            {
                throw new InvalidOperationException(
                    $"Node '{nodeId}' cannot be re-paired ({refusal}); a rekey token is only issued for an enrolled, non-revoked node (use a join token for a new node).");
            }

            await WriteUnlockedAsync(document with { JoinTokens = [.. document.JoinTokens, record] }, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }

        return new JoinTokenIssue(token, record);
    }

    public async Task<JoinTokenConsumption> TryConsumeJoinTokenAsync(string token, string nodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token))
        {
            return new JoinTokenConsumption(JoinTokenConsumeOutcome.Unknown, null);
        }

        byte[] presented = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadUnlockedAsync(ct).ConfigureAwait(false);

            // Compare against every stored hash without stopping early, so timing does not reveal the position.
            int index = -1;
            for (int i = 0; i < document.JoinTokens.Count; i++)
            {
                if (TryDecodeHash(document.JoinTokens[i].Sha256, out byte[] stored)
                    && CryptographicOperations.FixedTimeEquals(stored, presented)
                    && index < 0)
                {
                    index = i;
                }
            }

            if (index < 0)
            {
                return new JoinTokenConsumption(JoinTokenConsumeOutcome.Unknown, null);
            }

            var record = document.JoinTokens[index];
            if (record.UsedAt is not null)
            {
                return new JoinTokenConsumption(JoinTokenConsumeOutcome.AlreadyUsed, record);
            }

            var now = _timeProvider.GetUtcNow();
            if (now >= record.ExpiresAt)
            {
                return new JoinTokenConsumption(JoinTokenConsumeOutcome.Expired, record);
            }

            // A token bound to another node is refused and stays unused (it is still valid for its own node).
            if (!string.Equals(record.NodeId, nodeId, StringComparison.Ordinal))
            {
                return new JoinTokenConsumption(JoinTokenConsumeOutcome.WrongNode, record);
            }

            // A token whose node was revoked after it was issued is refused and stays unused (MN-090). Revoke also
            // expires such tokens; this covers files written before that rule.
            if (document.Nodes.FirstOrDefault(n => n.Id == record.NodeId) is { Revoked: true })
            {
                return new JoinTokenConsumption(JoinTokenConsumeOutcome.NodeRevoked, record);
            }

            var used = record with { UsedAt = now };
            var tokens = document.JoinTokens.ToList();
            tokens[index] = used;

            // Persisted before returning: if the write throws, the caller gets the exception and no certificate.
            await WriteUnlockedAsync(document with { JoinTokens = tokens }, ct).ConfigureAwait(false);
            return new JoinTokenConsumption(JoinTokenConsumeOutcome.Consumed, used);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<JoinTokenRecord>> ListJoinTokensAsync(CancellationToken ct = default)
    {
        var document = await ReadAsync(ct).ConfigureAwait(false);
        return document.JoinTokens;
    }

    /// <summary>
    /// Expires every unused, unexpired join token bound to <paramref name="nodeId"/> (its <c>ExpiresAt</c> becomes now) in
    /// one locked write and returns how many it expired (MN-102: the operator's "revoke token" action and the bootstrap
    /// bundle rewrite, W3 review MN-094). Nothing is written when none matches.
    /// </summary>
    public async Task<int> ExpireUnusedJoinTokensAsync(string nodeId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(nodeId);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            int count = document.JoinTokens.Count(t => IsLiveFor(t, nodeId, now));
            if (count > 0)
            {
                await WriteUnlockedAsync(document with { JoinTokens = BurnUnused(document.JoinTokens, nodeId, now) }, ct).ConfigureAwait(false);
            }

            return count;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Removes the join token records that are used or expired and whose <c>UsedAt</c> (or, unused, <c>ExpiresAt</c>) is
    /// more than <paramref name="olderThan"/> ago, in one locked write, and returns how many it removed (MN-102, W1 review
    /// MN-090). Unused tokens that are still valid are never removed. Nothing is written when none matches.
    /// </summary>
    public async Task<int> PruneJoinTokensAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await LoadUnlockedAsync(ct).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var cutoff = now - olderThan;
            var kept = document.JoinTokens.Where(t => !IsPrunable(t, now, cutoff)).ToList();
            int removed = document.JoinTokens.Count - kept.Count;
            if (removed > 0)
            {
                await WriteUnlockedAsync(document with { JoinTokens = kept }, ct).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Base64url SHA-256 of the token's UTF-8 bytes, as stored in <see cref="JoinTokenRecord.Sha256"/>.</summary>
    internal static string HashToken(string token) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private async Task<NodeRegistryDocument> ReadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadUnlockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Must be called while holding <see cref="_lock"/>.</summary>
    private async Task<NodeRegistryDocument> LoadUnlockedAsync(CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return new NodeRegistryDocument();
        }

        NodeRegistryDocument? document;
        await using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true))
        {
            document = await JsonSerializer.DeserializeAsync<NodeRegistryDocument>(stream, MasterStateJson.Options, ct).ConfigureAwait(false);
        }

        if (document is null)
        {
            return new NodeRegistryDocument();
        }

        if (document.SchemaVersion > NodeRegistryDocument.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"{NodeRegistryDocument.FileName} has schema version {document.SchemaVersion}; this build supports up to " +
                $"{NodeRegistryDocument.CurrentSchemaVersion}. Refusing to read or overwrite it.");
        }

        // An explicit null in the file is read as an empty list, so callers never see null collections.
        return document with
        {
            SchemaVersion = NodeRegistryDocument.CurrentSchemaVersion,
            Nodes = (document.Nodes ?? []).Where(n => n is not null).Select(Normalize).ToList(),
            JoinTokens = (document.JoinTokens ?? []).Where(t => t is not null).ToList(),
        };
    }

    /// <summary>Must be called while holding <see cref="_lock"/>. Temp file + move, like <see cref="JsonModelRegistry"/>.</summary>
    private async Task WriteUnlockedAsync(NodeRegistryDocument document, CancellationToken ct)
    {
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = _filePath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, document, MasterStateJson.Options, ct).ConfigureAwait(false);

            // To the disk, not only the OS cache, before the rename (MN-090): a consumed token's usedAt must survive a
            // power loss, or the token could issue a second certificate within its TTL (§3.2).
            stream.Flush(flushToDisk: true);
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
    }

    /// <summary>The token list with every unused, unexpired token bound to <paramref name="nodeId"/> expired now.</summary>
    private static List<JoinTokenRecord> BurnUnused(IReadOnlyList<JoinTokenRecord> tokens, string nodeId, DateTimeOffset now) =>
        tokens.Select(t => IsLiveFor(t, nodeId, now) ? t with { ExpiresAt = now } : t).ToList();

    /// <summary>True for an unused, unexpired token bound to <paramref name="nodeId"/>.</summary>
    private static bool IsLiveFor(JoinTokenRecord token, string nodeId, DateTimeOffset now) =>
        token.NodeId == nodeId && token.UsedAt is null && token.ExpiresAt > now;

    /// <summary>True for a used token used before <paramref name="cutoff"/>, or an unused one that expired before it.</summary>
    private static bool IsPrunable(JoinTokenRecord token, DateTimeOffset now, DateTimeOffset cutoff) =>
        token.UsedAt is { } used
            ? used < cutoff
            : token.ExpiresAt <= now && token.ExpiresAt < cutoff;

    /// <summary>A revoked record is never silently turned back into a live one (its id and uid are not reused).</summary>
    private static void EnsureNotUnrevoked(NodeRecord? existing, NodeRecord replacement)
    {
        if (existing is { Revoked: true } && !replacement.Revoked)
        {
            throw new InvalidOperationException(
                $"Node '{existing.Id}' is revoked; a revoked record is not reused (delete it first to pair the id again).");
        }
    }

    private static List<NodeRecord> Replace(IReadOnlyList<NodeRecord> nodes, NodeRecord node)
    {
        var list = nodes.ToList();
        int index = list.FindIndex(n => n.Id == node.Id);
        if (index < 0)
        {
            list.Add(node);
        }
        else
        {
            list[index] = node;
        }

        return list;
    }

    /// <summary>Null collections (an explicit <c>null</c> in the file or from a caller) become empty.</summary>
    private static NodeRecord Normalize(NodeRecord node) =>
        node.AltAddresses is null || node.Labels is null || node.GpuReserveGiB is null
            ? node with
            {
                AltAddresses = node.AltAddresses ?? [],
                Labels = node.Labels ?? [],
                GpuReserveGiB = node.GpuReserveGiB ?? new Dictionary<string, double>(),
            }
            : node;

    internal static void Validate(NodeRecord node)
    {
        if (!Protocol.IsValidNodeId(node.Id))
        {
            throw new ArgumentException($"Node id '{node.Id}' is invalid; it must match {Protocol.NodeIdPattern}.", nameof(node));
        }

        if (string.IsNullOrWhiteSpace(node.Uid))
        {
            throw new ArgumentException("Node uid must not be empty.", nameof(node));
        }

        if (string.IsNullOrWhiteSpace(node.Address))
        {
            throw new ArgumentException("Node address must not be empty.", nameof(node));
        }

        if (node.MemoryReserveGiB < 0 || double.IsNaN(node.MemoryReserveGiB) || double.IsInfinity(node.MemoryReserveGiB))
        {
            throw new ArgumentException("MemoryReserveGiB must be a finite, non-negative number.", nameof(node));
        }

        if (node.MaxConcurrentStarts is <= 0)
        {
            throw new ArgumentException("MaxConcurrentStarts must be positive when set.", nameof(node));
        }
    }

    private static bool TryDecodeHash(string? value, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        byte[] buffer = new byte[HashBytes + 3];
        if (!Convert.TryFromBase64String(base64, buffer, out int written) || written != HashBytes)
        {
            return false;
        }

        hash = buffer[..HashBytes];
        return true;
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
