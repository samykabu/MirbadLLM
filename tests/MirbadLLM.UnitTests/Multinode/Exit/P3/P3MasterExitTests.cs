using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

namespace MirbadLLM.UnitTests.Multinode.P3Exit;

/// <summary>
/// MN-237: the master half of the P3/P4 development exit (plan-p3p4 §4.1), driven end to end through the real master
/// (<c>MirbadLLM.Web</c>'s <c>Program</c> over <see cref="P3Fleet"/>) and the real agent contract — the paths the W13
/// review (MN-230, D130) and the W14 review (MN-234, D131) could only prove with fakes or below the exit level:
/// <list type="number">
/// <item>A llama.cpp model with a <b>partial offload</b>: the fit preview's host figure against the
/// <c>HostBudgetGiB</c> the launch actually sends (D130; the gap is bug MN-239-17, reported here with its repro).</item>
/// <item>A tune of a llama.cpp <b>chat</b> model through <c>POST /api/models/{id}/tune</c> <b>without a goal</b>: it
/// must produce a winner, not candidates rejected with "No N=16 measurement".</item>
/// <item><c>?node=</c> on a download, a folder list and a catalog call against a node that is <b>not approved</b> (409
/// <c>invalid_state</c>, nothing sent) and against an <b>unreachable</b> node (503, not 500).</item>
/// <item><c>GET</c>/<c>POST /api/nodes/{id}/fit</c> against an <b>unapproved</b> node: 200 with "waiting for approval",
/// <c>estimate</c> null, and not one byte to the node (D131 (2)).</item>
/// </list>
/// The CPU-only start (D131 (1)) and the <c>processes.json</c>-only agent restart (D131 (3)) need the real agent and
/// live in <see cref="P3NodeExitTests"/>.
/// </summary>
public sealed class P3MasterExitTests
{
    private const string Dgx = "dgx";
    private const string Win = LlamaCppFixtures.NodeId;

    /// <summary>The GGUF's layer count; a partial offload keeps half of them on the CPU.</summary>
    private const int HalfTheLayers = P3Fleet.EmbedderBlocks / 2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly JsonSerializerOptions Json = P3Fleet.Json;

    // ============================================================ (1) the partial offload's host figure (D130)

    [Fact]
    public async Task APartialOffload_SendsTheHostFigureThePreviewShowed()
    {
        // D130 made every llama.cpp launch carry its own host budget, because the agent's vLLM default (4 GiB × GPUs + 2)
        // reserved several GiB nobody needs. The figure it sends is PlacementFit.EstimateHostGiB, a flat 1 GiB for
        // llama.cpp — but the fit preview shows PlacementFit.LlamaCppHostGiB, which is 1 GiB *plus* the weights left on
        // the CPU (GgufMemoryEstimate.HostGiB). With GpuLayers below the GGUF's block count the two disagree, and the
        // operator sees a number the node is never asked to hold.
        await using var fleet = await P3Fleet.StartAsync(Ct);
        var model = PartialOffload();
        await fleet.SaveModelAsync(model);

        // What the preview tells the operator: the host part is 1 GiB + the layers in host RAM.
        (HttpStatusCode status, string body) = await fleet.GetAsync($"/api/nodes/{Win}/fit?modelId={model.Id}");
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        var fit = JsonSerializer.Deserialize<NodeFitResult>(body, Json)!;
        Assert.NotNull(fit.Estimate);
        Assert.True(fit.Estimate.HostGiB > 0, $"a partial offload left nothing in host RAM: {fit.Estimate.HostGiB}");
        Assert.NotNull(fit.Host);
        Assert.True(fit.Host.Estimated, "the host figure is not an estimate, so the model carries its own budget");
        Assert.Equal(PlacementFit.LlamaCppHostGiB(fit.Estimate), fit.Host.NeedGiB, 1e-9);

        // What the launch asks the node to hold. These must be the same number.
        await fleet.StartModelAsync(model.Id);
        var request = Assert.Single(fleet.WindowsAgent.LaunchRequests, r => r.Spec.Kind == LaunchDelivery.Process);
        Assert.Equal(fit.Host.NeedGiB, request.Admission.HostBudgetGiB!.Value, 1e-9);
    }

    [Fact]
    public async Task APartialOffload_IsShownWithItsHostLayers_AndTheLaunchStillCarriesAHostBudget()
    {
        // The half of MN-239-17 that is settled and must not regress: the preview reads the node's GGUF header through
        // the composed PlacementFit and names the layers left in host RAM, and the launch carries a host budget of its
        // own (never the agent's vLLM default). Only the two figures' agreement is the bug.
        await using var fleet = await P3Fleet.StartAsync(Ct);
        var model = PartialOffload();
        await fleet.SaveModelAsync(model);

        (HttpStatusCode status, string body) = await fleet.GetAsync($"/api/nodes/{Win}/fit?modelId={model.Id}");
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        var fit = JsonSerializer.Deserialize<NodeFitResult>(body, Json)!;

        // The estimate is the GGUF's: half the tensor bytes stay on the CPU (36 blocks, GpuLayers 18).
        Assert.NotNull(fit.Estimate);
        Assert.Equal(Math.Round(P3Fleet.EmbedderSummary().TensorBytes / 2 / LlamaCppMemoryEstimate.GiB, 2), fit.Estimate.HostGiB, 1e-9);
        Assert.Contains("of layers in host RAM", fit.Message, StringComparison.Ordinal);
        Assert.Equal(PlacementFit.LlamaCppHostGiB(fit.Estimate), fit.Host!.NeedGiB, 1e-9);
        Assert.True(fit.Host.NeedGiB > 1, $"the host figure of a partial offload is still the flat 1 GiB: {fit.Host.NeedGiB}");
        Assert.Contains(AgentRoutes.GgufInspect.Name, fleet.WindowsAgent.Requests);

        // A full offload leaves nothing on the CPU, and its message says so (the other side of the same formula).
        await fleet.SaveModelAsync(P3Fleet.Embedder());
        var offloaded = JsonSerializer.Deserialize<NodeFitResult>(
            (await fleet.GetAsync($"/api/nodes/{Win}/fit?modelId={P3Fleet.Embedder().Id}")).Body, Json)!;
        Assert.Equal(0, offloaded.Estimate!.HostGiB);
        Assert.DoesNotContain("in host RAM", offloaded.Message, StringComparison.Ordinal);

        // And the launch carries a host budget, whatever its value: never null, so the agent never falls back to
        // DiscreteAdmission's vLLM estimate (4 GiB × GPUs + 2) for a llama.cpp process.
        await fleet.StartModelAsync(model.Id);
        var request = Assert.Single(fleet.WindowsAgent.LaunchRequests, r => r.Spec.Kind == LaunchDelivery.Process);
        Assert.NotNull(request.Admission.HostBudgetGiB);
        Assert.NotEqual(DiscreteAdmissionHostEstimateOfOneGpu, request.Admission.HostBudgetGiB!.Value);
    }

    /// <summary>The agent's own vLLM host estimate for one GPU (<c>4 × 1 + 2</c>), which a llama.cpp launch must not use.</summary>
    private const double DiscreteAdmissionHostEstimateOfOneGpu = 6;

    [Fact]
    public async Task AFolderWithSeveralGgufs_RefusesWithTheNamesAsStructuredDetails_NotOnlyInTheSentence()
    {
        // MN-239 item 14. The last source-text tripwire MN-234 left behind asserted that FakeAgent.Storage.cs contains
        // the text "new GgufChoiceDetails(" — which a comment satisfies and a rename breaks. Driving the fake's own
        // refusal needs a fleet, which this class has: two GGUFs in one folder, asked without ?file=, and the editor's
        // production path (ModelSourcing.InspectGgufAsync) must get the names from the details rather than by parsing
        // the English. The message here names *no* files at all, so a parse cannot pass this test.
        await using var fleet = await P3Fleet.StartAsync(Ct);
        var second = P3Fleet.EmbedderSummary() with { File = "Qwen3-Embedding-4B-Q4_K_M.gguf" };
        fleet.WindowsAgent.SetGgufSummary(second);

        var choice = await MirbadLLM.Web.ModelSourcing.InspectGgufAsync(fleet.Services, Win, P3Fleet.EmbedderFolder, null, Ct);

        Assert.Null(choice.Summary);
        Assert.Equal([second.File, P3Fleet.EmbedderFile], choice.Candidates.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(P3Fleet.EmbedderFile, choice.Error, StringComparison.Ordinal);

        // Naming one still answers it, so the refusal is about the ambiguity and nothing else.
        var named = await MirbadLLM.Web.ModelSourcing.InspectGgufAsync(fleet.Services, Win, P3Fleet.EmbedderFolder, P3Fleet.EmbedderFile, Ct);
        Assert.Equal(P3Fleet.EmbedderFile, named.Summary!.File);
        Assert.Empty(named.Candidates);
    }

    // ============================================================ (2) a tune without a goal produces a winner (D130)

    [Fact]
    public async Task ATuneOfAChatModel_StartedWithoutAGoal_ProducesAWinner_AndNoCandidateIsRejectedForAMissingN16()
    {
        // D130: POST /api/models/{id}/tune without a goal is the classic Balanced, whose score needs an N=16
        // measurement. A llama.cpp model's classic chat levels stop at its slots (at most 8), so every candidate — the
        // baseline included — used to be rejected with "No N=16 measurement" and the run ended without a winner.
        // EffectiveGoal now maps it to a workload preset, whose levels always include 16. Driven here end to end: the
        // real master's ModelJobService, the real BenchmarkSuite over the agent proxy, and the fake llama-server.
        await using var fleet = await P3Fleet.StartAsync(Ct);
        Hurry(fleet.Services);
        var chat = ChatModel();
        fleet.WindowsAgent.SetGgufSummary(ChatSummary());
        await fleet.SaveModelAsync(chat);
        var jobs = fleet.Services.GetRequiredService<ModelJobService>();

        TuningGoal requested;
        using (var started = await fleet.Http.PostAsJsonAsync(
                   $"/api/models/{chat.Id}/tune", new TuneRequestBody([TuningDimension.Parallel]), Json, Ct))
        {
            string body = await started.Content.ReadAsStringAsync(Ct);
            Assert.True(started.StatusCode == HttpStatusCode.Accepted, $"{(int)started.StatusCode} {body}");
            requested = JsonSerializer.Deserialize<TuningGoal>(JsonNode.Parse(body)!["goal"]!.ToJsonString(), Json);
        }

        // The goal the run is scored on: a workload preset of the chat family, not the classic Balanced.
        Assert.Equal(WorkloadProfiles.PresetFor(TuningGoal.Balanced, pooling: false), requested);
        Assert.True(WorkloadProfiles.IsPreset(requested), $"{requested} is not a workload preset");
        Assert.NotEqual(TuningGoal.Balanced, requested);

        await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromMinutes(8), Ct);

        var run = Assert.Single(await fleet.Services.GetRequiredService<BenchmarkStore>().ListTuningAsync(chat.Id, Ct));
        Assert.Equal(TuningRunStatus.Completed, run.Status);
        Assert.Null(run.Error);

        // The point of D130: a winner, and no candidate rejected for a measurement the suite never takes.
        Assert.NotNull(run.WinnerIndex);
        Assert.NotNull(run.Winner);
        Assert.Equal(CandidateOutcome.Accepted, run.Winner!.Outcome);
        var rejections = run.Candidates.Where(c => c.Reason is { } reason && reason.Contains("N=16", StringComparison.Ordinal)).ToList();
        Assert.True(rejections.Count == 0, "rejected for a missing N=16 measurement: " + string.Join("; ", rejections.Select(c => $"{c.Label}: {c.Reason}")));
        Assert.DoesNotContain(run.Notes, n => n.Contains("No N=16 measurement", StringComparison.Ordinal));

        // Every measured candidate was really measured at N=16 (what the classic levels could not give it).
        var baseline = Assert.Single(run.Candidates, c => c.Index == 0);
        Assert.NotNull(baseline.Result);
        Assert.Contains(16, baseline.Result!.Levels.Select(l => l.Concurrency));

        // And the run is readable through the API the page uses.
        (HttpStatusCode status, string listBody) = await fleet.GetAsync($"/api/models/{chat.Id}/tuning");
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {listBody}");
        Assert.Contains(run.Id, listBody, StringComparison.Ordinal);
    }

    // ============================================================ (3) ?node= on an unapproved and an unreachable node

    [Fact]
    public async Task NodeScopedAdminCalls_AgainstAnUnapprovedNode_Refuse409_WithoutSendingAByte()
    {
        // D130/MN-230: approval is the master's trust gate. A download would hand an unapproved node the Hugging Face
        // token and write to its disk, so every ?node= route refuses 409 invalid_state before anything is sent.
        await using var fleet = await P3Fleet.StartAsync(Ct, approveWindows: false);
        int before = fleet.WindowsAgent.Requests.Count;

        foreach ((string method, string path) in NodeScopedRoutes(Win))
        {
            using var response = await Send(fleet, method, path);
            string body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{method} {path}: {(int)response.StatusCode} {body}");
            Assert.Equal(ErrorCodes.InvalidState, JsonNode.Parse(body)!["code"]!.GetValue<string>());
            Assert.Contains("not approved", body, StringComparison.Ordinal);
        }

        // Not one request reached the node, so the token in the POST body never left the master.
        Assert.Equal(before, fleet.WindowsAgent.Requests.Count);

        // The DGX answers the same routes exactly as a P2 master does: one fleet member, no ?node= needed.
        foreach (string path in new[] { "/api/downloads", "/api/model-folders", "/api/catalog" })
        {
            (HttpStatusCode status, string body) = await fleet.GetAsync(path);
            Assert.True(status == HttpStatusCode.OK, $"GET {path}: {(int)status} {body}");
        }
    }

    [Fact]
    public async Task NodeScopedAdminCalls_AgainstAnUnreachableNode_Answer503_NotA500()
    {
        // The other half of D130's refusal pair: an approved node the master cannot reach is 503, not the 500 an
        // unhandled NodeUnavailableException used to produce.
        await using var fleet = await P3Fleet.StartAsync(Ct);
        fleet.WindowsAgent.SetPartitioned(true);

        foreach ((string method, string path) in NodeReadingRoutes(Win))
        {
            using var response = await Send(fleet, method, path);
            string body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(
                response.StatusCode == HttpStatusCode.ServiceUnavailable,
                $"{method} {path}: {(int)response.StatusCode} {body}");
            Assert.DoesNotContain("Unhandled", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
        }

        // /api/catalog is the exception, and deliberately so: it is a master-local read
        // (RemoteVllmCatalogService: the node's last capability report plus the fixed option lists), so an unreachable
        // node still answers the editor's picker from what the master already knows. Nothing is sent to the node, so
        // there is nothing for the transport to fail — and the pre-approval 409 above still covers the trust gate.
        (HttpStatusCode catalog, string catalogBody) = await fleet.GetAsync("/api/catalog?node=" + Win);
        Assert.True(catalog == HttpStatusCode.OK, $"GET /api/catalog?node={Win}: {(int)catalog} {catalogBody}");

        // The DGX is unaffected: the partition is per node, and its routes still answer.
        foreach (string path in new[] { "/api/downloads", "/api/model-folders", "/api/catalog" })
        {
            (HttpStatusCode status, string body) = await fleet.GetAsync($"{path}?node={Dgx}");
            Assert.True(status == HttpStatusCode.OK, $"GET {path}?node={Dgx}: {(int)status} {body}");
        }

        fleet.WindowsAgent.SetPartitioned(false);
    }

    // ============================================================ (4) the fit route against an unapproved node (D131)

    [Fact]
    public async Task TheFitRoute_AgainstAnUnapprovedNode_Answers200WaitingForApproval_AndSendsTheNodeNothing()
    {
        // D131 (2): fit deliberately does not take the 409 path — the Nodes page shows a verdict before approval. The
        // invariant it does keep is "nothing reaches the agent before approval", now enforced in PlacementFit.GgufAsync.
        // Before MN-234 the composed PlacementFit would have read a GGUF header off a node the operator had not
        // approved. Both the GET and the POST shape are driven here.
        await using var fleet = await P3Fleet.StartAsync(Ct, approveWindows: false);
        var model = P3Fleet.Embedder();

        // A stored llama.cpp model on the pending node, written straight into models.json: the live registry rightly
        // refuses to save it (an unapproved node is no placement target, D102), and ?modelId= needs a stored model.
        await SeedOnDiskAsync(fleet, model);
        Assert.Equal(model.Id, (await fleet.Services.GetRequiredService<IModelRegistry>().GetAsync(model.Id, Ct))!.Id);
        await fleet.Services.GetRequiredService<INodeStatusCache>().RefreshNodeAsync(Win, Ct);
        Assert.Equal(NodeState.PendingApproval, fleet.Services.GetRequiredService<INodeStatusCache>().GetNode(Win)!.State);

        int before = fleet.WindowsAgent.Requests.Count;
        foreach (var (what, call) in new (string, Func<Task<HttpResponseMessage>>)[]
                 {
                     ($"GET /api/nodes/{Win}/fit?modelId={model.Id}", () => fleet.Http.GetAsync($"/api/nodes/{Win}/fit?modelId={model.Id}", Ct)),
                     ($"POST /api/nodes/{Win}/fit", () => fleet.Http.PostAsJsonAsync($"/api/nodes/{Win}/fit", new NodeFitRequest { Definition = model }, Json, Ct)),
                 })
        {
            using var response = await call();
            string body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{what}: {(int)response.StatusCode} {body}");
            var fit = JsonSerializer.Deserialize<NodeFitResult>(body, Json)!;
            Assert.Equal(Win, fit.NodeId);
            Assert.Contains("waiting for approval", fit.Message, StringComparison.Ordinal);
            Assert.Null(fit.Estimate);
            Assert.Null(fit.Fits);
            Assert.DoesNotContain("GGUF estimate", fit.Message, StringComparison.Ordinal);
        }

        Assert.Equal(before, fleet.WindowsAgent.Requests.Count);
        Assert.DoesNotContain(AgentRoutes.GgufInspect.Name, fleet.WindowsAgent.Requests);

        // The route above is held by CheckAsync's IsServing check, because the status cache has polled this node and
        // knows it is pending, and by the record having no capabilities yet. D131 (2) names the case neither reaches —
        // a PlacementFit built without a status cache, over a pending record that does carry a capability report with
        // gguf.inspect — and that is the only thing PlacementFit.GgufAsync's own approval gate stands in front of.
        // Driven here with the composed registries and the composed agent clients, and no status cache.
        var nodes = fleet.Services.GetRequiredService<INodeRegistry>();
        var pending = (await nodes.GetAsync(Win, Ct))!;
        Assert.False(pending.Approved);
        Assert.Null(pending.LastCapabilities); // the master stores none before approval
        await nodes.UpsertAsync(pending with { LastCapabilities = LlamaCppFixtures.Capabilities() }, Ct);
        Assert.Contains(AgentFeatures.GgufInspect, (await nodes.GetAsync(Win, Ct))!.LastCapabilities!.Features);

        var cacheless = new PlacementFit(
            nodes,
            fleet.Services.GetRequiredService<IModelRegistry>(),
            fleet.Services.GetService<INodeTelemetry>(),
            status: null,
            fleet.Services.GetService<TimeProvider>(),
            fleet.Services.GetRequiredService<IAgentClientFactory>());
        var withoutCache = await cacheless.CheckAsync(Win, new NodeFitRequest { Definition = model }, Ct);
        Assert.NotNull(withoutCache);
        Assert.Null(withoutCache.Estimate);
        Assert.Equal(before, fleet.WindowsAgent.Requests.Count);
        Assert.DoesNotContain(AgentRoutes.GgufInspect.Name, fleet.WindowsAgent.Requests);

        // Restore the record, so the approval below is the real path again.
        await nodes.UpsertAsync((await nodes.GetAsync(Win, Ct))! with { LastCapabilities = null }, Ct);

        // The complement, so the assertions above cannot pass for the wrong reason: approved, the same routes read the
        // node's GGUF header and answer with the estimate.
        using (var approve = await fleet.Http.PostAsync($"/api/nodes/{Win}/approve", content: null, Ct))
        {
            Assert.True(approve.StatusCode == HttpStatusCode.OK, $"{(int)approve.StatusCode} {await approve.Content.ReadAsStringAsync(Ct)}");
        }

        await P3Fleet.WaitUntilAsync(
            async () => (await fleet.Services.GetRequiredService<INodeRegistry>().GetAsync(Win, Ct))?.LastCapabilities is not null,
            "the master never read the approved node's capabilities");
        await P3Fleet.WaitUntilAsync(
            async () => JsonSerializer.Deserialize<NodeFitResult>(
                (await fleet.GetAsync($"/api/nodes/{Win}/fit?modelId={model.Id}")).Body, Json)!.Estimate is not null,
            "the approved node's fit never carried the GGUF estimate");
        Assert.Contains(AgentRoutes.GgufInspect.Name, fleet.WindowsAgent.Requests);
    }

    // ============================================================ helpers

    /// <summary>The <c>?node=</c> routes of design §5.6 that act on one node (MN-229, MN-230).</summary>
    private static (string Method, string Path)[] NodeScopedRoutes(string node) =>
    [
        ("GET", "/api/downloads?node=" + node),
        ("POST", "/api/downloads?node=" + node),
        ("GET", "/api/model-folders?node=" + node),
        ("GET", $"/api/model-folders/{P3Fleet.EmbedderFolder}/gguf?node={node}"),
        ("GET", "/api/catalog?node=" + node),
    ];

    /// <summary>The subset of <see cref="NodeScopedRoutes"/> that really reads the node (everything but the catalog).</summary>
    private static (string Method, string Path)[] NodeReadingRoutes(string node) =>
    [
        ("GET", "/api/downloads?node=" + node),
        ("POST", "/api/downloads?node=" + node),
        ("GET", "/api/model-folders?node=" + node),
        ("GET", $"/api/model-folders/{P3Fleet.EmbedderFolder}?node={node}"),
        ("GET", $"/api/model-folders/{P3Fleet.EmbedderFolder}/gguf?node={node}"),
    ];

    private static Task<HttpResponseMessage> Send(P3Fleet fleet, string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (!string.Equals(method, "GET", StringComparison.Ordinal))
        {
            // A real download body, token included: an unapproved node must never see it (MN-230).
            request.Content = JsonContent.Create(
                new DownloadRequest("Qwen/Qwen3-Embedding-4B-GGUF") { Token = "hf_exit_test_token" }, options: Json);
        }

        return SendAndDisposeAsync(fleet, request);
    }

    private static async Task<HttpResponseMessage> SendAndDisposeAsync(P3Fleet fleet, HttpRequestMessage request)
    {
        using (request)
        {
            return await fleet.Http.SendAsync(request, Ct);
        }
    }

    /// <summary>The embedder with half its layers left on the CPU and no host budget of its own (D130's partial offload).</summary>
    private static ModelDefinition PartialOffload()
    {
        var model = P3Fleet.Embedder();
        return model with
        {
            Id = "qwen3-embedding-4b-win-partial",
            ServedName = "qwen3-embedding-4b-win-partial",
            HostMemoryBudgetGiB = null,
            LlamaCpp = model.LlamaCpp! with { GpuLayers = HalfTheLayers },
        };
    }

    /// <summary>A chat GGUF on the 5060 Ti (the fixture planner model), with a budget the candidates' estimates fit in.</summary>
    private static ModelDefinition ChatModel()
    {
        var chat = LlamaCppFixtures.Chat();
        return chat with
        {
            AutoStart = false,
            MemoryBudgetGiB = 8,
            HostMemoryBudgetGiB = null,
            // The fixture's Ollama source (name:tag) is not a Hugging Face repo id, and the Ollama puller is deferred
            // (MN-221, O7): the folder is already on the node, so the tune needs no source at all.
            Sources = [],
            LlamaCpp = chat.LlamaCpp! with { ExecutableVersion = null },
        };
    }

    /// <summary>The chat model's GGUF header as the agent reads it (a small 1.7 B chat model).</summary>
    private static GgufSummary ChatSummary() => new()
    {
        Folder = "ov-intent-analysis-v7-q8",
        File = "model.gguf",
        GgufVersion = 3,
        Architecture = "qwen3",
        Name = "ov-intent-analysis-sft",
        FileType = "Q8_0",
        ParameterCount = 1_720_000_000,
        BlockCount = 28,
        ContextLength = 32768,
        EmbeddingLength = 2048,
        HeadCount = 16,
        HeadCountKv = 8,
        VocabSize = 151_669,
        CausalAttention = true,
        TensorBytes = 1_830_000_000,
        FileBytes = 1_840_000_000,
    };

    /// <summary>
    /// Writes <paramref name="model"/> into the master's <c>models.json</c> through a bare
    /// <see cref="JsonModelRegistry"/> (no validators), which the live registry re-reads on every call. The live
    /// registry would refuse the save itself: a node waiting for approval is no placement target (D102), and that is
    /// exactly the state this test needs a stored model in.
    /// </summary>
    private static async Task SeedOnDiskAsync(P3Fleet fleet, ModelDefinition model)
    {
        string dataPath = fleet.Services.GetRequiredService<IOptions<MirbadLLMOptions>>().Value.DataPath;
        using var bare = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataPath }));
        await bare.UpsertAsync(model, Ct);
    }

    /// <summary>
    /// Shortens the job service's waits and the benchmark suite's workloads so a full tuning run finishes inside a test
    /// run. The levels keep 1 and 16: N=16 is the measurement the whole path is about.
    /// </summary>
    private static void Hurry(IServiceProvider services)
    {
        var jobs = services.GetRequiredService<ModelJobOptions>();
        jobs.PollInterval = TimeSpan.FromMilliseconds(20);
        jobs.DrainTimeout = TimeSpan.FromMilliseconds(100);
        jobs.GpuIdleSampleDuration = TimeSpan.FromMilliseconds(10);
        jobs.GpuIdleSampleInterval = TimeSpan.FromMilliseconds(5);
        jobs.CandidateStartTimeout = TimeSpan.FromSeconds(30);
        jobs.ConfirmationRandom = new Random(1);

        var suite = services.GetRequiredService<BenchmarkSuiteOptions>();
        suite.WorkloadLevels = [1, 16];
        suite.MaxConcurrency = 16;
        suite.ChatOutputTokens = [8];
        suite.MaxTokens = 8;
        suite.SharedPrefixTokens = 64;
        suite.PrefillTokens = 64;
        suite.IncludePrefill = false;
        suite.RequestTimeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>The body of <c>POST /api/models/{id}/tune</c> with no goal (the API default is the classic Balanced).</summary>
    private sealed record TuneRequestBody(IReadOnlyList<TuningDimension> Steps);
}
