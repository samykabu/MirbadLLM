using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.UnitTests.HostServices;
using MirbadLLM.UnitTests.Router.Fakes;
using FakeHttpMessageHandler = MirbadLLM.UnitTests.Orchestration.Fakes.FakeHttpMessageHandler;
using FakeModelRegistry = MirbadLLM.UnitTests.Web.Fakes.FakeModelRegistry;

#pragma warning disable CA1707, xUnit1051 // underscore test names; TestContext.Current.CancellationToken forwarding not required for these hand-written fakes.

namespace MirbadLLM.UnitTests.Recovery;

/// <summary>
/// The controller's backup destination: portal-session authorization, the missing host agent, the effective
/// destination seen from inside the container, the backup guard, and the backup document's secret-free record.
/// </summary>
public sealed class BackupDestinationTests : IDisposable
{
    private const string SharePassword = "nas-pa$$,word";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mirbadllm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private const string ContainerShareMountInfo = """
        600 580 259:2 /srv/mirbadllm/backups /backups rw,relatime master:1 - ext4 /dev/nvme0n1p2 rw
        650 600 0:61 / /backups rw,relatime master:220 - cifs //192.0.2.200/backups/mirbadllm rw,vers=3.0,uid=1654
        """;

    private const string ContainerLocalMountInfo = """
        600 580 259:2 /srv/mirbadllm/backups /backups rw,relatime master:1 - ext4 /dev/nvme0n1p2 rw
        """;

    private static BackupDestinationRequest SmbRequest() => new()
    {
        Kind = BackupDestinationKind.Smb,
        Server = " 192.0.2.200 ",
        Share = "backups",
        Subfolder = "mirbadllm",
        Username = "dgx-backup",
        Password = SharePassword,
        SmbVersion = "3.0",
    };

    private sealed class Setup
    {
        public required FakeBackupMountClient Client { get; init; }

        public required FakeReauth Reauth { get; init; }

        public required BackupDestinationManager Manager { get; init; }

        public required BackupDirectory Directory { get; init; }

        public string MountInfo { get; set; } = ContainerLocalMountInfo;
    }

    private static Setup Create(FakeBackupMountClient? client = null, bool withClient = true)
    {
        client ??= new FakeBackupMountClient();
        var reauth = new FakeReauth();
        Setup? setup = null;
        var directory = new BackupDirectory(Options.Create(new BackupOptions { Directory = "/backups" }), () => setup!.MountInfo);
        var manager = new BackupDestinationManager(withClient ? client : null, directory, NullLogger<BackupDestinationManager>.Instance, reauth);
        setup = new Setup { Client = client, Reauth = reauth, Manager = manager, Directory = directory };
        return setup;
    }

    [Fact]
    public async Task Status_WithoutTheAgentSocket_SaysSo_AndStaysOnTheLocalDisk()
    {
        var setup = Create(new FakeBackupMountClient { Unavailable = new HostAgentUnavailableException("The host agent is not installed (no socket).") });

        var status = await setup.Manager.GetStatusAsync();

        Assert.False(status.AgentAvailable);
        Assert.Equal(BackupDestinationManager.AgentMissingMessage, status.AgentProblem);
        Assert.Contains("isn't installed", status.AgentProblem, StringComparison.Ordinal);
        Assert.Equal(BackupDestinationKind.Local, status.Kind);
        Assert.Equal("This DGX's disk (/srv/mirbadllm/backups)", status.Describe());
        Assert.Null(await setup.Manager.CheckBeforeBackupAsync());
    }

    [Fact]
    public async Task Status_WithNoClientRegistered_IsTheSameAsAMissingAgent()
    {
        var setup = Create(withClient: false);

        var status = await setup.Manager.GetStatusAsync();

        Assert.False(status.AgentAvailable);
        Assert.Equal(BackupDestinationManager.AgentMissingMessage, status.AgentProblem);
        await Assert.ThrowsAsync<BackupDestinationException>(() => setup.Manager.ApplyAsync(SmbRequest(), TestAdmin.User, "1.2.3.4"));
    }

    [Fact]
    public async Task Status_FromAnOlderAgent_AsksForAReinstall()
    {
        var setup = Create(new FakeBackupMountClient { Unavailable = new HostServiceNotFoundException("Not Found") });

        var status = await setup.Manager.GetStatusAsync();

        Assert.Equal(BackupDestinationManager.AgentOutdatedMessage, status.AgentProblem);
    }

    [Fact]
    public async Task Apply_RequiresASession_AndAnonymousChangesNothing()
    {
        var setup = Create();

        var wrong = await Assert.ThrowsAsync<AdminReauthenticationException>(() => setup.Manager.ApplyAsync(SmbRequest(), null, "1.2.3.4"));
        Assert.Equal(AdminReauthOutcome.LoginRequired, wrong.Outcome);
        await Assert.ThrowsAsync<AdminReauthenticationException>(() => setup.Manager.ApplyAsync(SmbRequest(), null, "1.2.3.4"));
        await Assert.ThrowsAsync<AdminReauthenticationException>(() => setup.Manager.DisconnectAsync(null, "1.2.3.4"));
        Assert.Empty(setup.Client.Sent);
        Assert.Equal(0, setup.Client.Removes);

        setup.Reauth.LoginEnabled = false;
        var disabled = await Assert.ThrowsAsync<AdminReauthenticationException>(() => setup.Manager.ApplyAsync(SmbRequest(), TestAdmin.User, "1.2.3.4"));
        Assert.Equal(AdminReauthOutcome.LoginDisabled, disabled.Outcome);
        Assert.Empty(setup.Client.Sent);
    }

    [Fact]
    public async Task Apply_WithTheAdminSession_SendsTheShareToTheAgentOnce()
    {
        var setup = Create();
        setup.MountInfo = ContainerShareMountInfo;

        var status = await setup.Manager.ApplyAsync(SmbRequest(), TestAdmin.User, "1.2.3.4");

        var sent = Assert.Single(setup.Client.Sent);
        Assert.Equal("smb", sent.Type);
        Assert.Equal("192.0.2.200", sent.Server);
        Assert.Equal(SharePassword, sent.Password);
        Assert.Null(sent.NfsVersion);
        Assert.Equal(BackupDestinationKind.Smb, status.Kind);
        Assert.True(status.ContainerSeesShare);
        Assert.Equal("//192.0.2.200/backups/mirbadllm (mounted, 1.2 TB free of 4 TB)", status.Describe());
        Assert.Null(await setup.Manager.CheckBeforeBackupAsync());
    }

    [Fact]
    public async Task Apply_WhenTheAgentRefuses_ThrowsItsMessage()
    {
        var setup = Create(new FakeBackupMountClient { ApplyFailure = new HostServiceException("Could not mount //192.0.2.200/backups: mount error(13): Permission denied — check the username and password.") });

        var ex = await Assert.ThrowsAsync<BackupDestinationException>(() => setup.Manager.ApplyAsync(SmbRequest(), TestAdmin.User, "1.2.3.4"));

        Assert.Contains("check the username and password", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_NeedsNoAdminPassword_AndReportsTheAgentsAnswer()
    {
        var setup = Create(new FakeBackupMountClient { TestAnswer = new HostAgentBackupMountTest(false, "mount error(13): Permission denied — check the username and password.") });

        var result = await setup.Manager.TestAsync(SmbRequest());

        Assert.False(result.Ok);
        Assert.Contains("check the username and password", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, setup.Reauth.Calls);
    }

    [Fact]
    public async Task Guard_RefusesABackupWhenTheShareIsNotWhatTheContainerSees()
    {
        var setup = Create();
        setup.Client.Mount = FakeBackupMountClient.Configured(mounted: true);

        string? problem = await setup.Manager.CheckBeforeBackupAsync();

        Assert.NotNull(problem);
        Assert.Contains("//192.0.2.200/backups/mirbadllm", problem, StringComparison.Ordinal);
        Assert.Contains("not visible inside mirbadllm", problem, StringComparison.Ordinal);
        var status = await setup.Manager.GetStatusAsync();
        Assert.True(status.ShareMissing);
        Assert.Equal("//192.0.2.200/backups/mirbadllm (mounted on the DGX, not yet visible to mirbadllm)", status.Describe());

        // Once the agent stops answering, the last known share still guards the local disk.
        setup.Client.Unavailable = new HostAgentUnavailableException("The host agent did not answer: connection refused");
        Assert.Contains("does not answer", await setup.Manager.CheckBeforeBackupAsync(), StringComparison.Ordinal);

        setup.MountInfo = ContainerShareMountInfo;
        Assert.Null(await setup.Manager.CheckBeforeBackupAsync());
    }

    [Fact]
    public async Task Scheduler_SkipsTheRun_WhenTheShareIsMissing()
    {
        string dir = Path.Combine(_root, "backups");
        Directory.CreateDirectory(dir);
        var options = Options.Create(new BackupOptions { Directory = dir, HostName = "dgx" });
        var client = new FakeBackupMountClient { Mount = FakeBackupMountClient.Configured(mounted: false) };
        var manager = new BackupDestinationManager(client, new BackupDirectory(options, () => ""), NullLogger<BackupDestinationManager>.Instance);
        var service = new BackupService(new FakeModelRegistry([]), new FakeApiKeyStore(), options, TimeProvider.System, destinations: manager);
        var scheduler = new BackupScheduler(service, new BackupDirectory(options, () => ""), options, TimeProvider.System, NullLogger<BackupScheduler>.Instance, manager);

        var result = await scheduler.RunNowAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("not mounted on the DGX", result.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task BackupDocument_RecordsTheDestination_WithoutAnySecret()
    {
        var options = Options.Create(new BackupOptions { HostName = "dgx" });
        var setup = Create();
        await setup.Manager.ApplyAsync(SmbRequest(), TestAdmin.User, "1.2.3.4");
        var service = new BackupService(new FakeModelRegistry([]), new FakeApiKeyStore(), options, TimeProvider.System, destinations: setup.Manager);

        var document = await service.CreateAsync(passphrase: null);
        string json = Encoding.UTF8.GetString(BackupService.Serialize(document));
        var parsed = BackupService.Parse(BackupService.Serialize(document));

        Assert.Equal("//192.0.2.200/backups/mirbadllm", parsed.BackupDestination!.Source);
        Assert.Equal("dgx-backup", parsed.BackupDestination.Username);
        Assert.Contains("\"backupDestination\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(SharePassword, json, StringComparison.Ordinal);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

        // Local disk: nothing recorded; and an older file without the field still parses.
        var local = new BackupService(new FakeModelRegistry([]), new FakeApiKeyStore(), options, TimeProvider.System,
            destinations: Create(new FakeBackupMountClient()).Manager);
        Assert.Null((await local.CreateAsync(null)).BackupDestination);
        Assert.Null(BackupService.Parse("""{"schemaVersion":1,"models":[]}"""u8).BackupDestination);
    }

    [Fact]
    public void BackupDirectory_SeesTheTopmostMountAtItsPath()
    {
        var mount = BackupDirectory.FindTopMount(ContainerShareMountInfo, "/backups");

        Assert.Equal(new BackupDirectoryMount("cifs", "//192.0.2.200/backups/mirbadllm"), mount);
        Assert.True(mount!.IsNetworkShare);
        Assert.False(BackupDirectory.FindTopMount(ContainerLocalMountInfo, "/backups")!.IsNetworkShare);
        Assert.Null(BackupDirectory.FindTopMount(ContainerLocalMountInfo, "/data"));
        Assert.Equal("nfs4", BackupDirectory.FindTopMount(@"1 2 0:1 / /mnt/a\040b rw - nfs4 nas:/x rw", "/mnt/a b")!.FileSystemType);
    }

    [Fact]
    public void FormatBytes_UsesDecimalUnits()
    {
        Assert.Equal("1.2 TB", BackupDestinationStatus.FormatBytes(1_200_000_000_000));
        Assert.Equal("980 GB", BackupDestinationStatus.FormatBytes(980_000_000_000));
        Assert.Equal("12 B", BackupDestinationStatus.FormatBytes(12));
    }

    [Fact]
    public async Task HostAgentClient_SpeaksTheBackupMountEndpoints()
    {
        var handler = new FakeHttpMessageHandler
        {
            Handler = request => request.RequestUri!.AbsolutePath switch
            {
                "/backup-mount/test" => Json("""{"ok":false,"message":"mount error(13)"}"""),
                "/backup-mount" when request.Method == HttpMethod.Put => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
                {
                    Content = new StringContent("""{"error":"Could not mount: mount error(13)","code":"failed"}""", Encoding.UTF8, "application/json"),
                },
                _ => Json("""{"target":"/srv/mirbadllm/backups","unit":"srv-mirbadllm-backups.mount","configured":true,"foreignUnit":false,"type":"nfs","server":"nas","share":"/volume1/b","version":"4.1","source":"nas:/volume1/b","mounted":true,"mountedType":"nfs4","totalBytes":10,"freeBytes":5}"""),
            },
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://hostd/") };
        var client = new HostAgentClient(http);
        var body = new HostAgentBackupMountRequest { Type = "smb", Server = "nas", Share = "b", Username = "u", Password = SharePassword };

        var status = await client.GetBackupMountAsync();
        var test = await client.TestBackupMountAsync(body);
        var ex = await Assert.ThrowsAsync<HostServiceException>(() => client.SetBackupMountAsync(body));

        Assert.True(status.Mounted);
        Assert.Equal("nfs4", status.MountedType);
        Assert.False(test.Ok);
        Assert.Equal("Could not mount: mount error(13)", ex.Message);
        Assert.Contains("\"password\":\"nas-pa$$,word\"", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.DoesNotContain(SharePassword, body.ToString()!, StringComparison.Ordinal);

        var missing = new HostAgentClient(http, () => false);
        await Assert.ThrowsAsync<HostAgentUnavailableException>(() => missing.GetBackupMountAsync());
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>A scripted host agent for the backup-mount endpoints; records what it was sent.</summary>
internal sealed class FakeBackupMountClient : IHostAgentBackupMountClient
{
    public static HostAgentBackupMount Local { get; } = new(
        "/srv/mirbadllm/backups", @"srv-mirbadllm-backups.mount", false, false, null, null, null, null, null, null, null, null, null,
        false, null, null, null, null, null, null, null);

    public static HostAgentBackupMount Configured(bool mounted) => new(
        "/srv/mirbadllm/backups", @"srv-mirbadllm-backups.mount", true, false, "smb", "192.0.2.200", "backups", "mirbadllm", "dgx-backup", null, "3.0",
        "//192.0.2.200/backups/mirbadllm", "SMB 3.0 as dgx-backup", mounted, mounted ? "//192.0.2.200/backups/mirbadllm" : null, mounted ? "cifs" : null,
        mounted ? 4_000_000_000_000 : null, mounted ? 1_200_000_000_000 : null, DateTimeOffset.UnixEpoch, null, null);

    public HostAgentBackupMount Mount { get; set; } = Local;

    public Exception? Unavailable { get; set; }

    public Exception? ApplyFailure { get; set; }

    public HostAgentBackupMountTest TestAnswer { get; set; } = new(true, "Connected.");

    public List<HostAgentBackupMountRequest> Sent { get; } = [];

    public List<HostAgentBackupMountRequest> Tested { get; } = [];

    public int Removes { get; private set; }

    public Task<HostAgentBackupMount> GetBackupMountAsync(CancellationToken ct = default) =>
        Unavailable is not null ? Task.FromException<HostAgentBackupMount>(Unavailable) : Task.FromResult(Mount);

    public Task<HostAgentBackupMountTest> TestBackupMountAsync(HostAgentBackupMountRequest request, CancellationToken ct = default)
    {
        if (Unavailable is not null)
        {
            return Task.FromException<HostAgentBackupMountTest>(Unavailable);
        }

        Tested.Add(request);
        return Task.FromResult(TestAnswer);
    }

    public Task<HostAgentBackupMount> SetBackupMountAsync(HostAgentBackupMountRequest request, CancellationToken ct = default)
    {
        var failure = Unavailable ?? ApplyFailure;
        if (failure is not null)
        {
            return Task.FromException<HostAgentBackupMount>(failure);
        }

        Sent.Add(request);
        Mount = Configured(mounted: true) with
        {
            Type = request.Type,
            Server = request.Server,
        };
        return Task.FromResult(Mount);
    }

    public Task<HostAgentBackupMount> RemoveBackupMountAsync(CancellationToken ct = default)
    {
        if (Unavailable is not null)
        {
            return Task.FromException<HostAgentBackupMount>(Unavailable);
        }

        Removes++;
        Mount = Local;
        return Task.FromResult(Mount);
    }
}
