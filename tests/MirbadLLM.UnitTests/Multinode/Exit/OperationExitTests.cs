using System.Net;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.UnitTests.Multinode.Contract;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// Design §13.6, operation identity: lost acknowledgement, duplicate ids with another payload, expired ids. Each test drives
/// the real master composition (<see cref="ExitMaster"/>: admin API, its <see cref="AgentOperationRunner"/> and agent
/// client) against FakeAgent and, where the scenario needs no failure knob, the real agent too.
/// </summary>
public sealed class OperationExitTests
{
    public static TheoryData<string> Agents => ["fake", "real"];

    [Fact]
    public async Task ALostLaunchAcknowledgementIsRetriedWithTheSameIdAndCreatesOneInstance()
    {
        await using var node = await FakeNode.StartAsync(models: Model("qwen"));
        await using var master = await node.Master().StartAsync();
        int dropped = 0;
        // The agent accepts and executes the launch durably, then the answer is lost (connection aborted), once.
        node.Agent.DropResponses(route => route == AgentRoutes.RuntimeLaunch && Interlocked.Exchange(ref dropped, 1) == 0);

        var (status, body) = await SendAsync(master.Http.PostAsync("/api/models/qwen/start", content: null, Ct));

        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        Assert.Equal(1, Volatile.Read(ref dropped));

        // The agent knows one launch of qwen; the master's retry carried the same operation id.
        var operations = await master.Client.ListOperationsAsync(new OperationsQuery(), Ct);
        var launch = Assert.Single(operations, o => o.Kind == OperationKinds.Launch && o.Target == "qwen");
        var references = await master.Get<IAgentOperationReferenceStore>().ListAsync(Ct);
        var reference = Assert.Single(references, r => r.Action == OperationKinds.Launch && r.Target == "qwen");
        Assert.Equal(launch.Id, reference.OperationId);
        Assert.Equal(launch.PayloadDigest, reference.PayloadDigest);

        // One instance, created by that operation, which becomes Ready and is served.
        var runtimes = await master.Client.ListRuntimesAsync(new RuntimesQuery(), Ct);
        var instance = Assert.Single(runtimes, r => r.ModelId == "qwen");
        Assert.Equal(launch.Id, instance.OperationId);
        await master.WaitForModelAsync("qwen", ModelState.Ready);
        await UntilAsync(async () => (await master.Get<IAgentOperationReferenceStore>().GetAsync(launch.Id, Ct))?.Status == AgentOperationReferenceStatus.Closed,
            "the launch reference to close");
        Assert.Equal(AgentOperationOutcome.Succeeded, (await master.Get<IAgentOperationReferenceStore>().GetAsync(launch.Id, Ct))!.Outcome);
        Assert.Single(await master.Client.ListRuntimesAsync(new RuntimesQuery(), Ct), r => r.ModelId == "qwen");
    }

    [Theory]
    [MemberData(nameof(Agents))]
    public async Task TheSameOperationIdWithAnotherPayloadIsOperationIdConflictAndRunsNothing(string agent)
    {
        await using var env = await AgentUnderMaster.StartAsync(agent);
        var runner = env.Master.Get<AgentOperationRunner>();
        var launch = AgentContractSuite.LaunchFor("exit-a");

        var first = await runner.SubmitAsync(NodeId, AgentRoutes.RuntimeLaunch, "exit-a", launch,
            (client, request, ct) => client.LaunchAsync("exit-a", request, ct), owner: null, Ct);
        var changed = launch with { Spec = launch.Spec with { Argv = [.. launch.Spec.Argv, "--max-model-len", "4096"] } };
        var conflict = await Assert.ThrowsAsync<AgentException>(() =>
            env.Master.Client.LaunchAsync("exit-a", changed, Ct));

        Assert.Equal(409, conflict.StatusCode);
        Assert.Equal(ErrorCodes.OperationIdConflict, conflict.Code);
        var stored = await env.Master.Client.GetOperationAsync(launch.OperationId, Ct);
        Assert.Equal(first.PayloadDigest, stored?.PayloadDigest);
        await UntilAsync(async () => (await env.Master.Client.GetRuntimeAsync("exit-a", Ct))?.State == ModelState.Ready, "exit-a Ready");
        var instance = Assert.Single(await env.Master.Client.ListRuntimesAsync(new RuntimesQuery(), Ct), r => r.ModelId == "exit-a");
        Assert.Equal(launch.OperationId, instance.OperationId);
        Assert.DoesNotContain("4096", string.Join(' ', (await env.Master.Client.GetLaunchConfigAsync("exit-a", Ct))!.Argv), StringComparison.Ordinal);

        // The same id for another kind (a stop) is a conflict too and stops nothing; the master's reference is the launch's.
        var reused = AgentContractSuite.Stop() with { OperationId = launch.OperationId, IssuedAt = launch.IssuedAt, ExpiresAt = launch.ExpiresAt };
        var otherKind = await Assert.ThrowsAsync<AgentException>(() => env.Master.Client.StopAsync("exit-a", reused, Ct));
        Assert.Equal(ErrorCodes.OperationIdConflict, otherKind.Code);
        Assert.Equal(ModelState.Ready, (await env.Master.Client.GetRuntimeAsync("exit-a", Ct))?.State);
        var reference = await env.Master.Get<IAgentOperationReferenceStore>().GetAsync(launch.OperationId, Ct);
        Assert.Equal(OperationKinds.Launch, reference?.Action);
        Assert.Equal(first.PayloadDigest, reference?.PayloadDigest);
    }

    [Theory]
    [MemberData(nameof(Agents))]
    public async Task ExpiredAndFutureEnvelopesAreRefusedAndNothingIsReExecuted(string agent)
    {
        await using var env = await AgentUnderMaster.StartAsync(agent);
        var client = env.Master.Client;
        var now = DateTimeOffset.UtcNow;

        // An unknown id whose envelope already expired: 410 operation_expired, nothing recorded, reserved or created.
        var expired = AgentContractSuite.LaunchFor("exit-expired") with { IssuedAt = now.AddHours(-25), ExpiresAt = now.AddHours(-1) };
        var gone = await Assert.ThrowsAsync<AgentException>(() => client.LaunchAsync("exit-expired", expired, Ct));
        Assert.Equal(410, gone.StatusCode);
        Assert.Equal(ErrorCodes.OperationExpired, gone.Code);
        Assert.Null(await client.GetOperationAsync(expired.OperationId, Ct));
        Assert.Null(await client.GetRuntimeAsync("exit-expired", Ct));
        Assert.Empty((await client.GetStatusAsync(Ct)).Reservations);

        // issuedAt far in the future: refused (400 clock_skew), nothing recorded.
        var future = AgentContractSuite.Stop() with { IssuedAt = now.AddMinutes(10), ExpiresAt = now.AddMinutes(10).AddHours(24) };
        var skew = await Assert.ThrowsAsync<AgentException>(() => client.StopAsync("exit-a", future, Ct));
        Assert.Equal(400, skew.StatusCode);
        Assert.Equal(ErrorCodes.ClockSkew, skew.Code);
        Assert.Null(await client.GetOperationAsync(future.OperationId, Ct));

        // A launch with a short envelope runs once; the same id with a changed expiresAt is a conflict.
        var shortLived = AgentContractSuite.LaunchFor("exit-a") with { IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(3) };
        var accepted = await client.LaunchAsync("exit-a", shortLived, Ct);
        var changedExpiry = await Assert.ThrowsAsync<AgentException>(() => client.LaunchAsync("exit-a", shortLived with { ExpiresAt = shortLived.ExpiresAt.AddMinutes(1) }, Ct));
        Assert.Equal(409, changedExpiry.StatusCode);
        Assert.Equal(ErrorCodes.OperationIdConflict, changedExpiry.Code);
        await UntilAsync(async () => (await client.GetRuntimeAsync("exit-a", Ct))?.State == ModelState.Ready, "exit-a Ready");
        string? instanceId = (await client.GetRuntimeAsync("exit-a", Ct))!.InstanceId;

        // After expiresAt the retry is answered from the log (design §4.4 item 2: a known id is answered whatever the clock
        // says, until retention prunes it) and nothing runs again: same operation, same instance, one instance.
        await UntilAsync(() => DateTimeOffset.UtcNow > shortLived.ExpiresAt.AddMilliseconds(200), "the envelope to expire");
        var retried = await client.LaunchAsync("exit-a", shortLived, Ct);
        Assert.Equal(accepted.Id, retried.Id);
        Assert.Equal(OperationState.Succeeded, retried.State);
        var instance = Assert.Single(await client.ListRuntimesAsync(new RuntimesQuery(), Ct), r => r.ModelId == "exit-a");
        Assert.Equal(instanceId, instance.InstanceId);
        Assert.Single(await client.ListOperationsAsync(new OperationsQuery(), Ct), o => o.Kind == OperationKinds.Launch && o.Target == "exit-a");
    }
}

/// <summary>The real master over FakeAgent ("fake") or the real agent ("real") as <c>dgx</c>, with no models.</summary>
internal sealed class AgentUnderMaster : IAsyncDisposable
{
    private readonly FakeNode? _fake;
    private readonly RealNode? _real;

    private AgentUnderMaster(FakeNode? fake, RealNode? real, ExitMaster master)
    {
        _fake = fake;
        _real = real;
        Master = master;
    }

    public ExitMaster Master { get; }

    public FakeNode Fake => _fake ?? throw new InvalidOperationException("Not a FakeAgent set-up.");

    public RealNode Real => _real ?? throw new InvalidOperationException("Not a real-agent set-up.");

    /// <summary>Holds instances in Starting (FakeAgent's start knob; the real agent's instances answer <c>/health</c> 503).</summary>
    public async Task SetStartsHeldAsync(bool held)
    {
        if (_fake is not null)
        {
            await _fake.Agent.SetStartsHeldAsync(held);
        }

        if (_real is not null)
        {
            _real.Node.StartsHeld = held;
        }
    }

    public static async Task<AgentUnderMaster> StartAsync(string agent, params ModelDefinition[] models)
    {
        if (agent == "fake")
        {
            var fake = await FakeNode.StartAsync(models: models);
            return new AgentUnderMaster(fake, null, await fake.Master().StartAsync());
        }

        var real = await RealNode.StartAsync(models: models);
        return new AgentUnderMaster(null, real, await real.Master().StartAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await Master.DisposeAsync();
        if (_fake is not null)
        {
            await _fake.DisposeAsync();
        }

        if (_real is not null)
        {
            await _real.DisposeAsync();
        }
    }
}
