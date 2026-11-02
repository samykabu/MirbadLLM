using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.UnitTests.HostServices;
using MirbadLLM.UnitTests.Downloads.Fakes;
using MirbadLLM.UnitTests.Recovery;
using MirbadLLM.UnitTests.Router.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Components.Pages;
using FakeModelRuntime = MirbadLLM.UnitTests.Web.Fakes.FakeModelRuntime;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Web;

/// <summary>bUnit basics for the Backup &amp; restore page: the back-up tab's warnings and download, and a restore from upload to progress.</summary>
public sealed class BackupPageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed record Page(Bunit.BunitContext Context, FakeModelRegistry Registry, BackupService Backups, BackupDownloadTokens Tokens, RestoreJobService Restores, Bunit.TestDoubles.BunitAuthorizationContext Auth);

    private Page Create(string? backupDirectory = null, string? passphrase = null, params ModelDefinition[] models)
    {
        var ctx = new Bunit.BunitContext();
        var auth = ctx.AddAuthorization();
        auth.SetAuthorized("admin");
        auth.SetClaims(TestAdmin.User.Claims.ToArray());
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var registry = new FakeModelRegistry(models);
        var keys = new FakeApiKeyStore();
        keys.AddActiveKey("ssk_laptop", ApiKeyScope.Inference, "laptop");
        var options = Options.Create(new BackupOptions { Directory = backupDirectory ?? Path.Combine(_root, "missing"), Passphrase = passphrase, HostName = "dgx" });
        var backups = new BackupService(registry, keys, options, TimeProvider.System);
        var directory = new BackupDirectory(options);
        var tokens = new BackupDownloadTokens(TimeProvider.System);
        var folders = new FakeCheckpointFolders();
        var restores = new RestoreJobService(registry, keys, new FakeModelRuntime(), new SequencedDownloader(folders), folders,
            new RestoreJobOptions { PollInterval = TimeSpan.FromMilliseconds(1) }, TimeProvider.System, NullLogger<RestoreJobService>.Instance);

        ctx.Services.AddSingleton(backups);
        ctx.Services.AddSingleton(tokens);
        ctx.Services.AddSingleton(new BackupScheduler(backups, directory, options, TimeProvider.System, NullLogger<BackupScheduler>.Instance));
        ctx.Services.AddSingleton(new RestorePlanner(registry, keys, folders, new FakeHuggingFaceRepoInspector()));
        ctx.Services.AddSingleton(restores);
        return new Page(ctx, registry, backups, tokens, restores, auth);
    }

    [Fact]
    public void BackUpTab_WarnsWithoutPassphrase_ChecksTheConfirmation_AndReportsTheDirectory()
    {
        var page = Create();
        using var ctx = page.Context;

        var cut = ctx.Render<Backup>();

        Assert.Equal("true", cut.Find("[data-testid=backup-tabs-tab-backup]").GetAttribute("aria-selected"));
        Assert.Equal("Backups need setup", cut.Find("#scheduled-title").TextContent);
        Assert.Contains("backups cannot run", cut.Find(".hub-title").TextContent, StringComparison.Ordinal);
        Assert.Equal(4, cut.FindAll("[data-testid=schedule-facts] > div").Count);
        Assert.Contains("API keys won't be restored", cut.Find("[data-testid=no-passphrase-warning]").TextContent, StringComparison.Ordinal);
        // Setup problems are one checklist, Needed then Recommended then Optional, each with the fix; raw messages sit behind Details.
        var checklist = cut.Find("[data-testid=setup-checklist]");
        Assert.Equal("Setup needed", checklist.QuerySelector("h3")!.TextContent);
        Assert.Equal(["backup-directory-problem", "scheduled-no-passphrase", "destination-agent-missing"],
            checklist.QuerySelectorAll(".setup-item").Select(i => i.GetAttribute("data-testid")));
        var directoryItem = cut.Find("[data-testid=backup-directory-problem]");
        Assert.Equal("needed", directoryItem.GetAttribute("data-level"));
        Assert.Contains("does not exist", directoryItem.QuerySelector("details.setup-details")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("restart MirbadLLM", directoryItem.QuerySelector(".setup-fix")!.TextContent, StringComparison.Ordinal);
        Assert.Equal("recommended", cut.Find("[data-testid=scheduled-no-passphrase]").GetAttribute("data-level"));
        Assert.Empty(cut.FindAll("[data-testid=scheduled-panel] .callout"));
        Assert.True(cut.Find("[data-testid=backup-now]").HasAttribute("disabled"));
        Assert.False(cut.Find("[data-testid=download-backup]").HasAttribute("disabled"));

        cut.Find("[data-testid=backup-passphrase]").Input("short");
        Assert.Contains("Too short", cut.Find("[data-testid=passphrase-strength]").TextContent, StringComparison.Ordinal);
        Assert.Equal("1", cut.Find("[data-testid=passphrase-meter]").GetAttribute("aria-valuenow"));
        Assert.Equal(4, cut.FindAll("[data-testid=passphrase-meter] span").Count);
        Assert.True(cut.Find("[data-testid=download-backup]").HasAttribute("disabled"));

        cut.Find("[data-testid=backup-passphrase]").Input("a long enough passphrase 42");
        cut.Find("[data-testid=backup-passphrase-confirm]").Input("something else");
        Assert.NotNull(cut.Find("[data-testid=passphrase-mismatch]"));
        Assert.Equal("true", cut.Find("[data-testid=backup-passphrase-confirm]").GetAttribute("aria-invalid"));
        Assert.True(cut.Find("[data-testid=download-backup]").HasAttribute("disabled"));

        cut.Find("[data-testid=backup-passphrase-confirm]").Input("a long enough passphrase 42");
        Assert.Contains("Strong", cut.Find("[data-testid=passphrase-strength]").TextContent, StringComparison.Ordinal);
        Assert.Equal(4, cut.FindAll("[data-testid=passphrase-meter] [data-on=true]").Count);
        Assert.False(cut.Find("[data-testid=download-backup]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=no-passphrase-warning]"));
    }

    [Fact]
    public void BackUpTab_WithoutADirectory_SaysScheduledBackupsAreOff()
    {
        var page = Create(backupDirectory: "");
        using var ctx = page.Context;
        var cut = ctx.Render<Backup>();
        Assert.Equal("Scheduled backups are off", cut.Find("#scheduled-title").TextContent);
    }

    [Fact]
    public void Download_IssuesAOneTimeTokenForTheGeneratedFile()
    {
        var page = Create(models: new ModelDefinition { Id = "a", ServedName = "A", ModelPath = "A" });
        using var ctx = page.Context;
        var cut = ctx.Render<Backup>();

        cut.Find("[data-testid=download-backup]").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=download-ready]")), TimeSpan.FromSeconds(10));
        Assert.Matches(@"mirbadllm-backup-dgx-\d{8}-\d{6}\.json", cut.Find("[data-testid=download-ready]").TextContent);
    }

    [Fact]
    public void ScheduledBackups_ListFilesWithDownloadLinks()
    {
        string dir = Path.Combine(_root, "backups");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "mirbadllm-backup-dgx-20261001-030000.json"), "{}");
        var page = Create(backupDirectory: dir, passphrase: "configured");
        using var ctx = page.Context;

        var cut = ctx.Render<Backup>();

        var link = cut.Find("[data-testid='backup-file-mirbadllm-backup-dgx-20261001-030000.json'] a");
        Assert.Equal("api/backup/files/mirbadllm-backup-dgx-20261001-030000.json", link.GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-testid=backup-directory-problem]"));
        Assert.Empty(cut.FindAll("[data-testid=scheduled-no-passphrase]"));
        Assert.False(cut.Find("[data-testid=backup-now]").HasAttribute("disabled"));
        Assert.Equal("Backups run every day", cut.Find("#scheduled-title").TextContent);
    }

    [Fact]
    public void BackupFiles_ArePaged_WithTheirOwnUrlKeys()
    {
        string dir = Path.Combine(_root, "many");
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 30; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"mirbadllm-backup-dgx-202610{i + 1:00}-030000.json"), "{}");
        }

        var page = Create(backupDirectory: dir, passphrase: "configured");
        using var ctx = page.Context;
        var nav = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var cut = ctx.Render<Backup>();

        Assert.Equal(25, cut.FindAll("[data-testid=backup-files] tbody tr").Count);
        Assert.Equal("1–25 of 30 files", cut.Find("[data-testid=backup-files-pager-range]").TextContent);

        cut.Find("[data-testid=backup-files-pager-size]").Change("10");
        Assert.Equal(10, cut.FindAll("[data-testid=backup-files] tbody tr").Count);
        Assert.Contains("files-size=10", nav.Uri, StringComparison.Ordinal);
        cut.Find("[data-testid=backup-files-pager-page-3]").Click();
        Assert.Contains("files-page=3", nav.Uri, StringComparison.Ordinal);
        Assert.Equal("21–30 of 30 files", cut.Find("[data-testid=backup-files-pager-range]").TextContent);
    }

    [Fact]
    public async Task RestorePreview_IsPaged_AndSelectAllStillCoversEveryModel()
    {
        var models = Enumerable.Range(0, 30)
            .Select(i => new ModelDefinition { Id = $"m{i:00}", ServedName = $"M{i:00}", ModelPath = $"M{i:00}" })
            .ToArray();
        var source = Create(models: models);
        var document = await source.Backups.CreateAsync("restore passphrase");
        byte[] bytes = BackupService.Serialize(document);
        source.Context.Dispose();

        var page = Create();
        using var ctx = page.Context;
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("backup?tab=restore");
        var cut = ctx.Render<Backup>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(bytes, BackupService.FileName(document)));
        cut.Find("[data-testid=restore-passphrase]").Change("restore passphrase");
        cut.Find("[data-testid=preview-restore]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=preview-pager]")), TimeSpan.FromSeconds(10));

        Assert.Equal(25, cut.FindAll("[data-testid=preview-table] tbody tr").Count);
        Assert.Equal("1–25 of 30 models", cut.Find("[data-testid=preview-pager-range]").TextContent);
        Assert.Contains("30 of 30 selected", cut.Find("[data-testid=preview-selected]").TextContent, StringComparison.Ordinal);

        cut.Find("[data-testid=preview-pager-next]").Click();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid=preview-table] tbody tr").Count), TimeSpan.FromSeconds(5));
        Assert.NotNull(cut.Find("[data-testid=preview-row-m29]"));
    }

    [Fact]
    public async Task Restore_UploadPreviewConfirm_ShowsProgress()
    {
        var model = new ModelDefinition
        {
            Id = "alpha", ServedName = "Alpha", ModelPath = "Alpha", Tags = ["chat"], Sources = [new ModelSource("o/Alpha", "Alpha")],
        };
        var source = Create(models: model);
        var document = await source.Backups.CreateAsync("restore passphrase");
        byte[] bytes = BackupService.Serialize(document);
        source.Context.Dispose();

        var page = Create();
        using var ctx = page.Context;
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("backup?tab=restore");
        var cut = ctx.Render<Backup>();
        Assert.Equal(3, cut.FindAll("[data-testid=restore-steps] > li").Count);
        Assert.Contains("Choose a backup file", cut.Find("[data-testid=restore-steps] [aria-current=step]").TextContent, StringComparison.Ordinal);

        // A wrong passphrase is refused before anything is shown or changed.
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(bytes, BackupService.FileName(document)));
        cut.Find("[data-testid=restore-passphrase]").Change("wrong one");
        cut.Find("[data-testid=preview-restore]").Click();
        cut.WaitForAssertion(() => Assert.Contains("Wrong passphrase", cut.Find("[data-testid=restore-error]").TextContent, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        Assert.Empty(await page.Registry.ListAsync());

        cut.Find("[data-testid=restore-passphrase]").Change("restore passphrase");
        cut.Find("[data-testid=preview-restore]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=preview-row-alpha]")), TimeSpan.FromSeconds(10));
        Assert.Contains("Pick what to restore", cut.Find("[data-testid=restore-steps] [aria-current=step]").TextContent, StringComparison.Ordinal);
        Assert.Contains("New", cut.Find("[data-testid=change-alpha]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Download", cut.Find("[data-testid=checkpoint-alpha]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 already here", cut.Find("[data-testid=preview-keys]").TextContent, StringComparison.Ordinal); // the laptop key
        Assert.True(cut.Find("[data-testid=select-alpha]").HasAttribute("checked"));

        cut.Find("[data-testid=restore-button]").Click();
        var plan = cut.Find("[data-testid=restore-plan]").TextContent;
        Assert.Contains("Writes 1 model definition: 1 new", plan, StringComparison.Ordinal);
        Assert.Contains("Downloads 1 checkpoint folder", plan, StringComparison.Ordinal);
        cut.Find("[data-testid=confirm-restore]").Click();

        await page.Restores.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        cut.WaitForAssertion(() => Assert.Equal("Restore", cut.Find("[data-testid=restore-steps] [aria-current=step] .wizard-step-label").TextContent),
            TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.Contains("Ready", cut.Find("[data-testid=restore-row-alpha]").TextContent, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        Assert.NotNull(await page.Registry.GetAsync("alpha"));

        // The finished state re-renders through the job's Changed event, after Completion.
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=new-restore]")), TimeSpan.FromSeconds(10));
        Assert.Contains("Finished", cut.Find("[data-testid=restore-job-state]").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restore_WithServicesList_UsesTheSession_AndAnonymousRestoresNothing(bool signedIn)
    {
        var model = new ModelDefinition { Id = "alpha", ServedName = "Alpha", ModelPath = "Alpha", Sources = [new ModelSource("o/Alpha", "Alpha")] };
        var source = Create(models: model);
        var document = (await source.Backups.CreateAsync("restore passphrase")) with { HostServices = new HostServicesBackup(null, []) };
        byte[] bytes = BackupService.Serialize(document);
        source.Context.Dispose();

        var page = Create();
        if (!signedIn) page.Auth.SetNotAuthorized();
        using var ctx = page.Context;
        var reauth = new FakeReauth();
        ctx.Services.AddSingleton<IAdminReauthenticator>(reauth);
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("backup?tab=restore");
        var cut = ctx.Render<Backup>();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(bytes, BackupService.FileName(document)));
        cut.Find("[data-testid=restore-passphrase]").Change("restore passphrase");
        cut.Find("[data-testid=preview-restore]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=preview-row-alpha]")), TimeSpan.FromSeconds(10));

        cut.Find("[data-testid=restore-button]").Click();
        Assert.Empty(cut.FindAll("[data-testid=restore-admin-password]"));
        Assert.False(cut.Find("[data-testid=confirm-restore]").HasAttribute("disabled"));
        cut.Find("[data-testid=confirm-restore]").Click();
        if (!signedIn)
        {
            cut.WaitForAssertion(() => Assert.Contains("Sign in to the portal", cut.Find("[data-testid=restore-confirm-error]").TextContent, StringComparison.Ordinal));
            Assert.Null(page.Restores.Current);
            Assert.Empty(await page.Registry.ListAsync());
            return;
        }
        cut.WaitForAssertion(() => Assert.NotNull(page.Restores.Current), TimeSpan.FromSeconds(10));
        await page.Restores.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(await page.Registry.GetAsync("alpha"));
        Assert.Equal(1, reauth.Calls);
    }

    [Fact]
    public void BackUpTab_WithoutAScheduledPassphrase_NamesEverySecretLeftOut()
    {
        var page = Create();
        using var ctx = page.Context;
        var cut = ctx.Render<Backup>();

        var item = cut.Find("[data-testid=scheduled-no-passphrase]");
        Assert.Contains("Scheduled backups carry no secrets", item.TextContent, StringComparison.Ordinal);
        Assert.Contains("the API keys, the saved tokens, the backup share's password and the CA", item.TextContent, StringComparison.Ordinal);
        Assert.Contains("MirbadLLM:Backup:Passphrase", item.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_ShowsRunningModelsHistoryAndAgentUpdates_AsOptions_AndHonoursUntickingStartRunning()
    {
        var model = new ModelDefinition { Id = "alpha", ServedName = "Alpha", ModelPath = "Alpha", Sources = [new ModelSource("o/Alpha", "Alpha")] };
        var source = Create(models: model);
        var document = (await source.Backups.CreateAsync(null)) with
        {
            RunningModels = new Dictionary<string, IReadOnlyList<string>> { ["dgx"] = ["alpha"] },
            AgentUpdatesAutomatic = true,
            History = new BackupHistory([], [], new Dictionary<string, double> { ["alpha"] = 90 }),
        };
        byte[] bytes = BackupService.Serialize(document);
        source.Context.Dispose();

        var page = Create();
        using var ctx = page.Context;
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("backup?tab=restore");
        var cut = ctx.Render<Backup>();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(bytes, BackupService.FileName(document)));
        cut.Find("[data-testid=preview-restore]").Click();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=preview-row-alpha]")), TimeSpan.FromSeconds(10));

        Assert.Contains("1 of the backup's models were running", cut.Find("[data-testid=preview-running]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Was running", cut.Find("[data-testid=autostart-alpha]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1 start time", cut.Find("[data-testid=preview-history]").TextContent, StringComparison.Ordinal);
        Assert.Contains("Automatic", cut.Find("[data-testid=preview-agent-updates]").TextContent, StringComparison.Ordinal);
        Assert.True(cut.Find("[data-testid=start-running]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=apply-node-settings]").HasAttribute("checked"));
        Assert.True(cut.Find("[data-testid=import-history]").HasAttribute("checked"));
        Assert.Empty(cut.FindAll("[data-testid=apply-destination]")); // nothing to apply without a destination

        cut.Find("[data-testid=restore-button]").Click();
        string plan = cut.Find("[data-testid=restore-plan]").TextContent;
        Assert.Contains("1 that was running when the backup was made", plan, StringComparison.Ordinal);
        Assert.Contains("sets automatic agent updates on", plan, StringComparison.Ordinal);
        Assert.Contains("1 start time that are not here", plan, StringComparison.Ordinal);

        cut.Find("[data-testid=start-running]").Change(false);
        cut.Find("[data-testid=import-history]").Change(false);
        plan = cut.Find("[data-testid=restore-plan]").TextContent;
        Assert.DoesNotContain("Then starts", plan, StringComparison.Ordinal);
        Assert.Contains("Leaves the benchmark, tuning and start-time history as it is.", plan, StringComparison.Ordinal);
        cut.Find("[data-testid=confirm-restore]").Click();

        await page.Restores.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        cut.WaitForAssertion(() => Assert.Contains("Ready", cut.Find("[data-testid=restore-row-alpha]").TextContent, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    // ---- Destination panel (NAS share) ----

    private static (BackupDestinationManager Manager, FakeBackupMountClient Client, FakeReauth Reauth) AddDestination(Bunit.BunitContext ctx, FakeBackupMountClient? client = null)
    {
        client ??= new FakeBackupMountClient();
        var reauth = new FakeReauth();
        var directory = new BackupDirectory(Options.Create(new BackupOptions { Directory = "/backups" }),
            () => client.Mount.Mounted ? "650 600 0:61 / /backups rw - cifs //192.0.2.200/backups/mirbadllm rw" : "");
        var manager = new BackupDestinationManager(client, directory, NullLogger<BackupDestinationManager>.Instance, reauth);
        ctx.Services.AddSingleton<IAdminReauthenticator>(reauth);
        ctx.Services.AddSingleton<IBackupDestinationManager>(manager);
        return (manager, client, reauth);
    }

    [Fact]
    public void Destination_WithoutTheHostAgent_StaysOnTheLocalDisk_AndSaysWhy()
    {
        var page = Create();
        using var ctx = page.Context;
        AddDestination(ctx, new FakeBackupMountClient { Unavailable = new HostAgentUnavailableException("The host agent is not installed (no socket).") });

        var cut = ctx.Render<Backup>();

        Assert.Contains("dgx's own disk, at /srv/mirbadllm/backups", cut.Find("[data-testid=destination-status]").TextContent, StringComparison.Ordinal);
        // One checklist item names the node; the raw agent message is only behind its Details disclosure.
        var item = cut.Find("[data-testid=setup-checklist] [data-testid=destination-agent-missing]");
        Assert.Equal("optional", item.GetAttribute("data-level"));
        Assert.Contains("A NAS share can't be set up on dgx yet", item.QuerySelector(".setup-title")!.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("host agent isn't installed", item.QuerySelector(".setup-fix")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("host agent isn't installed", item.QuerySelector("details.setup-details")!.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=destination-kind]"));
        Assert.Empty(cut.FindAll("[data-testid=destination-save]"));
    }

    [Fact]
    public void Destination_ShowsTheFieldsOfEachKind()
    {
        var page = Create();
        using var ctx = page.Context;
        AddDestination(ctx);
        var cut = ctx.Render<Backup>();

        Assert.True(cut.Find("[data-testid=destination-kind-local]").HasAttribute("checked"));
        Assert.Contains("on dgx's disk", cut.Find("[data-testid=destination-local-note]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=destination-fields]"));
        Assert.Empty(cut.FindAll("[data-testid=destination-disconnect]")); // nothing to disconnect

        cut.Find("[data-testid=destination-kind-smb]").Change(true);
        var server = cut.Find("[data-testid=destination-server]");
        Assert.Equal("192.0.2.200", server.GetAttribute("placeholder"));
        Assert.True(string.IsNullOrEmpty(server.GetAttribute("value")));
        Assert.Equal("destination-server-hint", server.GetAttribute("aria-describedby"));
        foreach (string id in new[] { "share", "subfolder", "username", "password", "domain", "smb-version" })
        {
            Assert.NotNull(cut.Find($"[data-testid=destination-{id}]"));
            Assert.NotNull(cut.Find($"[data-testid=info-toggle-destination-{id}]"));
        }

        Assert.Equal("password", cut.Find("[data-testid=destination-password]").GetAttribute("type"));
        Assert.Equal("3.0", cut.Find("[data-testid=destination-smb-version]").GetAttribute("value"));
        Assert.True(cut.Find("[data-testid=destination-test]").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid=destination-save]").HasAttribute("disabled"));

        cut.Find("[data-testid=destination-kind-nfs]").Change(true);
        Assert.Equal("/volume1/backups", cut.Find("[data-testid=destination-share]").GetAttribute("placeholder"));
        Assert.NotNull(cut.Find("[data-testid=destination-nfs-version]"));
        Assert.Empty(cut.FindAll("[data-testid=destination-username]"));
        Assert.Empty(cut.FindAll("[data-testid=destination-password]"));
        Assert.Empty(cut.FindAll("[data-testid=destination-domain]"));
    }

    [Fact]
    public void Destination_TestConnection_ShowsTheResultInline()
    {
        var page = Create();
        using var ctx = page.Context;
        var (_, client, reauth) = AddDestination(ctx, new FakeBackupMountClient
        {
            TestAnswer = new HostAgentBackupMountTest(false, "Could not mount //192.0.2.200/backups: mount error(13): Permission denied — check the username and password."),
        });
        var cut = ctx.Render<Backup>();

        FillSmb(cut, password: "nas-secret");
        cut.Find("[data-testid=destination-test]").Click();

        cut.WaitForAssertion(() => Assert.Contains("check the username and password", cut.Find("[data-testid=destination-test-result]").TextContent, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
        Assert.Contains("callout--failed", cut.Find("[data-testid=destination-test-result]").ClassName, StringComparison.Ordinal);
        var tested = Assert.Single(client.Tested);
        Assert.Equal("nas-secret", tested.Password);
        Assert.Equal(0, reauth.Calls); // a test changes nothing, so it needs no admin password
        Assert.Empty(client.Sent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Destination_Save_UsesTheSession_AndAnonymousChangesNothing(bool signedIn)
    {
        var page = Create();
        if (!signedIn) page.Auth.SetNotAuthorized();
        using var ctx = page.Context;
        var (_, client, reauth) = AddDestination(ctx);
        var cut = ctx.Render<Backup>();

        FillSmb(cut, password: "nas-secret");
        cut.Find("[data-testid=destination-save]").Click();

        var plan = cut.Find("[data-testid=destination-save-plan]").TextContent;
        Assert.Contains("mounts //192.0.2.200/backups/mirbadllm at /srv/mirbadllm/backups on dgx", plan, StringComparison.Ordinal);
        Assert.Contains("Existing local backups stay on the local disk underneath", plan, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=destination-admin-password]"));
        Assert.False(cut.Find("[data-testid=destination-save-confirm]").HasAttribute("disabled"));
        cut.Find("[data-testid=destination-save-confirm]").Click();
        if (!signedIn)
        {
            cut.WaitForAssertion(() => Assert.Contains("Sign in to the portal", cut.Find("[data-testid=destination-save-error]").TextContent, StringComparison.Ordinal));
            Assert.Empty(client.Sent);
            Assert.Equal("true", cut.Find("[data-testid=destination-save-dialog]").GetAttribute("data-open"));
            return;
        }
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("[data-testid=destination-save-dialog]").GetAttribute("data-open")), TimeSpan.FromSeconds(10));

        var sent = Assert.Single(client.Sent);
        Assert.Equal("nas-secret", sent.Password);
        Assert.Equal(1, reauth.Calls);
        Assert.Contains("Connected", cut.Find("[data-testid=destination-status]").TextContent, StringComparison.Ordinal);
        Assert.Contains("//192.0.2.200/backups/mirbadllm (mounted, 1.2 TB free of 4 TB)", cut.Find("[data-testid=destination-status]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1.2 TB free", cut.Find("[data-testid=effective-destination]").TextContent, StringComparison.Ordinal);

        // The share password is not kept in the form, so it is never rendered back.
        Assert.True(string.IsNullOrEmpty(cut.Find("[data-testid=destination-password]").GetAttribute("value")));
        Assert.DoesNotContain("nas-secret", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("dgx-backup", cut.Find("[data-testid=destination-username]").GetAttribute("value"));
    }

    [Fact]
    public void Destination_Disconnect_UsesTheSignedInSession()
    {
        var page = Create();
        using var ctx = page.Context;
        var (_, client, _) = AddDestination(ctx, new FakeBackupMountClient { Mount = FakeBackupMountClient.Configured(mounted: true) });
        var cut = ctx.Render<Backup>();

        Assert.True(cut.Find("[data-testid=destination-kind-smb]").HasAttribute("checked"));
        Assert.Equal("192.0.2.200", cut.Find("[data-testid=destination-server]").GetAttribute("value"));

        cut.Find("[data-testid=destination-kind-local]").Change(true);
        cut.Find("[data-testid=destination-disconnect]").Click();
        Assert.False(cut.Find("[data-testid=destination-disconnect-confirm]").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("[data-testid=destination-disconnect-password]"));
        cut.Find("[data-testid=destination-disconnect-confirm]").Click();

        cut.WaitForAssertion(() => Assert.Equal(1, client.Removes), TimeSpan.FromSeconds(10));
        cut.WaitForAssertion(() => Assert.Contains("Local disk", cut.Find("[data-testid=destination-status]").TextContent, StringComparison.Ordinal),
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Destination_ShareConfiguredButNotVisible_WarnsThatBackupsAreSkipped()
    {
        var page = Create();
        using var ctx = page.Context;
        var client = new FakeBackupMountClient { Mount = FakeBackupMountClient.Configured(mounted: false) };
        AddDestination(ctx, client);

        var cut = ctx.Render<Backup>();

        Assert.Contains("Not mounted", cut.Find("[data-testid=destination-status]").TextContent, StringComparison.Ordinal);
        Assert.Contains("scheduled backups are skipped", cut.Find("[data-testid=destination-share-missing]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_ShowsWhereBackupsWereGoing_AndCanPrefillTheDestination()
    {
        var source = Create();
        var document = (await source.Backups.CreateAsync(null)) with
        {
            BackupDestination = new BackupDestinationInfo("smb", "192.0.2.200", "backups", "mirbadllm", "dgx-backup", null, "3.0", "//192.0.2.200/backups/mirbadllm"),
        };
        byte[] bytes = BackupService.Serialize(document);
        source.Context.Dispose();

        var page = Create();
        using var ctx = page.Context;
        AddDestination(ctx);
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("backup?tab=restore");
        var cut = ctx.Render<Backup>();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(bytes, BackupService.FileName(document)));
        cut.Find("[data-testid=preview-restore]").Click();
        cut.WaitForAssertion(() => Assert.Contains("Backups were going to //192.0.2.200/backups/mirbadllm; re-enter the password to reconnect",
            cut.Find("[data-testid=preview-destination]").TextContent, StringComparison.Ordinal), TimeSpan.FromSeconds(10));

        cut.Find("[data-testid=use-backup-destination]").Click();
        cut.WaitForAssertion(() => Assert.Equal("192.0.2.200", cut.Find("[data-testid=destination-server]").GetAttribute("value")), TimeSpan.FromSeconds(10));
        Assert.True(cut.Find("[data-testid=destination-kind-smb]").HasAttribute("checked"));
        Assert.Equal("mirbadllm", cut.Find("[data-testid=destination-subfolder]").GetAttribute("value"));
        Assert.True(string.IsNullOrEmpty(cut.Find("[data-testid=destination-password]").GetAttribute("value")));
    }

    private static void FillSmb(IRenderedComponent<Backup> cut, string password)
    {
        cut.Find("[data-testid=destination-kind-smb]").Change(true);
        cut.Find("[data-testid=destination-server]").Input("192.0.2.200");
        cut.Find("[data-testid=destination-share]").Input("backups");
        cut.Find("[data-testid=destination-subfolder]").Input("mirbadllm");
        cut.Find("[data-testid=destination-username]").Input("dgx-backup");
        cut.Find("[data-testid=destination-password]").Input(password);
        Assert.False(cut.Find("[data-testid=destination-save]").HasAttribute("disabled"));
    }
}
