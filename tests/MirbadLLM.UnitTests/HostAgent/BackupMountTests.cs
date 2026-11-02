using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MirbadLLM.HostAgent;

namespace MirbadLLM.UnitTests.HostAgent;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

/// <summary>
/// mirbadllm-hostd's backup mount: strict validation (no option or unit-file injection), the exact unit,
/// options and credentials it renders, mountinfo parsing, and the test-then-apply / restore-on-failure ordering.
/// </summary>
public sealed class BackupMountTests
{
    private const string Password = "p,a=s\"s'w0rd";

    private static BackupMountRequest Smb(Action<BackupMountRequest>? change = null)
    {
        var request = new BackupMountRequest
        {
            Type = "smb",
            Server = "192.0.2.200",
            Share = "backups",
            Subfolder = "mirbadllm",
            Username = "dgx-backup",
            Password = Password,
        };
        change?.Invoke(request);
        return request;
    }

    private static BackupMountRequest Nfs(Action<BackupMountRequest>? change = null)
    {
        var request = new BackupMountRequest { Type = "nfs", Server = "192.0.2.200", Share = "/volume1/backups" };
        change?.Invoke(request);
        return request;
    }

    private static BackupMountSpec Valid(BackupMountRequest request)
    {
        Assert.True(BackupMountValidator.TryValidate(request, out var spec, out _, out string? error), error);
        return spec!;
    }

    // ---- Validation ----

    [Fact]
    public void Validation_AcceptsATypicalSmbShare_WithDefaults()
    {
        Assert.True(BackupMountValidator.TryValidate(Smb(), out var spec, out string? password, out string? error), error);

        Assert.Equal(new BackupMountSpec(BackupShareType.Smb, "192.0.2.200", "backups", "mirbadllm", "dgx-backup", null, "3.0"), spec);
        Assert.Equal(Password, password);
    }

    [Fact]
    public void Validation_AcceptsNfs_TrimsTrailingSlash_AndDefaultsTo41()
    {
        var spec = Valid(Nfs(r => r.Share = "/volume1/backups/"));

        Assert.Equal("/volume1/backups", spec.Share);
        Assert.Equal("4.1", spec.Version);
        Assert.Null(spec.Username);
    }

    [Theory]
    [InlineData("server", "nas,uid=0")]
    [InlineData("server", "nas\nWhat=//evil/x")]
    [InlineData("server", "nas;rm -rf /")]
    [InlineData("server", "-oexec")]
    [InlineData("server", "na s")]
    [InlineData("server", "999.1.1.1")]
    [InlineData("server", "fe80::1")] // IPv6 is NFS-only
    [InlineData("server", "")]
    [InlineData("share", "backups,uid=0")]
    [InlineData("share", "back\"ups")]
    [InlineData("share", "back'ups")]
    [InlineData("share", "a/b")]
    [InlineData("share", "..")]
    [InlineData("share", "x=y")]
    [InlineData("share", "b\nups")]
    [InlineData("share", "50%")]
    [InlineData("subfolder", "../etc")]
    [InlineData("subfolder", "a/../b")]
    [InlineData("subfolder", "/abs")]
    [InlineData("subfolder", "a,b")]
    [InlineData("subfolder", "a=b")]
    [InlineData("subfolder", "a//b")]
    [InlineData("username", "u,ser")]
    [InlineData("username", "u=x")]
    [InlineData("username", "u\nx")]
    [InlineData("username", "u ser")]
    [InlineData("username", "")]
    [InlineData("domain", "d,x")]
    [InlineData("domain", "d=x")]
    [InlineData("password", "a\nb")]
    [InlineData("password", "tab\there")]
    [InlineData("password", "")]
    [InlineData("smbVersion", "1.0")]
    [InlineData("smbVersion", "3.0,sec=none")]
    [InlineData("type", "cifs")]
    public void Validation_RefusesInjectionAndBadSmbValues(string field, string value)
    {
        var request = Smb(r =>
        {
            switch (field)
            {
                case "server": r.Server = value; break;
                case "share": r.Share = value; break;
                case "subfolder": r.Subfolder = value; break;
                case "username": r.Username = value; break;
                case "domain": r.Domain = value; break;
                case "password": r.Password = value; break;
                case "smbVersion": r.SmbVersion = value; break;
                case "type": r.Type = value; break;
            }
        });

        Assert.False(BackupMountValidator.TryValidate(request, out var spec, out _, out string? error));
        Assert.Null(spec);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("share", "relative/path")]
    [InlineData("share", "/volume1/../etc")]
    [InlineData("share", "/vol,ume1")]
    [InlineData("share", "/volume1/back ups")]
    [InlineData("share", "/volume1/x=y")]
    [InlineData("subfolder", "a b")]
    [InlineData("nfsVersion", "4.1,nolock")]
    [InlineData("nfsVersion", "2")]
    [InlineData("password", "secret")] // NFS has no password, so one must not be silently accepted
    [InlineData("username", "someone")]
    public void Validation_RefusesBadNfsValues(string field, string value)
    {
        var request = Nfs(r =>
        {
            switch (field)
            {
                case "share": r.Share = value; break;
                case "subfolder": r.Subfolder = value; break;
                case "nfsVersion": r.NfsVersion = value; break;
                case "password": r.Password = value; break;
                case "username": r.Username = value; break;
            }
        });

        Assert.False(BackupMountValidator.TryValidate(request, out _, out _, out _));
    }

    [Fact]
    public void Validation_AcceptsHostNamesAndIPv6ForNfs()
    {
        Assert.Equal("nas.lan", Valid(Smb(r => r.Server = "nas.lan")).Server);
        Assert.Equal("fd00::200", Valid(Nfs(r => r.Server = "fd00::200")).Server);
        Assert.Equal("Backups $ 2026", Valid(Smb(r => r.Share = "Backups $ 2026")).Share);
    }

    // ---- Rendering ----

    [Fact]
    public void Rendering_SmbUnit_IsExact_AndCarriesNoPassword()
    {
        var spec = Valid(Smb());
        var paths = new BackupMountPaths("/var/lib/mirbadllm-hostd/backup-mount.json");

        string unit = BackupMountRendering.Unit(spec, paths);

        Assert.Equal(@"/etc/systemd/system/srv-mirbadllm-backups.mount", paths.UnitPath);
        Assert.StartsWith(BackupMountRendering.ManagedMarker + "\n", unit, StringComparison.Ordinal);
        string[] lines = unit.Split('\n');
        Assert.Contains("After=network-online.target", lines);
        Assert.Contains("Wants=network-online.target", lines);
        Assert.Contains("What=//192.0.2.200/backups/mirbadllm", lines);
        Assert.Contains("Where=/srv/mirbadllm/backups", lines);
        Assert.Contains("Type=cifs", lines);
        Assert.Contains("Options=credentials=/etc/mirbadllm-hostd/backup-share.cred,uid=1654,gid=1654,file_mode=0640,dir_mode=0750,vers=3.0,_netdev,nofail", lines);
        Assert.Contains("WantedBy=multi-user.target", lines);
        Assert.DoesNotContain(Password, unit, StringComparison.Ordinal);
        Assert.DoesNotContain("dgx-backup", unit, StringComparison.Ordinal); // the user name lives in the credentials file too
    }

    [Fact]
    public void Rendering_NfsUnitAndOptions()
    {
        var paths = new BackupMountPaths("/state.json");
        var v41 = Valid(Nfs(r => r.Subfolder = "dgx"));
        var v3 = Valid(Nfs(r => r.NfsVersion = "3"));
        var v6 = Valid(Nfs(r => r.Server = "fd00::200"));

        string unit = BackupMountRendering.Unit(v41, paths);
        Assert.Contains("What=192.0.2.200:/volume1/backups/dgx\n", unit, StringComparison.Ordinal);
        Assert.Contains("Type=nfs\n", unit, StringComparison.Ordinal);
        Assert.Contains("Options=vers=4.1,soft,timeo=100,retrans=3,_netdev,nofail\n", unit, StringComparison.Ordinal);
        Assert.Equal("vers=3,soft,timeo=100,retrans=3,nolock,retry=0", BackupMountRendering.Options(v3, paths.CredentialsPath, forUnit: false));
        Assert.Equal("[fd00::200]:/volume1/backups", BackupMountRendering.Source(v6));
        Assert.Equal("NFS 4.1", BackupMountRendering.Summary(v41));
    }

    [Fact]
    public void Rendering_CredentialsFile_HoldsThePasswordVerbatim()
    {
        Assert.Equal($"username=dgx-backup\npassword={Password}\n", BackupMountRendering.Credentials("dgx-backup", Password, null));
        Assert.Equal("username=u\npassword=p\ndomain=WORKGROUP\n", BackupMountRendering.Credentials("u", "p", "WORKGROUP"));
    }

    [Fact]
    public void ProbeMountArguments_AreAnExactVector_WithoutThePassword()
    {
        var spec = Valid(Smb());

        var args = BackupMountManager.MountArguments(spec, "/tmp/p.cred", "/tmp/p");

        Assert.Equal(
            ["-n", "-t", "cifs", "-o", "credentials=/tmp/p.cred,uid=1654,gid=1654,file_mode=0640,dir_mode=0750,vers=3.0", "--", "//192.0.2.200/backups/mirbadllm", "/tmp/p"],
            args);
        Assert.DoesNotContain(args, a => a.Contains(Password, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("mount error(13): Permission denied\nRefer to the mount.cifs(8) manual page", "check the username and password")]
    [InlineData("mount error(2): No such file or directory", "does not exist on the NAS")]
    [InlineData("mount error(95): Operation not supported", "try another protocol version")]
    [InlineData("mount: /tmp/x: bad option; for several filesystems (e.g. nfs, cifs) you might need a /sbin/mount.<type> helper program.", "cifs-utils")]
    public void MountFailures_AreExplained(string stderr, string hint)
    {
        string message = BackupMountRendering.ExplainMountFailure(Valid(Smb()), new ProcessResult(32, "", stderr));

        Assert.Contains("//192.0.2.200/backups/mirbadllm", message, StringComparison.Ordinal);
        Assert.Contains(hint, message, StringComparison.Ordinal);
    }

    // ---- mountinfo ----

    private const string MountInfo = """
        24 1 259:2 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw
        30 24 259:2 /srv/mirbadllm/backups /srv/mirbadllm/backups rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw
        412 30 0:61 / /srv/mirbadllm/backups rw,relatime shared:220 - cifs //192.0.2.200/backups/mirbadllm rw,vers=3.0,uid=1654
        413 24 0:62 / /mnt/with\040space rw shared:221 - nfs4 nas:/x rw
        """;

    [Fact]
    public void MountInfo_FindsTheTopmostMountAtTheTarget_AndUnescapes()
    {
        var top = MountInfoParser.FindTop(MountInfo, "/srv/mirbadllm/backups");

        Assert.Equal(new MountInfoEntry("/srv/mirbadllm/backups", "cifs", "//192.0.2.200/backups/mirbadllm"), top);
        Assert.Equal("nfs4", MountInfoParser.FindTop(MountInfo, "/mnt/with space")!.FileSystemType);
        Assert.Null(MountInfoParser.FindTop(MountInfo, "/srv/mirbadllm"));
        Assert.Null(MountInfoParser.FindTop("garbage line\n", "/"));
    }

    // ---- Manager: ordering, failure handling, delete, status ----

    private sealed class Agent
    {
        public List<string> Journal { get; } = [];

        public FakeBackupMountHost Host { get; }

        public FakeProcessRunner Runner { get; } = new();

        public CapturingLogger<BackupMountManager> Logger { get; } = new();

        public BackupMountManager Manager { get; }

        public BackupMountPaths Paths { get; } = new("/var/lib/mirbadllm-hostd/backup-mount.json");

        public Func<string, IReadOnlyList<string>, ProcessResult?> Override { get; set; } = (_, _) => null;

        public Agent()
        {
            Host = new FakeBackupMountHost(Journal);
            Runner.Respond = (file, args) =>
            {
                Journal.Add($"{Path.GetFileName(file)} {string.Join(' ', args)}");
                if (Override(file, args) is { } result)
                {
                    return result;
                }

                if (file == BackupMountManager.MountPath)
                {
                    // A successful mount shows up in mountinfo for the probe directory.
                    return new ProcessResult(0, "", "");
                }

                return file == BackupMountManager.StatPath ? new ProcessResult(0, "1654:1654:750\n", "") : new ProcessResult(0, "", "");
            };
            Manager = new BackupMountManager(Runner, Host, Paths, Logger);
        }
    }

    [Fact]
    public async Task Apply_TestsFirst_ThenWritesCredentialsAndUnit_ThenAsksSystemd()
    {
        var agent = new Agent();

        var result = await agent.Manager.ApplyAsync(Smb(), TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Ok, result.Outcome);
        string Find(string prefix) => agent.Journal.First(e => e.StartsWith(prefix, StringComparison.Ordinal));
        int Index(string prefix) => agent.Journal.FindIndex(e => e.StartsWith(prefix, StringComparison.Ordinal));

        Assert.StartsWith("mount -n -t cifs -o credentials=/tmp/mirbadllm-probe-", Find("mount "), StringComparison.Ordinal);
        Assert.True(Index("mount ") < Index("probe-write ")
            && Index("probe-write ") < Index("umount ")
            && Index("umount ") < Index("write /etc/mirbadllm-hostd/backup-share.cred")
            && Index("write /etc/mirbadllm-hostd/backup-share.cred") < Index(@"write /etc/systemd/system/srv-mirbadllm-backups.mount")
            && Index(@"write /etc/systemd/system/srv-mirbadllm-backups.mount") < Index("systemctl daemon-reload")
            && Index("systemctl daemon-reload") < Index("systemctl enable")
            && Index("systemctl enable") < Index("systemctl restart")
            && Index("systemctl restart") < Index("write /var/lib/mirbadllm-hostd/backup-mount.json"),
            string.Join("\n", agent.Journal));
        Assert.Equal(@"systemctl restart --no-ask-password -- srv-mirbadllm-backups.mount", Find("systemctl restart"));
        Assert.Equal(@"systemctl enable --no-ask-password -- srv-mirbadllm-backups.mount", Find("systemctl enable"));

        var cred = agent.Host.Files["/etc/mirbadllm-hostd/backup-share.cred"];
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, cred.Mode);
        Assert.Contains($"password={Password}\n", cred.Content, StringComparison.Ordinal);
        Assert.StartsWith(BackupMountRendering.ManagedMarker, agent.Host.Files[agent.Paths.UnitPath].Content, StringComparison.Ordinal);

        // The probe's own credentials file and directory are gone again.
        Assert.DoesNotContain(agent.Host.Files.Keys, k => k.StartsWith("/tmp/", StringComparison.Ordinal));
        Assert.Empty(agent.Host.ProbeDirectories);

        // Nothing but the credentials file holds the password: not the state, the status, a command line or a log line.
        string state = agent.Host.Files[agent.Paths.StateFile].Content;
        Assert.Contains("\"server\": \"192.0.2.200\"", state, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, state, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(result.Status), StringComparison.Ordinal);
        Assert.DoesNotContain(agent.Runner.Calls, c => c.Arguments.Any(a => a.Contains(Password, StringComparison.Ordinal)));
        Assert.DoesNotContain(agent.Logger.Entries, e => e.Message.Contains(Password, StringComparison.Ordinal));
        Assert.Contains(agent.Logger.Entries, e => e.Message.Contains("backup-mount applied type=smb source=//192.0.2.200/backups/mirbadllm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_FailedMountTest_LeavesThePreviousConfigurationUntouched()
    {
        var agent = new Agent();
        agent.Host.Seed(agent.Paths.UnitPath, BackupMountRendering.ManagedMarker + "\nold unit\n");
        agent.Host.Seed(agent.Paths.CredentialsPath, "username=old\npassword=old\n");
        agent.Host.Seed(agent.Paths.StateFile, "{\"type\":\"smb\"}");
        agent.Override = (file, _) => file == BackupMountManager.MountPath
            ? new ProcessResult(32, "", "mount error(13): Permission denied\nRefer to the mount.cifs(8) manual page (e.g. man mount.cifs)")
            : null;

        var result = await agent.Manager.ApplyAsync(Smb(r => r.Password = "wrong"), TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Failed, result.Outcome);
        Assert.Contains("mount error(13): Permission denied — check the username and password", result.Message, StringComparison.Ordinal);
        Assert.Equal(BackupMountRendering.ManagedMarker + "\nold unit\n", agent.Host.Files[agent.Paths.UnitPath].Content);
        Assert.Equal("username=old\npassword=old\n", agent.Host.Files[agent.Paths.CredentialsPath].Content);
        Assert.Equal("{\"type\":\"smb\"}", agent.Host.Files[agent.Paths.StateFile].Content);
        Assert.Empty(agent.Runner.SystemctlCalls);
        Assert.DoesNotContain(agent.Journal, e => e.StartsWith("umount", StringComparison.Ordinal)); // never mounted
        Assert.Empty(agent.Host.ProbeDirectories);
    }

    [Fact]
    public async Task Apply_WhenTheUserCannotWrite_UnmountsTheProbe_AndChangesNothing()
    {
        var agent = new Agent();
        agent.Host.ProbeWriteResult = "The share is mounted but the controller's user (uid 1654) cannot write to it: Permission denied";

        var result = await agent.Manager.ApplyAsync(Smb(), TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Failed, result.Outcome);
        Assert.Contains("uid 1654", result.Message, StringComparison.Ordinal);
        Assert.Contains(agent.Journal, e => e.StartsWith("umount -n -- /tmp/mirbadllm-probe-", StringComparison.Ordinal));
        Assert.False(agent.Host.Files.ContainsKey(agent.Paths.UnitPath));
        Assert.False(agent.Host.Files.ContainsKey(agent.Paths.CredentialsPath));
        Assert.Empty(agent.Runner.SystemctlCalls);
    }

    [Fact]
    public async Task Apply_WhenSystemdCannotMount_PutsThePreviousFilesBack()
    {
        var agent = new Agent();
        string oldUnit = BackupMountRendering.ManagedMarker + "\nold unit\n";
        agent.Host.Seed(agent.Paths.UnitPath, oldUnit);
        agent.Host.Seed(agent.Paths.CredentialsPath, "username=old\npassword=old\n");
        int restarts = 0;
        agent.Override = (file, args) => file == SystemctlCommands.SystemctlPath && args[0] == "restart" && restarts++ == 0
            ? new ProcessResult(1, "", "Job for srv-mirbadllm-backups.mount failed.")
            : null;

        var result = await agent.Manager.ApplyAsync(Smb(), TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Failed, result.Outcome);
        Assert.Contains("previous destination was put back", result.Message, StringComparison.Ordinal);
        Assert.Equal(oldUnit, agent.Host.Files[agent.Paths.UnitPath].Content);
        Assert.Equal("username=old\npassword=old\n", agent.Host.Files[agent.Paths.CredentialsPath].Content);
        Assert.Equal(2, restarts); // the failed switch, then the old unit again
    }

    [Fact]
    public async Task Apply_RefusesToOverwriteAHandMadeUnit()
    {
        var agent = new Agent();
        agent.Host.Seed(agent.Paths.UnitPath, "[Mount]\nWhat=//nas/x\n");

        var result = await agent.Manager.ApplyAsync(Smb(), TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Conflict, result.Outcome);
        Assert.Contains("not written by mirbadllm", result.Message, StringComparison.Ordinal);
        Assert.Empty(agent.Runner.Calls);
        Assert.Equal("[Mount]\nWhat=//nas/x\n", agent.Host.Files[agent.Paths.UnitPath].Content);
    }

    [Fact]
    public async Task Apply_RefusesWhenSomethingElseAlreadyMountsTheTarget()
    {
        var agent = new Agent();
        agent.Host.MountInfo = MountInfo;

        var result = await agent.Manager.ApplyAsync(Nfs(), TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Conflict, result.Outcome);
        Assert.Empty(agent.Runner.Calls);
    }

    [Fact]
    public async Task Apply_Invalid_RunsNothing()
    {
        var agent = new Agent();

        var result = await agent.Manager.ApplyAsync(Smb(r => r.Share = "x,uid=0"), TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Invalid, result.Outcome);
        Assert.Empty(agent.Runner.Calls);
        Assert.Empty(agent.Journal);
    }

    [Fact]
    public async Task Apply_Nfs_RemovesAnOldSmbCredentialsFile()
    {
        var agent = new Agent();
        agent.Host.Seed(agent.Paths.CredentialsPath, "username=old\npassword=old\n");

        var result = await agent.Manager.ApplyAsync(Nfs(), TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Ok, result.Outcome);
        Assert.False(agent.Host.Files.ContainsKey(agent.Paths.CredentialsPath));
        Assert.Contains("Type=nfs\n", agent.Host.Files[agent.Paths.UnitPath].Content, StringComparison.Ordinal);
        Assert.Contains(agent.Journal, e => e.StartsWith("mount -n -t nfs -o vers=4.1,soft,timeo=100,retrans=3,retry=0 -- 192.0.2.200:/volume1/backups /tmp/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Test_ProbesButChangesNothing()
    {
        var agent = new Agent();

        var result = await agent.Manager.TestAsync(Smb(), TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Ok, result.Outcome);
        Assert.Contains("Connected to //192.0.2.200/backups/mirbadllm", result.Message, StringComparison.Ordinal);
        Assert.Empty(agent.Runner.SystemctlCalls);
        Assert.Empty(agent.Host.Files);
        Assert.DoesNotContain(agent.Journal, e => e.StartsWith("write /etc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Remove_DisablesTheUnit_DeletesUnitCredentialsAndState()
    {
        var agent = new Agent();
        await agent.Manager.ApplyAsync(Smb(), TestContext.Current.CancellationToken);
        agent.Journal.Clear();

        var result = await agent.Manager.RemoveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Ok, result.Outcome);
        Assert.Equal(@"systemctl disable --now --no-ask-password -- srv-mirbadllm-backups.mount", agent.Journal[0]);
        Assert.Empty(agent.Host.Files);
        Assert.Contains("systemctl daemon-reload", agent.Journal);
        Assert.Contains("stat -c %u:%g:%a -- /srv/mirbadllm/backups", agent.Journal);
        Assert.False(result.Status!.Configured);
        Assert.Null(result.Status.Warning);
    }

    [Fact]
    public async Task Remove_WhenTheLocalDirectoryIsWrong_SaysHowToFixIt()
    {
        var agent = new Agent();
        agent.Override = (file, _) => file == BackupMountManager.StatPath ? new ProcessResult(0, "0:0:755\n", "") : null;

        var result = await agent.Manager.RemoveAsync(TestContext.Current.CancellationToken);

        Assert.Contains("sudo install -d -o 1654 -g 1654 -m 0750 /srv/mirbadllm/backups", result.Status!.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_WhenUnmountFails_KeepsEverything()
    {
        var agent = new Agent();
        await agent.Manager.ApplyAsync(Smb(), TestContext.Current.CancellationToken);
        agent.Override = (file, args) => file == SystemctlCommands.SystemctlPath && args[0] == "disable"
            ? new ProcessResult(1, "", "umount: /srv/mirbadllm/backups: target is busy.")
            : null;

        var result = await agent.Manager.RemoveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackupMountOutcome.Failed, result.Outcome);
        Assert.True(agent.Host.Files.ContainsKey(agent.Paths.UnitPath));
        Assert.True(agent.Host.Files.ContainsKey(agent.Paths.CredentialsPath));
    }

    [Fact]
    public async Task Status_ReportsTheMountAndSpace_WithoutAnySecret()
    {
        var agent = new Agent();
        await agent.Manager.ApplyAsync(Smb(), TestContext.Current.CancellationToken);
        agent.Host.MountInfo = MountInfo;
        agent.Host.Space = (4_000_000_000_000, 1_200_000_000_000);

        var status = await agent.Manager.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.Configured);
        Assert.True(status.Mounted);
        Assert.Equal("cifs", status.MountedType);
        Assert.Equal("//192.0.2.200/backups/mirbadllm", status.Source);
        Assert.Equal("SMB 3.0 as dgx-backup", status.Options);
        Assert.Equal(1_200_000_000_000, status.FreeBytes);
        Assert.Equal(@"srv-mirbadllm-backups.mount", status.Unit);
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(status), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_FlagsAHandMadeAutomount()
    {
        var agent = new Agent();
        agent.Host.Seed(agent.Paths.AutomountPath, "[Automount]\n");

        var status = await agent.Manager.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.True(status.ForeignUnit);
        Assert.False(status.Configured);
        Assert.Contains("hand-made", status.Warning, StringComparison.Ordinal);
    }

    // ---- HTTP surface ----

    [Fact]
    public async Task Api_ValidatesAndReportsWithoutThePassword()
    {
        var agent = new Agent();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(agent.Manager);
        await using var app = builder.Build();
        app.MapBackupMountApi();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var client = app.GetTestClient();
        var ct = TestContext.Current.CancellationToken;

        using var invalid = await client.PutAsJsonAsync("/backup-mount", new { type = "smb", server = "nas", share = "x,uid=0", username = "u", password = "p" }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("\"code\":\"invalid\"", await invalid.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        agent.Override = (file, _) => file == BackupMountManager.MountPath ? new ProcessResult(32, "", "mount error(13): Permission denied") : null;
        using var test = await client.PostAsJsonAsync("/backup-mount/test", new { type = "smb", server = "192.0.2.200", share = "backups", username = "u", password = Password }, ct);
        Assert.Equal(HttpStatusCode.OK, test.StatusCode);
        string testBody = await test.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"ok\":false", testBody, StringComparison.Ordinal);
        Assert.Contains("check the username and password", testBody, StringComparison.Ordinal);

        using var failed = await client.PutAsJsonAsync("/backup-mount", new { type = "smb", server = "192.0.2.200", share = "backups", username = "u", password = Password }, ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);

        agent.Override = (_, _) => null;
        using var applied = await client.PutAsJsonAsync("/backup-mount", new { type = "smb", server = "192.0.2.200", share = "backups", username = "u", password = Password }, ct);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        string status = await client.GetStringAsync("/backup-mount", ct);
        Assert.Contains("\"configured\":true", status, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, status, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, await applied.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        using var removed = await client.DeleteAsync("/backup-mount", ct);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Contains("\"configured\":false", await removed.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }
}

/// <summary>An in-memory host: files with modes, mountinfo text, free space, and a scripted write test. Logs every change.</summary>
internal sealed class FakeBackupMountHost(List<string> journal) : IBackupMountHost
{
    public Dictionary<string, (string Content, UnixFileMode Mode)> Files { get; } = new(StringComparer.Ordinal);

    public HashSet<string> ProbeDirectories { get; } = new(StringComparer.Ordinal);

    public string MountInfo { get; set; } = "";

    public (long Total, long Free)? Space { get; set; }

    public string? ProbeWriteResult { get; set; }

    public void Seed(string path, string content) =>
        Files[path] = (content, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    public string? ReadFile(string path) => Files.TryGetValue(path, out var file) ? file.Content : null;

    public void WriteFile(string path, string content, UnixFileMode mode)
    {
        journal.Add($"write {path}");
        Files[path] = (content, mode);
    }

    public void DeleteFile(string path)
    {
        if (Files.Remove(path))
        {
            journal.Add($"delete {path}");
        }
    }

    public string ReadMountInfo() => MountInfo;

    public (long Total, long Free)? GetSpace(string path) => Space;

    public string CreateProbeDirectory(string root)
    {
        string path = $"{root}/mirbadllm-probe-{ProbeDirectories.Count + 1}";
        ProbeDirectories.Add(path);
        journal.Add($"mkdir {path}");
        return path;
    }

    public void DeleteProbeDirectory(string path)
    {
        ProbeDirectories.Remove(path);
        journal.Add($"rmdir {path}");
    }

    public string? ProbeWrite(string directory, int uid, int gid)
    {
        journal.Add($"probe-write {directory} as {uid}:{gid}");
        return ProbeWriteResult;
    }
}
