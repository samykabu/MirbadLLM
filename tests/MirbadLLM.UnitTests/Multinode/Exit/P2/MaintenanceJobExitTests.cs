using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, jobs and maintenance end to end (design §6.5 items 1–2 and 7, §13.6; MN-110/MN-111, D83, D91, D101–D103): real
/// benchmarks through the composed <see cref="ModelJobService"/> on the real master with real agents (a model's slow first
/// token keeps its job running until it is cancelled), and what clients, the job API and the model editor see.
/// <list type="bullet">
/// <item>Two approved nodes: a benchmark on the PC opens a window naming the PC; the PC's models answer 503 maintenance
/// naming the node (after the API key, P2 order) while the DGX's keep serving; the window closes with the job.</item>
/// <item>The single DGX (switch unset, automatic): a global window and P1's <c>/v1</c> (503 before 401, no node).</item>
/// <item>A DGX plus a pending node (D102/D103): nothing changes until the node is approved; a job already running at
/// approval keeps its global window, the next one is node-scoped.</item>
/// <item>Per-node jobs run concurrently within <c>MirbadLLM:Jobs:MaxConcurrent</c> (409 <c>job_conflict</c> naming the
/// node, 409 <c>job_limit_reached</c> naming the limit); <c>/api/benchmark</c> keeps its shape.</item>
/// <item>Boot job recovery runs per node: an Offline node does not hold back another node's tuning clean-up (MN-094).</item>
/// </list>
/// </summary>
public sealed class MaintenanceJobExitTests
{
    [Fact]
    public async Task ABenchmarkOnThePcPausesOnlyThePcsModelsNamingTheNodeWhileTheDgxKeepsServing()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx")] });
        await fleet.JoinAsync("pc", PcGpus);
        var master = fleet.Master;
        await master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[0]]));
        await master.CreateModelAsync(ModelBody("pc-other", "pc", [PcGpus[1]]));
        foreach (string id in new[] { "qwen", "pcm", "pc-other" })
        {
            await master.StartModelAsync(id);
        }

        string key = await InferenceKeyAsync(master);
        var maintenance = master.Get<MaintenanceModeService>();
        var jobs = master.Get<ModelJobService>();
        fleet["pc"].Node.Model.SetTimeToFirstTokenMs(600_000);
        try
        {
            await master.ExpectAsync("/api/models/pcm/benchmark", null, HttpStatusCode.Accepted);

            // The window names the PC (automatic mode, two fleet members).
            var window = Assert.Single(maintenance.Active);
            Assert.Equal("pc", window.NodeId);
            Assert.Equal("pcm", window.ModelId);
            var listed = Assert.Single((await master.GetJsonAsync("/api/jobs")).EnumerateArray());
            Assert.Equal("pc", listed.GetProperty("nodeId").GetString());
            Assert.Equal("pcm", listed.GetProperty("modelId").GetString());
            Assert.Equal(window.JobId, listed.GetProperty("jobId").GetString());

            // The model under test and the PC's other model: 503 maintenance naming the node, with a Retry-After.
            foreach (string id in new[] { "pcm", "pc-other" })
            {
                var paused = await ChatAsync(master, id, key);
                Assert.True(paused.Status == HttpStatusCode.ServiceUnavailable, $"{id}: {(int)paused.Status} {paused.Body}");
                Assert.Equal("maintenance", paused.Code);
                Assert.Contains("running a benchmark on pcm on node pc; API paused until ~", paused.Body, StringComparison.Ordinal);
                Assert.NotNull(paused.RetryAfter);
            }

            // P2 order: the key comes first; the DGX's model keeps serving; /v1/models lists what serves.
            Assert.Equal(HttpStatusCode.Unauthorized, (await ChatAsync(master, "pcm", key: null)).Status);
            var dgx = await ChatAsync(master, "qwen", key);
            Assert.True(dgx.Status == HttpStatusCode.OK, $"qwen during the PC's job: {(int)dgx.Status} {dgx.Body}");
            Assert.Equal(["qwen"], await ListedAsync(master, key));

            // The job is still the one running (the rows above all saw its window).
            Assert.Equal(window.JobId, Assert.Single(maintenance.Active).JobId);
            Assert.Null(jobs.JobOnNode("dgx"));

            // Stopped through the node's job API: the window closes and the PC serves again.
            await ExpectDeleteAsync(master, "/api/nodes/pc/job", HttpStatusCode.Accepted);
            await ExitEnv.UntilAsync(() => maintenance.Active.Count == 0 && jobs.JobOnNode("pc") is null, "the PC's window to close");
        }
        finally
        {
            fleet["pc"].Node.Model.SetTimeToFirstTokenMs(50);
            jobs.Cancel();
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }

        Assert.Empty((await master.GetJsonAsync("/api/jobs")).EnumerateArray());
        foreach (string id in new[] { "pcm", "pc-other", "qwen" })
        {
            var served = await ChatAsync(master, id, key);
            Assert.True(served.Status == HttpStatusCode.OK, $"{id} after the job: {(int)served.Status} {served.Body}");
        }

        Assert.Equal(["pc-other", "pcm", "qwen"], await ListedAsync(master, key));
    }

    [Fact]
    public async Task TheSingleDgxsBenchmarkOpensAGlobalWindowAndKeepsTheP1Order()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx"), Model("other", "dgx")] });
        var master = fleet.Master;
        await master.StartModelAsync("qwen");
        await master.StartModelAsync("other");
        string key = await InferenceKeyAsync(master);

        // The switch is unset: automatic, and with one fleet member that means global windows.
        var policy = master.Get<NodeScopedMaintenancePolicy>();
        Assert.Null(policy.Configured);
        Assert.False(await policy.IsOnAsync(Ct));

        var maintenance = master.Get<MaintenanceModeService>();
        var jobs = master.Get<ModelJobService>();
        fleet.Dgx.Node.Model.SetTimeToFirstTokenMs(600_000);
        try
        {
            await master.ExpectAsync("/api/models/qwen/benchmark", null, HttpStatusCode.Accepted);
            var window = Assert.Single(maintenance.Active);
            Assert.True(window.IsGlobal, $"the single DGX's window names node {window.NodeId}");

            // P1: 503 maintenance before the API key, with today's text (no node) and a Retry-After, also for /v1/models.
            foreach (string? withKey in new[] { null, key })
            {
                foreach (string id in new[] { "qwen", "other" })
                {
                    var paused = await ChatAsync(master, id, withKey);
                    Assert.True(paused.Status == HttpStatusCode.ServiceUnavailable, $"{id} key={withKey is not null}: {(int)paused.Status} {paused.Body}");
                    Assert.Equal("maintenance", paused.Code);
                    Assert.Contains("mirbadllm is running a benchmark on qwen; API paused until ~", paused.Body, StringComparison.Ordinal);
                    Assert.DoesNotContain("node", paused.Body, StringComparison.Ordinal);
                    Assert.NotNull(paused.RetryAfter);
                }

                using var list = await ModelsRequestAsync(master, withKey);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, list.StatusCode);
            }

            // The old endpoint reports the bootstrap node's job in today's shape and stops it.
            var status = await master.GetJsonAsync("/api/benchmark");
            Assert.Equal(["current", "last"], status.EnumerateObject().Select(p => p.Name));
            Assert.Equal(window.JobId, status.GetProperty("current").GetProperty("jobId").GetString());
            Assert.Equal(window.JobId, Assert.Single(maintenance.Active).JobId);
            await ExpectDeleteAsync(master, "/api/benchmark", HttpStatusCode.Accepted);
            await ExitEnv.UntilAsync(() => maintenance.Active.Count == 0 && jobs.JobOnNode("dgx") is null, "the window to close");
        }
        finally
        {
            fleet.Dgx.Node.Model.SetTimeToFirstTokenMs(50);
            jobs.Cancel();
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await ChatAsync(master, "qwen", key: null)).Status);
        var served = await ChatAsync(master, "qwen", key);
        Assert.True(served.Status == HttpStatusCode.OK, $"{(int)served.Status} {served.Body}");
    }

    [Fact]
    public async Task APendingNodeChangesNothingUntilApprovedAndAJobRunningAtApprovalKeepsItsGlobalWindow()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx"), Model("stray", "dgx")] });
        var master = fleet.Master;
        await fleet.JoinAsync("pc", PcGpus, approve: false);
        Assert.False((await master.Nodes.GetAsync("pc", Ct))!.Approved);
        await master.StartModelAsync("qwen");
        await master.StartModelAsync("stray");
        string key = await InferenceKeyAsync(master);
        var policy = master.Get<NodeScopedMaintenancePolicy>();
        var sweep = master.Get<MoveService>();

        // An instance the stale-instance sweep would stop on a two-node fleet: stray's, once stray is unplaced.
        var stray = (await master.Models.GetAsync("stray", Ct))!;
        await master.Models.UpsertAsync(stray with { Placement = null }, Ct);

        // The pending node is no fleet member (D102): automatic scoping stays off, a create without a placement lands on
        // dgx, the model editor has no Placement section, and the sweep stops nothing.
        Assert.False(await policy.IsOnAsync(Ct));
        await master.CreateModelAsync(ModelBody("created-pending", nodeId: null));
        Assert.Equal("dgx", (await master.Models.GetAsync("created-pending", Ct))!.Placement?.NodeId);
        Assert.DoesNotContain("data-testid=\"placement-section\"", await PageAsync(master, "/models/new"), StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"placement-section\"", await PageAsync(master, "/models/qwen"), StringComparison.Ordinal);
        Assert.Equal(0, await sweep.SweepStaleInstancesAsync(Ct));

        var maintenance = master.Get<MaintenanceModeService>();
        var jobs = master.Get<ModelJobService>();
        fleet.Dgx.Node.Model.SetTimeToFirstTokenMs(600_000);
        try
        {
            // A job while the node is pending: a global window, P1 order.
            await master.ExpectAsync("/api/models/qwen/benchmark", null, HttpStatusCode.Accepted);
            var global = Assert.Single(maintenance.Active);
            Assert.True(global.IsGlobal, $"the window names node {global.NodeId} while pc is pending");
            Assert.Equal("maintenance", (await ChatAsync(master, "qwen", key: null)).Code);

            // The node is approved while that job runs (D103): the job keeps its global window and the P1 order.
            await fleet.ApproveAsync("pc");
            await master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online after approval");
            Assert.True(await policy.IsOnAsync(Ct));
            var still = Assert.Single(maintenance.Active);
            Assert.Equal(global.JobId, still.JobId);
            Assert.True(still.IsGlobal, $"the running job's window moved to node {still.NodeId} at approval");
            var paused = await ChatAsync(master, "qwen", key: null);
            Assert.Equal("maintenance", paused.Code);
            Assert.DoesNotContain("node", paused.Body, StringComparison.Ordinal);
            Assert.True(jobs.CancelOnNode("dgx"));
            await ExitEnv.UntilAsync(() => maintenance.Active.Count == 0 && jobs.JobOnNode("dgx") is null, "the first job to end");

            // The next job's window is node-scoped: the key comes first again and the text names the node.
            await master.ExpectAsync("/api/models/qwen/benchmark", null, HttpStatusCode.Accepted);
            var scoped = Assert.Single(maintenance.Active);
            Assert.NotEqual(global.JobId, scoped.JobId);
            Assert.Equal("dgx", scoped.NodeId);
            Assert.Equal(HttpStatusCode.Unauthorized, (await ChatAsync(master, "qwen", key: null)).Status);
            var named = await ChatAsync(master, "qwen", key);
            Assert.Equal("maintenance", named.Code);
            Assert.Contains("running a benchmark on qwen on node dgx; API paused until ~", named.Body, StringComparison.Ordinal);
        }
        finally
        {
            fleet.Dgx.Node.Model.SetTimeToFirstTokenMs(50);
            jobs.Cancel();
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }

        // The approved node is a member: the editor offers placement, a create needs one, and the sweep now stops stray's
        // instance (the positive control of the "stops nothing" above).
        Assert.Contains("data-testid=\"placement-section\"", await PageAsync(master, "/models/new"), StringComparison.Ordinal);
        await master.ExpectAsync("/api/models", ModelBody("created-approved", nodeId: null), HttpStatusCode.BadRequest, ErrorCodes.PlacementRequired);
        await ExitEnv.UntilAsync(() => !fleet.Dgx.Docker.ModelContainers.Any(n => n.Contains("stray", StringComparison.Ordinal)), "the sweep to stop stray's instance");
        Assert.Contains(fleet.Dgx.Docker.ModelContainers, n => n.Contains("qwen", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PerNodeJobsRunConcurrentlyWithinTheLimitAndTheOldEndpointKeepsItsShape()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx")] });
        await fleet.JoinAsync("pc", PcGpus);
        await fleet.JoinAsync("pc2", Pc2Gpus);
        var master = fleet.Master;
        await master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[0]]));
        await master.CreateModelAsync(ModelBody("pcm-second", "pc", [PcGpus[1]]));
        await master.CreateModelAsync(ModelBody("pc2m", "pc2", [Pc2Gpus[0]]));
        foreach (string id in new[] { "qwen", "pcm", "pc2m" })
        {
            await master.StartModelAsync(id);
        }

        var maintenance = master.Get<MaintenanceModeService>();
        var jobs = master.Get<ModelJobService>();
        Assert.Equal(2, jobs.MaxConcurrent); // the default
        foreach (string node in new[] { "dgx", "pc", "pc2" })
        {
            fleet[node].Node.Model.SetTimeToFirstTokenMs(600_000);
        }

        try
        {
            // Two jobs at once, one per node, each with its own node-scoped window.
            string dgxJob = JsonDocument.Parse(await master.ExpectAsync("/api/models/qwen/benchmark", null, HttpStatusCode.Accepted)).RootElement.GetProperty("jobId").GetString()!;
            string pcJob = JsonDocument.Parse(await master.ExpectAsync("/api/models/pcm/benchmark", null, HttpStatusCode.Accepted)).RootElement.GetProperty("jobId").GetString()!;
            Assert.Equal(["dgx", "pc"], maintenance.Active.Select(w => w.NodeId!).Order(StringComparer.Ordinal));
            Assert.Equal(new[] { dgxJob, pcJob }.Order(StringComparer.Ordinal), maintenance.Active.Select(w => w.JobId).Order(StringComparer.Ordinal));

            var listed = (await master.GetJsonAsync("/api/jobs")).EnumerateArray().ToList();
            Assert.Equal([("dgx", "qwen", dgxJob), ("pc", "pcm", pcJob)], listed
                .Select(j => (j.GetProperty("nodeId").GetString()!, j.GetProperty("modelId").GetString()!, j.GetProperty("jobId").GetString()!))
                .OrderBy(j => j.Item1, StringComparer.Ordinal));
            Assert.Equal(dgxJob, (await master.GetJsonAsync("/api/nodes/dgx/job")).GetProperty("jobId").GetString());
            Assert.Equal(pcJob, (await master.GetJsonAsync("/api/nodes/pc/job")).GetProperty("jobId").GetString());
            var (none, noneBody) = await SendAsync(master.Admin.GetAsync("/api/nodes/pc2/job", Ct));
            Assert.True(none == HttpStatusCode.NotFound, $"pc2 has no job: {(int)none} {noneBody}");

            // A second job on a busy node: 409 job_conflict naming the node (checked before the model's state).
            string conflict = await master.ExpectAsync("/api/models/pcm-second/benchmark", null, HttpStatusCode.Conflict, ErrorCodes.JobConflict);
            using (var problem = JsonDocument.Parse(conflict))
            {
                Assert.Equal("pc", problem.RootElement.GetProperty("nodeId").GetString());
                Assert.Contains("A benchmark on pcm on node pc is in progress", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
            }

            // A third node's job above the limit: 409 job_limit_reached naming the limit and the jobs holding it.
            string limit = await master.ExpectAsync("/api/models/pc2m/benchmark", null, HttpStatusCode.Conflict, ErrorCodes.JobLimitReached);
            using (var problem = JsonDocument.Parse(limit))
            {
                Assert.Equal(2, problem.RootElement.GetProperty("limit").GetInt32());
                string detail = problem.RootElement.GetProperty("detail").GetString()!;
                Assert.Contains("(the limit, MirbadLLM:Jobs:MaxConcurrent, is 2)", detail, StringComparison.Ordinal);
                Assert.Contains("A benchmark on pc2m on node pc2 can't start", detail, StringComparison.Ordinal);
            }

            Assert.Null(jobs.JobOnNode("pc2"));
            Assert.Equal(2, maintenance.Active.Count);

            // The old endpoint: today's shape, the bootstrap node's job only (with the additive nodeId); its DELETE stops
            // only that job.
            var status = await master.GetJsonAsync("/api/benchmark");
            Assert.Equal(["current", "last"], status.EnumerateObject().Select(p => p.Name));
            Assert.Equal(dgxJob, status.GetProperty("current").GetProperty("jobId").GetString());
            Assert.Equal("qwen", status.GetProperty("current").GetProperty("modelId").GetString());
            await ExpectDeleteAsync(master, "/api/benchmark", HttpStatusCode.Accepted);
            await ExitEnv.UntilAsync(() => jobs.JobOnNode("dgx") is null, "the dgx job to stop");
            Assert.Equal(pcJob, jobs.JobOnNode("pc")?.JobId);
            var after = await master.GetJsonAsync("/api/benchmark");
            Assert.Equal(JsonValueKind.Null, after.GetProperty("current").ValueKind);
            Assert.Equal("Cancelled", after.GetProperty("last").GetProperty("status").GetString());

            // A slot is free again: the third node's job starts beside the PC's.
            await master.ExpectAsync("/api/models/pc2m/benchmark", null, HttpStatusCode.Accepted);
            Assert.Equal(["pc", "pc2"], maintenance.Active.Select(w => w.NodeId!).Order(StringComparer.Ordinal));
        }
        finally
        {
            foreach (string node in new[] { "dgx", "pc", "pc2" })
            {
                fleet[node].Node.Model.SetTimeToFirstTokenMs(50);
            }

            jobs.Cancel();
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }

        Assert.Empty(maintenance.Active);
    }

    [Fact]
    public async Task BootJobRecoveryOfOneNodeIsNotHeldBackByAnotherNodeBeingOffline()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx")] });
        var pc = await fleet.JoinAsync("pc", PcGpus);
        var master = fleet.Master;
        await master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[0]]));
        await master.StartModelAsync("qwen");
        await master.StartModelAsync("pcm");
        var jobs = master.Get<ModelJobService>();
        await ExitEnv.UntilAsync(() => jobs.HasRecovered("dgx") && jobs.HasRecovered("pc"), "the first boot's job recovery (D104)");
        Assert.True(master.Get<IJobRecoveryGate>().IsPerNode);

        // An interrupted tuning run left a candidate on each node (mirbadllm.tuning=1, other settings).
        var launcher = master.Get<IModelLauncher>();
        foreach (string id in new[] { "qwen", "pcm" })
        {
            var saved = (await master.Models.GetAsync(id, Ct))!;
            await launcher.LaunchAsync(saved with { MaxNumSeqs = 32 }, new LaunchOptions(Force: true, Tuning: true), Ct);
        }

        await ExitEnv.UntilAsync(async () => (await launcher.ListTuningContainersAsync(Ct)).Order(StringComparer.Ordinal).SequenceEqual(["pcm", "qwen"]),
            "both candidates to run");
        int dgxCreates = fleet.Dgx.CreateBodiesOf("qwen").Count;
        int pcCreates = pc.CreateBodiesOf("pcm").Count;
        Assert.Contains(ContainerLabels.Tuning, fleet.Dgx.CreateBodiesOf("qwen")[^1], StringComparison.Ordinal);
        Assert.Contains(ContainerLabels.Tuning, pc.CreateBodiesOf("pcm")[^1], StringComparison.Ordinal);

        // The PC goes away, then the master restarts (no job survives a restart; recovery runs at boot).
        await pc.StopAsync();
        await fleet.RestartMasterAsync();
        master = fleet.Master;
        jobs = master.Get<ModelJobService>();

        // dgx is recovered while the PC is still Offline: its candidate is replaced by the saved settings.
        await ExitEnv.UntilAsync(() => jobs.HasRecovered("dgx"), "dgx's job recovery while pc is offline");
        await ExitEnv.UntilAsync(() => fleet.Dgx.CreateBodiesOf("qwen").Count > dgxCreates, "dgx's candidate to be relaunched");
        string relaunched = fleet.Dgx.CreateBodiesOf("qwen")[^1];
        Assert.DoesNotContain(ContainerLabels.Tuning, relaunched, StringComparison.Ordinal);
        Assert.Equal("16", MaxNumSeqsOf(relaunched)); // the saved settings, not the candidate's 32
        Assert.False(jobs.HasRecovered("pc"), "pc was recovered while it was not reconciled");
        Assert.NotEqual(NodeState.Online, master.Cache.GetNode("pc")?.State);
        Assert.Equal(pcCreates, pc.CreateBodiesOf("pcm").Count);

        // The PC comes back: it is recovered then, its own candidate replaced.
        await pc.StartAsync();
        await master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online again");
        await ExitEnv.UntilAsync(() => jobs.HasRecovered("pc"), "pc's job recovery once it is reconciled");
        await ExitEnv.UntilAsync(() => pc.CreateBodiesOf("pcm").Count > pcCreates, "pc's candidate to be relaunched");
        Assert.DoesNotContain(ContainerLabels.Tuning, pc.CreateBodiesOf("pcm")[^1], StringComparison.Ordinal);
        Assert.Empty(await master.Get<IModelLauncher>().ListTuningContainersAsync(Ct));
    }

    /// <summary>The value of <c>--max-num-seqs</c> in a create body ("" when absent).</summary>
    private static string MaxNumSeqsOf(string createBody)
    {
        var match = System.Text.RegularExpressions.Regex.Match(createBody, @"--max-num-seqs""?\s*[,= ]\s*""?(\d+)");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static async Task<string> InferenceKeyAsync(P2Master master) =>
        (await master.Get<IApiKeyStore>().CreateAsync("exit", ApiKeyScope.Inference, Ct)).FullKey;

    private static async Task<(HttpStatusCode Status, string? Code, string Body, string? RetryAfter)> ChatAsync(P2Master master, string model, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4 }),
        };
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await master.Anonymous.SendAsync(request, Ct);
        string body = await response.Content.ReadAsStringAsync(Ct);
        string? code = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var c))
            {
                code = c.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return (response.StatusCode, code, body, response.Headers.RetryAfter?.ToString());
    }

    private static async Task<HttpResponseMessage> ModelsRequestAsync(P2Master master, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return await master.Anonymous.SendAsync(request, Ct);
    }

    /// <summary>The model ids <c>GET /v1/models</c> lists (200 expected), ordered.</summary>
    private static async Task<string[]> ListedAsync(P2Master master, string key)
    {
        using var response = await ModelsRequestAsync(master, key);
        string body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"/v1/models: {(int)response.StatusCode} {body}");
        using var json = JsonDocument.Parse(body);
        return [.. json.RootElement.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).Order(StringComparer.Ordinal)];
    }

    private static async Task ExpectDeleteAsync(P2Master master, string path, HttpStatusCode expected)
    {
        var (status, body) = await SendAsync(master.Admin.DeleteAsync(path, Ct));
        Assert.True(status == expected, $"DELETE {path}: expected {(int)expected}, got {(int)status} {body}");
    }

    /// <summary>A page as the logged-in admin's browser gets it (prerendered HTML).</summary>
    private static async Task<string> PageAsync(P2Master master, string path)
    {
        var (status, body) = await SendAsync(master.Admin.GetAsync(path, Ct));
        Assert.True(status == HttpStatusCode.OK, $"GET {path}: {(int)status}");
        return body;
    }
}
