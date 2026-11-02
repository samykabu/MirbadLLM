using System.Text;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.UnitTests.Multinode.Fakes;
using ManualClock = MirbadLLM.UnitTests.Orchestration.Fakes.ManualTimeProvider;

namespace MirbadLLM.UnitTests.Multinode.Contract;

/// <summary>The shared agent contract suite against <see cref="FakeAgent"/> (MN-022). MN-026 runs the same suite against the real agent.</summary>
public sealed class FakeAgentContractTests : AgentContractSuite
{
    /// <inheritdoc/>
    protected override async Task<IAgentContractHarness> StartAgentAsync() => await FakeAgent.StartAsync(ct: Ct);

    /// <inheritdoc/>
    protected override async Task<IAgentContractHarness> StartAgentWithoutHostFeaturesAsync() =>
        await FakeAgent.StartAsync(new FakeAgentOptions
        {
            Features = [.. FakeAgentOptions.P1Features.Except(
                [AgentFeatures.ServicesSystemd, AgentFeatures.ServicesDocker, AgentFeatures.BackupMount, AgentFeatures.GpuProcesses])],
        }, Ct);
}

/// <summary>
/// The <see cref="FakeAgent"/> failure knobs MN-043/MN-044 rely on (restart, crash between stages, lost acknowledgement,
/// partition, hostd outcomes) and protocol rules the shared suite cannot reach through a fresh agent (ring eviction,
/// retention, versions, features).
/// </summary>
public sealed class FakeAgentKnobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARestartIsANewBootThatKeepsTheOperationLog()
    {
        await using var agent = await FakeAgent.StartAsync(ct: Ct);
        using var client = new AgentContractClient(agent.Target);
        var stop = AgentContractSuite.Stop();
        var first = (await client.SendAsync(AgentRoutes.RuntimeStop, ["model-a"], stop, ct: Ct)).Read<OperationStatus>();
        await WaitTerminalAsync(client, stop.OperationId);
        string oldBoot = agent.BootId;
        long oldSeq = (await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>().Seq;

        await agent.RestartAsync();

        var info = (await client.SendAsync(AgentRoutes.Info, [], ct: Ct)).Read<InfoResponse>();
        Assert.NotEqual(oldBoot, info.BootId);
        Assert.Equal(client.Master, info.AcceptedMaster);
        await using (var events = await client.OpenEventsAsync(oldBoot, oldSeq, ct: Ct))
        {
            var reset = Assert.IsType<ResetEvent>((await events.NextAsync(TimeSpan.FromSeconds(10), Ct))?.Event);
            Assert.Equal(ResetEvent.BootChanged, reset.Reason);
        }

        var retry = await client.SendAsync(AgentRoutes.RuntimeStop, ["model-a"], stop, ct: Ct);
        Assert.Equal(202, retry.Status);
        var stored = retry.Read<OperationStatus>();
        Assert.Equal(first.StartedAt, stored.StartedAt);
        Assert.Equal(OperationState.Succeeded, stored.State);
    }

    [Fact]
    public async Task ACrashBetweenCreateAndStartIsReconciledAtRestart()
    {
        await using var agent = await FakeAgent.StartAsync(ct: Ct);
        using var client = new AgentContractClient(agent.Target);
        agent.CrashAfterStage(OperationKinds.Launch, OperationStages.Created);
        var launch = AgentContractSuite.LaunchFor("model-a");

        await SendIgnoringTransportAsync(client, AgentRoutes.RuntimeLaunch, ["model-a"], launch);
        await EventuallyAsync(() => agent.Crashed, "the crash");
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.SendAsync(AgentRoutes.Info, [], ct: Ct));

        await agent.RestartAsync();

        var final = await WaitTerminalAsync(client, launch.OperationId);
        Assert.Equal(OperationState.Succeeded, final.State);
        var runtimes = (await client.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct)).Read<List<RuntimeStatus>>();
        var instance = Assert.Single(runtimes);
        Assert.Equal(launch.OperationId, instance.OperationId);
        Assert.Empty((await client.SendAsync(AgentRoutes.Status, [], ct: Ct)).Read<StatusSnapshot>().Reservations);
    }

    [Fact]
    public async Task ALostAcknowledgementIsResolvedByRetryingTheSameOperation()
    {
        await using var agent = await FakeAgent.StartAsync(ct: Ct);
        using var client = new AgentContractClient(agent.Target);
        agent.DropResponses(route => route == AgentRoutes.RuntimeLaunch);
        var launch = AgentContractSuite.LaunchFor("model-a");

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.SendAsync(AgentRoutes.RuntimeLaunch, ["model-a"], launch, ct: Ct));
        agent.DropResponses(null);
        var retry = await client.SendAsync(AgentRoutes.RuntimeLaunch, ["model-a"], launch, ct: Ct);

        Assert.Equal(202, retry.Status);
        Assert.Equal(launch.OperationId, retry.Read<OperationStatus>().Id);
        Assert.Equal(OperationState.Succeeded, (await WaitTerminalAsync(client, launch.OperationId)).State);
        Assert.Single((await client.SendAsync(AgentRoutes.RuntimesList, [], ct: Ct)).Read<List<RuntimeStatus>>());
    }

    [Fact]
    public async Task APartitionCutsRequestsWhileOperationsContinue()
    {
        await using var agent = await FakeAgent.StartAsync(ct: Ct);
        using var client = new AgentContractClient(agent.Target);
        var launch = AgentContractSuite.LaunchFor("model-a");
        Assert.Equal(202, (await client.SendAsync(AgentRoutes.RuntimeLaunch, ["model-a"], launch, ct: Ct)).Status);

        agent.SetPartitioned(true);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.SendAsync(AgentRoutes.Status, [], ct: Ct));
        await Task.Delay(200, Ct);
        agent.SetPartitioned(false);

        Assert.Equal(OperationState.Succeeded, (await WaitTerminalAsync(client, launch.OperationId)).State);
    }

    [Fact]
    public async Task ACursorEvictedFromTheRingGetsAReset()
    {
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions { EventRingCapacity = 4 }, Ct);
        using var client = new AgentContractClient(agent.Target);
        for (int i = 0; i < 4; i++)
        {
            var stop = AgentContractSuite.Stop();
            await client.SendAsync(AgentRoutes.RuntimeStop, [$"model-{i}"], stop, ct: Ct);
            await WaitTerminalAsync(client, stop.OperationId);
        }

        await using var events = await client.OpenEventsAsync(agent.BootId, 1, ct: Ct);

        var reset = Assert.IsType<ResetEvent>((await events.NextAsync(TimeSpan.FromSeconds(10), Ct))?.Event);
        Assert.Equal(ResetEvent.Evicted, reset.Reason);
        Assert.Null(await events.NextAsync(TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task APrunedOperationIsGoneOnceItsEnvelopeExpired()
    {
        var clock = new ManualClock();
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions { Time = clock, MinRetainedRecords = 0 }, Ct);
        using var client = new AgentContractClient(agent.Target);
        var now = clock.GetUtcNow();
        var stop = new StopRequest { OperationId = AgentContractSuite.NewId(), IssuedAt = now, ExpiresAt = now.AddHours(24) };
        await client.SendAsync(AgentRoutes.RuntimeStop, ["model-a"], stop, options: new AgentRequestOptions { RequestTime = now }, ct: Ct);
        await WaitTerminalAsync(client, stop.OperationId);

        clock.Advance(TimeSpan.FromHours(25));
        var later = clock.GetUtcNow();
        var other = new StopRequest { OperationId = AgentContractSuite.NewId(), IssuedAt = later, ExpiresAt = later.AddHours(24) };
        Assert.Equal(202, (await client.SendAsync(AgentRoutes.RuntimeStop, ["model-b"], other, options: new AgentRequestOptions { RequestTime = later }, ct: Ct)).Status);

        var retry = await client.SendAsync(AgentRoutes.RuntimeStop, ["model-a"], stop, options: new AgentRequestOptions { RequestTime = later }, ct: Ct);
        Assert.Equal(410, retry.Status);
        Assert.Equal(ErrorCodes.OperationExpired, retry.Read<AgentErrorResponse>().Error.Code);
        Assert.Equal(404, (await client.SendAsync(AgentRoutes.OperationGet, [stop.OperationId], ct: Ct)).Status);
    }

    [Fact]
    public async Task ARouteOfAMissingFeatureIsNotSupported()
    {
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions { Features = [AgentFeatures.RuntimeVllmContainer] }, Ct);
        using var client = new AgentContractClient(agent.Target);

        var telemetry = await client.SendAsync(AgentRoutes.Telemetry, [], ct: Ct);
        var download = await client.SendAsync(AgentRoutes.DownloadStart, [], AgentContractSuite.Download("x"), ct: Ct);

        Assert.Equal(501, telemetry.Status);
        Assert.Equal(ErrorCodes.NotSupported, telemetry.Read<AgentErrorResponse>().Error.Code);
        Assert.Equal(501, download.Status);
    }

    [Fact]
    public async Task ANonOverlappingAgentAnswersOnlyInfo()
    {
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions { Protocol = new ProtocolRange(Protocol.CurrentVersion + 1, Protocol.CurrentVersion + 1) }, Ct);
        using var client = new AgentContractClient(agent.Target);

        var info = (await client.SendAsync(AgentRoutes.Info, [], ct: Ct)).Read<InfoResponse>();
        var status = await client.SendAsync(AgentRoutes.Status, [], ct: Ct);

        Assert.Null(Protocol.Negotiate(Protocol.Range, info.Protocol));
        Assert.Equal(400, status.Status);
        Assert.Equal(ErrorCodes.ProtocolMismatch, status.Read<AgentErrorResponse>().Error.Code);
    }

    [Theory]
    [InlineData(OperationKinds.ServiceRestart, OperationState.Unknown)]
    [InlineData(OperationKinds.ServiceStop, OperationState.Succeeded)]
    public async Task AHostActionDispatchedBeforeACrashIsReconciledOrUnknown(string kind, OperationState expected)
    {
        await using var agent = await FakeAgent.StartAsync(ct: Ct);
        using var client = new AgentContractClient(agent.Target);
        agent.CrashAfterStage(kind, OperationStages.Dispatched);
        var route = kind == OperationKinds.ServiceRestart ? AgentRoutes.HostServiceRestart : AgentRoutes.HostServiceStop;
        var request = new HostServiceActionRequest { OperationId = AgentContractSuite.NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };

        await SendIgnoringTransportAsync(client, route, [AgentContractSeed.Service], request);
        Assert.True(agent.Crashed);
        await agent.RestartAsync();

        var op = (await client.SendAsync(AgentRoutes.OperationGet, [request.OperationId], ct: Ct)).Read<OperationStatus>();
        Assert.Equal(expected, op.State);
        var services = (await client.SendAsync(AgentRoutes.HostServicesList, [], ct: Ct)).Read<HostServicesResponse>();
        var unit = Assert.Single(services.Units, u => u.Name == AgentContractSeed.Service);
        Assert.Equal(kind == OperationKinds.ServiceStop ? "inactive" : "active", unit.ActiveState);
    }

    [Fact]
    public async Task AHostdTimeoutEndsUnknownAndIsNotReplayed()
    {
        await using var agent = await FakeAgent.StartAsync(ct: Ct);
        using var client = new AgentContractClient(agent.Target);
        agent.SetHostdTimingOut(true);
        var request = new HostServiceActionRequest { OperationId = AgentContractSuite.NewId(), IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };

        var first = await client.SendAsync(AgentRoutes.HostServiceRestart, [AgentContractSeed.Service], request, ct: Ct);
        agent.SetHostdTimingOut(false);
        var retry = await client.SendAsync(AgentRoutes.HostServiceRestart, [AgentContractSeed.Service], request, ct: Ct);

        Assert.Equal(504, first.Status);
        Assert.Equal(ErrorCodes.OutcomeUnknown, first.Read<AgentErrorResponse>().Error.Code);
        // The same id replays the same answer (nothing is re-sent to hostd); the stored operation is Unknown.
        Assert.Equal(504, retry.Status);
        Assert.Equal(ErrorCodes.OutcomeUnknown, retry.Read<AgentErrorResponse>().Error.Code);
        var stored = (await client.SendAsync(AgentRoutes.OperationGet, [request.OperationId], ct: Ct)).Read<OperationStatus>();
        Assert.Equal(OperationState.Unknown, stored.State);
        Assert.Equal(ErrorCodes.OutcomeUnknown, stored.Error?.Code);
    }

    [Fact]
    public async Task TheProxyInFlightLimitIsServerBusy()
    {
        await using var agent = await FakeAgent.StartAsync(new FakeAgentOptions { MaxInFlightProxy = 0 }, Ct);
        using var client = new AgentContractClient(agent.Target);
        var launch = AgentContractSuite.LaunchFor("model-a");
        await client.SendAsync(AgentRoutes.RuntimeLaunch, ["model-a"], launch, ct: Ct);
        await WaitTerminalAsync(client, launch.OperationId);

        using var response = await client.ProxyAsync("model-a", "health", HttpMethod.Get, ct: Ct);

        Assert.Equal(503, (int)response.StatusCode);
        Assert.Equal("5", response.Headers.RetryAfter?.ToString());
        Assert.Contains(ErrorCodes.ServerBusy, await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOperationLogIsWrittenBeforeTheAnswerAndHoldsNoSecret()
    {
        await using var agent = await FakeAgent.StartAsync(ct: Ct);
        using var client = new AgentContractClient(agent.Target);
        var download = AgentContractSuite.Download("model-new") with { Token = "hf_logSecret" };

        Assert.Equal(202, (await client.SendAsync(AgentRoutes.DownloadStart, [], download, ct: Ct)).Status);

        string log;
        await using (var stream = new FileStream(Path.Combine(agent.StateDirectory, OperationRetention.LogRelativePath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            log = await reader.ReadToEndAsync(Ct);
        }

        Assert.Contains(download.OperationId, log, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"accepted\"", log, StringComparison.Ordinal);
        Assert.DoesNotContain("hf_logSecret", log, StringComparison.Ordinal);
        Assert.Contains(Protocol.RedactedValue, log, StringComparison.Ordinal);
    }

    private static async Task SendIgnoringTransportAsync(AgentContractClient client, AgentRoute route, string[] path, object body)
    {
        try
        {
            await client.SendAsync(route, path, body, ct: Ct);
        }
        catch (HttpRequestException)
        {
            // The simulated crash cut the connection.
        }
    }

    private static async Task<OperationStatus> WaitTerminalAsync(AgentContractClient client, string id)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            var reply = await client.SendAsync(AgentRoutes.OperationGet, [id], ct: Ct);
            if (reply.Status == 200 && reply.Read<OperationStatus>() is { } op && op.State.IsTerminal())
            {
                return op;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Operation {id} did not finish.");
            await Task.Delay(20, Ct);
        }
    }

    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(20, Ct);
        }
    }
}
