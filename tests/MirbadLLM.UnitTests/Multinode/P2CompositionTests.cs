using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Enrollment;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Benchmarking;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Pki;
using MirbadLLM.Orchestration.Recovery;
using MirbadLLM.Pki;
using MirbadLLM.Router;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Contract;

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-130: the P2 composition, driven through the real master (<c>MirbadLLM.Web</c>'s <c>Program</c>) and the real agent
/// (<c>MirbadLLM.Agent.Program.CreateAsync</c>) with only the outside world faked (Docker, hostd, <c>nvidia-smi</c>, the
/// model server).
/// <list type="bullet">
/// <item>A single DGX (unified GB10, bootstrap mode) behaves as P1: unified admission and <c>/status</c>, no P2 capability
/// flag, the P1 launch (all GPUs, the master's own config hash, no GPU labels), <c>/v1</c> buffered and streamed, the
/// P1 maintenance order (503 before 401, global windows), nothing stopped by the stale-instance sweep, no CA-rotation
/// files, recovery mode off.</item>
/// <item>A unified DGX plus a discrete PC turns the P2 features on for the PC only: per-GPU admission, the per-GPU
/// <c>/status</c> reader, <c>gpus.uuids</c>/<c>launch.env</c>/<c>admission.discrete</c>, node-scoped maintenance (automatic
/// mode), a launch by GPU UUID, and <c>ca_rotation_pending</c> while an open CA rotation has not reached a node.</item>
/// <item>Recovery mode composes the restricted host, installs a fleet backup and asks for the restart; the normal boot
/// that follows warns while the recovery option is still set.</item>
/// </list>
/// </summary>
public sealed class P2CompositionTests
{
    /// <summary>A GB10 as <c>nvidia-smi</c> reports it: no memory fields (placeholder UUID).</summary>
    private const string Gb10Uuid = "GPU-00000000-0000-0000-0000-0000000000b1";

    /// <summary>The discrete PC's two 16 GiB GPUs (placeholder UUIDs).</summary>
    private static readonly string[] PcGpus = ["GPU-00000000-0000-0000-0000-00000000c000", "GPU-00000000-0000-0000-0000-00000000c001"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASingleDgxP2MasterAndAgentBehaveExactlyAsP1()
    {
        int enrollPort = TestPorts.Reserve();
        int agentPort = TestPorts.Reserve();
        await using var node = await ContractNode.CreateAsync(enroll: false, Ct, nodeId: "dgx");
        node.NvidiaSmi = FakeNvidiaSmi.Gb10();
        string dataDir = Path.Combine(node.Root, "master-data");
        string bootstrapDir = Path.Combine(node.Root, "bootstrap");
        await SeedModelAsync(dataDir, "qwen", new ModelPlacement("dgx"));

        await using var master = new P2MasterFactory(dataDir, BootstrapSettings(enrollPort, agentPort, bootstrapDir, new()
        {
            ["MirbadLLM:Moves:SweepInterval"] = "00:00:00.200", // the stale-instance sweep runs often: it must stop nothing
        }));
        master.UseKestrel();
        master.StartServer();
        using var http = new HttpClient { BaseAddress = master.HttpAddress(), Timeout = TimeSpan.FromSeconds(60) };

        await using var agent = await RealAgentHarness.StartAsync(node, o =>
        {
            o.Port = agentPort;
            o.BootstrapDirectory = bootstrapDir;
            o.BootstrapWait = TimeSpan.FromSeconds(60);
            o.Addresses.Add("127.0.0.1");
            o.AllowTagPulls = true; // the P1 DGX setting
            o.MemoryModel = MemoryModel.Unified; // agent.dgx.json (D102); the probe's GB10 answer is the two-node test's
        }, Ct);

        var registry = master.Services.GetRequiredService<INodeRegistry>();
        var cache = master.Services.GetRequiredService<INodeStatusCache>();
        await WaitUntilAsync(() => Task.FromResult(cache.GetNode("dgx")?.State == NodeState.Online), "dgx never came Online");

        // The agent decided unified from the GB10 and composed exactly P1's admission and /status reader.
        Assert.Equal(MemoryModel.Unified, agent.App.Services.GetRequiredService<AgentMemoryModel>().Model);
        Assert.False(agent.App.Services.GetRequiredService<ReservationManager>().PerGpuAdmission);
        Assert.IsType<ProbeNodeMemoryReader>(agent.App.Services.GetRequiredService<INodeMemoryReader>());

        // Its capability report: unified, the GB10 sharing host memory, and not one P2 flag.
        await WaitUntilAsync(async () => (await registry.GetAsync("dgx", Ct))?.LastCapabilities is not null, "dgx's capabilities never reached the registry");
        NodeCapabilities caps = (await registry.GetAsync("dgx", Ct))!.LastCapabilities!;
        Assert.Equal(MemoryModel.Unified, caps.Memory.Model);
        var gpu = Assert.Single(caps.Gpus);
        Assert.Equal(Gb10Uuid, gpu.Uuid);
        Assert.True(gpu.SharesHostMemory);
        Assert.Empty(caps.Features.Intersect(AgentFeatures.P2));
        Assert.Contains(AgentFeatures.RuntimeVllmContainer, caps.Features);
        Assert.Contains(AgentFeatures.TelemetryV1, caps.Features);

        // /status memory as P1: unified, no per-GPU entries.
        await cache.RefreshNodeAsync("dgx", Ct);
        Assert.Equal(MemoryModel.Unified, cache.GetNode("dgx")!.Memory!.Model);
        Assert.Empty(cache.GetNode("dgx")!.Memory!.Gpus);
        // Online with no reason: none of P2's master-computed reasons (placed_gpu_missing, ca_rotation_pending) applies (MN-124).
        Assert.Equal(NodeState.Online, cache.GetNode("dgx")!.State);
        Assert.Empty(cache.GetNode("dgx")!.Reasons);

        // A start launches as P1: every GPU (Count -1, no device ids), no GPU read-back labels, and the config hash the
        // master computes for the definition (so it never reads "restart pending").
        await StartAsync(http, "qwen");
        JsonNode create = JsonNode.Parse(Assert.Single(node.Docker.CreateBodies, b => b.Contains("\"mirbadllm.model\":\"qwen\"", StringComparison.Ordinal)))!;
        JsonNode device = create["HostConfig"]!["DeviceRequests"]![0]!;
        Assert.Equal(-1, device["Count"]!.GetValue<int>());
        Assert.Null(device["DeviceIDs"]);
        JsonObject labels = create["Labels"]!.AsObject();
        Assert.False(labels.ContainsKey(ContainerLabels.GpuUuids));
        Assert.False(labels.ContainsKey(ContainerLabels.GpuTotalGiB));
        var qwen = (await master.Services.GetRequiredService<IModelRegistry>().GetAsync("qwen", Ct))!;
        Assert.Equal(await master.Services.GetRequiredService<RemoteModelRuntime>().ComputeConfigHashAsync(qwen, Ct), labels[ContainerLabels.ConfigHash]!.GetValue<string>());
        Assert.Equal(0, qwen.Placement!.Generation); // P1 saves keep generation 0

        // /v1 through the router and the agent's proxy, buffered and streamed.
        await WaitUntilAsync(async () =>
        {
            using var models = await http.GetAsync("/v1/models", Ct);
            return models.StatusCode == HttpStatusCode.OK && (await models.Content.ReadAsStringAsync(Ct)).Contains("\"qwen\"", StringComparison.Ordinal);
        }, "qwen never appeared in /v1/models");
        await AssertChatAsync(http, "qwen", apiKey: null);

        // A node that enrolled and waits for approval is no fleet member (W9 review MN-132, D102): from here on the DGX keeps
        // its single-node behaviour with it registered — global windows, no sweep stop, and a create without a placement
        // still lands on dgx (no 400 placement_required).
        // MN-233: it is a Windows PC reporting every P3/P4 flag, so this is a P3 master. Nothing below changes, and the
        // DGX's own capability report stays exactly P2's (the flags are per node, never fleet-wide).
        await registry.UpsertAsync(new NodeRecord
        {
            Id = "pending-pc",
            Uid = "uid-pending-pc",
            Address = "https://127.0.0.1:9",
            CreatedAt = DateTimeOffset.UtcNow,
            PendingCertSpkiSha256 = Convert.ToBase64String(new byte[32]),
            LastCapabilities = LlamaCppFixtures.Capabilities(),
        }, Ct);
        Assert.Empty((await registry.GetAsync("dgx", Ct))!.LastCapabilities!.Features.Intersect(AgentFeatures.P3Reported));
        // The per-node admin routes answer without ?node= while the PC waits, and refuse 409 invalid_state when it is
        // named: nothing is sent to a node that is not approved (MN-229, MN-230).
        using (var folders = await http.GetAsync("/api/model-folders", Ct))
        {
            Assert.True(folders.StatusCode == HttpStatusCode.OK, $"{(int)folders.StatusCode} {await folders.Content.ReadAsStringAsync(Ct)}");
        }

        using (var named = await http.GetAsync("/api/model-folders?node=pending-pc", Ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, named.StatusCode);
            Assert.Contains(ErrorCodes.InvalidState, await named.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        using (var created = await http.PostAsJsonAsync("/api/models", new
        {
            id = "qwen-new",
            servedName = "qwen-new",
            image = AgentContractSeed.Image,
            modelPath = AgentContractSeed.ModelFolder,
            memoryBudgetGiB = 1,
            maxModelLen = 8192,
            maxNumSeqs = 4,
        }, Ct))
        {
            Assert.True(created.StatusCode == HttpStatusCode.Created, $"{(int)created.StatusCode} {await created.Content.ReadAsStringAsync(Ct)}");
        }

        Assert.Equal("dgx", (await master.Services.GetRequiredService<IModelRegistry>().GetAsync("qwen-new", Ct))!.Placement?.NodeId);

        // Maintenance: automatic mode with one fleet member opens a global window, so /v1 answers in the P1 order: 503
        // maintenance before the API-key check, no node in the text; after the window, 401 without a key.
        var policy = master.Services.GetRequiredService<NodeScopedMaintenancePolicy>();
        Assert.Null(policy.Configured);
        Assert.False(await policy.IsOnAsync(Ct));
        Assert.True(master.Services.GetRequiredService<IOptions<RouterMaintenanceOptions>>().Value.NodeScoped);
        string key = (await master.Services.GetRequiredService<IApiKeyStore>().CreateAsync("p1-check", ApiKeyScope.Inference, Ct)).FullKey;
        var maintenance = master.Services.GetRequiredService<MaintenanceModeService>();
        var now = DateTimeOffset.UtcNow;
        maintenance.Enter(new MaintenanceState("p1-order", MaintenanceJobKinds.Benchmark, "qwen", now, now.AddMinutes(5), await policy.IsOnAsync(Ct) ? "dgx" : null));
        try
        {
            using var paused = await PostChatAsync(http, "qwen", apiKey: null, stream: false);
            string body = await paused.Content.ReadAsStringAsync(Ct);
            Assert.True(paused.StatusCode == HttpStatusCode.ServiceUnavailable, $"{(int)paused.StatusCode} {body}");
            Assert.Contains("maintenance", body, StringComparison.Ordinal);
            Assert.DoesNotContain("dgx", body, StringComparison.Ordinal);
            using var list = await http.GetAsync("/v1/models", Ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, list.StatusCode);
        }
        finally
        {
            maintenance.Exit("p1-order");
        }

        using (var unauthorized = await PostChatAsync(http, "qwen", apiKey: null, stream: false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        await AssertChatAsync(http, "qwen", key);

        // The stale-instance sweep (every 200 ms here) needs two nodes: on the single DGX it never stops anything (D92).
        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);
        Assert.Contains(node.Docker.ModelContainers, name => name.Contains("qwen", StringComparison.Ordinal));
        Assert.DoesNotContain(node.Docker.Calls, c => c.EndsWith("/stop", StringComparison.Ordinal) || c.StartsWith("DELETE /containers", StringComparison.Ordinal));

        // A model an ownership record holds refuses a plain stop (design §6.7), and nothing reaches the node.
        var ownership = master.Services.GetRequiredService<IOwnershipStore>();
        var hold = new OwnershipRecord { Id = "restore-test-qwen", Kind = OwnershipKind.Restore, ModelId = "qwen", Stage = "downloading", StartedAt = DateTimeOffset.UtcNow };
        Assert.True((await ownership.TryAcquireAsync(hold, Ct)).Acquired);
        using (var busy = await http.PostAsync("/api/models/qwen/stop", content: null, Ct))
        {
            string body = await busy.Content.ReadAsStringAsync(Ct);
            Assert.True(busy.StatusCode == HttpStatusCode.Conflict, $"{(int)busy.StatusCode} {body}");
            Assert.Contains(ErrorCodes.ModelBusy, body, StringComparison.Ordinal);
            Assert.Contains("restore-test-qwen", body, StringComparison.Ordinal);
        }

        Assert.Contains(node.Docker.ModelContainers, name => name.Contains("qwen", StringComparison.Ordinal));
        using (ModelWriteScope.AsOwner(hold.Id))
        {
            await HeldModelGuard.EnsureNotHeldAsync(ownership, "qwen", Ct); // the holder's own call passes
        }

        Assert.True(await ownership.ReleaseAsync(hold.Id, Ct));

        // A real benchmark through the composed job service (W9 review MN-132): its window is global, so /v1 answers in the
        // P1 order while it runs (the slow first token keeps the job running while /v1 is checked).
        var jobs = master.Services.GetRequiredService<ModelJobService>();
        node.Model.SetTimeToFirstTokenMs(1_500);
        try
        {
            using (var started = await http.PostAsync("/api/models/qwen/benchmark", content: null, Ct))
            {
                Assert.True(started.StatusCode == HttpStatusCode.Accepted, $"{(int)started.StatusCode} {await started.Content.ReadAsStringAsync(Ct)}");
            }

            var window = Assert.Single(maintenance.Active);
            Assert.True(window.IsGlobal, $"the benchmark's window names node {window.NodeId}");
            Assert.Equal("qwen", window.ModelId);
            using var during = await PostChatAsync(http, "qwen", apiKey: null, stream: false);
            string body = await during.Content.ReadAsStringAsync(Ct);
            Assert.True(during.StatusCode == HttpStatusCode.ServiceUnavailable, $"{(int)during.StatusCode} {body}");
            Assert.Contains("maintenance", body, StringComparison.Ordinal);
            Assert.DoesNotContain("dgx", body, StringComparison.Ordinal);
        }
        finally
        {
            node.Model.SetTimeToFirstTokenMs(50);
            jobs.CancelOnNode("dgx", JobKind.Benchmark);
            await jobs.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(60), Ct);
        }

        Assert.Empty(maintenance.Active);

        // The PKI is P1's: the CA on disk, no trust.json, no rotation files; the trust store holds the CA alone.
        string pki = Path.Combine(dataDir, MasterPkiOptions.DirectoryName);
        Assert.True(File.Exists(Path.Combine(pki, CaStore.CertificateFileName)));
        Assert.False(File.Exists(Path.Combine(pki, MasterTrustDocument.FileName)));
        Assert.Empty(Directory.EnumerateFiles(pki, "ca-next.*"));
        var trust = master.Services.GetRequiredService<MasterTrustStore>();
        Assert.Same(trust, master.Services.GetRequiredService<IMasterTrustAnchors>());
        Assert.Equal(Spki.Sha256(master.Services.GetRequiredService<CertificateAuthority>().Certificate), Assert.Single(trust.Document.Anchors).SpkiSha256);
        Assert.Contains(master.Services.GetServices<IHostedService>(), h => h is CaRotationService);
        Assert.Null(trust.PendingReason("dgx"));

        // Recovery mode is off and fleet recovery is composed but inactive; audit lines go to {DataPath}/audit.
        Assert.False(master.Services.GetRequiredService<MasterBoot>().IsRecovery);
        Assert.False(master.Services.GetRequiredService<MasterBoot>().RecoveryOptionStillSet);
        Assert.False(master.Services.GetRequiredService<FleetRecovery>().IsActive);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(dataDir, AuditLogFiles.DirectoryName), "*.jsonl"));

        // MN-233: approving the Windows PC makes this a two-node fleet, which is the one designed change — node-scoped
        // maintenance windows and ?node= on the per-node admin routes. The DGX's own answers above are what a P2 master
        // gives; nothing about its launches, capability report or /v1 behaviour depends on the PC existing.
        using (var approve = await http.PostAsync("/api/nodes/pending-pc/approve", content: null, Ct))
        {
            Assert.True(approve.StatusCode == HttpStatusCode.OK, $"approve: {(int)approve.StatusCode} {await approve.Content.ReadAsStringAsync(Ct)}");
        }

        Assert.True(await policy.IsOnAsync(Ct));
        using (var needsNode = await http.GetAsync("/api/model-folders", Ct))
        {
            Assert.Equal(HttpStatusCode.BadRequest, needsNode.StatusCode);
            Assert.Contains(ErrorCodes.NodeRequired, await needsNode.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        }

        // The DGX still answers every route and /v1, and still reports not one P3/P4 flag.
        using (var onDgx = await http.GetAsync("/api/model-folders?node=dgx", Ct))
        {
            Assert.True(onDgx.StatusCode == HttpStatusCode.OK, $"{(int)onDgx.StatusCode} {await onDgx.Content.ReadAsStringAsync(Ct)}");
        }

        await AssertChatAsync(http, "qwen", key);
        Assert.Empty((await registry.GetAsync("dgx", Ct))!.LastCapabilities!.Features.Intersect(AgentFeatures.P3Reported));

        using (var stop = await http.PostAsync("/api/models/qwen/stop", content: null, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }

        await WaitUntilAsync(() => Task.FromResult(node.Docker.ModelContainers.Count == 0), "the model container was not removed");
    }

    [Fact]
    public async Task AUnifiedDgxAndADiscretePcTurnTheP2FeaturesOnForThePcOnly()
    {
        int enrollPort = TestPorts.Reserve();
        int dgxPort = TestPorts.Reserve();
        int pcPort = TestPorts.Reserve();
        await using var dgxNode = await ContractNode.CreateAsync(enroll: false, Ct, nodeId: "dgx");
        dgxNode.NvidiaSmi = FakeNvidiaSmi.Gb10();
        await using var pcNode = await ContractNode.CreateAsync(enroll: false, Ct, nodeId: "pc");
        pcNode.NvidiaSmi = FakeNvidiaSmi.Discrete(PcGpus);
        string dataDir = Path.Combine(dgxNode.Root, "master-data");
        string bootstrapDir = Path.Combine(dgxNode.Root, "bootstrap");
        await SeedModelAsync(dataDir, "qwen", new ModelPlacement("dgx"));

        await using var master = new P2MasterFactory(dataDir, BootstrapSettings(enrollPort, dgxPort, bootstrapDir, new()));
        master.UseKestrel();
        master.StartServer();
        using var http = new HttpClient { BaseAddress = master.HttpAddress(), Timeout = TimeSpan.FromSeconds(60) };

        await using var dgx = await RealAgentHarness.StartAsync(dgxNode, o =>
        {
            o.Port = dgxPort;
            o.BootstrapDirectory = bootstrapDir;
            o.BootstrapWait = TimeSpan.FromSeconds(60);
            o.Addresses.Add("127.0.0.1");
            o.AllowTagPulls = true;
        }, Ct);

        // The PC joins with an operator token ("Add node") and is approved through the node API (MN-102).
        var registry = master.Services.GetRequiredService<INodeRegistry>();
        JoinTokenIssue issue = await registry.CreateJoinTokenAsync("pc", TimeSpan.FromHours(1), JoinTokenPurpose.Join, Ct);
        string caHash = Spki.ToJoinHash(master.Services.GetRequiredService<MasterCertificates>().Authority.SpkiSha256);
        using (var output = new StringWriter())
        using (var error = new StringWriter())
        {
            int exit = await JoinCommand.RunAsync(
                ["--enroll-url", $"https://127.0.0.1:{enrollPort}", "--token", issue.Token, "--ca-hash", caHash, "--node-id", "pc",
                 "--state-dir", pcNode.StateDirectory, "--address", $"https://127.0.0.1:{pcPort}"],
                output, error, ct: Ct);
            Assert.True(exit == JoinCommand.ExitOk, $"join exited {exit}: {output}{error}");
        }

        using (var approve = await http.PostAsync("/api/nodes/pc/approve", content: null, Ct))
        {
            Assert.True(approve.StatusCode == HttpStatusCode.OK, $"approve pc: {(int)approve.StatusCode} {await approve.Content.ReadAsStringAsync(Ct)}");
        }

        await using var pc = await RealAgentHarness.StartAsync(pcNode, o =>
        {
            o.Port = pcPort;
            o.Addresses.Add("127.0.0.1");
            o.AllowTagPulls = true;
        }, Ct);

        var cache = master.Services.GetRequiredService<INodeStatusCache>();
        await WaitUntilAsync(() => Task.FromResult(cache.GetNode("dgx")?.State == NodeState.Online), "dgx never came Online");
        await WaitUntilAsync(() => Task.FromResult(cache.GetNode("pc")?.State == NodeState.Online), "pc never came Online");

        // The PC's agent composed discrete admission (per GPU over its dedicated reader) and the per-GPU /status reader;
        // the DGX's stayed P1.
        Assert.Equal(MemoryModel.Discrete, pc.App.Services.GetRequiredService<AgentMemoryModel>().Model);
        Assert.True(pc.App.Services.GetRequiredService<ReservationManager>().PerGpuAdmission);
        Assert.IsType<DiscreteNodeMemoryReader>(pc.App.Services.GetRequiredService<INodeMemoryReader>());
        Assert.False(dgx.App.Services.GetRequiredService<ReservationManager>().PerGpuAdmission);

        // Capabilities: the PC reports its GPUs and every P2 flag its agent implements; the DGX none.
        await WaitUntilAsync(async () => (await registry.GetAsync("pc", Ct))?.LastCapabilities?.Memory.Model == MemoryModel.Discrete,
            "pc's capability report never reached the registry as discrete");
        NodeCapabilities pcCaps = (await registry.GetAsync("pc", Ct))!.LastCapabilities!;
        Assert.Equal(PcGpus, pcCaps.Gpus.OrderBy(g => g.Index).Select(g => g.Uuid));
        Assert.Contains(AgentFeatures.GpuSelectionUuids, pcCaps.Features);
        Assert.Contains(AgentFeatures.LaunchEnv, pcCaps.Features);
        Assert.Contains(AgentFeatures.AdmissionDiscrete, pcCaps.Features);
        Assert.DoesNotContain(AgentFeatures.ImagesDigestPinned, pcCaps.Features); // tag pulls allowed on this PC
        await WaitUntilAsync(async () => (await registry.GetAsync("dgx", Ct))?.LastCapabilities?.Memory.Model == MemoryModel.Unified,
            "dgx's capability report never reached the registry as unified");
        NodeCapabilities dgxCaps = (await registry.GetAsync("dgx", Ct))!.LastCapabilities!;
        Assert.Equal(MemoryModel.Unified, dgxCaps.Memory.Model);
        Assert.Empty(dgxCaps.Features.Intersect(AgentFeatures.P2));

        // /status of the PC carries both GPUs' memory.
        await WaitUntilAsync(async () =>
        {
            await cache.RefreshNodeAsync("pc", Ct);
            return cache.GetNode("pc")?.Memory is { Model: MemoryModel.Discrete, Gpus.Count: 2 };
        }, "pc's /status memory never showed two GPUs");

        // Two nodes: the automatic mode opens node-scoped windows.
        Assert.True(await master.Services.GetRequiredService<NodeScopedMaintenancePolicy>().IsOnAsync(Ct));

        // A model placed on the PC's GPU 1 by UUID launches with exactly that GPU and its read-back label.
        using (var created = await http.PostAsJsonAsync("/api/models", new
        {
            id = "pcm",
            servedName = "pcm",
            image = AgentContractSeed.Image,
            modelPath = AgentContractSeed.ModelFolder,
            memoryBudgetGiB = 1,
            maxModelLen = 8192,
            maxNumSeqs = 4,
            placement = new { nodeId = "pc", gpuUuids = new[] { PcGpus[1] } },
        }, Ct))
        {
            Assert.True(created.StatusCode == HttpStatusCode.Created, $"{(int)created.StatusCode} {await created.Content.ReadAsStringAsync(Ct)}");
        }

        await StartAsync(http, "pcm");
        JsonNode create = JsonNode.Parse(Assert.Single(pcNode.Docker.CreateBodies, b => b.Contains("\"mirbadllm.model\":\"pcm\"", StringComparison.Ordinal)))!;
        JsonNode device = create["HostConfig"]!["DeviceRequests"]![0]!;
        Assert.Equal([PcGpus[1]], device["DeviceIDs"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal(PcGpus[1], create["Labels"]![ContainerLabels.GpuUuids]!.GetValue<string>());
        Assert.DoesNotContain(dgxNode.Docker.CreateBodies, b => b.Contains("\"mirbadllm.model\":\"pcm\"", StringComparison.Ordinal));

        await WaitUntilAsync(async () =>
        {
            using var models = await http.GetAsync("/v1/models", Ct);
            return models.StatusCode == HttpStatusCode.OK && (await models.Content.ReadAsStringAsync(Ct)).Contains("\"pcm\"", StringComparison.Ordinal);
        }, "pcm never appeared in /v1/models");
        await AssertChatAsync(http, "pcm", apiKey: null);

        // An open CA rotation that has not reached a node makes it Degraded with ca_rotation_pending (MN-116).
        var trust = master.Services.GetRequiredService<MasterTrustStore>();
        string caSpki = Assert.Single(trust.Document.Anchors).SpkiSha256;
        string pcUid = (await registry.GetAsync("pc", Ct))!.Uid;
        string dgxUid = (await registry.GetAsync("dgx", Ct))!.Uid;
        await trust.UpdateAsync(d => d with
        {
            Rotation = new CaRotationRecord
            {
                RotationId = "rot-test",
                OldCaSpkiSha256 = caSpki,
                NewCaSpkiSha256 = caSpki,
                Stage = CaRotationStage.AgentsRotating,
                Nodes =
                [
                    new CaRotationNodeProgress { NodeId = "dgx", NodeUid = dgxUid, CertificateRotated = true },
                    new CaRotationNodeProgress { NodeId = "pc", NodeUid = pcUid },
                ],
            },
        }, Ct);
        await WaitUntilAsync(async () =>
        {
            await cache.RefreshNodeAsync("pc", Ct);
            return cache.GetNode("pc") is { State: NodeState.Degraded } view && view.Reasons.Any(r => r.Contains("CA rotation is open", StringComparison.Ordinal));
        }, "pc never read Degraded (ca_rotation_pending)");
        await cache.RefreshNodeAsync("dgx", Ct);
        Assert.Equal(NodeState.Online, cache.GetNode("dgx")!.State);
        await trust.UpdateAsync(d => d with { Rotation = null }, Ct);
        await WaitUntilAsync(async () =>
        {
            await cache.RefreshNodeAsync("pc", Ct);
            return cache.GetNode("pc")?.State == NodeState.Online;
        }, "pc stayed Degraded after the rotation record went");

        using (var stop = await http.PostAsync("/api/models/pcm/stop", content: null, Ct))
        {
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);
        }
    }

    [Fact]
    public async Task RecoveryModeServesTheRestrictedHostInstallsAFleetBackupAndWarnsWhileTheOptionStaysSet()
    {
        string root = Path.Combine(Path.GetTempPath(), "ss-p2-recovery-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            // A normal master (one not-yet-enrolled bootstrap node) makes the fleet backup, CA included.
            string source = Path.Combine(root, "source");
            BackupDocument backup;
            BackupSecrets secrets;
            string sourceCa;
            await using (var normal = new P2MasterFactory(source, RecoverySettings(root, "source")))
            {
                _ = normal.Services;
                backup = await normal.Services.GetRequiredService<BackupService>().CreateAsync("fleet-passphrase", Ct);
                secrets = BackupService.DecryptSecrets(backup, "fleet-passphrase")!;
                sourceCa = await File.ReadAllTextAsync(Path.Combine(source, MasterPkiOptions.DirectoryName, CaStore.CertificateFileName), Ct);
            }

            // An empty master started in recovery mode: the restricted host.
            string target = Path.Combine(root, "target");
            var recoverySettings = RecoverySettings(root, "target");
            recoverySettings["MirbadLLM:Recovery:Mode"] = "FleetRestore";
            await using (var recovery = new P2MasterFactory(target, recoverySettings))
            {
                using var http = recovery.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                var services = recovery.Services;
                Assert.True(services.GetRequiredService<MasterBoot>().IsRecovery);
                Assert.IsType<RecoveryMaintenanceWindow>(services.GetRequiredService<IMaintenanceMode>());
                Assert.DoesNotContain(services.GetServices<IHostedService>(),
                    h => h.GetType().Assembly.GetName().Name!.StartsWith("MirbadLLM.", StringComparison.Ordinal));
                var fleet = services.GetRequiredService<FleetRecovery>();
                Assert.True(fleet.IsActive);

                using (var v1 = await http.GetAsync("/v1/models", Ct))
                {
                    string body = await v1.Content.ReadAsStringAsync(Ct);
                    Assert.True(v1.StatusCode == HttpStatusCode.ServiceUnavailable, $"{(int)v1.StatusCode} {body}");
                    Assert.Contains("restoring", body, StringComparison.OrdinalIgnoreCase);
                }

                using (var api = await http.GetAsync("/api/models", Ct))
                {
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, api.StatusCode);
                    Assert.Contains(ErrorCodes.RecoveryMode, await api.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
                }

                // No agent bundles while restoring: a plain 503 (never the redirect a join command's curl would follow).
                foreach (string bundlePath in new[] { "/agent/bundles", "/agent/bundles/linux-x64.tar.gz" })
                {
                    using var bundle = await http.GetAsync(bundlePath, Ct);
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, bundle.StatusCode);
                    Assert.Contains(ErrorCodes.RecoveryMode, await bundle.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
                }

                using (var home = await http.GetAsync("/", Ct))
                {
                    Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
                    Assert.Equal("/backup", home.Headers.Location?.OriginalString);
                }

                using (var page = await http.GetAsync("/backup", Ct))
                {
                    Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                    Assert.Contains("recovery-mode-banner", await page.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
                }

                using (var health = await http.GetAsync("/healthz", Ct))
                {
                    Assert.Equal(HttpStatusCode.OK, health.StatusCode);
                }

                // The gate's asset rule (W9 review MN-132): a page whose id has a dot is a page, not a static file.
                foreach (string pagePath in new[] { "/models/qwen2.5-7b", "/nodes/pc.lan", "/models/x.css", "/monitoring" })
                {
                    using var gated = await http.GetAsync(pagePath, Ct);
                    Assert.True(gated.StatusCode == HttpStatusCode.Redirect, $"{pagePath}: {(int)gated.StatusCode}");
                    Assert.Equal("/backup", gated.Headers.Location?.OriginalString);
                }

                using (var metrics = await http.GetAsync("/metrics", Ct))
                {
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, metrics.StatusCode);
                }

                using (var css = await http.GetAsync("/app.css", Ct))
                {
                    Assert.True(css.StatusCode is not HttpStatusCode.Redirect and not HttpStatusCode.ServiceUnavailable, $"/app.css: {(int)css.StatusCode}");
                }

                // Nothing provisional was written: no seed model, no CA, no bootstrap node or bundle.
                Assert.Empty(await services.GetRequiredService<IModelRegistry>().ListAsync(Ct));
                Assert.False(File.Exists(Path.Combine(target, "models.json")));
                Assert.False(File.Exists(Path.Combine(target, MasterPkiOptions.DirectoryName, CaStore.CertificateFileName)));
                Assert.False(File.Exists(Path.Combine(target, NodeRegistryDocument.FileName)));
                Assert.False(Directory.Exists(Path.Combine(root, "target-bootstrap")) && Directory.EnumerateFiles(Path.Combine(root, "target-bootstrap")).Any());

                // The install writes the backup's master, CA, nodes and models, then asks the host to stop (restart policy).
                var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
                Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
                FleetRecoveryResult result = await fleet.InstallAsync(backup, secrets, ct: Ct);
                Assert.Equal(backup.Master!.MasterInstanceId, result.MasterInstanceId);
                Assert.True(lifetime.ApplicationStopping.IsCancellationRequested, "the install did not ask the host to restart");
            }

            Assert.Equal(sourceCa, await File.ReadAllTextAsync(Path.Combine(target, MasterPkiOptions.DirectoryName, CaStore.CertificateFileName), Ct));

            // The next boot is normal even though the option is still set, and the UI says to remove it.
            await using (var after = new P2MasterFactory(target, recoverySettings))
            {
                var boot = after.Services.GetRequiredService<MasterBoot>();
                Assert.False(boot.IsRecovery);
                Assert.True(boot.RecoveryOptionStillSet);
                Assert.Equal(backup.Master!.MasterInstanceId, after.Services.GetRequiredService<IMasterIdentity>().InstanceId);
                using var http = after.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                using var home = await http.GetAsync("/", Ct);
                string html = await home.Content.ReadAsStringAsync(Ct);
                Assert.True(home.StatusCode == HttpStatusCode.OK, $"{(int)home.StatusCode}");
                Assert.Contains("recovery-option-warning", html, StringComparison.Ordinal);
            }
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void AFailingAutomaticTakeoverBacksOffToTheLimit()
    {
        var options = new MirbadLLM.Orchestration.Fencing.TakeoverServiceOptions
        {
            RequestPollInterval = TimeSpan.FromSeconds(5),
            MaxRetryInterval = TimeSpan.FromMinutes(5),
        };
        Assert.Equal(TimeSpan.FromSeconds(5), MirbadLLM.Orchestration.Fencing.TakeoverService.RetryDelay(options, 1));
        Assert.Equal(TimeSpan.FromSeconds(10), MirbadLLM.Orchestration.Fencing.TakeoverService.RetryDelay(options, 2));
        Assert.Equal(TimeSpan.FromSeconds(40), MirbadLLM.Orchestration.Fencing.TakeoverService.RetryDelay(options, 4));
        Assert.Equal(TimeSpan.FromMinutes(5), MirbadLLM.Orchestration.Fencing.TakeoverService.RetryDelay(options, 7));
        Assert.Equal(TimeSpan.FromMinutes(5), MirbadLLM.Orchestration.Fencing.TakeoverService.RetryDelay(options, 1000));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    public void TheNodeScopedSwitchIsExplicitOrAutomatic(string? value, bool? expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [NodeScopedMaintenancePolicy.Key] = value }).Build();
        Assert.Equal(expected, NodeScopedMaintenancePolicy.ReadConfigured(configuration));
        Assert.Equal(RouterMaintenanceOptions.NodeScopedKey, NodeScopedMaintenancePolicy.Key);
    }

    [Fact]
    public void ANodeScopedSwitchThatIsNotABooleanIsRefused()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [NodeScopedMaintenancePolicy.Key] = "sometimes" }).Build();
        Assert.Throws<InvalidOperationException>(() => NodeScopedMaintenancePolicy.ReadConfigured(configuration));
    }

    private static Dictionary<string, string?> BootstrapSettings(int enrollPort, int agentPort, string bootstrapDir, Dictionary<string, string?> extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["MirbadLLM:Enrollment:Port"] = enrollPort.ToString(CultureInfo.InvariantCulture),
            ["MirbadLLM:Enrollment:BootstrapDirectory"] = bootstrapDir,
            ["MirbadLLM:Enrollment:BootstrapEnrollEndpoint"] = $"https://127.0.0.1:{enrollPort}",
            ["MirbadLLM:Enrollment:BootstrapAgentAddress"] = $"https://127.0.0.1:{agentPort}",
        };
        foreach ((string key, string? value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    private static Dictionary<string, string?> RecoverySettings(string root, string name) => new()
    {
        ["MirbadLLM:Enrollment:Port"] = "0",
        ["MirbadLLM:Enrollment:BootstrapDirectory"] = Path.Combine(root, name + "-bootstrap"),
    };

    private static async Task SeedModelAsync(string dataDir, string id, ModelPlacement placement)
    {
        Directory.CreateDirectory(dataDir);
        using var registry = new JsonModelRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }));
        foreach (var existing in await registry.ListAsync(Ct))
        {
            await registry.DeleteAsync(existing.Id, Ct);
        }

        await registry.UpsertAsync(new ModelDefinition
        {
            Id = id,
            ServedName = id,
            Image = AgentContractSeed.Image,
            ModelPath = AgentContractSeed.ModelFolder,
            MemoryBudgetGiB = 1,
            MaxModelLen = 8192,
            MaxNumSeqs = 16,
            Placement = placement,
        }, Ct);
    }

    /// <summary>Starts <paramref name="id"/> through the admin API, retrying while the node's start slot or state says wait.</summary>
    private static async Task StartAsync(HttpClient http, string id)
    {
        string last = "";
        await WaitUntilAsync(async () =>
        {
            using var start = await http.PostAsync($"/api/models/{id}/start", content: null, Ct);
            last = $"{(int)start.StatusCode} {await start.Content.ReadAsStringAsync(Ct)}";
            Assert.True(start.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable, $"start {id}: {last}");
            return start.StatusCode == HttpStatusCode.OK;
        }, $"start {id} kept answering 409/503");
    }

    private static Task<HttpResponseMessage> PostChatAsync(HttpClient http, string model, string? apiKey, bool stream)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new { model, messages = new[] { new { role = "user", content = "hi" } }, max_tokens = 4, stream }),
        };
        if (apiKey is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        }

        return SendAndDisposeAsync(http, request);
    }

    private static async Task<HttpResponseMessage> SendAndDisposeAsync(HttpClient http, HttpRequestMessage request)
    {
        using (request)
        {
            return await http.SendAsync(request, Ct);
        }
    }

    private static async Task AssertChatAsync(HttpClient http, string model, string? apiKey)
    {
        using (var chat = await PostChatAsync(http, model, apiKey, stream: false))
        {
            string body = await chat.Content.ReadAsStringAsync(Ct);
            Assert.True(chat.StatusCode == HttpStatusCode.OK, $"{model}: {(int)chat.StatusCode} {body}");
            using var json = JsonDocument.Parse(body);
            Assert.True(json.RootElement.TryGetProperty("choices", out _), body);
        }

        using (var stream = await PostChatAsync(http, model, apiKey, stream: true))
        {
            string body = await stream.Content.ReadAsStringAsync(Ct);
            Assert.True(stream.StatusCode == HttpStatusCode.OK, $"{model} stream: {(int)stream.StatusCode} {body}");
            Assert.Equal("text/event-stream", stream.Content.Headers.ContentType?.MediaType);
            Assert.Contains("data: [DONE]", body, StringComparison.Ordinal);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(60);
        while (!await condition())
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, message);
            await Task.Delay(100, Ct);
        }
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

    /// <summary><c>nvidia-smi</c> answering the inventory and telemetry queries for fixed GPUs.</summary>
    private sealed class FakeNvidiaSmi(string inventory, string telemetry) : INvidiaSmiRunner
    {
        /// <summary>A GB10: one GPU without memory fields (unified).</summary>
        public static FakeNvidiaSmi Gb10() => new(
            $"0, {Gb10Uuid}, NVIDIA GB10, [N/A], 12.1, 580.95.05\n",
            $"0, {Gb10Uuid}, 3, 41.25, 55, 2405, [N/A], [N/A], [N/A], NVIDIA GB10\n");

        /// <summary>Discrete GPUs of 16 GiB each, 0.5 GiB used.</summary>
        public static FakeNvidiaSmi Discrete(IReadOnlyList<string> uuids) => new(
            string.Concat(uuids.Select((u, i) => $"{i}, {u}, NVIDIA Test 16G, 16384, 12.0, 617.14\n")),
            string.Concat(uuids.Select((u, i) => $"{i}, {u}, 0, 10.00, 30, 210, 512, 16384, 15872, NVIDIA Test 16G\n")));

        public Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default) => Task.FromResult(arguments switch
        {
            NvidiaSmiGpuInventory.Arguments => new NvidiaSmiResult(true, 0, inventory, ""),
            NvidiaSmiGpuTelemetryProbe.Arguments => new NvidiaSmiResult(true, 0, telemetry, ""),
            _ => new NvidiaSmiResult(true, 0, "", ""),
        });
    }

    /// <summary>
    /// <c>Program</c> over <paramref name="dataDir"/>, with the background services on and no admin password; legacy host
    /// settings point nowhere. <see cref="WebApplicationFactory{TEntryPoint}.UseKestrel()"/> gives it the enroll listener.
    /// </summary>
    private sealed class P2MasterFactory(string dataDir, Dictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        public Uri HttpAddress()
        {
            var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
            string address = addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal));
            return new Uri(address.Replace("://[::]", "://127.0.0.1", StringComparison.Ordinal).Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("urls", "http://127.0.0.1:0");
            // Host settings reach Program's own reads before Build (the data path and the recovery option, read by
            // MasterBoot.Prepare); app configuration below is applied only once the host builds.
            builder.UseSetting("MirbadLLM:DataPath", dataDir);
            if (settings.TryGetValue("MirbadLLM:Recovery:Mode", out string? mode))
            {
                builder.UseSetting("MirbadLLM:Recovery:Mode", mode);
            }

            builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var all = new Dictionary<string, string?>
                {
                    ["urls"] = "http://127.0.0.1:0",
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
