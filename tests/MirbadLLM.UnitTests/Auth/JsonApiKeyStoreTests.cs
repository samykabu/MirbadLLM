using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Auth;

public sealed class JsonApiKeyStoreTests : IDisposable
{
    private readonly string _dataDir;
    private readonly ManualTimeProvider _time = new();

    public JsonApiKeyStoreTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private JsonApiKeyStore CreateStore() =>
        new(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }), _time, NullLogger<JsonApiKeyStore>.Instance);

    [Fact]
    public async Task CreateAsync_GeneratesAssignedPrefixedKey_AndNeverPersistsThePlaintext()
    {
        var store = CreateStore();

        var created = await store.CreateAsync("laptop", ApiKeyScope.Inference);

        Assert.StartsWith("ssk_", created.FullKey);
        Assert.Equal("laptop", created.Record.Name);
        Assert.Equal(ApiKeyScope.Inference, created.Record.Scope);
        Assert.True(created.Record.IsActive);
        Assert.Null(created.Record.RevokedAt);
        Assert.Null(created.Record.LastUsedAt);

        // The prefix is exactly the first 8 characters after "ssk_", and the hash is neither the key nor its prefix.
        Assert.Equal(created.FullKey["ssk_".Length..][..8], created.Record.Prefix);
        Assert.NotEqual(created.FullKey, created.Record.KeyHash);
        Assert.DoesNotContain(created.Record.Prefix, created.Record.KeyHash, StringComparison.Ordinal);

        string raw = await File.ReadAllTextAsync(Path.Combine(_dataDir, "apikeys.json"));
        Assert.DoesNotContain(created.FullKey, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_TwoKeys_GenerateDifferentPlaintextsAndHashes()
    {
        var store = CreateStore();

        var first = await store.CreateAsync("a", ApiKeyScope.Inference);
        var second = await store.CreateAsync("b", ApiKeyScope.Inference);

        Assert.NotEqual(first.FullKey, second.FullKey);
        Assert.NotEqual(first.Record.KeyHash, second.Record.KeyHash);
        Assert.NotEqual(first.Record.Id, second.Record.Id);
    }

    [Fact]
    public async Task ListAsync_ReturnsCreatedKeys()
    {
        var store = CreateStore();
        await store.CreateAsync("one", ApiKeyScope.Inference);
        await store.CreateAsync("two", ApiKeyScope.Metrics);

        var keys = await store.ListAsync();

        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public async Task RevokeAsync_MarksInactive_AndSecondRevokeReturnsFalse()
    {
        var store = CreateStore();
        var created = await store.CreateAsync("one", ApiKeyScope.Inference);

        bool firstRevoke = await store.RevokeAsync(created.Record.Id);
        bool secondRevoke = await store.RevokeAsync(created.Record.Id);

        Assert.True(firstRevoke);
        Assert.False(secondRevoke);
        var stored = (await store.ListAsync()).Single(k => k.Id == created.Record.Id);
        Assert.False(stored.IsActive);
        Assert.NotNull(stored.RevokedAt);
    }

    [Fact]
    public async Task RevokeAsync_UnknownId_ReturnsFalse()
    {
        var store = CreateStore();

        Assert.False(await store.RevokeAsync("no-such-id"));
    }

    [Fact]
    public async Task HasActiveKeyAsync_IgnoresRevokedAndOtherScopeKeys()
    {
        var store = CreateStore();
        var inference = await store.CreateAsync("inf", ApiKeyScope.Inference);
        await store.RevokeAsync(inference.Record.Id);

        Assert.False(await store.HasActiveKeyAsync(ApiKeyScope.Inference));
        Assert.False(await store.HasActiveKeyAsync(ApiKeyScope.Metrics));

        await store.CreateAsync("metrics", ApiKeyScope.Metrics);
        Assert.True(await store.HasActiveKeyAsync(ApiKeyScope.Metrics));
        Assert.False(await store.HasActiveKeyAsync(ApiKeyScope.Inference));
    }

    [Fact]
    public async Task HasAnyKeyAsync_StaysTrueAfterTheOnlyKeyOfThatScopeIsRevoked()
    {
        var store = CreateStore();
        var created = await store.CreateAsync("inf", ApiKeyScope.Inference);

        Assert.True(await store.HasAnyKeyAsync(ApiKeyScope.Inference));
        Assert.True(await store.HasActiveKeyAsync(ApiKeyScope.Inference));

        await store.RevokeAsync(created.Record.Id);

        // HasAnyKeyAsync is a one-way switch (revoking must not un-require auth); HasActiveKeyAsync reflects
        // "is there a currently-usable key" and correctly flips to false.
        Assert.True(await store.HasAnyKeyAsync(ApiKeyScope.Inference));
        Assert.False(await store.HasActiveKeyAsync(ApiKeyScope.Inference));
    }

    [Fact]
    public async Task FindByPresentedKeyAsync_MatchesOnlyTheCorrectActiveKey()
    {
        var store = CreateStore();
        var created = await store.CreateAsync("one", ApiKeyScope.Inference);
        await store.CreateAsync("two", ApiKeyScope.Metrics);

        var hit = await store.FindByPresentedKeyAsync(created.FullKey);
        var miss = await store.FindByPresentedKeyAsync("ssk_" + new string('a', 43));

        Assert.NotNull(hit);
        Assert.Equal(created.Record.Id, hit!.Id);
        Assert.Null(miss);
    }

    [Fact]
    public async Task FindByPresentedKeyAsync_RevokedKey_DoesNotMatch()
    {
        var store = CreateStore();
        var created = await store.CreateAsync("one", ApiKeyScope.Inference);
        await store.RevokeAsync(created.Record.Id);

        Assert.Null(await store.FindByPresentedKeyAsync(created.FullKey));
    }

    [Fact]
    public async Task FindByPresentedKeyAsync_UpdatesLastUsedAtInMemoryImmediately()
    {
        var store = CreateStore();
        var created = await store.CreateAsync("one", ApiKeyScope.Inference);
        Assert.Null(created.Record.LastUsedAt);

        _time.Advance(TimeSpan.FromSeconds(1));
        var hit = await store.FindByPresentedKeyAsync(created.FullKey);

        Assert.NotNull(hit!.LastUsedAt);
    }

    [Fact]
    public async Task FindByPresentedKeyAsync_PersistsLastUsedAtAtMostOncePerMinute()
    {
        var store = CreateStore();
        var created = await store.CreateAsync("one", ApiKeyScope.Inference);
        string afterCreate = await File.ReadAllTextAsync(Path.Combine(_dataDir, "apikeys.json"));

        // 30s after creation (< the 1-minute flush interval since the create-time flush): recorded in
        // memory, but not yet written to disk.
        _time.Advance(TimeSpan.FromSeconds(30));
        await store.FindByPresentedKeyAsync(created.FullKey);
        string afterFirstHit = await File.ReadAllTextAsync(Path.Combine(_dataDir, "apikeys.json"));
        Assert.Equal(afterCreate, afterFirstHit);

        // 40s after creation: still under a minute since the last flush, so still not written.
        _time.Advance(TimeSpan.FromSeconds(10));
        await store.FindByPresentedKeyAsync(created.FullKey);
        string afterSecondHit = await File.ReadAllTextAsync(Path.Combine(_dataDir, "apikeys.json"));
        Assert.Equal(afterFirstHit, afterSecondHit);

        // 70s after creation: now over a minute since the last flush, so this hit's LastUsedAt is persisted.
        _time.Advance(TimeSpan.FromSeconds(30));
        await store.FindByPresentedKeyAsync(created.FullKey);
        string afterThirdHit = await File.ReadAllTextAsync(Path.Combine(_dataDir, "apikeys.json"));
        Assert.NotEqual(afterSecondHit, afterThirdHit);
    }

    [Fact]
    public async Task StatePersistsAcrossNewStoreInstance()
    {
        var first = CreateStore();
        var created = await first.CreateAsync("one", ApiKeyScope.Inference);

        var second = CreateStore();
        var reloaded = await second.FindByPresentedKeyAsync(created.FullKey);

        Assert.NotNull(reloaded);
        Assert.Equal(created.Record.Id, reloaded!.Id);
        Assert.Equal(created.Record.Name, reloaded.Name);
    }

    [Fact]
    public async Task DeleteAsync_RevokedKey_RemovesItPermanently()
    {
        var store = CreateStore();
        var created = await store.CreateAsync("one", ApiKeyScope.Inference);
        await store.RevokeAsync(created.Record.Id);

        var result = await store.DeleteAsync(created.Record.Id);

        Assert.Equal(ApiKeyDeleteResult.Deleted, result);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task DeleteAsync_ActiveKey_Returns409EquivalentAndKeepsIt()
    {
        var store = CreateStore();
        var created = await store.CreateAsync("one", ApiKeyScope.Inference);

        var result = await store.DeleteAsync(created.Record.Id);

        Assert.Equal(ApiKeyDeleteResult.StillActive, result);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_ReturnsNotFound()
    {
        var store = CreateStore();

        Assert.Equal(ApiKeyDeleteResult.NotFound, await store.DeleteAsync("no-such-id"));
    }

    [Fact]
    public async Task DeletingEveryInferenceKey_GateStaysClosed()
    {
        var store = CreateStore();
        var created = await store.CreateAsync("one", ApiKeyScope.Inference);
        await store.RevokeAsync(created.Record.Id);
        await store.DeleteAsync(created.Record.Id);

        Assert.Empty(await store.ListAsync());
        Assert.True(await store.HasAnyKeyAsync(ApiKeyScope.Inference));
        Assert.False(await store.HasActiveKeyAsync(ApiKeyScope.Inference));

        // A fresh instance re-reads the flag from disk: it must not have been derived from the (now
        // empty) key list, or it would silently reopen /v1.
        var reloaded = CreateStore();
        Assert.True(await reloaded.HasAnyKeyAsync(ApiKeyScope.Inference));
    }

    [Fact]
    public async Task DeletingEveryMetricsKey_GateStaysClosed()
    {
        var store = CreateStore();
        var created = await store.CreateAsync("one", ApiKeyScope.Metrics);
        await store.RevokeAsync(created.Record.Id);
        await store.DeleteAsync(created.Record.Id);

        Assert.Empty(await store.ListAsync());
        Assert.True(await store.HasAnyKeyAsync(ApiKeyScope.Metrics));

        var reloaded = CreateStore();
        Assert.True(await reloaded.HasAnyKeyAsync(ApiKeyScope.Metrics));
    }

    [Fact]
    public async Task LegacyBareArrayFile_MigratesToFlaggedFormat_PreservingTheGate()
    {
        Directory.CreateDirectory(_dataDir);
        string legacyPath = Path.Combine(_dataDir, "apikeys.json");
        string id = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(legacyPath, $$"""
            [
              {
                "Id": "{{id}}",
                "Name": "legacy",
                "Scope": "Inference",
                "KeyHash": "deadbeef",
                "Prefix": "legacypf",
                "CreatedAt": "2025-01-01T00:00:00+00:00",
                "LastUsedAt": null,
                "RevokedAt": "2025-01-02T00:00:00+00:00"
              }
            ]
            """);

        var store = CreateStore();

        // The legacy file only ever held a revoked key, but the scope was present, so the gate must read
        // as "ever created" once migrated.
        Assert.True(await store.HasAnyKeyAsync(ApiKeyScope.Inference));
        Assert.False(await store.HasAnyKeyAsync(ApiKeyScope.Metrics));
        Assert.Single(await store.ListAsync());

        var deleted = await store.DeleteAsync(id);
        Assert.Equal(ApiKeyDeleteResult.Deleted, deleted);
        Assert.Empty(await store.ListAsync());

        // The gate survives both the migration and the subsequent deletion of the only remaining key.
        var reloaded = CreateStore();
        Assert.True(await reloaded.HasAnyKeyAsync(ApiKeyScope.Inference));

        string migrated = await File.ReadAllTextAsync(legacyPath);
        Assert.Contains("InferenceKeyEverCreated", migrated, StringComparison.Ordinal);
    }
}
