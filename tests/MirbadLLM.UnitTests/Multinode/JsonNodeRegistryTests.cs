using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Orchestration.Fakes;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required here.

namespace MirbadLLM.UnitTests.Multinode;

public sealed class JsonNodeRegistryTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private string FilePath => Path.Combine(_dataDir, "nodes.json");

    private JsonNodeRegistry CreateRegistry() => new(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }), _time);

    private static NodeRecord Node(string id = "pc-office") => new()
    {
        Id = id,
        Uid = "0f9a1b2c3d4e5f60718293a4b5c6d7e8",
        DisplayName = "Office PC",
        Address = "https://192.0.2.50:9443",
        AltAddresses = ["https://pc-office.lan:9443"],
        Approved = true,
        CertSpkiSha256 = "pin-current",
        PendingCertSpkiSha256 = "pin-pending",
        MemoryReserveGiB = 4,
        GpuReserveGiB = new Dictionary<string, double> { ["GPU-1"] = 1.5 },
        MaxConcurrentStarts = 2,
        Labels = ["x64", "rtx"],
    };

    // ---- CRUD -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task List_WhenFileMissing_IsEmptyAndDoesNotCreateTheFile()
    {
        using var registry = CreateRegistry();

        Assert.Empty(await registry.ListAsync());
        Assert.Empty(await registry.ListJoinTokensAsync());
        Assert.Null(await registry.GetAsync("dgx"));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Upsert_ThenGetFromANewInstance_RoundTripsAllFields()
    {
        var node = Node();
        using (var registry = CreateRegistry())
        {
            await registry.UpsertAsync(node);
        }

        using var reloaded = CreateRegistry();
        var stored = await reloaded.GetAsync("pc-office");

        Assert.NotNull(stored);
        Assert.Equal(node.Uid, stored.Uid);
        Assert.Equal(node.DisplayName, stored.DisplayName);
        Assert.Equal(node.AltAddresses, stored.AltAddresses);
        Assert.Equal(node.Labels, stored.Labels);
        Assert.Equal(1.5, stored.GpuReserveGiB["GPU-1"]);
        Assert.Equal(2, stored.MaxConcurrentStarts);
        Assert.Equal(4, stored.MemoryReserveGiB);
        Assert.Equal(_time.GetUtcNow(), stored.CreatedAt); // stamped because the caller left it default

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("pc-office", json.RootElement.GetProperty("nodes")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Upsert_ReplacesById_AndKeepsCreatedAtWhenNotGiven()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node());
        var created = (await registry.GetAsync("pc-office"))!.CreatedAt;
        _time.Advance(TimeSpan.FromHours(1));

        await registry.UpsertAsync(Node() with { DisplayName = "Renamed" });
        await registry.UpsertAsync(Node("dgx"));

        var nodes = await registry.ListAsync();
        Assert.Equal(2, nodes.Count);
        var office = Assert.Single(nodes, n => n.Id == "pc-office");
        Assert.Equal("Renamed", office.DisplayName);
        Assert.Equal(created, office.CreatedAt);
    }

    [Theory]
    [InlineData("Bad_Id", "uid", "https://a:9443")]
    [InlineData("", "uid", "https://a:9443")]
    [InlineData("ok", " ", "https://a:9443")]
    [InlineData("ok", "uid", "")]
    public async Task Upsert_InvalidRecord_Throws(string id, string uid, string address)
    {
        using var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(Node() with { Id = id, Uid = uid, Address = address }));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task Upsert_NegativeReserveOrZeroStarts_Throws()
    {
        using var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(Node() with { MemoryReserveGiB = -1 }));
        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpsertAsync(Node() with { MaxConcurrentStarts = 0 }));
    }

    [Fact]
    public async Task Update_ReadModifyWrite_IsPersisted_AndMissingNodeReturnsNullWithoutCallingTheDelegate()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node());
        var contact = _time.GetUtcNow();

        var updated = await registry.UpdateAsync("pc-office", n => n with { LastContact = contact, Draining = true });
        bool called = false;
        var missing = await registry.UpdateAsync("nope", n => { called = true; return n; });

        Assert.NotNull(updated);
        Assert.Equal(contact, updated.LastContact);
        Assert.Null(missing);
        Assert.False(called);
        using var reloaded = CreateRegistry();
        var stored = (await reloaded.GetAsync("pc-office"))!;
        Assert.Equal(contact, stored.LastContact);
        Assert.True(stored.Draining);
    }

    [Fact]
    public async Task Update_ChangingTheId_Throws_AndLeavesTheRecord()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node());

        await Assert.ThrowsAsync<ArgumentException>(() => registry.UpdateAsync("pc-office", n => n with { Id = "other" }));

        Assert.NotNull(await registry.GetAsync("pc-office"));
        Assert.Null(await registry.GetAsync("other"));
    }

    [Fact]
    public async Task Update_ConcurrentIncrements_AreNotLost()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node() with { Labels = [] });

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => registry.UpdateAsync("pc-office", n => n with { Labels = [.. n.Labels, $"l{i}"] }))));

        var stored = (await registry.GetAsync("pc-office"))!;
        Assert.Equal(20, stored.Labels.Count);
        Assert.Equal(20, stored.Labels.Distinct().Count());
    }

    [Fact]
    public async Task RotationRecord_IsPersisted()
    {
        using (var registry = CreateRegistry())
        {
            await registry.UpsertAsync(Node());
            await registry.UpdateAsync("pc-office", n => n with
            {
                Rotation = new CertificateRotationRecord
                {
                    RotationId = "rot-1",
                    NodeUid = n.Uid,
                    Stage = CertificateRotationStage.CsrReceived,
                    OperationId = "op-1",
                    NewSpkiSha256 = "new-pin",
                    StartedAt = _time.GetUtcNow(),
                    UpdatedAt = _time.GetUtcNow(),
                },
            });
            await registry.UpdateAsync("pc-office", n => n with
            {
                PendingCertSpkiSha256 = n.Rotation!.NewSpkiSha256,
                Rotation = n.Rotation with { Stage = CertificateRotationStage.CertIssued },
            });
        }

        using var reloaded = CreateRegistry();
        var stored = (await reloaded.GetAsync("pc-office"))!;
        Assert.NotNull(stored.Rotation);
        Assert.Equal("rot-1", stored.Rotation.RotationId);
        Assert.Equal(CertificateRotationStage.CertIssued, stored.Rotation.Stage);
        Assert.Equal("new-pin", stored.PendingCertSpkiSha256);
        Assert.Contains("\"certIssued\"", await File.ReadAllTextAsync(FilePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoke_KeepsTheRecordWithFlag_AndDeletesBothPins()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node());

        Assert.True(await registry.RevokeAsync("pc-office"));
        Assert.False(await registry.RevokeAsync("missing"));

        using var reloaded = CreateRegistry();
        var stored = Assert.Single(await reloaded.ListAsync());
        Assert.True(stored.Revoked);
        Assert.Equal(_time.GetUtcNow(), stored.RevokedAt);
        Assert.Null(stored.CertSpkiSha256);
        Assert.Null(stored.PendingCertSpkiSha256);
        Assert.Equal(Node().Uid, stored.Uid);
    }

    [Fact]
    public async Task Revoke_Twice_KeepsTheFirstRevokedAt()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node());
        await registry.RevokeAsync("pc-office");
        var first = (await registry.GetAsync("pc-office"))!.RevokedAt;
        _time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(await registry.RevokeAsync("pc-office"));
        Assert.Equal(first, (await registry.GetAsync("pc-office"))!.RevokedAt);
    }

    [Fact]
    public async Task Delete_RemovesTheRecord()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node());
        await registry.UpsertAsync(Node("dgx"));

        Assert.True(await registry.DeleteAsync("pc-office"));
        Assert.False(await registry.DeleteAsync("pc-office"));

        using var reloaded = CreateRegistry();
        Assert.Equal("dgx", Assert.Single(await reloaded.ListAsync()).Id);
    }

    // ---- File handling ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Write_IsTempPlusMove_LeavingNoTempFile()
    {
        using var registry = CreateRegistry();

        await registry.UpsertAsync(Node());
        await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.Equal(["nodes.json"], Directory.GetFiles(_dataDir).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Write_WhenTheMoveFails_Throws_AndTheTargetIsUntouched()
    {
        // The move is the commit point: if it cannot happen, the target is never partially written.
        Directory.CreateDirectory(FilePath);
        using var registry = CreateRegistry();

        await Assert.ThrowsAnyAsync<Exception>(() => registry.UpsertAsync(Node()));

        Assert.True(Directory.Exists(FilePath));
    }

    [Fact]
    public async Task Load_V1Fixture_ReadsEverything_AndSaveKeepsIt()
    {
        string fixture = Fixture("nodes.v1.json");
        Directory.CreateDirectory(_dataDir);
        await File.WriteAllTextAsync(FilePath, fixture);
        using var registry = CreateRegistry();

        var dgx = (await registry.GetAsync("dgx"))!;
        Assert.Equal(MemoryModel.Unified, dgx.LastCapabilities!.Memory.Model);
        Assert.NotEmpty(await registry.ListJoinTokensAsync());

        await registry.UpdateAsync("dgx", n => n); // rewrite unchanged

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(fixture), JsonNode.Parse(await File.ReadAllTextAsync(FilePath))));
    }

    [Fact]
    public async Task Load_ExplicitNullCollections_AreReadAsEmpty()
    {
        Directory.CreateDirectory(_dataDir);
        await File.WriteAllTextAsync(FilePath, """
            {"schemaVersion":1,"nodes":[{"id":"dgx","uid":"u","address":"https://127.0.0.1:9443","altAddresses":null,"labels":null,"gpuReserveGiB":null}],"joinTokens":null}
            """);
        using var registry = CreateRegistry();

        var dgx = (await registry.GetAsync("dgx"))!;
        Assert.Empty(dgx.AltAddresses);
        Assert.Empty(dgx.Labels);
        Assert.Empty(dgx.GpuReserveGiB);
        Assert.Empty(await registry.ListJoinTokensAsync());
    }

    [Fact]
    public async Task Load_NewerSchemaVersion_Throws_AndIsNotOverwritten()
    {
        Directory.CreateDirectory(_dataDir);
        const string future = """{"schemaVersion":2,"nodes":[],"joinTokens":[]}""";
        await File.WriteAllTextAsync(FilePath, future);
        using var registry = CreateRegistry();

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.UpsertAsync(Node()));
        Assert.Equal(future, await File.ReadAllTextAsync(FilePath));
    }

    [Fact]
    public async Task Load_CorruptFile_Throws_AndIsNotOverwritten()
    {
        Directory.CreateDirectory(_dataDir);
        await File.WriteAllTextAsync(FilePath, "{ not json");
        using var registry = CreateRegistry();

        await Assert.ThrowsAsync<JsonException>(() => registry.UpsertAsync(Node()));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(FilePath));
    }

    // ---- Join tokens ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CreateJoinToken_ReturnsPlaintextOnce_AndStoresOnlyTheHashExpiryAndNode()
    {
        using var registry = CreateRegistry();

        var issue = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromMinutes(60), JoinTokenPurpose.Join);

        // 32 random bytes, base64url without padding.
        Assert.Equal(43, issue.Token.Length);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", issue.Token);
        string expectedHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(issue.Token)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expectedHash, issue.Record.Sha256);
        Assert.Equal("pc-office", issue.Record.NodeId);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(60), issue.Record.ExpiresAt);
        Assert.Equal(_time.GetUtcNow(), issue.Record.CreatedAt);
        Assert.Null(issue.Record.UsedAt);
        Assert.Equal(JoinTokenPurpose.Join, issue.Record.Purpose);

        string file = await File.ReadAllTextAsync(FilePath);
        Assert.DoesNotContain(issue.Token, file, StringComparison.Ordinal);
        Assert.Contains(expectedHash, file, StringComparison.Ordinal);
        Assert.DoesNotContain(issue.Token, issue.ToString(), StringComparison.Ordinal);

        using var reloaded = CreateRegistry();
        var stored = Assert.Single(await reloaded.ListJoinTokensAsync());
        Assert.Equal(issue.Record, stored);
    }

    [Fact]
    public async Task CreateJoinToken_TokensAreUnique()
    {
        using var registry = CreateRegistry();

        var a = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        var b = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Bootstrap);

        Assert.NotEqual(a.Token, b.Token);
        Assert.Equal(2, (await registry.ListJoinTokensAsync()).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9)] // below the 10-minute minimum of §3.2 (MN-090)
    [InlineData(24 * 60 + 1)]
    public async Task CreateJoinToken_LifetimeOutOfRange_Throws(int minutes)
    {
        using var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromMinutes(minutes), JoinTokenPurpose.Join));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(24 * 60)]
    public async Task CreateJoinToken_AtTheLifetimeBounds_IsAccepted(int minutes)
    {
        using var registry = CreateRegistry();

        var issue = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromMinutes(minutes), JoinTokenPurpose.Join);

        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromMinutes(minutes), issue.Record.ExpiresAt);
    }

    // ---- Revocation and join tokens (MN-090) ------------------------------------------------------------------------

    [Fact]
    public async Task Revoke_ExpiresTheNodesUnusedTokens_InTheSameWrite_AndLeavesOthers()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node());
        var mine = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        var other = await registry.CreateJoinTokenAsync("pc-lab", TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        Assert.True(await registry.RevokeAsync("pc-office"));

        // Read the file directly: the burn is persisted with the revoke, not left to a later call.
        var onDisk = JsonSerializer.Deserialize<NodeRegistryDocument>(await File.ReadAllTextAsync(FilePath), MasterStateJson.Options)!;
        Assert.Equal(_time.GetUtcNow(), onDisk.JoinTokens.Single(t => t.NodeId == "pc-office").ExpiresAt);
        Assert.True(onDisk.Nodes.Single().Revoked);
        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await registry.TryConsumeJoinTokenAsync(mine.Token, "pc-office")).Outcome);
        Assert.Equal(JoinTokenConsumeOutcome.Consumed, (await registry.TryConsumeJoinTokenAsync(other.Token, "pc-lab")).Outcome);
    }

    [Fact]
    public async Task Delete_ExpiresTheNodesUnusedTokens()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node());
        var issue = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        Assert.True(await registry.DeleteAsync("pc-office"));

        Assert.Equal(JoinTokenConsumeOutcome.Expired, (await registry.TryConsumeJoinTokenAsync(issue.Token, "pc-office")).Outcome);
    }

    [Fact]
    public async Task TryConsume_ForARevokedNode_IsRefused_EvenIfTheTokenWasNotBurnt()
    {
        // A file written before revoke burnt tokens: the node is revoked, its token still unexpired and unused.
        using var registry = CreateRegistry();
        var issue = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        await registry.UpsertAsync(Node() with { Revoked = true });

        var result = await registry.TryConsumeJoinTokenAsync(issue.Token, "pc-office");

        Assert.Equal(JoinTokenConsumeOutcome.NodeRevoked, result.Outcome);
        Assert.Null(Assert.Single(await registry.ListJoinTokensAsync()).UsedAt);
    }

    [Fact]
    public async Task CreateJoinToken_ForARevokedNode_IsRefused()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node());
        await registry.RevokeAsync("pc-office");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join));
        Assert.Empty(await registry.ListJoinTokensAsync());
    }

    [Fact]
    public async Task ARevokedRecord_IsNotUnrevoked_ByUpsertOrUpdate_ButCanBeDeleted()
    {
        using var registry = CreateRegistry();
        await registry.UpsertAsync(Node());
        await registry.RevokeAsync("pc-office");

        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.UpsertAsync(Node()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.UpdateAsync("pc-office", n => n with { Revoked = false }));
        Assert.True((await registry.GetAsync("pc-office"))!.Revoked);

        // Updating other fields of a revoked record is fine, and delete-then-upsert re-pairs the id explicitly.
        Assert.NotNull(await registry.UpdateAsync("pc-office", n => n with { DisplayName = "old office" }));
        Assert.True(await registry.DeleteAsync("pc-office"));
        await registry.UpsertAsync(Node());
        Assert.False((await registry.GetAsync("pc-office"))!.Revoked);
    }

    [Fact]
    public async Task CreateJoinToken_InvalidNodeId_Throws()
    {
        using var registry = CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(
            () => registry.CreateJoinTokenAsync("Not Valid", TimeSpan.FromHours(1), JoinTokenPurpose.Join));
    }

    [Fact]
    public async Task TryConsume_Valid_ConsumesAndPersistsBeforeReturning()
    {
        using var registry = CreateRegistry();
        var issue = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        _time.Advance(TimeSpan.FromMinutes(5));

        var result = await registry.TryConsumeJoinTokenAsync(issue.Token, "pc-office");

        Assert.Equal(JoinTokenConsumeOutcome.Consumed, result.Outcome);
        Assert.Equal(_time.GetUtcNow(), result.Record!.UsedAt);

        // Persisted: read the file directly, without the registry.
        var onDisk = JsonSerializer.Deserialize<NodeRegistryDocument>(await File.ReadAllTextAsync(FilePath), MasterStateJson.Options)!;
        Assert.Equal(_time.GetUtcNow(), Assert.Single(onDisk.JoinTokens).UsedAt);

        // A new instance (master restart) refuses it.
        using var reloaded = CreateRegistry();
        var again = await reloaded.TryConsumeJoinTokenAsync(issue.Token, "pc-office");
        Assert.Equal(JoinTokenConsumeOutcome.AlreadyUsed, again.Outcome);
    }

    [Fact]
    public async Task TryConsume_FiftyConcurrentCallers_SucceedExactlyOnce()
    {
        using var registry = CreateRegistry();
        var issue = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        using var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return registry.TryConsumeJoinTokenAsync(issue.Token, "pc-office");
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, r => r.Outcome == JoinTokenConsumeOutcome.Consumed);
        Assert.Equal(49, results.Count(r => r.Outcome == JoinTokenConsumeOutcome.AlreadyUsed));
    }

    [Fact]
    public async Task TryConsume_FiftyConcurrentCallersOverFiveTokens_ConsumeEachExactlyOnce()
    {
        using var registry = CreateRegistry();
        var issues = new List<JoinTokenIssue>();
        for (int i = 0; i < 5; i++)
        {
            issues.Add(await registry.CreateJoinTokenAsync($"node-{i}", TimeSpan.FromHours(1), JoinTokenPurpose.Join));
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            Task.Run(() => registry.TryConsumeJoinTokenAsync(issues[i % 5].Token, $"node-{i % 5}"))));

        Assert.Equal(5, results.Count(r => r.Outcome == JoinTokenConsumeOutcome.Consumed));
        Assert.All(await registry.ListJoinTokensAsync(), t => Assert.NotNull(t.UsedAt));
    }

    [Fact]
    public async Task TryConsume_Expired_IsRefusedAndNotMarkedUsed()
    {
        using var registry = CreateRegistry();
        var issue = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromMinutes(10), JoinTokenPurpose.Join);
        _time.Advance(TimeSpan.FromMinutes(10));

        var result = await registry.TryConsumeJoinTokenAsync(issue.Token, "pc-office");

        Assert.Equal(JoinTokenConsumeOutcome.Expired, result.Outcome);
        Assert.Null(Assert.Single(await registry.ListJoinTokensAsync()).UsedAt);
    }

    [Fact]
    public async Task TryConsume_WrongNode_IsRefused_AndTheTokenStillWorksForItsNode()
    {
        using var registry = CreateRegistry();
        var issue = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        var wrong = await registry.TryConsumeJoinTokenAsync(issue.Token, "dgx");
        var right = await registry.TryConsumeJoinTokenAsync(issue.Token, "pc-office");

        Assert.Equal(JoinTokenConsumeOutcome.WrongNode, wrong.Outcome);
        Assert.Null(wrong.Record!.UsedAt);
        Assert.Equal(JoinTokenConsumeOutcome.Consumed, right.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task TryConsume_UnknownToken_IsRefused(string token)
    {
        using var registry = CreateRegistry();
        var issue = await registry.CreateJoinTokenAsync("pc-office", TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        var result = await registry.TryConsumeJoinTokenAsync(token, "pc-office");

        Assert.Equal(JoinTokenConsumeOutcome.Unknown, result.Outcome);
        Assert.Null(result.Record);
        Assert.Null(Assert.Single(await registry.ListJoinTokensAsync()).UsedAt);
        Assert.NotEmpty(issue.Token);
    }

    [Fact]
    public async Task TryConsume_PicksTheMatchingTokenAmongMany_AndIgnoresMalformedStoredHashes()
    {
        Directory.CreateDirectory(_dataDir);
        await File.WriteAllTextAsync(FilePath, """
            {"schemaVersion":1,"nodes":[],"joinTokens":[{"sha256":"!!not-base64!!","nodeId":"x","expiresAt":"2999-01-01T00:00:00Z"}]}
            """);
        using var registry = CreateRegistry();
        await registry.CreateJoinTokenAsync("node-a", TimeSpan.FromHours(1), JoinTokenPurpose.Join);
        var b = await registry.CreateJoinTokenAsync("node-b", TimeSpan.FromHours(1), JoinTokenPurpose.Join);

        var result = await registry.TryConsumeJoinTokenAsync(b.Token, "node-b");

        Assert.Equal(JoinTokenConsumeOutcome.Consumed, result.Outcome);
        var tokens = await registry.ListJoinTokensAsync();
        Assert.Single(tokens, t => t.UsedAt is not null);
        Assert.Equal("node-b", Assert.Single(tokens, t => t.UsedAt is not null).NodeId);
    }

    [Fact]
    public void HashToken_IsBase64UrlSha256OfUtf8()
    {
        Assert.Equal("LPJNul-wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ", JsonNodeRegistry.HashToken("hello"));
    }

    private static string Fixture(string name, [CallerFilePath] string sourceFile = "") =>
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "Contracts", "Fixtures", name));
}
