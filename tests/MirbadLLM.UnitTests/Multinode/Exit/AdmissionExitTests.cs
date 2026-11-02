using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Multinode.Contract;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode.P1Exit;

/// <summary>
/// Design §13.6 "concurrent admission" on the unified P1 node, driven through the master's admin API: N parallel starts
/// admit exactly one (one reservation), every other answers 409 with today's text plus "on node dgx"; once the first is
/// Ready the others start one by one on distinct ports. Also D64: a start cancelled before its container existed answers
/// 409.
/// </summary>
public sealed class AdmissionExitTests
{
    private static readonly string[] Models = ["m-a", "m-b", "m-c", "m-d"];

    [Theory]
    [InlineData("fake")]
    [InlineData("real")]
    public async Task ParallelStartsOnTheUnifiedNodeAdmitExactlyOneAndRefuseTheOthersWithTodaysTextOnNodeDgx(string agent)
    {
        await using var env = await AgentUnderMaster.StartAsync(agent, [.. Models.Select(id => Model(id))]);
        await env.SetStartsHeldAsync(true);

        var answers = await Task.WhenAll(Models.Select(async id =>
            (Id: id, Answer: await SendAsync(env.Master.Http.PostAsync($"/api/models/{id}/start", content: null, Ct)))));

        var admitted = Assert.Single(answers, a => a.Answer.Status == HttpStatusCode.OK);
        foreach (var (id, (status, body)) in answers.Where(a => a.Id != admitted.Id))
        {
            Assert.True(status == HttpStatusCode.Conflict, $"{id}: {(int)status} {body}");
            Assert.Equal(
                $"Model '{admitted.Id}' is still starting. Start '{id}' once it is Ready: vLLM measures memory while it loads, " +
                "so two models starting together both fail on node dgx.",
                Detail(body));
        }

        var client = env.Master.Client;
        var reservation = Assert.Single((await client.GetStatusAsync(Ct)).Reservations);
        Assert.Equal(admitted.Id, reservation.ModelId);
        var only = Assert.Single(await client.ListRuntimesAsync(new RuntimesQuery(), Ct));
        Assert.Equal(admitted.Id, only.ModelId);
        Assert.Equal(ModelState.Starting, only.State);

        // Ready releases the reservation; the others then start one at a time, each on its own port.
        await env.SetStartsHeldAsync(false);
        await env.Master.WaitForModelAsync(admitted.Id, ModelState.Ready);
        await UntilAsync(async () => (await client.GetStatusAsync(Ct)).Reservations.Count == 0, "the reservation to be released on Ready");
        foreach (string id in Models.Where(m => m != admitted.Id))
        {
            var (status, body) = await SendAsync(env.Master.Http.PostAsync($"/api/models/{id}/start", content: null, Ct));
            Assert.True(status == HttpStatusCode.OK, $"{id}: {(int)status} {body}");
            await env.Master.WaitForModelAsync(id, ModelState.Ready);
        }

        var all = await client.ListRuntimesAsync(new RuntimesQuery(), Ct);
        Assert.Equal(Models.Order(StringComparer.Ordinal), all.Select(r => r.ModelId).Order(StringComparer.Ordinal));
        Assert.All(all, r => Assert.Equal(ModelState.Ready, r.State));
        Assert.Equal(all.Count, all.Select(r => r.HostPort).Distinct().Count());
        if (agent == "real")
        {
            Assert.Equal(Models.Length, env.Real.Node.Docker.ModelContainers.Count);
        }
    }

    [Fact]
    public async Task AStartCancelledBeforeItsContainerExistedAnswers409AndCreatesNothing()
    {
        // W4 review (MN-096, D64): "a start ended before create is 409". Within one master a restart of the same model
        // queues behind the pending start (one launch per model at a time), so the launch is ended by a cancel the master's
        // own client sends while the admin start waits for the instance.
        await using var env = await AgentUnderMaster.StartAsync("fake", Model("qwen", image: AgentContractSeed.PullImage));
        var agent = env.Fake.Agent;
        await agent.SetPullsHeldAsync(true);
        var start = SendAsync(env.Master.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        var pending = await UntilValueAsync(async () => (await env.Master.Client.ListOperationsAsync(new OperationsQuery { State = OperationsQuery.ActiveState }, Ct))
            .FirstOrDefault(o => o.Kind == OperationKinds.Launch && o.Target == "qwen" && o.Stage == OperationStages.Pulling), "the start to pull");

        var cancel = await env.Master.Client.CancelOperationAsync(pending.Id, AgentContractSuite.Cancel(), Ct);
        Assert.Equal(OperationState.Succeeded, cancel.State);

        var (status, body) = await start;
        Assert.True(status == HttpStatusCode.Conflict, $"{(int)status} {body}");
        Assert.Equal("The start of model 'qwen' ended cancelled before its container was created. (on node dgx)", Detail(body));
        Assert.Equal(OperationState.Cancelled, (await env.Master.Client.GetOperationAsync(pending.Id, Ct))?.State);
        await agent.SetPullsHeldAsync(false);
        Assert.Empty(await env.Master.Client.ListRuntimesAsync(new RuntimesQuery(), Ct));
        Assert.Empty((await env.Master.Client.GetStatusAsync(Ct)).Reservations);

        // A new start launches once.
        var (again, againBody) = await SendAsync(env.Master.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        Assert.True(again == HttpStatusCode.OK, $"{(int)again} {againBody}");
        await env.Master.WaitForModelAsync("qwen", ModelState.Ready);
        var instance = Assert.Single(await env.Master.Client.ListRuntimesAsync(new RuntimesQuery(), Ct));
        Assert.NotEqual(pending.Id, instance.OperationId);
    }

    internal static string Detail(string problemJson)
    {
        using var doc = JsonDocument.Parse(problemJson);
        return doc.RootElement.GetProperty("detail").GetString() ?? "";
    }
}

/// <summary>
/// W3/W4 review expectations on a node that cannot take a command (D51, D57, D62): 409 for unplaced or draining, 503 for a
/// node that is not serving or cannot be reached, 503 for folder reads of an unreachable node, and a download while Docker
/// is down.
/// </summary>
public sealed class UnavailableNodeExitTests
{
    [Fact]
    public async Task StartsOfUnplacedAndDrainingModelsAre409AndOfAnUnreachableNode503()
    {
        await using var node = await FakeNode.StartAsync(models: [Model("qwen"), Model("loose") with { Placement = null }]);
        await using var master = await node.Master().StartAsync();

        var (unplaced, unplacedBody) = await SendAsync(master.Http.PostAsync("/api/models/loose/start", content: null, Ct));
        Assert.True(unplaced == HttpStatusCode.Conflict, $"{(int)unplaced} {unplacedBody}");
        Assert.Equal("Model 'loose' is not placed on a node.", AdmissionExitTests.Detail(unplacedBody));

        // Unreachable now (partition): 503, nothing launched.
        node.Agent.SetPartitioned(true);
        var (unreachable, unreachableBody) = await SendAsync(master.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        Assert.True(unreachable == HttpStatusCode.ServiceUnavailable, $"{(int)unreachable} {unreachableBody}");
        Assert.Contains("dgx", unreachableBody, StringComparison.Ordinal);
        node.Agent.SetPartitioned(false);
        await UntilAsync(async () =>
        {
            try
            {
                return (await master.Client.GetRuntimeAsync("qwen", Ct)) is null;
            }
            catch (AgentUnreachableException)
            {
                return false;
            }
        }, "the agent to answer again, with no qwen instance");

        // Draining (the registry flag; the master learns it through the registry sync): 409 for a new start.
        using (var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = master.DataDir }), TimeProvider.System))
        {
            await registry.UpdateAsync(NodeId, r => r with { Draining = true }, Ct);
        }

        await master.WaitForNodeAsync(n => n.Draining, "the draining flag to reach the master");
        var (draining, drainingBody) = await SendAsync(master.Http.PostAsync("/api/models/qwen/start", content: null, Ct));
        Assert.True(draining == HttpStatusCode.Conflict, $"{(int)draining} {drainingBody}");
        Assert.StartsWith("Node 'dgx' is draining", AdmissionExitTests.Detail(drainingBody), StringComparison.Ordinal);
        Assert.Null(await master.Client.GetRuntimeAsync("qwen", Ct));
    }

    [Fact]
    public async Task AStartAndAStopWhileDockerIsDownAre503WithTheReason()
    {
        // MN-044 observation, fixed by MN-047: a node dependency that is down answers the agent's own 503, never 500.
        await using var node = await RealNode.StartAsync(o => o.DockerStartupTimeout = TimeSpan.Zero, n => n.Docker.Down = true, Model("qwen"));
        await using var master = await node.Master().StartAsync(state: NodeState.Degraded);

        foreach (string path in new[] { "/api/models/qwen/start", "/api/models/qwen/stop" })
        {
            var (status, body) = await SendAsync(master.Http.PostAsync(path, content: null, Ct));
            Assert.True(status == HttpStatusCode.ServiceUnavailable, $"{path}: {(int)status} {body}");
            Assert.Contains("on node dgx", body, StringComparison.Ordinal);
        }

        Assert.Empty(node.Node.Docker.ModelContainers);
    }

    [Fact]
    public async Task FolderReadsOfAnUnreachableRealAgentAre503WithTheReason()
    {
        // W4 review (MN-096, D62): an unreachable node is neither "no folders" (empty list) nor "no such folder" (404).
        await using var node = await RealNode.StartAsync();
        await using var master = await node.Master().StartAsync();
        var (online, onlineBody) = await SendAsync(master.Http.GetAsync($"/api/model-folders/{AgentContractSeed.ModelFolder}", Ct));
        Assert.True(online == HttpStatusCode.OK, $"{(int)online} {onlineBody}");

        await node.StopAgentAsync();

        foreach (string path in new[] { "/api/model-folders", $"/api/model-folders/{AgentContractSeed.ModelFolder}" })
        {
            var (status, body) = await SendAsync(master.Http.GetAsync(path, Ct));
            Assert.True(status == HttpStatusCode.ServiceUnavailable, $"{path}: {(int)status} {body}");
            Assert.Contains("dgx", body, StringComparison.Ordinal);
        }

        // The vLLM catalog never fails (the node's last reported images).
        var (catalog, catalogBody) = await SendAsync(master.Http.GetAsync("/api/catalog", Ct));
        Assert.True(catalog == HttpStatusCode.OK, $"{(int)catalog} {catalogBody}");

        // Back once the agent is.
        await node.StartAgentAsync();
        await UntilAsync(async () => (await SendAsync(master.Http.GetAsync("/api/model-folders", Ct))).Status == HttpStatusCode.OK, "folders to answer again");
    }

    [Fact]
    public async Task ADownloadWhileDockerIsDownIsDockerUnavailableNotUnsupported()
    {
        // W4 review (MN-096, D57): features are capabilities, not health.
        await using var node = await RealNode.StartAsync(o => o.DockerStartupTimeout = TimeSpan.Zero, n =>
        {
            n.Docker.Down = true;
            n.Downloads.DockerDown = true;
        });
        await using var master = await node.Master().StartAsync(state: NodeState.Degraded);
        Assert.Contains(AgentFeatures.DownloadsHfContainer, (await master.Client.GetCapabilitiesAsync(Ct)).Features);

        var agentAnswer = await Assert.ThrowsAsync<AgentException>(() => master.Client.StartDownloadAsync(AgentContractSuite.Download("model-new"), Ct));
        Assert.Equal(503, agentAnswer.StatusCode);
        Assert.Equal(ErrorCodes.DockerUnavailable, agentAnswer.Code);

        // The admin API answers 503 like a start or stop with Docker down (MN-047) and says why; never 501/500/409.
        var (status, body) = await SendAsync(master.Http.PostAsync("/api/downloads",
            System.Net.Http.Json.JsonContent.Create(new DownloadRequest(AgentContractSeed.DownloadRepo, Folder: "model-new-2")), Ct));
        Assert.True(status == HttpStatusCode.ServiceUnavailable, $"{(int)status} {body}");
        Assert.Equal("Docker is not reachable on this node. (on node dgx)", AdmissionExitTests.Detail(body));
        Assert.False(Directory.Exists(Path.Combine(node.Node.ModelsRoot, "model-new-2")));
    }
}
