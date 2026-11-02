using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Hosting;
using MirbadLLM.Agent.Proxy;
using MirbadLLM.Agent.Runtimes;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.NodeRuntime.Runtimes.Process;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.Web.Api;
using AgentProgram = MirbadLLM.Agent.Program;

#pragma warning disable CA1707 // underscore test names read better for these scenarios.

namespace MirbadLLM.UnitTests.Multinode.DevSetup;

/// <summary>
/// MN-236: the Windows development setup on one box (<see cref="WindowsDevSetupFleet"/>, design-p3p4 §A.4.1/§A.4.3) —
/// the real master, the existing Linux dev agent and a Windows dev agent with the <b>real</b> process driver over the
/// fake <c>llama-server</c>. Together these tests are plan-p3p4's development-setup acceptance: enrollment, approval, an
/// embedding model and a chat model through <c>/v1</c>, a benchmark, a tune apply, and an agent restart with adoption.
/// <para>The three W14 review notes (MN-234, D131) this task carries are proven here too: the generated
/// <c>agent.json</c> sets <c>Agent:CacheRoot</c> and holds one <c>Agent:Executables</c> entry per name with
/// <c>Agent:Docker:Enabled = false</c>, and the node composes two runtime drivers whose models both resolve their
/// upstream and their API key through the composed resolver and proxy.</para>
/// </summary>
public sealed class WindowsDevSetupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ================================================================ enrollment, approval, /v1

    [Fact]
    public async Task TheWindowsDevNode_Enrolls_IsApproved_AndServesAnEmbeddingAndAChatModelThroughV1()
    {
        await using var fleet = await WindowsDevSetupFleet.StartAsync();

        // Enrollment and approval: the node joined with the dev helper's token (nothing pre-seeded) and was approved
        // through POST /api/nodes/win-1/approve, which is what pinned its certificate key.
        var win = await fleet.NodeAsync(WindowsDevSetupFleet.WinId);
        Assert.True(win.Approved);
        Assert.NotNull(win.CertSpkiSha256);
        Assert.Null(win.PendingCertSpkiSha256);
        Assert.Equal(NodeState.Online, fleet.Services.GetRequiredService<INodeStatusCache>().GetNode(WindowsDevSetupFleet.WinId)?.State);

        // What the Windows dev node reports: a discrete node with the fixtures' two GPUs, llama.cpp as a native process
        // with the dev catalogue's versions (the newest verified and default), the SCM, and — because Docker is off —
        // not one container runtime, image or Docker feature. That is what makes the master treat it as the real PC.
        NodeCapabilities caps = win.LastCapabilities!;
        Assert.Equal(MemoryModel.Discrete, caps.Memory.Model);
        Assert.Equal(WindowsDevSetupFleet.WinGpus, caps.Gpus.OrderBy(g => g.Index).Select(g => g.Uuid));
        Assert.Contains(AgentFeatures.RuntimeLlamaCppProcess, caps.Features);
        Assert.Contains(AgentFeatures.GgufInspect, caps.Features);
        Assert.Contains(AgentFeatures.GpuSelectionUuids, caps.Features);
        Assert.Contains(AgentFeatures.AdmissionGpuBudgets, caps.Features);
        Assert.Contains(AgentFeatures.ServicesScm, caps.Features);
        Assert.Empty(caps.Features.Intersect(DockerAvailability.DockerFeatures, StringComparer.Ordinal));
        Assert.DoesNotContain(caps.Runtimes, r => r.Delivery == LaunchDelivery.Container);
        Assert.Empty(caps.Images);
        var llama = Assert.Single(caps.Runtimes, r => r.Kind == RuntimeKind.LlamaCpp && r.Delivery == LaunchDelivery.Process);
        Assert.Equal(WindowsDevSetupFleet.LlamaCppVersions.Order(), (llama.Versions ?? []).Select(v => v.Version).Order());
        Assert.All(llama.Versions!, v => Assert.True(v.Verified, $"{v.Version} is not verified"));
        Assert.Equal(WindowsDevSetupFleet.DefaultLlamaCppVersion, Assert.Single(llama.Versions!, v => v.Default).Version);

        // The existing Linux dev agent is untouched: P1's unified node, no P2 or P3 flag.
        NodeCapabilities dgx = (await fleet.NodeAsync(WindowsDevSetupFleet.DgxId)).LastCapabilities!;
        Assert.Equal(MemoryModel.Unified, dgx.Memory.Model);
        Assert.Empty(dgx.Gpus);
        Assert.Empty(dgx.Features.Intersect(AgentFeatures.P3Reported, StringComparer.Ordinal));

        // An embedding model and a chat model, both llama.cpp processes on the Windows node, on a GPU each.
        var embedder = Embedder();
        var chat = Chat();
        foreach (var model in new[] { embedder, chat })
        {
            await fleet.CreateModelAsync(model);
            await fleet.StartModelAsync(model.Id);
            await fleet.WaitServedAsync(model.ServedName);
        }

        // They really are native processes of the dev catalogue's default version, started by the process driver.
        var driver = fleet.Win.Services.GetRequiredService<ProcessRuntimeDriver>();
        foreach (var model in new[] { embedder, chat })
        {
            RuntimeStatus status = (await driver.GetAsync(model.Id, Ct))!;
            Assert.Equal(ModelState.Ready, status.State);
            Assert.Equal(LaunchDelivery.Process, status.Delivery);
            Assert.Equal(RuntimeKind.LlamaCpp, status.Runtime);
        }

        Assert.Equal(2, fleet.WinProcesses.Running.Count);
        Assert.All(fleet.WinProcesses.Running, p => Assert.Contains(WindowsDevSetupFleet.DefaultLlamaCppVersion, p.ImagePath, StringComparison.Ordinal));
        using (var store = new ProcessRecordStore(Path.Combine(fleet.WinStateDirectory, ProcessRecordsDocument.FileName)))
        {
            Assert.Equal(2, (await store.ListAsync(Ct)).Count);
        }

        // /v1/embeddings through the master's router and the agent's proxy: the fake llama-server's own vector, and the
        // Matryoshka emulation of MN-217 when "dimensions" asks for fewer.
        using (var native = await fleet.Http.PostAsJsonAsync("/v1/embeddings", new { model = embedder.ServedName, input = "hello" }, Ct))
        {
            string body = await native.Content.ReadAsStringAsync(Ct);
            Assert.True(native.StatusCode == HttpStatusCode.OK, $"{(int)native.StatusCode} {body}");
            Assert.NotEmpty(JsonNode.Parse(body)!["data"]![0]!["embedding"]!.AsArray());
        }

        using (var truncated = await fleet.Http.PostAsJsonAsync("/v1/embeddings", new { model = embedder.ServedName, input = "hello", dimensions = 8 }, Ct))
        {
            string body = await truncated.Content.ReadAsStringAsync(Ct);
            Assert.True(truncated.StatusCode == HttpStatusCode.OK, $"{(int)truncated.StatusCode} {body}");
            var vector = JsonNode.Parse(body)!["data"]![0]!["embedding"]!.AsArray();
            Assert.Equal(8, vector.Count);
            Assert.Equal(1.0, Math.Sqrt(vector.Sum(v => (double)v! * (double)v!)), 1e-6);
        }

        // /v1/chat/completions, buffered and streamed.
        await AssertChatAsync(fleet, chat.ServedName);

        // The two models really were started as an embedding server and a chat server: the argv the master built for
        // each reached the node, and only the embedder carries --embedding/--pooling.
        using (var store = new ProcessRecordStore(Path.Combine(fleet.WinStateDirectory, ProcessRecordsDocument.FileName)))
        {
            var records = (await store.ListAsync(Ct)).ToDictionary(r => r.ModelId, StringComparer.Ordinal);
            Assert.Contains("--embedding", records[embedder.Id].Spec.Argv);
            Assert.Equal(["--pooling", "last"], records[embedder.Id].Spec.Argv.SkipWhile(a => a != "--pooling").Take(2));
            Assert.DoesNotContain("--embedding", records[chat.Id].Spec.Argv);
            Assert.All(records.Values, r => Assert.Equal(WindowsDevSetupFleet.DefaultLlamaCppVersion, r.ExecutableVersion));
            Assert.All(records.Values, r => Assert.Equal(ExecutableNames.LlamaServer, r.ExecutableName));
        }

        foreach (var model in new[] { embedder, chat })
        {
            using var stop = await fleet.Http.PostAsync($"/api/models/{model.Id}/stop", content: null, Ct);
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }
    }

    // ================================================================ a benchmark and a tune apply

    [Fact]
    public async Task TheWindowsDevNode_RunsABenchmark_AndAppliesATuneCandidate_ThroughTheMaster()
    {
        await using var fleet = await WindowsDevSetupFleet.StartAsync();
        var chat = TunableChat();
        await fleet.CreateModelAsync(chat);
        await fleet.StartModelAsync(chat.Id);
        await fleet.WaitServedAsync(chat.ServedName);

        var jobs = fleet.Services.GetRequiredService<ModelJobService>();
        var store = fleet.Services.GetRequiredService<BenchmarkStore>();

        // ---- a benchmark. The job is held by the Windows node, not the bootstrap node (MN-111), and its load goes
        // through that node's agent proxy over mTLS — the proxy is what adds the per-instance LLAMA_API_KEY, so a
        // benchmark of a native llama-server only measures anything because that path works end to end.
        using (var started = await fleet.Http.PostAsync($"/api/models/{chat.Id}/benchmark", content: null, Ct))
        {
            string body = await started.Content.ReadAsStringAsync(Ct);
            Assert.True(started.StatusCode == HttpStatusCode.Accepted, $"benchmark {chat.Id}: {(int)started.StatusCode} {body}");
            Assert.Equal(WindowsDevSetupFleet.WinId, JsonNode.Parse(body)!["nodeId"]!.GetValue<string>());
        }

        await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromMinutes(4), Ct);
        Assert.Equal("Completed", jobs.LastOutcomeOnNode(WindowsDevSetupFleet.WinId)?.Status);
        BenchmarkRun benchmark = Assert.Single(await store.ListBenchmarksAsync(chat.Id, Ct));
        Assert.Null(benchmark.Error);
        Assert.False(benchmark.Cancelled);
        Assert.Equal(BenchmarkDataPaths.AgentLoopback, benchmark.Proxy);
        var result = Assert.IsType<BenchmarkResult>(benchmark.Result);
        Assert.NotEmpty(result.Levels);
        Assert.All(result.Levels, l => Assert.Equal(0, l.Errors));
        Assert.All(result.Levels, l => Assert.True(l.TtftP50 > 0, $"N={l.Concurrency} timed no request."));

        // Both halves of what a benchmark measures are real here. The client-side token rates come from the final
        // usage chunk of the stream (stream_options.include_usage), which the fake llama-server had to learn to send
        // for this run to mean anything: without it every rate is 0 and every tuning candidate below is rejected with
        // "Missing measurements for this preset" although the run itself succeeded. The server-side counters come from
        // llama-server's own /metrics, scraped through the agent.
        Assert.All(result.Levels, l => Assert.True(l.AggregateTokS > 0, $"N={l.Concurrency} measured no generation."));
        Assert.All(result.Levels, l => Assert.True(l.PerRequestTokS > 0, $"N={l.Concurrency} measured no per-request rate."));
        Assert.All(result.Levels, l => Assert.True(l.Server?.PredictedTokS > 0, $"N={l.Concurrency} generated nothing on the server."));
        Assert.All(result.Levels, l => Assert.True(l.Server?.PromptTokens > 0, $"N={l.Concurrency} prompted nothing on the server."));

        // The history is reachable over the admin API too, as the Benchmarks page reads it.
        (HttpStatusCode history, string historyBody) = await fleet.GetAsync($"/api/models/{chat.Id}/benchmarks");
        Assert.True(history == HttpStatusCode.OK, $"{(int)history} {historyBody}");
        Assert.Contains(benchmark.Id, historyBody, StringComparison.Ordinal);

        // ---- a tune, started without a goal: the API default is the classic Balanced, which a llama.cpp model remaps
        // to the InteractiveChat workload preset (MN-230, D130). Scored the classic way every candidate — the baseline
        // included — would be rejected with "No N=16 measurement", because llama.cpp's chat levels stop at its slots.
        using (var started = await fleet.Http.PostAsJsonAsync(
            $"/api/models/{chat.Id}/tune", new TuneRequest(Steps: [TuningDimension.KvCacheTypes]), Ct))
        {
            string body = await started.Content.ReadAsStringAsync(Ct);
            Assert.True(started.StatusCode == HttpStatusCode.Accepted, $"tune {chat.Id}: {(int)started.StatusCode} {body}");
            Assert.Equal(WindowsDevSetupFleet.WinId, JsonNode.Parse(body)!["nodeId"]!.GetValue<string>());
        }

        await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromMinutes(6), Ct);
        TuningRun tuning = Assert.Single(await store.ListTuningAsync(chat.Id, Ct));
        Assert.Equal(TuningRunStatus.Completed, tuning.Status);
        Assert.Null(tuning.Error);
        Assert.Equal(TuningGoal.InteractiveChat, tuning.Goal);
        Assert.DoesNotContain(tuning.Candidates, c => c.Reason?.Contains("No N=16", StringComparison.Ordinal) == true);
        Assert.Contains(tuning.Candidates, c => c.Outcome == CandidateOutcome.Accepted);
        Assert.NotNull(tuning.WinnerIndex);
        TuningCandidateResult winner = Assert.Single(tuning.Candidates, c => c.Index == tuning.WinnerIndex);
        Assert.Equal(CandidateOutcome.Accepted, winner.Outcome);

        // ---- the apply: 200 with the merged definition, and the registry holds it.
        using (var applied = await fleet.Http.PostAsync($"/api/models/{chat.Id}/tuning/{tuning.Id}/candidates/{winner.Index}/apply", content: null, Ct))
        {
            string body = await applied.Content.ReadAsStringAsync(Ct);
            Assert.True(applied.StatusCode == HttpStatusCode.OK, $"apply {winner.Index}: {(int)applied.StatusCode} {body}");
        }

        var saved = await fleet.Services.GetRequiredService<IModelRegistry>().GetAsync(chat.Id, Ct);
        Assert.NotNull(saved);
        Assert.NotNull(saved.LlamaCpp);
        LlamaCppSettings tuned = winner.Settings.LlamaCpp!;
        Assert.Equal(
            (tuned.Parallel, tuned.FlashAttention, tuned.CacheTypeK, tuned.CacheTypeV, tuned.BatchSize, tuned.MicroBatchSize),
            (saved.LlamaCpp.Parallel, saved.LlamaCpp.FlashAttention, saved.LlamaCpp.CacheTypeK, saved.LlamaCpp.CacheTypeV,
             saved.LlamaCpp.BatchSize, saved.LlamaCpp.MicroBatchSize));

        // Only the tuned knobs moved: the dimensions llama.cpp tuning never varies are the operator's (D118).
        Assert.Equal(chat.LlamaCpp!.ContextSize, saved.LlamaCpp.ContextSize);
        Assert.Equal(chat.LlamaCpp.GpuLayers, saved.LlamaCpp.GpuLayers);
        Assert.Equal(chat.LlamaCpp.ModelFile, saved.LlamaCpp.ModelFile);
        Assert.Equal(chat.MemoryBudgetGiB, saved.MemoryBudgetGiB);
    }

    // ================================================================ an agent restart with adoption

    [Fact]
    public async Task TheWindowsDevAgent_Restarts_AndAdoptsItsRunningLlamaServerProcess()
    {
        await using var fleet = await WindowsDevSetupFleet.StartAsync();
        var embedder = Embedder();
        await fleet.CreateModelAsync(embedder);
        await fleet.StartModelAsync(embedder.Id);
        await fleet.WaitServedAsync(embedder.ServedName);

        var running = Assert.Single(fleet.WinProcesses.Running);
        string instanceId = (await fleet.Win.Services.GetRequiredService<ProcessRuntimeDriver>().GetAsync(embedder.Id, Ct))!.InstanceId!;
        Assert.True(fleet.WinHost.OpenHandles > 0, "the first agent held no job handle for its child");

        // The restart an agent upgrade or a service restart performs: the agent stops, its job handles close without
        // killing the children (no kill-on-close, spike S4 / F2), and a fresh agent composes over the same state.
        await fleet.RestartWindowsAgentAsync();

        // The llama-server process never stopped: same PID, never killed, still the only one.
        var survived = Assert.Single(fleet.WinProcesses.Running);
        Assert.Equal(running.Pid, survived.Pid);
        Assert.DoesNotContain(running, fleet.WinProcesses.Killed);

        // And the new agent adopted it rather than relaunching it: the same instance id, as process delivery, Ready.
        var driver = fleet.Win.Services.GetRequiredService<ProcessRuntimeDriver>();
        await WindowsDevSetupFleet.WaitUntilAsync(
            async () => await driver.GetAsync(embedder.Id, Ct) is { State: ModelState.Ready, Delivery: LaunchDelivery.Process },
            "the restarted agent never reported the adopted instance Ready");
        Assert.Equal(instanceId, (await driver.GetAsync(embedder.Id, Ct))!.InstanceId);

        // The master sees it Ready again without an operator action, and /v1 answers through the new agent's proxy —
        // which means the adopted instance's per-instance API key came back from processes.json too.
        var cache = fleet.Services.GetRequiredService<INodeStatusCache>();
        await WindowsDevSetupFleet.WaitUntilAsync(
            async () =>
            {
                await cache.RefreshNodeAsync(WindowsDevSetupFleet.WinId, Ct);
                return cache.GetRuntime(embedder.Id) is { State: ModelState.Ready };
            },
            "the master never saw the adopted instance Ready again");

        await fleet.WaitServedAsync(embedder.ServedName);
        using var embeddings = await fleet.Http.PostAsJsonAsync("/v1/embeddings", new { model = embedder.ServedName, input = "after the restart" }, Ct);
        string answer = await embeddings.Content.ReadAsStringAsync(Ct);
        Assert.True(embeddings.StatusCode == HttpStatusCode.OK, $"{(int)embeddings.StatusCode} {answer}");
        Assert.NotEmpty(JsonNode.Parse(answer)!["data"]![0]!["embedding"]!.AsArray());
    }

    // ================================================================ the two composed runtime drivers (D131)

    [Fact]
    public async Task TheWindowsDevNode_ComposesTheFakeRuntimeNextToTheProcessDriver_AndResolvesBothUpstreamsAndKeys()
    {
        await using var fleet = await WindowsDevSetupFleet.StartAsync();

        // With Docker off the process driver is *added* to the fake runtime's driver, not substituted for it (only the
        // Docker-on path replaces). Both are in the enumerable the proxy and the resolver are built from.
        var drivers = fleet.Win.Services.GetServices<IRuntimeDriver>().ToList();
        Assert.Equal(2, drivers.Count);
        var processes = Assert.Single(drivers.OfType<ProcessRuntimeDriver>());
        var fake = Assert.Single(drivers, d => d is not ProcessRuntimeDriver);
        Assert.Same(fake, fleet.Win.Services.GetRequiredService<FakeRuntimeDriver>());
        Assert.Equal(LaunchDelivery.Process, processes.Delivery);
        Assert.Equal(LaunchDelivery.Container, fake.Delivery);

        // A model of each driver, running at the same time on the same node: the llama.cpp embedder through the master
        // (the process driver), and a fake vLLM model straight through the fake driver — the master cannot place one
        // there, because a Docker-off node reports no container runtime at all, which is exactly the asymmetry that
        // makes the composed fold necessary rather than decorative.
        var embedder = Embedder();
        await fleet.CreateModelAsync(embedder);
        await fleet.StartModelAsync(embedder.Id);
        await fleet.WaitServedAsync(embedder.ServedName);

        const string FakeModelId = "fake-win-dev";
        var fakeStatus = await LaunchOnFakeRuntimeAsync(fleet, FakeModelId);
        Assert.Equal(ModelState.Ready, fakeStatus.State);

        // The composed resolver answers for both, each from its own driver and on its own loopback port, and asks the
        // owner it remembered (before MN-232 it took a single IRuntimeDriver and the process model resolved as "not
        // running"). Each driver answers only for its own model.
        var resolver = Assert.IsType<RuntimeDriverUpstreamResolver>(fleet.Win.Services.GetRequiredService<IProxyUpstreamResolver>());
        Uri? processUpstream = await resolver.ResolveAsync(embedder.Id, Ct);
        Uri? fakeUpstream = await resolver.ResolveAsync(FakeModelId, Ct);
        Assert.NotNull(processUpstream);
        Assert.NotNull(fakeUpstream);
        Assert.NotEqual(processUpstream, fakeUpstream);
        Assert.Equal(processUpstream, await processes.GetUpstreamAsync(embedder.Id, Ct));
        Assert.Equal(fakeUpstream, await fake.GetUpstreamAsync(FakeModelId, Ct));
        Assert.Null(await processes.GetUpstreamAsync(FakeModelId, Ct));
        Assert.Null(await fake.GetUpstreamAsync(embedder.Id, Ct));
        Assert.Null(await resolver.ResolveAsync("not-running", Ct));

        // And the API key: the proxy's key walk finds the process instance's per-instance LLAMA_API_KEY (the one the
        // protector sealed into processes.json) and, for the fake driver's model, no key at all — a container reached on
        // loopback needs none, and the walk must not hand it the other model's key.
        var proxy = fleet.Win.Services.GetRequiredService<AgentProxy>();
        string? processKey = await UpstreamApiKeyAsync(proxy, embedder.Id);
        Assert.False(string.IsNullOrEmpty(processKey));
        Assert.Null(await UpstreamApiKeyAsync(proxy, FakeModelId));
        using (var store = new ProcessRecordStore(Path.Combine(fleet.WinStateDirectory, ProcessRecordsDocument.FileName)))
        {
            ProcessRecord record = Assert.Single(await store.ListAsync(Ct));
            Assert.Equal(embedder.Id, record.ModelId);
            Assert.DoesNotContain(processKey!, record.ProtectedApiKey, StringComparison.Ordinal); // sealed, never stored in the clear
            Assert.Equal(processKey, fleet.WinKeys.Unprotect(record.ProtectedApiKey));
        }

        // The key the walk found is the live one: the fake llama-server refuses its own port without it and answers
        // with it, so the process model's /v1 traffic only gets through because the composed proxy sends that key.
        using (var served = await fleet.Http.PostAsJsonAsync("/v1/embeddings", new { model = embedder.ServedName, input = "hello" }, Ct))
        {
            string body = await served.Content.ReadAsStringAsync(Ct);
            Assert.True(served.StatusCode == HttpStatusCode.OK, $"{(int)served.StatusCode} {body}");
        }

        using var direct = new HttpClient { BaseAddress = processUpstream, Timeout = TimeSpan.FromSeconds(30) };
        using (var unauthorized = await direct.GetAsync("/v1/models", Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", processKey);
            using var authorized = await direct.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        }

        // The fake driver's model is open on loopback, as a container is: nothing was handed a key it does not want.
        using var fakeDirect = new HttpClient { BaseAddress = fakeUpstream, Timeout = TimeSpan.FromSeconds(30) };
        using (var open = await fakeDirect.GetAsync("/v1/models", Ct))
        {
            Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        }

        await fake.StopAsync(FakeModelId, NoStages.Instance, Ct);
    }

    // ================================================================ the generated agent.json (D131)

    [Fact]
    public void TheGeneratedWindowsDevConfig_KeepsDockerOff_SetsCacheRoot_AndHoldsOneExecutablesEntryPerName()
    {
        // The generator of WindowsDevSetupFleet is what docs/multinode/dev-setup-windows.md documents; this is its shape.
        using var catalogue = LlamaCppVersionFolder.Create(WindowsDevSetupFleet.DefaultLlamaCppVersion);
        using var older = LlamaCppVersionFolder.Create("b11410", catalogue.Root);
        var options = new AgentOptions
        {
            NodeId = WindowsDevSetupFleet.WinId,
            StateDirectory = Path.Combine(catalogue.Root, "state"),
            ListenAddress = "127.0.0.1",
            ModelsRoot = Path.Combine(catalogue.Root, "models"),
            ModelsHostPath = Path.Combine(catalogue.Root, "models"),
            CacheRoot = Path.Combine(catalogue.Root, "cache"),
            CacheHostPath = Path.Combine(catalogue.Root, "cache"),
            HostdExpected = false,
        };
        options.Docker.Enabled = false;
        options.Executables.Add(LlamaCppVersionFolder.Options(WindowsDevSetupFleet.DefaultLlamaCppVersion, catalogue, older));

        // Docker off, a cache root, and one entry holding both versions with the pinned default.
        options.Validate();
        Assert.False(options.Docker.Enabled);
        Assert.False(string.IsNullOrWhiteSpace(options.CacheRoot));
        ExecutableOptions entry = Assert.Single(options.Executables);
        Assert.Equal(ExecutableNames.LlamaServer, entry.Name);
        Assert.Equal(WindowsDevSetupFleet.DefaultLlamaCppVersion, entry.Default);
        Assert.Equal(2, entry.Versions.Count);

        // Two entries for one name are refused: each would derive its own default version, so the node's default
        // llama-server would follow JSON ordering (MN-234).
        var split = new AgentOptions
        {
            NodeId = WindowsDevSetupFleet.WinId,
            StateDirectory = options.StateDirectory,
            ListenAddress = "127.0.0.1",
            ModelsRoot = options.ModelsRoot,
            ModelsHostPath = options.ModelsHostPath,
            CacheRoot = options.CacheRoot,
            CacheHostPath = options.CacheHostPath,
            HostdExpected = false,
        };
        split.Docker.Enabled = false;
        split.Executables.Add(LlamaCppVersionFolder.Options(WindowsDevSetupFleet.DefaultLlamaCppVersion, catalogue));
        split.Executables.Add(LlamaCppVersionFolder.Options("b11410", older));
        var twice = Assert.Throws<ArgumentException>(split.Validate);
        Assert.Contains("put every version of an executable in one entry", twice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowsDevConfigWithoutACacheRoot_RefusesToStartWithAnAgentStartupException()
    {
        // The one place Agent:CacheRoot can be forgotten is a hand-written dev config: install-agent.ps1 always writes
        // it and agent.windows.example.json has it. The refusal must be an AgentStartupException (the only shape Main
        // catches), so the operator reads the reason and the Event Log carries it instead of the service dying
        // unhandled, which under the SCM looks like error 1053 (MN-234, design-p3p4 §A.4.3).
        string root = Path.Combine(Path.GetTempPath(), "ss-win-dev-nocache-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var options = new AgentOptions
            {
                NodeId = WindowsDevSetupFleet.WinId,
                StateDirectory = Path.Combine(root, "state"),
                ListenAddress = "127.0.0.1",
                Port = 0,
                ModelsRoot = Path.Combine(root, "models"),
                ModelsHostPath = Path.Combine(root, "models"),
                HostdExpected = false,
            };
            options.Docker.Enabled = false;
            Assert.True(string.IsNullOrWhiteSpace(options.CacheRoot));

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                EnvironmentName = Environments.Development,
            });
            builder.Logging.ClearProviders();
            var ex = await Assert.ThrowsAsync<AgentStartupException>(
                () => AgentProgram.CreateAsync(builder, options, startupLoggers: null, configureServices: null, Ct));
            Assert.Contains("Agent:CacheRoot is required on a node that runs native processes", ex.Message, StringComparison.Ordinal);

            // The refusal comes before enrollment, so a dev box with a half-written config has nothing to clean up.
            Assert.False(Directory.Exists(Path.Combine(root, "state")) && Directory.EnumerateFiles(Path.Combine(root, "state")).Any());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // ================================================================ helpers

    /// <summary>OpenViking's embedder (the P3 fixture) on the dev node's GPU 1, started by hand.</summary>
    private static ModelDefinition Embedder()
    {
        var model = LlamaCppFixtures.Embedder();
        return model with
        {
            AutoStart = false,
            Sources = [],
            Placement = new ModelPlacement(WindowsDevSetupFleet.WinId, [LlamaCppFixtures.Rtx5060Ti], [1]),
            LlamaCpp = model.LlamaCpp! with { ExecutableVersion = null },
        };
    }

    /// <summary>
    /// The chat model the benchmark and the tune run on: the fixture with an explicit <c>f16</c> KV cache and flash
    /// attention, so the <see cref="TuningDimension.KvCacheTypes"/> step has exactly one candidate (<c>q8_0</c>) and the
    /// dev setup's tune is the smallest real one.
    /// </summary>
    private static ModelDefinition TunableChat()
    {
        var model = Chat();
        return model with { LlamaCpp = model.LlamaCpp! with { FlashAttention = true, CacheTypeK = "f16", CacheTypeV = "f16" } };
    }

    /// <summary>The fixtures' llama.cpp chat model on the dev node's GPU 0.</summary>
    private static ModelDefinition Chat()
    {
        var model = LlamaCppFixtures.Chat();
        return model with
        {
            AutoStart = false,
            Sources = [],
            Placement = new ModelPlacement(WindowsDevSetupFleet.WinId, [LlamaCppFixtures.Rtx5070], [0]),
            LlamaCpp = model.LlamaCpp! with { ExecutableVersion = null },
        };
    }

    private static async Task AssertChatAsync(WindowsDevSetupFleet fleet, string servedName)
    {
        using (var chat = await fleet.Http.PostAsJsonAsync("/v1/chat/completions",
            new { model = servedName, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4 }, Ct))
        {
            string body = await chat.Content.ReadAsStringAsync(Ct);
            Assert.True(chat.StatusCode == HttpStatusCode.OK, $"{servedName}: {(int)chat.StatusCode} {body}");
            Assert.False(string.IsNullOrEmpty(
                JsonNode.Parse(body)!["choices"]![0]!["message"]!["content"]!.GetValue<string>()));
        }

        using (var stream = await fleet.Http.PostAsJsonAsync("/v1/chat/completions",
            new { model = servedName, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4, stream = true }, Ct))
        {
            string body = await stream.Content.ReadAsStringAsync(Ct);
            Assert.True(stream.StatusCode == HttpStatusCode.OK, $"{servedName} stream: {(int)stream.StatusCode} {body}");
            Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
            Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
        }
    }

    /// <summary>A fake vLLM instance on the dev node's second driver, launched through the driver itself.</summary>
    private static async Task<RuntimeStatus> LaunchOnFakeRuntimeAsync(WindowsDevSetupFleet fleet, string modelId)
    {
        var model = new ModelDefinition
        {
            Id = modelId,
            ServedName = modelId,
            ModelPath = "fake-model",
            MemoryBudgetGiB = 2,
            MaxModelLen = 8192,
            MaxNumSeqs = 4,
            AutoStart = false,
            Placement = new ModelPlacement(WindowsDevSetupFleet.WinId, [LlamaCppFixtures.Rtx5070], [0]),
        };
        var node = await fleet.NodeAsync(WindowsDevSetupFleet.WinId);
        BuiltLaunchSpec built = LaunchSpecBuilder.Build(model, node, "local/fake-vllm:dev", totalGiB: 32, tuning: false);
        var driver = fleet.Win.Services.GetRequiredService<FakeRuntimeDriver>();
        var result = await driver.LaunchAsync(
            new RuntimeLaunch
            {
                ModelId = modelId,
                OperationId = "op-dev-fake-1",
                Mode = LaunchMode.EnsureStarted,
                Spec = built.Spec,
                HostPort = TestPorts.Reserve(),
            },
            NoStages.Instance,
            Ct);
        Assert.NotNull(result.Instance);
        await WindowsDevSetupFleet.WaitUntilAsync(
            async () => (await driver.GetAsync(modelId, Ct))?.State == ModelState.Ready,
            $"{modelId} never became Ready on the fake runtime",
            seconds: 30);
        return (await driver.GetAsync(modelId, Ct))!;
    }

    private static Task<string?> UpstreamApiKeyAsync(AgentProxy proxy, string modelId) =>
        (Task<string?>)typeof(AgentProxy)
            .GetMethod("UpstreamApiKeyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(proxy, [modelId, CancellationToken.None])!;

    private sealed class NoStages : IRuntimeStageSink
    {
        public static readonly NoStages Instance = new();

        public Task BeforeStepAsync(string stage, CancellationToken ct) => Task.CompletedTask;

        public Task StageReachedAsync(string stage, OperationProgress? progress, CancellationToken ct) => Task.CompletedTask;
    }
}
