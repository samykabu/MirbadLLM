using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Benchmarking.Fakes;
using MirbadLLM.UnitTests.Multinode;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web.Api;
using MirbadLLM.Web.Components.Layout;
using FakeModelRegistry = MirbadLLM.UnitTests.Orchestration.Fakes.FakeModelRegistry;

namespace MirbadLLM.UnitTests.Benchmarking;

/// <summary>
/// MN-111 (design §5.6, §6.5 item 1, D15, D34, D79): one job per node and at most <see cref="JobOptions.MaxConcurrent"/>
/// across nodes; the 409s name the node (<c>job_conflict</c>) or the limit (<c>job_limit_reached</c>); the node-scoped
/// maintenance window, drain and ownership record; boot recovery gated per node (W3 review MN-094); and the job API:
/// <c>/api/jobs</c> (always an array), <c>/api/nodes/{id}/job</c>, and the unchanged bootstrap-node shape of
/// <c>/api/benchmark</c> and <c>/api/tuning</c>. The concurrency and recovery tests run over real FakeAgents (mTLS + HTTP/2,
/// <see cref="AgentJobTargets"/>); the rule and API tests over the in-process <see cref="FakeCluster"/>.
/// </summary>
public sealed class PerNodeJobTests
{
    private const string Dgx = "dgx";
    private const string Office = "pc-office";
    private const string Lab = "pc-lab";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly ModelDefinition Qwen = Model("qwen", Dgx);
    private static readonly ModelDefinition Gemma = Model("gemma", Office);
    private static readonly ModelDefinition Phi = Model("phi", Lab);

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private static ModelDefinition Model(string id, string? nodeId) => new()
    {
        Id = id,
        ServedName = id,
        ModelPath = AgentContractSeed.ModelFolder,
        MemoryBudgetGiB = 1,
        MaxModelLen = 8192,
        MaxNumSeqs = 16,
        Placement = nodeId is null ? null : new ModelPlacement(nodeId),
    };

    // ================================================================ over FakeAgents

    [Fact]
    public async Task TwoNodesRunAJobEachAtOnceThroughTheirOwnAgentAndAThirdNodeIsRefusedWithTheLimit()
    {
        await using var fleet = await Fleet.StartAsync([Qwen, Gemma, Phi], Dgx, Office, Lab);
        await fleet.LaunchReadyAsync(Qwen);
        await fleet.LaunchReadyAsync(Gemma);
        await fleet.LaunchReadyAsync(Phi);
        fleet.Suite.Hold(Qwen.Id);
        fleet.Suite.Hold(Gemma.Id);

        var onDgx = await fleet.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        var onOffice = await fleet.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);
        Assert.Equal(Dgx, onDgx.NodeId);
        Assert.Equal(Office, onOffice.NodeId);

        // Both are measuring at the same time.
        await fleet.Suite.WaitStartedAsync(Qwen.Id);
        await fleet.Suite.WaitStartedAsync(Gemma.Id);
        Assert.Equal([Dgx, Office], fleet.Jobs.Running.Select(j => j.NodeId!).Order(StringComparer.Ordinal));

        // A third job on a third, idle node: 409 job_limit_reached naming the limit (default 2).
        var limit = await Assert.ThrowsAsync<JobConflictException>(() => fleet.Jobs.StartBenchmarkAsync(Phi.Id, Ct));
        Assert.Equal(ErrorCodes.JobLimitReached, limit.Code);
        Assert.Equal(JobOptions.DefaultMaxConcurrent, limit.Limit);
        Assert.Null(limit.NodeId);
        Assert.Contains("(the limit, MirbadLLM:Jobs:MaxConcurrent, is 2)", limit.Message, StringComparison.Ordinal);
        Assert.Contains("a benchmark on qwen on node dgx; a benchmark on gemma on node pc-office", limit.Message, StringComparison.Ordinal);
        Assert.Null(fleet.Jobs.JobOnNode(Lab));

        fleet.Suite.Release(Qwen.Id);
        await UntilAsync(() => fleet.Jobs.JobOnNode(Dgx) is null, "the dgx job to finish");

        // A slot is free again: the third node's job starts while the office job still runs.
        var onLab = await fleet.Jobs.StartBenchmarkAsync(Phi.Id, Ct);
        Assert.Equal(Lab, onLab.NodeId);
        fleet.Suite.Release(Gemma.Id);
        await fleet.Jobs.WhenIdleAsync();

        foreach (var (model, node) in new[] { (Qwen, Dgx), (Gemma, Office), (Phi, Lab) })
        {
            var outcome = fleet.Jobs.LastOutcomeOnNode(node);
            Assert.Equal("Completed", outcome?.Status);
            Assert.Equal(model.Id, outcome?.ModelId);
            var run = Assert.Single(await fleet.Store.ListBenchmarksAsync(model.Id, Ct));
            Assert.Null(run.Error);
            Assert.NotNull(run.Result);

            // Each node's load went through that node's own agent proxy.
            var target = Assert.Single(fleet.Suite.Targets, t => t.ModelId == model.Id);
            Assert.Equal(new Uri(fleet.Agent(node).BaseAddress, $"agent/v1/proxy/{model.Id}/"), target.BaseUri);
        }
    }

    [Fact]
    public async Task BootRecoveryOfOneNodeDoesNotWaitForAnotherNodeThatIsNotReconciled()
    {
        await using var fleet = await Fleet.StartAsync([Qwen, Gemma], Dgx, Office);

        // Candidates left running on both nodes by interrupted tuning runs; the office node is not reconciled yet.
        await fleet.Cluster(Dgx).LaunchAsync(Qwen with { MaxNumSeqs = 32 }, new LaunchOptions(Tuning: true), Ct);
        await fleet.Cluster(Office).LaunchAsync(Gemma with { MaxNumSeqs = 32 }, new LaunchOptions(Tuning: true), Ct);
        fleet.Nodes.Set(Office, n => n with { BootId = null });

        // The gate is per node: dgx is reconciled although the office node is not.
        Assert.Equal([Dgx, Office], fleet.Targets.NodeIds.Order(StringComparer.Ordinal));
        Assert.True(await fleet.Targets.IsNodeReconciledAsync(Dgx, Ct));
        Assert.False(await fleet.Targets.IsNodeReconciledAsync(Office, Ct));
        Assert.False(await fleet.Targets.IsNodeReconciledAsync("unknown", Ct));
        Assert.False(await fleet.Targets.IsReconciledAsync(Ct));

        using var recovery = new ModelJobRecoveryService(fleet.Jobs, fleet.Targets);
        await recovery.StartAsync(Ct);
        try
        {
            // dgx is recovered (saved settings, not a candidate) while the office node still waits.
            await UntilAsync(async () => await fleet.TuningAsync(Dgx, Qwen.Id) == false, "dgx's candidate to be replaced");
            var dgxRelaunch = Assert.Single(fleet.Cluster(Dgx).Launches, l => !l.Options.Tuning);
            Assert.True(dgxRelaunch.Options.Force);
            Assert.Equal(Qwen.MaxNumSeqs, dgxRelaunch.Definition.MaxNumSeqs);

            await Task.Delay(200, Ct);
            Assert.True(await fleet.TuningAsync(Office, Gemma.Id), "The office node was recovered before it was reconciled.");
            Assert.DoesNotContain(fleet.Cluster(Office).Launches, l => !l.Options.Tuning);
            Assert.False(recovery.ExecuteTask!.IsCompleted);

            // The office node reconciles: it is recovered too (the service keeps watching for nodes until shutdown).
            fleet.Nodes.Set(Office, n => n with { BootId = fleet.Agent(Office).BootId });
            await UntilAsync(async () => await fleet.TuningAsync(Office, Gemma.Id) == false, "the office node's candidate to be replaced");
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
        }

        Assert.False(await fleet.TuningAsync(Office, Gemma.Id));
        var officeRelaunch = Assert.Single(fleet.Cluster(Office).Launches, l => !l.Options.Tuning);
        Assert.Equal(Gemma.MaxNumSeqs, officeRelaunch.Definition.MaxNumSeqs);

        // Each node relaunched only its own model.
        Assert.DoesNotContain(fleet.Cluster(Dgx).Launches, l => l.Definition.Id == Gemma.Id);
        Assert.DoesNotContain(fleet.Cluster(Office).Launches, l => l.Definition.Id == Qwen.Id);
    }

    // ================================================================ rules (in-process cluster)

    [Fact]
    public async Task ASecondJobOnABusyNodeIsAConflictNamingTheNodeWhileAnotherNodeStillStarts()
    {
        var gemma2 = Model("gemma-small", Office);
        var h = Harness.Create([Qwen, Gemma, gemma2]);
        h.Suite.Hold(Gemma.Id);
        await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        var ex = await Assert.ThrowsAsync<JobConflictException>(() => h.Jobs.StartTuningAsync(gemma2.Id, TuningGoal.Balanced, null, Ct));
        Assert.Equal(ErrorCodes.JobConflict, ex.Code);
        Assert.Equal(Office, ex.NodeId);
        Assert.Null(ex.Limit);
        Assert.Equal("A benchmark on gemma on node pc-office is in progress, so a tuning run can't start. Wait for it to finish or cancel it first.", ex.Message);

        // The other node is free.
        h.Suite.Hold(Qwen.Id);
        var onDgx = await h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        Assert.Equal(Dgx, onDgx.NodeId);
        Assert.Equal(2, h.Jobs.Running.Count);

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task OnTheBootstrapNodeTheConflictTextIsTodaysAndAModelWithoutPlacementHoldsTheBootstrapNode()
    {
        var unplaced = Model("unplaced", nodeId: null);
        var h = Harness.Create([Qwen, unplaced], bootstrapNodeId: Dgx);
        h.Suite.Hold(unplaced.Id);

        var job = await h.Jobs.StartBenchmarkAsync(unplaced.Id, Ct);
        Assert.Equal(Dgx, job.NodeId);

        var ex = await Assert.ThrowsAsync<JobConflictException>(() => h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct));
        Assert.Equal("A benchmark on unplaced is in progress, so a benchmark can't start. Wait for it to finish or cancel it first.", ex.Message);
        Assert.Equal(Dgx, ex.NodeId);

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task WithALimitOfOneAJobOnAnotherNodeIsRefusedWithTheExactLimitText()
    {
        var h = Harness.Create([Qwen, Gemma], limits: new JobOptions { MaxConcurrent = 1 });
        h.Suite.Hold(Qwen.Id);
        await h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);

        var ex = await Assert.ThrowsAsync<JobConflictException>(() => h.Jobs.StartTuningAsync(Gemma.Id, TuningGoal.Balanced, null, Ct));
        Assert.Equal(ErrorCodes.JobLimitReached, ex.Code);
        Assert.Equal(1, ex.Limit);
        Assert.Equal(
            "1 job already runs (the limit, MirbadLLM:Jobs:MaxConcurrent, is 1): a benchmark on qwen on node dgx. " +
            "A tuning run on gemma on node pc-office can't start until one finishes or is cancelled.",
            ex.Message);

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
        Assert.Equal(Office, (await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct)).NodeId);
        await h.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task AZeroOrNegativeLimitStillAllowsOneJob()
    {
        var h = Harness.Create([Qwen, Gemma], limits: new JobOptions { MaxConcurrent = 0 });
        h.Suite.Hold(Qwen.Id);

        await h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        Assert.Equal(1, h.Jobs.MaxConcurrent);
        Assert.Equal(1, (await Assert.ThrowsAsync<JobConflictException>(() => h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct))).Limit);

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task TheMaintenanceWindowNamesTheJobsNodeAndClosesWithTheJob()
    {
        var h = Harness.Create([Qwen, Gemma]);
        h.Suite.Hold(Gemma.Id);

        var job = await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        var window = Assert.IsType<MaintenanceState>(h.Maintenance.Current);
        Assert.Equal(job.JobId, window.JobId);
        Assert.Equal(Office, window.NodeId);
        Assert.Equal(MaintenanceJobKinds.Benchmark, window.JobKind);
        Assert.Equal(Gemma.Id, window.ModelId);

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
        Assert.Null(h.Maintenance.Current);
    }

    [Fact]
    public async Task CancellingOneNodesJobLeavesTheOtherNodesJobRunning()
    {
        var h = Harness.Create([Qwen, Gemma]);
        h.Suite.Hold(Qwen.Id);
        h.Suite.Hold(Gemma.Id);
        await h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        Assert.False(h.Jobs.CancelOnNode(Lab));
        Assert.False(h.Jobs.CancelOnNode(Office, JobKind.Tuning));
        Assert.True(h.Jobs.CancelOnNode(Office));
        await UntilAsync(() => h.Jobs.JobOnNode(Office) is null, "the office job to stop");

        Assert.Equal("Cancelled", h.Jobs.LastOutcomeOnNode(Office)?.Status);
        Assert.NotNull(h.Jobs.JobOnNode(Dgx));
        Assert.Null(h.Jobs.LastOutcomeOnNode(Dgx));

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
        Assert.Equal("Completed", h.Jobs.LastOutcomeOnNode(Dgx)?.Status);
        Assert.Equal(Qwen.Id, h.Jobs.LastOutcome?.ModelId);
    }

    [Fact]
    public async Task CancelWithoutANodeCancelsEveryRunningJob()
    {
        var h = Harness.Create([Qwen, Gemma]);
        h.Suite.Hold(Qwen.Id);
        h.Suite.Hold(Gemma.Id);
        await h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        Assert.True(h.Jobs.Cancel());
        await h.Jobs.WhenIdleAsync();

        Assert.Equal("Cancelled", h.Jobs.LastOutcomeOnNode(Dgx)?.Status);
        Assert.Equal("Cancelled", h.Jobs.LastOutcomeOnNode(Office)?.Status);
        Assert.Empty(h.Jobs.Running);
        Assert.False(h.Jobs.Cancel());
    }

    [Fact]
    public async Task TheDrainWaitsOnlyForModelsOnTheJobsNode()
    {
        var h = Harness.Create([Qwen, Gemma], drainTimeout: TimeSpan.FromMilliseconds(50));
        var fresh = DateTimeOffset.UtcNow.AddHours(1);
        h.History.ModelSamplesById[Qwen.Id] = [new ModelSample(fresh, Results.Kpis(running: 0))];
        h.History.ModelSamplesById[Gemma.Id] = [new ModelSample(fresh, Results.Kpis(running: 3, waiting: 1))];
        var since = DateTimeOffset.UtcNow;

        Assert.True(await h.Jobs.DrainAsync(since, Dgx, Ct));
        Assert.False(await h.Jobs.DrainAsync(since, Office, Ct));
        Assert.False(await h.Jobs.DrainAsync(since, Ct));

        // A benchmark on dgx is not marked noisy by the office node's traffic (that node keeps serving).
        await h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        await h.Jobs.WhenIdleAsync();
        var run = Assert.Single(await h.Store.ListBenchmarksAsync(Qwen.Id, Ct));
        Assert.DoesNotContain(run.Result!.Noise.Reasons, r => r.Contains("in-flight", StringComparison.Ordinal));

        // The same traffic on the job's own node is noise.
        await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);
        await h.Jobs.WhenIdleAsync();
        var noisy = Assert.Single(await h.Store.ListBenchmarksAsync(Gemma.Id, Ct));
        Assert.Contains(noisy.Result!.Noise.Reasons, r => r.Contains("in-flight", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyingATuningCandidateIsBlockedOnlyByAJobOnTheModelsNode()
    {
        var h = Harness.Create([Qwen, Gemma]);
        var candidate = new TuningCandidateResult(1, "Max num seqs", "max num seqs 32", Qwen with { MaxNumSeqs = 32 }, "hash", CandidateOutcome.Accepted,
            null, null, 1.1, null);
        var baseline = candidate with { Index = 0, Label = "Baseline", Settings = Qwen };
        await h.Store.SaveTuningAsync(new TuningRun("run-1", Qwen.Id, TuningGoal.Balanced, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow,
            TuningRunStatus.Completed, Qwen, [baseline, candidate], 1, 10, null, []), Ct);

        h.Suite.Hold(Gemma.Id);
        await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);
        var merged = await h.Jobs.ApplyTuningCandidateAsync(Qwen.Id, "run-1", 1, Ct);
        Assert.Equal(32, merged.MaxNumSeqs);

        h.Suite.Hold(Qwen.Id);
        await h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        var ex = await Assert.ThrowsAsync<JobConflictException>(() => h.Jobs.ApplyTuningCandidateAsync(Qwen.Id, "run-1", 1, Ct));
        Assert.Equal(Dgx, ex.NodeId);

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
    }

    // ---------------------------------------------------------------- ownership

    [Fact]
    public async Task AJobHoldsAJobOwnershipRecordOnItsModelUntilItEnds()
    {
        var store = new InMemoryOwnershipStore();
        var h = Harness.Create([Qwen, Gemma with { Placement = new ModelPlacement(Office, Generation: 4) }], ownership: store);
        h.Suite.Hold(Gemma.Id);

        var job = await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        var record = Assert.Single(await store.ListAsync(Ct));
        Assert.Equal(OwnershipKind.Job, record.Kind);
        Assert.Equal(Gemma.Id, record.ModelId);
        Assert.Equal(job.JobId, record.ReferenceId);
        Assert.Equal(4, record.PlacementGeneration);
        Assert.Equal(MaintenanceJobKinds.Benchmark, record.Stage);
        Assert.Equal(AuditActors.SystemJob, record.Actor);
        Assert.Equal("a benchmark on node pc-office", record.Description);

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
        Assert.Empty(await store.ListAsync(Ct));
    }

    [Fact]
    public async Task AModelHeldByAnotherOwnerIsRefusedAndTheNodeIsNotLeftReserved()
    {
        var store = new InMemoryOwnershipStore();
        var gemma2 = Model("gemma-small", Office);
        var h = Harness.Create([Qwen, Gemma, gemma2], ownership: store);
        await store.TryAcquireAsync(new OwnershipRecord
        {
            Id = "move-1", Kind = OwnershipKind.Move, ModelId = Gemma.Id, Stage = MoveStages.Downloading, StartedAt = DateTimeOffset.UtcNow,
            Description = "a move to pc-lab",
        }, Ct);

        var ex = await Assert.ThrowsAsync<ModelBusyException>(() => h.Jobs.StartTuningAsync(Gemma.Id, TuningGoal.Balanced, null, Ct));
        Assert.Equal("Model 'gemma' is busy: a move to pc-lab holds it.", ex.Message);
        Assert.Empty(h.Jobs.Running);
        Assert.Null(h.Jobs.JobOnNode(Office));
        Assert.Null(h.Maintenance.Current);

        // The node is free for another model.
        h.Suite.Hold(gemma2.Id);
        Assert.Equal(Office, (await h.Jobs.StartBenchmarkAsync(gemma2.Id, Ct)).NodeId);
        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
        Assert.Equal("move-1", Assert.Single(await store.ListAsync(Ct)).Id);
    }

    [Fact]
    public async Task RecoveryReleasesJobRecordsLeftByThePreviousProcessNodeByNode()
    {
        var store = new InMemoryOwnershipStore();
        var h = Harness.Create([Qwen, Gemma], ownership: store);
        var now = DateTimeOffset.UtcNow;
        await store.TryAcquireAsync(new OwnershipRecord { Id = "job-old-qwen", Kind = OwnershipKind.Job, ModelId = Qwen.Id, Stage = "tuning", StartedAt = now }, Ct);
        await store.TryAcquireAsync(new OwnershipRecord { Id = "job-old-gemma", Kind = OwnershipKind.Job, ModelId = Gemma.Id, Stage = "tuning", StartedAt = now }, Ct);
        await store.TryAcquireAsync(new OwnershipRecord { Id = "move-x", Kind = OwnershipKind.Move, ModelId = "other", Stage = MoveStages.Downloading, StartedAt = now }, Ct);

        await h.Jobs.RecoverNodeAsync(Dgx, Ct);
        Assert.Equal(["job-old-gemma", "move-x"], (await store.ListAsync(Ct)).Select(r => r.Id).Order(StringComparer.Ordinal));

        await h.Jobs.RecoverAsync(Ct);
        Assert.Equal("move-x", Assert.Single(await store.ListAsync(Ct)).Id);
    }

    [Fact]
    public async Task NodeRecoveryRelaunchesOnlyThatNodesCandidates()
    {
        var h = Harness.Create([Qwen, Gemma], tuningRunning: true);

        await h.Jobs.RecoverNodeAsync(Office, Ct);

        var relaunch = Assert.Single(h.Cluster.Launches);
        Assert.Equal(Gemma.Id, relaunch.Definition.Id);
        Assert.True(relaunch.Options.Force);
        Assert.False(relaunch.Options.Tuning);
    }

    [Fact]
    public async Task AGateWithoutANodeListKeepsTheSingleGlobalWait()
    {
        var h = Harness.Create([Qwen, Gemma], tuningRunning: true);
        var gate = new GlobalGate();
        using var recovery = new ModelJobRecoveryService(h.Jobs, gate);
        await recovery.StartAsync(Ct);
        try
        {
            await Task.Delay(100, Ct);
            Assert.Empty(h.Cluster.Launches);

            gate.Open.SetResult();
            await recovery.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
        }

        Assert.Equal([Gemma.Id, Qwen.Id], h.Cluster.Launches.Select(l => l.Definition.Id).Order(StringComparer.Ordinal));
    }

    // ================================================================ job API over HTTP

    [Fact]
    public async Task JobsIsAlwaysAnArrayOfNodeJobSummaries()
    {
        var embed = Model("embed", Office) with { KeepServingDuringJobs = true };
        var gemma = Gemma with { KeepServingDuringJobs = true };
        await using var api = await Api.StartAsync([Qwen, gemma, embed]);

        using var none = await api.Client.GetAsync(new Uri("/api/jobs", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        var empty = JsonDocument.Parse(await none.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(JsonValueKind.Array, empty.ValueKind);
        Assert.Equal(0, empty.GetArrayLength());

        api.Harness.Suite.Hold(Qwen.Id);
        api.Harness.Suite.Hold(gemma.Id);
        var first = await api.Harness.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        await api.Harness.Jobs.StartTuningAsync(gemma.Id, TuningGoal.Balanced, [TuningDimension.MaxNumSeqs], Ct);

        var jobs = await api.Client.GetFromJsonAsync<List<NodeJobSummary>>("/api/jobs", JsonOptions, Ct);
        Assert.NotNull(jobs);
        Assert.Equal(2, jobs.Count);
        var dgx = Assert.Single(jobs, j => j.NodeId == Dgx);
        Assert.Equal(first.JobId, dgx.JobId);
        Assert.Equal(MaintenanceJobKinds.Benchmark, dgx.Kind);
        Assert.Equal(Qwen.Id, dgx.ModelId);
        Assert.Empty(dgx.ExemptModelIds);
        var office = Assert.Single(jobs, j => j.NodeId == Office);
        Assert.Equal(MaintenanceJobKinds.Tuning, office.Kind);
        Assert.Equal(gemma.Id, office.ModelId);
        Assert.NotNull(office.Phase);

        // The office node's other keep-serving model is exempt; the model under test never is.
        Assert.Equal([embed.Id], office.ExemptModelIds);

        // Wire names: camelCase, the kind as the maintenance string.
        var raw = JsonDocument.Parse(await api.Client.GetStringAsync(new Uri("/api/jobs", UriKind.Relative), Ct)).RootElement;
        Assert.Equal("tuning", raw.EnumerateArray().Single(e => e.GetProperty("nodeId").GetString() == Office).GetProperty("kind").GetString());

        api.Harness.Suite.ReleaseAll();
        await api.Harness.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task TheNodeJobEndpointAnswersTheSnapshotAndCancelsOnlyThatNodeWithAnAuditLine()
    {
        await using var api = await Api.StartAsync([Qwen, Gemma]);
        api.Harness.Suite.Hold(Qwen.Id);
        api.Harness.Suite.Hold(Gemma.Id);
        await api.Harness.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        var officeJob = await api.Harness.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        using var idle = await api.Client.GetAsync(new Uri("/api/nodes/pc-lab/job", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NotFound, idle.StatusCode);

        var snapshot = await api.Client.GetFromJsonAsync<JobSnapshot>("/api/nodes/pc-office/job", JsonOptions, Ct);
        Assert.Equal(officeJob.JobId, snapshot!.JobId);
        Assert.Equal(Office, snapshot.NodeId);
        Assert.Equal(JobKind.Benchmark, snapshot.Kind);

        using var cancel = await api.Client.DeleteAsync(new Uri("/api/nodes/pc-office/job", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await UntilAsync(() => api.Harness.Jobs.JobOnNode(Office) is null, "the office job to stop");
        Assert.Equal("Cancelled", api.Harness.Jobs.LastOutcomeOnNode(Office)?.Status);
        Assert.NotNull(api.Harness.Jobs.JobOnNode(Dgx));

        var line = Assert.Single(api.Audit.Records);
        Assert.Equal(AuditActions.Job, line.Action);
        Assert.Equal(Office, line.NodeId);
        Assert.Equal(Gemma.Id, line.ModelId);
        Assert.Equal(officeJob.JobId, line.Target);
        Assert.Equal(AuditResults.Accepted, line.Result);
        Assert.StartsWith("api:", line.Actor, StringComparison.Ordinal);

        using var again = await api.Client.DeleteAsync(new Uri("/api/nodes/pc-office/job", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(AuditResults.Refused, api.Audit.Records[^1].Result);

        api.Harness.Suite.ReleaseAll();
        await api.Harness.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task TheOldEndpointsKeepTheirShapeAndReportOnlyTheBootstrapNode()
    {
        await using var api = await Api.StartAsync([Qwen, Gemma]);
        api.Harness.Suite.Hold(Gemma.Id);
        await api.Harness.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        // A job on another node is not the bootstrap node's job: today's shape, nothing running, nothing to cancel.
        var raw = JsonDocument.Parse(await api.Client.GetStringAsync(new Uri("/api/benchmark", UriKind.Relative), Ct)).RootElement;
        Assert.Equal(["current", "last"], raw.EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, raw.GetProperty("current").ValueKind);
        using var notHere = await api.Client.DeleteAsync(new Uri("/api/benchmark", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NotFound, notHere.StatusCode);
        Assert.NotNull(api.Harness.Jobs.JobOnNode(Office));

        // The bootstrap node's job is reported and cancelled there.
        api.Harness.Suite.Hold(Qwen.Id);
        var dgxJob = await api.Harness.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        var status = await api.Client.GetFromJsonAsync<JobStatusResponse>("/api/benchmark", JsonOptions, Ct);
        Assert.Equal(dgxJob.JobId, status!.Current!.JobId);
        using var cancel = await api.Client.DeleteAsync(new Uri("/api/benchmark", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await UntilAsync(() => api.Harness.Jobs.JobOnNode(Dgx) is null, "the dgx job to stop");
        Assert.NotNull(api.Harness.Jobs.JobOnNode(Office));

        var after = await api.Client.GetFromJsonAsync<JobStatusResponse>("/api/benchmark", JsonOptions, Ct);
        Assert.Null(after!.Current);
        Assert.Equal(Qwen.Id, after.Last!.ModelId);
        Assert.Equal("Cancelled", after.Last.Status);

        api.Harness.Suite.ReleaseAll();
        await api.Harness.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task ConflictsAndTheLimitAre409ProblemsWithTheirCodes()
    {
        var gemma2 = Model("gemma-small", Office);
        var store = new InMemoryOwnershipStore();
        await using var api = await Api.StartAsync([Qwen, Gemma, gemma2, Phi], store);
        api.Harness.Suite.Hold(Qwen.Id);
        api.Harness.Suite.Hold(Gemma.Id);
        await api.Harness.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        await api.Harness.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        using var busyNode = await api.Client.PostAsync(new Uri("/api/models/gemma-small/benchmark", UriKind.Relative), null, Ct);
        var conflict = await ProblemAsync(busyNode);
        Assert.Equal(HttpStatusCode.Conflict, busyNode.StatusCode);
        Assert.Equal(ErrorCodes.JobConflict, conflict.GetProperty("code").GetString());
        Assert.Equal(Office, conflict.GetProperty("nodeId").GetString());
        Assert.Contains("on gemma on node pc-office is in progress", conflict.GetProperty("detail").GetString(), StringComparison.Ordinal);

        using var tooMany = await api.Client.PostAsJsonAsync("/api/models/phi/tune", new TuneRequest(TuningGoal.Balanced), JsonOptions, Ct);
        var limit = await ProblemAsync(tooMany);
        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        Assert.Equal(ErrorCodes.JobLimitReached, limit.GetProperty("code").GetString());
        Assert.Equal(2, limit.GetProperty("limit").GetInt32());

        api.Harness.Suite.ReleaseAll();
        await api.Harness.Jobs.WhenIdleAsync();

        // A model another owner holds: 409 model_busy on the tune route too (not the 400 of an unsupported model).
        await store.TryAcquireAsync(new OwnershipRecord
        {
            Id = "move-1", Kind = OwnershipKind.Move, ModelId = Phi.Id, Stage = MoveStages.Downloading, StartedAt = DateTimeOffset.UtcNow,
        }, Ct);
        using var held = await api.Client.PostAsJsonAsync("/api/models/phi/tune", new TuneRequest(TuningGoal.Balanced), JsonOptions, Ct);
        Assert.Equal(HttpStatusCode.Conflict, held.StatusCode);
        Assert.Equal(ErrorCodes.ModelBusy, (await ProblemAsync(held)).GetProperty("code").GetString());
    }

    // ---------------------------------------------------------------- W7 review (MN-126)

    [Fact]
    public async Task WithNodeScopedMaintenanceOffTheJobsWindowIsGlobalExactlyAsInP1()
    {
        // P1 is live on one DGX: with no new configuration a job must pause the API as before P2 (global window, P1 order,
        // the 503 text without a node), even though the job itself still holds its node.
        var embed = Model("embed", Office) with { KeepServingDuringJobs = true };
        var h = Harness.Create([Qwen, Gemma, embed], nodeScoped: false);
        h.Suite.Hold(Gemma.Id);

        var job = await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        Assert.Equal(Office, job.NodeId);
        var window = Assert.Single(h.Maintenance.Active);
        Assert.True(window.IsGlobal);
        Assert.Null(window.NodeId);
        Assert.Equal(job.JobId, window.JobId);
        Assert.StartsWith("mirbadllm is running a benchmark on gemma; API paused until ~", window.Describe(DateTimeOffset.UtcNow), StringComparison.Ordinal);
        Assert.Same(window, h.Maintenance.ForModel(Qwen.Id));
        Assert.Same(window, h.Maintenance.ForModel(embed.Id));
        var summary = Assert.Single(await h.Jobs.ListJobSummariesAsync(Ct));
        Assert.Equal(Office, summary.NodeId);
        Assert.Empty(summary.ExemptModelIds);

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
        Assert.Empty(h.Maintenance.Active);
    }

    [Fact]
    public async Task ExemptModelsOnTheJobsApiFollowTheRoutersRuleAnUnplacedModelIsNeverExempt()
    {
        // The router pauses by placement (MaintenanceScope); an unplaced keep-serving model is on no node, so the job API must
        // not list it as exempt on the bootstrap node either.
        var unplaced = Model("unplaced", nodeId: null) with { KeepServingDuringJobs = true };
        var embed = Model("embed", Dgx) with { KeepServingDuringJobs = true };
        var h = Harness.Create([Qwen, embed, unplaced]);
        h.Suite.Hold(Qwen.Id);
        await h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);

        var summary = Assert.Single(await h.Jobs.ListJobSummariesAsync(Ct));
        Assert.Equal([embed.Id], summary.ExemptModelIds);
        Assert.Equal(summary.ExemptModelIds, MaintenanceModeService.ExemptModelIds(Assert.Single(h.Maintenance.Active), await h.Registry.ListAsync(Ct)));

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task ARecoveryRacingAJobStartNeverReleasesTheJobsFreshOwnershipRecord()
    {
        var store = new GatedOwnershipStore();
        var h = Harness.Create([Qwen, Gemma], ownership: store);
        h.Suite.Hold(Gemma.Id);

        // The record is in the store, the acquire call has not returned yet: a node recovery runs now.
        var start = h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await h.Jobs.RecoverNodeAsync(Office, Ct);
        store.Proceed.SetResult();
        var job = await start;

        var record = Assert.Single(await store.ListAsync(Ct));
        Assert.Equal(job.JobId, record.ReferenceId);

        // And a recovery while the job runs leaves it too; the job's end releases it.
        await h.Jobs.RecoverNodeAsync(Office, Ct);
        Assert.Single(await store.ListAsync(Ct));
        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
        Assert.Empty(await store.ListAsync(Ct));
        Assert.Empty(h.Maintenance.Active);
    }

    [Fact]
    public async Task TuningGatesInspectTheFolderOnTheJobsNode()
    {
        var calls = new ConcurrentQueue<(string Node, string Folder)>();
        var h = Harness.Create([Qwen, Gemma], inspectOnNode: (node, folder) =>
        {
            calls.Enqueue((node, folder));
            return null;
        });

        await h.Jobs.PreviewTuningAsync(Gemma.Id, dimensions: null, Ct);
        await h.Jobs.PreviewTuningAsync(Gemma.Id, dimensions: null, TuningGoal.InteractiveChat, Ct);

        Assert.Contains((Office, Gemma.ModelPath), calls);
        Assert.All(calls, c => Assert.Equal(Office, c.Node));

        calls.Clear();
        await h.Jobs.PreviewTuningAsync(Qwen.Id, dimensions: null, Ct);
        Assert.Contains((Dgx, Qwen.ModelPath), calls);
        Assert.All(calls, c => Assert.Equal(Dgx, c.Node));
    }

    [Fact]
    public async Task CancelJobStopsThatJobOnlyAndJobAffectingIsTheModelsNodesJob()
    {
        var h = Harness.Create([Qwen, Gemma, Model("gemma-2", Office)]);
        h.Suite.Hold(Qwen.Id);
        h.Suite.Hold(Gemma.Id);
        var qwenJob = await h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        var gemmaJob = await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        Assert.Equal(gemmaJob.JobId, h.Jobs.JobAffecting(Gemma)?.JobId);
        Assert.Equal(gemmaJob.JobId, h.Jobs.JobAffecting(Model("gemma-2", Office))?.JobId);
        Assert.Equal(qwenJob.JobId, h.Jobs.JobAffecting(Qwen)?.JobId);
        Assert.Null(h.Jobs.JobAffecting(Phi));

        Assert.False(h.Jobs.CancelJob("no-such-job"));
        Assert.True(h.Jobs.CancelJob(gemmaJob.JobId));
        await UntilAsync(() => h.Jobs.JobOnNode(Office) is null, "the office job to stop");
        Assert.Equal(qwenJob.JobId, h.Jobs.JobOnNode(Dgx)?.JobId);

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task TheBannersStopButtonStopsItsOwnWindowsJobNotTheOtherNodes()
    {
        var h = Harness.Create([Qwen, Gemma]);
        h.Suite.Hold(Qwen.Id);
        h.Suite.Hold(Gemma.Id);
        await h.Jobs.StartBenchmarkAsync(Qwen.Id, Ct);
        var gemmaJob = await h.Jobs.StartBenchmarkAsync(Gemma.Id, Ct);

        using var ctx = new BunitContext();
        ctx.Services.AddSingleton<IMaintenanceMode>(h.Maintenance);
        ctx.Services.AddSingleton<IModelRegistry>(h.Registry);
        ctx.Services.AddSingleton(h.Jobs);
        var cut = ctx.Render<MaintenanceBanner>();

        var office = cut.FindAll("[data-testid=maintenance-banner]").Single(b => b.GetAttribute("data-node") == Office);
        office.QuerySelector("[data-testid=maintenance-stop]")!.Click();

        await UntilAsync(() => h.Jobs.JobOnNode(Office) is null, "the office job to stop");
        Assert.NotNull(h.Jobs.JobOnNode(Dgx));
        Assert.Equal("Cancelled", h.Jobs.LastOutcomeOnNode(Office)?.Status);
        Assert.Equal(gemmaJob.JobId, h.Jobs.LastOutcomeOnNode(Office)?.JobId);

        h.Suite.ReleaseAll();
        await h.Jobs.WhenIdleAsync();
    }

    [Fact]
    public async Task BootRecoveryPicksUpANodeThatAppearsAfterBoot()
    {
        await using var fleet = await Fleet.StartAsync([Qwen, Gemma], Dgx, Office);
        await fleet.Cluster(Dgx).LaunchAsync(Qwen with { MaxNumSeqs = 32 }, new LaunchOptions(Tuning: true), Ct);
        await fleet.Cluster(Office).LaunchAsync(Gemma with { MaxNumSeqs = 32 }, new LaunchOptions(Tuning: true), Ct);

        // The office node is not in the status cache yet when recovery starts (the node monitor's first sync races boot).
        var office = fleet.Nodes.Remove(Office);
        Assert.Equal([Dgx], fleet.Targets.NodeIds);

        using var recovery = new ModelJobRecoveryService(fleet.Jobs, fleet.Targets, TimeSpan.FromMilliseconds(20));
        await recovery.StartAsync(Ct);
        try
        {
            await UntilAsync(async () => await fleet.TuningAsync(Dgx, Qwen.Id) == false, "dgx's candidate to be replaced");
            Assert.True(await fleet.TuningAsync(Office, Gemma.Id));

            fleet.Nodes.Add(office);
            await UntilAsync(async () => await fleet.TuningAsync(Office, Gemma.Id) == false, "the late node's candidate to be replaced");
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
        }

        Assert.Single(fleet.Cluster(Dgx).Launches, l => !l.Options.Tuning);
        Assert.Single(fleet.Cluster(Office).Launches, l => !l.Options.Tuning);
    }

    // ================================================================ helpers

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;

    private static Task UntilAsync(Func<bool> condition, string what) => UntilAsync(() => Task.FromResult(condition()), what);

    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>The job service over the in-process <see cref="FakeCluster"/>, every model Ready.</summary>
    private sealed class Harness
    {
        private Harness(FakeModelRegistry registry, FakeCluster cluster, ModelJobService jobs, MaintenanceModeService maintenance, FakeMetricsHistory history,
            HeldSuite suite, BenchmarkStore store)
        {
            Registry = registry;
            Cluster = cluster;
            Jobs = jobs;
            Maintenance = maintenance;
            History = history;
            Suite = suite;
            Store = store;
        }

        public FakeModelRegistry Registry { get; }

        public FakeCluster Cluster { get; }

        public ModelJobService Jobs { get; }

        public MaintenanceModeService Maintenance { get; }

        public FakeMetricsHistory History { get; }

        public HeldSuite Suite { get; }

        public BenchmarkStore Store { get; }

        public static Harness Create(
            IReadOnlyList<ModelDefinition> models, JobOptions? limits = null, string? bootstrapNodeId = null, IOwnershipStore? ownership = null,
            TimeSpan? drainTimeout = null, bool tuningRunning = false, bool nodeScoped = true,
            Func<string, string, ModelFolderInspection?>? inspectOnNode = null)
        {
            var registry = new FakeModelRegistry();
            var cluster = new FakeCluster();
            foreach (var model in models)
            {
                registry.Add(model);
                cluster.Running(model, tuning: tuningRunning);
            }

            var options = Options.Create(new MirbadLLMOptions { VllmImage = "default-image" });
            var maintenance = new MaintenanceModeService();
            var history = new FakeMetricsHistory();
            var suite = new HeldSuite();
            var store = new BenchmarkStore(Options.Create(new MirbadLLMOptions
            {
                DataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-pernode-" + Guid.NewGuid().ToString("N")),
            }));
            var jobs = new ModelJobService(registry, cluster, cluster, suite, store, maintenance, history, new FakeGpu(0), new StartupTimeTracker(), options,
                new ModelJobOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(1),
                    DrainTimeout = drainTimeout ?? TimeSpan.FromMilliseconds(10),
                    GpuIdleSampleDuration = TimeSpan.FromMilliseconds(1),
                    GpuIdleSampleInterval = TimeSpan.FromMilliseconds(1),
                },
                NullLogger<ModelJobService>.Instance, new ContainerNetworkTargets(),
                limits: limits, bootstrapNodeId: bootstrapNodeId, ownership: ownership, nodeScopedMaintenance: nodeScoped,
                inspectOnNode: inspectOnNode);
            return new Harness(registry, cluster, jobs, maintenance, history, suite, store);
        }
    }

    /// <summary><see cref="JobApiEndpoints"/> over a <see cref="Harness"/> on a test server, with a recording audit log.</summary>
    private sealed class Api : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private Api(WebApplication app, Harness harness, RecordingAuditLog audit)
        {
            _app = app;
            Harness = harness;
            Audit = audit;
            Client = app.GetTestClient();
        }

        public Harness Harness { get; }

        public RecordingAuditLog Audit { get; }

        public HttpClient Client { get; }

        public static async Task<Api> StartAsync(IReadOnlyList<ModelDefinition> models, IOwnershipStore? ownership = null)
        {
            var harness = Harness.Create(models, ownership: ownership);
            var audit = new RecordingAuditLog();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.Services.AddSingleton<IModelRegistry>(harness.Registry);
            builder.Services.AddSingleton(harness.Jobs);
            builder.Services.AddSingleton(harness.Store);
            builder.Services.AddSingleton<IAuditLog>(audit);
            var app = builder.Build();
            app.MapJobApi();
            await app.StartAsync(Ct);
            return new Api(app, harness, audit);
        }

        public async ValueTask DisposeAsync()
        {
            Harness.Jobs.Cancel();
            Harness.Suite.ReleaseAll();
            await Harness.Jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
            Client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// A thread-safe suite (several jobs measure at once): holds a model's run until released, records every target,
    /// then scores with fixed numbers or runs the real suite (<paramref name="inner"/>, through the agent).
    /// </summary>
    private sealed class HeldSuite(IBenchmarkSuite? inner = null) : IBenchmarkSuite
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _holds = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<BenchmarkTarget> _targets = new();

        public IReadOnlyList<BenchmarkTarget> Targets => [.. _targets];

        public int StepCount => inner?.StepCount ?? 5;

        public void Hold(string modelId) => _holds[modelId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(string modelId)
        {
            if (_holds.TryRemove(modelId, out var hold))
            {
                hold.TrySetResult();
            }
        }

        public void ReleaseAll()
        {
            foreach (string id in _holds.Keys)
            {
                Release(id);
            }
        }

        public Task WaitStartedAsync(string modelId) => Started(modelId).Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        public async Task<BenchmarkResult> RunAsync(BenchmarkTarget target, IProgress<BenchmarkProgress>? progress, CancellationToken ct = default)
        {
            _targets.Enqueue(target);
            progress?.Report(new BenchmarkProgress("N=1, run 1 of 3", 1, StepCount));
            Started(target.ModelId).TrySetResult();
            if (_holds.TryGetValue(target.ModelId, out var hold))
            {
                await hold.Task.WaitAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            return inner is null ? Results.Make(10, 100) : await inner.RunAsync(target, progress, ct);
        }

        private TaskCompletionSource Started(string modelId) =>
            _started.GetOrAdd(modelId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>The ownership store's contract in memory (MN-108 builds the file store in parallel).</summary>
    private sealed class InMemoryOwnershipStore : IOwnershipStore
    {
        private readonly Lock _gate = new();
        private readonly List<OwnershipRecord> _records = [];

        public Task<IReadOnlyList<OwnershipRecord>> ListAsync(CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<OwnershipRecord>>([.. _records]);
            }
        }

        public Task<OwnershipRecord?> GetForModelAsync(string modelId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_records.FirstOrDefault(r => r.ModelId == modelId));
            }
        }

        public Task<OwnershipAcquireResult> TryAcquireAsync(OwnershipRecord record, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_records.FirstOrDefault(r => r.ModelId == record.ModelId) is { } holder)
                {
                    return Task.FromResult(new OwnershipAcquireResult(false, holder));
                }

                _records.Add(record);
                return Task.FromResult(new OwnershipAcquireResult(true, null));
            }
        }

        public Task<OwnershipRecord?> UpdateAsync(string recordId, Func<OwnershipRecord, OwnershipRecord> update, CancellationToken ct = default)
        {
            lock (_gate)
            {
                int index = _records.FindIndex(r => r.Id == recordId);
                if (index < 0)
                {
                    return Task.FromResult<OwnershipRecord?>(null);
                }

                _records[index] = update(_records[index]);
                return Task.FromResult<OwnershipRecord?>(_records[index]);
            }
        }

        public Task<bool> ReleaseAsync(string recordId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_records.RemoveAll(r => r.Id == recordId) > 0);
            }
        }
    }

    /// <summary>An ownership store whose acquire stores the record, then waits for <see cref="Proceed"/> before returning.</summary>
    private sealed class GatedOwnershipStore : IOwnershipStore
    {
        private readonly InMemoryOwnershipStore _inner = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<OwnershipRecord>> ListAsync(CancellationToken ct = default) => _inner.ListAsync(ct);

        public Task<OwnershipRecord?> GetForModelAsync(string modelId, CancellationToken ct = default) => _inner.GetForModelAsync(modelId, ct);

        public async Task<OwnershipAcquireResult> TryAcquireAsync(OwnershipRecord record, CancellationToken ct = default)
        {
            var result = await _inner.TryAcquireAsync(record, ct);
            Entered.TrySetResult();
            await Proceed.Task.WaitAsync(ct);
            return result;
        }

        public Task<OwnershipRecord?> UpdateAsync(string recordId, Func<OwnershipRecord, OwnershipRecord> update, CancellationToken ct = default) =>
            _inner.UpdateAsync(recordId, update, ct);

        public Task<bool> ReleaseAsync(string recordId, CancellationToken ct = default) => _inner.ReleaseAsync(recordId, ct);
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        private readonly ConcurrentQueue<AuditRecord> _records = new();

        public IReadOnlyList<AuditRecord> Records => [.. _records];

        public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            _records.Enqueue(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([.. _records.Reverse()]);
    }

    /// <summary>A gate with no node list: the single global wait (P1 behaviour).</summary>
    private sealed class GlobalGate : IJobRecoveryGate
    {
        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilReconciledAsync(CancellationToken ct = default) => Open.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Several FakeAgents (one CA, real mTLS client factory) with <see cref="AgentJobTargets"/> as target, GPU idle check
    /// and recovery gate; one <see cref="RemoteJobTests.AgentCluster"/> per node stands in for <c>RemoteModelRuntime</c>,
    /// routed by each model's placement.
    /// </summary>
    private sealed class Fleet : IAsyncDisposable
    {
        private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "mirbadllm-fleet-jobs-" + Guid.NewGuid().ToString("N"));
        private readonly CertificateAuthority _ca;
        private readonly IssuedCredential _master;
        private readonly TrustAnchorSet _trust;
        private readonly JsonNodeRegistry _nodeRegistry;
        private readonly Dictionary<string, FakeAgent> _agents;
        private readonly Dictionary<string, RemoteJobTests.AgentCluster> _clusters;
        private readonly HttpClient _direct = new();

        private Fleet(CertificateAuthority ca, Dictionary<string, FakeAgent> agents, IReadOnlyList<ModelDefinition> models)
        {
            _ca = ca;
            _agents = agents;
            Directory.CreateDirectory(_dataPath);
            var options = Options.Create(new MirbadLLMOptions { DataPath = _dataPath, UseContainerNetwork = true });
            _master = ca.IssueMasterClientCertificate();
            _trust = TrustAnchorSet.Create([ca.Certificate]);
            _nodeRegistry = new JsonNodeRegistry(options, TimeProvider.System);
            Factory = new AgentClientFactory(_nodeRegistry, new TestMasterIdentity(), _master, _trust, TimeProvider.System);
            References = new JsonAgentOperationReferenceStore(options, TimeProvider.System);
            Nodes = new FleetCache(agents.Values.Select(a => new NodeLiveStatus
            {
                NodeId = a.NodeId, NodeUid = a.NodeUid, State = NodeState.Online, BootId = a.BootId, Address = a.BaseAddress.AbsoluteUri,
            }));
            Registry = new FakeModelRegistry();
            foreach (var model in models)
            {
                Registry.Add(model);
            }

            Targets = new AgentJobTargets(Factory, Nodes, Registry, References, TimeProvider.System,
                new AgentJobTargetsOptions { ReconcilePollInterval = TimeSpan.FromMilliseconds(20) });
            _clusters = agents.Keys.ToDictionary(id => id, id => new RemoteJobTests.AgentCluster(() => Factory.GetClient(id)), StringComparer.Ordinal);
            var cluster = new RoutedCluster(_clusters, Registry);
            Store = new BenchmarkStore(options);
            Suite = new HeldSuite(new BenchmarkSuite(_direct, history: null,
                new BenchmarkSuiteOptions { Levels = [1, 2], Runs = 1, MaxTokens = 4, IncludePrefill = false, RequestTimeout = TimeSpan.FromSeconds(30) },
                targets: Targets));
            Jobs = new ModelJobService(
                Registry, cluster, cluster, Suite, Store, new MaintenanceModeService(options), new FakeMetricsHistory(), new FakeGpu(77),
                new StartupTimeTracker(), options,
                new ModelJobOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(10),
                    DrainTimeout = TimeSpan.FromMilliseconds(30),
                    GpuIdleSampleDuration = TimeSpan.FromSeconds(1),
                    CandidateStartTimeout = TimeSpan.FromSeconds(30),
                    CleanupTimeout = TimeSpan.FromSeconds(30),
                },
                NullLogger<ModelJobService>.Instance,
                targets: Targets,
                gpuIdle: Targets,
                nodeScopedMaintenance: true);
        }

        public AgentClientFactory Factory { get; }

        public JsonAgentOperationReferenceStore References { get; }

        public FleetCache Nodes { get; }

        public FakeModelRegistry Registry { get; }

        public AgentJobTargets Targets { get; }

        public BenchmarkStore Store { get; }

        public HeldSuite Suite { get; }

        public ModelJobService Jobs { get; }

        public FakeAgent Agent(string nodeId) => _agents[nodeId];

        public RemoteJobTests.AgentCluster Cluster(string nodeId) => _clusters[nodeId];

        public static async Task<Fleet> StartAsync(IReadOnlyList<ModelDefinition> models, params string[] nodeIds)
        {
            var ca = CertificateAuthority.Create();
            var agents = new Dictionary<string, FakeAgent>(StringComparer.Ordinal);
            try
            {
                foreach (string nodeId in nodeIds)
                {
                    var agent = await FakeAgent.StartAsync(new FakeAgentOptions { NodeId = nodeId, Ca = ca, StartDuration = TimeSpan.FromMilliseconds(20) }, Ct);
                    agents[nodeId] = agent;
                    var server = await agent.GetModelServerAsync();
                    server.SetTokenRateMs(1);
                    server.SetTimeToFirstTokenMs(1);
                }
            }
            catch
            {
                foreach (var agent in agents.Values)
                {
                    await agent.DisposeAsync();
                }

                ca.Dispose();
                throw;
            }

            var fleet = new Fleet(ca, agents, models);
            foreach (var agent in agents.Values)
            {
                await fleet._nodeRegistry.UpsertAsync(new NodeRecord
                {
                    Id = agent.NodeId, Uid = agent.NodeUid, Address = agent.BaseAddress.AbsoluteUri, Approved = true, CertSpkiSha256 = agent.CurrentSpki,
                }, Ct);
            }

            await fleet.Factory.RefreshAsync(Ct);
            return fleet;
        }

        /// <summary>Launches the saved settings on the model's node and waits for Ready.</summary>
        public async Task LaunchReadyAsync(ModelDefinition model)
        {
            string nodeId = model.Placement!.NodeId;
            await Cluster(nodeId).LaunchAsync(model, new LaunchOptions(), Ct);
            await UntilAsync(async () => await Factory.GetClient(nodeId).GetRuntimeAsync(model.Id, Ct) is { State: ModelState.Ready }, model.Id + " Ready");
        }

        /// <summary>True when the model's instance on the node carries the tuning label.</summary>
        public async Task<bool> TuningAsync(string nodeId, string modelId) =>
            await Factory.GetClient(nodeId).GetRuntimeAsync(modelId, Ct) is { } runtime && RemoteJobTests.AgentCluster.IsTuning(runtime);

        public async ValueTask DisposeAsync()
        {
            // A failed assertion must not leave a held job behind (disposal would wait for it forever).
            Jobs.Cancel();
            Suite.ReleaseAll();
            await Jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
            Targets.Dispose();
            await Factory.DisposeAsync();
            foreach (var agent in _agents.Values)
            {
                await agent.DisposeAsync();
            }

            References.Dispose();
            _nodeRegistry.Dispose();
            _direct.Dispose();
            _master.Dispose();
            _trust.Dispose();
            _ca.Dispose();
            try
            {
                Directory.Delete(_dataPath, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    /// <summary>Routes each model to its node's cluster by placement (what <c>RemoteModelRuntime</c> does by the cache and placement).</summary>
    private sealed class RoutedCluster(IReadOnlyDictionary<string, RemoteJobTests.AgentCluster> byNode, FakeModelRegistry registry) : IModelRuntime, IModelLauncher
    {
        public async Task<IReadOnlyList<ModelStatus>> ListStatusAsync(CancellationToken ct = default)
        {
            var all = new List<ModelStatus>();
            foreach (var cluster in byNode.Values)
            {
                all.AddRange(await cluster.ListStatusAsync(ct));
            }

            return all;
        }

        public async Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default) => await (await OfAsync(id, ct)).GetStatusAsync(id, ct);

        public Task<ModelStatus> StartAsync(string id, bool force = false, CancellationToken ct = default) => throw new NotSupportedException();

        public async Task StopAsync(string id, CancellationToken ct = default) => await (await OfAsync(id, ct)).StopAsync(id, ct);

        public async IAsyncEnumerable<string> StreamLogsAsync(string id, int tail = 200, bool follow = true, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (string line in (await OfAsync(id, ct)).StreamLogsAsync(id, tail, follow, ct))
            {
                yield return line;
            }
        }

        public async Task<ModelStatus> LaunchAsync(ModelDefinition definition, LaunchOptions options, CancellationToken ct = default) =>
            await (await OfAsync(definition.Id, ct)).LaunchAsync(definition, options, ct);

        public async Task<ModelDefinition?> GetLaunchedDefinitionAsync(string id, CancellationToken ct = default) =>
            await (await OfAsync(id, ct)).GetLaunchedDefinitionAsync(id, ct);

        public async Task<string?> GetLaunchedConfigHashAsync(string id, CancellationToken ct = default) =>
            await (await OfAsync(id, ct)).GetLaunchedConfigHashAsync(id, ct);

        public Task<string> ComputeConfigHashAsync(ModelDefinition definition, CancellationToken ct = default) =>
            Task.FromResult(RemoteJobTests.AgentCluster.Hash(definition));

        public async Task<IReadOnlyList<string>> ListTuningContainersAsync(CancellationToken ct = default)
        {
            var all = new List<string>();
            foreach (var cluster in byNode.Values)
            {
                all.AddRange(await cluster.ListTuningContainersAsync(ct));
            }

            return all;
        }

        private async Task<RemoteJobTests.AgentCluster> OfAsync(string modelId, CancellationToken ct) =>
            (await registry.GetAsync(modelId, ct))?.Placement?.NodeId is { } nodeId && byNode.TryGetValue(nodeId, out var cluster)
                ? cluster
                : throw new InvalidOperationException($"{modelId} is not placed on a fleet node.");
    }

    /// <summary>The fleet's node views, set by the test (MN-027's cache keeps them current from events in production).</summary>
    private sealed class FleetCache : INodeStatusCache
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, NodeLiveStatus> _nodes;
        private long _version;

        public FleetCache(IEnumerable<NodeLiveStatus> nodes) =>
            _nodes = nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);

        public long Version => Interlocked.Read(ref _version);

        /// <summary>Takes a node out of the cache (as before the node monitor's first sync); returns its status.</summary>
        public NodeLiveStatus Remove(string nodeId)
        {
            lock (_gate)
            {
                _nodes.Remove(nodeId, out var status);
                Interlocked.Increment(ref _version);
                return status!;
            }
        }

        /// <summary>Puts a node (back) into the cache.</summary>
        public void Add(NodeLiveStatus status)
        {
            lock (_gate)
            {
                _nodes[status.NodeId] = status;
            }

            Interlocked.Increment(ref _version);
        }

        public void Set(string nodeId, Func<NodeLiveStatus, NodeLiveStatus> change)
        {
            lock (_gate)
            {
                _nodes[nodeId] = change(_nodes[nodeId]);
            }

            Interlocked.Increment(ref _version);
        }

        public IReadOnlyList<NodeLiveStatus> ListNodes()
        {
            lock (_gate)
            {
                return [.. _nodes.Values.OrderBy(n => n.NodeId, StringComparer.Ordinal)];
            }
        }

        public NodeLiveStatus? GetNode(string nodeId)
        {
            lock (_gate)
            {
                return _nodes.GetValueOrDefault(nodeId);
            }
        }

        // Instances are resolved from the placement (AgentJobTargets falls back to it).
        public NodeModelRuntime? GetRuntime(string modelId) => null;

        public IReadOnlyList<NodeModelRuntime> ListRuntimes(string? nodeId = null) => [];

        public ServedModelRoute? ResolveServedName(string servedName) => null;

        public Task RefreshModelAsync(string modelId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RefreshNodeAsync(string nodeId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
