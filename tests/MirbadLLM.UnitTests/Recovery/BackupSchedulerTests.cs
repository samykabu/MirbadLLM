using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.UnitTests.Orchestration.Fakes;
using MirbadLLM.UnitTests.Router.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Api;
using FakeModelRegistry = MirbadLLM.UnitTests.Web.Fakes.FakeModelRegistry;
using FakeModelRuntime = MirbadLLM.UnitTests.Web.Fakes.FakeModelRuntime;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Recovery;

/// <summary>Scheduled backups to a directory (write, prune, failure reporting), one-time download tokens and the /api/backup endpoints.</summary>
public sealed class BackupSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string BackupDir => Path.Combine(_root, "backups");

    private (BackupScheduler Scheduler, BackupDirectory Directory) Create(int keep = 3, string? passphrase = null, string? directory = null)
    {
        var options = Options.Create(new BackupOptions { Directory = directory ?? BackupDir, Keep = keep, Passphrase = passphrase, HostName = "dgx" });
        var registry = new FakeModelRegistry([new ModelDefinition { Id = "a", ServedName = "A", ModelPath = "A" }]);
        var service = new BackupService(registry, new FakeApiKeyStore(), options, _time);
        var dir = new BackupDirectory(options);
        return (new BackupScheduler(service, dir, options, _time, NullLogger<BackupScheduler>.Instance), dir);
    }

    [Fact]
    public async Task RunNow_WritesAtomicallyAndPrunesToKeep()
    {
        Directory.CreateDirectory(BackupDir);
        File.WriteAllText(Path.Combine(BackupDir, "notes.txt"), "not a backup");
        var (scheduler, directory) = Create(keep: 3);

        var results = new List<BackupRunResult>();
        for (int i = 0; i < 5; i++)
        {
            results.Add(await scheduler.RunNowAsync());
            _time.Advance(TimeSpan.FromDays(1));
        }

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error));
        var files = directory.List();
        Assert.Equal(3, files.Count);
        Assert.Equal(results[^1].FileName, files[0].Name);                    // newest first
        Assert.DoesNotContain(results[0].FileName, files.Select(f => f.Name));  // oldest pruned
        Assert.True(File.Exists(Path.Combine(BackupDir, "notes.txt")));       // other files untouched
        Assert.Empty(Directory.GetFiles(BackupDir, "*.tmp"));
        Assert.Equal(results[^1], scheduler.LastRun);
        Assert.False(results[^1].HasSecrets);
    }

    [Fact]
    public async Task RunNow_WithConfiguredPassphrase_IncludesSecrets()
    {
        Directory.CreateDirectory(BackupDir);
        var (scheduler, directory) = Create(passphrase: "scheduled passphrase");

        var result = await scheduler.RunNowAsync();

        Assert.True(result.HasSecrets);
        var parsed = BackupService.Parse(await File.ReadAllBytesAsync(directory.Resolve(result.FileName)!));
        Assert.NotNull(BackupService.DecryptSecrets(parsed, "scheduled passphrase"));
    }

    [Fact]
    public async Task MissingDirectory_IsReportedNotThrown_AndNotCreated()
    {
        var (scheduler, _) = Create();

        var result = await scheduler.RunNowAsync();
        var status = scheduler.GetStatus();

        Assert.False(result.Succeeded);
        Assert.Contains("does not exist", result.Error, StringComparison.Ordinal);
        Assert.False(status.DirectoryCheck.Ready);
        Assert.False(Directory.Exists(BackupDir));
        Assert.Empty(status.Files);
    }

    [Fact]
    public async Task UnwritableDirectory_IsReported()
    {
        // A file where the directory should be: Directory.Exists is false on every OS, and nothing can be written.
        Directory.CreateDirectory(_root);
        string notADirectory = Path.Combine(_root, "file-not-dir");
        File.WriteAllText(notADirectory, "x");
        var (scheduler, _) = Create(directory: notADirectory);

        var result = await scheduler.RunNowAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Disabled_WhenTheDirectoryIsEmpty()
    {
        var (scheduler, directory) = Create(directory: "");

        var status = scheduler.GetStatus();

        Assert.False(directory.Enabled);
        Assert.False(status.Enabled);
        Assert.Null(status.NextRunAt);
        Assert.Contains("off", status.DirectoryCheck.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-10-01T02:00:00", "2026-10-01T03:00:00")]
    [InlineData("2026-10-01T03:00:00", "2026-10-02T03:00:00")]
    [InlineData("2026-10-01T23:59:00", "2026-10-02T03:00:00")]
    public void NextRun_IsTheNextDailySlot(string now, string expected)
    {
        var next = BackupScheduler.NextRun(DateTimeOffset.Parse(now + "Z", System.Globalization.CultureInfo.InvariantCulture), new TimeOnly(3, 0), TimeZoneInfo.Utc);

        Assert.Equal(DateTimeOffset.Parse(expected + "Z", System.Globalization.CultureInfo.InvariantCulture), next);
    }

    [Theory]
    [InlineData("03:00", true)]
    [InlineData("3:30", true)]
    [InlineData("25:00", false)]
    [InlineData("noon", false)]
    public void DailyAt_Parsing(string value, bool valid) =>
        Assert.Equal(valid, new BackupOptions { DailyAt = value }.ParsedDailyAt is not null);

    [Theory]
    [InlineData("../secrets.json")]
    [InlineData("..%2Fsecrets.json")]
    [InlineData("mirbadllm-backup-dgx-20261001-030000.json/../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("mirbadllm-backup-../../x-20261001-030000.json")]
    [InlineData("models.json")]
    public void Resolve_RejectsAnythingButABareBackupName(string name)
    {
        Directory.CreateDirectory(BackupDir);
        var (_, directory) = Create();

        Assert.Null(directory.Resolve(name));
    }

    [Fact]
    public void Tokens_AreOneTimeAndExpire()
    {
        var tokens = new BackupDownloadTokens(_time);
        string first = tokens.Issue([1, 2, 3], "a.json");
        string second = tokens.Issue([4], "b.json");

        Assert.True(tokens.TryRedeem(first, out byte[] content, out string name));
        Assert.Equal([1, 2, 3], content);
        Assert.Equal("a.json", name);
        Assert.False(tokens.TryRedeem(first, out _, out _));

        _time.Advance(BackupDownloadTokens.Lifetime + TimeSpan.FromSeconds(1));
        Assert.False(tokens.TryRedeem(second, out _, out _));
        Assert.False(tokens.TryRedeem("made-up", out _, out _));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Api_DownloadTokenWorksOnce_FilesRejectTraversal_StatusHasNoPassphrase()
    {
        Directory.CreateDirectory(BackupDir);
        var (scheduler, directory) = Create(passphrase: "do-not-leak-this");
        var written = await scheduler.RunNowAsync();
        var tokens = new BackupDownloadTokens(_time);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton(tokens);
        builder.Services.AddSingleton(directory);
        builder.Services.AddSingleton(scheduler);
        builder.Services.AddSingleton(new RestoreJobService(
            new FakeModelRegistry(), new FakeApiKeyStore(), new FakeModelRuntime(), new Downloads.Fakes.FakeHuggingFaceDownloader(),
            new FakeCheckpointFolders(), new RestoreJobOptions(), TimeProvider.System, NullLogger<RestoreJobService>.Instance));
        await using var app = builder.Build();
        app.MapBackupApi();
        await app.StartAsync();
        using var client = app.GetTestClient();

        string token = tokens.Issue("{\"schemaVersion\":1}"u8.ToArray(), "mirbadllm-backup-dgx-20261001-030000.json");
        using var first = await client.GetAsync($"/api/backup/download/{token}");
        using var again = await client.GetAsync($"/api/backup/download/{token}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("attachment", first.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("mirbadllm-backup-dgx-20261001-030000.json", first.Content.Headers.ContentDisposition?.FileNameStar ?? first.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);

        using var file = await client.GetAsync($"/api/backup/files/{written.FileName}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        using var traversal = await client.GetAsync("/api/backup/files/..%2F..%2Fdata%2Fapikeys.json");
        Assert.True(traversal.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
        using var badName = await client.GetAsync("/api/backup/files/apikeys.json");
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        using var missing = await client.GetAsync("/api/backup/files/mirbadllm-backup-dgx-20200101-000000.json");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        string status = await client.GetStringAsync("/api/backup/status");
        Assert.DoesNotContain("do-not-leak-this", status, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(status);
        Assert.True(json.RootElement.GetProperty("scheduled").GetProperty("passphraseConfigured").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("scheduled").GetProperty("files").GetArrayLength());

        await app.StopAsync();
    }
}
