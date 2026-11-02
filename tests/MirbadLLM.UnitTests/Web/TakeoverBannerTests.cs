using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MirbadLLM.Agent.Contracts;
using MirbadLLM.Core;
using MirbadLLM.Orchestration.Audit;
using MirbadLLM.Orchestration.Fencing;
using MirbadLLM.Orchestration.Nodes;
using MirbadLLM.UnitTests.Web.Fakes;
using MirbadLLM.Web;
using MirbadLLM.Web.Components.Layout;

namespace MirbadLLM.UnitTests.Web;

/// <summary>
/// MN-121: the takeover banner and its two dialogs (design §12.1 "Fencing banner", §4.2 step 3), rendered with bUnit against
/// the real <see cref="TakeoverService"/> over the real <see cref="NodeStatusCache"/> (hand-written agents answering
/// <c>/info</c>, a mutable master identity), the real <see cref="AdminReauthenticator"/> with its portal-session checks and
/// a recording audit log. Covers: nothing on a healthy single DGX; the failed-state banner for stale/competing nodes with the
/// cache's reason text; the Version poll; the typed precondition and session checks (anonymous or login
/// disabled); a takeover that leaves an unreachable node Pending takeover without alarming; the per-node continuation that
/// sends the takeover id and agent epoch the operator saw; a continuation refused in place; and the red option warning.
/// </summary>
public sealed class TakeoverBannerTests
{
    private const string Dgx = "dgx";
    private const string Pc = "pc";
    private const string Password = "takeover-banner-pw";
    private const string SessionId = "session-id-1";

    private static readonly string Actor = AuditActors.Admin(AuditActorResolver.SessionHash(SessionId));

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    // ---- nothing to show ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHealthySingleNodeShowsNothing()
    {
        await using var env = await Env.CreateAsync();
        await env.SyncAsync(Dgx);
        Assert.Equal(NodeFencingState.Current, env.Service.GetStatus().Nodes.Single(n => n.NodeId == Dgx).State);

        var cut = env.Render();

        AssertNothingShown(cut);
        Assert.Equal("false", cut.Find("[data-testid=takeover-dialog]").GetAttribute("data-open"));
        Assert.Equal("false", cut.Find("[data-testid=continue-dialog]").GetAttribute("data-open"));
    }

    [Fact]
    public void WithoutAFencingCoordinatorNothingRenders()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = ctx.Render<TakeoverBanner>();

        AssertNothingShown(cut);
    }

    [Fact]
    public async Task ANodeThatIsOnlyPendingTakeoverOrUnknownNeedsNoActionAndShowsNothing()
    {
        // pc was unreachable during takeover tk-1 and has not answered since; dgx was not contacted yet (Unknown).
        await using var env = await Env.CreateAsync(marked: new TakeoverRecord
        {
            TakeoverId = "tk-1", Epoch = 10, PreviousEpoch = 1, At = DateTimeOffset.UnixEpoch, Unreachable = [Pc],
        });
        env.Agent(Pc).Partitioned = true;
        await env.SyncAsync(Pc);
        FencingStatus status = env.Service.GetStatus();
        Assert.Equal(NodeFencingState.PendingTakeover, status.Nodes.Single(n => n.NodeId == Pc).State);
        Assert.Equal(NodeFencingState.Unknown, status.Nodes.Single(n => n.NodeId == Dgx).State);
        Assert.False(status.ActionNeeded);

        var cut = env.Render();

        AssertNothingShown(cut);
    }

    // ---- the failed-state banner ------------------------------------------------------------------------------------

    [Fact]
    public async Task AStaleNodeShowsTheFailedBannerWithTheCachesReasonAndATakeOverAction()
    {
        await using var env = await Env.CreateAsync();
        env.Agent(Dgx).AcceptedEpoch = 9;
        await env.SyncAsync(Dgx);

        var cut = env.Render();

        var banner = cut.Find("[data-testid=takeover-banner]");
        Assert.Equal("alert", banner.GetAttribute("role"));
        Assert.Equal("A node follows another master: a takeover is needed", cut.Find("[data-testid=takeover-banner-title]").TextContent);
        Assert.Equal("1", cut.Find("[data-testid=takeover-banner-epoch]").TextContent);
        var node = Assert.Single(cut.FindAll("[data-testid=takeover-node]"));
        Assert.Equal(Dgx, node.GetAttribute("data-node"));
        Assert.Equal(env.Cache.GetNode(Dgx)!.Reasons[0], node.QuerySelector("[data-testid=takeover-node-reason]")!.TextContent);
        Assert.StartsWith("Stale master: the agent accepted epoch 9", node.QuerySelector("[data-testid=takeover-node-reason]")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=takeover-open]"));
        Assert.Empty(cut.FindAll("[data-testid^=continue-open-]"));
        Assert.Empty(cut.FindAll("[data-testid=takeover-option-warning]"));
    }

    [Fact]
    public async Task ACompetingMasterAtTheSameEpochAlsoAsksForATakeoverAndNamesTheDisplayName()
    {
        await using var env = await Env.CreateAsync(pcName: "Office PC");
        env.Agent(Pc).AcceptedEpoch = 1;
        env.Agent(Pc).AcceptedInstance = "another-master";
        await env.SyncAsync(Pc);
        Assert.Equal(NodeFencingState.Competing, env.Service.GetStatus().Nodes.Single(n => n.NodeId == Pc).State);

        var cut = env.Render();

        var node = Assert.Single(cut.FindAll("[data-testid=takeover-node]"));
        Assert.Contains("Office PC (pc)", node.TextContent, StringComparison.Ordinal);
        Assert.StartsWith("Competing master", node.QuerySelector("[data-testid=takeover-node-reason]")!.TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=takeover-open]"));
    }

    [Fact]
    public async Task TheBannerFollowsTheCoordinatorsVersionWhileThePageIsOpen()
    {
        await using var env = await Env.CreateAsync();
        await env.SyncAsync(Dgx);
        var cut = env.Render();
        AssertNothingShown(cut);
        long version = env.Service.Version;

        env.Agent(Dgx).AcceptedEpoch = 9; // another master drove it meanwhile
        await env.SyncAsync(Dgx);
        Assert.True(env.Service.Version > version);

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=takeover-banner]")), TimeSpan.FromSeconds(10));
    }

    // ---- the takeover dialog ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTakeoverDialogKeepsTheTypedPreconditionWithoutAPasswordPrompt()
    {
        await using var env = await Env.CreateAsync();
        env.Agent(Dgx).AcceptedEpoch = 9;
        await env.SyncAsync(Dgx);
        var cut = env.Render();
        cut.ClickAt("[data-testid=takeover-open]");
        Assert.True(ConfirmDisabled(cut, "takeover-confirm"));
        Assert.Empty(cut.FindAll("input[type=password]"));
        InputAt(cut, "[data-testid=takeover-confirm-input]", "yes");
        Assert.True(ConfirmDisabled(cut, "takeover-confirm"));
        InputAt(cut, "[data-testid=takeover-confirm-input]", "  The previous master is STOPPED ");
        Assert.False(ConfirmDisabled(cut, "takeover-confirm"));
        cut.ClickAt("[data-testid=takeover-confirm]");
        cut.WaitForAssertion(() => Assert.Equal(10, env.Identity.Epoch), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ATakeoverLeavesAnUnreachableNodePendingWithoutAlarmAndRecordsTheSessionAsOperator()
    {
        await using var env = await Env.CreateAsync();
        env.Agent(Dgx).AcceptedEpoch = 9;
        await env.SyncAsync(Dgx);
        env.Agent(Pc).Partitioned = true;
        var cut = env.Render();

        cut.ClickAt("[data-testid=takeover-open]");
        InputAt(cut, "[data-testid=takeover-confirm-input]", TakeoverConfirmations.PreviousMasterStopped);
        cut.ClickAt("[data-testid=takeover-confirm]");

        // The real coordinator ran: max(1, dgx 9) + 1, pc unreachable.
        TakeoverRecord record = Assert.Single(env.Identity.Records);
        Assert.Equal(10, env.Identity.Epoch);
        Assert.Equal([Pc], record.Unreachable);
        Assert.Equal(Actor, record.Operator);
        var done = Assert.Single(env.Audit.Lines);
        Assert.Equal((AuditActions.FencingTakeover, AuditResults.Succeeded, Actor), (done.Action, done.Result, done.Actor));

        // The dialog closed; dgx is Current, pc Pending takeover: no failed banner, a calm notice instead.
        Assert.Equal("false", cut.Find("[data-testid=takeover-dialog]").GetAttribute("data-open"));
        Assert.Equal(NodeFencingState.PendingTakeover, env.Service.GetStatus().Nodes.Single(n => n.NodeId == Pc).State);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=takeover-banner]")), TimeSpan.FromSeconds(10));
        var notice = cut.Find("[data-testid=takeover-done]");
        Assert.Equal("status", notice.GetAttribute("role"));
        Assert.Contains($"Takeover {record.TakeoverId} done: this master now runs epoch 10.", notice.TextContent, StringComparison.Ordinal);
        Assert.Contains("pc did not answer and show Pending takeover until they return. Nothing to do now.", notice.TextContent, StringComparison.Ordinal);

        cut.ClickAt("[data-testid=takeover-done-dismiss]");
        Assert.Empty(cut.FindAll("[data-testid=takeover-done]"));
    }

    [Fact]
    public async Task AnAnonymousSessionCannotTakeOverEvenWithTheTypedPrecondition()
    {
        await using var env = await Env.CreateAsync();
        env.Agent(Dgx).AcceptedEpoch = 9;
        await env.SyncAsync(Dgx);
        env.Auth.SetNotAuthorized();
        var cut = env.Render();
        cut.ClickAt("[data-testid=takeover-open]");
        InputAt(cut, "[data-testid=takeover-confirm-input]", TakeoverConfirmations.PreviousMasterStopped);
        cut.ClickAt("[data-testid=takeover-confirm]");
        Assert.Contains("Sign in to the portal", cut.Find("[data-testid=takeover-error]").TextContent, StringComparison.Ordinal);
        Assert.Equal(1, env.Identity.Epoch);
        Assert.Equal(AuditResults.Refused, Assert.Single(env.Audit.Lines).Result);
    }

    [Fact]
    public async Task TheSignedInSessionIsRecordedAsOperatorWithoutPasswordReentry()
    {
        await using var env = await Env.CreateAsync();
        env.Agent(Dgx).AcceptedEpoch = 9;
        await env.SyncAsync(Dgx);
        const string otherSession = "session-id-2";
        env.Auth.SetClaims(new Claim(ClaimTypes.Sid, otherSession));
        var cut = env.Render();
        cut.ClickAt("[data-testid=takeover-open]");
        InputAt(cut, "[data-testid=takeover-confirm-input]", TakeoverConfirmations.PreviousMasterStopped);
        cut.ClickAt("[data-testid=takeover-confirm]");
        cut.WaitForAssertion(() => Assert.Equal(10, env.Identity.Epoch), TimeSpan.FromSeconds(10));
        var done = Assert.Single(env.Audit.Lines, l => l.Result == AuditResults.Succeeded);
        Assert.Equal(AuditActors.Admin(AuditActorResolver.SessionHash(otherSession)), done.Actor);
    }

    [Fact]
    public void TheBudgetKeyIsTheSessionHashThenTheIpThenTheSharedBucket()
    {
        var session = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Sid, SessionId)], "Cookies"));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.7");

        Assert.Equal("sid:" + AuditActorResolver.SessionHash(SessionId), AdminCallers.ClientKey(null, session));
        Assert.Equal("sid:" + AuditActorResolver.SessionHash(SessionId), AdminCallers.ClientKey(http, session));
        Assert.DoesNotContain(SessionId, AdminCallers.ClientKey(http, session), StringComparison.Ordinal);
        Assert.Equal("192.0.2.7", AdminCallers.ClientKey(http, new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Equal("192.0.2.7", AdminPasswordGuard.ClientKey(http)); // the login form: no session yet, per IP
        http.User = session;
        Assert.Equal("sid:" + AuditActorResolver.SessionHash(SessionId), AdminPasswordGuard.ClientKey(http)); // the API with a login cookie
        Assert.Equal(AdminCallers.UnknownClient, AdminCallers.ClientKey(null, null));
    }

    [Fact]
    public async Task WithAdminLoginDisabledTheDialogExplainsTheFlagFileAndCannotConfirm()
    {
        await using var env = await Env.CreateAsync(adminPassword: null);
        env.Agent(Dgx).AcceptedEpoch = 9;
        await env.SyncAsync(Dgx);
        var cut = env.Render();

        cut.ClickAt("[data-testid=takeover-open]");

        Assert.Contains("{DataPath}/takeover", cut.Find("[data-testid=takeover-login-disabled]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=takeover-password]"));
        Assert.True(ConfirmDisabled(cut, "takeover-confirm"));
    }

    // ---- the continuation -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReturningNodeWithAHigherEpochIsContinuedWithTheTakeoverIdAndEpochTheOperatorSaw()
    {
        await using var env = await Env.CreateAsync(pcName: "Office PC");
        TakeoverRecord takeover = await env.TakeOverWithPcAwayAsync();
        env.Agent(Pc).Partitioned = false;
        env.Agent(Pc).AcceptedEpoch = 20; // driven by another master while away
        await env.SyncAsync(Pc);
        Assert.True(env.Service.GetStatus().ActionNeeded);
        var cut = env.Render();

        Assert.Equal("A node returned holding a higher epoch: continue the takeover for it", cut.Find("[data-testid=takeover-banner-title]").TextContent);
        Assert.Contains("continues the takeover", cut.Find("[data-testid=takeover-node-reason]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=takeover-open]")); // no stale node: only the continuation
        Assert.Contains("Continue takeover for Office PC (pc)", cut.Find("[data-testid=continue-open-pc]").TextContent, StringComparison.Ordinal);

        cut.ClickAt("[data-testid=continue-open-pc]");
        Assert.Equal("true", cut.Find("[data-testid=continue-dialog]").GetAttribute("data-open"));
        Assert.Contains(takeover.TakeoverId, cut.Find("[data-testid=continue-plan]").TextContent, StringComparison.Ordinal);
        Assert.Equal("20", cut.Find("[data-testid=continue-agent-epoch]").TextContent);
        Assert.Equal("21", cut.Find("[data-testid=continue-plan-epoch]").TextContent);

        InputAt(cut, "[data-testid=continue-confirm-input]", "PC"); // the node id, exactly
        Assert.True(ConfirmDisabled(cut, "continue-confirm"));
        InputAt(cut, "[data-testid=continue-confirm-input]", Pc);
        cut.ClickAt("[data-testid=continue-confirm]");

        Assert.Equal(21, env.Identity.Epoch);
        TakeoverContinuation continuation = Assert.Single(Assert.Single(env.Identity.Records).Continuations);
        Assert.Equal((Pc, 20L, 21L, Actor), (continuation.NodeId, continuation.AgentEpoch, continuation.NewEpoch, continuation.Operator));
        Assert.Equal("false", cut.Find("[data-testid=continue-dialog]").GetAttribute("data-open"));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=takeover-banner]")), TimeSpan.FromSeconds(10));
        Assert.Contains("continued for Office PC (pc): this master now runs epoch 21.", cut.Find("[data-testid=takeover-done]").TextContent, StringComparison.Ordinal);
        Assert.Equal(
            [(AuditActions.FencingTakeover, AuditResults.Succeeded), (AuditActions.FencingContinue, AuditResults.Succeeded)],
            env.Audit.Lines.Select(l => (l.Action, l.Result)).ToArray());
    }

    [Fact]
    public async Task AContinuationWhoseAgentEpochMovedIsRefusedInPlace()
    {
        await using var env = await Env.CreateAsync();
        await env.TakeOverWithPcAwayAsync();
        env.Agent(Pc).Partitioned = false;
        env.Agent(Pc).AcceptedEpoch = 20;
        await env.SyncAsync(Pc);
        var cut = env.Render();
        cut.ClickAt("[data-testid=continue-open-pc]");
        InputAt(cut, "[data-testid=continue-confirm-input]", Pc);

        env.Agent(Pc).AcceptedEpoch = 25; // moved again after the dialog showed 20
        cut.ClickAt("[data-testid=continue-confirm]");

        Assert.Equal("true", cut.Find("[data-testid=continue-dialog]").GetAttribute("data-open"));
        Assert.Contains("now reports epoch 25, not 20", cut.Find("[data-testid=continue-error]").TextContent, StringComparison.Ordinal);
        Assert.Equal(10, env.Identity.Epoch);
        Assert.Equal((AuditActions.FencingContinue, AuditResults.Refused), (env.Audit.Lines[^1].Action, env.Audit.Lines[^1].Result));
        Assert.Empty(cut.FindAll("[data-testid=takeover-done]"));
    }

    // ---- the takeover option ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheTakeoverOptionWithoutAnIdShowsTheRedEveryBootWarning()
    {
        await using var env = await Env.CreateAsync(fencing: new FencingOptions { Takeover = true });
        await env.SyncAsync(Dgx);

        var cut = env.Render();

        var warning = cut.Find("[data-testid=takeover-option-warning]");
        Assert.Equal("alert", warning.GetAttribute("role"));
        Assert.Contains("every restart of MirbadLLM takes over the nodes again", warning.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=takeover-banner]")); // no node needs action
    }

    [Fact]
    public async Task TheOneShotOptionOnceUsedSaysItIsIgnoredAndShouldBeRemoved()
    {
        await using var env = await Env.CreateAsync(fencing: new FencingOptions { Takeover = true, TakeoverId = "boot-tk" });
        env.Identity.Requested = true;
        env.Agent(Pc).Partitioned = true;
        var cut = env.Render();
        Assert.Contains("one-shot takeover boot-tk, which runs once", cut.Find("[data-testid=takeover-option-text]").TextContent, StringComparison.Ordinal);

        TakeoverRecord record = await env.Service.TakeOverAsync(AuditActors.Config, Ct);
        Assert.Equal("boot-tk", record.TakeoverId);

        cut.WaitForAssertion(() => Assert.Contains("takeover boot-tk already completed, so the option is ignored now",
            cut.Find("[data-testid=takeover-option-text]").TextContent, StringComparison.Ordinal), TimeSpan.FromSeconds(10));
    }

    // ---- shell and layout ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheLayoutHostsTheBannerOnEveryPageAndItWrapsAtPhoneWidth()
    {
        string layout = RepoFile("src/MirbadLLM.Web/Components/Layout/MainLayout.razor");
        int main = layout.IndexOf("<main class=\"main\"", StringComparison.Ordinal);
        int banner = layout.IndexOf("<TakeoverBanner @rendermode=\"InteractiveServer\" />", StringComparison.Ordinal);
        int page = layout.IndexOf("<div class=\"page\">", StringComparison.Ordinal);
        Assert.True(main > 0 && banner > main && banner < page, "the banner sits in <main>, above the page body");

        string css = RepoFile("src/MirbadLLM.Web/Components/Layout/TakeoverBanner.razor.css");
        Assert.Contains("flex-wrap: wrap", css, StringComparison.Ordinal);
        int phone = css.IndexOf("@media (max-width: 639.98px)", StringComparison.Ordinal);
        Assert.True(phone > 0 && css.IndexOf("min-height: 44px", phone, StringComparison.Ordinal) > phone, "44px targets on phones");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static void AssertNothingShown(IRenderedComponent<TakeoverBanner> cut)
    {
        Assert.Empty(cut.FindAll("[data-testid=takeover-banner]"));
        Assert.Empty(cut.FindAll("[data-testid=takeover-option-warning]"));
        Assert.Empty(cut.FindAll("[data-testid=takeover-done]"));
        Assert.Empty(cut.FindAll("[role=alert]"));
    }

    private static bool ConfirmDisabled(IRenderedComponent<TakeoverBanner> cut, string testId) =>
        cut.Find($"[data-testid={testId}]").HasAttribute("disabled");

    /// <summary>Raises <c>oninput</c> on the renderer's dispatcher, retrying when a poll render retired the handler id.</summary>
    private static void InputAt(IRenderedComponent<TakeoverBanner> cut, string selector, string value)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Task handler = Task.CompletedTask;
                cut.InvokeAsync(() => { handler = cut.Find(selector).InputAsync(new ChangeEventArgs { Value = value }); }).GetAwaiter().GetResult();
                handler.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                return;
            }
            catch (UnknownEventHandlerIdException) when (attempt < 5)
            {
                // A render retired the id between the DOM snapshot and the dispatch; nothing was delivered.
            }
        }
    }

    private static string RepoFile(string relative, [CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MirbadLLM.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    /// <summary>The banner over the real coordinator: two nodes (dgx, pc), each with its own hand-written agent.</summary>
    private sealed class Env : IAsyncDisposable
    {
        private readonly Dictionary<string, FakeAgent> _agents;

        private Env(BunitContext ctx, Identity identity, Dictionary<string, FakeAgent> agents, NodeStatusCache cache,
            TakeoverService service, RecordingAuditLog audit)
        {
            Ctx = ctx;
            Identity = identity;
            _agents = agents;
            Cache = cache;
            Service = service;
            Audit = audit;
        }

        public BunitContext Ctx { get; }

        public Identity Identity { get; }

        public NodeStatusCache Cache { get; }

        public TakeoverService Service { get; }

        public RecordingAuditLog Audit { get; }

        public Bunit.TestDoubles.BunitAuthorizationContext Auth { get; private init; } = null!;

        public static Task<Env> CreateAsync(
            TakeoverRecord? marked = null, string? pcName = null, string? adminPassword = Password, FencingOptions? fencing = null)
        {
            var identity = new Identity { Epoch = marked?.Epoch ?? 1 };
            if (marked is not null)
            {
                identity.Records = [marked];
            }

            NodeRecord[] nodes =
            [
                new() { Id = Dgx, Uid = "uid-dgx", Address = "https://dgx:7443/", Approved = true },
                new() { Id = Pc, Uid = "uid-pc", DisplayName = pcName, Address = "https://pc:7443/", Approved = true },
            ];
            var agents = nodes.ToDictionary(n => n.Id, n => new FakeAgent(n.Id, n.Uid), StringComparer.Ordinal);
            var factory = new Factory(agents);
            var cache = new NodeStatusCache(factory, identity, TimeProvider.System);
            cache.SetNodes(nodes);
            var audit = new RecordingAuditLog();
            fencing ??= new FencingOptions();
            var options = new TakeoverServiceOptions { InfoTimeout = TimeSpan.FromSeconds(5) };
            var service = new TakeoverService(identity, cache, new ListOnlyNodeRegistry(nodes), factory, TimeProvider.System,
                audit, fencing, options);

            var ctx = new BunitContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.Services.AddSingleton<IFencingCoordinator>(service);
            ctx.Services.AddSingleton<INodeStatusCache>(cache);
            ctx.Services.AddSingleton<IAuditLog>(audit);
            ctx.Services.AddSingleton(options);
            ctx.Services.AddSingleton(Options.Create(fencing));
            ctx.Services.AddSingleton<IAdminReauthenticator>(new AdminReauthenticator(
                Options.Create(new AuthOptions { AdminPassword = adminPassword })));
            var auth = ctx.AddAuthorization();
            auth.SetAuthorized("admin");
            auth.SetClaims(new Claim(ClaimTypes.Sid, SessionId));
            return Task.FromResult(new Env(ctx, identity, agents, cache, service, audit) { Auth = auth });
        }

        public FakeAgent Agent(string nodeId) => _agents[nodeId];

        public IRenderedComponent<TakeoverBanner> Render() =>
            Ctx.Render<TakeoverBanner>(p => p.Add(b => b.PollInterval, TimeSpan.FromMilliseconds(50)));

        /// <summary>One <c>/info</c> + snapshot of the node, as the monitor takes it (an unreachable agent is recorded).</summary>
        public async Task SyncAsync(string nodeId)
        {
            try
            {
                await Cache.RefreshNodeAsync(nodeId, Ct);
            }
            catch (AgentUnreachableException) when (Agent(nodeId).Partitioned)
            {
                // The cache recorded the failure, as the monitor's loop does.
            }
        }

        /// <summary>dgx answers holding epoch 9, pc is away: the takeover moves to 10 and marks pc Pending takeover.</summary>
        public async Task<TakeoverRecord> TakeOverWithPcAwayAsync()
        {
            Agent(Dgx).AcceptedEpoch = 9;
            Agent(Pc).Partitioned = true;
            await SyncAsync(Dgx);
            TakeoverRecord record = await Service.TakeOverAsync(Actor, Ct);
            Assert.Equal(10, record.Epoch);
            Assert.Equal([Pc], record.Unreachable);
            return record;
        }

        public async ValueTask DisposeAsync()
        {
            await Ctx.DisposeAsync();
            Service.Dispose();
        }
    }

    internal sealed class RecordingAuditLog : IAuditLog
    {
        private readonly List<AuditRecord> _lines = [];

        public IReadOnlyList<AuditRecord> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public Task WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            lock (_lines)
            {
                _lines.Add(record);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditRecord>> ReadAsync(AuditQuery query, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AuditRecord>>([]);
    }

    /// <summary>A mutable master identity persisting takeovers in memory, like <c>MasterIdentity</c>.</summary>
    internal sealed class Identity : IMasterIdentity
    {
        public long Epoch { get; set; }

        public IReadOnlyList<TakeoverRecord> Records { get; set; } = [];

        public bool Requested { get; set; }

        public string InstanceId => "restored";

        public string BootId => "boot";

        public MasterTriple Current => new(InstanceId, Epoch, BootId);

        public bool TakeoverRequested => Requested;

        public IReadOnlyList<TakeoverRecord> Takeovers => Records;

        public Task<MasterTriple> RaiseEpochToAsync(long epoch, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<MasterTriple> CompleteTakeoverAsync(TakeoverRecord record, CancellationToken ct = default)
        {
            // Like MasterIdentity: the epoch moves to the record's, or above it to its latest continuation's.
            Epoch = Math.Max(record.Epoch, (record.Continuations ?? []).Select(c => c.NewEpoch).DefaultIfEmpty(0).Max());
            Records = [.. Records.Where(r => r.TakeoverId != record.TakeoverId), record];
            Requested = false;
            return Task.FromResult(Current);
        }
    }

    private sealed class Factory(Dictionary<string, FakeAgent> agents) : IAgentClientFactory
    {
        public IAgentClient GetClient(string nodeId) => agents.TryGetValue(nodeId, out FakeAgent? agent) ? agent.Client : throw new KeyNotFoundException(nodeId);

        public Task<IAgentClientLease> CreateFreshClientAsync(string nodeId, CancellationToken ct = default) => throw new NotSupportedException();

        public void RebuildHandlers(string nodeId) => throw new NotSupportedException();

        public void SetNegotiatedProtocol(string nodeId, int version)
        {
        }
    }

    /// <summary>An agent answering <c>/info</c> (with a settable accepted master), capabilities and an empty status.</summary>
    internal sealed class FakeAgent
    {
        public FakeAgent(string nodeId, string uid)
        {
            NodeId = nodeId;
            Uid = uid;
            Client = AgentProxy.Create(this);
        }

        public string NodeId { get; }

        public string Uid { get; }

        public IAgentClient Client { get; }

        public long? AcceptedEpoch { get; set; }

        public string AcceptedInstance { get; set; } = "old-master";

        public volatile bool Partitioned;

        public Task<InfoResponse> InfoAsync()
        {
            if (Partitioned)
            {
                return Task.FromException<InfoResponse>(new AgentUnreachableException($"{NodeId} is partitioned"));
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            return Task.FromResult(new InfoResponse
            {
                NodeId = NodeId,
                NodeUid = Uid,
                AgentVersion = "1.0.0",
                Protocol = new ProtocolRange(Protocol.MinVersion, Protocol.CurrentVersion),
                BootId = "agent-boot",
                StartedAt = now,
                Time = now,
                AcceptedMaster = AcceptedEpoch is { } epoch
                    ? new MasterTriple(AcceptedInstance, epoch, "old-boot")
                    : null,
            });
        }

        public Task<NodeCapabilities> CapabilitiesAsync() => Partitioned
            ? Task.FromException<NodeCapabilities>(new AgentUnreachableException($"{NodeId} is partitioned"))
            : Task.FromResult(new NodeCapabilities
            {
                Protocol = new ProtocolRange(Protocol.MinVersion, Protocol.CurrentVersion),
                AgentVersion = "1.0.0",
                Features = [.. AgentFeatures.P1],
                Os = new NodeOsInfo("linux", "Ubuntu 24.04 LTS"),
                Arch = "x86_64",
                Memory = new NodeMemoryInfo(MemoryModel.Unified, 64),
            });

        public Task<StatusSnapshot> StatusAsync() => Partitioned
            ? Task.FromException<StatusSnapshot>(new AgentUnreachableException($"{NodeId} is partitioned"))
            : Task.FromResult(new StatusSnapshot
            {
                BootId = "agent-boot",
                Seq = 1,
                Time = DateTimeOffset.UtcNow,
                Health = new NodeHealth(),
                Memory = new NodeMemorySnapshot { Model = MemoryModel.Unified, HostTotalGiB = 64, HostAvailableGiB = 60 },
            });
    }

    /// <summary>An <see cref="IAgentClient"/> answering the three synchronization calls; every other call fails.</summary>
    public class AgentProxy : DispatchProxy
    {
        private object? _agent;

        private FakeAgent Target => (FakeAgent)_agent!;

        internal static IAgentClient Create(object agent)
        {
            IAgentClient client = DispatchProxy.Create<IAgentClient, AgentProxy>();
            ((AgentProxy)(object)client)._agent = agent;
            return client;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IAgentClient.GetInfoAsync) => Target.InfoAsync(),
            nameof(IAgentClient.GetCapabilitiesAsync) => Target.CapabilitiesAsync(),
            nameof(IAgentClient.GetStatusAsync) => Target.StatusAsync(),
            "get_NodeId" => Target.NodeId,
            _ => throw new NotSupportedException(targetMethod?.Name),
        };
    }
}
