using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Abstractions;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Agent.Recovery;
using MirbadLLM.Core;
using MirbadLLM.NodeRuntime.Abstractions;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Multinode;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Agent;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.P1Exit;

#pragma warning disable CA1707 // MethodUnderTest_Scenario_ExpectedBehavior test naming style, used throughout this project.

namespace MirbadLLM.UnitTests.Multinode.P3Exit;

/// <summary>
/// MN-237: the node half of the P3/P4 development exit (plan-p3p4 §4.1), driven against the <b>real agent</b>
/// composition (<c>MirbadLLM.Agent.Program.CreateAsync</c> over <see cref="P3Node"/>: the process driver over the fake
/// llama-server) — the two W14 paths (MN-234, D131) that only existed below the exit level:
/// <list type="number">
/// <item>A <b>CPU-only</b> llama.cpp start on the Windows node. D131 (5) ruled that <c>HostBudgetGiB</c> stays null for
/// a placement with no GPU because the agent then charges the whole <c>BudgetGiB</c> to host RAM
/// (<c>DiscreteAdmission.EffectiveHostBudgetGiB</c>, <c>gpuCount == 0</c>). Only <c>DiscreteReservationTests</c> proved
/// it, with a hand-built request; here the launch is the one <see cref="LaunchSpecBuilder"/> builds and it travels the
/// real <c>POST /runtimes/{modelId}/launch</c> route into the real <c>ReservationManager</c>.</item>
/// <item>An <b>agent restart on a node whose only record of the instance is <c>processes.json</c></b> (the operation log
/// compacted away): the instance comes back on <c>/runtimes</c> and in <c>/status</c> as <c>delivery: process</c>, and a
/// real master (<see cref="ExitMaster"/>) shows it Ready without an operator action.</item>
/// </list>
/// </summary>
public sealed class P3NodeExitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ============================================================ (1) a CPU-only llama.cpp start (D131 (1)/(5))

    [Fact]
    public async Task ACpuOnlyLlamaCppStart_IsRefusedWithTheHostPart_WhenItsBudgetExceedsFreeHostMemoryMinusTheReserve()
    {
        // The node's probe reports 127 GiB total and 100 GiB available; the node record reserves 8 GiB, so 92 GiB is
        // free for starts. A CPU-only placement sends HostBudgetGiB null (LaunchSpecBuilder.Admission, no GPUs), and the
        // agent charges the whole budget to host RAM — so 120 GiB must be refused on the host part, not on a GPU.
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        await app.StartAsync(Ct);
        await app.Services.GetRequiredService<IExecutableCatalog>().RefreshAsync(Ct);
        using var client = new AgentContractClient(node.Target(app));

        var tooBig = CpuOnly(budgetGiB: 120);
        Assert.Null(tooBig.Admission.HostBudgetGiB); // D131 (5): no GPU, so the master sends no host figure
        Assert.Equal(GpuSelectionMode.All, tooBig.Spec.Gpus.Mode); // a placement without GPUs is not a GPU selection
        Assert.DoesNotContain("--device", tooBig.Spec.Argv);
        Assert.Equal("0", tooBig.Spec.Argv[tooBig.Spec.Argv.ToList().IndexOf("-ngl") + 1]);

        var refused = await client.SendAsync(AgentRoutes.RuntimeLaunch, [CpuOnlyModelId], tooBig, ct: Ct);

        Assert.Equal(409, refused.Status);
        var error = refused.Read<AgentErrorResponse>().Error;
        Assert.Equal(ErrorCodes.AdmissionRefused, error.Code);
        var details = error.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.True(details.Host, "the refusal did not name the host part: " + error.Message);
        Assert.Null(details.Gpu);
        Assert.Equal(120, details.NeedGiB); // the whole budget, charged to host RAM
        Assert.Equal(100, details.AvailableGiB);
        Assert.Equal(HostReserveGiB, details.ReserveGiB);
        Assert.Equal(100 - HostReserveGiB, details.FreeGiB);
        Assert.Empty((await client.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct)).Read<IReadOnlyList<RuntimeStatus>>());
        Assert.Empty((await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>().Reservations);
    }

    [Fact]
    public async Task ACpuOnlyLlamaCppStart_ThatFits_HoldsItsWholeBudgetAgainstTheHostPseudoGpu()
    {
        await using var node = await P3Node.CreateAsync(Ct);
        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        await app.StartAsync(Ct);
        await app.Services.GetRequiredService<IExecutableCatalog>().RefreshAsync(Ct);
        using var client = new AgentContractClient(node.Target(app));

        // A slow load, so the reservation is still held while it is read: the agent releases it when the launch
        // operation completes, and from then on the instance's RAM is the probe's business, not the reservation's.
        var launch = CpuOnly(budgetGiB: 40, loadMs: 4_000);
        var accepted = await client.SendAsync(AgentRoutes.RuntimeLaunch, [CpuOnlyModelId], launch, ct: Ct);
        Assert.Equal(202, accepted.Status);
        await UntilAsync(
            async () => (await client.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct))
                .Read<IReadOnlyList<RuntimeStatus>>()
                .FirstOrDefault(r => r.ModelId == CpuOnlyModelId && r.State == ModelState.Starting),
            "the CPU-only instance never started");

        // The reservation: the pseudo-GPU "host" alone, holding the whole budget as its host budget (D131 (5)).
        var status = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>();
        var reservation = Assert.Single(status.Reservations, r => r.ModelId == CpuOnlyModelId);
        Assert.Equal([ReservationInfo.HostPseudoGpu], reservation.Gpus);
        Assert.Equal(40, reservation.BudgetGiB);
        Assert.Equal(40, reservation.HostBudgetGiB);
        Assert.Equal(
            40,
            MirbadLLM.Agent.Admission.DiscreteAdmission.EffectiveHostBudgetGiB(launch.Admission.BudgetGiB, launch.Admission.HostBudgetGiB, gpuCount: 0));

        // Another CPU-only start meets the start rule first: both want the same slot, the host pseudo-GPU.
        var clash = await client.SendAsync(
            AgentRoutes.RuntimeLaunch, ["cpu-only-second"], CpuOnly(budgetGiB: 1, modelId: "cpu-only-second"), ct: Ct);
        Assert.Equal(409, clash.Status);
        var clashError = clash.Read<AgentErrorResponse>().Error;
        Assert.Equal(ErrorCodes.StartConflict, clashError.Code);
        Assert.Equal(CpuOnlyModelId, clashError.ReadDetails<StartConflictDetails>()!.StartingModelId);

        // A GPU start does not meet the start rule (another slot), so it reaches host admission — and there the
        // CPU-only reservation is counted at its whole budget: 100 − 8 − 40 = 52 free, so a 60 GiB host budget is
        // refused on the host part. That is what D131 (5) means by "charges the whole BudgetGiB to host RAM".
        var onGpu = await client.SendAsync(
            AgentRoutes.RuntimeLaunch, ["gpu-neighbour"], OnOneGpu("gpu-neighbour", budgetGiB: 5, hostBudgetGiB: 60), ct: Ct);
        Assert.Equal(409, onGpu.Status);
        var details = onGpu.Read<AgentErrorResponse>().Error.ReadDetails<AdmissionRefusedDetails>()!;
        Assert.True(details.Host, "the GPU neighbour's refusal did not name the host part");
        Assert.Equal(40, details.ReservedByOthersGiB);
        Assert.Equal(52, details.FreeGiB);

        // The start itself finishes: a process instance with no GPU, Ready through the fake llama-server's /health.
        var ready = await UntilAsync(
            async () => (await client.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct))
                .Read<IReadOnlyList<RuntimeStatus>>()
                .FirstOrDefault(r => r.ModelId == CpuOnlyModelId && r.State == ModelState.Ready),
            "the CPU-only instance never reached Ready");
        Assert.Equal(LaunchDelivery.Process, ready.Delivery);
        Assert.Empty(ready.Gpus); // no GPU: the instance holds nothing on a card
    }

    // ============================================================ (2) processes.json-only restart (D131 (3))

    [Fact]
    public async Task AnAgentRestart_WithTheInstanceOnlyInProcessesJson_BringsItBack_AndAMasterShowsItReady()
    {
        // D131 (3): the operation log is deleted, so the launch that created the instance is not recoverable and the
        // reconciler can only find it by listing every driver. P3AgentCompositionTests proves the agent half; the exit
        // level adds the master: a real master over the real agent must show the instance Ready with no operator action
        // (no start, no refresh — only the node monitor's own poll).
        await using var node = await P3Node.CreateAsync(Ct);
        await node.SeedAdoptableRecordAsync(Ct);
        string operations = Path.Combine(node.StateDirectory, "operations");
        Assert.True(Directory.Exists(operations), "the seeding run wrote no operation log");
        Directory.Delete(operations, recursive: true);
        Assert.False(Directory.Exists(operations));

        await using WebApplication app = await node.ComposeAsync(ct: Ct);
        await app.StartAsync(Ct);

        // The agent's own view: /runtimes and /status carry the instance back as a process delivery.
        using var client = new AgentContractClient(node.Target(app));
        var runtimes = (await client.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct)).Read<IReadOnlyList<RuntimeStatus>>();
        var adopted = Assert.Single(runtimes, r => r.ModelId == P3Node.SeededModelId);
        Assert.Equal(LaunchDelivery.Process, adopted.Delivery);
        var snapshot = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>();
        Assert.Equal(LaunchDelivery.Process, Assert.Single(snapshot.Runtimes, r => r.ModelId == P3Node.SeededModelId).Delivery);

        // And the reconciler found it without an operation log at all (the path that would be dead if RuntimeOperations
        // stopped aggregating the drivers).
        var report = await app.Services.GetRequiredService<AgentReconciler>().RunAsync(Ct);
        Assert.Empty(report.Operations);
        Assert.Equal([P3Node.SeededModelId], report.Adopted.Select(a => a.ModelId));

        // The master: nothing is sent but its own polling. No /start, no RefreshNodeAsync.
        string dataDir = ExitEnv.NewDirectory("mn237-master");
        try
        {
            await RegisterAsync(dataDir, node, app);
            await using var master = new ExitMaster(dataDir, node.Node.Ca, new Dictionary<string, string?>
            {
                ["MirbadLLM:Nodes:BootstrapNodeId"] = P3NodeId,
            });
            _ = master.Services;

            var runtime = await UntilAsync(
                () => Task.FromResult(master.Cache.GetRuntime(P3Node.SeededModelId)),
                "the master never saw the adopted instance");
            await ExitEnv.UntilAsync(
                () => master.Cache.GetRuntime(P3Node.SeededModelId)?.State == ModelState.Ready,
                $"the adopted instance to reach Ready in the master's cache (it is {master.Cache.GetRuntime(P3Node.SeededModelId)?.State.ToString() ?? "unknown"})");
            Assert.Equal(P3NodeId, runtime.NodeId);
            Assert.Equal(NodeState.Online, master.Cache.GetNode(P3NodeId)!.State);

            // The master's own model view agrees, and nothing it did started the instance: the agent accepted no launch.
            var status = await master.Get<IModelRuntime>().GetStatusAsync(P3Node.SeededModelId, Ct);
            Assert.Equal(ModelState.Ready, status.State);
            Assert.DoesNotContain(
                (await client.SendAsync(AgentRoutes.OperationsList, [], ct: Ct)).Read<IReadOnlyList<OperationStatus>>(),
                o => o.Kind == OperationKinds.Launch);
        }
        finally
        {
            ExitEnv.DeleteDirectory(dataDir);
        }
    }

    // ============================================================ helpers

    private const string P3NodeId = LlamaCppFixtures.NodeId;

    private const string CpuOnlyModelId = "cpu-only-embedder";

    /// <summary>The node record's host-RAM reserve (<see cref="LlamaCppFixtures.Node"/>).</summary>
    private const double HostReserveGiB = 8;

    /// <summary>
    /// The launch the master builds for a llama.cpp model placed on the node with <b>no GPU</b>: the real
    /// <see cref="LaunchSpecBuilder"/> spec and admission, so the agent sees exactly what a start from the UI sends.
    /// </summary>
    /// <param name="budgetGiB">The model's memory budget.</param>
    /// <param name="modelId">Model id (and served name).</param>
    /// <param name="loadMs">
    /// The fake llama-server's load time (<c>FAKE_LLAMA_LOAD_MS</c>), added to the built spec's environment so a test
    /// can read the reservation while the start is still in flight. 0 = the fake's own default.
    /// </param>
    private static LaunchRequest CpuOnly(double budgetGiB, string modelId = CpuOnlyModelId, int loadMs = 0)
    {
        var record = LlamaCppFixtures.Node();
        var model = LlamaCppFixtures.Embedder();
        model = model with
        {
            Id = modelId,
            ServedName = modelId,
            MemoryBudgetGiB = budgetGiB,
            HostMemoryBudgetGiB = null,
            AutoStart = false,
            Placement = new ModelPlacement(P3NodeId), // on the node, on no GPU
            LlamaCpp = model.LlamaCpp! with { GpuLayers = 0, ExecutableVersion = null },
        };

        var built = LaunchSpecBuilder.BuildLlamaCpp(model, record, tuning: false);
        var spec = loadMs > 0
            ? built.Spec with
            {
                Env = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [MirbadLLM.FakeLlamaServer.FakeLlamaOptions.LoadMsVariable] = loadMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
            }
            : built.Spec;
        var now = DateTimeOffset.UtcNow;
        return new LaunchRequest
        {
            OperationId = "op-" + Guid.NewGuid().ToString("N")[..12],
            IssuedAt = now,
            ExpiresAt = now + OperationRetention.EnvelopeLifetime,
            Mode = LaunchMode.EnsureStarted,
            Spec = spec,
            Admission = LaunchSpecBuilder.Admission(model, record, force: false),
            ExpectedConfigHash = null,
            ExpectedExecutionFingerprint = built.ExecutionFingerprint,
        };
    }

    /// <summary>
    /// A llama.cpp launch on the node's second card with an explicit host-memory budget, so the host part of admission
    /// is what decides it (the GPU part has 13 GiB free after the 2.5 GiB reserve).
    /// </summary>
    private static LaunchRequest OnOneGpu(string modelId, double budgetGiB, double hostBudgetGiB)
    {
        var record = LlamaCppFixtures.Node();
        var model = LlamaCppFixtures.Embedder();
        model = model with
        {
            Id = modelId,
            ServedName = modelId,
            MemoryBudgetGiB = budgetGiB,
            HostMemoryBudgetGiB = hostBudgetGiB,
            AutoStart = false,
            Placement = new ModelPlacement(P3NodeId, [LlamaCppFixtures.Rtx5060Ti], [1]),
            LlamaCpp = model.LlamaCpp! with { ExecutableVersion = null },
        };

        var built = LaunchSpecBuilder.BuildLlamaCpp(model, record, tuning: false);
        var now = DateTimeOffset.UtcNow;
        return new LaunchRequest
        {
            OperationId = "op-" + Guid.NewGuid().ToString("N")[..12],
            IssuedAt = now,
            ExpiresAt = now + OperationRetention.EnvelopeLifetime,
            Mode = LaunchMode.EnsureStarted,
            Spec = built.Spec,
            Admission = LaunchSpecBuilder.Admission(model, record, force: false),
            ExpectedConfigHash = null,
            ExpectedExecutionFingerprint = built.ExecutionFingerprint,
        };
    }

    /// <summary>Writes <paramref name="app"/>'s address into <paramref name="dataDir"/>'s <c>nodes.json</c> as the approved node.</summary>
    private static async Task RegisterAsync(string dataDir, P3Node node, WebApplication app)
    {
        using var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), TimeProvider.System);
        await registry.UpsertAsync(new NodeRecord
        {
            Id = P3NodeId,
            Uid = node.Node.NodeUid,
            Address = node.Target(app).BaseAddress.AbsoluteUri,
            Approved = true,
            CertSpkiSha256 = node.Node.AgentSpki,
            MemoryReserveGiB = HostReserveGiB,
            CreatedAt = DateTimeOffset.UtcNow,
        }, Ct);
        await ExitEnv.SeedModelsAsync(dataDir);
    }

    private static async Task<T> UntilAsync<T>(Func<Task<T?>> probe, string what)
        where T : class
    {
        T? value = null;
        await ExitEnv.UntilAsync(async () => (value = await probe()) is not null, what);
        return value!;
    }
}
