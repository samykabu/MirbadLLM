using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, GPU placement on a discrete PC (design §5.1, §4.8/§6.1, §13.6 "GPU renumbering", "discrete admission"; D95,
/// D96): the real master and real agents, a placement by UUID through <c>POST /api/models</c> stored with its UUID and
/// generation and launched on exactly that card, renumbering that keeps the card, a removed card refused with
/// <c>gpu_missing</c>, tensor parallelism on equal cards only, and per-GPU admission naming the refusing GPU, while the
/// DGX in the same fleet keeps exactly P1.
/// </summary>
public sealed class PlacementExitTests
{
    private static readonly string Card0 = PcGpus[0];
    private static readonly string Card1 = PcGpus[1];

    [Fact]
    public async Task APlacementByUuidIsStoredWithTheCardAndLaunchesOnExactlyThatCardNotOnTheDgx()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        await P2Checks.WaitForDiscreteReportAsync(fleet, "pc", 2);

        // Created on card 0 (a create is generation 0), then the operator selects card 1 (a GPU change: generation 1).
        await fleet.Master.CreateModelAsync(ModelBody("pcm", "pc", [Card0], budgetGiB: 2));
        var created = P2Checks.StoredModel(fleet, "pcm");
        Assert.Equal(new ModelPlacement("pc", [Card0], [0], Generation: 0), created.Placement);
        Assert.Equal(0, created.Placement!.Generation);
        var (put, putBody) = await SendAsync(fleet.Master.Admin.PutAsJsonAsync("/api/models/pcm", ModelBody("pcm", "pc", [Card1], budgetGiB: 2), Ct));
        Assert.True(put == HttpStatusCode.OK, $"PUT pcm: {(int)put} {putBody}");

        // models.json holds the UUID (the identity), the index of the moment (display) and the new generation.
        var stored = P2Checks.StoredModel(fleet, "pcm");
        Assert.Equal("pc", stored.Placement!.NodeId);
        Assert.Equal([Card1], stored.Placement.GpuUuids);
        Assert.Equal([1], stored.Placement.GpuIndices);
        Assert.Equal(1, stored.Placement.Generation);

        await fleet.Master.StartModelAsync("pcm");

        // Exactly that card: Docker's device request names it by UUID, the read-back labels carry it and its capacity, a
        // single card means no tensor parallelism; nothing of it reached the DGX.
        JsonNode create = JsonNode.Parse(Assert.Single(pc.CreateBodiesOf("pcm")))!;
        Assert.Equal([Card1], P2Checks.DeviceIds(create));
        Assert.Equal(Card1, P2Checks.Label(create, ContainerLabels.GpuUuids));
        Assert.Equal("16", P2Checks.Label(create, ContainerLabels.GpuTotalGiB));
        Assert.DoesNotContain(VllmArgsFlag, P2Checks.Cmd(create));
        Assert.Empty(fleet.Dgx.CreateBodiesOf("pcm"));
        Assert.Null(await fleet.Master.Client("dgx").GetRuntimeAsync("pcm", Ct));

        var launch = (await fleet.Master.Client("pc").GetLaunchConfigAsync("pcm", Ct))!;
        Assert.Equal([new GpuRef(1, Card1)], launch.Gpus);
        Assert.False((await P2Checks.RuntimeAsync(fleet).GetStatusAsync("pcm", Ct)).RestartPending);

        // The DGX in the same fleet: P1 capabilities and admission, and a P1 launch (every GPU, no GPU labels).
        await P2Checks.AssertDgxIsP1Async(fleet);
        await P2Checks.AssertDgxLaunchesAsP1Async(fleet, "dgxm");
    }

    [Fact]
    public async Task RenumberedCardsKeepTheSavedCardAcrossAnAgentAndAModelRestart()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        await P2Checks.WaitForDiscreteReportAsync(fleet, "pc", 2);
        await fleet.Master.CreateModelAsync(ModelBody("pcm", "pc", [Card1], budgetGiB: 2));
        await fleet.Master.StartModelAsync("pcm");
        Assert.Equal([new GpuRef(1, Card1)], (await fleet.Master.Client("pc").GetLaunchConfigAsync("pcm", Ct))!.Gpus);

        // A driver update swaps the order: card 1 is index 0 now. The agent restarts (the container keeps running).
        pc.Smi.Renumber(Card1, Card0);
        await pc.StopAsync();
        await pc.StartAsync();
        await ExitEnv.UntilAsync(async () =>
            fleet.Master.Cache.GetNode("pc") is { IsServing: true } &&
            (await fleet.Master.Nodes.GetAsync("pc", Ct))?.LastCapabilities?.Gpus.FirstOrDefault(g => g.Uuid == Card1)?.Index == 0,
            "pc reconnected with card 1 at index 0");

        // The adopted instance still runs on its card, now reported at index 0, and is not restart pending.
        Assert.Equal([new GpuRef(0, Card1)], (await fleet.Master.Client("pc").GetLaunchConfigAsync("pcm", Ct))!.Gpus);
        Assert.False((await P2Checks.RuntimeAsync(fleet).GetStatusAsync("pcm", Ct)).RestartPending);

        await P2Checks.RestartModelAsync(fleet, "pcm");
        await ExitEnv.UntilAsync(() => pc.CreateBodiesOf("pcm").Count == 2, "pcm recreated by the restart");
        await ExitEnv.UntilAsync(async () => (await P2Checks.RuntimeAsync(fleet).GetStatusAsync("pcm", Ct)).State == ModelState.Ready, "pcm Ready after the restart");

        // Same card by UUID; the agent reports its current index; the saved placement is untouched; not restart pending.
        JsonNode create = JsonNode.Parse(pc.CreateBodiesOf("pcm")[1])!;
        Assert.Equal([Card1], P2Checks.DeviceIds(create));
        Assert.Equal(Card1, P2Checks.Label(create, ContainerLabels.GpuUuids));
        Assert.Equal([new GpuRef(0, Card1)], (await fleet.Master.Client("pc").GetLaunchConfigAsync("pcm", Ct))!.Gpus);
        Assert.Equal([Card1], P2Checks.StoredModel(fleet, "pcm").Placement!.GpuUuids);
        Assert.False((await P2Checks.RuntimeAsync(fleet).GetStatusAsync("pcm", Ct)).RestartPending);
    }

    [Fact]
    public async Task ARemovedCardIsRefusedWithGpuMissingNamingItAndNothingIsCreated()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        await P2Checks.WaitForDiscreteReportAsync(fleet, "pc", 2);
        await fleet.Master.CreateModelAsync(ModelBody("gone", "pc", [Card0], budgetGiB: 2));
        await fleet.Master.CreateModelAsync(ModelBody("kept", "pc", [Card1], budgetGiB: 2));

        // Pulled while the agent runs: the master's view still lists it, the agent re-reads its inventory and refuses.
        pc.Smi.Remove(Card0);
        string body = await fleet.Master.ExpectAsync("/api/models/gone/start", null, HttpStatusCode.UnprocessableEntity);
        Assert.Contains(Card0, body, StringComparison.Ordinal);
        Assert.Empty(pc.CreateBodiesOf("gone"));
        Assert.Null(await fleet.Master.Client("pc").GetRuntimeAsync("gone", Ct));

        // After an agent restart the master's view lost it too: the master refuses it the same way, before the agent.
        await pc.StopAsync();
        await pc.StartAsync();
        await ExitEnv.UntilAsync(async () =>
            fleet.Master.Cache.GetNode("pc") is { IsServing: true } &&
            (await fleet.Master.Nodes.GetAsync("pc", Ct))?.LastCapabilities?.Gpus is [{ Index: 0 } only] && only.Uuid == Card1,
            "pc reconnected with card 1 alone (index 0)");
        body = await fleet.Master.ExpectAsync("/api/models/gone/start", null, HttpStatusCode.UnprocessableEntity);
        Assert.Contains(Card0, body, StringComparison.Ordinal);
        Assert.Empty(pc.CreateBodiesOf("gone"));
        Assert.Null(await fleet.Master.Client("pc").GetRuntimeAsync("gone", Ct));
        Assert.Equal([Card0], P2Checks.StoredModel(fleet, "gone").Placement!.GpuUuids); // re-selecting is the operator's edit

        // The card that is still there serves.
        await fleet.Master.StartModelAsync("kept");
        Assert.Equal([Card1], P2Checks.DeviceIds(JsonNode.Parse(Assert.Single(pc.CreateBodiesOf("kept")))!));
    }

    /// <summary>
    /// MN-123 found, MN-124 fixed (D105): <c>ErrorCodes.ApiCodes</c> lists <c>gpu_missing</c> as a master API code ("bodies
    /// are ProblemDetails with the code in the code extension", D73); <c>POST /api/models/{id}/start</c> maps the refusal
    /// through <c>AdminApiEndpoints.NodeProblem</c>, which now carries the <c>code</c> extension for the agent's own refusal
    /// and for <c>LaunchSpecBuilder</c>'s (D95).
    /// </summary>
    [Fact]
    public async Task ARemovedCardAnswersTheGpuMissingCodeThroughTheApi()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        await P2Checks.WaitForDiscreteReportAsync(fleet, "pc", 2);
        await fleet.Master.CreateModelAsync(ModelBody("gone", "pc", [Card0], budgetGiB: 2));
        pc.Smi.Remove(Card0);

        await fleet.Master.ExpectAsync("/api/models/gone/start", null, HttpStatusCode.UnprocessableEntity, ErrorCodes.GpuMissing);
    }

    /// <summary>
    /// MN-123 found, MN-124 fixed (D105): <c>HealthReasonCodes.PlacedGpuMissing</c> (D73, design §5.1 "flagged on the model
    /// and node pages") is computed by <c>NodeStatusCache</c> from the placements and the node's last capabilities: a node
    /// whose report lost a placed card reads Degraded naming the model and the card.
    /// </summary>
    [Fact]
    public async Task ARemovedPlacedCardIsFlaggedOnTheNode()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        await P2Checks.WaitForDiscreteReportAsync(fleet, "pc", 2);
        await fleet.Master.CreateModelAsync(ModelBody("gone", "pc", [Card0], budgetGiB: 2));
        pc.Smi.Remove(Card0);
        await pc.StopAsync();
        await pc.StartAsync();

        await fleet.Master.WaitForNodeAsync("pc", n =>
            n.Capabilities?.Gpus.Count == 1 &&
            (n.Reasons.Any(r => r.Contains(Card0, StringComparison.Ordinal) || r.Contains(HealthReasonCodes.PlacedGpuMissing, StringComparison.Ordinal))),
            "pc flagged with placed_gpu_missing naming the removed card");
    }

    [Fact]
    public async Task TensorParallelRunsOnTwoEqualCardsAndUnequalCardsAreRefusedAsPlacementInvalid()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus);
        await P2Checks.WaitForDiscreteReportAsync(fleet, "pc", 2);

        await fleet.Master.CreateModelAsync(ModelBody("tp", "pc", [Card0, Card1], budgetGiB: 4));
        await fleet.Master.StartModelAsync("tp");
        JsonNode create = JsonNode.Parse(Assert.Single(pc.CreateBodiesOf("tp")))!;
        Assert.Equal([Card0, Card1], P2Checks.DeviceIds(create));
        Assert.Equal($"{Card0},{Card1}", P2Checks.Label(create, ContainerLabels.GpuUuids));
        var cmd = P2Checks.Cmd(create);
        int tp = cmd.IndexOf(VllmArgsFlag);
        Assert.True(tp >= 0 && tp + 1 < cmd.Count, $"no {VllmArgsFlag} in {string.Join(' ', cmd)}");
        Assert.Equal("2", cmd[tp + 1]);
        Assert.Equal([new GpuRef(0, Card0), new GpuRef(1, Card1)], (await fleet.Master.Client("pc").GetLaunchConfigAsync("tp", Ct))!.Gpus);

        // A second PC with a 16 GiB and a 24 GiB card.
        var pc2 = await fleet.JoinAsync("pc2", Pc2Gpus, start: false);
        var sized = SizedNvidiaSmi.Attach(pc2);
        sized.SetTotalMiB(Pc2Gpus[1], 24 * 1024);
        await pc2.StartAsync();
        await fleet.Master.WaitForNodeAsync("pc2", n => n.State == NodeState.Online, "pc2 Online");
        await P2Checks.WaitForDiscreteReportAsync(fleet, "pc2", 2);
        Assert.Equal(24, (await fleet.Master.Nodes.GetAsync("pc2", Ct))!.LastCapabilities!.Gpus.Single(g => g.Uuid == Pc2Gpus[1]).MemoryTotalGiB);

        string refused = await fleet.Master.ExpectAsync("/api/models", ModelBody("tp-unequal", "pc2", Pc2Gpus, budgetGiB: 4), HttpStatusCode.BadRequest, ErrorCodes.PlacementInvalid);
        using (var problem = JsonDocument.Parse(refused))
        {
            var problems = problem.RootElement.GetProperty("problems").EnumerateArray().ToList();
            Assert.Contains(problems, p => p.GetProperty("code").GetString() == PlacementProblemCodes.GpuCapacityMismatch && p.GetProperty("gpuUuid").GetString() == Pc2Gpus[1]);
        }

        var (status, _) = await SendAsync(fleet.Master.Admin.GetAsync("/api/models/tp-unequal", Ct));
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.DoesNotContain(P2Checks.StoredModels(fleet), m => m.Id == "tp-unequal");

        // The 24 GiB card alone is fine and launches with its own denominator.
        await fleet.Master.CreateModelAsync(ModelBody("big", "pc2", [Pc2Gpus[1]], budgetGiB: 4));
        await fleet.Master.StartModelAsync("big");
        JsonNode big = JsonNode.Parse(Assert.Single(pc2.CreateBodiesOf("big")))!;
        Assert.Equal([Pc2Gpus[1]], P2Checks.DeviceIds(big));
        Assert.Equal("24", P2Checks.Label(big, ContainerLabels.GpuTotalGiB));
        Assert.Empty(pc2.CreateBodiesOf("tp-unequal"));
    }

    [Fact]
    public async Task PerGpuAdmissionRefusesASecondModelOnAFullCardNamingItWhileTheOtherCardAdmits()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus, start: false);
        var sized = SizedNvidiaSmi.Attach(pc);
        await pc.StartAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online");
        await P2Checks.WaitForDiscreteReportAsync(fleet, "pc", 2);
        Assert.True(pc.Get<ReservationManager>().PerGpuAdmission);

        await fleet.Master.CreateModelAsync(ModelBody("first", "pc", [Card0], budgetGiB: 10));
        await fleet.Master.CreateModelAsync(ModelBody("second", "pc", [Card0], budgetGiB: 8));
        await fleet.Master.CreateModelAsync(ModelBody("other", "pc", [Card1], budgetGiB: 8));

        await fleet.Master.StartModelAsync("first");
        sized.SetUsedMiB(Card0, 512 + (10 * 1024)); // card 0 now holds the first model's 10 GiB, as nvidia-smi shows it

        // 8 GiB does not fit next to it on card 0 (5.5 GiB free): 409 naming GPU 0 on node pc; nothing created.
        string detail = await P2Checks.StartRefusedAsync(fleet, "second");
        Assert.Contains("on node pc (GPU 0)", detail, StringComparison.Ordinal);
        Assert.Contains("needs 8.0 GiB but only 5.5 GiB is free", detail, StringComparison.Ordinal);
        Assert.Empty(pc.CreateBodiesOf("second"));
        Assert.Null(await fleet.Master.Client("pc").GetRuntimeAsync("second", Ct));

        // The numbers for "Start anyway?" (D96): the master's runtime exception carries the agent's per-GPU details.
        var refusal = await Assert.ThrowsAsync<ModelMemoryAdmissionException>(() => P2Checks.RuntimeAsync(fleet).StartAsync("second", ct: Ct));
        var details = AgentErrorMapper.AdmissionDetails(refusal);
        Assert.NotNull(details);
        Assert.Equal(Card0, details.Gpu);
        Assert.Equal(8, details.NeedGiB);
        Assert.Equal(5.5, details.FreeGiB, 3);
        Assert.Equal(5.5, details.AvailableGiB, 3);

        // The other card admits the same 8 GiB.
        await fleet.Master.StartModelAsync("other");
        Assert.Equal([Card1], P2Checks.DeviceIds(JsonNode.Parse(Assert.Single(pc.CreateBodiesOf("other")))!));

        await P2Checks.AssertDgxIsP1Async(fleet);
    }

    private const string VllmArgsFlag = "--tensor-parallel-size";
}

/// <summary>Assertions and probes shared by the MN-123 placement and memory-model exit tests.</summary>
internal static class P2Checks
{
    public static IModelRuntime RuntimeAsync(P2Fleet fleet) => fleet.Master.Get<IModelRuntime>();

    /// <summary>Waits until the registry holds the node's discrete report with <paramref name="gpus"/> GPUs (what the validator reads).</summary>
    public static async Task<NodeCapabilities> WaitForDiscreteReportAsync(P2Fleet fleet, string nodeId, int gpus)
    {
        NodeCapabilities? caps = null;
        await ExitEnv.UntilAsync(async () =>
            (caps = (await fleet.Master.Nodes.GetAsync(nodeId, Ct))?.LastCapabilities) is { Memory.Model: MemoryModel.Discrete } c &&
            c.Gpus.Count == gpus && c.Features.Contains(AgentFeatures.GpuSelectionUuids),
            $"{nodeId}'s discrete report with {gpus} GPU(s)");
        return caps!;
    }

    /// <summary>The DGX of a fleet with PCs: P1's memory model, admission, <c>/status</c> reader and capability report.</summary>
    public static async Task AssertDgxIsP1Async(P2Fleet fleet)
    {
        Assert.Equal(MemoryModel.Unified, fleet.Dgx.Get<AgentMemoryModel>().Model);
        Assert.False(fleet.Dgx.Get<ReservationManager>().PerGpuAdmission);
        Assert.IsType<ProbeNodeMemoryReader>(fleet.Dgx.Get<INodeMemoryReader>());
        foreach (var caps in new[] { (await fleet.Master.Nodes.GetAsync("dgx", Ct))!.LastCapabilities!, await fleet.Master.Client("dgx").GetCapabilitiesAsync(Ct) })
        {
            Assert.Equal(MemoryModel.Unified, caps.Memory.Model);
            var gpu = Assert.Single(caps.Gpus);
            Assert.Equal(Gb10Uuid, gpu.Uuid);
            Assert.True(gpu.SharesHostMemory);
            Assert.Empty(caps.Features.Intersect(AgentFeatures.P2));
            Assert.Contains(AgentFeatures.RuntimeVllmContainer, caps.Features);
        }

        await fleet.Master.Cache.RefreshNodeAsync("dgx", Ct);
        Assert.Equal(MemoryModel.Unified, fleet.Master.Cache.GetNode("dgx")!.Memory!.Model);
        Assert.Empty(fleet.Master.Cache.GetNode("dgx")!.Memory!.Gpus);
    }

    /// <summary>A model created on the DGX without GPUs launches as P1: every GPU, no device ids, no GPU read-back labels.</summary>
    public static async Task AssertDgxLaunchesAsP1Async(P2Fleet fleet, string id)
    {
        await fleet.Master.CreateModelAsync(ModelBody(id, "dgx"));
        Assert.Equal(0, StoredModel(fleet, id).Placement!.Generation);
        await fleet.Master.StartModelAsync(id);
        AssertAllGpusLaunch(JsonNode.Parse(Assert.Single(fleet.Dgx.CreateBodiesOf(id)))!);
    }

    /// <summary>A P1 launch: <c>Count = -1</c>, no <c>DeviceIDs</c>, no GPU labels.</summary>
    public static void AssertAllGpusLaunch(JsonNode create)
    {
        JsonNode device = create["HostConfig"]!["DeviceRequests"]![0]!;
        Assert.Equal(-1, device["Count"]!.GetValue<int>());
        Assert.Null(device["DeviceIDs"]);
        JsonObject labels = create["Labels"]!.AsObject();
        Assert.False(labels.ContainsKey(ContainerLabels.GpuUuids));
        Assert.False(labels.ContainsKey(ContainerLabels.GpuTotalGiB));
    }

    public static IReadOnlyList<string> DeviceIds(JsonNode create) =>
        [.. create["HostConfig"]!["DeviceRequests"]![0]!["DeviceIDs"]!.AsArray().Select(d => d!.GetValue<string>())];

    public static string? Label(JsonNode create, string name) => create["Labels"]![name]?.GetValue<string>();

    public static List<string> Cmd(JsonNode create) => [.. create["Cmd"]!.AsArray().Select(a => a!.GetValue<string>())];

    /// <summary>The definitions in <c>{DataPath}/models.json</c> as the master last wrote them.</summary>
    public static IReadOnlyList<ModelDefinition> StoredModels(P2Fleet fleet)
    {
        string path = Path.Combine(fleet.DataDir, ModelRegistryDocument.FileName);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return JsonSerializer.Deserialize<ModelRegistryDocument>(stream, Json)!.Models;
    }

    public static ModelDefinition StoredModel(P2Fleet fleet, string id) => Assert.Single(StoredModels(fleet), m => m.Id == id);

    /// <summary><c>POST /api/models/{id}/restart</c>, retrying while the node's start slot says wait.</summary>
    public static Task RestartModelAsync(P2Fleet fleet, string id) =>
        ExitEnv.UntilAsync(async () =>
        {
            var (status, body) = await fleet.Master.PostAsync($"/api/models/{id}/restart");
            Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable, $"restart {id}: {(int)status} {body}");
            return status == HttpStatusCode.OK;
        }, $"restart {id} kept answering 409/503");

    /// <summary>
    /// <c>POST /api/models/{id}/start</c> that must be refused for memory: retried while another start still holds the slot
    /// (start conflict, "is still starting") or the node says wait (503); returns the 409's detail.
    /// </summary>
    public static async Task<string> StartRefusedAsync(P2Fleet fleet, string id)
    {
        string detail = "";
        await ExitEnv.UntilAsync(async () =>
        {
            var (status, body) = await fleet.Master.PostAsync($"/api/models/{id}/start");
            Assert.True(status is HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable, $"start {id} was not refused: {(int)status} {body}");
            if (status != HttpStatusCode.Conflict)
            {
                return false;
            }

            using var json = JsonDocument.Parse(body);
            detail = json.RootElement.GetProperty("detail").GetString() ?? "";
            return !detail.Contains("is still starting", StringComparison.Ordinal);
        }, $"start {id} refused for memory");
        return detail;
    }
}

/// <summary>
/// <c>nvidia-smi</c> of a discrete PC with a capacity and a used figure per card (MN-123): wraps the node's
/// <see cref="FakeNvidiaSmi"/> (its cards, their order, removal and <see cref="FakeNvidiaSmi.Failing"/> stay the source)
/// and answers 16 GiB cards with 0.5 GiB used unless a card says otherwise. Installed on the node before the agent starts.
/// </summary>
internal sealed class SizedNvidiaSmi(FakeNvidiaSmi inner) : INvidiaSmiRunner
{
    private const int DefaultTotalMiB = 16384;
    private const int DefaultUsedMiB = 512;
    private readonly ConcurrentDictionary<string, int> _totalMiB = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _usedMiB = new(StringComparer.Ordinal);

    public static SizedNvidiaSmi Attach(P2Agent agent)
    {
        Assert.Null(agent.Agent); // read when the agent is composed
        var sized = new SizedNvidiaSmi(agent.Smi);
        agent.Node.NvidiaSmi = sized;
        return sized;
    }

    public void SetTotalMiB(string uuid, int mib) => _totalMiB[uuid] = mib;

    public void SetUsedMiB(string uuid, int mib) => _usedMiB[uuid] = mib;

    public async Task<NvidiaSmiResult> RunAsync(string arguments, CancellationToken ct = default)
    {
        var answer = await inner.RunAsync(arguments, ct);
        if (!answer.Found || answer.ExitCode != 0)
        {
            return answer; // failing driver: as the inner fake answers
        }

        var c = CultureInfo.InvariantCulture;
        var uuids = inner.Uuids;
        string output = arguments switch
        {
            NvidiaSmiGpuInventory.Arguments => string.Concat(uuids.Select((u, i) =>
                string.Create(c, $"{i}, {u}, {Name(u)}, {Total(u)}, 12.0, 617.14\n"))),
            NvidiaSmiGpuTelemetryProbe.Arguments => string.Concat(uuids.Select((u, i) =>
                string.Create(c, $"{i}, {u}, 0, 10.00, 30, 210, {Used(u)}, {Total(u)}, {Total(u) - Used(u)}, {Name(u)}\n"))),
            _ => answer.Output,
        };
        return answer with { Output = output };
    }

    private int Total(string uuid) => _totalMiB.TryGetValue(uuid, out int mib) ? mib : DefaultTotalMiB;

    private int Used(string uuid) => _usedMiB.TryGetValue(uuid, out int mib) ? mib : DefaultUsedMiB;

    private string Name(string uuid) => string.Create(CultureInfo.InvariantCulture, $"NVIDIA Test {Total(uuid) / 1024}G");
}
