using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Web.Fakes.FakeModelRegistry;

#pragma warning disable CA1707 // underscore test names

namespace MirbadLLM.UnitTests.Orchestration;

/// <summary>Saved tokens (Access > Tokens): encrypted at rest, picked by unique name, carried by encrypted backups.</summary>
public sealed class JsonTokenStoreTests : IDisposable
{
    private const string Value = "hf_savedTokenSecret0123456789abcd";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly EphemeralDataProtectionProvider _protection = new();
    private readonly ManualTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private JsonTokenStore Store(string? dataDir = null) =>
        new(Options.Create(new MirbadLLMOptions { DataPath = dataDir ?? _dataDir }), _protection, _time);

    [Fact]
    public async Task Add_KeepsTheValueEncrypted_ShowsOnlyAHint_AndReadsBackAfterARestart()
    {
        using (var store = Store())
        {
            var saved = await store.AddAsync("  hf-personal ", Value, Ct);
            Assert.Equal("hf-personal", saved.Name);
            Assert.Equal("abcd", saved.Hint);
        }

        Assert.DoesNotContain(Value, await File.ReadAllTextAsync(Path.Combine(_dataDir, "tokens.json"), Ct), StringComparison.Ordinal);
        using var reopened = Store();
        var token = Assert.Single(await reopened.ListAsync(Ct));
        Assert.Equal(Value, await reopened.GetValueAsync(token.Id, Ct));
        Assert.Null(await reopened.GetValueAsync("missing", Ct));
    }

    [Fact]
    public async Task Names_AreRequiredAndUniqueIgnoringCase_AndAValueIsRequiredToAdd()
    {
        using var store = Store();
        var first = await store.AddAsync("HF", Value, Ct);
        var other = await store.AddAsync("other", Value, Ct);

        Assert.Throws<ArgumentException>(() => store.AddAsync("hf", Value, Ct).GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() => store.AddAsync(" ", Value, Ct).GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() => store.AddAsync("new", " ", Ct).GetAwaiter().GetResult());
        Assert.Throws<ArgumentException>(() => store.UpdateAsync(other.Id, "hf", null, Ct).GetAwaiter().GetResult());
        Assert.NotNull(await store.UpdateAsync(first.Id, "hf", null, Ct)); // renaming to itself, case changed, is fine
    }

    [Fact]
    public async Task Update_RenamesAndReplacesTheValueOnlyWhenOneIsGiven_DeleteRemovesIt()
    {
        using var store = Store();
        var saved = await store.AddAsync("hf", Value, Ct);

        var renamed = await store.UpdateAsync(saved.Id, "hf-work", "", Ct);
        Assert.Equal(("hf-work", "abcd"), (renamed!.Name, renamed.Hint));
        Assert.Equal(Value, await store.GetValueAsync(saved.Id, Ct));

        var replaced = await store.UpdateAsync(saved.Id, "hf-work", "hf_replacementToken9876wxyz", Ct);
        Assert.Equal("wxyz", replaced!.Hint);
        Assert.Equal("hf_replacementToken9876wxyz", await store.GetValueAsync(saved.Id, Ct));

        Assert.Null(await store.UpdateAsync("missing", "x", null, Ct));
        Assert.True(await store.DeleteAsync(saved.Id, Ct));
        Assert.False(await store.DeleteAsync(saved.Id, Ct));
        Assert.Empty(await store.ListAsync(Ct));
    }

    [Fact]
    public async Task Import_AddsNewTokens_AndSkipsKnownIdsAndNamesInUse()
    {
        using var source = Store(Path.Combine(_dataDir, "a"));
        await source.AddAsync("hf", Value, Ct);
        await source.AddAsync("work", "hf_workTokenValue000000wxyz", Ct);
        var exported = await source.ExportAsync(Ct);

        using var target = Store(Path.Combine(_dataDir, "b"));
        await target.AddAsync("HF", "hf_alreadyHereValue0000qrst", Ct);
        var result = await target.ImportAsync(exported, Ct);

        Assert.Equal(new SavedTokenImportResult(1, 1), result);
        var tokens = await target.ListAsync(Ct);
        Assert.Equal(["HF", "work"], tokens.Select(t => t.Name));
        Assert.Equal("hf_alreadyHereValue0000qrst", await target.GetValueAsync(tokens[0].Id, Ct)); // never overwritten
        Assert.Equal(new SavedTokenImportResult(0, 2), await target.ImportAsync(exported, Ct));
        Assert.DoesNotContain(Value, exported[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backup_CarriesTheTokensOnlyInsideTheEncryptedSecrets()
    {
        using var tokens = Store();
        await tokens.AddAsync("hf", Value, Ct);
        using var keys = new JsonApiKeyStore(
            Options.Create(new MirbadLLMOptions { DataPath = _dataDir }), _time,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonApiKeyStore>.Instance);
        var service = new BackupService(new FakeModelRegistry([]), keys, Options.Create(new BackupOptions()), _time, tokens: tokens);

        byte[] withSecrets = BackupService.Serialize(await service.CreateAsync("correct horse battery staple", Ct));
        byte[] withoutSecrets = BackupService.Serialize(await service.CreateAsync(null, Ct));

        Assert.DoesNotContain(Value, System.Text.Encoding.UTF8.GetString(withSecrets), StringComparison.Ordinal);
        var secrets = BackupService.DecryptSecrets(BackupService.Parse(withSecrets), "correct horse battery staple");
        var token = Assert.Single(secrets!.Tokens!);
        Assert.Equal(("hf", Value), (token.Name, token.Value));
        Assert.Null(BackupService.Parse(withoutSecrets).Secrets);
    }
}
