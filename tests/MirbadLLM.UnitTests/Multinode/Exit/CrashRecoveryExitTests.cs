using System.Net;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.UnitTests.Multinode.Contract;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// Design §13.6 "agent crash between create and start → reconciliation starts it" for every launch stage: a start through
/// the master's admin API, the agent crashing right after it durably recorded the stage, then restarting over the same
/// state. The end state is always exactly one Ready instance created by that one launch, no reservation left, and the
/// master's operation reference closed as Succeeded — the agent finished the accepted launch by itself.
/// </summary>
public sealed class CrashRecoveryExitTests
{
    /// <summary>Every stage a launch records (the image is pulled, so the pull stages run too).</summary>
    public static TheoryData<string> LaunchStages =>
    [
        OperationStages.Admitted, OperationStages.Pulling, OperationStages.Pulled, OperationStages.Created,
        OperationStages.Started, OperationStages.Ready,
    ];

    [Theory]
    [MemberData(nameof(LaunchStages))]
    public async Task AFakeAgentCrashAfterEachLaunchStageEndsInOneReadyInstanceAndNoReservation(string stage)
    {
        await using var node = await FakeNode.StartAsync(models: Model("qwen", image: AgentContractSeed.PullImage));
        await using var master = await node.Master().StartAsync();
        node.Agent.CrashAfterStage(OperationKinds.Launch, stage);

        var start = SendAsync(master.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        await UntilAsync(() => node.Agent.Crashed, $"the crash after '{stage}'");

        HttpStatusCode status;
        string body;
        if (stage == OperationStages.Admitted)
        {
            // Recorded inside the launch request, before its 202: the answer is lost and every retry of the master's client
            // hits the dead agent, so the admin API answers 503 (node unreachable), not 500.
            (status, body) = await start;
            Assert.True(status == HttpStatusCode.ServiceUnavailable, $"{(int)status} {body}");
            Assert.Contains("dgx", body, StringComparison.Ordinal);
            await node.Agent.RestartAsync();
        }
        else
        {
            // Accepted (202) before the crash: the master keeps polling the operation across the outage and answers once the
            // restarted agent reports the instance.
            await node.Agent.RestartAsync();
            (status, body) = await start;
            Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        }

        await master.WaitForModelAsync("qwen", ModelState.Ready);
        await AssertOneReadyInstanceAsync(master);
    }

    [Fact]
    public async Task TheRealAgentStoppedWhileTheInstanceStartsFinishesTheLaunchAfterARestart()
    {
        await using var node = await RealNode.StartAsync(models: Model("qwen"));
        await using var master = await node.Master().StartAsync();
        node.Node.StartsHeld = true; // the instance's /health answers 503: it stays Starting

        var (status, body) = await SendAsync(master.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        await UntilAsync(async () => (await master.Client.GetRuntimeAsync("qwen", Ct))?.State == ModelState.Starting, "qwen Starting");
        string bootBefore = (await master.Client.GetInfoAsync(Ct)).BootId;

        await node.StopAgentAsync();
        await Assert.ThrowsAsync<AgentUnreachableException>(() => master.Client.GetInfoAsync(Ct));
        await node.StartAgentAsync();
        node.Node.StartsHeld = false;

        await UntilAsync(async () =>
        {
            try
            {
                return (await master.Client.GetInfoAsync(Ct)).BootId != bootBefore;
            }
            catch (AgentUnreachableException)
            {
                return false;
            }
        }, "the master to reach the restarted agent");
        await master.WaitForModelAsync("qwen", ModelState.Ready);
        await AssertOneReadyInstanceAsync(master);
        Assert.Single(node.Node.Docker.ModelContainers);
    }

    private static async Task AssertOneReadyInstanceAsync(ExitMaster master)
    {
        var operations = await master.Client.ListOperationsAsync(new OperationsQuery(), Ct);
        var launch = Assert.Single(operations, o => o.Kind == OperationKinds.Launch && o.Target == "qwen");
        await UntilAsync(async () => (await master.Client.GetOperationAsync(launch.Id, Ct))?.State.IsTerminal() == true, "the launch to end");
        Assert.Equal(OperationState.Succeeded, (await master.Client.GetOperationAsync(launch.Id, Ct))?.State);

        var instance = Assert.Single(await master.Client.ListRuntimesAsync(new RuntimesQuery(), Ct));
        Assert.Equal("qwen", instance.ModelId);
        Assert.Equal(ModelState.Ready, instance.State);
        Assert.Equal(launch.Id, instance.OperationId);
        await UntilAsync(async () => (await master.Client.GetStatusAsync(Ct)).Reservations.Count == 0, "the start reservation to be released");

        var store = master.Get<IAgentOperationReferenceStore>();
        await UntilAsync(async () => (await store.GetAsync(launch.Id, Ct))?.Status == AgentOperationReferenceStatus.Closed, "the master's reference to close");
        Assert.Equal(AgentOperationOutcome.Succeeded, (await store.GetAsync(launch.Id, Ct))?.Outcome);
        Assert.DoesNotContain(await store.ListOpenAsync(Ct), r => r.Target == "qwen");
    }
}
