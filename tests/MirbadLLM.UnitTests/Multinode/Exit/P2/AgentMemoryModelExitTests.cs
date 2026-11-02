using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MirbadLLM.Agent;
using MirbadLLM.Agent.Admission;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Events;
using MirbadLLM.Agent.Telemetry;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, the agent's memory model (D101, D102; W9 review MN-132): decided once per process. An agent whose
/// <c>nvidia-smi</c> fails at startup stays unified — admission, capability report and flags — after the probe recovers
/// (log 2643 once) until it is restarted; <c>Agent:MemoryModel</c> forces either model over what the probe says. The DGX
/// in the same fleet keeps exactly P1.
/// </summary>
public sealed class AgentMemoryModelExitTests
{
    /// <summary>"nvidia-smi failed while deciding the memory model" (<c>AgentMemoryModel</c>).</summary>
    private const int ProbeUnanswered = 2642;

    /// <summary>"The GPU probe now reports a … node, but this agent decided … at startup" (<c>MemoryModelReportContributor</c>).</summary>
    private const int ReportDiffers = 2643;

    [Fact]
    public async Task ANvidiaSmiThatFailsAtStartupKeepsTheNodeUnifiedAfterItRecoversUntilTheAgentRestarts()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus, start: false);
        pc.Smi.Failing = true; // the driver is not loaded yet when the agent starts
        await pc.StartAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.IsServing, "pc serving with a failing nvidia-smi");

        // Decided unified, as P1: unified admission and /status reader, logged once.
        Assert.Equal(MemoryModel.Unified, pc.Get<AgentMemoryModel>().Model);
        Assert.False(pc.Get<ReservationManager>().PerGpuAdmission);
        Assert.IsType<ProbeNodeMemoryReader>(pc.Get<INodeMemoryReader>());
        Assert.Equal(1, pc.Log.Count(ProbeUnanswered));
        NodeCapabilities first = await WaitForReportAsync(fleet, "pc");
        AssertUnifiedP1Report(first);
        Assert.Empty(first.Gpus);

        // The driver comes up: the report now lists the cards but keeps the startup decision and the P1 flags.
        pc.Smi.Failing = false;
        NodeCapabilities recovered = await ExitEnv.UntilValueAsync(async () =>
            await fleet.Master.Client("pc").GetCapabilitiesAsync(Ct) is { Gpus.Count: 2 } c ? c : null, "pc's report listing both cards");
        Assert.Equal(PcGpus, recovered.Gpus.OrderBy(g => g.Index).Select(g => g.Uuid));
        AssertUnifiedP1Report(recovered);
        for (int i = 0; i < 3; i++)
        {
            AssertUnifiedP1Report(await fleet.Master.Client("pc").GetCapabilitiesAsync(Ct));
        }

        Assert.Equal(1, pc.Log.Count(ReportDiffers)); // once per process, however often the report is built
        Assert.Contains(pc.Log.Records, r => r.EventId == ReportDiffers && r.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Equal(MemoryModel.Unified, pc.Get<AgentMemoryModel>().Model);
        Assert.False(pc.Get<ReservationManager>().PerGpuAdmission);
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online once nvidia-smi works");

        // The master places and launches it as a unified node: the whole node, no GPU selection.
        await fleet.Master.ExpectAsync("/api/models", ModelBody("pcg", "pc", [PcGpus[0]]), HttpStatusCode.BadRequest, ErrorCodes.PlacementInvalid);
        await fleet.Master.CreateModelAsync(ModelBody("pcu", "pc", budgetGiB: 2));
        await fleet.Master.StartModelAsync("pcu");
        P2Checks.AssertAllGpusLaunch(JsonNode.Parse(Assert.Single(pc.CreateBodiesOf("pcu")))!);
        await fleet.Master.ExpectAsync("/api/models/pcu/stop", null, HttpStatusCode.NoContent);

        // A restart with a working nvidia-smi decides discrete: per-GPU admission and the P2 flags from then on.
        await pc.StopAsync();
        await pc.StartAsync();
        Assert.Equal(MemoryModel.Discrete, pc.Get<AgentMemoryModel>().Model);
        Assert.True(pc.Get<ReservationManager>().PerGpuAdmission);
        Assert.IsType<DiscreteNodeMemoryReader>(pc.Get<INodeMemoryReader>());
        NodeCapabilities discrete = await P2Checks.WaitForDiscreteReportAsync(fleet, "pc", 2);
        Assert.Contains(AgentFeatures.AdmissionDiscrete, discrete.Features);
        Assert.Contains(AgentFeatures.LaunchEnv, discrete.Features);
        Assert.Equal(1, pc.Log.Count(ReportDiffers));
        Assert.Equal(1, pc.Log.Count(ProbeUnanswered));

        await P2Checks.AssertDgxIsP1Async(fleet);
    }

    [Fact]
    public async Task MemoryModelUnifiedForcesP1OnADiscretePc()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus, configure: o => o.MemoryModel = MemoryModel.Unified, start: false);
        var sized = SizedNvidiaSmi.Attach(pc);
        sized.SetUsedMiB(PcGpus[0], 15 * 1024); // card 0 nearly full: per-GPU admission would refuse 8 GiB there
        sized.SetUsedMiB(PcGpus[1], 15 * 1024);
        await pc.StartAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online");

        Assert.Equal(MemoryModel.Unified, pc.Get<AgentMemoryModel>().Model);
        Assert.False(pc.Get<ReservationManager>().PerGpuAdmission);
        Assert.IsType<ProbeNodeMemoryReader>(pc.Get<INodeMemoryReader>());
        NodeCapabilities caps = await WaitForReportAsync(fleet, "pc");
        AssertUnifiedP1Report(caps);
        Assert.Equal(PcGpus, caps.Gpus.OrderBy(g => g.Index).Select(g => g.Uuid)); // the cards are still reported
        AssertUnifiedP1Report(await fleet.Master.Client("pc").GetCapabilitiesAsync(Ct));

        // Forced on purpose: no "restart the agent" warning (2643); the information line 2644 instead (below the harness's
        // Warning level here, asserted in AgentMemoryModelTests).
        Assert.Equal(0, pc.Log.Count(ReportDiffers));

        // Unified placement and admission: no GPU selection; 8 GiB is admitted against the host pool, every GPU launched.
        string refused = await fleet.Master.ExpectAsync("/api/models", ModelBody("pcg", "pc", [PcGpus[0]]), HttpStatusCode.BadRequest, ErrorCodes.PlacementInvalid);
        Assert.Contains(PlacementProblemCodes.GpusOnUnifiedNode, refused, StringComparison.Ordinal);
        await fleet.Master.CreateModelAsync(ModelBody("pcu", "pc", budgetGiB: 8));
        await fleet.Master.StartModelAsync("pcu");
        P2Checks.AssertAllGpusLaunch(JsonNode.Parse(Assert.Single(pc.CreateBodiesOf("pcu")))!);

        await P2Checks.AssertDgxIsP1Async(fleet);
    }

    [Fact]
    public async Task MemoryModelDiscreteForcesPerGpuAdmissionAndTheP2FlagsOverAFailingProbe()
    {
        await using var fleet = await P2Fleet.StartAsync();
        var pc = await fleet.JoinAsync("pc", PcGpus, configure: o => o.MemoryModel = MemoryModel.Discrete, start: false);
        var sized = SizedNvidiaSmi.Attach(pc);
        pc.Smi.Failing = true; // the probe alone would decide unified (2642)
        await pc.StartAsync();
        await fleet.Master.WaitForNodeAsync("pc", n => n.IsServing, "pc serving with a failing nvidia-smi");

        Assert.Equal(MemoryModel.Discrete, pc.Get<AgentMemoryModel>().Model);
        Assert.True(pc.Get<ReservationManager>().PerGpuAdmission);
        Assert.IsType<DiscreteNodeMemoryReader>(pc.Get<INodeMemoryReader>());
        Assert.Equal(0, pc.Log.Count(ProbeUnanswered)); // forced: the probe does not decide
        NodeCapabilities forced = await WaitForReportAsync(fleet, "pc");
        Assert.Equal(MemoryModel.Discrete, forced.Memory.Model);
        Assert.Contains(AgentFeatures.GpuSelectionUuids, forced.Features);
        Assert.Contains(AgentFeatures.AdmissionDiscrete, forced.Features);
        Assert.Contains(AgentFeatures.LaunchEnv, forced.Features);
        Assert.Equal(0, pc.Log.Count(ReportDiffers)); // forced: never the restart advice

        // The driver comes up; the master's next sync (a master restart) sees the cards.
        pc.Smi.Failing = false;
        sized.SetUsedMiB(PcGpus[0], 12 * 1024); // something holds 12 GiB of card 0
        await fleet.RestartMasterAsync();
        await P2Checks.WaitForDiscreteReportAsync(fleet, "pc", 2);

        // Per-GPU admission: 8 GiB is refused on card 0 (3.5 GiB free) naming it, and admitted on card 1.
        await fleet.Master.CreateModelAsync(ModelBody("on0", "pc", [PcGpus[0]], budgetGiB: 8));
        await fleet.Master.CreateModelAsync(ModelBody("on1", "pc", [PcGpus[1]], budgetGiB: 8));
        string detail = await P2Checks.StartRefusedAsync(fleet, "on0");
        Assert.Contains("on node pc (GPU 0)", detail, StringComparison.Ordinal);
        Assert.Empty(pc.CreateBodiesOf("on0"));
        await fleet.Master.StartModelAsync("on1");
        Assert.Equal([PcGpus[1]], P2Checks.DeviceIds(JsonNode.Parse(Assert.Single(pc.CreateBodiesOf("on1")))!));

        await P2Checks.AssertDgxIsP1Async(fleet);
    }

    /// <summary>A unified report with P1's feature list only.</summary>
    private static void AssertUnifiedP1Report(NodeCapabilities caps)
    {
        Assert.Equal(MemoryModel.Unified, caps.Memory.Model);
        Assert.Empty(caps.Features.Intersect(AgentFeatures.P2));
        Assert.Contains(AgentFeatures.RuntimeVllmContainer, caps.Features);
        Assert.Contains(AgentFeatures.TelemetryV1, caps.Features);
    }

    private static Task<NodeCapabilities> WaitForReportAsync(P2Fleet fleet, string nodeId) =>
        ExitEnv.UntilValueAsync(async () => (await fleet.Master.Nodes.GetAsync(nodeId, Ct))?.LastCapabilities, $"{nodeId}'s capability report in the registry");
}
