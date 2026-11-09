using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Multinode.P1Exit;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-109: moving and unplacing models (design §6.4, §6.7) through the real master composition (<c>Program</c>, every
/// background service on) and two FakeAgents sharing the master CA (<c>dgx</c> = source, <c>pc-office</c> = target):
/// the full move, refusals, the source-offline pause and force-commit with the stale-instance clean-up, cancel, a master
/// crash at every stage, and unplace.
/// </summary>
public sealed class MoveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ the move

    [Fact]
    public async Task Move_RunningModel_DownloadsOnTarget_StopsSource_CommitsOnce_StartsTarget_AndAudits()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        cluster.Dgx.AddFolder("m1-ckpt");
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");

        var (status, body) = await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });

        Assert.True(status == HttpStatusCode.Accepted, $"{(int)status} {body}");
        Assert.Equal(MoveStages.Downloading, body.GetProperty("stage").GetString());
        Assert.Equal("Move", body.GetProperty("kind").GetString());
        Assert.True(body.GetProperty("move").GetProperty("wasRunning").GetBoolean());
        await master.WaitForMoveEndAsync("m1");

        await master.AssertMovedAsync("m1", "m1-ckpt", expectRunning: true);
        Assert.Equal(MoveCluster.Target, master.Cache.ResolveServedName("m1")?.NodeId); // /v1 routes to the target
        var view = await master.GetJsonAsync("/api/models/m1/move");
        Assert.Equal(MoveStages.Done, view.GetProperty("record").GetProperty("stage").GetString());
        Assert.False(view.GetProperty("active").GetBoolean());

        var audit = await master.AuditAsync("m1");
        Assert.Contains(audit, l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Accepted && l.Actor.StartsWith("admin:", StringComparison.Ordinal));
        Assert.Contains(audit, l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Succeeded && l.Actor == AuditActors.SystemMove);
        // The agent operations of the move are attributed to system:move (owner move:{id}) with their operation ids.
        Assert.Contains(audit, l => l.Actor == AuditActors.SystemMove && l.OperationId is not null && l.NodeId == MoveCluster.Source);
    }

    [Fact]
    public async Task Move_StoppedModel_IsNotStartedOnTarget_AndNeedsNoPassword()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        var startTimes = master.Get<MirbadLLM.Orchestration.StartupTimeTracker>();
        startTimes.Record("m1", TimeSpan.FromSeconds(90)); // measured on the source

        var (status, body) = await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target });

        Assert.True(status == HttpStatusCode.Accepted, $"{(int)status} {body}");
        await master.WaitForMoveEndAsync("m1");
        await master.AssertMovedAsync("m1", "m1-ckpt", expectRunning: false);

        // The source's start time no longer applies on the target's hardware (design §6.5 item 8, MN-124).
        Assert.Null(startTimes.GetLast("m1"));
    }

    [Fact]
    public async Task Move_OfAnUnplacedModel_PlacesItFromTheRecordGeneration()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt") with { Placement = null });
        await using var master = await cluster.StartMasterAsync();

        var (status, body) = await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target });

        Assert.True(status == HttpStatusCode.Accepted, $"{(int)status} {body}");
        Assert.Equal("", body.GetProperty("move").GetProperty("sourceNodeId").GetString());
        await master.WaitForMoveEndAsync("m1");
        var placement = (await master.Models.GetAsync("m1", Ct))!.Placement!;
        Assert.Equal((MoveCluster.Target, 1L), (placement.NodeId, placement.Generation));
        Assert.Equal(1, await master.CountOperationsAsync(MoveCluster.Target, OperationKinds.Download, "m1-ckpt"));
    }

    [Fact]
    public async Task Move_MissingImageOnTarget_IsPulledThereBeforeTheSourceStops()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt") with { Image = AgentContractSeed.PullImage });
        await using var master = await cluster.StartMasterAsync();

        var (status, body) = await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target });

        Assert.True(status == HttpStatusCode.Accepted, $"{(int)status} {body}");
        await master.WaitForMoveEndAsync("m1");
        Assert.Equal(1, await master.CountOperationsAsync(MoveCluster.Target, OperationKinds.ImagePull, AgentContractSeed.PullImage));
        Assert.Contains(await master.Client(MoveCluster.Target).ListImagesAsync(Ct), i => i.Ref == AgentContractSeed.PullImage);
        Assert.Equal(MoveCluster.Target, (await master.Models.GetAsync("m1", Ct))!.Placement!.NodeId);
    }

    [Fact]
    public async Task Move_Refusals_HaveTheirCodes_AndAreAudited()
    {
        await using var cluster = await MoveCluster.StartAsync(
            extraNodes: [MoveCluster.ExtraNode("pc-drained", n => n with { Draining = true }), MoveCluster.ExtraNode("pc-old", n => n with { Revoked = true })],
            models:
            [
                MoveCluster.Model("m1", "m1-ckpt"),
                MoveCluster.Model("manual", "manual-ckpt") with { Sources = [] },
                MoveCluster.Model("drafted", "drafted-ckpt") with { Speculative = new SpeculativeDecoding("dflash", 8) { DraftModel = "/models/drafted-draft" } },
                MoveCluster.Model("held", "held-ckpt"),
            ]);
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");
        await master.Ownership.TryAcquireAsync(new OwnershipRecord
        {
            // An edit record: job and restore records without a running job are released by their own recovery at boot.
            Id = "edit-1", Kind = OwnershipKind.Edit, ModelId = "held", Stage = "editing", StartedAt = DateTimeOffset.UtcNow,
            Description = "a placement change",
        }, Ct);

        await master.AssertProblemAsync("/api/models/manual/move", new { targetNodeId = MoveCluster.Target }, HttpStatusCode.Conflict, ErrorCodes.ModelNotMovable);
        await master.AssertProblemAsync("/api/models/drafted/move", new { targetNodeId = MoveCluster.Target }, HttpStatusCode.Conflict, ErrorCodes.ModelNotMovable);
        await master.AssertProblemAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Source }, HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
        await master.AssertProblemAsync("/api/models/m1/move", new { targetNodeId = "pc-drained", password = MoveCluster.Password }, HttpStatusCode.Conflict, ErrorCodes.NodeDraining);
        await master.AssertProblemAsync("/api/models/m1/move", new { targetNodeId = "pc-old", password = MoveCluster.Password }, HttpStatusCode.Conflict, ErrorCodes.NodeRevoked);
        await master.AssertProblemAsync("/api/models/m1/move", new { targetNodeId = "pc-nowhere", password = MoveCluster.Password }, HttpStatusCode.BadRequest, ErrorCodes.PlacementInvalid);
        var anonymous = await Assert.ThrowsAsync<MoveRefusedException>(() => master.Moves.StartMoveAsync("m1",
            new ModelMoveRequest { TargetNodeId = MoveCluster.Target, Password = MoveCluster.Password },
            new MoveCaller("api:test", "sid:forged"), Ct));
        Assert.Equal(ErrorCodes.ReauthenticationFailed, anonymous.Code);
        var busy = await master.AssertProblemAsync("/api/models/held/move", new { targetNodeId = MoveCluster.Target }, HttpStatusCode.Conflict, ErrorCodes.ModelBusy);
        Assert.Equal("edit-1", busy.GetProperty("holder").GetProperty("id").GetString());
        await master.AssertProblemAsync("/api/models/m1/move", new { password = "x" }, HttpStatusCode.BadRequest, ErrorCodes.InvalidRequest);
        var (missing, _) = await master.PostAsync("/api/models/nope/move", new { targetNodeId = MoveCluster.Target });
        Assert.Equal(HttpStatusCode.NotFound, missing);

        // Nothing changed: m1 still runs on dgx at generation 0, nothing holds it, and every refusal has its audit line.
        Assert.Equal((MoveCluster.Source, 0L), ((await master.Models.GetAsync("m1", Ct))!.Placement!.NodeId, (await master.Models.GetAsync("m1", Ct))!.Placement!.Generation));
        Assert.Null(await master.Ownership.GetForModelAsync("m1", Ct));
        var refused = (await master.AuditAsync(null)).Where(l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Refused).ToList();
        Assert.Contains(refused, l => l.ErrorCode == ErrorCodes.ModelNotMovable);
        Assert.Contains(refused, l => l.ErrorCode == ErrorCodes.NodeDraining);
        Assert.Contains(refused, l => l.ErrorCode == ErrorCodes.ReauthenticationFailed);
        Assert.Contains(refused, l => l.ErrorCode == ErrorCodes.ModelBusy && l.ModelId == "held");
        Assert.Contains(refused, l => l.ErrorCode == ErrorCodes.PlacementInvalid);
    }

    [Fact]
    public async Task Move_WhileAnotherMoveHoldsTheModel_IsBusy_AndTheRoutesNeedALogin()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await cluster.Pc.SetDownloadsHeldAsync(true);

        var (first, _) = await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target });
        Assert.Equal(HttpStatusCode.Accepted, first);
        var busy = await master.AssertProblemAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target }, HttpStatusCode.Conflict, ErrorCodes.ModelBusy);
        Assert.Equal("Move", busy.GetProperty("holder").GetProperty("kind").GetString());
        // A placement edit, a delete and a revoke-unplace are refused by the ownership validator while the move runs.
        using (var delete = await master.Http.DeleteAsync("/api/models/m1", Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        }

        using var anonymous = master.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (string path in new[] { "/api/models/m1/move", "/api/models/m1/move/force-commit", "/api/models/m1/move/cancel", "/api/models/m1/unplace" })
        {
            using var response = await anonymous.PostAsJsonAsync(path, new { }, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await cluster.Pc.SetDownloadsHeldAsync(false);
        await master.WaitForMoveEndAsync("m1");
    }

    [Fact]
    public async Task Move_ProgressView_ShowsTheTargetDownloads()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await cluster.Pc.SetDownloadsHeldAsync(true);

        await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target });
        JsonElement view = default;
        await ExitEnv.UntilAsync(async () =>
        {
            view = await master.GetJsonAsync("/api/models/m1/move");
            return view.GetProperty("downloads").EnumerateArray().Any(d => d.GetProperty("state").GetString() == nameof(DownloadState.Downloading));
        }, "the target download in the move view");

        Assert.True(view.GetProperty("active").GetBoolean());
        Assert.True(view.GetProperty("targetServing").GetBoolean());
        Assert.False(view.GetProperty("canForceCommit").GetBoolean());
        var download = view.GetProperty("downloads")[0];
        Assert.Equal("org/m1-ckpt", download.GetProperty("repoId").GetString());
        Assert.True(download.GetProperty("totalBytes").GetInt64() > 0);
        await cluster.Pc.SetDownloadsHeldAsync(false);
        await master.WaitForMoveEndAsync("m1");
    }

    [Fact]
    public async Task Move_ADownloadThatEndsCancelled_FailsTheMove_BeforeTheSourceIsTouched()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");
        await cluster.Pc.SetDownloadsHeldAsync(true);

        await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });
        await ExitEnv.UntilAsync(async () => (await master.Client(MoveCluster.Target).GetDownloadAsync("m1-ckpt", Ct))?.State == DownloadState.Downloading, "the download");
        // Someone cancels the download on the node (another master action, an operator on the Nodes page).
        var envelope = master.Get<AgentOperationRunnerAccessor>().Runner.CreateEnvelope();
        await master.Client(MoveCluster.Target).CancelDownloadAsync("m1-ckpt",
            new DownloadCancelRequest { OperationId = envelope.OperationId, IssuedAt = envelope.IssuedAt, ExpiresAt = envelope.ExpiresAt }, Ct);
        await cluster.Pc.SetDownloadsHeldAsync(false);
        await master.WaitForMoveEndAsync("m1");

        var view = await master.GetJsonAsync("/api/models/m1/move");
        Assert.Equal(MoveStages.Failed, view.GetProperty("record").GetProperty("stage").GetString());
        Assert.Contains("cancelled", view.GetProperty("record").GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal((MoveCluster.Source, 0L), PlacementOf(await master.Models.GetAsync("m1", Ct)));
        Assert.Equal(ModelState.Ready, (await master.Client(MoveCluster.Source).GetRuntimeAsync("m1", Ct))?.State);
        Assert.Equal(0, await master.CountOperationsAsync(MoveCluster.Source, OperationKinds.Stop, "m1"));
        Assert.Contains(await master.AuditAsync("m1"), l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Failed);
    }

    [Fact]
    public async Task Cancel_DuringTheDownload_CancelsIt_LeavesTheRegistry_AndKeepsTheSourceServing()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");
        await master.AssertProblemAsync("/api/models/m1/move/cancel", new { }, HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        await cluster.Pc.SetDownloadsHeldAsync(true);
        await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });
        await ExitEnv.UntilAsync(async () => (await master.Client(MoveCluster.Target).GetDownloadAsync("m1-ckpt", Ct))?.State == DownloadState.Downloading, "the download");

        var (status, body) = await master.PostAsync("/api/models/m1/move/cancel", new { });

        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        Assert.Equal(MoveStages.Failed, body.GetProperty("stage").GetString());
        await master.WaitForMoveEndAsync("m1");
        await ExitEnv.UntilAsync(async () => (await master.Client(MoveCluster.Target).GetDownloadAsync("m1-ckpt", Ct))?.State == DownloadState.Cancelled, "the download cancelled");
        Assert.Equal((MoveCluster.Source, 0L), PlacementOf(await master.Models.GetAsync("m1", Ct)));
        Assert.Equal(ModelState.Ready, (await master.Client(MoveCluster.Source).GetRuntimeAsync("m1", Ct))?.State);
        await cluster.Pc.SetDownloadsHeldAsync(false);
    }

    // ================================================================ source offline: pause, return, force-commit

    [Fact]
    public async Task Move_SourceOffline_PausesAtStoppingSource_NeverCommits_AndContinuesWhenTheSourceReturns()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");
        cluster.Dgx.SetPartitioned(true);
        await master.WaitForNodeAsync(MoveCluster.Source, n => !n.IsServing, "dgx offline");

        var (status, body) = await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });
        Assert.True(status == HttpStatusCode.Accepted, $"{(int)status} {body}");
        var paused = await master.WaitForStageAsync("m1", MoveStages.StoppingSource);
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct); // several retry passes while paused

        // Paused: not committed, the target stays stopped, the record says why and offers the force-commit.
        Assert.Equal((MoveCluster.Source, 0L), PlacementOf(await master.Models.GetAsync("m1", Ct)));
        Assert.Null(await master.Client(MoveCluster.Target).GetRuntimeAsync("m1", Ct));
        Assert.Equal(0, await master.CountOperationsAsync(MoveCluster.Target, OperationKinds.Launch, "m1"));
        var record = (await master.Ownership.GetForModelAsync("m1", Ct))!;
        Assert.Equal(MoveStages.StoppingSource, record.Stage);
        Assert.Contains("force-commit", record.Error, StringComparison.Ordinal);
        var view = await master.GetJsonAsync("/api/models/m1/move");
        Assert.True(view.GetProperty("canForceCommit").GetBoolean());
        Assert.Equal(MoveService.ForceCommitConsequenceFor("dgx"), view.GetProperty("forceCommitConsequence").GetString());
        Assert.Equal("If dgx comes back, the old instance may still be running there until it is stopped; no traffic is sent to it.",
            view.GetProperty("forceCommitConsequence").GetString());
        _ = paused;

        // The source returns: the move stops it there (confirmed), commits and starts the target, by itself.
        cluster.Dgx.SetPartitioned(false);
        await master.WaitForMoveEndAsync("m1", TimeSpan.FromSeconds(90));
        await master.AssertMovedAsync("m1", "m1-ckpt", expectRunning: true);
    }

    [Fact]
    public async Task ForceCommit_SourceOffline_StartsTheTarget_AndTheStaleSourceInstanceIsStoppedWhenItReturns()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");
        await master.AssertProblemAsync("/api/models/m1/move/force-commit", new { confirm = "dgx" }, HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        cluster.Dgx.SetPartitioned(true);
        await master.WaitForNodeAsync(MoveCluster.Source, n => !n.IsServing, "dgx offline");
        await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });
        await master.WaitForStageAsync("m1", MoveStages.StoppingSource);

        await master.AssertProblemAsync("/api/models/m1/move/force-commit", new { password = MoveCluster.Password, confirm = "pc-office" }, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired);
        await Assert.ThrowsAsync<MoveRefusedException>(() => master.Moves.ForceCommitAsync("m1",
            new ModelMoveForceCommitRequest { Confirm = "dgx", Password = MoveCluster.Password }, new MoveCaller("api:test", "sid:forged"), Ct));
        var (status, body) = await master.PostAsync("/api/models/m1/move/force-commit", new { confirm = "dgx" });

        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        Assert.Equal(MoveStages.SourceStopped, body.GetProperty("stage").GetString());
        Assert.True(body.GetProperty("move").GetProperty("sourceCleanupPending").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("move").GetProperty("forceCommittedAt").ValueKind);
        await master.WaitForMoveEndAsync("m1");
        Assert.Equal((MoveCluster.Target, 1L), PlacementOf(await master.Models.GetAsync("m1", Ct)));
        await ExitEnv.UntilAsync(async () => (await master.Client(MoveCluster.Target).GetRuntimeAsync("m1", Ct))?.State == ModelState.Ready, "m1 Ready on pc-office");
        Assert.Equal(MoveCluster.Target, master.Cache.ResolveServedName("m1")?.NodeId); // no traffic to the old instance

        var audit = await master.AuditAsync("m1");
        var force = Assert.Single(audit, l => l.Action == AuditActions.ModelMoveForceCommit && l.Result == AuditResults.Succeeded);
        Assert.Equal(3, audit.Count(l => l.Action == AuditActions.ModelMoveForceCommit && l.Result == AuditResults.Refused)); // no move yet, confirm typo, anonymous session
        Assert.Contains("no traffic is sent to it", force.Detail, StringComparison.Ordinal);

        // The old instance still runs on the partitioned dgx (it never got a stop) until dgx returns: the reconciliation
        // stops it there, once.
        cluster.Dgx.SetPartitioned(false);
        await master.WaitForNodeAsync(MoveCluster.Source, n => n.IsServing, "dgx back");
        await ExitEnv.UntilAsync(async () => await master.Client(MoveCluster.Source).GetRuntimeAsync("m1", Ct) is null, "the stale instance on dgx stopped");
        Assert.Equal(ModelState.Ready, (await master.Client(MoveCluster.Target).GetRuntimeAsync("m1", Ct))?.State);
        Assert.Equal(1, await master.CountOperationsAsync(MoveCluster.Source, OperationKinds.Stop, "m1"));
        await ExitEnv.UntilAsync(async () => (await master.AuditAsync("m1")).Any(l =>
            l.Action == AuditActions.ModelStop && l.NodeId == MoveCluster.Source && l.Actor == AuditActors.SystemMove && l.Detail?.Contains("stale", StringComparison.Ordinal) == true),
            "the stale-instance audit line");
    }

    [Fact]
    public async Task ForceCommit_IsRefused_BeforeTheStopStage_AndWhileTheSourceServes()
    {
        await using var cluster = await MoveCluster.StartAsync(
            dgxOptions: o => o with { StopDuration = TimeSpan.FromSeconds(3) }, models: [MoveCluster.Model("m1", "m1-ckpt")]);
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");
        await cluster.Pc.SetDownloadsHeldAsync(true);
        await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });

        var downloading = await master.AssertProblemAsync("/api/models/m1/move/force-commit", new { confirm = "dgx" },
            HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        Assert.Contains(MoveStages.Downloading, downloading.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // At stopping-source while dgx serves (its stop takes 3 s): the move stops it there by itself, no force needed.
        await cluster.Pc.SetDownloadsHeldAsync(false);
        await master.WaitForStageAsync("m1", MoveStages.StoppingSource);
        var serving = await master.AssertProblemAsync("/api/models/m1/move/force-commit", new { confirm = "dgx" },
            HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        Assert.Contains("is serving", serving.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains(await master.AuditAsync("m1"), l => l.Action == AuditActions.ModelMoveForceCommit && l.Result == AuditResults.Refused);

        await master.WaitForMoveEndAsync("m1");
        await master.AssertMovedAsync("m1", "m1-ckpt", expectRunning: true);
        var finished = (await master.GetJsonAsync("/api/models/m1/move")).GetProperty("record").GetProperty("move");
        Assert.Equal(JsonValueKind.Null, finished.GetProperty("forceCommittedAt").ValueKind); // committed after a confirmed stop
    }

    // ================================================================ master crash at every stage

    [Theory]
    [InlineData(MoveStages.Downloading, MoveStages.Downloading)]
    [InlineData(MoveStages.StoppingSource, MoveStages.StoppingSource)]
    [InlineData(MoveStages.SourceStopped, MoveStages.SourceStopped)]
    [InlineData(MoveService.RegistryCommittedCheckpoint, MoveStages.SourceStopped)]
    [InlineData(MoveStages.PlacementCommitted, MoveStages.PlacementCommitted)]
    [InlineData(MoveStages.StartingTarget, MoveStages.StartingTarget)]
    public async Task MasterCrash_AtEachStage_TheNextMasterResumesFromIt_AndFinishesOnce(string checkpoint, string persistedStage)
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        var first = await cluster.StartMasterAsync();
        await first.StartModelAsync("m1");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Moves.CheckpointForTests = async (point, _, ct) =>
        {
            if (point == checkpoint)
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // the master stops right here
            }
        };

        await first.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });
        await reached.Task.WaitAsync(ExitEnv.Patience, Ct);
        await first.DisposeAsync();

        var onDisk = await cluster.ReadOwnershipAsync();
        var record = Assert.Single(onDisk);
        Assert.Equal(persistedStage, record.Stage);
        var stored = await cluster.ReadModelAsync("m1");
        Assert.Equal(checkpoint is MoveService.RegistryCommittedCheckpoint or MoveStages.PlacementCommitted or MoveStages.StartingTarget
            ? (MoveCluster.Target, 1L)
            : (MoveCluster.Source, 0L), PlacementOf(stored));

        await using var second = await cluster.StartMasterAsync();
        await second.WaitForMoveEndAsync("m1");

        await second.AssertMovedAsync("m1", "m1-ckpt", expectRunning: true);
    }

    [Fact]
    public async Task MasterCrash_MidDownload_TheNextMasterAdoptsTheRunningDownload_InsteadOfStartingAnother()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        var first = await cluster.StartMasterAsync();
        await first.StartModelAsync("m1");
        await cluster.Pc.SetDownloadsHeldAsync(true);
        await first.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });
        await ExitEnv.UntilAsync(async () => (await first.Client(MoveCluster.Target).GetDownloadAsync("m1-ckpt", Ct))?.State == DownloadState.Downloading, "the download");
        await first.DisposeAsync();
        Assert.Single((await cluster.ReadOwnershipAsync())[0].OutstandingOperationIds);

        await using var second = await cluster.StartMasterAsync();
        await Task.Delay(500, Ct);
        await cluster.Pc.SetDownloadsHeldAsync(false);
        await second.WaitForMoveEndAsync("m1");

        await second.AssertMovedAsync("m1", "m1-ckpt", expectRunning: true);
    }

    [Fact]
    public async Task MasterCrash_WithTheSourceStopInFlight_TheNextMasterWaitsForIt_InsteadOfStoppingAgain()
    {
        await using var cluster = await MoveCluster.StartAsync(
            dgxOptions: o => o with { StopDuration = TimeSpan.FromSeconds(2) }, models: [MoveCluster.Model("m1", "m1-ckpt")]);
        var first = await cluster.StartMasterAsync();
        await first.StartModelAsync("m1");
        await first.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });
        await ExitEnv.UntilAsync(async () => await first.CountOperationsAsync(MoveCluster.Source, OperationKinds.Stop, "m1") == 1, "the stop sent to dgx");
        await first.DisposeAsync();
        Assert.Equal(MoveStages.StoppingSource, (await cluster.ReadOwnershipAsync())[0].Stage);

        await using var second = await cluster.StartMasterAsync();
        await second.WaitForMoveEndAsync("m1");

        await second.AssertMovedAsync("m1", "m1-ckpt", expectRunning: true); // exactly one stop on dgx
    }

    [Fact]
    public async Task MasterCrash_WithTheTargetLaunchInFlight_TheNextMasterContinuesThatLaunch()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        var first = await cluster.StartMasterAsync();
        await first.StartModelAsync("m1");
        await cluster.Pc.SetStartsHeldAsync(true);
        await first.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });
        await ExitEnv.UntilAsync(async () => await first.CountOperationsAsync(MoveCluster.Target, OperationKinds.Launch, "m1") == 1, "the launch on pc-office");
        await first.DisposeAsync();
        Assert.Equal(MoveStages.StartingTarget, (await cluster.ReadOwnershipAsync())[0].Stage);

        await using var second = await cluster.StartMasterAsync();
        await Task.Delay(500, Ct);
        await cluster.Pc.SetStartsHeldAsync(false);
        await second.WaitForMoveEndAsync("m1");

        await second.AssertMovedAsync("m1", "m1-ckpt", expectRunning: true); // exactly one launch on pc-office
    }

    [Fact]
    public async Task MasterCrash_WhilePausedForAnOfflineSource_TheNextMasterKeepsItPaused_ThenForceCommits()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        var first = await cluster.StartMasterAsync();
        await first.StartModelAsync("m1");
        cluster.Dgx.SetPartitioned(true);
        await first.WaitForNodeAsync(MoveCluster.Source, n => !n.IsServing, "dgx offline");
        await first.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });
        await first.WaitForStageAsync("m1", MoveStages.StoppingSource);
        await first.DisposeAsync();

        await using var second = await cluster.StartMasterAsync(waitForSource: false);
        await second.WaitForNodeAsync(MoveCluster.Source, n => !n.IsServing, "dgx offline for the new master");
        await Task.Delay(1000, Ct);
        Assert.Equal(MoveStages.StoppingSource, (await second.Ownership.GetForModelAsync("m1", Ct))!.Stage);
        Assert.Equal((MoveCluster.Source, 0L), PlacementOf(await second.Models.GetAsync("m1", Ct)));
        Assert.Null(await second.Client(MoveCluster.Target).GetRuntimeAsync("m1", Ct));

        var (status, body) = await second.PostAsync("/api/models/m1/move/force-commit", new { confirm = "dgx" });
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        await second.WaitForMoveEndAsync("m1");
        Assert.Equal((MoveCluster.Target, 1L), PlacementOf(await second.Models.GetAsync("m1", Ct)));
        await ExitEnv.UntilAsync(async () => (await second.Client(MoveCluster.Target).GetRuntimeAsync("m1", Ct))?.State == ModelState.Ready, "m1 Ready on pc-office");
        cluster.Dgx.SetPartitioned(false);
        await ExitEnv.UntilAsync(async () =>
        {
            try
            {
                return await second.Client(MoveCluster.Source).GetRuntimeAsync("m1", Ct) is null;
            }
            catch (AgentUnreachableException)
            {
                return false;
            }
        }, "the stale instance on dgx stopped");
    }

    // ================================================================ unplace

    [Fact]
    public async Task Unplace_RunningModel_StopsIt_ConfirmsIt_AndClearsThePlacement()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");

        await Assert.ThrowsAsync<MoveRefusedException>(() => master.Moves.UnplaceAsync("m1",
            new ModelUnplaceRequest { Password = MoveCluster.Password }, new MoveCaller("api:test", "sid:forged"), Ct));
        var (status, body) = await master.PostAsync("/api/models/m1/unplace", new { });

        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        Assert.False(body.TryGetProperty("placement", out _));
        Assert.Null((await master.Models.GetAsync("m1", Ct))!.Placement);
        Assert.Null(await master.Client(MoveCluster.Source).GetRuntimeAsync("m1", Ct));
        Assert.Null(await master.Ownership.GetForModelAsync("m1", Ct));
        var (again, _) = await master.PostAsync("/api/models/m1/unplace", null);
        Assert.Equal(HttpStatusCode.OK, again); // already unplaced: nothing to do
        var lines = (await master.AuditAsync("m1")).Where(l => l.Action == AuditActions.ModelUnplace).ToList();
        Assert.Contains(lines, l => l.Result == AuditResults.Succeeded && l.NodeId == MoveCluster.Source);
        Assert.Contains(lines, l => l.Result == AuditResults.Refused && l.ErrorCode == ErrorCodes.ReauthenticationFailed);
        // An unplaced model is not started anywhere.
        using var start = await master.Http.PostAsync("/api/models/m1/start", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
    }

    [Fact]
    public async Task Unplace_IsRefused_ForAHeldModel_AndWhileItsNodeIsOffline()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"), MoveCluster.Model("held", "held-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await master.Ownership.TryAcquireAsync(new OwnershipRecord
        {
            Id = "edit-1", Kind = OwnershipKind.Edit, ModelId = "held", Stage = "editing", StartedAt = DateTimeOffset.UtcNow,
        }, Ct);

        await master.AssertProblemAsync("/api/models/held/unplace", null, HttpStatusCode.Conflict, ErrorCodes.ModelBusy);
        cluster.Dgx.SetPartitioned(true);
        await master.WaitForNodeAsync(MoveCluster.Source, n => !n.IsServing, "dgx offline");
        var offline = await master.AssertProblemAsync("/api/models/m1/unplace", null, HttpStatusCode.ServiceUnavailable, ErrorCodes.NodeOffline);

        Assert.Contains("stays placed", offline.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(MoveCluster.Source, (await master.Models.GetAsync("m1", Ct))!.Placement?.NodeId);
        Assert.Equal(MoveCluster.Source, (await master.Models.GetAsync("held", Ct))!.Placement?.NodeId);
        cluster.Dgx.SetPartitioned(false);
    }

    [Fact]
    public async Task MasterCrash_DuringAnUnplace_TheNextMasterFinishesIt()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        var first = await cluster.StartMasterAsync();
        await first.StartModelAsync("m1");
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Moves.CheckpointForTests = async (point, _, ct) =>
        {
            if (point == MoveService.UnplaceWriting)
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };

        var pending = first.PostAsync("/api/models/m1/unplace", new { password = MoveCluster.Password });
        await reached.Task.WaitAsync(ExitEnv.Patience, Ct);
        await first.DisposeAsync();
        _ = await Record.ExceptionAsync(() => pending); // the request died with the master
        var record = Assert.Single(await cluster.ReadOwnershipAsync());
        Assert.Equal((OwnershipKind.Edit, MoveService.UnplaceWriting), (record.Kind, record.Stage));
        Assert.Equal(MoveCluster.Source, (await cluster.ReadModelAsync("m1"))!.Placement?.NodeId);

        await using var second = await cluster.StartMasterAsync();
        await ExitEnv.UntilAsync(async () => (await second.Models.GetAsync("m1", Ct))!.Placement is null, "the unplace finished");
        await ExitEnv.UntilAsync(async () => await second.Ownership.GetForModelAsync("m1", Ct) is null, "the unplace record released");
        Assert.Null(await second.Client(MoveCluster.Source).GetRuntimeAsync("m1", Ct));
    }

    [Fact]
    public async Task StaleInstanceSweep_LeavesPlacedInstances_AndUnknownContainers_Alone()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");

        Assert.Equal(0, await master.Moves.SweepStaleInstancesAsync(Ct));
        await Task.Delay(1000, Ct); // several background passes
        Assert.Equal(ModelState.Ready, (await master.Client(MoveCluster.Source).GetRuntimeAsync("m1", Ct))?.State);
        Assert.Equal(0, await master.CountOperationsAsync(MoveCluster.Source, OperationKinds.Stop, "m1"));
    }

    /// <summary>
    /// W8 review (MN-128): the live single-DGX configuration (one node, the default <c>MirbadLLM:Moves</c> settings apart
    /// from a fast sweep) never stops anything: not an unplaced model's instance, not a model placed on a node id that is
    /// not registered, not a tuning candidate, not a model a job holds, not an adopted container from before P1 and not an
    /// unknown container. Without the one-node rule the first two would be stopped.
    /// </summary>
    [Fact]
    public async Task StaleInstanceSweep_OnASingleDgx_StopsNothing()
    {
        await using var node = await FakeNode.StartAsync(new FakeAgentOptions { HeartbeatInterval = TimeSpan.FromMilliseconds(300) },
            ExitEnv.Model("placed"),
            ExitEnv.Model("unplaced") with { Placement = null },
            ExitEnv.Model("elsewhere") with { Placement = new ModelPlacement("dgx-old") },
            ExitEnv.Model("tuned") with { Placement = null },
            ExitEnv.Model("held") with { Placement = null },
            ExitEnv.Model("adopted"));
        await using var master = await node.Master(settings: new Dictionary<string, string?> { ["MirbadLLM:Moves:SweepInterval"] = "00:00:00.100" }).StartAsync();
        var client = master.Client;
        // Boot job recovery would release the job record below and relaunch the tuning candidate as left over (D104).
        await ExitEnv.UntilAsync(() => Task.FromResult(master.Get<MirbadLLM.Orchestration.Benchmarking.ModelJobService>().HasRecovered(ExitEnv.NodeId)), "boot job recovery of dgx");
        var held = new OwnershipRecord
        {
            Id = "job-sweep-test",
            Kind = OwnershipKind.Job,
            ModelId = "held",
            Stage = "running",
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Assert.True((await master.Get<IOwnershipStore>().TryAcquireAsync(held, Ct)).Acquired);

        string[] ids = ["placed", "unplaced", "elsewhere", "tuned", "held", "adopted", "ghost"];
        foreach (string id in ids)
        {
            var labels = id switch
            {
                "tuned" => new Dictionary<string, string> { [ContainerLabels.Tuning] = "1" },
                _ => null,
            };
            await LaunchReadyAsync(client, id, labels);
        }

        // The sweep reads the node's instances from the master's cache: wait until it lists every one.
        await ExitEnv.UntilAsync(() => master.Cache.GetNode(ExitEnv.NodeId) is { } dgx &&
            ids.All(id => dgx.Runtimes.Any(r => r.ModelId == id && r.State == ModelState.Ready)), "every instance Ready in the master's node cache");

        Assert.Equal(0, await master.Get<MoveService>().SweepStaleInstancesAsync(Ct));
        await Task.Delay(1500, Ct); // fifteen background passes
        foreach (string id in ids)
        {
            Assert.Equal(ModelState.Ready, (await client.GetRuntimeAsync(id, Ct))?.State);
        }

        Assert.DoesNotContain(await client.ListOperationsAsync(new OperationsQuery(), Ct), o => o.Kind == OperationKinds.Stop);
    }

    /// <summary>
    /// W8 review (MN-128): with two nodes the sweep stops an instance of a model placed elsewhere, but never a tuning
    /// candidate or an instance of a model a job holds.
    /// </summary>
    [Fact]
    public async Task StaleInstanceSweep_WithTwoNodes_StopsAStaleInstance_ButNotTuningCandidates_OrJobHeldModels()
    {
        await using var cluster = await MoveCluster.StartAsync(
            MoveCluster.Model("stale", "stale-ckpt"), MoveCluster.Model("tuned", "tuned-ckpt"), MoveCluster.Model("held", "held-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        // Boot job recovery would release the job record below and relaunch the tuning candidate as left over (D104).
        var jobs = master.Get<MirbadLLM.Orchestration.Benchmarking.ModelJobService>();
        await ExitEnv.UntilAsync(() => Task.FromResult(jobs.HasRecovered(ExitEnv.NodeId) && jobs.HasRecovered(MoveCluster.Target)), "boot job recovery of both nodes");
        var held = new OwnershipRecord
        {
            Id = "job-sweep-test",
            Kind = OwnershipKind.Job,
            ModelId = "held",
            Stage = "running",
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Assert.True((await master.Ownership.TryAcquireAsync(held, Ct)).Acquired);
        var pc = master.Client(MoveCluster.Target);

        // All three are placed on dgx; the instances run on pc-office.
        await LaunchReadyAsync(pc, "tuned", new Dictionary<string, string> { [ContainerLabels.Tuning] = "1" });
        await LaunchReadyAsync(pc, "held");
        await pc.LaunchAsync("stale", AgentContractSuite.LaunchFor("stale", force: true), Ct); // the sweep may stop it before it is Ready

        await ExitEnv.UntilAsync(async () => await master.CountOperationsAsync(MoveCluster.Target, OperationKinds.Stop, "stale") > 0 &&
            await pc.GetRuntimeAsync("stale", Ct) is null, "the stale instance on pc-office stopped");
        Assert.Equal(ModelState.Ready, (await pc.GetRuntimeAsync("tuned", Ct))?.State);
        Assert.Equal(ModelState.Ready, (await pc.GetRuntimeAsync("held", Ct))?.State);
        Assert.Equal(0, await master.CountOperationsAsync(MoveCluster.Target, OperationKinds.Stop, "tuned"));
        Assert.Equal(0, await master.CountOperationsAsync(MoveCluster.Target, OperationKinds.Stop, "held"));
    }

    /// <summary>
    /// W8 review (MN-128): a gated repo moves with a Hugging Face token. The target's download gets it; it is never written
    /// to ownership.json, the audit log or the move view.
    /// </summary>
    [Fact]
    public async Task Move_WithAHuggingFaceToken_PassesItToTheTargetDownload_AndStoresItNowhere()
    {
        const string token = "hf_moveTestSecret0123456789";
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await cluster.Pc.SetDownloadsHeldAsync(true);

        var (status, body) = await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, hfToken = token });

        Assert.True(status == HttpStatusCode.Accepted, $"{(int)status} {body}");
        Assert.DoesNotContain(token, body.GetRawText(), StringComparison.Ordinal);
        await ExitEnv.UntilAsync(() => cluster.Pc.DownloadTokens.Count > 0, "the target's download started");
        Assert.Equal(token, Assert.Single(cluster.Pc.DownloadTokens));
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(cluster.DataDir, "ownership.json")), StringComparison.Ordinal);
        Assert.DoesNotContain(token, (await master.GetJsonAsync("/api/models/m1/move")).GetRawText(), StringComparison.Ordinal);

        await cluster.Pc.SetDownloadsHeldAsync(false);
        await master.WaitForMoveEndAsync("m1");
        Assert.Equal(MoveCluster.Target, (await master.Models.GetAsync("m1", Ct))!.Placement!.NodeId);
        Assert.DoesNotContain(await master.AuditAsync(null), l => (l.Detail ?? "").Contains(token, StringComparison.Ordinal));
        foreach (string file in Directory.GetFiles(cluster.DataDir, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain(token, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// W8 review (MN-128): a target that refuses the start after the commit (memory admission) ends the move with the
    /// reason, audited as failed, instead of retrying it forever while the record holds the model.
    /// </summary>
    [Fact]
    public async Task Move_TargetRefusesTheStart_EndsTheMoveWithTheReason_AndReleasesTheModel()
    {
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"));
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");
        cluster.Pc.SetAvailableMemory(0);

        var (status, body) = await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });

        Assert.True(status == HttpStatusCode.Accepted, $"{(int)status} {body}");
        await master.WaitForMoveEndAsync("m1");
        Assert.Equal((MoveCluster.Target, 1L), PlacementOf(await master.Models.GetAsync("m1", Ct)));
        var view = await master.GetJsonAsync("/api/models/m1/move");
        Assert.Equal(MoveStages.Done, view.GetProperty("record").GetProperty("stage").GetString());
        Assert.Contains("start was refused", view.GetProperty("record").GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains(await master.AuditAsync("m1"), l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Failed && l.Actor == AuditActors.SystemMove);
        Assert.Null(await master.Client(MoveCluster.Target).GetRuntimeAsync("m1", Ct));
    }

    /// <summary>
    /// W8 review (MN-128): another model starting on the target holds its start slot when the move starts the model there
    /// (409 start_conflict). That is momentary: the move tries again and ends without an error once the slot is free.
    /// </summary>
    [Fact]
    public async Task Move_TargetBusyStartingAnotherModel_RetriesTheStart_AndEndsWithoutAnError()
    {
        var other = MoveCluster.Model("other", "other-ckpt") with { Placement = new ModelPlacement(MoveCluster.Target) };
        await using var cluster = await MoveCluster.StartAsync(MoveCluster.Model("m1", "m1-ckpt"), other);
        await using var master = await cluster.StartMasterAsync();
        await master.StartModelAsync("m1");
        await cluster.Pc.SetStartsHeldAsync(true);
        using (var start = await master.Http.PostAsync("/api/models/other/start", null, Ct))
        {
            Assert.True(start.IsSuccessStatusCode, $"start other: {(int)start.StatusCode}");
        }

        await ExitEnv.UntilAsync(async () => (await master.Client(MoveCluster.Target).GetRuntimeAsync("other", Ct))?.State == ModelState.Starting, "other Starting on pc-office");

        var (status, body) = await master.PostAsync("/api/models/m1/move", new { targetNodeId = MoveCluster.Target, password = MoveCluster.Password });
        Assert.True(status == HttpStatusCode.Accepted, $"{(int)status} {body}");
        var waiting = await ExitEnv.UntilValueAsync(async () => await master.Ownership.GetForModelAsync("m1", Ct) is { Stage: MoveStages.StartingTarget, Error: { } e } r && e.Contains("tries again", StringComparison.Ordinal) ? r : null,
            "the move retrying its start on pc-office");
        Assert.Contains("still starting", waiting.Error, StringComparison.Ordinal);

        await cluster.Pc.SetStartsHeldAsync(false);
        await master.WaitForMoveEndAsync("m1");
        var view = await master.GetJsonAsync("/api/models/m1/move");
        Assert.Equal(MoveStages.Done, view.GetProperty("record").GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("record").TryGetProperty("error", out var error) ? error.ValueKind : JsonValueKind.Null);
        await ExitEnv.UntilAsync(async () => (await master.Client(MoveCluster.Target).GetRuntimeAsync("m1", Ct))?.State == ModelState.Ready, "m1 Ready on pc-office");
    }

    /// <summary>Launches an instance directly through the agent (one start at a time) and waits until it is Ready.</summary>
    private static async Task LaunchReadyAsync(IAgentClient client, string modelId, IReadOnlyDictionary<string, string>? labels = null)
    {
        await client.LaunchAsync(modelId, AgentContractSuite.LaunchFor(modelId, force: true, extraLabels: labels), Ct);
        await ExitEnv.UntilAsync(async () => (await client.GetRuntimeAsync(modelId, Ct))?.State == ModelState.Ready, $"{modelId} Ready");
    }

    private static (string NodeId, long Generation) PlacementOf(ModelDefinition? model) =>
        (model?.Placement?.NodeId ?? "(unplaced)", model?.Placement?.Generation ?? -1);
}

/// <summary>The runner, for tests that send an agent mutation of their own through the master's clients.</summary>
internal sealed record AgentOperationRunnerAccessor(MirbadLLM.Orchestration.Agents.AgentOperationRunner Runner);

/// <summary>
/// Two FakeAgents (<c>dgx</c>, <c>pc-office</c>) sharing one master CA, registered and approved in one master data
/// directory with fast heartbeats, and real masters (<see cref="ExitMaster"/>) over it with an admin password, fast move
/// polling and a 3 s Offline rule.
/// </summary>
internal sealed class MoveCluster : IAsyncDisposable
{
    public const string Source = "dgx";
    public const string Target = "pc-office";
    public const string Password = "move-test-password";

    private MoveCluster(CertificateAuthority ca, FakeAgent dgx, FakeAgent pc, string dataDir)
    {
        Ca = ca;
        Dgx = dgx;
        Pc = pc;
        DataDir = dataDir;
    }

    public CertificateAuthority Ca { get; }

    public FakeAgent Dgx { get; }

    public FakeAgent Pc { get; }

    public string DataDir { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A movable model on <c>dgx</c>: its checkpoint folder has a Hugging Face source.</summary>
    public static ModelDefinition Model(string id, string folder) => ExitEnv.Model(id) with
    {
        ModelPath = folder,
        Sources = [new ModelSource("org/" + folder, folder)],
    };

    /// <summary>A registered node record without an agent (draining, revoked, …).</summary>
    public static NodeRecord ExtraNode(string id, Func<NodeRecord, NodeRecord> shape) => shape(new NodeRecord
    {
        Id = id,
        Uid = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()),
        Address = $"https://{id}.invalid:9443",
        Approved = true,
        CertSpkiSha256 = Convert.ToBase64String(new byte[32]),
    });

    public static Task<MoveCluster> StartAsync(params ModelDefinition[] models) => StartAsync(null, null, [], models);

    public static async Task<MoveCluster> StartAsync(
        Func<FakeAgentOptions, FakeAgentOptions>? dgxOptions = null,
        Func<FakeAgentOptions, FakeAgentOptions>? pcOptions = null,
        NodeRecord[]? extraNodes = null,
        params ModelDefinition[] models)
    {
        var ca = CertificateAuthority.Create();
        var common = new FakeAgentOptions { Ca = ca, HeartbeatInterval = TimeSpan.FromMilliseconds(300) };
        var dgx = await FakeAgent.StartAsync((dgxOptions ?? (o => o))(common with { NodeId = Source }), Ct);
        var pc = await FakeAgent.StartAsync((pcOptions ?? (o => o))(common with { NodeId = Target }), Ct);
        string dataDir = ExitEnv.NewDirectory("move");
        using (var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), TimeProvider.System))
        {
            foreach (var agent in new[] { dgx, pc })
            {
                await registry.UpsertAsync(new NodeRecord
                {
                    Id = agent.NodeId,
                    Uid = agent.NodeUid,
                    Address = agent.BaseAddress.AbsoluteUri,
                    Approved = true,
                    CertSpkiSha256 = agent.CurrentSpki,
                }, Ct);
            }

            foreach (var extra in extraNodes ?? [])
            {
                await registry.UpsertAsync(extra, Ct);
            }
        }

        await ExitEnv.SeedModelsAsync(dataDir, models);
        return new MoveCluster(ca, dgx, pc, dataDir);
    }

    /// <summary>Boots a master over the data directory, logs in, and waits for both nodes (dgx only when <paramref name="waitForSource"/>).</summary>
    public async Task<MoveMaster> StartMasterAsync(bool waitForSource = true)
    {
        var settings = new Dictionary<string, string?>
        {
            ["MirbadLLM:AdminPassword"] = Password,
            ["MirbadLLM:Moves:PollInterval"] = "00:00:00.050",
            ["MirbadLLM:Moves:RetryInterval"] = "00:00:00.150",
            ["MirbadLLM:Moves:SweepInterval"] = "00:00:00.250",
            ["MirbadLLM:Moves:StopTimeout"] = "00:00:30",
        };
        var exit = new ExitMaster(DataDir, Ca, settings, builder => builder.ConfigureTestServices(services =>
        {
            // A 3 s Offline rule (heartbeats every 300 ms) instead of 15 s, so source-offline tests stay short.
            services.AddSingleton(sp => new NodeStatusCache(
                sp.GetRequiredService<IAgentClientFactory>(),
                sp.GetRequiredService<IMasterIdentity>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<IModelRegistry>(),
                sp.GetRequiredService<ILogger<NodeStatusCache>>(),
                new NodeMonitorOptions
                {
                    OfflineAfter = TimeSpan.FromSeconds(3),
                    ReconnectInitialDelay = TimeSpan.FromMilliseconds(200),
                    ReconnectMaxDelay = TimeSpan.FromSeconds(1),
                }));
            services.AddSingleton(sp => new AgentOperationRunnerAccessor(sp.GetRequiredService<MirbadLLM.Orchestration.Agents.AgentOperationRunner>()));
        }));
        var master = new MoveMaster(exit);
        try
        {
            await exit.StartAsync(waitForSource ? NodeState.Online : null);
            await master.LoginAsync();
            await master.WaitForNodeAsync(Target, n => n.State == NodeState.Online, "pc-office Online");
            return master;
        }
        catch
        {
            await master.DisposeAsync();
            throw;
        }
    }

    /// <summary><c>ownership.json</c> as a fresh master would read it.</summary>
    public async Task<IReadOnlyList<OwnershipRecord>> ReadOwnershipAsync()
    {
        using var store = new OwnershipStore(Options.Create(new MirbadLLMOptions { DataPath = DataDir }));
        return await store.ListAsync(Ct);
    }

    /// <summary>A model as stored in <c>models.json</c> (no master running).</summary>
    public async Task<ModelDefinition?> ReadModelAsync(string id)
    {
        using var registry = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = DataDir }));
        return await registry.GetAsync(id, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Dgx.DisposeAsync();
        await Pc.DisposeAsync();
        Ca.Dispose();
        ExitEnv.DeleteDirectory(DataDir);
    }
}

/// <summary>A running master of a <see cref="MoveCluster"/> with a logged-in admin client.</summary>
internal sealed partial class MoveMaster(ExitMaster master) : IAsyncDisposable
{
    private int _disposed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ExitMaster Factory => master;

    public HttpClient Http => master.Http;

    public INodeStatusCache Cache => master.Cache;

    public IModelRegistry Models => master.Get<IModelRegistry>();

    public IOwnershipStore Ownership => master.Get<IOwnershipStore>();

    public MoveService Moves => master.Get<MoveService>();

    public T Get<T>()
        where T : notnull => master.Get<T>();

    public IAgentClient Client(string nodeId) => master.Get<IAgentClientFactory>().GetClient(nodeId);

    public async Task LoginAsync()
    {
        using var page = await Http.GetAsync("/login", Ct);
        page.EnsureSuccessStatusCode();
        var match = AntiforgeryToken().Match(await page.Content.ReadAsStringAsync(Ct));
        Assert.True(match.Success, "no antiforgery token on /login");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = match.Groups[1].Value,
            ["password"] = MoveCluster.Password,
            ["returnUrl"] = "",
        });
        using var login = await Http.PostAsync("/login/submit", form, Ct);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.ToString());
    }

    public async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object? body)
    {
        using var response = body is null ? await Http.PostAsync(path, null, Ct) : await Http.PostAsJsonAsync(path, body, Ct);
        string text = await response.Content.ReadAsStringAsync(Ct);
        try
        {
            return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
        catch (JsonException)
        {
            return (response.StatusCode, JsonSerializer.SerializeToElement(new { text }));
        }
    }

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await Http.GetAsync(path, Ct);
        string text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Posts and asserts a ProblemDetails refusal with <paramref name="code"/>; returns its body.</summary>
    public async Task<JsonElement> AssertProblemAsync(string path, object? body, HttpStatusCode status, string code)
    {
        var (actual, problem) = await PostAsync(path, body);
        Assert.True(actual == status, $"{path}: expected {(int)status} {code}, got {(int)actual} {problem}");
        Assert.Equal(code, problem.GetProperty("code").GetString());
        return problem;
    }

    public async Task StartModelAsync(string modelId)
    {
        using var response = await Http.PostAsync($"/api/models/{modelId}/start", null, Ct);
        Assert.True(response.IsSuccessStatusCode, $"start {modelId}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(Ct)}");
        await ExitEnv.UntilAsync(async () => (await Client(MoveCluster.Source).GetRuntimeAsync(modelId, Ct))?.State == ModelState.Ready, $"{modelId} Ready on dgx");
        await ExitEnv.UntilAsync(() => master.Cache.GetRuntime(modelId)?.State == ModelState.Ready, $"{modelId} Ready in the master's cache");
    }

    public async Task<NodeLiveStatus> WaitForNodeAsync(string nodeId, Func<NodeLiveStatus, bool> predicate, string what)
    {
        NodeLiveStatus? node = null;
        await ExitEnv.UntilAsync(() => (node = master.Cache.GetNode(nodeId)) is { } n && predicate(n), what);
        return node!;
    }

    public Task<OwnershipRecord> WaitForStageAsync(string modelId, string stage) =>
        ExitEnv.UntilValueAsync(async () => await Ownership.GetForModelAsync(modelId, Ct) is { } r && r.Stage == stage ? r : null, $"{modelId} at {stage}");

    public Task WaitForMoveEndAsync(string modelId, TimeSpan? timeout = null) =>
        ExitEnv.UntilAsync(async () => await Ownership.GetForModelAsync(modelId, Ct) is null, $"the move of {modelId} to end", timeout);

    public async Task<int> CountOperationsAsync(string nodeId, string kind, string target) =>
        (await Client(nodeId).ListOperationsAsync(new OperationsQuery(), Ct)).Count(o => o.Kind == kind && o.Target == target);

    public async Task<IReadOnlyList<AuditRecord>> AuditAsync(string? modelId)
    {
        var lines = await master.Get<IAuditLog>().ReadAsync(new AuditQuery { ModelId = modelId, Max = 500 }, Ct);
        return lines;
    }

    /// <summary>
    /// The end state of a completed move to <c>pc-office</c>: placed there at generation 1 (committed exactly once), the
    /// checkpoint downloaded there by exactly one download, exactly one stop on dgx when it ran there, no instance on dgx,
    /// the instance on pc-office (one launch) when it was running, nothing holding the model.
    /// </summary>
    public async Task AssertMovedAsync(string modelId, string folder, bool expectRunning)
    {
        var placement = (await Models.GetAsync(modelId, Ct))!.Placement!;
        Assert.Equal((MoveCluster.Target, 1L), (placement.NodeId, placement.Generation));
        Assert.Null(await Ownership.GetForModelAsync(modelId, Ct));
        Assert.Contains(await Client(MoveCluster.Target).ListModelFoldersAsync(Ct), f => f.Folder == folder && !f.Partial);
        Assert.Equal(1, await CountOperationsAsync(MoveCluster.Target, OperationKinds.Download, folder));
        Assert.Null(await Client(MoveCluster.Source).GetRuntimeAsync(modelId, Ct));
        if (expectRunning)
        {
            Assert.Equal(1, await CountOperationsAsync(MoveCluster.Source, OperationKinds.Stop, modelId));
            await ExitEnv.UntilAsync(async () => (await Client(MoveCluster.Target).GetRuntimeAsync(modelId, Ct))?.State == ModelState.Ready, $"{modelId} Ready on pc-office");
            Assert.Equal(1, await CountOperationsAsync(MoveCluster.Target, OperationKinds.Launch, modelId));
        }
        else
        {
            Assert.Null(await Client(MoveCluster.Target).GetRuntimeAsync(modelId, Ct));
            Assert.Equal(0, await CountOperationsAsync(MoveCluster.Target, OperationKinds.Launch, modelId));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await master.DisposeAsync();
        }
    }

    [GeneratedRegex("""name="__RequestVerificationToken" value="([^"]+)""")]
    private static partial Regex AntiforgeryToken();
}
