using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.Pki;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Web;
using MirbadLLM.Web.Api;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-233: the P3/P4 master composition, driven through the real master (<c>MirbadLLM.Web</c>'s <c>Program</c>) with a
/// <see cref="FakeAgent"/> per node — a P1/P2 DGX and the owner's Windows PC as the frozen
/// <c>NodeCapabilities.windows.json</c> reports it (llama.cpp as a native process, <c>gguf.inspect</c>,
/// <c>admission.gpu-budgets</c>, <c>services.scm</c>, no Docker and no container runtime).
/// <list type="bullet">
/// <item>Composition: the llama.cpp builder path over <see cref="RemoteModelRuntime"/>, the KPI mapping through the
/// hosted <see cref="RemoteTelemetryCollector"/>, the one <see cref="PlacementFit"/> with the agent clients (MN-233's
/// fix: every construction site built it without agents, so <c>/api/nodes/{id}/fit</c> and the model editor never
/// showed a GGUF estimate), <see cref="ModelJobService"/> with its <c>inspectGguf</c> delegate and the
/// <c>EffectiveGoal</c> mapping, the win-x64 bundle catalogue and the per-node admin APIs.</item>
/// <item>A Windows node waiting for approval is no fleet member (D102): the DGX answers every route exactly as a P2
/// master, and anything addressed to the pending node is refused 409 <c>invalid_state</c> without a byte being sent.</item>
/// <item>Approved, it runs a llama.cpp model end to end: a <c>kind: process</c> launch with the node's verified
/// <c>llama-server</c> version, no legacy config hash, the execution fingerprint and the host budget of D130, then
/// <c>/v1</c> (buffered, streamed and the <c>dimensions</c> emulation) through the master's router and the agent's
/// proxy, KPIs without a NaN in the fleet sums, and GPU memory "measured at start".</item>
/// </list>
/// <para>The DGX stays a P2 node throughout: it reports none of <see cref="AgentFeatures.P3Reported"/>, and
/// <see cref="P2CompositionTests"/> keeps the live single-DGX equivalence with the real agent.</para>
/// </summary>
public sealed class P3CompositionTests
{
    private const string Dgx = "dgx";
    private const string Win = LlamaCppFixtures.NodeId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ============================================================== composition

    [Fact]
    public async Task TheP3Master_ComposesTheLlamaCppBuilderPath_TheKpiMapping_TheBundleCatalogue_AndThePerNodeApis()
    {
        await using var fleet = await P3Fleet.StartAsync(Ct);
        IServiceProvider services = fleet.Services;

        // The llama.cpp builder path: one RemoteModelRuntime behind IModelRuntime and IModelLauncher, and its config hash
        // of a llama.cpp model is the execution fingerprint of the process spec it would launch (design-p3p4 §A.1.6).
        var runtime = services.GetRequiredService<RemoteModelRuntime>();
        Assert.Same(runtime, services.GetRequiredService<IModelRuntime>());
        Assert.Same(runtime, services.GetRequiredService<IModelLauncher>());
        var embedder = P3Fleet.Embedder();
        // The Windows node's record is seeded without capabilities (MN-234), so everything below reads what the master
        // itself pulled off the agent rather than the fixture P3Fleet wrote; P3Fleet.ConnectAsync waits for that pull.
        var node = (await services.GetRequiredService<INodeRegistry>().GetAsync(Win, Ct))!;
        var built = LaunchSpecBuilder.BuildLlamaCpp(embedder, node, tuning: false);
        Assert.Equal(LaunchDelivery.Process, built.Spec.Kind);
        Assert.Equal(built.ExecutionFingerprint, await runtime.ComputeConfigHashAsync(embedder, Ct));

        // The KPI mapping reaches the master through the hosted collector (the only caller of ModelKpiCalculator).
        var collector = services.GetRequiredService<RemoteTelemetryCollector>();
        Assert.Same(collector, services.GetRequiredService<INodeTelemetry>());
        Assert.Contains(services.GetServices<IHostedService>(), h => ReferenceEquals(h, collector));
        Assert.Same(services.GetRequiredService<InMemoryMetricsHistory>(), services.GetRequiredService<IMetricsHistory>());

        // One PlacementFit, composed with the agent clients (MN-233): the fit route and the editor share it, so both show
        // the GGUF estimate of a node reporting gguf.inspect.
        var fit = services.GetRequiredService<PlacementFit>();
        Assert.Same(fit, services.GetRequiredService<PlacementFit>());
        Assert.NotNull((await fit.CheckAsync(Win, new NodeFitRequest { Definition = embedder }, Ct))!.Estimate);

        // EmbeddingsCompat's gate: the registry the router resolves per request, and the fixture model opts in.
        Assert.NotNull(services.GetRequiredService<IModelRegistry>());
        Assert.True(EmbeddingDimensions.Applies(embedder));

        // The bundle catalogue, with the win-x64 zip (design-p3p4 §A.4.5).
        var bundles = services.GetRequiredService<AgentBundleCatalog>();
        AgentBundle windows = Assert.Single(bundles.Bundles);
        Assert.Equal((AgentBundleCatalog.WindowsRid, fleet.WindowsBundleSha256), (windows.Rid, windows.Sha256));
        using (var index = await fleet.Http.GetAsync("/agent/bundles", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, index.StatusCode);
            Assert.Contains(AgentBundleCatalog.WindowsRid, await index.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var zip = await fleet.Http.GetAsync("/agent/bundles/win-x64.zip", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, zip.StatusCode);
            Assert.Equal("application/zip", zip.Content.Headers.ContentType?.MediaType);
            Assert.Equal(windows.Size, (await zip.Content.ReadAsByteArrayAsync(Ct)).Length);
        }

        // The per-node admin APIs are mapped on this master: nodes in /api/status, and ?node= required with two members.
        JsonObject status = JsonNode.Parse((await fleet.GetAsync("/api/status")).Body)!.AsObject();
        Assert.Equal([Dgx, Win], status["nodes"]!.AsArray().Select(n => (string)n!["id"]!));
        foreach (string path in new[] { "/api/downloads", "/api/model-folders", "/api/catalog" })
        {
            (HttpStatusCode noNode, string body) = await fleet.GetAsync(path);
            Assert.True(noNode == HttpStatusCode.BadRequest, $"GET {path}: {(int)noNode} {body}");
            Assert.Contains(ErrorCodes.NodeRequired, body, StringComparison.Ordinal);
        }

        // The DGX reports exactly P2's features: not one P3/P4 flag reaches it.
        var dgx = (await services.GetRequiredService<INodeRegistry>().GetAsync(Dgx, Ct))!;
        Assert.Empty(dgx.LastCapabilities!.Features.Intersect(AgentFeatures.P3Reported));
        Assert.Contains(AgentFeatures.RuntimeVllmContainer, dgx.LastCapabilities.Features);
        Assert.Equal("linux", dgx.LastCapabilities.Os.Family);
        // And the Windows node reports the P3/P4 flags and no Docker one.
        Assert.Equal("windows", node.LastCapabilities!.Os.Family);
        Assert.Equal(AgentFeatures.P3Reported.Order(StringComparer.Ordinal), node.LastCapabilities.Features.Intersect(AgentFeatures.P3Reported).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(AgentFeatures.RuntimeVllmContainer, node.LastCapabilities.Features);
        Assert.DoesNotContain(AgentFeatures.DownloadsHfContainer, node.LastCapabilities.Features);
        Assert.DoesNotContain(AgentFeatures.ServicesDocker, node.LastCapabilities.Features);

        // MN-234: and it looks like the node the product actually builds, not a Linux fake wearing a Windows OS string.
        // The real agent's DockerAvailability filter leaves no container runtime, no image list and no containers flag,
        // and the SCM contributor reports no hostd version; the frozen NodeCapabilities.windows.json says exactly that.
        // Without it the master offered the vLLM arm64 image in the editor's picker for this node, and the node sat
        // Degraded with "hostd does not answer" — so every assertion below ran against a node that is never Online.
        Assert.DoesNotContain(node.LastCapabilities.Runtimes, r => r.Delivery == LaunchDelivery.Container);
        Assert.Empty(node.LastCapabilities.Images);
        Assert.Equal(HostServiceManagers.Scm, node.LastCapabilities.HostServices!.Manager);
        Assert.False(node.LastCapabilities.HostServices.Containers);
        Assert.Null(node.LastCapabilities.HostServices.HostdVersion);
        Assert.Equal(NodeState.Online, services.GetRequiredService<INodeStatusCache>().GetNode(Win)!.State);
    }

    [Fact]
    public async Task TheComposedJobService_MapsAClassicGoalToAWorkloadPreset_ForALlamaCppModel()
    {
        await using var fleet = await P3Fleet.StartAsync(Ct);
        var embedder = P3Fleet.Embedder();
        await fleet.SaveModelAsync(embedder);
        var jobs = fleet.Services.GetRequiredService<ModelJobService>();

        // POST without a goal is the classic Balanced (the API default). A llama.cpp model's tune must run as a workload
        // preset instead (MN-230, D130): the classic levels stop at its slots, so the N=16 comparison has no measurement
        // and every candidate — the baseline included — would be rejected.
        try
        {
            using var started = await fleet.Http.PostAsync($"/api/models/{embedder.Id}/tune", content: null, Ct);
            string body = await started.Content.ReadAsStringAsync(Ct);
            Assert.True(started.StatusCode == HttpStatusCode.Accepted, $"{(int)started.StatusCode} {body}");
            var goal = JsonSerializer.Deserialize<TuningGoal>(JsonNode.Parse(body)!["goal"]!.ToJsonString(), P3Fleet.Json);
            Assert.Equal(WorkloadProfiles.PresetFor(TuningGoal.Balanced, pooling: true), goal);
            Assert.True(WorkloadProfiles.IsPreset(goal), $"{goal} is not a workload preset");
            Assert.NotEqual(TuningGoal.Balanced, goal);
            Assert.Equal(goal, jobs.JobOnNode(Win)!.Goal);
            // And the composed inspectGguf delegate (MN-225, wired in AddMirbadLLMOrchestration) read the model's GGUF
            // header from its node for the candidates' memory checks.
            await P3Fleet.WaitUntilAsync(
                () => Task.FromResult(fleet.WindowsAgent.Requests.Contains(AgentRoutes.GgufInspect.Name)),
                "the tune never read the model's GGUF header from the node");
        }
        finally
        {
            jobs.CancelOnNode(Win, JobKind.Tuning);
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }

        // A vLLM model keeps its classic goal, exactly as in P2.
        Assert.False(WorkloadProfiles.IsPreset(TuningGoal.Balanced));
        Assert.False(LlamaCppTuning.Applies(P3Fleet.VllmModel()));
        Assert.True(LlamaCppTuning.Applies(embedder));
    }

    // ============================================================== a Windows node waiting for approval

    [Fact]
    public async Task AWindowsNodeWaitingForApproval_IsNoFleetMember_AndNothingIsSentToIt()
    {
        await using var fleet = await P3Fleet.StartAsync(Ct, approveWindows: false);

        // Exactly P2's single-DGX answers: one fleet member, so no ?node= is required and every route goes to the DGX.
        foreach (string path in new[] { "/api/downloads", "/api/model-folders", "/api/catalog" })
        {
            (HttpStatusCode status, string body) = await fleet.GetAsync(path);
            Assert.True(status == HttpStatusCode.OK, $"GET {path}: {(int)status} {body}");
        }

        Assert.False(await fleet.Services.GetRequiredService<NodeScopedMaintenancePolicy>().IsOnAsync(Ct));

        // It is listed, not approved, and its P3 features never turn a flag on for the DGX.
        JsonArray nodes = JsonNode.Parse((await fleet.GetAsync("/api/status")).Body)!["nodes"]!.AsArray();
        JsonNode pending = Assert.Single(nodes, n => (string)n!["id"]! == Win)!;
        Assert.False((bool)pending["approved"]!);
        var dgx = (await fleet.Services.GetRequiredService<INodeRegistry>().GetAsync(Dgx, Ct))!;
        Assert.Empty(dgx.LastCapabilities!.Features.Intersect(AgentFeatures.P3Reported));

        // Anything addressed to it is refused 409 invalid_state before a byte leaves the master (MN-230: a download would
        // otherwise hand an unapproved node the Hugging Face token).
        int before = fleet.WindowsAgent.Requests.Count;
        foreach ((string method, string path) in new[]
                 {
                     ("GET", "/api/downloads?node=" + Win),
                     ("POST", "/api/downloads?node=" + Win),
                     ("GET", "/api/model-folders?node=" + Win),
                     ("GET", $"/api/model-folders/{P3Fleet.EmbedderFolder}/gguf?node={Win}"),
                     ("GET", "/api/catalog?node=" + Win),
                 })
        {
            using var response = await fleet.SendAsync(method, path);
            string body = await response.Content.ReadAsStringAsync(Ct);
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{method} {path}: {(int)response.StatusCode} {body}");
            Assert.Contains(ErrorCodes.InvalidState, body, StringComparison.Ordinal);
        }

        Assert.Equal(before, fleet.WindowsAgent.Requests.Count);

        // And a llama.cpp model may not be placed on it: an unapproved node is no placement target (D102).
        var refusal = await Assert.ThrowsAsync<PlacementValidationException>(
            () => fleet.Services.GetRequiredService<IModelRegistry>().UpsertAsync(P3Fleet.Embedder(), Ct));
        Assert.Contains(Win, refusal.Message, StringComparison.Ordinal);

        // Approving it makes the fleet two nodes: from here on every per-node route needs ?node= (design §5.6, MN-229).
        using (var approve = await fleet.Http.PostAsync($"/api/nodes/{Win}/approve", content: null, Ct))
        {
            Assert.True(approve.StatusCode == HttpStatusCode.OK, $"{(int)approve.StatusCode} {await approve.Content.ReadAsStringAsync(Ct)}");
        }

        (HttpStatusCode after, string afterBody) = await fleet.GetAsync("/api/downloads");
        Assert.True(after == HttpStatusCode.BadRequest, $"{(int)after} {afterBody}");
        Assert.Contains(ErrorCodes.NodeRequired, afterBody, StringComparison.Ordinal);
        Assert.True(await fleet.Services.GetRequiredService<NodeScopedMaintenancePolicy>().IsOnAsync(Ct));
        await fleet.Services.GetRequiredService<IModelRegistry>().UpsertAsync(P3Fleet.Embedder(), Ct); // now a valid placement
    }

    // ============================================================== an approved Windows node

    [Fact]
    public async Task AnApprovedWindowsNode_LaunchesALlamaCppModel_EndToEndThroughTheP3Master()
    {
        await using var fleet = await P3Fleet.StartAsync(Ct);
        var embedder = P3Fleet.Embedder();
        await fleet.SaveModelAsync(embedder);

        await fleet.StartModelAsync(embedder.Id);

        // The launch the master sent: a process spec by logical executable with the node's verified default version, no
        // image, no mounts, no legacy config hash, the execution fingerprint, and the host budget of D130.
        var request = Assert.Single(fleet.WindowsAgent.LaunchRequests, r => r.Spec.Kind == LaunchDelivery.Process);
        Assert.Equal(LaunchDelivery.Process, request.Spec.Kind);
        Assert.Equal(RuntimeKind.LlamaCpp, request.Spec.Runtime);
        Assert.Equal(ExecutableNames.LlamaServer, request.Spec.Executable);
        Assert.Equal(P3Fleet.DefaultLlamaCppVersion, request.Spec.ExecutableVersion); // the node's verified default (§A.1.2)
        Assert.Null(request.Spec.Image);
        Assert.Empty(request.Spec.Mounts);
        Assert.Null(request.ExpectedConfigHash);
        Assert.False(request.Spec.Labels.ContainsKey(ContainerLabels.ConfigHash));
        var node = (await fleet.Services.GetRequiredService<INodeRegistry>().GetAsync(Win, Ct))!;
        Assert.Equal(LaunchSpecBuilder.BuildLlamaCpp(embedder, node, tuning: false).ExecutionFingerprint, request.ExpectedExecutionFingerprint);
        Assert.Equal(embedder.MemoryBudgetGiB, request.Admission.BudgetGiB);
        Assert.Equal(embedder.HostMemoryBudgetGiB, request.Admission.HostBudgetGiB); // every llama.cpp launch carries one
        Assert.Equal(new Dictionary<string, double> { [LlamaCppFixtures.Rtx5060Ti] = P3Fleet.Rtx5060TiReserveGiB }, request.Admission.GpuReservesGiB);
        Assert.Contains("--embedding", request.Spec.Argv);
        Assert.Contains("--alias", request.Spec.Argv);
        // A process spec never reaches the DGX: it reports no runtime.llamacpp.process, and the launch is per node.
        Assert.DoesNotContain(fleet.DgxAgent.LaunchRequests, r => r.Spec.Kind == LaunchDelivery.Process);

        // The read-back is a llama.cpp definition, nothing reads "restart pending", and the launcher's config hash of the
        // running instance is the same fingerprint (so benchmarks and the tune page tell configurations apart).
        var status = await fleet.Services.GetRequiredService<IModelRuntime>().GetStatusAsync(embedder.Id, Ct);
        Assert.Equal(ModelState.Ready, status.State);
        Assert.False(status.RestartPending);
        var remote = fleet.Services.GetRequiredService<RemoteModelRuntime>();
        var launched = await remote.GetLaunchedDefinitionAsync(embedder.Id, Ct);
        Assert.Equal(RuntimeKind.LlamaCpp, launched!.Runtime);
        Assert.Equal(JsonSerializer.Serialize(embedder.LlamaCpp), JsonSerializer.Serialize(launched.LlamaCpp));
        Assert.Equal(request.ExpectedExecutionFingerprint, await remote.GetLaunchedConfigHashAsync(embedder.Id, Ct));

        // /v1 through the master's router and the agent's proxy: the served name is listed and chat answers buffered and
        // streamed, exactly as for a vLLM container.
        await P3Fleet.WaitUntilAsync(async () =>
        {
            using var models = await fleet.Http.GetAsync("/v1/models", Ct);
            return models.StatusCode == HttpStatusCode.OK &&
                (await models.Content.ReadAsStringAsync(Ct)).Contains(embedder.ServedName, StringComparison.Ordinal);
        }, $"{embedder.ServedName} never appeared in /v1/models");
        await fleet.AssertChatAsync(embedder.ServedName);

        // /v1/embeddings with "dimensions" goes through EmbeddingsCompat (MN-217, D119): the model server answers 384
        // values, the client asked for 8, and the answer is truncated and L2-renormalised as vLLM's Matryoshka answer is.
        using (var emulated = await fleet.Http.PostAsJsonAsync("/v1/embeddings", new { model = embedder.ServedName, input = "hi", dimensions = 8 }, Ct))
        {
            string body = await emulated.Content.ReadAsStringAsync(Ct);
            Assert.True(emulated.StatusCode == HttpStatusCode.OK, $"{(int)emulated.StatusCode} {body}");
            var vector = JsonNode.Parse(body)!["data"]![0]!["embedding"]!.AsArray();
            Assert.Equal(8, vector.Count);
            Assert.Equal(1.0, Math.Sqrt(vector.Sum(v => (double)v! * (double)v!)), 1e-6);
        }

        // Without the field the answer is the model's own, byte for byte.
        using (var native = await fleet.Http.PostAsJsonAsync("/v1/embeddings", new { model = embedder.ServedName, input = "hi" }, Ct))
        {
            Assert.Equal(HttpStatusCode.OK, native.StatusCode);
            Assert.Equal(384, JsonNode.Parse(await native.Content.ReadAsStringAsync(Ct))!["data"]![0]!["embedding"]!.AsArray().Count);
        }

        // Above the native size: 400, as vLLM refuses it.
        using (var tooMany = await fleet.Http.PostAsJsonAsync("/v1/embeddings", new { model = embedder.ServedName, input = "hi", dimensions = 4096 }, Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        }

        using (var stop = await fleet.Http.PostAsync($"/api/models/{embedder.Id}/stop", content: null, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }
    }

    [Fact]
    public async Task TheFitPreviewAndTheGgufRoute_OfTheP3Master_AnswerTheNodesGgufSummary()
    {
        await using var fleet = await P3Fleet.StartAsync(Ct);
        var embedder = P3Fleet.Embedder();
        await fleet.SaveModelAsync(embedder);

        // The fit route goes through the composed PlacementFit, which now carries the agent clients, so a node reporting
        // gguf.inspect answers the model file's summary and the preview adds the estimate (design-p3p4 §A.1.10). Before
        // MN-233 every construction site built it without agents and "estimate" was always absent.
        (HttpStatusCode status, string body) = await fleet.GetAsync($"/api/nodes/{Win}/fit?modelId={embedder.Id}");
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        var fit = JsonSerializer.Deserialize<NodeFitResult>(body, P3Fleet.Json)!;
        Assert.Equal(MemoryModel.Discrete, fit.Model);
        Assert.NotNull(fit.Estimate);
        Assert.True(fit.Estimate.BudgetGiB > 0, $"estimate {fit.Estimate.BudgetGiB}");
        Assert.Equal(0, fit.Estimate.HostGiB); // GpuLayers 999 ≥ the GGUF's block count: a full offload
        Assert.Contains("GGUF estimate", fit.Message, StringComparison.Ordinal);
        Assert.Contains(AgentRoutes.GgufInspect.Name, fleet.WindowsAgent.Requests);

        // The DGX reports P2's features only: no GGUF read is attempted and the preview has no estimate.
        int dgxRequests = fleet.DgxAgent.Requests.Count(r => r == AgentRoutes.GgufInspect.Name);
        var onDgx = await fleet.Services.GetRequiredService<PlacementFit>()
            .CheckAsync(Dgx, new NodeFitRequest { Definition = embedder with { Placement = new ModelPlacement(Dgx) } }, Ct);
        Assert.Null(onDgx!.Estimate);
        Assert.Equal(dgxRequests, fleet.DgxAgent.Requests.Count(r => r == AgentRoutes.GgufInspect.Name));

        // The per-node GGUF route of MN-229, mapped on this master.
        (HttpStatusCode ggufStatus, string ggufBody) =
            await fleet.GetAsync($"/api/model-folders/{P3Fleet.EmbedderFolder}/gguf?node={Win}&file={P3Fleet.EmbedderFile}");
        Assert.True(ggufStatus == HttpStatusCode.OK, $"{(int)ggufStatus} {ggufBody}");
        var summary = JsonSerializer.Deserialize<GgufSummary>(ggufBody, P3Fleet.Json)!;
        Assert.Equal((P3Fleet.EmbedderFolder, P3Fleet.EmbedderFile, P3Fleet.EmbedderBlocks), (summary.Folder, summary.File, summary.BlockCount));

        // The same route on the DGX: 501 not_supported (it does not report gguf.inspect).
        using var refused = await fleet.SendAsync("GET", $"/api/model-folders/{P3Fleet.EmbedderFolder}/gguf?node={Dgx}");
        Assert.Equal(HttpStatusCode.NotImplemented, refused.StatusCode);
        Assert.Equal(ErrorCodes.NotSupported, JsonNode.Parse(await refused.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ALlamaCppModelsKpis_ReachTheMaster_WithoutANaNInTheFleetSums()
    {
        await using var fleet = await P3Fleet.StartAsync(Ct);
        var embedder = P3Fleet.Embedder();
        await fleet.SaveModelAsync(embedder);
        await fleet.StartModelAsync(embedder.Id);

        // Two collector passes over two llama.cpp scrapes: the master takes ModelKpiCalculator's llama.cpp branch
        // (design-p3p4 §A.1.9) and fills the history the Dashboard and Monitoring read.
        var collector = fleet.Services.GetRequiredService<RemoteTelemetryCollector>();
        var history = fleet.Services.GetRequiredService<IMetricsHistory>();
        await P3Fleet.WaitUntilAsync(async () =>
        {
            fleet.WindowsAgent.TakeTelemetrySample();
            await collector.CollectOnceAsync(Ct);
            return history.Latest().ContainsKey(embedder.Id) && history.GetModel(embedder.Id, TimeSpan.FromMinutes(5)).Count >= 2;
        }, "the llama.cpp model's KPIs never reached the master's history");

        var kpis = history.Latest()[embedder.Id];
        Assert.Equal(1, kpis.Load.RunningRequests);
        Assert.Equal(2, kpis.Load.WaitingRequests);
        Assert.True(double.IsFinite(kpis.Load.GenerationTokensPerSecond), "generation tokens/s is not finite");
        Assert.True(double.IsFinite(kpis.Load.PromptTokensPerSecond), "prompt tokens/s is not finite");
        Assert.Equal(0.4, kpis.PrefixCacheHitRate!.Value, 1e-9); // Δcached 400 ÷ Δprompt 1000
        Assert.Equal(0.4, kpis.SpecDecodeAcceptanceRate!.Value, 1e-9); // Δaccepted 20 ÷ Δdraft 50
        // What llama.cpp has no source for: NaN (the value every page shows as "—"), never a number.
        Assert.True(double.IsNaN(kpis.Load.KvCacheUsage));
        Assert.True(double.IsNaN(kpis.RequestsPerMinute));
        Assert.True(double.IsNaN(kpis.PreemptionsPerMinute));
        Assert.True(double.IsNaN(kpis.ToolCallsPerMinute));
        Assert.Null(kpis.TimeToFirstToken);
        Assert.Empty(kpis.FinishedByReasonPerMinute);

        // The throughput a llama.cpp model does have is the real rate, not a placeholder (MN-234: these used to be
        // IsFinite checks, which CounterMath.PerSecond and PrometheusText's NaN filter make unfailable — the whole
        // mapping could be deleted and they stayed green). The fake's counters grow by a fixed step per sample.
        Assert.True(kpis.Load.GenerationTokensPerSecond > 0, "the llama.cpp generation rate never reached the master");
        Assert.True(kpis.Load.PromptTokensPerSecond > 0, "the llama.cpp prompt rate never reached the master");

        // That the fleet tiles sum only finite values is the Dashboard's own rule, proven where it lives
        // (Web/DashboardTests: KpiStrip_KeepsTheVllmRequestRate… and …GenerationRate…). A copy of FiniteSum here proved
        // nothing about Dashboard.razor, so it is gone (MN-234).

        // The master's own /metrics never writes a NaN into the exposition either.
        using (var metrics = await fleet.Http.GetAsync("/metrics", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
            string text = await metrics.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("NaN", text, StringComparison.OrdinalIgnoreCase);
        }

        // Model memory of a process instance is the figure measured at its start (design-p3p4 §A.1.9, D129 (1)), not a
        // GPU process row (WDDM reports none) and not the budget.
        Assert.True(ModelMemoryAttribution.IsProcessDelivery(embedder));
        await P3Fleet.WaitUntilAsync(async () =>
        {
            fleet.WindowsAgent.TakeTelemetrySample();
            await collector.CollectOnceAsync(Ct);
            var samples = history.GetHost(Win, TimeSpan.FromMinutes(5));
            return samples.Count > 0 && samples[^1].ModelMemoryGiB?.ContainsKey(embedder.Id) == true;
        }, "the process instance's memory never reached the host samples");
        var host = history.GetHost(Win, TimeSpan.FromMinutes(5));
        Assert.Equal(P3Fleet.MeasuredGpuGiB, host[^1].ModelMemoryGiB![embedder.Id], 1e-9);
        Assert.NotEqual(P3Fleet.MeasuredGpuGiB, embedder.MemoryBudgetGiB);
    }

}

/// <summary>
/// The P3 fleet of <see cref="P3CompositionTests"/>: the real master over a throwaway data path, a P1/P2 unified DGX and
/// the owner's Windows PC, both <see cref="FakeAgent"/>s under the master's own CA. The PC's capability report is the
/// frozen <c>NodeCapabilities.windows.json</c>: its two unequal GPUs, llama.cpp as a native process with
/// <c>b11476</c> verified and default, and no container runtime at all (Docker off, §A.4.3).
/// </summary>
internal sealed class P3Fleet : IAsyncDisposable
{
    public const string DgxId = "dgx";
    public const string WinId = LlamaCppFixtures.NodeId;

    /// <summary>The verified default <c>llama-server</c> version of the frozen fixture.</summary>
    public const string DefaultLlamaCppVersion = "b11476";

    /// <summary>The PC's per-GPU reserve on the 5060 Ti (the fixture node record's).</summary>
    public const double Rtx5060TiReserveGiB = 2.5;

    /// <summary>What the Windows agent reports as measured at the start of a process instance.</summary>
    public const double MeasuredGpuGiB = 7.25;

    /// <summary>The embedder's folder, file and layer count (the P3 fixture model).</summary>
    public const string EmbedderFolder = "Qwen3-Embedding-4B-GGUF";

    public const string EmbedderFile = "Qwen3-Embedding-4B-Q8_0.gguf";

    public const int EmbedderBlocks = 36;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly MasterFactory _master;
    private readonly CertificateAuthority _ca;
    private readonly string _dataDir;

    private P3Fleet(MasterFactory master, CertificateAuthority ca, string dataDir, FakeAgent dgx, FakeAgent win, HttpClient http, string bundleSha)
    {
        _master = master;
        _ca = ca;
        _dataDir = dataDir;
        DgxAgent = dgx;
        WindowsAgent = win;
        Http = http;
        WindowsBundleSha256 = bundleSha;
    }

    public FakeAgent DgxAgent { get; }

    public FakeAgent WindowsAgent { get; }

    public HttpClient Http { get; }

    public IServiceProvider Services => _master.Services;

    /// <summary>The SHA-256 of the win-x64 zip in the master's bundle folder.</summary>
    public string WindowsBundleSha256 { get; }

    /// <summary>OpenViking's embedder on the PC's 5060 Ti (the frozen P3 fixture), without an executable pin.</summary>
    public static ModelDefinition Embedder()
    {
        var model = LlamaCppFixtures.Embedder();
        return model with { AutoStart = false, LlamaCpp = model.LlamaCpp! with { ExecutableVersion = null } };
    }

    /// <summary>A vLLM model on the DGX, for the "nothing changed for P2" halves of the assertions.</summary>
    public static ModelDefinition VllmModel() => new()
    {
        Id = "qwen",
        ServedName = "qwen",
        ModelPath = "qwen",
        MemoryBudgetGiB = 20,
        MaxModelLen = 8192,
        MaxNumSeqs = 16,
        Placement = new ModelPlacement(DgxId),
    };

    public static async Task<P3Fleet> StartAsync(CancellationToken ct, bool approveWindows = true)
    {
        string root = Path.Combine(Path.GetTempPath(), "mirbadllm-p3-" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(root, "master-data");
        string bundleDir = Path.Combine(root, "agent-bundles");
        Directory.CreateDirectory(dataDir);
        string bundleSha = WindowsBundleFixtures.Write(bundleDir, version: "1.3.0");

        // An empty models.json before the host boots: nothing is seeded, so the boot auto-start launches nothing and
        // every launch a test sees is its own.
        using (var seed = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir })))
        {
            foreach (var existing in await seed.ListAsync(ct))
            {
                await seed.DeleteAsync(existing.Id, ct);
            }
        }

        var ca = CertificateAuthority.Create();
        var caps = LlamaCppFixtures.Capabilities();
        var dgx = await FakeAgent.StartAsync(new FakeAgentOptions { Ca = ca, NodeId = DgxId }, ct);
        var win = await FakeAgent.StartAsync(new FakeAgentOptions
        {
            Ca = ca,
            NodeId = WinId,
            NodeUid = LlamaCppFixtures.NodeUid,
            AgentVersion = caps.AgentVersion,
            Features = caps.Features,
            Os = caps.Os,
            Arch = caps.Arch,
            HostServicesManager = "scm",
            HostdAvailable = false,
            NoContainerRuntime = true,
            ExtraRuntimes = caps.Runtimes,
            DiscreteGpus = [.. caps.Gpus.OrderBy(g => g.Index).Select(g => new FakeDiscreteGpu(g.Uuid, g.Name ?? "GPU", g.MemoryTotalGiB ?? 16, 0.5, g.ComputeCapability ?? "12.0"))],
            TotalMemoryGiB = caps.Memory.TotalGiB,
            AvailableMemoryGiB = 100,
            ProcessMeasuredGpuGiB = MeasuredGpuGiB,
        }, ct);
        win.SetGgufSummary(EmbedderSummary());

        // Seeded before the host boots, as a real enrolment leaves nodes.json: the DGX approved, the PC enrolled and
        // waiting (its key in PendingCertSpkiSha256, so POST /api/nodes/{id}/approve is the real approval path).
        using (var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), TimeProvider.System))
        {
            await registry.UpsertAsync(new NodeRecord
            {
                Id = DgxId,
                Uid = dgx.NodeUid,
                Address = dgx.BaseAddress.AbsoluteUri,
                Approved = true,
                CertSpkiSha256 = dgx.CurrentSpki,
                CreatedAt = DateTimeOffset.UtcNow,
            }, ct);
            await registry.UpsertAsync(new NodeRecord
            {
                Id = WinId,
                Uid = win.NodeUid,
                DisplayName = "WIN-1",
                Address = win.BaseAddress.AbsoluteUri,
                Approved = approveWindows,
                CertSpkiSha256 = approveWindows ? win.CurrentSpki : null,
                PendingCertSpkiSha256 = approveWindows ? null : win.CurrentSpki,
                MemoryReserveGiB = 8,
                GpuReserveGiB = new Dictionary<string, double> { [LlamaCppFixtures.Rtx5060Ti] = Rtx5060TiReserveGiB },
                // Deliberately not seeded (MN-234): the record starts without capabilities, so every assertion about
                // what this node reports proves the master read them off the agent rather than reading back the
                // fixture this method just wrote. The DGX record is seeded the same way, without LastCapabilities.
                CreatedAt = DateTimeOffset.UtcNow,
            }, ct);
        }

        var master = new MasterFactory(dataDir, new Dictionary<string, string?>
        {
            ["MirbadLLM:Pki:CaCertPem"] = ca.CertificatePem,
            ["MirbadLLM:Pki:CaKeyPem"] = ca.ExportPrivateKeyPem(),
            ["MirbadLLM:Enrollment:Port"] = "0",
            ["MirbadLLM:Enrollment:BootstrapDirectory"] = Path.Combine(root, "bootstrap"),
            ["MirbadLLM:NodeApi:AgentBundleDirectory"] = bundleDir,
            // Long enough never to fire inside a test: the model health monitor's auto-start and the collector's loop are
            // driven explicitly here.
            ["MirbadLLM:HealthPollInterval"] = "00:10:00",
        });
        var http = master.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        var fleet = new P3Fleet(master, ca, dataDir, dgx, win, http, bundleSha);
        await fleet.ConnectAsync(approveWindows);
        return fleet;
    }

    /// <summary>The embedder's GGUF header as the agent reads it (enough for <c>LlamaCppMemoryEstimate</c>).</summary>
    public static GgufSummary EmbedderSummary() => new()
    {
        Folder = EmbedderFolder,
        File = EmbedderFile,
        GgufVersion = 3,
        Architecture = "qwen3",
        Name = "Qwen3-Embedding-4B",
        FileType = "Q8_0",
        ParameterCount = 4_020_000_000,
        BlockCount = EmbedderBlocks,
        ContextLength = 32768,
        EmbeddingLength = 2560,
        HeadCount = 32,
        HeadCountKv = 8,
        VocabSize = 151_669,
        PoolingType = "last",
        CausalAttention = true,
        AddEos = true,
        TensorBytes = 4_270_000_000,
        FileBytes = 4_280_000_000,
    };

    /// <summary>
    /// Waits until both agents are Online and their clients are known (the hosted sync and monitor's work), and until the
    /// master has read each approved node's capabilities into its registry record. The second half matters because
    /// MN-234 stopped seeding the Windows node's <c>LastCapabilities</c>: until the monitor's first pull lands, the
    /// record reports no runtime, so a llama.cpp placement is rightly refused and anything reading the node's features
    /// would be racing the fleet's own start-up rather than testing the master.
    /// </summary>
    private async Task ConnectAsync(bool approveWindows)
    {
        await Services.GetRequiredService<AgentClientFactory>().RefreshAsync(Ct);
        var cache = Services.GetRequiredService<INodeStatusCache>();
        await WaitUntilAsync(async () =>
        {
            await Services.GetRequiredService<AgentClientFactory>().RefreshAsync(Ct);
            foreach (string id in new[] { DgxId, WinId })
            {
                try
                {
                    await cache.RefreshNodeAsync(id, Ct);
                }
                catch (KeyNotFoundException)
                {
                    return false;
                }
            }

            return cache.GetNode(DgxId)?.State == NodeState.Online;
        }, "the fleet never came Online");

        var registry = Services.GetRequiredService<INodeRegistry>();
        string[] members = approveWindows ? [DgxId, WinId] : [DgxId];
        await WaitUntilAsync(
            async () =>
            {
                foreach (string id in members)
                {
                    if ((await registry.GetAsync(id, Ct))?.LastCapabilities is null)
                    {
                        return false;
                    }
                }

                return true;
            },
            "the master never read a fleet member's capabilities into its registry record");
    }

    /// <summary>Saves <paramref name="model"/> through the live registry, so the P2 placement validators run.</summary>
    public async Task SaveModelAsync(ModelDefinition model) =>
        await Services.GetRequiredService<IModelRegistry>().UpsertAsync(model, Ct);

    /// <summary>Starts <paramref name="id"/> through the admin API, retrying while the node's start slot or state says wait.</summary>
    public async Task StartModelAsync(string id)
    {
        string last = "";
        await WaitUntilAsync(async () =>
        {
            using var start = await Http.PostAsync($"/api/models/{id}/start", content: null, Ct);
            last = $"{(int)start.StatusCode} {await start.Content.ReadAsStringAsync(Ct)}";
            Assert.True(start.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable, $"start {id}: {last}");
            return start.StatusCode == HttpStatusCode.OK;
        }, $"start {id} kept answering 409/503: {last}");

        await Services.GetRequiredService<INodeStatusCache>().RefreshNodeAsync(WinId, Ct);
    }

    public async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        using var response = await Http.GetAsync(path, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    public Task<HttpResponseMessage> SendAsync(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsDelete(method))
        {
            request.Content = JsonContent.Create(new DownloadRequest("owner/Repo"), options: Json);
        }

        return SendAndDisposeAsync(request);
    }

    private async Task<HttpResponseMessage> SendAndDisposeAsync(HttpRequestMessage request)
    {
        using (request)
        {
            return await Http.SendAsync(request, Ct);
        }
    }

    /// <summary>A buffered and a streamed chat through the router and the agent's proxy.</summary>
    public async Task AssertChatAsync(string servedName)
    {
        using (var chat = await Http.PostAsJsonAsync("/v1/chat/completions",
            new { model = servedName, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4, stream = false }, Ct))
        {
            string body = await chat.Content.ReadAsStringAsync(Ct);
            Assert.True(chat.StatusCode == HttpStatusCode.OK, $"{servedName}: {(int)chat.StatusCode} {body}");
            Assert.True(JsonNode.Parse(body)!["choices"] is not null, body);
        }

        using (var stream = await Http.PostAsJsonAsync("/v1/chat/completions",
            new { model = servedName, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4, stream = true }, Ct))
        {
            string body = await stream.Content.ReadAsStringAsync(Ct);
            Assert.True(stream.StatusCode == HttpStatusCode.OK, $"{servedName} stream: {(int)stream.StatusCode} {body}");
            Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
            Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
        }
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, message);
            await Task.Delay(100, Ct);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await _master.DisposeAsync();
        await WindowsAgent.DisposeAsync();
        await DgxAgent.DisposeAsync();
        _ca.Dispose();
        TryDelete(Directory.GetParent(_dataDir)!.FullName);
    }

    private static void TryDelete(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary><c>Program</c> over <paramref name="dataDir"/> with the background services on and no admin password.</summary>
    private sealed class MasterFactory(string dataDir, Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Read by Program before the host builds (MasterBoot.Prepare).
            builder.UseSetting("MirbadLLM:DataPath", dataDir);
            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var all = new Dictionary<string, string?>
                {
                    ["MirbadLLM:DataPath"] = dataDir,
                    ["MirbadLLM:AdminPassword"] = null,
                    ["MirbadLLM:DockerEndpoint"] = "unix:///nonexistent/mirbadllm-tests/docker.sock",
                    ["MirbadLLM:HostAgentSocket"] = Path.Combine(dataDir, "no-hostd.sock"),
                    ["MirbadLLM:Backup:Directory"] = Path.Combine(dataDir, "backups"),
                };
                foreach ((string key, string? value) in settings)
                {
                    all[key] = value;
                }

                config.AddInMemoryCollection(all);
            });
        }
    }
}
