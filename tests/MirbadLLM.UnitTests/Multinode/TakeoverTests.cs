using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration;
using MirbadLLM.Orchestration.Agents;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Multinode.Contract;
using MirbadLLM.UnitTests.Multinode.Fakes;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using MirbadLLM.Web.Api;
using static MirbadLLM.UnitTests.Multinode.P1Exit.ExitEnv;

namespace MirbadLLM.UnitTests.Multinode;

/// <summary>
/// MN-115: takeover completion (design §4.2 step 3, §3.5 Pending takeover) through the real master composition with the
/// takeover service and <c>/api/fencing</c> added the way the P2 composition (MN-130) will add them, driving FakeAgents and
/// the real agent composition: the epoch moves above every reachable agent, unreachable nodes are Pending takeover, a
/// returning agent holding a higher epoch stays blocked until the operator continues (never automatically), a returning
/// agent below the master's epoch simply accepts it, the option is one-shot per <c>TakeoverId</c>, and every step is audited.
/// </summary>
public sealed partial class TakeoverTests
{
    private const string Pc = "pc";
    private const string AdminPassword = "takeover-admin-pw";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static readonly TakeoverServiceOptions FastPolls = new()
    {
        InfoTimeout = TimeSpan.FromSeconds(5),
        RequestPollInterval = TimeSpan.FromMilliseconds(100),
    };

    // ---- the operator's takeover through the API, real agent --------------------------------------------------------

    [Fact]
    public async Task ARestoredMasterTakesOverThroughTheApiAboveTheRealAgentsEpochAndThenDrivesIt()
    {
        await using var node = await RealNode.StartAsync(models: [Model("qwen")]);

        // A backup of /data at epoch 1 …
        await (await node.Master().StartAsync()).DisposeAsync();
        string restored = NewDirectory("restored");
        try
        {
            CopyDataDirectory(node.DataDir, restored);

            // … while the live master went on to epoch 6 and drove the agent, then was lost.
            await RaiseEpochAsync(node.DataDir, 5);
            await using (var live = await node.Master().StartAsync())
            {
                await StopAsync(live, NodeId, "qwen");
                Assert.Equal(6, (await live.Client.GetInfoAsync(Ct)).AcceptedMaster?.Epoch);
            }

            var audit = new RecordingAuditLog();
            await using var master = await new ExitMaster(restored, node.Node.Ca, LoginSettings(), WithTakeover(audit)).StartAsync(state: null);
            Assert.Equal(2, master.Identity.Epoch);
            await master.WaitForNodeAsync(n => n.State == NodeState.PendingTakeover, "Pending takeover (stale master)");

            // The admin cookie guards /api/fencing like the rest of /api.
            var (anonymous, _) = await SendAsync(master.Http.GetAsync("/api/fencing", Ct));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous);
            await LoginAsync(master.Http);

            FencingStatus before = await StatusAsync(master);
            Assert.True(before.ActionNeeded);
            Assert.Null(before.LastTakeover);
            Assert.Equal(new NodeFencingStatus(NodeId, NodeFencingState.Stale, 6), Assert.Single(before.Nodes));
            long versionBefore = master.Get<IFencingCoordinator>().Version;

            // The typed precondition still applies to a signed-in session; refusal changes nothing.
            var (unconfirmed, unconfirmedBody) = await PostAsync(master, "/api/fencing/takeover", new { password = AdminPassword, confirm = "yes" });
            AssertProblem(unconfirmed, unconfirmedBody, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired);
            Assert.Equal(2, master.Identity.Epoch);
            Assert.Empty(master.Identity.Takeovers);

            var (ok, okBody) = await PostAsync(master, "/api/fencing/takeover",
                new { confirm = TakeoverConfirmations.PreviousMasterStopped });
            Assert.True(ok == HttpStatusCode.OK, $"{(int)ok} {okBody}");
            var record = JsonSerializer.Deserialize<TakeoverRecord>(okBody, Json)!;

            // max(restored 2, the agent's 6) + 1, persisted with the record before any agent saw it.
            Assert.Equal(7, record.Epoch);
            Assert.Equal(2, record.PreviousEpoch);
            Assert.Equal(7, master.Identity.Epoch);
            Assert.StartsWith("admin:", record.Operator, StringComparison.Ordinal);
            var answered = Assert.Single(record.Answered);
            Assert.Equal((NodeId, node.Node.NodeUid, (long?)6), (answered.NodeId, answered.NodeUid, answered.AcceptedEpoch));
            Assert.Empty(record.Unreachable);
            var onDisk = JsonSerializer.Deserialize<MasterStateDocument>(
                await File.ReadAllBytesAsync(Path.Combine(restored, MasterStateDocument.FileName), Ct), MasterStateJson.Options)!;
            Assert.Equal(7, onDisk.Epoch);
            Assert.Equal(record.TakeoverId, Assert.Single(onDisk.Takeovers).TakeoverId);

            // The real agent accepts the restored master on its next command.
            await master.WaitForNodeAsync(n => n.State == NodeState.Online, "Online after the takeover");
            await StopAsync(master, NodeId, "qwen");
            Assert.Equal(master.Identity.Current, (await master.Client.GetInfoAsync(Ct)).AcceptedMaster);

            FencingStatus after = await StatusAsync(master);
            Assert.False(after.ActionNeeded);
            Assert.Equal(7, after.Epoch);
            Assert.Equal(record.TakeoverId, after.LastTakeover?.TakeoverId);
            Assert.Equal(NodeFencingState.Current, Assert.Single(after.Nodes).State);
            Assert.True(master.Get<IFencingCoordinator>().Version > versionBefore);

            // The confirmation refusal and takeover are audited; no line holds the login password.
            Assert.Equal(
                [(AuditResults.Refused, ErrorCodes.ConfirmationRequired), (AuditResults.Succeeded, null)],
                audit.Lines.Where(l => l.Action == AuditActions.FencingTakeover).Select(l => (l.Result, l.ErrorCode)).ToArray());
            var done = audit.Lines.Single(l => l.Action == AuditActions.FencingTakeover && l.Result == AuditResults.Succeeded);
            Assert.Equal(record.TakeoverId, done.Target);
            Assert.Equal(record.Operator, done.Actor);
            Assert.Contains("epoch 2 → 7", done.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(audit.Lines, l => JsonSerializer.Serialize(l).Contains(AdminPassword, StringComparison.Ordinal) ||
                                                    JsonSerializer.Serialize(l).Contains("not-it", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(restored);
        }
    }

    // ---- unreachable nodes, a returning agent with the highest epoch, the continuation --------------------------------

    [Fact]
    public async Task AnOfflineAgentHoldingTheHighestEpochIsPendingTakeoverThenBlockedOnReturnUntilTheOperatorContinues()
    {
        await using var cluster = await TwoNodes.StartAsync();

        // dgx was last driven at epoch 9, pc (now offline) at epoch 20: both above the restored master's epoch.
        await cluster.DriveAsync(NodeId, "qwen", epoch: 9);
        await cluster.DriveAsync(Pc, "pcm", epoch: 20);
        cluster.Pc.SetPartitioned(true);

        var audit = new RecordingAuditLog();
        await using var master = await cluster.Dgx.Master(cluster.Dgx.DataDir, LoginSettings(), WithTakeover(audit)).StartAsync(state: null);
        await UntilNodeAsync(master, NodeId, n => n.State == NodeState.PendingTakeover, "dgx stale");
        await LoginAsync(master.Http);
        Assert.Equal(1, master.Identity.Epoch);

        var (status, body) = await PostAsync(master, "/api/fencing/takeover",
            new { confirm = TakeoverConfirmations.PreviousMasterStopped });
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        var record = JsonSerializer.Deserialize<TakeoverRecord>(body, Json)!;

        // Only reachable agents count: max(1, dgx's 9) + 1 = 10; pc is recorded unreachable and Pending takeover.
        Assert.Equal(10, record.Epoch);
        Assert.Equal((NodeId, (long?)9), (Assert.Single(record.Answered).NodeId, record.Answered[0].AcceptedEpoch));
        Assert.Equal([Pc], record.Unreachable);
        await UntilNodeAsync(master, NodeId, n => n.State == NodeState.Online, "dgx Online after the takeover");
        var pending = await UntilNodeAsync(master, Pc, n => n.State == NodeState.PendingTakeover, "pc Pending takeover");
        Assert.False(pending.IsServing);
        Assert.Contains(record.TakeoverId, pending.Reasons[0], StringComparison.Ordinal);
        FencingStatus offline = await StatusAsync(master);
        Assert.False(offline.ActionNeeded); // nothing to decide until pc returns
        Assert.Equal(NodeFencingState.PendingTakeover, offline.Nodes.Single(n => n.NodeId == Pc).State);
        Assert.Equal(NodeFencingState.Current, offline.Nodes.Single(n => n.NodeId == NodeId).State);

        // pc returns holding epoch 20 ≥ 10: blocked (no routing, no commands), and it stays so — no automatic continuation.
        cluster.Pc.SetPartitioned(false);
        await UntilAsync(() => master.Get<NodeStatusCache>().ListFencing().Single(n => n.NodeId == Pc).State == NodeFencingState.NeedsContinuation,
            "pc to need a continuation");
        long version = master.Get<IFencingCoordinator>().Version;
        for (int i = 0; i < 3; i++)
        {
            await master.Cache.RefreshNodeAsync(Pc, Ct);
            Assert.Equal(NodeState.PendingTakeover, master.Cache.GetNode(Pc)?.State);
        }

        await Task.Delay(FastPolls.RequestPollInterval * 5, Ct); // the hosted loop runs too: still nothing automatic
        FencingStatus returned = await StatusAsync(master);
        Assert.True(returned.ActionNeeded);
        Assert.Equal(new NodeFencingStatus(Pc, NodeFencingState.NeedsContinuation, 20), returned.Nodes.Single(n => n.NodeId == Pc));
        Assert.Equal(10, master.Identity.Epoch);
        Assert.Single(master.Identity.Takeovers);
        Assert.Contains("continues the takeover", master.Cache.GetNode(Pc)!.Reasons[0], StringComparison.Ordinal);
        var stale = await Assert.ThrowsAsync<AgentException>(() => StopAsync(master, Pc, "pcm"));
        Assert.Equal(ErrorCodes.StaleMaster, stale.Code);
        var (chat, chatBody) = await SendAsync(master.Http.PostAsJsonAsync("/v1/chat/completions",
            new { model = "pcm", messages = new[] { new { role = "user", content = "hi" } } }, Ct));
        Assert.True(chat == HttpStatusCode.ServiceUnavailable, $"{(int)chat} {chatBody}");

        // The continuation must name the latest takeover, the epoch the agent holds and the node itself.
        var (wrongEpoch, wrongEpochBody) = await ContinueAsync(master, record.TakeoverId, agentEpoch: 19);
        AssertProblem(wrongEpoch, wrongEpochBody, HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        var (wrongId, wrongIdBody) = await ContinueAsync(master, "not-the-takeover", agentEpoch: 20);
        AssertProblem(wrongId, wrongIdBody, HttpStatusCode.Conflict, ErrorCodes.InvalidState);
        var (unconfirmed, unconfirmedBody) = await ContinueAsync(master, record.TakeoverId, agentEpoch: 20, confirm: NodeId);
        AssertProblem(unconfirmed, unconfirmedBody, HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired);
        Assert.Equal(10, master.Identity.Epoch);
        Assert.Equal(version, master.Get<IFencingCoordinator>().Version);

        // Continuing raises the epoch to 21 and records it in the takeover record.
        var (continued, continuedBody) = await ContinueAsync(master, record.TakeoverId, agentEpoch: 20);
        Assert.True(continued == HttpStatusCode.OK, $"{(int)continued} {continuedBody}");
        var updated = JsonSerializer.Deserialize<TakeoverRecord>(continuedBody, Json)!;
        Assert.Equal(record.TakeoverId, updated.TakeoverId);
        var continuation = Assert.Single(updated.Continuations);
        Assert.Equal((Pc, 20L, 21L), (continuation.NodeId, continuation.AgentEpoch, continuation.NewEpoch));
        Assert.Equal(21, master.Identity.Epoch);
        Assert.Single(master.Identity.Takeovers);

        // pc accepts the raised epoch, and so does every other node on its next command.
        await UntilNodeAsync(master, Pc, n => n.State == NodeState.Online, "pc Online after the continuation");
        await StopAsync(master, Pc, "pcm");
        await StopAsync(master, NodeId, "qwen");
        Assert.Equal(master.Identity.Current, (await ClientOf(master, Pc).GetInfoAsync(Ct)).AcceptedMaster);
        Assert.Equal(master.Identity.Current, (await master.Client.GetInfoAsync(Ct)).AcceptedMaster);
        FencingStatus done = await StatusAsync(master);
        Assert.False(done.ActionNeeded);
        Assert.All(done.Nodes, n => Assert.Equal(NodeFencingState.Current, n.State));
        Assert.True(master.Get<IFencingCoordinator>().Version > version);

        var lines = audit.Lines.Where(l => l.Action == AuditActions.FencingContinue).Select(l => (l.Result, l.ErrorCode)).ToArray();
        Assert.Equal(
            [(AuditResults.Refused, ErrorCodes.InvalidState), (AuditResults.Refused, ErrorCodes.InvalidState),
             (AuditResults.Refused, ErrorCodes.ConfirmationRequired), (AuditResults.Succeeded, null)],
            lines);
        Assert.Contains(audit.Lines, l => l.Action == AuditActions.FencingContinue && l.Result == AuditResults.Succeeded &&
            l.NodeId == Pc && l.Target == record.TakeoverId && l.Detail!.Contains("10 → 21", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APendingNodeStaysPendingAcrossAMasterRestartAndLeavesItByItselfWhenItsEpochIsLower()
    {
        await using var cluster = await TwoNodes.StartAsync();

        // This master drove pc once (pc holds epoch 1); then pc went offline and the master took over at its next boot.
        await using (var first = await cluster.Dgx.Master().StartAsync())
        {
            await UntilNodeAsync(first, Pc, n => n.State == NodeState.Online, "pc Online");
            await StopAsync(first, Pc, "pcm");
        }

        cluster.Pc.SetPartitioned(true);
        string takeoverId;
        await using (var second = await cluster.Dgx.Master(configure: WithTakeover(new RecordingAuditLog())).StartAsync())
        {
            var record = await second.Get<IFencingCoordinator>().TakeOverAsync("test", Ct);
            takeoverId = record.TakeoverId;
            Assert.Equal((2L, 3L), (record.PreviousEpoch, record.Epoch));
            Assert.Equal([Pc], record.Unreachable);
            await UntilNodeAsync(second, Pc, n => n.State == NodeState.PendingTakeover, "pc Pending takeover");
        }

        // A restart keeps the mark (from master.json) while pc is still away.
        await using var master = await cluster.Dgx.Master(configure: WithTakeover(new RecordingAuditLog())).StartAsync();
        Assert.Equal(4, master.Identity.Epoch);
        var pending = await UntilNodeAsync(master, Pc, n => n.State == NodeState.PendingTakeover, "pc Pending takeover after the restart");
        Assert.Contains(takeoverId, pending.Reasons[0], StringComparison.Ordinal);
        Assert.Equal(takeoverId, master.Get<NodeStatusCache>().PendingTakeoverOf(Pc));
        Assert.Equal(NodeFencingState.PendingTakeover, master.Get<IFencingCoordinator>().GetStatus().Nodes.Single(n => n.NodeId == Pc).State);

        // It returns holding epoch 1 < 4: it leaves Pending takeover without any continuation and takes commands.
        cluster.Pc.SetPartitioned(false);
        await UntilNodeAsync(master, Pc, n => n.State == NodeState.Online, "pc Online on return");
        Assert.Null(master.Get<NodeStatusCache>().PendingTakeoverOf(Pc));
        await StopAsync(master, Pc, "pcm");
        Assert.Equal(master.Identity.Current, (await ClientOf(master, Pc).GetInfoAsync(Ct)).AcceptedMaster);
        Assert.Empty(master.Identity.Takeovers[^1].Continuations);
        Assert.Equal(4, master.Identity.Epoch);

        // A continuation is refused for a node that does not need one.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            master.Get<IFencingCoordinator>().ContinueAsync(Pc, takeoverId, 1, "test", Ct));
    }

    // ---- the flag file and the one-shot option -----------------------------------------------------------------------

    [Fact]
    public async Task TheTakeoverOptionRunsOnceForItsTakeoverIdAndIsIgnoredOnLaterBoots()
    {
        await using var node = await FakeNode.StartAsync(models: [Model("qwen")]);
        string other = await node.NewDataDirAsync(Model("qwen"));
        await RaiseEpochAsync(other, 4);
        await using (var previous = await node.Master(other).StartAsync())
        {
            await StopAsync(previous, NodeId, "qwen"); // the agent holds epoch 5
        }

        var settings = new Dictionary<string, string?>
        {
            ["MirbadLLM:Fencing:Takeover"] = "true",
            ["MirbadLLM:Fencing:TakeoverId"] = "tk-once",
        };
        var audit = new RecordingAuditLog();
        await using (var master = await node.Master(settings: settings, configure: WithTakeover(audit)).StartAsync(state: null))
        {
            // The option is the operator's request (design §4.2 step 2): the master takes over at boot as "config".
            await UntilAsync(() => master.Identity.Takeovers.Count == 1, "the option's takeover");
            var record = master.Identity.Takeovers[0];
            Assert.Equal("tk-once", record.TakeoverId);
            Assert.Equal(AuditActors.Config, record.Operator);
            Assert.Equal(6, record.Epoch); // max(1, 5) + 1
            await master.WaitForNodeAsync(n => n.State == NodeState.Online, "Online after the option's takeover");
            var status = master.Get<IFencingCoordinator>().GetStatus();
            Assert.True(status.TakeoverOptionSet); // the red "remove the option" warning (MN-121)
            Assert.False(status.TakeoverRequested);
            Assert.Contains(audit.Lines, l => l.Actor == AuditActors.Config && l.Target == "tk-once" && l.Result == AuditResults.Succeeded);
        }

        // Left set, it is ignored: the next boot only increments the epoch and records no second takeover.
        await using (var again = await node.Master(settings: settings, configure: WithTakeover(audit)).StartAsync())
        {
            Assert.Equal(7, again.Identity.Epoch);
            await Task.Delay(FastPolls.RequestPollInterval * 5, Ct);
            Assert.Single(again.Identity.Takeovers);
            Assert.Equal(7, again.Identity.Epoch);
            var status = again.Get<IFencingCoordinator>().GetStatus();
            Assert.True(status.TakeoverOptionSet);
            Assert.False(status.TakeoverRequested);
        }

        Assert.Single(audit.Lines, l => l.Action == AuditActions.FencingTakeover);
    }

    [Fact]
    public async Task TheFlagFileCreatedWhileTheMasterRunsTriggersOneTakeoverAndIsDeleted()
    {
        await using var node = await FakeNode.StartAsync(models: [Model("qwen")]);
        await using var master = await node.Master(configure: WithTakeover(new RecordingAuditLog())).StartAsync();
        string flag = Path.Combine(node.DataDir, MasterStateDocument.TakeoverFlagFileName);

        await File.WriteAllTextAsync(flag, "", Ct);
        await UntilAsync(() => master.Identity.Takeovers.Count == 1 && !File.Exists(flag), "the flag's takeover");

        var record = master.Identity.Takeovers[0];
        Assert.Equal((1L, 2L, AuditActors.Config), (record.PreviousEpoch, record.Epoch, record.Operator));
        Assert.Equal(NodeId, Assert.Single(record.Answered).NodeId);
        await Task.Delay(FastPolls.RequestPollInterval * 5, Ct);
        Assert.Single(master.Identity.Takeovers);
        await master.WaitForNodeAsync(n => n.State == NodeState.Online, "Online");
    }

    [Fact]
    public async Task TheMasterRunsOneTakeoverLoopWhenTheTakeoverServiceIsAddedTwice()
    {
        // The master composition adds the service (MN-126) and WithTakeover adds it again for fast polls: one hosted
        // loop, with the later options. Two loops on one instance ran two takeovers for one flag file (MN-115).
        await using var node = await FakeNode.StartAsync(models: [Model("qwen")]);
        await using var master = await node.Master(configure: WithTakeover(new RecordingAuditLog())).StartAsync();
        var takeover = Assert.Single(master.Get<IEnumerable<IHostedService>>().OfType<TakeoverService>());
        Assert.Same(takeover, master.Get<IFencingCoordinator>());
        Assert.Same(FastPolls, master.Get<TakeoverServiceOptions>());

        var services = new ServiceCollection();
        services.AddTakeoverService();
        int registrations = services.Count;
        var later = new TakeoverServiceOptions { RequestPollInterval = TimeSpan.FromSeconds(1) };
        services.AddTakeoverService(later);
        Assert.Equal(registrations, services.Count);
        Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Same(later, Assert.Single(services, d => d.ServiceType == typeof(TakeoverServiceOptions)).ImplementationInstance);
    }

    [Fact]
    public async Task TheOptionsTakeoverIdIsOneShotInMasterJson()
    {
        string dir = NewDirectory("oneshot");
        try
        {
            var options = new FencingOptions { Takeover = true, TakeoverId = "tk-a" };
            using (var identity = MasterIdentity.Open(dir, options))
            {
                Assert.True(identity.TakeoverRequested);
                Assert.True(identity.TakeoverOptionPending);
                var record = TakeoverRules.CreateRecord(identity.Epoch, [], [], AuditActors.Config, DateTimeOffset.UtcNow, identity.TakeoverOptionId);
                Assert.Equal("tk-a", record.TakeoverId);
                await identity.CompleteTakeoverAsync(record, Ct);
                Assert.False(identity.TakeoverRequested);
            }

            using (var reopened = MasterIdentity.Open(dir, options))
            {
                Assert.True(reopened.TakeoverOptionConsumed);
                Assert.False(reopened.TakeoverRequested);
                Assert.True(reopened.TakeoverOptionSet);
            }

            // A new id asks again; no id keeps the P1 behaviour (each boot asks).
            using (var fresh = MasterIdentity.Open(dir, new FencingOptions { Takeover = true, TakeoverId = "tk-b" }))
            {
                Assert.True(fresh.TakeoverRequested);
            }

            using (var p1 = MasterIdentity.Open(dir, new FencingOptions { Takeover = true }))
            {
                Assert.True(p1.TakeoverRequested);
                Assert.False(p1.TakeoverOptionConsumed);
            }
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Fact]
    public void ANodeBothAnsweringAndListedUnreachableCountsAsAnswered()
    {
        var record = TakeoverRules.CreateRecord(3, [new TakeoverNodeEpoch("a", "uid", 8)], ["a", "b", "b"], "x", DateTimeOffset.UnixEpoch);
        Assert.Equal(9, record.Epoch);
        Assert.Equal(["b"], record.Unreachable);
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The takeover service with fast polls (the master composition already adds it, MN-126; a second
    /// <see cref="TakeoverServiceCollectionExtensions.AddTakeoverService"/> only replaces its options), a recording audit log
    /// and <c>/api/fencing</c> with its authentication in front of the pipeline.
    /// </summary>
    internal static Action<IWebHostBuilder> WithTakeover(IAuditLog audit) => builder => builder.ConfigureTestServices(services =>
    {
        services.AddSingleton(audit);
        services.AddTakeoverService(FastPolls);
        services.AddSingleton<IStartupFilter, FencingApiStartupFilter>();
    });

    private static Dictionary<string, string?> LoginSettings() => new() { ["MirbadLLM:AdminPassword"] = AdminPassword };

    private static IAgentClient ClientOf(ExitMaster master, string nodeId) => master.Get<IAgentClientFactory>().GetClient(nodeId);

    private static Task<OperationStatus> StopAsync(ExitMaster master, string nodeId, string modelId) =>
        master.Get<AgentOperationRunner>().SubmitAsync(nodeId, AgentRoutes.RuntimeStop, modelId, AgentContractSuite.Stop(),
            (client, request, ct) => client.StopAsync(modelId, request, ct), owner: null, Ct);

    private static async Task<NodeLiveStatus> UntilNodeAsync(ExitMaster master, string nodeId, Func<NodeLiveStatus, bool> predicate, string what)
    {
        NodeLiveStatus? node = null;
        await UntilAsync(() => (node = master.Cache.GetNode(nodeId)) is { } n && predicate(n), what);
        return node!;
    }

    private static async Task<FencingStatus> StatusAsync(ExitMaster master)
    {
        var (status, body) = await SendAsync(master.Http.GetAsync("/api/fencing", Ct));
        Assert.True(status == HttpStatusCode.OK, $"{(int)status} {body}");
        return JsonSerializer.Deserialize<FencingStatus>(body, Json)!;
    }

    private static Task<(HttpStatusCode Status, string Body)> PostAsync(ExitMaster master, string path, object body) =>
        SendAsync(master.Http.PostAsJsonAsync(path, body, Ct));

    private static Task<(HttpStatusCode Status, string Body)> ContinueAsync(ExitMaster master, string takeoverId, long agentEpoch, string confirm = Pc) =>
        PostAsync(master, "/api/fencing/takeover/continue",
            new { nodeId = Pc, takeoverId, agentEpoch, password = AdminPassword, confirm });

    private static void AssertProblem(HttpStatusCode status, string body, HttpStatusCode expected, string code)
    {
        Assert.True(status == expected, $"{(int)status} {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static async Task LoginAsync(HttpClient client)
    {
        using var page = await client.GetAsync("/login", Ct);
        page.EnsureSuccessStatusCode();
        var token = AntiforgeryToken().Match(await page.Content.ReadAsStringAsync(Ct));
        Assert.True(token.Success, "no antiforgery token on /login");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token.Groups[1].Value,
            ["password"] = AdminPassword,
            ["returnUrl"] = "",
        });
        using var login = await client.PostAsync("/login/submit", form, Ct);
        Assert.Equal("/", login.Headers.Location?.ToString());
    }

    [GeneratedRegex("""name="__RequestVerificationToken" value="([^"]+)""")]
    private static partial Regex AntiforgeryToken();

    /// <summary>Maps <c>/api/fencing</c> in front of the master's pipeline, with authentication and the fallback policy.</summary>
    private sealed class FencingApiStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.MapWhen(http => http.Request.Path.StartsWithSegments("/api/fencing"), branch =>
            {
                branch.UseRouting();
                branch.UseAuthentication();
                branch.UseAuthorization();
                branch.UseEndpoints(endpoints => endpoints.MapFencingApi());
            });
            next(app);
        };
    }

    /// <summary>An <see cref="IAuditLog"/> that keeps every line in memory.</summary>
    internal sealed class RecordingAuditLog : IAuditLog
    {
        private readonly ConcurrentQueue<AuditRecord> _lines = new();

        public IReadOnlyList<AuditRecord> Lines => [.. _lines];

        public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            _lines.Enqueue(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([.. _lines.Reverse()]);
    }

    /// <summary>
    /// Two FakeAgents, <c>dgx</c> (with model <c>qwen</c>) and <c>pc</c> (with <c>pcm</c>), enrolled in the first master
    /// data directory; <see cref="DriveAsync"/> makes another master at a given epoch drive one of them.
    /// </summary>
    private sealed class TwoNodes : IAsyncDisposable
    {
        private TwoNodes(FakeNode dgx, FakeAgent pc)
        {
            Dgx = dgx;
            Pc = pc;
        }

        public FakeNode Dgx { get; }

        public FakeAgent Pc { get; }

        public static ModelDefinition[] Models => [Model("qwen"), Model("pcm") with { Placement = new ModelPlacement(TakeoverTests.Pc) }];

        public static async Task<TwoNodes> StartAsync()
        {
            var dgx = await FakeNode.StartAsync(models: Models);
            var pc = await FakeAgent.StartAsync(new FakeAgentOptions { Ca = dgx.Ca, NodeId = TakeoverTests.Pc }, Ct);
            var nodes = new TwoNodes(dgx, pc);
            await nodes.RegisterPcAsync(dgx.DataDir);
            return nodes;
        }

        /// <summary>A master on its own data directory at <paramref name="epoch"/> sends one command to <paramref name="nodeId"/>.</summary>
        public async Task DriveAsync(string nodeId, string modelId, long epoch)
        {
            string dir = await Dgx.NewDataDirAsync(Models);
            await RegisterPcAsync(dir);
            await RaiseEpochAsync(dir, epoch - 1);
            await using var other = await Dgx.Master(dir).StartAsync(state: null);
            Assert.Equal(epoch, other.Identity.Epoch);
            await UntilNodeAsync(other, nodeId, n => n.State == NodeState.Online, $"{nodeId} Online on the epoch-{epoch} master");
            await StopAsync(other, nodeId, modelId);
            Assert.Equal(epoch, (await ClientOf(other, nodeId).GetInfoAsync(Ct)).AcceptedMaster?.Epoch);
        }

        public async ValueTask DisposeAsync()
        {
            await Pc.DisposeAsync();
            await Dgx.DisposeAsync();
        }

        private async Task RegisterPcAsync(string dataDir)
        {
            using var registry = new JsonNodeRegistry(Options.Create(new MirbadLLMOptions { DataPath = dataDir }), TimeProvider.System);
            await registry.UpsertAsync(new NodeRecord
            {
                Id = TakeoverTests.Pc,
                Uid = Pc.NodeUid,
                Address = Pc.BaseAddress.AbsoluteUri,
                Approved = true,
                CertSpkiSha256 = Pc.CurrentSpki,
            }, Ct);
        }
    }
}
