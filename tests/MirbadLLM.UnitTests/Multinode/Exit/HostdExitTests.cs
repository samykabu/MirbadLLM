using System.Net;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.UnitTests.Multinode.Contract;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// Design §13.6 "ambiguous hostd outcomes" (§4.4 item 8, D42): the agent killed after <c>dispatched</c> for a stop (a
/// desired state: reconciled to Succeeded), a restart and a backup-mount PUT (unverifiable: Unknown with
/// <c>outcome_unknown</c>, nothing replayed, a same-id retry answers it again), and a hostd timeout. Driven through the
/// master's Services API (<c>/api/services</c>) and its operation runner against FakeAgent's simulated hostd.
/// </summary>
public sealed class HostdExitTests
{
    private const string Unit = AgentContractSeed.Service;

    [Fact]
    public async Task AStopDispatchedBeforeAnAgentCrashIsReconciledToSucceeded()
    {
        await using var node = await FakeNode.StartAsync();
        await using var master = await node.Master().StartAsync();
        Assert.Equal("active", (await UnitAsync(master)).ActiveState);
        node.Agent.CrashAfterStage(OperationKinds.ServiceStop, OperationStages.Dispatched);

        // The agent dies right after recording 'dispatched': the master cannot know whether hostd acted.
        var (status, body) = await SendAsync(master.Http.PostAsync($"/api/services/{Unit}/stop", content: null, Ct));
        Assert.True(node.Agent.Crashed);
        Assert.True(status == HttpStatusCode.BadGateway, $"{(int)status} {body}");
        Assert.Contains("Outcome unknown", body, StringComparison.Ordinal);

        // After the restart the desired state is re-applied: the stop is Succeeded and the unit is stopped.
        await node.Agent.RestartAsync();
        var stop = await UntilValueAsync(async () => (await ListOperationsAsync(master)).FirstOrDefault(o => o.Kind == OperationKinds.ServiceStop && o.State.IsTerminal()),
            "the stop to be reconciled");
        Assert.Equal(OperationState.Succeeded, stop.State);
        Assert.Equal("inactive", (await UnitAsync(master)).ActiveState);
        var store = master.Get<IAgentOperationReferenceStore>();
        await UntilAsync(async () => (await store.GetAsync(stop.Id, Ct))?.Status == AgentOperationReferenceStatus.Closed, "the master's reference to close");
        Assert.Equal(AgentOperationOutcome.Succeeded, (await store.GetAsync(stop.Id, Ct))?.Outcome);
    }

    [Fact]
    public async Task ARestartDispatchedBeforeAnAgentCrashIsUnknownAndIsNotReplayed()
    {
        await using var node = await FakeNode.StartAsync();
        await using var master = await node.Master().StartAsync();
        node.Agent.CrashAfterStage(OperationKinds.ServiceRestart, OperationStages.Dispatched);

        // The agent dies right after recording 'dispatched'; whether hostd restarted the unit is not known to anyone.
        var (status, body) = await SendAsync(master.Http.PostAsync($"/api/services/{Unit}/restart", content: null, Ct));
        Assert.True(node.Agent.Crashed);
        Assert.True(status == HttpStatusCode.BadGateway, $"{(int)status} {body}");
        Assert.Contains("Outcome unknown", body, StringComparison.Ordinal);
        await node.Agent.RestartAsync();

        var restart = await UntilValueAsync(async () => (await ListOperationsAsync(master)).FirstOrDefault(o => o.Kind == OperationKinds.ServiceRestart && o.State.IsTerminal()),
            "the restart to be reconciled");
        Assert.Equal(OperationState.Unknown, restart.State);
        Assert.Equal(ErrorCodes.OutcomeUnknown, restart.Error?.Code);
        int? pidAfterRecovery = (await UnitAsync(master)).MainPid;

        // A same-id retry answers the unknown outcome again and still runs nothing.
        var retry = new HostServiceActionRequest { OperationId = restart.Id, IssuedAt = restart.IssuedAt, ExpiresAt = restart.ExpiresAt };
        var again = await Assert.ThrowsAsync<AgentException>(() => master.Client.RunHostServiceAsync(Unit, HostServiceAction.Restart, retry, Ct));
        Assert.Equal(504, again.StatusCode);
        Assert.Equal(ErrorCodes.OutcomeUnknown, again.Code);
        Assert.Equal(pidAfterRecovery, (await UnitAsync(master)).MainPid);
        Assert.Single(await ListOperationsAsync(master), o => o.Kind == OperationKinds.ServiceRestart);
    }

    [Fact]
    public async Task ABackupMountPutDispatchedBeforeAnAgentCrashIsUnknownAndIsNotReplayed()
    {
        await using var node = await FakeNode.StartAsync();
        await using var master = await node.Master().StartAsync();
        node.Agent.CrashAfterStage(OperationKinds.BackupMountSet, OperationStages.Dispatched);
        var envelope = master.Get<AgentOperationRunner>().CreateEnvelope();
        var put = new BackupMountSetRequest
        {
            OperationId = envelope.OperationId,
            IssuedAt = envelope.IssuedAt,
            ExpiresAt = envelope.ExpiresAt,
            Settings = new BackupMountSettings { Type = "smb", Server = "nas.local", Share = "backup", Username = "svc", Password = "exit-share-password" },
        };

        await Assert.ThrowsAsync<AgentUnreachableException>(() => master.Get<AgentOperationRunner>().SubmitAsync(
            NodeId, AgentRoutes.BackupMountSet, "backup-mount", put, (client, request, ct) => client.SetBackupMountAsync(request, ct), owner: null, Ct));
        Assert.True(node.Agent.Crashed);
        Assert.Equal(AgentOperationReferenceStatus.Open, (await master.Get<IAgentOperationReferenceStore>().GetAsync(put.OperationId, Ct))?.Status);
        await node.Agent.RestartAsync();

        var stored = await UntilValueAsync(async () => await master.Client.GetOperationAsync(put.OperationId, Ct) is { } o && o.State.IsTerminal() ? o : null,
            "the mount PUT to be reconciled");
        Assert.Equal(OperationState.Unknown, stored.State);
        Assert.Equal(ErrorCodes.OutcomeUnknown, stored.Error?.Code);
        Assert.Equal(1, (await ListOperationsAsync(master)).Count(o => o.Kind == OperationKinds.BackupMountSet));

        // A same-id retry answers the unknown outcome again; nothing new is recorded or run.
        var again = await Assert.ThrowsAsync<AgentException>(() => master.Client.SetBackupMountAsync(put, Ct));
        Assert.Equal(504, again.StatusCode);
        Assert.Equal(ErrorCodes.OutcomeUnknown, again.Code);
        Assert.Equal(OperationState.Unknown, (await master.Client.GetOperationAsync(put.OperationId, Ct))?.State);
        Assert.Equal(1, (await ListOperationsAsync(master)).Count(o => o.Kind == OperationKinds.BackupMountSet));
        Assert.DoesNotContain("exit-share-password", System.Text.Json.JsonSerializer.Serialize(stored, AgentJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostdTimeoutIsOutcomeUnknownAtTheServicesApiAndOnRetry()
    {
        await using var node = await FakeNode.StartAsync();
        await using var master = await node.Master().StartAsync();
        int? pidBefore = (await UnitAsync(master)).MainPid;
        node.Agent.SetHostdTimingOut(true);

        var (status, body) = await SendAsync(master.Http.PostAsync($"/api/services/{Unit}/restart", content: null, Ct));

        Assert.True(status == HttpStatusCode.BadGateway, $"{(int)status} {body}");
        Assert.Contains("Outcome unknown", body, StringComparison.Ordinal);
        var restart = Assert.Single(await ListOperationsAsync(master), o => o.Kind == OperationKinds.ServiceRestart);
        Assert.Equal(OperationState.Unknown, restart.State);
        Assert.Equal(ErrorCodes.OutcomeUnknown, restart.Error?.Code);

        node.Agent.SetHostdTimingOut(false);
        var retry = new HostServiceActionRequest { OperationId = restart.Id, IssuedAt = restart.IssuedAt, ExpiresAt = restart.ExpiresAt };
        var again = await Assert.ThrowsAsync<AgentException>(() => master.Client.RunHostServiceAsync(Unit, HostServiceAction.Restart, retry, Ct));
        Assert.Equal(ErrorCodes.OutcomeUnknown, again.Code);
        Assert.Equal(pidBefore, (await UnitAsync(master)).MainPid);
    }

    private static async Task<HostUnit> UnitAsync(ExitMaster master) =>
        (await master.Client.ListHostServicesAsync(new HostServicesQuery(), Ct)).Units.Single(u => u.Name == Unit);

    private static Task<IReadOnlyList<OperationStatus>> ListOperationsAsync(ExitMaster master) =>
        master.Client.ListOperationsAsync(new OperationsQuery(), Ct);
}
