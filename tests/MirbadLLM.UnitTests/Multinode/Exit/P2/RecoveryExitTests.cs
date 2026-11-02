using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, restore modes and recovery mode (design §9.4, §10.5; MN-113, MN-130, MN-132; D76, D87, D98, D101–D103), end to
/// end on real masters with real agents: a backup made by a real fleet, configuration import into the same master (nodes
/// matched by uid) and into another one (same slugs, other machines: the target picker), per-node Services, a restore while
/// a benchmark holds a model, fleet DR on an empty master in recovery mode with an admin password (login, the password
/// re-check, the install, the gate's asset rule, an old cookie without <c>sid</c> signed out) and its refusal on a non-empty
/// master. The restore steps call the services exactly as the Restore tab (<c>Backup.razor</c>) does: there is no HTTP
/// surface for restores (D103), the page runs <see cref="RestorePlanner.PreviewAsync"/>, <see cref="RestoreJobService.Start"/>,
/// <see cref="IAdminReauthenticator.Verify"/> and <see cref="FleetRecovery.InstallAsync"/> in the master's process.
/// </summary>
public sealed class RecoveryExitTests
{
    private const string Passphrase = "p2-exit-backup-passphrase";

    [Fact]
    public async Task ConfigurationImportMatchesNodesByUidAndAsksForATargetWhenOnlyTheSlugMatches()
    {
        await using var source = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("alpha", "dgx")] });
        await source.JoinAsync("pc", PcGpus);
        await source.Master.CreateModelAsync(ModelBody("beta", "pc", [PcGpus[1]]));
        // The PC's Services list, as the Services page keeps it ({DataPath}/host-services.pc.json, MN-120).
        await NodeHostServicesFiles.ImportAsync(source.DataDir, "pc",
            new HostServicesBackup(new Dictionary<string, string> { ["ollama"] = "always" }, [new ManagedContainerOptions { Name = "ollama" }]), Ct);
        var (backup, secrets) = await BackupAsync(source.Master);
        Assert.Equal(["dgx", "pc"], backup.Nodes!.Select(n => n.Id).Order(StringComparer.Ordinal));
        Assert.Equal(["ollama"], backup.HostServicesFor("pc")!.Containers!.Select(c => c.Name));

        // The same master after losing its models and the PC's list: both nodes match by uid, nothing to pick.
        await DeleteModelAsync(source.Master, "alpha");
        await DeleteModelAsync(source.Master, "beta");
        File.Delete(Path.Combine(source.DataDir, NodeHostServicesFiles.FileName("pc")));
        var planner = source.Master.Get<RestorePlanner>();
        RestorePreview same = await planner.PreviewAsync(backup, secrets, ct: Ct);
        Assert.True(same.AllPlacementsMatched);
        Assert.All(same.Models, m => Assert.Equal(RestorePlacementMatch.SameUid, m.Placement!.Match));
        Assert.Equal("pc", same.Models.Single(m => m.Model.Id == "beta").Placement!.DefaultTargetNodeId);

        RestoreJobSnapshot sameJob = await RunAsync(source.Master,
            new RestoreRequest(backup, secrets, ["alpha", "beta"], ReplaceExisting: true, ImportApiKeys: false, ImportHostServices: true));
        Assert.True(sameJob.State == RestoreJobState.Completed, $"{sameJob.State} {sameJob.Error} {string.Join(" | ", sameJob.Notes)}");
        Assert.Equal("dgx", (await source.Master.Models.GetAsync("alpha", Ct))!.Placement!.NodeId);
        var beta = (await source.Master.Models.GetAsync("beta", Ct))!;
        Assert.Equal("pc", beta.Placement!.NodeId);
        Assert.Equal([PcGpus[1]], beta.Placement.GpuUuids!);
        using (var store = new HostServicesStore(Path.Combine(source.DataDir, NodeHostServicesFiles.FileName("pc"))))
        {
            Assert.Equal(["ollama"], (await store.ExportAsync(Ct)).Containers!.Select(c => c.Name));
        }

        // Another master: its own DGX and PC are other machines under the same names (new uids at enrollment).
        await using var target = await P2Fleet.StartAsync();
        await target.JoinAsync("pc", Pc2Gpus);
        await DeleteAllModelsAsync(target.Master);
        RestorePreview other = await target.Master.Get<RestorePlanner>().PreviewAsync(backup, secrets, ct: Ct);
        Assert.False(other.AllPlacementsMatched);
        Assert.All(other.Models, m =>
        {
            Assert.Equal(RestorePlacementMatch.SlugOnly, m.Placement!.Match);
            Assert.True(m.Placement.NeedsTarget, $"{m.Model.Id} needs no target");
            Assert.Null(m.Model.Placement);
        });
        Assert.Equal(["dgx", "pc"], other.TargetNodes.Select(n => n.Id));

        // The operator picks the targets: alpha onto the DGX, beta onto the other PC with one of its own cards (the backup's
        // GPU UUIDs belong to the old machine and are not carried to another one).
        RestoreJobSnapshot otherJob = await RunAsync(target.Master,
            new RestoreRequest(backup, secrets, ["alpha", "beta"], ReplaceExisting: true, ImportApiKeys: false, ImportHostServices: true,
                Targets: [new RestoreTarget("alpha", "dgx"), new RestoreTarget("beta", "pc", [Pc2Gpus[0]])]));
        Assert.True(otherJob.State == RestoreJobState.Completed, $"{otherJob.State} {otherJob.Error} {string.Join(" | ", otherJob.Notes)}");
        Assert.All(otherJob.Models, m => Assert.NotEqual(RestoreModelState.Failed, m.State));
        Assert.Equal("dgx", (await target.Master.Models.GetAsync("alpha", Ct))!.Placement!.NodeId);
        var restoredBeta = (await target.Master.Models.GetAsync("beta", Ct))!;
        Assert.Equal("pc", restoredBeta.Placement!.NodeId);
        Assert.Equal([Pc2Gpus[0]], restoredBeta.Placement.GpuUuids!);

        // The PC's Services list is for the machine with its uid: none here.
        Assert.False(File.Exists(Path.Combine(target.DataDir, NodeHostServicesFiles.FileName("pc"))));
        Assert.Contains(otherJob.Notes, n => n.Contains("'pc' were not restored: no node here has the same uid", StringComparison.Ordinal));

        // Never node trust: the target's registry still holds its own machines.
        var local = await target.Master.Nodes.ListAsync(Ct);
        Assert.DoesNotContain(local, n => backup.Nodes!.Any(b => b.Uid == n.Uid));

        // One audit line per restore, no secret in it.
        Assert.Single(await target.AuditAsync(), l => l.Action == AuditActions.Restore);
        Assert.DoesNotContain(Passphrase, target.AuditText(), StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, source.AuditText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// MN-123 found, MN-124 fixed (D105): the Restore tab's target picker sends <c>RestoreTarget(model, node)</c> without
    /// GPUs; on a discrete node that is not the model's uid match the restore now picks the largest equal cards itself (the
    /// backup's GPU count) instead of writing a GPU-less placement the validator refuses.
    /// </summary>
    [Fact]
    public async Task AModelPickedOntoADiscretePcAsTheRestoreTabSendsItIsNotFailedForMissingGpus()
    {
        await using var source = await P2Fleet.StartAsync();
        await source.JoinAsync("pc", PcGpus);
        await source.Master.CreateModelAsync(ModelBody("beta", "pc", [PcGpus[1]]));
        var (backup, secrets) = await BackupAsync(source.Master);

        await using var target = await P2Fleet.StartAsync();
        await target.JoinAsync("pc", Pc2Gpus);
        await DeleteAllModelsAsync(target.Master);
        RestorePreview preview = await target.Master.Get<RestorePlanner>().PreviewAsync(backup, secrets, ct: Ct);
        Assert.True(preview.Models.Single(m => m.Model.Id == "beta").Placement!.NeedsTarget);

        // Exactly what the page sends after the operator picks "pc" in the target picker.
        RestoreJobSnapshot job = await RunAsync(target.Master,
            new RestoreRequest(backup, secrets, ["beta"], ReplaceExisting: true, ImportApiKeys: false, ImportHostServices: false,
                Targets: [new RestoreTarget("beta", "pc")]));
        var row = job.Models.Single(m => m.Id == "beta");
        Assert.True(row.State != RestoreModelState.Failed, $"{row.State}: {row.Detail}");
        Assert.Contains(job.Notes, n => n.StartsWith("'beta': Placed on 'pc' GPU 0 (chosen automatically", StringComparison.Ordinal));

        // One card, as in the backup: the target's GPU 0 (equal cards, lowest index), stored by its UUID.
        var restored = (await target.Master.Models.GetAsync("beta", Ct))!;
        Assert.Equal("pc", restored.Placement!.NodeId);
        Assert.Equal([Pc2Gpus[0]], restored.Placement.GpuUuids);
        Assert.True(preview.TargetNodes.Single(n => n.Id == "pc").Discrete);
    }

    [Fact]
    public async Task ARestoreWhileABenchmarkHoldsTheModelSkipsItNamingTheHolder()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx")] });
        await fleet.Master.StartModelAsync("qwen");
        var (backup, secrets) = await BackupAsync(fleet.Master);
        // Edited after the backup (the backup holds MaxNumSeqs 16): a restore that wrote qwen would bring 16 back.
        var edited = (await fleet.Master.Models.GetAsync("qwen", Ct))! with { MaxNumSeqs = 8 };
        await fleet.Master.Models.UpsertAsync(edited, Ct);
        Assert.Equal(16, backup.Models.Single(m => m.Id == "qwen").MaxNumSeqs);

        // A real benchmark through the API holds qwen (an OwnershipKind.Job record) while its slow first token keeps it running.
        var jobs = fleet.Master.Get<ModelJobService>();
        fleet.Dgx.Node.Model.SetTimeToFirstTokenMs(3_000);
        try
        {
            var (status, body) = await fleet.Master.PostAsync("/api/models/qwen/benchmark");
            Assert.True(status == HttpStatusCode.Accepted, $"{(int)status} {body}");
            var ownership = fleet.Master.Get<IOwnershipStore>();
            OwnershipRecord holder = await ExitEnv.UntilValueAsync(() => ownership.GetForModelAsync("qwen", Ct), "the benchmark's hold on qwen");
            Assert.Equal(OwnershipKind.Job, holder.Kind);

            // Import with "replace existing": qwen is not written while the job holds it (D87); the job keeps running.
            RestoreJobSnapshot job = await RunAsync(fleet.Master,
                new RestoreRequest(backup, secrets, ["qwen"], ReplaceExisting: true, ImportApiKeys: false, ImportHostServices: false));
            Assert.True(job.State == RestoreJobState.Completed, $"{job.State} {job.Error}");
            var row = job.Models.Single(m => m.Id == "qwen");
            Assert.Equal(RestoreModelState.Skipped, row.State);
            Assert.Contains($"Not restored: {ModelBusyException.DescribeHolder(holder)} holds the model", row.Detail, StringComparison.Ordinal);
            Assert.Equal(holder.Id, (await ownership.GetForModelAsync("qwen", Ct))?.Id);
            Assert.Equal(8, (await fleet.Master.Models.GetAsync("qwen", Ct))!.MaxNumSeqs);
            Assert.DoesNotContain(await ownership.ListAsync(Ct), r => r.Id.StartsWith(RestoreJobService.RecordIdPrefix, StringComparison.Ordinal));
        }
        finally
        {
            fleet.Dgx.Node.Model.SetTimeToFirstTokenMs(50);
            jobs.CancelOnNode("dgx", JobKind.Benchmark);
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }
    }

    [Fact]
    public async Task FleetRecoveryInRecoveryModeUsesTheAdminSessionAndInstallsBeforeNormalInit()
    {
        await using var source = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("alpha", "dgx")] });
        await source.JoinAsync("pc", PcGpus);
        var (backup, secrets) = await BackupAsync(source.Master);
        string sourceCa = await File.ReadAllTextAsync(Path.Combine(source.DataDir, MasterPkiOptions.DirectoryName, CaStore.CertificateFileName), Ct);
        var sourceNodes = await source.Master.Nodes.ListAsync(Ct);
        string root = ExitEnv.NewDirectory("p2-recovery");
        string target = Path.Combine(root, "data");
        try
        {
            await using (var recovery = new P2Master(target, RecoverySettings(root), services: null).Boot())
            {
            Assert.True(recovery.Get<MasterBoot>().IsRecovery);

            // Nothing provisional: no seed model, no CA, no bootstrap node.
            Assert.False(File.Exists(Path.Combine(target, "models.json")));
            Assert.False(File.Exists(Path.Combine(target, MasterPkiOptions.DirectoryName, CaStore.CertificateFileName)));
            Assert.False(File.Exists(Path.Combine(target, NodeRegistryDocument.FileName)));

            // The admin password guards the restricted host; the login works there.
            using (var anonymous = await recovery.Anonymous.GetAsync("/backup", Ct))
            {
                Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
                Assert.Equal("/login", PathOf(anonymous));
            }

            await recovery.LoginAsync();
            string page = await PageAsync(recovery.Admin, "/backup");
            Assert.Contains("recovery-mode-banner", page, StringComparison.Ordinal);
            Assert.Contains("restore-mode-fleet", page, StringComparison.Ordinal);

            // The gate's asset rule (W9 review): a page whose id has a dot redirects to /backup, a real asset is served.
            foreach (string path in new[] { "/models/qwen2.5-7b", "/nodes/pc.lan", "/" })
            {
                using var gated = await recovery.Admin.GetAsync(path, Ct);
                Assert.True(gated.StatusCode == HttpStatusCode.Redirect, $"{path}: {(int)gated.StatusCode}");
                Assert.Equal("/backup", gated.Headers.Location?.OriginalString);
            }

            using (var css = await recovery.Admin.GetAsync("/app.css", Ct))
            {
                Assert.True(css.StatusCode == HttpStatusCode.OK, $"/app.css: {(int)css.StatusCode}");
                Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
            }

            using (var api = await recovery.Admin.GetAsync("/api/models", Ct))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);
                Assert.Contains(ErrorCodes.RecoveryMode, await api.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
            }

            // A cookie issued before D81 (no sid claim) is signed out; the same ticket with a sid is accepted (control).
            await AssertOldCookieSignedOutAsync(recovery, "/backup");

            // The Restore tab's flow: preview (emptiness), the session check, then the install.
            var fleet = recovery.Get<FleetRecovery>();
            Assert.True(fleet.IsActive);
            FleetRecovery.ValidateBackup(backup, secrets);
            Assert.True((await fleet.CheckEmptinessAsync(Ct)).IsEmpty);
            var reauth = recovery.Get<IAdminReauthenticator>();
            Assert.True(reauth.LoginEnabled);
            Assert.Equal(AdminReauthOutcome.LoginRequired, reauth.Verify(null));
            Assert.Equal(AdminReauthOutcome.Verified, reauth.Verify(TestAdmin.User));
            var lifetime = recovery.Get<IHostApplicationLifetime>();
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
            FleetRecoveryResult result = await fleet.InstallAsync(backup, secrets, ct: Ct);
            Assert.Equal(backup.Master!.MasterInstanceId, result.MasterInstanceId);
            Assert.Equal(2, result.Nodes);
            Assert.Equal(1, result.Models);
            Assert.True(result.CaWrittenToDisk);
            Assert.True(lifetime.ApplicationStopping.IsCancellationRequested, "the install did not ask the host to restart");
            }

                // Installed before normal init: the backup's CA, the nodes with their uids and pins, the takeover requested.
                Assert.Equal(sourceCa, await File.ReadAllTextAsync(Path.Combine(target, MasterPkiOptions.DirectoryName, CaStore.CertificateFileName), Ct));
                using var restored = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = target }), TimeProvider.System);
                var nodes = await restored.ListAsync(Ct);
                Assert.Equal(sourceNodes.Select(n => (n.Id, n.Uid, n.CertSpkiSha256, n.Approved)).Order(),
                    nodes.Select(n => (n.Id, n.Uid, n.CertSpkiSha256, n.Approved)).Order());
                Assert.True(File.Exists(Path.Combine(target, MasterStateDocument.TakeoverFlagFileName)));
                Assert.False(Directory.Exists(Path.Combine(target, FleetRecovery.StagingDirectoryName)));
                Assert.DoesNotContain(Passphrase, P2Env.ReadAuditText(target), StringComparison.Ordinal);
        }
        finally
        {
            ExitEnv.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task FleetRecoveryIsRefusedOnANonEmptyMasterAndOutsideRecoveryMode()
    {
        await using var source = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("alpha", "dgx")] });
        var (backup, secrets) = await BackupAsync(source.Master);

        // A normal master: fleet recovery is not available (configuration import is).
        var normal = await Assert.ThrowsAsync<FleetRecoveryException>(() => source.Master.Get<FleetRecovery>().InstallAsync(backup, secrets, ct: Ct));
        Assert.Equal(ErrorCodes.InvalidState, normal.Code);

        // Recovery mode on a master that already has a fleet (an enrolled DGX, a used CA): master_not_empty, nothing changes.
        string root = ExitEnv.NewDirectory("p2-notempty");
        string copy = Path.Combine(root, "data");
        try
        {
            await source.RestartMasterAsync(whileStopped: () =>
            {
                ExitEnv.CopyDataDirectory(source.DataDir, copy);
                return Task.CompletedTask;
            });
            byte[] nodesBefore = await File.ReadAllBytesAsync(Path.Combine(copy, NodeRegistryDocument.FileName), Ct);
            byte[] modelsBefore = await File.ReadAllBytesAsync(Path.Combine(copy, "models.json"), Ct);
            await using (var recovery = new P2Master(copy, RecoverySettings(root), services: null).Boot())
            {
                var fleet = recovery.Get<FleetRecovery>();
                Assert.True(fleet.IsActive);
                MasterEmptiness emptiness = await fleet.CheckEmptinessAsync(Ct);
                Assert.False(emptiness.IsEmpty);
                Assert.NotEmpty(emptiness.Blockers);
                var refused = await Assert.ThrowsAsync<FleetRecoveryException>(() => fleet.InstallAsync(backup, secrets, ct: Ct));
                Assert.Equal(ErrorCodes.MasterNotEmpty, refused.Code);
                Assert.Equal(409, refused.Status);
                Assert.NotEmpty(refused.Blockers);
                Assert.False(recovery.Get<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
            }

            Assert.Equal(nodesBefore, await File.ReadAllBytesAsync(Path.Combine(copy, NodeRegistryDocument.FileName), Ct));
            Assert.Equal(modelsBefore, await File.ReadAllBytesAsync(Path.Combine(copy, "models.json"), Ct));
            Assert.False(File.Exists(Path.Combine(copy, MasterStateDocument.TakeoverFlagFileName)));
        }
        finally
        {
            ExitEnv.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task AnOldSessionCookieWithoutASidIsSignedOutOnTheNormalMasterToo()
    {
        await using var fleet = await P2Fleet.StartAsync();
        await AssertOldCookieSignedOutAsync(fleet.Master, "/");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>Recovery mode for a master over a fresh data directory (no enroll listener in recovery mode, D101).</summary>
    private static Dictionary<string, string?> RecoverySettings(string root) => new()
    {
        ["MirbadLLM:Recovery:Mode"] = "FleetRestore",
        ["MirbadLLM:Enrollment:Port"] = "0",
        ["MirbadLLM:Enrollment:BootstrapDirectory"] = Path.Combine(root, "bootstrap"),
    };

    /// <summary>A backup made by the master's own <see cref="BackupService"/>, through the file format.</summary>
    private static async Task<(BackupDocument Backup, BackupSecrets? Secrets)> BackupAsync(P2Master master)
    {
        var parsed = BackupService.Parse(BackupService.Serialize(await master.Get<BackupService>().CreateAsync(Passphrase, Ct)));
        var secrets = BackupService.DecryptSecrets(parsed, Passphrase);
        Assert.NotNull(secrets?.Pki);
        return (parsed, secrets);
    }

    private static async Task<RestoreJobSnapshot> RunAsync(P2Master master, RestoreRequest request)
    {
        var restores = master.Get<RestoreJobService>();
        restores.Start(request);
        await restores.Completion.WaitAsync(TimeSpan.FromSeconds(120), Ct);
        return restores.Current!;
    }

    private static async Task DeleteModelAsync(P2Master master, string id)
    {
        var (status, body) = await SendAsync(master.Admin.DeleteAsync($"/api/models/{id}", Ct));
        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.NoContent, $"DELETE {id}: {(int)status} {body}");
    }

    private static async Task DeleteAllModelsAsync(P2Master master)
    {
        foreach (var model in await master.Models.ListAsync(Ct))
        {
            await DeleteModelAsync(master, model.Id);
        }
    }

    private static async Task<string> PageAsync(HttpClient client, string path)
    {
        var (status, body) = await SendAsync(client.GetAsync(path, Ct));
        Assert.True(status == HttpStatusCode.OK, $"GET {path}: {(int)status}");
        return body;
    }

    /// <summary>
    /// An admin cookie as the pre-P2 login issued it (name only, no <see cref="ClaimTypes.Sid"/>), protected with the
    /// master's own data-protection keys and cookie format: the request is signed out (redirect to the login, the cookie
    /// cleared). The same ticket with a sid is accepted, so the cookie itself is valid.
    /// </summary>
    private static async Task AssertOldCookieSignedOutAsync(P2Master master, string path)
    {
        var options = master.Get<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        string name = options.Cookie.Name!;

        using (var withSid = await GetWithCookieAsync(master, path, name, Protect(options, withSid: true)))
        {
            Assert.True(withSid.StatusCode == HttpStatusCode.OK, $"a cookie with a sid: {(int)withSid.StatusCode} {withSid.Headers.Location}");
        }

        using var old = await GetWithCookieAsync(master, path, name, Protect(options, withSid: false));
        Assert.Equal(HttpStatusCode.Redirect, old.StatusCode);
        Assert.Equal("/login", PathOf(old));
        string cleared = string.Join("\n", old.Headers.TryGetValues("Set-Cookie", out var values) ? values : []);
        Assert.Contains(name + "=;", cleared, StringComparison.Ordinal);
        Assert.Contains("expires=Thu, 01 Jan 1970", cleared, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The path a redirect points to (the cookie handler answers with an absolute URL).</summary>
    private static string PathOf(HttpResponseMessage redirect)
    {
        Uri location = redirect.Headers.Location ?? throw new InvalidOperationException("no Location header");
        return location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
    }

    private static string Protect(CookieAuthenticationOptions options, bool withSid)
    {
        List<Claim> claims = [new(ClaimTypes.Name, "admin")];
        if (withSid)
        {
            claims.Add(new Claim(ClaimTypes.Sid, "0123456789abcdef0123456789abcdef"));
        }

        var now = DateTimeOffset.UtcNow;
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IssuedUtc = now.AddDays(-1), ExpiresUtc = now.AddDays(6) },
            CookieAuthenticationDefaults.AuthenticationScheme);
        return options.TicketDataFormat.Protect(ticket);
    }

    private static async Task<HttpResponseMessage> GetWithCookieAsync(P2Master master, string path, string name, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{name}={value}");
        return await master.Anonymous.SendAsync(request, Ct);
    }
}
