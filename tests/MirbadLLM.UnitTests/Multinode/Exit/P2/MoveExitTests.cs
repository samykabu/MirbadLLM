using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, moves (design §5.6, §6.4, §6.7, §14 P2; D87, D92–D94, D101, D103): every test drives the real master
/// (<c>Program</c> on Kestrel, its own CA, admin login) and the real agents of a <see cref="P2Fleet"/> — the unified DGX
/// enrolled from its bundle and a discrete two-GPU PC joined with a token — through <c>/api/models/{id}/move</c>,
/// <c>/move/force-commit</c> and <c>/unplace</c>, and asserts what an operator sees: the agents' operation logs, the
/// checkpoint folder and the containers on each node, the registry's placement and generation, <c>/v1</c> traffic on the
/// node's model server, <c>ownership.json</c> and the audit log. Only the outside world of each node is faked (Docker,
/// hostd, <c>nvidia-smi</c>, the Hub, the model server). Unlike <c>MoveTests</c> (FakeAgents), the stops, downloads,
/// admission refusals and start conflicts here come from the real agent code.
/// </summary>
public sealed partial class MoveExitTests
{
    private const string Folder = "m1-ckpt";

    // ================================================================ 1. unified -> discrete, with the download

    /// <summary>
    /// A running model on the DGX moves to GPU 1 of the PC: the checkpoint is downloaded on the PC (one download, the
    /// folder appears there), the DGX instance is stopped once, the placement is committed once (generation + 1, the GPU
    /// UUID stored), the PC launches it once on exactly that card, <c>/v1</c> is served by the PC's model server and not
    /// the DGX's, and the audit log has the admin's accepted line and system:move's succeeded line.
    /// </summary>
    [Fact]
    public async Task ARunningModelMovesFromTheDgxToOnePcGpuWithItsCheckpointDownloadedThere()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Movable("m1", Folder)] });
        SeedCheckpoint(fleet.Dgx, Folder);
        var hub = new RecordingHub();
        var pc = await JoinPcAsync(fleet, hub);
        var master = fleet.Master;
        await master.StartModelAsync("m1");
        Assert.Equal("dgx", master.Cache.ResolveServedName("m1")?.NodeId);
        Assert.False(Directory.Exists(Path.Combine(pc.Node.ModelsRoot, Folder)), "the PC has no checkpoint before the move");
        long generation = (await master.Models.GetAsync("m1", Ct))!.Placement!.Generation;
        int dgxCreates = fleet.Dgx.CreateBodiesOf("m1").Count;

        var record = await MoveAsync(master, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[1] }, password = Password });

        Assert.Equal(MoveStages.Downloading, record.GetProperty("stage").GetString());
        Assert.Equal("Move", record.GetProperty("kind").GetString());
        var details = record.GetProperty("move");
        Assert.Equal(("dgx", "pc"), (details.GetProperty("sourceNodeId").GetString(), details.GetProperty("targetNodeId").GetString()));
        Assert.Equal([PcGpus[1]], details.GetProperty("targetGpuUuids").EnumerateArray().Select(g => g.GetString()));
        Assert.True(details.GetProperty("wasRunning").GetBoolean());
        var view = await WaitForMoveEndAsync(master, "m1");
        Assert.Equal(MoveStages.Done, view.GetProperty("record").GetProperty("stage").GetString());
        Assert.Null(ErrorOf(view));
        Assert.False(view.GetProperty("active").GetBoolean());

        // Committed once, on the selected card.
        var placement = (await master.Models.GetAsync("m1", Ct))!.Placement!;
        Assert.Equal(("pc", generation + 1), (placement.NodeId, placement.Generation));
        Assert.Equal(new[] { PcGpus[1] }, placement.GpuUuids);
        Assert.Null(await master.Get<IOwnershipStore>().GetForModelAsync("m1", Ct));

        // The checkpoint was downloaded on the PC, by one download of the model's source.
        Assert.True(File.Exists(Path.Combine(pc.Node.ModelsRoot, Folder, "config.json")));
        Assert.Contains(await master.Client("pc").ListModelFoldersAsync(Ct), f => f.Folder == Folder && !f.Partial && f.SizeBytes > 0);
        Assert.Equal(1, await CountAsync(master, "pc", OperationKinds.Download, Folder));
        Assert.Equal(["org/m1"], hub.Starts.Select(s => s.RepoId));

        // The DGX instance was stopped once and nothing started there again.
        Assert.Null(await master.Client("dgx").GetRuntimeAsync("m1", Ct));
        Assert.Equal(1, await CountAsync(master, "dgx", OperationKinds.Stop, "m1"));
        Assert.Equal(dgxCreates, fleet.Dgx.CreateBodiesOf("m1").Count);

        // Started once on the PC, with exactly GPU 1.
        await ExitEnv.UntilAsync(async () => (await master.Client("pc").GetRuntimeAsync("m1", Ct))?.State == ModelState.Ready, "m1 Ready on pc");
        Assert.Equal(1, await CountAsync(master, "pc", OperationKinds.Launch, "m1"));
        string create = Assert.Single(pc.CreateBodiesOf("m1"));
        using (var json = JsonDocument.Parse(create))
        {
            var device = json.RootElement.GetProperty("HostConfig").GetProperty("DeviceRequests")[0];
            Assert.Equal([PcGpus[1]], device.GetProperty("DeviceIDs").EnumerateArray().Select(d => d.GetString()));
        }

        // /v1 is served from the PC: the request reaches the PC's model server, not the DGX's.
        await ExitEnv.UntilAsync(() => master.Cache.ResolveServedName("m1") is { NodeId: "pc", ModelState: ModelState.Ready }, "/v1 routing m1 to pc");
        double pcServed = await ServedAsync(pc);
        double dgxServed = await ServedAsync(fleet.Dgx);
        await ChatAsync(master, "m1");
        Assert.Equal(pcServed + 1, await ServedAsync(pc));
        Assert.Equal(dgxServed, await ServedAsync(fleet.Dgx));

        var audit = await fleet.AuditAsync(new AuditQuery { ModelId = "m1", Max = 500 });
        Assert.Single(audit, l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Accepted && l.Actor.StartsWith("admin:", StringComparison.Ordinal) && l.NodeId == "pc");
        var done = Assert.Single(audit, l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Succeeded);
        Assert.Equal(AuditActors.SystemMove, done.Actor);
        Assert.Contains("moved from dgx to pc", done.Detail, StringComparison.Ordinal);
        // The move's agent operations are attributed to system:move on both nodes.
        Assert.Contains(audit, l => l.Actor == AuditActors.SystemMove && l.OperationId is not null && l.NodeId == "dgx");
        Assert.Contains(audit, l => l.Actor == AuditActors.SystemMove && l.OperationId is not null && l.NodeId == "pc");
    }

    // ================================================================ 2. a master crash mid-move resumes

    /// <summary>
    /// The master stops while the PC is still downloading (the Hub holds the download) and a new master boots over the
    /// same data: it resumes the record at <c>downloading</c>, adopts the running download instead of starting another,
    /// and finishes the move once: one download, one stop on the DGX, one launch on the PC, generation + 1.
    /// </summary>
    [Fact]
    public async Task AMasterRestartDuringTheDownloadResumesTheMoveAndFinishesItOnce()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Movable("m1", Folder)] });
        SeedCheckpoint(fleet.Dgx, Folder);
        var hub = new RecordingHub();
        var pc = await JoinPcAsync(fleet, hub);
        await fleet.Master.StartModelAsync("m1");
        long generation = (await fleet.Master.Models.GetAsync("m1", Ct))!.Placement!.Generation;
        pc.Node.Downloads.Held = true;

        string recordId = (await MoveAsync(fleet.Master, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[0] }, password = Password }))
            .GetProperty("id").GetString()!;
        await ExitEnv.UntilAsync(async () => (await fleet.Master.Client("pc").GetDownloadAsync(Folder, Ct))?.State == DownloadState.Downloading, "the download running on pc");
        // The source keeps serving while the checkpoint downloads.
        Assert.Equal(ModelState.Ready, (await fleet.Master.Client("dgx").GetRuntimeAsync("m1", Ct))?.State);

        await fleet.RestartMasterAsync();
        var master = fleet.Master;
        var resumed = await master.Get<IOwnershipStore>().GetForModelAsync("m1", Ct);
        Assert.Equal((recordId, MoveStages.Downloading), (resumed?.Id, resumed?.Stage));
        await ExitEnv.UntilAsync(() => master.Log.Records.Any(r => r.Message.Contains($"Resuming {recordId}", StringComparison.Ordinal)), "the resume log line");

        pc.Node.Downloads.Held = false;
        var view = await WaitForMoveEndAsync(master, "m1", TimeSpan.FromSeconds(120));
        Assert.Equal((recordId, MoveStages.Done), (view.GetProperty("record").GetProperty("id").GetString(), view.GetProperty("record").GetProperty("stage").GetString()));
        Assert.Null(ErrorOf(view));

        var placement = (await master.Models.GetAsync("m1", Ct))!.Placement!;
        Assert.Equal(("pc", generation + 1), (placement.NodeId, placement.Generation));
        Assert.Single(hub.Starts);
        Assert.Equal(1, await CountAsync(master, "pc", OperationKinds.Download, Folder));
        Assert.Equal(1, await CountAsync(master, "dgx", OperationKinds.Stop, "m1"));
        await ExitEnv.UntilAsync(async () => (await master.Client("pc").GetRuntimeAsync("m1", Ct))?.State == ModelState.Ready, "m1 Ready on pc");
        Assert.Equal(1, await CountAsync(master, "pc", OperationKinds.Launch, "m1"));
        Assert.Single(pc.CreateBodiesOf("m1"));
        Assert.Null(await master.Client("dgx").GetRuntimeAsync("m1", Ct));
    }

    // ================================================================ 3. offline source, force-commit, the sweep

    /// <summary>
    /// The DGX agent stops while the PC downloads: once the checkpoint is there the move pauses at
    /// <c>stopping-source</c> (the PC stays stopped, nothing is committed) and <c>GET /move</c> offers the force-commit with
    /// its consequence. The operator force-commits (a typo in the confirmation is refused first): the model starts on the
    /// PC and <c>/v1</c> goes there. When the DGX agent returns, its old instance (the container kept running while the
    /// agent was down) is stopped by the stale-instance sweep, once.
    /// </summary>
    [Fact]
    public async Task AnOfflineSourcePausesTheMoveAndAfterAForceCommitTheSweepStopsTheStaleInstanceWhenItReturns()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Movable("m1", Folder)] });
        SeedCheckpoint(fleet.Dgx, Folder);
        var pc = await JoinPcAsync(fleet);
        var master = fleet.Master;
        await master.StartModelAsync("m1");
        long generation = (await master.Models.GetAsync("m1", Ct))!.Placement!.Generation;
        pc.Node.Downloads.Held = true;
        await MoveAsync(master, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[0] }, password = Password });
        await ExitEnv.UntilAsync(async () => (await master.Client("pc").GetDownloadAsync(Folder, Ct))?.State == DownloadState.Downloading, "the download running on pc");

        await fleet.Dgx.StopAsync();
        await master.WaitForNodeAsync("dgx", n => !n.IsServing, "dgx not serving");
        pc.Node.Downloads.Held = false;
        await ExitEnv.UntilAsync(async () => await master.Get<IOwnershipStore>().GetForModelAsync("m1", Ct) is { Stage: MoveStages.StoppingSource, Error: { } note } &&
            note.Contains("force-commit", StringComparison.Ordinal), "the move paused at stopping-source");

        // Paused. A bounded "nothing happens" check: for 2 s (about 13 retry passes at the harness's 150 ms RetryInterval)
        // the record stays paused, nothing is committed and nothing reaches the PC.
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTimeOffset.UtcNow < until)
        {
            Assert.Equal(MoveStages.StoppingSource, (await master.Get<IOwnershipStore>().GetForModelAsync("m1", Ct))?.Stage);
            Assert.Equal(("dgx", generation), PlacementOf(await master.Models.GetAsync("m1", Ct)));
            Assert.Null(await master.Client("pc").GetRuntimeAsync("m1", Ct));
            await Task.Delay(200, Ct);
        }

        Assert.Equal(0, await CountAsync(master, "pc", OperationKinds.Launch, "m1"));
        Assert.Empty(pc.CreateBodiesOf("m1"));
        var view = await master.GetJsonAsync("/api/models/m1/move");
        Assert.True(view.GetProperty("canForceCommit").GetBoolean());
        Assert.False(view.GetProperty("sourceServing").GetBoolean());
        Assert.True(view.GetProperty("targetServing").GetBoolean());
        string consequence = view.GetProperty("forceCommitConsequence").GetString()!;
        Assert.Equal(MoveService.ForceCommitConsequenceFor("dgx"), consequence);
        Assert.Contains("no traffic is sent to it", consequence, StringComparison.Ordinal);

        await master.ExpectAsync("/api/models/m1/move/force-commit", new { password = Password, confirm = "pc" }, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired);
        string forced = await master.ExpectAsync("/api/models/m1/move/force-commit", new { password = Password, confirm = "dgx" }, HttpStatusCode.OK);
        using (var json = JsonDocument.Parse(forced))
        {
            Assert.Equal(MoveStages.SourceStopped, json.RootElement.GetProperty("stage").GetString());
            Assert.True(json.RootElement.GetProperty("move").GetProperty("sourceCleanupPending").GetBoolean());
        }

        var end = await WaitForMoveEndAsync(master, "m1");
        Assert.Equal(MoveStages.Done, end.GetProperty("record").GetProperty("stage").GetString());
        Assert.Equal(("pc", generation + 1), PlacementOf(await master.Models.GetAsync("m1", Ct)));
        await ExitEnv.UntilAsync(async () => (await master.Client("pc").GetRuntimeAsync("m1", Ct))?.State == ModelState.Ready, "m1 Ready on pc");
        await ExitEnv.UntilAsync(() => master.Cache.ResolveServedName("m1") is { NodeId: "pc", ModelState: ModelState.Ready }, "/v1 routing m1 to pc");
        double pcServed = await ServedAsync(pc);
        await ChatAsync(master, "m1");
        Assert.Equal(pcServed + 1, await ServedAsync(pc));

        var audit = await fleet.AuditAsync(new AuditQuery { ModelId = "m1", Max = 500 });
        var force = Assert.Single(audit, l => l.Action == AuditActions.ModelMoveForceCommit && l.Result == AuditResults.Succeeded);
        Assert.Contains("no traffic is sent to it", force.Detail, StringComparison.Ordinal);
        Assert.Single(audit, l => l.Action == AuditActions.ModelMoveForceCommit && l.Result == AuditResults.Refused);
        Assert.Contains(audit, l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Succeeded && l.Detail!.Contains("force-committed", StringComparison.Ordinal));

        // The old instance's container kept running on the DGX while its agent was down: no stop ever reached it.
        Assert.DoesNotContain(fleet.Dgx.Docker.Calls, c => c.StartsWith("POST /containers/", StringComparison.Ordinal) && c.Contains("/stop", StringComparison.Ordinal));
        await fleet.Dgx.StartAsync();
        await master.WaitForNodeAsync("dgx", n => n.State == NodeState.Online, "dgx back Online");
        await ExitEnv.UntilAsync(async () => await master.Client("dgx").GetRuntimeAsync("m1", Ct) is null, "the stale instance on dgx stopped");
        await ExitEnv.UntilAsync(async () => (await fleet.AuditAsync(new AuditQuery { ModelId = "m1", Max = 500 })).Any(l =>
            l.Action == AuditActions.ModelStop && l.NodeId == "dgx" && l.Actor == AuditActors.SystemMove && l.Detail?.Contains("stale", StringComparison.Ordinal) == true),
            "the stale-instance audit line");
        Assert.Equal(1, await CountAsync(master, "dgx", OperationKinds.Stop, "m1"));
        Assert.Equal(ModelState.Ready, (await master.Client("pc").GetRuntimeAsync("m1", Ct))?.State);
        Assert.Equal("pc", master.Cache.ResolveServedName("m1")?.NodeId);
    }

    /// <summary>
    /// D92/D102: on the single DGX (also with a PC enrolled but not yet approved) the stale-instance sweep stops nothing,
    /// not even an instance of an unplaced model; once the PC is approved (two fleet members) the same instance is stale
    /// and the sweep stops it. The contrast is what makes the first half able to fail.
    /// </summary>
    [Fact]
    public async Task TheStaleInstanceSweepStopsNothingOnASingleDgxAndStartsOnceASecondNodeIsApproved()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Movable("loose", Folder) with { Placement = null }] });
        SeedCheckpoint(fleet.Dgx, Folder);
        var master = fleet.Master;
        Assert.Null((await master.Models.GetAsync("loose", Ct))!.Placement);
        // An instance of the unplaced model on the DGX, launched through the agent as an operator's tool would.
        await master.Client("dgx").LaunchAsync("loose", AgentContractSuite.LaunchFor("loose", force: true), Ct);
        await ExitEnv.UntilAsync(() => master.Cache.GetNode("dgx")?.Runtimes.Any(r => r.ModelId == "loose" && r.State == ModelState.Ready) == true,
            "loose Ready in the master's node cache");
        var moves = master.Get<MoveService>();

        Assert.Equal(0, await moves.SweepStaleInstancesAsync(Ct));
        await fleet.JoinAsync("pc", PcGpus, approve: false);
        Assert.Equal(0, await moves.SweepStaleInstancesAsync(Ct)); // a pending node is no fleet member (D102)
        // A bounded "nothing happens" check of the background loop: 1.5 s is six passes at the harness's 250 ms SweepInterval.
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        Assert.Equal(ModelState.Ready, (await master.Client("dgx").GetRuntimeAsync("loose", Ct))?.State);
        Assert.Equal(0, await CountAsync(master, "dgx", OperationKinds.Stop, "loose"));

        await fleet.ApproveAsync("pc");
        await master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online");
        await ExitEnv.UntilAsync(async () => await master.Client("dgx").GetRuntimeAsync("loose", Ct) is null, "the unplaced model's instance stopped by the sweep");
        Assert.Equal(1, await CountAsync(master, "dgx", OperationKinds.Stop, "loose"));
    }

    // ================================================================ 4. unplace

    /// <summary>
    /// Unplace (design §5.6, D94): refused with 409 <c>model_busy</c> naming the holder while a job record holds the model
    /// (nothing reaches the DGX), refused without the password while it runs; then it stops the model on the DGX (one
    /// stop), clears the placement and audits <c>model.unplace</c>. The generation lives on the placement, so the next
    /// placement (a move of the unplaced model to the PC) starts again at 1, below the old generation 5.
    /// </summary>
    [Fact]
    public async Task UnplaceStopsTheModelAndClearsThePlacementIsRefusedWhileHeldAndTheGenerationRestarts()
    {
        var model = Movable("m1", Folder) with { Placement = new ModelPlacement("dgx", Generation: 5) };
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [model] });
        SeedCheckpoint(fleet.Dgx, Folder);
        await JoinPcAsync(fleet);
        var master = fleet.Master;
        await master.StartModelAsync("m1");
        Assert.Equal(("dgx", 5L), PlacementOf(await master.Models.GetAsync("m1", Ct)));

        // Held by a job: refused, naming it, and nothing reaches the node.
        await ExitEnv.UntilAsync(() => master.Get<ModelJobService>().HasRecovered("dgx"), "boot job recovery of dgx"); // it would release the record (D104)
        var ownership = master.Get<IOwnershipStore>();
        var job = new OwnershipRecord { Id = "job-unplace-test", Kind = OwnershipKind.Job, ModelId = "m1", Stage = "running", StartedAt = DateTimeOffset.UtcNow, Description = "a benchmark job" };
        Assert.True((await ownership.TryAcquireAsync(job, Ct)).Acquired);
        int dgxOperations = await CountAsync(master, "dgx");
        string busy = await master.ExpectAsync("/api/models/m1/unplace", new { password = Password }, HttpStatusCode.Conflict, ErrorCodes.ModelBusy);
        AssertHolder(busy, "job-unplace-test", "Job");
        Assert.Equal(dgxOperations, await CountAsync(master, "dgx"));
        Assert.Equal(ModelState.Ready, (await master.Client("dgx").GetRuntimeAsync("m1", Ct))?.State);
        Assert.Equal(("dgx", 5L), PlacementOf(await master.Models.GetAsync("m1", Ct)));
        Assert.True(await ownership.ReleaseAsync(job.Id, Ct));

        // The signed-in session authorizes stopping the running model, without a password in the request.
        string unplaced = await master.ExpectAsync("/api/models/m1/unplace", null, HttpStatusCode.OK);
        using (var json = JsonDocument.Parse(unplaced))
        {
            Assert.True(!json.RootElement.TryGetProperty("placement", out var p) || p.ValueKind == JsonValueKind.Null, unplaced);
        }

        Assert.Null((await master.Models.GetAsync("m1", Ct))!.Placement);
        Assert.Null(await master.Client("dgx").GetRuntimeAsync("m1", Ct));
        Assert.Equal(1, await CountAsync(master, "dgx", OperationKinds.Stop, "m1"));
        Assert.Null(await ownership.GetForModelAsync("m1", Ct));
        var audit = await fleet.AuditAsync(new AuditQuery { ModelId = "m1", Max = 500 });
        Assert.Single(audit, l => l.Action == AuditActions.ModelUnplace && l.Result == AuditResults.Succeeded && l.NodeId == "dgx");
        Assert.Contains(audit, l => l.Action == AuditActions.ModelUnplace && l.Result == AuditResults.Refused && l.ErrorCode == ErrorCodes.ModelBusy);

        // Unplacing an unplaced model is a no-op; placing it again starts the generation at 1 (D94).
        await master.ExpectAsync("/api/models/m1/unplace", null, HttpStatusCode.OK);
        var moved = await MoveAsync(master, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[0] } });
        Assert.Equal("", moved.GetProperty("move").GetProperty("sourceNodeId").GetString());
        await WaitForMoveEndAsync(master, "m1");
        Assert.Equal(("pc", 1L), PlacementOf(await master.Models.GetAsync("m1", Ct)));
        Assert.Null(await master.Client("pc").GetRuntimeAsync("m1", Ct)); // it was not running, so it is not started
    }

    // ================================================================ 5. a gated repo and the Hugging Face token (D93)

    /// <summary>
    /// D93 with a gated repo (the PC's Hub refuses it without the token, with the inspector's real text): a move without a
    /// token fails before anything stopped; a move with <c>hfToken</c> passes it to the PC's download; a move whose master
    /// restarts before the download was sent resumes without the token and fails with the Hub's hint; started again with
    /// the token it completes. The token is never in the move answers or view, any file of the master's data directory
    /// (models.json, ownership.json, the audit log), the master's or the PC agent's log, or the PC's state directory.
    /// <para>The master restart at an exact point uses <see cref="MoveService.CheckpointForTests"/> (the persisted
    /// <c>downloading</c> stage, before the download is sent): a real master cannot be stopped between two statements.</para>
    /// </summary>
    [Fact]
    public async Task AGatedRepoMovesWithTheHfTokenWhichIsStoredNowhereAndIsGoneAfterAMasterRestart()
    {
        const string token = "hf_p2ExitMoveTokenSecret0123456789";
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Movable("m1", Folder)] });
        SeedCheckpoint(fleet.Dgx, Folder);
        var hub = new RecordingHub { RequiredToken = token };
        var pc = await JoinPcAsync(fleet, hub);
        var firstMaster = fleet.Master;

        // Without the token: refused by the Hub on the PC, the move fails, nothing changed.
        await MoveAsync(firstMaster, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[0] } });
        var failed = await WaitForMoveEndAsync(firstMaster, "m1");
        Assert.Equal(MoveStages.Failed, failed.GetProperty("record").GetProperty("stage").GetString());
        Assert.Contains("Provide a Hugging Face access token", ErrorOf(failed), StringComparison.Ordinal);
        Assert.Equal("dgx", (await firstMaster.Models.GetAsync("m1", Ct))!.Placement!.NodeId);
        // Exact counts would race: a download start whose answer the master did not get in time (a per-attempt timeout
        // under load) is a transient failure, and the move sends another start with a new operation id, which the PC's
        // preflight inspects again (MoveService.EnsureDownloadedAsync / IsTransient). Every inspection lacked the token.
        Assert.Empty(hub.Starts);
        Assert.NotEmpty(hub.Inspections);
        Assert.All(hub.Inspections, Assert.Null);

        // With the token, crashed before the download is sent: the next master has no token.
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstMaster.Get<MoveService>().CheckpointForTests = async (point, _, ct) =>
        {
            if (point == MoveStages.Downloading)
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        var record = await MoveAsync(firstMaster, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[0] }, hfToken = token });
        Assert.DoesNotContain(token, record.GetRawText(), StringComparison.Ordinal);
        await reached.Task.WaitAsync(ExitEnv.Patience, Ct);
        Assert.DoesNotContain(token, (await firstMaster.GetJsonAsync("/api/models/m1/move")).GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, ReadShared(Path.Combine(fleet.DataDir, OwnershipDocument.FileName)), StringComparison.Ordinal);
        Assert.All(hub.Inspections, Assert.Null); // the token never reached the PC before the restart
        int beforeRestart = hub.Inspections.Count;

        await fleet.RestartMasterAsync();
        var master = fleet.Master;
        var resumed = await WaitForMoveEndAsync(master, "m1");
        Assert.Equal((record.GetProperty("id").GetString(), MoveStages.Failed),
            (resumed.GetProperty("record").GetProperty("id").GetString(), resumed.GetProperty("record").GetProperty("stage").GetString()));
        Assert.Contains("Provide a Hugging Face access token", ErrorOf(resumed), StringComparison.Ordinal);
        var resumedInspections = hub.Inspections.Skip(beforeRestart).ToList();
        Assert.NotEmpty(resumedInspections);
        Assert.All(resumedInspections, Assert.Null); // the resumed move asked the Hub without a token
        Assert.Empty(hub.Starts);
        int beforeRetry = hub.Inspections.Count;
        Assert.Equal("dgx", (await master.Models.GetAsync("m1", Ct))!.Placement!.NodeId);

        // Started again with the token: the PC's download gets it and the move completes.
        await MoveAsync(master, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[0] }, hfToken = token });
        var done = await WaitForMoveEndAsync(master, "m1");
        Assert.Equal(MoveStages.Done, done.GetProperty("record").GetProperty("stage").GetString());
        Assert.DoesNotContain(token, done.GetRawText(), StringComparison.Ordinal);
        // At least one start (a transient retry may send another, which the PC refuses as already running), each with the token.
        Assert.NotEmpty(hub.Starts);
        Assert.All(hub.Starts, s => Assert.Equal(token, s.Token));
        var retryInspections = hub.Inspections.Skip(beforeRetry).ToList();
        Assert.NotEmpty(retryInspections);
        Assert.All(retryInspections, t => Assert.Equal(token, t));
        Assert.Equal("pc", (await master.Models.GetAsync("m1", Ct))!.Placement!.NodeId);

        // Stored nowhere.
        foreach (string file in Directory.GetFiles(fleet.DataDir, "*", SearchOption.AllDirectories).Where(IsScanned))
        {
            Assert.DoesNotContain(token, ReadShared(file), StringComparison.Ordinal);
        }

        foreach (string file in Directory.GetFiles(pc.Node.StateDirectory, "*", SearchOption.AllDirectories).Where(IsScanned))
        {
            Assert.DoesNotContain(token, ReadShared(file), StringComparison.Ordinal);
        }

        Assert.DoesNotContain(token, fleet.AuditText(), StringComparison.Ordinal);
        Assert.DoesNotContain(firstMaster.Log.Records.Concat(master.Log.Records).Concat(pc.Log.Records), r => r.Message.Contains(token, StringComparison.Ordinal));
    }

    // ================================================================ 6. after the commit (D94)

    /// <summary>
    /// D94: the PC's real per-GPU admission refuses the start after the commit (a 15.9 GiB budget on a 16 GiB card with
    /// 0.5 GiB used): the move ends <c>done</c> with the reason in <c>record.error</c>, audited failed, the placement stays
    /// committed on the PC, nothing runs anywhere and the model is released (a manual start then answers the refusal).
    /// </summary>
    [Fact]
    public async Task ATargetStartRefusedAfterTheCommitEndsTheMoveDoneWithTheReason()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Movable("m1", Folder, budgetGiB: 15.9)] });
        SeedCheckpoint(fleet.Dgx, Folder);
        var pc = await JoinPcAsync(fleet);
        var master = fleet.Master;
        await master.StartModelAsync("m1");
        long generation = (await master.Models.GetAsync("m1", Ct))!.Placement!.Generation;

        await MoveAsync(master, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[1] }, password = Password });
        var view = await WaitForMoveEndAsync(master, "m1");

        Assert.Equal(MoveStages.Done, view.GetProperty("record").GetProperty("stage").GetString());
        string error = ErrorOf(view)!;
        Assert.Contains("start was refused", error, StringComparison.Ordinal);
        Assert.Equal(("pc", generation + 1), PlacementOf(await master.Models.GetAsync("m1", Ct)));
        Assert.Null(await master.Get<IOwnershipStore>().GetForModelAsync("m1", Ct));
        Assert.Null(await master.Client("pc").GetRuntimeAsync("m1", Ct));
        Assert.Null(await master.Client("dgx").GetRuntimeAsync("m1", Ct));
        Assert.Empty(pc.CreateBodiesOf("m1"));
        var audit = await fleet.AuditAsync(new AuditQuery { ModelId = "m1", Max = 500 });
        Assert.Contains(audit, l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Failed && l.Actor == AuditActors.SystemMove &&
            l.Detail!.Contains("start was refused", StringComparison.Ordinal));
        // Released: the operator's start reaches the PC and gets the admission refusal (not model_busy).
        var (status, body) = await master.PostAsync("/api/models/m1/start");
        Assert.True(status == HttpStatusCode.Conflict && CodeOf(body) != ErrorCodes.ModelBusy, $"{(int)status} {body}");
    }

    /// <summary>
    /// D94: another model starting on the same PC GPU holds the agent's start slot (409 <c>start_conflict</c>) when the
    /// committed move starts the model there; the move retries (record at <c>starting-target</c> with a "tries again" note)
    /// and, once the other start is through, starts it and ends <c>done</c> without an error.
    /// </summary>
    [Fact]
    public async Task AStartConflictOnTheTargetGpuIsRetriedAndTheMoveEndsWithoutAnError()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions
        {
            Models = [Movable("m1", Folder), Model("other", "pc", [PcGpus[1]])],
        });
        SeedCheckpoint(fleet.Dgx, Folder);
        var pc = await JoinPcAsync(fleet);
        var master = fleet.Master;
        await master.StartModelAsync("m1");
        pc.Node.StartsHeld = true;
        await master.StartModelAsync("other", waitReady: false);
        await ExitEnv.UntilAsync(async () => (await master.Client("pc").GetRuntimeAsync("other", Ct))?.State == ModelState.Starting, "other Starting on pc");

        await MoveAsync(master, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[1] }, password = Password });
        await ExitEnv.UntilAsync(async () => await master.Get<IOwnershipStore>().GetForModelAsync("m1", Ct) is { Stage: MoveStages.StartingTarget, Error: { } note } &&
            note.Contains("tries again", StringComparison.Ordinal), "the move retrying its start on pc");
        Assert.Null(await master.Client("pc").GetRuntimeAsync("m1", Ct));

        pc.Node.StartsHeld = false;
        var view = await WaitForMoveEndAsync(master, "m1");
        Assert.Equal(MoveStages.Done, view.GetProperty("record").GetProperty("stage").GetString());
        Assert.Null(ErrorOf(view));
        await ExitEnv.UntilAsync(async () => (await master.Client("pc").GetRuntimeAsync("m1", Ct))?.State == ModelState.Ready, "m1 Ready on pc");
        Assert.Equal(ModelState.Ready, (await master.Client("pc").GetRuntimeAsync("other", Ct))?.State);
        Assert.Single(pc.CreateBodiesOf("m1"));
        Assert.Contains(await fleet.AuditAsync(new AuditQuery { ModelId = "m1", Max = 500 }),
            l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Succeeded && l.Actor == AuditActors.SystemMove);
    }

    /// <summary>
    /// D94, the bound: a start conflict that outlasts <c>MirbadLLM:Moves:StartTargetTimeout</c> (3 s here) ends the move
    /// <c>done</c> with "could not be started there within", the placement committed, the model released.
    /// </summary>
    [Fact]
    public async Task AStartConflictThatOutlastsStartTargetTimeoutEndsTheMoveWithTheReason()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions
        {
            Models = [Movable("m1", Folder), Model("other", "pc", [PcGpus[1]])],
            Settings = new Dictionary<string, string?> { ["MirbadLLM:Moves:StartTargetTimeout"] = "00:00:03" },
        });
        SeedCheckpoint(fleet.Dgx, Folder);
        var pc = await JoinPcAsync(fleet);
        var master = fleet.Master;
        await master.StartModelAsync("m1");
        pc.Node.StartsHeld = true;
        await master.StartModelAsync("other", waitReady: false);
        await ExitEnv.UntilAsync(async () => (await master.Client("pc").GetRuntimeAsync("other", Ct))?.State == ModelState.Starting, "other Starting on pc");

        await MoveAsync(master, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[1] }, password = Password });
        var view = await WaitForMoveEndAsync(master, "m1");

        Assert.Equal(MoveStages.Done, view.GetProperty("record").GetProperty("stage").GetString());
        Assert.Contains("could not be started there within", ErrorOf(view), StringComparison.Ordinal);
        Assert.Equal("pc", (await master.Models.GetAsync("m1", Ct))!.Placement!.NodeId);
        Assert.Null(await master.Get<IOwnershipStore>().GetForModelAsync("m1", Ct));
        Assert.Null(await master.Client("pc").GetRuntimeAsync("m1", Ct));
        Assert.Contains(await fleet.AuditAsync(new AuditQuery { ModelId = "m1", Max = 500 }),
            l => l.Action == AuditActions.ModelMove && l.Result == AuditResults.Failed && l.Actor == AuditActors.SystemMove);
    }

    // ================================================================ 7. held models refuse manual start/stop/restart (MN-130)

    /// <summary>
    /// MN-130/D101: a model a live move holds, and one a job record holds, refuse a manual start, stop and restart with 409
    /// <c>model_busy</c> naming the holder (id, kind, description); none of these reaches the DGX (its operation log and
    /// Docker calls are unchanged, the instances keep running). Released (the move cancelled, the job record released),
    /// the same stop goes through.
    /// </summary>
    [Fact]
    public async Task AHeldModelRefusesManualStartStopAndRestartNamingTheHolderAndNothingReachesTheNode()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Movable("m1", Folder), Model("m2", "dgx")] });
        SeedCheckpoint(fleet.Dgx, Folder);
        var pc = await JoinPcAsync(fleet);
        var master = fleet.Master;
        await master.StartModelAsync("m1");
        await master.StartModelAsync("m2");
        await ExitEnv.UntilAsync(() => master.Get<ModelJobService>().HasRecovered("dgx"), "boot job recovery of dgx"); // it would release the job record (D104)

        // m1: held by a real move (its download held on the PC).
        pc.Node.Downloads.Held = true;
        string moveId = (await MoveAsync(master, "m1", new { targetNodeId = "pc", gpuUuids = new[] { PcGpus[0] }, password = Password })).GetProperty("id").GetString()!;
        await ExitEnv.UntilAsync(async () => (await master.Client("pc").GetDownloadAsync(Folder, Ct))?.State == DownloadState.Downloading, "the download running on pc");
        // m2: held by a job record.
        var ownership = master.Get<IOwnershipStore>();
        var job = new OwnershipRecord { Id = "job-held-test", Kind = OwnershipKind.Job, ModelId = "m2", Stage = "running", StartedAt = DateTimeOffset.UtcNow, Description = "a benchmark job" };
        Assert.True((await ownership.TryAcquireAsync(job, Ct)).Acquired);

        int dgxOperations = await CountAsync(master, "dgx");
        int dgxCalls = fleet.Dgx.Docker.Calls.Count(IsContainerMutation);
        foreach (string action in new[] { "start", "stop", "restart" })
        {
            AssertHolder(await master.ExpectAsync($"/api/models/m1/{action}", null, HttpStatusCode.Conflict, ErrorCodes.ModelBusy), moveId, "Move", "a move to node pc");
            AssertHolder(await master.ExpectAsync($"/api/models/m2/{action}", null, HttpStatusCode.Conflict, ErrorCodes.ModelBusy), "job-held-test", "Job", "a benchmark job");
        }

        Assert.Equal(dgxOperations, await CountAsync(master, "dgx"));
        Assert.Equal(dgxCalls, fleet.Dgx.Docker.Calls.Count(IsContainerMutation));
        Assert.Equal(0, await CountAsync(master, "pc", OperationKinds.Launch, "m1"));
        Assert.Equal(ModelState.Ready, (await master.Client("dgx").GetRuntimeAsync("m1", Ct))?.State);
        Assert.Equal(ModelState.Ready, (await master.Client("dgx").GetRuntimeAsync("m2", Ct))?.State);

        // Released: the same stop reaches the node.
        await master.ExpectAsync("/api/models/m1/move/cancel", null, HttpStatusCode.OK);
        await WaitForMoveEndAsync(master, "m1");
        Assert.True(await ownership.ReleaseAsync(job.Id, Ct));
        await master.ExpectAsync("/api/models/m1/stop", null, HttpStatusCode.NoContent);
        await master.ExpectAsync("/api/models/m2/stop", null, HttpStatusCode.NoContent);
        await ExitEnv.UntilAsync(async () => await master.Client("dgx").GetRuntimeAsync("m1", Ct) is null && await master.Client("dgx").GetRuntimeAsync("m2", Ct) is null,
            "m1 and m2 stopped on dgx");
        Assert.Equal("dgx", (await master.Models.GetAsync("m1", Ct))!.Placement!.NodeId);
    }

    // ================================================================ helpers

    /// <summary>A model on <paramref name="nodeId"/> whose checkpoint folder has a Hugging Face source (so it can move).</summary>
    private static ModelDefinition Movable(string id, string folder, double budgetGiB = 1, string nodeId = "dgx") =>
        Model(id, nodeId, budgetGiB: budgetGiB) with { ModelPath = folder, Sources = [new ModelSource("org/" + id, folder)] };

    /// <summary>The checkpoint folder on a node (as an earlier download would have left it).</summary>
    private static void SeedCheckpoint(P2Agent agent, string folder)
    {
        string path = Path.Combine(agent.Node.ModelsRoot, folder);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "config.json"), """{"architectures":["LlamaForCausalLM"],"model_type":"llama","hidden_size":64,"num_hidden_layers":2}""");
        File.WriteAllBytes(Path.Combine(path, "model.safetensors"), new byte[1024]);
    }

    /// <summary>
    /// The PC joins and is approved; its agent starts with <paramref name="hub"/> (when given) as its Hub downloader and repo
    /// inspector; returns once it is Online and its two GPUs are in the registry (the move's placement checks read them).
    /// </summary>
    private static async Task<P2Agent> JoinPcAsync(P2Fleet fleet, RecordingHub? hub = null)
    {
        var pc = await fleet.JoinAsync("pc", PcGpus, start: false);
        if (hub is not null)
        {
            hub.Inner = pc.Node.Downloads;
            var previous = pc.Node.ExtraServices;
            pc.Node.ExtraServices = services =>
            {
                previous?.Invoke(services);
                services.AddSingleton<IHuggingFaceDownloader>(hub);
                services.AddSingleton<IHuggingFaceRepoInspector>(hub);
            };
        }

        await pc.StartAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online");
        await ExitEnv.UntilAsync(async () => (await fleet.Master.Nodes.GetAsync("pc", Ct))?.LastCapabilities?.Gpus.Count == 2, "pc's GPUs in the registry");
        return pc;
    }

    /// <summary><c>POST /api/models/{id}/move</c>, 202 expected; the record.</summary>
    private static async Task<JsonElement> MoveAsync(P2Master master, string id, object body) =>
        JsonDocument.Parse(await master.ExpectAsync($"/api/models/{id}/move", body, HttpStatusCode.Accepted)).RootElement.Clone();

    /// <summary>Waits until <c>GET /api/models/{id}/move</c> shows a finished move; the view.</summary>
    private static async Task<JsonElement> WaitForMoveEndAsync(P2Master master, string id, TimeSpan? timeout = null)
    {
        JsonElement view = default;
        await ExitEnv.UntilAsync(async () =>
        {
            var (status, body) = await SendAsync(master.Admin.GetAsync($"/api/models/{id}/move", Ct));
            if (status != HttpStatusCode.OK)
            {
                return false;
            }

            view = JsonDocument.Parse(body).RootElement.Clone();
            return !view.GetProperty("active").GetBoolean();
        }, $"the move of {id} to end", timeout ?? TimeSpan.FromSeconds(90));
        return view;
    }

    private static string? ErrorOf(JsonElement view) =>
        view.GetProperty("record").TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;

    private static (string NodeId, long Generation) PlacementOf(ModelDefinition? model) =>
        (model?.Placement?.NodeId ?? "(unplaced)", model?.Placement?.Generation ?? -1);

    /// <summary>Operations in a node's agent log (of a kind and target, or all).</summary>
    private static async Task<int> CountAsync(P2Master master, string nodeId, string? kind = null, string? target = null) =>
        (await master.Client(nodeId).ListOperationsAsync(new OperationsQuery(), Ct)).Count(o => (kind is null || o.Kind == kind) && (target is null || o.Target == target));

    /// <summary>A model_busy ProblemDetails names the holder.</summary>
    private static void AssertHolder(string body, string holderId, string kind, string? description = null)
    {
        using var json = JsonDocument.Parse(body);
        var holder = json.RootElement.GetProperty("holder");
        Assert.Equal((holderId, kind), (holder.GetProperty("id").GetString(), holder.GetProperty("kind").GetString()));
        if (description is not null)
        {
            Assert.Equal(description, holder.GetProperty("description").GetString());
            Assert.Contains(description, json.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
    }

    private static bool IsContainerMutation(string call) =>
        call.StartsWith("POST /containers/", StringComparison.Ordinal) || call.StartsWith("DELETE /containers/", StringComparison.Ordinal);

    /// <summary>One chat completion through the master's <c>/v1</c> (no API keys exist, so no key is needed).</summary>
    private static async Task ChatAsync(P2Master master, string model)
    {
        using var chat = await master.Anonymous.PostAsJsonAsync("/v1/chat/completions",
            new { model, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4, stream = false }, Ct);
        string body = await chat.Content.ReadAsStringAsync(Ct);
        Assert.True(chat.StatusCode == HttpStatusCode.OK, $"/v1 {model}: {(int)chat.StatusCode} {body}");
    }

    /// <summary>Completed chat requests the node's model server answered (its <c>vllm:request_success_total</c>).</summary>
    private static async Task<double> ServedAsync(P2Agent agent)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = agent.Node.Model.BaseUri };
        string metrics = await http.GetStringAsync("metrics", Ct);
        var match = RequestSuccess().Match(metrics);
        Assert.True(match.Success, metrics);
        return double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Files a token scan reads: not lock files, and not an atomic write's <c>*.tmp</c> (its content lands in the file renamed over).</summary>
    private static bool IsScanned(string path) =>
        !path.EndsWith(".lock", StringComparison.Ordinal) && !path.EndsWith(".tmp", StringComparison.Ordinal);

    /// <summary>
    /// A file's text while its writer may be active; "" for a file that went away since it was listed (an atomic write's
    /// <c>*.tmp</c>, which the store renames over the file read after it).
    /// </summary>
    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"^vllm:request_success_total\S* (\S+)", RegexOptions.Multiline)]
    private static partial Regex RequestSuccess();

    /// <summary>
    /// The PC's Hub around its <see cref="ContractDownloads"/>: records every download start (repo, token) and every repo
    /// inspection's token; with <see cref="RequiredToken"/> it is a gated repo, refused by the inspection (the agent's
    /// preflight) with the real inspector's text unless that token is given.
    /// </summary>
    private sealed class RecordingHub : IHuggingFaceDownloader, IHuggingFaceRepoInspector
    {
        private readonly ConcurrentQueue<DownloadRequest> _starts = new();
        private readonly ConcurrentQueue<string?> _inspections = new();

        public ContractDownloads Inner { get; set; } = null!;

        public string? RequiredToken { get; init; }

        public IReadOnlyList<DownloadRequest> Starts => [.. _starts];

        public IReadOnlyList<string?> Inspections => [.. _inspections];

        public Task<RepoInspection> InspectAsync(string repoId, string? revision, string? token, CancellationToken ct = default)
        {
            _inspections.Enqueue(token);
            if (RequiredToken is not null && !string.Equals(token, RequiredToken, StringComparison.Ordinal))
            {
                throw new HuggingFaceApiException($"'{repoId}' looks like a gated or private model. Provide a Hugging Face access token that has been granted access to it.");
            }

            return Inner.InspectAsync(repoId, revision, token, ct);
        }

        public Task<DownloadStatus> StartAsync(DownloadRequest request, CancellationToken ct = default)
        {
            _starts.Enqueue(request);
            return Inner.StartAsync(request, ct);
        }

        public Task<IReadOnlyList<DownloadStatus>> ListAsync(CancellationToken ct = default) => Inner.ListAsync(ct);

        public Task<DownloadStatus?> GetAsync(string folder, CancellationToken ct = default) => Inner.GetAsync(folder, ct);

        public Task<bool> CancelAsync(string folder, CancellationToken ct = default) => Inner.CancelAsync(folder, ct);
    }
}
