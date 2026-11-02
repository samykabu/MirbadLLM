using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Multinode.Contract;

namespace MirbadLLM.UnitTests.Multinode.Chaos;

/// <summary>
/// Product bugs MN-044's chaos runs found, as minimal reproductions on the real master and agent; fixed by MN-047 (the
/// mapped exception, D67; node health in the event stream, D68) and kept as regression tests.
/// </summary>
[Trait("Category", "Chaos")]
public sealed class ChaosFindingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Found by seed 4402 (plan 4402000: "op Start qwen" then "docker outage" 85 ms later): a launch the agent accepted
    /// fails <c>docker_unavailable</c> before its container exists; <c>RemoteModelRuntime.FinishedLaunchAsync</c> asks
    /// <c>CreatedByAsync</c> whether an instance was created, whose <c>GetRuntimeAsync</c> catches only
    /// <see cref="AgentUnreachableException"/>, so the agent's <c>docker_unavailable</c> answer to that read escapes
    /// <c>StartAsync</c> as a raw <see cref="AgentException"/> instead of the mapped exception the class documents
    /// (<c>AgentErrorMapper.ToMasterException(operation.Error)</c> → <see cref="HostServiceException"/>); the admin API answers
    /// 500 with the agent's exception type.
    /// </summary>
    [Fact]
    public async Task ALaunchThatFailsBeforeCreateWhileDockerIsDownThrowsTheMappedException()
    {
        await using var cluster = await ChaosCluster.StartAsync(4402, Ct);
        await ChaosRunner.SettleAsync(cluster, ChaosSettings.SettleTimeout, Ct);
        var registry = cluster.MasterServices.GetRequiredService<IModelRegistry>();
        await registry.UpsertAsync(ChaosCluster.Models[0] with { Id = "pulled", ServedName = "pulled", Image = AgentContractSeed.PullImage }, Ct);

        // Hold the launch in its pull stage (accepted, no container yet), then take Docker away and let the pull end.
        cluster.Node.Docker.SetPullsHeld(true);
        var runtime = cluster.MasterServices.GetRequiredService<IModelRuntime>();
        var start = Task.Run(() => runtime.StartAsync("pulled", force: false, Ct), Ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(await cluster.AgentClient().ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }, Ct))
                   .Any(o => o.Target == "pulled" && o.Stage == OperationStages.Pulling))
        {
            Assert.True(DateTime.UtcNow < deadline, "the launch never reached its pull stage");
            await Task.Delay(50, Ct);
        }

        cluster.SetDockerDown(true);
        cluster.Node.Docker.SetPullsHeld(false);
        var thrown = await Record.ExceptionAsync(() => start);

        Assert.NotNull(thrown);
        Assert.IsNotType<AgentException>(thrown);
        Assert.IsType<HostServiceException>(thrown);
    }

    /// <summary>
    /// Found by scenario seed 4420 (D56 step: agent restart while Docker is down, then Docker back): node health reaches the
    /// master only through a snapshot. No agent event carries health (heartbeats carry boot and seq only;
    /// <c>NodeStatusCache</c> sets <c>Health</c> from <c>/status</c> snapshots alone), so while the event stream stays up the
    /// master never sees Docker go down (this test) or come back (<see cref="ANodeThatRecoversWhileConnectedBecomesOnline"/>);
    /// design §3.4/§3.5 promise state-change events and Online ↔ Degraded transitions.
    /// </summary>
    [Fact]
    public async Task DockerGoingDownWhileConnectedMakesTheNodeDegraded()
    {
        await using var cluster = await ChaosCluster.StartAsync(4420, Ct);
        await ChaosRunner.SettleAsync(cluster, ChaosSettings.SettleTimeout, Ct);

        cluster.SetDockerDown(true);
        await EventuallyAsync(async () => (await cluster.AgentClient().GetStatusAsync(Ct)).Health.Reasons.Any(r => r.Code == HealthReasonCodes.DockerUnreachable));

        // Several heartbeats later the master must show what the agent reports.
        await Task.Delay(TimeSpan.FromSeconds(12), Ct);
        Assert.Equal(NodeState.Degraded, cluster.CachedNode()?.State);
    }

    /// <summary>The recovery direction of <see cref="DockerGoingDownWhileConnectedMakesTheNodeDegraded"/> (seed 4420).</summary>
    [Fact]
    public async Task ANodeThatRecoversWhileConnectedBecomesOnline()
    {
        await using var cluster = await ChaosCluster.StartAsync(4420, Ct);
        await ChaosRunner.SettleAsync(cluster, ChaosSettings.SettleTimeout, Ct);

        // The agent starts while Docker is down: its snapshot makes the node Degraded.
        await cluster.StopAgentAsync(crash: true);
        cluster.SetDockerDown(true);
        await cluster.StartAgentAsync(Ct);
        await EventuallyAsync(() => Task.FromResult(cluster.CachedNode()?.State == NodeState.Degraded));

        cluster.SetDockerDown(false);
        await EventuallyAsync(async () => (await cluster.AgentClient().GetStatusAsync(Ct)).Health.Reasons.Count == 0);
        await Task.Delay(TimeSpan.FromSeconds(12), Ct);
        Assert.Equal(NodeState.Online, cluster.CachedNode()?.State);
    }

    [Fact]
    public void TheChaosPlanBudgetStaysAboveItsOwnOperationCeiling()
    {
        // MN-239's diagnosis of D129 (6). SeededFaultSequencesSettleToEveryInvariant(4402) reported "an operation never
        // returned" twice — once in MN-219's sweep, once in MN-239's full Linux run — and never reproduced alone (5
        // runs of the class, 33 s each, green). It could not: the violation was the test's own arithmetic. An operation
        // that exceeds ChaosRunner.OperationCeiling is logged as "no answer within …" and its task *completes*, so the
        // only way the whole-plan wait can time out is wall-clock. With a 45 s ceiling and a 60 s plan wait, a plan
        // whose last step is issued a few hundred ms in had about 14 s of slack for thread-pool starvation — which a
        // fully parallel 4 500-test run on a 16 GiB container supplies. Lengthening the wait blindly would hide the
        // next real violation, so the relationship is the thing that is pinned: the plan must allow a whole operation
        // ceiling plus a real margin, whatever either value becomes.
        Assert.True(
            ChaosRunner.PlanCeiling >= ChaosRunner.OperationCeiling + ChaosRunner.PlanMargin,
            $"a plan waits {ChaosRunner.PlanCeiling.TotalSeconds:0} s for operations each allowed " +
            $"{ChaosRunner.OperationCeiling.TotalSeconds:0} s, which leaves less than " +
            $"{ChaosRunner.PlanMargin.TotalSeconds:0} s for scheduling: the violation it reports would be the " +
            "machine's load, not the cluster's behaviour.");
        Assert.True(ChaosRunner.OperationCeiling > TimeSpan.Zero && ChaosRunner.PlanMargin > TimeSpan.Zero);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "precondition not reached");
            await Task.Delay(100, Ct);
        }
    }
}
