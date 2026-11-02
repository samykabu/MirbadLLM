using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using FakeModelRegistry = MirbadLLM.UnitTests.Web.Fakes.FakeModelRegistry;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Recovery;

/// <summary>Writing and reading backup files: round trips, the encrypted secrets section, and every way a file is refused.</summary>
public sealed class BackupServiceTests : IDisposable
{
    private const string Passphrase = "correct horse battery staple";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private static readonly ModelDefinition Alpha = new()
    {
        Id = "alpha",
        ServedName = "Alpha-9B",
        ModelPath = "Alpha-9B",
        MemoryBudgetGiB = 24,
        AutoStart = true,
        Tags = ["chat", "tool-calling"],
        Sources = [new ModelSource("owner/Alpha-9B", "Alpha-9B", "main", ["*.json", "*.safetensors"])],
    };

    private async Task<(BackupService Service, JsonApiKeyStore Keys, HostServicesStore Services)> CreateAsync()
    {
        var keys = new JsonApiKeyStore(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }), _time, NullLogger<JsonApiKeyStore>.Instance);
        await keys.CreateAsync("laptop-cline", ApiKeyScope.Inference);
        await keys.CreateAsync("prometheus", ApiKeyScope.Metrics);
        var services = new HostServicesStore(Path.Combine(_dataDir, "host-services.json"));
        await services.GetContainersAsync([new ManagedContainerOptions { Name = "openviking", Protected = true }]);
        var service = new BackupService(
            new FakeModelRegistry([Alpha]), keys, Options.Create(new BackupOptions { HostName = "dgx spark/01" }), _time, services);
        return (service, keys, services);
    }

    [Fact]
    public async Task RoundTrip_WithPassphrase_KeepsModelsServicesAndKeyHashes()
    {
        var (service, keys, _) = await CreateAsync();

        var document = await service.CreateAsync(Passphrase);
        byte[] bytes = BackupService.Serialize(document);
        var parsed = BackupService.Parse(bytes);
        var secrets = BackupService.DecryptSecrets(parsed, Passphrase);

        Assert.Equal(BackupDocument.MultiNodeSchemaVersion, parsed.SchemaVersion);
        Assert.Equal("dgx-spark-01", parsed.HostName);
        Assert.False(string.IsNullOrEmpty(parsed.AppVersion));
        var model = Assert.Single(parsed.Models);
        Assert.Equal(["chat", "tool-calling"], model.Tags);
        Assert.Equal("owner/Alpha-9B", Assert.Single(model.Sources).RepoId);
        Assert.Equal("openviking", Assert.Single(parsed.HostServicesFor("dgx")!.Containers!).Name);

        var original = await keys.ListAsync();
        Assert.NotNull(secrets);
        Assert.Equal(original.Select(k => (k.Id, k.KeyHash, k.Scope)), secrets.ApiKeys.Select(k => (k.Id, k.KeyHash, k.Scope)));

        // The hashes are only in the encrypted section, never in clear text.
        string text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"schemaVersion\": 2", text, StringComparison.Ordinal);
        Assert.All(original, k => Assert.DoesNotContain(k.KeyHash, text, StringComparison.Ordinal));
        Assert.DoesNotContain(Passphrase, text, StringComparison.Ordinal);
        Assert.Equal(BackupCrypto.Iterations, parsed.Secrets!.Iterations);
    }

    [Fact]
    public async Task RoundTrip_KeepsAnMlxModelsSettingsAndItsPlacementOnTheMac()
    {
        // macOS Phase 3/5: an MLX model is backed up and restored with every MLX setting and its Mac placement.
        var mlx = new ModelDefinition
        {
            Id = "qwen3-0-6b-mlx",
            ServedName = "Qwen3-0.6B-mlx",
            ModelPath = "Qwen3-0.6B-4bit",
            MemoryBudgetGiB = 1.4,
            Runtime = RuntimeKind.Mlx,
            Mlx = new MlxSettings
            {
                ModelFolder = "Qwen3-0.6B-4bit",
                ContextSize = 2048,
                Parallel = 1,
                PromptCacheGiB = 0.25,
                KvBits = 8,
                MaxTokens = 512,
                TrustRemoteCode = false,
                ExecutableVersion = "0.32.2",
                ExtraArgs = ["--temp", "0.6"],
            },
            Placement = new ModelPlacement("mac-1"),
        };
        var keys = new JsonApiKeyStore(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }), _time, NullLogger<JsonApiKeyStore>.Instance);
        var service = new BackupService(new FakeModelRegistry([Alpha, mlx]), keys, Options.Create(new BackupOptions { HostName = "dgx" }), _time);

        var parsed = BackupService.Parse(BackupService.Serialize(await service.CreateAsync(passphrase: null)));

        var restored = Assert.Single(parsed.Models, m => m.Id == "qwen3-0-6b-mlx");
        Assert.Equal(RuntimeKind.Mlx, restored.Runtime);
        Assert.Equal(mlx.Mlx, restored.Mlx); // MlxSettings compares every field, ExtraArgs by sequence
        Assert.Equal("mac-1", restored.Placement!.NodeId);
        Assert.Null(Assert.Single(parsed.Models, m => m.Id == "alpha").Mlx);
    }

    [Fact]
    public async Task RoundTrip_WithoutPassphrase_HasNoSecretsSection()
    {
        var (service, _, _) = await CreateAsync();

        var document = await service.CreateAsync(passphrase: null);
        byte[] bytes = BackupService.Serialize(document);
        var parsed = BackupService.Parse(bytes);

        Assert.Null(parsed.Secrets);
        Assert.DoesNotContain("secrets", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);
        Assert.Null(BackupService.DecryptSecrets(parsed, "anything"));
        Assert.Single(parsed.Models);
    }

    [Fact]
    public async Task WrongPassphrase_IsAClearErrorAndNothingIsReturned()
    {
        var (service, _, _) = await CreateAsync();
        var parsed = BackupService.Parse(BackupService.Serialize(await service.CreateAsync(Passphrase)));

        var ex = Assert.Throws<BackupPassphraseException>(() => BackupService.DecryptSecrets(parsed, "wrong passphrase"));
        Assert.Contains("Wrong passphrase", ex.Message, StringComparison.Ordinal);
        Assert.Throws<BackupPassphraseException>(() => BackupService.DecryptSecrets(parsed, ""));
    }

    [Fact]
    public async Task TamperedCiphertext_IsRejected()
    {
        var (service, _, _) = await CreateAsync();
        var json = JsonNode.Parse(BackupService.Serialize(await service.CreateAsync(Passphrase)))!;
        byte[] ciphertext = Convert.FromBase64String(json["secrets"]!["ciphertext"]!.GetValue<string>());
        ciphertext[0] ^= 0x01;
        json["secrets"]!["ciphertext"] = Convert.ToBase64String(ciphertext);

        var parsed = BackupService.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()));

        Assert.Throws<BackupPassphraseException>(() => BackupService.DecryptSecrets(parsed, Passphrase));
    }

    [Fact]
    public async Task WeakenedIterationCount_IsRefusedBeforeDecrypting()
    {
        var (service, _, _) = await CreateAsync();
        var json = JsonNode.Parse(BackupService.Serialize(await service.CreateAsync(Passphrase)))!;
        json["secrets"]!["iterations"] = 1000;

        Assert.Throws<BackupFormatException>(() => BackupService.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 3, "models": [] }""", "schema version 3")]
    [InlineData("""{ "models": [] }""", "no schemaVersion")]
    [InlineData("""[1, 2, 3]""", "no schemaVersion")]
    [InlineData("""not json""", "not valid JSON")]
    [InlineData("""{ "schemaVersion": 1, "models": [ { "id": "a", "servedName": "A", "modelPath": "A" }, { "id": "a", "servedName": "B", "modelPath": "B" } ] }""", "twice")]
    public void Parse_RefusesFilesItCannotRead(string content, string expected)
    {
        var ex = Assert.Throws<BackupFormatException>(() => BackupService.Parse(Encoding.UTF8.GetBytes(content)));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FillsListsMissingFromAHandWrittenFile()
    {
        var parsed = BackupService.Parse(Encoding.UTF8.GetBytes("""
            { "schemaVersion": 1, "createdAt": "2026-10-01T03:00:00Z", "models": [ { "id": "a", "servedName": "A", "modelPath": "A" } ] }
            """));

        var model = Assert.Single(parsed.Models);
        Assert.Empty(model.Tags);
        Assert.Empty(model.Sources);
        Assert.Empty(model.ExtraArgs);
    }

    [Fact]
    public void FileName_IsHostAndUtcTimestamp()
    {
        var document = new BackupDocument { HostName = "dgx-spark", CreatedAt = new DateTimeOffset(2026, 10, 1, 5, 4, 3, TimeSpan.FromHours(2)) };

        Assert.Equal("mirbadllm-backup-dgx-spark-20261001-030403.json", BackupService.FileName(document));
        Assert.True(BackupDirectory.IsBackupFileName(BackupService.FileName(document)));
    }

    [Fact]
    public async Task ApiKeyImport_MergesByIdAndHash_NeverDuplicates()
    {
        var (service, keys, _) = await CreateAsync();
        var secrets = BackupService.DecryptSecrets(BackupService.Parse(BackupService.Serialize(await service.CreateAsync(Passphrase))), Passphrase)!;
        var fresh = new JsonApiKeyStore(Options.Create(new MirbadLLMOptions { DataPath = Path.Combine(_dataDir, "fresh") }), _time, NullLogger<JsonApiKeyStore>.Instance);

        var first = await fresh.ImportAsync(secrets.ApiKeys);
        var second = await fresh.ImportAsync(secrets.ApiKeys);
        var renamedDuplicate = await fresh.ImportAsync([secrets.ApiKeys[0] with { Id = "other-id" }]);

        Assert.Equal(new ApiKeyImportResult(2, 0), first);
        Assert.Equal(new ApiKeyImportResult(0, 2), second);
        Assert.Equal(new ApiKeyImportResult(0, 1), renamedDuplicate);
        Assert.Equal(2, (await fresh.ListAsync()).Count);
        Assert.True(await fresh.HasAnyKeyAsync(ApiKeyScope.Inference));
        Assert.True(await fresh.HasAnyKeyAsync(ApiKeyScope.Metrics));

        // The original plaintext keys keep working against the restored hashes.
        var original = await keys.ListAsync();
        Assert.Equal(original.Select(k => k.KeyHash).Order(), (await fresh.ListAsync()).Select(k => k.KeyHash).Order());
    }

    [Fact]
    public async Task ApiKeyImport_RejectsRecordsWithoutAHash()
    {
        var store = new JsonApiKeyStore(Options.Create(new MirbadLLMOptions { DataPath = _dataDir }), _time, NullLogger<JsonApiKeyStore>.Instance);
        var bad = new ApiKeyRecord("id", "n", ApiKeyScope.Inference, "not-a-hash", "abcd", DateTimeOffset.UtcNow, null, null);

        await Assert.ThrowsAsync<ArgumentException>(() => store.ImportAsync([bad]));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task HostServicesImport_ReplacesTheContainerList()
    {
        var (_, _, services) = await CreateAsync();
        var other = new HostServicesStore(Path.Combine(_dataDir, "other", "host-services.json"));

        await other.ImportAsync(await services.ExportAsync());
        var list = await other.GetContainersAsync([new ManagedContainerOptions { Name = "seed-default" }]);

        Assert.Equal("openviking", Assert.Single(list).Name);
        Assert.True(list[0].Protected);
    }

    [Fact]
    public void Serialize_UsesCamelCaseAndStringEnums()
    {
        var document = new BackupDocument { Models = [Alpha] };
        using var json = JsonDocument.Parse(BackupService.Serialize(document));

        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(json.RootElement.GetProperty("models")[0].TryGetProperty("servedName", out _));
    }
}
