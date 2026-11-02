using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Multinode.P1Exit;
using static MirbadLLM.UnitTests.Multinode.P2Exit.P2Env;

namespace MirbadLLM.UnitTests.Multinode.P2Exit;

/// <summary>
/// MN-123, fencing in P2 (design §4.2 step 3, §3.5 Pending takeover, §12.1 banner; MN-115, MN-121, MN-130; D90): the real
/// master over a restored older <c>/data</c> with the real DGX and PC agents, driven through <c>/api/fencing</c> and the
/// layout's takeover banner; the continuation of an agent that returns holding a higher epoch; and a takeover option that
/// keeps failing (backoff, one Failed audit line per attempt).
/// </summary>
public sealed class FencingP2ExitTests
{
    /// <summary>Fast takeover polls, so the hosted loop runs many passes within a test (the product default is 5 s).</summary>
    private static readonly TakeoverServiceOptions FastPolls = new()
    {
        InfoTimeout = TimeSpan.FromSeconds(10),
        RequestPollInterval = TimeSpan.FromMilliseconds(100),
        MaxRetryInterval = TimeSpan.FromMilliseconds(800),
    };

    [Fact]
    public async Task ARestoredOlderDataShowsTheBannerRefusesAnUnconfirmedTakeoverAndTakesOverThroughTheApi()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions { Models = [Model("qwen", "dgx")] });
        await fleet.JoinAsync("pc", PcGpus);
        await fleet.Master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[0]]));
        string snapshot = ExitEnv.NewDirectory("p2-fence");
        try
        {
            // The backup of /data (taken while the master is down), then the live master goes on two more boots and drives both nodes.
            await fleet.RestartMasterAsync(whileStopped: () => CopyAsync(fleet.DataDir, snapshot));
            await fleet.RestartMasterAsync();
            await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online on the live master");
            await fleet.Master.StartModelAsync("qwen");
            await fleet.Master.StartModelAsync("pcm");
            long live = Epoch(fleet.Master);
            await WaitAcceptedAsync(fleet.Master, "dgx", live);
            await WaitAcceptedAsync(fleet.Master, "pc", live);

            // The live master is lost; the older /data is restored and boots one epoch below what the agents hold.
            await fleet.RestartMasterAsync(waitForDgx: false, whileStopped: () => ReplaceAsync(fleet.DataDir, snapshot));
            var master = fleet.Master;
            Assert.Equal(live - 1, Epoch(master));
            await master.WaitForNodeAsync("dgx", n => n.State == NodeState.PendingTakeover, "dgx blocked (stale master)");
            await master.WaitForNodeAsync("pc", n => n.State == NodeState.PendingTakeover, "pc blocked (stale master)");

            FencingStatus before = await StatusAsync(master);
            Assert.True(before.ActionNeeded);
            Assert.Null(before.LastTakeover);
            Assert.Equal([new NodeFencingStatus("dgx", NodeFencingState.Stale, live), new NodeFencingStatus("pc", NodeFencingState.Stale, live)],
                before.Nodes.OrderBy(n => n.NodeId, StringComparer.Ordinal));
            string page = await PageAsync(master, "/");
            Assert.Contains("data-testid=\"takeover-banner\"", page, StringComparison.Ordinal);
            Assert.Contains("data-testid=\"takeover-open\"", page, StringComparison.Ordinal);

            // A blocked node runs nothing for this master.
            var (blocked, blockedBody) = await master.PostAsync("/api/models/qwen/stop");
            Assert.True(blocked != HttpStatusCode.OK, $"a stop on a stale node answered {(int)blocked} {blockedBody}");

            // Without the typed precondition: refused, nothing changes.
            await master.ExpectAsync("/api/fencing/takeover", new { password = Password, confirm = "yes" },
                HttpStatusCode.Conflict, ErrorCodes.ConfirmationRequired);
            Assert.Equal(live - 1, Epoch(master));
            Assert.Empty(master.Get<IMasterIdentity>().Takeovers);

            // With it (and the password): above every agent, both answered.
            string body = await master.ExpectAsync("/api/fencing/takeover",
                new { password = Password, confirm = TakeoverConfirmations.PreviousMasterStopped }, HttpStatusCode.OK);
            var record = JsonSerializer.Deserialize<TakeoverRecord>(body, Json)!;
            Assert.Equal(live + 1, record.Epoch);
            Assert.Equal(live - 1, record.PreviousEpoch);
            Assert.Equal(["dgx", "pc"], record.Answered.Select(a => a.NodeId).Order(StringComparer.Ordinal));
            Assert.Empty(record.Unreachable);
            Assert.StartsWith("admin:", record.Operator, StringComparison.Ordinal);

            // The banner is gone, both nodes are Online and take commands at the new epoch.
            await master.WaitForNodeAsync("dgx", n => n.State == NodeState.Online, "dgx Online after the takeover");
            await master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online after the takeover");
            FencingStatus after = await StatusAsync(master);
            Assert.False(after.ActionNeeded);
            Assert.Equal(record.TakeoverId, after.LastTakeover?.TakeoverId);
            await ExitEnv.UntilAsync(async () => !(await PageAsync(master, "/")).Contains("data-testid=\"takeover-banner\"", StringComparison.Ordinal),
                "the takeover banner to go");
            await master.ExpectAsync("/api/models/qwen/stop", null, HttpStatusCode.NoContent);
            await master.ExpectAsync("/api/models/pcm/stop", null, HttpStatusCode.NoContent);
            await master.StartModelAsync("pcm");
            await WaitAcceptedAsync(master, "dgx", live + 1);
            await WaitAcceptedAsync(master, "pc", live + 1);

            // Audited: the refusal and the takeover, with the session as actor.
            var lines = (await fleet.AuditAsync()).Where(l => l.Action == AuditActions.FencingTakeover).Reverse().ToList();
            Assert.Equal([(AuditResults.Refused, ErrorCodes.ConfirmationRequired), (AuditResults.Succeeded, (string?)null)],
                lines.Select(l => (l.Result, l.ErrorCode)));
            Assert.Equal(record.TakeoverId, lines[1].Target);
            Assert.Equal(record.Operator, lines[1].Actor);
        }
        finally
        {
            ExitEnv.DeleteDirectory(snapshot);
        }
    }

    [Fact]
    public async Task AnOfflinePcHoldingAHigherEpochStaysBlockedOnReturnUntilTheOperatorContinues()
    {
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions
        {
            Models = [Model("qwen", "dgx")],
            Services = s => s.AddTakeoverService(FastPolls),
        });
        await fleet.JoinAsync("pc", PcGpus);
        await fleet.Master.CreateModelAsync(ModelBody("pcm", "pc", [PcGpus[0]]));
        string snapshot = ExitEnv.NewDirectory("p2-cont");
        try
        {
            // The backup of /data; the DGX is down from then on, while the live master drives the PC for two more boots.
            long snapshotEpoch = Epoch(fleet.Master);
            await fleet.RestartMasterAsync(waitForDgx: false, whileStopped: async () =>
            {
                await CopyAsync(fleet.DataDir, snapshot);
                await fleet.Dgx.StopAsync();
            });
            await fleet.RestartMasterAsync(waitForDgx: false);
            await fleet.Master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online on the live master");
            await fleet.Master.StartModelAsync("pcm");
            long pcEpoch = Epoch(fleet.Master);
            Assert.Equal(snapshotEpoch + 2, pcEpoch);
            await WaitAcceptedAsync(fleet.Master, "pc", pcEpoch);

            // The live master and the PC go down; the older /data is restored and the DGX comes back.
            await fleet["pc"].StopAsync();
            await fleet.RestartMasterAsync(whileStopped: async () =>
            {
                await ReplaceAsync(fleet.DataDir, snapshot);
                await fleet.Dgx.StartAsync();
            });
            var master = fleet.Master;
            Assert.Equal(snapshotEpoch + 1, Epoch(master));

            // The operator takes over: dgx answers, pc is unreachable and Pending takeover (no action until it returns).
            string body = await master.ExpectAsync("/api/fencing/takeover",
                new { password = Password, confirm = TakeoverConfirmations.PreviousMasterStopped }, HttpStatusCode.OK);
            var record = JsonSerializer.Deserialize<TakeoverRecord>(body, Json)!;
            Assert.Equal(snapshotEpoch + 2, record.Epoch); // max(restored, dgx's) + 1: the PC's epoch is unknown
            Assert.Equal(["pc"], record.Unreachable);
            Assert.Equal("dgx", Assert.Single(record.Answered).NodeId);
            var pending = await master.WaitForNodeAsync("pc", n => n.State == NodeState.PendingTakeover, "pc Pending takeover");
            Assert.False(pending.IsServing);
            Assert.False((await StatusAsync(master)).ActionNeeded);

            // pc returns holding epoch ≥ the takeover's: blocked, the banner asks for a continuation.
            await fleet["pc"].StartAsync();
            await ExitEnv.UntilAsync(() => FencingOf(master, "pc")?.State == NodeFencingState.NeedsContinuation, "pc to need a continuation");
            FencingStatus returned = await StatusAsync(master);
            Assert.True(returned.ActionNeeded);
            Assert.Equal(new NodeFencingStatus("pc", NodeFencingState.NeedsContinuation, pcEpoch), returned.Nodes.Single(n => n.NodeId == "pc"));
            Assert.Contains("data-testid=\"continue-open-pc\"", await PageAsync(master, "/"), StringComparison.Ordinal);

            // Never automatic: re-reads of the node and the takeover loop's passes leave it blocked. Bounded "nothing happens"
            // check: three forced /info reads plus ten passes of the 100 ms hosted loop (FastPolls); a continuation would
            // have raised the epoch at the first one.
            for (int i = 0; i < 3; i++)
            {
                await master.Cache.RefreshNodeAsync("pc", Ct);
                Assert.Equal(NodeState.PendingTakeover, master.Cache.GetNode("pc")?.State);
            }

            await Task.Delay(FastPolls.RequestPollInterval * 10, Ct);
            Assert.Equal(NodeState.PendingTakeover, master.Cache.GetNode("pc")?.State);
            Assert.Equal(snapshotEpoch + 2, Epoch(master));
            Assert.Empty(master.Get<IMasterIdentity>().Takeovers[^1].Continuations);
            var (refused, refusedBody) = await master.PostAsync("/api/models/pcm/stop");
            Assert.True(refused != HttpStatusCode.OK, $"a stop on the blocked pc answered {(int)refused} {refusedBody}");

            // The continuation must name the epoch the agent holds; then the master moves above it and pc accepts.
            await master.ExpectAsync("/api/fencing/takeover/continue",
                new { nodeId = "pc", takeoverId = record.TakeoverId, agentEpoch = pcEpoch - 1, password = Password, confirm = "pc" },
                HttpStatusCode.Conflict, ErrorCodes.InvalidState);
            string continued = await master.ExpectAsync("/api/fencing/takeover/continue",
                new { nodeId = "pc", takeoverId = record.TakeoverId, agentEpoch = pcEpoch, password = Password, confirm = "pc" },
                HttpStatusCode.OK);
            var updated = JsonSerializer.Deserialize<TakeoverRecord>(continued, Json)!;
            var continuation = Assert.Single(updated.Continuations);
            Assert.Equal(("pc", pcEpoch, pcEpoch + 1), (continuation.NodeId, continuation.AgentEpoch, continuation.NewEpoch));
            Assert.Equal(pcEpoch + 1, Epoch(master));

            await master.WaitForNodeAsync("pc", n => n.State == NodeState.Online, "pc Online after the continuation");
            await master.WaitForNodeAsync("dgx", n => n.State == NodeState.Online, "dgx Online after the continuation");
            await master.ExpectAsync("/api/models/pcm/stop", null, HttpStatusCode.NoContent);
            await master.ExpectAsync("/api/models/qwen/stop", null, HttpStatusCode.NoContent);
            await WaitAcceptedAsync(master, "pc", pcEpoch + 1);
            await WaitAcceptedAsync(master, "dgx", pcEpoch + 1);
            Assert.False((await StatusAsync(master)).ActionNeeded);

            var lines = (await fleet.AuditAsync()).Where(l => l.Action == AuditActions.FencingContinue).Reverse().ToList();
            Assert.Equal([(AuditResults.Refused, ErrorCodes.InvalidState), (AuditResults.Succeeded, (string?)null)],
                lines.Select(l => (l.Result, l.ErrorCode)));
            Assert.All(lines, l => Assert.Equal("pc", l.NodeId));
        }
        finally
        {
            ExitEnv.DeleteDirectory(snapshot);
        }
    }

    [Fact]
    public async Task ATakeoverOptionThatKeepsFailingBacksOffAndWritesOneFailedLinePerAttempt()
    {
        // What makes a takeover fail (MN-130): not an unreachable or refusing node (those are recorded unreachable and the
        // takeover succeeds, TakeoverService.ProbeAsync), but an attempt that throws. Here the DGX answers /info holding the
        // highest possible epoch (a master booted from a master.json at long.MaxValue - 1 drove it), so every attempt
        // overflows computing max + 1 and fails with internal_error, as long as the option stays set.
        await using var fleet = await P2Fleet.StartAsync(new P2FleetOptions
        {
            Models = [Model("qwen", "dgx")],
            Services = s => s.AddTakeoverService(FastPolls),
        });
        string snapshot = ExitEnv.NewDirectory("p2-fail");
        try
        {
            await fleet.RestartMasterAsync(whileStopped: async () =>
            {
                await CopyAsync(fleet.DataDir, snapshot);
                await ExitEnv.RaiseEpochAsync(fleet.DataDir, long.MaxValue - 1);
            });
            Assert.Equal(long.MaxValue, Epoch(fleet.Master));
            await fleet.Master.ExpectAsync("/api/models/qwen/stop", null, HttpStatusCode.NoContent); // agents accept an epoch with a command
            await WaitAcceptedAsync(fleet.Master, "dgx", long.MaxValue);

            fleet.Settings["MirbadLLM:Fencing:Takeover"] = "true";
            fleet.Settings["MirbadLLM:Fencing:TakeoverId"] = "tk-keeps-failing";
            await fleet.RestartMasterAsync(waitForDgx: false, whileStopped: () => ReplaceAsync(fleet.DataDir, snapshot));
            var master = fleet.Master;
            long epoch = Epoch(master);

            const int Attempts = 5;
            await ExitEnv.UntilAsync(() => FailedLines(fleet).Count >= Attempts, $"{Attempts} failed takeover attempts");
            await master.Get<TakeoverService>().StopAsync(Ct); // quiesce the loop, then count exactly

            var failed = FailedLines(fleet);
            Assert.True(failed.Count >= Attempts);
            Assert.All(failed, l =>
            {
                Assert.Equal(AuditActors.Config, l.Actor);
                Assert.Equal(ErrorCodes.InternalError, l.ErrorCode);
                Assert.Contains("OverflowException", l.Detail, StringComparison.Ordinal);
            });

            // One Failed line per attempt: as many as the master logged failed attempts, and as many retries scheduled,
            // numbered 1, 2, 3, … (no attempt without its line, no line without its attempt).
            var messages = master.Log.Records.Select(r => r.Message).ToList();
            Assert.Equal(failed.Count, messages.Count(m => m == "Takeover by config failed."));
            var retries = messages.Where(m => m.StartsWith("The requested takeover failed ", StringComparison.Ordinal)).ToList();
            Assert.Equal(failed.Count, retries.Count);
            Assert.Equal(Enumerable.Range(1, retries.Count).Select(n => $"The requested takeover failed {n} time(s) in a row;"),
                retries.Select(m => m[..m.IndexOf(';', StringComparison.Ordinal)] + ";"));

            // Backoff: each wait is at least RetryDelay(n) (100, 200, 400, 800, 800 ms …), so the attempts slow down to the cap.
            for (int n = 1; n < failed.Count; n++)
            {
                TimeSpan gap = failed[n].Time - failed[n - 1].Time;
                TimeSpan wait = TakeoverService.RetryDelay(FastPolls, n);
                Assert.True(gap >= wait, $"attempt {n + 1} came {gap.TotalMilliseconds} ms after attempt {n}, before its {wait.TotalMilliseconds} ms backoff");
            }

            Assert.Equal(FastPolls.MaxRetryInterval, TakeoverService.RetryDelay(FastPolls, Attempts));

            // Nothing was taken over; the request and the red option warning stay.
            Assert.Empty(master.Get<IMasterIdentity>().Takeovers);
            Assert.Equal(epoch, Epoch(master));
            Assert.DoesNotContain(await fleet.AuditAsync(), l => l.Action == AuditActions.FencingTakeover && l.Result == AuditResults.Succeeded);
            FencingStatus status = await StatusAsync(master);
            Assert.True(status.TakeoverRequested);
            Assert.True(status.TakeoverOptionSet);
            Assert.Contains("data-testid=\"takeover-option-warning\"", await PageAsync(master, "/"), StringComparison.Ordinal);
        }
        finally
        {
            ExitEnv.DeleteDirectory(snapshot);
        }
    }

    private static List<AuditRecord> FailedLines(P2Fleet fleet) =>
        [.. ReadLines(fleet.AuditText()).Where(l => l.Action == AuditActions.FencingTakeover && l.Result == AuditResults.Failed).OrderBy(l => l.Time)];

    private static IEnumerable<AuditRecord> ReadLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonSerializer.Deserialize<AuditRecord>(line, MasterStateJson.Options)!);

    private static long Epoch(P2Master master) => master.Get<IMasterIdentity>().Epoch;

    private static NodeFencingStatus? FencingOf(P2Master master, string nodeId) =>
        master.Get<NodeStatusCache>().ListFencing().FirstOrDefault(n => n.NodeId == nodeId);

    private static async Task<FencingStatus> StatusAsync(P2Master master) =>
        (await master.GetJsonAsync("/api/fencing")).Deserialize<FencingStatus>(Json)!;

    private static async Task<string> PageAsync(P2Master master, string path)
    {
        var (status, body) = await SendAsync(master.Admin.GetAsync(path, Ct));
        Assert.True(status == HttpStatusCode.OK, $"GET {path}: {(int)status}");
        return body;
    }

    /// <summary>Waits until the node's agent reports <paramref name="epoch"/> as its accepted master epoch.</summary>
    private static Task WaitAcceptedAsync(P2Master master, string nodeId, long epoch) =>
        ExitEnv.UntilAsync(async () =>
        {
            try
            {
                return (await master.Client(nodeId).GetInfoAsync(Ct)).AcceptedMaster?.Epoch == epoch;
            }
            catch (Exception ex) when (ex is AgentException or AgentUnreachableException or HttpRequestException)
            {
                return false;
            }
        }, $"{nodeId} to accept epoch {epoch}");

    private static Task CopyAsync(string from, string to)
    {
        ExitEnv.CopyDataDirectory(from, to);
        return Task.CompletedTask;
    }

    /// <summary>Restores an older <c>/data</c>: the data directory becomes exactly <paramref name="snapshot"/>.</summary>
    private static Task ReplaceAsync(string dataDir, string snapshot)
    {
        ExitEnv.DeleteDirectory(dataDir);
        Assert.False(Directory.Exists(dataDir), $"{dataDir} could not be deleted");
        ExitEnv.CopyDataDirectory(snapshot, dataDir);
        return Task.CompletedTask;
    }
}
